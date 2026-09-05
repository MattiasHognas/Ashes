// The `Ashes.Text`/`Ashes.Byte`/`Ashes.Rune` builtin emitters for
// `AshesCompiler.Backend.IrCodegen`: text formatting/parsing (`fromInt`, `parseInt`,
// `parseFloat`, `toHex`, `uncons`/`unconsText`), the byte-buffer family (get/compare/indexOf/
// sub/append/allocate/fromList/hash/LE encoders and decoders/checked updates/scanHash), and
// UTF-8 rune encoding (`Rune.toText`). Depends only on the LLVM bindings and
// `IrCodegen.Support`.

import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen.Support
import AshesCompiler.Backend.IrCodegen.Syscalls.LinuxX64
import Ashes.Number.UInt
export (
    value emitTextFromInt,
    value emitBytesGetPanicMessage,
    value emitBytesGet,
    value emitBytesCompare,
    value emitBytesIndexOf,
    value emitBytesSubClamp,
    value emitBytesSubText,
    value emitBytesSubView,
    value emitTextUnconsText,
    value emitDecimalDigitCheck,
    value emitDecimalDigitValue,
    value emitTextParseInt,
    value emitRuneByteOrZero,
    value emitRuneCombine,
    value emitRuneByteRange,
    value emitTextUncons,
    value emitBytesSingleton,
    value emitBytesHash,
    value emitBytesAppendByte,
    value emitBytesAllocatePanicMessage,
    value emitBytesAllocate,
    value emitBytesFromList,
    value emitBytesPanicLine,
    value emitBytesEmpty,
    value emitBytesLeStores,
    value emitBytesUnsignedLe,
    value emitBytesLeReads,
    value emitBytesReadLeUnsigned,
    value emitBytesCheckedRange,
    value emitBytesCopyOnWrite,
    value emitBytesCopyRange,
    value emitBytesSetUnsigned,
    value emitBytesScanHash,
    value emitTextToHex,
    value emitTextParseFloat,
    value emitRuneLeadByte,
    value emitRuneContinuationByte,
    value emitRuneStoreByte,
    value emitRuneToText,
)

// `TextFromInt`'s finish: the same prologue/zero/digit/sign phases as `PrintInt` (the phase
// helpers above), with the write-syscall phase replaced by handing the filled tail of the digit
// buffer (its address word and byte count) to `place`, which allocates the string where the
// instruction's placement asks and answers the universal `i64` value word.
let emitTextFromInt context function_ i64 builder place value =
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
                            in
                                let _ = positionBuilderAtEnd(builder)(blocks.writeBlock)
                                in
                                    let count = buildLoad(builder)(i64)(printState.indexSlot)("from_int_count")
                                    in
                                        let startIndex =
                                            buildSub(builder)(constInt(i64)(32u64)(false))(count)("from_int_start_index")
                                        in
                                            let dataPtr = buildGEP(builder)(printState.bufferType)(printState.buffer)([constInt(i64)(0u64)(false), startIndex])(2u32)("from_int_data_ptr")
                                            in
                                                let result =
                                                    place(buildPtrToInt(builder)(dataPtr)(i64)("from_int_data_addr"))(count)
                                                in
                                                    let _ = buildBr(builder)(blocks.continueBlock)
                                                    in
                                                        let _ = positionBuilderAtEnd(builder)(blocks.continueBlock)
                                                        in result)

// `Bytes.get`'s out-of-bounds exit: the fixed message plus newline written from a stack buffer via
// the raw `write` syscall, then exit `1` — the same fixed-ASCII-line shape `emitPrintBoolBranch`
// uses, since a codegen-internal message has no `IrStringLiteral` global to print through.
let emitBytesGetPanicMessage builder i64 i8 =
    (let bufferType = arrayType(i8)(31u64)
    in
        let buffer = buildEntryAlloca(builder)(bufferType)("bytes_get_panic_msg")
        in
            // "Bytes.get: index out of bounds\n"
            let _ = storeAsciiBytes(builder)(i64)(i8)(bufferType)(buffer)(0)([66, 121, 116, 101, 115, 46, 103, 101, 116, 58, 32, 105, 110, 100, 101, 120, 32, 111, 117, 116, 32, 111, 102, 32, 98, 111, 117, 110, 100, 115, 10])
            in
                let addr = buildPtrToInt(builder)(buffer)(i64)("bytes_get_panic_addr")
                in
                    let _ =
                        false
                        |> constInt(i64)(31u64)
                        |> emitLinuxWrite(builder)(i64)(constInt(i64)(1u64)(false))(addr)
                    in
                        false
                        |> constInt(i64)(1u64)
                        |> emitLinuxProcessExitWithCode(builder)(i64))

// `Bytes.get(bytes)(index)`: bounds-checked single-byte read, zero-extended to the universal `i64`
// word — `EmitBytesGet`'s exact panic-or-load shape.
let emitBytesGet context function_ i64 i8 ptrType builder bytesRef indexVal =
    match emitStringParts(builder)(i64)(ptrType)(bytesRef)("bytes_get") with
        | (len, bytesAddr) ->
            let panicBlock = appendBasicBlock(context)(function_)("bytes_get_panic")
            in
                let okBlock = appendBasicBlock(context)(function_)("bytes_get_ok")
                in
                    let oob = buildICmp(builder)(intPredicateUge)(indexVal)(len)("bytes_get_oob")
                    in
                        let _ = buildCondBr(builder)(oob)(panicBlock)(okBlock)
                        in
                            let _ = positionBuilderAtEnd(builder)(panicBlock)
                            in
                                let _ = emitBytesGetPanicMessage(builder)(i64)(i8)
                                in
                                    let _ = positionBuilderAtEnd(builder)(okBlock)
                                    in
                                        let bytesPtr = buildIntToPtr(builder)(bytesAddr)(ptrType)("bytes_get_data_ptr")
                                        in
                                            let elemPtr = buildGEP(builder)(i8)(bytesPtr)([indexVal])(1u32)("bytes_get_elem_ptr")
                                            in
                                                let byteVal = buildLoad(builder)(i8)(elemPtr)("bytes_get_byte")
                                                in buildZExt(builder)(byteVal)(i64)("bytes_get_result")

// `Bytes.compare(left)(right)`: three-way lexicographic order — `memcmp` over the common prefix,
// ties broken by length (shorter first) — `EmitBytesCompare`'s exact select chain.
let emitBytesCompare context i64 ptrType builder memcmpFn memcmpType leftRef rightRef =
    match emitStringParts(builder)(i64)(ptrType)(leftRef)("bytes_cmp_left") with
        | (leftLen, leftAddr) ->
            match emitStringParts(builder)(i64)(ptrType)(rightRef)("bytes_cmp_right") with
                | (rightLen, rightAddr) ->
                    let leftSmaller = buildICmp(builder)(intPredicateUlt)(leftLen)(rightLen)("bytes_cmp_left_smaller")
                    in
                        let minLen = buildSelect(builder)(leftSmaller)(leftLen)(rightLen)("bytes_cmp_min_len")
                        in
                            let leftPtr = buildIntToPtr(builder)(leftAddr)(ptrType)("bytes_cmp_left_ptr")
                            in
                                let rightPtr = buildIntToPtr(builder)(rightAddr)(ptrType)("bytes_cmp_right_ptr")
                                in
                                    let raw = buildCall(builder)(memcmpType)(memcmpFn)([leftPtr, rightPtr, minLen])(3u32)("bytes_cmp_memcmp")
                                    in
                                        let zero32 =
                                            constInt(int32Type(context))(0u64)(false)
                                        in
                                            let zero = constInt(i64)(0u64)(false)
                                            in
                                                let one = constInt(i64)(1u64)(false)
                                                in
                                                    let negOne =
                                                        constInt(i64)(Ashes.Number.UInt.fromInt64(-1))(false)
                                                    in
                                                        let rawIsZero = buildICmp(builder)(intPredicateEq)(raw)(zero32)("bytes_cmp_prefix_eq")
                                                        in
                                                            let rawNeg = buildICmp(builder)(intPredicateSlt)(raw)(zero32)("bytes_cmp_raw_neg")
                                                            in
                                                                let bySign = buildSelect(builder)(rawNeg)(negOne)(one)("bytes_cmp_by_sign")
                                                                in
                                                                    let lenEq = buildICmp(builder)(intPredicateEq)(leftLen)(rightLen)("bytes_cmp_len_eq")
                                                                    in
                                                                        let byLenNonEq = buildSelect(builder)(leftSmaller)(negOne)(one)("bytes_cmp_by_len_ne")
                                                                        in
                                                                            let byLen = buildSelect(builder)(lenEq)(zero)(byLenNonEq)("bytes_cmp_by_len")
                                                                            in buildSelect(builder)(rawIsZero)(byLen)(bySign)("bytes_cmp_result")

// `Bytes.indexOf(bytes)(needle)(from)`: index of the first byte equal to `needle` at or after
// `max(from, 0)`, or `-1` — `EmitBytesIndexOfScalarScan`'s exact loop (the memchr/SWAR fast paths
// stage 0 layers on top are optimizations this codegen does not need yet).
let emitBytesIndexOf context function_ i64 i8 ptrType builder bytesRef needleVal fromVal =
    match emitStringParts(builder)(i64)(ptrType)(bytesRef)("bytes_idx") with
        | (len, bytesAddr) ->
            let zero = constInt(i64)(0u64)(false)
            in
                let fromNeg = buildICmp(builder)(intPredicateSlt)(fromVal)(zero)("bytes_idx_from_neg")
                in
                    let fromStart = buildSelect(builder)(fromNeg)(zero)(fromVal)("bytes_idx_from")
                    in
                        let dataPtr = buildIntToPtr(builder)(bytesAddr)(ptrType)("bytes_idx_data_ptr")
                        in
                            let needle8 = buildTrunc(builder)(needleVal)(i8)("bytes_idx_needle")
                            in
                                let idxSlot = buildEntryAlloca(builder)(i64)("bytes_idx_slot")
                                in
                                    let resultSlot = buildEntryAlloca(builder)(i64)("bytes_idx_result")
                                    in
                                        let _ = buildStore(builder)(fromStart)(idxSlot)
                                        in
                                            let checkBlock = appendBasicBlock(context)(function_)("bytes_idx_check")
                                            in
                                                let bodyBlock = appendBasicBlock(context)(function_)("bytes_idx_body")
                                                in
                                                    let foundBlock = appendBasicBlock(context)(function_)("bytes_idx_found")
                                                    in
                                                        let advanceBlock = appendBasicBlock(context)(function_)("bytes_idx_advance")
                                                        in
                                                            let notFoundBlock = appendBasicBlock(context)(function_)("bytes_idx_notfound")
                                                            in
                                                                let doneBlock = appendBasicBlock(context)(function_)("bytes_idx_done")
                                                                in
                                                                    let _ = buildBr(builder)(checkBlock)
                                                                    in
                                                                        let _ = positionBuilderAtEnd(builder)(checkBlock)
                                                                        in
                                                                            let idx = buildLoad(builder)(i64)(idxSlot)("bytes_idx_val")
                                                                            in
                                                                                let more = buildICmp(builder)(intPredicateUlt)(idx)(len)("bytes_idx_more")
                                                                                in
                                                                                    let _ = buildCondBr(builder)(more)(bodyBlock)(notFoundBlock)
                                                                                    in
                                                                                        let _ = positionBuilderAtEnd(builder)(bodyBlock)
                                                                                        in
                                                                                            let bytePtr = buildGEP(builder)(i8)(dataPtr)([idx])(1u32)("bytes_idx_byte_ptr")
                                                                                            in
                                                                                                let curByte = buildLoad(builder)(i8)(bytePtr)("bytes_idx_byte")
                                                                                                in
                                                                                                    let eq = buildICmp(builder)(intPredicateEq)(curByte)(needle8)("bytes_idx_eq")
                                                                                                    in
                                                                                                        let _ = buildCondBr(builder)(eq)(foundBlock)(advanceBlock)
                                                                                                        in
                                                                                                            let _ = positionBuilderAtEnd(builder)(foundBlock)
                                                                                                            in
                                                                                                                let _ = buildStore(builder)(idx)(resultSlot)
                                                                                                                in
                                                                                                                    let _ = buildBr(builder)(doneBlock)
                                                                                                                    in
                                                                                                                        let _ = positionBuilderAtEnd(builder)(advanceBlock)
                                                                                                                        in
                                                                                                                            let _ =
                                                                                                                                buildStore(builder)(buildAdd(builder)(idx)(constInt(i64)(1u64)(false))("bytes_idx_next"))(idxSlot)
                                                                                                                            in
                                                                                                                                let _ = buildBr(builder)(checkBlock)
                                                                                                                                in
                                                                                                                                    let _ = positionBuilderAtEnd(builder)(notFoundBlock)
                                                                                                                                    in
                                                                                                                                        let _ =
                                                                                                                                            buildStore(builder)(constInt(i64)(Ashes.Number.UInt.fromInt64(-1))(false))(resultSlot)
                                                                                                                                        in
                                                                                                                                            let _ = buildBr(builder)(doneBlock)
                                                                                                                                            in
                                                                                                                                                let _ = positionBuilderAtEnd(builder)(doneBlock)
                                                                                                                                                in buildLoad(builder)(i64)(resultSlot)("bytes_idx_result_val")

// Clamps `start` into `[0, srcLen]` and `len` into `[0, srcLen - start]` — the shared range
// discipline of `EmitBytesSubText`/`EmitBytesSubView`, so neither ever reads out of bounds.
let emitBytesSubClamp builder i64 srcLen startVal lenVal name =
    (let zero = constInt(i64)(0u64)(false)
    in
        let startNeg = buildICmp(builder)(intPredicateSlt)(startVal)(zero)(name + "_start_neg")
        in
            let start0 = buildSelect(builder)(startNeg)(zero)(startVal)(name + "_start0")
            in
                let startBig = buildICmp(builder)(intPredicateSgt)(start0)(srcLen)(name + "_start_big")
                in
                    let start = buildSelect(builder)(startBig)(srcLen)(start0)(name + "_start")
                    in
                        let avail = buildSub(builder)(srcLen)(start)(name + "_avail")
                        in
                            let lenNeg = buildICmp(builder)(intPredicateSlt)(lenVal)(zero)(name + "_len_neg")
                            in
                                let len0 = buildSelect(builder)(lenNeg)(zero)(lenVal)(name + "_len0")
                                in
                                    let lenBig = buildICmp(builder)(intPredicateSgt)(len0)(avail)(name + "_len_big")
                                    in (start, buildSelect(builder)(lenBig)(avail)(len0)(name + "_len")))

// `Bytes.subText(bytes)(start)(len)`: copies the clamped range into a fresh RC heap string —
// `emitStringConcatN`'s exact `{count, size, len, bytes}` allocation with a single `memcpy`.
let emitBytesSubText builder i64 i8 ptrType mallocFn mallocType memcpyFn memcpyType bytesRef startVal lenVal =
    match emitStringParts(builder)(i64)(ptrType)(bytesRef)("bytes_sub") with
        | (srcLen, srcAddr) ->
            match emitBytesSubClamp(builder)(i64)(srcLen)(startVal)(lenVal)("bytes_sub") with
                | (start, copyLen) ->
                    let totalSize =
                        buildAdd(builder)(copyLen)(constInt(i64)(24u64)(false))("bytes_sub_total_size")
                    in
                        let headerPtr = buildCall(builder)(mallocType)(mallocFn)([totalSize])(1u32)("bytes_sub_header")
                        in
                            let _ =
                                buildStore(builder)(constInt(i64)(1u64)(false))(headerPtr)
                            in
                                let sizePtr = gepBytes(builder)(i64)(i8)(headerPtr)(8)("bytes_sub_size_ptr")
                                in
                                    let _ =
                                        buildStore(builder)(buildAdd(builder)(copyLen)(constInt(i64)(8u64)(false))("bytes_sub_size_value"))(sizePtr)
                                    in
                                        let payloadPtr = gepBytes(builder)(i64)(i8)(headerPtr)(16)("bytes_sub_payload_ptr")
                                        in
                                            let _ = buildStore(builder)(copyLen)(payloadPtr)
                                            in
                                                let destBytesPtr = gepBytes(builder)(i64)(i8)(headerPtr)(24)("bytes_sub_dest_bytes_ptr")
                                                in
                                                    let srcStartAddr = buildAdd(builder)(srcAddr)(start)("bytes_sub_src_start_addr")
                                                    in
                                                        let srcStartPtr = buildIntToPtr(builder)(srcStartAddr)(ptrType)("bytes_sub_src_start_ptr")
                                                        in
                                                            let _ = buildCall(builder)(memcpyType)(memcpyFn)([destBytesPtr, srcStartPtr, copyLen])(3u32)("bytes_sub_memcpy")
                                                            in buildPtrToInt(builder)(payloadPtr)(i64)("bytes_sub_result")

// `Bytes.subView(bytes)(start)(len)`: a zero-copy view `{len|VIEW, backingBytesAddr}` over the
// clamped range in a fresh 16-byte RC payload — O(1), the backing must outlive the view exactly as
// stage 0's `EmitBytesSubView` documents.
let emitBytesSubView builder i64 i8 ptrType mallocFn mallocType bytesRef startVal lenVal =
    match emitStringParts(builder)(i64)(ptrType)(bytesRef)("bytes_subv") with
        | (srcLen, srcAddr) ->
            match emitBytesSubClamp(builder)(i64)(srcLen)(startVal)(lenVal)("bytes_subv") with
                | (start, viewLen) ->
                    let headerPtr = buildCall(builder)(mallocType)(mallocFn)([constInt(i64)(32u64)(false)])(1u32)("bytes_subv_header")
                    in
                        let _ =
                            buildStore(builder)(constInt(i64)(1u64)(false))(headerPtr)
                        in
                            let sizePtr = gepBytes(builder)(i64)(i8)(headerPtr)(8)("bytes_subv_size_ptr")
                            in
                                let _ =
                                    buildStore(builder)(constInt(i64)(16u64)(false))(sizePtr)
                                in
                                    let taggedLen =
                                        buildOr(builder)(viewLen)(constInt(i64)(Ashes.Number.UInt.fromInt64(1 << 63))(false))("bytes_subv_tagged_len")
                                    in
                                        let payloadPtr = gepBytes(builder)(i64)(i8)(headerPtr)(16)("bytes_subv_payload_ptr")
                                        in
                                            let _ = buildStore(builder)(taggedLen)(payloadPtr)
                                            in
                                                let ptrWordPtr = gepBytes(builder)(i64)(i8)(headerPtr)(24)("bytes_subv_ptr_word")
                                                in
                                                    let _ =
                                                        buildStore(builder)(buildAdd(builder)(srcAddr)(start)("bytes_subv_src_start"))(ptrWordPtr)
                                                    in buildPtrToInt(builder)(payloadPtr)(i64)("bytes_subv_result")

// `Text.unconsText(text)`: `None` for the empty string, otherwise `Some((head, rest))` where
// `head` is the first UTF-8 scalar's bytes (its width classed from the lead byte alone, clamped
// to one byte when the buffer is shorter than the class claims) — `EmitTextUnconsText`'s exact
// shape, with head and rest always copied into fresh RC strings (stage 0's runtime-managed path;
// its zero-copy arena views are an optimization this codegen's malloc stand-in does not need).
let emitTextUnconsText context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType textRef =
    match emitStringParts(builder)(i64)(ptrType)(textRef)("text_uncons") with
        | (len, bytesAddr) ->
            let resultSlot = buildEntryAlloca(builder)(i64)("text_uncons_result")
            in
                let emptyBlock = appendBasicBlock(context)(function_)("text_uncons_empty")
                in
                    let nonEmptyBlock = appendBasicBlock(context)(function_)("text_uncons_non_empty")
                    in
                        let continueBlock = appendBasicBlock(context)(function_)("text_uncons_continue")
                        in
                            let isEmpty =
                                buildICmp(builder)(intPredicateEq)(len)(constInt(i64)(0u64)(false))("text_uncons_is_empty")
                            in
                                let _ = buildCondBr(builder)(isEmpty)(emptyBlock)(nonEmptyBlock)
                                in
                                    let _ = positionBuilderAtEnd(builder)(emptyBlock)
                                    in
                                        let _ =
                                            buildStore(builder)(emitAllocAdtRuntimeManaged(builder)(i64)(i8)(mallocFn)(mallocType)(0)(0)(false)("text_uncons_none"))(resultSlot)
                                        in
                                            let _ = buildBr(builder)(continueBlock)
                                            in
                                                let _ = positionBuilderAtEnd(builder)(nonEmptyBlock)
                                                in
                                                    let bytesPtr = buildIntToPtr(builder)(bytesAddr)(ptrType)("text_uncons_bytes_ptr")
                                                    in
                                                        let firstByte =
                                                            buildZExt(builder)(buildLoad(builder)(i8)(bytesPtr)("text_uncons_first"))(i64)("text_uncons_first_i64")
                                                        in
                                                            let isAscii =
                                                                buildICmp(builder)(intPredicateUlt)(firstByte)(constInt(i64)(128u64)(false))("text_uncons_is_ascii")
                                                            in
                                                                let isTwoByte =
                                                                    buildICmp(builder)(intPredicateUle)(firstByte)(constInt(i64)(223u64)(false))("text_uncons_is_two_byte")
                                                                in
                                                                    let isThreeByte =
                                                                        buildICmp(builder)(intPredicateUle)(firstByte)(constInt(i64)(239u64)(false))("text_uncons_is_three_byte")
                                                                    in
                                                                        let widthThreeOrFour =
                                                                            buildSelect(builder)(isThreeByte)(constInt(i64)(3u64)(false))(constInt(i64)(4u64)(false))("text_uncons_width_3_or_4")
                                                                        in
                                                                            let widthTwoOrMore =
                                                                                buildSelect(builder)(isTwoByte)(constInt(i64)(2u64)(false))(widthThreeOrFour)("text_uncons_width_2_or_more")
                                                                            in
                                                                                let widthCandidate =
                                                                                    buildSelect(builder)(isAscii)(constInt(i64)(1u64)(false))(widthTwoOrMore)("text_uncons_width_candidate")
                                                                                in
                                                                                    let hasFullScalar = buildICmp(builder)(intPredicateUge)(len)(widthCandidate)("text_uncons_has_full_scalar")
                                                                                    in
                                                                                        let headLen =
                                                                                            buildSelect(builder)(hasFullScalar)(widthCandidate)(constInt(i64)(1u64)(false))("text_uncons_head_len")
                                                                                        in
                                                                                            let tailLen = buildSub(builder)(len)(headLen)("text_uncons_tail_len")
                                                                                            in
                                                                                                let tailAddr = buildAdd(builder)(bytesAddr)(headLen)("text_uncons_tail_addr")
                                                                                                in
                                                                                                    let headRef = emitHeapStringFromBytesAddr(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(bytesAddr)(headLen)("text_uncons_head")
                                                                                                    in
                                                                                                        let tailRef = emitHeapStringFromBytesAddr(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(tailAddr)(tailLen)("text_uncons_tail")
                                                                                                        in
                                                                                                            let tuplePtr = emitRcAllocPayloadPtr(builder)(i64)(i8)(mallocFn)(mallocType)(16)("text_uncons_tuple")
                                                                                                            in
                                                                                                                let _ = buildStore(builder)(headRef)(tuplePtr)
                                                                                                                in
                                                                                                                    let tupleTailPtr = gepBytes(builder)(i64)(i8)(tuplePtr)(8)("text_uncons_tuple_tail_ptr")
                                                                                                                    in
                                                                                                                        let _ = buildStore(builder)(tailRef)(tupleTailPtr)
                                                                                                                        in
                                                                                                                            let someValue = emitAllocAdtRuntimeManaged(builder)(i64)(i8)(mallocFn)(mallocType)(1)(1)(false)("text_uncons_some")
                                                                                                                            in
                                                                                                                                let somePtr = buildIntToPtr(builder)(someValue)(ptrType)("text_uncons_some_ptr")
                                                                                                                                in
                                                                                                                                    let someFieldPtr = gepBytes(builder)(i64)(i8)(somePtr)(8)("text_uncons_some_field_ptr")
                                                                                                                                    in
                                                                                                                                        let _ =
                                                                                                                                            buildStore(builder)(buildPtrToInt(builder)(tuplePtr)(i64)("text_uncons_tuple_value"))(someFieldPtr)
                                                                                                                                        in
                                                                                                                                            let _ = buildStore(builder)(someValue)(resultSlot)
                                                                                                                                            in
                                                                                                                                                let _ = buildBr(builder)(continueBlock)
                                                                                                                                                in
                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(continueBlock)
                                                                                                                                                    in buildLoad(builder)(i64)(resultSlot)("text_uncons_result_value")

let emitDecimalDigitCheck builder i64 byteValue name =
    buildAnd(builder)(buildICmp(builder)(intPredicateUge)(byteValue)(constInt(i64)(48u64)(false))(name + "_ge_zero"))(buildICmp(builder)(intPredicateUle)(byteValue)(constInt(i64)(57u64)(false))(name + "_le_nine"))(name)

let emitDecimalDigitValue builder i64 byteValue name =
    buildSub(builder)(byteValue)(constInt(i64)(48u64)(false))(name)

// `Text.parseInt(text)`: sign, decimal digits, and overflow thresholds against the Int bounds,
// producing `Ok(value)` or `Error(message)` with stage 0's exact message strings and block
// structure (`EmitTextParseInt`).
let emitTextParseInt context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType textRef =
    match emitStringParts(builder)(i64)(ptrType)(textRef)("tpi") with
        | (len, bytesAddr) ->
            let bytesPtr = buildIntToPtr(builder)(bytesAddr)(ptrType)("tpi_bytes_ptr")
            in
                let indexSlot = buildEntryAlloca(builder)(i64)("tpi_index")
                in
                    let accSlot = buildEntryAlloca(builder)(i64)("tpi_acc")
                    in
                        let negativeSlot = buildEntryAlloca(builder)(i64)("tpi_negative")
                        in
                            let resultSlot = buildEntryAlloca(builder)(i64)("tpi_result")
                            in
                                let _ =
                                    buildStore(builder)(constInt(i64)(0u64)(false))(indexSlot)
                                in
                                    let _ =
                                        buildStore(builder)(constInt(i64)(0u64)(false))(accSlot)
                                    in
                                        let _ =
                                            buildStore(builder)(constInt(i64)(0u64)(false))(negativeSlot)
                                        in
                                            let invalidBlock = appendBasicBlock(context)(function_)("tpi_invalid")
                                            in
                                                let signCheckBlock = appendBasicBlock(context)(function_)("tpi_sign_check")
                                                in
                                                    let minusBlock = appendBasicBlock(context)(function_)("tpi_minus")
                                                    in
                                                        let loopCheckBlock = appendBasicBlock(context)(function_)("tpi_loop_check")
                                                        in
                                                            let loopBodyBlock = appendBasicBlock(context)(function_)("tpi_loop_body")
                                                            in
                                                                let updateBlock = appendBasicBlock(context)(function_)("tpi_update")
                                                                in
                                                                    let accOkBlock = appendBasicBlock(context)(function_)("tpi_acc_ok")
                                                                    in
                                                                        let overflowBlock = appendBasicBlock(context)(function_)("tpi_overflow")
                                                                        in
                                                                            let finishBlock = appendBasicBlock(context)(function_)("tpi_finish")
                                                                            in
                                                                                let continueBlock = appendBasicBlock(context)(function_)("tpi_continue")
                                                                                in
                                                                                    let maxPositive = constInt(i64)(9223372036854775807u64)(false)
                                                                                    in
                                                                                        let maxNegativeMagnitude =
                                                                                            constInt(i64)(Ashes.Number.UInt.fromInt64(1 << 63))(false)
                                                                                        in
                                                                                            let isEmpty =
                                                                                                buildICmp(builder)(intPredicateEq)(len)(constInt(i64)(0u64)(false))("tpi_is_empty")
                                                                                            in
                                                                                                let _ = buildCondBr(builder)(isEmpty)(invalidBlock)(signCheckBlock)
                                                                                                in
                                                                                                    let _ = positionBuilderAtEnd(builder)(signCheckBlock)
                                                                                                    in
                                                                                                        let firstByte =
                                                                                                            emitLoadByteAtI64(builder)(i64)(i8)(bytesPtr)(constInt(i64)(0u64)(false))("tpi_first")
                                                                                                        in
                                                                                                            let isMinus =
                                                                                                                buildICmp(builder)(intPredicateEq)(firstByte)(constInt(i64)(45u64)(false))("tpi_is_minus")
                                                                                                            in
                                                                                                                let _ = buildCondBr(builder)(isMinus)(minusBlock)(loopCheckBlock)
                                                                                                                in
                                                                                                                    let _ = positionBuilderAtEnd(builder)(minusBlock)
                                                                                                                    in
                                                                                                                        let _ =
                                                                                                                            buildStore(builder)(constInt(i64)(1u64)(false))(negativeSlot)
                                                                                                                        in
                                                                                                                            let _ =
                                                                                                                                buildStore(builder)(constInt(i64)(1u64)(false))(indexSlot)
                                                                                                                            in
                                                                                                                                let onlyMinus =
                                                                                                                                    buildICmp(builder)(intPredicateEq)(len)(constInt(i64)(1u64)(false))("tpi_only_minus")
                                                                                                                                in
                                                                                                                                    let _ = buildCondBr(builder)(onlyMinus)(invalidBlock)(loopCheckBlock)
                                                                                                                                    in
                                                                                                                                        let _ = positionBuilderAtEnd(builder)(loopCheckBlock)
                                                                                                                                        in
                                                                                                                                            let index = buildLoad(builder)(i64)(indexSlot)("tpi_index_value")
                                                                                                                                            in
                                                                                                                                                let loopDone = buildICmp(builder)(intPredicateEq)(index)(len)("tpi_done")
                                                                                                                                                in
                                                                                                                                                    let _ = buildCondBr(builder)(loopDone)(finishBlock)(loopBodyBlock)
                                                                                                                                                    in
                                                                                                                                                        let _ = positionBuilderAtEnd(builder)(loopBodyBlock)
                                                                                                                                                        in
                                                                                                                                                            let currentByte = emitLoadByteAtI64(builder)(i64)(i8)(bytesPtr)(index)("tpi_current")
                                                                                                                                                            in
                                                                                                                                                                let isDigit = emitDecimalDigitCheck(builder)(i64)(currentByte)("tpi_digit_check")
                                                                                                                                                                in
                                                                                                                                                                    let _ = buildCondBr(builder)(isDigit)(updateBlock)(invalidBlock)
                                                                                                                                                                    in
                                                                                                                                                                        let _ = positionBuilderAtEnd(builder)(updateBlock)
                                                                                                                                                                        in
                                                                                                                                                                            let digit = emitDecimalDigitValue(builder)(i64)(currentByte)("tpi_digit")
                                                                                                                                                                            in
                                                                                                                                                                                let acc = buildLoad(builder)(i64)(accSlot)("tpi_acc_value")
                                                                                                                                                                                in
                                                                                                                                                                                    let negativeFlag = buildLoad(builder)(i64)(negativeSlot)("tpi_negative_value")
                                                                                                                                                                                    in
                                                                                                                                                                                        let isNegative =
                                                                                                                                                                                            buildICmp(builder)(intPredicateNe)(negativeFlag)(constInt(i64)(0u64)(false))("tpi_is_negative")
                                                                                                                                                                                        in
                                                                                                                                                                                            let limit = buildSelect(builder)(isNegative)(maxNegativeMagnitude)(maxPositive)("tpi_limit")
                                                                                                                                                                                            in
                                                                                                                                                                                                let threshold =
                                                                                                                                                                                                    buildUDiv(builder)(buildSub(builder)(limit)(digit)("tpi_limit_minus_digit"))(constInt(i64)(10u64)(false))("tpi_threshold")
                                                                                                                                                                                                in
                                                                                                                                                                                                    let overflow = buildICmp(builder)(intPredicateUgt)(acc)(threshold)("tpi_overflow_check")
                                                                                                                                                                                                    in
                                                                                                                                                                                                        let _ = buildCondBr(builder)(overflow)(overflowBlock)(accOkBlock)
                                                                                                                                                                                                        in
                                                                                                                                                                                                            let _ = positionBuilderAtEnd(builder)(accOkBlock)
                                                                                                                                                                                                            in
                                                                                                                                                                                                                let nextAcc =
                                                                                                                                                                                                                    buildAdd(builder)(buildMul(builder)(acc)(constInt(i64)(10u64)(false))("tpi_mul10"))(digit)("tpi_next_acc")
                                                                                                                                                                                                                in
                                                                                                                                                                                                                    let _ = buildStore(builder)(nextAcc)(accSlot)
                                                                                                                                                                                                                    in
                                                                                                                                                                                                                        let _ =
                                                                                                                                                                                                                            buildStore(builder)(buildAdd(builder)(index)(constInt(i64)(1u64)(false))("tpi_next_index"))(indexSlot)
                                                                                                                                                                                                                        in
                                                                                                                                                                                                                            let _ = buildBr(builder)(loopCheckBlock)
                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                let _ = positionBuilderAtEnd(builder)(finishBlock)
                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                    let magnitude = buildLoad(builder)(i64)(accSlot)("tpi_magnitude")
                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                        let finalNegativeFlag = buildLoad(builder)(i64)(negativeSlot)("tpi_final_negative")
                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                            let finalIsNegative =
                                                                                                                                                                                                                                                buildICmp(builder)(intPredicateNe)(finalNegativeFlag)(constInt(i64)(0u64)(false))("tpi_final_is_negative")
                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                let signedValue =
                                                                                                                                                                                                                                                    buildSelect(builder)(finalIsNegative)(buildSub(builder)(constInt(i64)(0u64)(false))(magnitude)("tpi_negated"))(magnitude)("tpi_final_value")
                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                    let _ =
                                                                                                                                                                                                                                                        buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(0)(signedValue)("tpi_ok"))(resultSlot)
                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                        let _ = buildBr(builder)(continueBlock)
                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                            let _ = positionBuilderAtEnd(builder)(invalidBlock)
                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                let invalidMessage = emitAsciiHeapString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)([65, 115, 104, 101, 115, 46, 84, 101, 120, 116, 46, 112, 97, 114, 115, 101, 73, 110, 116, 40, 41, 32, 105, 110, 118, 97, 108, 105, 100, 32, 105, 110, 112, 117, 116])("tpi_invalid_msg")
                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                    let _ =
                                                                                                                                                                                                                                                                        buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(1)(invalidMessage)("tpi_invalid_result"))(resultSlot)
                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                        let _ = buildBr(builder)(continueBlock)
                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                            let _ = positionBuilderAtEnd(builder)(overflowBlock)
                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                let overflowMessage = emitAsciiHeapString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)([65, 115, 104, 101, 115, 46, 84, 101, 120, 116, 46, 112, 97, 114, 115, 101, 73, 110, 116, 40, 41, 32, 111, 118, 101, 114, 102, 108, 111, 119])("tpi_overflow_msg")
                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                    let _ =
                                                                                                                                                                                                                                                                                        buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(1)(overflowMessage)("tpi_overflow_result"))(resultSlot)
                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                        let _ = buildBr(builder)(continueBlock)
                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                            let _ = positionBuilderAtEnd(builder)(continueBlock)
                                                                                                                                                                                                                                                                                            in buildLoad(builder)(i64)(resultSlot)("tpi_result_value")

// The byte at fixed `index` when the string is long enough, else 0 — a branch, never a
// speculative read past the payload (`EmitLoadRuneByteOrZero`).
let emitRuneByteOrZero context function_ i64 i8 builder bytesPtr len index name =
    (let slot = buildEntryAlloca(builder)(i64)(name + "_slot")
    in
        let loadBlock = appendBasicBlock(context)(function_)(name + "_load")
        in
            let zeroBlock = appendBasicBlock(context)(function_)(name + "_zero")
            in
                let doneBlock = appendBasicBlock(context)(function_)(name + "_done")
                in
                    let exists =
                        buildICmp(builder)(intPredicateUgt)(len)(constInt(i64)(Ashes.Number.UInt.fromInt64(index))(false))(name + "_exists")
                    in
                        let _ = buildCondBr(builder)(exists)(loadBlock)(zeroBlock)
                        in
                            let _ = positionBuilderAtEnd(builder)(loadBlock)
                            in
                                let _ =
                                    buildStore(builder)(emitLoadByteAtI64(builder)(i64)(i8)(bytesPtr)(constInt(i64)(Ashes.Number.UInt.fromInt64(index))(false))(name))(slot)
                                in
                                    let _ = buildBr(builder)(doneBlock)
                                    in
                                        let _ = positionBuilderAtEnd(builder)(zeroBlock)
                                        in
                                            let _ =
                                                buildStore(builder)(constInt(i64)(0u64)(false))(slot)
                                            in
                                                let _ = buildBr(builder)(doneBlock)
                                                in
                                                    let _ = positionBuilderAtEnd(builder)(doneBlock)
                                                    in buildLoad(builder)(i64)(slot)(name + "_value"))

let emitRuneCombine builder i64 head tail shift name =
    buildOr(builder)(buildShl(builder)(head)(constInt(i64)(Ashes.Number.UInt.fromInt64(shift))(false))(name + "_shift"))(buildAnd(builder)(tail)(constInt(i64)(63u64)(false))(name + "_tail"))(name)

let emitRuneByteRange builder i64 value lower upper name =
    buildAnd(builder)(buildICmp(builder)(intPredicateUge)(value)(constInt(i64)(Ashes.Number.UInt.fromInt64(lower))(false))(name + "_lower"))(buildICmp(builder)(intPredicateUle)(value)(constInt(i64)(Ashes.Number.UInt.fromInt64(upper))(false))(name + "_upper"))(name)

// `Text.uncons(text)`: `None` for the empty string, else `Some((rune, rest))` with the first
// UTF-8 scalar fully validated and decoded — overlong, surrogate, and out-of-range lead/tail
// combinations fall back to U+FFFD at width 1 (`EmitTextUncons`/`EmitDecodeFirstRune` exactly);
// the rest is copied into a fresh RC string like `unconsText`'s tail.
let emitTextUncons context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType textRef =
    match emitStringParts(builder)(i64)(ptrType)(textRef)("runeu") with
        | (len, bytesAddr) ->
            let resultSlot = buildEntryAlloca(builder)(i64)("runeu_result")
            in
                let emptyBlock = appendBasicBlock(context)(function_)("runeu_empty")
                in
                    let valueBlock = appendBasicBlock(context)(function_)("runeu_value")
                    in
                        let doneBlock = appendBasicBlock(context)(function_)("runeu_done")
                        in
                            let isEmpty =
                                buildICmp(builder)(intPredicateEq)(len)(constInt(i64)(0u64)(false))("runeu_is_empty")
                            in
                                let _ = buildCondBr(builder)(isEmpty)(emptyBlock)(valueBlock)
                                in
                                    let _ = positionBuilderAtEnd(builder)(emptyBlock)
                                    in
                                        let _ =
                                            buildStore(builder)(emitAllocAdtRuntimeManaged(builder)(i64)(i8)(mallocFn)(mallocType)(0)(0)(false)("runeu_none"))(resultSlot)
                                        in
                                            let _ = buildBr(builder)(doneBlock)
                                            in
                                                let _ = positionBuilderAtEnd(builder)(valueBlock)
                                                in
                                                    let bytesPtr = buildIntToPtr(builder)(bytesAddr)(ptrType)("runeu_bytes_ptr")
                                                    in
                                                        let b0 =
                                                            emitLoadByteAtI64(builder)(i64)(i8)(bytesPtr)(constInt(i64)(0u64)(false))("runeu_b0")
                                                        in
                                                            let b1 = emitRuneByteOrZero(context)(function_)(i64)(i8)(builder)(bytesPtr)(len)(1)("runeu_b1")
                                                            in
                                                                let b2 = emitRuneByteOrZero(context)(function_)(i64)(i8)(builder)(bytesPtr)(len)(2)("runeu_b2")
                                                                in
                                                                    let b3 = emitRuneByteOrZero(context)(function_)(i64)(i8)(builder)(bytesPtr)(len)(3)("runeu_b3")
                                                                    in
                                                                        let ascii =
                                                                            buildICmp(builder)(intPredicateUlt)(b0)(constInt(i64)(128u64)(false))("runeu_ascii")
                                                                        in
                                                                            let cont1 = emitRuneByteRange(builder)(i64)(b1)(128)(191)("runeu_cont1")
                                                                            in
                                                                                let cont2 = emitRuneByteRange(builder)(i64)(b2)(128)(191)("runeu_cont2")
                                                                                in
                                                                                    let cont3 = emitRuneByteRange(builder)(i64)(b3)(128)(191)("runeu_cont3")
                                                                                    in
                                                                                        let len2 =
                                                                                            buildICmp(builder)(intPredicateUge)(len)(constInt(i64)(2u64)(false))("runeu_len2")
                                                                                        in
                                                                                            let len3 =
                                                                                                buildICmp(builder)(intPredicateUge)(len)(constInt(i64)(3u64)(false))("runeu_len3")
                                                                                            in
                                                                                                let len4 =
                                                                                                    buildICmp(builder)(intPredicateUge)(len)(constInt(i64)(4u64)(false))("runeu_len4")
                                                                                                in
                                                                                                    let valid2 =
                                                                                                        buildAnd(builder)(emitRuneByteRange(builder)(i64)(b0)(194)(223)("runeu_lead2"))(buildAnd(builder)(cont1)(len2)("runeu_valid2_tail"))("runeu_valid2")
                                                                                                    in
                                                                                                        let boundary3 =
                                                                                                            buildAnd(builder)(buildOr(builder)(buildICmp(builder)(intPredicateNe)(b0)(constInt(i64)(224u64)(false))("runeu_not_e0"))(buildICmp(builder)(intPredicateUge)(b1)(constInt(i64)(160u64)(false))("runeu_e0_tail"))("runeu_not_overlong3"))(buildOr(builder)(buildICmp(builder)(intPredicateNe)(b0)(constInt(i64)(237u64)(false))("runeu_not_ed"))(buildICmp(builder)(intPredicateUlt)(b1)(constInt(i64)(160u64)(false))("runeu_ed_tail"))("runeu_not_surrogate"))("runeu_boundary3")
                                                                                                        in
                                                                                                            let valid3 =
                                                                                                                buildAnd(builder)(buildAnd(builder)(emitRuneByteRange(builder)(i64)(b0)(224)(239)("runeu_lead3"))(buildAnd(builder)(cont1)(buildAnd(builder)(cont2)(len3)("runeu_valid3_len"))("runeu_valid3_conts"))("runeu_valid3_base"))(boundary3)("runeu_valid3")
                                                                                                            in
                                                                                                                let boundary4 =
                                                                                                                    buildAnd(builder)(buildOr(builder)(buildICmp(builder)(intPredicateNe)(b0)(constInt(i64)(240u64)(false))("runeu_not_f0"))(buildICmp(builder)(intPredicateUge)(b1)(constInt(i64)(144u64)(false))("runeu_f0_tail"))("runeu_not_overlong4"))(buildOr(builder)(buildICmp(builder)(intPredicateNe)(b0)(constInt(i64)(244u64)(false))("runeu_not_f4"))(buildICmp(builder)(intPredicateUle)(b1)(constInt(i64)(143u64)(false))("runeu_f4_tail"))("runeu_in_range4"))("runeu_boundary4")
                                                                                                                in
                                                                                                                    let valid4 =
                                                                                                                        buildAnd(builder)(buildAnd(builder)(emitRuneByteRange(builder)(i64)(b0)(240)(244)("runeu_lead4"))(buildAnd(builder)(cont1)(buildAnd(builder)(cont2)(cont3)("runeu_cont23"))("runeu_valid4_conts"))("runeu_valid4_base"))(buildAnd(builder)(len4)(boundary4)("runeu_valid4_tail"))("runeu_valid4")
                                                                                                                    in
                                                                                                                        let width =
                                                                                                                            buildSelect(builder)(valid2)(constInt(i64)(2u64)(false))(buildSelect(builder)(valid3)(constInt(i64)(3u64)(false))(buildSelect(builder)(valid4)(constInt(i64)(4u64)(false))(constInt(i64)(1u64)(false))("runeu_width4"))("runeu_width3"))("runeu_width2")
                                                                                                                        in
                                                                                                                            let cp2 =
                                                                                                                                emitRuneCombine(builder)(i64)(buildAnd(builder)(b0)(constInt(i64)(31u64)(false))("runeu_cp2_head"))(b1)(6)("runeu_cp2")
                                                                                                                            in
                                                                                                                                let cp3 =
                                                                                                                                    emitRuneCombine(builder)(i64)(emitRuneCombine(builder)(i64)(buildAnd(builder)(b0)(constInt(i64)(15u64)(false))("runeu_cp3_head"))(b1)(6)("runeu_cp3_mid"))(b2)(6)("runeu_cp3")
                                                                                                                                in
                                                                                                                                    let cp4 =
                                                                                                                                        emitRuneCombine(builder)(i64)(emitRuneCombine(builder)(i64)(emitRuneCombine(builder)(i64)(buildAnd(builder)(b0)(constInt(i64)(7u64)(false))("runeu_cp4_head"))(b1)(6)("runeu_cp4_1"))(b2)(6)("runeu_cp4_2"))(b3)(6)("runeu_cp4")
                                                                                                                                    in
                                                                                                                                        let rune =
                                                                                                                                            buildSelect(builder)(ascii)(b0)(buildSelect(builder)(valid2)(cp2)(buildSelect(builder)(valid3)(cp3)(buildSelect(builder)(valid4)(cp4)(constInt(i64)(65533u64)(false))("runeu_invalid"))("runeu_value3"))("runeu_value2"))("runeu_rune")
                                                                                                                                        in
                                                                                                                                            let tailLen = buildSub(builder)(len)(width)("runeu_tail_len")
                                                                                                                                            in
                                                                                                                                                let tailAddr = buildAdd(builder)(bytesAddr)(width)("runeu_tail_addr")
                                                                                                                                                in
                                                                                                                                                    let tailRef = emitHeapStringFromBytesAddr(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(tailAddr)(tailLen)("runeu_tail")
                                                                                                                                                    in
                                                                                                                                                        let tuplePtr = emitRcAllocPayloadPtr(builder)(i64)(i8)(mallocFn)(mallocType)(16)("runeu_tuple")
                                                                                                                                                        in
                                                                                                                                                            let _ = buildStore(builder)(rune)(tuplePtr)
                                                                                                                                                            in
                                                                                                                                                                let tupleTailPtr = gepBytes(builder)(i64)(i8)(tuplePtr)(8)("runeu_tuple_tail_ptr")
                                                                                                                                                                in
                                                                                                                                                                    let _ = buildStore(builder)(tailRef)(tupleTailPtr)
                                                                                                                                                                    in
                                                                                                                                                                        let someValue = emitAllocAdtRuntimeManaged(builder)(i64)(i8)(mallocFn)(mallocType)(1)(1)(false)("runeu_some")
                                                                                                                                                                        in
                                                                                                                                                                            let somePtr = buildIntToPtr(builder)(someValue)(ptrType)("runeu_some_ptr")
                                                                                                                                                                            in
                                                                                                                                                                                let someFieldPtr = gepBytes(builder)(i64)(i8)(somePtr)(8)("runeu_some_field_ptr")
                                                                                                                                                                                in
                                                                                                                                                                                    let _ =
                                                                                                                                                                                        buildStore(builder)(buildPtrToInt(builder)(tuplePtr)(i64)("runeu_tuple_value"))(someFieldPtr)
                                                                                                                                                                                    in
                                                                                                                                                                                        let _ = buildStore(builder)(someValue)(resultSlot)
                                                                                                                                                                                        in
                                                                                                                                                                                            let _ = buildBr(builder)(doneBlock)
                                                                                                                                                                                            in
                                                                                                                                                                                                let _ = positionBuilderAtEnd(builder)(doneBlock)
                                                                                                                                                                                                in buildLoad(builder)(i64)(resultSlot)("runeu_result_value")

// `Byte.singleton(byte)`: a one-byte RC Bytes value — length word 1, the byte truncated into the
// first data slot (`EmitBytesSingleton`).
let emitBytesSingleton builder i64 i8 allocate byteVal =
    (let payloadPtr = allocate(16)("bytes_singleton")
    in
        let _ =
            buildStore(builder)(constInt(i64)(1u64)(false))(payloadPtr)
        in
            let dataPtr = gepBytes(builder)(i64)(i8)(payloadPtr)(8)("bytes_singleton_data")
            in
                let _ =
                    buildStore(builder)(buildTrunc(builder)(byteVal)(i8)("bytes_singleton_byte"))(dataPtr)
                in buildPtrToInt(builder)(payloadPtr)(i64)("bytes_singleton_result"))

// `Byte.hash(bytes)`: 64-bit FNV-1a over the payload (`EmitBytesHash`'s exact loop and constants).
let emitBytesHash context function_ i64 i8 ptrType builder bytesRef =
    match emitStringParts(builder)(i64)(ptrType)(bytesRef)("bytes_hash") with
        | (len, dataAddr) ->
            let dataPtr = buildIntToPtr(builder)(dataAddr)(ptrType)("bytes_hash_data")
            in
                let hashSlot = buildEntryAlloca(builder)(i64)("bytes_hash_acc")
                in
                    let idxSlot = buildEntryAlloca(builder)(i64)("bytes_hash_idx")
                    in
                        let _ =
                            buildStore(builder)(constInt(i64)(14695981039346656037u64)(false))(hashSlot)
                        in
                            let _ =
                                buildStore(builder)(constInt(i64)(0u64)(false))(idxSlot)
                            in
                                let checkBlock = appendBasicBlock(context)(function_)("bytes_hash_check")
                                in
                                    let bodyBlock = appendBasicBlock(context)(function_)("bytes_hash_body")
                                    in
                                        let doneBlock = appendBasicBlock(context)(function_)("bytes_hash_done")
                                        in
                                            let _ = buildBr(builder)(checkBlock)
                                            in
                                                let _ = positionBuilderAtEnd(builder)(checkBlock)
                                                in
                                                    let idx = buildLoad(builder)(i64)(idxSlot)("bytes_hash_idx_value")
                                                    in
                                                        let more = buildICmp(builder)(intPredicateUlt)(idx)(len)("bytes_hash_more")
                                                        in
                                                            let _ = buildCondBr(builder)(more)(bodyBlock)(doneBlock)
                                                            in
                                                                let _ = positionBuilderAtEnd(builder)(bodyBlock)
                                                                in
                                                                    let byteVal = emitLoadByteAtI64(builder)(i64)(i8)(dataPtr)(idx)("bytes_hash_byte")
                                                                    in
                                                                        let current = buildLoad(builder)(i64)(hashSlot)("bytes_hash_current")
                                                                        in
                                                                            let mixed =
                                                                                buildMul(builder)(buildXor(builder)(current)(byteVal)("bytes_hash_xor"))(constInt(i64)(1099511628211u64)(false))("bytes_hash_mul")
                                                                            in
                                                                                let _ = buildStore(builder)(mixed)(hashSlot)
                                                                                in
                                                                                    let _ =
                                                                                        buildStore(builder)(buildAdd(builder)(idx)(constInt(i64)(1u64)(false))("bytes_hash_idx_next"))(idxSlot)
                                                                                    in
                                                                                        let _ = buildBr(builder)(checkBlock)
                                                                                        in
                                                                                            let _ = positionBuilderAtEnd(builder)(doneBlock)
                                                                                            in buildLoad(builder)(i64)(hashSlot)("bytes_hash_result")

// `Byte.appendByte(bytes)(byte)`: a fresh RC Bytes one byte longer — old payload copied, the new
// byte truncated into the last slot (`EmitBytesAppendByte`).
let emitBytesAppendByte builder i64 i8 ptrType allocateDynamic memcpyFn memcpyType bytesRef byteVal =
    match emitStringParts(builder)(i64)(ptrType)(bytesRef)("bytes_appb") with
        | (oldLen, srcAddr) ->
            let newLen =
                buildAdd(builder)(oldLen)(constInt(i64)(1u64)(false))("bytes_appb_new_len")
            in
                let payloadPtr =
                    allocateDynamic(buildAdd(builder)(newLen)(constInt(i64)(8u64)(false))("bytes_appb_size"))("bytes_appb")
                in
                    let _ = buildStore(builder)(newLen)(payloadPtr)
                    in
                        let destBytesPtr = gepBytes(builder)(i64)(i8)(payloadPtr)(8)("bytes_appb_dest")
                        in
                            let srcPtr = buildIntToPtr(builder)(srcAddr)(ptrType)("bytes_appb_src")
                            in
                                let _ = buildCall(builder)(memcpyType)(memcpyFn)([destBytesPtr, srcPtr, oldLen])(3u32)("bytes_appb_copy")
                                in
                                    let newBytePtr = buildGEP(builder)(i8)(destBytesPtr)([oldLen])(1u32)("bytes_appb_new_ptr")
                                    in
                                        let _ =
                                            buildStore(builder)(buildTrunc(builder)(byteVal)(i8)("bytes_appb_byte"))(newBytePtr)
                                        in buildPtrToInt(builder)(payloadPtr)(i64)("bytes_appb_result")

// `Byte.allocate`'s invalid-length exit: fixed ASCII line and exit 1, the same stack-buffer
// `write` shape as `emitBytesGetPanicMessage`.
let emitBytesAllocatePanicMessage builder i64 i8 =
    (let bufferType = arrayType(i8)(56u64)
    in
        let buffer = buildEntryAlloca(builder)(bufferType)("bytes_allocate_panic_msg")
        in
            // "Bytes.allocate: length must be between 0 and 1073741824\n"
            let _ = storeAsciiBytes(builder)(i64)(i8)(bufferType)(buffer)(0)([66, 121, 116, 101, 115, 46, 97, 108, 108, 111, 99, 97, 116, 101, 58, 32, 108, 101, 110, 103, 116, 104, 32, 109, 117, 115, 116, 32, 98, 101, 32, 98, 101, 116, 119, 101, 101, 110, 32, 48, 32, 97, 110, 100, 32, 49, 48, 55, 51, 55, 52, 49, 56, 50, 52, 10])
            in
                let addr = buildPtrToInt(builder)(buffer)(i64)("bytes_allocate_panic_addr")
                in
                    let _ =
                        false
                        |> constInt(i64)(56u64)
                        |> emitLinuxWrite(builder)(i64)(constInt(i64)(1u64)(false))(addr)
                    in
                        false
                        |> constInt(i64)(1u64)
                        |> emitLinuxProcessExitWithCode(builder)(i64))

// `Byte.allocate(length)`: bounds guard, then a fresh zero-filled RC Bytes of exactly `length`
// data bytes (`EmitBytesAllocate`, with the memset replaced by a plain store loop).
let emitBytesAllocate context function_ i64 i8 builder allocateDynamic lengthVal =
    (let negative =
        buildICmp(builder)(intPredicateSlt)(lengthVal)(constInt(i64)(0u64)(false))("bytes_allocate_negative")
    in
        let tooLarge =
            buildICmp(builder)(intPredicateUgt)(lengthVal)(constInt(i64)(1073741824u64)(false))("bytes_allocate_too_large")
        in
            let invalid = buildOr(builder)(negative)(tooLarge)("bytes_allocate_invalid")
            in
                let panicBlock = appendBasicBlock(context)(function_)("bytes_allocate_panic")
                in
                    let okBlock = appendBasicBlock(context)(function_)("bytes_allocate_ok")
                    in
                        let _ = buildCondBr(builder)(invalid)(panicBlock)(okBlock)
                        in
                            let _ = positionBuilderAtEnd(builder)(panicBlock)
                            in
                                let _ = emitBytesAllocatePanicMessage(builder)(i64)(i8)
                                in
                                    let _ = positionBuilderAtEnd(builder)(okBlock)
                                    in
                                        let payloadPtr =
                                            allocateDynamic(buildAdd(builder)(lengthVal)(constInt(i64)(8u64)(false))("bytes_allocate_size"))("bytes_allocate")
                                        in
                                            let _ = buildStore(builder)(lengthVal)(payloadPtr)
                                            in
                                                let dataPtr = gepBytes(builder)(i64)(i8)(payloadPtr)(8)("bytes_allocate_data")
                                                in
                                                    let idxSlot = buildEntryAlloca(builder)(i64)("bytes_allocate_idx")
                                                    in
                                                        let _ =
                                                            buildStore(builder)(constInt(i64)(0u64)(false))(idxSlot)
                                                        in
                                                            let fillCheckBlock = appendBasicBlock(context)(function_)("bytes_allocate_fill_check")
                                                            in
                                                                let fillBodyBlock = appendBasicBlock(context)(function_)("bytes_allocate_fill_body")
                                                                in
                                                                    let doneBlock = appendBasicBlock(context)(function_)("bytes_allocate_done")
                                                                    in
                                                                        let _ = buildBr(builder)(fillCheckBlock)
                                                                        in
                                                                            let _ = positionBuilderAtEnd(builder)(fillCheckBlock)
                                                                            in
                                                                                let idx = buildLoad(builder)(i64)(idxSlot)("bytes_allocate_idx_value")
                                                                                in
                                                                                    let more = buildICmp(builder)(intPredicateUlt)(idx)(lengthVal)("bytes_allocate_more")
                                                                                    in
                                                                                        let _ = buildCondBr(builder)(more)(fillBodyBlock)(doneBlock)
                                                                                        in
                                                                                            let _ = positionBuilderAtEnd(builder)(fillBodyBlock)
                                                                                            in
                                                                                                let elemPtr = buildGEP(builder)(i8)(dataPtr)([idx])(1u32)("bytes_allocate_elem")
                                                                                                in
                                                                                                    let _ =
                                                                                                        buildStore(builder)(buildTrunc(builder)(constInt(i64)(0u64)(false))(i8)("bytes_allocate_zero"))(elemPtr)
                                                                                                    in
                                                                                                        let _ =
                                                                                                            buildStore(builder)(buildAdd(builder)(idx)(constInt(i64)(1u64)(false))("bytes_allocate_idx_next"))(idxSlot)
                                                                                                        in
                                                                                                            let _ = buildBr(builder)(fillCheckBlock)
                                                                                                            in
                                                                                                                let _ = positionBuilderAtEnd(builder)(doneBlock)
                                                                                                                in buildPtrToInt(builder)(payloadPtr)(i64)("bytes_allocate_result"))

// `Byte.fromList(list)`: two passes over the cons cells — count, then allocate and fill each
// byte in order (`EmitBytesFromList`'s exact shape; cons layout head at 0, tail at 8, nil = 0).
let emitBytesFromList context function_ i64 i8 ptrType builder mallocFn mallocType listRef =
    (let countSlot = buildEntryAlloca(builder)(i64)("bfl_count")
    in
        let curSlot = buildEntryAlloca(builder)(i64)("bfl_cur")
        in
            let idxSlot = buildEntryAlloca(builder)(i64)("bfl_idx")
            in
                let resultSlot = buildEntryAlloca(builder)(i64)("bfl_result")
                in
                    let _ =
                        buildStore(builder)(constInt(i64)(0u64)(false))(countSlot)
                    in
                        let _ = buildStore(builder)(listRef)(curSlot)
                        in
                            let countCheckBlock = appendBasicBlock(context)(function_)("bfl_count_check")
                            in
                                let countBodyBlock = appendBasicBlock(context)(function_)("bfl_count_body")
                                in
                                    let allocBlock = appendBasicBlock(context)(function_)("bfl_alloc")
                                    in
                                        let fillCheckBlock = appendBasicBlock(context)(function_)("bfl_fill_check")
                                        in
                                            let fillBodyBlock = appendBasicBlock(context)(function_)("bfl_fill_body")
                                            in
                                                let doneBlock = appendBasicBlock(context)(function_)("bfl_done")
                                                in
                                                    let _ = buildBr(builder)(countCheckBlock)
                                                    in
                                                        let _ = positionBuilderAtEnd(builder)(countCheckBlock)
                                                        in
                                                            let curCount = buildLoad(builder)(i64)(curSlot)("bfl_cur_count")
                                                            in
                                                                let countDone =
                                                                    buildICmp(builder)(intPredicateEq)(curCount)(constInt(i64)(0u64)(false))("bfl_count_done")
                                                                in
                                                                    let _ = buildCondBr(builder)(countDone)(allocBlock)(countBodyBlock)
                                                                    in
                                                                        let _ = positionBuilderAtEnd(builder)(countBodyBlock)
                                                                        in
                                                                            let count = buildLoad(builder)(i64)(countSlot)("bfl_count_value")
                                                                            in
                                                                                let _ =
                                                                                    buildStore(builder)(buildAdd(builder)(count)(constInt(i64)(1u64)(false))("bfl_count_next"))(countSlot)
                                                                                in
                                                                                    let countCellPtr = buildIntToPtr(builder)(curCount)(ptrType)("bfl_count_cell")
                                                                                    in
                                                                                        let countTailPtr = gepBytes(builder)(i64)(i8)(countCellPtr)(8)("bfl_count_tail_ptr")
                                                                                        in
                                                                                            let _ =
                                                                                                buildStore(builder)(buildLoad(builder)(i64)(countTailPtr)("bfl_count_tail"))(curSlot)
                                                                                            in
                                                                                                let _ = buildBr(builder)(countCheckBlock)
                                                                                                in
                                                                                                    let _ = positionBuilderAtEnd(builder)(allocBlock)
                                                                                                    in
                                                                                                        let length = buildLoad(builder)(i64)(countSlot)("bfl_length")
                                                                                                        in
                                                                                                            let totalSize =
                                                                                                                buildAdd(builder)(length)(constInt(i64)(24u64)(false))("bfl_total")
                                                                                                            in
                                                                                                                let headerPtr = buildCall(builder)(mallocType)(mallocFn)([totalSize])(1u32)("bfl_header")
                                                                                                                in
                                                                                                                    let _ =
                                                                                                                        buildStore(builder)(constInt(i64)(1u64)(false))(headerPtr)
                                                                                                                    in
                                                                                                                        let sizePtr = gepBytes(builder)(i64)(i8)(headerPtr)(8)("bfl_size_ptr")
                                                                                                                        in
                                                                                                                            let _ =
                                                                                                                                buildStore(builder)(buildAdd(builder)(length)(constInt(i64)(8u64)(false))("bfl_size"))(sizePtr)
                                                                                                                            in
                                                                                                                                let payloadPtr = gepBytes(builder)(i64)(i8)(headerPtr)(16)("bfl_payload")
                                                                                                                                in
                                                                                                                                    let _ = buildStore(builder)(length)(payloadPtr)
                                                                                                                                    in
                                                                                                                                        let dataPtr = gepBytes(builder)(i64)(i8)(headerPtr)(24)("bfl_data")
                                                                                                                                        in
                                                                                                                                            let _ =
                                                                                                                                                buildStore(builder)(buildPtrToInt(builder)(payloadPtr)(i64)("bfl_payload_value"))(resultSlot)
                                                                                                                                            in
                                                                                                                                                let _ = buildStore(builder)(listRef)(curSlot)
                                                                                                                                                in
                                                                                                                                                    let _ =
                                                                                                                                                        buildStore(builder)(constInt(i64)(0u64)(false))(idxSlot)
                                                                                                                                                    in
                                                                                                                                                        let _ = buildBr(builder)(fillCheckBlock)
                                                                                                                                                        in
                                                                                                                                                            let _ = positionBuilderAtEnd(builder)(fillCheckBlock)
                                                                                                                                                            in
                                                                                                                                                                let curFill = buildLoad(builder)(i64)(curSlot)("bfl_cur_fill")
                                                                                                                                                                in
                                                                                                                                                                    let fillDone =
                                                                                                                                                                        buildICmp(builder)(intPredicateEq)(curFill)(constInt(i64)(0u64)(false))("bfl_fill_done")
                                                                                                                                                                    in
                                                                                                                                                                        let _ = buildCondBr(builder)(fillDone)(doneBlock)(fillBodyBlock)
                                                                                                                                                                        in
                                                                                                                                                                            let _ = positionBuilderAtEnd(builder)(fillBodyBlock)
                                                                                                                                                                            in
                                                                                                                                                                                let fillCellPtr = buildIntToPtr(builder)(curFill)(ptrType)("bfl_fill_cell")
                                                                                                                                                                                in
                                                                                                                                                                                    let headVal = buildLoad(builder)(i64)(fillCellPtr)("bfl_head")
                                                                                                                                                                                    in
                                                                                                                                                                                        let idx = buildLoad(builder)(i64)(idxSlot)("bfl_idx_value")
                                                                                                                                                                                        in
                                                                                                                                                                                            let elemPtr = buildGEP(builder)(i8)(dataPtr)([idx])(1u32)("bfl_elem")
                                                                                                                                                                                            in
                                                                                                                                                                                                let _ =
                                                                                                                                                                                                    buildStore(builder)(buildTrunc(builder)(headVal)(i8)("bfl_byte"))(elemPtr)
                                                                                                                                                                                                in
                                                                                                                                                                                                    let _ =
                                                                                                                                                                                                        buildStore(builder)(buildAdd(builder)(idx)(constInt(i64)(1u64)(false))("bfl_idx_next"))(idxSlot)
                                                                                                                                                                                                    in
                                                                                                                                                                                                        let fillTailPtr = gepBytes(builder)(i64)(i8)(fillCellPtr)(8)("bfl_fill_tail_ptr")
                                                                                                                                                                                                        in
                                                                                                                                                                                                            let _ =
                                                                                                                                                                                                                buildStore(builder)(buildLoad(builder)(i64)(fillTailPtr)("bfl_fill_tail"))(curSlot)
                                                                                                                                                                                                            in
                                                                                                                                                                                                                let _ = buildBr(builder)(fillCheckBlock)
                                                                                                                                                                                                                in
                                                                                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(doneBlock)
                                                                                                                                                                                                                    in buildLoad(builder)(i64)(resultSlot)("bfl_result_value"))

// A fixed ASCII line written from a stack buffer via the raw `write` syscall followed by exit 1 —
// the generalized form of the `Bytes.get` panic (`EmitPanic`/`EmitBytesGuard`'s observable shape).
let emitBytesPanicLine builder i64 i8 codes =
    (let count = Ashes.Collection.List.length(codes)
    in
        let bufferType =
            count
            |> Ashes.Number.UInt.fromInt64
            |> arrayType(i8)
        in
            let buffer = buildEntryAlloca(builder)(bufferType)("bytes_panic_msg")
            in
                let _ = storeAsciiBytes(builder)(i64)(i8)(bufferType)(buffer)(0)(codes)
                in
                    let addr = buildPtrToInt(builder)(buffer)(i64)("bytes_panic_addr")
                    in
                        let _ =
                            false
                            |> constInt(i64)(Ashes.Number.UInt.fromInt64(count))
                            |> emitLinuxWrite(builder)(i64)(constInt(i64)(1u64)(false))(addr)
                        in
                            false
                            |> constInt(i64)(1u64)
                            |> emitLinuxProcessExitWithCode(builder)(i64))

// `Byte.empty(Unit)`: a zero-length RC Bytes value (`EmitBytesEmpty`).
let emitBytesEmpty builder i64 i8 allocate =
    (let payloadPtr = allocate(8)("bytes_empty")
    in
        let _ =
            buildStore(builder)(constInt(i64)(0u64)(false))(payloadPtr)
        in buildPtrToInt(builder)(payloadPtr)(i64)("bytes_empty_result"))

// Little-endian byte stores of `value` into `dataPtr[baseOffset + 0 .. width - 1]`.
let recursive emitBytesLeStores builder i64 i8 dataPtr baseOffset value width index name =
    if index >= width
    then Unit
    else
        let shifted =
            if index == 0
            then value
            else
                buildLShr(builder)(value)(constInt(i64)(Ashes.Number.UInt.fromInt64(index * 8))(false))(name + "_shr" + Ashes.Text.fromInt(index))
        in
            let byteOffset =
                buildAdd(builder)(baseOffset)(constInt(i64)(Ashes.Number.UInt.fromInt64(index))(false))(name + "_off" + Ashes.Text.fromInt(index))
            in
                let pointer = buildGEP(builder)(i8)(dataPtr)([byteOffset])(1u32)(name + "_ptr" + Ashes.Text.fromInt(index))
                in
                    let _ =
                        buildStore(builder)(buildTrunc(builder)(shifted)(i8)(name + "_b" + Ashes.Text.fromInt(index)))(pointer)
                    in emitBytesLeStores(builder)(i64)(i8)(dataPtr)(baseOffset)(value)(width)(index + 1)(name)

// `Byte.u16Le`/`u32Le`/`u64Le`: a fresh `width`-byte RC Bytes value holding the little-endian
// encoding (`EmitBytesU16Le`'s exact shape, parameterized over the width).
let emitBytesUnsignedLe builder i64 i8 allocate width value name =
    (let payloadPtr = allocate(16)(name)
    in
        let _ =
            buildStore(builder)(constInt(i64)(Ashes.Number.UInt.fromInt64(width))(false))(payloadPtr)
        in
            let dataPtr = gepBytes(builder)(i64)(i8)(payloadPtr)(8)(name + "_data")
            in
                let _ =
                    emitBytesLeStores(builder)(i64)(i8)(dataPtr)(constInt(i64)(0u64)(false))(value)(width)(0)(name)
                in buildPtrToInt(builder)(payloadPtr)(i64)(name + "_result"))

// Little-endian byte reads assembling `dataPtr[offset + 0 .. width - 1]` into one word.
let recursive emitBytesLeReads builder i64 i8 dataPtr offsetVal width index acc name =
    if index >= width
    then acc
    else
        let idx =
            buildAdd(builder)(offsetVal)(constInt(i64)(Ashes.Number.UInt.fromInt64(index))(false))(name + "_idx" + Ashes.Text.fromInt(index))
        in
            let pointer = buildGEP(builder)(i8)(dataPtr)([idx])(1u32)(name + "_eptr" + Ashes.Text.fromInt(index))
            in
                let extended =
                    buildZExt(builder)(buildLoad(builder)(i8)(pointer)(name + "_byte" + Ashes.Text.fromInt(index)))(i64)(name + "_ext" + Ashes.Text.fromInt(index))
                in
                    let merged =
                        if index == 0
                        then extended
                        else
                            buildOr(builder)(acc)(buildShl(builder)(extended)(constInt(i64)(Ashes.Number.UInt.fromInt64(index * 8))(false))(name + "_shl" + Ashes.Text.fromInt(index)))(name + "_or" + Ashes.Text.fromInt(index))
                    in emitBytesLeReads(builder)(i64)(i8)(dataPtr)(offsetVal)(width)(index + 1)(merged)(name)

// `Byte.getU16Le`/`getU32Le`/`getU64Le`: bounds-checked little-endian decode
// (`EmitBytesReadLeUnsigned`'s panic-or-assemble shape).
let emitBytesReadLeUnsigned context function_ i64 i8 ptrType builder width panicCodes bytesRef offsetVal name =
    match emitStringParts(builder)(i64)(ptrType)(bytesRef)(name) with
        | (len, dataAddr) ->
            let end_ =
                buildAdd(builder)(offsetVal)(constInt(i64)(Ashes.Number.UInt.fromInt64(width))(false))(name + "_end")
            in
                let oob = buildICmp(builder)(intPredicateUgt)(end_)(len)(name + "_oob")
                in
                    let panicBlock = appendBasicBlock(context)(function_)(name + "_panic")
                    in
                        let okBlock = appendBasicBlock(context)(function_)(name + "_ok")
                        in
                            let _ = buildCondBr(builder)(oob)(panicBlock)(okBlock)
                            in
                                let _ = positionBuilderAtEnd(builder)(panicBlock)
                                in
                                    let _ = emitBytesPanicLine(builder)(i64)(i8)(panicCodes)
                                    in
                                        let _ = positionBuilderAtEnd(builder)(okBlock)
                                        in
                                            let dataPtr = buildIntToPtr(builder)(dataAddr)(ptrType)(name + "_data")
                                            in
                                                emitBytesLeReads(builder)(i64)(i8)(dataPtr)(offsetVal)(width)(0)(constInt(i64)(0u64)(false))(name)

// `EmitCheckedBytesRange`: offset/length negativity and past-end checks against `bufferLength`,
// panicking with the caller's message when invalid.
let emitBytesCheckedRange context function_ i64 i8 builder bufferLength offset length panicCodes name =
    (let zero = constInt(i64)(0u64)(false)
    in
        let offsetNegative = buildICmp(builder)(intPredicateSlt)(offset)(zero)(name + "_offset_negative")
        in
            let lengthNegative = buildICmp(builder)(intPredicateSlt)(length)(zero)(name + "_length_negative")
            in
                let offsetPastEnd = buildICmp(builder)(intPredicateUgt)(offset)(bufferLength)(name + "_offset_past_end")
                in
                    let available = buildSub(builder)(bufferLength)(offset)(name + "_available")
                    in
                        let lengthPastEnd = buildICmp(builder)(intPredicateUgt)(length)(available)(name + "_length_past_end")
                        in
                            let invalid =
                                buildOr(builder)(buildOr(builder)(offsetNegative)(lengthNegative)(name + "_negative"))(buildOr(builder)(offsetPastEnd)(lengthPastEnd)(name + "_past_end"))(name + "_invalid")
                            in
                                let panicBlock = appendBasicBlock(context)(function_)(name + "_range_panic")
                                in
                                    let validBlock = appendBasicBlock(context)(function_)(name + "_range_valid")
                                    in
                                        let _ = buildCondBr(builder)(invalid)(panicBlock)(validBlock)
                                        in
                                            let _ = positionBuilderAtEnd(builder)(panicBlock)
                                            in
                                                let _ = emitBytesPanicLine(builder)(i64)(i8)(panicCodes)
                                                in positionBuilderAtEnd(builder)(validBlock))

// A fresh RC copy of a Bytes value (`EmitBytesCopyOnWrite` with the reuse path always off: every
// selfhost dispatch arm passes reuse = false, so the result never aliases the input).
let emitBytesCopyOnWrite builder i64 i8 ptrType mallocFn mallocType memcpyFn memcpyType bytesRef name =
    match emitStringParts(builder)(i64)(ptrType)(bytesRef)(name + "_src") with
        | (len, srcAddr) -> emitHeapStringFromBytesAddr(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(srcAddr)(len)(name)

// `Byte.copyRange(bytes)(offset)(source)(sourceOffset)(length)`: both ranges checked, the
// destination copied fresh, then one `memcpy` of the range. The fresh copy never aliases the
// source buffer, so stage 0's same-buffer scratch path is unreachable here and the direct copy
// suffices (`EmitBytesCopyRange`/`EmitBytesRangeCopy`).
let emitBytesCopyRange context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType destRef destOffset sourceRef sourceOffset length =
    (let destLen = emitStringLengthValue(builder)(i64)(ptrType)(destRef)("bytes_copyrange_dest_len")
    in
        let _ = emitBytesCheckedRange(context)(function_)(i64)(i8)(builder)(destLen)(destOffset)(length)([66, 121, 116, 101, 115, 46, 99, 111, 112, 121, 82, 97, 110, 103, 101, 58, 32, 100, 101, 115, 116, 105, 110, 97, 116, 105, 111, 110, 32, 114, 97, 110, 103, 101, 32, 111, 117, 116, 32, 111, 102, 32, 98, 111, 117, 110, 100, 115, 10])("bytes_copyrange_dest")
        in
            let sourceLen = emitStringLengthValue(builder)(i64)(ptrType)(sourceRef)("bytes_copyrange_source_len")
            in
                let _ = emitBytesCheckedRange(context)(function_)(i64)(i8)(builder)(sourceLen)(sourceOffset)(length)([66, 121, 116, 101, 115, 46, 99, 111, 112, 121, 82, 97, 110, 103, 101, 58, 32, 115, 111, 117, 114, 99, 101, 32, 114, 97, 110, 103, 101, 32, 111, 117, 116, 32, 111, 102, 32, 98, 111, 117, 110, 100, 115, 10])("bytes_copyrange_source")
                in
                    let result = emitBytesCopyOnWrite(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(destRef)("bytes_copyrange")
                    in
                        match emitStringParts(builder)(i64)(ptrType)(result)("bytes_copyrange_result") with
                            | (_resultLen, resultAddr) ->
                                match emitStringParts(builder)(i64)(ptrType)(sourceRef)("bytes_copyrange_from") with
                                    | (_srcLen, srcAddr) ->
                                        let destination =
                                            buildIntToPtr(builder)(buildAdd(builder)(resultAddr)(destOffset)("bytes_copyrange_dest_addr"))(ptrType)("bytes_copyrange_destination")
                                        in
                                            let source =
                                                buildIntToPtr(builder)(buildAdd(builder)(srcAddr)(sourceOffset)("bytes_copyrange_src_addr"))(ptrType)("bytes_copyrange_source_start")
                                            in
                                                let _ = buildCall(builder)(memcpyType)(memcpyFn)([destination, source, length])(3u32)("bytes_copyrange_copy")
                                                in result)

// `Byte.set`/`setU16Le`/`setU32Le`/`setU64Le`: range checked, the input copied fresh, then the
// little-endian byte stores at the offset (`EmitBytesSetUnsigned`).
let emitBytesSetUnsigned context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType width panicCodes bytesRef offset value name =
    (let bufferLength = emitStringLengthValue(builder)(i64)(ptrType)(bytesRef)(name + "_len")
    in
        let _ =
            emitBytesCheckedRange(context)(function_)(i64)(i8)(builder)(bufferLength)(offset)(constInt(i64)(Ashes.Number.UInt.fromInt64(width))(false))(panicCodes)(name)
        in
            let result = emitBytesCopyOnWrite(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(bytesRef)(name)
            in
                match emitStringParts(builder)(i64)(ptrType)(result)(name + "_result") with
                    | (_resultLen, resultAddr) ->
                        let dataPtr = buildIntToPtr(builder)(resultAddr)(ptrType)(name + "_data")
                        in
                            let _ = emitBytesLeStores(builder)(i64)(i8)(dataPtr)(offset)(value)(width)(0)(name)
                            in result)

// `Byte.scanHash(bytes)(needle)(from)`: one pass that stops at the first `needle` byte while
// FNV-1a-hashing the bytes before it, returning the `(index, hash)` tuple (`EmitBytesScanHash`).
let emitBytesScanHash context function_ i64 i8 ptrType builder mallocFn mallocType bytesRef needleVal fromVal =
    match emitStringParts(builder)(i64)(ptrType)(bytesRef)("bytes_sh") with
        | (len, dataAddr) ->
            let dataPtr = buildIntToPtr(builder)(dataAddr)(ptrType)("bytes_sh_data")
            in
                let needle8 = buildTrunc(builder)(needleVal)(i8)("bytes_sh_needle")
                in
                    let zero = constInt(i64)(0u64)(false)
                    in
                        let fromNeg = buildICmp(builder)(intPredicateSlt)(fromVal)(zero)("bytes_sh_from_neg")
                        in
                            let fromStart = buildSelect(builder)(fromNeg)(zero)(fromVal)("bytes_sh_from")
                            in
                                let idxSlot = buildEntryAlloca(builder)(i64)("bytes_sh_idx")
                                in
                                    let hashSlot = buildEntryAlloca(builder)(i64)("bytes_sh_hash")
                                    in
                                        let foundSlot = buildEntryAlloca(builder)(i64)("bytes_sh_found")
                                        in
                                            let _ = buildStore(builder)(fromStart)(idxSlot)
                                            in
                                                let _ =
                                                    buildStore(builder)(constInt(i64)(14695981039346656037u64)(false))(hashSlot)
                                                in
                                                    let _ =
                                                        buildStore(builder)(constInt(i64)(Ashes.Number.UInt.fromInt64(-1))(false))(foundSlot)
                                                    in
                                                        let checkBlock = appendBasicBlock(context)(function_)("bytes_sh_check")
                                                        in
                                                            let bodyBlock = appendBasicBlock(context)(function_)("bytes_sh_body")
                                                            in
                                                                let hitBlock = appendBasicBlock(context)(function_)("bytes_sh_hit")
                                                                in
                                                                    let stepBlock = appendBasicBlock(context)(function_)("bytes_sh_step")
                                                                    in
                                                                        let doneBlock = appendBasicBlock(context)(function_)("bytes_sh_done")
                                                                        in
                                                                            let _ = buildBr(builder)(checkBlock)
                                                                            in
                                                                                let _ = positionBuilderAtEnd(builder)(checkBlock)
                                                                                in
                                                                                    let idx = buildLoad(builder)(i64)(idxSlot)("bytes_sh_i")
                                                                                    in
                                                                                        let more = buildICmp(builder)(intPredicateUlt)(idx)(len)("bytes_sh_more")
                                                                                        in
                                                                                            let _ = buildCondBr(builder)(more)(bodyBlock)(doneBlock)
                                                                                            in
                                                                                                let _ = positionBuilderAtEnd(builder)(bodyBlock)
                                                                                                in
                                                                                                    let bytePtr = buildGEP(builder)(i8)(dataPtr)([idx])(1u32)("bytes_sh_ptr")
                                                                                                    in
                                                                                                        let curByte = buildLoad(builder)(i8)(bytePtr)("bytes_sh_byte")
                                                                                                        in
                                                                                                            let eq = buildICmp(builder)(intPredicateEq)(curByte)(needle8)("bytes_sh_eq")
                                                                                                            in
                                                                                                                let _ = buildCondBr(builder)(eq)(hitBlock)(stepBlock)
                                                                                                                in
                                                                                                                    let _ = positionBuilderAtEnd(builder)(hitBlock)
                                                                                                                    in
                                                                                                                        let _ = buildStore(builder)(idx)(foundSlot)
                                                                                                                        in
                                                                                                                            let _ = buildBr(builder)(doneBlock)
                                                                                                                            in
                                                                                                                                let _ = positionBuilderAtEnd(builder)(stepBlock)
                                                                                                                                in
                                                                                                                                    let h = buildLoad(builder)(i64)(hashSlot)("bytes_sh_h")
                                                                                                                                    in
                                                                                                                                        let byte64 = buildZExt(builder)(curByte)(i64)("bytes_sh_b64")
                                                                                                                                        in
                                                                                                                                            let hm =
                                                                                                                                                buildMul(builder)(buildXor(builder)(h)(byte64)("bytes_sh_hx"))(constInt(i64)(1099511628211u64)(false))("bytes_sh_hm")
                                                                                                                                            in
                                                                                                                                                let _ = buildStore(builder)(hm)(hashSlot)
                                                                                                                                                in
                                                                                                                                                    let _ =
                                                                                                                                                        buildStore(builder)(buildAdd(builder)(idx)(constInt(i64)(1u64)(false))("bytes_sh_next"))(idxSlot)
                                                                                                                                                    in
                                                                                                                                                        let _ = buildBr(builder)(checkBlock)
                                                                                                                                                        in
                                                                                                                                                            let _ = positionBuilderAtEnd(builder)(doneBlock)
                                                                                                                                                            in
                                                                                                                                                                let tuplePtr = emitRcAllocPayloadPtr(builder)(i64)(i8)(mallocFn)(mallocType)(16)("bytes_sh_tuple")
                                                                                                                                                                in
                                                                                                                                                                    let _ =
                                                                                                                                                                        buildStore(builder)(buildLoad(builder)(i64)(foundSlot)("bytes_sh_found_v"))(tuplePtr)
                                                                                                                                                                    in
                                                                                                                                                                        let hashPtr = gepBytes(builder)(i64)(i8)(tuplePtr)(8)("bytes_sh_t1")
                                                                                                                                                                        in
                                                                                                                                                                            let _ =
                                                                                                                                                                                buildStore(builder)(buildLoad(builder)(i64)(hashSlot)("bytes_sh_hash_v"))(hashPtr)
                                                                                                                                                                            in buildPtrToInt(builder)(tuplePtr)(i64)("bytes_sh_result")

// `Text.toHex(value)`: `0x`-prefixed lowercase hex of the signed value's magnitude, digits written
// back-to-front into a 32-byte stack buffer, then copied into a fresh RC string
// (`EmitIntToHexString`'s zero/digit/prefix/sign phases).
let emitTextToHex context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType value =
    (let bufferType = arrayType(i8)(32u64)
    in
        let buffer = buildEntryAlloca(builder)(bufferType)("tth_buffer")
        in
            let indexSlot = buildEntryAlloca(builder)(i64)("tth_index")
            in
                let workSlot = buildEntryAlloca(builder)(i64)("tth_work")
                in
                    let zero = constInt(i64)(0u64)(false)
                    in
                        let isNegative = buildICmp(builder)(intPredicateSlt)(value)(zero)("tth_is_negative")
                        in
                            let _ = buildStore(builder)(zero)(indexSlot)
                            in
                                let _ =
                                    buildStore(builder)(buildSelect(builder)(isNegative)(buildSub(builder)(zero)(value)("tth_magnitude_neg"))(value)("tth_magnitude"))(workSlot)
                                in
                                    let zeroBlock = appendBasicBlock(context)(function_)("tth_zero")
                                    in
                                        let loopCheckBlock = appendBasicBlock(context)(function_)("tth_loop_check")
                                        in
                                            let loopBodyBlock = appendBasicBlock(context)(function_)("tth_loop_body")
                                            in
                                                let prefixBlock = appendBasicBlock(context)(function_)("tth_prefix")
                                                in
                                                    let signBlock = appendBasicBlock(context)(function_)("tth_sign")
                                                    in
                                                        let finishBlock = appendBasicBlock(context)(function_)("tth_finish")
                                                        in
                                                            let isZero = buildICmp(builder)(intPredicateEq)(value)(zero)("tth_is_zero")
                                                            in
                                                                let _ = buildCondBr(builder)(isZero)(zeroBlock)(loopCheckBlock)
                                                                in
                                                                    let _ = positionBuilderAtEnd(builder)(zeroBlock)
                                                                    in
                                                                        let _ =
                                                                            false
                                                                            |> constInt(i64)(48u64)
                                                                            |> storePrintBufferByte(builder)(i64)(i8)(bufferType)(buffer)(constInt(i64)(31u64)(false))
                                                                        in
                                                                            let _ =
                                                                                buildStore(builder)(constInt(i64)(1u64)(false))(indexSlot)
                                                                            in
                                                                                let _ = buildBr(builder)(prefixBlock)
                                                                                in
                                                                                    let _ = positionBuilderAtEnd(builder)(loopCheckBlock)
                                                                                    in
                                                                                        let work = buildLoad(builder)(i64)(workSlot)("tth_work_value")
                                                                                        in
                                                                                            let loopDone = buildICmp(builder)(intPredicateEq)(work)(zero)("tth_done")
                                                                                            in
                                                                                                let _ = buildCondBr(builder)(loopDone)(prefixBlock)(loopBodyBlock)
                                                                                                in
                                                                                                    let _ = positionBuilderAtEnd(builder)(loopBodyBlock)
                                                                                                    in
                                                                                                        let nibble =
                                                                                                            buildAnd(builder)(work)(constInt(i64)(15u64)(false))("tth_nibble")
                                                                                                        in
                                                                                                            let isDecimal =
                                                                                                                buildICmp(builder)(intPredicateUlt)(nibble)(constInt(i64)(10u64)(false))("tth_is_decimal")
                                                                                                            in
                                                                                                                let digitAscii =
                                                                                                                    buildSelect(builder)(isDecimal)(buildAdd(builder)(nibble)(constInt(i64)(48u64)(false))("tth_decimal_ascii"))(buildAdd(builder)(buildSub(builder)(nibble)(constInt(i64)(10u64)(false))("tth_hex_alpha_index"))(constInt(i64)(97u64)(false))("tth_hex_ascii"))("tth_ascii")
                                                                                                                in
                                                                                                                    let idx = buildLoad(builder)(i64)(indexSlot)("tth_idx_value")
                                                                                                                    in
                                                                                                                        let _ =
                                                                                                                            storePrintBufferByte(builder)(i64)(i8)(bufferType)(buffer)(buildSub(builder)(constInt(i64)(31u64)(false))(idx)("tth_write_idx"))(digitAscii)
                                                                                                                        in
                                                                                                                            let _ =
                                                                                                                                buildStore(builder)(buildLShr(builder)(work)(constInt(i64)(4u64)(false))("tth_next_work"))(workSlot)
                                                                                                                            in
                                                                                                                                let _ =
                                                                                                                                    buildStore(builder)(buildAdd(builder)(idx)(constInt(i64)(1u64)(false))("tth_idx_next"))(indexSlot)
                                                                                                                                in
                                                                                                                                    let _ = buildBr(builder)(loopCheckBlock)
                                                                                                                                    in
                                                                                                                                        let _ = positionBuilderAtEnd(builder)(prefixBlock)
                                                                                                                                        in
                                                                                                                                            let idxBeforePrefix = buildLoad(builder)(i64)(indexSlot)("tth_idx_before_prefix")
                                                                                                                                            in
                                                                                                                                                let _ =
                                                                                                                                                    false
                                                                                                                                                    |> constInt(i64)(120u64)
                                                                                                                                                    |> storePrintBufferByte(builder)(i64)(i8)(bufferType)(buffer)(buildSub(builder)(constInt(i64)(31u64)(false))(idxBeforePrefix)("tth_x_index"))
                                                                                                                                                in
                                                                                                                                                    let idxWithX =
                                                                                                                                                        buildAdd(builder)(idxBeforePrefix)(constInt(i64)(1u64)(false))("tth_idx_with_x")
                                                                                                                                                    in
                                                                                                                                                        let _ =
                                                                                                                                                            false
                                                                                                                                                            |> constInt(i64)(48u64)
                                                                                                                                                            |> storePrintBufferByte(builder)(i64)(i8)(bufferType)(buffer)(buildSub(builder)(constInt(i64)(31u64)(false))(idxWithX)("tth_zero_index"))
                                                                                                                                                        in
                                                                                                                                                            let _ =
                                                                                                                                                                buildStore(builder)(buildAdd(builder)(idxWithX)(constInt(i64)(1u64)(false))("tth_idx_with_prefix"))(indexSlot)
                                                                                                                                                            in
                                                                                                                                                                let _ = buildCondBr(builder)(isNegative)(signBlock)(finishBlock)
                                                                                                                                                                in
                                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(signBlock)
                                                                                                                                                                    in
                                                                                                                                                                        let idxBeforeSign = buildLoad(builder)(i64)(indexSlot)("tth_idx_before_sign")
                                                                                                                                                                        in
                                                                                                                                                                            let _ =
                                                                                                                                                                                false
                                                                                                                                                                                |> constInt(i64)(45u64)
                                                                                                                                                                                |> storePrintBufferByte(builder)(i64)(i8)(bufferType)(buffer)(buildSub(builder)(constInt(i64)(31u64)(false))(idxBeforeSign)("tth_sign_index"))
                                                                                                                                                                            in
                                                                                                                                                                                let _ =
                                                                                                                                                                                    buildStore(builder)(buildAdd(builder)(idxBeforeSign)(constInt(i64)(1u64)(false))("tth_idx_with_sign"))(indexSlot)
                                                                                                                                                                                in
                                                                                                                                                                                    let _ = buildBr(builder)(finishBlock)
                                                                                                                                                                                    in
                                                                                                                                                                                        let _ = positionBuilderAtEnd(builder)(finishBlock)
                                                                                                                                                                                        in
                                                                                                                                                                                            let count = buildLoad(builder)(i64)(indexSlot)("tth_count")
                                                                                                                                                                                            in
                                                                                                                                                                                                let startAddr =
                                                                                                                                                                                                    buildAdd(builder)(buildPtrToInt(builder)(buffer)(i64)("tth_buffer_addr"))(buildSub(builder)(constInt(i64)(32u64)(false))(count)("tth_start_index"))("tth_start_addr")
                                                                                                                                                                                                in emitHeapStringFromBytesAddr(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(startAddr)(count)("tth_string"))

// `Text.parseFloat(text)`: sign, integer digits, optional fraction, optional `e`/`E` exponent —
// each phase a block, the value carried through `f64` slots, ranges checked against the maximum
// finite double (built from its exact bit pattern; the frontend has no exponent literals), and
// `Ok(bits)`/`Error(message)` with stage 0's exact message strings and block structure
// (`EmitTextParseFloat`'s integer/fraction/exponent/scale phases).
let emitTextParseFloat context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType textRef =
    match emitStringParts(builder)(i64)(ptrType)(textRef)("tpf") with
        | (len, bytesAddr) ->
            let f64 = doubleType(context)
            in
                let bytesPtr = buildIntToPtr(builder)(bytesAddr)(ptrType)("tpf_bytes_ptr")
                in
                    let zero = constInt(i64)(0u64)(false)
                    in
                        let one = constInt(i64)(1u64)(false)
                        in
                            let ten =
                                buildBitCast(builder)(constInt(i64)(4621819117588971520u64)(false))(f64)("tpf_ten")
                            in
                                let maxFloat =
                                    buildBitCast(builder)(constInt(i64)(9218868437227405311u64)(false))(f64)("tpf_max_float")
                                in
                                    let indexSlot = buildEntryAlloca(builder)(i64)("tpf_index")
                                    in
                                        let valueSlot = buildEntryAlloca(builder)(f64)("tpf_value")
                                        in
                                            let fractionPlaceSlot = buildEntryAlloca(builder)(f64)("tpf_fraction_place")
                                            in
                                                let negativeSlot = buildEntryAlloca(builder)(i64)("tpf_negative")
                                                in
                                                    let exponentSlot = buildEntryAlloca(builder)(i64)("tpf_exponent")
                                                    in
                                                        let exponentNegativeSlot = buildEntryAlloca(builder)(i64)("tpf_exponent_negative")
                                                        in
                                                            let resultSlot = buildEntryAlloca(builder)(i64)("tpf_result")
                                                            in
                                                                let _ = buildStore(builder)(zero)(indexSlot)
                                                                in
                                                                    let _ =
                                                                        buildStore(builder)(buildBitCast(builder)(constInt(i64)(0u64)(false))(f64)("tpf_zero_f64"))(valueSlot)
                                                                    in
                                                                        let _ =
                                                                            buildStore(builder)(buildBitCast(builder)(constInt(i64)(4591870180066957722u64)(false))(f64)("tpf_tenth"))(fractionPlaceSlot)
                                                                        in
                                                                            let _ = buildStore(builder)(zero)(negativeSlot)
                                                                            in
                                                                                let _ = buildStore(builder)(zero)(exponentSlot)
                                                                                in
                                                                                    let _ = buildStore(builder)(zero)(exponentNegativeSlot)
                                                                                    in
                                                                                        let invalidBlock = appendBasicBlock(context)(function_)("tpf_invalid")
                                                                                        in
                                                                                            let rangeBlock = appendBasicBlock(context)(function_)("tpf_range")
                                                                                            in
                                                                                                let signCheckBlock = appendBasicBlock(context)(function_)("tpf_sign_check")
                                                                                                in
                                                                                                    let minusBlock = appendBasicBlock(context)(function_)("tpf_minus")
                                                                                                    in
                                                                                                        let integerFirstDigitBlock = appendBasicBlock(context)(function_)("tpf_integer_first_digit")
                                                                                                        in
                                                                                                            let integerLoopBodyBlock = appendBasicBlock(context)(function_)("tpf_integer_loop_body")
                                                                                                            in
                                                                                                                let integerValueOkBlock = appendBasicBlock(context)(function_)("tpf_integer_value_ok")
                                                                                                                in
                                                                                                                    let integerMaybeContinueBlock = appendBasicBlock(context)(function_)("tpf_integer_maybe_continue")
                                                                                                                    in
                                                                                                                        let integerAfterDigitBlock = appendBasicBlock(context)(function_)("tpf_integer_after_digit")
                                                                                                                        in
                                                                                                                            let suffixInspectBlock = appendBasicBlock(context)(function_)("tpf_suffix_inspect")
                                                                                                                            in
                                                                                                                                let fractionStartBlock = appendBasicBlock(context)(function_)("tpf_fraction_start")
                                                                                                                                in
                                                                                                                                    let fractionFirstDigitBlock = appendBasicBlock(context)(function_)("tpf_fraction_first_digit")
                                                                                                                                    in
                                                                                                                                        let fractionLoopBodyBlock = appendBasicBlock(context)(function_)("tpf_fraction_loop_body")
                                                                                                                                        in
                                                                                                                                            let fractionMaybeContinueBlock = appendBasicBlock(context)(function_)("tpf_fraction_maybe_continue")
                                                                                                                                            in
                                                                                                                                                let exponentMarkerBlock = appendBasicBlock(context)(function_)("tpf_exponent_marker")
                                                                                                                                                in
                                                                                                                                                    let exponentSignInspectBlock = appendBasicBlock(context)(function_)("tpf_exponent_sign_inspect")
                                                                                                                                                    in
                                                                                                                                                        let exponentDigitDirectBlock = appendBasicBlock(context)(function_)("tpf_exponent_digit_direct")
                                                                                                                                                        in
                                                                                                                                                            let exponentMinusBlock = appendBasicBlock(context)(function_)("tpf_exponent_minus")
                                                                                                                                                            in
                                                                                                                                                                let exponentPlusBlock = appendBasicBlock(context)(function_)("tpf_exponent_plus")
                                                                                                                                                                in
                                                                                                                                                                    let exponentFirstDigitBlock = appendBasicBlock(context)(function_)("tpf_exponent_first_digit")
                                                                                                                                                                    in
                                                                                                                                                                        let exponentLoopBodyBlock = appendBasicBlock(context)(function_)("tpf_exponent_loop_body")
                                                                                                                                                                        in
                                                                                                                                                                            let exponentAccOkBlock = appendBasicBlock(context)(function_)("tpf_exponent_acc_ok")
                                                                                                                                                                            in
                                                                                                                                                                                let exponentMaybeContinueBlock = appendBasicBlock(context)(function_)("tpf_exponent_maybe_continue")
                                                                                                                                                                                in
                                                                                                                                                                                    let exponentDoneBlock = appendBasicBlock(context)(function_)("tpf_exponent_done")
                                                                                                                                                                                    in
                                                                                                                                                                                        let exponentRangeOkBlock = appendBasicBlock(context)(function_)("tpf_exponent_range_ok")
                                                                                                                                                                                        in
                                                                                                                                                                                            let exponentDispatchBlock = appendBasicBlock(context)(function_)("tpf_exponent_dispatch")
                                                                                                                                                                                            in
                                                                                                                                                                                                let exponentMulCheckBlock = appendBasicBlock(context)(function_)("tpf_exponent_mul_check")
                                                                                                                                                                                                in
                                                                                                                                                                                                    let exponentMulBodyBlock = appendBasicBlock(context)(function_)("tpf_exponent_mul_body")
                                                                                                                                                                                                    in
                                                                                                                                                                                                        let mulRangeOkBlock = appendBasicBlock(context)(function_)("tpf_mul_range_ok")
                                                                                                                                                                                                        in
                                                                                                                                                                                                            let exponentDivCheckBlock = appendBasicBlock(context)(function_)("tpf_exponent_div_check")
                                                                                                                                                                                                            in
                                                                                                                                                                                                                let exponentDivBodyBlock = appendBasicBlock(context)(function_)("tpf_exponent_div_body")
                                                                                                                                                                                                                in
                                                                                                                                                                                                                    let finishBlock = appendBasicBlock(context)(function_)("tpf_finish")
                                                                                                                                                                                                                    in
                                                                                                                                                                                                                        let continueBlock = appendBasicBlock(context)(function_)("tpf_continue")
                                                                                                                                                                                                                        in
                                                                                                                                                                                                                            let isEmpty = buildICmp(builder)(intPredicateEq)(len)(zero)("tpf_is_empty")
                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                let _ = buildCondBr(builder)(isEmpty)(invalidBlock)(signCheckBlock)
                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(signCheckBlock)
                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                        let firstByte = emitLoadByteAtI64(builder)(i64)(i8)(bytesPtr)(zero)("tpf_first")
                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                            let isMinus =
                                                                                                                                                                                                                                                buildICmp(builder)(intPredicateEq)(firstByte)(constInt(i64)(45u64)(false))("tpf_is_minus")
                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                let _ = buildCondBr(builder)(isMinus)(minusBlock)(integerFirstDigitBlock)
                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(minusBlock)
                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                        let _ = buildStore(builder)(one)(negativeSlot)
                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                            let _ = buildStore(builder)(one)(indexSlot)
                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                let onlyMinus = buildICmp(builder)(intPredicateEq)(len)(one)("tpf_only_minus")
                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                    let _ = buildCondBr(builder)(onlyMinus)(invalidBlock)(integerFirstDigitBlock)
                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                        let _ = positionBuilderAtEnd(builder)(integerFirstDigitBlock)
                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                            let intStartIndex = buildLoad(builder)(i64)(indexSlot)("tpf_integer_start_index")
                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                let intStartByte = emitLoadByteAtI64(builder)(i64)(i8)(bytesPtr)(intStartIndex)("tpf_integer_start")
                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                    let intStartsWithDigit = emitDecimalDigitCheck(builder)(i64)(intStartByte)("tpf_integer_start_digit")
                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                        let _ = buildCondBr(builder)(intStartsWithDigit)(integerLoopBodyBlock)(invalidBlock)
                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                            let _ = positionBuilderAtEnd(builder)(integerLoopBodyBlock)
                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                let integerBodyIndex = buildLoad(builder)(i64)(indexSlot)("tpf_integer_body_index")
                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                    let integerBodyByte = emitLoadByteAtI64(builder)(i64)(i8)(bytesPtr)(integerBodyIndex)("tpf_integer_body")
                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                        let integerDigitFloat =
                                                                                                                                                                                                                                                                                                            buildSIToFP(builder)(emitDecimalDigitValue(builder)(i64)(integerBodyByte)("tpf_integer_digit"))(f64)("tpf_integer_digit_f64")
                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                            let currentValue = buildLoad(builder)(f64)(valueSlot)("tpf_integer_value")
                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                let updatedValue =
                                                                                                                                                                                                                                                                                                                    buildFAdd(builder)(buildFMul(builder)(currentValue)(ten)("tpf_integer_mul10"))(integerDigitFloat)("tpf_integer_next_value")
                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                    let valueInRange = buildFCmp(builder)(realPredicateOle)(updatedValue)(maxFloat)("tpf_integer_value_in_range")
                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                        let _ = buildCondBr(builder)(valueInRange)(integerValueOkBlock)(rangeBlock)
                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                            let _ = positionBuilderAtEnd(builder)(integerValueOkBlock)
                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                let _ = buildStore(builder)(updatedValue)(valueSlot)
                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                    let integerNextIndex = buildAdd(builder)(integerBodyIndex)(one)("tpf_integer_next_index")
                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                        let _ = buildStore(builder)(integerNextIndex)(indexSlot)
                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                            let integerAtEnd = buildICmp(builder)(intPredicateEq)(integerNextIndex)(len)("tpf_integer_at_end")
                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                let _ = buildCondBr(builder)(integerAtEnd)(finishBlock)(integerMaybeContinueBlock)
                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(integerMaybeContinueBlock)
                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                        let integerNextByte = emitLoadByteAtI64(builder)(i64)(i8)(bytesPtr)(integerNextIndex)("tpf_integer_next")
                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                            let integerHasNextDigit = emitDecimalDigitCheck(builder)(i64)(integerNextByte)("tpf_integer_has_next")
                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                let _ = buildCondBr(builder)(integerHasNextDigit)(integerLoopBodyBlock)(integerAfterDigitBlock)
                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(integerAfterDigitBlock)
                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                        let suffixByte =
                                                                                                                                                                                                                                                                                                                                                                            emitLoadByteAtI64(builder)(i64)(i8)(bytesPtr)(buildLoad(builder)(i64)(indexSlot)("tpf_suffix_index"))("tpf_suffix")
                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                            let isDot =
                                                                                                                                                                                                                                                                                                                                                                                buildICmp(builder)(intPredicateEq)(suffixByte)(constInt(i64)(46u64)(false))("tpf_is_dot")
                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                let _ = buildCondBr(builder)(isDot)(fractionStartBlock)(suffixInspectBlock)
                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(suffixInspectBlock)
                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                        let suffixInspectByte =
                                                                                                                                                                                                                                                                                                                                                                                            emitLoadByteAtI64(builder)(i64)(i8)(bytesPtr)(buildLoad(builder)(i64)(indexSlot)("tpf_suffix_inspect_index"))("tpf_suffix_inspect")
                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                            let isExponentMarker =
                                                                                                                                                                                                                                                                                                                                                                                                buildOr(builder)(buildICmp(builder)(intPredicateEq)(suffixInspectByte)(constInt(i64)(101u64)(false))("tpf_is_lower_exp"))(buildICmp(builder)(intPredicateEq)(suffixInspectByte)(constInt(i64)(69u64)(false))("tpf_is_upper_exp"))("tpf_is_exponent_marker")
                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                let _ = buildCondBr(builder)(isExponentMarker)(exponentMarkerBlock)(invalidBlock)
                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(fractionStartBlock)
                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                        let fractionIndex =
                                                                                                                                                                                                                                                                                                                                                                                                            buildAdd(builder)(buildLoad(builder)(i64)(indexSlot)("tpf_fraction_index"))(one)("tpf_fraction_start_index")
                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                            let _ = buildStore(builder)(fractionIndex)(indexSlot)
                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                let fractionPastEnd = buildICmp(builder)(intPredicateEq)(fractionIndex)(len)("tpf_fraction_past_end")
                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                    let _ = buildCondBr(builder)(fractionPastEnd)(invalidBlock)(fractionFirstDigitBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                        let _ = positionBuilderAtEnd(builder)(fractionFirstDigitBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                            let fractionFirstByte =
                                                                                                                                                                                                                                                                                                                                                                                                                                emitLoadByteAtI64(builder)(i64)(i8)(bytesPtr)(buildLoad(builder)(i64)(indexSlot)("tpf_fraction_first_index"))("tpf_fraction_first")
                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                let fractionStartsWithDigit = emitDecimalDigitCheck(builder)(i64)(fractionFirstByte)("tpf_fraction_first_digit")
                                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                                    let _ = buildCondBr(builder)(fractionStartsWithDigit)(fractionLoopBodyBlock)(invalidBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                                        let _ = positionBuilderAtEnd(builder)(fractionLoopBodyBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                                            let fractionBodyIndex = buildLoad(builder)(i64)(indexSlot)("tpf_fraction_body_index")
                                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                                let fractionBodyByte = emitLoadByteAtI64(builder)(i64)(i8)(bytesPtr)(fractionBodyIndex)("tpf_fraction_body")
                                                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                                                    let fractionDigitFloat =
                                                                                                                                                                                                                                                                                                                                                                                                                                                        buildSIToFP(builder)(emitDecimalDigitValue(builder)(i64)(fractionBodyByte)("tpf_fraction_digit"))(f64)("tpf_fraction_digit_f64")
                                                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                                                        let fractionPlace = buildLoad(builder)(f64)(fractionPlaceSlot)("tpf_fraction_place_value")
                                                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                                                            let _ =
                                                                                                                                                                                                                                                                                                                                                                                                                                                                buildStore(builder)(buildFAdd(builder)(buildLoad(builder)(f64)(valueSlot)("tpf_fraction_value"))(buildFMul(builder)(fractionDigitFloat)(fractionPlace)("tpf_fraction_contribution"))("tpf_fraction_next_value"))(valueSlot)
                                                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                let _ =
                                                                                                                                                                                                                                                                                                                                                                                                                                                                    buildStore(builder)(buildFDiv(builder)(fractionPlace)(ten)("tpf_fraction_next_place"))(fractionPlaceSlot)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                    let fractionNextIndex = buildAdd(builder)(fractionBodyIndex)(one)("tpf_fraction_next_index")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                        let _ = buildStore(builder)(fractionNextIndex)(indexSlot)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                            let fractionAtEnd = buildICmp(builder)(intPredicateEq)(fractionNextIndex)(len)("tpf_fraction_at_end")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                let _ = buildCondBr(builder)(fractionAtEnd)(finishBlock)(fractionMaybeContinueBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(fractionMaybeContinueBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        let fractionNextByte = emitLoadByteAtI64(builder)(i64)(i8)(bytesPtr)(fractionNextIndex)("tpf_fraction_next")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            let fractionHasNextDigit = emitDecimalDigitCheck(builder)(i64)(fractionNextByte)("tpf_fraction_has_next")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                let _ = buildCondBr(builder)(fractionHasNextDigit)(fractionLoopBodyBlock)(suffixInspectBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(exponentMarkerBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        let exponentMarkerIndex =
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            buildAdd(builder)(buildLoad(builder)(i64)(indexSlot)("tpf_exponent_marker_index"))(one)("tpf_exponent_start_index")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            let _ = buildStore(builder)(exponentMarkerIndex)(indexSlot)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                let exponentPastEnd = buildICmp(builder)(intPredicateEq)(exponentMarkerIndex)(len)("tpf_exponent_past_end")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    let _ = buildCondBr(builder)(exponentPastEnd)(invalidBlock)(exponentSignInspectBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        let _ = positionBuilderAtEnd(builder)(exponentSignInspectBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            let exponentSignIndex = buildLoad(builder)(i64)(indexSlot)("tpf_exponent_sign_index")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                let exponentSignByte = emitLoadByteAtI64(builder)(i64)(i8)(bytesPtr)(exponentSignIndex)("tpf_exponent_sign")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    let exponentIsMinus =
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        buildICmp(builder)(intPredicateEq)(exponentSignByte)(constInt(i64)(45u64)(false))("tpf_exponent_is_minus")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        let _ = buildCondBr(builder)(exponentIsMinus)(exponentMinusBlock)(exponentDigitDirectBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            let _ = positionBuilderAtEnd(builder)(exponentDigitDirectBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                let exponentIsPlus =
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    buildICmp(builder)(intPredicateEq)(exponentSignByte)(constInt(i64)(43u64)(false))("tpf_exponent_is_plus")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    let _ = buildCondBr(builder)(exponentIsPlus)(exponentPlusBlock)(exponentFirstDigitBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        let _ = positionBuilderAtEnd(builder)(exponentMinusBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            let _ = buildStore(builder)(one)(exponentNegativeSlot)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                let exponentAfterMinusIndex = buildAdd(builder)(exponentSignIndex)(one)("tpf_exponent_after_minus")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    let _ = buildStore(builder)(exponentAfterMinusIndex)(indexSlot)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        let exponentMinusPastEnd = buildICmp(builder)(intPredicateEq)(exponentAfterMinusIndex)(len)("tpf_exponent_minus_past_end")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            let _ = buildCondBr(builder)(exponentMinusPastEnd)(invalidBlock)(exponentFirstDigitBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                let _ = positionBuilderAtEnd(builder)(exponentPlusBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    let exponentAfterPlusIndex = buildAdd(builder)(exponentSignIndex)(one)("tpf_exponent_after_plus")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        let _ = buildStore(builder)(exponentAfterPlusIndex)(indexSlot)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            let exponentPlusPastEnd = buildICmp(builder)(intPredicateEq)(exponentAfterPlusIndex)(len)("tpf_exponent_plus_past_end")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                let _ = buildCondBr(builder)(exponentPlusPastEnd)(invalidBlock)(exponentFirstDigitBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(exponentFirstDigitBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        let exponentFirstByte =
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            emitLoadByteAtI64(builder)(i64)(i8)(bytesPtr)(buildLoad(builder)(i64)(indexSlot)("tpf_exponent_first_index"))("tpf_exponent_first")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            let exponentStartsWithDigit = emitDecimalDigitCheck(builder)(i64)(exponentFirstByte)("tpf_exponent_first_digit")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                let _ = buildCondBr(builder)(exponentStartsWithDigit)(exponentLoopBodyBlock)(invalidBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(exponentLoopBodyBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        let exponentBodyIndex = buildLoad(builder)(i64)(indexSlot)("tpf_exponent_body_index")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            let exponentDigit =
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                emitDecimalDigitValue(builder)(i64)(emitLoadByteAtI64(builder)(i64)(i8)(bytesPtr)(exponentBodyIndex)("tpf_exponent_body"))("tpf_exponent_digit")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                let exponentValue = buildLoad(builder)(i64)(exponentSlot)("tpf_exponent_value")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    let exponentThreshold =
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        buildUDiv(builder)(buildSub(builder)(constInt(i64)(324u64)(false))(exponentDigit)("tpf_exponent_limit_minus_digit"))(constInt(i64)(10u64)(false))("tpf_exponent_threshold")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        let exponentOverflow = buildICmp(builder)(intPredicateUgt)(exponentValue)(exponentThreshold)("tpf_exponent_overflow")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            let _ = buildCondBr(builder)(exponentOverflow)(rangeBlock)(exponentAccOkBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                let _ = positionBuilderAtEnd(builder)(exponentAccOkBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    let _ =
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        buildStore(builder)(buildAdd(builder)(buildMul(builder)(exponentValue)(constInt(i64)(10u64)(false))("tpf_exponent_mul10"))(exponentDigit)("tpf_next_exponent"))(exponentSlot)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        let exponentNextIndex = buildAdd(builder)(exponentBodyIndex)(one)("tpf_exponent_next_index")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            let _ = buildStore(builder)(exponentNextIndex)(indexSlot)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                let exponentAtEnd = buildICmp(builder)(intPredicateEq)(exponentNextIndex)(len)("tpf_exponent_at_end")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    let _ = buildCondBr(builder)(exponentAtEnd)(exponentDoneBlock)(exponentMaybeContinueBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        let _ = positionBuilderAtEnd(builder)(exponentMaybeContinueBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            let exponentNextByte = emitLoadByteAtI64(builder)(i64)(i8)(bytesPtr)(exponentNextIndex)("tpf_exponent_next")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                let exponentHasNextDigit = emitDecimalDigitCheck(builder)(i64)(exponentNextByte)("tpf_exponent_has_next")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    let _ = buildCondBr(builder)(exponentHasNextDigit)(exponentLoopBodyBlock)(invalidBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        let _ = positionBuilderAtEnd(builder)(exponentDoneBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            let exponentNegativeFlag = buildLoad(builder)(i64)(exponentNegativeSlot)("tpf_exponent_negative_flag")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                let exponentIsNegative = buildICmp(builder)(intPredicateNe)(exponentNegativeFlag)(zero)("tpf_exponent_is_negative")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    let parsedExponent = buildLoad(builder)(i64)(exponentSlot)("tpf_parsed_exponent")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        let positiveRangeViolation =
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            buildAnd(builder)(buildICmp(builder)(intPredicateEq)(exponentNegativeFlag)(zero)("tpf_positive_exponent"))(buildICmp(builder)(intPredicateUgt)(parsedExponent)(constInt(i64)(308u64)(false))("tpf_positive_exponent_too_large"))("tpf_positive_range_violation")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            let _ = buildCondBr(builder)(positiveRangeViolation)(rangeBlock)(exponentRangeOkBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                let _ = positionBuilderAtEnd(builder)(exponentRangeOkBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    let zeroExponent = buildICmp(builder)(intPredicateEq)(parsedExponent)(zero)("tpf_zero_exponent")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        let _ = buildCondBr(builder)(zeroExponent)(finishBlock)(exponentDispatchBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            let _ = positionBuilderAtEnd(builder)(exponentDispatchBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                let _ = buildCondBr(builder)(exponentIsNegative)(exponentDivCheckBlock)(exponentMulCheckBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(exponentMulCheckBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        let mulCounter = buildLoad(builder)(i64)(exponentSlot)("tpf_mul_counter")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            let mulDone = buildICmp(builder)(intPredicateEq)(mulCounter)(zero)("tpf_mul_done")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                let _ = buildCondBr(builder)(mulDone)(finishBlock)(exponentMulBodyBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(exponentMulBodyBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        let mulNextValue =
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            buildFMul(builder)(buildLoad(builder)(f64)(valueSlot)("tpf_mul_value"))(ten)("tpf_mul_next_value")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            let mulInRange = buildFCmp(builder)(realPredicateOle)(mulNextValue)(maxFloat)("tpf_mul_in_range")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                let _ = buildCondBr(builder)(mulInRange)(mulRangeOkBlock)(rangeBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(mulRangeOkBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        let _ = buildStore(builder)(mulNextValue)(valueSlot)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            let _ =
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                buildStore(builder)(buildSub(builder)(mulCounter)(one)("tpf_mul_next_counter"))(exponentSlot)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                let _ = buildBr(builder)(exponentMulCheckBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(exponentDivCheckBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        let divCounter = buildLoad(builder)(i64)(exponentSlot)("tpf_div_counter")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            let divDone = buildICmp(builder)(intPredicateEq)(divCounter)(zero)("tpf_div_done")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                let _ = buildCondBr(builder)(divDone)(finishBlock)(exponentDivBodyBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(exponentDivBodyBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        let _ =
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            buildStore(builder)(buildFDiv(builder)(buildLoad(builder)(f64)(valueSlot)("tpf_div_value"))(ten)("tpf_div_next_value"))(valueSlot)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            let _ =
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                buildStore(builder)(buildSub(builder)(divCounter)(one)("tpf_div_next_counter"))(exponentSlot)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                let _ = buildBr(builder)(exponentDivCheckBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(finishBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        let signFlag = buildLoad(builder)(i64)(negativeSlot)("tpf_sign_flag")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            let finalIsNegative = buildICmp(builder)(intPredicateNe)(signFlag)(zero)("tpf_final_is_negative")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                let unsignedValue = buildLoad(builder)(f64)(valueSlot)("tpf_unsigned_value")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    let finalValue =
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        buildSelect(builder)(finalIsNegative)(buildXor(builder)(buildBitCast(builder)(unsignedValue)(i64)("tpf_unsigned_bits"))(constInt(i64)(9223372036854775808u64)(false))("tpf_negated_bits"))(buildBitCast(builder)(unsignedValue)(i64)("tpf_unsigned_bits_positive"))("tpf_final_value")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        let finalBits = finalValue
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            let _ =
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(0)(finalBits)("tpf_ok"))(resultSlot)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                let _ = buildBr(builder)(continueBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(invalidBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        let invalidMessage = emitAsciiHeapString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)([65, 115, 104, 101, 115, 46, 84, 101, 120, 116, 46, 112, 97, 114, 115, 101, 70, 108, 111, 97, 116, 40, 41, 32, 105, 110, 118, 97, 108, 105, 100, 32, 105, 110, 112, 117, 116])("tpf_invalid_msg")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            let _ =
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(1)(invalidMessage)("tpf_invalid_result"))(resultSlot)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                let _ = buildBr(builder)(continueBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(rangeBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        let rangeMessage = emitAsciiHeapString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)([65, 115, 104, 101, 115, 46, 84, 101, 120, 116, 46, 112, 97, 114, 115, 101, 70, 108, 111, 97, 116, 40, 41, 32, 111, 117, 116, 32, 111, 102, 32, 114, 97, 110, 103, 101])("tpf_range_msg")
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            let _ =
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(1)(rangeMessage)("tpf_range_result"))(resultSlot)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                let _ = buildBr(builder)(continueBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(continueBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    in buildLoad(builder)(i64)(resultSlot)("tpf_result_value")

// The UTF-8 lead byte for a rune of the given width: the rune shifted down and OR'd with the
// width's prefix bits — `RuneLeadByte`'s shape.
let emitRuneLeadByte builder i64 rune shift prefix name =
    buildOr(builder)(buildLShr(builder)(rune)(constInt(i64)(Ashes.Number.UInt.fromInt64(shift))(false))(name + "_shift"))(constInt(i64)(Ashes.Number.UInt.fromInt64(prefix))(false))(name)

// A UTF-8 continuation byte: six payload bits from the given shift, prefixed `10` —
// `RuneContinuationByte`'s shape.
let emitRuneContinuationByte builder i64 rune shift name =
    (let shifted =
        if shift == 0
        then rune
        else
            buildLShr(builder)(rune)(constInt(i64)(Ashes.Number.UInt.fromInt64(shift))(false))(name + "_shift")
    in
        buildOr(builder)(buildAnd(builder)(shifted)(constInt(i64)(63u64)(false))(name + "_payload"))(constInt(i64)(128u64)(false))(name))

let emitRuneStoreByte builder i64 i8 bytesPtr index value name =
    (let pointer =
        buildGEP(builder)(i8)(bytesPtr)([constInt(i64)(Ashes.Number.UInt.fromInt64(index))(false)])(1u32)(name + "_ptr")
    in
        buildStore(builder)(buildTrunc(builder)(value)(i8)(name))(pointer))

// `Rune.toText(rune)`: the rune encoded as a fresh RC heap string of its UTF-8 width — a reserved
// four-byte payload so the straight-line stores never address past the allocation, with the
// logical length being the selected width; `EmitRuneToText`/`EmitRuneStoreUtf8`'s exact select
// chains.
let emitRuneToText builder i64 i8 allocate rune =
    (let widthThreeOrFour =
        buildSelect(builder)(buildICmp(builder)(intPredicateUlt)(rune)(constInt(i64)(65536u64)(false))("rune_text_three"))(constInt(i64)(3u64)(false))(constInt(i64)(4u64)(false))("rune_text_width34")
    in
        let widthTwoOrMore =
            buildSelect(builder)(buildICmp(builder)(intPredicateUlt)(rune)(constInt(i64)(2048u64)(false))("rune_text_two"))(constInt(i64)(2u64)(false))(widthThreeOrFour)("rune_text_width234")
        in
            let width =
                buildSelect(builder)(buildICmp(builder)(intPredicateUlt)(rune)(constInt(i64)(128u64)(false))("rune_text_ascii"))(constInt(i64)(1u64)(false))(widthTwoOrMore)("rune_text_width")
            in
                let textPtr = allocate(12)("rune_text")
                in
                    let _ = buildStore(builder)(width)(textPtr)
                    in
                        let bytesPtr = gepBytes(builder)(i64)(i8)(textPtr)(8)("rune_text_bytes")
                        in
                            let one =
                                buildICmp(builder)(intPredicateEq)(width)(constInt(i64)(1u64)(false))("rune_store_one")
                            in
                                let two =
                                    buildICmp(builder)(intPredicateEq)(width)(constInt(i64)(2u64)(false))("rune_store_two")
                                in
                                    let three =
                                        buildICmp(builder)(intPredicateEq)(width)(constInt(i64)(3u64)(false))("rune_store_three")
                                    in
                                        let first =
                                            buildSelect(builder)(one)(rune)(
                                                buildSelect(builder)(two)(emitRuneLeadByte(builder)(i64)(rune)(6)(192)("rune_lead2"))(
                                                    buildSelect(builder)(three)(emitRuneLeadByte(builder)(i64)(rune)(12)(224)("rune_lead3"))(emitRuneLeadByte(builder)(i64)(rune)(18)(240)("rune_lead4"))("rune_first34")
                                                )("rune_first234")
                                            )("rune_first")
                                        in
                                            let _ = emitRuneStoreByte(builder)(i64)(i8)(bytesPtr)(0)(first)("rune_text_b0")
                                            in
                                                let second =
                                                    buildSelect(builder)(two)(emitRuneContinuationByte(builder)(i64)(rune)(0)("rune_second2"))(
                                                        buildSelect(builder)(three)(emitRuneContinuationByte(builder)(i64)(rune)(6)("rune_second3"))(emitRuneContinuationByte(builder)(i64)(rune)(12)("rune_second4"))("rune_second34")
                                                    )("rune_second")
                                                in
                                                    let _ = emitRuneStoreByte(builder)(i64)(i8)(bytesPtr)(1)(second)("rune_text_b1")
                                                    in
                                                        let third =
                                                            buildSelect(builder)(three)(emitRuneContinuationByte(builder)(i64)(rune)(0)("rune_third3"))(emitRuneContinuationByte(builder)(i64)(rune)(6)("rune_third4"))("rune_third")
                                                        in
                                                            let _ = emitRuneStoreByte(builder)(i64)(i8)(bytesPtr)(2)(third)("rune_text_b2")
                                                            in
                                                                let _ =
                                                                    emitRuneStoreByte(builder)(i64)(i8)(bytesPtr)(3)(emitRuneContinuationByte(builder)(i64)(rune)(0)("rune_fourth"))("rune_text_b3")
                                                                in buildPtrToInt(builder)(textPtr)(i64)("rune_text_result"))
