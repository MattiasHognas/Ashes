using Ashes.Backend.Llvm.Interop;
using Ashes.Semantics;
using static Ashes.Semantics.IrInst;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{
    /// <summary>
    /// Creates a module-level global constant byte array and returns a pointer
    /// to its first element. The global is marked internal linkage, constant,
    /// and unnamed_addr so LLVM can merge duplicates and place it in .rodata.
    /// </summary>
    private static LlvmValueHandle CreateGlobalConstantBytes(LlvmCodegenState state, IReadOnlyList<byte> bytes, string prefix)
    {
        int id = state.Target.NextGlobalConstantId();
        LlvmTypeHandle arrayType = LlvmApi.ArrayType2(state.I8, (ulong)bytes.Count);

        // Build constant initializer: [N x i8] c"..."
        var elements = new LlvmValueHandle[bytes.Count];
        for (int i = 0; i < bytes.Count; i++)
        {
            elements[i] = LlvmApi.ConstInt(state.I8, bytes[i], 0);
        }

        LlvmValueHandle constArray = LlvmApi.ConstArray2(state.I8, elements);

        // Create a global variable with the constant data
        LlvmValueHandle global = LlvmApi.AddGlobal(state.Target.Module, arrayType, $".{prefix}_{id}");
        LlvmApi.SetInitializer(global, constArray);
        LlvmApi.SetLinkage(global, LlvmLinkage.Internal);
        LlvmApi.SetGlobalConstant(global, 1);
        LlvmApi.SetUnnamedAddr(global, 1); // LocalUnnamedAddr

        // Return pointer to the first byte
        return LlvmApi.BuildGEP2(state.Target.Builder,
            arrayType,
            global,
            [LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0)],
            prefix + "_ptr");
    }

    private static LlvmValueHandle EmitAlloc(LlvmCodegenState state, int sizeBytes)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle sizeConst = LlvmApi.ConstInt(state.I64, (ulong)AlignRuntimeSize(sizeBytes), 0);
        EmitHeapEnsureSpace(state, sizeConst);
        // After EnsureSpace the cursor global points to valid space in the current chunk.
        LlvmValueHandle cursor = LlvmApi.BuildLoad2(builder, state.I64, state.HeapCursorSlot, "heap_cursor_value");
        LlvmValueHandle nextCursor = LlvmApi.BuildAdd(builder, cursor, sizeConst, "heap_cursor_next");
        LlvmApi.BuildStore(builder, nextCursor, state.HeapCursorSlot);
        return cursor;
    }

    private static LlvmValueHandle EmitStackAlloc(LlvmCodegenState state, int sizeBytes, string name)
    {
        int alignedSizeBytes = AlignRuntimeSize(sizeBytes);
        LlvmTypeHandle bufferType = LlvmApi.ArrayType2(state.I64, (ulong)(alignedSizeBytes / 8));
        LlvmValueHandle bufferPtr = LlvmApi.BuildAlloca(state.Target.Builder, bufferType, name);
        LlvmValueHandle bytePtr = LlvmApi.BuildBitCast(state.Target.Builder, bufferPtr, state.I8Ptr, name + "_i8");
        return LlvmApi.BuildPtrToInt(state.Target.Builder, bytePtr, state.I64, name + "_addr");
    }

    private static int AlignRuntimeSize(int sizeBytes)
    {
        return (sizeBytes + 7) & ~7;
    }

    private static LlvmValueHandle EmitAllocAdt(LlvmCodegenState state, int tag, int fieldCount)
    {
        LlvmValueHandle ptr = EmitAlloc(state, (1 + fieldCount) * 8);
        StoreMemory(state, ptr, 0, LlvmApi.ConstInt(state.I64, (ulong)tag, 0), $"adt_tag_{tag}");
        return ptr;
    }

    private static LlvmValueHandle EmitStackAllocAdt(LlvmCodegenState state, int tag, int fieldCount)
    {
        LlvmValueHandle ptr = EmitStackAlloc(state, (1 + fieldCount) * 8, $"adt_stack_{tag}");
        StoreMemory(state, ptr, 0, LlvmApi.ConstInt(state.I64, (ulong)tag, 0), $"adt_stack_tag_{tag}");
        return ptr;
    }

    private static bool StoreMemory(LlvmCodegenState state, LlvmValueHandle baseAddress, int offsetBytes, LlvmValueHandle value, string name)
    {
        LlvmValueHandle ptr = GetMemoryPointer(state, baseAddress, offsetBytes, name + "_ptr");
        LlvmApi.BuildStore(state.Target.Builder, NormalizeToI64(state, value), ptr);
        return false;
    }

    private static LlvmValueHandle LoadMemory(LlvmCodegenState state, LlvmValueHandle baseAddress, int offsetBytes, string name)
    {
        LlvmValueHandle ptr = GetMemoryPointer(state, baseAddress, offsetBytes, name + "_ptr");
        return LlvmApi.BuildLoad2(state.Target.Builder, state.I64, ptr, name);
    }

    private static LlvmValueHandle GetMemoryPointer(LlvmCodegenState state, LlvmValueHandle baseAddress, int offsetBytes, string name)
    {
        LlvmValueHandle basePtr = LlvmApi.BuildIntToPtr(state.Target.Builder, baseAddress, state.I8Ptr, name + "_base");
        LlvmValueHandle bytePtr = LlvmApi.BuildGEP2(state.Target.Builder,
            state.I8,
            basePtr,
            [
                LlvmApi.ConstInt(state.I64, (ulong)offsetBytes, 0)
            ],
            name + "_byte");
        return LlvmApi.BuildBitCast(state.Target.Builder, bytePtr, state.I64Ptr, name);
    }

    private static LlvmValueHandle EmitStringComparison(LlvmCodegenState state, LlvmValueHandle leftRef, LlvmValueHandle rightRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "str_cmp_result");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

        LlvmValueHandle leftLen = LoadStringLength(state, leftRef, "str_cmp_left_len");
        LlvmValueHandle rightLen = LoadStringLength(state, rightRef, "str_cmp_right_len");
        LlvmValueHandle leftBytes = GetStringBytesPointer(state, leftRef, "str_cmp_left_bytes");
        LlvmValueHandle rightBytes = GetStringBytesPointer(state, rightRef, "str_cmp_right_bytes");

        var lenEqBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "str_cmp_len_eq");
        var notEqBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "str_cmp_not_eq");
        var eqBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "str_cmp_eq");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "str_cmp_continue");

        // If lengths differ → not equal
        LlvmValueHandle lenEq = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, leftLen, rightLen, "str_cmp_len_match");
        LlvmApi.BuildCondBr(builder, lenEq, lenEqBlock, notEqBlock);

        LlvmApi.PositionBuilderAtEnd(builder, notEqBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        // Lengths equal → call memcmp to compare byte data
        LlvmApi.PositionBuilderAtEnd(builder, lenEqBlock);
        LlvmTypeHandle i32 = LlvmApi.Int32TypeInContext(state.Target.Context);
        LlvmTypeHandle memcmpType = LlvmApi.FunctionType(i32, [state.I8Ptr, state.I8Ptr, state.I64]);
        LlvmValueHandle memcmpFn = LlvmApi.GetNamedFunction(state.Target.Module, "memcmp");
        LlvmValueHandle cmpResult = LlvmApi.BuildCall2(builder, memcmpType, memcmpFn,
            [leftBytes, rightBytes, leftLen], "str_cmp_memcmp");
        LlvmValueHandle isZero = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, cmpResult, LlvmApi.ConstInt(i32, 0, 0), "str_cmp_is_eq");
        LlvmApi.BuildCondBr(builder, isZero, eqBlock, notEqBlock);

        LlvmApi.PositionBuilderAtEnd(builder, eqBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "str_cmp_result_value");
    }

    private static LlvmValueHandle EmitStringConcat(LlvmCodegenState state, LlvmValueHandle leftRef, LlvmValueHandle rightRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle leftLen = LoadStringLength(state, leftRef, "str_cat_left_len");
        LlvmValueHandle rightLen = LoadStringLength(state, rightRef, "str_cat_right_len");
        LlvmValueHandle totalLen = LlvmApi.BuildAdd(builder, leftLen, rightLen, "str_cat_total_len");
        LlvmValueHandle totalBytes = LlvmApi.BuildAdd(builder, totalLen, LlvmApi.ConstInt(state.I64, 8, 0), "str_cat_total_bytes");
        LlvmValueHandle destRef = EmitAllocDynamic(state, totalBytes);
        StoreMemory(state, destRef, 0, totalLen, "str_cat_len");

        LlvmValueHandle destBytes = GetStringBytesPointer(state, destRef, "str_cat_dest_bytes");
        LlvmValueHandle leftBytes = GetStringBytesPointer(state, leftRef, "str_cat_left_bytes");
        LlvmValueHandle rightBytes = GetStringBytesPointer(state, rightRef, "str_cat_right_bytes");
        EmitCopyBytes(state, destBytes, leftBytes, leftLen, "str_cat_copy_left");
        LlvmValueHandle rightDest = LlvmApi.BuildGEP2(builder, state.I8, destBytes, [leftLen], "str_cat_right_dest");
        EmitCopyBytes(state, rightDest, rightBytes, rightLen, "str_cat_copy_right");
        return destRef;
    }

    private static void EmitCopyBytes(LlvmCodegenState state, LlvmValueHandle destBytes, LlvmValueHandle sourceBytes, LlvmValueHandle length, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // Use llvm.memcpy intrinsic — LLVM will lower this to vectorized
        // copies (rep movsb / movaps) instead of a byte-by-byte loop.
        LlvmApi.BuildMemCpy(builder, destBytes, 1, sourceBytes, 1, length);
    }

    private static LlvmValueHandle LoadStringLength(LlvmCodegenState state, LlvmValueHandle stringRef, string name)
    {
        return LoadMemory(state, stringRef, 0, name);
    }

    private static LlvmValueHandle GetStringBytesPointer(LlvmCodegenState state, LlvmValueHandle stringRef, string name)
    {
        LlvmValueHandle byteAddress = LlvmApi.BuildAdd(state.Target.Builder, stringRef, LlvmApi.ConstInt(state.I64, 8, 0), name + "_addr");
        return LlvmApi.BuildIntToPtr(state.Target.Builder, byteAddress, state.I8Ptr, name);
    }

    private static LlvmValueHandle EmitAllocDynamic(LlvmCodegenState state, LlvmValueHandle sizeBytes)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle normalizedSize = AlignRuntimeSize(state, sizeBytes, "heap_alloc_size");
        EmitHeapEnsureSpace(state, normalizedSize);
        LlvmValueHandle cursor = LlvmApi.BuildLoad2(builder, state.I64, state.HeapCursorSlot, "heap_cursor_value_dyn");
        LlvmValueHandle nextCursor = LlvmApi.BuildAdd(builder, cursor, normalizedSize, "heap_cursor_next_dyn");
        LlvmApi.BuildStore(builder, nextCursor, state.HeapCursorSlot);
        return cursor;
    }

    private static LlvmValueHandle AlignRuntimeSize(LlvmCodegenState state, LlvmValueHandle sizeBytes, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle normalizedSize = NormalizeToI64(state, sizeBytes);
        LlvmValueHandle plusSeven = LlvmApi.BuildAdd(builder, normalizedSize, LlvmApi.ConstInt(state.I64, 7, 0), name + "_plus_7");
        return LlvmApi.BuildAnd(builder, plusSeven, LlvmApi.ConstInt(state.I64, 0xFFFFFFFFFFFFFFF8UL, 0), name + "_aligned");
    }

    /// <summary>
    /// Allocates the initial heap chunk at program entry via mmap (Linux) or VirtualAlloc (Windows).
    /// Sets __ashes_heap_cursor and __ashes_heap_end globals.
    /// The first 8 bytes of each chunk store the base address of the previous chunk (0 for the
    /// first chunk). This linked-list header enables RestoreArenaState to walk back and reclaim
    /// OS chunks that are no longer reachable after an arena reset.
    /// </summary>
    private static void EmitHeapChunkInit(LlvmCodegenState state)
    {
        LlvmValueHandle chunkBase = EmitAllocateOsMemory(state, LlvmApi.ConstInt(state.I64, HeapChunkBytes, 0), "init_heap");
        EmitHeapChunkInitCheck(state, chunkBase);
        // Write prev_base = 0 into the chunk header (first chunk has no predecessor).
        StoreMemory(state, chunkBase, 0, LlvmApi.ConstInt(state.I64, 0, 0), "init_heap_prev_base");
        // Allocations start at offset 8 (after the header).
        LlvmValueHandle cursorStart = LlvmApi.BuildAdd(state.Target.Builder, chunkBase,
            LlvmApi.ConstInt(state.I64, 8, 0), "init_heap_cursor_start");
        LlvmApi.BuildStore(state.Target.Builder, cursorStart, state.HeapCursorSlot);
        LlvmValueHandle chunkEnd = LlvmApi.BuildAdd(state.Target.Builder, chunkBase,
            LlvmApi.ConstInt(state.I64, HeapChunkBytes, 0), "init_heap_end");
        LlvmApi.BuildStore(state.Target.Builder, chunkEnd, state.HeapEndSlot);
    }

    /// <summary>
    /// Ensures the current heap chunk has enough space for sizeBytes.
    /// If cursor + size would exceed the chunk end, allocates new chunk(s) from the OS
    /// until the request fits in the current chunk.
    /// </summary>
    private static void EmitHeapEnsureSpace(LlvmCodegenState state, LlvmValueHandle sizeBytes)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        var checkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "heap_check");
        var growBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "heap_grow");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "heap_ok");

        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkBlock);
        LlvmValueHandle cursor = LlvmApi.BuildLoad2(builder, state.I64, state.HeapCursorSlot, "heap_check_cursor");
        LlvmValueHandle needed = LlvmApi.BuildAdd(builder, cursor, sizeBytes, "heap_check_needed");
        LlvmValueHandle heapEnd = LlvmApi.BuildLoad2(builder, state.I64, state.HeapEndSlot, "heap_end");
        LlvmValueHandle overflow = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, needed, heapEnd, "heap_overflow");
        LlvmApi.BuildCondBr(builder, overflow, growBlock, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, growBlock);
        EmitHeapGrow(state);
        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
    }

    private static readonly byte[] HeapAllocFailedMessage =
        "Runtime error: failed to allocate heap memory from OS\n"u8.ToArray();

    /// <summary>
    /// Allocates a new heap chunk from the OS and updates cursor/end globals.
    /// Writes the current chunk's base address into the new chunk's header so that
    /// <see cref="EmitRestoreArenaState"/> can walk back and reclaim abandoned chunks.
    /// </summary>
    private static void EmitHeapGrow(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // Capture the current chunk's base before allocating the new one.
        LlvmValueHandle prevEnd = LlvmApi.BuildLoad2(builder, state.I64, state.HeapEndSlot, "grow_heap_prev_end");
        LlvmValueHandle prevBase = LlvmApi.BuildSub(builder, prevEnd,
            LlvmApi.ConstInt(state.I64, HeapChunkBytes, 0), "grow_heap_prev_base");
        LlvmValueHandle chunkBase = EmitAllocateOsMemory(state, LlvmApi.ConstInt(state.I64, HeapChunkBytes, 0), "grow_heap");
        EmitHeapChunkInitCheck(state, chunkBase);
        // Store prevBase into the new chunk's header (linked-list link).
        StoreMemory(state, chunkBase, 0, prevBase, "grow_heap_prev_base_hdr");
        // Allocations start at offset 8 (after the header).
        LlvmValueHandle cursorStart = LlvmApi.BuildAdd(builder, chunkBase,
            LlvmApi.ConstInt(state.I64, 8, 0), "grow_heap_cursor_start");
        LlvmApi.BuildStore(builder, cursorStart, state.HeapCursorSlot);
        LlvmValueHandle chunkEnd = LlvmApi.BuildAdd(builder, chunkBase,
            LlvmApi.ConstInt(state.I64, HeapChunkBytes, 0), "grow_heap_end");
        LlvmApi.BuildStore(builder, chunkEnd, state.HeapEndSlot);
    }

    /// <summary>
    /// Saves the current heap cursor and end pointers into local slots.
    /// Used at ownership scope entry for arena-based deallocation.
    /// </summary>
    private static bool EmitSaveArenaState(LlvmCodegenState state, int cursorLocalSlot, int endLocalSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle cursor = LlvmApi.BuildLoad2(builder, state.I64, state.HeapCursorSlot, "arena_save_cursor");
        LlvmApi.BuildStore(builder, cursor, state.LocalSlots[cursorLocalSlot]);
        LlvmValueHandle end = LlvmApi.BuildLoad2(builder, state.I64, state.HeapEndSlot, "arena_save_end");
        LlvmApi.BuildStore(builder, end, state.LocalSlots[endLocalSlot]);
        return false;
    }

    /// <summary>
    /// Restores the heap cursor and end pointers from local slots previously saved
    /// by <see cref="EmitSaveArenaState"/>. This resets the bump allocator to the
    /// scope-entry watermark, effectively freeing all heap memory allocated since
    /// the matching SaveArenaState.
    ///
    /// <para>
    /// Before resetting, the current heap end is saved to <paramref name="preRestoreEndSlot"/>
    /// so that a subsequent <see cref="EmitReclaimArenaChunks"/> can determine which
    /// OS chunks to free. This instruction does NOT free OS chunks itself — that is
    /// deferred to <see cref="EmitReclaimArenaChunks"/> so that any intervening
    /// <see cref="EmitCopyOutArena"/> can safely read from the not-yet-freed chunks.
    /// </para>
    /// </summary>
    private static bool EmitRestoreArenaState(LlvmCodegenState state, int cursorLocalSlot, int endLocalSlot, int preRestoreEndSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        // Save the current heap end before resetting — needed by ReclaimArenaChunks.
        LlvmValueHandle currentEnd = LlvmApi.BuildLoad2(builder, state.I64, state.HeapEndSlot, "arena_pre_restore_end");
        LlvmApi.BuildStore(builder, currentEnd, state.LocalSlots[preRestoreEndSlot]);

        // Reset cursor and end globals to the saved watermark.
        LlvmValueHandle savedCursor = LlvmApi.BuildLoad2(builder, state.I64, state.LocalSlots[cursorLocalSlot], "arena_restore_cursor");
        LlvmValueHandle savedEnd = LlvmApi.BuildLoad2(builder, state.I64, state.LocalSlots[endLocalSlot], "arena_restore_end");
        LlvmApi.BuildStore(builder, savedCursor, state.HeapCursorSlot);
        LlvmApi.BuildStore(builder, savedEnd, state.HeapEndSlot);
        return false;
    }

    /// <summary>
    /// Frees OS chunks that were allocated between the saved watermark and the
    /// pre-restore heap state. Called AFTER <see cref="EmitRestoreArenaState"/> and
    /// any <see cref="EmitCopyOutArena"/> instructions.
    ///
    /// <para>
    /// When the pre-restore end matches the saved end (same chunk — fast path),
    /// no chunks need to be freed. When they differ (slow path), walks the chunk
    /// linked list from the pre-restore chunk back to the saved chunk, calling
    /// <c>munmap</c> (Linux) or <c>VirtualFree</c> (Windows) on each abandoned chunk.
    /// </para>
    /// </summary>
    private static bool EmitReclaimArenaChunks(LlvmCodegenState state, int savedEndSlot, int preRestoreEndSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        LlvmValueHandle savedEnd = LlvmApi.BuildLoad2(builder, state.I64, state.LocalSlots[savedEndSlot], "reclaim_saved_end");
        LlvmValueHandle preRestoreEnd = LlvmApi.BuildLoad2(builder, state.I64, state.LocalSlots[preRestoreEndSlot], "reclaim_pre_restore_end");

        // Fast path: same chunk — nothing to free.
        LlvmValueHandle sameChunk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, preRestoreEnd, savedEnd, "reclaim_same_chunk");

        var freeChunksBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "reclaim_free_chunks");
        var reclaimDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "reclaim_done");
        LlvmApi.BuildCondBr(builder, sameChunk, reclaimDoneBlock, freeChunksBlock);

        // Slow path: abandoned chunks exist — walk the linked list and free them.
        LlvmApi.PositionBuilderAtEnd(builder, freeChunksBlock);
        LlvmValueHandle curEndSlot = LlvmApi.BuildAlloca(builder, state.I64, "reclaim_cur_end_slot");
        LlvmApi.BuildStore(builder, preRestoreEnd, curEndSlot);
        var loopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "reclaim_free_loop");
        LlvmApi.BuildBr(builder, loopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBlock);
        LlvmValueHandle curEnd = LlvmApi.BuildLoad2(builder, state.I64, curEndSlot, "reclaim_loop_cur_end");
        LlvmValueHandle curBase = LlvmApi.BuildSub(builder, curEnd, LlvmApi.ConstInt(state.I64, HeapChunkBytes, 0), "reclaim_loop_cur_base");
        // Read prev_base from the chunk header (first 8 bytes of the chunk), then free the chunk.
        LlvmValueHandle prevBase = LoadMemory(state, curBase, 0, "reclaim_loop_prev_base");
        EmitFreeOsMemory(state, curBase, HeapChunkBytes, "reclaim_free_chunk");
        LlvmValueHandle nextEnd = LlvmApi.BuildAdd(builder, prevBase, LlvmApi.ConstInt(state.I64, HeapChunkBytes, 0), "reclaim_loop_next_end");
        LlvmApi.BuildStore(builder, nextEnd, curEndSlot);
        LlvmValueHandle doneFreeing = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, nextEnd, savedEnd, "reclaim_loop_done");
        LlvmApi.BuildCondBr(builder, doneFreeing, reclaimDoneBlock, loopBlock);

        // Merge point.
        LlvmApi.PositionBuilderAtEnd(builder, reclaimDoneBlock);
        return false;
    }

    /// <summary>
    /// Copies a heap object to a fresh allocation starting at the arena watermark.
    /// Called AFTER <see cref="EmitRestoreArenaState"/> but BEFORE
    /// <see cref="EmitReclaimArenaChunks"/>: the cursor is at the watermark W, and
    /// OS chunks have not yet been freed, so the source bytes at
    /// <paramref name="srcTemp"/> are still physically readable. Because dest ≤ src,
    /// a forward memcpy is always safe with no destructive overlap.
    ///
    /// <para>
    /// For strings (<paramref name="staticSizeBytes"/> == -1): reads the length field
    /// from the source object and allocates <c>8 + length</c> bytes.
    /// For fixed-size objects (<paramref name="staticSizeBytes"/> &gt; 0): allocates
    /// exactly <paramref name="staticSizeBytes"/> bytes (e.g. 16 for a cons cell).
    /// A nil (0) source pointer is passed through unchanged — this handles empty
    /// lists in TCO copy-out where the tail pointer is nil.
    /// </para>
    /// </summary>
    private static LlvmValueHandle EmitCopyOutArena(LlvmCodegenState state, int srcTemp, int staticSizeBytes)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle srcPtr = LoadTemp(state, srcTemp);

        // Fixed-size objects (e.g. cons cells) may have a nil source pointer —
        // for example, an empty list tail in a TCO iteration. Guard against
        // copying from address 0 by branching around the alloc+memcpy.
        if (staticSizeBytes > 0)
        {
            LlvmValueHandle isNil = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
                srcPtr, LlvmApi.ConstInt(state.I64, 0, 0), "copy_out_nil_check");

            // Alloca to hold the result across both paths — no phi node binding
            // available; mem2reg promotes this to a phi automatically.
            LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "copy_out_result_slot");
            LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

            var copyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_out_do");
            var mergeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_out_merge");
            LlvmApi.BuildCondBr(builder, isNil, mergeBlock, copyBlock);

            // Non-nil path: allocate and copy.
            LlvmApi.PositionBuilderAtEnd(builder, copyBlock);
            LlvmValueHandle sizeBytes = LlvmApi.ConstInt(state.I64, (ulong)staticSizeBytes, 0);
            LlvmValueHandle destPtr = EmitAllocDynamic(state, sizeBytes);
            LlvmValueHandle srcPtrBytes = LlvmApi.BuildIntToPtr(builder, srcPtr, state.I8Ptr, "copy_out_src_ptr");
            LlvmValueHandle destPtrBytes = LlvmApi.BuildIntToPtr(builder, destPtr, state.I8Ptr, "copy_out_dest_ptr");
            EmitCopyBytes(state, destPtrBytes, srcPtrBytes, sizeBytes, "copy_out");
            LlvmApi.BuildStore(builder, destPtr, resultSlot);
            LlvmApi.BuildBr(builder, mergeBlock);

            // Merge: load result (0 for nil path, destPtr for copy path).
            LlvmApi.PositionBuilderAtEnd(builder, mergeBlock);
            return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "copy_out_result");
        }

        // Dynamic size (strings): source is never nil — Ashes strings are always
        // heap-allocated {length, bytes} structs; the type system has no nullable
        // string representation, so every TStr value is a valid non-zero pointer.
        return EmitCopyOutStringValue(state, srcPtr);
    }

    /// <summary>
    /// Copies a string value ({length:i64, bytes…}) from the arena to a fresh allocation.
    /// Reads the length field to determine total size (8 + length), allocates that many bytes,
    /// and memcpy's the entire string. The source pointer must be non-nil.
    /// </summary>
    private static LlvmValueHandle EmitCopyOutStringValue(LlvmCodegenState state, LlvmValueHandle srcPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle length = LoadMemory(state, srcPtr, 0, "copy_out_str_len");
        LlvmValueHandle dynSize = LlvmApi.BuildAdd(builder, length, LlvmApi.ConstInt(state.I64, 8, 0), "copy_out_str_total");

        LlvmValueHandle dynDest = EmitAllocDynamic(state, dynSize);
        LlvmValueHandle dynSrcBytes = LlvmApi.BuildIntToPtr(builder, srcPtr, state.I8Ptr, "copy_out_src_ptr");
        LlvmValueHandle dynDestBytes = LlvmApi.BuildIntToPtr(builder, dynDest, state.I8Ptr, "copy_out_dest_ptr");
        EmitCopyBytes(state, dynDestBytes, dynSrcBytes, dynSize, "copy_out");
        return dynDest;
    }

    /// <summary>
    /// Deep-copies an entire cons-cell chain out of the arena. Uses a three-phase
    /// approach to avoid aliasing between source and destination cells (which share
    /// the same arena region after RestoreArenaState resets the cursor):
    /// <para>
    /// <b>Count:</b> Walk the source list to count cells (N).
    /// </para>
    /// <para>
    /// <b>Cache:</b> Stack-allocate N i64 slots via dynamic alloca, then
    /// walk the source list again, storing each head value into the buffer. After
    /// this phase all source data has been read; the buffer is on the stack, not
    /// in the arena, so it is safe from arena overwrites.
    /// </para>
    /// <para>
    /// <b>Build:</b> Allocate N fresh 16-byte arena cells, populating each
    /// from the cached head values and linking tail pointers. When
    /// <paramref name="headCopy"/> is <see cref="ListHeadCopyKind.String"/>, each
    /// cached head (a string pointer) is also copied to a fresh allocation. When
    /// <paramref name="headCopy"/> is <see cref="ListHeadCopyKind.InnerList"/>, each
    /// cached head (an inner list pointer) is deep-copied recursively with inline heads.
    /// </para>
    /// </summary>
    private static LlvmValueHandle EmitCopyOutList(LlvmCodegenState state, int srcTemp, ListHeadCopyKind headCopy)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle srcPtr = LoadTemp(state, srcTemp);
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle one = LlvmApi.ConstInt(state.I64, 1, 0);
        LlvmValueHandle cellSize = LlvmApi.ConstInt(state.I64, 16, 0);

        // Guard: if the source list is nil, return nil immediately.
        LlvmValueHandle srcIsNil = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            srcPtr, zero, "copy_list_src_nil");
        LlvmValueHandle overallResultSlot = LlvmApi.BuildAlloca(builder, state.I64, "copy_list_overall_result");
        LlvmApi.BuildStore(builder, zero, overallResultSlot);
        var copyListStart = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_list_start");
        var copyListFinal = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_list_final");
        LlvmApi.BuildCondBr(builder, srcIsNil, copyListFinal, copyListStart);

        LlvmApi.PositionBuilderAtEnd(builder, copyListStart);

        // ── Count source cells ────────────────────────────────────────
        LlvmValueHandle countSlot = LlvmApi.BuildAlloca(builder, state.I64, "copy_list_count");
        LlvmApi.BuildStore(builder, zero, countSlot);
        LlvmValueHandle countCurSlot = LlvmApi.BuildAlloca(builder, state.I64, "copy_list_count_cur");
        LlvmApi.BuildStore(builder, srcPtr, countCurSlot);

        var countHead = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_list_count_head");
        var countBody = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_list_count_body");
        var countDone = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_list_count_done");
        LlvmApi.BuildBr(builder, countHead);

        LlvmApi.PositionBuilderAtEnd(builder, countHead);
        LlvmValueHandle countCur = LlvmApi.BuildLoad2(builder, state.I64, countCurSlot, "copy_list_count_cur_val");
        LlvmValueHandle countIsNil = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            countCur, zero, "copy_list_count_nil");
        LlvmApi.BuildCondBr(builder, countIsNil, countDone, countBody);

        LlvmApi.PositionBuilderAtEnd(builder, countBody);
        LlvmValueHandle oldCount = LlvmApi.BuildLoad2(builder, state.I64, countSlot, "copy_list_count_old");
        LlvmValueHandle newCount = LlvmApi.BuildAdd(builder, oldCount, one, "copy_list_count_inc");
        LlvmApi.BuildStore(builder, newCount, countSlot);
        LlvmValueHandle countTail = LoadMemory(state, countCur, 8, "copy_list_count_tail");
        LlvmApi.BuildStore(builder, countTail, countCurSlot);
        LlvmApi.BuildBr(builder, countHead);

        LlvmApi.PositionBuilderAtEnd(builder, countDone);
        LlvmValueHandle totalCells = LlvmApi.BuildLoad2(builder, state.I64, countSlot, "copy_list_total_cells");

        // ── Cache head values into a stack-allocated buffer ─────────────
        // Dynamic alloca: allocate totalCells × i64 on the stack.
        LlvmValueHandle headBuf = LlvmApi.BuildArrayAlloca(builder, state.I64, totalCells, "copy_list_head_buf");
        LlvmValueHandle cacheCurSlot = LlvmApi.BuildAlloca(builder, state.I64, "copy_list_cache_cur");
        LlvmApi.BuildStore(builder, srcPtr, cacheCurSlot);
        LlvmValueHandle cacheIdxSlot = LlvmApi.BuildAlloca(builder, state.I64, "copy_list_cache_idx");
        LlvmApi.BuildStore(builder, zero, cacheIdxSlot);

        var cacheHead = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_list_cache_head");
        var cacheBody = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_list_cache_body");
        var cacheDone = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_list_cache_done");
        LlvmApi.BuildBr(builder, cacheHead);

        LlvmApi.PositionBuilderAtEnd(builder, cacheHead);
        LlvmValueHandle cacheCur = LlvmApi.BuildLoad2(builder, state.I64, cacheCurSlot, "copy_list_cache_cur_val");
        LlvmValueHandle cacheIsNil = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            cacheCur, zero, "copy_list_cache_nil");
        LlvmApi.BuildCondBr(builder, cacheIsNil, cacheDone, cacheBody);

        LlvmApi.PositionBuilderAtEnd(builder, cacheBody);
        LlvmValueHandle headVal = LoadMemory(state, cacheCur, 0, "copy_list_cache_head_val");
        LlvmValueHandle cacheIdx = LlvmApi.BuildLoad2(builder, state.I64, cacheIdxSlot, "copy_list_cache_idx_val");
        LlvmValueHandle bufSlot = LlvmApi.BuildGEP2(builder, state.I64, headBuf, [cacheIdx], "copy_list_buf_slot");
        LlvmApi.BuildStore(builder, headVal, bufSlot);
        LlvmValueHandle nextCacheIdx = LlvmApi.BuildAdd(builder, cacheIdx, one, "copy_list_cache_idx_inc");
        LlvmApi.BuildStore(builder, nextCacheIdx, cacheIdxSlot);
        LlvmValueHandle cacheTail = LoadMemory(state, cacheCur, 8, "copy_list_cache_tail");
        LlvmApi.BuildStore(builder, cacheTail, cacheCurSlot);
        LlvmApi.BuildBr(builder, cacheHead);

        LlvmApi.PositionBuilderAtEnd(builder, cacheDone);

        // ── Build destination list from cached head values ──────────────
        // Arena allocations happen here. Source cells are never read again.
        LlvmValueHandle buildIdxSlot = LlvmApi.BuildAlloca(builder, state.I64, "copy_list_build_idx");
        LlvmApi.BuildStore(builder, zero, buildIdxSlot);
        LlvmValueHandle prevCellSlot = LlvmApi.BuildAlloca(builder, state.I64, "copy_list_prev");
        LlvmApi.BuildStore(builder, zero, prevCellSlot);

        // Eagerly allocate the first cell from headBuf[0].
        LlvmValueHandle firstHeadSlot = LlvmApi.BuildGEP2(builder, state.I64, headBuf, [zero], "copy_list_first_head_slot");
        LlvmValueHandle firstHeadVal = LlvmApi.BuildLoad2(builder, state.I64, firstHeadSlot, "copy_list_first_head_val");
        LlvmValueHandle firstHeadCopied = EmitCopyOutListHead(state, firstHeadVal, headCopy);
        LlvmValueHandle firstCell = EmitAllocDynamic(state, cellSize);
        StoreMemory(state, firstCell, 0, firstHeadCopied, "copy_list_store_first_head");
        StoreMemory(state, firstCell, 8, zero, "copy_list_store_first_tail");
        LlvmApi.BuildStore(builder, firstCell, prevCellSlot);
        LlvmApi.BuildStore(builder, one, buildIdxSlot);

        var buildHead = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_list_build_head");
        var buildBody = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_list_build_body");
        var buildDone = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_list_build_done");
        LlvmApi.BuildBr(builder, buildHead);

        LlvmApi.PositionBuilderAtEnd(builder, buildHead);
        LlvmValueHandle buildIdx = LlvmApi.BuildLoad2(builder, state.I64, buildIdxSlot, "copy_list_build_idx_val");
        LlvmValueHandle buildDoneCheck = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge,
            buildIdx, totalCells, "copy_list_build_done_check");
        LlvmApi.BuildCondBr(builder, buildDoneCheck, buildDone, buildBody);

        LlvmApi.PositionBuilderAtEnd(builder, buildBody);
        LlvmValueHandle buildHeadSlot = LlvmApi.BuildGEP2(builder, state.I64, headBuf, [buildIdx], "copy_list_build_head_slot");
        LlvmValueHandle buildHeadVal = LlvmApi.BuildLoad2(builder, state.I64, buildHeadSlot, "copy_list_build_head_val");
        LlvmValueHandle buildHeadCopied = EmitCopyOutListHead(state, buildHeadVal, headCopy);
        LlvmValueHandle newCell = EmitAllocDynamic(state, cellSize);
        StoreMemory(state, newCell, 0, buildHeadCopied, "copy_list_store_head");
        StoreMemory(state, newCell, 8, zero, "copy_list_store_tail");

        // Link: prevCell.tail = newCell
        LlvmValueHandle prevCell = LlvmApi.BuildLoad2(builder, state.I64, prevCellSlot, "copy_list_prev_val");
        StoreMemory(state, prevCell, 8, newCell, "copy_list_link_tail");

        // Advance
        LlvmApi.BuildStore(builder, newCell, prevCellSlot);
        LlvmValueHandle nextBuildIdx = LlvmApi.BuildAdd(builder, buildIdx, one, "copy_list_build_idx_inc");
        LlvmApi.BuildStore(builder, nextBuildIdx, buildIdxSlot);
        LlvmApi.BuildBr(builder, buildHead);

        // Done
        LlvmApi.PositionBuilderAtEnd(builder, buildDone);
        LlvmApi.BuildStore(builder, firstCell, overallResultSlot);
        LlvmApi.BuildBr(builder, copyListFinal);

        LlvmApi.PositionBuilderAtEnd(builder, copyListFinal);
        return LlvmApi.BuildLoad2(builder, state.I64, overallResultSlot, "copy_list_result_val");
    }

    /// <summary>
    /// Copies a list head value according to the specified <paramref name="headCopy"/> kind.
    /// For <see cref="ListHeadCopyKind.Inline"/>, returns the value unchanged.
    /// For <see cref="ListHeadCopyKind.String"/>, copies the string to a fresh allocation.
    /// For <see cref="ListHeadCopyKind.InnerList"/>, deep-copies the inner cons-cell chain
    /// (with inline/copy-type heads) via the same three-phase algorithm.
    /// </summary>
    private static LlvmValueHandle EmitCopyOutListHead(LlvmCodegenState state, LlvmValueHandle headVal, ListHeadCopyKind headCopy)
    {
        switch (headCopy)
        {
            case ListHeadCopyKind.Inline:
                return headVal;

            case ListHeadCopyKind.String:
                return EmitCopyOutStringValue(state, headVal);

            case ListHeadCopyKind.InnerList:
                return EmitCopyOutListFromValue(state, headVal);

            default:
                return headVal;
        }
    }

    /// <summary>
    /// Deep-copies a cons-cell chain starting from the given pointer value (not a temp slot).
    /// Uses the same three-phase count/cache/build algorithm as <see cref="EmitCopyOutList"/>
    /// but with inline (copy-type) head values only. Used for inner list elements in
    /// <see cref="ListHeadCopyKind.InnerList"/> copy-out.
    /// </summary>
    private static LlvmValueHandle EmitCopyOutListFromValue(LlvmCodegenState state, LlvmValueHandle srcPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle one = LlvmApi.ConstInt(state.I64, 1, 0);
        LlvmValueHandle cellSize = LlvmApi.ConstInt(state.I64, 16, 0);

        // Guard: if the source list is nil, return nil immediately.
        LlvmValueHandle srcIsNil = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            srcPtr, zero, "copy_inner_src_nil");
        LlvmValueHandle overallResultSlot = LlvmApi.BuildAlloca(builder, state.I64, "copy_inner_overall_result");
        LlvmApi.BuildStore(builder, zero, overallResultSlot);
        var copyListStart = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_inner_start");
        var copyListFinal = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_inner_final");
        LlvmApi.BuildCondBr(builder, srcIsNil, copyListFinal, copyListStart);

        LlvmApi.PositionBuilderAtEnd(builder, copyListStart);

        // ── Count source cells ────────────────────────────────────────
        LlvmValueHandle countSlot = LlvmApi.BuildAlloca(builder, state.I64, "copy_inner_count");
        LlvmApi.BuildStore(builder, zero, countSlot);
        LlvmValueHandle countCurSlot = LlvmApi.BuildAlloca(builder, state.I64, "copy_inner_count_cur");
        LlvmApi.BuildStore(builder, srcPtr, countCurSlot);

        var countHead = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_inner_count_head");
        var countBody = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_inner_count_body");
        var countDone = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_inner_count_done");
        LlvmApi.BuildBr(builder, countHead);

        LlvmApi.PositionBuilderAtEnd(builder, countHead);
        LlvmValueHandle countCur = LlvmApi.BuildLoad2(builder, state.I64, countCurSlot, "copy_inner_count_cur_val");
        LlvmValueHandle countIsNil = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            countCur, zero, "copy_inner_count_nil");
        LlvmApi.BuildCondBr(builder, countIsNil, countDone, countBody);

        LlvmApi.PositionBuilderAtEnd(builder, countBody);
        LlvmValueHandle oldCount = LlvmApi.BuildLoad2(builder, state.I64, countSlot, "copy_inner_count_old");
        LlvmValueHandle newCount = LlvmApi.BuildAdd(builder, oldCount, one, "copy_inner_count_inc");
        LlvmApi.BuildStore(builder, newCount, countSlot);
        LlvmValueHandle countTail = LoadMemory(state, countCur, 8, "copy_inner_count_tail");
        LlvmApi.BuildStore(builder, countTail, countCurSlot);
        LlvmApi.BuildBr(builder, countHead);

        LlvmApi.PositionBuilderAtEnd(builder, countDone);
        LlvmValueHandle totalCells = LlvmApi.BuildLoad2(builder, state.I64, countSlot, "copy_inner_total_cells");

        // ── Cache head values into a stack-allocated buffer ─────────────
        LlvmValueHandle headBuf = LlvmApi.BuildArrayAlloca(builder, state.I64, totalCells, "copy_inner_head_buf");
        LlvmValueHandle cacheCurSlot = LlvmApi.BuildAlloca(builder, state.I64, "copy_inner_cache_cur");
        LlvmApi.BuildStore(builder, srcPtr, cacheCurSlot);
        LlvmValueHandle cacheIdxSlot = LlvmApi.BuildAlloca(builder, state.I64, "copy_inner_cache_idx");
        LlvmApi.BuildStore(builder, zero, cacheIdxSlot);

        var cacheHead = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_inner_cache_head");
        var cacheBody = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_inner_cache_body");
        var cacheDone = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_inner_cache_done");
        LlvmApi.BuildBr(builder, cacheHead);

        LlvmApi.PositionBuilderAtEnd(builder, cacheHead);
        LlvmValueHandle cacheCur = LlvmApi.BuildLoad2(builder, state.I64, cacheCurSlot, "copy_inner_cache_cur_val");
        LlvmValueHandle cacheIsNil = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            cacheCur, zero, "copy_inner_cache_nil");
        LlvmApi.BuildCondBr(builder, cacheIsNil, cacheDone, cacheBody);

        LlvmApi.PositionBuilderAtEnd(builder, cacheBody);
        LlvmValueHandle headVal = LoadMemory(state, cacheCur, 0, "copy_inner_cache_head_val");
        LlvmValueHandle cacheIdx = LlvmApi.BuildLoad2(builder, state.I64, cacheIdxSlot, "copy_inner_cache_idx_val");
        LlvmValueHandle bufSlot = LlvmApi.BuildGEP2(builder, state.I64, headBuf, [cacheIdx], "copy_inner_buf_slot");
        LlvmApi.BuildStore(builder, headVal, bufSlot);
        LlvmValueHandle nextCacheIdx = LlvmApi.BuildAdd(builder, cacheIdx, one, "copy_inner_cache_idx_inc");
        LlvmApi.BuildStore(builder, nextCacheIdx, cacheIdxSlot);
        LlvmValueHandle cacheTail = LoadMemory(state, cacheCur, 8, "copy_inner_cache_tail");
        LlvmApi.BuildStore(builder, cacheTail, cacheCurSlot);
        LlvmApi.BuildBr(builder, cacheHead);

        LlvmApi.PositionBuilderAtEnd(builder, cacheDone);

        // ── Build destination list from cached head values ──────────────
        LlvmValueHandle buildIdxSlot = LlvmApi.BuildAlloca(builder, state.I64, "copy_inner_build_idx");
        LlvmApi.BuildStore(builder, zero, buildIdxSlot);
        LlvmValueHandle prevCellSlot = LlvmApi.BuildAlloca(builder, state.I64, "copy_inner_prev");
        LlvmApi.BuildStore(builder, zero, prevCellSlot);

        LlvmValueHandle firstHeadSlot = LlvmApi.BuildGEP2(builder, state.I64, headBuf, [zero], "copy_inner_first_head_slot");
        LlvmValueHandle firstHeadVal = LlvmApi.BuildLoad2(builder, state.I64, firstHeadSlot, "copy_inner_first_head_val");
        LlvmValueHandle firstCell = EmitAllocDynamic(state, cellSize);
        StoreMemory(state, firstCell, 0, firstHeadVal, "copy_inner_store_first_head");
        StoreMemory(state, firstCell, 8, zero, "copy_inner_store_first_tail");
        LlvmApi.BuildStore(builder, firstCell, prevCellSlot);
        LlvmApi.BuildStore(builder, one, buildIdxSlot);

        var buildHead = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_inner_build_head");
        var buildBody = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_inner_build_body");
        var buildDone = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_inner_build_done");
        LlvmApi.BuildBr(builder, buildHead);

        LlvmApi.PositionBuilderAtEnd(builder, buildHead);
        LlvmValueHandle buildIdx = LlvmApi.BuildLoad2(builder, state.I64, buildIdxSlot, "copy_inner_build_idx_val");
        LlvmValueHandle buildDoneCheck = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge,
            buildIdx, totalCells, "copy_inner_build_done_check");
        LlvmApi.BuildCondBr(builder, buildDoneCheck, buildDone, buildBody);

        LlvmApi.PositionBuilderAtEnd(builder, buildBody);
        LlvmValueHandle buildHeadSlot = LlvmApi.BuildGEP2(builder, state.I64, headBuf, [buildIdx], "copy_inner_build_head_slot");
        LlvmValueHandle buildHeadVal = LlvmApi.BuildLoad2(builder, state.I64, buildHeadSlot, "copy_inner_build_head_val");
        LlvmValueHandle newCell = EmitAllocDynamic(state, cellSize);
        StoreMemory(state, newCell, 0, buildHeadVal, "copy_inner_store_head");
        StoreMemory(state, newCell, 8, zero, "copy_inner_store_tail");

        LlvmValueHandle prevCell = LlvmApi.BuildLoad2(builder, state.I64, prevCellSlot, "copy_inner_prev_val");
        StoreMemory(state, prevCell, 8, newCell, "copy_inner_link_tail");

        LlvmApi.BuildStore(builder, newCell, prevCellSlot);
        LlvmValueHandle nextBuildIdx = LlvmApi.BuildAdd(builder, buildIdx, one, "copy_inner_build_idx_inc");
        LlvmApi.BuildStore(builder, nextBuildIdx, buildIdxSlot);
        LlvmApi.BuildBr(builder, buildHead);

        LlvmApi.PositionBuilderAtEnd(builder, buildDone);
        LlvmApi.BuildStore(builder, firstCell, overallResultSlot);
        LlvmApi.BuildBr(builder, copyListFinal);

        LlvmApi.PositionBuilderAtEnd(builder, copyListFinal);
        return LlvmApi.BuildLoad2(builder, state.I64, overallResultSlot, "copy_inner_result_val");
    }

    /// <summary>
    /// Copies a closure (24 bytes: {code, env, env_size}) and its environment
    /// out of the arena. If the env pointer is non-nil, allocates a fresh env of
    /// env_size bytes and memcpy's it, then allocates a fresh 24-byte closure and
    /// stores the new env pointer.
    /// </summary>
    private static LlvmValueHandle EmitCopyOutClosure(LlvmCodegenState state, int srcTemp)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle srcPtr = LoadTemp(state, srcTemp);

        // Load fields from source closure
        LlvmValueHandle code = LoadMemory(state, srcPtr, 0, "copy_closure_code");
        LlvmValueHandle envPtr = LoadMemory(state, srcPtr, 8, "copy_closure_env");
        LlvmValueHandle envSize = LoadMemory(state, srcPtr, 16, "copy_closure_env_size");

        // Alloca for the new env pointer (nil path: keep 0; non-nil path: new alloc)
        LlvmValueHandle newEnvSlot = LlvmApi.BuildAlloca(builder, state.I64, "copy_closure_new_env_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), newEnvSlot);

        LlvmValueHandle envIsNil = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            envPtr, LlvmApi.ConstInt(state.I64, 0, 0), "copy_closure_env_nil");
        var envCopyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_closure_env_copy");
        var envMergeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "copy_closure_env_merge");
        LlvmApi.BuildCondBr(builder, envIsNil, envMergeBlock, envCopyBlock);

        // Non-nil env: allocate and copy
        LlvmApi.PositionBuilderAtEnd(builder, envCopyBlock);
        LlvmValueHandle newEnv = EmitAllocDynamic(state, envSize);
        LlvmValueHandle envSrcBytes = LlvmApi.BuildIntToPtr(builder, envPtr, state.I8Ptr, "copy_closure_env_src");
        LlvmValueHandle envDestBytes = LlvmApi.BuildIntToPtr(builder, newEnv, state.I8Ptr, "copy_closure_env_dest");
        EmitCopyBytes(state, envDestBytes, envSrcBytes, envSize, "copy_closure_env");
        LlvmApi.BuildStore(builder, newEnv, newEnvSlot);
        LlvmApi.BuildBr(builder, envMergeBlock);

        // Merge: allocate new closure struct
        LlvmApi.PositionBuilderAtEnd(builder, envMergeBlock);
        LlvmValueHandle newEnvPtr = LlvmApi.BuildLoad2(builder, state.I64, newEnvSlot, "copy_closure_new_env");
        LlvmValueHandle closureSize = LlvmApi.ConstInt(state.I64, 24, 0);
        LlvmValueHandle newClosure = EmitAllocDynamic(state, closureSize);
        StoreMemory(state, newClosure, 0, code, "copy_closure_store_code");
        StoreMemory(state, newClosure, 8, newEnvPtr, "copy_closure_store_env");
        StoreMemory(state, newClosure, 16, envSize, "copy_closure_store_env_size");

        return newClosure;
    }

    /// <summary>
    /// TCO-specific: copies a single cons cell (16 bytes) out of the arena and
    /// copies or deep-copies the head value according to <paramref name="headCopy"/>.
    /// <para>
    /// In TCO loops, only the top cons cell (created in the current iteration) is above
    /// the arena watermark. The tail pointer references pre-watermark memory from previous
    /// iterations and is preserved unchanged.
    /// </para>
    /// <para>
    /// For nil source pointers (empty list), returns 0 immediately.
    /// </para>
    /// </summary>
    private static LlvmValueHandle EmitCopyOutTcoListCell(LlvmCodegenState state, int srcTemp, ListHeadCopyKind headCopy)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle srcPtr = LoadTemp(state, srcTemp);
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle cellSize = LlvmApi.ConstInt(state.I64, 16, 0);

        // Nil guard: empty list → return 0.
        LlvmValueHandle isNil = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            srcPtr, zero, "tco_cell_nil_check");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "tco_cell_result_slot");
        LlvmApi.BuildStore(builder, zero, resultSlot);
        var copyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tco_cell_copy");
        var mergeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tco_cell_merge");
        LlvmApi.BuildCondBr(builder, isNil, mergeBlock, copyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, copyBlock);

        // Read head and tail from the source cell.
        LlvmValueHandle oldHead = LoadMemory(state, srcPtr, 0, "tco_cell_old_head");
        LlvmValueHandle oldTail = LoadMemory(state, srcPtr, 8, "tco_cell_old_tail");

        // Copy the head value according to the element type.
        // CopyOutTcoListCell is only emitted for String or InnerList heads;
        // Inline heads use CopyOutArena(16) instead, so Inline here is a bug.
        LlvmValueHandle newHead = headCopy switch
        {
            ListHeadCopyKind.String => EmitCopyOutStringValue(state, oldHead),
            ListHeadCopyKind.InnerList => EmitCopyOutListFromValue(state, oldHead),
            _ => throw new InvalidOperationException(
                $"CopyOutTcoListCell should not be emitted with HeadCopy={headCopy}; " +
                "Inline heads use CopyOutArena(16) instead."),
        };

        // Allocate new 16-byte cons cell and populate.
        LlvmValueHandle newCell = EmitAllocDynamic(state, cellSize);
        StoreMemory(state, newCell, 0, newHead, "tco_cell_store_head");
        StoreMemory(state, newCell, 8, oldTail, "tco_cell_store_tail");
        LlvmApi.BuildStore(builder, newCell, resultSlot);
        LlvmApi.BuildBr(builder, mergeBlock);

        LlvmApi.PositionBuilderAtEnd(builder, mergeBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "tco_cell_result");
    }

    /// <summary>
    /// Frees an OS memory chunk previously allocated by <see cref="EmitAllocateOsMemory"/>.
    /// Linux: <c>munmap(ptr, size)</c> — syscall 11 (x86-64) / 215 (AArch64).
    /// Windows: <c>VirtualFree(ptr, 0, MEM_RELEASE)</c> — <c>dwSize</c> must be 0
    ///   for <c>MEM_RELEASE</c>.
    /// </summary>
    private static void EmitFreeOsMemory(LlvmCodegenState state, LlvmValueHandle basePtr, long sizeBytes, string prefix)
    {
        if (IsLinuxFlavor(state.Flavor))
        {
            EmitLinuxSyscall(state, SyscallMunmap,
                basePtr,
                LlvmApi.ConstInt(state.I64, (ulong)sizeBytes, 0),
                LlvmApi.ConstInt(state.I64, 0, 0), // unused third arg
                prefix + "_munmap");
        }
        else
        {
            LlvmBuilderHandle builder = state.Target.Builder;
            const uint memRelease = 0x8000; // MEM_RELEASE
            LlvmTypeHandle virtualFreeType = LlvmApi.FunctionType(state.I32, [state.I64, state.I64, state.I32]);
            LlvmValueHandle virtualFreePtr = LlvmApi.BuildLoad2(builder,
                LlvmApi.PointerTypeInContext(state.Target.Context, 0),
                state.WindowsVirtualFreeImport,
                prefix + "_vf_ptr");
            LlvmApi.BuildCall2(builder,
                virtualFreeType,
                virtualFreePtr,
                [
                    basePtr,                                          // lpAddress
                    LlvmApi.ConstInt(state.I64, 0, 0),               // dwSize = 0 (MEM_RELEASE requirement)
                    LlvmApi.ConstInt(state.I32, memRelease, 0)        // dwFreeType = MEM_RELEASE
                ],
                prefix + "_vf_call");
        }
    }

    /// <summary>
    /// Checks if the OS memory allocation succeeded. On Linux raw syscalls report failures as
    /// negative errno values in the range [-4095, -1]; on Windows VirtualAlloc returns NULL (0).
    /// Panics with a diagnostic message if the allocation failed.
    /// </summary>
    private static void EmitHeapChunkInitCheck(LlvmCodegenState state, LlvmValueHandle chunkBase)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle failed;

        if (IsLinuxFlavor(state.Flavor))
        {
            LlvmValueHandle linuxErrnoMin = LlvmApi.ConstInt(state.I64, unchecked((ulong)(-4095L)), 1);
            LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 1);
            LlvmValueHandle isErrnoMinOrAbove = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, chunkBase, linuxErrnoMin, "mmap_errno_min_or_above");
            LlvmValueHandle isNegative = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, chunkBase, zero, "mmap_negative");
            failed = LlvmApi.BuildAnd(builder, isErrnoMinOrAbove, isNegative, "mmap_failed");
        }
        else
        {
            LlvmValueHandle failValue = LlvmApi.ConstInt(state.I64, 0, 0); // NULL
            failed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, chunkBase, failValue, "virtualalloc_failed");
        }
        var failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "heap_alloc_fail");
        var okBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "heap_alloc_ok");
        LlvmApi.BuildCondBr(builder, failed, failBlock, okBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        EmitHeapAllocFailedPanic(state);

        LlvmApi.PositionBuilderAtEnd(builder, okBlock);
    }

    private static void EmitHeapAllocFailedPanic(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        if (IsLinuxFlavor(state.Flavor))
        {
            LlvmValueHandle msgPtr = EmitStackByteArray(state, HeapAllocFailedMessage);
            LlvmValueHandle msgLen = LlvmApi.ConstInt(state.I64, (ulong)HeapAllocFailedMessage.Length, 0);
            EmitLinuxSyscall(state, SyscallWrite,
                LlvmApi.ConstInt(state.I64, 2, 0),
                LlvmApi.BuildPtrToInt(builder, msgPtr, state.I64, "oom_msg_i64"),
                msgLen,
                "sys_write_oom");
            EmitExit(state, LlvmApi.ConstInt(state.I64, 1, 0));
        }
        else
        {
            EmitWindowsExitProcess(state, LlvmApi.ConstInt(state.I32, 1, 0));
        }
    }

    /// <summary>
    /// Allocates memory from the OS.
    /// Linux: mmap(NULL, size, PROT_READ|PROT_WRITE, MAP_PRIVATE|MAP_ANONYMOUS, -1, 0)
    /// Windows: VirtualAlloc(NULL, size, MEM_COMMIT|MEM_RESERVE, PAGE_READWRITE)
    /// Returns the base address as an i64.
    /// </summary>
    private static LlvmValueHandle EmitAllocateOsMemory(LlvmCodegenState state, LlvmValueHandle sizeBytes, string prefix)
    {
        if (IsLinuxFlavor(state.Flavor))
        {
            return EmitLinuxMmap(state, sizeBytes, prefix);
        }

        return EmitWindowsVirtualAlloc(state, sizeBytes, prefix);
    }

    private static LlvmValueHandle EmitLinuxMmap(LlvmCodegenState state, LlvmValueHandle sizeBytes, string prefix)
    {
        // mmap(addr=NULL, length, prot=PROT_READ|PROT_WRITE, flags=MAP_PRIVATE|MAP_ANONYMOUS, fd=-1, offset=0)
        const long protReadWrite = 0x1 | 0x2;       // PROT_READ | PROT_WRITE
        const long mapPrivateAnon = 0x02 | 0x20;     // MAP_PRIVATE | MAP_ANONYMOUS
        return EmitLinuxSyscall6(state, SyscallMmap,
            LlvmApi.ConstInt(state.I64, 0, 0),                          // addr = NULL
            sizeBytes,                                                    // length
            LlvmApi.ConstInt(state.I64, (ulong)protReadWrite, 0),        // prot
            LlvmApi.ConstInt(state.I64, (ulong)mapPrivateAnon, 0),       // flags
            LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1),     // fd = -1
            LlvmApi.ConstInt(state.I64, 0, 0),                           // offset = 0
            prefix + "_mmap");
    }

    private static LlvmValueHandle EmitWindowsVirtualAlloc(LlvmCodegenState state, LlvmValueHandle sizeBytes, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // VirtualAlloc(lpAddress=NULL, dwSize, flAllocationType=MEM_COMMIT|MEM_RESERVE, flProtect=PAGE_READWRITE)
        const uint memCommitReserve = 0x1000 | 0x2000; // MEM_COMMIT | MEM_RESERVE
        const uint pageReadWrite = 0x04;                // PAGE_READWRITE

        LlvmTypeHandle virtualAllocType = LlvmApi.FunctionType(state.I64, [state.I64, state.I64, state.I32, state.I32]);
        LlvmValueHandle virtualAllocPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsVirtualAllocImport,
            prefix + "_va_ptr");
        return LlvmApi.BuildCall2(builder,
            virtualAllocType,
            virtualAllocPtr,
            [
                LlvmApi.ConstInt(state.I64, 0, 0),                        // lpAddress = NULL
                NormalizeToI64(state, sizeBytes),                          // dwSize
                LlvmApi.ConstInt(state.I32, memCommitReserve, 0),          // flAllocationType
                LlvmApi.ConstInt(state.I32, pageReadWrite, 0)              // flProtect
            ],
            prefix + "_va_call");
    }

    private static LlvmValueHandle EmitStringToCString(LlvmCodegenState state, LlvmValueHandle stringRef, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle len = LoadStringLength(state, stringRef, prefix + "_len");
        LlvmValueHandle cstrRef = EmitAllocDynamic(state, LlvmApi.BuildAdd(builder, len, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_size"));
        LlvmValueHandle destPtr = LlvmApi.BuildIntToPtr(builder, cstrRef, state.I8Ptr, prefix + "_dest");
        EmitCopyBytes(state, destPtr, GetStringBytesPointer(state, stringRef, prefix + "_src"), len, prefix + "_copy");
        LlvmValueHandle terminatorPtr = LlvmApi.BuildGEP2(builder, state.I8, destPtr, [len], prefix + "_nul_ptr");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I8, 0, 0), terminatorPtr);
        return destPtr;
    }

    private static LlvmValueHandle EmitUnitValue(LlvmCodegenState state)
    {
        return EmitAllocAdt(state, 0, 0);
    }

    private static LlvmValueHandle EmitResultOk(LlvmCodegenState state, LlvmValueHandle value)
    {
        LlvmValueHandle result = EmitAllocAdt(state, 0, 1);
        StoreMemory(state, result, 8, value, "result_ok_value");
        return result;
    }

    private static LlvmValueHandle EmitResultError(LlvmCodegenState state, LlvmValueHandle errorStringRef)
    {
        LlvmValueHandle result = EmitAllocAdt(state, 1, 1);
        StoreMemory(state, result, 8, errorStringRef, "result_error_value");
        return result;
    }

    /// <summary>
    /// Emit a string literal as a read-only global constant in the .rodata section.
    /// The global has layout { i64 length, [N x i8] data } matching the runtime
    /// string representation, so no heap allocation is needed. Since Ashes strings
    /// are immutable, this is always safe for literals.
    /// </summary>
    private static LlvmValueHandle EmitHeapStringLiteral(LlvmCodegenState state, string value)
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(value);

        // Build the constant initializer: { i64 len, [N x i8] data }
        LlvmTypeHandle arrayType = LlvmApi.ArrayType2(state.I8, (ulong)utf8.Length);

        var byteElements = new LlvmValueHandle[utf8.Length];
        for (int i = 0; i < utf8.Length; i++)
        {
            byteElements[i] = LlvmApi.ConstInt(state.I8, utf8[i], 0);
        }

        LlvmValueHandle constLen = LlvmApi.ConstInt(state.I64, (ulong)utf8.Length, 0);
        LlvmValueHandle constData = LlvmApi.ConstArray2(state.I8, byteElements);
        LlvmValueHandle constStruct = LlvmApi.ConstStructInContext(
            state.Target.Context, [constLen, constData]);

        LlvmTypeHandle structType = LlvmApi.StructTypeInContext(
            state.Target.Context, [state.I64, arrayType]);

        int id = state.Target.NextGlobalConstantId();
        LlvmValueHandle global = LlvmApi.AddGlobal(state.Target.Module, structType, $".str_lit_{id}");
        LlvmApi.SetInitializer(global, constStruct);
        LlvmApi.SetLinkage(global, LlvmLinkage.Internal);
        LlvmApi.SetGlobalConstant(global, 1);
        LlvmApi.SetUnnamedAddr(global, 1); // LocalUnnamedAddr — enable merging

        return LlvmApi.BuildPtrToInt(state.Target.Builder, global, state.I64, "str_lit_ref");
    }

    private static LlvmValueHandle EmitHeapStringFromBytes(LlvmCodegenState state, IReadOnlyList<byte> bytes, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle len = LlvmApi.ConstInt(state.I64, (ulong)bytes.Count, 0);
        LlvmValueHandle stringRef = EmitAllocDynamic(state, LlvmApi.BuildAdd(builder, len, LlvmApi.ConstInt(state.I64, 8, 0), prefix + "_size"));
        StoreMemory(state, stringRef, 0, len, prefix + "_len");

        if (bytes.Count > 0)
        {
            LlvmValueHandle destPtr = GetStringBytesPointer(state, stringRef, prefix + "_bytes");
            LlvmValueHandle srcPtr = CreateGlobalConstantBytes(state, bytes, prefix);
            LlvmApi.BuildMemCpy(builder, destPtr, 1, srcPtr, 1, len);
        }

        return stringRef;
    }

    private static LlvmValueHandle GetStringBytesAddress(LlvmCodegenState state, LlvmValueHandle stringRef, string name)
    {
        return LlvmApi.BuildAdd(state.Target.Builder, stringRef, LlvmApi.ConstInt(state.I64, 8, 0), name);
    }

    private static LlvmValueHandle EmitValidateUtf8(LlvmCodegenState state, LlvmValueHandle bytesPtr, LlvmValueHandle len, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_index");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_result");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), indexSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

        var loopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_loop");
        var asciiBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_ascii");
        var twoBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_two");
        var threeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_three");
        var e0Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_e0");
        var edBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_ed");
        var f0Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_f0");
        var fourBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_four");
        var f4Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_f4");
        var validBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_valid");
        var invalidBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_invalid");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_continue");

        LlvmApi.BuildBr(builder, loopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBlock);
        LlvmValueHandle index = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, prefix + "_index_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, index, len, prefix + "_done");
        var inspectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_inspect");
        LlvmApi.BuildCondBr(builder, done, validBlock, inspectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, inspectBlock);
        LlvmValueHandle firstByte = LoadByteAt(state, bytesPtr, index, prefix + "_byte0");
        LlvmValueHandle firstByte64 = LlvmApi.BuildZExt(builder, firstByte, state.I64, prefix + "_byte0_i64");
        LlvmValueHandle isAscii = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, firstByte64, LlvmApi.ConstInt(state.I64, 0x80, 0), prefix + "_is_ascii");
        var nonAsciiBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_non_ascii");
        LlvmApi.BuildCondBr(builder, isAscii, asciiBlock, nonAsciiBlock);

        LlvmApi.PositionBuilderAtEnd(builder, nonAsciiBlock);
        LlvmValueHandle ltC2 = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, firstByte64, LlvmApi.ConstInt(state.I64, 0xC2, 0), prefix + "_lt_c2");
        var geC2Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_ge_c2");
        LlvmApi.BuildCondBr(builder, ltC2, invalidBlock, geC2Block);

        LlvmApi.PositionBuilderAtEnd(builder, geC2Block);
        LlvmValueHandle leDf = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ule, firstByte64, LlvmApi.ConstInt(state.I64, 0xDF, 0), prefix + "_le_df");
        var gtDfBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_gt_df");
        LlvmApi.BuildCondBr(builder, leDf, twoBlock, gtDfBlock);

        LlvmApi.PositionBuilderAtEnd(builder, gtDfBlock);
        LlvmValueHandle isE0 = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, firstByte64, LlvmApi.ConstInt(state.I64, 0xE0, 0), prefix + "_is_e0");
        var afterE0Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_after_e0");
        LlvmApi.BuildCondBr(builder, isE0, e0Block, afterE0Block);

        LlvmApi.PositionBuilderAtEnd(builder, afterE0Block);
        LlvmValueHandle leEc = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ule, firstByte64, LlvmApi.ConstInt(state.I64, 0xEC, 0), prefix + "_le_ec");
        var afterEcBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_after_ec");
        LlvmApi.BuildCondBr(builder, leEc, threeBlock, afterEcBlock);

        LlvmApi.PositionBuilderAtEnd(builder, afterEcBlock);
        LlvmValueHandle isEd = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, firstByte64, LlvmApi.ConstInt(state.I64, 0xED, 0), prefix + "_is_ed");
        var afterEdBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_after_ed");
        LlvmApi.BuildCondBr(builder, isEd, edBlock, afterEdBlock);

        LlvmApi.PositionBuilderAtEnd(builder, afterEdBlock);
        LlvmValueHandle leEf = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ule, firstByte64, LlvmApi.ConstInt(state.I64, 0xEF, 0), prefix + "_le_ef");
        var afterEfBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_after_ef");
        LlvmApi.BuildCondBr(builder, leEf, threeBlock, afterEfBlock);

        LlvmApi.PositionBuilderAtEnd(builder, afterEfBlock);
        LlvmValueHandle isF0 = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, firstByte64, LlvmApi.ConstInt(state.I64, 0xF0, 0), prefix + "_is_f0");
        var afterF0Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_after_f0");
        LlvmApi.BuildCondBr(builder, isF0, f0Block, afterF0Block);

        LlvmApi.PositionBuilderAtEnd(builder, afterF0Block);
        LlvmValueHandle leF3 = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ule, firstByte64, LlvmApi.ConstInt(state.I64, 0xF3, 0), prefix + "_le_f3");
        var afterF3Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_after_f3");
        LlvmApi.BuildCondBr(builder, leF3, fourBlock, afterF3Block);

        LlvmApi.PositionBuilderAtEnd(builder, afterF3Block);
        LlvmValueHandle isF4 = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, firstByte64, LlvmApi.ConstInt(state.I64, 0xF4, 0), prefix + "_is_f4");
        LlvmApi.BuildCondBr(builder, isF4, f4Block, invalidBlock);

        LlvmApi.PositionBuilderAtEnd(builder, asciiBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, index, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_ascii_next"), indexSlot);
        LlvmApi.BuildBr(builder, loopBlock);

        EmitUtf8SequenceValidation(state, bytesPtr, len, indexSlot, 2, 0x80, 0xBF, prefix + "_two", twoBlock, loopBlock, invalidBlock);
        EmitUtf8SequenceValidation(state, bytesPtr, len, indexSlot, 3, 0x80, 0xBF, prefix + "_three", threeBlock, loopBlock, invalidBlock);
        EmitUtf8SequenceValidation(state, bytesPtr, len, indexSlot, 3, 0xA0, 0xBF, prefix + "_e0", e0Block, loopBlock, invalidBlock);
        EmitUtf8SequenceValidation(state, bytesPtr, len, indexSlot, 3, 0x80, 0x9F, prefix + "_ed", edBlock, loopBlock, invalidBlock);
        EmitUtf8SequenceValidation(state, bytesPtr, len, indexSlot, 4, 0x90, 0xBF, prefix + "_f0", f0Block, loopBlock, invalidBlock);
        EmitUtf8SequenceValidation(state, bytesPtr, len, indexSlot, 4, 0x80, 0xBF, prefix + "_four", fourBlock, loopBlock, invalidBlock);
        EmitUtf8SequenceValidation(state, bytesPtr, len, indexSlot, 4, 0x80, 0x8F, prefix + "_f4", f4Block, loopBlock, invalidBlock);

        LlvmApi.PositionBuilderAtEnd(builder, validBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, invalidBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, prefix + "_result_value");
    }

    private static void EmitUtf8SequenceValidation(
        LlvmCodegenState state,
        LlvmValueHandle bytesPtr,
        LlvmValueHandle len,
        LlvmValueHandle indexSlot,
        int sequenceLength,
        int secondByteMin,
        int secondByteMax,
        string prefix,
        LlvmBasicBlockHandle entryBlock,
        LlvmBasicBlockHandle successBlock,
        LlvmBasicBlockHandle invalidBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, entryBlock);
        LlvmValueHandle index = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, prefix + "_index_value");
        LlvmValueHandle remaining = LlvmApi.BuildSub(builder, len, index, prefix + "_remaining");
        LlvmValueHandle enoughBytes = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, remaining, LlvmApi.ConstInt(state.I64, (ulong)sequenceLength, 0), prefix + "_enough");
        var bodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_body");
        LlvmApi.BuildCondBr(builder, enoughBytes, bodyBlock, invalidBlock);

        LlvmApi.PositionBuilderAtEnd(builder, bodyBlock);
        LlvmValueHandle secondByte = LoadByteAt(state, bytesPtr, LlvmApi.BuildAdd(builder, index, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_second_index"), prefix + "_second_byte");
        LlvmValueHandle secondByte64 = LlvmApi.BuildZExt(builder, secondByte, state.I64, prefix + "_second_i64");
        LlvmValueHandle secondInRange = BuildByteRangeCheck(state, secondByte64, secondByteMin, secondByteMax, prefix + "_second_range");
        LlvmBasicBlockHandle nextBlock = bodyBlock;
        for (int offset = 2; offset < sequenceLength; offset++)
        {
            var checkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_cont_" + offset);
            LlvmApi.BuildCondBr(builder, secondInRange, checkBlock, invalidBlock);
            LlvmApi.PositionBuilderAtEnd(builder, checkBlock);
            nextBlock = checkBlock;
            LlvmValueHandle extraByte = LoadByteAt(state, bytesPtr, LlvmApi.BuildAdd(builder, index, LlvmApi.ConstInt(state.I64, (ulong)offset, 0), prefix + "_idx_" + offset), prefix + "_byte_" + offset);
            LlvmValueHandle extraByte64 = LlvmApi.BuildZExt(builder, extraByte, state.I64, prefix + "_byte_i64_" + offset);
            LlvmValueHandle extraInRange = BuildByteRangeCheck(state, extraByte64, 0x80, 0xBF, prefix + "_range_" + offset);
            secondInRange = extraInRange;
        }

        LlvmApi.PositionBuilderAtEnd(builder, nextBlock);
        LlvmBasicBlockHandle advanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_advance");
        LlvmApi.BuildCondBr(builder, secondInRange, advanceBlock, invalidBlock);
        LlvmApi.PositionBuilderAtEnd(builder, advanceBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, index, LlvmApi.ConstInt(state.I64, (ulong)sequenceLength, 0), prefix + "_next"), indexSlot);
        LlvmApi.BuildBr(builder, successBlock);
    }

    private static LlvmValueHandle BuildByteRangeCheck(LlvmCodegenState state, LlvmValueHandle byteValue, int minInclusive, int maxInclusive, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle geMin = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, byteValue, LlvmApi.ConstInt(state.I64, (ulong)minInclusive, 0), prefix + "_ge_min");
        LlvmValueHandle leMax = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ule, byteValue, LlvmApi.ConstInt(state.I64, (ulong)maxInclusive, 0), prefix + "_le_max");
        return LlvmApi.BuildAnd(builder, geMin, leMax, prefix + "_in_range");
    }

    private static LlvmValueHandle LoadByteAt(LlvmCodegenState state, LlvmValueHandle bytesPtr, LlvmValueHandle index, string name)
    {
        LlvmValueHandle bytePtr = LlvmApi.BuildGEP2(state.Target.Builder, state.I8, bytesPtr, [index], name + "_ptr");
        return LlvmApi.BuildLoad2(state.Target.Builder, state.I8, bytePtr, name);
    }

    private static LlvmValueHandle EmitNonNegativeIntToString(LlvmCodegenState state, LlvmValueHandle value, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle bufferType = LlvmApi.ArrayType2(state.I8, 32);
        LlvmValueHandle buffer = LlvmApi.BuildAlloca(builder, bufferType, prefix + "_buffer");
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_index");
        LlvmValueHandle workSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_work");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), indexSlot);
        LlvmApi.BuildStore(builder, value, workSlot);

        var zeroBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_zero");
        var loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_loop_check");
        var loopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_loop_body");
        var finishBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_finish");
        LlvmValueHandle isZero = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, value, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_is_zero");
        LlvmApi.BuildCondBr(builder, isZero, zeroBlock, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, zeroBlock);
        StoreBufferByte(state, buffer, LlvmApi.ConstInt(state.I64, 31, 0), (byte)'0');
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), indexSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle work = LlvmApi.BuildLoad2(builder, state.I64, workSlot, prefix + "_work_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, work, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_done");
        LlvmApi.BuildCondBr(builder, done, finishBlock, loopBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBodyBlock);
        LlvmValueHandle digit = LlvmApi.BuildURem(builder, work, LlvmApi.ConstInt(state.I64, 10, 0), prefix + "_digit");
        LlvmApi.BuildStore(builder, LlvmApi.BuildUDiv(builder, work, LlvmApi.ConstInt(state.I64, 10, 0), prefix + "_next_work"), workSlot);
        LlvmValueHandle idx = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, prefix + "_idx_value");
        StoreBufferByte(state, buffer, LlvmApi.BuildSub(builder, LlvmApi.ConstInt(state.I64, 31, 0), idx, prefix + "_write_idx"), LlvmApi.BuildAdd(builder, digit, LlvmApi.ConstInt(state.I64, (byte)'0', 0), prefix + "_ascii"));
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, idx, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_idx_next"), indexSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        LlvmValueHandle count = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, prefix + "_count");
        LlvmValueHandle startIndex = LlvmApi.BuildSub(builder, LlvmApi.ConstInt(state.I64, 32, 0), count, prefix + "_start_index");
        LlvmValueHandle startPtr = GetArrayElementPointer(state, bufferType, buffer, startIndex, prefix + "_start_ptr");
        return EmitHeapStringSliceFromBytesPointer(state, startPtr, count, prefix + "_string");
    }

    private static LlvmValueHandle EmitHeapStringSliceFromBytesPointer(LlvmCodegenState state, LlvmValueHandle bytesPtr, LlvmValueHandle len, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle normalizedLen = NormalizeToI64(state, len);
        LlvmValueHandle stringRef = EmitAllocDynamic(state, LlvmApi.BuildAdd(builder, normalizedLen, LlvmApi.ConstInt(state.I64, 8, 0), prefix + "_size"));
        StoreMemory(state, stringRef, 0, normalizedLen, prefix + "_len");

        LlvmValueHandle destBytes = GetStringBytesPointer(state, stringRef, prefix + "_dest");
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle idxSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_idx");
        LlvmApi.BuildStore(builder, zero, idxSlot);

        var copyLoopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_copy_loop");
        var copyBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_copy_body");
        var copyDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_copy_done");
        LlvmApi.BuildBr(builder, copyLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, copyLoopBlock);
        LlvmValueHandle idx = LlvmApi.BuildLoad2(builder, state.I64, idxSlot, prefix + "_idx_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, idx, normalizedLen, prefix + "_copy_done_check");
        LlvmApi.BuildCondBr(builder, done, copyDoneBlock, copyBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, copyBodyBlock);
        LlvmValueHandle srcByte = LoadByteAt(state, bytesPtr, idx, prefix + "_src_byte");
        LlvmValueHandle destBytePtr = LlvmApi.BuildGEP2(builder, state.I8, destBytes, [idx], prefix + "_dest_byte_ptr");
        LlvmApi.BuildStore(builder, srcByte, destBytePtr);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, idx, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_idx_next"), idxSlot);
        LlvmApi.BuildBr(builder, copyLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, copyDoneBlock);
        return stringRef;
    }

    private static void EmitConditionalWrite(LlvmCodegenState state, LlvmValueHandle condition, string whenTrue, string whenFalse, bool appendNewline)
    {
        var trueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "bool_true");
        var falseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "bool_false");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "bool_continue");
        LlvmApi.BuildCondBr(state.Target.Builder, condition, trueBlock, falseBlock);

        LlvmApi.PositionBuilderAtEnd(state.Target.Builder, trueBlock);
        EmitWriteBytes(
            state,
            EmitStackByteArray(state, System.Text.Encoding.UTF8.GetBytes(whenTrue)),
            LlvmApi.ConstInt(state.I64, (ulong)whenTrue.Length, 0));
        if (appendNewline)
        {
            EmitWriteBytes(state, EmitStackByteArray(state, [10]), LlvmApi.ConstInt(state.I64, 1, 0));
        }
        LlvmApi.BuildBr(state.Target.Builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(state.Target.Builder, falseBlock);
        EmitWriteBytes(state, EmitStackByteArray(state, System.Text.Encoding.UTF8.GetBytes(whenFalse)), LlvmApi.ConstInt(state.I64, (ulong)whenFalse.Length, 0));
        if (appendNewline)
        {
            EmitWriteBytes(state, EmitStackByteArray(state, [10]), LlvmApi.ConstInt(state.I64, 1, 0));
        }
        LlvmApi.BuildBr(state.Target.Builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(state.Target.Builder, continueBlock);
    }

    private static LlvmValueHandle EmitStackStringObject(LlvmCodegenState state, string value)
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(value);
        LlvmTypeHandle objectType = LlvmApi.ArrayType2(state.I8, (uint)(utf8.Length + 8));
        LlvmValueHandle storage = LlvmApi.BuildAlloca(state.Target.Builder, objectType, "str_obj");
        LlvmValueHandle lenPtr = LlvmApi.BuildBitCast(state.Target.Builder, storage, state.I64Ptr, "str_obj_len_ptr");
        LlvmApi.BuildStore(state.Target.Builder, LlvmApi.ConstInt(state.I64, (ulong)utf8.Length, 0), lenPtr);

        if (utf8.Length > 0)
        {
            LlvmValueHandle bytesPtr = GetArrayElementPointer(state, objectType, storage, LlvmApi.ConstInt(state.I64, 8, 0), "str_obj_bytes");
            LlvmValueHandle srcPtr = CreateGlobalConstantBytes(state, utf8, "str_obj_data");
            LlvmApi.BuildMemCpy(state.Target.Builder, bytesPtr, 1, srcPtr, 1,
                LlvmApi.ConstInt(state.I64, (ulong)utf8.Length, 0));
        }

        return LlvmApi.BuildPtrToInt(state.Target.Builder, storage, state.I64, "str_obj_i64");
    }

    private static LlvmValueHandle EmitStackByteArray(LlvmCodegenState state, IReadOnlyList<byte> bytes)
    {
        LlvmTypeHandle arrayType = LlvmApi.ArrayType2(state.I8, (uint)bytes.Count);
        LlvmValueHandle storage = LlvmApi.BuildAlloca(state.Target.Builder, arrayType, "byte_array");

        if (bytes.Count > 0)
        {
            LlvmValueHandle destPtr = GetArrayElementPointer(state, arrayType, storage, LlvmApi.ConstInt(state.I64, 0, 0), "byte_array_dest");
            LlvmValueHandle srcPtr = CreateGlobalConstantBytes(state, bytes, "byte_data");
            LlvmApi.BuildMemCpy(state.Target.Builder, destPtr, 1, srcPtr, 1,
                LlvmApi.ConstInt(state.I64, (ulong)bytes.Count, 0));
        }

        return GetArrayElementPointer(state, arrayType, storage, LlvmApi.ConstInt(state.I64, 0, 0), "byte_array_ptr");
    }

    private static void StoreBufferByte(LlvmCodegenState state, LlvmValueHandle buffer, LlvmValueHandle index, byte value)
    {
        StoreBufferByte(state, buffer, index, LlvmApi.ConstInt(state.I64, value, 0));
    }

    private static void StoreBufferByte(LlvmCodegenState state, LlvmValueHandle buffer, LlvmValueHandle index, LlvmValueHandle value)
    {
        LlvmValueHandle ptr = GetArrayElementPointer(state, LlvmApi.ArrayType2(state.I8, 32), buffer, index, "buf_ptr");
        LlvmValueHandle byteValue = LlvmApi.GetTypeKind(LlvmApi.TypeOf(value)) == LlvmTypeKind.Integer && LlvmApi.GetIntTypeWidth(LlvmApi.TypeOf(value)) == 8
            ? value
            : LlvmApi.BuildTrunc(state.Target.Builder, value, state.I8, "to_i8");
        LlvmApi.BuildStore(state.Target.Builder, byteValue, ptr);
    }

    private static LlvmValueHandle GetArrayElementPointer(LlvmCodegenState state, LlvmTypeHandle arrayType, LlvmValueHandle storage, LlvmValueHandle index, string name)
    {
        return LlvmApi.BuildGEP2(state.Target.Builder,
            arrayType,
            storage,
            [
                LlvmApi.ConstInt(state.I64, 0, 0),
                index
            ],
            name);
    }
}
