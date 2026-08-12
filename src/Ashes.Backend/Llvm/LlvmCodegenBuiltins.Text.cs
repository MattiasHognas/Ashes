using Ashes.Semantics;
using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{

    private static LlvmValueHandle EmitTextUnconsText(LlvmCodegenState state, LlvmValueHandle textRef, bool runtimeManaged)
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
        // The immediate arena path keeps zero-copy views. An escaping runtime-managed result instead
        // owns copied Strings plus its tuple so no child points back into a reclaimed call region.
        LlvmValueHandle headRef = runtimeManaged
            ? EmitHeapStringSliceFromBytesPointer(state, bytesPtr, headLen, "text_uncons_head", runtimeManaged: true)
            : EmitStringView(state, bytesPtr, headLen, "text_uncons_head");
        LlvmValueHandle tailRef = runtimeManaged
            ? EmitHeapStringSliceFromBytesPointer(state, tailPtr, tailLen, "text_uncons_tail", runtimeManaged: true)
            : EmitStringView(state, tailPtr, tailLen, "text_uncons_tail");
        LlvmValueHandle tupleRef = runtimeManaged
            ? EmitRuntimeRcAlloc(state, 16, "rc_text_uncons_tuple")
            : EmitAlloc(state, 16);
        StoreMemory(state, tupleRef, 0, headRef, "text_uncons_tuple_head");
        StoreMemory(state, tupleRef, 8, tailRef, "text_uncons_tuple_tail");
        LlvmValueHandle someRef = EmitAllocAdt(state, 1, 1, runtimeManaged);
        StoreMemory(state, someRef, 8, tupleRef, "text_uncons_some_value");
        LlvmApi.BuildStore(builder, someRef, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "text_uncons_result_value");
    }

    private static LlvmValueHandle EmitTextUncons(LlvmCodegenState state, LlvmValueHandle textRef, bool runtimeManaged)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle len = LoadStringLength(state, textRef, "rune_uncons_len");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "rune_uncons_result");
        LlvmBasicBlockHandle emptyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rune_uncons_empty");
        LlvmBasicBlockHandle valueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rune_uncons_value");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rune_uncons_done");
        LlvmApi.BuildCondBr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, len, LlvmApi.ConstInt(state.I64, 0, 0), "rune_uncons_is_empty"),
            emptyBlock,
            valueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, emptyBlock);
        LlvmApi.BuildStore(builder, EmitAllocAdt(state, 0, 0, runtimeManaged), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, valueBlock);
        LlvmValueHandle bytes = GetStringBytesPointer(state, textRef, "rune_uncons_bytes");
        (LlvmValueHandle rune, LlvmValueHandle width) = EmitDecodeFirstRune(state, bytes, len);
        LlvmValueHandle tailLen = LlvmApi.BuildSub(builder, len, width, "rune_uncons_tail_len");
        LlvmValueHandle tailBytes = LlvmApi.BuildGEP2(builder, state.I8, bytes, [width], "rune_uncons_tail_bytes");
        LlvmValueHandle tail = runtimeManaged
            ? EmitHeapStringSliceFromBytesPointer(state, tailBytes, tailLen, "rune_uncons_tail", runtimeManaged: true)
            : EmitStringView(state, tailBytes, tailLen, "rune_uncons_tail");
        LlvmValueHandle tuple = runtimeManaged ? EmitRuntimeRcAlloc(state, 16, "rc_rune_uncons_tuple") : EmitAlloc(state, 16);
        StoreMemory(state, tuple, 0, rune, "rune_uncons_tuple_rune");
        StoreMemory(state, tuple, 8, tail, "rune_uncons_tuple_tail");
        LlvmValueHandle some = EmitAllocAdt(state, 1, 1, runtimeManaged);
        StoreMemory(state, some, 8, tuple, "rune_uncons_some_value");
        LlvmApi.BuildStore(builder, some, resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "rune_uncons_result_value");
    }

    private static (LlvmValueHandle Rune, LlvmValueHandle Width) EmitDecodeFirstRune(
        LlvmCodegenState state,
        LlvmValueHandle bytes,
        LlvmValueHandle len)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle b0 = LoadByteAsI64(state, bytes, LlvmApi.ConstInt(state.I64, 0, 0), "rune_b0");
        LlvmValueHandle b1 = EmitLoadRuneByteOrZero(state, bytes, len, 1, "rune_b1");
        LlvmValueHandle b2 = EmitLoadRuneByteOrZero(state, bytes, len, 2, "rune_b2");
        LlvmValueHandle b3 = EmitLoadRuneByteOrZero(state, bytes, len, 3, "rune_b3");
        LlvmValueHandle ascii = RuneByteBelow(state, b0, 0x80, "rune_ascii");
        LlvmValueHandle cont1 = RuneContinuation(state, b1, "rune_cont1");
        LlvmValueHandle cont2 = RuneContinuation(state, b2, "rune_cont2");
        LlvmValueHandle cont3 = RuneContinuation(state, b3, "rune_cont3");
        LlvmValueHandle valid2 = RuneAll(state,
            RuneByteRange(state, b0, 0xC2, 0xDF, "rune_lead2"), cont1,
            RuneLengthAtLeast(state, len, 2, "rune_len2"), "rune_valid2");
        LlvmValueHandle valid3 = RuneAll(state,
            RuneByteRange(state, b0, 0xE0, 0xEF, "rune_lead3"), cont1,
            RuneAll(state, cont2, RuneLengthAtLeast(state, len, 3, "rune_len3"), "rune_valid3_tail"), "rune_valid3_base");
        valid3 = LlvmApi.BuildAnd(builder, valid3, EmitRuneThreeByteBoundary(state, b0, b1), "rune_valid3");
        LlvmValueHandle valid4 = RuneAll(state,
            RuneByteRange(state, b0, 0xF0, 0xF4, "rune_lead4"), cont1,
            RuneAll(state, cont2, cont3, "rune_cont23"), "rune_valid4_base");
        valid4 = LlvmApi.BuildAnd(builder, valid4,
            RuneAll(state, RuneLengthAtLeast(state, len, 4, "rune_len4"), EmitRuneFourByteBoundary(state, b0, b1), "rune_valid4_tail"),
            "rune_valid4");
        LlvmValueHandle width = LlvmApi.BuildSelect(builder, valid2, LlvmApi.ConstInt(state.I64, 2, 0),
            LlvmApi.BuildSelect(builder, valid3, LlvmApi.ConstInt(state.I64, 3, 0),
                LlvmApi.BuildSelect(builder, valid4, LlvmApi.ConstInt(state.I64, 4, 0), LlvmApi.ConstInt(state.I64, 1, 0), "rune_width4"),
                "rune_width3"), "rune_width2");
        LlvmValueHandle cp2 = RuneDecodeTwo(state, b0, b1);
        LlvmValueHandle cp3 = RuneDecodeThree(state, b0, b1, b2);
        LlvmValueHandle cp4 = RuneDecodeFour(state, b0, b1, b2, b3);
        LlvmValueHandle rune = LlvmApi.BuildSelect(builder, ascii, b0,
            LlvmApi.BuildSelect(builder, valid2, cp2,
                LlvmApi.BuildSelect(builder, valid3, cp3,
                    LlvmApi.BuildSelect(builder, valid4, cp4, LlvmApi.ConstInt(state.I64, 0xFFFD, 0), "rune_invalid"),
                    "rune_value3"), "rune_value2"), "rune_value");
        return (rune, width);
    }

    private static LlvmValueHandle EmitLoadRuneByteOrZero(
        LlvmCodegenState state,
        LlvmValueHandle bytes,
        LlvmValueHandle len,
        ulong index,
        string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle slot = LlvmApi.BuildAlloca(builder, state.I64, name + "_slot");
        LlvmBasicBlockHandle loadBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, name + "_load");
        LlvmBasicBlockHandle zeroBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, name + "_zero");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, name + "_done");
        LlvmApi.BuildCondBr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, len, LlvmApi.ConstInt(state.I64, index, 0), name + "_exists"),
            loadBlock,
            zeroBlock);
        LlvmApi.PositionBuilderAtEnd(builder, loadBlock);
        LlvmApi.BuildStore(builder, LoadByteAsI64(state, bytes, LlvmApi.ConstInt(state.I64, index, 0), name), slot);
        LlvmApi.BuildBr(builder, doneBlock);
        LlvmApi.PositionBuilderAtEnd(builder, zeroBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), slot);
        LlvmApi.BuildBr(builder, doneBlock);
        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, slot, name + "_value");
    }

    private static LlvmValueHandle RuneLengthAtLeast(LlvmCodegenState state, LlvmValueHandle len, ulong count, string name) =>
        LlvmApi.BuildICmp(state.Target.Builder, LlvmIntPredicate.Uge, len, LlvmApi.ConstInt(state.I64, count, 0), name);

    private static LlvmValueHandle RuneByteBelow(LlvmCodegenState state, LlvmValueHandle value, ulong upper, string name) =>
        LlvmApi.BuildICmp(state.Target.Builder, LlvmIntPredicate.Ult, value, LlvmApi.ConstInt(state.I64, upper, 0), name);

    private static LlvmValueHandle RuneByteRange(LlvmCodegenState state, LlvmValueHandle value, ulong lower, ulong upper, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        return LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, value, LlvmApi.ConstInt(state.I64, lower, 0), name + "_lower"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ule, value, LlvmApi.ConstInt(state.I64, upper, 0), name + "_upper"), name);
    }

    private static LlvmValueHandle RuneContinuation(LlvmCodegenState state, LlvmValueHandle value, string name) =>
        RuneByteRange(state, value, 0x80, 0xBF, name);

    private static LlvmValueHandle RuneAll(LlvmCodegenState state, LlvmValueHandle left, LlvmValueHandle right, string name) =>
        LlvmApi.BuildAnd(state.Target.Builder, left, right, name);

    private static LlvmValueHandle RuneAll(
        LlvmCodegenState state,
        LlvmValueHandle first,
        LlvmValueHandle second,
        LlvmValueHandle third,
        string name) => RuneAll(state, RuneAll(state, first, second, name + "_left"), third, name);

    private static LlvmValueHandle EmitRuneThreeByteBoundary(LlvmCodegenState state, LlvmValueHandle b0, LlvmValueHandle b1)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle notOverlong = LlvmApi.BuildOr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, b0, LlvmApi.ConstInt(state.I64, 0xE0, 0), "rune_not_e0"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, b1, LlvmApi.ConstInt(state.I64, 0xA0, 0), "rune_e0_tail"), "rune_not_overlong3");
        LlvmValueHandle notSurrogate = LlvmApi.BuildOr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, b0, LlvmApi.ConstInt(state.I64, 0xED, 0), "rune_not_ed"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, b1, LlvmApi.ConstInt(state.I64, 0xA0, 0), "rune_ed_tail"), "rune_not_surrogate");
        return LlvmApi.BuildAnd(builder, notOverlong, notSurrogate, "rune_boundary3");
    }

    private static LlvmValueHandle EmitRuneFourByteBoundary(LlvmCodegenState state, LlvmValueHandle b0, LlvmValueHandle b1)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle notOverlong = LlvmApi.BuildOr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, b0, LlvmApi.ConstInt(state.I64, 0xF0, 0), "rune_not_f0"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, b1, LlvmApi.ConstInt(state.I64, 0x90, 0), "rune_f0_tail"), "rune_not_overlong4");
        LlvmValueHandle inRange = LlvmApi.BuildOr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, b0, LlvmApi.ConstInt(state.I64, 0xF4, 0), "rune_not_f4"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ule, b1, LlvmApi.ConstInt(state.I64, 0x8F, 0), "rune_f4_tail"), "rune_in_range4");
        return LlvmApi.BuildAnd(builder, notOverlong, inRange, "rune_boundary4");
    }

    private static LlvmValueHandle RuneDecodeTwo(LlvmCodegenState state, LlvmValueHandle b0, LlvmValueHandle b1) =>
        RuneCombine(state, LlvmApi.BuildAnd(state.Target.Builder, b0, LlvmApi.ConstInt(state.I64, 0x1F, 0), "rune_cp2_head"), b1, 6, "rune_cp2");

    private static LlvmValueHandle RuneDecodeThree(LlvmCodegenState state, LlvmValueHandle b0, LlvmValueHandle b1, LlvmValueHandle b2) =>
        RuneCombine(state, RuneCombine(state,
            LlvmApi.BuildAnd(state.Target.Builder, b0, LlvmApi.ConstInt(state.I64, 0x0F, 0), "rune_cp3_head"), b1, 6, "rune_cp3_mid"), b2, 6, "rune_cp3");

    private static LlvmValueHandle RuneDecodeFour(LlvmCodegenState state, LlvmValueHandle b0, LlvmValueHandle b1, LlvmValueHandle b2, LlvmValueHandle b3) =>
        RuneCombine(state, RuneCombine(state, RuneCombine(state,
            LlvmApi.BuildAnd(state.Target.Builder, b0, LlvmApi.ConstInt(state.I64, 0x07, 0), "rune_cp4_head"), b1, 6, "rune_cp4_1"), b2, 6, "rune_cp4_2"), b3, 6, "rune_cp4");

    private static LlvmValueHandle RuneCombine(LlvmCodegenState state, LlvmValueHandle head, LlvmValueHandle tail, ulong shift, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        return LlvmApi.BuildOr(builder,
            LlvmApi.BuildShl(builder, head, LlvmApi.ConstInt(state.I64, shift, 0), name + "_shift"),
            LlvmApi.BuildAnd(builder, tail, LlvmApi.ConstInt(state.I64, 0x3F, 0), name + "_tail"), name);
    }

    private static LlvmValueHandle EmitRuneFromInt(LlvmCodegenState state, LlvmValueHandle value, bool runtimeManaged)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle belowSurrogate = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, value, LlvmApi.ConstInt(state.I64, 0xD800, 0), "rune_from_below_surrogate");
        LlvmValueHandle aboveSurrogate = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, value, LlvmApi.ConstInt(state.I64, 0xDFFF, 0), "rune_from_above_surrogate");
        LlvmValueHandle nonNegative = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, value, LlvmApi.ConstInt(state.I64, 0, 0), "rune_from_nonnegative");
        LlvmValueHandle inRange = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, value, LlvmApi.ConstInt(state.I64, 0x10FFFF, 0), "rune_from_in_range");
        LlvmValueHandle valid = RuneAll(state, nonNegative, inRange, "rune_from_bounds");
        valid = RuneAll(state, valid, LlvmApi.BuildOr(builder, belowSurrogate, aboveSurrogate, "rune_from_not_surrogate"), "rune_from_valid");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "rune_from_result");
        LlvmBasicBlockHandle validBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rune_from_some");
        LlvmBasicBlockHandle invalidBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rune_from_none");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rune_from_done");
        LlvmApi.BuildCondBr(builder, valid, validBlock, invalidBlock);

        LlvmApi.PositionBuilderAtEnd(builder, validBlock);
        LlvmValueHandle some = EmitAllocAdt(state, 1, 1, runtimeManaged);
        StoreMemory(state, some, 8, value, "rune_from_some_value");
        LlvmApi.BuildStore(builder, some, resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, invalidBlock);
        LlvmApi.BuildStore(builder, EmitAllocAdt(state, 0, 0, runtimeManaged), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "rune_from_result_value");
    }

    private static LlvmValueHandle EmitRuneToText(LlvmCodegenState state, LlvmValueHandle rune, bool runtimeManaged)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle width = LlvmApi.BuildSelect(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, rune, LlvmApi.ConstInt(state.I64, 0x80, 0), "rune_text_ascii"),
            LlvmApi.ConstInt(state.I64, 1, 0),
            LlvmApi.BuildSelect(builder,
                LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, rune, LlvmApi.ConstInt(state.I64, 0x800, 0), "rune_text_two"),
                LlvmApi.ConstInt(state.I64, 2, 0),
                LlvmApi.BuildSelect(builder,
                    LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, rune, LlvmApi.ConstInt(state.I64, 0x10000, 0), "rune_text_three"),
                    LlvmApi.ConstInt(state.I64, 3, 0), LlvmApi.ConstInt(state.I64, 4, 0), "rune_text_width34"),
                "rune_text_width234"), "rune_text_width");
        // Reserve the maximum four-byte payload so straight-line stores never address beyond the
        // allocation; the logical string length remains the selected UTF-8 width.
        LlvmValueHandle size = LlvmApi.ConstInt(state.I64, 12, 0);
        LlvmValueHandle text = runtimeManaged
            ? EmitRuntimeRcAllocDynamic(state, size, "rc_rune_text")
            : EmitAllocDynamic(state, size);
        StoreMemory(state, text, 0, width, "rune_text_len");
        LlvmValueHandle bytes = GetStringBytesPointer(state, text, "rune_text_bytes");
        EmitRuneStoreUtf8(state, bytes, rune, width);
        return text;
    }

    private static void EmitRuneStoreUtf8(LlvmCodegenState state, LlvmValueHandle bytes, LlvmValueHandle rune, LlvmValueHandle width)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle one = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, width, LlvmApi.ConstInt(state.I64, 1, 0), "rune_store_one");
        LlvmValueHandle two = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, width, LlvmApi.ConstInt(state.I64, 2, 0), "rune_store_two");
        LlvmValueHandle three = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, width, LlvmApi.ConstInt(state.I64, 3, 0), "rune_store_three");
        LlvmValueHandle first = LlvmApi.BuildSelect(builder, one, rune,
            LlvmApi.BuildSelect(builder, two, RuneLeadByte(state, rune, 6, 0xC0, "rune_lead2"),
                LlvmApi.BuildSelect(builder, three, RuneLeadByte(state, rune, 12, 0xE0, "rune_lead3"),
                    RuneLeadByte(state, rune, 18, 0xF0, "rune_lead4"), "rune_first34"), "rune_first234"), "rune_first");
        EmitRuneStoreByte(state, bytes, 0, first, "rune_text_b0");
        LlvmValueHandle second = LlvmApi.BuildSelect(builder, two, RuneContinuationByte(state, rune, 0, "rune_second2"),
            LlvmApi.BuildSelect(builder, three, RuneContinuationByte(state, rune, 6, "rune_second3"),
                RuneContinuationByte(state, rune, 12, "rune_second4"), "rune_second34"), "rune_second");
        EmitRuneStoreByte(state, bytes, 1, second, "rune_text_b1");
        LlvmValueHandle third = LlvmApi.BuildSelect(builder, three, RuneContinuationByte(state, rune, 0, "rune_third3"),
            RuneContinuationByte(state, rune, 6, "rune_third4"), "rune_third");
        EmitRuneStoreByte(state, bytes, 2, third, "rune_text_b2");
        EmitRuneStoreByte(state, bytes, 3, RuneContinuationByte(state, rune, 0, "rune_fourth"), "rune_text_b3");
    }

    private static LlvmValueHandle RuneLeadByte(LlvmCodegenState state, LlvmValueHandle rune, ulong shift, ulong prefix, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        return LlvmApi.BuildOr(builder,
            LlvmApi.BuildLShr(builder, rune, LlvmApi.ConstInt(state.I64, shift, 0), name + "_shift"),
            LlvmApi.ConstInt(state.I64, prefix, 0), name);
    }

    private static LlvmValueHandle RuneContinuationByte(LlvmCodegenState state, LlvmValueHandle rune, ulong shift, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle shifted = shift == 0 ? rune : LlvmApi.BuildLShr(builder, rune, LlvmApi.ConstInt(state.I64, shift, 0), name + "_shift");
        return LlvmApi.BuildOr(builder,
            LlvmApi.BuildAnd(builder, shifted, LlvmApi.ConstInt(state.I64, 0x3F, 0), name + "_payload"),
            LlvmApi.ConstInt(state.I64, 0x80, 0), name);
    }

    private static void EmitRuneStoreByte(LlvmCodegenState state, LlvmValueHandle bytes, ulong index, LlvmValueHandle value, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle pointer = LlvmApi.BuildGEP2(builder, state.I8, bytes, [LlvmApi.ConstInt(state.I64, index, 0)], name + "_ptr");
        LlvmApi.BuildStore(builder, LlvmApi.BuildTrunc(builder, value, state.I8, name), pointer);
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
