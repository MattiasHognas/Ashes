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
// - `CodegenContext` bundles everything that stays fixed for a whole function (`context`/
//   `function_`, the scalar LLVM types in `CoreLlvmTypes`, the declared libc entry points in
//   `ExternalFunctions`, `localSlots`, `labelBlocks`, `stringLiteralGlobals`) so it threads through
//   as one value instead of an ever-growing parameter list; only `tempEnv` actually grows
//   instruction by instruction. Deeply nested per-block/per-phase construction (a basic block's
//   own several simultaneously-live values feeding one branch, say) is bundled into a small record
//   too (`PrintIntBlocks`, `StrCmpBlocks`, `PrintIntState`) rather than threaded positionally, and
//   the actual block-by-block emission is split into small named phase functions — `emitPrintInt`
//   and `emitStringEquals` are each a short linear sequence of such phases, not one long `let`
//   staircase.
// - `codegenProgram` builds the true program entry AND every lifted function in
//   `IrProgram.functions` (the ordinary helper functions a real program has: every top-level
//   `let f x = ...`, every lambda, every curried partial application), all as
//   `i64 label(i64 env, i64 arg, i64 flag)` with the exact calling convention
//   `LlvmCodegenExpressions.cs`'s `EmitCallClosure`/`EmitCallKnown` use. A lifted function's
//   `Return` is an ordinary `ret` of its result word. The entry function's `Return` is instead
//   lowered the way `LlvmCodegenExpressions.cs`'s `EmitReturn` lowers ONLY the entry function's
//   `Return`: normal program completion is not a `ret` at all — there is no return address on the
//   stack once the OS has jumped straight to this code as the process's actual entry point — it
//   is a raw Linux `exit` syscall (`60`, matching real Ashes semantics: the process always exits
//   `0` on normal completion; a different code needs the separate `Ashes.IO.exit`/`ExitProcess`
//   instruction, not attempted here) followed by `buildUnreachable`, since a syscall that
//   terminates the process never returns to the caller. The entry `Return`'s own `source` temp is
//   therefore unused — the computed value it names was real IR arithmetic and is still genuinely
//   built, just never surfaced as an exit code. `AshesCompiler.Backend.ElfLinker` (linux-x64)
//   links this codegen's output into a directly-runnable executable, so this is observable by
//   actually running one: `strace` shows a single `exit(0)` syscall and nothing else, matching the
//   disassembly's `syscall`+`unreachable` tail.
// - Closures are the real 32-byte `{code, env, packedEnvironmentSize, dropper}` objects
//   `LlvmCodegenExpressions.cs` lays out (`MakeClosure`/`MakeClosureStack`), called indirectly
//   through their `code` word (`CallClosure`) or directly by label once `IrOptimizer.ash` has
//   devirtualized the call (`CallKnown`); a captured environment is an `Alloc`'d block written
//   with `StoreMemOffset` and read back inside the callee with `LoadEnv` through local slot `0`.
//   Every non-RC-managed allocation this needs (`Alloc`, `MakeClosure`) is a bare `malloc` standing
//   in for the scoped-arena bump allocation stage 0 would make — never a stack slot, since a
//   closure and its environment routinely outlive the frame that built them — and is never freed.

import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Backend.Llvm
import Ashes.Number.UInt
export (
    value codegenEntryFunction,
    value codegenProgram,
)

// The scalar LLVM types every instruction case may need, computed once per module.
type CoreLlvmTypes =
    | i64: LLVMTypeRef
    | i1: LLVMTypeRef
    | i8: LLVMTypeRef
    | i32: LLVMTypeRef
    | ptrType: LLVMTypeRef

let coreLlvmTypes context =
    CoreLlvmTypes(
        i64 = int64Type(context),
        i1 = int1Type(context),
        i8 = int8Type(context),
        i32 = int32Type(context),
        ptrType = pointerType(context)(0u32)
    )

// The narrow set of libc entry points this codegen can call — `malloc`/`free` for RC-managed
// `AllocAdt`/`RcDrop`, `memcmp` for `CmpStrEq`/`CmpStrNe`, `memcpy` for `ConcatStr`/`ConcatStrN` —
// declared once per module, matching `AshesCompiler.Backend.ElfLinker`'s own recognized-symbol
// table (any new entry here needs a matching one-line addition there).
type ExternalFunctions =
    | mallocFn: LLVMValueRef
    | mallocType: LLVMTypeRef
    | freeFn: LLVMValueRef
    | freeType: LLVMTypeRef
    | memcmpFn: LLVMValueRef
    | memcmpType: LLVMTypeRef
    | memcpyFn: LLVMValueRef
    | memcpyType: LLVMTypeRef

let declareExternalFunctions module_ context types =
    (let mallocType = functionType(types.ptrType)([types.i64])(1u32)(false)
    in
        let freeType =
            functionType(voidType(context))([types.ptrType])(1u32)(false)
        in
            let memcmpType = functionType(types.i32)([types.ptrType, types.ptrType, types.i64])(3u32)(false)
            in
                let memcpyType = functionType(types.ptrType)([types.ptrType, types.ptrType, types.i64])(3u32)(false)
                in
                    ExternalFunctions(
                        mallocFn = addFunction(module_)("malloc")(mallocType),
                        mallocType = mallocType,
                        freeFn = addFunction(module_)("free")(freeType),
                        freeType = freeType,
                        memcmpFn = addFunction(module_)("memcmp")(memcmpType),
                        memcmpType = memcmpType,
                        memcpyFn = addFunction(module_)("memcpy")(memcpyType),
                        memcpyType = memcpyType
                    ))

// Bundles everything that stays fixed for a whole function so it threads through as one value
// instead of an ever-growing parameter list; only `tempEnv` (in `codegenInstructionKind`'s own
// fold state) actually grows instruction by instruction.
type CodegenContext =
    | context: LLVMContextRef
    | function_: LLVMValueRef
    | types: CoreLlvmTypes
    | externals: ExternalFunctions
    | localSlots: List((IrLocal, LLVMValueRef))
    | labelBlocks: List((Str, LLVMBasicBlockRef))
    | stringLiteralGlobals: List((Str, LLVMValueRef))
    | liftedFunctions: List((Str, LLVMValueRef))
    | closureFunctionType: LLVMTypeRef
    | isEntry: Bool

// Everything shared by every function in one module — computed once by `codegenFunctions`, then
// handed to each function body's own `CodegenContext` construction unchanged.
type ModuleCodegen =
    | moduleRef: LLVMModuleRef
    | moduleContext: LLVMContextRef
    | moduleTypes: CoreLlvmTypes
    | moduleExternals: ExternalFunctions
    | moduleStringLiteralGlobals: List((Str, LLVMValueRef))
    | moduleLiftedFunctions: List((Str, LLVMValueRef))
    | moduleClosureFunctionType: LLVMTypeRef
    | moduleBuilder: LLVMBuilderRef

// `i64 f(i64 env, i64 arg, i64 argumentOwnershipFlag)`: the one uniform native signature every
// lifted (non-entry) function has, matching `LlvmCodegenExpressions.cs`'s own `EmitCallClosure`/
// `EmitCallKnown` exactly — a closure call site never knows its callee's source-level arity, so
// every function takes its environment word and ONE argument word (currying supplies the rest via
// nested closures) plus the runtime-managed-argument flag `LoadArgumentOwnership` reads back.
let closureFunctionTypeOf i64 = functionType(i64)([i64, i64, i64])(3u32)(false)

// Every lifted function gets `internal` linkage, as `LlvmCodegen.cs`'s own declaration loop
// gives it — nothing outside the module ever names one. Not merely tidiness: a modern LLVM no
// longer treats an external-linkage symbol as `dso_local` under the static relocation model, so
// taking a default-linkage function's address (`MakeClosure`'s code word) compiles to a
// GOT-relative load (`R_X86_64_REX_GOTPCRELX`) that no GOT exists to satisfy here, whereas an
// internal symbol's address is a plain `.text`-relative reference and its `call` sites need no
// relocation at all.
let recursive declareLiftedFunctions module_ closureFnType functions =
    match functions with
        | [] -> []
        | function_ :: rest ->
            match function_ with
                | IrFunction { label = label } ->
                    let functionValue = addFunction(module_)(label)(closureFnType)
                    in
                        let _ = setLinkage(functionValue)(linkageInternal)
                        in (label, functionValue) :: declareLiftedFunctions(module_)(closureFnType)(rest)

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
// `exitCode` is an already-built `i64` value, not a compile-time literal, so both the entry
// function's own always-`0` `Return` and `PanicStr`'s always-`1` exit share this one helper.
let emitLinuxProcessExitWithCode builder i64 exitCode =
    (let zero = constInt(i64)(0u64)(false)
    in
        let _ =
            emitLinuxSyscallCall(builder)(i64)(constInt(i64)(60u64)(false))(exitCode)(zero)(zero)("sys_exit")
        in buildUnreachable(builder))

let emitLinuxProcessExit builder i64 =
    false
    |> constInt(i64)(0u64)
    |> emitLinuxProcessExitWithCode(builder)(i64)

// `write(fd, ptr, len)` — the raw, unbuffered path `LlvmCodegenPlatform.cs`'s own `EmitWriteBytesRaw`
// takes when a program never touches `Ashes.IO.writeBuffered`/`flush` (the only path this codegen
// implements; a buffered stdout ring, its lock, and the flush-on-exit contract are a separate,
// bigger, unattempted slice). `ptr` is an `i64` address (from `buildPtrToInt`), not an LLVM
// pointer value — every syscall argument here is a plain register-width word.
let emitLinuxWrite builder i64 fd ptr len =
    emitLinuxSyscallCall(builder)(i64)(constInt(i64)(1u64)(false))(fd)(ptr)(len)("sys_write")

// Reads a runtime-managed `Str` value's own `[len:i64][bytes...]` header (word `0` is `len`, byte
// offset `8` is where the raw bytes start — the SAME layout `LoadConstStr`'s global builds, and
// the general one every real `Str` value uses, not just a literal) and writes it to stdout via the
// raw `write` syscall, then a trailing newline byte — matching `LlvmCodegenExpressions.cs`'s own
// `EmitPrintStringFromTemp(appendNewline: true)` exactly. `stringRef`'s own `i64` value doubles as
// the byte address once offset by `8`, so writing needs no extra pointer round-trip beyond the one
// `buildLoad` already needs to read `len`. Shared by `PrintStr` and `PanicStr` — stage 0's own
// `EmitPanic` prints its message through this exact same helper (`EmitPrintStringFromTemp`) before
// exiting, not a stderr-specific write.
let emitPrintStrBytesWithNewline builder i64 i8 ptrType stringRef =
    (let basePtr = buildIntToPtr(builder)(stringRef)(ptrType)("str_len_ptr")
    in
        let len = buildLoad(builder)(i64)(basePtr)("str_len")
        in
            let byteAddress =
                buildAdd(builder)(stringRef)(constInt(i64)(8u64)(false))("str_bytes_addr")
            in
                let _ =
                    emitLinuxWrite(builder)(i64)(constInt(i64)(1u64)(false))(byteAddress)(len)
                in
                    let newlineBuf = buildAlloca(builder)(i8)("print_str_newline")
                    in
                        let _ =
                            buildStore(builder)(constInt(i8)(10u64)(false))(newlineBuf)
                        in
                            let newlineAddr = buildPtrToInt(builder)(newlineBuf)(i64)("newline_addr")
                            in
                                false
                                |> constInt(i64)(1u64)
                                |> emitLinuxWrite(builder)(i64)(constInt(i64)(1u64)(false))(newlineAddr))

// The four basic blocks `emitStringEquals`'s three-way branch (lengths differ / lengths match but
// bytes differ / bytes match) needs, bundled so each phase helper below takes one value instead of
// four positional block parameters.
type StrCmpBlocks =
    | lenEqBlock: LLVMBasicBlockRef
    | notEqBlock: LLVMBasicBlockRef
    | eqBlock: LLVMBasicBlockRef
    | continueBlock: LLVMBasicBlockRef

let createStrCmpBlocks context function_ =
    StrCmpBlocks(
        lenEqBlock = appendBasicBlock(context)(function_)("str_cmp_len_eq"),
        notEqBlock = appendBasicBlock(context)(function_)("str_cmp_not_eq"),
        eqBlock = appendBasicBlock(context)(function_)("str_cmp_eq"),
        continueBlock = appendBasicBlock(context)(function_)("str_cmp_continue")
    )

// Two `Str` values of different length can never be equal, so `memcmp` (in `emitStrCmpByteCompare`
// below) is only ever reached once lengths already match.
let emitStrCmpLenCheck builder leftLen rightLen blocks =
    buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(leftLen)(rightLen)("str_cmp_len_match"))(blocks.lenEqBlock)(blocks.notEqBlock)

let emitStrCmpNotEqualPath builder i64 resultSlot blocks =
    (let _ = positionBuilderAtEnd(builder)(blocks.notEqBlock)
    in
        let _ =
            buildStore(builder)(constInt(i64)(0u64)(false))(resultSlot)
        in buildBr(builder)(blocks.continueBlock))

// The one block that actually calls `memcmp`, reached only once lengths already match.
let emitStrCmpByteCompare context i64 ptrType builder memcmpFn memcmpType leftRef rightRef leftLen blocks =
    (let _ = positionBuilderAtEnd(builder)(blocks.lenEqBlock)
    in
        let leftBytesAddr =
            buildAdd(builder)(leftRef)(constInt(i64)(8u64)(false))("str_cmp_left_bytes_addr")
        in
            let rightBytesAddr =
                buildAdd(builder)(rightRef)(constInt(i64)(8u64)(false))("str_cmp_right_bytes_addr")
            in
                let leftBytesPtr = buildIntToPtr(builder)(leftBytesAddr)(ptrType)("str_cmp_left_bytes_ptr")
                in
                    let rightBytesPtr = buildIntToPtr(builder)(rightBytesAddr)(ptrType)("str_cmp_right_bytes_ptr")
                    in
                        let cmpResult = buildCall(builder)(memcmpType)(memcmpFn)([leftBytesPtr, rightBytesPtr, leftLen])(3u32)("str_cmp_memcmp")
                        in
                            let isZero =
                                buildICmp(builder)(intPredicateEq)(cmpResult)(constInt(int32Type(context))(0u64)(false))("str_cmp_is_eq")
                            in buildCondBr(builder)(isZero)(blocks.eqBlock)(blocks.notEqBlock))

let emitStrCmpEqualPath builder i64 resultSlot blocks =
    (let _ = positionBuilderAtEnd(builder)(blocks.eqBlock)
    in
        let _ =
            buildStore(builder)(constInt(i64)(1u64)(false))(resultSlot)
        in buildBr(builder)(blocks.continueBlock))

// Matches `LlvmCodegenMemory.cs`'s own `EmitStringComparison` exactly: a length check first, then
// a real libc `memcmp` call over the raw payload bytes once lengths already match — the same
// declare-and-call pattern `malloc`/`free` already established for an external symbol this codegen
// needs (`AshesCompiler.Backend.ElfLinker` picks up any new `.text` call to a name in its own
// `linuxDynamicImportLibraries` table automatically, so `memcmp` needed only a one-line addition
// there, no new linker mechanism). No `phi` binding exists in this package's LLVM surface, so the
// three-way branch above merges through a `resultSlot` alloca exactly like `PrintIntState`'s own
// slot-based merge and every other branch-merge in this file. Returns a plain `i64` `0`/`1` — the
// same representation `CmpIntEq`'s `buildZExt` already establishes for every boolean result in
// this codegen — so `CmpStrNe` can invert it with a plain `1 - result` rather than re-deriving the
// comparison.
let emitStringEquals context function_ i64 ptrType builder memcmpFn memcmpType leftRef rightRef =
    (let resultSlot = buildAlloca(builder)(i64)("str_cmp_result")
    in
        let leftLenPtr = buildIntToPtr(builder)(leftRef)(ptrType)("str_cmp_left_len_ptr")
        in
            let rightLenPtr = buildIntToPtr(builder)(rightRef)(ptrType)("str_cmp_right_len_ptr")
            in
                let leftLen = buildLoad(builder)(i64)(leftLenPtr)("str_cmp_left_len")
                in
                    let rightLen = buildLoad(builder)(i64)(rightLenPtr)("str_cmp_right_len")
                    in
                        let blocks = createStrCmpBlocks(context)(function_)
                        in
                            let _ = emitStrCmpLenCheck(builder)(leftLen)(rightLen)(blocks)
                            in
                                let _ = emitStrCmpNotEqualPath(builder)(i64)(resultSlot)(blocks)
                                in
                                    let _ = emitStrCmpByteCompare(context)(i64)(ptrType)(builder)(memcmpFn)(memcmpType)(leftRef)(rightRef)(leftLen)(blocks)
                                    in
                                        let _ = emitStrCmpEqualPath(builder)(i64)(resultSlot)(blocks)
                                        in
                                            let _ = positionBuilderAtEnd(builder)(blocks.continueBlock)
                                            in buildLoad(builder)(i64)(resultSlot)("str_cmp_result_value"))

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
        in
            buildStore(builder)(buildTrunc(builder)(value)(i8)("to_i8"))(ptr))

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
// land in the correct left-to-right order without a separate reverse pass. The division is
// UNSIGNED (`urem`/`udiv`), never `srem`/`sdiv`: `printIntPrologue`'s `0 - value` negation of
// `Int.min` overflows back to `Int.min` itself, whose bit pattern read unsigned is exactly the
// magnitude `9223372036854775808` — signed division would instead peel negative "digits" off it
// and print garbage for that one value.
let printIntDigitLoopBody builder i64 i8 printState work =
    match printState with
        | PrintIntState { bufferType = bufferType, buffer = buffer, workSlot = workSlot, indexSlot = indexSlot } ->
            let ten = constInt(i64)(10u64)(false)
            in
                let digit = buildURem(builder)(work)(ten)("digit")
                in
                    let nextWork = buildUDiv(builder)(work)(ten)("next_work")
                    in
                        let _ = buildStore(builder)(nextWork)(workSlot)
                        in
                            let idx = buildLoad(builder)(i64)(indexSlot)("digit_idx")
                            in
                                let writeIndex =
                                    buildSub(builder)(constInt(i64)(31u64)(false))(idx)("digit_write_index")
                                in
                                    let asciiDigit =
                                        buildAdd(builder)(digit)(constInt(i64)(48u64)(false))("ascii_digit")
                                    in
                                        let _ = storePrintBufferByte(builder)(i64)(i8)(bufferType)(buffer)(writeIndex)(asciiDigit)
                                        in
                                            let idxNext =
                                                buildAdd(builder)(idx)(constInt(i64)(1u64)(false))("idx_inc")
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
                let startIndex =
                    buildSub(builder)(constInt(i64)(32u64)(false))(count)("start_index")
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
                                            let _ =
                                                buildStore(builder)(constInt(i8)(10u64)(false))(newlineByte)
                                            in
                                                let newlineAddr = buildPtrToInt(builder)(newlineByte)(i64)("print_newline_addr")
                                                in
                                                    false
                                                    |> constInt(i64)(1u64)
                                                    |> emitLinuxWrite(builder)(i64)(stdoutFd)(newlineAddr)

// `PrintInt`'s six extra basic blocks, bundled so each phase helper below takes one value instead
// of six positional block parameters.
type PrintIntBlocks =
    | zeroBlock: LLVMBasicBlockRef
    | loopCheckBlock: LLVMBasicBlockRef
    | loopBodyBlock: LLVMBasicBlockRef
    | maybeSignBlock: LLVMBasicBlockRef
    | signBlock: LLVMBasicBlockRef
    | writeBlock: LLVMBasicBlockRef
    | continueBlock: LLVMBasicBlockRef

let createPrintIntBlocks context function_ =
    PrintIntBlocks(
        zeroBlock = appendBasicBlock(context)(function_)("print_int_zero"),
        loopCheckBlock = appendBasicBlock(context)(function_)("print_int_loop_check"),
        loopBodyBlock = appendBasicBlock(context)(function_)("print_int_loop_body"),
        maybeSignBlock = appendBasicBlock(context)(function_)("print_int_maybe_sign"),
        signBlock = appendBasicBlock(context)(function_)("print_int_sign"),
        writeBlock = appendBasicBlock(context)(function_)("print_int_write"),
        continueBlock = appendBasicBlock(context)(function_)("print_int_continue")
    )

// A `0` value skips the digit loop entirely (its remainder-of-zero loop-exit condition never fires
// the way it should for the value `0` itself), so entry dispatches straight to `zeroBlock`;
// anything else falls into the ordinary digit loop.
let emitPrintIntEntryDispatch builder i64 printState blocks =
    (let initialWork = buildLoad(builder)(i64)(printState.workSlot)("initial_work")
    in
        let isZero =
            buildICmp(builder)(intPredicateEq)(initialWork)(constInt(i64)(0u64)(false))("is_zero")
        in buildCondBr(builder)(isZero)(blocks.zeroBlock)(blocks.loopCheckBlock))

let emitPrintIntZeroPath builder i64 i8 printState blocks =
    (let _ = positionBuilderAtEnd(builder)(blocks.zeroBlock)
    in
        let _ =
            false
            |> constInt(i64)(48u64)
            |> storePrintBufferByte(builder)(i64)(i8)(printState.bufferType)(printState.buffer)(constInt(i64)(31u64)(false))
        in
            let _ =
                buildStore(builder)(constInt(i64)(1u64)(false))(printState.indexSlot)
            in buildBr(builder)(blocks.writeBlock))

// Peels one base-10 digit per iteration (`printIntDigitLoopBody`) until `work` reaches `0`.
let emitPrintIntLoop builder i64 i8 printState blocks =
    (let _ = positionBuilderAtEnd(builder)(blocks.loopCheckBlock)
    in
        let work = buildLoad(builder)(i64)(printState.workSlot)("work_value")
        in
            let loopDone =
                buildICmp(builder)(intPredicateEq)(work)(constInt(i64)(0u64)(false))("loop_done")
            in
                let _ = buildCondBr(builder)(loopDone)(blocks.maybeSignBlock)(blocks.loopBodyBlock)
                in
                    let _ = positionBuilderAtEnd(builder)(blocks.loopBodyBlock)
                    in
                        let _ = printIntDigitLoopBody(builder)(i64)(i8)(printState)(work)
                        in buildBr(builder)(blocks.loopCheckBlock))

// A sign byte is written only for a genuinely negative input.
let emitPrintIntSignPath builder i64 i8 printState blocks =
    (let _ = positionBuilderAtEnd(builder)(blocks.maybeSignBlock)
    in
        let _ = buildCondBr(builder)(printState.isNegative)(blocks.signBlock)(blocks.writeBlock)
        in
            let _ = positionBuilderAtEnd(builder)(blocks.signBlock)
            in
                let idxBeforeSign = buildLoad(builder)(i64)(printState.indexSlot)("idx_before_sign")
                in
                    let signIndex =
                        buildSub(builder)(constInt(i64)(31u64)(false))(idxBeforeSign)("sign_index")
                    in
                        let _ =
                            false
                            |> constInt(i64)(45u64)
                            |> storePrintBufferByte(builder)(i64)(i8)(printState.bufferType)(printState.buffer)(signIndex)
                        in
                            let idxWithSign =
                                buildAdd(builder)(idxBeforeSign)(constInt(i64)(1u64)(false))("idx_with_sign")
                            in
                                let _ = buildStore(builder)(idxWithSign)(printState.indexSlot)
                                in buildBr(builder)(blocks.writeBlock))

let emitPrintIntWritePath builder i64 i8 printState blocks =
    (let _ = positionBuilderAtEnd(builder)(blocks.writeBlock)
    in
        let _ = printIntWriteAndNewline(builder)(i64)(i8)(printState)
        in
            let _ = buildBr(builder)(blocks.continueBlock)
            in positionBuilderAtEnd(builder)(blocks.continueBlock))

// Orchestrates `PrintInt`'s six extra basic blocks (zero/loop-check/loop-body/maybe-sign/sign/write,
// plus the continuation the rest of the function's codegen resumes into) around the phase helpers
// above, matching `LlvmCodegenPlatform.cs`'s own `EmitPrintInt` block-for-block.
let emitPrintInt context function_ i64 builder value =
    (let i8 = int8Type(context)
    in
        let printState = printIntPrologue(builder)(i64)(i8)(value)
        in
            let blocks = createPrintIntBlocks(context)(function_)
            in
                let _ = emitPrintIntEntryDispatch(builder)(i64)(printState)(blocks)
                in
                    let _ = emitPrintIntZeroPath(builder)(i64)(i8)(printState)(blocks)
                    in
                        let _ = emitPrintIntLoop(builder)(i64)(i8)(printState)(blocks)
                        in
                            let _ = emitPrintIntSignPath(builder)(i64)(i8)(printState)(blocks)
                            in emitPrintIntWritePath(builder)(i64)(i8)(printState)(blocks))

// Byte-offset pointer arithmetic shared by `AllocAdt`'s RC-managed header/payload writes and
// `SetAdtField`'s field store: an `i8`-element `buildGEP` with a single scalar index, the same
// "different element type than every struct/array `buildGEP` use" shape
// `selfhost/tests/backend/Main.ash`'s own `defineRcAllocFunction`/`defineRcRetainFunction`
// established for RC header arithmetic.
let gepBytes builder i64 i8 ptr offset name =
    buildGEP(builder)(i8)(ptr)([constInt(i64)(Ashes.Number.UInt.fromInt64(offset))(false)])(1u32)(name)

// The RC-managed `AllocAdt` branch: `malloc`s the real 16-byte `{i64 reference_count, i64
// allocation_size}` header from architecture.md plus one `i64` word per tag/field
// (`[tag][field0]...[fieldN-1]`, matching architecture.md's own `ADT / record` layout row), writes
// `count = 1` and the payload size, and returns the PAYLOAD pointer (past the header) as this
// codegen's universal `i64` word representation — the same "public pointer never carries the
// header" contract `selfhost/tests/backend/Main.ash`'s own `defineRcAllocFunction` established.
// `SetAdtField` writes into this same payload region. **Explicitly out of scope**: nothing drops
// this allocation yet (`CoreLowering.ash` does not emit `RcDrop` anywhere for a multi-field
// value), so a runtime-managed value from this path leaks today — an explicit, temporary
// limitation matching every other stand-in in this arc, closed by the next slice (Perceus drop
// insertion).
// One real `malloc`'d RC-managed block — the 16-byte `{i64 reference_count, i64 allocation_size}`
// header followed by `payloadSizeBytes` of payload — initialized to `count = 1` and the payload
// size, returning the PAYLOAD pointer (past the header). Shared by every RC-managed allocation
// this codegen makes (`AllocAdt`, `Alloc`, `MakeClosure`), so all of them agree with `RcDrop`'s
// own `-16` walk back to the header.
let emitRcAllocPayloadPtr builder i64 i8 mallocFn mallocType payloadSizeBytes name =
    (let totalSize =
        constInt(i64)(Ashes.Number.UInt.fromInt64(16 + payloadSizeBytes))(false)
    in
        let headerPtr = buildCall(builder)(mallocType)(mallocFn)([totalSize])(1u32)(name + "_header")
        in
            let sizePtr = gepBytes(builder)(i64)(i8)(headerPtr)(8)(name + "_size_ptr")
            in
                let _ =
                    buildStore(builder)(constInt(i64)(1u64)(false))(headerPtr)
                in
                    let _ =
                        buildStore(builder)(constInt(i64)(Ashes.Number.UInt.fromInt64(payloadSizeBytes))(false))(sizePtr)
                    in gepBytes(builder)(i64)(i8)(headerPtr)(16)(name + "_payload_ptr"))

let emitAllocAdtRuntimeManaged builder i64 i8 mallocFn mallocType tag fieldCount resultName =
    (let payloadPtr = emitRcAllocPayloadPtr(builder)(i64)(i8)(mallocFn)(mallocType)((fieldCount + 1) * 8)("adt")
    in
        let _ =
            buildStore(builder)(constInt(i64)(Ashes.Number.UInt.fromInt64(tag))(false))(payloadPtr)
        in buildPtrToInt(builder)(payloadPtr)(i64)(resultName))

// A plain `malloc` of `sizeBytes` with no header at all: the stand-in for a scoped-arena bump
// allocation (`Alloc`/`MakeClosure` with `runtimeManaged = false`, which `CoreLowering.ash` emits
// for every closure environment and closure object today) — this codegen has no real arena, and
// a stack `alloca` would be wrong here because both a closure object and its environment
// routinely outlive the function that built them (a curried function RETURNS the closure that
// captures its first argument). Never freed: the same leak-not-miscompile trade every other
// arena stand-in in this file makes.
let emitArenaStandInAlloc builder i64 mallocFn mallocType sizeBytes name =
    buildCall(builder)(mallocType)(mallocFn)([constInt(i64)(Ashes.Number.UInt.fromInt64(sizeBytes))(false)])(1u32)(name)

// `sizeBytes` of stack storage as `[n x i64]` (`AllocStack`/`MakeClosureStack`: lowering only
// picks the stack form when it has proven the value never escapes the current frame).
let emitStackAlloc builder i64 sizeBytes name =
    buildAlloca(builder)((sizeBytes + 7) / 8
    |> Ashes.Number.UInt.fromInt64
    |> arrayType(i64))(name)

// The closure object `LlvmCodegenExpressions.cs`'s `EmitMakeClosure`/`EmitMakeClosureStack` lay
// out: four `i64` words `{code, env, packedEnvironmentSize, dropper}`. `code` is the lifted
// function's own address (`CallClosure` loads it back and calls through it), `env` the
// environment word the function receives as its first parameter, the packed word the environment
// byte size with the two ownership bits `LlvmCodegenExpressions.cs` defines (`1 << 63` = the
// result is runtime-managed, `1 << 62` = the argument is), and `dropper` the resource-cleanup
// hook (always `0` for an ordinary closure).
let closureSizeBytes = 32

let packClosureEnvironmentSize envSizeBytes returnsRuntimeManaged acceptsRuntimeManagedArgument =
    envSizeBytes + (if returnsRuntimeManaged
    then 1 << 63
    else 0) + (if acceptsRuntimeManagedArgument
    then 1 << 62
    else 0)

let emitStoreClosureWords builder i64 i8 closurePtr codeFn envRef packedSize resultName =
    (let _ =
        buildStore(builder)(buildPtrToInt(builder)(codeFn)(i64)("closure_code_word"))(closurePtr)
    in
        let _ =
            "closure_env_slot"
            |> gepBytes(builder)(i64)(i8)(closurePtr)(8)
            |> buildStore(builder)(envRef)
        in
            let _ =
                "closure_env_size_slot"
                |> gepBytes(builder)(i64)(i8)(closurePtr)(16)
                |> buildStore(builder)(constInt(i64)(Ashes.Number.UInt.fromInt64(packedSize))(false))
            in
                let _ =
                    "closure_dropper_slot"
                    |> gepBytes(builder)(i64)(i8)(closurePtr)(24)
                    |> buildStore(builder)(constInt(i64)(0u64)(false))
                in buildPtrToInt(builder)(closurePtr)(i64)(resultName))

// An indirect call through a closure object: load its `code` and `env` words, then call the code
// pointer with `(env, arg, flag)` — the same uniform signature `closureFunctionTypeOf` declares
// for every lifted function, so a direct `CallKnown` and this differ only in how the callee is
// named. `closureRef`/`argRef`/`flagRef` arrive already resolved to LLVM values (see the
// `ConcatStr` cases in `codegenInstructionKind` for why resolution stays at the call site).
let emitCallClosure builder i64 i8 ptrType closureFnType closureRef argRef flagRef resultName =
    (let closurePtr = buildIntToPtr(builder)(closureRef)(ptrType)("closure_ptr")
    in
        let codeWord = buildLoad(builder)(i64)(closurePtr)("closure_code")
        in
            let envRef =
                buildLoad(builder)(i64)(gepBytes(builder)(i64)(i8)(closurePtr)(8)("closure_env_slot"))("closure_env")
            in
                let codePtr = buildIntToPtr(builder)(codeWord)(ptrType)("closure_code_ptr")
                in buildCall(builder)(closureFnType)(codePtr)([envRef, argRef, flagRef])(3u32)(resultName))

// `LoadEnv(index)`: word `index` of the environment block whose address the function received as
// its first parameter — stored into local slot `0` on entry (see `buildFunctionContext`), exactly
// where `LlvmCodegen.cs` keeps it too, so `envSlot` is that slot's own alloca.
let emitLoadEnv builder i64 i8 ptrType envSlot index resultName =
    (let envRef = buildLoad(builder)(i64)(envSlot)("env_word")
    in
        let envPtr = buildIntToPtr(builder)(envRef)(ptrType)("env_ptr")
        in
            buildLoad(builder)(i64)(gepBytes(builder)(i64)(i8)(envPtr)(index * 8)("env_field_ptr"))(resultName))

let memOffsetPtr builder i64 i8 ptrType baseRef offsetBytes name =
    gepBytes(builder)(i64)(i8)(buildIntToPtr(builder)(baseRef)(ptrType)(name + "_base"))(offsetBytes)(name)

// The non-RC-managed, zero-field `AllocAdt` branch — exactly what a `Unit` result (e.g. `PrintInt`'s
// own return value) lowers to: a plain stack `alloca` standing in for a real arena bump allocation,
// correct only because today's supported program shapes never loop around a top-level `AllocAdt` —
// a genuine scoped-arena allocator remains a separate, much bigger slice. A non-RC-managed
// `AllocAdt` WITH fields panics rather than silently miscompiling (`CoreLowering.ash` never emits
// that combination today), so this only ever runs for `fieldCount == 0`.
let emitAllocAdtStack builder i64 tag resultName =
    (let cell = buildAlloca(builder)(i64)("adt_cell")
    in
        let _ =
            buildStore(builder)(constInt(i64)(Ashes.Number.UInt.fromInt64(tag))(false))(cell)
        in buildPtrToInt(builder)(cell)(i64)(resultName))

// Releases one RC-managed `AllocAdt` cell: walks back to the header with the NEGATIVE byte-offset
// GEP that mirrors `AllocAdt`'s own forward one (the public value pointer never carries the header
// with it — same contract as `selfhost/tests/backend/Main.ash`'s hand-built `defineRcRetainFunction`),
// decrements the count, and `free`s the header only once it reaches zero. No `phi` binding exists
// in this package's LLVM surface, so the two-way branch merges by simply falling through both
// blocks into the same `continueBlock`.
let emitRcDrop context function_ i64 i8 ptrType builder freeFn freeType sourceRef =
    (let valuePtr = buildIntToPtr(builder)(sourceRef)(ptrType)("rc_drop_value_ptr")
    in
        let headerPtr = gepBytes(builder)(i64)(i8)(valuePtr)(-16)("rc_drop_header_ptr")
        in
            let oldCount = buildLoad(builder)(i64)(headerPtr)("rc_drop_old_count")
            in
                let newCount =
                    buildSub(builder)(oldCount)(constInt(i64)(1u64)(false))("rc_drop_new_count")
                in
                    let _ = buildStore(builder)(newCount)(headerPtr)
                    in
                        let isZero =
                            buildICmp(builder)(intPredicateEq)(newCount)(constInt(i64)(0u64)(false))("rc_drop_is_zero")
                        in
                            let freeBlock = appendBasicBlock(context)(function_)("rc_drop_free")
                            in
                                let continueBlock = appendBasicBlock(context)(function_)("rc_drop_continue")
                                in
                                    let _ = buildCondBr(builder)(isZero)(freeBlock)(continueBlock)
                                    in
                                        let _ = positionBuilderAtEnd(builder)(freeBlock)
                                        in
                                            let _ = buildCall(builder)(freeType)(freeFn)([headerPtr])(1u32)("")
                                            in
                                                let _ = buildBr(builder)(continueBlock)
                                                in positionBuilderAtEnd(builder)(continueBlock))

// Sums every part's own `len` field into one `i64` add chain. `partRefs` arrives already resolved
// to LLVM values, never raw `IrTemp`s needing a `lookupIndexed` lookup in here — see the
// `ConcatStr`/`ConcatStrN` cases in `codegenInstructionKind` below for why resolution happens at
// their own call site instead of inside this function. Safe to sum lengths BEFORE any allocation
// happens (unlike a naive single-pass copy) because `partRefs` is a compile-time-fixed list
// straight from the IR instruction — no runtime loop or cursor is needed for either this or
// `emitConcatCopyParts` below, only two separate host-language (Ashes) recursions over that same
// fixed list, one per LLVM pass.
let recursive sumPartLengths builder i64 ptrType partRefs =
    match partRefs with
        | [] -> constInt(i64)(0u64)(false)
        | partRef :: rest ->
            let partLenPtr = buildIntToPtr(builder)(partRef)(ptrType)("str_cat_part_len_ptr")
            in
                let partLen = buildLoad(builder)(i64)(partLenPtr)("str_cat_part_len")
                in
                    buildAdd(builder)(partLen)(sumPartLengths(builder)(i64)(ptrType)(rest))("str_cat_len_acc")

// Copies each part's own payload bytes into its final position in `destBytesPtr`, back to back,
// via a real libc `memcpy` per part — `offset` is an already-built `i64` LLVM value (not a
// compile-time constant), so the GEP into `destBytesPtr` is genuinely dynamic per part, exactly
// the same "index list accepts a runtime value" shape `storePrintBufferByte`'s own `buildGEP` call
// already established. One allocation for the sum of every part's length (computed by
// `sumPartLengths` before this ever runs), each part's bytes copied directly into its final
// position — O(n) total bytes copied, not the O(n^2) a left-nested chain of pairwise concatenation
// calls would pay, matching `LlvmCodegenMemory.cs`'s own `EmitStringConcatN` shape.
let recursive emitConcatCopyParts builder i64 i8 ptrType memcpyFn memcpyType destBytesPtr offset partRefs =
    match partRefs with
        | [] -> Unit
        | partRef :: rest ->
            let partLenPtr = buildIntToPtr(builder)(partRef)(ptrType)("str_cat_part_len_ptr")
            in
                let partLen = buildLoad(builder)(i64)(partLenPtr)("str_cat_part_len")
                in
                    let partBytesAddr =
                        buildAdd(builder)(partRef)(constInt(i64)(8u64)(false))("str_cat_part_bytes_addr")
                    in
                        let partBytesPtr = buildIntToPtr(builder)(partBytesAddr)(ptrType)("str_cat_part_bytes_ptr")
                        in
                            let destOffsetPtr = buildGEP(builder)(i8)(destBytesPtr)([offset])(1u32)("str_cat_dest_offset_ptr")
                            in
                                let _ = buildCall(builder)(memcpyType)(memcpyFn)([destOffsetPtr, partBytesPtr, partLen])(3u32)("str_cat_memcpy")
                                in
                                    let nextOffset = buildAdd(builder)(offset)(partLen)("str_cat_offset_next")
                                    in emitConcatCopyParts(builder)(i64)(i8)(ptrType)(memcpyFn)(memcpyType)(destBytesPtr)(nextOffset)(rest)

// Allocates one real `malloc`'d RC-managed `Str` (`{i64 refcount, i64 unusedAllocSize, i64 len,
// bytes...}`, the SAME layout `AllocAdt`'s own runtime-managed branch and every string literal
// global already use — `unusedAllocSize` mirrors `AllocAdt`'s own convention of recording the byte
// size of everything after the 16-byte header, `len + bytes` for a string) for the sum of every
// part's length, then copies each part's bytes into position. Ignores `ConcatStr`/`ConcatStrN`'s
// own `runtimeManaged` flag rather than branching on it: `CoreLowering.ash` always constructs it
// `false` (no ownership-placement pass exists yet to ever set it `true`), and this codegen has no
// real scoped-arena allocator to fall back to for the `false` case either — exactly the same
// pragmatic "always take the one path this backend can actually execute" call `AllocAdt`'s own
// runtime-managed branch already makes, documented there for the same reason. The result is
// therefore never freed (no drop-insertion pass targets a concatenation result yet), a leak, not a
// correctness bug for the short-lived programs this backend currently produces. Takes already-
// resolved `partRefs`, never `tempEnv`/raw `IrTemp`s — see `sumPartLengths` above for why.
let emitStringConcatN i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType partRefs =
    (let totalLen = sumPartLengths(builder)(i64)(ptrType)(partRefs)
    in
        let totalSize =
            buildAdd(builder)(totalLen)(constInt(i64)(24u64)(false))("str_cat_total_size")
        in
            let headerPtr = buildCall(builder)(mallocType)(mallocFn)([totalSize])(1u32)("str_cat_header")
            in
                let _ =
                    buildStore(builder)(constInt(i64)(1u64)(false))(headerPtr)
                in
                    let sizePtr = gepBytes(builder)(i64)(i8)(headerPtr)(8)("str_cat_size_ptr")
                    in
                        let sizeValue =
                            buildAdd(builder)(totalLen)(constInt(i64)(8u64)(false))("str_cat_size_value")
                        in
                            let _ = buildStore(builder)(sizeValue)(sizePtr)
                            in
                                let payloadPtr = gepBytes(builder)(i64)(i8)(headerPtr)(16)("str_cat_payload_ptr")
                                in
                                    let _ = buildStore(builder)(totalLen)(payloadPtr)
                                    in
                                        let destBytesPtr = gepBytes(builder)(i64)(i8)(headerPtr)(24)("str_cat_dest_bytes_ptr")
                                        in
                                            let _ =
                                                emitConcatCopyParts(builder)(i64)(i8)(ptrType)(memcpyFn)(memcpyType)(destBytesPtr)(constInt(i64)(0u64)(false))(partRefs)
                                            in buildPtrToInt(builder)(payloadPtr)(i64)("str_cat_result"))

// `1 << 62`: the same immortal-refcount sentinel `LlvmCodegenMemory.cs`'s own `EmitHeapStringLiteral`
// writes into a string literal's header instead of a real count of `1`. Decrementing this value by
// any realistic number of drops never reaches zero, so the existing `RcDrop` codegen (unchanged for
// this) naturally never frees a literal's static storage — no sentinel-aware branch needed there.
let runtimeRcImmortalSentinel = Ashes.Number.UInt.fromInt64(1 << 62)

let recursive stringLiteralByteConstants bytes i8 index length =
    if index >= length
    then []
    else
        let byteConstant =
            index
            |> Ashes.Byte.get(bytes)
            |> Ashes.Number.UInt.toInt
            |> Ashes.Number.UInt.fromInt64
            |> constInt(i8)
            |> (given (build) -> build(false))
        in byteConstant :: stringLiteralByteConstants(bytes)(i8)(index + 1)(length)

// Builds one `.rodata`-shaped global per string literal, matching `EmitHeapStringLiteral`'s exact
// layout — `{i64 immortalRefCount, i64 unusedAllocSize, i64 len, [N x i8] bytes}`, the same 16-byte
// RC header every heap ADT cell has, immediately followed by the string's own `len`+bytes payload —
// so a literal's value pointer (header address + 16, computed the same way `AllocAdt` computes its
// own payload pointer) is safe to pass anywhere an ordinary runtime-managed `Str` is expected, with
// no real heap allocation at all.
let buildStringLiteralGlobal module_ context i64 i8 index literal =
    match literal with
        | IrStringLiteral { label = label, value = value } ->
            let bytes = Ashes.Byte.fromText(value)
            in
                let length = Ashes.Byte.length(bytes)
                in
                    let lengthU64 = Ashes.Number.UInt.fromInt64(length)
                    in
                        let byteConstants = stringLiteralByteConstants(bytes)(i8)(0)(length)
                        in
                            let arrayTy = arrayType(i8)(lengthU64)
                            in
                                let structTy = structType(context)([i64, i64, i64, arrayTy])(4u32)(false)
                                in
                                    let structConst =
                                        constStruct(context)(
                                            [
                                                constInt(i64)(runtimeRcImmortalSentinel)(false),
                                                constInt(i64)(0u64)(false),
                                                constInt(i64)(lengthU64)(false),
                                                constArray(i8)(byteConstants)(lengthU64)
                                            ]
                                        )(4u32)(false)
                                    in
                                        let global = addGlobal(module_)(structTy)(".str_lit_" + Ashes.Text.fromInt(index))
                                        in
                                            let _ =
                                                Unit
                                                |> (given (_) -> setInitializer(global)(structConst))
                                                |> (given (_) -> setGlobalConstant(global)(true))
                                                |> (given (_) -> setLinkage(global)(linkageInternal))
                                            in (label, global)

let recursive buildStringLiteralGlobalsFromIndex module_ context i64 i8 index literals =
    match literals with
        | [] -> []
        | literal :: rest -> buildStringLiteralGlobal(module_)(context)(i64)(i8)(index)(literal) :: buildStringLiteralGlobalsFromIndex(module_)(context)(i64)(i8)(index + 1)(rest)

// `LLVMBuildSwitch`'s case count is a capacity hint, not a hard limit (LLVM grows the case table as
// needed) — a fixed reservation avoids needing an Int-to-`u32` conversion that doesn't exist yet
// (`Ashes.Number.UInt` only narrows to `u8`/widens to `u64`) for a value this codegen already knows
// at LLVM-IR-build time, not one it would need to compute from a runtime IR value.
let switchTagCaseCapacity = 8u32

// Resolves every case's label to its `LLVMBasicBlockRef` FIRST (pure reads out of `labelBlocks`,
// no FFI calls at all), returning `(tag, block)` pairs — deliberately NOT interleaved with the
// `LLVMAddCase` FFI calls that consume this list. Interleaving a `labelBlocks` lookup after an
// `addCase` FFI call was confirmed (by direct experiment) to corrupt later lookups into the same
// list — a real, reproducible miscompilation not yet root-caused to a specific line, most likely
// in how this self-hosted backend's own arena/scope machinery treats an FFI call boundary. Doing
// every read before any FFI call sidesteps it entirely; do not reorder this back into one pass.
let recursive resolveSwitchCases cases labelBlocks =
    match cases with
        | [] -> []
        | IrSwitchCase { tag = tag, label = label } :: rest -> (tag, lookupIndexed(label)(labelBlocks)) :: resolveSwitchCases(rest)(labelBlocks)

let recursive addResolvedSwitchCases switchInst i64 resolved =
    match resolved with
        | [] -> Unit
        | (tag, block) :: rest ->
            let _ =
                addCase(switchInst)(constInt(i64)(Ashes.Number.UInt.fromInt64(tag))(true))(block)
            in addResolvedSwitchCases(switchInst)(i64)(rest)

let codegenInstructionKind cx builder kind state =
    match state with
        | (tempEnv, terminated) ->
            match cx with
                | CodegenContext { context = context, function_ = function_, types = types, externals = externals, localSlots = localSlots, labelBlocks = labelBlocks, stringLiteralGlobals = stringLiteralGlobals, liftedFunctions = liftedFunctions, closureFunctionType = closureFunctionType, isEntry = isEntry } ->
                    match types with
                        | CoreLlvmTypes { i64 = i64, i8 = i8, i1 = i1, ptrType = ptrType } ->
                            match externals with
                                | ExternalFunctions { mallocFn = mallocFn, mallocType = mallocType, freeFn = freeFn, freeType = freeType, memcmpFn = memcmpFn, memcmpType = memcmpType, memcpyFn = memcpyFn, memcpyType = memcpyType } ->
                                    match kind with
                                        | LoadConstInt(target, value) ->
                                            ((target, constInt(i64)(Ashes.Number.UInt.fromInt64(value))(true)) :: tempEnv, terminated)
                        // Represented the same as every other scalar in `tempEnv` — a plain `i64`
                        // (0 or 1), matching `StoreLocal`/`LoadLocal`'s uniform `i64` local slots.
                        // `CmpIntGt`/`CmpIntEq`/`CmpIntNe` below zero-extend their native `i1`
                        // `icmp` result to the same representation for exactly this reason: a Bool
                        // value must round-trip through a local slot (the `&&`/`||` desugaring in
                        // `CoreLowering.ash` stores its branch result into one) with no bits lost.
                                        | LoadConstBool(target, value) ->
                                            ((target, constInt(i64)(Ashes.Number.UInt.fromInt64(if value
                                            then 1
                                            else 0))(true)) :: tempEnv, terminated)
                        // The global's own value IS a pointer (to its header word), so the value
                        // pointer (past the header, matching `AllocAdt`'s own convention) is just a
                        // `+16` byte GEP off it directly — no `buildIntToPtr` round-trip needed
                        // first, unlike a temp-held pointer that already went through `i64`.
                                        | LoadConstStr(target, label) ->
                                            let global = lookupIndexed(label)(stringLiteralGlobals)
                                            in
                                                let valuePtr = gepBytes(builder)(i64)(i8)(global)(16)("str_lit_value_ptr")
                                                in ((target, buildPtrToInt(builder)(valuePtr)(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | MulInt(target, left, right) ->
                                            ((target, buildMul(builder)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | AddInt(target, left, right) ->
                                            ((target, buildAdd(builder)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | SubInt(target, left, right) ->
                                            ((target, buildSub(builder)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | CmpIntGt(target, left, right) ->
                                            ((target, buildZExt(builder)(buildICmp(builder)(intPredicateSgt)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target) + "_i1"))(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | CmpIntEq(target, left, right) ->
                                            ((target, buildZExt(builder)(buildICmp(builder)(intPredicateEq)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target) + "_i1"))(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | CmpIntNe(target, left, right) ->
                                            ((target, buildZExt(builder)(buildICmp(builder)(intPredicateNe)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target) + "_i1"))(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | CmpStrEq(target, left, right) ->
                                            let result =
                                                tempEnv
                                                |> lookupIndexed(right)
                                                |> emitStringEquals(context)(function_)(i64)(ptrType)(builder)(memcmpFn)(memcmpType)(lookupIndexed(left)(tempEnv))
                                            in ((target, result) :: tempEnv, terminated)
                        // `1 - equalResult`, not a second comparison: `emitStringEquals` always
                        // returns exactly `0` or `1`, so inverting it arithmetically is sound and
                        // needs no extra branch beyond the one `CmpStrEq` already builds.
                                        | CmpStrNe(target, left, right) ->
                                            let equalResult =
                                                tempEnv
                                                |> lookupIndexed(right)
                                                |> emitStringEquals(context)(function_)(i64)(ptrType)(builder)(memcmpFn)(memcmpType)(lookupIndexed(left)(tempEnv))
                                            in
                                                let result =
                                                    buildSub(builder)(constInt(i64)(1u64)(false))(equalResult)("t" + Ashes.Text.fromInt(target))
                                                in ((target, result) :: tempEnv, terminated)
                        // `IrOptimizer.ash`'s `foldConcatStrChains` runs as the very last pass over
                        // the whole program and rewrites every `ConcatStr` it can safely fold into
                        // a `ConcatStrN` (declined only when an arena/stack bracket, a label, or a
                        // branch sits between the chain's parts — `chainRangeIsSafe`), so real
                        // source reaching this codegen almost always presents as `ConcatStrN`, not
                        // a bare two-operand `ConcatStr` — this case exists for robustness against
                        // whatever the fold declines, sharing the exact same N-ary helper with a
                        // two-element part list rather than a separate pairwise implementation.
                        // `parts`/`[left, right]` are resolved to LLVM values here, at the
                        // `codegenInstructionKind` call site — where every other case in this match
                        // resolves its temps via `lookupIndexed` — so `emitStringConcatN` and its
                        // helpers take plain `List(LLVMValueRef)` and never touch `tempEnv`.
                                        | ConcatStr(target, left, right, _managed) ->
                                            let result =
                                                emitStringConcatN(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(
                                                    [lookupIndexed(left)(tempEnv), lookupIndexed(right)(tempEnv)]
                                                )
                                            in ((target, result) :: tempEnv, terminated)
                                        | ConcatStrN(target, parts, _managed) ->
                                            let result =
                                                emitStringConcatN(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(
                                                    Ashes.Collection.List.map(given (part) -> lookupIndexed(part)(tempEnv))(parts)
                                                )
                                            in ((target, result) :: tempEnv, terminated)
                        // A `Borrow` is a Perceus book-keeping marker (no retain/drop obligation
                        // crosses it) — with no real reference-count tracking in this codegen yet,
                        // it is exactly an alias of the same SSA value under a new temp number.
                                        | Borrow(target, sourceTemp) -> ((target, lookupIndexed(sourceTemp)(tempEnv)) :: tempEnv, terminated)
                        // `CopyOutArena` moves a value out of a scope-local arena before the arena
                        // itself is reclaimed. Arena instructions are no-ops in this codegen (no real
                        // bump-allocated arena exists yet — see `SaveArenaState`/`RestoreArenaState`/
                        // `ReclaimArenaChunks` below), so there is nothing to copy out of: the source
                        // SSA value is already valid past the reclaim point, and this is an alias.
                                        | CopyOutArena(destTemp, srcTemp, _staticSizeBytes, _runtimeManaged, _purpose, _semanticType) -> ((destTemp, lookupIndexed(srcTemp)(tempEnv)) :: tempEnv, terminated)
                                        | StoreLocal(slot, source) ->
                                            let _ =
                                                localSlots
                                                |> lookupIndexed(slot)
                                                |> buildStore(builder)(lookupIndexed(source)(tempEnv))
                                            in (tempEnv, terminated)
                                        | LoadLocal(target, slot) ->
                                            ((target, buildLoad(builder)(i64)(lookupIndexed(slot)(localSlots))("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
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
                                            let _ =
                                                labelBlocks
                                                |> lookupIndexed(target)
                                                |> buildBr(builder)
                                            in (tempEnv, true)
                                        | JumpIfFalse(cond, target) ->
                                            let fallthroughBlock = appendBasicBlock(context)(function_)("fallthrough")
                                            in
                                // Every Bool value in `tempEnv` is a canonical 0/1 `i64` (see
                                // `LoadConstBool`/`CmpIntGt` above), but `buildCondBr` requires an
                                // `i1` condition — truncate back down right at the branch, the one
                                // place this codegen actually needs the narrower type.
                                                let condI1 =
                                                    buildTrunc(builder)(lookupIndexed(cond)(tempEnv))(i1)("cond_i1")
                                                in
                                                    let _ =
                                                        labelBlocks
                                                        |> lookupIndexed(target)
                                                        |> buildCondBr(builder)(condI1)(fallthroughBlock)
                                                    in
                                                        let _ = positionBuilderAtEnd(builder)(fallthroughBlock)
                                                        in (tempEnv, false)
                                        | SwitchTag(tagTemp, cases, defaultLabel) ->
                                            let resolved = resolveSwitchCases(cases)(labelBlocks)
                                            in
                                                let switchInst =
                                                    buildSwitch(builder)(lookupIndexed(tagTemp)(tempEnv))(lookupIndexed(defaultLabel)(labelBlocks))(switchTagCaseCapacity)
                                                in
                                                    let _ = addResolvedSwitchCases(switchInst)(i64)(resolved)
                                                    in (tempEnv, true)
                                        | SaveArenaState(_, _, _) -> (tempEnv, terminated)
                                        | RestoreArenaState(_, _, _, _) -> (tempEnv, terminated)
                                        | ReclaimArenaChunks(_, _, _) -> (tempEnv, terminated)
                                        | PrintInt(source) ->
                                            let _ =
                                                tempEnv
                                                |> lookupIndexed(source)
                                                |> emitPrintInt(context)(function_)(i64)(builder)
                                            in (tempEnv, false)
                                        | PrintStr(source) ->
                                            let _ =
                                                tempEnv
                                                |> lookupIndexed(source)
                                                |> emitPrintStrBytesWithNewline(builder)(i64)(i8)(ptrType)
                                            in (tempEnv, false)
                        // Matches `LlvmCodegenExpressions.cs`'s own `EmitPanic` exactly: print the
                        // message through the SAME helper `PrintStr` uses (stage 0's own
                        // `EmitPanic` calls `EmitPrintStringFromTemp` — a panic's message goes to
                        // stdout, not a stderr-specific path), then exit `1` rather than `0`. A
                        // syscall that terminates the process never returns, so `terminated = true`
                        // here matches `Return`'s own case below, not the `false` every other
                        // instruction in this function returns.
                                        | PanicStr(source) ->
                                            let _ =
                                                tempEnv
                                                |> lookupIndexed(source)
                                                |> emitPrintStrBytesWithNewline(builder)(i64)(i8)(ptrType)
                                            in
                                                let _ =
                                                    false
                                                    |> constInt(i64)(1u64)
                                                    |> emitLinuxProcessExitWithCode(builder)(i64)
                                                in (tempEnv, true)
                        // See `emitAllocAdtRuntimeManaged`/`emitAllocAdtStack` above for the two
                        // branches' own layout/scope documentation. A non-RC-managed `AllocAdt`
                        // with fields panics rather than silently miscompiling — `CoreLowering.ash`
                        // never emits that combination today.
                                        | AllocAdt(target, tag, fieldCount, runtimeManaged) ->
                                            let resultName = "t" + Ashes.Text.fromInt(target)
                                            in
                                                if runtimeManaged
                                                then ((target, emitAllocAdtRuntimeManaged(builder)(i64)(i8)(mallocFn)(mallocType)(tag)(fieldCount)(resultName)) :: tempEnv, terminated)
                                                else
                                                    if fieldCount != 0
                                                    then Ashes.IO.panic("codegen: non-RC-managed AllocAdt with fields not yet supported")
                                                    else ((target, emitAllocAdtStack(builder)(i64)(tag)(resultName)) :: tempEnv, terminated)
                        // Stores one field into an already-allocated ADT's payload (word `1 + fieldIndex`,
                        // since word `0` is the tag — see `AllocAdt`'s own layout comment above). The
                        // `ptr` operand arrives as this codegen's universal `i64` word representation, so
                        // it round-trips through `buildIntToPtr` before the byte-offset GEP.
                                        | SetAdtField(ptr, fieldIndex, source) ->
                                            let basePtr =
                                                buildIntToPtr(builder)(lookupIndexed(ptr)(tempEnv))(ptrType)("adt_field_base")
                                            in
                                                let fieldPtr = gepBytes(builder)(i64)(i8)(basePtr)((fieldIndex + 1) * 8)("adt_field_ptr")
                                                in
                                                    let _ =
                                                        buildStore(builder)(lookupIndexed(source)(tempEnv))(fieldPtr)
                                                    in (tempEnv, terminated)
                        // The read half of `SetAdtField`: same word offset, a load instead of a store.
                                        | GetAdtField(target, ptr, fieldIndex) ->
                                            let basePtr =
                                                buildIntToPtr(builder)(lookupIndexed(ptr)(tempEnv))(ptrType)("adt_field_base")
                                            in
                                                let fieldPtr = gepBytes(builder)(i64)(i8)(basePtr)((fieldIndex + 1) * 8)("adt_field_ptr")
                                                in ((target, buildLoad(builder)(i64)(fieldPtr)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                        // Reads word `0` (the tag) — the same offset `AllocAdt` writes it to.
                                        | GetAdtTag(target, ptr) ->
                                            let basePtr =
                                                buildIntToPtr(builder)(lookupIndexed(ptr)(tempEnv))(ptrType)("adt_tag_base")
                                            in ((target, buildLoad(builder)(i64)(basePtr)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                        // See `emitRcDrop` above for the release itself. `CoreLowering.ash`'s
                        // `lowerDeadRcTopLevelLet` only ever emits this with `runtimeManaged =
                        // true`, `mayBeEmpty = false`, `structuralDropperLabel = None` (every
                        // constructor it can currently fire on wraps one plain scalar field,
                        // nothing to cascade into) — any other combination panics rather than
                        // silently dropping the wrong thing or leaking a child that needed its own
                        // release first.
                                        | RcDrop(sourceTemp, _typeName, _ownerSlot, runtimeManaged, mayBeEmpty, structuralDropperLabel) ->
                                            if runtimeManaged == false
                                            then Ashes.IO.panic("codegen: non-RC-managed RcDrop not yet supported")
                                            else
                                                if mayBeEmpty
                                                then Ashes.IO.panic("codegen: mayBeEmpty RcDrop not yet supported")
                                                else
                                                    match structuralDropperLabel with
                                                        | Some(_label) -> Ashes.IO.panic("codegen: cascading RcDrop (structuralDropperLabel) not yet supported")
                                                        | None ->
                                                            let _ =
                                                                tempEnv
                                                                |> lookupIndexed(sourceTemp)
                                                                |> emitRcDrop(context)(function_)(i64)(i8)(ptrType)(builder)(freeFn)(freeType)
                                                            in (tempEnv, false)
                        // See `closureSizeBytes`/`emitStoreClosureWords` above for the object's
                        // layout. The RC-managed form gets the same 16-byte header every other
                        // RC-managed allocation here has (so a future closure drop can walk back to
                        // it); the ordinary form is an arena stand-in `malloc` — see
                        // `emitArenaStandInAlloc` for why not a stack slot.
                                        | MakeClosure(target, funcLabel, envPtrTemp, envSizeBytes, runtimeManaged, returnsRuntimeManaged, acceptsRuntimeManagedArgument) ->
                                            let closurePtr =
                                                if runtimeManaged
                                                then emitRcAllocPayloadPtr(builder)(i64)(i8)(mallocFn)(mallocType)(closureSizeBytes)("rc_closure")
                                                else emitArenaStandInAlloc(builder)(i64)(mallocFn)(mallocType)(closureSizeBytes)("closure")
                                            in
                                                let result =
                                                    emitStoreClosureWords(builder)(i64)(i8)(closurePtr)(lookupIndexed(funcLabel)(liftedFunctions))(lookupIndexed(envPtrTemp)(tempEnv))(
                                                        packClosureEnvironmentSize(envSizeBytes)(returnsRuntimeManaged)(acceptsRuntimeManagedArgument)
                                                    )("t" + Ashes.Text.fromInt(target))
                                                in ((target, result) :: tempEnv, terminated)
                                        | MakeClosureStack(target, funcLabel, envPtrTemp, envSizeBytes, returnsRuntimeManaged, acceptsRuntimeManagedArgument) ->
                                            let closurePtr = emitStackAlloc(builder)(i64)(closureSizeBytes)("closure_stack")
                                            in
                                                let result =
                                                    emitStoreClosureWords(builder)(i64)(i8)(closurePtr)(lookupIndexed(funcLabel)(liftedFunctions))(lookupIndexed(envPtrTemp)(tempEnv))(
                                                        packClosureEnvironmentSize(envSizeBytes)(returnsRuntimeManaged)(acceptsRuntimeManagedArgument)
                                                    )("t" + Ashes.Text.fromInt(target))
                                                in ((target, result) :: tempEnv, terminated)
                                        | LoadFuncAddr(target, funcLabel) ->
                                            ((target, buildPtrToInt(builder)(lookupIndexed(funcLabel)(liftedFunctions))(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                        // The runtime-managed-argument flag temp is `-1` when the call site has
                        // none (`IrText.ash`'s own `optionalIntOperand` convention, and what
                        // `LlvmCodegen.cs`'s `LoadRuntimeManagedArgumentFlag` checks for), in which
                        // case the callee receives a literal `0`. Resolved inline here rather than
                        // in a helper — see the `ConcatStr` cases above.
                                        | CallClosure(target, closureTemp, argTemp, flagTemp) ->
                                            let flagRef =
                                                if flagTemp < 0
                                                then constInt(i64)(0u64)(false)
                                                else lookupIndexed(flagTemp)(tempEnv)
                                            in
                                                let result =
                                                    emitCallClosure(builder)(i64)(i8)(ptrType)(closureFunctionType)(lookupIndexed(closureTemp)(tempEnv))(lookupIndexed(argTemp)(tempEnv))(flagRef)(
                                                        "t" + Ashes.Text.fromInt(target)
                                                    )
                                                in ((target, result) :: tempEnv, terminated)
                        // A direct call of a statically-known lifted function: same `(env, arg,
                        // flag)` signature as `CallClosure`, just naming the callee outright so
                        // LLVM can see (and inline) it. Always a plain call, never a native tail
                        // call: `LlvmCodegen.cs`'s `DetermineTailCallKind` analysis is not ported.
                                        | CallKnown(target, funcLabel, envTemp, argTemp, flagTemp, _environmentIsStackAllocated) ->
                                            let flagRef =
                                                if flagTemp < 0
                                                then constInt(i64)(0u64)(false)
                                                else lookupIndexed(flagTemp)(tempEnv)
                                            in
                                                let result =
                                                    buildCall(builder)(closureFunctionType)(lookupIndexed(funcLabel)(liftedFunctions))([lookupIndexed(envTemp)(tempEnv), lookupIndexed(argTemp)(tempEnv), flagRef])(3u32)(
                                                        "t" + Ashes.Text.fromInt(target)
                                                    )
                                                in ((target, result) :: tempEnv, terminated)
                                        | LoadEnv(target, index) ->
                                            ((target, emitLoadEnv(builder)(i64)(i8)(ptrType)(lookupIndexed(0)(localSlots))(index)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | LoadArgumentOwnership(target) -> ((target, getParam(function_)(2u32)) :: tempEnv, terminated)
                        // See `emitRcAllocPayloadPtr`/`emitArenaStandInAlloc` for the two forms.
                                        | Alloc(target, sizeBytes, runtimeManaged) ->
                                            let blockPtr =
                                                if runtimeManaged
                                                then emitRcAllocPayloadPtr(builder)(i64)(i8)(mallocFn)(mallocType)(sizeBytes)("rc_alloc")
                                                else emitArenaStandInAlloc(builder)(i64)(mallocFn)(mallocType)(sizeBytes)("alloc")
                                            in ((target, buildPtrToInt(builder)(blockPtr)(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | AllocStack(target, sizeBytes) ->
                                            ((target, buildPtrToInt(builder)(emitStackAlloc(builder)(i64)(sizeBytes)("stack_alloc"))(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | StoreMemOffset(basePtr, offsetBytes, source) ->
                                            let _ =
                                                "store_mem"
                                                |> memOffsetPtr(builder)(i64)(i8)(ptrType)(lookupIndexed(basePtr)(tempEnv))(offsetBytes)
                                                |> buildStore(builder)(lookupIndexed(source)(tempEnv))
                                            in (tempEnv, terminated)
                                        | LoadMemOffset(target, basePtr, offsetBytes) ->
                                            ((target, buildLoad(builder)(i64)(memOffsetPtr(builder)(i64)(i8)(ptrType)(lookupIndexed(basePtr)(tempEnv))(offsetBytes)("load_mem"))("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                        // Only the entry function's `Return` is the process's own exit (see the
                        // header comment); a lifted function's is an ordinary `ret` of its `i64`
                        // result to whichever `CallClosure`/`CallKnown` invoked it.
                                        | Return(source) ->
                                            if isEntry
                                            then
                                                let _ = emitLinuxProcessExit(builder)(i64)
                                                in (tempEnv, true)
                                            else
                                                let _ =
                                                    tempEnv
                                                    |> lookupIndexed(source)
                                                    |> buildRet(builder)
                                                in (tempEnv, true)
                                        | _ -> Ashes.IO.panic("codegen: unsupported IrInstructionKind for this minimal slice")

let recursive codegenInstructions cx builder instructions state =
    match instructions with
        | [] -> state
        | instruction :: rest ->
            match instruction with
                | IrInstruction { instruction = kind } ->
                    state
                    |> codegenInstructionKind(cx)(builder)(kind)
                    |> codegenInstructions(cx)(builder)(rest)

// Builds one function's own scaffolding (entry block, local slots, label blocks) once its
// `irFunction`'s instructions are known and returns the `CodegenContext` its body is emitted
// under — everything module-level and function-independent comes from `mc` unchanged. A lifted
// function with `hasEnvAndArgParams` stores its two incoming words into local slots `0` (the
// environment — `LoadEnv` reads through it) and `1` (the argument) before anything else, exactly
// as `LlvmCodegen.cs`'s `EmitFunctionBodyAllocateSlots` does; the entry function takes no
// parameters at all.
let buildFunctionContext mc functionValue isEntry irFunction =
    match mc with
        | ModuleCodegen { moduleContext = context, moduleTypes = types, moduleExternals = externals, moduleStringLiteralGlobals = stringLiteralGlobals, moduleLiftedFunctions = liftedFunctions, moduleClosureFunctionType = closureFnType, moduleBuilder = builder } ->
            let entryBlock = appendBasicBlock(context)(functionValue)("entry")
            in
                let _ = positionBuilderAtEnd(builder)(entryBlock)
                in
                    match irFunction with
                        | IrFunction { instructions = instructions, localCount = localCount, hasEnvAndArgParams = hasEnvAndArgParams } ->
                            let localSlots = allocateLocalSlots(builder)(types.i64)(localCount)(0)
                            in
                                let _ =
                                    if isEntry == false && hasEnvAndArgParams
                                    then
                                        let _ =
                                            localSlots
                                            |> lookupIndexed(0)
                                            |> buildStore(builder)(getParam(functionValue)(0u32))
                                        in
                                            let _ =
                                                localSlots
                                                |> lookupIndexed(1)
                                                |> buildStore(builder)(getParam(functionValue)(1u32))
                                            in Unit
                                    else Unit
                                in
                                    let labelBlocks =
                                        instructions
                                        |> collectLabelNames
                                        |> createLabelBlocks(context)(functionValue)
                                    in
                                        let cx =
                                            CodegenContext(
                                                context = context,
                                                function_ = functionValue,
                                                types = types,
                                                externals = externals,
                                                localSlots = localSlots,
                                                labelBlocks = labelBlocks,
                                                stringLiteralGlobals = stringLiteralGlobals,
                                                liftedFunctions = liftedFunctions,
                                                closureFunctionType = closureFnType,
                                                isEntry = isEntry
                                            )
                                        in (cx, instructions)

let codegenFunctionBody mc functionValue isEntry irFunction =
    match buildFunctionContext(mc)(functionValue)(isEntry)(irFunction) with
        | (cx, instructions) ->
            let _ = codegenInstructions(cx)(mc.moduleBuilder)(instructions)(([], false))
            in Unit

// Emits every lifted function's body into the `LLVMValueRef` `declareLiftedFunctions` already
// created for its label — every function is declared before ANY body is emitted, so a body can
// name a function that appears later in `functions` (or itself, for recursion) via `MakeClosure`/
// `CallKnown`/`LoadFuncAddr` without any ordering constraint.
let recursive codegenLiftedFunctions mc functions =
    match functions with
        | [] -> Unit
        | function_ :: rest ->
            match function_ with
                | IrFunction { label = label } ->
                    let _ =
                        codegenFunctionBody(mc)(lookupIndexed(label)(mc.moduleLiftedFunctions))(false)(function_)
                    in codegenLiftedFunctions(mc)(rest)

// Builds `void <name>()` for `entryFunction` plus `i64 <label>(i64, i64, i64)` for every function
// in `functions`, all in one fresh module, and returns `(module_, builder)`, matching every other
// module builder's shape in `selfhost/tests/backend` so the same `emitModule` verification
// pipeline applies unchanged. The entry is `void`, not `i64`, since it genuinely never returns a
// value — every path ends in the exit syscall's `unreachable`, not a `ret`; a lifted function
// returns its `i64` result word normally. `malloc`/`free`/`memcmp`/`memcpy` are declared once per
// module (not re-declared per use site) with real pointer return/param types, and the string
// literal globals are built once per module too — both are shared by every function body. One
// `IRBuilder` serves every function (it is repositioned at each new entry block), so the caller
// still disposes exactly one, as before. The entry function is declared first so it stays at
// `.text` offset `0`; its body is emitted LAST, after every lifted function, purely so the
// lifted-function lookups it needs (`MakeClosure`/`CallKnown` naming a label) resolve the same
// way a lifted body's own do.
let codegenFunctions name context entryFunction functions stringLiterals =
    (let module_ = createModule(name)(context)
    in
        let types = coreLlvmTypes(context)
        in
            let entryValue =
                false
                |> functionType(voidType(context))([])(0u32)
                |> addFunction(module_)(name)
            in
                let closureFnType = closureFunctionTypeOf(types.i64)
                in
                    let mc =
                        ModuleCodegen(
                            moduleRef = module_,
                            moduleContext = context,
                            moduleTypes = types,
                            moduleExternals = declareExternalFunctions(module_)(context)(types),
                            moduleStringLiteralGlobals = buildStringLiteralGlobalsFromIndex(module_)(context)(types.i64)(types.i8)(0)(stringLiterals),
                            moduleLiftedFunctions = declareLiftedFunctions(module_)(closureFnType)(functions),
                            moduleClosureFunctionType = closureFnType,
                            moduleBuilder = createBuilder(context)
                        )
                    in
                        let _ = codegenLiftedFunctions(mc)(functions)
                        in
                            let _ = codegenFunctionBody(mc)(entryValue)(true)(entryFunction)
                            in (module_, mc.moduleBuilder))

// The entry function alone, for a hand-built `IrFunction` with no lifted functions at all.
let codegenEntryFunction name context irFunction stringLiterals = codegenFunctions(name)(context)(irFunction)([])(stringLiterals)

// A whole lowered program: its entry function plus every lifted helper it contains.
let codegenProgram name context program =
    match program with
        | IrProgram { entryFunction = entryFunction, functions = functions, stringLiterals = stringLiterals } -> codegenFunctions(name)(context)(entryFunction)(functions)(stringLiterals)
