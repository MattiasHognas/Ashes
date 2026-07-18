using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{
    // PCRE2 compile/match option flags (see pcre2.h).
    private const ulong Pcre2Utf = 0x00080000;                    // treat pattern + subject as UTF-8
    private const ulong Pcre2Ucp = 0x00020000;                    // \d \w \s etc. use Unicode properties
    private const ulong Pcre2SubstituteGlobal = 0x00000100;       // replace all matches
    private const ulong Pcre2SubstituteOverflowLength = 0x00001000; // report required length instead of failing
    private const int Pcre2ErrorNomemory = -48;

    // The compiled-pattern + per-match scratch region. Compiled patterns (pcre2_code*) persist for
    // the program's lifetime; per-match allocations (match data, heap frames) are reclaimed by a
    // cursor save/restore bracketed around each match/substitute. 64 MiB is lazily backed by the OS
    // (Linux only faults in touched pages); exhaustion makes malloc return NULL, which PCRE2 reports
    // as PCRE2_ERROR_NOMEMORY and the emitters surface as "no match" / an empty result.
    private const ulong Pcre2RegionBytes = 64UL * 1024 * 1024;

    private static (LlvmValueHandle Cursor, LlvmValueHandle End) GetPcre2RegionGlobals(LlvmTargetContext target)
    {
        LlvmTypeHandle i64 = LlvmApi.Int64TypeInContext(target.Context);
        LlvmValueHandle cursor = target.GetOrAddNamedGlobal("__ashes_pcre2_cursor", () =>
        {
            LlvmValueHandle g = LlvmApi.AddGlobal(target.Module, i64, "__ashes_pcre2_cursor");
            LlvmApi.SetInitializer(g, LlvmApi.ConstInt(i64, 0, 0));
            LlvmApi.SetLinkage(g, LlvmLinkage.Internal);
            return g;
        });
        LlvmValueHandle end = target.GetOrAddNamedGlobal("__ashes_pcre2_end", () =>
        {
            LlvmValueHandle g = LlvmApi.AddGlobal(target.Module, i64, "__ashes_pcre2_end");
            LlvmApi.SetInitializer(g, LlvmApi.ConstInt(i64, 0, 0));
            LlvmApi.SetLinkage(g, LlvmLinkage.Internal);
            return g;
        });
        return (cursor, end);
    }

    /// <summary>
    /// Emits the <c>malloc</c>/<c>free</c> definitions the linked PCRE2 payload calls. They are a
    /// pure bump allocator over the region (see <see cref="GetPcre2RegionGlobals"/>); the region
    /// itself is lazily OS-allocated on the first regex op by <see cref="EmitEnsurePcre2Region"/>
    /// (which runs in a user function that has a valid state for mmap/VirtualAlloc). malloc has
    /// external linkage so the optimizer keeps it even though its only caller (PCRE2) is linked in
    /// afterwards. Emitted once, gated on <see cref="ProgramUsesRegexRuntimeAbi"/>.
    /// </summary>
    private static void EmitPcre2Allocator(LlvmTargetContext target, LlvmTypeHandle i64, LlvmTypeHandle i8Ptr)
    {
        LlvmBuilderHandle builder = target.Builder;
        var (cursorGlobal, endGlobal) = GetPcre2RegionGlobals(target);

        // void *malloc(size_t size) : bump the region, NULL on exhaustion
        {
            LlvmTypeHandle mallocType = LlvmApi.FunctionType(i8Ptr, [i64]);
            LlvmValueHandle fn = LlvmApi.AddFunction(target.Module, "malloc", mallocType);
            ApplyBuiltinAttributes(target, fn, isReadOnly: false, returnsPointer: true);

            LlvmBasicBlockHandle entry = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "entry");
            LlvmBasicBlockHandle okBlock = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "ok");
            LlvmBasicBlockHandle failBlock = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "fail");

            LlvmApi.PositionBuilderAtEnd(builder, entry);
            LlvmValueHandle size = LlvmApi.GetParam(fn, 0);
            // Align up to 16 bytes so PCRE2's structures stay suitably aligned.
            LlvmValueHandle aligned = LlvmApi.BuildAnd(builder,
                LlvmApi.BuildAdd(builder, size, LlvmApi.ConstInt(i64, 15, 0), "malloc_size_pad"),
                LlvmApi.ConstInt(i64, 0xFFFFFFFFFFFFFFF0UL, 0), "malloc_size_aligned");
            LlvmValueHandle cursor = LlvmApi.BuildLoad2(builder, i64, cursorGlobal, "malloc_cursor");
            LlvmValueHandle end = LlvmApi.BuildLoad2(builder, i64, endGlobal, "malloc_end");
            LlvmValueHandle next = LlvmApi.BuildAdd(builder, cursor, aligned, "malloc_next");
            // Exhausted when next > end (unsigned). An uninitialised region (cursor == end == 0)
            // also fails here, since next > 0.
            LlvmValueHandle overflow = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, next, end, "malloc_overflow");
            LlvmApi.BuildCondBr(builder, overflow, failBlock, okBlock);

            LlvmApi.PositionBuilderAtEnd(builder, okBlock);
            LlvmApi.BuildStore(builder, next, cursorGlobal);
            LlvmApi.BuildRet(builder, LlvmApi.BuildIntToPtr(builder, cursor, i8Ptr, "malloc_ptr"));

            LlvmApi.PositionBuilderAtEnd(builder, failBlock);
            LlvmApi.BuildRet(builder, LlvmApi.ConstNull(i8Ptr));
        }

        // void free(void *ptr) : no-op; the region is reclaimed by cursor restore
        {
            LlvmTypeHandle freeType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(target.Context), [i8Ptr]);
            LlvmValueHandle fn = LlvmApi.AddFunction(target.Module, "free", freeType);
            LlvmBasicBlockHandle entry = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "entry");
            LlvmApi.PositionBuilderAtEnd(builder, entry);
            LlvmApi.BuildRetVoid(builder);
        }
    }

    /// <summary>Lazily OS-allocates the PCRE2 region on first use (cursor == 0). Emitted inline at
    /// the start of every regex emitter, so malloc always sees an initialised region.</summary>
    private static void EmitEnsurePcre2Region(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        var (cursorGlobal, endGlobal) = GetPcre2RegionGlobals(state.Target);
        LlvmValueHandle cursor = LlvmApi.BuildLoad2(builder, state.I64, cursorGlobal, "pcre2_region_cursor");
        LlvmValueHandle uninitialised = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, cursor, LlvmApi.ConstInt(state.I64, 0, 0), "pcre2_region_uninit");

        var initBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "pcre2_region_init");
        var contBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "pcre2_region_cont");
        LlvmApi.BuildCondBr(builder, uninitialised, initBlock, contBlock);

        LlvmApi.PositionBuilderAtEnd(builder, initBlock);
        LlvmValueHandle regionBase = EmitAllocateOsMemory(state, LlvmApi.ConstInt(state.I64, Pcre2RegionBytes, 0), "pcre2_region");
        LlvmApi.BuildStore(builder, regionBase, cursorGlobal);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, regionBase, LlvmApi.ConstInt(state.I64, Pcre2RegionBytes, 0), "pcre2_region_end"), endGlobal);
        LlvmApi.BuildBr(builder, contBlock);

        LlvmApi.PositionBuilderAtEnd(builder, contBlock);
    }

    private static LlvmValueHandle GetOrDeclarePcre2Function(LlvmCodegenState state, string name, LlvmTypeHandle fnType)
    {
        LlvmValueHandle fn = LlvmApi.GetNamedFunction(state.Target.Module, name);
        if (fn.Ptr == 0)
        {
            fn = LlvmApi.AddFunction(state.Target.Module, name, fnType);
        }

        return fn;
    }

    // pcre2_compile_8(pattern, length, options, &errorcode, &erroffset, ccontext) -> pcre2_code*
    private static LlvmValueHandle EmitPcre2Compile(LlvmCodegenState state, LlvmValueHandle patternRef, LlvmValueHandle errcodeSlot, LlvmValueHandle erroffsetSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle patBytes = GetStringBytesPointer(state, patternRef, "rx_pat");
        LlvmValueHandle patLen = LoadStringLength(state, patternRef, "rx_pat_len");
        LlvmTypeHandle fnType = LlvmApi.FunctionType(state.I8Ptr, [state.I8Ptr, state.I64, state.I32, state.I8Ptr, state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle fn = GetOrDeclarePcre2Function(state, "pcre2_compile_8", fnType);
        return LlvmApi.BuildCall2(builder, fnType, fn,
            [
                patBytes,
                patLen,
                LlvmApi.ConstInt(state.I32, Pcre2Utf | Pcre2Ucp, 0),
                errcodeSlot,
                erroffsetSlot,
                LlvmApi.ConstNull(state.I8Ptr),
            ],
            "rx_code");
    }

    private static LlvmValueHandle EmitRegexCompile(LlvmCodegenState state, LlvmValueHandle patternRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        EmitEnsurePcre2Region(state);
        LlvmValueHandle errcodeSlot = LlvmApi.BuildAlloca(builder, state.I32, "rx_errcode");
        LlvmValueHandle erroffsetSlot = LlvmApi.BuildAlloca(builder, state.I64, "rx_erroffset");
        LlvmValueHandle code = EmitPcre2Compile(state, patternRef, errcodeSlot, erroffsetSlot);
        return LlvmApi.BuildPtrToInt(builder, code, state.I64, "rx_code_i64");
    }

    private static LlvmValueHandle EmitRegexCompileError(LlvmCodegenState state, LlvmValueHandle patternRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        EmitEnsurePcre2Region(state);
        LlvmValueHandle errcodeSlot = LlvmApi.BuildAlloca(builder, state.I32, "rx_err_errcode");
        LlvmValueHandle erroffsetSlot = LlvmApi.BuildAlloca(builder, state.I64, "rx_err_erroffset");
        LlvmValueHandle code = EmitPcre2Compile(state, patternRef, errcodeSlot, erroffsetSlot);

        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "rx_err_result");
        LlvmValueHandle isError = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            LlvmApi.BuildPtrToInt(builder, code, state.I64, "rx_err_code_i64"),
            LlvmApi.ConstInt(state.I64, 0, 0), "rx_err_is_error");

        var errorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rx_err_error");
        var okBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rx_err_ok");
        var contBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rx_err_cont");
        LlvmApi.BuildCondBr(builder, isError, errorBlock, okBlock);

        // Valid pattern: empty message.
        LlvmApi.PositionBuilderAtEnd(builder, okBlock);
        LlvmApi.BuildStore(builder, EmitHeapStringLiteral(state, ""), resultSlot);
        LlvmApi.BuildBr(builder, contBlock);

        // Invalid pattern: render the PCRE2 message for the error code into a buffer.
        LlvmApi.PositionBuilderAtEnd(builder, errorBlock);
        LlvmTypeHandle bufType = LlvmApi.ArrayType2(state.I8, 256);
        LlvmValueHandle buf = LlvmApi.BuildAlloca(builder, bufType, "rx_err_buf");
        LlvmValueHandle bufPtr = GetArrayElementPointer(state, bufType, buf, LlvmApi.ConstInt(state.I64, 0, 0), "rx_err_buf_ptr");
        LlvmTypeHandle msgFnType = LlvmApi.FunctionType(state.I32, [state.I32, state.I8Ptr, state.I64]);
        LlvmValueHandle msgFn = GetOrDeclarePcre2Function(state, "pcre2_get_error_message_8", msgFnType);
        LlvmValueHandle msgLen = LlvmApi.BuildCall2(builder, msgFnType, msgFn,
            [LlvmApi.BuildLoad2(builder, state.I32, errcodeSlot, "rx_err_errcode_val"), bufPtr, LlvmApi.ConstInt(state.I64, 256, 0)],
            "rx_err_msg_len");
        LlvmValueHandle msgLen64 = LlvmApi.BuildSExt(builder, msgLen, state.I64, "rx_err_msg_len64");
        LlvmValueHandle negative = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, msgLen64, LlvmApi.ConstInt(state.I64, 0, 0), "rx_err_msg_neg");
        LlvmValueHandle clampedLen = LlvmApi.BuildSelect(builder, negative, LlvmApi.ConstInt(state.I64, 0, 0), msgLen64, "rx_err_msg_len_clamped");
        LlvmApi.BuildStore(builder, EmitHeapStringSliceFromBytesPointer(state, bufPtr, clampedLen, "rx_err_msg"), resultSlot);
        LlvmApi.BuildBr(builder, contBlock);

        LlvmApi.PositionBuilderAtEnd(builder, contBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "rx_err_result_val");
    }

    // Shared match prelude: create match data, run pcre2_match at startOffset, return (rc, md, ovector).
    private static (LlvmValueHandle Rc, LlvmValueHandle Ovector) EmitPcre2Match(
        LlvmCodegenState state, LlvmValueHandle code, LlvmValueHandle subjBytes, LlvmValueHandle subjLen, LlvmValueHandle startI64)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle createFnType = LlvmApi.FunctionType(state.I8Ptr, [state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle createFn = GetOrDeclarePcre2Function(state, "pcre2_match_data_create_from_pattern_8", createFnType);
        LlvmValueHandle md = LlvmApi.BuildCall2(builder, createFnType, createFn, [code, LlvmApi.ConstNull(state.I8Ptr)], "rx_md");

        LlvmTypeHandle matchFnType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, state.I64, state.I64, state.I32, state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle matchFn = GetOrDeclarePcre2Function(state, "pcre2_match_8", matchFnType);
        LlvmValueHandle rc = LlvmApi.BuildCall2(builder, matchFnType, matchFn,
            [code, subjBytes, subjLen, startI64, LlvmApi.ConstInt(state.I32, 0, 0), md, LlvmApi.ConstNull(state.I8Ptr)],
            "rx_rc");

        LlvmTypeHandle ovFnType = LlvmApi.FunctionType(state.I8Ptr, [state.I8Ptr]);
        LlvmValueHandle ovFn = GetOrDeclarePcre2Function(state, "pcre2_get_ovector_pointer_8", ovFnType);
        LlvmValueHandle ovector = LlvmApi.BuildCall2(builder, ovFnType, ovFn, [md], "rx_ovector");
        return (rc, LlvmApi.BuildPtrToInt(builder, ovector, state.I64, "rx_ovector_i64"));
    }

    // find(code, subject, start) -> Option((Int, Int)) : the next match's (start, end) byte offsets.
    private static LlvmValueHandle EmitRegexFind(LlvmCodegenState state, LlvmValueHandle codeI64, LlvmValueHandle subjectRef, LlvmValueHandle startI64)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        EmitEnsurePcre2Region(state);
        var (cursorGlobal, _) = GetPcre2RegionGlobals(state.Target);
        LlvmValueHandle saved = LlvmApi.BuildLoad2(builder, state.I64, cursorGlobal, "rx_find_saved");

        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "rx_find_result");
        LlvmApi.BuildStore(builder, EmitAllocAdt(state, 0, 0), resultSlot); // None

        LlvmValueHandle code = LlvmApi.BuildIntToPtr(builder, codeI64, state.I8Ptr, "rx_find_code");
        LlvmValueHandle subjBytes = GetStringBytesPointer(state, subjectRef, "rx_find_subj");
        LlvmValueHandle subjLen = LoadStringLength(state, subjectRef, "rx_find_len");
        var (rc, ovector) = EmitPcre2Match(state, code, subjBytes, subjLen, startI64);

        LlvmValueHandle matched = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, rc, LlvmApi.ConstInt(state.I32, 1, 0), "rx_find_matched");
        var matchedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rx_find_hit");
        var contBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rx_find_cont");
        LlvmApi.BuildCondBr(builder, matched, matchedBlock, contBlock);

        LlvmApi.PositionBuilderAtEnd(builder, matchedBlock);
        LlvmValueHandle start0 = LoadMemory(state, ovector, 0, "rx_find_start");
        LlvmValueHandle end0 = LoadMemory(state, ovector, 8, "rx_find_end");
        LlvmValueHandle tuple = EmitAlloc(state, 16);
        StoreMemory(state, tuple, 0, start0, "rx_find_tuple_start");
        StoreMemory(state, tuple, 8, end0, "rx_find_tuple_end");
        LlvmValueHandle some = EmitAllocAdt(state, 1, 1);
        StoreMemory(state, some, 8, tuple, "rx_find_some");
        LlvmApi.BuildStore(builder, some, resultSlot);
        LlvmApi.BuildBr(builder, contBlock);

        LlvmApi.PositionBuilderAtEnd(builder, contBlock);
        LlvmApi.BuildStore(builder, saved, cursorGlobal); // reclaim scratch
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "rx_find_result_val");
    }

    // captures(code, subject, start) -> Option(List(Option(Str))) : all group substrings (None = unset).
    private static LlvmValueHandle EmitRegexCaptures(LlvmCodegenState state, LlvmValueHandle codeI64, LlvmValueHandle subjectRef, LlvmValueHandle startI64)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        EmitEnsurePcre2Region(state);
        var (cursorGlobal, _) = GetPcre2RegionGlobals(state.Target);
        LlvmValueHandle saved = LlvmApi.BuildLoad2(builder, state.I64, cursorGlobal, "rx_cap_saved");

        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "rx_cap_result");
        LlvmApi.BuildStore(builder, EmitAllocAdt(state, 0, 0), resultSlot); // None

        LlvmValueHandle code = LlvmApi.BuildIntToPtr(builder, codeI64, state.I8Ptr, "rx_cap_code");
        LlvmValueHandle subjBytes = GetStringBytesPointer(state, subjectRef, "rx_cap_subj");
        LlvmValueHandle subjLen = LoadStringLength(state, subjectRef, "rx_cap_len");
        var (rc, ovector) = EmitPcre2Match(state, code, subjBytes, subjLen, startI64);

        // ovector count (pairs). pcre2_get_ovector_count_8 exists but is not in the keep-list; derive
        // the group count from rc instead (rc = 1 + highest set group, >= 1 on a match).
        LlvmValueHandle matched = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, rc, LlvmApi.ConstInt(state.I32, 1, 0), "rx_cap_matched");
        var matchedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rx_cap_hit");
        var contBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rx_cap_cont");
        LlvmApi.BuildCondBr(builder, matched, matchedBlock, contBlock);

        LlvmApi.PositionBuilderAtEnd(builder, matchedBlock);
        EmitRegexCapturesBuildList(state, rc, ovector, subjBytes, resultSlot, contBlock);

        LlvmApi.PositionBuilderAtEnd(builder, contBlock);
        LlvmApi.BuildStore(builder, saved, cursorGlobal); // reclaim scratch
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "rx_cap_result_val");
    }

    private static void EmitRegexCapturesBuildList(
        LlvmCodegenState state, LlvmValueHandle rc, LlvmValueHandle ovector, LlvmValueHandle subjBytes,
        LlvmValueHandle resultSlot, LlvmBasicBlockHandle contBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle count = LlvmApi.BuildSExt(builder, rc, state.I64, "rx_cap_count");
        LlvmValueHandle listSlot = LlvmApi.BuildAlloca(builder, state.I64, "rx_cap_list");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), listSlot); // Nil
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, "rx_cap_index");
        LlvmApi.BuildStore(builder, count, indexSlot);

        var loopCheck = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rx_cap_loop_check");
        var loopBody = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rx_cap_loop_body");
        var unsetBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rx_cap_unset");
        var setBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rx_cap_set");
        var elemCont = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rx_cap_elem_cont");
        var loopDone = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rx_cap_loop_done");
        LlvmApi.BuildBr(builder, loopCheck);

        LlvmApi.PositionBuilderAtEnd(builder, loopCheck);
        LlvmValueHandle idx = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "rx_cap_idx");
        LlvmValueHandle more = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, idx, LlvmApi.ConstInt(state.I64, 0, 0), "rx_cap_more");
        LlvmApi.BuildCondBr(builder, more, loopBody, loopDone);

        LlvmApi.PositionBuilderAtEnd(builder, loopBody);
        LlvmValueHandle groupIndex = LlvmApi.BuildSub(builder, idx, LlvmApi.ConstInt(state.I64, 1, 0), "rx_cap_group");
        LlvmApi.BuildStore(builder, groupIndex, indexSlot);
        LlvmValueHandle pairAddr = LlvmApi.BuildAdd(builder, ovector, LlvmApi.BuildMul(builder, groupIndex, LlvmApi.ConstInt(state.I64, 16, 0), "rx_cap_pair_off"), "rx_cap_pair_addr");
        LlvmValueHandle groupStart = LoadMemory(state, pairAddr, 0, "rx_cap_gstart");
        LlvmValueHandle groupEnd = LoadMemory(state, pairAddr, 8, "rx_cap_gend");
        LlvmValueHandle elemSlot = LlvmApi.BuildAlloca(builder, state.I64, "rx_cap_elem");
        LlvmValueHandle isUnset = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, groupStart, LlvmApi.ConstInt(state.I64, 0xFFFFFFFFFFFFFFFFUL, 0), "rx_cap_unset_cmp");
        LlvmApi.BuildCondBr(builder, isUnset, unsetBlock, setBlock);

        EmitRegexCapturesElement(state, subjBytes, groupStart, groupEnd, elemSlot, unsetBlock, setBlock, elemCont);

        LlvmApi.PositionBuilderAtEnd(builder, elemCont);
        LlvmValueHandle cons = EmitAlloc(state, 16);
        StoreMemory(state, cons, 0, LlvmApi.BuildLoad2(builder, state.I64, elemSlot, "rx_cap_elem_val"), "rx_cap_cons_head");
        StoreMemory(state, cons, 8, LlvmApi.BuildLoad2(builder, state.I64, listSlot, "rx_cap_prev_list"), "rx_cap_cons_tail");
        LlvmApi.BuildStore(builder, cons, listSlot);
        LlvmApi.BuildBr(builder, loopCheck);

        LlvmApi.PositionBuilderAtEnd(builder, loopDone);
        LlvmValueHandle someList = EmitAllocAdt(state, 1, 1);
        StoreMemory(state, someList, 8, LlvmApi.BuildLoad2(builder, state.I64, listSlot, "rx_cap_final_list"), "rx_cap_some_list");
        LlvmApi.BuildStore(builder, someList, resultSlot);
        LlvmApi.BuildBr(builder, contBlock);
    }

    private static void EmitRegexCapturesElement(
        LlvmCodegenState state, LlvmValueHandle subjBytes, LlvmValueHandle groupStart, LlvmValueHandle groupEnd,
        LlvmValueHandle elemSlot, LlvmBasicBlockHandle unsetBlock, LlvmBasicBlockHandle setBlock, LlvmBasicBlockHandle elemCont)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        LlvmApi.PositionBuilderAtEnd(builder, unsetBlock);
        LlvmApi.BuildStore(builder, EmitAllocAdt(state, 0, 0), elemSlot); // None
        LlvmApi.BuildBr(builder, elemCont);

        LlvmApi.PositionBuilderAtEnd(builder, setBlock);
        LlvmValueHandle groupLen = LlvmApi.BuildSub(builder, groupEnd, groupStart, "rx_cap_glen");
        LlvmValueHandle groupPtr = LlvmApi.BuildGEP2(builder, state.I8, subjBytes, [groupStart], "rx_cap_gptr");
        LlvmValueHandle view = EmitStringView(state, groupPtr, groupLen, "rx_cap_view");
        LlvmValueHandle someElem = EmitAllocAdt(state, 1, 1);
        StoreMemory(state, someElem, 8, view, "rx_cap_some");
        LlvmApi.BuildStore(builder, someElem, elemSlot);
        LlvmApi.BuildBr(builder, elemCont);
    }

    // substitute(code, subject, replacement) -> Str : global replace, $1 group refs.
    private static LlvmValueHandle EmitRegexSubstitute(LlvmCodegenState state, LlvmValueHandle codeI64, LlvmValueHandle subjectRef, LlvmValueHandle replacementRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        EmitEnsurePcre2Region(state);
        var (cursorGlobal, _) = GetPcre2RegionGlobals(state.Target);
        LlvmValueHandle saved = LlvmApi.BuildLoad2(builder, state.I64, cursorGlobal, "rx_sub_saved");

        LlvmValueHandle code = LlvmApi.BuildIntToPtr(builder, codeI64, state.I8Ptr, "rx_sub_code");
        LlvmValueHandle subjBytes = GetStringBytesPointer(state, subjectRef, "rx_sub_subj");
        LlvmValueHandle subjLen = LoadStringLength(state, subjectRef, "rx_sub_len");
        LlvmValueHandle replBytes = GetStringBytesPointer(state, replacementRef, "rx_sub_repl");
        LlvmValueHandle replLen = LoadStringLength(state, replacementRef, "rx_sub_repl_len");

        LlvmTypeHandle subFnType = LlvmApi.FunctionType(state.I32,
            [state.I8Ptr, state.I8Ptr, state.I64, state.I64, state.I32, state.I8Ptr, state.I8Ptr, state.I8Ptr, state.I64, state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle subFn = GetOrDeclarePcre2Function(state, "pcre2_substitute_8", subFnType);
        ulong options = Pcre2SubstituteGlobal | Pcre2SubstituteOverflowLength;

        // First attempt with a heuristic buffer (2*subject + 256). outlen is in/out: in = capacity,
        // out = used length (or, on overflow, the required length).
        LlvmValueHandle cap0 = LlvmApi.BuildAdd(builder,
            LlvmApi.BuildMul(builder, subjLen, LlvmApi.ConstInt(state.I64, 2, 0), "rx_sub_cap0_2x"),
            LlvmApi.ConstInt(state.I64, 256, 0), "rx_sub_cap0");
        LlvmValueHandle outlenSlot = LlvmApi.BuildAlloca(builder, state.I64, "rx_sub_outlen");
        LlvmValueHandle bufSlot = LlvmApi.BuildAlloca(builder, state.I64, "rx_sub_buf");
        LlvmValueHandle buf0 = EmitAllocDynamic(state, cap0);
        LlvmApi.BuildStore(builder, buf0, bufSlot);
        LlvmApi.BuildStore(builder, cap0, outlenSlot);
        LlvmValueHandle rc0 = LlvmApi.BuildCall2(builder, subFnType, subFn,
            [
                code, subjBytes, subjLen, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I32, options, 0),
                LlvmApi.ConstNull(state.I8Ptr), LlvmApi.ConstNull(state.I8Ptr),
                replBytes, replLen,
                LlvmApi.BuildIntToPtr(builder, buf0, state.I8Ptr, "rx_sub_buf0_ptr"), outlenSlot,
            ],
            "rx_sub_rc0");

        LlvmValueHandle overflow = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, rc0, LlvmApi.ConstInt(state.I32, unchecked((ulong)Pcre2ErrorNomemory), 1), "rx_sub_overflow");
        var retryBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rx_sub_retry");
        var doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rx_sub_done");
        LlvmApi.BuildCondBr(builder, overflow, retryBlock, doneBlock);

        EmitRegexSubstituteRetry(state, subFnType, subFn, options, code, subjBytes, subjLen, replBytes, replLen,
            outlenSlot, bufSlot, retryBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        LlvmValueHandle outlen = LlvmApi.BuildLoad2(builder, state.I64, outlenSlot, "rx_sub_outlen_val");
        LlvmValueHandle buf = LlvmApi.BuildLoad2(builder, state.I64, bufSlot, "rx_sub_buf_val");
        LlvmValueHandle result = EmitHeapStringSliceFromBytesPointer(state, LlvmApi.BuildIntToPtr(builder, buf, state.I8Ptr, "rx_sub_buf_ptr"), outlen, "rx_sub_result");
        LlvmApi.BuildStore(builder, saved, cursorGlobal); // reclaim scratch
        return result;
    }

    private static void EmitRegexSubstituteRetry(
        LlvmCodegenState state, LlvmTypeHandle subFnType, LlvmValueHandle subFn, ulong options,
        LlvmValueHandle code, LlvmValueHandle subjBytes, LlvmValueHandle subjLen, LlvmValueHandle replBytes, LlvmValueHandle replLen,
        LlvmValueHandle outlenSlot, LlvmValueHandle bufSlot, LlvmBasicBlockHandle retryBlock, LlvmBasicBlockHandle doneBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        // Retry once with exactly the required length (outlen holds it after an OVERFLOW_LENGTH probe).
        LlvmApi.PositionBuilderAtEnd(builder, retryBlock);
        LlvmValueHandle need = LlvmApi.BuildLoad2(builder, state.I64, outlenSlot, "rx_sub_need");
        LlvmValueHandle buf1 = EmitAllocDynamic(state, need);
        LlvmApi.BuildStore(builder, buf1, bufSlot);
        LlvmApi.BuildStore(builder, need, outlenSlot);
        LlvmApi.BuildCall2(builder, subFnType, subFn,
            [
                code, subjBytes, subjLen, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I32, options, 0),
                LlvmApi.ConstNull(state.I8Ptr), LlvmApi.ConstNull(state.I8Ptr),
                replBytes, replLen,
                LlvmApi.BuildIntToPtr(builder, buf1, state.I8Ptr, "rx_sub_buf1_ptr"), outlenSlot,
            ],
            "rx_sub_rc1");
        LlvmApi.BuildBr(builder, doneBlock);
    }
}
