// The first genuinely IR-driven slice of the self-hosted backend: walks a REAL `IrFunction`
// produced by `AshesCompiler.Semantics` (via `AshesCompiler.Frontend.Parser.parseProgram` +
// `AshesCompiler.Semantics.CoreLowering.lowerCoreProgramWithSource`, the same pipeline
// `selfhost/tests/ir-program-parity` already trusts against stage 0) and drives
// `AshesCompiler.Backend.Llvm` from its actual instructions — not a human hand-simulating what
// codegen should produce, which is all every earlier test in this arc ever did.
//
// Boundary:
// - Covers `LoadConstInt`, `MulInt`, `AddInt`, `SubInt`, `CmpIntGt`, `StoreLocal`, `LoadLocal`,
//   `Label`, `Jump`, `JumpIfFalse`, and `Return` — enough for `simple_arith` and `let_bindings`
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
//   ignored rather than silently producing wrong code for a case they can't yet handle.
// - `PrintInt` is the first genuinely user-observable instruction this codegen supports: converts
//   its `Int` source to decimal ASCII in a 32-byte stack buffer (`printIntPrologue`/
//   `printIntDigitLoopBody`/`printIntWriteAndNewline`, porting `LlvmCodegenPlatform.cs`'s own
//   `EmitPrintInt`), then writes it via the raw, unbuffered Linux `write` syscall (`1`) —
//   `emitLinuxSyscallCall` generalizes `Return`'s own inline-assembly `syscall` mechanism to any
//   3-argument syscall, shared by `exit` and `write`. Entirely stack-local: no global/`.data`
//   reference anywhere, so it needs nothing new from `AshesCompiler.Backend.ElfLinker`'s current
//   relocation-free scope. `AllocAdt` is supported only for the zero-field, non-RC-managed case
//   (exactly what `PrintInt`'s own `Unit` result lowers to): a plain stack `alloca` standing in for
//   a real arena bump allocation, correct only because today's supported program shapes never loop
//   around a top-level `AllocAdt` — a genuine scoped-arena allocator remains a separate, much
//   bigger slice. Any field-carrying or RC-managed `AllocAdt`, and every other instruction kind
//   (closures, non-trivial ADTs, strings, RC), panics with a clear "unsupported" message rather
//   than silently producing wrong code.
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
//   arithmetic and is still genuinely built, just never surfaced as an exit code.
//   `AshesCompiler.Backend.ElfLinker` (linux-x64, static-only) now links this codegen's output
//   into a directly-runnable executable, so this is observable by actually running one: `strace`
//   shows a single `exit(0)` syscall and nothing else, matching the disassembly's `syscall`+
//   `unreachable` tail.

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

// Any linux-x64 3-argument syscall, matching `LlvmCodegenPlatform.cs`'s own `EmitSyscallX86`
// exactly: `syscall` through inline assembly with the same register-constraint string (`rax` holds
// the syscall number going in and doubles as the return-value register `LLVMGetInlineAsm` still
// declares, whether or not a given syscall — `exit` never does — actually returns to use it),
// `rdi`/`rsi`/`rdx` as the three syscall arguments, `rcx`/`r11` clobbered (the `syscall`
// instruction itself overwrites them) alongside memory. Shared by `exit` (`60`) and `write` (`1`)
// — the only two syscalls this codegen needs so far.
let emitLinuxSyscallCall builder i64 nr arg1 arg2 arg3 name =
    (let syscallType = functionType(i64)([i64, i64, i64, i64])(4u32)(false)
    in
        let syscallAsm = getInlineAsm(syscallType)("syscall")("={rax},{rax},{rdi},{rsi},{rdx},~{rcx},~{r11},~{memory}")(true)(false)
        in buildCall(builder)(syscallType)(syscallAsm)([nr, arg1, arg2, arg3])(4u32)(name))

// `exit` (not `exit_group`) terminates only the calling thread — the right choice for a
// single-threaded program, matching what the real compiler emits here too. A `syscall` that
// terminates the process never returns, so the block ends with `buildUnreachable`, never a `ret`.
let emitLinuxProcessExit builder i64 =
    (let zero = constInt(i64)(0u64)(false)
    in
        let _ = emitLinuxSyscallCall(builder)(i64)(constInt(i64)(60u64)(false))(zero)(zero)(zero)("sys_exit")
        in buildUnreachable(builder))

// `write(fd, ptr, len)` — the raw, unbuffered path `LlvmCodegenPlatform.cs`'s own `EmitWriteBytesRaw`
// takes when a program never touches `Ashes.IO.writeBuffered`/`flush` (the only path this codegen
// implements; a buffered stdout ring, its lock, and the flush-on-exit contract are a separate,
// bigger, unattempted slice). `ptr` is an `i64` address (from `buildPtrToInt`), not an LLVM
// pointer value — every syscall argument here is a plain register-width word.
let emitLinuxWrite builder i64 fd ptr len = emitLinuxSyscallCall(builder)(i64)(constInt(i64)(1u64)(false))(fd)(ptr)(len)("sys_write")

// The five values `PrintInt`'s helper functions all need, computed once by `printIntPrologue` and
// threaded through unchanged — the same "bundle the fixed values" shape `CodegenContext` uses for
// a whole function, here scoped to one instruction's own control flow instead.
type PrintIntState =
    | buffer: LLVMValueRef
    | bufferType: LLVMTypeRef
    | indexSlot: LLVMValueRef
    | workSlot: LLVMValueRef
    | isNegative: LLVMValueRef

// Truncates `value` to `i8` and stores it at byte `index` of the `bufferType`-shaped stack
// `buffer` — the same GEP-then-truncate-then-store shape `LlvmCodegenMemory.cs`'s own
// `StoreBufferByte` uses (unconditionally truncating here, since every caller in this file always
// passes an `i64` value, never an already-8-bit one).
let storePrintBufferByte builder i64 i8 bufferType buffer index value =
    (let zero = constInt(i64)(0u64)(false)
    in
        let ptr = buildGEP(builder)(bufferType)(buffer)([zero, index])(2u32)("buf_ptr")
        in buildStore(builder)(buildTrunc(builder)(value)(i8)("to_i8"))(ptr))

// Allocates the 32-byte stack digit buffer plus the index/work stack slots `PrintInt`'s block
// structure shares, matching `LlvmCodegenPlatform.cs`'s own `EmitPrintIntPrologue`: `workSlot`
// starts at `value`'s absolute value (a negative `value` is negated via `buildSelect`, no branch
// needed for that part), `indexSlot` starts at `0`. Genuinely stack-only — no global/`.data`
// reference anywhere in this instruction, so it needs nothing new from
// `AshesCompiler.Backend.ElfLinker`'s current relocation-free scope.
let printIntPrologue builder i64 i8 value =
    (let bufferType = arrayType(i8)(32u64)
    in
        let buffer = buildAlloca(builder)(bufferType)("print_buf")
        in
            let indexSlot = buildAlloca(builder)(i64)("print_idx")
            in
                let workSlot = buildAlloca(builder)(i64)("print_work")
                in
                    let zero = constInt(i64)(0u64)(false)
                    in
                        let _ = buildStore(builder)(zero)(indexSlot)
                        in
                            let isNegative = buildICmp(builder)(intPredicateSlt)(value)(zero)("is_negative")
                            in
                                let negated = buildSub(builder)(zero)(value)("negated_value")
                                in
                                    let absValue = buildSelect(builder)(isNegative)(negated)(value)("abs_value")
                                    in
                                        let _ = buildStore(builder)(absValue)(workSlot)
                                        in PrintIntState(buffer = buffer, bufferType = bufferType, indexSlot = indexSlot, workSlot = workSlot, isNegative = isNegative))

// One iteration of the decimal digit loop: peel the last base-10 digit off `work` (already loaded
// by the caller), write its ASCII byte, and advance both `workSlot` (for the next iteration's
// `loopCheckBlock` read) and `indexSlot`. Matches `LlvmCodegenPlatform.cs`'s own
// `EmitPrintIntDigitLoopBody` — buffer filled from the END backward (`31 - index`), so the digits
// land in the correct left-to-right order without a separate reverse pass.
let printIntDigitLoopBody builder i64 i8 printState work =
    match printState with
        | PrintIntState { bufferType = bufferType, buffer = buffer, workSlot = workSlot, indexSlot = indexSlot } ->
            let ten = constInt(i64)(10u64)(false)
            in
                let digit = buildSRem(builder)(work)(ten)("digit")
                in
                    let nextWork = buildSDiv(builder)(work)(ten)("next_work")
                    in
                        let _ = buildStore(builder)(nextWork)(workSlot)
                        in
                            let idx = buildLoad(builder)(i64)(indexSlot)("digit_idx")
                            in
                                let writeIndex = buildSub(builder)(constInt(i64)(31u64)(false))(idx)("digit_write_index")
                                in
                                    let asciiDigit = buildAdd(builder)(digit)(constInt(i64)(48u64)(false))("ascii_digit")
                                    in
                                        let _ = storePrintBufferByte(builder)(i64)(i8)(bufferType)(buffer)(writeIndex)(asciiDigit)
                                        in
                                            let idxNext = buildAdd(builder)(idx)(constInt(i64)(1u64)(false))("idx_inc")
                                            in buildStore(builder)(idxNext)(indexSlot)

// Writes the filled portion of the digit buffer (`32 - count` bytes, since it was filled from the
// end) via the raw `write` syscall, then a single-byte `\n` from its own one-byte stack slot — two
// writes total, matching `LlvmCodegenPlatform.cs`'s own `EmitPrintIntWriteAndNewline`. No shared
// global newline constant (the real backend's `EmitStackByteArray` copies one from a `.rodata`
// blob): a fresh one-byte `alloca` avoids needing any global data or the relocation it would cost,
// for one byte it is not worth sharing anyway.
let printIntWriteAndNewline builder i64 i8 printState =
    match printState with
        | PrintIntState { bufferType = bufferType, buffer = buffer, indexSlot = indexSlot } ->
            let count = buildLoad(builder)(i64)(indexSlot)("print_count")
            in
                let startIndex = buildSub(builder)(constInt(i64)(32u64)(false))(count)("start_index")
                in
                    let zero = constInt(i64)(0u64)(false)
                    in
                        let dataPtr = buildGEP(builder)(bufferType)(buffer)([zero, startIndex])(2u32)("print_data_ptr")
                        in
                            let dataAddr = buildPtrToInt(builder)(dataPtr)(i64)("print_data_addr")
                            in
                                let stdoutFd = constInt(i64)(1u64)(false)
                                in
                                    let _ = emitLinuxWrite(builder)(i64)(stdoutFd)(dataAddr)(count)
                                    in
                                        let newlineByte = buildAlloca(builder)(i8)("print_newline")
                                        in
                                            let _ = buildStore(builder)(constInt(i8)(10u64)(false))(newlineByte)
                                            in
                                                let newlineAddr = buildPtrToInt(builder)(newlineByte)(i64)("print_newline_addr")
                                                in emitLinuxWrite(builder)(i64)(stdoutFd)(newlineAddr)(constInt(i64)(1u64)(false))

// Orchestrates `PrintInt`'s six extra basic blocks (zero/loop-check/loop-body/maybe-sign/sign/write,
// plus the continuation the rest of the function's codegen resumes into) around the three helpers
// above, matching `LlvmCodegenPlatform.cs`'s own `EmitPrintInt` block-for-block: a `0` value skips
// the digit loop entirely (its remainder-of-zero loop-exit condition never fires the way it should
// for the value `0` itself), the loop peels digits until `work` reaches `0`, and a sign byte is
// written only for a genuinely negative input.
let emitPrintInt context function_ i64 builder value =
    (let i8 = int8Type(context)
    in
        let printState = printIntPrologue(builder)(i64)(i8)(value)
        in
            match printState with
                | PrintIntState { bufferType = bufferType, buffer = buffer, indexSlot = indexSlot, workSlot = workSlot, isNegative = isNegative } ->
                    let zero = constInt(i64)(0u64)(false)
                    in
                        let zeroBlock = appendBasicBlock(context)(function_)("print_int_zero")
                        in
                            let loopCheckBlock = appendBasicBlock(context)(function_)("print_int_loop_check")
                            in
                                let loopBodyBlock = appendBasicBlock(context)(function_)("print_int_loop_body")
                                in
                                    let maybeSignBlock = appendBasicBlock(context)(function_)("print_int_maybe_sign")
                                    in
                                        let signBlock = appendBasicBlock(context)(function_)("print_int_sign")
                                        in
                                            let writeBlock = appendBasicBlock(context)(function_)("print_int_write")
                                            in
                                                let continueBlock = appendBasicBlock(context)(function_)("print_int_continue")
                                                in
                                                    let initialWork = buildLoad(builder)(i64)(workSlot)("initial_work")
                                                    in
                                                        let isZero = buildICmp(builder)(intPredicateEq)(initialWork)(zero)("is_zero")
                                                        in
                                                            let _ = buildCondBr(builder)(isZero)(zeroBlock)(loopCheckBlock)
                                                            in
                                                                let _ = positionBuilderAtEnd(builder)(zeroBlock)
                                                                in
                                                                    let zeroDigit = constInt(i64)(48u64)(false)
                                                                    in
                                                                        let _ = storePrintBufferByte(builder)(i64)(i8)(bufferType)(buffer)(constInt(i64)(31u64)(false))(zeroDigit)
                                                                        in
                                                                            let _ = buildStore(builder)(constInt(i64)(1u64)(false))(indexSlot)
                                                                            in
                                                                                let _ = buildBr(builder)(writeBlock)
                                                                                in
                                                                                    let _ = positionBuilderAtEnd(builder)(loopCheckBlock)
                                                                                    in
                                                                                        let work = buildLoad(builder)(i64)(workSlot)("work_value")
                                                                                        in
                                                                                            let loopDone = buildICmp(builder)(intPredicateEq)(work)(zero)("loop_done")
                                                                                            in
                                                                                                let _ = buildCondBr(builder)(loopDone)(maybeSignBlock)(loopBodyBlock)
                                                                                                in
                                                                                                    let _ = positionBuilderAtEnd(builder)(loopBodyBlock)
                                                                                                    in
                                                                                                        let _ = printIntDigitLoopBody(builder)(i64)(i8)(printState)(work)
                                                                                                        in
                                                                                                            let _ = buildBr(builder)(loopCheckBlock)
                                                                                                            in
                                                                                                                let _ = positionBuilderAtEnd(builder)(maybeSignBlock)
                                                                                                                in
                                                                                                                    let _ = buildCondBr(builder)(isNegative)(signBlock)(writeBlock)
                                                                                                                    in
                                                                                                                        let _ = positionBuilderAtEnd(builder)(signBlock)
                                                                                                                        in
                                                                                                                            let idxBeforeSign = buildLoad(builder)(i64)(indexSlot)("idx_before_sign")
                                                                                                                            in
                                                                                                                                let signIndex = buildSub(builder)(constInt(i64)(31u64)(false))(idxBeforeSign)("sign_index")
                                                                                                                                in
                                                                                                                                    let minusSign = constInt(i64)(45u64)(false)
                                                                                                                                    in
                                                                                                                                        let _ = storePrintBufferByte(builder)(i64)(i8)(bufferType)(buffer)(signIndex)(minusSign)
                                                                                                                                        in
                                                                                                                                            let idxWithSign = buildAdd(builder)(idxBeforeSign)(constInt(i64)(1u64)(false))("idx_with_sign")
                                                                                                                                            in
                                                                                                                                                let _ = buildStore(builder)(idxWithSign)(indexSlot)
                                                                                                                                                in
                                                                                                                                                    let _ = buildBr(builder)(writeBlock)
                                                                                                                                                    in
                                                                                                                                                        let _ = positionBuilderAtEnd(builder)(writeBlock)
                                                                                                                                                        in
                                                                                                                                                            let _ = printIntWriteAndNewline(builder)(i64)(i8)(printState)
                                                                                                                                                            in
                                                                                                                                                                let _ = buildBr(builder)(continueBlock)
                                                                                                                                                                in positionBuilderAtEnd(builder)(continueBlock))

let codegenInstructionKind cx builder kind state =
    match state with
        | (tempEnv, terminated) ->
            match cx with
                | CodegenContext { context = context, function_ = function_, i64 = i64, localSlots = localSlots, labelBlocks = labelBlocks } ->
                    match kind with
                        | LoadConstInt(target, value) -> ((target, constInt(i64)(Ashes.Number.UInt.fromInt64(value))(true)) :: tempEnv, terminated)
                        | MulInt(target, left, right) -> ((target, buildMul(builder)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                        | AddInt(target, left, right) -> ((target, buildAdd(builder)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                        | SubInt(target, left, right) -> ((target, buildSub(builder)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
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
                        | PrintInt(source) ->
                            let _ = emitPrintInt(context)(function_)(i64)(builder)(lookupIndexed(source)(tempEnv))
                            in (tempEnv, false)
                        // A zero-field, arena-shaped (`runtimeManaged = false`) `AllocAdt` — exactly what
                        // a `Unit` result (e.g. `PrintInt`'s own return value) lowers to — gets a plain
                        // stack `alloca` standing in for a real arena bump allocation: this program shape
                        // never loops around a top-level `AllocAdt`, so a stack slot per call site never
                        // accumulates. This is NOT a substitute for real scoped-arena codegen (a future
                        // instruction sequence that allocates inside a loop body would leak native stack
                        // every iteration, exactly the failure mode documented in
                        // docs/md/future/SELF_HOSTING.md's entry-block-alloca checklist item) — it only
                        // covers today's single-shot, non-looping call sites. Any field-carrying or
                        // RC-managed `AllocAdt` panics rather than silently miscompiling.
                        | AllocAdt(target, tag, fieldCount, runtimeManaged) ->
                            if runtimeManaged
                            then Ashes.IO.panic("codegen: RC-managed AllocAdt not yet supported")
                            else
                                if fieldCount != 0
                                then Ashes.IO.panic("codegen: AllocAdt with fields not yet supported")
                                else
                                    let cell = buildAlloca(builder)(i64)("adt_cell")
                                    in
                                        let _ = buildStore(builder)(constInt(i64)(Ashes.Number.UInt.fromInt64(tag))(false))(cell)
                                        in ((target, buildPtrToInt(builder)(cell)(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
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
