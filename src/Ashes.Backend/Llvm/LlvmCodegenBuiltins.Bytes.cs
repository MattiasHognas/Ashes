using Ashes.Semantics;
using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{

    // Bytes
    // TBytes has the same heap layout as TStr: [length:i64, data:u8[length]].
    // All helpers that work on TStr (LoadStringLength, GetStringBytesPointer,
    // EmitStringConcat, EmitAllocDynamic, EmitAlloc, etc.) are reused directly.

    private static LlvmValueHandle EmitBytesEmpty(LlvmCodegenState state)
    {
        // Allocate 8 bytes (length word only, no data), store length = 0.
        LlvmValueHandle bytesRef = EmitAlloc(state, 8);
        StoreMemory(state, bytesRef, 0, LlvmApi.ConstInt(state.I64, 0, 0), "bytes_empty_len");
        return bytesRef;
    }

    private static LlvmValueHandle EmitBytesSingleton(LlvmCodegenState state, LlvmValueHandle byteVal)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // Allocate 16 bytes: 8 for length + 8 aligned for 1 data byte.
        LlvmValueHandle bytesRef = EmitAlloc(state, 16);
        StoreMemory(state, bytesRef, 0, LlvmApi.ConstInt(state.I64, 1, 0), "bytes_singleton_len");
        LlvmValueHandle dataPtr = GetStringBytesPointer(state, bytesRef, "bytes_singleton_data");
        // byteVal is an i64 (Ashes uniform representation); truncate to i8 for storage.
        LlvmValueHandle truncated = LlvmApi.BuildTrunc(builder, byteVal, state.I8, "bytes_singleton_byte");
        LlvmApi.BuildStore(builder, truncated, dataPtr);
        return bytesRef;
    }

    private static LlvmValueHandle EmitBytesLength(LlvmCodegenState state, LlvmValueHandle bytesRef)
    {
        return LoadStringLength(state, bytesRef, "bytes_length");
    }

    // 64-bit FNV-1a over the byte payload. Returned as i64 (Ashes Int).
    private static LlvmValueHandle EmitBytesHash(LlvmCodegenState state, LlvmValueHandle bytesRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle len = LoadStringLength(state, bytesRef, "bytes_hash_len");
        LlvmValueHandle dataPtr = GetStringBytesPointer(state, bytesRef, "bytes_hash_data");
        LlvmValueHandle hashSlot = LlvmApi.BuildAlloca(builder, state.I64, "bytes_hash_acc");
        LlvmValueHandle idxSlot = LlvmApi.BuildAlloca(builder, state.I64, "bytes_hash_idx");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 14695981039346656037UL, 0), hashSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), idxSlot);

        var checkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "bytes_hash_check");
        var bodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "bytes_hash_body");
        var doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "bytes_hash_done");

        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkBlock);
        LlvmValueHandle idx = LlvmApi.BuildLoad2(builder, state.I64, idxSlot, "bytes_hash_idx_val");
        LlvmValueHandle more = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, idx, len, "bytes_hash_more");
        LlvmApi.BuildCondBr(builder, more, bodyBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, bodyBlock);
        LlvmValueHandle bytePtr = LlvmApi.BuildGEP2(builder, state.I8, dataPtr, [idx], "bytes_hash_byte_ptr");
        LlvmValueHandle byteVal = LlvmApi.BuildZExt(builder, LlvmApi.BuildLoad2(builder, state.I8, bytePtr, "bytes_hash_byte"), state.I64, "bytes_hash_byte_i64");
        LlvmValueHandle h = LlvmApi.BuildLoad2(builder, state.I64, hashSlot, "bytes_hash_h");
        LlvmValueHandle xored = LlvmApi.BuildXor(builder, h, byteVal, "bytes_hash_xor");
        LlvmValueHandle mixed = LlvmApi.BuildMul(builder, xored, LlvmApi.ConstInt(state.I64, 1099511628211UL, 0), "bytes_hash_mul");
        LlvmApi.BuildStore(builder, mixed, hashSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, idx, LlvmApi.ConstInt(state.I64, 1, 0), "bytes_hash_idx_next"), idxSlot);
        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, hashSlot, "bytes_hash_result");
    }

    private static LlvmValueHandle EmitBytesGet(LlvmCodegenState state, LlvmValueHandle bytesRef, LlvmValueHandle indexVal)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle len = LoadStringLength(state, bytesRef, "bytes_get_len");
        LlvmValueHandle oob = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, indexVal, len, "bytes_get_oob");

        var panicBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "bytes_get_panic");
        var okBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "bytes_get_ok");
        LlvmApi.BuildCondBr(builder, oob, panicBlock, okBlock);

        LlvmApi.PositionBuilderAtEnd(builder, panicBlock);
        EmitPanic(state, EmitStackStringObject(state, "Bytes.get: index out of bounds"));

        LlvmApi.PositionBuilderAtEnd(builder, okBlock);
        LlvmValueHandle dataPtr = GetStringBytesPointer(state, bytesRef, "bytes_get_data");
        LlvmValueHandle elemPtr = LlvmApi.BuildGEP2(builder, state.I8, dataPtr, [indexVal], "bytes_get_elem_ptr");
        LlvmValueHandle byteVal = LlvmApi.BuildLoad2(builder, state.I8, elemPtr, "bytes_get_byte");
        return LlvmApi.BuildZExt(builder, byteVal, state.I64, "bytes_get_result");
    }

    // Ashes.Byte.indexOf(bytes)(needle)(from) : Int — index of the first byte equal to `needle`
    // at or after `from`, or -1. A memchr over [max(from,0), len). No allocation.
    private static LlvmValueHandle EmitBytesIndexOf(LlvmCodegenState state, LlvmValueHandle bytesRef, LlvmValueHandle needleVal, LlvmValueHandle fromVal)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle len = LoadStringLength(state, bytesRef, "bytes_idx_len");
        LlvmValueHandle dataPtr = GetStringBytesPointer(state, bytesRef, "bytes_idx_data");
        LlvmValueHandle needle8 = LlvmApi.BuildTrunc(builder, needleVal, state.I8, "bytes_idx_needle");

        // Clamp `from` up to 0 (negative starts scan at the beginning).
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle fromNeg = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, fromVal, zero, "bytes_idx_from_neg");
        LlvmValueHandle fromStart = LlvmApi.BuildSelect(builder, fromNeg, zero, fromVal, "bytes_idx_from_start");

        // Linkage-aware scan selection. When the image is already dynamically linked against glibc
        // (TLS runtime present — see the __ashes_glibc_runtime marker), delegate to libc memchr:
        // glibc's is SIMD-optimized (SSE2/AVX2), and the dependency is already paid for. Otherwise
        // use the freestanding SWAR word scan so the image stays fully static — importing memchr
        // was the only thing forcing otherwise-hermetic binaries onto the dynamic loader. Windows
        // keeps the freestanding scalar loop (no libc memchr wired there).
        bool glibcAlreadyLinked = state.Target.TargetTriple.Contains("linux", StringComparison.Ordinal)
            && LlvmApi.GetNamedGlobal(state.Target.Module, "__ashes_glibc_runtime").Ptr != 0;
        if (glibcAlreadyLinked)
        {
            return EmitBytesIndexOfMemchr(state, dataPtr, len, needle8, fromStart);
        }

        return state.Target.TargetTriple.Contains("linux", StringComparison.Ordinal)
            ? EmitBytesIndexOfSwar(state, dataPtr, len, needle8, fromStart)
            : EmitBytesIndexOfScalarScan(state, dataPtr, len, needle8, fromStart);
    }

    private static LlvmValueHandle EmitBytesIndexOfMemchr(LlvmCodegenState state, LlvmValueHandle dataPtr, LlvmValueHandle len, LlvmValueHandle needle8, LlvmValueHandle fromStart)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // Clamp fromStart up to len so `len - fromStart` (memchr's size_t length) can't underflow.
        LlvmValueHandle fromPastEnd = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, fromStart, len, "bytes_idx_from_pastend");
        LlvmValueHandle scanStart = LlvmApi.BuildSelect(builder, fromPastEnd, len, fromStart, "bytes_idx_scan_start");
        LlvmValueHandle scanPtr = LlvmApi.BuildGEP2(builder, state.I8, dataPtr, [scanStart], "bytes_idx_scan_ptr");
        LlvmValueHandle scanLen = LlvmApi.BuildSub(builder, len, scanStart, "bytes_idx_scan_len");
        // memchr's needle is an int whose low byte is compared; zero-extend our u8 to i32.
        LlvmValueHandle needle32 = LlvmApi.BuildZExt(builder, needle8, state.I32, "bytes_idx_needle32");
        LlvmTypeHandle memchrType = LlvmApi.FunctionType(state.I8Ptr, [state.I8Ptr, state.I32, state.I64]);
        LlvmValueHandle found = EmitLinuxImportedCall(state, "memchr", memchrType, [scanPtr, needle32, scanLen], "bytes_idx_memchr");
        LlvmValueHandle isNull = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, found, LlvmApi.ConstNull(state.I8Ptr), "bytes_idx_isnull");
        LlvmValueHandle foundInt = LlvmApi.BuildPtrToInt(builder, found, state.I64, "bytes_idx_found_int");
        LlvmValueHandle dataInt = LlvmApi.BuildPtrToInt(builder, dataPtr, state.I64, "bytes_idx_data_int");
        LlvmValueHandle foundIdx = LlvmApi.BuildSub(builder, foundInt, dataInt, "bytes_idx_found_idx");
        LlvmValueHandle negOne = LlvmApi.ConstInt(state.I64, unchecked((ulong)-1L), 0);
        return LlvmApi.BuildSelect(builder, isNull, negOne, foundIdx, "bytes_idx_result_val");
    }

    private static LlvmValueHandle EmitBytesIndexOfScalarScan(LlvmCodegenState state, LlvmValueHandle dataPtr, LlvmValueHandle len, LlvmValueHandle needle8, LlvmValueHandle fromStart)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle idxSlot = LlvmApi.BuildAlloca(builder, state.I64, "bytes_idx_slot");
        LlvmApi.BuildStore(builder, fromStart, idxSlot);

        var checkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "bytes_idx_check");
        var bodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "bytes_idx_body");
        var foundBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "bytes_idx_found");
        var advanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "bytes_idx_advance");
        var notFoundBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "bytes_idx_notfound");
        var doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "bytes_idx_done");

        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "bytes_idx_result");
        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkBlock);
        LlvmValueHandle idx = LlvmApi.BuildLoad2(builder, state.I64, idxSlot, "bytes_idx_val");
        LlvmValueHandle more = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, idx, len, "bytes_idx_more");
        LlvmApi.BuildCondBr(builder, more, bodyBlock, notFoundBlock);

        LlvmApi.PositionBuilderAtEnd(builder, bodyBlock);
        LlvmValueHandle bytePtr = LlvmApi.BuildGEP2(builder, state.I8, dataPtr, [idx], "bytes_idx_byte_ptr");
        LlvmValueHandle curByte = LlvmApi.BuildLoad2(builder, state.I8, bytePtr, "bytes_idx_byte");
        LlvmValueHandle eq = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, curByte, needle8, "bytes_idx_eq");
        LlvmApi.BuildCondBr(builder, eq, foundBlock, advanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, foundBlock);
        LlvmApi.BuildStore(builder, idx, resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, advanceBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, idx, LlvmApi.ConstInt(state.I64, 1, 0), "bytes_idx_next"), idxSlot);
        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, notFoundBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)-1L), 0), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "bytes_idx_result_val");
    }

    private const string MemchrSwarHelperName = "__ashes_memchr_swar";

    // Freestanding word-at-a-time byte search: index of the first `needle` byte in
    // data[start, len), or -1. Shared per-module helper so static images never import libc
    // memchr. The loop scans single bytes until the absolute address is 8-aligned, then tests
    // whole words with the Hacker's-Delight zero-byte mask ((x - 0x01…) & ~x & 0x80…), dropping
    // back to the byte path on a word hit (the hit byte is located within at most eight scalar
    // steps, after which alignment is reached again). Handles unaligned Bytes views because
    // alignment is computed from the absolute address, not the index.
    private static LlvmValueHandle EmitBytesIndexOfSwar(LlvmCodegenState state, LlvmValueHandle dataPtr, LlvmValueHandle len, LlvmValueHandle needle8, LlvmValueHandle fromStart)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle helper = GetOrEmitMemchrSwarHelper(state);
        LlvmTypeHandle helperType = MemchrSwarHelperType(state);
        return LlvmApi.BuildCall2(builder, helperType, helper, [dataPtr, len, needle8, fromStart], "bytes_idx_swar");
    }

    private static LlvmTypeHandle MemchrSwarHelperType(LlvmCodegenState state) =>
        LlvmApi.FunctionType(state.I64, [state.I8Ptr, state.I64, state.I8, state.I64]);

    private static LlvmValueHandle GetOrEmitMemchrSwarHelper(LlvmCodegenState state)
    {
        LlvmValueHandle existing = LlvmApi.GetNamedFunction(state.Target.Module, MemchrSwarHelperName);
        if (existing.Ptr != 0)
        {
            return existing;
        }

        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmBasicBlockHandle savedBlock = LlvmApi.GetInsertBlock(builder);

        LlvmValueHandle fn = LlvmApi.AddFunction(state.Target.Module, MemchrSwarHelperName, MemchrSwarHelperType(state));
        LlvmApi.SetLinkage(fn, LlvmLinkage.Internal);
        LlvmApi.AddAttributeAtIndex(fn, LlvmApi.AttributeIndexFunction,
            LlvmApi.CreateEnumAttribute(state.Target.Context, LlvmApi.GetEnumAttributeKindForName("nounwind"), 0));
        EmitMemchrSwarHelperBody(state, fn);

        LlvmApi.PositionBuilderAtEnd(builder, savedBlock);
        return fn;
    }

    private static void EmitMemchrSwarHelperBody(LlvmCodegenState state, LlvmValueHandle fn)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle data = LlvmApi.GetParam(fn, 0);
        LlvmValueHandle len = LlvmApi.GetParam(fn, 1);
        LlvmValueHandle needle = LlvmApi.GetParam(fn, 2);
        LlvmValueHandle start = LlvmApi.GetParam(fn, 3);

        var entryBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "entry");
        var headBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "head");
        var alignBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "align_check");
        var wordBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "word");
        var byteBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "byte");
        var missBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "miss");
        var hitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "hit");

        LlvmApi.PositionBuilderAtEnd(builder, entryBlock);
        LlvmValueHandle idxSlot = LlvmApi.BuildAlloca(builder, state.I64, "swar_idx");
        LlvmApi.BuildStore(builder, start, idxSlot);
        LlvmValueHandle needleWord = LlvmApi.BuildMul(builder,
            LlvmApi.BuildZExt(builder, needle, state.I64, "swar_needle64"),
            LlvmApi.ConstInt(state.I64, 0x0101010101010101UL, 0), "swar_broadcast");
        LlvmValueHandle dataAddr = LlvmApi.BuildPtrToInt(builder, data, state.I64, "swar_data_addr");
        LlvmApi.BuildBr(builder, headBlock);

        LlvmApi.PositionBuilderAtEnd(builder, headBlock);
        LlvmValueHandle idx = LlvmApi.BuildLoad2(builder, state.I64, idxSlot, "swar_i");
        LlvmValueHandle inRange = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, idx, len, "swar_in_range");
        LlvmApi.BuildCondBr(builder, inRange, alignBlock, missBlock);

        LlvmApi.PositionBuilderAtEnd(builder, alignBlock);
        LlvmValueHandle addr = LlvmApi.BuildAdd(builder, dataAddr, idx, "swar_addr");
        LlvmValueHandle aligned = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            LlvmApi.BuildAnd(builder, addr, LlvmApi.ConstInt(state.I64, 7, 0), "swar_addr_low"),
            LlvmApi.ConstInt(state.I64, 0, 0), "swar_aligned");
        LlvmValueHandle wordFits = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle,
            LlvmApi.BuildAdd(builder, idx, LlvmApi.ConstInt(state.I64, 8, 0), "swar_word_end"), len, "swar_word_fits");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildAnd(builder, aligned, wordFits, "swar_take_word"), wordBlock, byteBlock);

        EmitMemchrSwarWordAndByteBlocks(state, fn, idxSlot, data, needle, needleWord, wordBlock, byteBlock, headBlock, hitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, missBlock);
        LlvmApi.BuildRet(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)-1L), 0));

        LlvmApi.PositionBuilderAtEnd(builder, hitBlock);
        LlvmApi.BuildRet(builder, LlvmApi.BuildLoad2(builder, state.I64, idxSlot, "swar_hit_idx"));
    }

    private static void EmitMemchrSwarWordAndByteBlocks(
        LlvmCodegenState state, LlvmValueHandle fn, LlvmValueHandle idxSlot, LlvmValueHandle data, LlvmValueHandle needle, LlvmValueHandle needleWord,
        LlvmBasicBlockHandle wordBlock, LlvmBasicBlockHandle byteBlock, LlvmBasicBlockHandle headBlock, LlvmBasicBlockHandle hitBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        LlvmApi.PositionBuilderAtEnd(builder, wordBlock);
        LlvmValueHandle wordIdx = LlvmApi.BuildLoad2(builder, state.I64, idxSlot, "swar_wi");
        LlvmValueHandle wordPtr = LlvmApi.BuildGEP2(builder, state.I8, data, [wordIdx], "swar_word_ptr");
        LlvmValueHandle word = LlvmApi.BuildLoad2(builder, state.I64, wordPtr, "swar_word");
        LlvmValueHandle xored = LlvmApi.BuildXor(builder, word, needleWord, "swar_xored");
        LlvmValueHandle sub = LlvmApi.BuildSub(builder, xored, LlvmApi.ConstInt(state.I64, 0x0101010101010101UL, 0), "swar_sub");
        LlvmValueHandle notX = LlvmApi.BuildXor(builder, xored, LlvmApi.ConstInt(state.I64, 0xFFFFFFFFFFFFFFFFUL, 0), "swar_not");
        LlvmValueHandle mask = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildAnd(builder, sub, notX, "swar_sub_not"),
            LlvmApi.ConstInt(state.I64, 0x8080808080808080UL, 0), "swar_mask");
        LlvmValueHandle anyHit = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, mask, LlvmApi.ConstInt(state.I64, 0, 0), "swar_any_hit");
        LlvmValueHandle advanced = LlvmApi.BuildAdd(builder, wordIdx, LlvmApi.ConstInt(state.I64, 8, 0), "swar_wi_next");
        var wordMissBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "word_miss");
        LlvmApi.BuildCondBr(builder, anyHit, byteBlock, wordMissBlock);

        LlvmApi.PositionBuilderAtEnd(builder, wordMissBlock);
        LlvmApi.BuildStore(builder, advanced, idxSlot);
        LlvmApi.BuildBr(builder, headBlock);

        LlvmApi.PositionBuilderAtEnd(builder, byteBlock);
        LlvmValueHandle byteIdx = LlvmApi.BuildLoad2(builder, state.I64, idxSlot, "swar_bi");
        LlvmValueHandle bytePtr = LlvmApi.BuildGEP2(builder, state.I8, data, [byteIdx], "swar_byte_ptr");
        LlvmValueHandle curByte = LlvmApi.BuildLoad2(builder, state.I8, bytePtr, "swar_byte");
        LlvmValueHandle eq = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, curByte, needle, "swar_eq");
        var byteMissBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "byte_miss");
        LlvmApi.BuildCondBr(builder, eq, hitBlock, byteMissBlock);

        LlvmApi.PositionBuilderAtEnd(builder, byteMissBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, byteIdx, LlvmApi.ConstInt(state.I64, 1, 0), "swar_bi_next"), idxSlot);
        LlvmApi.BuildBr(builder, headBlock);
    }

    // Ashes.Byte.scanHash(bytes)(needle)(from) : (Int, Int) — one fused pass: index of the first
    // needle byte at or after `from` (or -1), and the FNV-1a hash of the bytes scanned before it
    // (identical to Ashes.Byte.hash over that range). FNV is inherently sequential, so the fused
    // scalar loop costs what the hash pass alone would; the separate memchr pass disappears.
    private static LlvmValueHandle EmitBytesScanHash(LlvmCodegenState state, LlvmValueHandle bytesRef, LlvmValueHandle needleVal, LlvmValueHandle fromVal)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle len = LoadStringLength(state, bytesRef, "bytes_sh_len");
        LlvmValueHandle dataPtr = GetStringBytesPointer(state, bytesRef, "bytes_sh_data");
        LlvmValueHandle needle8 = LlvmApi.BuildTrunc(builder, needleVal, state.I8, "bytes_sh_needle");
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);

        LlvmValueHandle fromNeg = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, fromVal, zero, "bytes_sh_from_neg");
        LlvmValueHandle fromStart = LlvmApi.BuildSelect(builder, fromNeg, zero, fromVal, "bytes_sh_from");

        LlvmValueHandle idxSlot = LlvmApi.BuildAlloca(builder, state.I64, "bytes_sh_idx");
        LlvmValueHandle hashSlot = LlvmApi.BuildAlloca(builder, state.I64, "bytes_sh_hash");
        LlvmValueHandle foundSlot = LlvmApi.BuildAlloca(builder, state.I64, "bytes_sh_found");
        LlvmApi.BuildStore(builder, fromStart, idxSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 14695981039346656037UL, 0), hashSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)-1L), 0), foundSlot);

        EmitBytesScanHashLoop(state, dataPtr, len, needle8, idxSlot, hashSlot, foundSlot);

        LlvmValueHandle tuplePtr = EmitAlloc(state, 16);
        StoreMemory(state, tuplePtr, 0, LlvmApi.BuildLoad2(builder, state.I64, foundSlot, "bytes_sh_found_v"), "bytes_sh_t0");
        StoreMemory(state, tuplePtr, 8, LlvmApi.BuildLoad2(builder, state.I64, hashSlot, "bytes_sh_hash_v"), "bytes_sh_t1");
        return tuplePtr;
    }

    private static void EmitBytesScanHashLoop(
        LlvmCodegenState state, LlvmValueHandle dataPtr, LlvmValueHandle len, LlvmValueHandle needle8,
        LlvmValueHandle idxSlot, LlvmValueHandle hashSlot, LlvmValueHandle foundSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        var checkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "bytes_sh_check");
        var bodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "bytes_sh_body");
        var hitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "bytes_sh_hit");
        var stepBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "bytes_sh_step");
        var doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "bytes_sh_done");
        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkBlock);
        LlvmValueHandle idx = LlvmApi.BuildLoad2(builder, state.I64, idxSlot, "bytes_sh_i");
        LlvmValueHandle more = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, idx, len, "bytes_sh_more");
        LlvmApi.BuildCondBr(builder, more, bodyBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, bodyBlock);
        LlvmValueHandle bytePtr = LlvmApi.BuildGEP2(builder, state.I8, dataPtr, [idx], "bytes_sh_ptr");
        LlvmValueHandle curByte = LlvmApi.BuildLoad2(builder, state.I8, bytePtr, "bytes_sh_byte");
        LlvmValueHandle eq = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, curByte, needle8, "bytes_sh_eq");
        LlvmApi.BuildCondBr(builder, eq, hitBlock, stepBlock);

        LlvmApi.PositionBuilderAtEnd(builder, hitBlock);
        LlvmApi.BuildStore(builder, idx, foundSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, stepBlock);
        LlvmValueHandle h = LlvmApi.BuildLoad2(builder, state.I64, hashSlot, "bytes_sh_h");
        LlvmValueHandle byte64 = LlvmApi.BuildZExt(builder, curByte, state.I64, "bytes_sh_b64");
        LlvmValueHandle hx = LlvmApi.BuildXor(builder, h, byte64, "bytes_sh_hx");
        LlvmValueHandle hm = LlvmApi.BuildMul(builder, hx, LlvmApi.ConstInt(state.I64, 1099511628211UL, 0), "bytes_sh_hm");
        LlvmApi.BuildStore(builder, hm, hashSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, idx, LlvmApi.ConstInt(state.I64, 1, 0), "bytes_sh_next"), idxSlot);
        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
    }

    // Ashes.Byte.compare(left)(right) : Int — three-way lexicographic byte order, normalized to
    // -1/0/1. One memcmp over min(len) plus a length tie-break, instead of a byte-at-a-time loop.
    // Uses the module's freestanding memcmp (present on every target); LLVM lowers small
    // fixed-pattern calls efficiently and glibc is not required.
    private static LlvmValueHandle EmitBytesCompare(LlvmCodegenState state, LlvmValueHandle leftRef, LlvmValueHandle rightRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle leftLen = LoadStringLength(state, leftRef, "bytes_cmp_left_len");
        LlvmValueHandle rightLen = LoadStringLength(state, rightRef, "bytes_cmp_right_len");
        LlvmValueHandle leftPtr = GetStringBytesPointer(state, leftRef, "bytes_cmp_left_data");
        LlvmValueHandle rightPtr = GetStringBytesPointer(state, rightRef, "bytes_cmp_right_data");

        LlvmValueHandle leftSmaller = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, leftLen, rightLen, "bytes_cmp_left_smaller");
        LlvmValueHandle minLen = LlvmApi.BuildSelect(builder, leftSmaller, leftLen, rightLen, "bytes_cmp_min_len");

        LlvmTypeHandle memcmpType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, state.I64]);
        LlvmValueHandle memcmpFn = LlvmApi.GetNamedFunction(state.Target.Module, "memcmp");
        LlvmValueHandle raw = LlvmApi.BuildCall2(builder, memcmpType, memcmpFn,
            [leftPtr, rightPtr, minLen], "bytes_cmp_memcmp");

        LlvmValueHandle zero32 = LlvmApi.ConstInt(state.I32, 0, 0);
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle one = LlvmApi.ConstInt(state.I64, 1, 0);
        LlvmValueHandle negOne = LlvmApi.ConstInt(state.I64, unchecked((ulong)-1L), 0);

        // Common prefix equal → order by length (shorter sorts first); else the sign of memcmp.
        LlvmValueHandle rawIsZero = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, raw, zero32, "bytes_cmp_prefix_eq");
        LlvmValueHandle rawNeg = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, raw, zero32, "bytes_cmp_raw_neg");
        LlvmValueHandle bySign = LlvmApi.BuildSelect(builder, rawNeg, negOne, one, "bytes_cmp_by_sign");
        LlvmValueHandle lenEq = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, leftLen, rightLen, "bytes_cmp_len_eq");
        LlvmValueHandle byLenNonEq = LlvmApi.BuildSelect(builder, leftSmaller, negOne, one, "bytes_cmp_by_len_ne");
        LlvmValueHandle byLen = LlvmApi.BuildSelect(builder, lenEq, zero, byLenNonEq, "bytes_cmp_by_len");
        return LlvmApi.BuildSelect(builder, rawIsZero, byLen, bySign, "bytes_cmp_result");
    }

    // Ashes.Byte.subText(bytes)(start)(len) : Str — copies `len` bytes from `start` into a fresh
    // Str ([length][bytes]). Range is clamped into the source so it never reads out of bounds.
    // Ashes.Byte.subView(bytes)(start)(len) : Str — a zero-copy view {len|VIEW, ptr} over the
    // source range (clamped like subText). O(1); the backing must outlive the view.
    private static LlvmValueHandle EmitBytesSubView(LlvmCodegenState state, LlvmValueHandle bytesRef, LlvmValueHandle startVal, LlvmValueHandle lenVal)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle srcLen = LoadStringLength(state, bytesRef, "bytes_subv_srclen");
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);

        LlvmValueHandle startNeg = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, startVal, zero, "bytes_subv_start_neg");
        LlvmValueHandle start0 = LlvmApi.BuildSelect(builder, startNeg, zero, startVal, "bytes_subv_start0");
        LlvmValueHandle startBig = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, start0, srcLen, "bytes_subv_start_big");
        LlvmValueHandle start = LlvmApi.BuildSelect(builder, startBig, srcLen, start0, "bytes_subv_start");

        LlvmValueHandle avail = LlvmApi.BuildSub(builder, srcLen, start, "bytes_subv_avail");
        LlvmValueHandle lenNeg = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, lenVal, zero, "bytes_subv_len_neg");
        LlvmValueHandle len0 = LlvmApi.BuildSelect(builder, lenNeg, zero, lenVal, "bytes_subv_len0");
        LlvmValueHandle lenBig = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, len0, avail, "bytes_subv_len_big");
        LlvmValueHandle viewLen = LlvmApi.BuildSelect(builder, lenBig, avail, len0, "bytes_subv_len");

        LlvmValueHandle srcData = GetStringBytesPointer(state, bytesRef, "bytes_subv_src");
        LlvmValueHandle srcStart = LlvmApi.BuildGEP2(builder, state.I8, srcData, [start], "bytes_subv_srcstart");
        return EmitStringView(state, srcStart, viewLen, "bytes_subv");
    }

    private static LlvmValueHandle EmitBytesSubText(LlvmCodegenState state, LlvmValueHandle bytesRef, LlvmValueHandle startVal, LlvmValueHandle lenVal)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle srcLen = LoadStringLength(state, bytesRef, "bytes_sub_srclen");
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);

        // Clamp start into [0, srcLen].
        LlvmValueHandle startNeg = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, startVal, zero, "bytes_sub_start_neg");
        LlvmValueHandle start0 = LlvmApi.BuildSelect(builder, startNeg, zero, startVal, "bytes_sub_start0");
        LlvmValueHandle startBig = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, start0, srcLen, "bytes_sub_start_big");
        LlvmValueHandle start = LlvmApi.BuildSelect(builder, startBig, srcLen, start0, "bytes_sub_start");

        // avail = srcLen - start (>= 0). Clamp len into [0, avail].
        LlvmValueHandle avail = LlvmApi.BuildSub(builder, srcLen, start, "bytes_sub_avail");
        LlvmValueHandle lenNeg = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, lenVal, zero, "bytes_sub_len_neg");
        LlvmValueHandle len0 = LlvmApi.BuildSelect(builder, lenNeg, zero, lenVal, "bytes_sub_len0");
        LlvmValueHandle lenBig = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, len0, avail, "bytes_sub_len_big");
        LlvmValueHandle copyLen = LlvmApi.BuildSelect(builder, lenBig, avail, len0, "bytes_sub_copylen");

        LlvmValueHandle totalBytes = LlvmApi.BuildAdd(builder, copyLen, LlvmApi.ConstInt(state.I64, 8, 0), "bytes_sub_total");
        LlvmValueHandle destRef = EmitAllocDynamic(state, totalBytes);
        StoreMemory(state, destRef, 0, copyLen, "bytes_sub_len");
        LlvmValueHandle destData = GetStringBytesPointer(state, destRef, "bytes_sub_dest");
        LlvmValueHandle srcData = GetStringBytesPointer(state, bytesRef, "bytes_sub_src");
        LlvmValueHandle srcStart = LlvmApi.BuildGEP2(builder, state.I8, srcData, [start], "bytes_sub_srcstart");
        EmitCopyBytes(state, destData, srcStart, copyLen, "bytes_sub_copy");
        return destRef;
    }

    private static LlvmValueHandle EmitBytesAppend(LlvmCodegenState state, LlvmValueHandle leftRef, LlvmValueHandle rightRef)
    {
        // Identical to EmitStringConcat — same heap layout.
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle leftLen = LoadStringLength(state, leftRef, "bytes_app_left_len");
        LlvmValueHandle rightLen = LoadStringLength(state, rightRef, "bytes_app_right_len");
        LlvmValueHandle totalLen = LlvmApi.BuildAdd(builder, leftLen, rightLen, "bytes_app_total_len");
        LlvmValueHandle totalBytes = LlvmApi.BuildAdd(builder, totalLen, LlvmApi.ConstInt(state.I64, 8, 0), "bytes_app_total_bytes");
        LlvmValueHandle destRef = EmitAllocDynamic(state, totalBytes);
        StoreMemory(state, destRef, 0, totalLen, "bytes_app_len");
        LlvmValueHandle destData = GetStringBytesPointer(state, destRef, "bytes_app_dest");
        LlvmValueHandle leftData = GetStringBytesPointer(state, leftRef, "bytes_app_left");
        LlvmValueHandle rightData = GetStringBytesPointer(state, rightRef, "bytes_app_right");
        EmitCopyBytes(state, destData, leftData, leftLen, "bytes_app_copy_left");
        LlvmValueHandle rightDest = LlvmApi.BuildGEP2(builder, state.I8, destData, [leftLen], "bytes_app_right_dest");
        EmitCopyBytes(state, rightDest, rightData, rightLen, "bytes_app_copy_right");
        return destRef;
    }

    private static LlvmValueHandle EmitBytesAppendByte(LlvmCodegenState state, LlvmValueHandle bytesRef, LlvmValueHandle byteVal)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle oldLen = LoadStringLength(state, bytesRef, "bytes_appb_old_len");
        LlvmValueHandle newLen = LlvmApi.BuildAdd(builder, oldLen, LlvmApi.ConstInt(state.I64, 1, 0), "bytes_appb_new_len");
        LlvmValueHandle totalBytes = LlvmApi.BuildAdd(builder, newLen, LlvmApi.ConstInt(state.I64, 8, 0), "bytes_appb_total_bytes");
        LlvmValueHandle destRef = EmitAllocDynamic(state, totalBytes);
        StoreMemory(state, destRef, 0, newLen, "bytes_appb_len");
        LlvmValueHandle destData = GetStringBytesPointer(state, destRef, "bytes_appb_dest");
        LlvmValueHandle srcData = GetStringBytesPointer(state, bytesRef, "bytes_appb_src");
        EmitCopyBytes(state, destData, srcData, oldLen, "bytes_appb_copy");
        LlvmValueHandle newBytePtr = LlvmApi.BuildGEP2(builder, state.I8, destData, [oldLen], "bytes_appb_new_ptr");
        LlvmValueHandle truncated = LlvmApi.BuildTrunc(builder, byteVal, state.I8, "bytes_appb_byte");
        LlvmApi.BuildStore(builder, truncated, newBytePtr);
        return destRef;
    }

    private static LlvmValueHandle EmitBytesFromList(LlvmCodegenState state, LlvmValueHandle listRef)
    {
        // Two-pass: count cells, allocate, fill.
        // List cons layout: [head:i64 @ offset 0, tail:i64 @ offset 8]; nil = 0.
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle countSlot = LlvmApi.BuildAlloca(builder, state.I64, "bfl_count");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), countSlot);
        LlvmValueHandle curSlot = LlvmApi.BuildAlloca(builder, state.I64, "bfl_cur");
        LlvmApi.BuildStore(builder, listRef, curSlot);

        var countLoopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "bfl_count_loop");
        var countBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "bfl_count_body");
        var allocBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "bfl_alloc");
        var fillLoopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "bfl_fill_loop");
        var fillBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "bfl_fill_body");
        var doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "bfl_done");

        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "bfl_result");

        EmitBytesFromListCountPass(state, countSlot, curSlot, countLoopBlock, countBodyBlock, allocBlock);
        EmitBytesFromListAllocAndFill(state, listRef, countSlot, curSlot, resultSlot, allocBlock, fillLoopBlock, fillBodyBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "bfl_result_val");
    }

    private static void EmitBytesFromListCountPass(
        LlvmCodegenState state, LlvmValueHandle countSlot, LlvmValueHandle curSlot,
        LlvmBasicBlockHandle countLoopBlock, LlvmBasicBlockHandle countBodyBlock, LlvmBasicBlockHandle allocBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        // Pass 1: count.
        LlvmApi.BuildBr(builder, countLoopBlock);
        LlvmApi.PositionBuilderAtEnd(builder, countLoopBlock);
        LlvmValueHandle curCount = LlvmApi.BuildLoad2(builder, state.I64, curSlot, "bfl_cur_count");
        LlvmValueHandle countDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, curCount, LlvmApi.ConstInt(state.I64, 0, 0), "bfl_count_done");
        LlvmApi.BuildCondBr(builder, countDone, allocBlock, countBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, countBodyBlock);
        LlvmValueHandle cnt = LlvmApi.BuildLoad2(builder, state.I64, countSlot, "bfl_cnt_val");
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, cnt, LlvmApi.ConstInt(state.I64, 1, 0), "bfl_cnt_next"), countSlot);
        LlvmValueHandle tailCount = LoadMemory(state, curCount, 8, "bfl_tail_count");
        LlvmApi.BuildStore(builder, tailCount, curSlot);
        LlvmApi.BuildBr(builder, countLoopBlock);
    }

    private static void EmitBytesFromListAllocAndFill(
        LlvmCodegenState state, LlvmValueHandle listRef, LlvmValueHandle countSlot, LlvmValueHandle curSlot, LlvmValueHandle resultSlot,
        LlvmBasicBlockHandle allocBlock, LlvmBasicBlockHandle fillLoopBlock, LlvmBasicBlockHandle fillBodyBlock, LlvmBasicBlockHandle doneBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        // Allocate result.
        LlvmApi.PositionBuilderAtEnd(builder, allocBlock);
        LlvmValueHandle length = LlvmApi.BuildLoad2(builder, state.I64, countSlot, "bfl_length");
        LlvmValueHandle totalBytes = LlvmApi.BuildAdd(builder, length, LlvmApi.ConstInt(state.I64, 8, 0), "bfl_total_bytes");
        LlvmValueHandle destRef = EmitAllocDynamic(state, totalBytes);
        StoreMemory(state, destRef, 0, length, "bfl_dest_len");
        LlvmApi.BuildStore(builder, destRef, resultSlot);
        // Reset cursor for fill pass.
        LlvmApi.BuildStore(builder, listRef, curSlot);
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, "bfl_index");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), indexSlot);
        LlvmApi.BuildBr(builder, fillLoopBlock);

        // Pass 2: fill.
        LlvmApi.PositionBuilderAtEnd(builder, fillLoopBlock);
        LlvmValueHandle curFill = LlvmApi.BuildLoad2(builder, state.I64, curSlot, "bfl_cur_fill");
        LlvmValueHandle fillDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, curFill, LlvmApi.ConstInt(state.I64, 0, 0), "bfl_fill_done");
        LlvmApi.BuildCondBr(builder, fillDone, doneBlock, fillBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, fillBodyBlock);
        LlvmValueHandle headVal = LoadMemory(state, curFill, 0, "bfl_head");
        LlvmValueHandle idx = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "bfl_idx");
        LlvmValueHandle destData = GetStringBytesPointer(state, destRef, "bfl_dest_data");
        LlvmValueHandle elemPtr = LlvmApi.BuildGEP2(builder, state.I8, destData, [idx], "bfl_elem_ptr");
        LlvmValueHandle truncByte = LlvmApi.BuildTrunc(builder, headVal, state.I8, "bfl_byte");
        LlvmApi.BuildStore(builder, truncByte, elemPtr);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, idx, LlvmApi.ConstInt(state.I64, 1, 0), "bfl_idx_next"), indexSlot);
        LlvmValueHandle tailFill = LoadMemory(state, curFill, 8, "bfl_tail_fill");
        LlvmApi.BuildStore(builder, tailFill, curSlot);
        LlvmApi.BuildBr(builder, fillLoopBlock);
    }

    private static LlvmValueHandle EmitBytesU16Le(LlvmCodegenState state, LlvmValueHandle value)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // Allocate 8 (length) + 8 aligned for 2 bytes.
        LlvmValueHandle bytesRef = EmitAlloc(state, 16);
        StoreMemory(state, bytesRef, 0, LlvmApi.ConstInt(state.I64, 2, 0), "bytes_u16_len");
        LlvmValueHandle dataPtr = GetStringBytesPointer(state, bytesRef, "bytes_u16_data");
        // Store two LE bytes.
        LlvmValueHandle b0 = LlvmApi.BuildTrunc(builder, value, state.I8, "bytes_u16_b0");
        LlvmValueHandle b1 = LlvmApi.BuildTrunc(builder, LlvmApi.BuildLShr(builder, value, LlvmApi.ConstInt(state.I64, 8, 0), "bytes_u16_shr8"), state.I8, "bytes_u16_b1");
        LlvmValueHandle ptr0 = LlvmApi.BuildGEP2(builder, state.I8, dataPtr, [LlvmApi.ConstInt(state.I64, 0, 0)], "bytes_u16_ptr0");
        LlvmValueHandle ptr1 = LlvmApi.BuildGEP2(builder, state.I8, dataPtr, [LlvmApi.ConstInt(state.I64, 1, 0)], "bytes_u16_ptr1");
        LlvmApi.BuildStore(builder, b0, ptr0);
        LlvmApi.BuildStore(builder, b1, ptr1);
        return bytesRef;
    }

    private static LlvmValueHandle EmitBytesU32Le(LlvmCodegenState state, LlvmValueHandle value)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle bytesRef = EmitAlloc(state, 16);
        StoreMemory(state, bytesRef, 0, LlvmApi.ConstInt(state.I64, 4, 0), "bytes_u32_len");
        LlvmValueHandle dataPtr = GetStringBytesPointer(state, bytesRef, "bytes_u32_data");
        for (int i = 0; i < 4; i++)
        {
            LlvmValueHandle shifted = i == 0 ? value : LlvmApi.BuildLShr(builder, value, LlvmApi.ConstInt(state.I64, (ulong)(i * 8), 0), $"bytes_u32_shr{i}");
            LlvmValueHandle byteVal = LlvmApi.BuildTrunc(builder, shifted, state.I8, $"bytes_u32_b{i}");
            LlvmValueHandle ptr = LlvmApi.BuildGEP2(builder, state.I8, dataPtr, [LlvmApi.ConstInt(state.I64, (ulong)i, 0)], $"bytes_u32_ptr{i}");
            LlvmApi.BuildStore(builder, byteVal, ptr);
        }
        return bytesRef;
    }

    private static LlvmValueHandle EmitBytesU64Le(LlvmCodegenState state, LlvmValueHandle value)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle bytesRef = EmitAlloc(state, 16);
        StoreMemory(state, bytesRef, 0, LlvmApi.ConstInt(state.I64, 8, 0), "bytes_u64_len");
        LlvmValueHandle dataPtr = GetStringBytesPointer(state, bytesRef, "bytes_u64_data");
        for (int i = 0; i < 8; i++)
        {
            LlvmValueHandle shifted = i == 0 ? value : LlvmApi.BuildLShr(builder, value, LlvmApi.ConstInt(state.I64, (ulong)(i * 8), 0), $"bytes_u64_shr{i}");
            LlvmValueHandle byteVal = LlvmApi.BuildTrunc(builder, shifted, state.I8, $"bytes_u64_b{i}");
            LlvmValueHandle ptr = LlvmApi.BuildGEP2(builder, state.I8, dataPtr, [LlvmApi.ConstInt(state.I64, (ulong)i, 0)], $"bytes_u64_ptr{i}");
            LlvmApi.BuildStore(builder, byteVal, ptr);
        }
        return bytesRef;
    }

    private static LlvmValueHandle EmitBytesGetU16Le(LlvmCodegenState state, LlvmValueHandle bytesRef, LlvmValueHandle offsetVal)
    {
        return EmitBytesReadLeUnsigned(state, bytesRef, offsetVal, 2, "bytes_getu16");
    }

    private static LlvmValueHandle EmitBytesGetU32Le(LlvmCodegenState state, LlvmValueHandle bytesRef, LlvmValueHandle offsetVal)
    {
        return EmitBytesReadLeUnsigned(state, bytesRef, offsetVal, 4, "bytes_getu32");
    }

    private static LlvmValueHandle EmitBytesGetU64Le(LlvmCodegenState state, LlvmValueHandle bytesRef, LlvmValueHandle offsetVal)
    {
        return EmitBytesReadLeUnsigned(state, bytesRef, offsetVal, 8, "bytes_getu64");
    }

    private static LlvmValueHandle EmitBytesReadLeUnsigned(LlvmCodegenState state, LlvmValueHandle bytesRef, LlvmValueHandle offsetVal, int byteCount, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle len = LoadStringLength(state, bytesRef, prefix + "_len");
        // Bounds check: offset + byteCount <= len
        LlvmValueHandle end = LlvmApi.BuildAdd(builder, offsetVal, LlvmApi.ConstInt(state.I64, (ulong)byteCount, 0), prefix + "_end");
        LlvmValueHandle oob = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, end, len, prefix + "_oob");

        var panicBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_panic");
        var okBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_ok");
        LlvmApi.BuildCondBr(builder, oob, panicBlock, okBlock);

        LlvmApi.PositionBuilderAtEnd(builder, panicBlock);
        EmitPanic(state, EmitStackStringObject(state, $"Bytes.getU{byteCount * 8}Le: offset out of bounds"));

        LlvmApi.PositionBuilderAtEnd(builder, okBlock);
        LlvmValueHandle dataPtr = GetStringBytesPointer(state, bytesRef, prefix + "_data");
        LlvmValueHandle result = LlvmApi.ConstInt(state.I64, 0, 0);
        for (int i = 0; i < byteCount; i++)
        {
            LlvmValueHandle idx = LlvmApi.BuildAdd(builder, offsetVal, LlvmApi.ConstInt(state.I64, (ulong)i, 0), prefix + $"_idx{i}");
            LlvmValueHandle elemPtr = LlvmApi.BuildGEP2(builder, state.I8, dataPtr, [idx], prefix + $"_eptr{i}");
            LlvmValueHandle byteVal = LlvmApi.BuildLoad2(builder, state.I8, elemPtr, prefix + $"_byte{i}");
            LlvmValueHandle extended = LlvmApi.BuildZExt(builder, byteVal, state.I64, prefix + $"_ext{i}");
            if (i == 0)
            {
                result = extended;
            }
            else
            {
                LlvmValueHandle shifted = LlvmApi.BuildShl(builder, extended, LlvmApi.ConstInt(state.I64, (ulong)(i * 8), 0), prefix + $"_shl{i}");
                result = LlvmApi.BuildOr(builder, result, shifted, prefix + $"_or{i}");
            }
        }
        return result;
    }
}
