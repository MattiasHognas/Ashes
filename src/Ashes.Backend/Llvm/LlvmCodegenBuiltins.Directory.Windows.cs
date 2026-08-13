using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{
    private const ulong WindowsMaximumPathChars = 32768;

    private static LlvmValueHandle EmitWindowsFileReplaceCore(
        LlvmCodegenState state,
        LlvmValueHandle sourceRef,
        LlvmValueHandle destinationRef)
    {
        (LlvmValueHandle source, LlvmValueHandle sourceCount) = EmitWindowsWidePath(state, sourceRef, "fs_replace_source");
        (LlvmValueHandle destination, LlvmValueHandle destinationCount) = EmitWindowsWidePath(state, destinationRef, "fs_replace_destination");
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I32, "fs_replace_status");
        LlvmBasicBlockHandle moveBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_replace_move");
        LlvmBasicBlockHandle errorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_replace_conversion_error");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_replace_done");
        LlvmValueHandle sourceValid = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, sourceCount, LlvmApi.ConstInt(state.I32, 0, 0), "fs_replace_source_valid");
        LlvmValueHandle destinationValid = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, destinationCount, LlvmApi.ConstInt(state.I32, 0, 0), "fs_replace_destination_valid");
        LlvmValueHandle sourceSupported = EmitWindowsReplaceSourceSupported(state, source);
        LlvmValueHandle pathsValid = LlvmApi.BuildAnd(builder, sourceValid, destinationValid, "fs_replace_paths_valid");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildAnd(builder, pathsValid, sourceSupported, "fs_replace_valid"), moveBlock, errorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, moveBlock);
        LlvmTypeHandle moveType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, state.I32]);
        LlvmValueHandle move = EmitOrDeclareExternalFunction(state, "MoveFileExW", moveType);
        LlvmValueHandle moved = LlvmApi.BuildCall2(builder, moveType, move, [source, destination, LlvmApi.ConstInt(state.I32, 1, 0)], "fs_replace_move_call");
        LlvmValueHandle succeeded = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, moved, LlvmApi.ConstInt(state.I32, 0, 0), "fs_replace_moved");
        LlvmApi.BuildStore(builder, LlvmApi.BuildSelect(builder, succeeded, LlvmApi.ConstInt(state.I32, 0, 0), LlvmApi.ConstInt(state.I32, unchecked((uint)-1), 1), "fs_replace_move_status"), statusSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, errorBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, unchecked((uint)-1), 1), statusSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return EmitFilesystemStatusResult(state, LlvmApi.BuildLoad2(builder, state.I32, statusSlot, "fs_replace_final_status"), FileReplaceFailedMessage, "fs_replace");
    }

    private static LlvmValueHandle EmitWindowsReplaceSourceSupported(LlvmCodegenState state, LlvmValueHandle source)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle attrsType = LlvmApi.FunctionType(state.I32, [state.I8Ptr]);
        LlvmValueHandle attrsFunction = EmitOrDeclareExternalFunction(state, "GetFileAttributesW", attrsType);
        LlvmValueHandle attrs = LlvmApi.BuildCall2(builder, attrsType, attrsFunction, [source], "fs_replace_source_attrs");
        LlvmValueHandle isDirectory = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildAnd(builder, attrs, LlvmApi.ConstInt(state.I32, 0x10, 0), "fs_replace_source_dir_bits"), LlvmApi.ConstInt(state.I32, 0, 0), "fs_replace_source_is_dir");
        LlvmValueHandle isReparse = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildAnd(builder, attrs, LlvmApi.ConstInt(state.I32, 0x400, 0), "fs_replace_source_reparse_bits"), LlvmApi.ConstInt(state.I32, 0, 0), "fs_replace_source_is_reparse");
        LlvmValueHandle notReparse = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, isReparse, LlvmApi.ConstNull(LlvmApi.TypeOf(isReparse)), "fs_replace_source_not_reparse");
        LlvmValueHandle ordinaryDirectory = LlvmApi.BuildAnd(builder, isDirectory, notReparse, "fs_replace_source_ordinary_dir");
        return LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, ordinaryDirectory, LlvmApi.ConstNull(LlvmApi.TypeOf(ordinaryDirectory)), "fs_replace_source_supported");
    }

    private static LlvmValueHandle EmitWindowsDirectoryCreateAllCore(LlvmCodegenState state, LlvmValueHandle pathRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        (LlvmValueHandle path, LlvmValueHandle count) = EmitWindowsWidePath(state, pathRef, "dir_create_path");
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, "dir_create_index");
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I32, "dir_create_status");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), indexSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), statusSlot);
        var (checkBlock, byteBlock, componentBlock, advanceBlock, finalBlock, doneBlock) = CreateDirectoryCreateBlocks(state);
        LlvmValueHandle valid = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, count, LlvmApi.ConstInt(state.I32, 1, 0), "dir_create_path_valid");
        LlvmApi.BuildCondBr(builder, valid, checkBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkBlock);
        LlvmValueHandle index = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "dir_create_index_value");
        LlvmValueHandle length = LlvmApi.BuildSub(builder, LlvmApi.BuildZExt(builder, count, state.I64, "dir_create_count_i64"), LlvmApi.ConstInt(state.I64, 1, 0), "dir_create_length");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, index, length, "dir_create_in_range"), byteBlock, finalBlock);

        LlvmApi.PositionBuilderAtEnd(builder, byteBlock);
        LlvmTypeHandle i16 = LlvmApi.Int16TypeInContext(state.Target.Context);
        LlvmValueHandle characterPtr = LlvmApi.BuildGEP2(builder, i16, path, [index], "dir_create_character_ptr");
        LlvmValueHandle character = LlvmApi.BuildLoad2(builder, i16, characterPtr, "dir_create_character");
        LlvmValueHandle slash = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, character, LlvmApi.ConstInt(i16, (byte)'/', 0), "dir_create_slash");
        LlvmValueHandle backslash = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, character, LlvmApi.ConstInt(i16, (byte)'\\', 0), "dir_create_backslash");
        LlvmValueHandle isSeparator = LlvmApi.BuildOr(builder, slash, backslash, "dir_create_separator");
        LlvmValueHandle previous = LlvmApi.BuildLoad2(builder, i16, LlvmApi.BuildGEP2(builder, i16, path, [LlvmApi.BuildSub(builder, index, LlvmApi.ConstInt(state.I64, 1, 0), "dir_create_previous_index")], "dir_create_previous_ptr"), "dir_create_previous");
        LlvmValueHandle afterDrive = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, previous, LlvmApi.ConstInt(i16, (byte)':', 0), "dir_create_after_drive");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildAnd(builder, isSeparator, afterDrive, "dir_create_component_separator"), componentBlock, advanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, componentBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(i16, 0, 0), characterPtr);
        LlvmValueHandle componentStatus = EmitWindowsCreateDirectoryExistingOk(state, path, "dir_create_component_call");
        LlvmApi.BuildStore(builder, character, characterPtr);
        LlvmApi.BuildStore(builder, componentStatus, statusSlot);
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, componentStatus, LlvmApi.ConstInt(state.I32, 0, 0), "dir_create_component_failed"), doneBlock, advanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, advanceBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "dir_create_advance_index"), LlvmApi.ConstInt(state.I64, 1, 0), "dir_create_next"), indexSlot);
        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finalBlock);
        LlvmApi.BuildStore(builder, EmitWindowsCreateDirectoryExistingOk(state, path, "dir_create_final_call"), statusSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        LlvmValueHandle stored = LlvmApi.BuildLoad2(builder, state.I32, statusSlot, "dir_create_stored_status");
        LlvmValueHandle status = LlvmApi.BuildSelect(builder, valid, stored, LlvmApi.ConstInt(state.I32, unchecked((uint)-1), 1), "dir_create_status_value");
        return EmitFilesystemStatusResult(state, status, DirectoryCreateFailedMessage, "dir_create_result");
    }

    private static LlvmValueHandle EmitWindowsCreateDirectoryExistingOk(LlvmCodegenState state, LlvmValueHandle path, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle createType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle create = EmitOrDeclareExternalFunction(state, "CreateDirectoryW", createType);
        LlvmValueHandle created = LlvmApi.BuildCall2(builder, createType, create, [path, NullPointer(state)], prefix + "_create");
        LlvmTypeHandle attrsType = LlvmApi.FunctionType(state.I32, [state.I8Ptr]);
        LlvmValueHandle attrsFunction = EmitOrDeclareExternalFunction(state, "GetFileAttributesW", attrsType);
        LlvmValueHandle attrs = LlvmApi.BuildCall2(builder, attrsType, attrsFunction, [path], prefix + "_attrs");
        LlvmValueHandle isDirectory = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildAnd(builder, attrs, LlvmApi.ConstInt(state.I32, 0x10, 0), prefix + "_dir_bits"), LlvmApi.ConstInt(state.I32, 0, 0), prefix + "_is_dir");
        LlvmValueHandle isReparse = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildAnd(builder, attrs, LlvmApi.ConstInt(state.I32, 0x400, 0), prefix + "_reparse_bits"), LlvmApi.ConstInt(state.I32, 0, 0), prefix + "_is_reparse");
        LlvmValueHandle existingOk = LlvmApi.BuildAnd(builder, isDirectory, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, isReparse, LlvmApi.ConstNull(LlvmApi.TypeOf(isReparse)), prefix + "_not_reparse"), prefix + "_existing_ok");
        LlvmValueHandle succeeded = LlvmApi.BuildOr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, created, LlvmApi.ConstInt(state.I32, 0, 0), prefix + "_created"), existingOk, prefix + "_succeeded");
        return LlvmApi.BuildSelect(builder, succeeded, LlvmApi.ConstInt(state.I32, 0, 0), LlvmApi.ConstInt(state.I32, unchecked((uint)-1), 1), prefix + "_status");
    }

    private static LlvmValueHandle EmitWindowsDirectoryRemoveTreeCore(LlvmCodegenState state, LlvmValueHandle pathRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        (LlvmValueHandle path, LlvmValueHandle count) = EmitWindowsWidePath(state, pathRef, "dir_remove_path");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "dir_remove_result");
        LlvmBasicBlockHandle probeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_remove_probe");
        LlvmBasicBlockHandle okBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_remove_missing_ok");
        LlvmBasicBlockHandle removeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_remove_operation");
        LlvmBasicBlockHandle errorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_remove_invalid_path");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_remove_done");
        LlvmValueHandle valid = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, count, LlvmApi.ConstInt(state.I32, 1, 0), "dir_remove_valid");
        LlvmApi.BuildCondBr(builder, valid, probeBlock, errorBlock);
        LlvmApi.PositionBuilderAtEnd(builder, probeBlock);
        EmitWindowsRemoveTreeProbe(state, path, okBlock, removeBlock, errorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, errorBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, DirectoryRemoveFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, okBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, EmitUnitValue(state)), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, removeBlock);
        EmitWindowsRemoveTreeOperation(state, path, count, resultSlot, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "dir_remove_result_value");
    }

    private static void EmitWindowsRemoveTreeOperation(
        LlvmCodegenState state,
        LlvmValueHandle path,
        LlvmValueHandle count,
        LlvmValueHandle resultSlot,
        LlvmBasicBlockHandle doneBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle i16 = LlvmApi.Int16TypeInContext(state.Target.Context);
        LlvmValueHandle secondTerminatorIndex = LlvmApi.BuildZExt(builder, count, state.I64, "dir_remove_second_nul_index");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(i16, 0, 0), LlvmApi.BuildGEP2(builder, i16, path, [secondTerminatorIndex], "dir_remove_second_nul"));
        LlvmTypeHandle operationType = LlvmApi.ArrayType2(state.I8, 64);
        LlvmValueHandle operation = LlvmApi.BuildAlloca(builder, operationType, "dir_remove_operation_data");
        for (ulong offset = 0; offset < 64; offset += 8)
        {
            LlvmValueHandle word = LlvmApi.BuildBitCast(builder, LlvmApi.BuildGEP2(builder, state.I8, operation, [LlvmApi.ConstInt(state.I64, offset, 0)], "dir_remove_zero_offset"), state.I64Ptr, "dir_remove_zero_word");
            LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), word);
        }
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 3, 0), LlvmApi.BuildBitCast(builder, LlvmApi.BuildGEP2(builder, state.I8, operation, [LlvmApi.ConstInt(state.I64, 8, 0)], "dir_remove_func_offset"), state.I32Ptr, "dir_remove_func_ptr"));
        LlvmApi.BuildStore(builder, path, LlvmApi.BuildBitCast(builder, LlvmApi.BuildGEP2(builder, state.I8, operation, [LlvmApi.ConstInt(state.I64, 16, 0)], "dir_remove_from_offset"), LlvmApi.PointerTypeInContext(state.Target.Context, 0), "dir_remove_from_ptr"));
        LlvmTypeHandle i16Ptr = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(i16, 0x414, 0), LlvmApi.BuildBitCast(builder, LlvmApi.BuildGEP2(builder, state.I8, operation, [LlvmApi.ConstInt(state.I64, 32, 0)], "dir_remove_flags_offset"), i16Ptr, "dir_remove_flags_ptr"));
        LlvmTypeHandle shellType = LlvmApi.FunctionType(state.I32, [state.I8Ptr]);
        LlvmValueHandle shell = EmitOrDeclareExternalFunction(state, "SHFileOperationW", shellType);
        LlvmValueHandle status = LlvmApi.BuildCall2(builder, shellType, shell, [LlvmApi.BuildBitCast(builder, operation, state.I8Ptr, "dir_remove_operation_ptr")], "dir_remove_call");
        LlvmValueHandle aborted = LlvmApi.BuildLoad2(builder, state.I32, LlvmApi.BuildBitCast(builder, LlvmApi.BuildGEP2(builder, state.I8, operation, [LlvmApi.ConstInt(state.I64, 36, 0)], "dir_remove_aborted_offset"), state.I32Ptr, "dir_remove_aborted_ptr"), "dir_remove_aborted");
        LlvmValueHandle completed = LlvmApi.BuildAnd(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, status, LlvmApi.ConstInt(state.I32, 0, 0), "dir_remove_status_ok"), LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, aborted, LlvmApi.ConstInt(state.I32, 0, 0), "dir_remove_not_aborted"), "dir_remove_completed");
        LlvmValueHandle normalized = LlvmApi.BuildSelect(builder, completed, LlvmApi.ConstInt(state.I32, 0, 0), LlvmApi.ConstInt(state.I32, unchecked((uint)-1), 1), "dir_remove_normalized");
        LlvmApi.BuildStore(builder, EmitFilesystemStatusResult(state, normalized, DirectoryRemoveFailedMessage, "dir_remove_operation_result"), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);
    }

    private static void EmitWindowsRemoveTreeProbe(
        LlvmCodegenState state,
        LlvmValueHandle path,
        LlvmBasicBlockHandle okBlock,
        LlvmBasicBlockHandle removeBlock,
        LlvmBasicBlockHandle errorBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle attrsType = LlvmApi.FunctionType(state.I32, [state.I8Ptr]);
        LlvmValueHandle attrsFunction = EmitOrDeclareExternalFunction(state, "GetFileAttributesW", attrsType);
        LlvmValueHandle attrs = LlvmApi.BuildCall2(builder, attrsType, attrsFunction, [path], "dir_remove_attrs");
        LlvmValueHandle missing = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, attrs, LlvmApi.ConstInt(state.I32, 0xffffffff, 0), "dir_remove_missing");
        LlvmBasicBlockHandle classifyErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_remove_classify_error");
        LlvmApi.BuildCondBr(builder, missing, classifyErrorBlock, removeBlock);

        LlvmApi.PositionBuilderAtEnd(builder, classifyErrorBlock);
        LlvmTypeHandle getLastErrorType = LlvmApi.FunctionType(state.I32, []);
        LlvmValueHandle getLastError = EmitOrDeclareExternalFunction(state, "GetLastError", getLastErrorType);
        LlvmValueHandle error = LlvmApi.BuildCall2(builder, getLastErrorType, getLastError, [], "dir_remove_error");
        LlvmValueHandle fileNotFound = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, error, LlvmApi.ConstInt(state.I32, 2, 0), "dir_remove_file_not_found");
        LlvmValueHandle pathNotFound = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, error, LlvmApi.ConstInt(state.I32, 3, 0), "dir_remove_path_not_found");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildOr(builder, fileNotFound, pathNotFound, "dir_remove_not_found"), okBlock, errorBlock);
    }

    private static LlvmValueHandle EmitWindowsDirectoryEntriesCore(LlvmCodegenState state, LlvmValueHandle pathRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        (LlvmValueHandle path, LlvmValueHandle pathCount) = EmitWindowsWidePath(state, pathRef, "dir_entries_path");
        LlvmValueHandle arraySlot = LlvmApi.BuildAlloca(builder, state.I8Ptr, "dir_entries_array");
        LlvmValueHandle countSlot = LlvmApi.BuildAlloca(builder, state.I64, "dir_entries_count");
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I32, "dir_entries_status");
        LlvmApi.BuildStore(builder, NullPointer(state), arraySlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), countSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 1, 0), statusSlot);
        LlvmTypeHandle findDataType = LlvmApi.ArrayType2(state.I8, 592);
        LlvmValueHandle findData = LlvmApi.BuildAlloca(builder, findDataType, "dir_entries_find_data");
        LlvmTypeHandle utf8Type = LlvmApi.ArrayType2(state.I8, 1040);
        LlvmValueHandle utf8 = LlvmApi.BuildAlloca(builder, utf8Type, "dir_entries_utf8");
        LlvmBasicBlockHandle prepareBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_entries_prepare");
        LlvmBasicBlockHandle entryBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_entries_entry");
        LlvmBasicBlockHandle appendBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_entries_append");
        LlvmBasicBlockHandle nextBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_entries_next");
        LlvmBasicBlockHandle closeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_entries_close");
        LlvmBasicBlockHandle finishBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_entries_finish");
        LlvmValueHandle validPath = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, pathCount, LlvmApi.ConstInt(state.I32, 1, 0), "dir_entries_valid_path");
        LlvmApi.BuildCondBr(builder, validPath, prepareBlock, finishBlock);

        LlvmValueHandle handle = EmitWindowsDirectoryEntriesOpen(state, path, pathCount, findData, prepareBlock, entryBlock, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, entryBlock);
        LlvmTypeHandle i16 = LlvmApi.Int16TypeInContext(state.Target.Context);
        LlvmValueHandle wideName = LlvmApi.BuildGEP2(builder, state.I8, findData, [LlvmApi.ConstInt(state.I64, 44, 0)], "dir_entries_wide_name");
        LlvmValueHandle isDot = EmitDirectoryEntryIsDot(state, wideName, i16, "dir_entries");
        LlvmBasicBlockHandle convertBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_entries_convert");
        LlvmApi.BuildCondBr(builder, isDot, nextBlock, convertBlock);

        EmitWindowsDirectoryEntryAppend(state, wideName, utf8, arraySlot, countSlot, statusSlot, convertBlock, appendBlock, nextBlock, closeBlock);

        EmitWindowsDirectoryEntriesNext(state, handle, findData, statusSlot, nextBlock, entryBlock, closeBlock);

        EmitWindowsDirectoryEntriesClose(state, handle, statusSlot, closeBlock, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        return EmitDirectoryEntriesResult(state, arraySlot, countSlot, statusSlot, "dir_entries");
    }

    private static void EmitWindowsDirectoryEntriesNext(
        LlvmCodegenState state,
        LlvmValueHandle handle,
        LlvmValueHandle findData,
        LlvmValueHandle statusSlot,
        LlvmBasicBlockHandle nextBlock,
        LlvmBasicBlockHandle entryBlock,
        LlvmBasicBlockHandle closeBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, nextBlock);
        LlvmTypeHandle findNextType = LlvmApi.FunctionType(state.I32, [state.I64, state.I8Ptr]);
        LlvmValueHandle findNext = EmitOrDeclareExternalFunction(state, "FindNextFileW", findNextType);
        LlvmValueHandle hasNext = LlvmApi.BuildCall2(builder, findNextType, findNext, [handle, LlvmApi.BuildBitCast(builder, findData, state.I8Ptr, "dir_entries_next_data")], "dir_entries_find_next");
        LlvmBasicBlockHandle endBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_entries_end");
        LlvmValueHandle completed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, hasNext, LlvmApi.ConstInt(state.I32, 0, 0), "dir_entries_complete");
        LlvmApi.BuildCondBr(builder, completed, endBlock, entryBlock);

        LlvmApi.PositionBuilderAtEnd(builder, endBlock);
        LlvmTypeHandle getLastErrorType = LlvmApi.FunctionType(state.I32, []);
        LlvmValueHandle getLastError = EmitOrDeclareExternalFunction(state, "GetLastError", getLastErrorType);
        LlvmValueHandle lastError = LlvmApi.BuildCall2(builder, getLastErrorType, getLastError, Array.Empty<LlvmValueHandle>(), "dir_entries_last_error");
        LlvmValueHandle cleanEnd = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, lastError, LlvmApi.ConstInt(state.I32, 18, 0), "dir_entries_clean_end");
        LlvmApi.BuildStore(builder, LlvmApi.BuildSelect(builder, cleanEnd, LlvmApi.ConstInt(state.I32, 0, 0), LlvmApi.ConstInt(state.I32, 1, 0), "dir_entries_end_status"), statusSlot);
        LlvmApi.BuildBr(builder, closeBlock);
    }

    private static void EmitWindowsDirectoryEntryAppend(
        LlvmCodegenState state,
        LlvmValueHandle wideName,
        LlvmValueHandle utf8,
        LlvmValueHandle arraySlot,
        LlvmValueHandle countSlot,
        LlvmValueHandle statusSlot,
        LlvmBasicBlockHandle convertBlock,
        LlvmBasicBlockHandle appendBlock,
        LlvmBasicBlockHandle nextBlock,
        LlvmBasicBlockHandle closeBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, convertBlock);
        LlvmTypeHandle convertType = LlvmApi.FunctionType(state.I32, [state.I32, state.I32, state.I8Ptr, state.I32, state.I8Ptr, state.I32, state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle convert = EmitOrDeclareExternalFunction(state, "WideCharToMultiByte", convertType);
        LlvmValueHandle utf8Pointer = LlvmApi.BuildBitCast(builder, utf8, state.I8Ptr, "dir_entries_utf8_ptr");
        LlvmValueHandle utf8Length = LlvmApi.BuildCall2(builder, convertType, convert, [LlvmApi.ConstInt(state.I32, 65001, 0), LlvmApi.ConstInt(state.I32, 0x80, 0), wideName, LlvmApi.ConstInt(state.I32, unchecked((ulong)(-1L)), 1), utf8Pointer, LlvmApi.ConstInt(state.I32, 1040, 0), NullPointer(state), NullPointer(state)], "dir_entries_convert_name");
        LlvmValueHandle converted = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, utf8Length, LlvmApi.ConstInt(state.I32, 0, 0), "dir_entries_converted");
        LlvmBasicBlockHandle invalidBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_entries_invalid_utf8");
        LlvmApi.BuildCondBr(builder, converted, appendBlock, invalidBlock);

        LlvmApi.PositionBuilderAtEnd(builder, invalidBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 2, 0), statusSlot);
        LlvmApi.BuildBr(builder, closeBlock);

        LlvmApi.PositionBuilderAtEnd(builder, appendBlock);
        LlvmValueHandle appendStatus = EmitDirectoryNameAppend(state, arraySlot, countSlot, utf8Pointer, "dir_entries_append_call");
        LlvmValueHandle appendFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, appendStatus, LlvmApi.ConstInt(state.I32, 0, 0), "dir_entries_append_failed");
        LlvmApi.BuildCondBr(builder, appendFailed, closeBlock, nextBlock);
    }

    private static LlvmValueHandle EmitWindowsDirectoryEntriesOpen(
        LlvmCodegenState state,
        LlvmValueHandle path,
        LlvmValueHandle pathCount,
        LlvmValueHandle findData,
        LlvmBasicBlockHandle prepareBlock,
        LlvmBasicBlockHandle entryBlock,
        LlvmBasicBlockHandle finishBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, prepareBlock);
        LlvmTypeHandle attrsType = LlvmApi.FunctionType(state.I32, [state.I8Ptr]);
        LlvmValueHandle attrsFunction = EmitOrDeclareExternalFunction(state, "GetFileAttributesW", attrsType);
        LlvmValueHandle attrs = LlvmApi.BuildCall2(builder, attrsType, attrsFunction, [path], "dir_entries_attrs");
        LlvmValueHandle dirBits = LlvmApi.BuildAnd(builder, attrs, LlvmApi.ConstInt(state.I32, 0x10, 0), "dir_entries_dir_bits");
        LlvmValueHandle reparseBits = LlvmApi.BuildAnd(builder, attrs, LlvmApi.ConstInt(state.I32, 0x400, 0), "dir_entries_reparse_bits");
        LlvmValueHandle isDirectory = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, dirBits, LlvmApi.ConstInt(state.I32, 0, 0), "dir_entries_is_directory");
        LlvmValueHandle isNotReparse = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, reparseBits, LlvmApi.ConstInt(state.I32, 0, 0), "dir_entries_not_reparse");
        LlvmBasicBlockHandle findBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_entries_find");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildAnd(builder, isDirectory, isNotReparse, "dir_entries_root_ok"), findBlock, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, findBlock);
        LlvmTypeHandle i16 = LlvmApi.Int16TypeInContext(state.Target.Context);
        LlvmValueHandle terminatorIndex = LlvmApi.BuildSub(builder, LlvmApi.BuildZExt(builder, pathCount, state.I64, "dir_entries_path_count_i64"), LlvmApi.ConstInt(state.I64, 1, 0), "dir_entries_terminator_index");
        LlvmValueHandle lastIndex = LlvmApi.BuildSub(builder, terminatorIndex, LlvmApi.ConstInt(state.I64, 1, 0), "dir_entries_last_index");
        LlvmValueHandle last = LlvmApi.BuildLoad2(builder, i16, LlvmApi.BuildGEP2(builder, i16, path, [lastIndex], "dir_entries_last_ptr"), "dir_entries_last");
        LlvmValueHandle isSlash = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, last, LlvmApi.ConstInt(i16, (byte)'/', 0), "dir_entries_last_slash");
        LlvmValueHandle isBackslash = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, last, LlvmApi.ConstInt(i16, (byte)'\\', 0), "dir_entries_last_backslash");
        LlvmValueHandle hasSeparator = LlvmApi.BuildOr(builder, isSlash, isBackslash, "dir_entries_has_separator");
        LlvmValueHandle needsSeparator = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, hasSeparator, LlvmApi.ConstNull(LlvmApi.TypeOf(hasSeparator)), "dir_entries_needs_separator");
        LlvmValueHandle wildcardIndex = LlvmApi.BuildAdd(builder, terminatorIndex, LlvmApi.BuildZExt(builder, needsSeparator, state.I64, "dir_entries_separator_count"), "dir_entries_wildcard_index");
        LlvmValueHandle separator = LlvmApi.BuildSelect(builder, needsSeparator, LlvmApi.ConstInt(i16, (byte)'\\', 0), LlvmApi.ConstInt(i16, (byte)'*', 0), "dir_entries_separator_value");
        LlvmApi.BuildStore(builder, separator, LlvmApi.BuildGEP2(builder, i16, path, [terminatorIndex], "dir_entries_separator_ptr"));
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(i16, (byte)'*', 0), LlvmApi.BuildGEP2(builder, i16, path, [wildcardIndex], "dir_entries_wildcard_ptr"));
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(i16, 0, 0), LlvmApi.BuildGEP2(builder, i16, path, [LlvmApi.BuildAdd(builder, wildcardIndex, LlvmApi.ConstInt(state.I64, 1, 0), "dir_entries_pattern_end")], "dir_entries_pattern_nul"));
        LlvmTypeHandle findFirstType = LlvmApi.FunctionType(state.I64, [state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle findFirst = EmitOrDeclareExternalFunction(state, "FindFirstFileW", findFirstType);
        LlvmValueHandle handle = LlvmApi.BuildCall2(builder, findFirstType, findFirst, [path, LlvmApi.BuildBitCast(builder, findData, state.I8Ptr, "dir_entries_find_data_ptr")], "dir_entries_find_first");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, handle, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), "dir_entries_find_failed"), finishBlock, entryBlock);
        return handle;
    }

    private static void EmitWindowsDirectoryEntriesClose(
        LlvmCodegenState state,
        LlvmValueHandle handle,
        LlvmValueHandle statusSlot,
        LlvmBasicBlockHandle closeBlock,
        LlvmBasicBlockHandle finishBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, closeBlock);
        LlvmTypeHandle closeType = LlvmApi.FunctionType(state.I32, [state.I64]);
        LlvmValueHandle close = EmitOrDeclareExternalFunction(state, "FindClose", closeType);
        LlvmValueHandle closeStatus = LlvmApi.BuildCall2(builder, closeType, close, [handle], "dir_entries_find_close");
        LlvmValueHandle priorStatus = LlvmApi.BuildLoad2(builder, state.I32, statusSlot, "dir_entries_prior_status");
        LlvmValueHandle closeOk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, closeStatus, LlvmApi.ConstInt(state.I32, 0, 0), "dir_entries_close_ok");
        LlvmApi.BuildStore(builder, LlvmApi.BuildSelect(builder, closeOk, priorStatus, LlvmApi.ConstInt(state.I32, 1, 0), "dir_entries_final_status"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static (LlvmValueHandle Pointer, LlvmValueHandle Count) EmitWindowsWidePath(
        LlvmCodegenState state,
        LlvmValueHandle pathRef,
        string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle i16 = LlvmApi.Int16TypeInContext(state.Target.Context);
        LlvmTypeHandle bufferType = LlvmApi.ArrayType2(i16, WindowsMaximumPathChars + 1);
        LlvmValueHandle buffer = LlvmApi.BuildAlloca(builder, bufferType, prefix + "_wide_buffer");
        LlvmValueHandle cstr = EmitStringToCString(state, pathRef, prefix + "_utf8");
        LlvmTypeHandle convertType = LlvmApi.FunctionType(state.I32, [state.I32, state.I32, state.I8Ptr, state.I32, state.I8Ptr, state.I32]);
        LlvmValueHandle convert = EmitOrDeclareExternalFunction(state, "MultiByteToWideChar", convertType);
        LlvmValueHandle pointer = LlvmApi.BuildBitCast(builder, buffer, state.I8Ptr, prefix + "_wide_ptr");
        LlvmValueHandle count = LlvmApi.BuildCall2(builder, convertType, convert, [LlvmApi.ConstInt(state.I32, 65001, 0), LlvmApi.ConstInt(state.I32, 8, 0), cstr, LlvmApi.ConstInt(state.I32, unchecked((ulong)(-1L)), 1), pointer, LlvmApi.ConstInt(state.I32, WindowsMaximumPathChars, 0)], prefix + "_convert");
        return (pointer, count);
    }
}
