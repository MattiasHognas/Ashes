using System.Buffers.Binary;
using Ashes.Backend.Backends;
using Ashes.Semantics;
using LLVMSharp.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{
    private static LLVMValueRef EmitAlloc(LlvmCodegenState state, int sizeBytes)
    {
        LLVMValueRef cursor = state.Target.Builder.BuildLoad2(state.I64, state.HeapCursorSlot, "heap_cursor_value");
        LLVMValueRef nextCursor = state.Target.Builder.BuildAdd(cursor, LLVMValueRef.CreateConstInt(state.I64, (ulong)sizeBytes, false), "heap_cursor_next");
        state.Target.Builder.BuildStore(nextCursor, state.HeapCursorSlot);
        return cursor;
    }

    private static LLVMValueRef EmitAllocAdt(LlvmCodegenState state, int tag, int fieldCount)
    {
        LLVMValueRef ptr = EmitAlloc(state, (1 + fieldCount) * 8);
        StoreMemory(state, ptr, 0, LLVMValueRef.CreateConstInt(state.I64, (ulong)tag, false), $"adt_tag_{tag}");
        return ptr;
    }

    private static bool StoreMemory(LlvmCodegenState state, LLVMValueRef baseAddress, int offsetBytes, LLVMValueRef value, string name)
    {
        LLVMValueRef ptr = GetMemoryPointer(state, baseAddress, offsetBytes, name + "_ptr");
        state.Target.Builder.BuildStore(NormalizeToI64(state, value), ptr);
        return false;
    }

    private static LLVMValueRef LoadMemory(LlvmCodegenState state, LLVMValueRef baseAddress, int offsetBytes, string name)
    {
        LLVMValueRef ptr = GetMemoryPointer(state, baseAddress, offsetBytes, name + "_ptr");
        return state.Target.Builder.BuildLoad2(state.I64, ptr, name);
    }

    private static LLVMValueRef GetMemoryPointer(LlvmCodegenState state, LLVMValueRef baseAddress, int offsetBytes, string name)
    {
        LLVMValueRef basePtr = state.Target.Builder.BuildIntToPtr(baseAddress, state.I8Ptr, name + "_base");
        LLVMValueRef bytePtr = state.Target.Builder.BuildGEP2(
            state.I8,
            basePtr,
            new[]
            {
                LLVMValueRef.CreateConstInt(state.I64, (ulong)offsetBytes, false)
            },
            name + "_byte");
        return state.Target.Builder.BuildBitCast(bytePtr, state.I64Ptr, name);
    }

    private static LLVMValueRef EmitStringComparison(LlvmCodegenState state, LLVMValueRef leftRef, LLVMValueRef rightRef)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, "str_cmp_result");
        LLVMValueRef indexSlot = builder.BuildAlloca(state.I64, "str_cmp_idx");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), resultSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), indexSlot);

        LLVMValueRef leftLen = LoadStringLength(state, leftRef, "str_cmp_left_len");
        LLVMValueRef rightLen = LoadStringLength(state, rightRef, "str_cmp_right_len");
        LLVMValueRef leftBytes = GetStringBytesPointer(state, leftRef, "str_cmp_left_bytes");
        LLVMValueRef rightBytes = GetStringBytesPointer(state, rightRef, "str_cmp_right_bytes");

        var lenEqBlock = state.Function.AppendBasicBlock("str_cmp_len_eq");
        var notEqBlock = state.Function.AppendBasicBlock("str_cmp_not_eq");
        var loopCheckBlock = state.Function.AppendBasicBlock("str_cmp_loop_check");
        var loopBodyBlock = state.Function.AppendBasicBlock("str_cmp_loop_body");
        var eqBlock = state.Function.AppendBasicBlock("str_cmp_eq");
        var continueBlock = state.Function.AppendBasicBlock("str_cmp_continue");

        LLVMValueRef lenEq = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, leftLen, rightLen, "str_cmp_len_match");
        builder.BuildCondBr(lenEq, lenEqBlock, notEqBlock);

        builder.PositionAtEnd(notEqBlock);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(lenEqBlock);
        LLVMValueRef isEmpty = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, leftLen, LLVMValueRef.CreateConstInt(state.I64, 0, false), "str_cmp_empty");
        builder.BuildCondBr(isEmpty, eqBlock, loopCheckBlock);

        builder.PositionAtEnd(loopCheckBlock);
        LLVMValueRef index = builder.BuildLoad2(state.I64, indexSlot, "str_cmp_index");
        LLVMValueRef done = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, index, leftLen, "str_cmp_done");
        builder.BuildCondBr(done, eqBlock, loopBodyBlock);

        builder.PositionAtEnd(loopBodyBlock);
        LLVMValueRef leftBytePtr = builder.BuildGEP2(state.I8, leftBytes, new[] { index }, "str_cmp_left_byte_ptr");
        LLVMValueRef rightBytePtr = builder.BuildGEP2(state.I8, rightBytes, new[] { index }, "str_cmp_right_byte_ptr");
        LLVMValueRef leftByte = builder.BuildLoad2(state.I8, leftBytePtr, "str_cmp_left_byte");
        LLVMValueRef rightByte = builder.BuildLoad2(state.I8, rightBytePtr, "str_cmp_right_byte");
        LLVMValueRef bytesEq = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, leftByte, rightByte, "str_cmp_bytes_eq");
        LLVMValueRef nextIndex = builder.BuildAdd(index, LLVMValueRef.CreateConstInt(state.I64, 1, false), "str_cmp_next_index");
        builder.BuildStore(nextIndex, indexSlot);
        builder.BuildCondBr(bytesEq, loopCheckBlock, notEqBlock);

        builder.PositionAtEnd(eqBlock);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 1, false), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, "str_cmp_result_value");
    }

    private static LLVMValueRef EmitStringConcat(LlvmCodegenState state, LLVMValueRef leftRef, LLVMValueRef rightRef)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef leftLen = LoadStringLength(state, leftRef, "str_cat_left_len");
        LLVMValueRef rightLen = LoadStringLength(state, rightRef, "str_cat_right_len");
        LLVMValueRef totalLen = builder.BuildAdd(leftLen, rightLen, "str_cat_total_len");
        LLVMValueRef totalBytes = builder.BuildAdd(totalLen, LLVMValueRef.CreateConstInt(state.I64, 8, false), "str_cat_total_bytes");
        LLVMValueRef destRef = EmitAllocDynamic(state, totalBytes);
        StoreMemory(state, destRef, 0, totalLen, "str_cat_len");

        LLVMValueRef destBytes = GetStringBytesPointer(state, destRef, "str_cat_dest_bytes");
        LLVMValueRef leftBytes = GetStringBytesPointer(state, leftRef, "str_cat_left_bytes");
        LLVMValueRef rightBytes = GetStringBytesPointer(state, rightRef, "str_cat_right_bytes");
        EmitCopyBytes(state, destBytes, leftBytes, leftLen, "str_cat_copy_left");
        LLVMValueRef rightDest = builder.BuildGEP2(state.I8, destBytes, new[] { leftLen }, "str_cat_right_dest");
        EmitCopyBytes(state, rightDest, rightBytes, rightLen, "str_cat_copy_right");
        return destRef;
    }

    private static void EmitCopyBytes(LlvmCodegenState state, LLVMValueRef destBytes, LLVMValueRef sourceBytes, LLVMValueRef length, string prefix)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef indexSlot = builder.BuildAlloca(state.I64, prefix + "_idx");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), indexSlot);

        var checkBlock = state.Function.AppendBasicBlock(prefix + "_check");
        var bodyBlock = state.Function.AppendBasicBlock(prefix + "_body");
        var continueBlock = state.Function.AppendBasicBlock(prefix + "_continue");
        builder.BuildBr(checkBlock);

        builder.PositionAtEnd(checkBlock);
        LLVMValueRef index = builder.BuildLoad2(state.I64, indexSlot, prefix + "_index");
        LLVMValueRef done = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, index, length, prefix + "_done");
        builder.BuildCondBr(done, continueBlock, bodyBlock);

        builder.PositionAtEnd(bodyBlock);
        LLVMValueRef sourcePtr = builder.BuildGEP2(state.I8, sourceBytes, new[] { index }, prefix + "_src_ptr");
        LLVMValueRef destPtr = builder.BuildGEP2(state.I8, destBytes, new[] { index }, prefix + "_dst_ptr");
        LLVMValueRef value = builder.BuildLoad2(state.I8, sourcePtr, prefix + "_value");
        builder.BuildStore(value, destPtr);
        LLVMValueRef nextIndex = builder.BuildAdd(index, LLVMValueRef.CreateConstInt(state.I64, 1, false), prefix + "_next");
        builder.BuildStore(nextIndex, indexSlot);
        builder.BuildBr(checkBlock);

        builder.PositionAtEnd(continueBlock);
    }

    private static LLVMValueRef LoadStringLength(LlvmCodegenState state, LLVMValueRef stringRef, string name)
    {
        return LoadMemory(state, stringRef, 0, name);
    }

    private static LLVMValueRef GetStringBytesPointer(LlvmCodegenState state, LLVMValueRef stringRef, string name)
    {
        LLVMValueRef byteAddress = state.Target.Builder.BuildAdd(stringRef, LLVMValueRef.CreateConstInt(state.I64, 8, false), name + "_addr");
        return state.Target.Builder.BuildIntToPtr(byteAddress, state.I8Ptr, name);
    }

    private static LLVMValueRef EmitAllocDynamic(LlvmCodegenState state, LLVMValueRef sizeBytes)
    {
        LLVMValueRef cursor = state.Target.Builder.BuildLoad2(state.I64, state.HeapCursorSlot, "heap_cursor_value_dyn");
        LLVMValueRef nextCursor = state.Target.Builder.BuildAdd(cursor, NormalizeToI64(state, sizeBytes), "heap_cursor_next_dyn");
        state.Target.Builder.BuildStore(nextCursor, state.HeapCursorSlot);
        return cursor;
    }

    private static LLVMValueRef EmitStringToCString(LlvmCodegenState state, LLVMValueRef stringRef, string prefix)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef len = LoadStringLength(state, stringRef, prefix + "_len");
        LLVMValueRef cstrRef = EmitAllocDynamic(state, builder.BuildAdd(len, LLVMValueRef.CreateConstInt(state.I64, 1, false), prefix + "_size"));
        LLVMValueRef destPtr = builder.BuildIntToPtr(cstrRef, state.I8Ptr, prefix + "_dest");
        EmitCopyBytes(state, destPtr, GetStringBytesPointer(state, stringRef, prefix + "_src"), len, prefix + "_copy");
        LLVMValueRef terminatorPtr = builder.BuildGEP2(state.I8, destPtr, new[] { len }, prefix + "_nul_ptr");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I8, 0, false), terminatorPtr);
        return destPtr;
    }

    private static LLVMValueRef EmitUnitValue(LlvmCodegenState state)
    {
        return EmitAllocAdt(state, 0, 0);
    }

    private static LLVMValueRef EmitResultOk(LlvmCodegenState state, LLVMValueRef value)
    {
        LLVMValueRef result = EmitAllocAdt(state, 0, 1);
        StoreMemory(state, result, 8, value, "result_ok_value");
        return result;
    }

    private static LLVMValueRef EmitResultError(LlvmCodegenState state, LLVMValueRef errorStringRef)
    {
        LLVMValueRef result = EmitAllocAdt(state, 1, 1);
        StoreMemory(state, result, 8, errorStringRef, "result_error_value");
        return result;
    }

    private static LLVMValueRef EmitHeapStringLiteral(LlvmCodegenState state, string value)
    {
        return EmitHeapStringFromBytes(state, System.Text.Encoding.UTF8.GetBytes(value), "heap_string_literal");
    }

    private static LLVMValueRef EmitHeapStringFromBytes(LlvmCodegenState state, IReadOnlyList<byte> bytes, string prefix)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef len = LLVMValueRef.CreateConstInt(state.I64, (ulong)bytes.Count, false);
        LLVMValueRef stringRef = EmitAllocDynamic(state, builder.BuildAdd(len, LLVMValueRef.CreateConstInt(state.I64, 8, false), prefix + "_size"));
        StoreMemory(state, stringRef, 0, len, prefix + "_len");
        LLVMValueRef destPtr = GetStringBytesPointer(state, stringRef, prefix + "_bytes");
        for (int i = 0; i < bytes.Count; i++)
        {
            LLVMValueRef cellPtr = builder.BuildGEP2(
                state.I8,
                destPtr,
                new[] { LLVMValueRef.CreateConstInt(state.I64, (ulong)i, false) },
                $"{prefix}_byte_ptr_{i}");
            builder.BuildStore(LLVMValueRef.CreateConstInt(state.I8, bytes[i], false), cellPtr);
        }

        return stringRef;
    }

    private static LLVMValueRef GetStringBytesAddress(LlvmCodegenState state, LLVMValueRef stringRef, string name)
    {
        return state.Target.Builder.BuildAdd(stringRef, LLVMValueRef.CreateConstInt(state.I64, 8, false), name);
    }

    private static LLVMValueRef EmitValidateUtf8(LlvmCodegenState state, LLVMValueRef bytesPtr, LLVMValueRef len, string prefix)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef indexSlot = builder.BuildAlloca(state.I64, prefix + "_index");
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, prefix + "_result");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), indexSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), resultSlot);

        var loopBlock = state.Function.AppendBasicBlock(prefix + "_loop");
        var asciiBlock = state.Function.AppendBasicBlock(prefix + "_ascii");
        var twoBlock = state.Function.AppendBasicBlock(prefix + "_two");
        var threeBlock = state.Function.AppendBasicBlock(prefix + "_three");
        var e0Block = state.Function.AppendBasicBlock(prefix + "_e0");
        var edBlock = state.Function.AppendBasicBlock(prefix + "_ed");
        var f0Block = state.Function.AppendBasicBlock(prefix + "_f0");
        var fourBlock = state.Function.AppendBasicBlock(prefix + "_four");
        var f4Block = state.Function.AppendBasicBlock(prefix + "_f4");
        var validBlock = state.Function.AppendBasicBlock(prefix + "_valid");
        var invalidBlock = state.Function.AppendBasicBlock(prefix + "_invalid");
        var continueBlock = state.Function.AppendBasicBlock(prefix + "_continue");

        builder.BuildBr(loopBlock);

        builder.PositionAtEnd(loopBlock);
        LLVMValueRef index = builder.BuildLoad2(state.I64, indexSlot, prefix + "_index_value");
        LLVMValueRef done = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, index, len, prefix + "_done");
        var inspectBlock = state.Function.AppendBasicBlock(prefix + "_inspect");
        builder.BuildCondBr(done, validBlock, inspectBlock);

        builder.PositionAtEnd(inspectBlock);
        LLVMValueRef firstByte = LoadByteAt(state, bytesPtr, index, prefix + "_byte0");
        LLVMValueRef firstByte64 = builder.BuildZExt(firstByte, state.I64, prefix + "_byte0_i64");
        LLVMValueRef isAscii = builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, firstByte64, LLVMValueRef.CreateConstInt(state.I64, 0x80, false), prefix + "_is_ascii");
        var nonAsciiBlock = state.Function.AppendBasicBlock(prefix + "_non_ascii");
        builder.BuildCondBr(isAscii, asciiBlock, nonAsciiBlock);

        builder.PositionAtEnd(nonAsciiBlock);
        LLVMValueRef ltC2 = builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, firstByte64, LLVMValueRef.CreateConstInt(state.I64, 0xC2, false), prefix + "_lt_c2");
        var geC2Block = state.Function.AppendBasicBlock(prefix + "_ge_c2");
        builder.BuildCondBr(ltC2, invalidBlock, geC2Block);

        builder.PositionAtEnd(geC2Block);
        LLVMValueRef leDf = builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, firstByte64, LLVMValueRef.CreateConstInt(state.I64, 0xDF, false), prefix + "_le_df");
        var gtDfBlock = state.Function.AppendBasicBlock(prefix + "_gt_df");
        builder.BuildCondBr(leDf, twoBlock, gtDfBlock);

        builder.PositionAtEnd(gtDfBlock);
        LLVMValueRef isE0 = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, firstByte64, LLVMValueRef.CreateConstInt(state.I64, 0xE0, false), prefix + "_is_e0");
        var afterE0Block = state.Function.AppendBasicBlock(prefix + "_after_e0");
        builder.BuildCondBr(isE0, e0Block, afterE0Block);

        builder.PositionAtEnd(afterE0Block);
        LLVMValueRef leEc = builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, firstByte64, LLVMValueRef.CreateConstInt(state.I64, 0xEC, false), prefix + "_le_ec");
        var afterEcBlock = state.Function.AppendBasicBlock(prefix + "_after_ec");
        builder.BuildCondBr(leEc, threeBlock, afterEcBlock);

        builder.PositionAtEnd(afterEcBlock);
        LLVMValueRef isEd = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, firstByte64, LLVMValueRef.CreateConstInt(state.I64, 0xED, false), prefix + "_is_ed");
        var afterEdBlock = state.Function.AppendBasicBlock(prefix + "_after_ed");
        builder.BuildCondBr(isEd, edBlock, afterEdBlock);

        builder.PositionAtEnd(afterEdBlock);
        LLVMValueRef leEf = builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, firstByte64, LLVMValueRef.CreateConstInt(state.I64, 0xEF, false), prefix + "_le_ef");
        var afterEfBlock = state.Function.AppendBasicBlock(prefix + "_after_ef");
        builder.BuildCondBr(leEf, threeBlock, afterEfBlock);

        builder.PositionAtEnd(afterEfBlock);
        LLVMValueRef isF0 = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, firstByte64, LLVMValueRef.CreateConstInt(state.I64, 0xF0, false), prefix + "_is_f0");
        var afterF0Block = state.Function.AppendBasicBlock(prefix + "_after_f0");
        builder.BuildCondBr(isF0, f0Block, afterF0Block);

        builder.PositionAtEnd(afterF0Block);
        LLVMValueRef leF3 = builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, firstByte64, LLVMValueRef.CreateConstInt(state.I64, 0xF3, false), prefix + "_le_f3");
        var afterF3Block = state.Function.AppendBasicBlock(prefix + "_after_f3");
        builder.BuildCondBr(leF3, fourBlock, afterF3Block);

        builder.PositionAtEnd(afterF3Block);
        LLVMValueRef isF4 = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, firstByte64, LLVMValueRef.CreateConstInt(state.I64, 0xF4, false), prefix + "_is_f4");
        builder.BuildCondBr(isF4, f4Block, invalidBlock);

        builder.PositionAtEnd(asciiBlock);
        builder.BuildStore(builder.BuildAdd(index, LLVMValueRef.CreateConstInt(state.I64, 1, false), prefix + "_ascii_next"), indexSlot);
        builder.BuildBr(loopBlock);

        EmitUtf8SequenceValidation(state, bytesPtr, len, indexSlot, 2, 0x80, 0xBF, prefix + "_two", twoBlock, loopBlock, invalidBlock);
        EmitUtf8SequenceValidation(state, bytesPtr, len, indexSlot, 3, 0x80, 0xBF, prefix + "_three", threeBlock, loopBlock, invalidBlock);
        EmitUtf8SequenceValidation(state, bytesPtr, len, indexSlot, 3, 0xA0, 0xBF, prefix + "_e0", e0Block, loopBlock, invalidBlock);
        EmitUtf8SequenceValidation(state, bytesPtr, len, indexSlot, 3, 0x80, 0x9F, prefix + "_ed", edBlock, loopBlock, invalidBlock);
        EmitUtf8SequenceValidation(state, bytesPtr, len, indexSlot, 4, 0x90, 0xBF, prefix + "_f0", f0Block, loopBlock, invalidBlock);
        EmitUtf8SequenceValidation(state, bytesPtr, len, indexSlot, 4, 0x80, 0xBF, prefix + "_four", fourBlock, loopBlock, invalidBlock);
        EmitUtf8SequenceValidation(state, bytesPtr, len, indexSlot, 4, 0x80, 0x8F, prefix + "_f4", f4Block, loopBlock, invalidBlock);

        builder.PositionAtEnd(validBlock);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 1, false), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(invalidBlock);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, prefix + "_result_value");
    }

    private static void EmitUtf8SequenceValidation(
        LlvmCodegenState state,
        LLVMValueRef bytesPtr,
        LLVMValueRef len,
        LLVMValueRef indexSlot,
        int sequenceLength,
        int secondByteMin,
        int secondByteMax,
        string prefix,
        LLVMBasicBlockRef entryBlock,
        LLVMBasicBlockRef successBlock,
        LLVMBasicBlockRef invalidBlock)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        builder.PositionAtEnd(entryBlock);
        LLVMValueRef index = builder.BuildLoad2(state.I64, indexSlot, prefix + "_index_value");
        LLVMValueRef remaining = builder.BuildSub(len, index, prefix + "_remaining");
        LLVMValueRef enoughBytes = builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, remaining, LLVMValueRef.CreateConstInt(state.I64, (ulong)sequenceLength, false), prefix + "_enough");
        var bodyBlock = state.Function.AppendBasicBlock(prefix + "_body");
        builder.BuildCondBr(enoughBytes, bodyBlock, invalidBlock);

        builder.PositionAtEnd(bodyBlock);
        LLVMValueRef secondByte = LoadByteAt(state, bytesPtr, builder.BuildAdd(index, LLVMValueRef.CreateConstInt(state.I64, 1, false), prefix + "_second_index"), prefix + "_second_byte");
        LLVMValueRef secondByte64 = builder.BuildZExt(secondByte, state.I64, prefix + "_second_i64");
        LLVMValueRef secondInRange = BuildByteRangeCheck(state, secondByte64, secondByteMin, secondByteMax, prefix + "_second_range");
        LLVMBasicBlockRef nextBlock = bodyBlock;
        for (int offset = 2; offset < sequenceLength; offset++)
        {
            var checkBlock = state.Function.AppendBasicBlock(prefix + "_cont_" + offset);
            builder.BuildCondBr(secondInRange, checkBlock, invalidBlock);
            builder.PositionAtEnd(checkBlock);
            nextBlock = checkBlock;
            LLVMValueRef extraByte = LoadByteAt(state, bytesPtr, builder.BuildAdd(index, LLVMValueRef.CreateConstInt(state.I64, (ulong)offset, false), prefix + "_idx_" + offset), prefix + "_byte_" + offset);
            LLVMValueRef extraByte64 = builder.BuildZExt(extraByte, state.I64, prefix + "_byte_i64_" + offset);
            LLVMValueRef extraInRange = BuildByteRangeCheck(state, extraByte64, 0x80, 0xBF, prefix + "_range_" + offset);
            secondInRange = extraInRange;
        }

        builder.PositionAtEnd(nextBlock);
        LLVMBasicBlockRef advanceBlock = state.Function.AppendBasicBlock(prefix + "_advance");
        builder.BuildCondBr(secondInRange, advanceBlock, invalidBlock);
        builder.PositionAtEnd(advanceBlock);
        builder.BuildStore(builder.BuildAdd(index, LLVMValueRef.CreateConstInt(state.I64, (ulong)sequenceLength, false), prefix + "_next"), indexSlot);
        builder.BuildBr(successBlock);
    }

    private static LLVMValueRef BuildByteRangeCheck(LlvmCodegenState state, LLVMValueRef byteValue, int minInclusive, int maxInclusive, string prefix)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef geMin = builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, byteValue, LLVMValueRef.CreateConstInt(state.I64, (ulong)minInclusive, false), prefix + "_ge_min");
        LLVMValueRef leMax = builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, byteValue, LLVMValueRef.CreateConstInt(state.I64, (ulong)maxInclusive, false), prefix + "_le_max");
        return builder.BuildAnd(geMin, leMax, prefix + "_in_range");
    }

    private static LLVMValueRef LoadByteAt(LlvmCodegenState state, LLVMValueRef bytesPtr, LLVMValueRef index, string name)
    {
        LLVMValueRef bytePtr = state.Target.Builder.BuildGEP2(state.I8, bytesPtr, new[] { index }, name + "_ptr");
        return state.Target.Builder.BuildLoad2(state.I8, bytePtr, name);
    }

    private static LLVMValueRef EmitNonNegativeIntToString(LlvmCodegenState state, LLVMValueRef value, string prefix)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef bufferType = LLVMTypeRef.CreateArray(state.I8, 32);
        LLVMValueRef buffer = builder.BuildAlloca(bufferType, prefix + "_buffer");
        LLVMValueRef indexSlot = builder.BuildAlloca(state.I64, prefix + "_index");
        LLVMValueRef workSlot = builder.BuildAlloca(state.I64, prefix + "_work");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), indexSlot);
        builder.BuildStore(value, workSlot);

        var zeroBlock = state.Function.AppendBasicBlock(prefix + "_zero");
        var loopCheckBlock = state.Function.AppendBasicBlock(prefix + "_loop_check");
        var loopBodyBlock = state.Function.AppendBasicBlock(prefix + "_loop_body");
        var finishBlock = state.Function.AppendBasicBlock(prefix + "_finish");
        LLVMValueRef isZero = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, value, LLVMValueRef.CreateConstInt(state.I64, 0, false), prefix + "_is_zero");
        builder.BuildCondBr(isZero, zeroBlock, loopCheckBlock);

        builder.PositionAtEnd(zeroBlock);
        StoreBufferByte(state, buffer, LLVMValueRef.CreateConstInt(state.I64, 31, false), (byte)'0');
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 1, false), indexSlot);
        builder.BuildBr(finishBlock);

        builder.PositionAtEnd(loopCheckBlock);
        LLVMValueRef work = builder.BuildLoad2(state.I64, workSlot, prefix + "_work_value");
        LLVMValueRef done = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, work, LLVMValueRef.CreateConstInt(state.I64, 0, false), prefix + "_done");
        builder.BuildCondBr(done, finishBlock, loopBodyBlock);

        builder.PositionAtEnd(loopBodyBlock);
        LLVMValueRef digit = builder.BuildURem(work, LLVMValueRef.CreateConstInt(state.I64, 10, false), prefix + "_digit");
        builder.BuildStore(builder.BuildUDiv(work, LLVMValueRef.CreateConstInt(state.I64, 10, false), prefix + "_next_work"), workSlot);
        LLVMValueRef idx = builder.BuildLoad2(state.I64, indexSlot, prefix + "_idx_value");
        StoreBufferByte(state, buffer, builder.BuildSub(LLVMValueRef.CreateConstInt(state.I64, 31, false), idx, prefix + "_write_idx"), builder.BuildAdd(digit, LLVMValueRef.CreateConstInt(state.I64, (byte)'0', false), prefix + "_ascii"));
        builder.BuildStore(builder.BuildAdd(idx, LLVMValueRef.CreateConstInt(state.I64, 1, false), prefix + "_idx_next"), indexSlot);
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(finishBlock);
        LLVMValueRef count = builder.BuildLoad2(state.I64, indexSlot, prefix + "_count");
        LLVMValueRef startIndex = builder.BuildSub(LLVMValueRef.CreateConstInt(state.I64, 32, false), count, prefix + "_start_index");
        LLVMValueRef startPtr = GetArrayElementPointer(state, bufferType, buffer, startIndex, prefix + "_start_ptr");
        return EmitHeapStringSliceFromBytesPointer(state, startPtr, count, prefix + "_string");
    }

    private static LLVMValueRef EmitHeapStringSliceFromBytesPointer(LlvmCodegenState state, LLVMValueRef bytesPtr, LLVMValueRef len, string prefix)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef stringRef = EmitAllocDynamic(state, builder.BuildAdd(len, LLVMValueRef.CreateConstInt(state.I64, 8, false), prefix + "_size"));
        StoreMemory(state, stringRef, 0, len, prefix + "_len");
        EmitCopyBytes(state, GetStringBytesPointer(state, stringRef, prefix + "_dest"), bytesPtr, len, prefix + "_copy");
        return stringRef;
    }

    private static void EmitConditionalWrite(LlvmCodegenState state, LLVMValueRef condition, string whenTrue, string whenFalse, bool appendNewline)
    {
        var trueBlock = state.Function.AppendBasicBlock("bool_true");
        var falseBlock = state.Function.AppendBasicBlock("bool_false");
        var continueBlock = state.Function.AppendBasicBlock("bool_continue");
        state.Target.Builder.BuildCondBr(condition, trueBlock, falseBlock);

        state.Target.Builder.PositionAtEnd(trueBlock);
        EmitWriteBytes(
            state,
            EmitStackByteArray(state, System.Text.Encoding.UTF8.GetBytes(whenTrue)),
            LLVMValueRef.CreateConstInt(state.I64, (ulong)whenTrue.Length, false));
        if (appendNewline)
        {
            EmitWriteBytes(state, EmitStackByteArray(state, [10]), LLVMValueRef.CreateConstInt(state.I64, 1, false));
        }
        state.Target.Builder.BuildBr(continueBlock);

        state.Target.Builder.PositionAtEnd(falseBlock);
        EmitWriteBytes(state, EmitStackByteArray(state, System.Text.Encoding.UTF8.GetBytes(whenFalse)), LLVMValueRef.CreateConstInt(state.I64, (ulong)whenFalse.Length, false));
        if (appendNewline)
        {
            EmitWriteBytes(state, EmitStackByteArray(state, [10]), LLVMValueRef.CreateConstInt(state.I64, 1, false));
        }
        state.Target.Builder.BuildBr(continueBlock);

        state.Target.Builder.PositionAtEnd(continueBlock);
    }

    private static LLVMValueRef EmitStackStringObject(LlvmCodegenState state, string value)
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(value);
        LLVMTypeRef objectType = LLVMTypeRef.CreateArray(state.I8, (uint)(utf8.Length + 8));
        LLVMValueRef storage = state.Target.Builder.BuildAlloca(objectType, "str_obj");
        LLVMValueRef lenPtr = state.Target.Builder.BuildBitCast(storage, state.I64Ptr, "str_obj_len_ptr");
        state.Target.Builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, (ulong)utf8.Length, false), lenPtr);
        LLVMValueRef bytesPtr = GetArrayElementPointer(state, objectType, storage, LLVMValueRef.CreateConstInt(state.I64, 8, false), "str_obj_bytes");
        for (int i = 0; i < utf8.Length; i++)
        {
            LLVMValueRef cellPtr = state.Target.Builder.BuildGEP2(
                state.I8,
                bytesPtr,
                new[]
                {
                    LLVMValueRef.CreateConstInt(state.I64, (ulong)i, false)
                },
                $"str_byte_ptr_{i}");
            state.Target.Builder.BuildStore(LLVMValueRef.CreateConstInt(state.I8, utf8[i], false), cellPtr);
        }

        return state.Target.Builder.BuildPtrToInt(storage, state.I64, "str_obj_i64");
    }

    private static LLVMValueRef EmitStackByteArray(LlvmCodegenState state, IReadOnlyList<byte> bytes)
    {
        LLVMTypeRef arrayType = LLVMTypeRef.CreateArray(state.I8, (uint)bytes.Count);
        LLVMValueRef storage = state.Target.Builder.BuildAlloca(arrayType, "byte_array");
        for (int i = 0; i < bytes.Count; i++)
        {
            LLVMValueRef ptr = GetArrayElementPointer(state, arrayType, storage, LLVMValueRef.CreateConstInt(state.I64, (ulong)i, false), $"byte_{i}");
            state.Target.Builder.BuildStore(LLVMValueRef.CreateConstInt(state.I8, bytes[i], false), ptr);
        }

        return GetArrayElementPointer(state, arrayType, storage, LLVMValueRef.CreateConstInt(state.I64, 0, false), "byte_array_ptr");
    }

    private static void StoreBufferByte(LlvmCodegenState state, LLVMValueRef buffer, LLVMValueRef index, byte value)
    {
        StoreBufferByte(state, buffer, index, LLVMValueRef.CreateConstInt(state.I64, value, false));
    }

    private static void StoreBufferByte(LlvmCodegenState state, LLVMValueRef buffer, LLVMValueRef index, LLVMValueRef value)
    {
        LLVMValueRef ptr = GetArrayElementPointer(state, LLVMTypeRef.CreateArray(state.I8, 32), buffer, index, "buf_ptr");
        LLVMValueRef byteValue = value.TypeOf.Kind == LLVMTypeKind.LLVMIntegerTypeKind && value.TypeOf.IntWidth == 8
            ? value
            : state.Target.Builder.BuildTrunc(value, state.I8, "to_i8");
        state.Target.Builder.BuildStore(byteValue, ptr);
    }

    private static LLVMValueRef GetArrayElementPointer(LlvmCodegenState state, LLVMTypeRef arrayType, LLVMValueRef storage, LLVMValueRef index, string name)
    {
        return state.Target.Builder.BuildGEP2(
            arrayType,
            storage,
            new[]
            {
                LLVMValueRef.CreateConstInt(state.I64, 0, false),
                index
            },
            name);
    }
}
