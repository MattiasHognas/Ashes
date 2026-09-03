// Shared low-level emission helpers for `AshesCompiler.Backend.IrCodegen`'s slices: the LLVM
// scalar-type bundle, the view-aware `Str`/`Bytes` header readers and writers, RC-managed
// allocation with the real 16-byte `{count, size}` header, closure-object layout and calls,
// string concatenation/printing/comparison, and the `PrintInt`/`PrintBool` digit machinery. The
// per-instruction dispatch and the builtin-family emitters live in the sibling slices
// (`IrCodegen.Rc`, `IrCodegen.Filesystem`, `IrCodegen.TextBytes`, `IrCodegen.Environment`,
// `IrCodegen.Process`) and in `IrCodegen` itself.
//
// Everything here is platform-neutral EXCEPT the fd-writing print helpers
// (`emitWriteStrBytesToFd`, `emitPrintStrBytesWithNewline`, and the `PrintInt`/`PrintBool` paths
// built on them), which reach `IrCodegen.Syscalls.LinuxX64` for `write` — they are the first
// helpers a second target has to route through a primitive seam rather than a direct call.

import AshesCompiler.Semantics.TaglessAdtLayout.adtAllocationSizeBytes
import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen.Syscalls.LinuxX64
import Ashes.Number.UInt
export (
    type CoreLlvmTypes(..),
    value coreLlvmTypes,
    value lookupIndexed,
    value emitStringParts,
    value emitWriteStrBytesToFd,
    value emitWriteNewlineToFd,
    value emitStringToCString,
    value emitPrintStrBytesWithNewline,
    type StrCmpBlocks(..),
    value createStrCmpBlocks,
    value emitStrCmpLenCheck,
    value emitStrCmpNotEqualPath,
    value emitStrCmpByteCompare,
    value emitStrCmpEqualPath,
    value emitStringEquals,
    type PrintIntState(..),
    value storePrintBufferByte,
    value maskShiftAmount,
    value storeAsciiBytes,
    value emitPrintBoolBranch,
    value emitPrintBool,
    value printIntPrologue,
    value printIntDigitLoopBody,
    value printIntWriteAndNewline,
    type PrintIntBlocks(..),
    value createPrintIntBlocks,
    value emitPrintIntEntryDispatch,
    value emitPrintIntZeroPath,
    value emitPrintIntLoop,
    value emitPrintIntSignPath,
    value emitPrintIntWritePath,
    value emitPrintInt,
    value gepBytes,
    value emitRcAllocPayloadPtr,
    value emitAllocAdtRuntimeManaged,
    value emitStackAlloc,
    value closureSizeBytes,
    value packClosureEnvironmentSize,
    value emitStoreClosureWords,
    value emitCallClosure,
    value emitLoadEnv,
    value memOffsetPtr,
    value sumPartLengths,
    value emitConcatCopyParts,
    value emitStringConcatN,
    value emitStringLengthValue,
    value emitHeapStringFromBytesAddr,
    value emitAsciiHeapString,
    value emitResultAdt,
    value emitLoadByteAtI64,
    value emitRcAllocPayloadPtrDynamic,
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

let recursive lookupIndexed key env =
    match env with
        | [] -> Ashes.IO.panic("codegen: unknown index " + Ashes.Trait.Show.show(key))
        | (boundKey, value) :: rest ->
            if boundKey == key
            then value
            else lookupIndexed(key)(rest)

// A `Str`/`Bytes` value is either owned (`[len:i64][bytes...]`, bytes inline at `ref + 8`) or a
// view (`[len|VIEW:i64][backingBytesAddr:i64]`, bit 63 of the length word set and the byte address
// stored at `ref + 8`) — `LlvmCodegenMemory.cs`'s `LoadStringLength`/`GetStringBytesPointer`
// contract. Returns the masked length and the branchless select of the two byte addresses; every
// consumer of a string's bytes goes through this one helper so views are valid everywhere.
let emitStringParts builder i64 ptrType stringRef name =
    (let basePtr = buildIntToPtr(builder)(stringRef)(ptrType)(name + "_hdr_ptr")
    in
        let raw = buildLoad(builder)(i64)(basePtr)(name + "_hdr")
        in
            let len =
                buildAnd(builder)(raw)(constInt(i64)(Ashes.Number.UInt.fromInt64(9223372036854775807))(false))(name + "_len")
            in
                let viewBits =
                    buildAnd(builder)(raw)(constInt(i64)(Ashes.Number.UInt.fromInt64(1 << 63))(false))(name + "_view_bit")
                in
                    let isView =
                        buildICmp(builder)(intPredicateNe)(viewBits)(constInt(i64)(0u64)(false))(name + "_is_view")
                    in
                        let inlineAddr =
                            buildAdd(builder)(stringRef)(constInt(i64)(8u64)(false))(name + "_inline_addr")
                        in
                            let viewPtrPtr = buildIntToPtr(builder)(inlineAddr)(ptrType)(name + "_view_ptr_ptr")
                            in
                                let viewPtr = buildLoad(builder)(i64)(viewPtrPtr)(name + "_view_ptr")
                                in (len, buildSelect(builder)(isView)(viewPtr)(inlineAddr)(name + "_bytes_addr")))

// Writes a runtime-managed `Str`/`Bytes` value's own `[len:i64][bytes...]` header bytes (word `0`
// is `len`, byte offset `8` is where the raw bytes start — the SAME layout `LoadConstStr`'s global
// builds, and the general one every real `Str`/`Bytes` value uses, not just a literal) to `fd` via
// the raw `write` syscall, with no trailing newline. `stringRef`'s own `i64` value doubles as the
// byte address once offset by `8`, so writing needs no extra pointer round-trip beyond the one
// `emitStringParts` already needs to read `len`. Shared by every direct (non-buffered) write path —
// `Ashes.IO.write`/`writeBytes` (fd 1), `writeError` (fd 2), and `print`/`panic`'s own
// newline-appending wrapper below.
let emitWriteStrBytesToFd builder i64 ptrType fd stringRef =
    match emitStringParts(builder)(i64)(ptrType)(stringRef)("write_str") with
        | (len, byteAddress) -> emitLinuxWrite(builder)(i64)(fd)(byteAddress)(len)

// A single `\n` byte written to `fd` via the raw `write` syscall, from a one-byte stack slot.
// Shared by `print`/`panic`'s stdout newline and `Ashes.IO.writeLine`/`writeErrorLine`'s own.
let emitWriteNewlineToFd builder i64 i8 fd =
    (let newlineBuf = buildEntryAlloca(builder)(i8)("write_newline")
    in
        let _ =
            buildStore(builder)(constInt(i8)(10u64)(false))(newlineBuf)
        in
            let newlineAddr = buildPtrToInt(builder)(newlineBuf)(i64)("newline_addr")
            in
                false
                |> constInt(i64)(1u64)
                |> emitLinuxWrite(builder)(i64)(fd)(newlineAddr))

// A fresh, NUL-terminated `malloc` buffer holding `stringRef`'s bytes — every syscall path needs a
// real C string, but an Ashes `Str`/`Bytes` value is length-prefixed and never NUL-terminated
// (`emitStringParts`' own contract). Matches `LlvmCodegenMemory.cs`'s `EmitStringToCString` exactly:
// `len + 1` bytes, the payload copied via `memcpy`, one trailing zero byte written at `[len]`. Never
// freed — the same leak-not-miscompile trade every other arena stand-in in this file makes.
let emitStringToCString builder i64 i8 ptrType mallocFn mallocType memcpyFn memcpyType stringRef name =
    match emitStringParts(builder)(i64)(ptrType)(stringRef)(name) with
        | (len, srcAddr) ->
            let totalSize =
                buildAdd(builder)(len)(constInt(i64)(1u64)(false))(name + "_size")
            in
                let destPtr = buildCall(builder)(mallocType)(mallocFn)([totalSize])(1u32)(name + "_buf")
                in
                    let srcPtr = buildIntToPtr(builder)(srcAddr)(ptrType)(name + "_src_ptr")
                    in
                        let _ = buildCall(builder)(memcpyType)(memcpyFn)([destPtr, srcPtr, len])(3u32)(name + "_memcpy")
                        in
                            let terminatorPtr = buildGEP(builder)(i8)(destPtr)([len])(1u32)(name + "_nul_ptr")
                            in
                                let _ =
                                    buildStore(builder)(constInt(i8)(0u64)(false))(terminatorPtr)
                                in destPtr

// `print`/`panic`'s own stdout (fd 1) write-then-newline, matching `LlvmCodegenExpressions.cs`'s
// `EmitPrintStringFromTemp(appendNewline: true)` exactly. Stage 0's own `EmitPanic` prints its
// message through this exact same helper before exiting, not a stderr-specific write.
let emitPrintStrBytesWithNewline builder i64 i8 ptrType stringRef =
    (let _ =
        emitWriteStrBytesToFd(builder)(i64)(ptrType)(constInt(i64)(1u64)(false))(stringRef)
    in
        false
        |> constInt(i64)(1u64)
        |> emitWriteNewlineToFd(builder)(i64)(i8))

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
let emitStrCmpByteCompare context i64 ptrType builder memcmpFn memcmpType leftBytesAddr rightBytesAddr leftLen blocks =
    (let _ = positionBuilderAtEnd(builder)(blocks.lenEqBlock)
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
    (let resultSlot = buildEntryAlloca(builder)(i64)("str_cmp_result")
    in
        match emitStringParts(builder)(i64)(ptrType)(leftRef)("str_cmp_left") with
            | (leftLen, leftBytesAddr) ->
                match emitStringParts(builder)(i64)(ptrType)(rightRef)("str_cmp_right") with
                    | (rightLen, rightBytesAddr) ->
                        let blocks = createStrCmpBlocks(context)(function_)
                        in
                            let _ = emitStrCmpLenCheck(builder)(leftLen)(rightLen)(blocks)
                            in
                                let _ = emitStrCmpNotEqualPath(builder)(i64)(resultSlot)(blocks)
                                in
                                    let _ = emitStrCmpByteCompare(context)(i64)(ptrType)(builder)(memcmpFn)(memcmpType)(leftBytesAddr)(rightBytesAddr)(leftLen)(blocks)
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

// `amount & 63` — see the `ShlInt`/`ShrInt` cases for why.
let maskShiftAmount builder i64 amount =
    buildAnd(builder)(amount)(constInt(i64)(63u64)(false))("shift_amount")

let recursive storeAsciiBytes builder i64 i8 bufferType buffer index codes =
    match codes with
        | [] -> Unit
        | code :: rest ->
            let _ =
                false
                |> constInt(i64)(Ashes.Number.UInt.fromInt64(code))
                |> storePrintBufferByte(builder)(i64)(i8)(bufferType)(buffer)(constInt(i64)(Ashes.Number.UInt.fromInt64(index))(false))
            in storeAsciiBytes(builder)(i64)(i8)(bufferType)(buffer)(index + 1)(rest)

// Writes one of two fixed ASCII lines into a fresh stack buffer and `write`s it — one block per
// outcome, both falling into `continueBlock`.
let emitPrintBoolBranch builder i64 i8 bufferType codes block continueBlock =
    (let _ = positionBuilderAtEnd(builder)(block)
    in
        let buffer = buildEntryAlloca(builder)(bufferType)("bool_buf")
        in
            let _ = storeAsciiBytes(builder)(i64)(i8)(bufferType)(buffer)(0)(codes)
            in
                let bufferAddr = buildPtrToInt(builder)(buffer)(i64)("bool_buf_addr")
                in
                    let _ =
                        false
                        |> constInt(i64)(codes
                        |> Ashes.Collection.List.length
                        |> Ashes.Number.UInt.fromInt64)
                        |> emitLinuxWrite(builder)(i64)(constInt(i64)(1u64)(false))(bufferAddr)
                    in buildBr(builder)(continueBlock))

// `PrintBool`: the canonical 0/1 `i64` a Bool is represented as (see `LoadConstBool`) selects
// between `true\n` and `false\n`, each written from a stack buffer via the raw `write` syscall —
// matching `LlvmCodegenExpressions.cs`'s `EmitPrintBool`/`EmitConditionalWrite` (`icmp ne 0`,
// two blocks, static bytes, newline appended), and entirely stack-local like `PrintInt`.
let emitPrintBool context function_ i64 i8 builder value =
    (let bufferType = arrayType(i8)(6u64)
    in
        let isTrue =
            buildICmp(builder)(intPredicateNe)(value)(constInt(i64)(0u64)(false))("bool_is_true")
        in
            let trueBlock = appendBasicBlock(context)(function_)("bool_true")
            in
                let falseBlock = appendBasicBlock(context)(function_)("bool_false")
                in
                    let continueBlock = appendBasicBlock(context)(function_)("bool_continue")
                    in
                        let _ = buildCondBr(builder)(isTrue)(trueBlock)(falseBlock)
                        in
                            let _ = emitPrintBoolBranch(builder)(i64)(i8)(bufferType)([116, 114, 117, 101, 10])(trueBlock)(continueBlock)
                            in
                                let _ = emitPrintBoolBranch(builder)(i64)(i8)(bufferType)([102, 97, 108, 115, 101, 10])(falseBlock)(continueBlock)
                                in positionBuilderAtEnd(builder)(continueBlock))

// Allocates the 32-byte stack digit buffer plus the index/work stack slots `PrintInt`'s block
// structure shares, matching `LlvmCodegenPlatform.cs`'s own `EmitPrintIntPrologue`: `workSlot`
// starts at `value`'s absolute value (a negative `value` is negated via `buildSelect`, no branch
// needed for that part), `indexSlot` starts at `0`. Genuinely stack-only — no global/`.data`
// reference anywhere in this instruction, so it needs nothing new from
// `AshesCompiler.Backend.ElfLinker`'s current relocation-free scope.
let printIntPrologue builder i64 i8 value =
    (let bufferType = arrayType(i8)(32u64)
    in
        let buffer = buildEntryAlloca(builder)(bufferType)("print_buf")
        in
            let indexSlot = buildEntryAlloca(builder)(i64)("print_idx")
            in
                let workSlot = buildEntryAlloca(builder)(i64)("print_work")
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
                                        let newlineByte = buildEntryAlloca(builder)(i8)("print_newline")
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
// `SetAdtField` writes into this same payload region; `IrCodegen.Rc`'s release walks back to the
// header from it.
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

// A runtime-managed (RC) ADT cell: the payload behind the RC header is `[tag][fields...]`, or
// `[fields...]` with no tag word when `tagless` (a single-constructor cell, see TaglessAdtLayout).
let emitAllocAdtRuntimeManaged builder i64 i8 mallocFn mallocType tag fieldCount tagless resultName =
    (let payloadPtr =
        emitRcAllocPayloadPtr(builder)(i64)(i8)(mallocFn)(mallocType)(adtAllocationSizeBytes(tagless)(fieldCount))("adt")
    in
        let _ =
            if tagless
            then Unit
            else
                Unit
                |> (given (_) ->
                    buildStore(builder)(constInt(i64)(Ashes.Number.UInt.fromInt64(tag))(false))(payloadPtr))
                |> (given (_) -> Unit)
        in buildPtrToInt(builder)(payloadPtr)(i64)(resultName))

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
            match emitStringParts(builder)(i64)(ptrType)(partRef)("str_cat_part") with
                | (partLen, _bytesAddr) ->
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
            match emitStringParts(builder)(i64)(ptrType)(partRef)("str_cat_part") with
                | (partLen, partBytesAddr) ->
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

// The `len` word every heap `Str`/`Bytes` value starts with, masked free of the view flag
// (`LlvmCodegenMemory.cs`'s `LoadStringLength`: bit 63 marks a borrowed view of another value's
// bytes; the length itself occupies the low 63 bits).
let emitStringLengthValue builder i64 ptrType valueRef name =
    (let lenPtr = buildIntToPtr(builder)(valueRef)(ptrType)(name + "_ptr")
    in
        let raw = buildLoad(builder)(i64)(lenPtr)(name + "_raw")
        in
            buildAnd(builder)(raw)(constInt(i64)(Ashes.Number.UInt.fromInt64(9223372036854775807))(false))(name))

// A fresh RC heap string copied from `len` bytes at address `srcAddr` — the `{count, size, len,
// bytes}` layout and single `memcpy` `emitStringConcatN` established, shared by the slicing and
// UTF-8 builtins below.
let emitHeapStringFromBytesAddr builder i64 i8 ptrType mallocFn mallocType memcpyFn memcpyType srcAddr len name =
    (let totalSize =
        buildAdd(builder)(len)(constInt(i64)(24u64)(false))(name + "_total_size")
    in
        let headerPtr = buildCall(builder)(mallocType)(mallocFn)([totalSize])(1u32)(name + "_header")
        in
            let _ =
                buildStore(builder)(constInt(i64)(1u64)(false))(headerPtr)
            in
                let sizePtr = gepBytes(builder)(i64)(i8)(headerPtr)(8)(name + "_size_ptr")
                in
                    let _ =
                        buildStore(builder)(buildAdd(builder)(len)(constInt(i64)(8u64)(false))(name + "_size_value"))(sizePtr)
                    in
                        let payloadPtr = gepBytes(builder)(i64)(i8)(headerPtr)(16)(name + "_payload_ptr")
                        in
                            let _ = buildStore(builder)(len)(payloadPtr)
                            in
                                let destBytesPtr = gepBytes(builder)(i64)(i8)(headerPtr)(24)(name + "_dest_bytes_ptr")
                                in
                                    let srcPtr = buildIntToPtr(builder)(srcAddr)(ptrType)(name + "_src_ptr")
                                    in
                                        let _ = buildCall(builder)(memcpyType)(memcpyFn)([destBytesPtr, srcPtr, len])(3u32)(name + "_memcpy")
                                        in buildPtrToInt(builder)(payloadPtr)(i64)(name + "_result"))

// A fresh RC heap string holding a fixed ASCII message: the codes staged in a stack buffer, then
// copied into the shared heap-string layout.
let emitAsciiHeapString builder i64 i8 ptrType mallocFn mallocType memcpyFn memcpyType codes name =
    (let count = Ashes.Collection.List.length(codes)
    in
        let bufferType =
            count
            |> Ashes.Number.UInt.fromInt64
            |> arrayType(i8)
        in
            let buffer = buildEntryAlloca(builder)(bufferType)(name + "_buf")
            in
                let _ = storeAsciiBytes(builder)(i64)(i8)(bufferType)(buffer)(0)(codes)
                in
                    let bufferAddr = buildPtrToInt(builder)(buffer)(i64)(name + "_buf_addr")
                    in
                        emitHeapStringFromBytesAddr(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(bufferAddr)(constInt(i64)(Ashes.Number.UInt.fromInt64(count))(false))(name))

// `Ok(value)` (tag 0) / `Error(value)` (tag 1): one field stored past the tag word — stage 0's
// `EmitResultOk`/`EmitResultError` tags exactly.
let emitResultAdt builder i64 i8 ptrType mallocFn mallocType tag fieldValue name =
    (let adtValue = emitAllocAdtRuntimeManaged(builder)(i64)(i8)(mallocFn)(mallocType)(tag)(1)(false)(name)
    in
        let adtPtr = buildIntToPtr(builder)(adtValue)(ptrType)(name + "_ptr")
        in
            let fieldPtr = gepBytes(builder)(i64)(i8)(adtPtr)(8)(name + "_field_ptr")
            in
                let _ = buildStore(builder)(fieldValue)(fieldPtr)
                in adtValue)

// The byte at dynamic `index`, zero-extended to the universal `i64` word.
let emitLoadByteAtI64 builder i64 i8 bytesPtr index name =
    (let pointer = buildGEP(builder)(i8)(bytesPtr)([index])(1u32)(name + "_ptr")
    in
        buildZExt(builder)(buildLoad(builder)(i8)(pointer)(name))(i64)(name + "_i64"))

// `emitRcAllocPayloadPtr`'s runtime-sized twin: `payloadSize` is an LLVM `i64` value rather than a
// compile-time constant, for a buffer whose size only the running program knows
// (`File.readChunk`'s caller-chosen count).
let emitRcAllocPayloadPtrDynamic builder i64 i8 mallocFn mallocType payloadSize name =
    (let headerPtr =
        name + "_total"
        |> buildAdd(builder)(payloadSize)(constInt(i64)(16u64)(false))
        |> (given (totalSize) -> buildCall(builder)(mallocType)(mallocFn)([totalSize])(1u32)(name + "_header"))
    in
        headerPtr
        |> buildStore(builder)(constInt(i64)(1u64)(false))
        |> (given (_) ->
            name + "_size_ptr"
            |> gepBytes(builder)(i64)(i8)(headerPtr)(8)
            |> buildStore(builder)(payloadSize))
        |> (given (_) -> gepBytes(builder)(i64)(i8)(headerPtr)(16)(name + "_payload_ptr")))
