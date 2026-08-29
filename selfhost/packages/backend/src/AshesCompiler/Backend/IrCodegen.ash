// The first genuinely IR-driven slice of the self-hosted backend: walks a REAL `IrFunction`
// produced by `AshesCompiler.Semantics` (via `AshesCompiler.Frontend.Parser.parseProgram` +
// `AshesCompiler.Semantics.CoreLowering.lowerCoreProgramWithSource`, the same pipeline
// `selfhost/tests/ir-program-parity` already trusts against stage 0) and drives
// `AshesCompiler.Backend.Llvm` from its actual instructions — not a human hand-simulating what
// codegen should produce, which is all every earlier test in this arc ever did.
//
// Boundary:
// - Covers `LoadConstInt`, `MulInt`, `AddInt`, `CmpIntGt`, `StoreLocal`, `LoadLocal`, `Label`,
//   `Jump`, `JumpIfFalse`, and `Return` — enough for `simple_arith` and `let_bindings`
//   (`selfhost/tests/ir-program-parity`'s own two trusted scalar fixtures) plus a plain
//   `if`/`then`/`else` expression (not yet one of that suite's fixtures, since `if` still needs
//   the constructor-layout/closure machinery `pattern_match` does before it can join it — this
//   codegen doesn't need that, only real self-hosted lowering to succeed for the shape used).
//   `if`'s own lowered IR is exactly the SAME no-`phi` slot pattern this whole arc's earlier
//   hand-built tests already used (`buildMaxModule` et al.): a `StoreLocal` into a shared result
//   slot in each arm, joined by a `LoadLocal` after both arms converge on one label — the real
//   compiler's own strategy turns out to match this package's LLVM codegen model exactly.
//   `SaveArenaState`/`RestoreArenaState`/`ReclaimArenaChunks` (emitted around every top-level `let`
//   scope, even a provably-scalar one) are treated as genuine no-ops: real scoped-arena codegen is
//   a separate, much bigger slice this one deliberately does not attempt, so these are explicitly
//   ignored rather than silently producing wrong code for a case they can't yet handle. Anything
//   else panics with a clear "unsupported" message. Closures, ADTs, strings, RC, and every other
//   instruction kind remain unstarted follow-up slices.
// - Every IR value is a full-width `i64` word (architecture.md: "every value is an i64 word"), so
//   a temp environment is just `List((IrTemp, LLVMValueRef))` — no type-directed dispatch needed
//   for this instruction subset. `Ashes.Number.UInt.fromInt64` (added alongside the first version
//   of this slice) is what makes `LoadConstInt`'s dynamic `Int` payload usable with `constInt`'s
//   `u64` parameter.
// - Locals get one `buildAlloca`'d `i64` slot each, allocated up front from `IrFunction`'s own
//   `localCount` — looked up by index the same way temps are, in a separate, fixed
//   (never-appended-to) environment: which local index maps to which alloca pointer never changes
//   once the function's slots are built, only the value stored at that pointer does.
// - Labels need a block PRE-CREATED before the main codegen pass, since a `Jump`/`JumpIfFalse` can
//   name a label that appears later in the instruction stream than the branch itself —
//   `collectLabelNames` walks the instruction list once up front to find every `Label`, and
//   `createLabelBlocks` turns each into a real (initially empty) `LLVMBasicBlockRef` before any
//   instruction is actually codegen'd. `JumpIfFalse` itself has no explicit "otherwise" target in
//   the IR (only the false-branch label; falling through is implicit), so it synthesizes an
//   unnamed continuation block for that implicit fallthrough and repositions the builder there —
//   the same shape `buildCondBr` needs two explicit blocks for, just with one of them anonymous.
// - The fold threads `(tempEnv, terminated)`, not just `tempEnv`: `terminated` tracks whether the
//   block currently being written already ends with a terminator, mirroring `LlvmCodegen.cs`'s own
//   flag exactly. The IR itself can rely on genuinely implicit fallthrough at a `Label` boundary
//   (an arm with no explicit `Jump` before the next label — an `if`'s last arm falling into the
//   merge point, say), but LLVM basic blocks have no fallthrough concept at all: every block must
//   end with an explicit terminator, so `Label` inserts a bridging `buildBr` to its own block
//   first whenever the block being left isn't terminated yet. Getting this wrong doesn't fail to
//   compile — emitting a function with an unterminated block segfaults `LLVMTargetMachineEmitToMemoryBuffer`
//   outright, which is how this was found.
// - `CodegenContext` bundles the five values that stay fixed for a whole function
//   (`context`/`function_`/`i64`/`localSlots`/`labelBlocks`) so they thread through as one value
//   instead of an ever-growing parameter list; only `tempEnv` actually grows instruction by
//   instruction.
// - `codegenEntryFunction` only ever builds the true program entry (there is no support yet for
//   `IrProgram.functions`, the list of ordinary helper functions a real program also has), so its
//   `Return` is unconditionally lowered the way `LlvmCodegenExpressions.cs`'s `EmitReturn` lowers
//   ONLY the entry function's `Return`, never an ordinary one: normal program completion is not a
//   `ret` at all — there is no return address on the stack once the OS has jumped straight to this
//   code as the process's actual entry point — it is a raw Linux `exit` syscall (`60`, matching
//   real Ashes semantics: the process always exits `0` on normal completion; a different code
//   needs the separate `Ashes.IO.exit`/`ExitProcess` instruction, not attempted here) followed by
//   `buildUnreachable`, since a syscall that terminates the process never returns to the caller.
//   `Return`'s own `source` temp is therefore unused — the computed value it names was real IR
//   arithmetic and is still genuinely built, just never surfaced as an exit code. This can't yet
//   be observed by actually running a linked executable (there is no linker yet), only by reading
//   the disassembly and confirming the tail is `syscall`+`unreachable`, never `ret`.

import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Backend.Llvm
import Ashes.Number.UInt
export (
    value codegenEntryFunction,
)

type CodegenContext =
    | context: LLVMContextRef
    | function_: LLVMValueRef
    | i64: LLVMTypeRef
    | localSlots: List((IrLocal, LLVMValueRef))
    | labelBlocks: List((Str, LLVMBasicBlockRef))

let recursive lookupIndexed key env =
    match env with
        | [] -> Ashes.IO.panic("codegen: unknown index " + Ashes.Trait.Show.show(key))
        | (boundKey, value) :: rest ->
            if boundKey == key
            then value
            else lookupIndexed(key)(rest)

// Allocates one `i64` slot per local, `0..count-1`, and returns the fixed `IrLocal -> LLVMValueRef`
// mapping every `StoreLocal`/`LoadLocal` in the function looks up by index.
let recursive allocateLocalSlots builder i64 count index =
    if index >= count
    then []
    else (index, buildAlloca(builder)(i64)("local" + Ashes.Text.fromInt(index))) :: allocateLocalSlots(builder)(i64)(count)(index + 1)

let recursive collectLabelNames instructions =
    match instructions with
        | [] -> []
        | instruction :: rest ->
            match instruction with
                | IrInstruction { instruction = kind } ->
                    match kind with
                        | Label(name) -> name :: collectLabelNames(rest)
                        | _ -> collectLabelNames(rest)

let recursive createLabelBlocks context function_ names =
    match names with
        | [] -> []
        | name :: rest -> (name, appendBasicBlock(context)(function_)(name)) :: createLabelBlocks(context)(function_)(rest)

// The linux-x64 `exit` syscall (`60`), matching `LlvmCodegenPlatform.cs`'s own `EmitSyscallX86`
// exactly: `syscall` through inline assembly with the same register-constraint string (`rax` holds
// the syscall number and doubles as the return-value register `LLVMGetInlineAsm` still declares,
// even though a real `exit` never returns to use it), `rdi`/`rsi`/`rdx` as the three syscall
// arguments, `rcx`/`r11` clobbered (the `syscall` instruction itself overwrites them) alongside
// memory. `exit` (not `exit_group`) terminates only the calling thread — the right choice for a
// single-threaded program, matching what the real compiler emits here too. A `syscall` that
// terminates the process never returns, so the block ends with `buildUnreachable`, never a `ret`.
let emitLinuxProcessExit builder i64 =
    (let syscallType = functionType(i64)([i64, i64, i64, i64])(4u32)(false)
    in
        let syscallAsm = getInlineAsm(syscallType)("syscall")("={rax},{rax},{rdi},{rsi},{rdx},~{rcx},~{r11},~{memory}")(true)(false)
        in
            let sixty = constInt(i64)(60u64)(false)
            in
                let zero = constInt(i64)(0u64)(false)
                in
                    let _ = buildCall(builder)(syscallType)(syscallAsm)([sixty, zero, zero, zero])(4u32)("sys_exit")
                    in buildUnreachable(builder))

let codegenInstructionKind cx builder kind state =
    match state with
        | (tempEnv, terminated) ->
            match cx with
                | CodegenContext { context = context, function_ = function_, i64 = i64, localSlots = localSlots, labelBlocks = labelBlocks } ->
                    match kind with
                        | LoadConstInt(target, value) -> ((target, constInt(i64)(Ashes.Number.UInt.fromInt64(value))(true)) :: tempEnv, terminated)
                        | MulInt(target, left, right) -> ((target, buildMul(builder)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                        | AddInt(target, left, right) -> ((target, buildAdd(builder)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                        | CmpIntGt(target, left, right) -> ((target, buildICmp(builder)(intPredicateSgt)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                        | StoreLocal(slot, source) ->
                            let _ = buildStore(builder)(lookupIndexed(source)(tempEnv))(lookupIndexed(slot)(localSlots))
                            in (tempEnv, terminated)
                        | LoadLocal(target, slot) -> ((target, buildLoad(builder)(i64)(lookupIndexed(slot)(localSlots))("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                        | Label(name) ->
                            let labelBlock = lookupIndexed(name)(labelBlocks)
                            in
                                let _ =
                                    if terminated
                                    then Unit
                                    else
                                        let _ = buildBr(builder)(labelBlock)
                                        in Unit
                                in
                                    let _ = positionBuilderAtEnd(builder)(labelBlock)
                                    in (tempEnv, false)
                        | Jump(target) ->
                            let _ = buildBr(builder)(lookupIndexed(target)(labelBlocks))
                            in (tempEnv, true)
                        | JumpIfFalse(cond, target) ->
                            let fallthroughBlock = appendBasicBlock(context)(function_)("fallthrough")
                            in
                                let _ = buildCondBr(builder)(lookupIndexed(cond)(tempEnv))(fallthroughBlock)(lookupIndexed(target)(labelBlocks))
                                in
                                    let _ = positionBuilderAtEnd(builder)(fallthroughBlock)
                                    in (tempEnv, false)
                        | SaveArenaState(_, _, _) -> (tempEnv, terminated)
                        | RestoreArenaState(_, _, _, _) -> (tempEnv, terminated)
                        | ReclaimArenaChunks(_, _, _) -> (tempEnv, terminated)
                        | Return(_) ->
                            let _ = emitLinuxProcessExit(builder)(i64)
                            in (tempEnv, true)
                        | _ -> Ashes.IO.panic("codegen: unsupported IrInstructionKind for this minimal slice")

let recursive codegenInstructions cx builder instructions state =
    match instructions with
        | [] -> state
        | instruction :: rest ->
            match instruction with
                | IrInstruction { instruction = kind } -> codegenInstructions(cx)(builder)(rest)(codegenInstructionKind(cx)(builder)(kind)(state))

// Builds `void <name>()` in a fresh module from `irFunction`'s real instructions and returns
// `(module_, builder)`, matching every other module builder's shape in `selfhost/tests/backend` so
// the same `emitModule` verification pipeline applies unchanged. `void`, not `i64`, since the
// function genuinely never returns a value anymore — every path ends in the exit syscall's
// `unreachable`, not a `ret`. `i64` (the type internal temps/locals use) is a separate local value.
let codegenEntryFunction name context irFunction =
    (let module_ = createModule(name)(context)
    in
        let i64 = int64Type(context)
        in
            let functionValue = addFunction(module_)(name)(functionType(voidType(context))([])(0u32)(false))
            in
                let entryBlock = appendBasicBlock(context)(functionValue)("entry")
                in
                    let builder = createBuilder(context)
                    in
                        let _ = positionBuilderAtEnd(builder)(entryBlock)
                        in
                            match irFunction with
                                | IrFunction { instructions = instructions, localCount = localCount } ->
                                    let localSlots = allocateLocalSlots(builder)(i64)(localCount)(0)
                                    in
                                        let labelBlocks = createLabelBlocks(context)(functionValue)(collectLabelNames(instructions))
                                        in
                                            let cx =
                                                CodegenContext(
                                                    context = context,
                                                    function_ = functionValue,
                                                    i64 = i64,
                                                    localSlots = localSlots,
                                                    labelBlocks = labelBlocks
                                                )
                                            in
                                                let _ = codegenInstructions(cx)(builder)(instructions)(([], false))
                                                in (module_, builder))
