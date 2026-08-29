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
    | i8: LLVMTypeRef
    | i1: LLVMTypeRef
    | ptrType: LLVMTypeRef
    | localSlots: List((IrLocal, LLVMValueRef))
    | labelBlocks: List((Str, LLVMBasicBlockRef))
    | mallocFn: LLVMValueRef
    | mallocType: LLVMTypeRef
    | freeFn: LLVMValueRef
    | freeType: LLVMTypeRef
    | memcmpFn: LLVMValueRef
    | memcmpType: LLVMTypeRef
    | memcpyFn: LLVMValueRef
    | memcpyType: LLVMTypeRef
    | stringLiteralGlobals: List((Str, LLVMValueRef))

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

// Matches `LlvmCodegenMemory.cs`'s own `EmitStringComparison` exactly: a length check first (two
// `Str` values of different length can never be equal, so `memcmp` is only ever called once
// lengths already match), then a real libc `memcmp` call over the raw payload bytes — the same
// declare-and-call pattern `malloc`/`free` already established for an external symbol this codegen
// needs (`AshesCompiler.Backend.ElfLinker` picks up any new `.text` call to a name in its own
// `linuxDynamicImportLibraries` table automatically, so `memcmp` needed only a one-line addition
// there, no new linker mechanism). No `phi` binding exists in this package's LLVM surface, so the
// three-way branch (lengths differ / lengths match but bytes differ / bytes match) merges through
// a `resultSlot` alloca exactly like `PrintIntState`'s own slot-based merge below and every other
// branch-merge in this file. Returns a plain `i64` `0`/`1` — the same representation `CmpIntEq`'s
// `buildZExt` already establishes for every boolean result in this codegen — so `CmpStrNe` can
// invert it with a plain `1 - result` rather than re-deriving the comparison.
let emitStringEquals context function_ i64 ptrType builder memcmpFn memcmpType leftRef rightRef =
    (let resultSlot = buildAlloca(builder)(i64)("str_cmp_result")
    in
        let leftLenPtr = buildIntToPtr(builder)(leftRef)(ptrType)("str_cmp_left_len_ptr")
        in
            let leftLen = buildLoad(builder)(i64)(leftLenPtr)("str_cmp_left_len")
            in
                let rightLenPtr = buildIntToPtr(builder)(rightRef)(ptrType)("str_cmp_right_len_ptr")
                in
                    let rightLen = buildLoad(builder)(i64)(rightLenPtr)("str_cmp_right_len")
                    in
                        let lenEqBlock = appendBasicBlock(context)(function_)("str_cmp_len_eq")
                        in
                            let notEqBlock = appendBasicBlock(context)(function_)("str_cmp_not_eq")
                            in
                                let eqBlock = appendBasicBlock(context)(function_)("str_cmp_eq")
                                in
                                    let continueBlock = appendBasicBlock(context)(function_)("str_cmp_continue")
                                    in
                                        let lenEqCond = buildICmp(builder)(intPredicateEq)(leftLen)(rightLen)("str_cmp_len_match")
                                        in
                                            let _ = buildCondBr(builder)(lenEqCond)(lenEqBlock)(notEqBlock)
                                            in
                                                let _ = positionBuilderAtEnd(builder)(notEqBlock)
                                                in
                                                    let _ =
                                                        buildStore(builder)(constInt(i64)(0u64)(false))(resultSlot)
                                                    in
                                                        let _ = buildBr(builder)(continueBlock)
                                                        in
                                                            let _ = positionBuilderAtEnd(builder)(lenEqBlock)
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
                                                                                    in
                                                                                        let _ = buildCondBr(builder)(isZero)(eqBlock)(notEqBlock)
                                                                                        in
                                                                                            let _ = positionBuilderAtEnd(builder)(eqBlock)
                                                                                            in
                                                                                                let _ =
                                                                                                    buildStore(builder)(constInt(i64)(1u64)(false))(resultSlot)
                                                                                                in
                                                                                                    let _ = buildBr(builder)(continueBlock)
                                                                                                    in
                                                                                                        let _ = positionBuilderAtEnd(builder)(continueBlock)
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
                                                                        let _ =
                                                                            storePrintBufferByte(builder)(i64)(i8)(bufferType)(buffer)(constInt(i64)(31u64)(false))(zeroDigit)
                                                                        in
                                                                            let _ =
                                                                                buildStore(builder)(constInt(i64)(1u64)(false))(indexSlot)
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
                                                                                                                                let signIndex =
                                                                                                                                    buildSub(builder)(constInt(i64)(31u64)(false))(idxBeforeSign)("sign_index")
                                                                                                                                in
                                                                                                                                    let minusSign = constInt(i64)(45u64)(false)
                                                                                                                                    in
                                                                                                                                        let _ = storePrintBufferByte(builder)(i64)(i8)(bufferType)(buffer)(signIndex)(minusSign)
                                                                                                                                        in
                                                                                                                                            let idxWithSign =
                                                                                                                                                buildAdd(builder)(idxBeforeSign)(constInt(i64)(1u64)(false))("idx_with_sign")
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

// Byte-offset pointer arithmetic shared by `AllocAdt`'s RC-managed header/payload writes and
// `SetAdtField`'s field store: an `i8`-element `buildGEP` with a single scalar index, the same
// "different element type than every struct/array `buildGEP` use" shape
// `selfhost/tests/backend/Main.ash`'s own `defineRcAllocFunction`/`defineRcRetainFunction`
// established for RC header arithmetic.
let gepBytes builder i64 i8 ptr offset name =
    buildGEP(builder)(i8)(ptr)([constInt(i64)(Ashes.Number.UInt.fromInt64(offset))(false)])(1u32)(name)

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
                | CodegenContext { context = context, function_ = function_, i64 = i64, i8 = i8, i1 = i1, ptrType = ptrType, localSlots = localSlots, labelBlocks = labelBlocks, mallocFn = mallocFn, mallocType = mallocType, freeFn = freeFn, freeType = freeType, memcmpFn = memcmpFn, memcmpType = memcmpType, memcpyFn = memcpyFn, memcpyType = memcpyType, stringLiteralGlobals = stringLiteralGlobals } ->
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
                        // `parts`/`[left, right]` are resolved to LLVM values HERE, at the ordinary
                        // (non-recursive) `codegenInstructionKind` call site — exactly where every
                        // other case in this whole match already resolves a temp via `lookupIndexed`
                        // alongside its own FFI calls — rather than inside `emitStringConcatN` or a
                        // separate helper: a self-recursive helper (or even a `Ashes.Collection.List.map`
                        // closure) that itself calls `lookupIndexed` (needs `ConsoleIO`) hits a real
                        // capability-row inference limitation in this compiler when the surrounding
                        // scope also needs `UnsafeFfi` — confirmed by extensive bisection, not
                        // assumed; every fix attempt that kept the resolution inside a nested
                        // function reproduced the same spurious `ASH018` regardless of whether that
                        // function was a hand-written recursive helper, a locally-nested one, or the
                        // standard library's own `map`. Resolving inline here, where `lookupIndexed`
                        // is already always called directly (never through a wrapper) alongside FFI
                        // calls in every other case, sidesteps it entirely.
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
                        // A zero-field, arena-shaped (`runtimeManaged = false`) `AllocAdt` — exactly what
                        // a `Unit` result (e.g. `PrintInt`'s own return value) lowers to — gets a plain
                        // stack `alloca` standing in for a real arena bump allocation: this program shape
                        // never loops around a top-level `AllocAdt`, so a stack slot per call site never
                        // accumulates. This is NOT a substitute for real scoped-arena codegen (a future
                        // instruction sequence that allocates inside a loop body would leak native stack
                        // every iteration, exactly the failure mode documented in
                        // docs/md/future/SELF_HOSTING.md's entry-block-alloca checklist item) — it only
                        // covers today's single-shot, non-looping call sites.
                        //
                        // A field-carrying, RC-managed `AllocAdt` (`CoreLowering.ash` now emits this for
                        // any constructor with at least one field) `malloc`s the real 16-byte
                        // `{i64 reference_count, i64 allocation_size}` header from architecture.md plus
                        // one `i64` word per tag/field (`[tag][field0]...[fieldN-1]`, matching
                        // architecture.md's own `ADT / record` layout row), writes `count = 1` and the
                        // payload size, and returns the PAYLOAD pointer (past the header) as the temp's
                        // `i64` value — the same "public pointer never carries the header" contract
                        // `selfhost/tests/backend/Main.ash`'s own `defineRcAllocFunction` established.
                        // `SetAdtField` below writes into this same payload region. **Explicitly out of
                        // scope**: nothing drops this allocation yet (`CoreLowering.ash` does not emit
                        // `RcDrop` anywhere), so a runtime-managed value from this path leaks today — an
                        // explicit, temporary limitation matching every other stand-in in this arc,
                        // closed by the next slice (Perceus drop insertion). A non-RC-managed `AllocAdt`
                        // with fields panics rather than silently miscompiling — `CoreLowering.ash` never
                        // emits that combination today.
                        | AllocAdt(target, tag, fieldCount, runtimeManaged) ->
                            if runtimeManaged
                            then
                                let payloadWords = fieldCount + 1
                                in
                                    let totalSize =
                                        constInt(i64)(Ashes.Number.UInt.fromInt64(16 + payloadWords * 8))(false)
                                    in
                                        let headerPtr = buildCall(builder)(mallocType)(mallocFn)([totalSize])(1u32)("adt_header")
                                        in
                                            let sizePtr = gepBytes(builder)(i64)(i8)(headerPtr)(8)("adt_size_ptr")
                                            in
                                                let _ =
                                                    buildStore(builder)(constInt(i64)(1u64)(false))(headerPtr)
                                                in
                                                    let _ =
                                                        buildStore(builder)(constInt(i64)(Ashes.Number.UInt.fromInt64(payloadWords * 8))(false))(sizePtr)
                                                    in
                                                        let payloadPtr = gepBytes(builder)(i64)(i8)(headerPtr)(16)("adt_payload_ptr")
                                                        in
                                                            let _ =
                                                                buildStore(builder)(constInt(i64)(Ashes.Number.UInt.fromInt64(tag))(false))(payloadPtr)
                                                            in ((target, buildPtrToInt(builder)(payloadPtr)(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                            else
                                if fieldCount != 0
                                then Ashes.IO.panic("codegen: non-RC-managed AllocAdt with fields not yet supported")
                                else
                                    let cell = buildAlloca(builder)(i64)("adt_cell")
                                    in
                                        let _ =
                                            buildStore(builder)(constInt(i64)(Ashes.Number.UInt.fromInt64(tag))(false))(cell)
                                        in ((target, buildPtrToInt(builder)(cell)(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
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
                        // A single, non-cascading release: walks back to the RC header with the
                        // NEGATIVE byte-offset GEP that mirrors `AllocAdt`'s own forward one (the
                        // public value pointer never carries the header with it — same contract as
                        // `selfhost/tests/backend/Main.ash`'s hand-built `defineRcRetainFunction`),
                        // decrements the count, and `free`s the header only once it reaches zero.
                        // `CoreLowering.ash`'s `lowerDeadRcTopLevelLet` only ever emits this with
                        // `runtimeManaged = true`, `mayBeEmpty = false`, `structuralDropperLabel =
                        // None` (every constructor it can currently fire on wraps one plain scalar
                        // field, nothing to cascade into) — any other combination panics rather than
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
                                            let valuePtr =
                                                buildIntToPtr(builder)(lookupIndexed(sourceTemp)(tempEnv))(ptrType)("rc_drop_value_ptr")
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
                                                                                        in
                                                                                            let _ = positionBuilderAtEnd(builder)(continueBlock)
                                                                                            in (tempEnv, false)
                        | Return(_) ->
                            let _ = emitLinuxProcessExit(builder)(i64)
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

// Builds `void <name>()` in a fresh module from `irFunction`'s real instructions and returns
// `(module_, builder)`, matching every other module builder's shape in `selfhost/tests/backend` so
// the same `emitModule` verification pipeline applies unchanged. `void`, not `i64`, since the
// function genuinely never returns a value anymore — every path ends in the exit syscall's
// `unreachable`, not a `ret`. `i64` (the type internal temps/locals use) is a separate local value.
// `malloc`/`free` are declared once per module (not re-declared per `AllocAdt`/`RcDrop` site) with
// real pointer return/param types (`ptr malloc(i64)`, `void free(ptr)`), not `i64`; `AllocAdt`/
// `RcDrop`'s own codegen convert to/from the `i64` word every temp is represented as via
// `buildPtrToInt`/`buildIntToPtr`, same as the existing arena-`alloca` case already does.
let codegenEntryFunction name context irFunction stringLiterals =
    (let module_ = createModule(name)(context)
    in
        let i64 = int64Type(context)
        in
            let i1 = int1Type(context)
            in
                let i8 = int8Type(context)
                in
                    let ptrType = pointerType(context)(0u32)
                    in
                        let mallocType = functionType(ptrType)([i64])(1u32)(false)
                        in
                            let mallocFn = addFunction(module_)("malloc")(mallocType)
                            in
                                let freeType =
                                    functionType(voidType(context))([ptrType])(1u32)(false)
                                in
                                    let freeFn = addFunction(module_)("free")(freeType)
                                    in
                                        let i32 = int32Type(context)
                                        in
                                            let memcmpType = functionType(i32)([ptrType, ptrType, i64])(3u32)(false)
                                            in
                                                let memcmpFn = addFunction(module_)("memcmp")(memcmpType)
                                                in
                                                    let memcpyType = functionType(ptrType)([ptrType, ptrType, i64])(3u32)(false)
                                                    in
                                                        let memcpyFn = addFunction(module_)("memcpy")(memcpyType)
                                                        in
                                                            let stringLiteralGlobals = buildStringLiteralGlobalsFromIndex(module_)(context)(i64)(i8)(0)(stringLiterals)
                                                            in
                                                                let functionValue =
                                                                    false
                                                                    |> functionType(voidType(context))([])(0u32)
                                                                    |> addFunction(module_)(name)
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
                                                                                            let labelBlocks =
                                                                                                instructions
                                                                                                |> collectLabelNames
                                                                                                |> createLabelBlocks(context)(functionValue)
                                                                                            in
                                                                                                let cx =
                                                                                                    CodegenContext(
                                                                                                        context = context,
                                                                                                        function_ = functionValue,
                                                                                                        i64 = i64,
                                                                                                        i8 = i8,
                                                                                                        i1 = i1,
                                                                                                        ptrType = ptrType,
                                                                                                        localSlots = localSlots,
                                                                                                        labelBlocks = labelBlocks,
                                                                                                        mallocFn = mallocFn,
                                                                                                        mallocType = mallocType,
                                                                                                        freeFn = freeFn,
                                                                                                        freeType = freeType,
                                                                                                        memcmpFn = memcmpFn,
                                                                                                        memcmpType = memcmpType,
                                                                                                        memcpyFn = memcpyFn,
                                                                                                        memcpyType = memcpyType,
                                                                                                        stringLiteralGlobals = stringLiteralGlobals
                                                                                                    )
                                                                                                in
                                                                                                    let _ = codegenInstructions(cx)(builder)(instructions)(([], false))
                                                                                                    in (module_, builder))
