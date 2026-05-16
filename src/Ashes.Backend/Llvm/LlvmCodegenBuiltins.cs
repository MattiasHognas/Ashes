using Ashes.Semantics;
using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{
    private static LlvmValueHandle EmitReadLine(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle inputBufType = LlvmApi.ArrayType2(state.I8, InputBufSize);
        LlvmValueHandle inputBuf = LlvmApi.BuildAlloca(builder, inputBufType, "read_line_buf");
        LlvmValueHandle inputBufPtr = GetArrayElementPointer(state, inputBufType, inputBuf, LlvmApi.ConstInt(state.I64, 0, 0), "read_line_buf_ptr");
        LlvmValueHandle byteSlot = LlvmApi.BuildAlloca(builder, state.I8, "read_line_byte");
        LlvmValueHandle lenSlot = LlvmApi.BuildAlloca(builder, state.I64, "read_line_len");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "read_line_result");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), lenSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

        LlvmValueHandle stdinHandle = default;
        LlvmValueHandle bytesReadSlot = default;
        if (state.Flavor == LlvmCodegenFlavor.WindowsX64)
        {
            stdinHandle = EmitWindowsGetStdHandle(state, StdInputHandle, "stdin_handle");
            bytesReadSlot = LlvmApi.BuildAlloca(builder, state.I32, "read_line_bytes_read");
        }

        var loopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_loop");
        var inspectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_inspect");
        var skipCrBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_skip_cr");
        var storeByteBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_store_byte");
        var appendByteBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_append_byte");
        var eofBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_eof");
        var finishSomeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_finish_some");
        var returnNoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_return_none");
        var overflowBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_overflow");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_continue");

        LlvmApi.BuildBr(builder, loopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBlock);
        LlvmValueHandle bytesRead = IsLinuxFlavor(state.Flavor)
            ? EmitLinuxSyscall(
                state,
                SyscallRead,
                LlvmApi.ConstInt(state.I64, 0, 0),
                LlvmApi.BuildPtrToInt(builder, byteSlot, state.I64, "read_line_byte_ptr"),
                LlvmApi.ConstInt(state.I64, 1, 0),
                "sys_read_line")
            : EmitWindowsReadByte(state, stdinHandle, byteSlot, bytesReadSlot);
        LlvmValueHandle hasByte = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, bytesRead, LlvmApi.ConstInt(state.I64, 0, 0), "read_line_has_byte");
        LlvmApi.BuildCondBr(builder, hasByte, inspectBlock, eofBlock);

        LlvmApi.PositionBuilderAtEnd(builder, inspectBlock);
        LlvmValueHandle currentByte = LlvmApi.BuildLoad2(builder, state.I8, byteSlot, "read_line_current_byte");
        LlvmValueHandle isLf = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, currentByte, LlvmApi.ConstInt(state.I8, 10, 0), "read_line_is_lf");
        LlvmApi.BuildCondBr(builder, isLf, finishSomeBlock, skipCrBlock);

        LlvmApi.PositionBuilderAtEnd(builder, skipCrBlock);
        LlvmValueHandle isCr = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, currentByte, LlvmApi.ConstInt(state.I8, 13, 0), "read_line_is_cr");
        LlvmApi.BuildCondBr(builder, isCr, loopBlock, storeByteBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storeByteBlock);
        LlvmValueHandle currentLen = LlvmApi.BuildLoad2(builder, state.I64, lenSlot, "read_line_len_value");
        LlvmValueHandle atCapacity = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, currentLen, LlvmApi.ConstInt(state.I64, InputBufSize, 0), "read_line_at_capacity");
        LlvmApi.BuildCondBr(builder, atCapacity, overflowBlock, appendByteBlock);

        LlvmApi.PositionBuilderAtEnd(builder, appendByteBlock);
        LlvmValueHandle destPtr = LlvmApi.BuildGEP2(builder, state.I8, inputBufPtr, [currentLen], "read_line_dest_ptr");
        LlvmApi.BuildStore(builder, currentByte, destPtr);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, currentLen, LlvmApi.ConstInt(state.I64, 1, 0), "read_line_len_next"), lenSlot);
        LlvmApi.BuildBr(builder, loopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, eofBlock);
        LlvmValueHandle lenAtEof = LlvmApi.BuildLoad2(builder, state.I64, lenSlot, "read_line_len_at_eof");
        LlvmValueHandle isEmpty = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, lenAtEof, LlvmApi.ConstInt(state.I64, 0, 0), "read_line_is_empty");
        LlvmApi.BuildCondBr(builder, isEmpty, returnNoneBlock, finishSomeBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finishSomeBlock);
        LlvmValueHandle finalLen = LlvmApi.BuildLoad2(builder, state.I64, lenSlot, "read_line_final_len");
        LlvmValueHandle stringRef = EmitAllocDynamic(state, LlvmApi.BuildAdd(builder, finalLen, LlvmApi.ConstInt(state.I64, 8, 0), "read_line_string_bytes"));
        StoreMemory(state, stringRef, 0, finalLen, "read_line_string_len");
        EmitCopyBytes(state, GetStringBytesPointer(state, stringRef, "read_line_string_dest"), inputBufPtr, finalLen, "read_line_copy_bytes");
        LlvmValueHandle someRef = EmitAllocAdt(state, 1, 1);
        StoreMemory(state, someRef, 8, stringRef, "read_line_some_value");
        LlvmApi.BuildStore(builder, someRef, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, returnNoneBlock);
        LlvmApi.BuildStore(builder, EmitAllocAdt(state, 0, 0), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, overflowBlock);
        EmitPanic(state, EmitStackStringObject(state, "readLine input too long"));

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "read_line_result_value");
    }

    private static LlvmValueHandle EmitFileReadText(LlvmCodegenState state, LlvmValueHandle pathRef)
    {
        return IsLinuxFlavor(state.Flavor)
            ? EmitLinuxFileReadText(state, pathRef)
            : EmitWindowsFileReadText(state, pathRef);
    }

    private static LlvmValueHandle EmitFileWriteText(LlvmCodegenState state, LlvmValueHandle pathRef, LlvmValueHandle textRef)
    {
        return IsLinuxFlavor(state.Flavor)
            ? EmitLinuxFileWriteText(state, pathRef, textRef)
            : EmitWindowsFileWriteText(state, pathRef, textRef);
    }

    private static LlvmValueHandle EmitFileExists(LlvmCodegenState state, LlvmValueHandle pathRef)
    {
        return IsLinuxFlavor(state.Flavor)
            ? EmitLinuxFileExists(state, pathRef)
            : EmitWindowsFileExists(state, pathRef);
    }

    private static LlvmValueHandle EmitTextUncons(LlvmCodegenState state, LlvmValueHandle textRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle len = LoadStringLength(state, textRef, "text_uncons_len");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "text_uncons_result");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

        var emptyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_uncons_empty");
        var nonEmptyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_uncons_non_empty");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_uncons_continue");

        LlvmValueHandle isEmpty = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, len, LlvmApi.ConstInt(state.I64, 0, 0), "text_uncons_is_empty");
        LlvmApi.BuildCondBr(builder, isEmpty, emptyBlock, nonEmptyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, emptyBlock);
        LlvmApi.BuildStore(builder, EmitAllocAdt(state, 0, 0), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, nonEmptyBlock);
        LlvmValueHandle bytesPtr = GetStringBytesPointer(state, textRef, "text_uncons_bytes");
        LlvmValueHandle firstByte = LlvmApi.BuildZExt(builder, LoadByteAt(state, bytesPtr, LlvmApi.ConstInt(state.I64, 0, 0), "text_uncons_first_byte"), state.I64, "text_uncons_first_byte_i64");
        LlvmValueHandle isAscii = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, firstByte, LlvmApi.ConstInt(state.I64, 0x80, 0), "text_uncons_is_ascii");
        LlvmValueHandle isTwoByte = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ule, firstByte, LlvmApi.ConstInt(state.I64, 0xDF, 0), "text_uncons_is_two_byte");
        LlvmValueHandle isThreeByte = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ule, firstByte, LlvmApi.ConstInt(state.I64, 0xEF, 0), "text_uncons_is_three_byte");
        LlvmValueHandle widthThreeOrFour = LlvmApi.BuildSelect(builder, isThreeByte, LlvmApi.ConstInt(state.I64, 3, 0), LlvmApi.ConstInt(state.I64, 4, 0), "text_uncons_width_3_or_4");
        LlvmValueHandle widthTwoOrMore = LlvmApi.BuildSelect(builder, isTwoByte, LlvmApi.ConstInt(state.I64, 2, 0), widthThreeOrFour, "text_uncons_width_2_or_more");
        LlvmValueHandle widthCandidate = LlvmApi.BuildSelect(builder, isAscii, LlvmApi.ConstInt(state.I64, 1, 0), widthTwoOrMore, "text_uncons_width_candidate");
        LlvmValueHandle hasFullScalar = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, len, widthCandidate, "text_uncons_has_full_scalar");
        LlvmValueHandle headLen = LlvmApi.BuildSelect(builder, hasFullScalar, widthCandidate, LlvmApi.ConstInt(state.I64, 1, 0), "text_uncons_head_len");
        LlvmValueHandle tailLen = LlvmApi.BuildSub(builder, len, headLen, "text_uncons_tail_len");
        LlvmValueHandle tailPtr = LlvmApi.BuildGEP2(builder, state.I8, bytesPtr, [headLen], "text_uncons_tail_ptr");
        LlvmValueHandle headRef = EmitHeapStringSliceFromBytesPointer(state, bytesPtr, headLen, "text_uncons_head");
        LlvmValueHandle tailRef = EmitHeapStringSliceFromBytesPointer(state, tailPtr, tailLen, "text_uncons_tail");
        LlvmValueHandle tupleRef = EmitAlloc(state, 16);
        StoreMemory(state, tupleRef, 0, headRef, "text_uncons_tuple_head");
        StoreMemory(state, tupleRef, 8, tailRef, "text_uncons_tuple_tail");
        LlvmValueHandle someRef = EmitAllocAdt(state, 1, 1);
        StoreMemory(state, someRef, 8, tupleRef, "text_uncons_some_value");
        LlvmApi.BuildStore(builder, someRef, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "text_uncons_result_value");
    }

    private static LlvmValueHandle EmitTextParseInt(LlvmCodegenState state, LlvmValueHandle textRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle len = LoadStringLength(state, textRef, "text_parse_int_len");
        LlvmValueHandle bytesPtr = GetStringBytesPointer(state, textRef, "text_parse_int_bytes");
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, "text_parse_int_index");
        LlvmValueHandle accSlot = LlvmApi.BuildAlloca(builder, state.I64, "text_parse_int_acc");
        LlvmValueHandle negativeSlot = LlvmApi.BuildAlloca(builder, state.I64, "text_parse_int_negative");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "text_parse_int_result");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), indexSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), accSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), negativeSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

        LlvmValueHandle maxPositive = LlvmApi.ConstInt(state.I64, (ulong)long.MaxValue, 0);
        LlvmValueHandle maxNegativeMagnitude = LlvmApi.ConstInt(state.I64, 1UL << 63, 0);

        var invalidBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_int_invalid");
        var signCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_int_sign_check");
        var minusBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_int_minus");
        var loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_int_loop_check");
        var loopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_int_loop_body");
        var updateBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_int_update");
        var overflowBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_int_overflow");
        var finishBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_int_finish");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_int_continue");

        LlvmValueHandle isEmpty = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, len, LlvmApi.ConstInt(state.I64, 0, 0), "text_parse_int_is_empty");
        LlvmApi.BuildCondBr(builder, isEmpty, invalidBlock, signCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, signCheckBlock);
        LlvmValueHandle firstByte = LoadByteAsI64(state, bytesPtr, LlvmApi.ConstInt(state.I64, 0, 0), "text_parse_int_first_byte");
        LlvmValueHandle isMinus = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, firstByte, LlvmApi.ConstInt(state.I64, (byte)'-', 0), "text_parse_int_is_minus");
        LlvmApi.BuildCondBr(builder, isMinus, minusBlock, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, minusBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), negativeSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), indexSlot);
        LlvmValueHandle onlyMinus = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, len, LlvmApi.ConstInt(state.I64, 1, 0), "text_parse_int_only_minus");
        LlvmApi.BuildCondBr(builder, onlyMinus, invalidBlock, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle index = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "text_parse_int_index_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, index, len, "text_parse_int_done");
        LlvmApi.BuildCondBr(builder, done, finishBlock, loopBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBodyBlock);
        LlvmValueHandle currentByte = LoadByteAsI64(state, bytesPtr, index, "text_parse_int_current_byte");
        LlvmValueHandle isDigit = BuildDecimalDigitCheck(state, currentByte, "text_parse_int_digit_check");
        LlvmApi.BuildCondBr(builder, isDigit, updateBlock, invalidBlock);

        LlvmApi.PositionBuilderAtEnd(builder, updateBlock);
        LlvmValueHandle digit = BuildDecimalDigitValue(state, currentByte, "text_parse_int_digit");
        LlvmValueHandle acc = LlvmApi.BuildLoad2(builder, state.I64, accSlot, "text_parse_int_acc_value");
        LlvmValueHandle negativeFlag = LlvmApi.BuildLoad2(builder, state.I64, negativeSlot, "text_parse_int_negative_value");
        LlvmValueHandle isNegative = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, negativeFlag, LlvmApi.ConstInt(state.I64, 0, 0), "text_parse_int_is_negative");
        LlvmValueHandle limit = LlvmApi.BuildSelect(builder, isNegative, maxNegativeMagnitude, maxPositive, "text_parse_int_limit");
        LlvmValueHandle threshold = LlvmApi.BuildUDiv(builder, LlvmApi.BuildSub(builder, limit, digit, "text_parse_int_limit_minus_digit"), LlvmApi.ConstInt(state.I64, 10, 0), "text_parse_int_threshold");
        LlvmValueHandle overflow = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, acc, threshold, "text_parse_int_overflow_check");
        var accOkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_int_acc_ok");
        LlvmApi.BuildCondBr(builder, overflow, overflowBlock, accOkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, accOkBlock);
        LlvmValueHandle nextAcc = LlvmApi.BuildAdd(builder, LlvmApi.BuildMul(builder, acc, LlvmApi.ConstInt(state.I64, 10, 0), "text_parse_int_mul10"), digit, "text_parse_int_next_acc");
        LlvmApi.BuildStore(builder, nextAcc, accSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, index, LlvmApi.ConstInt(state.I64, 1, 0), "text_parse_int_next_index"), indexSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        LlvmValueHandle magnitude = LlvmApi.BuildLoad2(builder, state.I64, accSlot, "text_parse_int_magnitude");
        LlvmValueHandle finalNegativeFlag = LlvmApi.BuildLoad2(builder, state.I64, negativeSlot, "text_parse_int_final_negative_flag");
        LlvmValueHandle finalIsNegative = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, finalNegativeFlag, LlvmApi.ConstInt(state.I64, 0, 0), "text_parse_int_final_is_negative");
        LlvmValueHandle signedValue = LlvmApi.BuildSelect(builder, finalIsNegative, LlvmApi.BuildSub(builder, LlvmApi.ConstInt(state.I64, 0, 0), magnitude, "text_parse_int_negated"), magnitude, "text_parse_int_final_value");
        LlvmApi.BuildStore(builder, EmitResultOk(state, signedValue), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, invalidBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TextParseIntInvalidMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, overflowBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TextParseIntOverflowMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "text_parse_int_result_value");
    }

    private static LlvmValueHandle EmitTextParseFloat(LlvmCodegenState state, LlvmValueHandle textRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle len = LoadStringLength(state, textRef, "text_parse_float_len");
        LlvmValueHandle bytesPtr = GetStringBytesPointer(state, textRef, "text_parse_float_bytes");
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, "text_parse_float_index");
        LlvmValueHandle valueSlot = LlvmApi.BuildAlloca(builder, state.F64, "text_parse_float_value");
        LlvmValueHandle fractionPlaceSlot = LlvmApi.BuildAlloca(builder, state.F64, "text_parse_float_fraction_place");
        LlvmValueHandle negativeSlot = LlvmApi.BuildAlloca(builder, state.I64, "text_parse_float_negative");
        LlvmValueHandle exponentSlot = LlvmApi.BuildAlloca(builder, state.I64, "text_parse_float_exponent");
        LlvmValueHandle exponentNegativeSlot = LlvmApi.BuildAlloca(builder, state.I64, "text_parse_float_exponent_negative");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "text_parse_float_result");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), indexSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstReal(state.F64, 0.0), valueSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstReal(state.F64, 0.1), fractionPlaceSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), negativeSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), exponentSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), exponentNegativeSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

        LlvmValueHandle maxFloat = LlvmApi.ConstReal(state.F64, double.MaxValue);

        var invalidBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_invalid");
        var rangeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_range");
        var signCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_sign_check");
        var minusBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_minus");
        var integerFirstDigitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_integer_first_digit");
        var integerLoopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_integer_loop_check");
        var integerLoopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_integer_loop_body");
        var integerAfterDigitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_integer_after_digit");
        var suffixInspectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_suffix_inspect");
        var fractionStartBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_fraction_start");
        var fractionFirstDigitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_fraction_first_digit");
        var fractionLoopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_fraction_loop_body");
        var exponentMarkerBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_exponent_marker");
        var exponentSignInspectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_exponent_sign_inspect");
        var exponentMinusBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_exponent_minus");
        var exponentPlusBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_exponent_plus");
        var exponentFirstDigitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_exponent_first_digit");
        var exponentLoopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_exponent_loop_body");
        var exponentDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_exponent_done");
        var exponentMulCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_exponent_mul_check");
        var exponentMulBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_exponent_mul_body");
        var exponentDivCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_exponent_div_check");
        var exponentDivBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_exponent_div_body");
        var finishBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_finish");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_continue");

        LlvmValueHandle isEmpty = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, len, LlvmApi.ConstInt(state.I64, 0, 0), "text_parse_float_is_empty");
        LlvmApi.BuildCondBr(builder, isEmpty, invalidBlock, signCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, signCheckBlock);
        LlvmValueHandle firstByte = LoadByteAsI64(state, bytesPtr, LlvmApi.ConstInt(state.I64, 0, 0), "text_parse_float_first_byte");
        LlvmValueHandle isMinus = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, firstByte, LlvmApi.ConstInt(state.I64, (byte)'-', 0), "text_parse_float_is_minus");
        LlvmApi.BuildCondBr(builder, isMinus, minusBlock, integerFirstDigitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, minusBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), negativeSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), indexSlot);
        LlvmValueHandle onlyMinus = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, len, LlvmApi.ConstInt(state.I64, 1, 0), "text_parse_float_only_minus");
        LlvmApi.BuildCondBr(builder, onlyMinus, invalidBlock, integerFirstDigitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, integerFirstDigitBlock);
        LlvmValueHandle intStartIndex = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "text_parse_float_integer_start_index");
        LlvmValueHandle intStartByte = LoadByteAsI64(state, bytesPtr, intStartIndex, "text_parse_float_integer_start_byte");
        LlvmValueHandle intStartsWithDigit = BuildDecimalDigitCheck(state, intStartByte, "text_parse_float_integer_start_digit_check");
        LlvmApi.BuildCondBr(builder, intStartsWithDigit, integerLoopBodyBlock, invalidBlock);

        LlvmApi.PositionBuilderAtEnd(builder, integerLoopCheckBlock);
        LlvmValueHandle integerIndex = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "text_parse_float_integer_index");
        LlvmValueHandle integerDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, integerIndex, len, "text_parse_float_integer_done");
        LlvmApi.BuildCondBr(builder, integerDone, finishBlock, integerAfterDigitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, integerLoopBodyBlock);
        LlvmValueHandle integerBodyIndex = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "text_parse_float_integer_body_index");
        LlvmValueHandle integerBodyByte = LoadByteAsI64(state, bytesPtr, integerBodyIndex, "text_parse_float_integer_body_byte");
        LlvmValueHandle integerDigit = BuildDecimalDigitValue(state, integerBodyByte, "text_parse_float_integer_digit");
        LlvmValueHandle integerDigitFloat = LlvmApi.BuildSIToFP(builder, integerDigit, state.F64, "text_parse_float_integer_digit_f64");
        LlvmValueHandle currentValue = LlvmApi.BuildLoad2(builder, state.F64, valueSlot, "text_parse_float_integer_value");
        LlvmValueHandle updatedValue = LlvmApi.BuildFAdd(builder, LlvmApi.BuildFMul(builder, currentValue, LlvmApi.ConstReal(state.F64, 10.0), "text_parse_float_integer_mul10"), integerDigitFloat, "text_parse_float_integer_next_value");
        LlvmValueHandle valueInRange = LlvmApi.BuildFCmp(builder, LlvmRealPredicate.Ole, updatedValue, maxFloat, "text_parse_float_integer_value_in_range");
        var integerValueOkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_integer_value_ok");
        LlvmApi.BuildCondBr(builder, valueInRange, integerValueOkBlock, rangeBlock);

        LlvmApi.PositionBuilderAtEnd(builder, integerValueOkBlock);
        LlvmApi.BuildStore(builder, updatedValue, valueSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, integerBodyIndex, LlvmApi.ConstInt(state.I64, 1, 0), "text_parse_float_integer_next_index"), indexSlot);
        LlvmValueHandle integerNextIndex = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "text_parse_float_integer_next_index_value");
        LlvmValueHandle integerAtEnd = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, integerNextIndex, len, "text_parse_float_integer_at_end");
        var integerMaybeContinueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_integer_maybe_continue");
        LlvmApi.BuildCondBr(builder, integerAtEnd, finishBlock, integerMaybeContinueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, integerMaybeContinueBlock);
        LlvmValueHandle integerNextByte = LoadByteAsI64(state, bytesPtr, integerNextIndex, "text_parse_float_integer_next_byte");
        LlvmValueHandle integerHasNextDigit = BuildDecimalDigitCheck(state, integerNextByte, "text_parse_float_integer_has_next_digit");
        LlvmApi.BuildCondBr(builder, integerHasNextDigit, integerLoopBodyBlock, integerAfterDigitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, integerAfterDigitBlock);
        LlvmValueHandle suffixIndex = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "text_parse_float_suffix_index");
        LlvmValueHandle suffixByte = LoadByteAsI64(state, bytesPtr, suffixIndex, "text_parse_float_suffix_byte");
        LlvmValueHandle isDot = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, suffixByte, LlvmApi.ConstInt(state.I64, (byte)'.', 0), "text_parse_float_is_dot");
        LlvmApi.BuildCondBr(builder, isDot, fractionStartBlock, suffixInspectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, suffixInspectBlock);
        LlvmValueHandle suffixInspectByte = LoadByteAsI64(state, bytesPtr, LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "text_parse_float_suffix_inspect_index"), "text_parse_float_suffix_inspect_byte");
        LlvmValueHandle isLowerExp = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, suffixInspectByte, LlvmApi.ConstInt(state.I64, (byte)'e', 0), "text_parse_float_is_lower_exp");
        LlvmValueHandle isUpperExp = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, suffixInspectByte, LlvmApi.ConstInt(state.I64, (byte)'E', 0), "text_parse_float_is_upper_exp");
        LlvmValueHandle isExponentMarker = LlvmApi.BuildOr(builder, isLowerExp, isUpperExp, "text_parse_float_is_exponent_marker");
        LlvmApi.BuildCondBr(builder, isExponentMarker, exponentMarkerBlock, invalidBlock);

        LlvmApi.PositionBuilderAtEnd(builder, fractionStartBlock);
        LlvmValueHandle fractionIndex = LlvmApi.BuildAdd(builder, LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "text_parse_float_fraction_index"), LlvmApi.ConstInt(state.I64, 1, 0), "text_parse_float_fraction_start_index");
        LlvmApi.BuildStore(builder, fractionIndex, indexSlot);
        LlvmValueHandle fractionPastEnd = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, fractionIndex, len, "text_parse_float_fraction_past_end");
        LlvmApi.BuildCondBr(builder, fractionPastEnd, invalidBlock, fractionFirstDigitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, fractionFirstDigitBlock);
        LlvmValueHandle fractionFirstIndex = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "text_parse_float_fraction_first_index");
        LlvmValueHandle fractionFirstByte = LoadByteAsI64(state, bytesPtr, fractionFirstIndex, "text_parse_float_fraction_first_byte");
        LlvmValueHandle fractionStartsWithDigit = BuildDecimalDigitCheck(state, fractionFirstByte, "text_parse_float_fraction_first_digit_check");
        LlvmApi.BuildCondBr(builder, fractionStartsWithDigit, fractionLoopBodyBlock, invalidBlock);

        LlvmApi.PositionBuilderAtEnd(builder, fractionLoopBodyBlock);
        LlvmValueHandle fractionBodyIndex = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "text_parse_float_fraction_body_index");
        LlvmValueHandle fractionBodyByte = LoadByteAsI64(state, bytesPtr, fractionBodyIndex, "text_parse_float_fraction_body_byte");
        LlvmValueHandle fractionDigit = BuildDecimalDigitValue(state, fractionBodyByte, "text_parse_float_fraction_digit");
        LlvmValueHandle fractionDigitFloat = LlvmApi.BuildSIToFP(builder, fractionDigit, state.F64, "text_parse_float_fraction_digit_f64");
        LlvmValueHandle fractionValue = LlvmApi.BuildLoad2(builder, state.F64, valueSlot, "text_parse_float_fraction_value");
        LlvmValueHandle fractionPlace = LlvmApi.BuildLoad2(builder, state.F64, fractionPlaceSlot, "text_parse_float_fraction_place_value");
        LlvmValueHandle fractionContribution = LlvmApi.BuildFMul(builder, fractionDigitFloat, fractionPlace, "text_parse_float_fraction_contribution");
        LlvmApi.BuildStore(builder, LlvmApi.BuildFAdd(builder, fractionValue, fractionContribution, "text_parse_float_fraction_next_value"), valueSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildFDiv(builder, fractionPlace, LlvmApi.ConstReal(state.F64, 10.0), "text_parse_float_fraction_next_place"), fractionPlaceSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, fractionBodyIndex, LlvmApi.ConstInt(state.I64, 1, 0), "text_parse_float_fraction_next_index"), indexSlot);
        LlvmValueHandle fractionNextIndex = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "text_parse_float_fraction_next_index_value");
        LlvmValueHandle fractionAtEnd = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, fractionNextIndex, len, "text_parse_float_fraction_at_end");
        var fractionMaybeContinueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_fraction_maybe_continue");
        LlvmApi.BuildCondBr(builder, fractionAtEnd, finishBlock, fractionMaybeContinueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, fractionMaybeContinueBlock);
        LlvmValueHandle fractionNextByte = LoadByteAsI64(state, bytesPtr, fractionNextIndex, "text_parse_float_fraction_next_byte");
        LlvmValueHandle fractionHasNextDigit = BuildDecimalDigitCheck(state, fractionNextByte, "text_parse_float_fraction_has_next_digit");
        LlvmApi.BuildCondBr(builder, fractionHasNextDigit, fractionLoopBodyBlock, suffixInspectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, exponentMarkerBlock);
        LlvmValueHandle exponentMarkerIndex = LlvmApi.BuildAdd(builder, LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "text_parse_float_exponent_marker_index"), LlvmApi.ConstInt(state.I64, 1, 0), "text_parse_float_exponent_start_index");
        LlvmApi.BuildStore(builder, exponentMarkerIndex, indexSlot);
        LlvmValueHandle exponentPastEnd = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, exponentMarkerIndex, len, "text_parse_float_exponent_past_end");
        LlvmApi.BuildCondBr(builder, exponentPastEnd, invalidBlock, exponentSignInspectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, exponentSignInspectBlock);
        LlvmValueHandle exponentSignIndex = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "text_parse_float_exponent_sign_index");
        LlvmValueHandle exponentSignByte = LoadByteAsI64(state, bytesPtr, exponentSignIndex, "text_parse_float_exponent_sign_byte");
        LlvmValueHandle exponentIsMinus = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, exponentSignByte, LlvmApi.ConstInt(state.I64, (byte)'-', 0), "text_parse_float_exponent_is_minus");
        LlvmValueHandle exponentIsPlus = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, exponentSignByte, LlvmApi.ConstInt(state.I64, (byte)'+', 0), "text_parse_float_exponent_is_plus");
        var exponentDigitDirectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_exponent_digit_direct");
        LlvmApi.BuildCondBr(builder, exponentIsMinus, exponentMinusBlock, exponentDigitDirectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, exponentDigitDirectBlock);
        LlvmApi.BuildCondBr(builder, exponentIsPlus, exponentPlusBlock, exponentFirstDigitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, exponentMinusBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), exponentNegativeSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, exponentSignIndex, LlvmApi.ConstInt(state.I64, 1, 0), "text_parse_float_exponent_after_minus_index"), indexSlot);
        LlvmValueHandle exponentMinusPastEnd = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "text_parse_float_exponent_minus_index_value"), len, "text_parse_float_exponent_minus_past_end");
        LlvmApi.BuildCondBr(builder, exponentMinusPastEnd, invalidBlock, exponentFirstDigitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, exponentPlusBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, exponentSignIndex, LlvmApi.ConstInt(state.I64, 1, 0), "text_parse_float_exponent_after_plus_index"), indexSlot);
        LlvmValueHandle exponentPlusPastEnd = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "text_parse_float_exponent_plus_index_value"), len, "text_parse_float_exponent_plus_past_end");
        LlvmApi.BuildCondBr(builder, exponentPlusPastEnd, invalidBlock, exponentFirstDigitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, exponentFirstDigitBlock);
        LlvmValueHandle exponentFirstIndex = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "text_parse_float_exponent_first_index");
        LlvmValueHandle exponentFirstByte = LoadByteAsI64(state, bytesPtr, exponentFirstIndex, "text_parse_float_exponent_first_byte");
        LlvmValueHandle exponentStartsWithDigit = BuildDecimalDigitCheck(state, exponentFirstByte, "text_parse_float_exponent_first_digit_check");
        LlvmApi.BuildCondBr(builder, exponentStartsWithDigit, exponentLoopBodyBlock, invalidBlock);

        LlvmApi.PositionBuilderAtEnd(builder, exponentLoopBodyBlock);
        LlvmValueHandle exponentBodyIndex = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "text_parse_float_exponent_body_index");
        LlvmValueHandle exponentBodyByte = LoadByteAsI64(state, bytesPtr, exponentBodyIndex, "text_parse_float_exponent_body_byte");
        LlvmValueHandle exponentDigit = BuildDecimalDigitValue(state, exponentBodyByte, "text_parse_float_exponent_digit");
        LlvmValueHandle exponentValue = LlvmApi.BuildLoad2(builder, state.I64, exponentSlot, "text_parse_float_exponent_value");
        LlvmValueHandle exponentThreshold = LlvmApi.BuildUDiv(builder, LlvmApi.BuildSub(builder, LlvmApi.ConstInt(state.I64, 324, 0), exponentDigit, "text_parse_float_exponent_limit_minus_digit"), LlvmApi.ConstInt(state.I64, 10, 0), "text_parse_float_exponent_threshold");
        LlvmValueHandle exponentOverflow = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, exponentValue, exponentThreshold, "text_parse_float_exponent_overflow");
        var exponentAccOkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_exponent_acc_ok");
        LlvmApi.BuildCondBr(builder, exponentOverflow, rangeBlock, exponentAccOkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, exponentAccOkBlock);
        LlvmValueHandle nextExponent = LlvmApi.BuildAdd(builder, LlvmApi.BuildMul(builder, exponentValue, LlvmApi.ConstInt(state.I64, 10, 0), "text_parse_float_exponent_mul10"), exponentDigit, "text_parse_float_next_exponent");
        LlvmApi.BuildStore(builder, nextExponent, exponentSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, exponentBodyIndex, LlvmApi.ConstInt(state.I64, 1, 0), "text_parse_float_exponent_next_index"), indexSlot);
        LlvmValueHandle exponentNextIndex = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "text_parse_float_exponent_next_index_value");
        LlvmValueHandle exponentAtEnd = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, exponentNextIndex, len, "text_parse_float_exponent_at_end");
        var exponentMaybeContinueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_exponent_maybe_continue");
        LlvmApi.BuildCondBr(builder, exponentAtEnd, exponentDoneBlock, exponentMaybeContinueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, exponentMaybeContinueBlock);
        LlvmValueHandle exponentNextByte = LoadByteAsI64(state, bytesPtr, exponentNextIndex, "text_parse_float_exponent_next_byte");
        LlvmValueHandle exponentHasNextDigit = BuildDecimalDigitCheck(state, exponentNextByte, "text_parse_float_exponent_has_next_digit");
        LlvmApi.BuildCondBr(builder, exponentHasNextDigit, exponentLoopBodyBlock, invalidBlock);

        LlvmApi.PositionBuilderAtEnd(builder, exponentDoneBlock);
        LlvmValueHandle exponentNegativeFlag = LlvmApi.BuildLoad2(builder, state.I64, exponentNegativeSlot, "text_parse_float_exponent_negative_flag");
        LlvmValueHandle exponentIsNegative = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, exponentNegativeFlag, LlvmApi.ConstInt(state.I64, 0, 0), "text_parse_float_exponent_is_negative");
        LlvmValueHandle parsedExponent = LlvmApi.BuildLoad2(builder, state.I64, exponentSlot, "text_parse_float_parsed_exponent");
        LlvmValueHandle positiveExponentTooLarge = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, parsedExponent, LlvmApi.ConstInt(state.I64, 308, 0), "text_parse_float_positive_exponent_too_large");
        LlvmValueHandle positiveExponent = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, exponentNegativeFlag, LlvmApi.ConstInt(state.I64, 0, 0), "text_parse_float_positive_exponent");
        LlvmValueHandle positiveRangeViolation = LlvmApi.BuildAnd(builder, positiveExponent, positiveExponentTooLarge, "text_parse_float_positive_range_violation");
        var exponentRangeOkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_exponent_range_ok");
        LlvmApi.BuildCondBr(builder, positiveRangeViolation, rangeBlock, exponentRangeOkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, exponentRangeOkBlock);
        LlvmValueHandle zeroExponent = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, parsedExponent, LlvmApi.ConstInt(state.I64, 0, 0), "text_parse_float_zero_exponent");
        var exponentDispatchBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_exponent_dispatch");
        LlvmApi.BuildCondBr(builder, zeroExponent, finishBlock, exponentDispatchBlock);

        LlvmApi.PositionBuilderAtEnd(builder, exponentDispatchBlock);
        LlvmApi.BuildCondBr(builder, exponentIsNegative, exponentDivCheckBlock, exponentMulCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, exponentMulCheckBlock);
        LlvmValueHandle mulCounter = LlvmApi.BuildLoad2(builder, state.I64, exponentSlot, "text_parse_float_mul_counter");
        LlvmValueHandle mulDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, mulCounter, LlvmApi.ConstInt(state.I64, 0, 0), "text_parse_float_mul_done");
        LlvmApi.BuildCondBr(builder, mulDone, finishBlock, exponentMulBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, exponentMulBodyBlock);
        LlvmValueHandle mulValue = LlvmApi.BuildLoad2(builder, state.F64, valueSlot, "text_parse_float_mul_value");
        LlvmValueHandle mulNextValue = LlvmApi.BuildFMul(builder, mulValue, LlvmApi.ConstReal(state.F64, 10.0), "text_parse_float_mul_next_value");
        LlvmValueHandle mulInRange = LlvmApi.BuildFCmp(builder, LlvmRealPredicate.Ole, mulNextValue, maxFloat, "text_parse_float_mul_in_range");
        var mulRangeOkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_mul_range_ok");
        LlvmApi.BuildCondBr(builder, mulInRange, mulRangeOkBlock, rangeBlock);

        LlvmApi.PositionBuilderAtEnd(builder, mulRangeOkBlock);
        LlvmApi.BuildStore(builder, mulNextValue, valueSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildSub(builder, mulCounter, LlvmApi.ConstInt(state.I64, 1, 0), "text_parse_float_mul_next_counter"), exponentSlot);
        LlvmApi.BuildBr(builder, exponentMulCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, exponentDivCheckBlock);
        LlvmValueHandle divCounter = LlvmApi.BuildLoad2(builder, state.I64, exponentSlot, "text_parse_float_div_counter");
        LlvmValueHandle divDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, divCounter, LlvmApi.ConstInt(state.I64, 0, 0), "text_parse_float_div_done");
        LlvmApi.BuildCondBr(builder, divDone, finishBlock, exponentDivBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, exponentDivBodyBlock);
        LlvmValueHandle divValue = LlvmApi.BuildLoad2(builder, state.F64, valueSlot, "text_parse_float_div_value");
        LlvmApi.BuildStore(builder, LlvmApi.BuildFDiv(builder, divValue, LlvmApi.ConstReal(state.F64, 10.0), "text_parse_float_div_next_value"), valueSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildSub(builder, divCounter, LlvmApi.ConstInt(state.I64, 1, 0), "text_parse_float_div_next_counter"), exponentSlot);
        LlvmApi.BuildBr(builder, exponentDivCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        LlvmValueHandle signFlag = LlvmApi.BuildLoad2(builder, state.I64, negativeSlot, "text_parse_float_sign_flag");
        LlvmValueHandle finalIsNegative = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, signFlag, LlvmApi.ConstInt(state.I64, 0, 0), "text_parse_float_final_is_negative");
        LlvmValueHandle unsignedValue = LlvmApi.BuildLoad2(builder, state.F64, valueSlot, "text_parse_float_unsigned_value");
        LlvmValueHandle finalValue = LlvmApi.BuildSelect(builder, finalIsNegative, LlvmApi.BuildFSub(builder, LlvmApi.ConstReal(state.F64, 0.0), unsignedValue, "text_parse_float_negated_value"), unsignedValue, "text_parse_float_final_value");
        LlvmApi.BuildStore(builder, EmitResultOk(state, finalValue), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, invalidBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TextParseFloatInvalidMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, rangeBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TextParseFloatRangeMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "text_parse_float_result_value");
    }

    private static LlvmValueHandle LoadByteAsI64(LlvmCodegenState state, LlvmValueHandle bytesPtr, LlvmValueHandle index, string prefix)
    {
        return LlvmApi.BuildZExt(state.Target.Builder, LoadByteAt(state, bytesPtr, index, prefix), state.I64, prefix + "_i64");
    }

    private static LlvmValueHandle BuildDecimalDigitCheck(LlvmCodegenState state, LlvmValueHandle byteValue, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle geZero = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, byteValue, LlvmApi.ConstInt(state.I64, (byte)'0', 0), prefix + "_ge_zero");
        LlvmValueHandle leNine = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ule, byteValue, LlvmApi.ConstInt(state.I64, (byte)'9', 0), prefix + "_le_nine");
        return LlvmApi.BuildAnd(builder, geZero, leNine, prefix + "_is_digit");
    }

    private static LlvmValueHandle BuildDecimalDigitValue(LlvmCodegenState state, LlvmValueHandle byteValue, string prefix)
    {
        return LlvmApi.BuildSub(state.Target.Builder, byteValue, LlvmApi.ConstInt(state.I64, (byte)'0', 0), prefix + "_value");
    }

    private static LlvmValueHandle EmitLinuxFileReadText(LlvmCodegenState state, LlvmValueHandle pathRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle pathCstr = EmitStringToCString(state, pathRef, "fs_read_path");
        LlvmValueHandle fdSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_read_fd");
        LlvmValueHandle stringSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_read_string");
        LlvmValueHandle remainingSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_read_remaining");
        LlvmValueHandle cursorSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_read_cursor");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_read_result");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), fdSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

        var openBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_open");
        var seekEndBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_seek_end");
        var seekStartBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_seek_start");
        var allocBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_alloc");
        var readCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_loop_check");
        var readBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_loop_body");
        var readDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_done");
        var utf8CheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_utf8_check");
        var closeOkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_close_ok");
        var closeInvalidBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_close_invalid");
        var closeErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_close_error");
        var maybeCloseErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_maybe_close_error");
        var closeHandleBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_close_handle");
        var returnErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_return_error");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_continue");

        LlvmApi.BuildBr(builder, openBlock);

        LlvmApi.PositionBuilderAtEnd(builder, openBlock);
        LlvmValueHandle fd = EmitLinuxSyscall(
            state,
            SyscallOpen,
            LlvmApi.BuildPtrToInt(builder, pathCstr, state.I64, "fs_read_path_ptr"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "fs_read_open_call");
        LlvmApi.BuildStore(builder, fd, fdSlot);
        LlvmValueHandle openFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, fd, LlvmApi.ConstInt(state.I64, 0, 0), "fs_read_open_failed");
        LlvmApi.BuildCondBr(builder, openFailed, returnErrorBlock, seekEndBlock);

        LlvmApi.PositionBuilderAtEnd(builder, seekEndBlock);
        LlvmValueHandle fileLength = EmitLinuxSyscall(
            state,
            SyscallLseek,
            fd,
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, 2, 0),
            "fs_read_seek_end_call");
        LlvmValueHandle seekEndFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, fileLength, LlvmApi.ConstInt(state.I64, 0, 0), "fs_read_seek_end_failed");
        LlvmApi.BuildCondBr(builder, seekEndFailed, maybeCloseErrorBlock, seekStartBlock);

        LlvmApi.PositionBuilderAtEnd(builder, seekStartBlock);
        LlvmValueHandle seekStart = EmitLinuxSyscall(
            state,
            SyscallLseek,
            fd,
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "fs_read_seek_start_call");
        LlvmValueHandle seekStartFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, seekStart, LlvmApi.ConstInt(state.I64, 0, 0), "fs_read_seek_start_failed");
        LlvmApi.BuildCondBr(builder, seekStartFailed, maybeCloseErrorBlock, allocBlock);

        LlvmApi.PositionBuilderAtEnd(builder, allocBlock);
        LlvmValueHandle exceedsLimit = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, fileLength, LlvmApi.ConstInt(state.I64, MaxFileReadBytes, 0), "fs_read_exceeds_limit");
        var withinLimitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_within_limit");
        LlvmApi.BuildCondBr(builder, exceedsLimit, maybeCloseErrorBlock, withinLimitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, withinLimitBlock);
        LlvmValueHandle stringRef = EmitAllocDynamic(state, LlvmApi.BuildAdd(builder, fileLength, LlvmApi.ConstInt(state.I64, 8, 0), "fs_read_total_bytes"));
        StoreMemory(state, stringRef, 0, fileLength, "fs_read_len");
        LlvmApi.BuildStore(builder, stringRef, stringSlot);
        LlvmApi.BuildStore(builder, fileLength, remainingSlot);
        LlvmApi.BuildStore(builder, GetStringBytesAddress(state, stringRef, "fs_read_cursor_start"), cursorSlot);
        LlvmValueHandle isEmpty = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, fileLength, LlvmApi.ConstInt(state.I64, 0, 0), "fs_read_empty");
        LlvmApi.BuildCondBr(builder, isEmpty, utf8CheckBlock, readCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, readCheckBlock);
        LlvmValueHandle remaining = LlvmApi.BuildLoad2(builder, state.I64, remainingSlot, "fs_read_remaining_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, remaining, LlvmApi.ConstInt(state.I64, 0, 0), "fs_read_done");
        LlvmApi.BuildCondBr(builder, done, utf8CheckBlock, readBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, readBodyBlock);
        LlvmValueHandle cursorAddress = LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, "fs_read_cursor_value");
        LlvmValueHandle readBytes = EmitLinuxSyscall(
            state,
            SyscallRead,
            LlvmApi.BuildLoad2(builder, state.I64, fdSlot, "fs_read_fd_value"),
            cursorAddress,
            remaining,
            "fs_read_read_call");
        LlvmValueHandle readFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, readBytes, LlvmApi.ConstInt(state.I64, 0, 0), "fs_read_failed");
        LlvmApi.BuildCondBr(builder, readFailed, maybeCloseErrorBlock, readDoneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, readDoneBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildSub(builder, remaining, readBytes, "fs_read_remaining_next"), remainingSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, cursorAddress, readBytes, "fs_read_cursor_next"), cursorSlot);
        LlvmApi.BuildBr(builder, readCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, utf8CheckBlock);
        LlvmValueHandle utf8Valid = EmitValidateUtf8(
            state,
            GetStringBytesPointer(state, LlvmApi.BuildLoad2(builder, state.I64, stringSlot, "fs_read_string_value"), "fs_read_utf8_ptr"),
            LoadStringLength(state, LlvmApi.BuildLoad2(builder, state.I64, stringSlot, "fs_read_string_len_value"), "fs_read_utf8_len"),
            "fs_read_utf8");
        LlvmValueHandle isUtf8Valid = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, utf8Valid, LlvmApi.ConstInt(state.I64, 0, 0), "fs_read_is_utf8_valid");
        LlvmApi.BuildCondBr(builder, isUtf8Valid, closeOkBlock, closeInvalidBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeOkBlock);
        EmitLinuxSyscall(
            state,
            SyscallClose,
            LlvmApi.BuildLoad2(builder, state.I64, fdSlot, "fs_read_close_fd"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "fs_read_close_ok_call");
        LlvmApi.BuildStore(builder, EmitResultOk(state, LlvmApi.BuildLoad2(builder, state.I64, stringSlot, "fs_read_ok_value")), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeInvalidBlock);
        EmitLinuxSyscall(
            state,
            SyscallClose,
            LlvmApi.BuildLoad2(builder, state.I64, fdSlot, "fs_read_invalid_fd"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "fs_read_close_invalid_call");
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, FileReadInvalidUtf8Message)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, maybeCloseErrorBlock);
        LlvmValueHandle fdValue = LlvmApi.BuildLoad2(builder, state.I64, fdSlot, "fs_read_error_fd");
        LlvmValueHandle shouldClose = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, fdValue, LlvmApi.ConstInt(state.I64, 0, 0), "fs_read_should_close");
        LlvmApi.BuildCondBr(builder, shouldClose, closeHandleBlock, returnErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeHandleBlock);
        EmitLinuxSyscall(
            state,
            SyscallClose,
            LlvmApi.BuildLoad2(builder, state.I64, fdSlot, "fs_read_close_error_fd"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "fs_read_close_error_call");
        LlvmApi.BuildBr(builder, returnErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, returnErrorBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, FileReadFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeErrorBlock);
        LlvmApi.BuildBr(builder, returnErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "fs_read_result_value");
    }

    private static LlvmValueHandle EmitLinuxFileWriteText(LlvmCodegenState state, LlvmValueHandle pathRef, LlvmValueHandle textRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle pathCstr = EmitStringToCString(state, pathRef, "fs_write_path");
        LlvmValueHandle fdSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_write_fd");
        LlvmValueHandle remainingSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_write_remaining");
        LlvmValueHandle cursorSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_write_cursor");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_write_result");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), fdSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

        var openBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_open");
        var loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_loop_check");
        var loopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_loop_body");
        var advanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_advance");
        var closeOkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_close_ok");
        var maybeCloseErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_maybe_close_error");
        var closeErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_close_error");
        var returnErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_return_error");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_continue");

        LlvmApi.BuildBr(builder, openBlock);

        LlvmApi.PositionBuilderAtEnd(builder, openBlock);
        LlvmValueHandle fd = EmitLinuxSyscall(
            state,
            SyscallOpen,
            LlvmApi.BuildPtrToInt(builder, pathCstr, state.I64, "fs_write_path_ptr"),
            LlvmApi.ConstInt(state.I64, 0x241, 0),
            LlvmApi.ConstInt(state.I64, 420, 0),
            "fs_write_open_call");
        LlvmApi.BuildStore(builder, fd, fdSlot);
        LlvmValueHandle openFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, fd, LlvmApi.ConstInt(state.I64, 0, 0), "fs_write_open_failed");
        LlvmApi.BuildStore(builder, LoadStringLength(state, textRef, "fs_write_text_len"), remainingSlot);
        LlvmApi.BuildStore(builder, GetStringBytesAddress(state, textRef, "fs_write_text_ptr"), cursorSlot);
        LlvmApi.BuildCondBr(builder, openFailed, returnErrorBlock, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle remaining = LlvmApi.BuildLoad2(builder, state.I64, remainingSlot, "fs_write_remaining_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, remaining, LlvmApi.ConstInt(state.I64, 0, 0), "fs_write_done");
        LlvmApi.BuildCondBr(builder, done, closeOkBlock, loopBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBodyBlock);
        LlvmValueHandle cursorAddress = LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, "fs_write_cursor_value");
        LlvmValueHandle bytesWritten = EmitLinuxSyscall(
            state,
            SyscallWrite,
            LlvmApi.BuildLoad2(builder, state.I64, fdSlot, "fs_write_fd_value"),
            cursorAddress,
            remaining,
            "fs_write_write_call");
        LlvmValueHandle writeFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, bytesWritten, LlvmApi.ConstInt(state.I64, 0, 0), "fs_write_failed");
        LlvmApi.BuildCondBr(builder, writeFailed, maybeCloseErrorBlock, advanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, advanceBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildSub(builder, remaining, bytesWritten, "fs_write_remaining_next"), remainingSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, cursorAddress, bytesWritten, "fs_write_cursor_next"), cursorSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeOkBlock);
        EmitLinuxSyscall(
            state,
            SyscallClose,
            LlvmApi.BuildLoad2(builder, state.I64, fdSlot, "fs_write_close_fd"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "fs_write_close_ok_call");
        LlvmApi.BuildStore(builder, EmitResultOk(state, EmitUnitValue(state)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, maybeCloseErrorBlock);
        LlvmValueHandle fdValue = LlvmApi.BuildLoad2(builder, state.I64, fdSlot, "fs_write_error_fd");
        LlvmValueHandle shouldClose = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, fdValue, LlvmApi.ConstInt(state.I64, 0, 0), "fs_write_should_close");
        LlvmApi.BuildCondBr(builder, shouldClose, closeErrorBlock, returnErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeErrorBlock);
        EmitLinuxSyscall(
            state,
            SyscallClose,
            LlvmApi.BuildLoad2(builder, state.I64, fdSlot, "fs_write_close_error_fd"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "fs_write_close_error_call");
        LlvmApi.BuildBr(builder, returnErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, returnErrorBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, FileWriteFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "fs_write_result_value");
    }

    private static LlvmValueHandle EmitLinuxFileExists(LlvmCodegenState state, LlvmValueHandle pathRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle pathCstr = EmitStringToCString(state, pathRef, "fs_exists_path");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_exists_result");
        var openBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_exists_open");
        var foundBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_exists_found");
        var missingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_exists_missing");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_exists_continue");

        LlvmApi.BuildBr(builder, openBlock);

        LlvmApi.PositionBuilderAtEnd(builder, openBlock);
        LlvmValueHandle fd = EmitLinuxSyscall(
            state,
            SyscallOpen,
            LlvmApi.BuildPtrToInt(builder, pathCstr, state.I64, "fs_exists_path_ptr"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "fs_exists_open_call");
        LlvmValueHandle openFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, fd, LlvmApi.ConstInt(state.I64, 0, 0), "fs_exists_open_failed");
        LlvmApi.BuildCondBr(builder, openFailed, missingBlock, foundBlock);

        LlvmApi.PositionBuilderAtEnd(builder, foundBlock);
        EmitLinuxSyscall(
            state,
            SyscallClose,
            fd,
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "fs_exists_close_call");
        LlvmApi.BuildStore(builder, EmitResultOk(state, LlvmApi.ConstInt(state.I64, 1, 0)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, missingBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, LlvmApi.ConstInt(state.I64, 0, 0)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "fs_exists_result_value");
    }

    private static LlvmValueHandle EmitWindowsFileReadText(LlvmCodegenState state, LlvmValueHandle pathRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle pathCstr = EmitStringToCString(state, pathRef, "fs_read_path");
        LlvmValueHandle handleSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_read_handle");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_read_result");
        LlvmValueHandle bytesReadSlot = LlvmApi.BuildAlloca(builder, state.I32, "fs_read_bytes_read");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), handleSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

        var openBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_win_open");
        var readBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_win_read");
        var utf8Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_win_utf8");
        var closeOkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_win_close_ok");
        var closeInvalidBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_win_close_invalid");
        var closeErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_win_close_error");
        var returnErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_win_return_error");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_win_continue");

        LlvmApi.BuildBr(builder, openBlock);

        LlvmApi.PositionBuilderAtEnd(builder, openBlock);
        LlvmValueHandle handle = EmitWindowsCreateFile(
            state,
            pathCstr,
            unchecked((int)0x80000000),
            1,
            3,
            "fs_read_create_file");
        LlvmApi.BuildStore(builder, handle, handleSlot);
        LlvmValueHandle openFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, handle, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), "fs_read_handle_invalid");
        LlvmApi.BuildCondBr(builder, openFailed, returnErrorBlock, readBlock);

        LlvmApi.PositionBuilderAtEnd(builder, readBlock);
        LlvmValueHandle stringRef = EmitAllocDynamic(state, LlvmApi.ConstInt(state.I64, MaxFileReadBytes + 8, 0));
        StoreMemory(state, stringRef, 0, LlvmApi.ConstInt(state.I64, 0, 0), "fs_read_win_len_init");
        LlvmValueHandle readSucceeded = EmitWindowsReadFile(
            state,
            LlvmApi.BuildLoad2(builder, state.I64, handleSlot, "fs_read_handle_value"),
            GetStringBytesPointer(state, stringRef, "fs_read_win_bytes"),
            LlvmApi.ConstInt(state.I32, MaxFileReadBytes, 0),
            bytesReadSlot,
            "fs_read_win_read_call");
        LlvmApi.BuildStore(builder, LlvmApi.BuildZExt(builder, LlvmApi.BuildLoad2(builder, state.I32, bytesReadSlot, "fs_read_bytes_read_value"), state.I64, "fs_read_bytes_i64"), GetMemoryPointer(state, stringRef, 0, "fs_read_win_len_ptr"));
        LlvmApi.BuildCondBr(builder, readSucceeded, utf8Block, closeErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, utf8Block);
        LlvmValueHandle utf8Valid = EmitValidateUtf8(
            state,
            GetStringBytesPointer(state, stringRef, "fs_read_win_utf8_ptr"),
            LoadStringLength(state, stringRef, "fs_read_win_utf8_len"),
            "fs_read_win_utf8");
        LlvmValueHandle isUtf8Valid = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, utf8Valid, LlvmApi.ConstInt(state.I64, 0, 0), "fs_read_win_is_utf8_valid");
        LlvmApi.BuildCondBr(builder, isUtf8Valid, closeOkBlock, closeInvalidBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeOkBlock);
        EmitWindowsCloseHandle(state, LlvmApi.BuildLoad2(builder, state.I64, handleSlot, "fs_read_close_handle"), "fs_read_close_ok");
        LlvmApi.BuildStore(builder, EmitResultOk(state, stringRef), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeInvalidBlock);
        EmitWindowsCloseHandle(state, LlvmApi.BuildLoad2(builder, state.I64, handleSlot, "fs_read_invalid_handle"), "fs_read_close_invalid");
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, FileReadInvalidUtf8Message)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeErrorBlock);
        EmitWindowsCloseHandle(state, LlvmApi.BuildLoad2(builder, state.I64, handleSlot, "fs_read_error_handle"), "fs_read_close_error");
        LlvmApi.BuildBr(builder, returnErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, returnErrorBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, FileReadFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "fs_read_win_result_value");
    }

    private static LlvmValueHandle EmitWindowsFileWriteText(LlvmCodegenState state, LlvmValueHandle pathRef, LlvmValueHandle textRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle pathCstr = EmitStringToCString(state, pathRef, "fs_write_path");
        LlvmValueHandle handleSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_write_handle");
        LlvmValueHandle remainingSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_write_remaining");
        LlvmValueHandle cursorSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_write_cursor");
        LlvmValueHandle bytesWrittenSlot = LlvmApi.BuildAlloca(builder, state.I32, "fs_write_bytes_written");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_write_result");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), handleSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

        var openBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_win_open");
        var loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_win_loop_check");
        var loopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_win_loop_body");
        var advanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_win_advance");
        var closeOkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_win_close_ok");
        var closeErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_win_close_error");
        var returnErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_win_return_error");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_win_continue");

        LlvmApi.BuildBr(builder, openBlock);

        LlvmApi.PositionBuilderAtEnd(builder, openBlock);
        LlvmValueHandle handle = EmitWindowsCreateFile(
            state,
            pathCstr,
            0x40000000,
            0,
            2,
            "fs_write_create_file");
        LlvmApi.BuildStore(builder, handle, handleSlot);
        LlvmApi.BuildStore(builder, LoadStringLength(state, textRef, "fs_write_win_text_len"), remainingSlot);
        LlvmApi.BuildStore(builder, GetStringBytesAddress(state, textRef, "fs_write_win_text_ptr"), cursorSlot);
        LlvmValueHandle openFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, handle, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), "fs_write_handle_invalid");
        LlvmApi.BuildCondBr(builder, openFailed, returnErrorBlock, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle remaining = LlvmApi.BuildLoad2(builder, state.I64, remainingSlot, "fs_write_win_remaining_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, remaining, LlvmApi.ConstInt(state.I64, 0, 0), "fs_write_win_done");
        LlvmApi.BuildCondBr(builder, done, closeOkBlock, loopBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBodyBlock);
        LlvmValueHandle chunkSize = LlvmApi.BuildSelect(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, remaining, LlvmApi.ConstInt(state.I64, uint.MaxValue, 0), "fs_write_win_chunk_gt"),
            LlvmApi.ConstInt(state.I64, uint.MaxValue, 0),
            remaining,
            "fs_write_win_chunk_size");
        LlvmValueHandle wrote = EmitWindowsWriteFile(
            state,
            LlvmApi.BuildLoad2(builder, state.I64, handleSlot, "fs_write_handle_value"),
            LlvmApi.BuildIntToPtr(builder, LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, "fs_write_cursor_value"), state.I8Ptr, "fs_write_cursor_ptr"),
            LlvmApi.BuildTrunc(builder, chunkSize, state.I32, "fs_write_chunk_i32"),
            bytesWrittenSlot,
            "fs_write_win_write_call");
        LlvmApi.BuildCondBr(builder, wrote, advanceBlock, closeErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, advanceBlock);
        LlvmValueHandle bytesWritten = LlvmApi.BuildZExt(builder, LlvmApi.BuildLoad2(builder, state.I32, bytesWrittenSlot, "fs_write_bytes_written_value"), state.I64, "fs_write_bytes_written_i64");
        LlvmValueHandle wroteZero = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, bytesWritten, LlvmApi.ConstInt(state.I64, 0, 0), "fs_write_wrote_zero");
        var zeroWriteBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_win_zero");
        var updateBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_win_update");
        LlvmApi.BuildCondBr(builder, wroteZero, zeroWriteBlock, updateBlock);

        LlvmApi.PositionBuilderAtEnd(builder, zeroWriteBlock);
        LlvmApi.BuildBr(builder, closeErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, updateBlock);
        LlvmValueHandle cursorValue = LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, "fs_write_cursor_current");
        LlvmApi.BuildStore(builder, LlvmApi.BuildSub(builder, remaining, bytesWritten, "fs_write_remaining_next"), remainingSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, cursorValue, bytesWritten, "fs_write_cursor_next"), cursorSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeOkBlock);
        EmitWindowsCloseHandle(state, LlvmApi.BuildLoad2(builder, state.I64, handleSlot, "fs_write_close_handle"), "fs_write_close_ok");
        LlvmApi.BuildStore(builder, EmitResultOk(state, EmitUnitValue(state)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeErrorBlock);
        EmitWindowsCloseHandle(state, LlvmApi.BuildLoad2(builder, state.I64, handleSlot, "fs_write_error_handle"), "fs_write_close_error");
        LlvmApi.BuildBr(builder, returnErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, returnErrorBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, FileWriteFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "fs_write_win_result_value");
    }

    private static LlvmValueHandle EmitWindowsFileExists(LlvmCodegenState state, LlvmValueHandle pathRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle pathCstr = EmitStringToCString(state, pathRef, "fs_exists_path");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_exists_win_result");
        var checkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_exists_win_check");
        var missingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_exists_win_missing");
        var foundBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_exists_win_found");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_exists_win_continue");

        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkBlock);
        LlvmValueHandle attrs = EmitWindowsGetFileAttributes(state, pathCstr, "fs_exists_get_attrs");
        LlvmValueHandle missing = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, attrs, LlvmApi.ConstInt(state.I32, uint.MaxValue, 0), "fs_exists_missing");
        LlvmApi.BuildCondBr(builder, missing, missingBlock, foundBlock);

        LlvmApi.PositionBuilderAtEnd(builder, foundBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, LlvmApi.ConstInt(state.I64, 1, 0)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, missingBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, LlvmApi.ConstInt(state.I64, 0, 0)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "fs_exists_win_result_value");
    }

    private static void EmitNetworkingRuntimeAbi(
        LlvmTargetContext target,
        LlvmCodegenFlavor flavor,
        LlvmTypeHandle i32,
        LlvmTypeHandle i32Ptr,
        LlvmValueHandle heapCursorGlobal,
        LlvmValueHandle heapEndGlobal,
        LlvmValueHandle windowsGetStdHandleImport,
        LlvmValueHandle windowsWriteFileImport,
        LlvmValueHandle windowsReadFileImport,
        LlvmValueHandle windowsCreateFileImport,
        LlvmValueHandle windowsCloseHandleImport,
        LlvmValueHandle windowsGetFileAttributesImport,
        LlvmValueHandle windowsWsaStartupImport,
        LlvmValueHandle windowsSocketImport,
        LlvmValueHandle windowsConnectImport,
        LlvmValueHandle windowsSendImport,
        LlvmValueHandle windowsRecvImport,
        LlvmValueHandle windowsCloseSocketImport,
        LlvmValueHandle windowsIoctlSocketImport,
        LlvmValueHandle windowsWsaGetLastErrorImport,
        LlvmValueHandle windowsWsaPollImport,
        LlvmValueHandle windowsLoadLibraryImport,
        LlvmValueHandle windowsGetProcAddressImport,
        LlvmValueHandle windowsCertOpenSystemStoreImport,
        LlvmValueHandle windowsCertEnumCertificatesInStoreImport,
        LlvmValueHandle windowsCertCloseStoreImport,
        LlvmValueHandle windowsBindImport,
        LlvmValueHandle windowsSetSockOptImport,
        LlvmValueHandle windowsWsaIoctlImport,
        LlvmValueHandle windowsWsaSendImport,
        LlvmValueHandle windowsWsaRecvImport,
        LlvmValueHandle windowsCreateIoCompletionPortImport,
        LlvmValueHandle windowsGetQueuedCompletionStatusImport,
        LlvmValueHandle windowsIocpPortGlobal,
        LlvmValueHandle windowsExitProcessImport,
        LlvmValueHandle windowsGetCommandLineImport,
        LlvmValueHandle windowsWideCharToMultiByteImport,
        LlvmValueHandle windowsLocalFreeImport,
        LlvmValueHandle windowsCommandLineToArgvImport,
        LlvmValueHandle windowsSleepImport,
        LlvmValueHandle windowsVirtualAllocImport,
        LlvmValueHandle windowsVirtualFreeImport,
        LlvmAttributeHandle nounwindAttr)
    {
        LlvmTypeHandle i64 = LlvmApi.Int64TypeInContext(target.Context);
        LlvmTypeHandle i8 = LlvmApi.Int8TypeInContext(target.Context);
        LlvmTypeHandle f64 = LlvmApi.DoubleTypeInContext(target.Context);
        LlvmTypeHandle i8Ptr = LlvmApi.PointerTypeInContext(target.Context, 0);
        LlvmTypeHandle i64Ptr = LlvmApi.PointerTypeInContext(target.Context, 0);
        LinuxTlsGlobals linuxTlsGlobals = IsLinuxFlavor(flavor) || flavor == LlvmCodegenFlavor.WindowsX64
            ? new LinuxTlsGlobals(
                CreateInternalI64Global("__ashes_tls_init_status"),
                CreateInternalI64Global("__ashes_tls_ctx"),
                CreateInternalI64Global("__ashes_tls_libssl_handle"),
                CreateInternalI64Global("__ashes_tls_libcrypto_handle"))
            : default;

        DeclareRuntimeFunction("ashes_tcp_connect", LlvmApi.FunctionType(i64, [i64, i64]));
        DeclareRuntimeFunction("ashes_tcp_send", LlvmApi.FunctionType(i64, [i64, i64]));
        DeclareRuntimeFunction("ashes_tcp_receive", LlvmApi.FunctionType(i64, [i64, i64]));
        DeclareRuntimeFunction("ashes_tcp_close", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_http_get", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_http_post", LlvmApi.FunctionType(i64, [i64, i64]));
        DeclareRuntimeFunction("ashes_tls_runtime_init", LlvmApi.FunctionType(i64, []));
        DeclareRuntimeFunction("ashes_step_tcp_connect_task", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_step_tcp_send_task", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_step_tcp_receive_task", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_step_tcp_close_task", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_step_tls_connect_task", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_step_tls_handshake_task", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_step_tls_send_task", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_step_tls_receive_task", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_step_tls_close_task", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_step_task_until_wait_or_done", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_wait_pending_task_list", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_step_http_get_task", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_step_http_post_task", LlvmApi.FunctionType(i64, [i64]));

        EmitRuntimeFunction(
            "ashes_tcp_connect",
            LlvmApi.FunctionType(i64, [i64, i64]),
            (state, fn) => EmitTcpConnect(state, LlvmApi.GetParam(fn, 0), LlvmApi.GetParam(fn, 1)));

        EmitRuntimeFunction(
            "ashes_tcp_send",
            LlvmApi.FunctionType(i64, [i64, i64]),
            (state, fn) => EmitTcpSend(state, LlvmApi.GetParam(fn, 0), LlvmApi.GetParam(fn, 1)));

        EmitRuntimeFunction(
            "ashes_tcp_receive",
            LlvmApi.FunctionType(i64, [i64, i64]),
            (state, fn) => EmitTcpReceive(state, LlvmApi.GetParam(fn, 0), LlvmApi.GetParam(fn, 1)));

        EmitRuntimeFunction(
            "ashes_tcp_close",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => EmitTcpClose(state, LlvmApi.GetParam(fn, 0)));

        EmitRuntimeFunction(
            "ashes_http_get",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => EmitHttpRequest(state, LlvmApi.GetParam(fn, 0), LlvmApi.ConstInt(state.I64, 0, 0), hasBody: false));

        EmitRuntimeFunction(
            "ashes_http_post",
            LlvmApi.FunctionType(i64, [i64, i64]),
            (state, fn) => EmitHttpRequest(state, LlvmApi.GetParam(fn, 0), LlvmApi.GetParam(fn, 1), hasBody: true));

        EmitRuntimeFunction(
            "ashes_tls_runtime_init",
            LlvmApi.FunctionType(i64, []),
            (state, _) => IsLinuxFlavor(state.Flavor)
                ? EmitEnsureLinuxTlsRuntimeInitialized(state, linuxTlsGlobals, "tls_runtime_init")
                : state.Flavor == LlvmCodegenFlavor.WindowsX64
                    ? EmitEnsureWindowsTlsRuntimeInitialized(state, linuxTlsGlobals, "tls_runtime_init")
                : LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1));

        EmitRuntimeFunction(
            "ashes_step_tcp_connect_task",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => EmitStepTcpConnectTask(state, LlvmApi.GetParam(fn, 0)));

        EmitRuntimeFunction(
            "ashes_step_tcp_send_task",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => EmitStepTcpSendTask(state, LlvmApi.GetParam(fn, 0)));

        EmitRuntimeFunction(
            "ashes_step_tcp_receive_task",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => EmitStepTcpReceiveTask(state, LlvmApi.GetParam(fn, 0)));

        EmitRuntimeFunction(
            "ashes_step_tcp_close_task",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => EmitStepTcpCloseTask(state, LlvmApi.GetParam(fn, 0)));

        EmitRuntimeFunction(
            "ashes_step_tls_connect_task",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => EmitStepTlsConnectTask(state, LlvmApi.GetParam(fn, 0)));

        EmitRuntimeFunction(
            "ashes_step_tls_handshake_task",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => IsLinuxFlavor(state.Flavor) || state.Flavor == LlvmCodegenFlavor.WindowsX64
                ? EmitStepTlsHandshakeTask(state, LlvmApi.GetParam(fn, 0), linuxTlsGlobals)
                : EmitStepTlsTodoTask(state, LlvmApi.GetParam(fn, 0), "Ashes TLS handshake runtime is not implemented yet", "step_tls_handshake_todo"));

        EmitRuntimeFunction(
            "ashes_step_tls_send_task",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => IsLinuxFlavor(state.Flavor) || state.Flavor == LlvmCodegenFlavor.WindowsX64
                ? EmitStepTlsSendTask(state, LlvmApi.GetParam(fn, 0), linuxTlsGlobals)
                : EmitStepTlsTodoTask(state, LlvmApi.GetParam(fn, 0), "Ashes TLS send runtime is not implemented yet", "step_tls_send_todo"));

        EmitRuntimeFunction(
            "ashes_step_tls_receive_task",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => IsLinuxFlavor(state.Flavor) || state.Flavor == LlvmCodegenFlavor.WindowsX64
                ? EmitStepTlsReceiveTask(state, LlvmApi.GetParam(fn, 0), linuxTlsGlobals)
                : EmitStepTlsTodoTask(state, LlvmApi.GetParam(fn, 0), "Ashes TLS receive runtime is not implemented yet", "step_tls_receive_todo"));

        EmitRuntimeFunction(
            "ashes_step_tls_close_task",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => IsLinuxFlavor(state.Flavor) || state.Flavor == LlvmCodegenFlavor.WindowsX64
                ? EmitStepTlsCloseTask(state, LlvmApi.GetParam(fn, 0), linuxTlsGlobals)
                : EmitStepTlsTodoTask(state, LlvmApi.GetParam(fn, 0), "Ashes TLS close runtime is not implemented yet", "step_tls_close_todo"));

        EmitRuntimeFunction(
            "ashes_step_task_until_wait_or_done",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => EmitStepTaskUntilPendingOrDone(state, LlvmApi.GetParam(fn, 0), "runtime_step_task"));

        EmitRuntimeFunction(
            "ashes_wait_pending_task_list",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => EmitWaitForPendingTaskList(state, LlvmApi.GetParam(fn, 0), "runtime_wait_tasks"));

        EmitRuntimeFunction(
            "ashes_step_http_get_task",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => EmitStepHttpGetTask(state, LlvmApi.GetParam(fn, 0)));

        EmitRuntimeFunction(
            "ashes_step_http_post_task",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => EmitStepHttpPostTask(state, LlvmApi.GetParam(fn, 0)));

        LlvmValueHandle CreateInternalI64Global(string symbolName)
        {
            LlvmValueHandle global = LlvmApi.AddGlobal(target.Module, i64, symbolName);
            LlvmApi.SetLinkage(global, LlvmLinkage.Internal);
            LlvmApi.SetInitializer(global, LlvmApi.ConstInt(i64, 0, 0));
            return global;
        }

        void EmitRuntimeFunction(string symbolName, LlvmTypeHandle functionType, Func<LlvmCodegenState, LlvmValueHandle, LlvmValueHandle> emitBody)
        {
            LlvmValueHandle function = LlvmApi.GetNamedFunction(target.Module, symbolName);
            if (function.Ptr == 0)
            {
                function = LlvmApi.AddFunction(target.Module, symbolName, functionType);
            }
            LlvmApi.SetLinkage(function, LlvmLinkage.External);
            LlvmApi.AddAttributeAtIndex(function, LlvmApi.AttributeIndexFunction, nounwindAttr);

            LlvmBasicBlockHandle entryBlock = LlvmApi.AppendBasicBlockInContext(target.Context, function, "entry");
            LlvmApi.PositionBuilderAtEnd(target.Builder, entryBlock);

            LlvmValueHandle programArgsSlot = LlvmApi.BuildAlloca(target.Builder, i64, symbolName + "_program_args");
            LlvmApi.BuildStore(target.Builder, LlvmApi.ConstInt(i64, 0, 0), programArgsSlot);

            var runtimeState = new LlvmCodegenState(
                target,
                function,
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, LlvmValueHandle>(StringComparer.Ordinal),
                programArgsSlot,
                Array.Empty<LlvmValueHandle>(),
                Array.Empty<LlvmValueHandle>(),
                heapCursorGlobal,
                heapEndGlobal,
                new Dictionary<string, LlvmBasicBlockHandle>(StringComparer.Ordinal),
                new Dictionary<int, LlvmBasicBlockHandle>(),
                i64,
                i32,
                i8,
                f64,
                i8Ptr,
                i32Ptr,
                i64Ptr,
                default,
                windowsGetStdHandleImport,
                windowsWriteFileImport,
                windowsReadFileImport,
                windowsCreateFileImport,
                windowsCloseHandleImport,
                windowsGetFileAttributesImport,
                windowsWsaStartupImport,
                windowsSocketImport,
                windowsConnectImport,
                windowsSendImport,
                windowsRecvImport,
                windowsCloseSocketImport,
                windowsIoctlSocketImport,
                windowsWsaGetLastErrorImport,
                windowsWsaPollImport,
                windowsLoadLibraryImport,
                windowsGetProcAddressImport,
                windowsCertOpenSystemStoreImport,
                windowsCertEnumCertificatesInStoreImport,
                windowsCertCloseStoreImport,
                windowsBindImport,
                windowsSetSockOptImport,
                windowsWsaIoctlImport,
                windowsWsaSendImport,
                windowsWsaRecvImport,
                windowsCreateIoCompletionPortImport,
                windowsGetQueuedCompletionStatusImport,
                windowsIocpPortGlobal,
                windowsExitProcessImport,
                windowsGetCommandLineImport,
                windowsWideCharToMultiByteImport,
                windowsLocalFreeImport,
                windowsCommandLineToArgvImport,
                windowsSleepImport,
                windowsVirtualAllocImport,
                windowsVirtualFreeImport,
                flavor,
                false,
                false);

            LlvmApi.BuildRet(target.Builder, NormalizeToI64(runtimeState, emitBody(runtimeState, function)));
        }

        void DeclareRuntimeFunction(string symbolName, LlvmTypeHandle functionType)
        {
            if (LlvmApi.GetNamedFunction(target.Module, symbolName).Ptr == 0)
            {
                LlvmApi.AddFunction(target.Module, symbolName, functionType);
            }
        }
    }

    private static LlvmValueHandle EmitHttpGetAbiCall(LlvmCodegenState state, LlvmValueHandle urlRef)
        => EmitNetworkingRuntimeCall(state, "ashes_http_get", [urlRef], "http_get_abi");

    private static LlvmValueHandle EmitHttpPostAbiCall(LlvmCodegenState state, LlvmValueHandle urlRef, LlvmValueHandle bodyRef)
        => EmitNetworkingRuntimeCall(state, "ashes_http_post", [urlRef, bodyRef], "http_post_abi");

    private static LlvmValueHandle EmitTcpConnectAbiCall(LlvmCodegenState state, LlvmValueHandle hostRef, LlvmValueHandle port)
        => EmitNetworkingRuntimeCall(state, "ashes_tcp_connect", [hostRef, port], "tcp_connect_abi");

    private static LlvmValueHandle EmitTcpSendAbiCall(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle textRef)
        => EmitNetworkingRuntimeCall(state, "ashes_tcp_send", [socket, textRef], "tcp_send_abi");

    private static LlvmValueHandle EmitTcpReceiveAbiCall(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle maxBytes)
        => EmitNetworkingRuntimeCall(state, "ashes_tcp_receive", [socket, maxBytes], "tcp_receive_abi");

    private static LlvmValueHandle EmitTcpCloseAbiCall(LlvmCodegenState state, LlvmValueHandle socket)
        => EmitNetworkingRuntimeCall(state, "ashes_tcp_close", [socket], "tcp_close_abi");

    private static LlvmValueHandle EmitTlsCloseAbiCall(LlvmCodegenState state, LlvmValueHandle session)
        => EmitRunTask(
            state,
            EmitCreateLeafNetworkingTask(
                state,
                TaskStructLayout.StateTlsClose,
                session,
                LlvmApi.ConstInt(state.I64, 0, 0),
                "tls_close_abi_task"));

    private static LlvmValueHandle EmitNetworkingRuntimeCall(LlvmCodegenState state, string symbolName, ReadOnlySpan<LlvmValueHandle> args, string name)
    {
        LlvmValueHandle function = LlvmApi.GetNamedFunction(state.Target.Module, symbolName);
        if (function.Ptr == 0)
        {
            throw new InvalidOperationException($"Missing networking runtime symbol '{symbolName}'.");
        }

        var parameterTypes = new LlvmTypeHandle[args.Length];
        Array.Fill(parameterTypes, state.I64);
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I64, parameterTypes);
        return LlvmApi.BuildCall2(state.Target.Builder, functionType, function, args, name);
    }

    private static LlvmValueHandle EmitOrDeclareExternalFunction(LlvmCodegenState state, string symbolName, LlvmTypeHandle functionType)
    {
        LlvmValueHandle function = LlvmApi.GetNamedFunction(state.Target.Module, symbolName);
        if (function.Ptr == 0)
        {
            function = LlvmApi.AddFunction(state.Target.Module, symbolName, functionType);
            LlvmApi.SetLinkage(function, LlvmLinkage.External);
        }

        return function;
    }

    private static LlvmValueHandle EmitLinuxImportedCall(LlvmCodegenState state, string symbolName, LlvmTypeHandle functionType, ReadOnlySpan<LlvmValueHandle> args, string name)
    {
        LlvmValueHandle function = EmitOrDeclareExternalFunction(state, symbolName, functionType);
        return LlvmApi.BuildCall2(state.Target.Builder, functionType, function, args, name);
    }

    private static LlvmValueHandle EmitLinuxDlopen(LlvmCodegenState state, LlvmValueHandle pathCstr, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I8Ptr, [state.I8Ptr, state.I32]);
        LlvmValueHandle handlePtr = EmitLinuxImportedCall(
            state,
            "dlopen",
            functionType,
            [pathCstr, LlvmApi.ConstInt(state.I32, LinuxRtldNow, 0)],
            name);
        return LlvmApi.BuildPtrToInt(builder, handlePtr, state.I64, name + "_i64");
    }

    private static LlvmValueHandle EmitLinuxDlsym(LlvmCodegenState state, LlvmValueHandle libraryHandle, string symbolName, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I8Ptr, [state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle libraryPtr = LlvmApi.BuildIntToPtr(builder, libraryHandle, state.I8Ptr, name + "_library_ptr");
        LlvmValueHandle symbolCstr = EmitStringToCString(state, EmitHeapStringLiteral(state, symbolName), name + "_symbol_name");
        LlvmValueHandle symbolPtr = EmitLinuxImportedCall(state, "dlsym", functionType, [libraryPtr, symbolCstr], name + "_call");
        return LlvmApi.BuildPtrToInt(builder, symbolPtr, state.I64, name + "_i64");
    }

    private static LlvmValueHandle EmitLinuxStrlen(LlvmCodegenState state, LlvmValueHandle cstrPtr, string name)
    {
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I64, [state.I8Ptr]);
        return EmitLinuxImportedCall(state, "strlen", functionType, [cstrPtr], name);
    }

    private static LlvmValueHandle EmitWindowsLoadLibraryWithFallback(LlvmCodegenState state, string primaryName, string fallbackName, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle librarySlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_library_slot");
        LlvmBasicBlockHandle fallbackBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_fallback");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");

        LlvmValueHandle primaryHandle = EmitWindowsLoadLibrary(state,
            EmitStringToCString(state, EmitHeapStringLiteral(state, primaryName), prefix + "_primary_name"),
            prefix + "_primary");
        LlvmApi.BuildStore(builder, primaryHandle, librarySlot);
        LlvmValueHandle hasPrimary = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, primaryHandle, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_primary");
        LlvmApi.BuildCondBr(builder, hasPrimary, doneBlock, fallbackBlock);

        LlvmApi.PositionBuilderAtEnd(builder, fallbackBlock);
        LlvmValueHandle fallbackHandle = EmitWindowsLoadLibrary(state,
            EmitStringToCString(state, EmitHeapStringLiteral(state, fallbackName), prefix + "_fallback_name"),
            prefix + "_fallback");
        LlvmApi.BuildStore(builder, fallbackHandle, librarySlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, librarySlot, prefix + "_library");
    }

    private static LlvmValueHandle EmitTlsResolveSymbol(LlvmCodegenState state, LlvmValueHandle libraryHandle, string symbolName, string name)
    {
        return state.Flavor == LlvmCodegenFlavor.WindowsX64
            ? EmitWindowsGetProcAddress(state,
                libraryHandle,
                EmitStringToCString(state, EmitHeapStringLiteral(state, symbolName), name + "_symbol_name"),
                name + "_getproc")
            : EmitLinuxDlsym(state, libraryHandle, symbolName, name + "_dlsym");
    }

    private static LlvmValueHandle EmitLoadI32AtOffset(LlvmCodegenState state, LlvmValueHandle baseAddress, int offset, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle basePtr = LlvmApi.BuildIntToPtr(builder, baseAddress, state.I8Ptr, name + "_base_ptr");
        LlvmValueHandle bytePtr = LlvmApi.BuildGEP2(builder, state.I8, basePtr, [LlvmApi.ConstInt(state.I64, unchecked((ulong)offset), 0)], name + "_byte_ptr");
        LlvmValueHandle i32Ptr = LlvmApi.BuildBitCast(builder, bytePtr, state.I32Ptr, name + "_i32_ptr");
        return LlvmApi.BuildLoad2(builder, state.I32, i32Ptr, name);
    }

    private static LlvmValueHandle EmitCStringToHeapString(LlvmCodegenState state, LlvmValueHandle cstrPtr, string prefix)
    {
        LlvmValueHandle len = EmitLinuxStrlen(state, cstrPtr, prefix + "_strlen");
        return EmitHeapStringSliceFromBytesPointer(state, cstrPtr, len, prefix + "_string");
    }

    private static LlvmValueHandle EmitEnsureLinuxTlsRuntimeInitialized(LlvmCodegenState state, LinuxTlsGlobals globals, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmBasicBlockHandle initBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_init");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");
        LlvmBasicBlockHandle afterLibsslBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_after_libssl");
        LlvmBasicBlockHandle afterLibcryptoBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_after_libcrypto");
        LlvmBasicBlockHandle failMissingLibraryBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_fail_missing_library");
        LlvmBasicBlockHandle failMissingSymbolBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_fail_missing_symbol");
        LlvmBasicBlockHandle failInitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_fail_init");

        LlvmValueHandle currentStatus = LlvmApi.BuildLoad2(builder, state.I64, globals.InitStatusGlobal, prefix + "_current_status");
        LlvmValueHandle needsInit = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, currentStatus, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_needs_init");
        LlvmApi.BuildCondBr(builder, needsInit, initBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, initBlock);
        LlvmValueHandle libsslHandle = EmitLinuxDlopen(state, EmitStringToCString(state, EmitHeapStringLiteral(state, "libssl.so.3"), prefix + "_libssl_name"), prefix + "_dlopen_libssl");
        LlvmValueHandle hasLibssl = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, libsslHandle, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_libssl");
        LlvmApi.BuildStore(builder, libsslHandle, globals.LibsslHandleGlobal);
        LlvmApi.BuildCondBr(builder, hasLibssl, afterLibsslBlock, failMissingLibraryBlock);

        LlvmApi.PositionBuilderAtEnd(builder, afterLibsslBlock);
        LlvmValueHandle libcryptoHandle = EmitLinuxDlopen(state, EmitStringToCString(state, EmitHeapStringLiteral(state, "libcrypto.so.3"), prefix + "_libcrypto_name"), prefix + "_dlopen_libcrypto");
        LlvmValueHandle hasLibcrypto = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, libcryptoHandle, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_libcrypto");
        LlvmApi.BuildStore(builder, libcryptoHandle, globals.LibcryptoHandleGlobal);
        LlvmApi.BuildCondBr(builder, hasLibcrypto, afterLibcryptoBlock, failMissingLibraryBlock);

        LlvmApi.PositionBuilderAtEnd(builder, afterLibcryptoBlock);
        LlvmValueHandle openSslInitFn = EmitLinuxDlsym(state, libsslHandle, "OPENSSL_init_ssl", prefix + "_resolve_init_ssl");
        LlvmValueHandle tlsClientMethodFn = EmitLinuxDlsym(state, libsslHandle, "TLS_client_method", prefix + "_resolve_tls_client_method");
        LlvmValueHandle sslCtxNewFn = EmitLinuxDlsym(state, libsslHandle, "SSL_CTX_new", prefix + "_resolve_ctx_new");
        LlvmValueHandle sslCtxSetVerifyFn = EmitLinuxDlsym(state, libsslHandle, "SSL_CTX_set_verify", prefix + "_resolve_ctx_set_verify");
        LlvmValueHandle sslCtxSetDefaultVerifyPathsFn = EmitLinuxDlsym(state, libsslHandle, "SSL_CTX_set_default_verify_paths", prefix + "_resolve_ctx_set_default_verify_paths");
        LlvmValueHandle haveAllSymbols = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, openSslInitFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_init_ssl"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, tlsClientMethodFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_tls_client_method"),
            prefix + "_have_first_symbols");
        haveAllSymbols = LlvmApi.BuildAnd(builder,
            haveAllSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, sslCtxNewFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_ctx_new"),
            prefix + "_have_second_symbols");
        haveAllSymbols = LlvmApi.BuildAnd(builder,
            haveAllSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, sslCtxSetVerifyFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_ctx_set_verify"),
            prefix + "_have_third_symbols");
        haveAllSymbols = LlvmApi.BuildAnd(builder,
            haveAllSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, sslCtxSetDefaultVerifyPathsFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_ctx_set_default_verify_paths"),
            prefix + "_have_all_symbols");
        LlvmBasicBlockHandle initializeCtxBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_initialize_ctx");
        LlvmApi.BuildCondBr(builder, haveAllSymbols, initializeCtxBlock, failMissingSymbolBlock);

        LlvmApi.PositionBuilderAtEnd(builder, initializeCtxBlock);
        LlvmTypeHandle initSslType = LlvmApi.FunctionType(state.I32, [state.I64, state.I8Ptr]);
        LlvmValueHandle initSslStatus = LlvmApi.BuildCall2(builder,
            initSslType,
            LlvmApi.BuildIntToPtr(builder, openSslInitFn, LlvmApi.PointerTypeInContext(state.Target.Context, 0), prefix + "_init_ssl_ptr"),
            [LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstNull(state.I8Ptr)],
            prefix + "_init_ssl_call");
        LlvmValueHandle initSslOk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildZExt(builder, initSslStatus, state.I64, prefix + "_init_ssl_i64"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_init_ssl_ok");
        LlvmBasicBlockHandle createContextBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create_ctx");
        LlvmApi.BuildCondBr(builder, initSslOk, createContextBlock, failInitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createContextBlock);
        LlvmTypeHandle tlsClientMethodType = LlvmApi.FunctionType(state.I8Ptr, []);
        LlvmValueHandle tlsMethod = LlvmApi.BuildCall2(builder,
            tlsClientMethodType,
            LlvmApi.BuildIntToPtr(builder, tlsClientMethodFn, LlvmApi.PointerTypeInContext(state.Target.Context, 0), prefix + "_tls_client_method_ptr"),
            Array.Empty<LlvmValueHandle>(),
            prefix + "_tls_client_method_call");
        LlvmValueHandle haveTlsMethod = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildPtrToInt(builder, tlsMethod, state.I64, prefix + "_tls_method_i64"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_tls_method");
        LlvmBasicBlockHandle configureContextBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_configure_ctx");
        LlvmApi.BuildCondBr(builder, haveTlsMethod, configureContextBlock, failInitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, configureContextBlock);
        LlvmTypeHandle sslCtxNewType = LlvmApi.FunctionType(state.I8Ptr, [state.I8Ptr]);
        LlvmValueHandle sslCtx = LlvmApi.BuildCall2(builder,
            sslCtxNewType,
            LlvmApi.BuildIntToPtr(builder, sslCtxNewFn, LlvmApi.PointerTypeInContext(state.Target.Context, 0), prefix + "_ctx_new_ptr"),
            [tlsMethod],
            prefix + "_ctx_new_call");
        LlvmValueHandle haveCtx = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildPtrToInt(builder, sslCtx, state.I64, prefix + "_ctx_i64"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_ctx");
        LlvmBasicBlockHandle configureVerifyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_configure_verify");
        LlvmApi.BuildCondBr(builder, haveCtx, configureVerifyBlock, failInitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, configureVerifyBlock);
        LlvmTypeHandle sslCtxSetVerifyType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr, state.I32, state.I8Ptr]);
        LlvmApi.BuildCall2(builder,
            sslCtxSetVerifyType,
            LlvmApi.BuildIntToPtr(builder, sslCtxSetVerifyFn, LlvmApi.PointerTypeInContext(state.Target.Context, 0), prefix + "_ctx_set_verify_ptr"),
            [sslCtx, LlvmApi.ConstInt(state.I32, TlsVerifyPeer, 0), LlvmApi.ConstNull(state.I8Ptr)],
            string.Empty);
        LlvmTypeHandle sslCtxSetDefaultVerifyPathsType = LlvmApi.FunctionType(state.I32, [state.I8Ptr]);
        LlvmValueHandle defaultVerifyPathsStatus = LlvmApi.BuildCall2(builder,
            sslCtxSetDefaultVerifyPathsType,
            LlvmApi.BuildIntToPtr(builder, sslCtxSetDefaultVerifyPathsFn, LlvmApi.PointerTypeInContext(state.Target.Context, 0), prefix + "_ctx_set_default_verify_paths_ptr"),
            [sslCtx],
            prefix + "_ctx_set_default_verify_paths_call");
        LlvmValueHandle verifyPathsOk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildZExt(builder, defaultVerifyPathsStatus, state.I64, prefix + "_verify_paths_i64"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_verify_paths_ok");
        LlvmBasicBlockHandle successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_success");
        LlvmApi.BuildCondBr(builder, verifyPathsOk, successBlock, failInitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildPtrToInt(builder, sslCtx, state.I64, prefix + "_ctx_store_value"), globals.ContextGlobal);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), globals.InitStatusGlobal);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failMissingLibraryBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), globals.InitStatusGlobal);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failMissingSymbolBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-2L)), 1), globals.InitStatusGlobal);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failInitBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-3L)), 1), globals.InitStatusGlobal);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, globals.InitStatusGlobal, prefix + "_status");
    }

    private static LlvmValueHandle EmitPopulateWindowsTlsTrustStore(LlvmCodegenState state, LlvmValueHandle sslCtx, LlvmValueHandle libsslHandle, LlvmValueHandle libcryptoHandle, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle rootStoreSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_root_store_slot");
        LlvmValueHandle sslStoreSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_ssl_store_slot");
        LlvmValueHandle previousContextSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_previous_context_slot");
        LlvmValueHandle importedAnySlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_imported_any_slot");
        LlvmValueHandle encodedBytesSlot = LlvmApi.BuildAlloca(builder, state.I8Ptr, prefix + "_encoded_bytes_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), rootStoreSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), sslStoreSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), previousContextSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), importedAnySlot);

        LlvmValueHandle sslCtxGetCertStoreFn = EmitTlsResolveSymbol(state, libsslHandle, "SSL_CTX_get_cert_store", prefix + "_resolve_get_cert_store");
        LlvmValueHandle d2iX509Fn = EmitTlsResolveSymbol(state, libcryptoHandle, "d2i_X509", prefix + "_resolve_d2i_x509");
        LlvmValueHandle x509StoreAddCertFn = EmitTlsResolveSymbol(state, libcryptoHandle, "X509_STORE_add_cert", prefix + "_resolve_store_add_cert");
        LlvmValueHandle x509FreeFn = EmitTlsResolveSymbol(state, libcryptoHandle, "X509_free", prefix + "_resolve_x509_free");
        LlvmValueHandle haveSymbols = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, sslCtxGetCertStoreFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_get_cert_store"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, d2iX509Fn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_d2i_x509"),
            prefix + "_have_first_symbols");
        haveSymbols = LlvmApi.BuildAnd(builder,
            haveSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, x509StoreAddCertFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_store_add_cert"),
            prefix + "_have_second_symbols");
        haveSymbols = LlvmApi.BuildAnd(builder,
            haveSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, x509FreeFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_x509_free"),
            prefix + "_have_all_symbols");

        LlvmBasicBlockHandle openStoreBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_open_store");
        LlvmBasicBlockHandle checkSslStoreBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_ssl_store");
        LlvmBasicBlockHandle loopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_loop");
        LlvmBasicBlockHandle decodeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_decode");
        LlvmBasicBlockHandle addBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_add");
        LlvmBasicBlockHandle loopAdvanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_loop_advance");
        LlvmBasicBlockHandle closeCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_close_check");
        LlvmBasicBlockHandle closeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_close");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");
        LlvmApi.BuildCondBr(builder, haveSymbols, openStoreBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, openStoreBlock);
        LlvmValueHandle rootStore = EmitWindowsCertOpenSystemStore(state,
            EmitStringToCString(state, EmitHeapStringLiteral(state, "ROOT"), prefix + "_root_name"),
            prefix + "_open_root");
        LlvmApi.BuildStore(builder, rootStore, rootStoreSlot);
        LlvmValueHandle hasRootStore = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, rootStore, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_root_store");
        LlvmApi.BuildCondBr(builder, hasRootStore, checkSslStoreBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkSslStoreBlock);
        LlvmTypeHandle sslCtxGetCertStoreType = LlvmApi.FunctionType(state.I8Ptr, [state.I8Ptr]);
        LlvmValueHandle sslStorePtr = EmitCallFunctionAddress(state,
            sslCtxGetCertStoreFn,
            sslCtxGetCertStoreType,
            [sslCtx],
            prefix + "_get_cert_store_call");
        LlvmValueHandle sslStoreHandle = LlvmApi.BuildPtrToInt(builder, sslStorePtr, state.I64, prefix + "_ssl_store_handle");
        LlvmApi.BuildStore(builder, sslStoreHandle, sslStoreSlot);
        LlvmValueHandle hasSslStore = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, sslStoreHandle, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_ssl_store");
        LlvmApi.BuildCondBr(builder, hasSslStore, loopBlock, closeCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBlock);
        LlvmValueHandle previousContext = LlvmApi.BuildLoad2(builder, state.I64, previousContextSlot, prefix + "_previous_context");
        LlvmValueHandle currentContext = EmitWindowsCertEnumCertificatesInStore(state,
            LlvmApi.BuildLoad2(builder, state.I64, rootStoreSlot, prefix + "_root_store_handle"),
            previousContext,
            prefix + "_enum_cert");
        LlvmApi.BuildStore(builder, currentContext, previousContextSlot);
        LlvmValueHandle hasCurrentContext = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, currentContext, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_current_context");
        LlvmApi.BuildCondBr(builder, hasCurrentContext, decodeBlock, closeCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, decodeBlock);
        LlvmValueHandle encodedBytesHandle = LoadMemory(state, currentContext, 8, prefix + "_encoded_bytes_handle");
        LlvmValueHandle encodedLength = LlvmApi.BuildZExt(builder, EmitLoadI32AtOffset(state, currentContext, 16, prefix + "_encoded_length"), state.I64, prefix + "_encoded_length_i64");
        LlvmApi.BuildStore(builder, LlvmApi.BuildIntToPtr(builder, encodedBytesHandle, state.I8Ptr, prefix + "_encoded_bytes_ptr"), encodedBytesSlot);
        LlvmTypeHandle d2iX509Type = LlvmApi.FunctionType(state.I8Ptr, [state.I8Ptr, state.I8Ptr, state.I64]);
        LlvmValueHandle certificatePtr = EmitCallFunctionAddress(state,
            d2iX509Fn,
            d2iX509Type,
            [LlvmApi.ConstNull(state.I8Ptr), encodedBytesSlot, encodedLength],
            prefix + "_d2i_x509_call");
        LlvmValueHandle certificateHandle = LlvmApi.BuildPtrToInt(builder, certificatePtr, state.I64, prefix + "_certificate_handle");
        LlvmValueHandle haveCertificate = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, certificateHandle, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_certificate");
        LlvmApi.BuildCondBr(builder, haveCertificate, addBlock, loopAdvanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, addBlock);
        LlvmTypeHandle x509StoreAddCertType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr]);
        _ = EmitCallFunctionAddress(state,
            x509StoreAddCertFn,
            x509StoreAddCertType,
            [LlvmApi.BuildIntToPtr(builder, LlvmApi.BuildLoad2(builder, state.I64, sslStoreSlot, prefix + "_ssl_store_handle_value"), state.I8Ptr, prefix + "_ssl_store_ptr"), certificatePtr],
            prefix + "_store_add_cert_call");
        LlvmTypeHandle x509FreeType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr]);
        _ = EmitCallFunctionAddress(state, x509FreeFn, x509FreeType, [certificatePtr], string.Empty);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), importedAnySlot);
        LlvmApi.BuildBr(builder, loopAdvanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopAdvanceBlock);
        LlvmApi.BuildBr(builder, loopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeCheckBlock);
        LlvmValueHandle rootStoreHandle = LlvmApi.BuildLoad2(builder, state.I64, rootStoreSlot, prefix + "_root_store_value");
        LlvmValueHandle shouldClose = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, rootStoreHandle, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_should_close");
        LlvmApi.BuildCondBr(builder, shouldClose, closeBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeBlock);
        EmitWindowsCertCloseStore(state, rootStoreHandle, prefix + "_close_root_store");
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, importedAnySlot, prefix + "_imported_any");
    }

    private static LlvmValueHandle EmitEnsureWindowsTlsRuntimeInitialized(LlvmCodegenState state, LinuxTlsGlobals globals, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmBasicBlockHandle initBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_init");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");
        LlvmBasicBlockHandle afterLibsslBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_after_libssl");
        LlvmBasicBlockHandle afterLibcryptoBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_after_libcrypto");
        LlvmBasicBlockHandle failMissingLibraryBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_fail_missing_library");
        LlvmBasicBlockHandle failMissingSymbolBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_fail_missing_symbol");
        LlvmBasicBlockHandle failInitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_fail_init");

        LlvmValueHandle currentStatus = LlvmApi.BuildLoad2(builder, state.I64, globals.InitStatusGlobal, prefix + "_current_status");
        LlvmValueHandle needsInit = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, currentStatus, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_needs_init");
        LlvmApi.BuildCondBr(builder, needsInit, initBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, initBlock);
        LlvmValueHandle libsslHandle = EmitWindowsLoadLibraryWithFallback(state, "libssl-3-x64.dll", "libssl-3.dll", prefix + "_libssl");
        LlvmApi.BuildStore(builder, libsslHandle, globals.LibsslHandleGlobal);
        LlvmValueHandle hasLibssl = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, libsslHandle, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_libssl");
        LlvmApi.BuildCondBr(builder, hasLibssl, afterLibsslBlock, failMissingLibraryBlock);

        LlvmApi.PositionBuilderAtEnd(builder, afterLibsslBlock);
        LlvmValueHandle libcryptoHandle = EmitWindowsLoadLibraryWithFallback(state, "libcrypto-3-x64.dll", "libcrypto-3.dll", prefix + "_libcrypto");
        LlvmApi.BuildStore(builder, libcryptoHandle, globals.LibcryptoHandleGlobal);
        LlvmValueHandle hasLibcrypto = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, libcryptoHandle, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_libcrypto");
        LlvmApi.BuildCondBr(builder, hasLibcrypto, afterLibcryptoBlock, failMissingLibraryBlock);

        LlvmApi.PositionBuilderAtEnd(builder, afterLibcryptoBlock);
        LlvmValueHandle openSslInitFn = EmitTlsResolveSymbol(state, libsslHandle, "OPENSSL_init_ssl", prefix + "_resolve_init_ssl");
        LlvmValueHandle tlsClientMethodFn = EmitTlsResolveSymbol(state, libsslHandle, "TLS_client_method", prefix + "_resolve_tls_client_method");
        LlvmValueHandle sslCtxNewFn = EmitTlsResolveSymbol(state, libsslHandle, "SSL_CTX_new", prefix + "_resolve_ctx_new");
        LlvmValueHandle sslCtxSetVerifyFn = EmitTlsResolveSymbol(state, libsslHandle, "SSL_CTX_set_verify", prefix + "_resolve_ctx_set_verify");
        LlvmValueHandle sslCtxSetDefaultVerifyPathsFn = EmitTlsResolveSymbol(state, libsslHandle, "SSL_CTX_set_default_verify_paths", prefix + "_resolve_ctx_set_default_verify_paths");
        LlvmValueHandle haveAllSymbols = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, openSslInitFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_init_ssl"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, tlsClientMethodFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_tls_client_method"),
            prefix + "_have_first_symbols");
        haveAllSymbols = LlvmApi.BuildAnd(builder,
            haveAllSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, sslCtxNewFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_ctx_new"),
            prefix + "_have_second_symbols");
        haveAllSymbols = LlvmApi.BuildAnd(builder,
            haveAllSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, sslCtxSetVerifyFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_ctx_set_verify"),
            prefix + "_have_third_symbols");
        haveAllSymbols = LlvmApi.BuildAnd(builder,
            haveAllSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, sslCtxSetDefaultVerifyPathsFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_ctx_set_default_verify_paths"),
            prefix + "_have_all_symbols");
        LlvmBasicBlockHandle initializeCtxBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_initialize_ctx");
        LlvmApi.BuildCondBr(builder, haveAllSymbols, initializeCtxBlock, failMissingSymbolBlock);

        LlvmApi.PositionBuilderAtEnd(builder, initializeCtxBlock);
        LlvmTypeHandle initSslType = LlvmApi.FunctionType(state.I32, [state.I64, state.I8Ptr]);
        LlvmValueHandle initSslStatus = LlvmApi.BuildCall2(builder,
            initSslType,
            LlvmApi.BuildIntToPtr(builder, openSslInitFn, LlvmApi.PointerTypeInContext(state.Target.Context, 0), prefix + "_init_ssl_ptr"),
            [LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstNull(state.I8Ptr)],
            prefix + "_init_ssl_call");
        LlvmValueHandle initSslOk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildZExt(builder, initSslStatus, state.I64, prefix + "_init_ssl_i64"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_init_ssl_ok");
        LlvmBasicBlockHandle createContextBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create_ctx");
        LlvmApi.BuildCondBr(builder, initSslOk, createContextBlock, failInitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createContextBlock);
        LlvmTypeHandle tlsClientMethodType = LlvmApi.FunctionType(state.I8Ptr, []);
        LlvmValueHandle tlsMethod = LlvmApi.BuildCall2(builder,
            tlsClientMethodType,
            LlvmApi.BuildIntToPtr(builder, tlsClientMethodFn, LlvmApi.PointerTypeInContext(state.Target.Context, 0), prefix + "_tls_client_method_ptr"),
            Array.Empty<LlvmValueHandle>(),
            prefix + "_tls_client_method_call");
        LlvmValueHandle haveTlsMethod = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildPtrToInt(builder, tlsMethod, state.I64, prefix + "_tls_method_i64"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_tls_method");
        LlvmBasicBlockHandle configureContextBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_configure_ctx");
        LlvmApi.BuildCondBr(builder, haveTlsMethod, configureContextBlock, failInitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, configureContextBlock);
        LlvmTypeHandle sslCtxNewType = LlvmApi.FunctionType(state.I8Ptr, [state.I8Ptr]);
        LlvmValueHandle sslCtx = LlvmApi.BuildCall2(builder,
            sslCtxNewType,
            LlvmApi.BuildIntToPtr(builder, sslCtxNewFn, LlvmApi.PointerTypeInContext(state.Target.Context, 0), prefix + "_ctx_new_ptr"),
            [tlsMethod],
            prefix + "_ctx_new_call");
        LlvmValueHandle haveCtx = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildPtrToInt(builder, sslCtx, state.I64, prefix + "_ctx_i64"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_ctx");
        LlvmBasicBlockHandle configureVerifyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_configure_verify");
        LlvmApi.BuildCondBr(builder, haveCtx, configureVerifyBlock, failInitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, configureVerifyBlock);
        LlvmTypeHandle sslCtxSetVerifyType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr, state.I32, state.I8Ptr]);
        LlvmApi.BuildCall2(builder,
            sslCtxSetVerifyType,
            LlvmApi.BuildIntToPtr(builder, sslCtxSetVerifyFn, LlvmApi.PointerTypeInContext(state.Target.Context, 0), prefix + "_ctx_set_verify_ptr"),
            [sslCtx, LlvmApi.ConstInt(state.I32, TlsVerifyPeer, 0), LlvmApi.ConstNull(state.I8Ptr)],
            string.Empty);
        LlvmTypeHandle sslCtxSetDefaultVerifyPathsType = LlvmApi.FunctionType(state.I32, [state.I8Ptr]);
        LlvmValueHandle defaultVerifyPathsStatus = LlvmApi.BuildCall2(builder,
            sslCtxSetDefaultVerifyPathsType,
            LlvmApi.BuildIntToPtr(builder, sslCtxSetDefaultVerifyPathsFn, LlvmApi.PointerTypeInContext(state.Target.Context, 0), prefix + "_ctx_set_default_verify_paths_ptr"),
            [sslCtx],
            prefix + "_ctx_set_default_verify_paths_call");
        LlvmValueHandle verifyPathsOk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildZExt(builder, defaultVerifyPathsStatus, state.I64, prefix + "_verify_paths_i64"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_verify_paths_ok");
        LlvmValueHandle importedRootStore = EmitPopulateWindowsTlsTrustStore(state, sslCtx, libsslHandle, libcryptoHandle, prefix + "_import_root_store");
        LlvmValueHandle trustConfigured = LlvmApi.BuildOr(builder,
            verifyPathsOk,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, importedRootStore, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_imported_root_store_ok"),
            prefix + "_trust_configured");
        LlvmBasicBlockHandle successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_success");
        LlvmApi.BuildCondBr(builder, trustConfigured, successBlock, failInitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildPtrToInt(builder, sslCtx, state.I64, prefix + "_ctx_store_value"), globals.ContextGlobal);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), globals.InitStatusGlobal);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failMissingLibraryBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), globals.InitStatusGlobal);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failMissingSymbolBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-2L)), 1), globals.InitStatusGlobal);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failInitBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-3L)), 1), globals.InitStatusGlobal);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, globals.InitStatusGlobal, prefix + "_status");
    }

    private static LlvmValueHandle EmitTlsInitFailureResult(LlvmCodegenState state, LlvmValueHandle initStatus)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle isMissingRuntime = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, initStatus, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), "tls_init_is_missing_runtime");
        LlvmValueHandle errorText = LlvmApi.BuildSelect(builder,
            isMissingRuntime,
            EmitHeapStringLiteral(state, HttpsRequiresOpenSslRuntimeMessage),
            EmitHeapStringLiteral(state, TlsRuntimeInitFailedMessage),
            "tls_init_error_text");
        return EmitResultError(state, errorText);
    }

    private static LlvmValueHandle EmitCallFunctionAddress(LlvmCodegenState state, LlvmValueHandle functionAddress, LlvmTypeHandle functionType, ReadOnlySpan<LlvmValueHandle> args, string name)
    {
        LlvmValueHandle functionPtr = LlvmApi.BuildIntToPtr(state.Target.Builder, functionAddress, LlvmApi.PointerTypeInContext(state.Target.Context, 0), name + "_ptr");
        return LlvmApi.BuildCall2(state.Target.Builder, functionType, functionPtr, args, name);
    }

    private static LlvmValueHandle EmitCreateTlsSession(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle sslHandle, string prefix)
    {
        LlvmValueHandle session = EmitAlloc(state, TlsSessionLayout.TotalSize);
        StoreMemory(state, session, TlsSessionLayout.Socket, socket, prefix + "_socket");
        StoreMemory(state, session, TlsSessionLayout.SslHandle, sslHandle, prefix + "_ssl");
        return session;
    }

    private static LlvmValueHandle EmitLoadTlsSessionSocket(LlvmCodegenState state, LlvmValueHandle session, string prefix)
        => LoadMemory(state, session, TlsSessionLayout.Socket, prefix + "_socket");

    private static LlvmValueHandle EmitLoadTlsSessionSsl(LlvmCodegenState state, LlvmValueHandle session, string prefix)
        => LoadMemory(state, session, TlsSessionLayout.SslHandle, prefix + "_ssl");

    private static void EmitCleanupTlsSession(LlvmCodegenState state, LinuxTlsGlobals globals, LlvmValueHandle session, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle libsslHandle = LlvmApi.BuildLoad2(builder, state.I64, globals.LibsslHandleGlobal, prefix + "_libssl_handle");
        LlvmValueHandle sslFreeFn = EmitTlsResolveSymbol(state, libsslHandle, "SSL_free", prefix + "_resolve_ssl_free");
        LlvmTypeHandle sslFreeType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr]);
        LlvmValueHandle sslHandle = EmitLoadTlsSessionSsl(state, session, prefix + "_load_ssl");
        LlvmApi.BuildCall2(builder,
            sslFreeType,
            LlvmApi.BuildIntToPtr(builder, sslFreeFn, LlvmApi.PointerTypeInContext(state.Target.Context, 0), prefix + "_ssl_free_ptr"),
            [LlvmApi.BuildIntToPtr(builder, sslHandle, state.I8Ptr, prefix + "_ssl_ptr")],
            string.Empty);
        _ = EmitTcpClose(state, EmitLoadTlsSessionSocket(state, session, prefix + "_load_socket"));
    }

    private readonly record struct LinuxTlsGlobals(
        LlvmValueHandle InitStatusGlobal,
        LlvmValueHandle ContextGlobal,
        LlvmValueHandle LibsslHandleGlobal,
        LlvmValueHandle LibcryptoHandleGlobal);

    private static LlvmValueHandle EmitLeafTaskCompletedStatus(LlvmCodegenState state)
        => LlvmApi.ConstInt(state.I64, 1, 0);

    private static LlvmValueHandle EmitLeafTaskPendingStatus(LlvmCodegenState state)
        => LlvmApi.ConstInt(state.I64, 0, 0);

    private static void EmitClearLeafTaskWait(LlvmCodegenState state, LlvmValueHandle taskPtr, string prefix)
    {
        StoreMemory(state, taskPtr, TaskStructLayout.WaitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitNone, 0), prefix + "_wait_kind_clear");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitHandle, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_wait_handle_clear");
    }

    private static LlvmValueHandle EmitCompleteLeafTask(LlvmCodegenState state, LlvmValueHandle taskPtr, LlvmValueHandle result, string prefix)
    {
        EmitClearLeafTaskWait(state, taskPtr, prefix);
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot, result, prefix + "_result");
        StoreMemory(state, taskPtr, TaskStructLayout.StateIndex,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateCompleted), 1), prefix + "_done");
        return EmitLeafTaskCompletedStatus(state);
    }

    private static LlvmValueHandle EmitPendingLeafTask(LlvmCodegenState state, LlvmValueHandle taskPtr, long waitKind, LlvmValueHandle waitHandle, string prefix)
    {
        StoreMemory(state, taskPtr, TaskStructLayout.WaitKind, LlvmApi.ConstInt(state.I64, unchecked((ulong)waitKind), 0), prefix + "_wait_kind");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitHandle, waitHandle, prefix + "_wait_handle");
        return EmitLeafTaskPendingStatus(state);
    }

    private static void EmitSetSocketNonBlocking(LlvmCodegenState state, LlvmValueHandle socket, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        if (IsLinuxFlavor(state.Flavor))
        {
            LlvmValueHandle currentFlags = EmitLinuxSyscall(
                state,
                SyscallFcntl,
                socket,
                LlvmApi.ConstInt(state.I64, LinuxFcntlGetFlags, 0),
                LlvmApi.ConstInt(state.I64, 0, 0),
                prefix + "_getfl");
            LlvmValueHandle nextFlags = LlvmApi.BuildOr(builder,
                currentFlags,
                LlvmApi.ConstInt(state.I64, LinuxOpenNonBlocking, 0),
                prefix + "_flags_with_nonblock");
            EmitLinuxSyscall(
                state,
                SyscallFcntl,
                socket,
                LlvmApi.ConstInt(state.I64, LinuxFcntlSetFlags, 0),
                nextFlags,
                prefix + "_setfl");
        }
        else
        {
            LlvmValueHandle nonBlockingSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_nonblocking_slot");
            LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), nonBlockingSlot);
            EmitWindowsIoctlSocket(
                state,
                socket,
                LlvmApi.ConstInt(state.I64, WindowsFionBio, 0),
                nonBlockingSlot,
                prefix + "_ioctlsocket");
        }
    }

    private static LlvmValueHandle EmitStepTcpConnectTask(LlvmCodegenState state, LlvmValueHandle taskPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle hostRef = LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, "step_tcp_connect_host");
        LlvmValueHandle port = LoadMemory(state, taskPtr, TaskStructLayout.IoArg1, "step_tcp_connect_port");
        LlvmValueHandle socketSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_connect_socket_slot");
        LlvmValueHandle addrSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_connect_addr_slot");
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_connect_status_slot");
        LlvmApi.BuildStore(builder, LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tcp_connect_socket_cached"), socketSlot);
        LlvmApi.BuildStore(builder, LoadMemory(state, taskPtr, TaskStructLayout.WaitData1, "step_tcp_connect_addr_cached"), addrSlot);
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);

        LlvmValueHandle socketKnown = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne,
            LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "step_tcp_connect_socket_known"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "step_tcp_connect_has_socket");
        LlvmBasicBlockHandle reuseSocketBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_reuse_socket");
        LlvmBasicBlockHandle setupSocketBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_setup_socket");
        LlvmBasicBlockHandle connectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_connect");
        LlvmBasicBlockHandle pendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_pending");
        LlvmBasicBlockHandle failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_fail");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_done_block");
        LlvmApi.BuildCondBr(builder, socketKnown, reuseSocketBlock, setupSocketBlock);

        LlvmApi.PositionBuilderAtEnd(builder, setupSocketBlock);
        LlvmValueHandle resolveResult = EmitResolveHostIpv4OrLocalhost(state, hostRef, "step_tcp_connect_resolve");
        LlvmValueHandle resolveTag = LoadMemory(state, resolveResult, 0, "step_tcp_connect_resolve_tag");
        LlvmValueHandle resolveFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, resolveTag, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_connect_resolve_failed");
        LlvmBasicBlockHandle validatePortBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_validate_port");
        LlvmBasicBlockHandle openSocketBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_open_socket");
        LlvmApi.BuildCondBr(builder, resolveFailed, failBlock, validatePortBlock);

        LlvmApi.PositionBuilderAtEnd(builder, validatePortBlock);
        LlvmValueHandle validPort = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, port, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_connect_port_gt_zero"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, port, LlvmApi.ConstInt(state.I64, 65535, 0), "step_tcp_connect_port_le_max"),
            "step_tcp_connect_port_valid");
        LlvmApi.BuildCondBr(builder, validPort, openSocketBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, openSocketBlock);
        LlvmValueHandle openedSocket;
        if (IsLinuxFlavor(state.Flavor))
        {
            openedSocket = EmitLinuxSyscall(
                state,
                SyscallSocket,
                LlvmApi.ConstInt(state.I64, 2, 0),
                LlvmApi.ConstInt(state.I64, 1, 0),
                LlvmApi.ConstInt(state.I64, 0, 0),
                "step_tcp_connect_socket_call");
        }
        else
        {
            LlvmTypeHandle wsadataType = LlvmApi.ArrayType2(state.I8, 512);
            LlvmValueHandle wsadata = LlvmApi.BuildAlloca(builder, wsadataType, "step_tcp_connect_wsadata");
            EmitWindowsWsaStartup(state, GetArrayElementPointer(state, wsadataType, wsadata, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_connect_wsadata_ptr"), "step_tcp_connect_wsastartup");
            openedSocket = EmitWindowsSocket(state, 2, 1, 6, "step_tcp_connect_socket_call");
        }
        LlvmApi.BuildStore(builder, openedSocket, socketSlot);
        LlvmApi.BuildStore(builder, LoadMemory(state, resolveResult, 8, "step_tcp_connect_addr_value"), addrSlot);
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, openedSocket, "step_tcp_connect_store_socket");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData1, LoadMemory(state, resolveResult, 8, "step_tcp_connect_store_addr_value"), "step_tcp_connect_store_addr");
        EmitSetSocketNonBlocking(state, openedSocket, "step_tcp_connect_nonblocking");
        LlvmApi.BuildBr(builder, connectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, reuseSocketBlock);
        LlvmApi.BuildBr(builder, connectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectBlock);
        LlvmTypeHandle sockaddrType = LlvmApi.ArrayType2(state.I8, 16);
        LlvmValueHandle sockaddrStorage = LlvmApi.BuildAlloca(builder, sockaddrType, "step_tcp_connect_sockaddr");
        LlvmValueHandle sockaddrBytes = GetArrayElementPointer(state, sockaddrType, sockaddrStorage, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_connect_sockaddr_bytes");
        LlvmTypeHandle i16 = LlvmApi.Int16TypeInContext(state.Target.Context);
        LlvmTypeHandle i16Ptr = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.BuildBitCast(builder, sockaddrBytes, state.I64Ptr, "step_tcp_connect_sockaddr_i64"));
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.BuildBitCast(builder,
            LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, [LlvmApi.ConstInt(state.I64, 8, 0)], "step_tcp_connect_sockaddr_tail"),
            state.I64Ptr,
            "step_tcp_connect_sockaddr_tail_i64"));
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(i16, 2, 0), LlvmApi.BuildBitCast(builder, sockaddrBytes, i16Ptr, "step_tcp_connect_family_ptr"));
        LlvmValueHandle portPtr = LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, [LlvmApi.ConstInt(state.I64, 2, 0)], "step_tcp_connect_port_ptr_byte");
        LlvmApi.BuildStore(builder,
            LlvmApi.BuildTrunc(builder, EmitByteSwap16(state, port, "step_tcp_connect_port_network"), i16, "step_tcp_connect_port_i16"),
            LlvmApi.BuildBitCast(builder, portPtr, i16Ptr, "step_tcp_connect_port_ptr"));
        LlvmValueHandle addrPtr = LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, [LlvmApi.ConstInt(state.I64, 4, 0)], "step_tcp_connect_addr_ptr_byte");
        LlvmApi.BuildStore(builder,
            LlvmApi.BuildTrunc(builder, LlvmApi.BuildLoad2(builder, state.I64, addrSlot, "step_tcp_connect_addr_loaded"), state.I32, "step_tcp_connect_addr_i32"),
            LlvmApi.BuildBitCast(builder, addrPtr, state.I32Ptr, "step_tcp_connect_addr_ptr"));

        LlvmValueHandle connectSucceeded;
        LlvmValueHandle connectPending;
        if (IsLinuxFlavor(state.Flavor))
        {
            LlvmValueHandle connectResult = EmitLinuxSyscall(
                state,
                SyscallConnect,
                LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "step_tcp_connect_socket_value"),
                LlvmApi.BuildPtrToInt(builder, sockaddrBytes, state.I64, "step_tcp_connect_sockaddr_ptr"),
                LlvmApi.ConstInt(state.I64, 16, 0),
                "step_tcp_connect_call");
            connectSucceeded = LlvmApi.BuildOr(builder,
                LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, connectResult, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_connect_ok"),
                LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, connectResult, LlvmApi.ConstInt(state.I64, unchecked((ulong)LinuxErrIsConnected), 1), "step_tcp_connect_is_connected"),
                "step_tcp_connect_succeeded");
            connectPending = LlvmApi.BuildOr(builder,
                LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, connectResult, LlvmApi.ConstInt(state.I64, unchecked((ulong)LinuxErrInProgress), 1), "step_tcp_connect_in_progress"),
                LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, connectResult, LlvmApi.ConstInt(state.I64, unchecked((ulong)LinuxErrAlready), 1), "step_tcp_connect_already"),
                "step_tcp_connect_pending");
        }
        else
        {
            LlvmValueHandle connectOk = EmitWindowsConnect(state, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "step_tcp_connect_socket_value"), sockaddrBytes, "step_tcp_connect_call");
            LlvmValueHandle wsaError = LlvmApi.BuildSExt(builder, EmitWindowsWsaGetLastError(state, "step_tcp_connect_error"), state.I64, "step_tcp_connect_error_i64");
            connectSucceeded = LlvmApi.BuildOr(builder,
                connectOk,
                LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, wsaError, LlvmApi.ConstInt(state.I64, WindowsWsaErrorIsConnected, 0), "step_tcp_connect_is_connected"),
                "step_tcp_connect_succeeded");
            connectPending = LlvmApi.BuildOr(builder,
                LlvmApi.BuildOr(builder,
                    LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, wsaError, LlvmApi.ConstInt(state.I64, WindowsWsaErrorWouldBlock, 0), "step_tcp_connect_would_block"),
                    LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, wsaError, LlvmApi.ConstInt(state.I64, WindowsWsaErrorInProgress, 0), "step_tcp_connect_in_progress"),
                    "step_tcp_connect_pending_pair"),
                LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, wsaError, LlvmApi.ConstInt(state.I64, WindowsWsaErrorAlready, 0), "step_tcp_connect_already"),
                "step_tcp_connect_pending");
        }
        LlvmBasicBlockHandle successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_success");
        LlvmBasicBlockHandle pendingCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_pending_check");
        LlvmApi.BuildCondBr(builder, connectSucceeded, successBlock, pendingCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingCheckBlock);
        LlvmApi.BuildCondBr(builder, connectPending, pendingBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        LlvmApi.BuildStore(builder,
            EmitCompleteLeafTask(state, taskPtr, EmitResultOk(state, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "step_tcp_connect_socket_ok")), "step_tcp_connect_complete"),
            statusSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingBlock);
        LlvmApi.BuildStore(builder,
            EmitPendingLeafTask(state, taskPtr, TaskStructLayout.WaitSocketWrite, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "step_tcp_connect_pending_socket"), "step_tcp_connect_pending_store"),
            statusSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildStore(builder,
            EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TcpConnectFailedMessage)), "step_tcp_connect_fail_complete"),
            statusSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, statusSlot, "step_tcp_connect_status");
    }

    private static LlvmValueHandle EmitStepTcpSendTask(LlvmCodegenState state, LlvmValueHandle taskPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle socket = LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, "step_tcp_send_socket");
        LlvmValueHandle textRef = LoadMemory(state, taskPtr, TaskStructLayout.IoArg1, "step_tcp_send_text");
        LlvmValueHandle sentSoFar = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tcp_send_sent_so_far");
        LlvmValueHandle totalLen = LoadStringLength(state, textRef, "step_tcp_send_total_len");
        LlvmValueHandle remaining = LlvmApi.BuildSub(builder, totalLen, sentSoFar, "step_tcp_send_remaining");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, remaining, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_send_done_bool");
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_send_status_slot");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmBasicBlockHandle alreadyDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_already_done");
        LlvmBasicBlockHandle sendBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_send");
        LlvmBasicBlockHandle finishBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_finish");
        LlvmApi.BuildCondBr(builder, done, alreadyDoneBlock, sendBlock);

        LlvmApi.PositionBuilderAtEnd(builder, alreadyDoneBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultOk(state, totalLen), "step_tcp_send_already_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, sendBlock);
        LlvmValueHandle cursorPtr = LlvmApi.BuildIntToPtr(builder,
            LlvmApi.BuildAdd(builder, GetStringBytesAddress(state, textRef, "step_tcp_send_base"), sentSoFar, "step_tcp_send_cursor_addr"),
            state.I8Ptr,
            "step_tcp_send_cursor_ptr");
        LlvmValueHandle sentRaw = IsLinuxFlavor(state.Flavor)
            ? EmitLinuxSyscall(state, SyscallWrite, socket, LlvmApi.BuildPtrToInt(builder, cursorPtr, state.I64, "step_tcp_send_cursor_i64"), remaining, "step_tcp_send_call")
            : LlvmApi.BuildSExt(builder,
                EmitWindowsSend(state, socket, cursorPtr,
                    LlvmApi.BuildTrunc(builder,
                        LlvmApi.BuildSelect(builder,
                            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, remaining, LlvmApi.ConstInt(state.I64, int.MaxValue, 0), "step_tcp_send_limit_gt"),
                            LlvmApi.ConstInt(state.I64, int.MaxValue, 0),
                            remaining,
                            "step_tcp_send_chunk_len"),
                        state.I32,
                        "step_tcp_send_chunk_i32"),
                    "step_tcp_send_call"),
                state.I64,
                "step_tcp_send_sent_raw");
        LlvmValueHandle sentPositive = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, sentRaw, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_send_sent_positive");
        LlvmBasicBlockHandle sentBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_sent_block");
        LlvmBasicBlockHandle pendingCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_pending_check");
        LlvmBasicBlockHandle failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_fail");
        LlvmApi.BuildCondBr(builder, sentPositive, sentBlock, pendingCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, sentBlock);
        LlvmValueHandle nextSent = LlvmApi.BuildAdd(builder, sentSoFar, sentRaw, "step_tcp_send_next_sent");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, nextSent, "step_tcp_send_store_sent");
        LlvmValueHandle sendDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, nextSent, totalLen, "step_tcp_send_send_done");
        LlvmBasicBlockHandle sendDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_send_done_block");
        LlvmBasicBlockHandle sendPendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_send_pending_block");
        LlvmApi.BuildCondBr(builder, sendDone, sendDoneBlock, sendPendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, sendDoneBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultOk(state, totalLen), "step_tcp_send_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, sendPendingBlock);
        LlvmApi.BuildStore(builder, EmitPendingLeafTask(state, taskPtr, TaskStructLayout.WaitSocketWrite, socket, "step_tcp_send_more_pending"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingCheckBlock);
        LlvmValueHandle isPending;
        if (IsLinuxFlavor(state.Flavor))
        {
            isPending = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, sentRaw, LlvmApi.ConstInt(state.I64, unchecked((ulong)LinuxErrWouldBlock), 1), "step_tcp_send_linux_pending");
        }
        else
        {
            LlvmValueHandle wsaError = LlvmApi.BuildSExt(builder, EmitWindowsWsaGetLastError(state, "step_tcp_send_error"), state.I64, "step_tcp_send_error_i64");
            isPending = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, wsaError, LlvmApi.ConstInt(state.I64, WindowsWsaErrorWouldBlock, 0), "step_tcp_send_windows_pending");
        }
        LlvmBasicBlockHandle pendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_pending_block");
        LlvmApi.BuildCondBr(builder, isPending, pendingBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingBlock);
        LlvmApi.BuildStore(builder, EmitPendingLeafTask(state, taskPtr, TaskStructLayout.WaitSocketWrite, socket, "step_tcp_send_pending"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TcpSendFailedMessage)), "step_tcp_send_fail_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, statusSlot, "step_tcp_send_status");
    }

    private static LlvmValueHandle EmitStepTcpReceiveTask(LlvmCodegenState state, LlvmValueHandle taskPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle socket = LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, "step_tcp_receive_socket");
        LlvmValueHandle maxBytes = LoadMemory(state, taskPtr, TaskStructLayout.IoArg1, "step_tcp_receive_max");
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_receive_status_slot");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmValueHandle positiveMax = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, maxBytes, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_receive_positive_max");
        LlvmBasicBlockHandle allocateBufferBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_allocate_buffer");
        LlvmBasicBlockHandle failMaxBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_fail_max");
        LlvmBasicBlockHandle readBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_read");
        LlvmBasicBlockHandle finishBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_finish");
        LlvmApi.BuildCondBr(builder, positiveMax, allocateBufferBlock, failMaxBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failMaxBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TcpInvalidMaxBytesMessage)), "step_tcp_receive_invalid_max"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, allocateBufferBlock);
        LlvmValueHandle bufferRef = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tcp_receive_buffer_ref");
        LlvmValueHandle hasBuffer = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, bufferRef, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_receive_has_buffer");
        LlvmBasicBlockHandle reuseBufferBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_reuse_buffer");
        LlvmBasicBlockHandle createBufferBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_create_buffer");
        LlvmApi.BuildCondBr(builder, hasBuffer, reuseBufferBlock, createBufferBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createBufferBlock);
        LlvmValueHandle newBufferRef = EmitAllocDynamic(state, LlvmApi.BuildAdd(builder, maxBytes, LlvmApi.ConstInt(state.I64, 8, 0), "step_tcp_receive_buffer_size"));
        StoreMemory(state, newBufferRef, 0, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_receive_buffer_len_init");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, newBufferRef, "step_tcp_receive_store_buffer");
        LlvmApi.BuildBr(builder, readBlock);

        LlvmApi.PositionBuilderAtEnd(builder, reuseBufferBlock);
        LlvmApi.BuildBr(builder, readBlock);

        LlvmApi.PositionBuilderAtEnd(builder, readBlock);
        LlvmValueHandle activeBuffer = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tcp_receive_active_buffer");
        LlvmValueHandle readCount = IsLinuxFlavor(state.Flavor)
            ? EmitLinuxSyscall(state, SyscallRead, socket, LlvmApi.BuildPtrToInt(builder, GetStringBytesPointer(state, activeBuffer, "step_tcp_receive_bytes"), state.I64, "step_tcp_receive_bytes_i64"), maxBytes, "step_tcp_receive_call")
            : LlvmApi.BuildSExt(builder, EmitWindowsRecv(state, socket, GetStringBytesPointer(state, activeBuffer, "step_tcp_receive_bytes"), LlvmApi.BuildTrunc(builder, maxBytes, state.I32, "step_tcp_receive_max_i32"), "step_tcp_receive_call"), state.I64, "step_tcp_receive_call_i64");
        LlvmValueHandle readOk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, readCount, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_receive_ok");
        LlvmBasicBlockHandle handleReadBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_handle_read");
        LlvmBasicBlockHandle pendingCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_pending_check");
        LlvmApi.BuildCondBr(builder, readOk, handleReadBlock, pendingCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, handleReadBlock);
        StoreMemory(state, activeBuffer, 0, readCount, "step_tcp_receive_store_len");
        LlvmValueHandle emptyRead = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, readCount, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_receive_empty");
        LlvmBasicBlockHandle successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_success");
        LlvmBasicBlockHandle validateUtf8Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_validate_utf8");
        LlvmApi.BuildCondBr(builder, emptyRead, successBlock, validateUtf8Block);

        LlvmApi.PositionBuilderAtEnd(builder, validateUtf8Block);
        LlvmValueHandle utf8Valid = EmitValidateUtf8(state, GetStringBytesPointer(state, activeBuffer, "step_tcp_receive_validate_bytes"), readCount, "step_tcp_receive_utf8");
        LlvmValueHandle validUtf8 = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, utf8Valid, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_receive_valid_utf8");
        LlvmBasicBlockHandle invalidUtf8Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_invalid_utf8");
        LlvmApi.BuildCondBr(builder, validUtf8, successBlock, invalidUtf8Block);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultOk(state, activeBuffer), "step_tcp_receive_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, invalidUtf8Block);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TcpInvalidUtf8Message)), "step_tcp_receive_invalid_utf8_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingCheckBlock);
        LlvmValueHandle isPending;
        if (IsLinuxFlavor(state.Flavor))
        {
            isPending = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, readCount, LlvmApi.ConstInt(state.I64, unchecked((ulong)LinuxErrWouldBlock), 1), "step_tcp_receive_linux_pending");
        }
        else
        {
            LlvmValueHandle wsaError = LlvmApi.BuildSExt(builder, EmitWindowsWsaGetLastError(state, "step_tcp_receive_error"), state.I64, "step_tcp_receive_error_i64");
            isPending = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, wsaError, LlvmApi.ConstInt(state.I64, WindowsWsaErrorWouldBlock, 0), "step_tcp_receive_windows_pending");
        }
        LlvmBasicBlockHandle pendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_pending_block");
        LlvmBasicBlockHandle failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_fail_block");
        LlvmApi.BuildCondBr(builder, isPending, pendingBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingBlock);
        LlvmApi.BuildStore(builder, EmitPendingLeafTask(state, taskPtr, TaskStructLayout.WaitSocketRead, socket, "step_tcp_receive_pending"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TcpReceiveFailedMessage)), "step_tcp_receive_fail_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, statusSlot, "step_tcp_receive_status");
    }

    private static LlvmValueHandle EmitStepTcpCloseTask(LlvmCodegenState state, LlvmValueHandle taskPtr)
    {
        return EmitCompleteLeafTask(
            state,
            taskPtr,
            EmitTcpClose(state, LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, "step_tcp_close_socket")),
            "step_tcp_close_complete");
    }

    private static LlvmValueHandle EmitStepTlsConnectTask(LlvmCodegenState state, LlvmValueHandle taskPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tls_connect_status_slot");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);

        LlvmBasicBlockHandle connectStageBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_connect_connect_stage");
        LlvmBasicBlockHandle handshakeStageBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_connect_handshake_stage");
        LlvmBasicBlockHandle finishBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_connect_finish");

        LlvmValueHandle stage = LoadMemory(state, taskPtr, TaskStructLayout.WaitData1, "step_tls_connect_stage");
        LlvmValueHandle isHandshakeStage = LlvmApi.BuildICmp(
            builder,
            LlvmIntPredicate.Eq,
            stage,
            LlvmApi.ConstInt(state.I64, 1, 0),
            "step_tls_connect_is_handshake_stage");
        LlvmApi.BuildCondBr(builder, isHandshakeStage, handshakeStageBlock, connectStageBlock);

        EmitTlsConnectTcpStage(state, connectStageBlock, taskPtr, finishBlock, statusSlot);
        EmitTlsConnectHandshakeStage(state, handshakeStageBlock, taskPtr, finishBlock, statusSlot);

        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, statusSlot, "step_tls_connect_status");
    }

    private static LlvmValueHandle EmitStepTlsHandshakeTask(LlvmCodegenState state, LlvmValueHandle taskPtr, LinuxTlsGlobals globals)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tls_handshake_status_slot");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);

        LlvmValueHandle initStatus = EmitNetworkingRuntimeCall(state, "ashes_tls_runtime_init", Array.Empty<LlvmValueHandle>(), "step_tls_handshake_init");
        LlvmValueHandle initReady = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, initStatus, LlvmApi.ConstInt(state.I64, 1, 0), "step_tls_handshake_init_ready");

        LlvmBasicBlockHandle initFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_init_fail");
        LlvmBasicBlockHandle sessionCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_session_check");
        LlvmBasicBlockHandle createBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_create");
        LlvmBasicBlockHandle createSocketFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_create_socket_fail");
        LlvmBasicBlockHandle cleanupFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_cleanup_fail");
        LlvmBasicBlockHandle connectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_connect");
        LlvmBasicBlockHandle inspectErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_inspect_error");
        LlvmBasicBlockHandle pendingReadBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_pending_read");
        LlvmBasicBlockHandle pendingWriteCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_pending_write_check");
        LlvmBasicBlockHandle pendingWriteBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_pending_write");
        LlvmBasicBlockHandle failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_fail");
        LlvmBasicBlockHandle successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_success");
        LlvmBasicBlockHandle finishBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_finish");

        LlvmApi.BuildCondBr(builder, initReady, sessionCheckBlock, initFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, initFailBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitTlsInitFailureResult(state, initStatus), "step_tls_handshake_init_fail_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, sessionCheckBlock);
        LlvmValueHandle existingSession = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tls_handshake_existing_session");
        LlvmValueHandle hasSession = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, existingSession, LlvmApi.ConstInt(state.I64, 0, 0), "step_tls_handshake_has_session");
        LlvmApi.BuildCondBr(builder, hasSession, connectBlock, createBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createBlock);
        LlvmValueHandle socket = LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, "step_tls_handshake_socket");
        LlvmValueHandle host = LoadMemory(state, taskPtr, TaskStructLayout.IoArg1, "step_tls_handshake_host");
        LlvmValueHandle ctxHandle = LlvmApi.BuildLoad2(builder, state.I64, globals.ContextGlobal, "step_tls_handshake_ctx_handle");
        LlvmValueHandle libsslHandle = LlvmApi.BuildLoad2(builder, state.I64, globals.LibsslHandleGlobal, "step_tls_handshake_libssl_handle");
        LlvmValueHandle libcryptoHandle = LlvmApi.BuildLoad2(builder, state.I64, globals.LibcryptoHandleGlobal, "step_tls_handshake_libcrypto_handle");
        LlvmValueHandle sslNewFn = EmitTlsResolveSymbol(state, libsslHandle, "SSL_new", "step_tls_handshake_resolve_ssl_new");
        LlvmTypeHandle sslNewType = LlvmApi.FunctionType(state.I8Ptr, [state.I8Ptr]);
        LlvmValueHandle sslPtr = EmitCallFunctionAddress(state,
            sslNewFn,
            sslNewType,
            [LlvmApi.BuildIntToPtr(builder, ctxHandle, state.I8Ptr, "step_tls_handshake_ctx_ptr")],
            "step_tls_handshake_ssl_new");
        LlvmValueHandle sslHandle = LlvmApi.BuildPtrToInt(builder, sslPtr, state.I64, "step_tls_handshake_ssl_handle");
        LlvmValueHandle haveSsl = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, sslHandle, LlvmApi.ConstInt(state.I64, 0, 0), "step_tls_handshake_have_ssl");
        LlvmBasicBlockHandle configureBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_configure");
        LlvmApi.BuildCondBr(builder, haveSsl, configureBlock, createSocketFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, configureBlock);
        LlvmValueHandle session = EmitCreateTlsSession(state, socket, sslHandle, "step_tls_handshake_session");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, session, "step_tls_handshake_store_session");
        LlvmValueHandle hostCstr = EmitStringToCString(state, host, "step_tls_handshake_host_cstr");
        LlvmValueHandle sslCtrlFn = EmitTlsResolveSymbol(state, libsslHandle, "SSL_ctrl", "step_tls_handshake_resolve_ssl_ctrl");
        LlvmTypeHandle sslCtrlType = LlvmApi.FunctionType(state.I64, [state.I8Ptr, state.I32, state.I64, state.I8Ptr]);
        LlvmValueHandle sniStatus = EmitCallFunctionAddress(state,
            sslCtrlFn,
            sslCtrlType,
            [sslPtr, LlvmApi.ConstInt(state.I32, TlsCtrlSetSni, 0), LlvmApi.ConstInt(state.I64, 0, 0), hostCstr],
            "step_tls_handshake_set_sni");
        LlvmValueHandle sniOk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, sniStatus, LlvmApi.ConstInt(state.I64, 0, 0), "step_tls_handshake_sni_ok");
        LlvmBasicBlockHandle setHostBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_set_host");
        LlvmApi.BuildCondBr(builder, sniOk, setHostBlock, cleanupFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, setHostBlock);
        LlvmValueHandle sslGet0ParamFn = EmitTlsResolveSymbol(state, libsslHandle, "SSL_get0_param", "step_tls_handshake_resolve_ssl_get0_param");
        LlvmTypeHandle sslGet0ParamType = LlvmApi.FunctionType(state.I8Ptr, [state.I8Ptr]);
        LlvmValueHandle paramPtr = EmitCallFunctionAddress(state, sslGet0ParamFn, sslGet0ParamType, [sslPtr], "step_tls_handshake_get0_param");
        LlvmValueHandle haveParam = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildPtrToInt(builder, paramPtr, state.I64, "step_tls_handshake_param_i64"), LlvmApi.ConstInt(state.I64, 0, 0), "step_tls_handshake_have_param");
        LlvmBasicBlockHandle setFdBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_set_fd");
        LlvmBasicBlockHandle setHostFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_set_host_fail");
        LlvmApi.BuildCondBr(builder, haveParam, setFdBlock, setHostFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, setFdBlock);
        LlvmValueHandle setHostFn = EmitTlsResolveSymbol(state, libcryptoHandle, "X509_VERIFY_PARAM_set1_host", "step_tls_handshake_resolve_set1_host");
        LlvmTypeHandle setHostType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, state.I64]);
        LlvmValueHandle setHostStatus = EmitCallFunctionAddress(state,
            setHostFn,
            setHostType,
            [paramPtr, hostCstr, LlvmApi.ConstInt(state.I64, 0, 0)],
            "step_tls_handshake_set1_host");
        LlvmValueHandle setHostOk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildZExt(builder, setHostStatus, state.I64, "step_tls_handshake_set_host_i64"), LlvmApi.ConstInt(state.I64, 0, 0), "step_tls_handshake_set_host_ok");
        LlvmApi.BuildCondBr(builder, setHostOk, setHostFailBlock, cleanupFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, setHostFailBlock);
        LlvmValueHandle sslSetFdFn = EmitTlsResolveSymbol(state, libsslHandle, "SSL_set_fd", "step_tls_handshake_resolve_set_fd");
        LlvmTypeHandle sslSetFdType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I32]);
        LlvmValueHandle setFdStatus = EmitCallFunctionAddress(state,
            sslSetFdFn,
            sslSetFdType,
            [sslPtr, LlvmApi.BuildTrunc(builder, socket, state.I32, "step_tls_handshake_socket_i32")],
            "step_tls_handshake_set_fd");
        LlvmValueHandle setFdOk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildZExt(builder, setFdStatus, state.I64, "step_tls_handshake_set_fd_i64"), LlvmApi.ConstInt(state.I64, 0, 0), "step_tls_handshake_set_fd_ok");
        LlvmApi.BuildCondBr(builder, setFdOk, connectBlock, cleanupFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createSocketFailBlock);
        _ = EmitTcpClose(state, socket);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TlsHandshakeFailedMessage)), "step_tls_handshake_create_fail_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, cleanupFailBlock);
        EmitCleanupTlsSession(state, globals, LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tls_handshake_cleanup_session"), "step_tls_handshake_cleanup");
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TlsHandshakeFailedMessage)), "step_tls_handshake_cleanup_fail_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectBlock);
        LlvmValueHandle activeSession = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tls_handshake_active_session");
        LlvmValueHandle activeSsl = EmitLoadTlsSessionSsl(state, activeSession, "step_tls_handshake_active_ssl");
        LlvmValueHandle sslConnectFn = EmitTlsResolveSymbol(state, LlvmApi.BuildLoad2(builder, state.I64, globals.LibsslHandleGlobal, "step_tls_handshake_connect_libssl"), "SSL_connect", "step_tls_handshake_resolve_ssl_connect");
        LlvmTypeHandle sslConnectType = LlvmApi.FunctionType(state.I32, [state.I8Ptr]);
        LlvmValueHandle connectStatus = EmitCallFunctionAddress(state,
            sslConnectFn,
            sslConnectType,
            [LlvmApi.BuildIntToPtr(builder, activeSsl, state.I8Ptr, "step_tls_handshake_active_ssl_ptr")],
            "step_tls_handshake_connect_call");
        LlvmValueHandle connectOk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, connectStatus, LlvmApi.ConstInt(state.I32, 1, 0), "step_tls_handshake_connect_ok");
        LlvmApi.BuildCondBr(builder, connectOk, successBlock, inspectErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, inspectErrorBlock);
        LlvmValueHandle sslGetErrorFn = EmitTlsResolveSymbol(state, LlvmApi.BuildLoad2(builder, state.I64, globals.LibsslHandleGlobal, "step_tls_handshake_error_libssl"), "SSL_get_error", "step_tls_handshake_resolve_ssl_get_error");
        LlvmTypeHandle sslGetErrorType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I32]);
        LlvmValueHandle errorCode = EmitCallFunctionAddress(state,
            sslGetErrorFn,
            sslGetErrorType,
            [LlvmApi.BuildIntToPtr(builder, activeSsl, state.I8Ptr, "step_tls_handshake_error_ssl_ptr"), connectStatus],
            "step_tls_handshake_get_error_call");
        LlvmValueHandle wantRead = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, errorCode, LlvmApi.ConstInt(state.I32, TlsErrorWantRead, 0), "step_tls_handshake_want_read");
        LlvmApi.BuildCondBr(builder, wantRead, pendingReadBlock, pendingWriteCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingReadBlock);
        LlvmApi.BuildStore(builder, EmitPendingLeafTask(state, taskPtr, TaskStructLayout.WaitTlsWantRead, EmitLoadTlsSessionSocket(state, activeSession, "step_tls_handshake_pending_read_socket"), "step_tls_handshake_pending_read"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingWriteCheckBlock);
        LlvmValueHandle wantWrite = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, errorCode, LlvmApi.ConstInt(state.I32, TlsErrorWantWrite, 0), "step_tls_handshake_want_write");
        LlvmApi.BuildCondBr(builder, wantWrite, pendingWriteBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingWriteBlock);
        LlvmApi.BuildStore(builder, EmitPendingLeafTask(state, taskPtr, TaskStructLayout.WaitTlsWantWrite, EmitLoadTlsSessionSocket(state, activeSession, "step_tls_handshake_pending_write_socket"), "step_tls_handshake_pending_write"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        EmitCleanupTlsSession(state, globals, activeSession, "step_tls_handshake_fail_cleanup");
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TlsHandshakeFailedMessage)), "step_tls_handshake_fail_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultOk(state, activeSession), "step_tls_handshake_success_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, statusSlot, "step_tls_handshake_status");
    }

    private static LlvmValueHandle EmitStepTlsSendTask(LlvmCodegenState state, LlvmValueHandle taskPtr, LinuxTlsGlobals globals)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle session = LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, "step_tls_send_session");
        LlvmValueHandle textRef = LoadMemory(state, taskPtr, TaskStructLayout.IoArg1, "step_tls_send_text");
        LlvmValueHandle totalLen = LoadStringLength(state, textRef, "step_tls_send_total_len");
        LlvmValueHandle sentSoFar = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tls_send_sent_so_far");
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tls_send_status_slot");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);

        LlvmValueHandle alreadyDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, sentSoFar, totalLen, "step_tls_send_already_done");
        LlvmBasicBlockHandle sendBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_send_send");
        LlvmBasicBlockHandle applyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_send_apply");
        LlvmBasicBlockHandle inspectErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_send_inspect_error");
        LlvmBasicBlockHandle pendingReadBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_send_pending_read");
        LlvmBasicBlockHandle pendingWriteCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_send_pending_write_check");
        LlvmBasicBlockHandle pendingWriteBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_send_pending_write");
        LlvmBasicBlockHandle failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_send_fail");
        LlvmBasicBlockHandle completeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_send_complete");
        LlvmBasicBlockHandle finishBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_send_finish");
        LlvmApi.BuildCondBr(builder, alreadyDone, completeBlock, sendBlock);

        LlvmApi.PositionBuilderAtEnd(builder, sendBlock);
        LlvmValueHandle remaining = LlvmApi.BuildSub(builder, totalLen, sentSoFar, "step_tls_send_remaining");
        LlvmValueHandle sslWriteFn = EmitTlsResolveSymbol(state, LlvmApi.BuildLoad2(builder, state.I64, globals.LibsslHandleGlobal, "step_tls_send_libssl"), "SSL_write", "step_tls_send_resolve_ssl_write");
        LlvmTypeHandle sslWriteType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, state.I32]);
        LlvmValueHandle cursorPtr = LlvmApi.BuildGEP2(builder, state.I8, GetStringBytesPointer(state, textRef, "step_tls_send_bytes"), [sentSoFar], "step_tls_send_cursor_ptr");
        LlvmValueHandle writeStatus = EmitCallFunctionAddress(state,
            sslWriteFn,
            sslWriteType,
            [LlvmApi.BuildIntToPtr(builder, EmitLoadTlsSessionSsl(state, session, "step_tls_send_ssl"), state.I8Ptr, "step_tls_send_ssl_ptr"), cursorPtr, LlvmApi.BuildTrunc(builder, remaining, state.I32, "step_tls_send_remaining_i32")],
            "step_tls_send_call");
        LlvmValueHandle wroteBytes = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, writeStatus, LlvmApi.ConstInt(state.I32, 0, 0), "step_tls_send_wrote_bytes");
        LlvmApi.BuildCondBr(builder, wroteBytes, applyBlock, inspectErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, applyBlock);
        LlvmValueHandle nextSent = LlvmApi.BuildAdd(builder, sentSoFar, LlvmApi.BuildSExt(builder, writeStatus, state.I64, "step_tls_send_written_i64"), "step_tls_send_next_sent");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, nextSent, "step_tls_send_store_sent");
        LlvmValueHandle completed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, nextSent, totalLen, "step_tls_send_completed");
        LlvmApi.BuildCondBr(builder, completed, completeBlock, pendingWriteBlock);

        LlvmApi.PositionBuilderAtEnd(builder, inspectErrorBlock);
        LlvmValueHandle sslGetErrorFn = EmitTlsResolveSymbol(state, LlvmApi.BuildLoad2(builder, state.I64, globals.LibsslHandleGlobal, "step_tls_send_error_libssl"), "SSL_get_error", "step_tls_send_resolve_ssl_get_error");
        LlvmTypeHandle sslGetErrorType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I32]);
        LlvmValueHandle errorCode = EmitCallFunctionAddress(state,
            sslGetErrorFn,
            sslGetErrorType,
            [LlvmApi.BuildIntToPtr(builder, EmitLoadTlsSessionSsl(state, session, "step_tls_send_error_ssl"), state.I8Ptr, "step_tls_send_error_ssl_ptr"), writeStatus],
            "step_tls_send_get_error");
        LlvmValueHandle wantRead = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, errorCode, LlvmApi.ConstInt(state.I32, TlsErrorWantRead, 0), "step_tls_send_want_read");
        LlvmApi.BuildCondBr(builder, wantRead, pendingReadBlock, pendingWriteCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingReadBlock);
        LlvmApi.BuildStore(builder, EmitPendingLeafTask(state, taskPtr, TaskStructLayout.WaitTlsWantRead, EmitLoadTlsSessionSocket(state, session, "step_tls_send_pending_read_socket"), "step_tls_send_pending_read"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingWriteCheckBlock);
        LlvmValueHandle wantWrite = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, errorCode, LlvmApi.ConstInt(state.I32, TlsErrorWantWrite, 0), "step_tls_send_want_write");
        LlvmApi.BuildCondBr(builder, wantWrite, pendingWriteBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingWriteBlock);
        LlvmApi.BuildStore(builder, EmitPendingLeafTask(state, taskPtr, TaskStructLayout.WaitTlsWantWrite, EmitLoadTlsSessionSocket(state, session, "step_tls_send_pending_write_socket"), "step_tls_send_pending_write"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TlsSendFailedMessage)), "step_tls_send_fail_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, completeBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultOk(state, totalLen), "step_tls_send_complete_result"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, statusSlot, "step_tls_send_status");
    }

    private static LlvmValueHandle EmitStepTlsReceiveTask(LlvmCodegenState state, LlvmValueHandle taskPtr, LinuxTlsGlobals globals)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle session = LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, "step_tls_receive_session");
        LlvmValueHandle maxBytes = LoadMemory(state, taskPtr, TaskStructLayout.IoArg1, "step_tls_receive_max_bytes");
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tls_receive_status_slot");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);

        LlvmValueHandle positiveMax = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, maxBytes, LlvmApi.ConstInt(state.I64, 0, 0), "step_tls_receive_positive_max");
        LlvmBasicBlockHandle initBufferBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_init_buffer");
        LlvmBasicBlockHandle receiveBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_receive");
        LlvmBasicBlockHandle handleReadBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_handle_read");
        LlvmBasicBlockHandle inspectErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_inspect_error");
        LlvmBasicBlockHandle pendingReadBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_pending_read");
        LlvmBasicBlockHandle pendingWriteCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_pending_write_check");
        LlvmBasicBlockHandle pendingWriteBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_pending_write");
        LlvmBasicBlockHandle failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_fail");
        LlvmBasicBlockHandle successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_success");
        LlvmBasicBlockHandle validateUtf8Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_validate_utf8");
        LlvmBasicBlockHandle invalidUtf8Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_invalid_utf8");
        LlvmBasicBlockHandle finishBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_finish");

        LlvmApi.BuildCondBr(builder, positiveMax, initBufferBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, initBufferBlock);
        LlvmValueHandle bufferRef = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tls_receive_buffer_ref");
        LlvmValueHandle hasBuffer = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, bufferRef, LlvmApi.ConstInt(state.I64, 0, 0), "step_tls_receive_has_buffer");
        LlvmBasicBlockHandle allocateBufferBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_allocate_buffer");
        LlvmApi.BuildCondBr(builder, hasBuffer, receiveBlock, allocateBufferBlock);

        LlvmApi.PositionBuilderAtEnd(builder, allocateBufferBlock);
        LlvmValueHandle newBufferRef = EmitAllocDynamic(state, LlvmApi.BuildAdd(builder, maxBytes, LlvmApi.ConstInt(state.I64, 8, 0), "step_tls_receive_buffer_size"));
        StoreMemory(state, newBufferRef, 0, LlvmApi.ConstInt(state.I64, 0, 0), "step_tls_receive_store_buffer_len");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, newBufferRef, "step_tls_receive_store_buffer_ref");
        LlvmApi.BuildBr(builder, receiveBlock);

        LlvmApi.PositionBuilderAtEnd(builder, receiveBlock);
        LlvmValueHandle activeBuffer = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tls_receive_active_buffer");
        LlvmValueHandle sslReadFn = EmitTlsResolveSymbol(state, LlvmApi.BuildLoad2(builder, state.I64, globals.LibsslHandleGlobal, "step_tls_receive_libssl"), "SSL_read", "step_tls_receive_resolve_ssl_read");
        LlvmTypeHandle sslReadType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, state.I32]);
        LlvmValueHandle readStatus = EmitCallFunctionAddress(state,
            sslReadFn,
            sslReadType,
            [LlvmApi.BuildIntToPtr(builder, EmitLoadTlsSessionSsl(state, session, "step_tls_receive_ssl"), state.I8Ptr, "step_tls_receive_ssl_ptr"), GetStringBytesPointer(state, activeBuffer, "step_tls_receive_bytes"), LlvmApi.BuildTrunc(builder, maxBytes, state.I32, "step_tls_receive_max_i32")],
            "step_tls_receive_call");
        LlvmValueHandle readPositive = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, readStatus, LlvmApi.ConstInt(state.I32, 0, 0), "step_tls_receive_read_positive");
        LlvmValueHandle readZero = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, readStatus, LlvmApi.ConstInt(state.I32, 0, 0), "step_tls_receive_read_zero");
        LlvmBasicBlockHandle zeroReadBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_zero_read");
        LlvmApi.BuildCondBr(builder, readPositive, handleReadBlock, zeroReadBlock);

        LlvmApi.PositionBuilderAtEnd(builder, zeroReadBlock);
        LlvmApi.BuildCondBr(builder, readZero, successBlock, inspectErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, handleReadBlock);
        LlvmValueHandle readCount = LlvmApi.BuildSExt(builder, readStatus, state.I64, "step_tls_receive_read_count");
        StoreMemory(state, activeBuffer, 0, readCount, "step_tls_receive_store_read_len");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, readCount, LlvmApi.ConstInt(state.I64, 0, 0), "step_tls_receive_empty_read"), successBlock, validateUtf8Block);

        LlvmApi.PositionBuilderAtEnd(builder, validateUtf8Block);
        LlvmValueHandle utf8Valid = EmitValidateUtf8(state, GetStringBytesPointer(state, activeBuffer, "step_tls_receive_validate_bytes"), readCount, "step_tls_receive_utf8");
        LlvmValueHandle validUtf8 = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, utf8Valid, LlvmApi.ConstInt(state.I64, 0, 0), "step_tls_receive_valid_utf8");
        LlvmApi.BuildCondBr(builder, validUtf8, successBlock, invalidUtf8Block);

        LlvmApi.PositionBuilderAtEnd(builder, inspectErrorBlock);
        LlvmValueHandle sslGetErrorFn = EmitTlsResolveSymbol(state, LlvmApi.BuildLoad2(builder, state.I64, globals.LibsslHandleGlobal, "step_tls_receive_error_libssl"), "SSL_get_error", "step_tls_receive_resolve_ssl_get_error");
        LlvmTypeHandle sslGetErrorType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I32]);
        LlvmValueHandle errorCode = EmitCallFunctionAddress(state,
            sslGetErrorFn,
            sslGetErrorType,
            [LlvmApi.BuildIntToPtr(builder, EmitLoadTlsSessionSsl(state, session, "step_tls_receive_error_ssl"), state.I8Ptr, "step_tls_receive_error_ssl_ptr"), readStatus],
            "step_tls_receive_get_error");
        LlvmValueHandle wantRead = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, errorCode, LlvmApi.ConstInt(state.I32, TlsErrorWantRead, 0), "step_tls_receive_want_read");
        LlvmApi.BuildCondBr(builder, wantRead, pendingReadBlock, pendingWriteCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingReadBlock);
        LlvmApi.BuildStore(builder, EmitPendingLeafTask(state, taskPtr, TaskStructLayout.WaitTlsWantRead, EmitLoadTlsSessionSocket(state, session, "step_tls_receive_pending_read_socket"), "step_tls_receive_pending_read"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingWriteCheckBlock);
        LlvmValueHandle wantWrite = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, errorCode, LlvmApi.ConstInt(state.I32, TlsErrorWantWrite, 0), "step_tls_receive_want_write");
        LlvmApi.BuildCondBr(builder, wantWrite, pendingWriteBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingWriteBlock);
        LlvmApi.BuildStore(builder, EmitPendingLeafTask(state, taskPtr, TaskStructLayout.WaitTlsWantWrite, EmitLoadTlsSessionSocket(state, session, "step_tls_receive_pending_write_socket"), "step_tls_receive_pending_write"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultOk(state, LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tls_receive_success_buffer")), "step_tls_receive_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, invalidUtf8Block);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TlsInvalidUtf8Message)), "step_tls_receive_invalid_utf8_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TlsReceiveFailedMessage)), "step_tls_receive_fail_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, statusSlot, "step_tls_receive_status");
    }

    private static LlvmValueHandle EmitStepTlsCloseTask(LlvmCodegenState state, LlvmValueHandle taskPtr, LinuxTlsGlobals globals)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle session = LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, "step_tls_close_session");
        LlvmValueHandle libsslHandle = LlvmApi.BuildLoad2(builder, state.I64, globals.LibsslHandleGlobal, "step_tls_close_libssl");
        LlvmValueHandle sslShutdownFn = EmitTlsResolveSymbol(state, libsslHandle, "SSL_shutdown", "step_tls_close_resolve_ssl_shutdown");
        LlvmTypeHandle sslShutdownType = LlvmApi.FunctionType(state.I32, [state.I8Ptr]);
        LlvmValueHandle sslHandle = EmitLoadTlsSessionSsl(state, session, "step_tls_close_ssl");
        _ = EmitCallFunctionAddress(state,
            sslShutdownFn,
            sslShutdownType,
            [LlvmApi.BuildIntToPtr(builder, sslHandle, state.I8Ptr, "step_tls_close_ssl_ptr")],
            "step_tls_close_shutdown_call");
        EmitCleanupTlsSession(state, globals, session, "step_tls_close_cleanup");
        return EmitCompleteLeafTask(
            state,
            taskPtr,
            EmitResultOk(state, EmitUnitValue(state)),
            "step_tls_close_complete");
    }

    private static LlvmValueHandle EmitStepTcpConnectTaskWindowsIocp(LlvmCodegenState state, LlvmValueHandle taskPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_connect_win_status_slot");
        LlvmValueHandle errorRefSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_connect_win_error_ref_slot");
        LlvmValueHandle socketSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_connect_win_socket_slot");
        LlvmValueHandle addrSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_connect_win_addr_slot");
        LlvmValueHandle pendingContextSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_connect_win_pending_context_slot");
        LlvmValueHandle waitContext = LoadMemory(state, taskPtr, TaskStructLayout.WaitHandle, "step_tcp_connect_win_wait_context");
        LlvmValueHandle hostRef = LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, "step_tcp_connect_win_host");
        LlvmValueHandle port = LoadMemory(state, taskPtr, TaskStructLayout.IoArg1, "step_tcp_connect_win_port");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildStore(builder, EmitHeapStringLiteral(state, TcpConnectFailedMessage), errorRefSlot);
        LlvmApi.BuildStore(builder, LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tcp_connect_win_cached_socket"), socketSlot);
        LlvmApi.BuildStore(builder, LoadMemory(state, taskPtr, TaskStructLayout.WaitData1, "step_tcp_connect_win_cached_addr"), addrSlot);
        LlvmApi.BuildStore(builder, waitContext, pendingContextSlot);

        LlvmBasicBlockHandle resumeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_resume");
        LlvmBasicBlockHandle setupBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_setup");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_done");
        LlvmApi.BuildCondBr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, waitContext, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_connect_win_has_context"),
            resumeBlock,
            setupBlock);

        LlvmApi.PositionBuilderAtEnd(builder, resumeBlock);
        LlvmValueHandle resumeStatus = EmitWindowsIocpOperationStatus(state, waitContext, "step_tcp_connect_win_resume");
        LlvmValueHandle resumePending = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, resumeStatus, LlvmApi.ConstInt(state.I64, WindowsIocpOperationLayout.StatePending, 0), "step_tcp_connect_win_resume_pending");
        LlvmBasicBlockHandle resumeCompletedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_resume_completed");
        LlvmBasicBlockHandle failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_fail");
        LlvmBasicBlockHandle pendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_pending");
        LlvmApi.BuildCondBr(builder, resumePending, pendingBlock, resumeCompletedBlock);

        LlvmApi.PositionBuilderAtEnd(builder, resumeCompletedBlock);
        LlvmValueHandle resumeSucceeded = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, resumeStatus, LlvmApi.ConstInt(state.I64, WindowsIocpOperationLayout.StateCompleted, 0), "step_tcp_connect_win_resume_succeeded");
        LlvmBasicBlockHandle successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_success");
        LlvmBasicBlockHandle resumeFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_resume_fail");
        LlvmApi.BuildCondBr(builder, resumeSucceeded, successBlock, resumeFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, resumeFailBlock);
        LlvmApi.BuildStore(builder, EmitHeapStringLiteral(state, "Ashes.Net.Tcp.connect() failed: IOCP completion failed"), errorRefSlot);
        LlvmApi.BuildBr(builder, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, setupBlock);
        LlvmValueHandle resolveResult = EmitResolveHostIpv4OrLocalhost(state, hostRef, "step_tcp_connect_win_resolve");
        LlvmValueHandle resolveTag = LoadMemory(state, resolveResult, 0, "step_tcp_connect_win_resolve_tag");
        LlvmValueHandle resolveFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, resolveTag, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_connect_win_resolve_failed");
        LlvmBasicBlockHandle validatePortBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_validate_port");
        LlvmApi.BuildCondBr(builder, resolveFailed, failBlock, validatePortBlock);

        LlvmApi.PositionBuilderAtEnd(builder, validatePortBlock);
        LlvmValueHandle validPort = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, port, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_connect_win_port_gt_zero"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, port, LlvmApi.ConstInt(state.I64, 65535, 0), "step_tcp_connect_win_port_le_max"),
            "step_tcp_connect_win_port_valid");
        LlvmBasicBlockHandle openSocketBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_open_socket");
        LlvmApi.BuildCondBr(builder, validPort, openSocketBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, openSocketBlock);
        LlvmTypeHandle wsadataType = LlvmApi.ArrayType2(state.I8, 512);
        LlvmValueHandle wsadata = LlvmApi.BuildAlloca(builder, wsadataType, "step_tcp_connect_win_wsadata");
        EmitWindowsWsaStartup(state, GetArrayElementPointer(state, wsadataType, wsadata, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_connect_win_wsadata_ptr"), "step_tcp_connect_win_wsastartup");
        LlvmValueHandle socket = EmitWindowsSocket(state, 2, 1, 6, "step_tcp_connect_win_socket_call");
        LlvmApi.BuildStore(builder, socket, socketSlot);
        LlvmApi.BuildStore(builder, LoadMemory(state, resolveResult, 8, "step_tcp_connect_win_addr_value"), addrSlot);
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, socket, "step_tcp_connect_win_store_socket");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData1, LoadMemory(state, resolveResult, 8, "step_tcp_connect_win_store_addr_value"), "step_tcp_connect_win_store_addr");

        LlvmTypeHandle sockaddrType = LlvmApi.ArrayType2(state.I8, 16);
        LlvmValueHandle sockaddrStorage = LlvmApi.BuildAlloca(builder, sockaddrType, "step_tcp_connect_win_sockaddr");
        LlvmValueHandle sockaddrBytes = GetArrayElementPointer(state, sockaddrType, sockaddrStorage, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_connect_win_sockaddr_bytes");
        LlvmTypeHandle i16 = LlvmApi.Int16TypeInContext(state.Target.Context);
        LlvmTypeHandle i16Ptr = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.BuildBitCast(builder, sockaddrBytes, state.I64Ptr, "step_tcp_connect_win_sockaddr_i64"));
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.BuildBitCast(builder,
            LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, [LlvmApi.ConstInt(state.I64, 8, 0)], "step_tcp_connect_win_sockaddr_tail"),
            state.I64Ptr,
            "step_tcp_connect_win_sockaddr_tail_i64"));
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(i16, 2, 0), LlvmApi.BuildBitCast(builder, sockaddrBytes, i16Ptr, "step_tcp_connect_win_family_ptr"));
        LlvmValueHandle portPtr = LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, [LlvmApi.ConstInt(state.I64, 2, 0)], "step_tcp_connect_win_port_ptr_byte");
        LlvmApi.BuildStore(builder,
            LlvmApi.BuildTrunc(builder, EmitByteSwap16(state, port, "step_tcp_connect_win_port_network"), i16, "step_tcp_connect_win_port_i16"),
            LlvmApi.BuildBitCast(builder, portPtr, i16Ptr, "step_tcp_connect_win_port_ptr"));
        LlvmValueHandle addrPtr = LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, [LlvmApi.ConstInt(state.I64, 4, 0)], "step_tcp_connect_win_addr_ptr_byte");
        LlvmApi.BuildStore(builder,
            LlvmApi.BuildTrunc(builder, LlvmApi.BuildLoad2(builder, state.I64, addrSlot, "step_tcp_connect_win_addr_loaded"), state.I32, "step_tcp_connect_win_addr_i32"),
            LlvmApi.BuildBitCast(builder, addrPtr, state.I32Ptr, "step_tcp_connect_win_addr_ptr"));

        EmitWindowsAssociateSocketWithIocp(state, socket, "step_tcp_connect_win_associate");
        LlvmValueHandle connectExPtr = EmitWindowsLoadConnectExPointer(state, socket, "step_tcp_connect_win_connectex");
        LlvmValueHandle hasConnectEx = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, connectExPtr, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_connect_win_has_connectex");
        LlvmBasicBlockHandle bindBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_bind_any");
        LlvmBasicBlockHandle missingConnectExBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_missing_connectex");
        LlvmApi.BuildCondBr(builder, hasConnectEx, bindBlock, missingConnectExBlock);

        LlvmApi.PositionBuilderAtEnd(builder, missingConnectExBlock);
        LlvmApi.BuildStore(builder, EmitHeapStringLiteral(state, "Ashes.Net.Tcp.connect() failed: ConnectEx unavailable"), errorRefSlot);
        LlvmApi.BuildBr(builder, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, bindBlock);
        LlvmValueHandle bindResult = EmitWindowsBindIpv4Any(state, socket, "step_tcp_connect_win_bind_any");
        LlvmValueHandle bindSucceeded = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, bindResult, LlvmApi.ConstInt(state.I32, 0, 0), "step_tcp_connect_win_bind_ok");
        LlvmBasicBlockHandle connectIssueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_issue_connect");
        LlvmBasicBlockHandle bindFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_bind_fail");
        LlvmApi.BuildCondBr(builder, bindSucceeded, connectIssueBlock, bindFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, bindFailBlock);
        LlvmValueHandle bindError = LlvmApi.BuildZExt(builder, EmitWindowsWsaGetLastError(state, "step_tcp_connect_win_bind_error_code"), state.I64, "step_tcp_connect_win_bind_error_i64");
        LlvmValueHandle bindErrorText = EmitStringConcat(
            state,
            EmitHeapStringLiteral(state, "Ashes.Net.Tcp.connect() failed: bind "),
            EmitNonNegativeIntToString(state, bindError, "step_tcp_connect_win_bind_error_text"));
        LlvmApi.BuildStore(builder, bindErrorText, errorRefSlot);
        LlvmApi.BuildBr(builder, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectIssueBlock);
        LlvmValueHandle operationContext = EmitWindowsCreateIocpOperationContext(state, "step_tcp_connect_win_op_context");
        LlvmValueHandle bytesSentSlot = LlvmApi.BuildAlloca(builder, state.I32, "step_tcp_connect_win_bytes_sent_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), bytesSentSlot);
        LlvmValueHandle connectResult = EmitWindowsConnectEx(state, connectExPtr, socket, sockaddrBytes, EmitWindowsIocpOverlappedPtr(state, operationContext, "step_tcp_connect_win_overlapped"), bytesSentSlot, "step_tcp_connect_win_connectex_call");
        LlvmValueHandle connectImmediate = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, connectResult, LlvmApi.ConstInt(state.I32, 0, 0), "step_tcp_connect_win_connect_immediate");
        LlvmBasicBlockHandle connectErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_connect_error");
        LlvmApi.BuildCondBr(builder, connectImmediate, successBlock, connectErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectErrorBlock);
        LlvmValueHandle connectError = LlvmApi.BuildZExt(builder, EmitWindowsWsaGetLastError(state, "step_tcp_connect_win_connect_error_code"), state.I64, "step_tcp_connect_win_connect_error_i64");
        LlvmValueHandle isIoPending = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, connectError, LlvmApi.ConstInt(state.I64, WindowsErrorIoPending, 0), "step_tcp_connect_win_is_io_pending");
        LlvmBasicBlockHandle queuePendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_queue_pending");
        LlvmBasicBlockHandle connectImmediateFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_connect_immediate_fail");
        LlvmApi.BuildCondBr(builder, isIoPending, queuePendingBlock, connectImmediateFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectImmediateFailBlock);
        LlvmValueHandle connectErrorText = EmitStringConcat(
            state,
            EmitHeapStringLiteral(state, "Ashes.Net.Tcp.connect() failed: ConnectEx "),
            EmitNonNegativeIntToString(state, connectError, "step_tcp_connect_win_connect_error_text"));
        LlvmApi.BuildStore(builder, connectErrorText, errorRefSlot);
        LlvmApi.BuildBr(builder, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, queuePendingBlock);
        LlvmApi.BuildStore(builder, operationContext, pendingContextSlot);
        LlvmApi.BuildBr(builder, pendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        LlvmValueHandle connectedSocket = LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "step_tcp_connect_win_socket_ok");
        EmitWindowsUpdateConnectContext(state, connectedSocket, "step_tcp_connect_win_update_context");
        EmitSetSocketNonBlocking(state, connectedSocket, "step_tcp_connect_win_nonblocking");
        LlvmApi.BuildStore(builder,
            EmitCompleteLeafTask(state, taskPtr, EmitResultOk(state, connectedSocket), "step_tcp_connect_win_complete"),
            statusSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingBlock);
        LlvmApi.BuildStore(builder,
            EmitPendingLeafTask(state, taskPtr, TaskStructLayout.WaitSocketWrite, LlvmApi.BuildLoad2(builder, state.I64, pendingContextSlot, "step_tcp_connect_win_pending_context"), "step_tcp_connect_win_pending_store"),
            statusSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildStore(builder,
            EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, LlvmApi.BuildLoad2(builder, state.I64, errorRefSlot, "step_tcp_connect_win_error_ref")), "step_tcp_connect_win_fail_complete"),
            statusSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, statusSlot, "step_tcp_connect_win_status");
    }

    private static LlvmValueHandle EmitStepTcpSendTaskWindowsIocp(LlvmCodegenState state, LlvmValueHandle taskPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle socket = LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, "step_tcp_send_win_socket");
        LlvmValueHandle textRef = LoadMemory(state, taskPtr, TaskStructLayout.IoArg1, "step_tcp_send_win_text");
        LlvmValueHandle totalLen = LoadStringLength(state, textRef, "step_tcp_send_win_total_len");
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_send_win_status_slot");
        LlvmValueHandle sentSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_send_win_sent_slot");
        LlvmValueHandle pendingContextSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_send_win_pending_context_slot");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildStore(builder, LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tcp_send_win_sent_cached"), sentSlot);
        LlvmValueHandle waitContext = LoadMemory(state, taskPtr, TaskStructLayout.WaitHandle, "step_tcp_send_win_wait_context");
        LlvmApi.BuildStore(builder, waitContext, pendingContextSlot);

        LlvmBasicBlockHandle resumeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_resume");
        LlvmBasicBlockHandle loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_loop_check");
        LlvmBasicBlockHandle issueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_issue");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_done");
        LlvmBasicBlockHandle failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_fail");
        LlvmBasicBlockHandle pendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_pending");
        LlvmBasicBlockHandle completeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_complete");
        LlvmApi.BuildCondBr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, waitContext, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_send_win_has_context"),
            resumeBlock,
            loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, resumeBlock);
        LlvmValueHandle resumeStatus = EmitWindowsIocpOperationStatus(state, waitContext, "step_tcp_send_win_resume");
        LlvmValueHandle resumePending = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, resumeStatus, LlvmApi.ConstInt(state.I64, WindowsIocpOperationLayout.StatePending, 0), "step_tcp_send_win_resume_pending");
        LlvmBasicBlockHandle resumeCompletedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_resume_completed");
        LlvmApi.BuildCondBr(builder, resumePending, pendingBlock, resumeCompletedBlock);

        LlvmApi.PositionBuilderAtEnd(builder, resumeCompletedBlock);
        LlvmValueHandle resumeSucceeded = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, resumeStatus, LlvmApi.ConstInt(state.I64, WindowsIocpOperationLayout.StateCompleted, 0), "step_tcp_send_win_resume_succeeded");
        LlvmBasicBlockHandle applyCompletionBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_apply_completion");
        LlvmApi.BuildCondBr(builder, resumeSucceeded, applyCompletionBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, applyCompletionBlock);
        LlvmValueHandle completedBytes = EmitWindowsIocpBytesTransferred(state, waitContext, "step_tcp_send_win_resume");
        LlvmValueHandle completedPositive = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, completedBytes, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_send_win_completed_positive");
        LlvmBasicBlockHandle applyPositiveBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_apply_positive");
        LlvmApi.BuildCondBr(builder, completedPositive, applyPositiveBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, applyPositiveBlock);
        LlvmValueHandle resumedSent = LlvmApi.BuildAdd(builder, LlvmApi.BuildLoad2(builder, state.I64, sentSlot, "step_tcp_send_win_sent_value"), completedBytes, "step_tcp_send_win_sent_after_resume");
        LlvmApi.BuildStore(builder, resumedSent, sentSlot);
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, resumedSent, "step_tcp_send_win_store_sent_after_resume");
        EmitClearLeafTaskWait(state, taskPtr, "step_tcp_send_win_clear_wait_after_resume");
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle currentSent = LlvmApi.BuildLoad2(builder, state.I64, sentSlot, "step_tcp_send_win_current_sent");
        LlvmValueHandle remaining = LlvmApi.BuildSub(builder, totalLen, currentSent, "step_tcp_send_win_remaining");
        LlvmValueHandle isDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, remaining, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_send_win_is_done");
        LlvmApi.BuildCondBr(builder, isDone, completeBlock, issueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, issueBlock);
        LlvmValueHandle cursorPtr = LlvmApi.BuildIntToPtr(builder,
            LlvmApi.BuildAdd(builder, GetStringBytesAddress(state, textRef, "step_tcp_send_win_base"), currentSent, "step_tcp_send_win_cursor_addr"),
            state.I8Ptr,
            "step_tcp_send_win_cursor_ptr");
        LlvmValueHandle chunkLen = LlvmApi.BuildSelect(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, remaining, LlvmApi.ConstInt(state.I64, int.MaxValue, 0), "step_tcp_send_win_chunk_gt_max"),
            LlvmApi.ConstInt(state.I64, int.MaxValue, 0),
            remaining,
            "step_tcp_send_win_chunk_len");
        LlvmValueHandle sentRaw = LlvmApi.BuildSExt(builder,
            EmitWindowsSend(state, socket, cursorPtr, LlvmApi.BuildTrunc(builder, chunkLen, state.I32, "step_tcp_send_win_chunk_i32"), "step_tcp_send_win_sync_send"),
            state.I64,
            "step_tcp_send_win_sync_sent_raw");
        LlvmValueHandle sentPositive = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, sentRaw, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_send_win_sync_positive");
        LlvmBasicBlockHandle syncSentBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_sync_sent");
        LlvmBasicBlockHandle syncPendingCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_sync_pending_check");
        LlvmApi.BuildCondBr(builder, sentPositive, syncSentBlock, syncPendingCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, syncSentBlock);
        LlvmValueHandle nextSent = LlvmApi.BuildAdd(builder, currentSent, sentRaw, "step_tcp_send_win_next_sent");
        LlvmApi.BuildStore(builder, nextSent, sentSlot);
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, nextSent, "step_tcp_send_win_store_sent_sync");
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, syncPendingCheckBlock);
        LlvmValueHandle syncError = LlvmApi.BuildSExt(builder, EmitWindowsWsaGetLastError(state, "step_tcp_send_win_sync_error"), state.I64, "step_tcp_send_win_sync_error_i64");
        LlvmValueHandle syncWouldBlock = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, syncError, LlvmApi.ConstInt(state.I64, WindowsWsaErrorWouldBlock, 0), "step_tcp_send_win_sync_would_block");
        LlvmBasicBlockHandle issueOverlappedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_issue_overlapped");
        LlvmApi.BuildCondBr(builder, syncWouldBlock, issueOverlappedBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, issueOverlappedBlock);
        EmitWindowsAssociateSocketWithIocp(state, socket, "step_tcp_send_win_associate");
        LlvmValueHandle operationContext = EmitWindowsCreateIocpOperationContext(state, "step_tcp_send_win_op_context");
        LlvmValueHandle bytesSentSlot = LlvmApi.BuildAlloca(builder, state.I32, "step_tcp_send_win_bytes_sent_slot");
        LlvmValueHandle overlappedResult = EmitWindowsIssueWsaSend(state, socket, cursorPtr, chunkLen, EmitWindowsIocpOverlappedPtr(state, operationContext, "step_tcp_send_win_overlapped"), bytesSentSlot, "step_tcp_send_win_issue_wsa_send");
        LlvmValueHandle overlappedImmediate = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, overlappedResult, LlvmApi.ConstInt(state.I32, 0, 0), "step_tcp_send_win_overlapped_immediate");
        LlvmBasicBlockHandle overlappedImmediateBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_overlapped_immediate");
        LlvmBasicBlockHandle overlappedErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_overlapped_error");
        LlvmApi.BuildCondBr(builder, overlappedImmediate, overlappedImmediateBlock, overlappedErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, overlappedImmediateBlock);
        LlvmValueHandle immediateBytes = LlvmApi.BuildZExt(builder, LlvmApi.BuildLoad2(builder, state.I32, bytesSentSlot, "step_tcp_send_win_immediate_bytes"), state.I64, "step_tcp_send_win_immediate_bytes_i64");
        LlvmValueHandle immediatePositive = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, immediateBytes, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_send_win_immediate_positive");
        LlvmBasicBlockHandle applyImmediateBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_apply_immediate");
        LlvmApi.BuildCondBr(builder, immediatePositive, applyImmediateBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, applyImmediateBlock);
        LlvmValueHandle nextImmediateSent = LlvmApi.BuildAdd(builder, currentSent, immediateBytes, "step_tcp_send_win_next_immediate_sent");
        LlvmApi.BuildStore(builder, nextImmediateSent, sentSlot);
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, nextImmediateSent, "step_tcp_send_win_store_sent_immediate");
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, overlappedErrorBlock);
        LlvmValueHandle overlappedError = LlvmApi.BuildSExt(builder, EmitWindowsWsaGetLastError(state, "step_tcp_send_win_overlapped_error_code"), state.I64, "step_tcp_send_win_overlapped_error_i64");
        LlvmValueHandle overlappedPending = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, overlappedError, LlvmApi.ConstInt(state.I64, WindowsErrorIoPending, 0), "step_tcp_send_win_overlapped_pending");
        LlvmBasicBlockHandle storePendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_store_pending");
        LlvmApi.BuildCondBr(builder, overlappedPending, storePendingBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storePendingBlock);
        LlvmApi.BuildStore(builder, operationContext, pendingContextSlot);
        LlvmApi.BuildBr(builder, pendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, completeBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultOk(state, totalLen), "step_tcp_send_win_complete_result"), statusSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingBlock);
        LlvmApi.BuildStore(builder, EmitPendingLeafTask(state, taskPtr, TaskStructLayout.WaitSocketWrite, LlvmApi.BuildLoad2(builder, state.I64, pendingContextSlot, "step_tcp_send_win_pending_context"), "step_tcp_send_win_pending_store"), statusSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TcpSendFailedMessage)), "step_tcp_send_win_fail_complete"), statusSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, statusSlot, "step_tcp_send_win_status");
    }

    private static LlvmValueHandle EmitStepTcpReceiveTaskWindowsIocp(LlvmCodegenState state, LlvmValueHandle taskPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle socket = LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, "step_tcp_receive_win_socket");
        LlvmValueHandle maxBytes = LoadMemory(state, taskPtr, TaskStructLayout.IoArg1, "step_tcp_receive_win_max");
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_receive_win_status_slot");
        LlvmValueHandle readCountSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_receive_win_read_count_slot");
        LlvmValueHandle pendingContextSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_receive_win_pending_context_slot");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), readCountSlot);

        LlvmValueHandle positiveMax = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, maxBytes, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_receive_win_positive_max");
        LlvmBasicBlockHandle allocateBufferBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_allocate_buffer");
        LlvmBasicBlockHandle failMaxBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_fail_max");
        LlvmBasicBlockHandle afterBufferBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_after_buffer");
        LlvmBasicBlockHandle handleReadBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_handle_read");
        LlvmBasicBlockHandle finishBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_finish");
        LlvmBasicBlockHandle failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_fail");
        LlvmBasicBlockHandle pendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_pending");
        LlvmApi.BuildCondBr(builder, positiveMax, allocateBufferBlock, failMaxBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failMaxBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TcpInvalidMaxBytesMessage)), "step_tcp_receive_win_invalid_max"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, allocateBufferBlock);
        LlvmValueHandle bufferRef = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tcp_receive_win_buffer_ref");
        LlvmValueHandle hasBuffer = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, bufferRef, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_receive_win_has_buffer");
        LlvmBasicBlockHandle reuseBufferBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_reuse_buffer");
        LlvmBasicBlockHandle createBufferBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_create_buffer");
        LlvmApi.BuildCondBr(builder, hasBuffer, reuseBufferBlock, createBufferBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createBufferBlock);
        LlvmValueHandle newBufferRef = EmitAllocDynamic(state, LlvmApi.BuildAdd(builder, maxBytes, LlvmApi.ConstInt(state.I64, 8, 0), "step_tcp_receive_win_buffer_size"));
        StoreMemory(state, newBufferRef, 0, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_receive_win_buffer_len_init");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, newBufferRef, "step_tcp_receive_win_store_buffer");
        LlvmApi.BuildBr(builder, afterBufferBlock);

        LlvmApi.PositionBuilderAtEnd(builder, reuseBufferBlock);
        LlvmApi.BuildBr(builder, afterBufferBlock);

        LlvmApi.PositionBuilderAtEnd(builder, afterBufferBlock);
        LlvmValueHandle activeBuffer = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tcp_receive_win_active_buffer");
        LlvmValueHandle waitContext = LoadMemory(state, taskPtr, TaskStructLayout.WaitHandle, "step_tcp_receive_win_wait_context");
        LlvmApi.BuildStore(builder, waitContext, pendingContextSlot);
        LlvmBasicBlockHandle resumeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_resume");
        LlvmBasicBlockHandle readBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_read_block");
        LlvmApi.BuildCondBr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, waitContext, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_receive_win_has_context"),
            resumeBlock,
            readBlock);

        LlvmApi.PositionBuilderAtEnd(builder, resumeBlock);
        LlvmValueHandle resumeStatus = EmitWindowsIocpOperationStatus(state, waitContext, "step_tcp_receive_win_resume");
        LlvmValueHandle resumePending = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, resumeStatus, LlvmApi.ConstInt(state.I64, WindowsIocpOperationLayout.StatePending, 0), "step_tcp_receive_win_resume_pending");
        LlvmBasicBlockHandle resumeCompletedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_resume_completed");
        LlvmApi.BuildCondBr(builder, resumePending, pendingBlock, resumeCompletedBlock);

        LlvmApi.PositionBuilderAtEnd(builder, resumeCompletedBlock);
        LlvmValueHandle resumeSucceeded = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, resumeStatus, LlvmApi.ConstInt(state.I64, WindowsIocpOperationLayout.StateCompleted, 0), "step_tcp_receive_win_resume_succeeded");
        LlvmBasicBlockHandle applyCompletionBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_apply_completion");
        LlvmApi.BuildCondBr(builder, resumeSucceeded, applyCompletionBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, applyCompletionBlock);
        LlvmApi.BuildStore(builder, EmitWindowsIocpBytesTransferred(state, waitContext, "step_tcp_receive_win_resume"), readCountSlot);
        EmitClearLeafTaskWait(state, taskPtr, "step_tcp_receive_win_clear_wait_after_resume");
        LlvmApi.BuildBr(builder, handleReadBlock);

        LlvmApi.PositionBuilderAtEnd(builder, readBlock);
        LlvmValueHandle syncReadCount = LlvmApi.BuildSExt(builder, EmitWindowsRecv(state, socket, GetStringBytesPointer(state, activeBuffer, "step_tcp_receive_win_bytes"), LlvmApi.BuildTrunc(builder, maxBytes, state.I32, "step_tcp_receive_win_max_i32"), "step_tcp_receive_win_sync_recv"), state.I64, "step_tcp_receive_win_sync_read_count");
        LlvmValueHandle readOk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, syncReadCount, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_receive_win_sync_ok");
        LlvmBasicBlockHandle syncPendingCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_sync_pending_check");
        LlvmBasicBlockHandle syncReadSuccessBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_sync_read_success");
        LlvmApi.BuildCondBr(builder, readOk, syncReadSuccessBlock, syncPendingCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, syncReadSuccessBlock);
        LlvmApi.BuildStore(builder, syncReadCount, readCountSlot);
        LlvmApi.BuildBr(builder, handleReadBlock);

        LlvmApi.PositionBuilderAtEnd(builder, handleReadBlock);
        LlvmValueHandle readCount = LlvmApi.BuildLoad2(builder, state.I64, readCountSlot, "step_tcp_receive_win_read_count_value");
        StoreMemory(state, activeBuffer, 0, readCount, "step_tcp_receive_win_store_len");
        LlvmValueHandle emptyRead = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, readCount, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_receive_win_empty");
        LlvmBasicBlockHandle successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_success");
        LlvmBasicBlockHandle validateUtf8Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_validate_utf8");
        LlvmApi.BuildCondBr(builder, emptyRead, successBlock, validateUtf8Block);

        LlvmApi.PositionBuilderAtEnd(builder, validateUtf8Block);
        LlvmValueHandle utf8Valid = EmitValidateUtf8(state, GetStringBytesPointer(state, activeBuffer, "step_tcp_receive_win_validate_bytes"), readCount, "step_tcp_receive_win_utf8");
        LlvmValueHandle validUtf8 = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, utf8Valid, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_receive_win_valid_utf8");
        LlvmBasicBlockHandle invalidUtf8Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_invalid_utf8");
        LlvmApi.BuildCondBr(builder, validUtf8, successBlock, invalidUtf8Block);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultOk(state, activeBuffer), "step_tcp_receive_win_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, invalidUtf8Block);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TcpInvalidUtf8Message)), "step_tcp_receive_win_invalid_utf8_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, syncPendingCheckBlock);
        LlvmValueHandle syncError = LlvmApi.BuildSExt(builder, EmitWindowsWsaGetLastError(state, "step_tcp_receive_win_sync_error"), state.I64, "step_tcp_receive_win_sync_error_i64");
        LlvmValueHandle syncWouldBlock = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, syncError, LlvmApi.ConstInt(state.I64, WindowsWsaErrorWouldBlock, 0), "step_tcp_receive_win_sync_would_block");
        LlvmBasicBlockHandle issueOverlappedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_issue_overlapped");
        LlvmApi.BuildCondBr(builder, syncWouldBlock, issueOverlappedBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, issueOverlappedBlock);
        EmitWindowsAssociateSocketWithIocp(state, socket, "step_tcp_receive_win_associate");
        LlvmValueHandle operationContext = EmitWindowsCreateIocpOperationContext(state, "step_tcp_receive_win_op_context");
        LlvmValueHandle bytesReceivedSlot = LlvmApi.BuildAlloca(builder, state.I32, "step_tcp_receive_win_bytes_received_slot");
        LlvmValueHandle overlappedResult = EmitWindowsIssueWsaRecv(state, socket, GetStringBytesPointer(state, activeBuffer, "step_tcp_receive_win_overlapped_bytes"), maxBytes, EmitWindowsIocpOverlappedPtr(state, operationContext, "step_tcp_receive_win_overlapped"), bytesReceivedSlot, "step_tcp_receive_win_issue_wsa_recv");
        LlvmValueHandle overlappedImmediate = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, overlappedResult, LlvmApi.ConstInt(state.I32, 0, 0), "step_tcp_receive_win_overlapped_immediate");
        LlvmBasicBlockHandle overlappedImmediateBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_overlapped_immediate");
        LlvmBasicBlockHandle overlappedErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_overlapped_error");
        LlvmApi.BuildCondBr(builder, overlappedImmediate, overlappedImmediateBlock, overlappedErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, overlappedImmediateBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildZExt(builder, LlvmApi.BuildLoad2(builder, state.I32, bytesReceivedSlot, "step_tcp_receive_win_immediate_bytes"), state.I64, "step_tcp_receive_win_immediate_bytes_i64"), readCountSlot);
        LlvmApi.BuildBr(builder, handleReadBlock);

        LlvmApi.PositionBuilderAtEnd(builder, overlappedErrorBlock);
        LlvmValueHandle overlappedError = LlvmApi.BuildSExt(builder, EmitWindowsWsaGetLastError(state, "step_tcp_receive_win_overlapped_error_code"), state.I64, "step_tcp_receive_win_overlapped_error_i64");
        LlvmValueHandle overlappedPending = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, overlappedError, LlvmApi.ConstInt(state.I64, WindowsErrorIoPending, 0), "step_tcp_receive_win_overlapped_pending");
        LlvmBasicBlockHandle storePendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_store_pending");
        LlvmApi.BuildCondBr(builder, overlappedPending, storePendingBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storePendingBlock);
        LlvmApi.BuildStore(builder, operationContext, pendingContextSlot);
        LlvmApi.BuildBr(builder, pendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingBlock);
        LlvmApi.BuildStore(builder, EmitPendingLeafTask(state, taskPtr, TaskStructLayout.WaitSocketRead, LlvmApi.BuildLoad2(builder, state.I64, pendingContextSlot, "step_tcp_receive_win_pending_context"), "step_tcp_receive_win_pending_store"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TcpReceiveFailedMessage)), "step_tcp_receive_win_fail_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, statusSlot, "step_tcp_receive_win_status");
    }

    private static LlvmValueHandle EmitStepHttpGetTask(LlvmCodegenState state, LlvmValueHandle taskPtr)
        => EmitStepHttpTask(state, taskPtr, hasBody: false, "step_http_get");

    private static LlvmValueHandle EmitStepHttpPostTask(LlvmCodegenState state, LlvmValueHandle taskPtr)
        => EmitStepHttpTask(state, taskPtr, hasBody: true, "step_http_post");

    private static void EmitTlsConnectTcpStage(
        LlvmCodegenState state,
        LlvmBasicBlockHandle stageBlock,
        LlvmValueHandle taskPtr,
        LlvmBasicBlockHandle finishBlock,
        LlvmValueHandle statusSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        const string prefix = "step_tls_connect_tcp";
        LlvmBasicBlockHandle createBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create");
        LlvmBasicBlockHandle stepBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_step");
        LlvmBasicBlockHandle pendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_pending");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");
        LlvmBasicBlockHandle successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_success");
        LlvmBasicBlockHandle errorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_error");

        LlvmApi.PositionBuilderAtEnd(builder, stageBlock);
        LlvmValueHandle awaitedTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_awaited_task");
        LlvmValueHandle hasAwaitedTask = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, awaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_awaited_task");
        LlvmApi.BuildCondBr(builder, hasAwaitedTask, stepBlock, createBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createBlock);
        LlvmValueHandle connectTask = EmitCreateLeafNetworkingTask(
            state,
            TaskStructLayout.StateTcpConnect,
            LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, prefix + "_host"),
            LoadMemory(state, taskPtr, TaskStructLayout.IoArg1, prefix + "_port"),
            prefix + "_child");
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, connectTask, prefix + "_store_child");
        LlvmApi.BuildBr(builder, stepBlock);

        LlvmApi.PositionBuilderAtEnd(builder, stepBlock);
        LlvmValueHandle stepTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_step_task");
        LlvmValueHandle childStatus = EmitNetworkingRuntimeCall(state, "ashes_step_task_until_wait_or_done", [stepTask], prefix + "_child_status");
        LlvmValueHandle childDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, childStatus, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_child_done");
        LlvmApi.BuildCondBr(builder, childDone, doneBlock, pendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingBlock);
        LlvmApi.BuildStore(builder, EmitPendingAwaitedHttpTask(state, taskPtr, stepTask, prefix + "_pending_status"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        EmitClearLeafTaskWait(state, taskPtr, prefix + "_clear_wait");
        LlvmValueHandle childResult = LoadMemory(state, stepTask, TaskStructLayout.ResultSlot, prefix + "_child_result");
        LlvmValueHandle childFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LoadMemory(state, childResult, 0, prefix + "_child_tag"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_child_failed");
        LlvmApi.BuildCondBr(builder, childFailed, errorBlock, successBlock);

        LlvmApi.PositionBuilderAtEnd(builder, errorBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_child_error");
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, childResult, prefix + "_error_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_child_success");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, LoadMemory(state, childResult, 8, prefix + "_socket_value"), prefix + "_store_socket");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData1, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_advance_stage");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static void EmitTlsConnectHandshakeStage(
        LlvmCodegenState state,
        LlvmBasicBlockHandle stageBlock,
        LlvmValueHandle taskPtr,
        LlvmBasicBlockHandle finishBlock,
        LlvmValueHandle statusSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        const string prefix = "step_tls_connect_handshake";
        LlvmBasicBlockHandle createBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create");
        LlvmBasicBlockHandle stepBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_step");
        LlvmBasicBlockHandle pendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_pending");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");
        LlvmBasicBlockHandle successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_success");
        LlvmBasicBlockHandle errorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_error");

        LlvmApi.PositionBuilderAtEnd(builder, stageBlock);
        LlvmValueHandle awaitedTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_awaited_task");
        LlvmValueHandle hasAwaitedTask = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, awaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_awaited_task");
        LlvmApi.BuildCondBr(builder, hasAwaitedTask, stepBlock, createBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createBlock);
        LlvmValueHandle handshakeTask = EmitCreateLeafNetworkingTask(
            state,
            TaskStructLayout.StateTlsHandshake,
            LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, prefix + "_socket"),
            LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, prefix + "_host"),
            prefix + "_child");
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, handshakeTask, prefix + "_store_child");
        LlvmApi.BuildBr(builder, stepBlock);

        LlvmApi.PositionBuilderAtEnd(builder, stepBlock);
        LlvmValueHandle stepTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_step_task");
        LlvmValueHandle childStatus = EmitNetworkingRuntimeCall(state, "ashes_step_task_until_wait_or_done", [stepTask], prefix + "_child_status");
        LlvmValueHandle childDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, childStatus, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_child_done");
        LlvmApi.BuildCondBr(builder, childDone, doneBlock, pendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingBlock);
        LlvmApi.BuildStore(builder, EmitPendingAwaitedHttpTask(state, taskPtr, stepTask, prefix + "_pending_status"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        EmitClearLeafTaskWait(state, taskPtr, prefix + "_clear_wait");
        LlvmValueHandle childResult = LoadMemory(state, stepTask, TaskStructLayout.ResultSlot, prefix + "_child_result");
        LlvmValueHandle childFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LoadMemory(state, childResult, 0, prefix + "_child_tag"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_child_failed");
        LlvmApi.BuildCondBr(builder, childFailed, errorBlock, successBlock);

        LlvmApi.PositionBuilderAtEnd(builder, errorBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_child_error");
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, childResult, prefix + "_error_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_child_success");
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, childResult, prefix + "_success_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static LlvmValueHandle EmitStepTlsTodoTask(LlvmCodegenState state, LlvmValueHandle taskPtr, string message, string prefix)
    {
        return EmitCompleteLeafTask(
            state,
            taskPtr,
            EmitResultError(state, EmitHeapStringLiteral(state, message)),
            prefix + "_complete");
    }

    private static LlvmValueHandle EmitStepHttpTask(LlvmCodegenState state, LlvmValueHandle taskPtr, bool hasBody, string prefix)
    {
        const long StageConnect = 0;
        const long StageTlsHandshake = 1;
        const long StageSend = 2;
        const long StageReceive = 3;
        const long StageCloseSuccess = 4;
        const long StageCloseError = 5;
        const long StageMask = 7;
        const long StageTlsFlag = 8;

        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_status_slot");
        LlvmValueHandle hostSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_host_slot");
        LlvmValueHandle pathSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_path_slot");
        LlvmValueHandle portSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_port_slot");
        LlvmValueHandle schemeSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_scheme_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), statusSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), hostSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), pathSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 80, 0), portSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), schemeSlot);

        LlvmBasicBlockHandle dispatchBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_dispatch");
        LlvmBasicBlockHandle connectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_connect");
        LlvmBasicBlockHandle handshakeCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_handshake_check");
        LlvmBasicBlockHandle handshakeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_handshake");
        LlvmBasicBlockHandle sendCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_send_check");
        LlvmBasicBlockHandle sendBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_send");
        LlvmBasicBlockHandle receiveCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_receive_check");
        LlvmBasicBlockHandle receiveBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_receive");
        LlvmBasicBlockHandle closeSuccessCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_close_success_check");
        LlvmBasicBlockHandle closeSuccessBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_close_success");
        LlvmBasicBlockHandle closeErrorCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_close_error_check");
        LlvmBasicBlockHandle closeErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_close_error");
        LlvmBasicBlockHandle invalidBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_invalid");
        LlvmBasicBlockHandle finishBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_finish");

        LlvmApi.BuildBr(builder, dispatchBlock);

        LlvmApi.PositionBuilderAtEnd(builder, dispatchBlock);
        LlvmValueHandle stageValue = LoadMemory(state, taskPtr, TaskStructLayout.WaitData1, prefix + "_stage_value");
        LlvmValueHandle stageKind = LlvmApi.BuildAnd(builder, stageValue, LlvmApi.ConstInt(state.I64, StageMask, 0), prefix + "_stage_kind");
        LlvmValueHandle isTlsStage = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            LlvmApi.BuildAnd(builder, stageValue, LlvmApi.ConstInt(state.I64, StageTlsFlag, 0), prefix + "_stage_tls_bits"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            prefix + "_is_tls_stage");
        LlvmValueHandle isConnectStage = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, stageKind, LlvmApi.ConstInt(state.I64, StageConnect, 0), prefix + "_is_connect_stage");
        LlvmApi.BuildCondBr(builder, isConnectStage, connectBlock, handshakeCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, handshakeCheckBlock);
        LlvmValueHandle isHandshakeStage = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, stageKind, LlvmApi.ConstInt(state.I64, StageTlsHandshake, 0), prefix + "_is_handshake_stage");
        LlvmApi.BuildCondBr(builder, isHandshakeStage, handshakeBlock, sendCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, sendCheckBlock);
        LlvmValueHandle isSendStage = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, stageKind, LlvmApi.ConstInt(state.I64, StageSend, 0), prefix + "_is_send_stage");
        LlvmApi.BuildCondBr(builder, isSendStage, sendBlock, receiveCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, receiveCheckBlock);
        LlvmValueHandle isReceiveStage = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, stageKind, LlvmApi.ConstInt(state.I64, StageReceive, 0), prefix + "_is_receive_stage");
        LlvmApi.BuildCondBr(builder, isReceiveStage, receiveBlock, closeSuccessCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeSuccessCheckBlock);
        LlvmValueHandle isCloseSuccessStage = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, stageKind, LlvmApi.ConstInt(state.I64, StageCloseSuccess, 0), prefix + "_is_close_success_stage");
        LlvmApi.BuildCondBr(builder, isCloseSuccessStage, closeSuccessBlock, closeErrorCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeErrorCheckBlock);
        LlvmValueHandle isCloseErrorStage = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, stageKind, LlvmApi.ConstInt(state.I64, StageCloseError, 0), prefix + "_is_close_error_stage");
        LlvmApi.BuildCondBr(builder, isCloseErrorStage, closeErrorBlock, invalidBlock);

        EmitHttpStageConnect(state, connectBlock, taskPtr, hostSlot, pathSlot, portSlot, schemeSlot, hasBody, prefix + "_connect_stage", finishBlock, statusSlot);
        EmitHttpStageTlsHandshake(state, handshakeBlock, taskPtr, hostSlot, pathSlot, portSlot, schemeSlot, prefix + "_handshake_stage", finishBlock, statusSlot);
        EmitHttpStageSend(state, sendBlock, taskPtr, hostSlot, pathSlot, portSlot, schemeSlot, isTlsStage, hasBody, prefix + "_send_stage", finishBlock, statusSlot);
        EmitHttpStageReceive(state, receiveBlock, taskPtr, isTlsStage, prefix + "_receive_stage", finishBlock, statusSlot);
        EmitHttpStageClose(state, closeSuccessBlock, taskPtr, isTlsStage, closeOnError: false, prefix + "_close_success_stage", finishBlock, statusSlot);
        EmitHttpStageClose(state, closeErrorBlock, taskPtr, isTlsStage, closeOnError: true, prefix + "_close_error_stage", finishBlock, statusSlot);

        LlvmApi.PositionBuilderAtEnd(builder, invalidBlock);
        LlvmApi.BuildStore(builder,
            EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, "unknown http task stage")), prefix + "_invalid_complete"),
            statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, statusSlot, prefix + "_status");
    }

    private static void EmitHttpStageConnect(
        LlvmCodegenState state,
        LlvmBasicBlockHandle stageBlock,
        LlvmValueHandle taskPtr,
        LlvmValueHandle hostSlot,
        LlvmValueHandle pathSlot,
        LlvmValueHandle portSlot,
        LlvmValueHandle schemeSlot,
        bool hasBody,
        string prefix,
        LlvmBasicBlockHandle finishBlock,
        LlvmValueHandle statusSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmBasicBlockHandle createBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create");
        LlvmBasicBlockHandle stepBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_step");
        LlvmBasicBlockHandle pendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_pending");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");
        LlvmBasicBlockHandle successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_success");
        LlvmBasicBlockHandle errorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_error");

        LlvmApi.PositionBuilderAtEnd(builder, stageBlock);
        LlvmValueHandle awaitedTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_awaited_task");
        LlvmValueHandle hasAwaitedTask = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, awaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_awaited_task");
        LlvmApi.BuildCondBr(builder, hasAwaitedTask, stepBlock, createBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createBlock);
        LlvmValueHandle parseError = EmitParseHttpUrl(state, LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, prefix + "_url"), hostSlot, pathSlot, portSlot, schemeSlot, prefix + "_parse_url");
        LlvmValueHandle parseFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, parseError, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_parse_failed");
        LlvmBasicBlockHandle parseFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_fail");
        LlvmBasicBlockHandle parseSuccessBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_success");
        LlvmApi.BuildCondBr(builder, parseFailed, parseFailBlock, parseSuccessBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseFailBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, parseError, prefix + "_parse_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseSuccessBlock);
        LlvmValueHandle isHttpsUrl = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildLoad2(builder, state.I64, schemeSlot, prefix + "_parsed_scheme_value"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_parsed_is_https");
        StoreMemory(state,
            taskPtr,
            TaskStructLayout.WaitData1,
            LlvmApi.BuildSelect(builder, isHttpsUrl, LlvmApi.ConstInt(state.I64, 8, 0), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_connect_stage_value"),
            prefix + "_store_connect_stage_value");
        LlvmValueHandle connectTask = EmitCreateLeafNetworkingTask(state,
            TaskStructLayout.StateTcpConnect,
            LlvmApi.BuildLoad2(builder, state.I64, hostSlot, prefix + "_host_value"),
            LlvmApi.BuildLoad2(builder, state.I64, portSlot, prefix + "_port_value"),
            prefix + "_child");
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, connectTask, prefix + "_store_child");
        LlvmApi.BuildBr(builder, stepBlock);

        LlvmApi.PositionBuilderAtEnd(builder, stepBlock);
        LlvmValueHandle stepTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_step_task");
        LlvmValueHandle childStatus = EmitNetworkingRuntimeCall(state, "ashes_step_task_until_wait_or_done", [stepTask], prefix + "_child_status");
        LlvmValueHandle childDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, childStatus, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_child_done");
        LlvmApi.BuildCondBr(builder, childDone, doneBlock, pendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingBlock);
        LlvmApi.BuildStore(builder, EmitPendingAwaitedHttpTask(state, taskPtr, stepTask, prefix + "_pending_status"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        EmitClearLeafTaskWait(state, taskPtr, prefix + "_clear_wait");
        LlvmValueHandle childResult = LoadMemory(state, stepTask, TaskStructLayout.ResultSlot, prefix + "_child_result");
        LlvmValueHandle childFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LoadMemory(state, childResult, 0, prefix + "_child_tag"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_child_failed");
        LlvmApi.BuildCondBr(builder, childFailed, errorBlock, successBlock);

        LlvmApi.PositionBuilderAtEnd(builder, errorBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, childResult, prefix + "_error_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        LlvmValueHandle isHttps = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            LlvmApi.BuildAnd(builder, LoadMemory(state, taskPtr, TaskStructLayout.WaitData1, prefix + "_current_stage_value"), LlvmApi.ConstInt(state.I64, 8, 0), prefix + "_current_stage_tls_bits"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            prefix + "_is_https");
        LlvmBasicBlockHandle handshakeStageBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_handshake_stage");
        LlvmBasicBlockHandle sendStageBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_send_stage");
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_child");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, LoadMemory(state, childResult, 8, prefix + "_socket_value"), prefix + "_socket_store");
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_response");
        LlvmApi.BuildCondBr(builder, isHttps, handshakeStageBlock, sendStageBlock);

        LlvmApi.PositionBuilderAtEnd(builder, handshakeStageBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData1, LlvmApi.ConstInt(state.I64, 9, 0), prefix + "_advance_tls_handshake_stage");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, sendStageBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData1, LlvmApi.ConstInt(state.I64, 2, 0), prefix + "_advance_send_stage");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static void EmitHttpStageTlsHandshake(
        LlvmCodegenState state,
        LlvmBasicBlockHandle stageBlock,
        LlvmValueHandle taskPtr,
        LlvmValueHandle hostSlot,
        LlvmValueHandle pathSlot,
        LlvmValueHandle portSlot,
        LlvmValueHandle schemeSlot,
        string prefix,
        LlvmBasicBlockHandle finishBlock,
        LlvmValueHandle statusSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmBasicBlockHandle createBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create");
        LlvmBasicBlockHandle stepBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_step");
        LlvmBasicBlockHandle pendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_pending");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");
        LlvmBasicBlockHandle successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_success");
        LlvmBasicBlockHandle errorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_error");

        LlvmApi.PositionBuilderAtEnd(builder, stageBlock);
        LlvmValueHandle awaitedTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_awaited_task");
        LlvmValueHandle hasAwaitedTask = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, awaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_awaited_task");
        LlvmApi.BuildCondBr(builder, hasAwaitedTask, stepBlock, createBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createBlock);
        LlvmValueHandle parseError = EmitParseHttpUrl(state, LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, prefix + "_url"), hostSlot, pathSlot, portSlot, schemeSlot, prefix + "_parse_url");
        LlvmValueHandle parseFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, parseError, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_parse_failed");
        LlvmBasicBlockHandle parseFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_fail");
        LlvmBasicBlockHandle parseSuccessBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_success");
        LlvmApi.BuildCondBr(builder, parseFailed, parseFailBlock, parseSuccessBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseFailBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, parseError, prefix + "_parse_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseSuccessBlock);
        LlvmValueHandle handshakeTask = EmitCreateLeafNetworkingTask(state,
            TaskStructLayout.StateTlsHandshake,
            LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, prefix + "_socket_value"),
            LlvmApi.BuildLoad2(builder, state.I64, hostSlot, prefix + "_host_value"),
            prefix + "_child");
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, handshakeTask, prefix + "_store_child");
        LlvmApi.BuildBr(builder, stepBlock);

        LlvmApi.PositionBuilderAtEnd(builder, stepBlock);
        LlvmValueHandle stepTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_step_task");
        LlvmValueHandle childStatus = EmitNetworkingRuntimeCall(state, "ashes_step_task_until_wait_or_done", [stepTask], prefix + "_child_status");
        LlvmValueHandle childDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, childStatus, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_child_done");
        LlvmApi.BuildCondBr(builder, childDone, doneBlock, pendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingBlock);
        LlvmApi.BuildStore(builder, EmitPendingAwaitedHttpTask(state, taskPtr, stepTask, prefix + "_pending_status"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        EmitClearLeafTaskWait(state, taskPtr, prefix + "_clear_wait");
        LlvmValueHandle childResult = LoadMemory(state, stepTask, TaskStructLayout.ResultSlot, prefix + "_child_result");
        LlvmValueHandle childFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LoadMemory(state, childResult, 0, prefix + "_child_tag"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_child_failed");
        LlvmApi.BuildCondBr(builder, childFailed, errorBlock, successBlock);

        LlvmApi.PositionBuilderAtEnd(builder, errorBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_child_error");
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, childResult, prefix + "_error_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_child");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, LoadMemory(state, childResult, 8, prefix + "_session_value"), prefix + "_session_store");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData1, LlvmApi.ConstInt(state.I64, 10, 0), prefix + "_advance_send_stage");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static void EmitHttpStageSend(
        LlvmCodegenState state,
        LlvmBasicBlockHandle stageBlock,
        LlvmValueHandle taskPtr,
        LlvmValueHandle hostSlot,
        LlvmValueHandle pathSlot,
        LlvmValueHandle portSlot,
        LlvmValueHandle schemeSlot,
        LlvmValueHandle isTlsStage,
        bool hasBody,
        string prefix,
        LlvmBasicBlockHandle finishBlock,
        LlvmValueHandle statusSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmBasicBlockHandle createBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create");
        LlvmBasicBlockHandle stepBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_step");
        LlvmBasicBlockHandle pendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_pending");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");
        LlvmBasicBlockHandle successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_success");
        LlvmBasicBlockHandle errorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_error");

        LlvmApi.PositionBuilderAtEnd(builder, stageBlock);
        LlvmValueHandle awaitedTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_awaited_task");
        LlvmValueHandle hasAwaitedTask = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, awaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_awaited_task");
        LlvmApi.BuildCondBr(builder, hasAwaitedTask, stepBlock, createBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createBlock);
        LlvmValueHandle parseError = EmitParseHttpUrl(state, LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, prefix + "_url"), hostSlot, pathSlot, portSlot, schemeSlot, prefix + "_parse_url");
        LlvmValueHandle parseFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, parseError, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_parse_failed");
        LlvmBasicBlockHandle parseFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_fail");
        LlvmBasicBlockHandle parseSuccessBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_success");
        LlvmApi.BuildCondBr(builder, parseFailed, parseFailBlock, parseSuccessBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseFailBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot, parseError, prefix + "_store_parse_error");
        StoreMemory(state, taskPtr,
            TaskStructLayout.WaitData1,
            LlvmApi.BuildSelect(builder, isTlsStage, LlvmApi.ConstInt(state.I64, 13, 0), LlvmApi.ConstInt(state.I64, 5, 0), prefix + "_close_error_stage"),
            prefix + "_advance_close_error");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseSuccessBlock);
        LlvmValueHandle requestRef = EmitHttpRequestString(
            state,
            LlvmApi.BuildLoad2(builder, state.I64, pathSlot, prefix + "_path_value"),
            LlvmApi.BuildLoad2(builder, state.I64, hostSlot, prefix + "_host_value"),
            LoadMemory(state, taskPtr, TaskStructLayout.IoArg1, prefix + "_body_value"),
            hasBody);
        LlvmBasicBlockHandle createTlsChildBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create_tls_child");
        LlvmBasicBlockHandle createTcpChildBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create_tcp_child");
        LlvmBasicBlockHandle createdChildBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_created_child");
        LlvmApi.BuildCondBr(builder, isTlsStage, createTlsChildBlock, createTcpChildBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createTlsChildBlock);
        StoreMemory(state,
            taskPtr,
            TaskStructLayout.AwaitedTask,
            EmitCreateLeafNetworkingTask(state,
                TaskStructLayout.StateTlsSend,
                LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, prefix + "_tls_socket_value"),
                requestRef,
                prefix + "_tls_child"),
            prefix + "_store_tls_child");
        LlvmApi.BuildBr(builder, createdChildBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createTcpChildBlock);
        StoreMemory(state,
            taskPtr,
            TaskStructLayout.AwaitedTask,
            EmitCreateLeafNetworkingTask(state,
                TaskStructLayout.StateTcpSend,
                LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, prefix + "_tcp_socket_value"),
                requestRef,
                prefix + "_tcp_child"),
            prefix + "_store_tcp_child");
        LlvmApi.BuildBr(builder, createdChildBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createdChildBlock);
        LlvmApi.BuildBr(builder, stepBlock);

        LlvmApi.PositionBuilderAtEnd(builder, stepBlock);
        LlvmValueHandle stepTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_step_task");
        LlvmValueHandle childStatus = EmitNetworkingRuntimeCall(state, "ashes_step_task_until_wait_or_done", [stepTask], prefix + "_child_status");
        LlvmValueHandle childDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, childStatus, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_child_done");
        LlvmApi.BuildCondBr(builder, childDone, doneBlock, pendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingBlock);
        LlvmApi.BuildStore(builder, EmitPendingAwaitedHttpTask(state, taskPtr, stepTask, prefix + "_pending_status"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        EmitClearLeafTaskWait(state, taskPtr, prefix + "_clear_wait");
        LlvmValueHandle childResult = LoadMemory(state, stepTask, TaskStructLayout.ResultSlot, prefix + "_child_result");
        LlvmValueHandle childFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LoadMemory(state, childResult, 0, prefix + "_child_tag"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_child_failed");
        LlvmApi.BuildCondBr(builder, childFailed, errorBlock, successBlock);

        LlvmApi.PositionBuilderAtEnd(builder, errorBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_child_error");
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot, childResult, prefix + "_store_error");
        StoreMemory(state, taskPtr,
            TaskStructLayout.WaitData1,
            LlvmApi.BuildSelect(builder, isTlsStage, LlvmApi.ConstInt(state.I64, 13, 0), LlvmApi.ConstInt(state.I64, 5, 0), prefix + "_error_stage"),
            prefix + "_advance_close_error");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_child");
        StoreMemory(state, taskPtr,
            TaskStructLayout.WaitData1,
            LlvmApi.BuildSelect(builder, isTlsStage, LlvmApi.ConstInt(state.I64, 11, 0), LlvmApi.ConstInt(state.I64, 3, 0), prefix + "_receive_stage"),
            prefix + "_advance_stage");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static void EmitHttpStageReceive(
        LlvmCodegenState state,
        LlvmBasicBlockHandle stageBlock,
        LlvmValueHandle taskPtr,
        LlvmValueHandle isTlsStage,
        string prefix,
        LlvmBasicBlockHandle finishBlock,
        LlvmValueHandle statusSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmBasicBlockHandle createBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create");
        LlvmBasicBlockHandle stepBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_step");
        LlvmBasicBlockHandle pendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_pending");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");
        LlvmBasicBlockHandle errorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_error");
        LlvmBasicBlockHandle inspectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_inspect");
        LlvmBasicBlockHandle chunkEmptyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_chunk_empty");
        LlvmBasicBlockHandle appendBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_append");

        LlvmApi.PositionBuilderAtEnd(builder, stageBlock);
        LlvmValueHandle awaitedTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_awaited_task");
        LlvmValueHandle hasAwaitedTask = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, awaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_awaited_task");
        LlvmApi.BuildCondBr(builder, hasAwaitedTask, stepBlock, createBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createBlock);
        LlvmBasicBlockHandle createTlsChildBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create_tls_child");
        LlvmBasicBlockHandle createTcpChildBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create_tcp_child");
        LlvmBasicBlockHandle createdChildBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_created_child");
        LlvmApi.BuildCondBr(builder, isTlsStage, createTlsChildBlock, createTcpChildBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createTlsChildBlock);
        StoreMemory(state,
            taskPtr,
            TaskStructLayout.AwaitedTask,
            EmitCreateLeafNetworkingTask(state,
                TaskStructLayout.StateTlsReceive,
                LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, prefix + "_tls_socket_value"),
                LlvmApi.ConstInt(state.I64, 65536, 0),
                prefix + "_tls_child"),
            prefix + "_store_tls_child");
        LlvmApi.BuildBr(builder, createdChildBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createTcpChildBlock);
        StoreMemory(state,
            taskPtr,
            TaskStructLayout.AwaitedTask,
            EmitCreateLeafNetworkingTask(state,
                TaskStructLayout.StateTcpReceive,
                LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, prefix + "_tcp_socket_value"),
                LlvmApi.ConstInt(state.I64, 65536, 0),
                prefix + "_tcp_child"),
            prefix + "_store_tcp_child");
        LlvmApi.BuildBr(builder, createdChildBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createdChildBlock);
        LlvmApi.BuildBr(builder, stepBlock);

        LlvmApi.PositionBuilderAtEnd(builder, stepBlock);
        LlvmValueHandle stepTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_step_task");
        LlvmValueHandle childStatus = EmitNetworkingRuntimeCall(state, "ashes_step_task_until_wait_or_done", [stepTask], prefix + "_child_status");
        LlvmValueHandle childDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, childStatus, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_child_done");
        LlvmApi.BuildCondBr(builder, childDone, doneBlock, pendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingBlock);
        LlvmApi.BuildStore(builder, EmitPendingAwaitedHttpTask(state, taskPtr, stepTask, prefix + "_pending_status"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        EmitClearLeafTaskWait(state, taskPtr, prefix + "_clear_wait");
        LlvmValueHandle childResult = LoadMemory(state, stepTask, TaskStructLayout.ResultSlot, prefix + "_child_result");
        LlvmValueHandle childFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LoadMemory(state, childResult, 0, prefix + "_child_tag"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_child_failed");
        LlvmApi.BuildCondBr(builder, childFailed, errorBlock, inspectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, errorBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_child_error");
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot, childResult, prefix + "_store_error");
        StoreMemory(state, taskPtr,
            TaskStructLayout.WaitData1,
            LlvmApi.BuildSelect(builder, isTlsStage, LlvmApi.ConstInt(state.I64, 13, 0), LlvmApi.ConstInt(state.I64, 5, 0), prefix + "_error_stage"),
            prefix + "_advance_close_error");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, inspectBlock);
        LlvmValueHandle chunkRef = LoadMemory(state, childResult, 8, prefix + "_chunk_ref");
        LlvmValueHandle chunkLen = LoadStringLength(state, chunkRef, prefix + "_chunk_len");
        LlvmValueHandle chunkEmpty = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, chunkLen, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_chunk_empty_bool");
        LlvmApi.BuildCondBr(builder, chunkEmpty, chunkEmptyBlock, appendBlock);

        LlvmApi.PositionBuilderAtEnd(builder, chunkEmptyBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_child_empty");
        StoreMemory(state, taskPtr,
            TaskStructLayout.WaitData1,
            LlvmApi.BuildSelect(builder, isTlsStage, LlvmApi.ConstInt(state.I64, 12, 0), LlvmApi.ConstInt(state.I64, 4, 0), prefix + "_close_success_stage"),
            prefix + "_advance_close_success");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, appendBlock);
        LlvmValueHandle currentResponse = LoadMemory(state, taskPtr, TaskStructLayout.ResultSlot, prefix + "_current_response");
        LlvmValueHandle hasResponse = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, currentResponse, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_response");
        LlvmBasicBlockHandle concatBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_concat_response");
        LlvmBasicBlockHandle firstChunkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_first_chunk");
        LlvmApi.BuildCondBr(builder, hasResponse, concatBlock, firstChunkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, concatBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot, EmitStringConcat(state, currentResponse, chunkRef), prefix + "_store_concat_response");
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_child_concat");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, firstChunkBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot, chunkRef, prefix + "_store_first_chunk");
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_child_first_chunk");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static void EmitHttpStageClose(
        LlvmCodegenState state,
        LlvmBasicBlockHandle stageBlock,
        LlvmValueHandle taskPtr,
        LlvmValueHandle isTlsStage,
        bool closeOnError,
        string prefix,
        LlvmBasicBlockHandle finishBlock,
        LlvmValueHandle statusSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmBasicBlockHandle createBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create");
        LlvmBasicBlockHandle stepBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_step");
        LlvmBasicBlockHandle pendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_pending");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");
        LlvmBasicBlockHandle closeSuccessBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_close_success");
        LlvmBasicBlockHandle closeFailureBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_close_failure");

        LlvmApi.PositionBuilderAtEnd(builder, stageBlock);
        LlvmValueHandle awaitedTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_awaited_task");
        LlvmValueHandle hasAwaitedTask = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, awaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_awaited_task");
        LlvmApi.BuildCondBr(builder, hasAwaitedTask, stepBlock, createBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createBlock);
        LlvmBasicBlockHandle createTlsChildBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create_tls_child");
        LlvmBasicBlockHandle createTcpChildBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create_tcp_child");
        LlvmBasicBlockHandle createdChildBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_created_child");
        LlvmApi.BuildCondBr(builder, isTlsStage, createTlsChildBlock, createTcpChildBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createTlsChildBlock);
        StoreMemory(state,
            taskPtr,
            TaskStructLayout.AwaitedTask,
            EmitCreateLeafNetworkingTask(state,
                TaskStructLayout.StateTlsClose,
                LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, prefix + "_tls_socket_value"),
                LlvmApi.ConstInt(state.I64, 0, 0),
                prefix + "_tls_child"),
            prefix + "_store_tls_child");
        LlvmApi.BuildBr(builder, createdChildBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createTcpChildBlock);
        StoreMemory(state,
            taskPtr,
            TaskStructLayout.AwaitedTask,
            EmitCreateLeafNetworkingTask(state,
                TaskStructLayout.StateTcpClose,
                LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, prefix + "_tcp_socket_value"),
                LlvmApi.ConstInt(state.I64, 0, 0),
                prefix + "_tcp_child"),
            prefix + "_store_tcp_child");
        LlvmApi.BuildBr(builder, createdChildBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createdChildBlock);
        LlvmApi.BuildBr(builder, stepBlock);

        LlvmApi.PositionBuilderAtEnd(builder, stepBlock);
        LlvmValueHandle stepTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_step_task");
        LlvmValueHandle childStatus = EmitNetworkingRuntimeCall(state, "ashes_step_task_until_wait_or_done", [stepTask], prefix + "_child_status");
        LlvmValueHandle childDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, childStatus, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_child_done");
        LlvmApi.BuildCondBr(builder, childDone, doneBlock, pendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingBlock);
        LlvmApi.BuildStore(builder, EmitPendingAwaitedHttpTask(state, taskPtr, stepTask, prefix + "_pending_status"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        EmitClearLeafTaskWait(state, taskPtr, prefix + "_clear_wait");
        LlvmValueHandle childResult = LoadMemory(state, stepTask, TaskStructLayout.ResultSlot, prefix + "_child_result");
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_child");
        LlvmValueHandle childFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LoadMemory(state, childResult, 0, prefix + "_child_tag"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_child_failed");
        LlvmApi.BuildCondBr(builder, childFailed, closeFailureBlock, closeSuccessBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeFailureBlock);
        LlvmValueHandle failureResult = closeOnError
            ? LoadMemory(state, taskPtr, TaskStructLayout.ResultSlot, prefix + "_stored_failure")
            : childResult;
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, failureResult, prefix + "_failure_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeSuccessBlock);
        LlvmValueHandle finalResult = closeOnError
            ? LoadMemory(state, taskPtr, TaskStructLayout.ResultSlot, prefix + "_error_result")
            : EmitParseHttpResponseResult(state, LoadMemory(state, taskPtr, TaskStructLayout.ResultSlot, prefix + "_response_ref"), prefix + "_parse_response");
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, finalResult, prefix + "_success_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static LlvmValueHandle EmitPendingAwaitedHttpTask(LlvmCodegenState state, LlvmValueHandle taskPtr, LlvmValueHandle awaitedTask, string prefix)
    {
        StoreMemory(state, taskPtr, TaskStructLayout.WaitKind, LoadMemory(state, awaitedTask, TaskStructLayout.WaitKind, prefix + "_wait_kind"), prefix + "_store_wait_kind");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitHandle, LoadMemory(state, awaitedTask, TaskStructLayout.WaitHandle, prefix + "_wait_handle"), prefix + "_store_wait_handle");
        return EmitLeafTaskPendingStatus(state);
    }

    private static LlvmValueHandle EmitParseHttpUrl(LlvmCodegenState state, LlvmValueHandle urlRef, LlvmValueHandle hostSlot, LlvmValueHandle pathSlot, LlvmValueHandle portSlot, LlvmValueHandle schemeSlot, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_result_slot");
        LlvmValueHandle portValueSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_port_value_slot");
        LlvmValueHandle portIndexSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_port_index_slot");
        LlvmValueHandle hostEndSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_host_end_slot");
        LlvmValueHandle schemeOffsetSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_scheme_offset_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 80, 0), portSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 80, 0), portValueSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), portIndexSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), hostEndSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), schemeOffsetSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), schemeSlot);

        LlvmValueHandle urlLen = LoadStringLength(state, urlRef, prefix + "_len");
        LlvmValueHandle urlBytes = GetStringBytesPointer(state, urlRef, prefix + "_bytes");
        LlvmValueHandle httpsPrefix = EmitHeapStringLiteral(state, "https://");
        LlvmValueHandle httpPrefix = EmitHeapStringLiteral(state, "http://");

        LlvmBasicBlockHandle httpsCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_https_check");
        LlvmBasicBlockHandle httpsBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_https");
        LlvmBasicBlockHandle httpCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_http_check");
        LlvmBasicBlockHandle httpBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_http");
        LlvmBasicBlockHandle findSlashBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_find_slash");
        LlvmBasicBlockHandle parsePortCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_port_check");
        LlvmBasicBlockHandle parsePortLoopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_port_loop");
        LlvmBasicBlockHandle parsePortBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_port_body");
        LlvmBasicBlockHandle buildPathBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_build_path");
        LlvmBasicBlockHandle malformedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_malformed");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");

        LlvmApi.BuildBr(builder, httpsCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, httpsCheckBlock);
        LlvmValueHandle isHttps = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, EmitStartsWith(state, urlRef, httpsPrefix, prefix + "_is_https"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_https_bool");
        LlvmApi.BuildCondBr(builder, isHttps, httpsBlock, httpCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, httpsBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), schemeSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 443, 0), portSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 443, 0), portValueSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 8, 0), schemeOffsetSlot);
        LlvmApi.BuildBr(builder, findSlashBlock);

        LlvmApi.PositionBuilderAtEnd(builder, httpCheckBlock);
        LlvmValueHandle isHttp = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, EmitStartsWith(state, urlRef, httpPrefix, prefix + "_is_http"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_http_bool");
        LlvmApi.BuildCondBr(builder, isHttp, httpBlock, malformedBlock);

        LlvmApi.PositionBuilderAtEnd(builder, httpBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), schemeSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 80, 0), portSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 80, 0), portValueSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 7, 0), schemeOffsetSlot);
        LlvmApi.BuildBr(builder, findSlashBlock);

        LlvmApi.PositionBuilderAtEnd(builder, findSlashBlock);
        LlvmValueHandle schemeOffset = LlvmApi.BuildLoad2(builder, state.I64, schemeOffsetSlot, prefix + "_scheme_offset");
        LlvmValueHandle isHttpsScheme = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildLoad2(builder, state.I64, schemeSlot, prefix + "_scheme_value"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_scheme_is_https");
        LlvmValueHandle slashIndexHttp = EmitFindByte(state, urlBytes, urlLen, 7, (byte)'/', prefix + "_slash_index_http");
        LlvmValueHandle slashIndexHttps = EmitFindByte(state, urlBytes, urlLen, 8, (byte)'/', prefix + "_slash_index_https");
        LlvmValueHandle slashIndex = LlvmApi.BuildSelect(builder, isHttpsScheme, slashIndexHttps, slashIndexHttp, prefix + "_slash_index");
        LlvmValueHandle hasSlash = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, slashIndex, LlvmApi.ConstInt(state.I64, 0, 1), prefix + "_has_slash");
        LlvmValueHandle hostSearchEnd = LlvmApi.BuildSelect(builder, hasSlash, slashIndex, urlLen, prefix + "_host_search_end");
        LlvmValueHandle colonIndexHttp = EmitFindByte(state, urlBytes, hostSearchEnd, 7, (byte)':', prefix + "_colon_index_http");
        LlvmValueHandle colonIndexHttps = EmitFindByte(state, urlBytes, hostSearchEnd, 8, (byte)':', prefix + "_colon_index_https");
        LlvmValueHandle colonIndex = LlvmApi.BuildSelect(builder, isHttpsScheme, colonIndexHttps, colonIndexHttp, prefix + "_colon_index");
        LlvmValueHandle hasColon = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, colonIndex, LlvmApi.ConstInt(state.I64, 0, 1), prefix + "_has_colon");
        LlvmValueHandle hostEnd = LlvmApi.BuildSelect(builder, hasColon, colonIndex, hostSearchEnd, prefix + "_host_end");
        LlvmValueHandle hostLen = LlvmApi.BuildSub(builder, hostEnd, schemeOffset, prefix + "_host_len");
        LlvmValueHandle missingHost = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, hostLen, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_missing_host");
        LlvmBasicBlockHandle storeHostBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_store_host");
        LlvmApi.BuildCondBr(builder, missingHost, malformedBlock, storeHostBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storeHostBlock);
        LlvmApi.BuildStore(builder, hostEnd, hostEndSlot);
        LlvmApi.BuildStore(builder, EmitHeapStringSliceFromBytesPointer(state, LlvmApi.BuildGEP2(builder, state.I8, urlBytes, [schemeOffset], prefix + "_host_ptr"), hostLen, prefix + "_host"), hostSlot);
        LlvmApi.BuildCondBr(builder, hasColon, parsePortCheckBlock, buildPathBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parsePortCheckBlock);
        LlvmValueHandle portStart = LlvmApi.BuildAdd(builder, colonIndex, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_port_start");
        LlvmValueHandle emptyPort = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, portStart, hostSearchEnd, prefix + "_empty_port");
        LlvmBasicBlockHandle parsePortInitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_port_init");
        LlvmApi.BuildCondBr(builder, emptyPort, malformedBlock, parsePortInitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parsePortInitBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), portValueSlot);
        LlvmApi.BuildStore(builder, portStart, portIndexSlot);
        LlvmApi.BuildBr(builder, parsePortLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parsePortLoopBlock);
        LlvmValueHandle portIndex = LlvmApi.BuildLoad2(builder, state.I64, portIndexSlot, prefix + "_port_index");
        LlvmValueHandle portDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, portIndex, hostSearchEnd, prefix + "_port_done");
        LlvmApi.BuildCondBr(builder, portDone, buildPathBlock, parsePortBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parsePortBodyBlock);
        LlvmValueHandle portByte = LoadByteAt(state, urlBytes, portIndex, prefix + "_port_byte");
        LlvmValueHandle digitValue = LlvmApi.BuildZExt(builder, portByte, state.I64, prefix + "_digit_value");
        LlvmValueHandle isDigit = BuildByteRangeCheck(state, digitValue, (byte)'0', (byte)'9', prefix + "_is_digit");
        LlvmBasicBlockHandle storeDigitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_store_digit");
        LlvmApi.BuildCondBr(builder, isDigit, storeDigitBlock, malformedBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storeDigitBlock);
        LlvmValueHandle currentPort = LlvmApi.BuildLoad2(builder, state.I64, portValueSlot, prefix + "_current_port");
        LlvmValueHandle parsedDigit = LlvmApi.BuildSub(builder, digitValue, LlvmApi.ConstInt(state.I64, (byte)'0', 0), prefix + "_parsed_digit");
        LlvmValueHandle nextPort = LlvmApi.BuildAdd(builder, LlvmApi.BuildMul(builder, currentPort, LlvmApi.ConstInt(state.I64, 10, 0), prefix + "_port_mul"), parsedDigit, prefix + "_next_port");
        LlvmValueHandle portTooLarge = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, nextPort, LlvmApi.ConstInt(state.I64, 65535, 0), prefix + "_port_too_large");
        LlvmBasicBlockHandle storePortBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_store_port");
        LlvmApi.BuildCondBr(builder, portTooLarge, malformedBlock, storePortBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storePortBlock);
        LlvmApi.BuildStore(builder, nextPort, portValueSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, portIndex, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_port_index_next"), portIndexSlot);
        LlvmApi.BuildBr(builder, parsePortLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, buildPathBlock);
        LlvmValueHandle finalPort = LlvmApi.BuildLoad2(builder, state.I64, portValueSlot, prefix + "_final_port");
        LlvmApi.BuildStore(builder, finalPort, portSlot);
        LlvmValueHandle pathRef = LlvmApi.BuildSelect(builder,
            hasSlash,
            EmitHeapStringSliceFromBytesPointer(state, LlvmApi.BuildGEP2(builder, state.I8, urlBytes, [slashIndex], prefix + "_path_ptr"), LlvmApi.BuildSub(builder, urlLen, slashIndex, prefix + "_path_len"), prefix + "_path"),
            EmitHeapStringLiteral(state, "/"),
            prefix + "_path_ref");
        LlvmApi.BuildStore(builder, pathRef, pathSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, malformedBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, HttpMalformedUrlMessage)), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, prefix + "_result");
    }

    private static LlvmValueHandle EmitParseHttpResponseResult(LlvmCodegenState state, LlvmValueHandle responseRef, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_result_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

        LlvmBasicBlockHandle prepareBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_prepare");
        LlvmBasicBlockHandle parseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse");
        LlvmBasicBlockHandle malformedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_malformed");
        LlvmBasicBlockHandle chunkedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_chunked");
        LlvmBasicBlockHandle successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_success");
        LlvmBasicBlockHandle errorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_error");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");

        LlvmApi.BuildBr(builder, prepareBlock);

        LlvmApi.PositionBuilderAtEnd(builder, prepareBlock);
        LlvmValueHandle effectiveResponse = LlvmApi.BuildSelect(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, responseRef, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_is_null_response"),
            EmitHeapStringLiteral(state, string.Empty),
            responseRef,
            prefix + "_effective_response");
        LlvmValueHandle responseLen = LoadStringLength(state, effectiveResponse, prefix + "_len");
        LlvmValueHandle tooShort = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, responseLen, LlvmApi.ConstInt(state.I64, 12, 0), prefix + "_too_short");
        LlvmApi.BuildCondBr(builder, tooShort, malformedBlock, parseBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseBlock);
        LlvmValueHandle responseBytes = GetStringBytesPointer(state, effectiveResponse, prefix + "_bytes");
        LlvmValueHandle separatorIndex = EmitFindByteSequence(state, responseBytes, responseLen, "\r\n\r\n"u8.ToArray(), prefix + "_separator");
        LlvmValueHandle hasSeparator = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, separatorIndex, LlvmApi.ConstInt(state.I64, 0, 1), prefix + "_has_separator");
        LlvmBasicBlockHandle parseStatusBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_status");
        LlvmApi.BuildCondBr(builder, hasSeparator, parseStatusBlock, malformedBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseStatusBlock);
        LlvmValueHandle statusSpaceIndex = EmitFindByte(state, responseBytes, separatorIndex, 0, (byte)' ', prefix + "_status_space");
        LlvmValueHandle hasStatusSpace = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, statusSpaceIndex, LlvmApi.ConstInt(state.I64, 0, 1), prefix + "_has_status_space");
        LlvmBasicBlockHandle parseDigitsBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_digits");
        LlvmApi.BuildCondBr(builder, hasStatusSpace, parseDigitsBlock, malformedBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseDigitsBlock);
        LlvmValueHandle statusEnd = LlvmApi.BuildAdd(builder, statusSpaceIndex, LlvmApi.ConstInt(state.I64, 3, 0), prefix + "_status_end");
        LlvmValueHandle digitsInRange = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, statusEnd, separatorIndex, prefix + "_digits_in_range");
        LlvmBasicBlockHandle detectChunkedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_detect_chunked");
        LlvmApi.BuildCondBr(builder, digitsInRange, detectChunkedBlock, malformedBlock);

        LlvmApi.PositionBuilderAtEnd(builder, detectChunkedBlock);
        LlvmValueHandle hundredsByte = LoadByteAt(state, responseBytes, LlvmApi.BuildAdd(builder, statusSpaceIndex, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_hundreds_index"), prefix + "_hundreds_byte");
        LlvmValueHandle tensByte = LoadByteAt(state, responseBytes, LlvmApi.BuildAdd(builder, statusSpaceIndex, LlvmApi.ConstInt(state.I64, 2, 0), prefix + "_tens_index"), prefix + "_tens_byte");
        LlvmValueHandle onesByte = LoadByteAt(state, responseBytes, LlvmApi.BuildAdd(builder, statusSpaceIndex, LlvmApi.ConstInt(state.I64, 3, 0), prefix + "_ones_index"), prefix + "_ones_byte");
        LlvmValueHandle digitsValid = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildAnd(builder,
                BuildByteRangeCheck(state, LlvmApi.BuildZExt(builder, hundredsByte, state.I64, prefix + "_hundreds_i64"), (byte)'0', (byte)'9', prefix + "_hundreds_range"),
                BuildByteRangeCheck(state, LlvmApi.BuildZExt(builder, tensByte, state.I64, prefix + "_tens_i64"), (byte)'0', (byte)'9', prefix + "_tens_range"),
                prefix + "_first_digits_valid"),
            BuildByteRangeCheck(state, LlvmApi.BuildZExt(builder, onesByte, state.I64, prefix + "_ones_i64"), (byte)'0', (byte)'9', prefix + "_ones_range"),
            prefix + "_digits_valid");
        LlvmBasicBlockHandle buildBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_build_body");
        LlvmApi.BuildCondBr(builder, digitsValid, buildBodyBlock, malformedBlock);

        LlvmApi.PositionBuilderAtEnd(builder, buildBodyBlock);
        LlvmValueHandle chunkedHeaderIndex = EmitFindByteSequence(state, responseBytes, separatorIndex, "Transfer-Encoding: chunked"u8.ToArray(), prefix + "_chunked_header");
        LlvmValueHandle hasChunkedHeader = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, chunkedHeaderIndex, LlvmApi.ConstInt(state.I64, 0, 1), prefix + "_has_chunked_header");
        LlvmApi.BuildCondBr(builder, hasChunkedHeader, chunkedBlock, successBlock);

        LlvmApi.PositionBuilderAtEnd(builder, chunkedBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, HttpUnsupportedTransferEncodingMessage)), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        LlvmValueHandle statusCode = LlvmApi.BuildAdd(builder,
            LlvmApi.BuildAdd(builder,
                LlvmApi.BuildMul(builder, LlvmApi.BuildSub(builder, LlvmApi.BuildZExt(builder, hundredsByte, state.I64, prefix + "_hundreds_code"), LlvmApi.ConstInt(state.I64, (byte)'0', 0), prefix + "_hundreds_digit"), LlvmApi.ConstInt(state.I64, 100, 0), prefix + "_hundreds_mul"),
                LlvmApi.BuildMul(builder, LlvmApi.BuildSub(builder, LlvmApi.BuildZExt(builder, tensByte, state.I64, prefix + "_tens_code"), LlvmApi.ConstInt(state.I64, (byte)'0', 0), prefix + "_tens_digit"), LlvmApi.ConstInt(state.I64, 10, 0), prefix + "_tens_mul"),
                prefix + "_status_sum"),
            LlvmApi.BuildSub(builder, LlvmApi.BuildZExt(builder, onesByte, state.I64, prefix + "_ones_code"), LlvmApi.ConstInt(state.I64, (byte)'0', 0), prefix + "_ones_digit"),
            prefix + "_status_code");
        LlvmValueHandle bodyStart = LlvmApi.BuildAdd(builder, separatorIndex, LlvmApi.ConstInt(state.I64, 4, 0), prefix + "_body_start");
        LlvmValueHandle bodyLength = LlvmApi.BuildSub(builder, responseLen, bodyStart, prefix + "_body_length");
        LlvmValueHandle bodyRef = EmitHeapStringSliceFromBytesPointer(state, LlvmApi.BuildGEP2(builder, state.I8, responseBytes, [bodyStart], prefix + "_body_ptr"), bodyLength, prefix + "_body");
        LlvmValueHandle isSuccess = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, statusCode, LlvmApi.ConstInt(state.I64, 200, 0), prefix + "_status_ge_200"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ule, statusCode, LlvmApi.ConstInt(state.I64, 299, 0), prefix + "_status_le_299"),
            prefix + "_status_is_success");
        LlvmApi.BuildCondBr(builder, isSuccess, errorBlock, errorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, errorBlock);
        LlvmValueHandle finalResult = LlvmApi.BuildSelect(builder,
            isSuccess,
            EmitResultOk(state, bodyRef),
            EmitResultError(state, EmitHttpStatusErrorString(state, statusCode, prefix + "_status_error")),
            prefix + "_final_result");
        LlvmApi.BuildStore(builder, finalResult, resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, malformedBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, HttpMalformedResponseMessage)), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, prefix + "_result");
    }

    private static LlvmValueHandle EmitTcpConnect(LlvmCodegenState state, LlvmValueHandle hostRef, LlvmValueHandle port)
    {
        return IsLinuxFlavor(state.Flavor)
            ? EmitLinuxTcpConnect(state, hostRef, port)
            : EmitWindowsTcpConnect(state, hostRef, port);
    }

    private static LlvmValueHandle EmitTcpSend(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle textRef)
    {
        return IsLinuxFlavor(state.Flavor)
            ? EmitLinuxTcpSend(state, socket, textRef)
            : EmitWindowsTcpSend(state, socket, textRef);
    }

    private static LlvmValueHandle EmitTcpReceive(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle maxBytes)
    {
        return IsLinuxFlavor(state.Flavor)
            ? EmitLinuxTcpReceive(state, socket, maxBytes)
            : EmitWindowsTcpReceive(state, socket, maxBytes);
    }

    private static LlvmValueHandle EmitTcpClose(LlvmCodegenState state, LlvmValueHandle socket)
    {
        return IsLinuxFlavor(state.Flavor)
            ? EmitLinuxTcpClose(state, socket)
            : EmitWindowsTcpClose(state, socket);
    }

    /// <summary>
    /// Emits a Drop operation for deterministic cleanup of owned values.
    /// Resource types (Socket) route to platform-specific close functions.
    /// Other owned types (String, List, ADTs, Closures) are no-ops in the
    /// current linear allocator — the IR records the drop for correctness;
    /// actual deallocation is handled by arena-based memory reclamation.
    /// Returns false because Drop does not terminate the current basic block.
    /// </summary>
    private static bool EmitDrop(LlvmCodegenState state, LlvmValueHandle value, string typeName)
    {
        switch (typeName)
        {
            case "Socket":
                // Drop a socket by routing cleanup through the networking ABI.
                // The result (Result[Unit, Str]) is discarded — Drop is
                // fire-and-forget; runtime errors during cleanup are ignored.
                EmitTcpCloseAbiCall(state, value);
                return false;

            case "TlsSocket":
                EmitTlsCloseAbiCall(state, value);
                return false;

            default:
                // Owned heap types (String, List, ADTs, Closures, etc.):
                // No-op per-object — bulk deallocation is handled by
                // RestoreArenaState which resets the heap cursor at scope
                // exit for copy-type scopes. The Drop instruction is kept
                // in IR for semantic correctness and resource cleanup routing.
                return false;
        }
    }

    private static LlvmValueHandle EmitHttpRequest(LlvmCodegenState state, LlvmValueHandle urlRef, LlvmValueHandle bodyRef, bool hasBody)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_result");
        LlvmValueHandle hostSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_host");
        LlvmValueHandle pathSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_path");
        LlvmValueHandle portSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_port");
        LlvmValueHandle responseSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_response");
        LlvmValueHandle socketSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_socket");
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_index");
        LlvmValueHandle hostStartSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_host_start");
        LlvmValueHandle hostEndSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_host_end");
        LlvmValueHandle pathStartSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_path_start");
        LlvmValueHandle pathLenSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_path_len");
        LlvmValueHandle portValueSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_port_value");
        LlvmValueHandle portDigitsSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_port_digits");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), hostSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), pathSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 80, 0), portSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), responseSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), socketSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), indexSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 7, 0), hostStartSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), hostEndSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), pathStartSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), pathLenSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 80, 0), portValueSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), portDigitsSlot);

        LlvmValueHandle urlLen = LoadStringLength(state, urlRef, "http_url_len");
        LlvmValueHandle urlBytes = GetStringBytesPointer(state, urlRef, "http_url_bytes");

        var httpsCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_https_check");
        var httpCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_http_check");
        var scanHostSetupBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_scan_host_setup");
        var scanHostBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_scan_host");
        var parsePortBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_port");
        var parsePortLoopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_port_loop");
        var parsePortInspectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_port_inspect");
        var havePathBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_have_path");
        var defaultPathBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_default_path");
        var connectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_connect");
        var sendBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_send");
        var recvLoopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_recv_loop");
        var recvInspectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_recv_inspect");
        var recvDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_recv_done");
        var parseResponseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_response");
        var httpsErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_https_error");
        var closeErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_close_error");
        var malformedResponseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_malformed_response");
        var chunkedErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_chunked_error");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_continue");

        LlvmApi.BuildBr(builder, httpsCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, httpsCheckBlock);
        LlvmValueHandle httpsPrefix = EmitHeapStringLiteral(state, "https://");
        LlvmValueHandle isHttps = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            EmitStartsWith(state, urlRef, httpsPrefix, "http_is_https"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "http_is_https_bool");
        LlvmApi.BuildCondBr(builder, isHttps, httpsErrorBlock, httpCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, httpCheckBlock);
        LlvmValueHandle httpPrefix = EmitHeapStringLiteral(state, "http://");
        LlvmValueHandle isHttp = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            EmitStartsWith(state, urlRef, httpPrefix, "http_is_http"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "http_is_http_bool");
        var malformedUrlBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_malformed_url");
        LlvmApi.BuildCondBr(builder, isHttp, scanHostSetupBlock, malformedUrlBlock);

        LlvmApi.PositionBuilderAtEnd(builder, malformedUrlBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, HttpMalformedUrlMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, scanHostSetupBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 7, 0), indexSlot);
        LlvmApi.BuildBr(builder, scanHostBlock);

        LlvmApi.PositionBuilderAtEnd(builder, scanHostBlock);
        LlvmValueHandle hostLoopIndex = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "http_host_loop_index");
        LlvmValueHandle hostLoopDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, hostLoopIndex, urlLen, "http_host_loop_done");
        var hostInspectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_host_inspect");
        LlvmApi.BuildCondBr(builder, hostLoopDone, defaultPathBlock, hostInspectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, hostInspectBlock);
        LlvmValueHandle hostByte = LoadByteAt(state, urlBytes, hostLoopIndex, "http_host_byte");
        LlvmValueHandle isColon = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, hostByte, LlvmApi.ConstInt(state.I8, (byte)':', 0), "http_host_is_colon");
        var hostCheckSlashBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_host_check_slash");
        LlvmApi.BuildCondBr(builder, isColon, parsePortBlock, hostCheckSlashBlock);

        LlvmApi.PositionBuilderAtEnd(builder, hostCheckSlashBlock);
        LlvmValueHandle isSlash = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, hostByte, LlvmApi.ConstInt(state.I8, (byte)'/', 0), "http_host_is_slash");
        var hostRejectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_host_reject");
        var hostAdvanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_host_advance");
        LlvmApi.BuildCondBr(builder, isSlash, defaultPathBlock, hostRejectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, hostRejectBlock);
        LlvmValueHandle isQuestion = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, hostByte, LlvmApi.ConstInt(state.I8, (byte)'?', 0), "http_host_is_question");
        var hostHashCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_host_hash_check");
        LlvmApi.BuildCondBr(builder, isQuestion, malformedUrlBlock, hostHashCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, hostHashCheckBlock);
        LlvmValueHandle isHash = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, hostByte, LlvmApi.ConstInt(state.I8, (byte)'#', 0), "http_host_is_hash");
        LlvmApi.BuildCondBr(builder, isHash, malformedUrlBlock, hostAdvanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, hostAdvanceBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, hostLoopIndex, LlvmApi.ConstInt(state.I64, 1, 0), "http_host_index_next"), indexSlot);
        LlvmApi.BuildBr(builder, scanHostBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parsePortBlock);
        LlvmValueHandle hostEnd = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "http_host_end");
        LlvmValueHandle hostLenValue = LlvmApi.BuildSub(builder, hostEnd, LlvmApi.ConstInt(state.I64, 7, 0), "http_host_len_before_port");
        LlvmValueHandle missingHost = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, hostLenValue, LlvmApi.ConstInt(state.I64, 0, 0), "http_missing_host");
        var parsePortSetupBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_port_setup");
        LlvmApi.BuildCondBr(builder, missingHost, malformedUrlBlock, parsePortSetupBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parsePortSetupBlock);
        LlvmApi.BuildStore(builder, hostEnd, hostEndSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), portValueSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), portDigitsSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, hostEnd, LlvmApi.ConstInt(state.I64, 1, 0), "http_port_index_start"), indexSlot);
        LlvmApi.BuildBr(builder, parsePortLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parsePortLoopBlock);
        LlvmValueHandle portIndex = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "http_port_index");
        LlvmValueHandle portDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, portIndex, urlLen, "http_port_done");
        LlvmApi.BuildCondBr(builder, portDone, defaultPathBlock, parsePortInspectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parsePortInspectBlock);
        LlvmValueHandle portByte = LoadByteAt(state, urlBytes, portIndex, "http_port_byte");
        LlvmValueHandle portIsSlash = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, portByte, LlvmApi.ConstInt(state.I8, (byte)'/', 0), "http_port_is_slash");
        var portDigitCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_port_digit_check");
        LlvmApi.BuildCondBr(builder, portIsSlash, defaultPathBlock, portDigitCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, portDigitCheckBlock);
        LlvmValueHandle portDigitValue = LlvmApi.BuildZExt(builder, portByte, state.I64, "http_port_digit_value");
        LlvmValueHandle portIsDigit = BuildByteRangeCheck(state, portDigitValue, (byte)'0', (byte)'9', "http_port_digit_range");
        var portAdvanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_port_advance");
        LlvmApi.BuildCondBr(builder, portIsDigit, portAdvanceBlock, malformedUrlBlock);

        LlvmApi.PositionBuilderAtEnd(builder, portAdvanceBlock);
        LlvmValueHandle currentPort = LlvmApi.BuildLoad2(builder, state.I64, portValueSlot, "http_port_current");
        LlvmValueHandle parsedDigit = LlvmApi.BuildSub(builder, portDigitValue, LlvmApi.ConstInt(state.I64, (byte)'0', 0), "http_parsed_digit");
        LlvmValueHandle nextPort = LlvmApi.BuildAdd(builder, LlvmApi.BuildMul(builder, currentPort, LlvmApi.ConstInt(state.I64, 10, 0), "http_port_mul"), parsedDigit, "http_port_next");
        LlvmValueHandle tooLargePort = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, nextPort, LlvmApi.ConstInt(state.I64, 65535, 0), "http_port_too_large");
        var storePortBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_store_port");
        LlvmApi.BuildCondBr(builder, tooLargePort, malformedUrlBlock, storePortBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storePortBlock);
        LlvmApi.BuildStore(builder, nextPort, portValueSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, LlvmApi.BuildLoad2(builder, state.I64, portDigitsSlot, "http_port_digits_value"), LlvmApi.ConstInt(state.I64, 1, 0), "http_port_digits_next"), portDigitsSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, portIndex, LlvmApi.ConstInt(state.I64, 1, 0), "http_port_index_next"), indexSlot);
        LlvmApi.BuildBr(builder, parsePortLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, defaultPathBlock);
        LlvmValueHandle finalHostEnd = LlvmApi.BuildLoad2(builder, state.I64, hostEndSlot, "http_final_host_end");
        LlvmValueHandle hostEndUnset = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, finalHostEnd, LlvmApi.ConstInt(state.I64, 0, 0), "http_host_end_unset");
        var setHostEndBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_set_host_end");
        var buildHostBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_build_host");
        LlvmApi.BuildCondBr(builder, hostEndUnset, setHostEndBlock, buildHostBlock);

        LlvmApi.PositionBuilderAtEnd(builder, setHostEndBlock);
        LlvmValueHandle currentIndex = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "http_current_index");
        LlvmValueHandle hostLenAtEnd = LlvmApi.BuildSub(builder, currentIndex, LlvmApi.ConstInt(state.I64, 7, 0), "http_host_len_at_end");
        LlvmValueHandle noHost = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, hostLenAtEnd, LlvmApi.ConstInt(state.I64, 0, 0), "http_no_host");
        LlvmApi.BuildCondBr(builder, noHost, malformedUrlBlock, buildHostBlock);

        LlvmApi.PositionBuilderAtEnd(builder, buildHostBlock);
        LlvmValueHandle actualHostEnd = LlvmApi.BuildSelect(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, LlvmApi.BuildLoad2(builder, state.I64, hostEndSlot, "http_host_end_existing"), LlvmApi.ConstInt(state.I64, 0, 0), "http_host_end_is_zero"),
            LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "http_host_end_from_index"),
            LlvmApi.BuildLoad2(builder, state.I64, hostEndSlot, "http_host_end_final"),
            "http_actual_host_end");
        LlvmValueHandle actualHostLen = LlvmApi.BuildSub(builder, actualHostEnd, LlvmApi.ConstInt(state.I64, 7, 0), "http_actual_host_len");
        LlvmValueHandle hostPtr = LlvmApi.BuildGEP2(builder, state.I8, urlBytes, [LlvmApi.ConstInt(state.I64, 7, 0)], "http_host_ptr");
        LlvmApi.BuildStore(builder, EmitHeapStringSliceFromBytesPointer(state, hostPtr, actualHostLen, "http_host"), hostSlot);
        LlvmValueHandle digitsCount = LlvmApi.BuildLoad2(builder, state.I64, portDigitsSlot, "http_digits_count");
        LlvmValueHandle hasPortDigits = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, digitsCount, LlvmApi.ConstInt(state.I64, 0, 0), "http_has_port_digits");
        var storeParsedPortBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_store_parsed_port");
        LlvmApi.BuildCondBr(builder, hasPortDigits, storeParsedPortBlock, havePathBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storeParsedPortBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildLoad2(builder, state.I64, portValueSlot, "http_port_value_final"), portSlot);
        LlvmApi.BuildBr(builder, havePathBlock);

        LlvmApi.PositionBuilderAtEnd(builder, havePathBlock);
        LlvmValueHandle pathIndex = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "http_path_index");
        LlvmValueHandle hasExplicitPath = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, pathIndex, urlLen, "http_has_explicit_path");
        var explicitPathBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_explicit_path");
        var defaultPathStoreBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_default_path_store");
        LlvmApi.BuildCondBr(builder, hasExplicitPath, explicitPathBlock, defaultPathStoreBlock);

        LlvmApi.PositionBuilderAtEnd(builder, explicitPathBlock);
        LlvmValueHandle explicitPathPtr = LlvmApi.BuildGEP2(builder, state.I8, urlBytes, [pathIndex], "http_explicit_path_ptr");
        LlvmValueHandle explicitPathLen = LlvmApi.BuildSub(builder, urlLen, pathIndex, "http_explicit_path_len");
        LlvmApi.BuildStore(builder, EmitHeapStringSliceFromBytesPointer(state, explicitPathPtr, explicitPathLen, "http_path"), pathSlot);
        LlvmApi.BuildBr(builder, connectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, defaultPathStoreBlock);
        LlvmApi.BuildStore(builder, EmitHeapStringLiteral(state, "/"), pathSlot);
        LlvmApi.BuildBr(builder, connectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectBlock);
        LlvmValueHandle connectResult = EmitTcpConnect(state, LlvmApi.BuildLoad2(builder, state.I64, hostSlot, "http_host_value"), LlvmApi.BuildLoad2(builder, state.I64, portSlot, "http_port_value"));
        LlvmValueHandle connectTag = LoadMemory(state, connectResult, 0, "http_connect_tag");
        LlvmValueHandle connectFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, connectTag, LlvmApi.ConstInt(state.I64, 0, 0), "http_connect_failed");
        var connectStoreBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_connect_store");
        LlvmApi.BuildCondBr(builder, connectFailed, connectStoreBlock, sendBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectStoreBlock);
        LlvmApi.BuildStore(builder, connectResult, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, sendBlock);
        LlvmValueHandle socketValue = LoadMemory(state, connectResult, 8, "http_socket_value");
        LlvmApi.BuildStore(builder, socketValue, socketSlot);
        LlvmValueHandle requestRef = EmitHttpRequestString(state, LlvmApi.BuildLoad2(builder, state.I64, pathSlot, "http_path_value"), LlvmApi.BuildLoad2(builder, state.I64, hostSlot, "http_host_header_value"), bodyRef, hasBody);
        LlvmValueHandle sendResult = EmitTcpSend(state, socketValue, requestRef);
        LlvmValueHandle sendTag = LoadMemory(state, sendResult, 0, "http_send_tag");
        LlvmValueHandle sendFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, sendTag, LlvmApi.ConstInt(state.I64, 0, 0), "http_send_failed");
        var sendErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_send_error");
        LlvmApi.BuildCondBr(builder, sendFailed, sendErrorBlock, recvLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, sendErrorBlock);
        EmitTcpClose(state, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "http_send_error_socket"));
        LlvmApi.BuildStore(builder, sendResult, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, recvLoopBlock);
        LlvmValueHandle recvResult = EmitTcpReceive(state, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "http_recv_socket"), LlvmApi.ConstInt(state.I64, 65536, 0));
        LlvmValueHandle recvTag = LoadMemory(state, recvResult, 0, "http_recv_tag");
        LlvmValueHandle recvFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, recvTag, LlvmApi.ConstInt(state.I64, 0, 0), "http_recv_failed");
        var recvErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_recv_error");
        LlvmApi.BuildCondBr(builder, recvFailed, recvErrorBlock, recvInspectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, recvErrorBlock);
        EmitTcpClose(state, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "http_recv_error_socket"));
        LlvmApi.BuildStore(builder, recvResult, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, recvInspectBlock);
        LlvmValueHandle chunkRef = LoadMemory(state, recvResult, 8, "http_chunk_ref");
        LlvmValueHandle chunkLen = LoadStringLength(state, chunkRef, "http_chunk_len");
        LlvmValueHandle chunkEmpty = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, chunkLen, LlvmApi.ConstInt(state.I64, 0, 0), "http_chunk_empty");
        var recvAppendBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_recv_append");
        LlvmApi.BuildCondBr(builder, chunkEmpty, recvDoneBlock, recvAppendBlock);

        LlvmApi.PositionBuilderAtEnd(builder, recvAppendBlock);
        LlvmValueHandle currentResponse = LlvmApi.BuildLoad2(builder, state.I64, responseSlot, "http_current_response");
        LlvmValueHandle hasResponse = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, currentResponse, LlvmApi.ConstInt(state.I64, 0, 0), "http_has_response");
        var concatResponseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_concat_response");
        var storeFirstChunkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_store_first_chunk");
        LlvmApi.BuildCondBr(builder, hasResponse, concatResponseBlock, storeFirstChunkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storeFirstChunkBlock);
        LlvmApi.BuildStore(builder, chunkRef, responseSlot);
        LlvmApi.BuildBr(builder, recvLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, concatResponseBlock);
        LlvmApi.BuildStore(builder, EmitStringConcat(state, currentResponse, chunkRef), responseSlot);
        LlvmApi.BuildBr(builder, recvLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, recvDoneBlock);
        LlvmValueHandle closeResult = EmitTcpClose(state, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "http_close_socket"));
        LlvmValueHandle closeTag = LoadMemory(state, closeResult, 0, "http_close_tag");
        LlvmValueHandle closeFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, closeTag, LlvmApi.ConstInt(state.I64, 0, 0), "http_close_failed");
        LlvmApi.BuildCondBr(builder, closeFailed, closeErrorBlock, parseResponseBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseResponseBlock);
        LlvmValueHandle responseRef = LlvmApi.BuildLoad2(builder, state.I64, responseSlot, "http_response_value");
        LlvmValueHandle emptyResponse = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, responseRef, LlvmApi.ConstInt(state.I64, 0, 0), "http_empty_response");
        var ensureEmptyResponseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_ensure_empty_response");
        var parseResponseContinueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_response_continue");
        LlvmApi.BuildCondBr(builder, emptyResponse, ensureEmptyResponseBlock, parseResponseContinueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, ensureEmptyResponseBlock);
        LlvmApi.BuildStore(builder, EmitHeapStringLiteral(state, string.Empty), responseSlot);
        LlvmApi.BuildBr(builder, parseResponseContinueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseResponseContinueBlock);
        LlvmValueHandle finalResponse = LlvmApi.BuildLoad2(builder, state.I64, responseSlot, "http_final_response");
        LlvmValueHandle responseLen = LoadStringLength(state, finalResponse, "http_response_len");
        LlvmValueHandle responseTooShort = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, responseLen, LlvmApi.ConstInt(state.I64, 12, 0), "http_response_too_short");
        var parseHeadersBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_headers");
        LlvmApi.BuildCondBr(builder, responseTooShort, malformedResponseBlock, parseHeadersBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseHeadersBlock);
        LlvmValueHandle responseBytes = GetStringBytesPointer(state, finalResponse, "http_response_bytes");
        LlvmValueHandle separatorIndex = EmitFindByteSequence(state, responseBytes, responseLen, "\r\n\r\n"u8.ToArray(), "http_separator");
        LlvmValueHandle hasSeparator = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, separatorIndex, LlvmApi.ConstInt(state.I64, 0, 1), "http_has_separator");
        var parseStatusBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_status");
        LlvmApi.BuildCondBr(builder, hasSeparator, parseStatusBlock, malformedResponseBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseStatusBlock);
        LlvmValueHandle headerLength = separatorIndex;
        LlvmValueHandle statusSpaceIndex = EmitFindByte(state, responseBytes, headerLength, 0, (byte)' ', "http_status_space");
        LlvmValueHandle hasStatusSpace = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, statusSpaceIndex, LlvmApi.ConstInt(state.I64, 0, 1), "http_has_status_space");
        var parseDigitsBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_digits");
        LlvmApi.BuildCondBr(builder, hasStatusSpace, parseDigitsBlock, malformedResponseBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseDigitsBlock);
        LlvmValueHandle statusEnd = LlvmApi.BuildAdd(builder, statusSpaceIndex, LlvmApi.ConstInt(state.I64, 3, 0), "http_status_end");
        LlvmValueHandle digitsInRange = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, statusEnd, headerLength, "http_status_digits_in_range");
        var parseDigitsContinueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_digits_continue");
        LlvmApi.BuildCondBr(builder, digitsInRange, parseDigitsContinueBlock, malformedResponseBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseDigitsContinueBlock);
        LlvmValueHandle hundredsByte = LoadByteAt(state, responseBytes, LlvmApi.BuildAdd(builder, statusSpaceIndex, LlvmApi.ConstInt(state.I64, 1, 0), "http_hundreds_idx"), "http_hundreds_byte");
        LlvmValueHandle tensByte = LoadByteAt(state, responseBytes, LlvmApi.BuildAdd(builder, statusSpaceIndex, LlvmApi.ConstInt(state.I64, 2, 0), "http_tens_idx"), "http_tens_byte");
        LlvmValueHandle onesByte = LoadByteAt(state, responseBytes, LlvmApi.BuildAdd(builder, statusSpaceIndex, LlvmApi.ConstInt(state.I64, 3, 0), "http_ones_idx"), "http_ones_byte");
        LlvmValueHandle digitsValid = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildAnd(builder,
                BuildByteRangeCheck(state, LlvmApi.BuildZExt(builder, hundredsByte, state.I64, "http_hundreds_i64"), (byte)'0', (byte)'9', "http_hundreds_range"),
                BuildByteRangeCheck(state, LlvmApi.BuildZExt(builder, tensByte, state.I64, "http_tens_i64"), (byte)'0', (byte)'9', "http_tens_range"),
                "http_digits_first"),
            BuildByteRangeCheck(state, LlvmApi.BuildZExt(builder, onesByte, state.I64, "http_ones_i64"), (byte)'0', (byte)'9', "http_ones_range"),
            "http_digits_valid");
        var detectChunkedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_detect_chunked");
        LlvmApi.BuildCondBr(builder, digitsValid, detectChunkedBlock, malformedResponseBlock);

        LlvmApi.PositionBuilderAtEnd(builder, detectChunkedBlock);
        LlvmValueHandle chunkedHeaderIndex = EmitFindByteSequence(state, responseBytes, headerLength, "Transfer-Encoding: chunked"u8.ToArray(), "http_chunked_header");
        LlvmValueHandle hasChunkedHeader = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, chunkedHeaderIndex, LlvmApi.ConstInt(state.I64, 0, 1), "http_has_chunked_header");
        var buildBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_build_body");
        LlvmApi.BuildCondBr(builder, hasChunkedHeader, chunkedErrorBlock, buildBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, buildBodyBlock);
        LlvmValueHandle statusCode = LlvmApi.BuildAdd(builder,
            LlvmApi.BuildAdd(builder,
                LlvmApi.BuildMul(builder, LlvmApi.BuildSub(builder, LlvmApi.BuildZExt(builder, hundredsByte, state.I64, "http_hundreds_code"), LlvmApi.ConstInt(state.I64, (byte)'0', 0), "http_hundreds_digit"), LlvmApi.ConstInt(state.I64, 100, 0), "http_hundreds_mul"),
                LlvmApi.BuildMul(builder, LlvmApi.BuildSub(builder, LlvmApi.BuildZExt(builder, tensByte, state.I64, "http_tens_code"), LlvmApi.ConstInt(state.I64, (byte)'0', 0), "http_tens_digit"), LlvmApi.ConstInt(state.I64, 10, 0), "http_tens_mul"),
                "http_status_prefix_sum"),
            LlvmApi.BuildSub(builder, LlvmApi.BuildZExt(builder, onesByte, state.I64, "http_ones_code"), LlvmApi.ConstInt(state.I64, (byte)'0', 0), "http_ones_digit"),
            "http_status_code");
        LlvmValueHandle bodyStart = LlvmApi.BuildAdd(builder, separatorIndex, LlvmApi.ConstInt(state.I64, 4, 0), "http_body_start");
        LlvmValueHandle bodyLength = LlvmApi.BuildSub(builder, responseLen, bodyStart, "http_body_len");
        LlvmValueHandle bodyBytes = LlvmApi.BuildGEP2(builder, state.I8, responseBytes, [bodyStart], "http_body_ptr");
        LlvmValueHandle bodyString = EmitHeapStringSliceFromBytesPointer(state, bodyBytes, bodyLength, "http_body");
        LlvmValueHandle statusOk = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, statusCode, LlvmApi.ConstInt(state.I64, 200, 0), "http_status_ge_200"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ule, statusCode, LlvmApi.ConstInt(state.I64, 299, 0), "http_status_le_299"),
            "http_status_ok");
        var statusOkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_status_ok_block");
        var statusErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_status_error_block");
        LlvmApi.BuildCondBr(builder, statusOk, statusOkBlock, statusErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, statusOkBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, bodyString), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, statusErrorBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHttpStatusErrorString(state, statusCode, "http_status_error")), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, httpsErrorBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, HttpHttpsNotSupportedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeErrorBlock);
        LlvmApi.BuildStore(builder, closeResult, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, malformedResponseBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, HttpMalformedResponseMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, chunkedErrorBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, HttpUnsupportedTransferEncodingMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "http_result_value");
    }

    private static LlvmValueHandle EmitLinuxTcpConnect(LlvmCodegenState state, LlvmValueHandle hostRef, LlvmValueHandle port)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_connect_result");
        LlvmValueHandle socketSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_connect_socket");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), socketSlot);
        LlvmValueHandle resolveResult = EmitResolveHostIpv4OrLocalhost(state, hostRef, "tcp_connect_resolve");
        LlvmValueHandle resolveTag = LoadMemory(state, resolveResult, 0, "tcp_connect_resolve_tag");
        LlvmValueHandle resolveFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, resolveTag, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_resolve_failed");
        var resolveErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_resolve_error");
        var validatePortBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_validate_port");
        var openSocketBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_open_socket");
        var connectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_connect");
        var connectFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_fail");
        var connectCloseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_close_socket");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_continue");
        LlvmApi.BuildCondBr(builder, resolveFailed, resolveErrorBlock, validatePortBlock);

        LlvmApi.PositionBuilderAtEnd(builder, resolveErrorBlock);
        LlvmApi.BuildStore(builder, resolveResult, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, validatePortBlock);
        LlvmValueHandle validPort = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, port, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_port_gt_zero"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, port, LlvmApi.ConstInt(state.I64, 65535, 0), "tcp_connect_port_le_max"),
            "tcp_connect_port_valid");
        LlvmApi.BuildCondBr(builder, validPort, openSocketBlock, connectFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, openSocketBlock);
        LlvmValueHandle socketValue = EmitLinuxSyscall(
            state,
            SyscallSocket,
            LlvmApi.ConstInt(state.I64, 2, 0),
            LlvmApi.ConstInt(state.I64, 1, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "tcp_connect_socket_call");
        LlvmApi.BuildStore(builder, socketValue, socketSlot);
        LlvmValueHandle socketFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, socketValue, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_socket_failed");
        LlvmApi.BuildCondBr(builder, socketFailed, connectFailBlock, connectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectBlock);
        LlvmTypeHandle sockaddrType = LlvmApi.ArrayType2(state.I8, 16);
        LlvmValueHandle sockaddrStorage = LlvmApi.BuildAlloca(builder, sockaddrType, "tcp_connect_sockaddr");
        LlvmValueHandle sockaddrBytes = GetArrayElementPointer(state, sockaddrType, sockaddrStorage, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_sockaddr_bytes");
        LlvmTypeHandle i16 = LlvmApi.Int16TypeInContext(state.Target.Context);
        LlvmTypeHandle i16Ptr = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmValueHandle sockaddrI64Ptr = LlvmApi.BuildBitCast(builder, sockaddrBytes, state.I64Ptr, "tcp_connect_sockaddr_i64");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), sockaddrI64Ptr);
        LlvmValueHandle sockaddrTailPtr = LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, [LlvmApi.ConstInt(state.I64, 8, 0)], "tcp_connect_sockaddr_tail");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.BuildBitCast(builder, sockaddrTailPtr, state.I64Ptr, "tcp_connect_sockaddr_tail_i64"));
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(i16, 2, 0), LlvmApi.BuildBitCast(builder, sockaddrBytes, i16Ptr, "tcp_connect_family_ptr"));
        LlvmValueHandle portPtr = LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, [LlvmApi.ConstInt(state.I64, 2, 0)], "tcp_connect_port_ptr_byte");
        LlvmApi.BuildStore(builder, LlvmApi.BuildTrunc(builder, EmitByteSwap16(state, port, "tcp_connect_port_network"), i16, "tcp_connect_port_i16"), LlvmApi.BuildBitCast(builder, portPtr, i16Ptr, "tcp_connect_port_ptr"));
        LlvmValueHandle addrPtr = LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, [LlvmApi.ConstInt(state.I64, 4, 0)], "tcp_connect_addr_ptr_byte");
        LlvmApi.BuildStore(builder, LlvmApi.BuildTrunc(builder, LoadMemory(state, resolveResult, 8, "tcp_connect_addr_value"), state.I32, "tcp_connect_addr_i32"), LlvmApi.BuildBitCast(builder, addrPtr, state.I32Ptr, "tcp_connect_addr_ptr"));
        LlvmValueHandle connectResult = EmitLinuxSyscall(
            state,
            SyscallConnect,
            LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "tcp_connect_socket_value"),
            LlvmApi.BuildPtrToInt(builder, sockaddrBytes, state.I64, "tcp_connect_sockaddr_ptr"),
            LlvmApi.ConstInt(state.I64, 16, 0),
            "tcp_connect_call");
        LlvmValueHandle connectFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, connectResult, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_failed_bool");
        var connectSuccessBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_success");
        LlvmApi.BuildCondBr(builder, connectFailed, connectCloseBlock, connectSuccessBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectCloseBlock);
        EmitLinuxSyscall(state, SyscallClose, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "tcp_connect_close_socket_value"), LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_close_call");
        LlvmApi.BuildBr(builder, connectFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectFailBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TcpConnectFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectSuccessBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "tcp_connect_success_socket")), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "tcp_connect_result_value");
    }

    private static LlvmValueHandle EmitWindowsTcpConnect(LlvmCodegenState state, LlvmValueHandle hostRef, LlvmValueHandle port)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_connect_win_result");
        LlvmValueHandle socketSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_connect_win_socket");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), socketSlot);
        LlvmValueHandle resolveResult = EmitResolveHostIpv4OrLocalhost(state, hostRef, "tcp_connect_win_resolve");
        LlvmValueHandle resolveTag = LoadMemory(state, resolveResult, 0, "tcp_connect_win_resolve_tag");
        LlvmValueHandle resolveFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, resolveTag, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_win_resolve_failed");
        var resolveErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_win_resolve_error");
        var validatePortBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_win_validate_port");
        var initWinsockBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_win_init_winsock");
        var openSocketBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_win_open_socket");
        var connectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_win_connect");
        var connectCloseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_win_close_socket");
        var connectFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_win_fail");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_win_continue");
        LlvmApi.BuildCondBr(builder, resolveFailed, resolveErrorBlock, validatePortBlock);

        LlvmApi.PositionBuilderAtEnd(builder, resolveErrorBlock);
        LlvmApi.BuildStore(builder, resolveResult, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, validatePortBlock);
        LlvmValueHandle validPort = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, port, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_win_port_gt_zero"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, port, LlvmApi.ConstInt(state.I64, 65535, 0), "tcp_connect_win_port_le_max"),
            "tcp_connect_win_port_valid");
        LlvmApi.BuildCondBr(builder, validPort, initWinsockBlock, connectFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, initWinsockBlock);
        LlvmTypeHandle wsadataType = LlvmApi.ArrayType2(state.I8, 512);
        LlvmValueHandle wsadata = LlvmApi.BuildAlloca(builder, wsadataType, "tcp_connect_win_wsadata");
        LlvmValueHandle winsockStarted = EmitWindowsWsaStartup(state, GetArrayElementPointer(state, wsadataType, wsadata, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_win_wsadata_ptr"), "tcp_connect_win_wsastartup");
        LlvmApi.BuildCondBr(builder, winsockStarted, openSocketBlock, connectFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, openSocketBlock);
        LlvmValueHandle socketValue = EmitWindowsSocket(state, 2, 1, 6, "tcp_connect_win_socket_call");
        LlvmApi.BuildStore(builder, socketValue, socketSlot);
        LlvmValueHandle socketFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, socketValue, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), "tcp_connect_win_socket_failed");
        LlvmApi.BuildCondBr(builder, socketFailed, connectFailBlock, connectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectBlock);
        LlvmTypeHandle sockaddrType = LlvmApi.ArrayType2(state.I8, 16);
        LlvmValueHandle sockaddrStorage = LlvmApi.BuildAlloca(builder, sockaddrType, "tcp_connect_win_sockaddr");
        LlvmValueHandle sockaddrBytes = GetArrayElementPointer(state, sockaddrType, sockaddrStorage, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_win_sockaddr_bytes");
        LlvmTypeHandle i16 = LlvmApi.Int16TypeInContext(state.Target.Context);
        LlvmTypeHandle i16Ptr = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmValueHandle sockaddrI64Ptr = LlvmApi.BuildBitCast(builder, sockaddrBytes, state.I64Ptr, "tcp_connect_win_sockaddr_i64");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), sockaddrI64Ptr);
        LlvmValueHandle sockaddrTailPtr = LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, [LlvmApi.ConstInt(state.I64, 8, 0)], "tcp_connect_win_sockaddr_tail");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.BuildBitCast(builder, sockaddrTailPtr, state.I64Ptr, "tcp_connect_win_sockaddr_tail_i64"));
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(i16, 2, 0), LlvmApi.BuildBitCast(builder, sockaddrBytes, i16Ptr, "tcp_connect_win_family_ptr"));
        LlvmValueHandle portPtr = LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, [LlvmApi.ConstInt(state.I64, 2, 0)], "tcp_connect_win_port_ptr_byte");
        LlvmApi.BuildStore(builder, LlvmApi.BuildTrunc(builder, EmitByteSwap16(state, port, "tcp_connect_win_port_network"), i16, "tcp_connect_win_port_i16"), LlvmApi.BuildBitCast(builder, portPtr, i16Ptr, "tcp_connect_win_port_ptr"));
        LlvmValueHandle addrPtr = LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, [LlvmApi.ConstInt(state.I64, 4, 0)], "tcp_connect_win_addr_ptr_byte");
        LlvmApi.BuildStore(builder, LlvmApi.BuildTrunc(builder, LoadMemory(state, resolveResult, 8, "tcp_connect_win_addr_value"), state.I32, "tcp_connect_win_addr_i32"), LlvmApi.BuildBitCast(builder, addrPtr, state.I32Ptr, "tcp_connect_win_addr_ptr"));
        LlvmValueHandle connectResult = EmitWindowsConnect(state, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "tcp_connect_win_socket_value"), sockaddrBytes, "tcp_connect_win_connect_call");
        var connectSuccessBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_win_success");
        LlvmApi.BuildCondBr(builder, connectResult, connectSuccessBlock, connectCloseBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectCloseBlock);
        EmitWindowsCloseSocket(state, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "tcp_connect_win_close_socket_value"), "tcp_connect_win_close_socket_call");
        LlvmApi.BuildBr(builder, connectFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectFailBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TcpConnectFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectSuccessBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "tcp_connect_win_success_socket")), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "tcp_connect_win_result_value");
    }

    private static LlvmValueHandle EmitLinuxTcpSend(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle textRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_send_result");
        LlvmValueHandle remainingSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_send_remaining");
        LlvmValueHandle cursorSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_send_cursor");
        LlvmValueHandle totalLen = LoadStringLength(state, textRef, "tcp_send_total_len");
        LlvmApi.BuildStore(builder, totalLen, remainingSlot);
        LlvmApi.BuildStore(builder, GetStringBytesAddress(state, textRef, "tcp_send_cursor_start"), cursorSlot);
        var loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_loop_check");
        var loopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_loop_body");
        var updateBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_update");
        var failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_fail");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_continue");
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle remaining = LlvmApi.BuildLoad2(builder, state.I64, remainingSlot, "tcp_send_remaining_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, remaining, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_send_done");
        var doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_done_block");
        LlvmApi.BuildCondBr(builder, done, doneBlock, loopBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBodyBlock);
        LlvmValueHandle sent = EmitLinuxSyscall(state, SyscallWrite, socket, LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, "tcp_send_cursor_value"), remaining, "tcp_send_syscall");
        LlvmValueHandle sendFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, sent, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_send_failed");
        LlvmApi.BuildCondBr(builder, sendFailed, failBlock, updateBlock);

        LlvmApi.PositionBuilderAtEnd(builder, updateBlock);
        LlvmValueHandle cursor = LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, "tcp_send_cursor_current");
        LlvmApi.BuildStore(builder, LlvmApi.BuildSub(builder, remaining, sent, "tcp_send_remaining_next"), remainingSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, cursor, sent, "tcp_send_cursor_next"), cursorSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, totalLen), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TcpSendFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "tcp_send_result_value");
    }

    private static LlvmValueHandle EmitWindowsTcpSend(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle textRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_send_win_result");
        LlvmValueHandle remainingSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_send_win_remaining");
        LlvmValueHandle cursorSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_send_win_cursor");
        LlvmValueHandle totalLen = LoadStringLength(state, textRef, "tcp_send_win_total_len");
        LlvmApi.BuildStore(builder, totalLen, remainingSlot);
        LlvmApi.BuildStore(builder, GetStringBytesAddress(state, textRef, "tcp_send_win_cursor_start"), cursorSlot);
        var loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_win_loop_check");
        var loopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_win_loop_body");
        var updateBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_win_update");
        var failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_win_fail");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_win_continue");
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle remaining = LlvmApi.BuildLoad2(builder, state.I64, remainingSlot, "tcp_send_win_remaining_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, remaining, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_send_win_done");
        var doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_win_done_block");
        LlvmApi.BuildCondBr(builder, done, doneBlock, loopBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBodyBlock);
        LlvmValueHandle chunk = LlvmApi.BuildSelect(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, remaining, LlvmApi.ConstInt(state.I64, int.MaxValue, 0), "tcp_send_win_chunk_gt"),
            LlvmApi.ConstInt(state.I64, int.MaxValue, 0),
            remaining,
            "tcp_send_win_chunk");
        LlvmValueHandle sentRaw = EmitWindowsSend(state, socket, LlvmApi.BuildIntToPtr(builder, LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, "tcp_send_win_cursor_value"), state.I8Ptr, "tcp_send_win_cursor_ptr"), LlvmApi.BuildTrunc(builder, chunk, state.I32, "tcp_send_win_chunk_i32"), "tcp_send_win_call");
        LlvmValueHandle sendFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, sentRaw, LlvmApi.ConstInt(state.I32, 0, 1), "tcp_send_win_failed");
        LlvmApi.BuildCondBr(builder, sendFailed, failBlock, updateBlock);

        LlvmApi.PositionBuilderAtEnd(builder, updateBlock);
        LlvmValueHandle sent = LlvmApi.BuildSExt(builder, sentRaw, state.I64, "tcp_send_win_sent");
        LlvmValueHandle cursor = LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, "tcp_send_win_cursor_current");
        LlvmApi.BuildStore(builder, LlvmApi.BuildSub(builder, remaining, sent, "tcp_send_win_remaining_next"), remainingSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, cursor, sent, "tcp_send_win_cursor_next"), cursorSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, totalLen), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TcpSendFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "tcp_send_win_result_value");
    }

    private static LlvmValueHandle EmitLinuxTcpReceive(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle maxBytes)
    {
        return EmitTcpReceiveCommon(state, socket, maxBytes, "tcp_receive", static (s, sock, bytesPtr, max, name) => EmitLinuxSyscall(s, SyscallRead, sock, LlvmApi.BuildPtrToInt(s.Target.Builder, bytesPtr, s.I64, name + "_ptr"), LlvmApi.BuildSExt(s.Target.Builder, max, s.I64, name + "_len"), name));
    }

    private static LlvmValueHandle EmitWindowsTcpReceive(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle maxBytes)
    {
        return EmitTcpReceiveCommon(state, socket, maxBytes, "tcp_receive_win", static (s, sock, bytesPtr, max, name) => LlvmApi.BuildSExt(s.Target.Builder, EmitWindowsRecv(s, sock, bytesPtr, max, name), s.I64, name + "_sext"));
    }

    private static LlvmValueHandle EmitTcpReceiveCommon(
        LlvmCodegenState state,
        LlvmValueHandle socket,
        LlvmValueHandle maxBytes,
        string prefix,
        Func<LlvmCodegenState, LlvmValueHandle, LlvmValueHandle, LlvmValueHandle, string, LlvmValueHandle> emitRead)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_result");
        var invalidMaxBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_invalid_max");
        var readBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_read");
        var handleReadBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_handle_read");
        var invalidUtf8Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_invalid_utf8");
        var failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_fail");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_continue");
        LlvmValueHandle positiveMax = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, maxBytes, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_positive_max");
        LlvmApi.BuildCondBr(builder, positiveMax, readBlock, invalidMaxBlock);

        LlvmApi.PositionBuilderAtEnd(builder, invalidMaxBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TcpInvalidMaxBytesMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, readBlock);
        LlvmValueHandle stringRef = EmitAllocDynamic(state, LlvmApi.BuildAdd(builder, maxBytes, LlvmApi.ConstInt(state.I64, 8, 0), prefix + "_size"));
        StoreMemory(state, stringRef, 0, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_len_init");
        LlvmValueHandle readCount = emitRead(state, socket, GetStringBytesPointer(state, stringRef, prefix + "_bytes"), LlvmApi.BuildTrunc(builder, maxBytes, state.I32, prefix + "_max_i32"), prefix + "_read_call");
        LlvmValueHandle readFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, readCount, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_read_failed");
        LlvmApi.BuildCondBr(builder, readFailed, failBlock, handleReadBlock);

        LlvmApi.PositionBuilderAtEnd(builder, handleReadBlock);
        StoreMemory(state, stringRef, 0, readCount, prefix + "_len_store");
        LlvmValueHandle isEmpty = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, readCount, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_is_empty");
        var successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_success");
        var validateBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_validate");
        LlvmApi.BuildCondBr(builder, isEmpty, successBlock, validateBlock);

        LlvmApi.PositionBuilderAtEnd(builder, validateBlock);
        LlvmValueHandle utf8Valid = EmitValidateUtf8(state, GetStringBytesPointer(state, stringRef, prefix + "_validate_bytes"), readCount, prefix + "_utf8");
        LlvmValueHandle valid = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, utf8Valid, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_utf8_valid");
        LlvmApi.BuildCondBr(builder, valid, successBlock, invalidUtf8Block);

        LlvmApi.PositionBuilderAtEnd(builder, invalidUtf8Block);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TcpInvalidUtf8Message)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, stringRef), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TcpReceiveFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, prefix + "_result_value");
    }

    private static LlvmValueHandle EmitLinuxTcpClose(LlvmCodegenState state, LlvmValueHandle socket)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle result = EmitLinuxSyscall(state, SyscallClose, socket, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), "tcp_close_call");
        LlvmValueHandle success = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, result, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_close_success");
        return LlvmApi.BuildSelect(builder, success, EmitResultOk(state, EmitUnitValue(state)), EmitResultError(state, EmitHeapStringLiteral(state, TcpCloseFailedMessage)), "tcp_close_result");
    }

    private static LlvmValueHandle EmitWindowsTcpClose(LlvmCodegenState state, LlvmValueHandle socket)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle closeResult = EmitWindowsCloseSocket(state, socket, "tcp_close_win_call");
        LlvmValueHandle success = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, closeResult, LlvmApi.ConstInt(state.I32, 0, 0), "tcp_close_win_success");
        return LlvmApi.BuildSelect(builder, success, EmitResultOk(state, EmitUnitValue(state)), EmitResultError(state, EmitHeapStringLiteral(state, TcpCloseFailedMessage)), "tcp_close_win_result");
    }

    private static LlvmValueHandle EmitResolveHostIpv4OrLocalhost(LlvmCodegenState state, LlvmValueHandle hostRef, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_result");
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_index");
        LlvmValueHandle partSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_part");
        LlvmValueHandle currentSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_current");
        LlvmValueHandle seenDigitSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_seen_digit");
        LlvmValueHandle addressSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_address");
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TcpResolveFailedMessage)), resultSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), indexSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), partSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), currentSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), seenDigitSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), addressSlot);

        LlvmValueHandle localhostEquals = EmitStringComparison(state, hostRef, EmitStackStringObject(state, "localhost"));
        LlvmValueHandle isLocalhost = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, localhostEquals, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_is_localhost");
        LlvmValueHandle hostLen = LoadStringLength(state, hostRef, prefix + "_host_len");
        LlvmValueHandle hostBytes = GetStringBytesPointer(state, hostRef, prefix + "_host_bytes");
        var localhostBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_localhost");
        var parseLoopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_loop");
        var parseInspectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_inspect");
        var digitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_digit");
        var dotBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_dot");
        var failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_fail");
        var finalizeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_finalize");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_continue");
        LlvmApi.BuildCondBr(builder, isLocalhost, localhostBlock, parseLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, localhostBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, LlvmApi.ConstInt(state.I64, 0x0100007FUL, 0)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseLoopBlock);
        LlvmValueHandle index = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, prefix + "_index_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, index, hostLen, prefix + "_done");
        LlvmApi.BuildCondBr(builder, done, finalizeBlock, parseInspectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseInspectBlock);
        LlvmValueHandle currentByte = LoadByteAt(state, hostBytes, index, prefix + "_current_byte");
        LlvmValueHandle currentByte64 = LlvmApi.BuildZExt(builder, currentByte, state.I64, prefix + "_current_byte_i64");
        LlvmValueHandle isDigit = BuildByteRangeCheck(state, currentByte64, (byte)'0', (byte)'9', prefix + "_digit_range");
        var dotCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_dot_check");
        LlvmApi.BuildCondBr(builder, isDigit, digitBlock, dotCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, dotCheckBlock);
        LlvmValueHandle isDot = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, currentByte, LlvmApi.ConstInt(state.I8, (byte)'.', 0), prefix + "_is_dot");
        LlvmApi.BuildCondBr(builder, isDot, dotBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, digitBlock);
        LlvmValueHandle currentValue = LlvmApi.BuildLoad2(builder, state.I64, currentSlot, prefix + "_current_value");
        LlvmValueHandle parsedDigit = LlvmApi.BuildSub(builder, currentByte64, LlvmApi.ConstInt(state.I64, (byte)'0', 0), prefix + "_parsed_digit");
        LlvmValueHandle nextValue = LlvmApi.BuildAdd(builder, LlvmApi.BuildMul(builder, currentValue, LlvmApi.ConstInt(state.I64, 10, 0), prefix + "_mul"), parsedDigit, prefix + "_next_value");
        LlvmValueHandle valueTooLarge = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, nextValue, LlvmApi.ConstInt(state.I64, 255, 0), prefix + "_value_too_large");
        var storeDigitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_store_digit");
        LlvmApi.BuildCondBr(builder, valueTooLarge, failBlock, storeDigitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storeDigitBlock);
        LlvmApi.BuildStore(builder, nextValue, currentSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), seenDigitSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, index, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_index_next"), indexSlot);
        LlvmApi.BuildBr(builder, parseLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, dotBlock);
        LlvmValueHandle seenDigit = LlvmApi.BuildLoad2(builder, state.I64, seenDigitSlot, prefix + "_seen_digit_value");
        LlvmValueHandle part = LlvmApi.BuildLoad2(builder, state.I64, partSlot, prefix + "_part_value");
        LlvmValueHandle dotValid = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, seenDigit, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_dot_seen_digit"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, part, LlvmApi.ConstInt(state.I64, 3, 0), prefix + "_dot_part_lt_three"),
            prefix + "_dot_valid");
        var storeDotBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_store_dot");
        LlvmApi.BuildCondBr(builder, dotValid, storeDotBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storeDotBlock);
        LlvmValueHandle addressValue = LlvmApi.BuildLoad2(builder, state.I64, addressSlot, prefix + "_address_value");
        LlvmValueHandle shiftedOctet = LlvmApi.BuildShl(builder, LlvmApi.BuildLoad2(builder, state.I64, currentSlot, prefix + "_octet_value"), LlvmApi.BuildMul(builder, part, LlvmApi.ConstInt(state.I64, 8, 0), prefix + "_octet_shift"), prefix + "_shifted_octet");
        LlvmApi.BuildStore(builder, LlvmApi.BuildOr(builder, addressValue, shiftedOctet, prefix + "_address_next"), addressSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, part, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_part_next"), partSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), currentSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), seenDigitSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, index, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_index_after_dot"), indexSlot);
        LlvmApi.BuildBr(builder, parseLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finalizeBlock);
        LlvmValueHandle finalSeenDigit = LlvmApi.BuildLoad2(builder, state.I64, seenDigitSlot, prefix + "_final_seen_digit");
        LlvmValueHandle finalPart = LlvmApi.BuildLoad2(builder, state.I64, partSlot, prefix + "_final_part");
        LlvmValueHandle finalValid = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, finalSeenDigit, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_final_seen_digit_ok"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, finalPart, LlvmApi.ConstInt(state.I64, 3, 0), prefix + "_final_part_eq_three"),
            prefix + "_final_valid");
        var storeFinalBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_store_final");
        LlvmApi.BuildCondBr(builder, finalValid, storeFinalBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storeFinalBlock);
        LlvmValueHandle finalAddress = LlvmApi.BuildOr(builder,
            LlvmApi.BuildLoad2(builder, state.I64, addressSlot, prefix + "_address_before_final"),
            LlvmApi.BuildShl(builder, LlvmApi.BuildLoad2(builder, state.I64, currentSlot, prefix + "_current_before_final"), LlvmApi.ConstInt(state.I64, 24, 0), prefix + "_final_shifted_octet"),
            prefix + "_final_address");
        LlvmApi.BuildStore(builder, EmitResultOk(state, finalAddress), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, prefix + "_result_value");
    }

    private static LlvmValueHandle EmitHttpRequestString(LlvmCodegenState state, LlvmValueHandle pathRef, LlvmValueHandle hostRef, LlvmValueHandle bodyRef, bool hasBody)
    {
        LlvmValueHandle request = EmitHeapStringLiteral(state, hasBody ? "POST " : "GET ");
        request = EmitStringConcat(state, request, pathRef);
        request = EmitStringConcat(state, request, EmitHeapStringLiteral(state, " HTTP/1.1\r\nHost: "));
        request = EmitStringConcat(state, request, hostRef);
        if (hasBody)
        {
            request = EmitStringConcat(state, request, EmitHeapStringLiteral(state, "\r\nContent-Length: "));
            request = EmitStringConcat(state, request, EmitNonNegativeIntToString(state, LoadStringLength(state, bodyRef, "http_body_length"), "http_body_length_string"));
        }

        request = EmitStringConcat(state, request, EmitHeapStringLiteral(state, "\r\nConnection: close\r\n\r\n"));
        if (hasBody)
        {
            request = EmitStringConcat(state, request, bodyRef);
        }

        return request;
    }

    private static LlvmValueHandle EmitHttpStatusErrorString(LlvmCodegenState state, LlvmValueHandle statusCode, string prefix)
    {
        return EmitStringConcat(state, EmitHeapStringLiteral(state, "HTTP "), EmitNonNegativeIntToString(state, statusCode, prefix + "_code"));
    }

    private static LlvmValueHandle EmitStartsWith(LlvmCodegenState state, LlvmValueHandle sourceRef, LlvmValueHandle prefixRef, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle sourceLen = LoadStringLength(state, sourceRef, prefix + "_source_len");
        LlvmValueHandle prefixLen = LoadStringLength(state, prefixRef, prefix + "_prefix_len");
        LlvmValueHandle enough = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, sourceLen, prefixLen, prefix + "_enough");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_result");
        var compareBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_compare");
        var falseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_false");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_continue");
        LlvmApi.BuildCondBr(builder, enough, compareBlock, falseBlock);

        LlvmApi.PositionBuilderAtEnd(builder, falseBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, compareBlock);
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_index");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), indexSlot);
        LlvmValueHandle sourceBytes = GetStringBytesPointer(state, sourceRef, prefix + "_source_bytes");
        LlvmValueHandle prefixBytes = GetStringBytesPointer(state, prefixRef, prefix + "_prefix_bytes");
        var loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_loop_check");
        var loopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_loop_body");
        var successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_success");
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle index = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, prefix + "_index_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, index, prefixLen, prefix + "_done");
        LlvmApi.BuildCondBr(builder, done, successBlock, loopBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBodyBlock);
        LlvmValueHandle sourceByte = LoadByteAt(state, sourceBytes, index, prefix + "_source_byte");
        LlvmValueHandle prefixByte = LoadByteAt(state, prefixBytes, index, prefix + "_prefix_byte");
        LlvmValueHandle matches = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, sourceByte, prefixByte, prefix + "_matches");
        var advanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_advance");
        LlvmApi.BuildCondBr(builder, matches, advanceBlock, falseBlock);

        LlvmApi.PositionBuilderAtEnd(builder, advanceBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, index, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_index_next"), indexSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, prefix + "_result_value");
    }

    private static LlvmValueHandle EmitFindByte(LlvmCodegenState state, LlvmValueHandle bytesPtr, LlvmValueHandle len, int startOffset, byte targetByte, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_result");
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_index");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), resultSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, (ulong)startOffset, 0), indexSlot);
        var loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_loop_check");
        var loopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_loop_body");
        var foundBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_found");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_continue");
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle index = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, prefix + "_index_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, index, len, prefix + "_done");
        LlvmApi.BuildCondBr(builder, done, continueBlock, loopBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBodyBlock);
        LlvmValueHandle currentByte = LoadByteAt(state, bytesPtr, index, prefix + "_byte");
        LlvmValueHandle matches = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, currentByte, LlvmApi.ConstInt(state.I8, targetByte, 0), prefix + "_matches");
        var advanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_advance");
        LlvmApi.BuildCondBr(builder, matches, foundBlock, advanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, foundBlock);
        LlvmApi.BuildStore(builder, index, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, advanceBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, index, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_index_next"), indexSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, prefix + "_result_value");
    }

    private static LlvmValueHandle EmitFindByteSequence(LlvmCodegenState state, LlvmValueHandle bytesPtr, LlvmValueHandle len, IReadOnlyList<byte> patternBytes, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_result");
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_index");
        LlvmValueHandle patternLen = LlvmApi.ConstInt(state.I64, (ulong)patternBytes.Count, 0);
        LlvmValueHandle patternPtr = EmitStackByteArray(state, patternBytes);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), resultSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), indexSlot);
        var loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_loop_check");
        var loopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_loop_body");
        var compareLoopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_compare_loop");
        var foundBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_found");
        var advanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_advance");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_continue");
        LlvmValueHandle compareIndexSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_compare_index");
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle index = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, prefix + "_index_value");
        LlvmValueHandle canMatch = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ule, LlvmApi.BuildAdd(builder, index, patternLen, prefix + "_candidate_end"), len, prefix + "_can_match");
        LlvmApi.BuildCondBr(builder, canMatch, loopBodyBlock, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBodyBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), compareIndexSlot);
        LlvmApi.BuildBr(builder, compareLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, compareLoopBlock);
        LlvmValueHandle compareIndex = LlvmApi.BuildLoad2(builder, state.I64, compareIndexSlot, prefix + "_compare_index_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, compareIndex, patternLen, prefix + "_compare_done");
        var compareBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_compare_body");
        LlvmApi.BuildCondBr(builder, done, foundBlock, compareBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, compareBodyBlock);
        LlvmValueHandle actualByte = LoadByteAt(state, bytesPtr, LlvmApi.BuildAdd(builder, index, compareIndex, prefix + "_actual_index"), prefix + "_actual_byte");
        LlvmValueHandle expectedByte = LoadByteAt(state, patternPtr, compareIndex, prefix + "_expected_byte");
        LlvmValueHandle matches = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, actualByte, expectedByte, prefix + "_compare_matches");
        var compareAdvanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_compare_advance");
        LlvmApi.BuildCondBr(builder, matches, compareAdvanceBlock, advanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, compareAdvanceBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, compareIndex, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_compare_index_next"), compareIndexSlot);
        LlvmApi.BuildBr(builder, compareLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, foundBlock);
        LlvmApi.BuildStore(builder, index, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, advanceBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, index, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_index_next"), indexSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, prefix + "_result_value");
    }

    private static LlvmValueHandle EmitByteSwap16(LlvmCodegenState state, LlvmValueHandle value, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle maskedLow = LlvmApi.BuildAnd(builder, value, LlvmApi.ConstInt(state.I64, 0xFF, 0), prefix + "_low");
        LlvmValueHandle maskedHigh = LlvmApi.BuildAnd(builder, LlvmApi.BuildLShr(builder, value, LlvmApi.ConstInt(state.I64, 8, 0), prefix + "_shr"), LlvmApi.ConstInt(state.I64, 0xFF, 0), prefix + "_high");
        return LlvmApi.BuildOr(builder, LlvmApi.BuildShl(builder, maskedLow, LlvmApi.ConstInt(state.I64, 8, 0), prefix + "_low_shifted"), maskedHigh, prefix + "_result");
    }

    private static void EmitEntryProgramArgsInitialization(LlvmCodegenState state)
    {
        LlvmApi.BuildStore(state.Target.Builder, LlvmApi.ConstInt(state.I64, 0, 0), state.ProgramArgsSlot);

        if (IsLinuxFlavor(state.Flavor))
        {
            EmitLinuxProgramArgsInitialization(state);
            return;
        }

        EmitWindowsProgramArgsInitialization(state);
    }

    private static void EmitLinuxProgramArgsInitialization(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle listSlot = LlvmApi.BuildAlloca(builder, state.I64, "program_args_list");
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, "program_args_index");
        LlvmValueHandle argPtrSlot = LlvmApi.BuildAlloca(builder, state.I64, "program_args_arg_ptr");
        LlvmValueHandle lenSlot = LlvmApi.BuildAlloca(builder, state.I64, "program_args_arg_len");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), listSlot);

        LlvmValueHandle stackPtr = state.EntryStackPointer;
        LlvmValueHandle argc = LoadMemory(state, stackPtr, 0, "program_args_argc");

        var initBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_init");
        var loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_loop_check");
        var lenCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_len_check");
        var lenBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_len_body");
        var buildNodeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_build_node");
        var doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_done");

        LlvmValueHandle hasArgs = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Sgt,
            argc,
            LlvmApi.ConstInt(state.I64, 1, 0),
            "program_args_has_args");
        LlvmApi.BuildCondBr(builder, hasArgs, initBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, initBlock);
        LlvmApi.BuildStore(builder,
            LlvmApi.BuildSub(builder, argc, LlvmApi.ConstInt(state.I64, 1, 0), "program_args_start_index"),
            indexSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle index = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "program_args_index_value");
        LlvmValueHandle shouldContinue = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Sgt,
            index,
            LlvmApi.ConstInt(state.I64, 0, 0),
            "program_args_continue");
        LlvmApi.BuildCondBr(builder, shouldContinue, lenCheckBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, lenCheckBlock);
        LlvmValueHandle argvEntryOffset = LlvmApi.BuildMul(builder, index, LlvmApi.ConstInt(state.I64, 8, 0), "program_args_argv_entry_offset");
        LlvmValueHandle argvEntryAddress = LlvmApi.BuildAdd(builder,
            stackPtr,
            LlvmApi.BuildAdd(builder, LlvmApi.ConstInt(state.I64, 8, 0), argvEntryOffset, "program_args_argv_offset"),
            "program_args_argv_entry_addr");
        LlvmValueHandle argPtr = LoadMemory(state, argvEntryAddress, 0, "program_args_argv_entry");
        LlvmApi.BuildStore(builder, argPtr, argPtrSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), lenSlot);

        var lenLoopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_len_loop_check");
        LlvmApi.BuildBr(builder, lenLoopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, lenLoopCheckBlock);
        LlvmValueHandle currentLen = LlvmApi.BuildLoad2(builder, state.I64, lenSlot, "program_args_current_len");
        LlvmValueHandle currentArgPtr = LlvmApi.BuildLoad2(builder, state.I64, argPtrSlot, "program_args_current_arg_ptr");
        LlvmValueHandle currentBytePtr = LlvmApi.BuildGEP2(builder,
            state.I8,
            LlvmApi.BuildIntToPtr(builder, currentArgPtr, state.I8Ptr, "program_args_arg_bytes"),
            [currentLen],
            "program_args_current_byte_ptr");
        LlvmValueHandle currentByte = LlvmApi.BuildLoad2(builder, state.I8, currentBytePtr, "program_args_current_byte");
        LlvmValueHandle reachedTerminator = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Eq,
            currentByte,
            LlvmApi.ConstInt(state.I8, 0, 0),
            "program_args_reached_terminator");
        LlvmApi.BuildCondBr(builder, reachedTerminator, buildNodeBlock, lenBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, lenBodyBlock);
        LlvmApi.BuildStore(builder,
            LlvmApi.BuildAdd(builder, currentLen, LlvmApi.ConstInt(state.I64, 1, 0), "program_args_next_len"),
            lenSlot);
        LlvmApi.BuildBr(builder, lenLoopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, buildNodeBlock);
        LlvmValueHandle argLen = LlvmApi.BuildLoad2(builder, state.I64, lenSlot, "program_args_arg_len_value");
        LlvmValueHandle stringRef = EmitAllocDynamic(
            state,
            LlvmApi.BuildAdd(builder, argLen, LlvmApi.ConstInt(state.I64, 8, 0), "program_args_string_bytes"));
        StoreMemory(state, stringRef, 0, argLen, "program_args_string_len");
        EmitCopyBytes(
            state,
            GetStringBytesPointer(state, stringRef, "program_args_string_dest"),
            LlvmApi.BuildIntToPtr(builder, LlvmApi.BuildLoad2(builder, state.I64, argPtrSlot, "program_args_copy_arg_ptr"), state.I8Ptr, "program_args_string_src"),
            argLen,
            "program_args_copy_bytes");
        LlvmValueHandle consRef = EmitAlloc(state, 16);
        StoreMemory(state, consRef, 0, stringRef, "program_args_cons_head");
        StoreMemory(state, consRef, 8, LlvmApi.BuildLoad2(builder, state.I64, listSlot, "program_args_prev_list"), "program_args_cons_tail");
        LlvmApi.BuildStore(builder, consRef, listSlot);
        LlvmApi.BuildStore(builder,
            LlvmApi.BuildSub(builder, LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "program_args_index_before_dec"), LlvmApi.ConstInt(state.I64, 1, 0), "program_args_index_dec"),
            indexSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildLoad2(builder, state.I64, listSlot, "program_args_final_list"), state.ProgramArgsSlot);
    }

    private static void EmitWindowsProgramArgsInitialization(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle i16 = LlvmApi.Int16TypeInContext(state.Target.Context);
        LlvmTypeHandle i16Ptr = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmTypeHandle i16PtrPtr = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmTypeHandle getCommandLineType = LlvmApi.FunctionType(i16Ptr, []);
        LlvmTypeHandle wideCharToMultiByteType = LlvmApi.FunctionType(state.I32, [state.I32, state.I32, i16Ptr, state.I32, state.I8Ptr, state.I32, state.I8Ptr, state.I8Ptr]);
        LlvmTypeHandle localFreeType = LlvmApi.FunctionType(state.I8Ptr, [state.I8Ptr]);
        LlvmTypeHandle commandLineToArgvType = LlvmApi.FunctionType(i16PtrPtr, [i16Ptr, state.I32Ptr]);

        LlvmValueHandle listSlot = LlvmApi.BuildAlloca(builder, state.I64, "program_args_list");
        LlvmValueHandle argcSlot = LlvmApi.BuildAlloca(builder, state.I32, "program_args_argc");
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I32, "program_args_index");
        LlvmValueHandle wideArgSlot = LlvmApi.BuildAlloca(builder, i16Ptr, "program_args_wide_arg");
        LlvmValueHandle wideLenSlot = LlvmApi.BuildAlloca(builder, state.I32, "program_args_wide_len");
        LlvmValueHandle stringRefSlot = LlvmApi.BuildAlloca(builder, state.I64, "program_args_string_ref");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), listSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), argcSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), stringRefSlot);

        LlvmValueHandle getCommandLinePtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsGetCommandLineImport,
            "get_command_line_ptr");
        LlvmValueHandle commandLinePtr = LlvmApi.BuildCall2(builder,
            getCommandLineType,
            getCommandLinePtr,
            Array.Empty<LlvmValueHandle>(),
            "command_line");

        LlvmValueHandle commandLineToArgvPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsCommandLineToArgvImport,
            "command_line_to_argv_ptr");
        LlvmValueHandle argvWide = LlvmApi.BuildCall2(builder,
            commandLineToArgvType,
            commandLineToArgvPtr,
            [commandLinePtr, argcSlot],
            "argv_wide");

        var haveArgvBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_have_argv");
        var maybeLoopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_maybe_loop");
        var loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_loop_check");
        var wideArgSetupBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_wide_arg_setup");
        var wideLenBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_wide_len_body");
        var wideLenIncBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_wide_len_inc");
        var convertArgBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_convert_arg");
        var createUtf8StringBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_create_utf8_string");
        var createEmptyStringBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_create_empty_string");
        var linkArgBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_link_arg");
        var freeArgvBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_free_argv");
        var doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_done");

        LlvmValueHandle hasArgv = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            LlvmApi.BuildPtrToInt(builder, argvWide, state.I64, "argv_wide_i64"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "program_args_has_argv");
        LlvmApi.BuildCondBr(builder, hasArgv, haveArgvBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, haveArgvBlock);
        LlvmValueHandle argc = LlvmApi.BuildLoad2(builder, state.I32, argcSlot, "program_args_argc_value");
        LlvmValueHandle hasUserArgs = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Sgt,
            argc,
            LlvmApi.ConstInt(state.I32, 1, 0),
            "program_args_has_user_args");
        LlvmApi.BuildCondBr(builder, hasUserArgs, maybeLoopBlock, freeArgvBlock);

        LlvmApi.PositionBuilderAtEnd(builder, maybeLoopBlock);
        LlvmApi.BuildStore(builder,
            LlvmApi.BuildSub(builder, argc, LlvmApi.ConstInt(state.I32, 1, 0), "program_args_start_index"),
            indexSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle index = LlvmApi.BuildLoad2(builder, state.I32, indexSlot, "program_args_index_value");
        LlvmValueHandle shouldContinue = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Sgt,
            index,
            LlvmApi.ConstInt(state.I32, 0, 0),
            "program_args_continue");
        LlvmApi.BuildCondBr(builder, shouldContinue, wideArgSetupBlock, freeArgvBlock);

        LlvmApi.PositionBuilderAtEnd(builder, wideArgSetupBlock);
        LlvmValueHandle wideArgPtrPtr = LlvmApi.BuildGEP2(builder,
            i16Ptr,
            argvWide,
            [LlvmApi.BuildSExt(builder, index, state.I64, "program_args_index_i64")],
            "program_args_wide_arg_ptr");
        LlvmValueHandle wideArgPtr = LlvmApi.BuildLoad2(builder, i16Ptr, wideArgPtrPtr, "program_args_wide_arg_value");
        LlvmApi.BuildStore(builder, wideArgPtr, wideArgSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), wideLenSlot);
        LlvmApi.BuildBr(builder, wideLenBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, wideLenBodyBlock);
        LlvmValueHandle wideLen = LlvmApi.BuildLoad2(builder, state.I32, wideLenSlot, "program_args_wide_len_value");
        LlvmValueHandle wideCharPtr = LlvmApi.BuildGEP2(builder,
            i16,
            LlvmApi.BuildLoad2(builder, i16Ptr, wideArgSlot, "program_args_wide_arg_current"),
            [LlvmApi.BuildSExt(builder, wideLen, state.I64, "program_args_wide_len_i64")],
            "program_args_wide_char_ptr");
        LlvmValueHandle wideChar = LlvmApi.BuildLoad2(builder, i16, wideCharPtr, "program_args_wide_char");
        LlvmValueHandle atTerminator = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Eq,
            wideChar,
            LlvmApi.ConstInt(i16, 0, 0),
            "program_args_at_wide_terminator");
        LlvmApi.BuildCondBr(builder, atTerminator, convertArgBlock, wideLenIncBlock);

        LlvmApi.PositionBuilderAtEnd(builder, wideLenIncBlock);
        LlvmApi.BuildStore(builder,
            LlvmApi.BuildAdd(builder, LlvmApi.BuildLoad2(builder, state.I32, wideLenSlot, "program_args_wide_len_before_inc"), LlvmApi.ConstInt(state.I32, 1, 0), "program_args_wide_len_inc"),
            wideLenSlot);
        LlvmApi.BuildBr(builder, wideLenBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, convertArgBlock);
        LlvmValueHandle wideArg = LlvmApi.BuildLoad2(builder, i16Ptr, wideArgSlot, "program_args_wide_arg_for_convert");
        LlvmValueHandle wcharCount = LlvmApi.BuildLoad2(builder, state.I32, wideLenSlot, "program_args_wchar_count");
        LlvmValueHandle wideCharToMultiBytePtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsWideCharToMultiByteImport,
            "wide_char_to_multi_byte_ptr");
        LlvmValueHandle nullI8Ptr = LlvmApi.BuildIntToPtr(builder, LlvmApi.ConstInt(state.I64, 0, 0), state.I8Ptr, "null_i8_ptr");
        LlvmValueHandle byteCount = LlvmApi.BuildCall2(builder,
            wideCharToMultiByteType,
            wideCharToMultiBytePtr,
            [
                LlvmApi.ConstInt(state.I32, Utf8CodePage, 0),
                LlvmApi.ConstInt(state.I32, 0, 0),
                wideArg,
                wcharCount,
                nullI8Ptr,
                LlvmApi.ConstInt(state.I32, 0, 0),
                nullI8Ptr,
                nullI8Ptr
            ],
            "program_args_byte_count");
        LlvmValueHandle hasBytes = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Sgt,
            byteCount,
            LlvmApi.ConstInt(state.I32, 0, 0),
            "program_args_has_bytes");
        LlvmApi.BuildCondBr(builder, hasBytes, createUtf8StringBlock, createEmptyStringBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createUtf8StringBlock);
        LlvmValueHandle stringRef = EmitAllocDynamic(
            state,
            LlvmApi.BuildAdd(builder, LlvmApi.BuildZExt(builder, byteCount, state.I64, "program_args_byte_count_i64"), LlvmApi.ConstInt(state.I64, 8, 0), "program_args_string_bytes"));
        StoreMemory(state, stringRef, 0, LlvmApi.BuildZExt(builder, byteCount, state.I64, "program_args_string_len"), "program_args_string_len");
        LlvmValueHandle stringDest = GetStringBytesPointer(state, stringRef, "program_args_string_dest");
        LlvmApi.BuildCall2(builder,
            wideCharToMultiByteType,
            wideCharToMultiBytePtr,
            [
                LlvmApi.ConstInt(state.I32, Utf8CodePage, 0),
                LlvmApi.ConstInt(state.I32, 0, 0),
                wideArg,
                wcharCount,
                stringDest,
                byteCount,
                nullI8Ptr,
                nullI8Ptr
            ],
            "program_args_copy_utf8");
        LlvmApi.BuildStore(builder, stringRef, stringRefSlot);
        LlvmApi.BuildBr(builder, linkArgBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createEmptyStringBlock);
        LlvmValueHandle emptyStringRef = EmitAlloc(state, 8);
        StoreMemory(state, emptyStringRef, 0, LlvmApi.ConstInt(state.I64, 0, 0), "program_args_empty_string_len");
        LlvmApi.BuildStore(builder, emptyStringRef, stringRefSlot);
        LlvmApi.BuildBr(builder, linkArgBlock);

        LlvmApi.PositionBuilderAtEnd(builder, linkArgBlock);
        LlvmValueHandle consRef = EmitAlloc(state, 16);
        StoreMemory(state, consRef, 0, LlvmApi.BuildLoad2(builder, state.I64, stringRefSlot, "program_args_string_ref_value"), "program_args_cons_head");
        StoreMemory(state, consRef, 8, LlvmApi.BuildLoad2(builder, state.I64, listSlot, "program_args_prev_list"), "program_args_cons_tail");
        LlvmApi.BuildStore(builder, consRef, listSlot);
        LlvmApi.BuildStore(builder,
            LlvmApi.BuildSub(builder, LlvmApi.BuildLoad2(builder, state.I32, indexSlot, "program_args_index_before_dec"), LlvmApi.ConstInt(state.I32, 1, 0), "program_args_index_dec"),
            indexSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, freeArgvBlock);
        LlvmValueHandle localFreePtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsLocalFreeImport,
            "local_free_ptr");
        LlvmApi.BuildCall2(builder,
            localFreeType,
            localFreePtr,
            [LlvmApi.BuildBitCast(builder, argvWide, state.I8Ptr, "argv_wide_hlocal")],
            "program_args_local_free");
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildLoad2(builder, state.I64, listSlot, "program_args_final_list"), state.ProgramArgsSlot);
    }
}
