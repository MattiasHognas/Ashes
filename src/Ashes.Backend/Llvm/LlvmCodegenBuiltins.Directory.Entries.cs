using Ashes.Backend.Llvm.Interop;
using Ashes.Semantics;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{
    private static LlvmValueHandle EmitLinuxDirectoryEntriesCore(LlvmCodegenState state, LlvmValueHandle pathRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        var (arraySlot, countSlot, statusSlot, path) = CreateLinuxDirectoryEntriesState(state, pathRef);

        var (openBlock, streamBlock, loopBlock, entryBlock, nameBlock, appendBlock, closeBlock, finishBlock) = CreateLinuxDirectoryEntriesBlocks(state);
        LlvmApi.BuildBr(builder, openBlock);

        LlvmApi.PositionBuilderAtEnd(builder, openBlock);
        LlvmValueHandle fd = EmitLinuxSyscall(
            state,
            SyscallOpen,
            LlvmApi.BuildPtrToInt(builder, path, state.I64, "dir_entries_path_ptr"),
            LlvmApi.ConstInt(state.I64, 0xB0000, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "dir_entries_open_call");
        LlvmValueHandle openFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, fd, LlvmApi.ConstInt(state.I64, 0, 0), "dir_entries_open_failed");
        LlvmApi.BuildCondBr(builder, openFailed, finishBlock, streamBlock);

        LlvmValueHandle directory = EmitLinuxDirectoryStreamOpen(state, fd, streamBlock, loopBlock, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBlock);
        LlvmValueHandle errno = EmitLinuxErrnoPointer(state, "dir_entries_errno");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), errno);
        LlvmTypeHandle readdirType = LlvmApi.FunctionType(state.I8Ptr, [state.I8Ptr]);
        LlvmValueHandle entry = EmitLinuxImportedCall(state, "readdir", readdirType, [directory], "dir_entries_readdir");
        LlvmValueHandle atEnd = IsNullPointer(state, entry, "dir_entries_at_end");
        LlvmBasicBlockHandle endBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_entries_end");
        LlvmApi.BuildCondBr(builder, atEnd, endBlock, entryBlock);

        LlvmApi.PositionBuilderAtEnd(builder, endBlock);
        LlvmValueHandle readError = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildLoad2(builder, state.I32, errno, "dir_entries_errno_value"), LlvmApi.ConstInt(state.I32, 0, 0), "dir_entries_read_error");
        LlvmApi.BuildStore(builder, LlvmApi.BuildSelect(builder, readError, LlvmApi.ConstInt(state.I32, 1, 0), LlvmApi.ConstInt(state.I32, 0, 0), "dir_entries_read_status"), statusSlot);
        LlvmApi.BuildBr(builder, closeBlock);

        LlvmApi.PositionBuilderAtEnd(builder, entryBlock);
        LlvmValueHandle name = LlvmApi.BuildGEP2(builder, state.I8, entry, [LlvmApi.ConstInt(state.I64, 19, 0)], "dir_entries_name_ptr");
        LlvmValueHandle isDot = EmitDirectoryEntryIsDot(state, name, state.I8, "dir_entries");
        LlvmApi.BuildCondBr(builder, isDot, loopBlock, nameBlock);

        LlvmApi.PositionBuilderAtEnd(builder, nameBlock);
        LlvmValueHandle length = EmitLinuxStrlen(state, name, "dir_entries_name_length");
        LlvmValueHandle valid = EmitUtf8IsValid(state, name, length, "dir_entries_name");
        LlvmBasicBlockHandle invalidBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_entries_invalid_utf8");
        LlvmApi.BuildCondBr(builder, valid, appendBlock, invalidBlock);

        LlvmApi.PositionBuilderAtEnd(builder, appendBlock);
        LlvmValueHandle appendStatus = EmitDirectoryNameAppend(state, arraySlot, countSlot, name, "dir_entries_append_call");
        LlvmValueHandle appendFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, appendStatus, LlvmApi.ConstInt(state.I32, 0, 0), "dir_entries_append_failed");
        LlvmApi.BuildCondBr(builder, appendFailed, closeBlock, loopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, invalidBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 2, 0), statusSlot);
        LlvmApi.BuildBr(builder, closeBlock);

        EmitLinuxDirectoryEntriesClose(state, directory, statusSlot, closeBlock, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        return EmitDirectoryEntriesResult(state, arraySlot, countSlot, statusSlot, "dir_entries");
    }

    private static LlvmValueHandle EmitUtf8IsValid(
        LlvmCodegenState state,
        LlvmValueHandle bytes,
        LlvmValueHandle length,
        string prefix)
    {
        LlvmValueHandle validation = EmitValidateUtf8(state, bytes, length, prefix + "_utf8");
        return LlvmApi.BuildICmp(state.Target.Builder, LlvmIntPredicate.Ne, validation, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_valid");
    }

    private static LlvmValueHandle EmitLinuxDirectoryStreamOpen(
        LlvmCodegenState state,
        LlvmValueHandle fd,
        LlvmBasicBlockHandle streamBlock,
        LlvmBasicBlockHandle loopBlock,
        LlvmBasicBlockHandle finishBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, streamBlock);
        LlvmTypeHandle fdopendirType = LlvmApi.FunctionType(state.I8Ptr, [state.I32]);
        LlvmValueHandle directory = EmitLinuxImportedCall(state, "fdopendir", fdopendirType, [LlvmApi.BuildTrunc(builder, fd, state.I32, "dir_entries_fd_i32")], "dir_entries_fdopendir");
        LlvmBasicBlockHandle errorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_entries_stream_error");
        LlvmApi.BuildCondBr(builder, IsNullPointer(state, directory, "dir_entries_stream_failed"), errorBlock, loopBlock);
        LlvmApi.PositionBuilderAtEnd(builder, errorBlock);
        EmitLinuxSyscall(state, SyscallClose, fd, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), "dir_entries_stream_close");
        LlvmApi.BuildBr(builder, finishBlock);
        return directory;
    }

    private static LlvmValueHandle EmitLinuxErrnoPointer(LlvmCodegenState state, string prefix)
    {
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I32Ptr, []);
        LlvmValueHandle function = EmitOrDeclareExternalFunction(state, "__errno_location", functionType);
        return LlvmApi.BuildCall2(state.Target.Builder, functionType, function, Array.Empty<LlvmValueHandle>(), prefix);
    }

    private static LinuxDirectoryEntriesState CreateLinuxDirectoryEntriesState(LlvmCodegenState state, LlvmValueHandle pathRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle arraySlot = LlvmApi.BuildAlloca(builder, state.I8Ptr, "dir_entries_array");
        LlvmValueHandle countSlot = LlvmApi.BuildAlloca(builder, state.I64, "dir_entries_count");
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I32, "dir_entries_status");
        LlvmValueHandle path = EmitStringToCString(state, pathRef, "dir_entries_path");
        LlvmApi.BuildStore(builder, NullPointer(state), arraySlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), countSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 1, 0), statusSlot);
        return new(arraySlot, countSlot, statusSlot, path);
    }

    private readonly record struct LinuxDirectoryEntriesState(
        LlvmValueHandle ArraySlot,
        LlvmValueHandle CountSlot,
        LlvmValueHandle StatusSlot,
        LlvmValueHandle Path);

    private static LinuxDirectoryEntriesBlocks CreateLinuxDirectoryEntriesBlocks(LlvmCodegenState state)
        => new(
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_entries_open"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_entries_stream"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_entries_loop"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_entries_entry"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_entries_name"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_entries_append"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_entries_close"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_entries_finish"));

    private readonly record struct LinuxDirectoryEntriesBlocks(
        LlvmBasicBlockHandle Open,
        LlvmBasicBlockHandle Stream,
        LlvmBasicBlockHandle Loop,
        LlvmBasicBlockHandle Entry,
        LlvmBasicBlockHandle Name,
        LlvmBasicBlockHandle Append,
        LlvmBasicBlockHandle Close,
        LlvmBasicBlockHandle Finish);

    private static LlvmValueHandle EmitDirectoryEntryIsDot(
        LlvmCodegenState state,
        LlvmValueHandle name,
        LlvmTypeHandle characterType,
        string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle first = LlvmApi.BuildLoad2(builder, characterType, name, prefix + "_first");
        LlvmValueHandle second = LlvmApi.BuildLoad2(builder, characterType, LlvmApi.BuildGEP2(builder, characterType, name, [LlvmApi.ConstInt(state.I64, 1, 0)], prefix + "_second_ptr"), prefix + "_second");
        LlvmValueHandle third = LlvmApi.BuildLoad2(builder, characterType, LlvmApi.BuildGEP2(builder, characterType, name, [LlvmApi.ConstInt(state.I64, 2, 0)], prefix + "_third_ptr"), prefix + "_third");
        LlvmValueHandle firstDot = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, first, LlvmApi.ConstInt(characterType, (byte)'.', 0), prefix + "_first_dot");
        LlvmValueHandle oneDot = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, second, LlvmApi.ConstInt(characterType, 0, 0), prefix + "_one_dot");
        LlvmValueHandle secondDot = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, second, LlvmApi.ConstInt(characterType, (byte)'.', 0), prefix + "_second_dot");
        LlvmValueHandle thirdZero = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, third, LlvmApi.ConstInt(characterType, 0, 0), prefix + "_third_zero");
        LlvmValueHandle twoDots = LlvmApi.BuildAnd(builder, secondDot, thirdZero, prefix + "_two_dots");
        return LlvmApi.BuildAnd(builder, firstDot, LlvmApi.BuildOr(builder, oneDot, twoDots, prefix + "_dot_tail"), prefix + "_is_dot");
    }

    private static void EmitLinuxDirectoryEntriesClose(
        LlvmCodegenState state,
        LlvmValueHandle directory,
        LlvmValueHandle statusSlot,
        LlvmBasicBlockHandle closeBlock,
        LlvmBasicBlockHandle finishBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, closeBlock);
        LlvmTypeHandle closedirType = LlvmApi.FunctionType(state.I32, [state.I8Ptr]);
        LlvmValueHandle closeStatus = EmitLinuxImportedCall(state, "closedir", closedirType, [directory], "dir_entries_closedir");
        LlvmValueHandle priorStatus = LlvmApi.BuildLoad2(builder, state.I32, statusSlot, "dir_entries_prior_status");
        LlvmValueHandle closeOk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, closeStatus, LlvmApi.ConstInt(state.I32, 0, 0), "dir_entries_close_ok");
        LlvmValueHandle normalized = LlvmApi.BuildSelect(builder, closeOk, priorStatus, LlvmApi.ConstInt(state.I32, 1, 0), "dir_entries_final_status");
        LlvmApi.BuildStore(builder, normalized, statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static LlvmValueHandle EmitDirectoryNameAppend(
        LlvmCodegenState state,
        LlvmValueHandle arraySlot,
        LlvmValueHandle countSlot,
        LlvmValueHandle name,
        string prefix)
    {
        LlvmValueHandle function = EmitOrGetDirectoryNameAppend(state);
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, state.I8Ptr]);
        return LlvmApi.BuildCall2(
            state.Target.Builder,
            functionType,
            function,
            [LlvmApi.BuildBitCast(state.Target.Builder, arraySlot, state.I8Ptr, prefix + "_array_slot"), LlvmApi.BuildBitCast(state.Target.Builder, countSlot, state.I8Ptr, prefix + "_count_slot"), name],
            prefix);
    }

    private static LlvmValueHandle EmitOrGetDirectoryNameAppend(LlvmCodegenState state)
    {
        const string symbol = "__ashes_directory_name_append";
        LlvmValueHandle existing = LlvmApi.GetNamedFunction(state.Target.Module, symbol);
        if (existing.Ptr != 0)
        {
            return existing;
        }

        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmBasicBlockHandle saved = LlvmApi.GetInsertBlock(builder);
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle function = LlvmApi.AddFunction(state.Target.Module, symbol, functionType);
        LlvmBasicBlockHandle entry = LlvmApi.AppendBasicBlockInContext(state.Target.Context, function, "entry");
        LlvmBasicBlockHandle allocationFailed = LlvmApi.AppendBasicBlockInContext(state.Target.Context, function, "allocation_failed");
        LlvmBasicBlockHandle copyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, function, "copy");
        LlvmBasicBlockHandle reallocFailed = LlvmApi.AppendBasicBlockInContext(state.Target.Context, function, "realloc_failed");
        LlvmBasicBlockHandle success = LlvmApi.AppendBasicBlockInContext(state.Target.Context, function, "success");
        LlvmApi.PositionBuilderAtEnd(builder, entry);
        LlvmValueHandle arraySlot = LlvmApi.GetParam(function, 0);
        LlvmValueHandle countSlot = LlvmApi.GetParam(function, 1);
        LlvmValueHandle name = LlvmApi.GetParam(function, 2);
        LlvmTypeHandle strlenType = LlvmApi.FunctionType(state.I64, [state.I8Ptr]);
        LlvmValueHandle strlen = EmitOrDeclareExternalFunction(state, "strlen", strlenType);
        LlvmValueHandle length = LlvmApi.BuildCall2(builder, strlenType, strlen, [name], "name_length");
        LlvmTypeHandle mallocType = LlvmApi.FunctionType(state.I8Ptr, [state.I64]);
        LlvmValueHandle malloc = EmitOrDeclareExternalFunction(state, "malloc", mallocType);
        LlvmValueHandle copy = LlvmApi.BuildCall2(builder, mallocType, malloc, [LlvmApi.BuildAdd(builder, length, LlvmApi.ConstInt(state.I64, 1, 0), "copy_size")], "name_copy");
        LlvmApi.BuildCondBr(builder, IsNullPointer(state, copy, "name_copy_failed"), allocationFailed, copyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, copyBlock);
        LlvmTypeHandle memmoveType = LlvmApi.FunctionType(state.I8Ptr, [state.I8Ptr, state.I8Ptr, state.I64]);
        LlvmValueHandle memmove = EmitOrDeclareExternalFunction(state, "memmove", memmoveType);
        LlvmApi.BuildCall2(builder, memmoveType, memmove, [copy, name, LlvmApi.BuildAdd(builder, length, LlvmApi.ConstInt(state.I64, 1, 0), "copy_bytes")], "copy_name");
        LlvmValueHandle count = LlvmApi.BuildLoad2(builder, state.I64, countSlot, "name_count");
        LlvmValueHandle oldArray = LlvmApi.BuildLoad2(builder, state.I8Ptr, arraySlot, "name_array");
        LlvmTypeHandle reallocType = LlvmApi.FunctionType(state.I8Ptr, [state.I8Ptr, state.I64]);
        LlvmValueHandle realloc = EmitOrDeclareExternalFunction(state, "realloc", reallocType);
        LlvmValueHandle newArray = LlvmApi.BuildCall2(builder, reallocType, realloc, [oldArray, LlvmApi.BuildMul(builder, LlvmApi.BuildAdd(builder, count, LlvmApi.ConstInt(state.I64, 1, 0), "name_next_count"), LlvmApi.ConstInt(state.I64, 8, 0), "name_array_bytes")], "name_array_grown");
        LlvmApi.BuildCondBr(builder, IsNullPointer(state, newArray, "name_realloc_failed"), reallocFailed, success);

        EmitDirectoryNameAppendFailures(state, copy, reallocFailed, allocationFailed);

        EmitDirectoryNameAppendSuccess(state, arraySlot, countSlot, copy, newArray, count, success);
        LlvmApi.PositionBuilderAtEnd(builder, saved);
        return function;
    }

    private static void EmitDirectoryNameAppendFailures(
        LlvmCodegenState state,
        LlvmValueHandle copy,
        LlvmBasicBlockHandle reallocFailed,
        LlvmBasicBlockHandle allocationFailed)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, reallocFailed);
        EmitFreePointer(state, copy, "name_copy_free");
        LlvmApi.BuildRet(builder, LlvmApi.ConstInt(state.I32, unchecked((uint)-1), 1));
        LlvmApi.PositionBuilderAtEnd(builder, allocationFailed);
        LlvmApi.BuildRet(builder, LlvmApi.ConstInt(state.I32, unchecked((uint)-1), 1));
    }

    private static void EmitDirectoryNameAppendSuccess(
        LlvmCodegenState state,
        LlvmValueHandle arraySlot,
        LlvmValueHandle countSlot,
        LlvmValueHandle copy,
        LlvmValueHandle newArray,
        LlvmValueHandle count,
        LlvmBasicBlockHandle success)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, success);
        LlvmValueHandle nameSlot = LlvmApi.BuildGEP2(builder, state.I8Ptr, newArray, [count], "name_slot");
        LlvmApi.BuildStore(builder, copy, nameSlot);
        LlvmApi.BuildStore(builder, newArray, arraySlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, count, LlvmApi.ConstInt(state.I64, 1, 0), "stored_count"), countSlot);
        LlvmApi.BuildRet(builder, LlvmApi.ConstInt(state.I32, 0, 0));
    }

    private static LlvmValueHandle EmitDirectoryEntriesResult(
        LlvmCodegenState state,
        LlvmValueHandle arraySlot,
        LlvmValueHandle countSlot,
        LlvmValueHandle statusSlot,
        string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        var (array, count, status, resultSlot, listSlot, indexSlot) = CreateDirectoryEntriesResultState(state, arraySlot, countSlot, statusSlot, prefix);

        var (sortBlock, buildCheckBlock, buildBlock, okBlock, cleanupCheckBlock, cleanupBlock, errorBlock, doneBlock) = CreateDirectoryEntriesResultBlocks(state, prefix);
        LlvmValueHandle succeeded = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, status, LlvmApi.ConstInt(state.I32, 0, 0), prefix + "_succeeded");
        LlvmApi.BuildCondBr(builder, succeeded, sortBlock, cleanupCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, sortBlock);
        EmitDirectoryNameSort(state, array, count, prefix + "_qsort");
        LlvmApi.BuildBr(builder, buildCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, buildCheckBlock);
        LlvmValueHandle index = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, prefix + "_index");
        LlvmValueHandle hasNext = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, index, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_next");
        LlvmApi.BuildCondBr(builder, hasNext, buildBlock, okBlock);

        LlvmApi.PositionBuilderAtEnd(builder, buildBlock);
        LlvmValueHandle nextIndex = LlvmApi.BuildSub(builder, index, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_next_index");
        LlvmValueHandle name = LoadDirectoryNamePointer(state, array, nextIndex, prefix + "_name");
        LlvmValueHandle length = EmitCStringLength(state, name, prefix + "_name_length");
        LlvmValueHandle stringRef = EmitHeapStringSliceFromBytesPointer(state, name, length, prefix + "_string");
        LlvmValueHandle cons = EmitAlloc(state, HeapLayouts.List.FixedAllocationSizeBytes);
        StoreListHead(state, cons, stringRef, prefix + "_head");
        StoreListTail(state, cons, LlvmApi.BuildLoad2(builder, state.I64, listSlot, prefix + "_tail"), prefix + "_tail_store");
        LlvmApi.BuildStore(builder, cons, listSlot);
        EmitFreePointer(state, name, prefix + "_name_free");
        LlvmApi.BuildStore(builder, nextIndex, indexSlot);
        LlvmApi.BuildBr(builder, buildCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, okBlock);
        EmitFreePointer(state, array, prefix + "_array_free");
        LlvmApi.BuildStore(builder, EmitResultOk(state, LlvmApi.BuildLoad2(builder, state.I64, listSlot, prefix + "_list")), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, cleanupCheckBlock);
        LlvmValueHandle cleanupIndex = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, prefix + "_cleanup_index");
        LlvmValueHandle cleanupMore = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, cleanupIndex, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_cleanup_more");
        LlvmApi.BuildCondBr(builder, cleanupMore, cleanupBlock, errorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, cleanupBlock);
        LlvmValueHandle cleanupNext = LlvmApi.BuildSub(builder, cleanupIndex, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_cleanup_next");
        EmitFreePointer(state, LoadDirectoryNamePointer(state, array, cleanupNext, prefix + "_cleanup_name"), prefix + "_cleanup_name_free");
        LlvmApi.BuildStore(builder, cleanupNext, indexSlot);
        LlvmApi.BuildBr(builder, cleanupCheckBlock);

        EmitDirectoryEntriesError(state, array, status, resultSlot, errorBlock, doneBlock, prefix);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, prefix + "_result");
    }

    private static DirectoryEntriesResultState CreateDirectoryEntriesResultState(
        LlvmCodegenState state,
        LlvmValueHandle arraySlot,
        LlvmValueHandle countSlot,
        LlvmValueHandle statusSlot,
        string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle array = LlvmApi.BuildLoad2(builder, state.I8Ptr, arraySlot, prefix + "_array_value");
        LlvmValueHandle count = LlvmApi.BuildLoad2(builder, state.I64, countSlot, prefix + "_count_value");
        LlvmValueHandle status = LlvmApi.BuildLoad2(builder, state.I32, statusSlot, prefix + "_status_value");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_result_slot");
        LlvmValueHandle listSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_list_slot");
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_index_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), listSlot);
        LlvmApi.BuildStore(builder, count, indexSlot);
        return new(array, count, status, resultSlot, listSlot, indexSlot);
    }

    private readonly record struct DirectoryEntriesResultState(
        LlvmValueHandle Array,
        LlvmValueHandle Count,
        LlvmValueHandle Status,
        LlvmValueHandle ResultSlot,
        LlvmValueHandle ListSlot,
        LlvmValueHandle IndexSlot);

    private static DirectoryEntriesResultBlocks CreateDirectoryEntriesResultBlocks(LlvmCodegenState state, string prefix)
        => new(
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_sort"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_build_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_build"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_ok"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_cleanup_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_cleanup"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_error"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done"));

    private readonly record struct DirectoryEntriesResultBlocks(
        LlvmBasicBlockHandle Sort,
        LlvmBasicBlockHandle BuildCheck,
        LlvmBasicBlockHandle Build,
        LlvmBasicBlockHandle Ok,
        LlvmBasicBlockHandle CleanupCheck,
        LlvmBasicBlockHandle Cleanup,
        LlvmBasicBlockHandle Error,
        LlvmBasicBlockHandle Done);

    private static void EmitDirectoryEntriesError(
        LlvmCodegenState state,
        LlvmValueHandle array,
        LlvmValueHandle status,
        LlvmValueHandle resultSlot,
        LlvmBasicBlockHandle errorBlock,
        LlvmBasicBlockHandle doneBlock,
        string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, errorBlock);
        EmitFreePointer(state, array, prefix + "_cleanup_array_free");
        LlvmValueHandle invalidUtf8 = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, status, LlvmApi.ConstInt(state.I32, 2, 0), prefix + "_invalid_utf8");
        LlvmValueHandle errorText = LlvmApi.BuildSelect(builder, invalidUtf8, EmitHeapStringLiteral(state, DirectoryEntriesInvalidUtf8Message), EmitHeapStringLiteral(state, DirectoryEntriesFailedMessage), prefix + "_error_text");
        LlvmApi.BuildStore(builder, EmitResultError(state, errorText), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);
    }

    private static void EmitDirectoryNameSort(LlvmCodegenState state, LlvmValueHandle array, LlvmValueHandle count, string prefix)
    {
        LlvmTypeHandle qsortType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr, state.I64, state.I64, state.I8Ptr]);
        LlvmValueHandle qsort = EmitOrDeclareExternalFunction(state, "qsort", qsortType);
        LlvmApi.BuildCall2(state.Target.Builder, qsortType, qsort, [array, count, LlvmApi.ConstInt(state.I64, 8, 0), EmitOrGetDirectoryNameCompare(state)], string.Empty);
    }

    private static LlvmValueHandle EmitOrGetDirectoryNameCompare(LlvmCodegenState state)
    {
        const string symbol = "__ashes_directory_name_compare";
        LlvmValueHandle existing = LlvmApi.GetNamedFunction(state.Target.Module, symbol);
        if (existing.Ptr != 0)
        {
            return existing;
        }

        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmBasicBlockHandle saved = LlvmApi.GetInsertBlock(builder);
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle function = LlvmApi.AddFunction(state.Target.Module, symbol, functionType);
        LlvmBasicBlockHandle entry = LlvmApi.AppendBasicBlockInContext(state.Target.Context, function, "entry");
        LlvmApi.PositionBuilderAtEnd(builder, entry);
        LlvmValueHandle left = LlvmApi.BuildLoad2(builder, state.I8Ptr, LlvmApi.GetParam(function, 0), "left");
        LlvmValueHandle right = LlvmApi.BuildLoad2(builder, state.I8Ptr, LlvmApi.GetParam(function, 1), "right");
        LlvmTypeHandle strcmpType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle strcmp = EmitOrDeclareExternalFunction(state, "strcmp", strcmpType);
        LlvmApi.BuildRet(builder, LlvmApi.BuildCall2(builder, strcmpType, strcmp, [left, right], "comparison"));
        LlvmApi.PositionBuilderAtEnd(builder, saved);
        return function;
    }

    private static LlvmValueHandle LoadDirectoryNamePointer(LlvmCodegenState state, LlvmValueHandle array, LlvmValueHandle index, string prefix)
    {
        LlvmValueHandle slot = LlvmApi.BuildGEP2(state.Target.Builder, state.I8Ptr, array, [index], prefix + "_slot");
        return LlvmApi.BuildLoad2(state.Target.Builder, state.I8Ptr, slot, prefix + "_value");
    }

    private static LlvmValueHandle EmitCStringLength(LlvmCodegenState state, LlvmValueHandle pointer, string prefix)
    {
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I64, [state.I8Ptr]);
        LlvmValueHandle function = EmitOrDeclareExternalFunction(state, "strlen", functionType);
        return LlvmApi.BuildCall2(state.Target.Builder, functionType, function, [pointer], prefix);
    }

    private static void EmitFreePointer(LlvmCodegenState state, LlvmValueHandle pointer, string prefix)
    {
        LlvmTypeHandle functionType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr]);
        LlvmValueHandle function = EmitOrDeclareExternalFunction(state, "free", functionType);
        LlvmApi.BuildCall2(state.Target.Builder, functionType, function, [pointer], string.Empty);
    }

    private static LlvmValueHandle NullPointer(LlvmCodegenState state)
        => LlvmApi.BuildIntToPtr(state.Target.Builder, LlvmApi.ConstInt(state.I64, 0, 0), state.I8Ptr, "null_pointer");

    private static LlvmValueHandle IsNullPointer(LlvmCodegenState state, LlvmValueHandle pointer, string name)
        => LlvmApi.BuildICmp(state.Target.Builder, LlvmIntPredicate.Eq, LlvmApi.BuildPtrToInt(state.Target.Builder, pointer, state.I64, name + "_integer"), LlvmApi.ConstInt(state.I64, 0, 0), name);
}
