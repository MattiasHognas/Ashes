using Ashes.Semantics;
using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{

    private static LlvmValueHandle EmitTextUncons(LlvmCodegenState state, LlvmValueHandle textRef, bool runtimeManaged)
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
        LlvmApi.BuildStore(builder, EmitAllocAdt(state, 0, 0, runtimeManaged), resultSlot);
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
        // Views into the input string (no copy): the backing outlives these (it's the string being
        // unconsed), and any persisted value materializes via copy-out. This is what makes a
        // char-by-char uncons loop O(T) instead of O(T^2).
        LlvmValueHandle headRef = EmitStringView(state, bytesPtr, headLen, "text_uncons_head");
        LlvmValueHandle tailRef = EmitStringView(state, tailPtr, tailLen, "text_uncons_tail");
        LlvmValueHandle tupleRef = EmitAlloc(state, 16);
        StoreMemory(state, tupleRef, 0, headRef, "text_uncons_tuple_head");
        StoreMemory(state, tupleRef, 8, tailRef, "text_uncons_tuple_tail");
        LlvmValueHandle someRef = EmitAllocAdt(state, 1, 1, runtimeManaged);
        StoreMemory(state, someRef, 8, tupleRef, "text_uncons_some_value");
        LlvmApi.BuildStore(builder, someRef, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "text_uncons_result_value");
    }

    private readonly record struct IntParseSlots(LlvmValueHandle IndexSlot, LlvmValueHandle AccSlot, LlvmValueHandle NegativeSlot, LlvmValueHandle ResultSlot);

    private readonly record struct IntParseBlocks(
        LlvmBasicBlockHandle InvalidBlock, LlvmBasicBlockHandle SignCheckBlock, LlvmBasicBlockHandle MinusBlock,
        LlvmBasicBlockHandle LoopCheckBlock, LlvmBasicBlockHandle LoopBodyBlock, LlvmBasicBlockHandle UpdateBlock,
        LlvmBasicBlockHandle OverflowBlock, LlvmBasicBlockHandle FinishBlock, LlvmBasicBlockHandle ContinueBlock);

    private static (IntParseSlots Slots, IntParseBlocks Blocks, LlvmValueHandle MaxPositive, LlvmValueHandle MaxNegativeMagnitude) EmitTextParseIntPrologue(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
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

        var blocks = new IntParseBlocks(
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_int_invalid"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_int_sign_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_int_minus"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_int_loop_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_int_loop_body"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_int_update"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_int_overflow"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_int_finish"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_int_continue"));
        return (new IntParseSlots(indexSlot, accSlot, negativeSlot, resultSlot), blocks, maxPositive, maxNegativeMagnitude);
    }

    private static LlvmValueHandle EmitTextParseInt(LlvmCodegenState state, LlvmValueHandle textRef, bool runtimeManaged)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle len = LoadStringLength(state, textRef, "text_parse_int_len");
        LlvmValueHandle bytesPtr = GetStringBytesPointer(state, textRef, "text_parse_int_bytes");
        var (slots, blocks, maxPositive, maxNegativeMagnitude) = EmitTextParseIntPrologue(state);
        var (indexSlot, accSlot, negativeSlot, resultSlot) = slots;
        var (invalidBlock, signCheckBlock, minusBlock, loopCheckBlock, loopBodyBlock, updateBlock, overflowBlock, finishBlock, continueBlock) = blocks;

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

        EmitTextParseIntUpdateBlock(state, index, currentByte, accSlot, negativeSlot, indexSlot, maxPositive, maxNegativeMagnitude, updateBlock, overflowBlock, loopCheckBlock);
        EmitTextParseIntTerminalBlocks(state, accSlot, negativeSlot, resultSlot, finishBlock, invalidBlock, overflowBlock, continueBlock, runtimeManaged);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "text_parse_int_result_value");
    }

    private static void EmitTextParseIntUpdateBlock(LlvmCodegenState state, LlvmValueHandle index, LlvmValueHandle currentByte, LlvmValueHandle accSlot, LlvmValueHandle negativeSlot, LlvmValueHandle indexSlot, LlvmValueHandle maxPositive, LlvmValueHandle maxNegativeMagnitude, LlvmBasicBlockHandle updateBlock, LlvmBasicBlockHandle overflowBlock, LlvmBasicBlockHandle loopCheckBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
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
    }

    private static void EmitTextParseIntTerminalBlocks(LlvmCodegenState state, LlvmValueHandle accSlot, LlvmValueHandle negativeSlot, LlvmValueHandle resultSlot, LlvmBasicBlockHandle finishBlock, LlvmBasicBlockHandle invalidBlock, LlvmBasicBlockHandle overflowBlock, LlvmBasicBlockHandle continueBlock, bool runtimeManaged)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        LlvmValueHandle magnitude = LlvmApi.BuildLoad2(builder, state.I64, accSlot, "text_parse_int_magnitude");
        LlvmValueHandle finalNegativeFlag = LlvmApi.BuildLoad2(builder, state.I64, negativeSlot, "text_parse_int_final_negative_flag");
        LlvmValueHandle finalIsNegative = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, finalNegativeFlag, LlvmApi.ConstInt(state.I64, 0, 0), "text_parse_int_final_is_negative");
        LlvmValueHandle signedValue = LlvmApi.BuildSelect(builder, finalIsNegative, LlvmApi.BuildSub(builder, LlvmApi.ConstInt(state.I64, 0, 0), magnitude, "text_parse_int_negated"), magnitude, "text_parse_int_final_value");
        LlvmApi.BuildStore(builder, EmitResultOk(state, signedValue, runtimeManaged), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, invalidBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TextParseIntInvalidMessage), runtimeManaged), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, overflowBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TextParseIntOverflowMessage), runtimeManaged), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);
    }

    private readonly record struct FloatParseSlots(
        LlvmValueHandle IndexSlot, LlvmValueHandle ValueSlot, LlvmValueHandle FractionPlaceSlot,
        LlvmValueHandle NegativeSlot, LlvmValueHandle ExponentSlot, LlvmValueHandle ExponentNegativeSlot,
        LlvmValueHandle ResultSlot);

    private readonly record struct FloatParseBlocks(
        LlvmBasicBlockHandle InvalidBlock, LlvmBasicBlockHandle RangeBlock, LlvmBasicBlockHandle SignCheckBlock,
        LlvmBasicBlockHandle MinusBlock, LlvmBasicBlockHandle IntegerFirstDigitBlock, LlvmBasicBlockHandle IntegerLoopCheckBlock,
        LlvmBasicBlockHandle IntegerLoopBodyBlock, LlvmBasicBlockHandle IntegerAfterDigitBlock, LlvmBasicBlockHandle SuffixInspectBlock,
        LlvmBasicBlockHandle FractionStartBlock, LlvmBasicBlockHandle FractionFirstDigitBlock, LlvmBasicBlockHandle FractionLoopBodyBlock,
        LlvmBasicBlockHandle ExponentMarkerBlock, LlvmBasicBlockHandle ExponentSignInspectBlock, LlvmBasicBlockHandle ExponentMinusBlock,
        LlvmBasicBlockHandle ExponentPlusBlock, LlvmBasicBlockHandle ExponentFirstDigitBlock, LlvmBasicBlockHandle ExponentLoopBodyBlock,
        LlvmBasicBlockHandle ExponentDoneBlock, LlvmBasicBlockHandle ExponentMulCheckBlock, LlvmBasicBlockHandle ExponentMulBodyBlock,
        LlvmBasicBlockHandle ExponentDivCheckBlock, LlvmBasicBlockHandle ExponentDivBodyBlock, LlvmBasicBlockHandle FinishBlock,
        LlvmBasicBlockHandle ContinueBlock);

    private static LlvmValueHandle EmitTextParseFloat(LlvmCodegenState state, LlvmValueHandle textRef, bool runtimeManaged)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle len = LoadStringLength(state, textRef, "text_parse_float_len");
        LlvmValueHandle bytesPtr = GetStringBytesPointer(state, textRef, "text_parse_float_bytes");
        var slots = EmitTextParseFloatAllocateSlots(state);
        LlvmValueHandle maxFloat = LlvmApi.ConstReal(state.F64, double.MaxValue);
        var blocks = EmitTextParseFloatCreateBlocks(state);

        LlvmValueHandle isEmpty = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, len, LlvmApi.ConstInt(state.I64, 0, 0), "text_parse_float_is_empty");
        LlvmApi.BuildCondBr(builder, isEmpty, blocks.InvalidBlock, blocks.SignCheckBlock);

        EmitTextParseFloatIntegerPhase(state, len, bytesPtr, maxFloat, slots, blocks);
        EmitTextParseFloatIntegerLoopBody(state, len, bytesPtr, maxFloat, slots, blocks);
        EmitTextParseFloatFractionPhase(state, len, bytesPtr, slots, blocks);
        EmitTextParseFloatExponentParsePhase(state, len, bytesPtr, slots, blocks);
        EmitTextParseFloatExponentFirstDigit(state, bytesPtr, slots, blocks);
        EmitTextParseFloatExponentLoopPhase(state, len, bytesPtr, maxFloat, slots, blocks);
        EmitTextParseFloatExponentApplyPhase(state, maxFloat, slots, blocks);
        EmitTextParseFloatTerminals(state, slots, blocks, runtimeManaged);

        LlvmApi.PositionBuilderAtEnd(builder, blocks.ContinueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, slots.ResultSlot, "text_parse_float_result_value");
    }

    private static FloatParseSlots EmitTextParseFloatAllocateSlots(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
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
        return new FloatParseSlots(indexSlot, valueSlot, fractionPlaceSlot, negativeSlot, exponentSlot, exponentNegativeSlot, resultSlot);
    }

    private static FloatParseBlocks EmitTextParseFloatCreateBlocks(LlvmCodegenState state)
    {
        return new FloatParseBlocks(
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_invalid"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_range"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_sign_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_minus"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_integer_first_digit"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_integer_loop_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_integer_loop_body"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_integer_after_digit"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_suffix_inspect"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_fraction_start"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_fraction_first_digit"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_fraction_loop_body"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_exponent_marker"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_exponent_sign_inspect"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_exponent_minus"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_exponent_plus"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_exponent_first_digit"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_exponent_loop_body"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_exponent_done"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_exponent_mul_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_exponent_mul_body"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_exponent_div_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_exponent_div_body"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_finish"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "text_parse_float_continue"));
    }

    private static void EmitTextParseFloatIntegerPhase(LlvmCodegenState state, LlvmValueHandle len, LlvmValueHandle bytesPtr, LlvmValueHandle maxFloat, FloatParseSlots slots, FloatParseBlocks blocks)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        var (indexSlot, valueSlot, fractionPlaceSlot, negativeSlot, exponentSlot, exponentNegativeSlot, resultSlot) = slots;
        var (invalidBlock, rangeBlock, signCheckBlock, minusBlock, integerFirstDigitBlock, integerLoopCheckBlock, integerLoopBodyBlock, integerAfterDigitBlock, suffixInspectBlock, fractionStartBlock, fractionFirstDigitBlock, fractionLoopBodyBlock, exponentMarkerBlock, exponentSignInspectBlock, exponentMinusBlock, exponentPlusBlock, exponentFirstDigitBlock, exponentLoopBodyBlock, exponentDoneBlock, exponentMulCheckBlock, exponentMulBodyBlock, exponentDivCheckBlock, exponentDivBodyBlock, finishBlock, continueBlock) = blocks;

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

    }

    private static void EmitTextParseFloatIntegerLoopBody(LlvmCodegenState state, LlvmValueHandle len, LlvmValueHandle bytesPtr, LlvmValueHandle maxFloat, FloatParseSlots slots, FloatParseBlocks blocks)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        var (indexSlot, valueSlot, fractionPlaceSlot, negativeSlot, exponentSlot, exponentNegativeSlot, resultSlot) = slots;
        var (invalidBlock, rangeBlock, signCheckBlock, minusBlock, integerFirstDigitBlock, integerLoopCheckBlock, integerLoopBodyBlock, integerAfterDigitBlock, suffixInspectBlock, fractionStartBlock, fractionFirstDigitBlock, fractionLoopBodyBlock, exponentMarkerBlock, exponentSignInspectBlock, exponentMinusBlock, exponentPlusBlock, exponentFirstDigitBlock, exponentLoopBodyBlock, exponentDoneBlock, exponentMulCheckBlock, exponentMulBodyBlock, exponentDivCheckBlock, exponentDivBodyBlock, finishBlock, continueBlock) = blocks;

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
    }

    private static void EmitTextParseFloatFractionPhase(LlvmCodegenState state, LlvmValueHandle len, LlvmValueHandle bytesPtr, FloatParseSlots slots, FloatParseBlocks blocks)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        var (indexSlot, valueSlot, fractionPlaceSlot, negativeSlot, exponentSlot, exponentNegativeSlot, resultSlot) = slots;
        var (invalidBlock, rangeBlock, signCheckBlock, minusBlock, integerFirstDigitBlock, integerLoopCheckBlock, integerLoopBodyBlock, integerAfterDigitBlock, suffixInspectBlock, fractionStartBlock, fractionFirstDigitBlock, fractionLoopBodyBlock, exponentMarkerBlock, exponentSignInspectBlock, exponentMinusBlock, exponentPlusBlock, exponentFirstDigitBlock, exponentLoopBodyBlock, exponentDoneBlock, exponentMulCheckBlock, exponentMulBodyBlock, exponentDivCheckBlock, exponentDivBodyBlock, finishBlock, continueBlock) = blocks;

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
    }

    private static void EmitTextParseFloatExponentParsePhase(LlvmCodegenState state, LlvmValueHandle len, LlvmValueHandle bytesPtr, FloatParseSlots slots, FloatParseBlocks blocks)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        var (indexSlot, valueSlot, fractionPlaceSlot, negativeSlot, exponentSlot, exponentNegativeSlot, resultSlot) = slots;
        var (invalidBlock, rangeBlock, signCheckBlock, minusBlock, integerFirstDigitBlock, integerLoopCheckBlock, integerLoopBodyBlock, integerAfterDigitBlock, suffixInspectBlock, fractionStartBlock, fractionFirstDigitBlock, fractionLoopBodyBlock, exponentMarkerBlock, exponentSignInspectBlock, exponentMinusBlock, exponentPlusBlock, exponentFirstDigitBlock, exponentLoopBodyBlock, exponentDoneBlock, exponentMulCheckBlock, exponentMulBodyBlock, exponentDivCheckBlock, exponentDivBodyBlock, finishBlock, continueBlock) = blocks;

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
    }

    private static void EmitTextParseFloatExponentFirstDigit(LlvmCodegenState state, LlvmValueHandle bytesPtr, FloatParseSlots slots, FloatParseBlocks blocks)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, blocks.ExponentFirstDigitBlock);
        LlvmValueHandle exponentFirstIndex = LlvmApi.BuildLoad2(builder, state.I64, slots.IndexSlot, "text_parse_float_exponent_first_index");
        LlvmValueHandle exponentFirstByte = LoadByteAsI64(state, bytesPtr, exponentFirstIndex, "text_parse_float_exponent_first_byte");
        LlvmValueHandle exponentStartsWithDigit = BuildDecimalDigitCheck(state, exponentFirstByte, "text_parse_float_exponent_first_digit_check");
        LlvmApi.BuildCondBr(builder, exponentStartsWithDigit, blocks.ExponentLoopBodyBlock, blocks.InvalidBlock);
    }

    private static void EmitTextParseFloatExponentLoopPhase(LlvmCodegenState state, LlvmValueHandle len, LlvmValueHandle bytesPtr, LlvmValueHandle maxFloat, FloatParseSlots slots, FloatParseBlocks blocks)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        var (indexSlot, valueSlot, fractionPlaceSlot, negativeSlot, exponentSlot, exponentNegativeSlot, resultSlot) = slots;
        var (invalidBlock, rangeBlock, signCheckBlock, minusBlock, integerFirstDigitBlock, integerLoopCheckBlock, integerLoopBodyBlock, integerAfterDigitBlock, suffixInspectBlock, fractionStartBlock, fractionFirstDigitBlock, fractionLoopBodyBlock, exponentMarkerBlock, exponentSignInspectBlock, exponentMinusBlock, exponentPlusBlock, exponentFirstDigitBlock, exponentLoopBodyBlock, exponentDoneBlock, exponentMulCheckBlock, exponentMulBodyBlock, exponentDivCheckBlock, exponentDivBodyBlock, finishBlock, continueBlock) = blocks;

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
    }

    private static void EmitTextParseFloatExponentApplyPhase(LlvmCodegenState state, LlvmValueHandle maxFloat, FloatParseSlots slots, FloatParseBlocks blocks)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        var (indexSlot, valueSlot, fractionPlaceSlot, negativeSlot, exponentSlot, exponentNegativeSlot, resultSlot) = slots;
        var (invalidBlock, rangeBlock, signCheckBlock, minusBlock, integerFirstDigitBlock, integerLoopCheckBlock, integerLoopBodyBlock, integerAfterDigitBlock, suffixInspectBlock, fractionStartBlock, fractionFirstDigitBlock, fractionLoopBodyBlock, exponentMarkerBlock, exponentSignInspectBlock, exponentMinusBlock, exponentPlusBlock, exponentFirstDigitBlock, exponentLoopBodyBlock, exponentDoneBlock, exponentMulCheckBlock, exponentMulBodyBlock, exponentDivCheckBlock, exponentDivBodyBlock, finishBlock, continueBlock) = blocks;

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

        EmitTextParseFloatExponentScale(state, maxFloat, slots, blocks);
    }

    private static void EmitTextParseFloatExponentScale(LlvmCodegenState state, LlvmValueHandle maxFloat, FloatParseSlots slots, FloatParseBlocks blocks)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        var (indexSlot, valueSlot, fractionPlaceSlot, negativeSlot, exponentSlot, exponentNegativeSlot, resultSlot) = slots;
        var (invalidBlock, rangeBlock, signCheckBlock, minusBlock, integerFirstDigitBlock, integerLoopCheckBlock, integerLoopBodyBlock, integerAfterDigitBlock, suffixInspectBlock, fractionStartBlock, fractionFirstDigitBlock, fractionLoopBodyBlock, exponentMarkerBlock, exponentSignInspectBlock, exponentMinusBlock, exponentPlusBlock, exponentFirstDigitBlock, exponentLoopBodyBlock, exponentDoneBlock, exponentMulCheckBlock, exponentMulBodyBlock, exponentDivCheckBlock, exponentDivBodyBlock, finishBlock, continueBlock) = blocks;

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
    }

    private static void EmitTextParseFloatTerminals(LlvmCodegenState state, FloatParseSlots slots, FloatParseBlocks blocks, bool runtimeManaged)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        var (indexSlot, valueSlot, fractionPlaceSlot, negativeSlot, exponentSlot, exponentNegativeSlot, resultSlot) = slots;
        var (invalidBlock, rangeBlock, signCheckBlock, minusBlock, integerFirstDigitBlock, integerLoopCheckBlock, integerLoopBodyBlock, integerAfterDigitBlock, suffixInspectBlock, fractionStartBlock, fractionFirstDigitBlock, fractionLoopBodyBlock, exponentMarkerBlock, exponentSignInspectBlock, exponentMinusBlock, exponentPlusBlock, exponentFirstDigitBlock, exponentLoopBodyBlock, exponentDoneBlock, exponentMulCheckBlock, exponentMulBodyBlock, exponentDivCheckBlock, exponentDivBodyBlock, finishBlock, continueBlock) = blocks;

        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        LlvmValueHandle signFlag = LlvmApi.BuildLoad2(builder, state.I64, negativeSlot, "text_parse_float_sign_flag");
        LlvmValueHandle finalIsNegative = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, signFlag, LlvmApi.ConstInt(state.I64, 0, 0), "text_parse_float_final_is_negative");
        LlvmValueHandle unsignedValue = LlvmApi.BuildLoad2(builder, state.F64, valueSlot, "text_parse_float_unsigned_value");
        LlvmValueHandle finalValue = LlvmApi.BuildSelect(builder, finalIsNegative, LlvmApi.BuildFSub(builder, LlvmApi.ConstReal(state.F64, 0.0), unsignedValue, "text_parse_float_negated_value"), unsignedValue, "text_parse_float_final_value");
        LlvmApi.BuildStore(builder, EmitResultOk(state, finalValue, runtimeManaged), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, invalidBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TextParseFloatInvalidMessage), runtimeManaged), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, rangeBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TextParseFloatRangeMessage), runtimeManaged), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);
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

        EmitFindByteSequenceCompareLoop(state, bytesPtr, patternPtr, patternLen, index, compareIndexSlot, foundBlock, compareLoopBlock, advanceBlock, prefix);

        LlvmApi.PositionBuilderAtEnd(builder, foundBlock);
        LlvmApi.BuildStore(builder, index, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, advanceBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, index, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_index_next"), indexSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, prefix + "_result_value");
    }

    private static void EmitFindByteSequenceCompareLoop(LlvmCodegenState state, LlvmValueHandle bytesPtr, LlvmValueHandle patternPtr, LlvmValueHandle patternLen, LlvmValueHandle index, LlvmValueHandle compareIndexSlot, LlvmBasicBlockHandle foundBlock, LlvmBasicBlockHandle compareLoopBlock, LlvmBasicBlockHandle advanceBlock, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
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
    }

    private static LlvmValueHandle EmitByteSwap16(LlvmCodegenState state, LlvmValueHandle value, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle maskedLow = LlvmApi.BuildAnd(builder, value, LlvmApi.ConstInt(state.I64, 0xFF, 0), prefix + "_low");
        LlvmValueHandle maskedHigh = LlvmApi.BuildAnd(builder, LlvmApi.BuildLShr(builder, value, LlvmApi.ConstInt(state.I64, 8, 0), prefix + "_shr"), LlvmApi.ConstInt(state.I64, 0xFF, 0), prefix + "_high");
        return LlvmApi.BuildOr(builder, LlvmApi.BuildShl(builder, maskedLow, LlvmApi.ConstInt(state.I64, 8, 0), prefix + "_low_shifted"), maskedHigh, prefix + "_result");
    }

    // Ashes.Text.byteLength : the length field of an Ashes string IS the byte count.
    private static LlvmValueHandle EmitTextByteLength(LlvmCodegenState state, LlvmValueHandle textRef)
    {
        return LoadStringLength(state, textRef, "text_byte_length");
    }
}
