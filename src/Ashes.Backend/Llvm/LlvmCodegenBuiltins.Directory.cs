using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{
    private const string DirectoryEntriesFailedMessage = "Ashes.IO.Directory.entries: could not enumerate directory";
    private const string DirectoryEntriesInvalidUtf8Message = "Ashes.IO.Directory.entries: entry name is not valid UTF-8";
    private const string DirectoryCreateFailedMessage = "Ashes.IO.Directory.createAll: could not create directory";
    private const string DirectoryRemoveFailedMessage = "Ashes.IO.Directory.removeTree: could not remove tree";
    private const string FileReplaceFailedMessage = "Ashes.IO.File.replace: could not replace destination";

    private static LlvmValueHandle EmitFileReplace(
        LlvmCodegenState state,
        LlvmValueHandle sourceRef,
        LlvmValueHandle destinationRef)
    {
        return IsLinuxFlavor(state.Flavor)
            ? EmitLinuxFileReplace(state, sourceRef, destinationRef)
            : EmitWindowsFileReplace(state, sourceRef, destinationRef);
    }

    private static LlvmValueHandle EmitDirectoryEntries(LlvmCodegenState state, LlvmValueHandle pathRef)
    {
        return IsLinuxFlavor(state.Flavor)
            ? EmitLinuxDirectoryEntries(state, pathRef)
            : EmitWindowsDirectoryEntries(state, pathRef);
    }

    private static LlvmValueHandle EmitDirectoryCreateAll(LlvmCodegenState state, LlvmValueHandle pathRef)
    {
        return IsLinuxFlavor(state.Flavor)
            ? EmitLinuxDirectoryCreateAll(state, pathRef)
            : EmitWindowsDirectoryCreateAll(state, pathRef);
    }

    private static LlvmValueHandle EmitDirectoryRemoveTree(LlvmCodegenState state, LlvmValueHandle pathRef)
    {
        return IsLinuxFlavor(state.Flavor)
            ? EmitLinuxDirectoryRemoveTree(state, pathRef)
            : EmitWindowsDirectoryRemoveTree(state, pathRef);
    }

    private static LlvmValueHandle EmitFilesystemStatusResult(
        LlvmCodegenState state,
        LlvmValueHandle status,
        string errorMessage,
        string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_result");
        LlvmBasicBlockHandle okBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_ok");
        LlvmBasicBlockHandle errorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_error");
        LlvmBasicBlockHandle continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_continue");
        LlvmValueHandle succeeded = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, status, LlvmApi.ConstInt(state.I32, 0, 0), prefix + "_succeeded");
        LlvmApi.BuildCondBr(builder, succeeded, okBlock, errorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, okBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, EmitUnitValue(state)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, errorBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, errorMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, prefix + "_value");
    }

    private static LlvmValueHandle EmitLinuxFileReplace(
        LlvmCodegenState state,
        LlvmValueHandle sourceRef,
        LlvmValueHandle destinationRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle source = EmitStringToCString(state, sourceRef, "fs_replace_source");
        LlvmValueHandle destination = EmitStringToCString(state, destinationRef, "fs_replace_destination");
        LlvmTypeHandle openType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I32]);
        LlvmValueHandle directory = EmitLinuxImportedCall(state, "open", openType, [source, LlvmApi.ConstInt(state.I32, 0x230000, 0)], "fs_replace_source_directory_probe");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_replace_result_slot");
        LlvmBasicBlockHandle rejectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_replace_reject_directory");
        LlvmBasicBlockHandle renameBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_replace_rename");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_replace_done");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, directory, LlvmApi.ConstInt(state.I32, 0, 0), "fs_replace_source_is_directory"), rejectBlock, renameBlock);

        LlvmApi.PositionBuilderAtEnd(builder, rejectBlock);
        LlvmTypeHandle closeType = LlvmApi.FunctionType(state.I32, [state.I32]);
        EmitLinuxImportedCall(state, "close", closeType, [directory], "fs_replace_source_directory_close");
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, FileReplaceFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, renameBlock);
        LlvmValueHandle status = EmitLinuxImportedCall(state, "rename", functionType, [source, destination], "fs_replace_call");
        LlvmApi.BuildStore(builder, EmitFilesystemStatusResult(state, status, FileReplaceFailedMessage, "fs_replace"), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "fs_replace_result");
    }

    private static LlvmValueHandle EmitLinuxDirectoryCreateAll(LlvmCodegenState state, LlvmValueHandle pathRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle path = EmitStringToCString(state, pathRef, "dir_create_path");
        LlvmValueHandle length = LoadStringLength(state, pathRef, "dir_create_length");
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, "dir_create_index");
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I32, "dir_create_status");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), indexSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), statusSlot);

        var (checkBlock, byteBlock, componentBlock, advanceBlock, finalBlock, doneBlock) = CreateDirectoryCreateBlocks(state);
        LlvmValueHandle nonEmpty = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, length, LlvmApi.ConstInt(state.I64, 0, 0), "dir_create_nonempty");
        LlvmApi.BuildCondBr(builder, nonEmpty, checkBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkBlock);
        LlvmValueHandle index = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "dir_create_index_value");
        LlvmValueHandle inRange = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, index, length, "dir_create_in_range");
        LlvmApi.BuildCondBr(builder, inRange, byteBlock, finalBlock);

        LlvmApi.PositionBuilderAtEnd(builder, byteBlock);
        LlvmValueHandle bytePtr = LlvmApi.BuildGEP2(builder, state.I8, path, [index], "dir_create_byte_ptr");
        LlvmValueHandle current = LlvmApi.BuildLoad2(builder, state.I8, bytePtr, "dir_create_byte_value");
        LlvmValueHandle isSlash = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, current, LlvmApi.ConstInt(state.I8, (byte)'/', 0), "dir_create_is_slash");
        LlvmApi.BuildCondBr(builder, isSlash, componentBlock, advanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, componentBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I8, 0, 0), bytePtr);
        LlvmValueHandle componentStatus = EmitLinuxMkdirExistingOk(state, path, "dir_create_component_call");
        LlvmApi.BuildStore(builder, componentStatus, statusSlot);
        LlvmApi.BuildStore(builder, current, bytePtr);
        LlvmValueHandle componentFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, componentStatus, LlvmApi.ConstInt(state.I32, 0, 0), "dir_create_component_failed");
        LlvmApi.BuildCondBr(builder, componentFailed, doneBlock, advanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, advanceBlock);
        LlvmValueHandle next = LlvmApi.BuildAdd(builder, LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "dir_create_advance_index"), LlvmApi.ConstInt(state.I64, 1, 0), "dir_create_next");
        LlvmApi.BuildStore(builder, next, indexSlot);
        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finalBlock);
        LlvmApi.BuildStore(builder, EmitLinuxMkdirExistingOk(state, path, "dir_create_final_call"), statusSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        LlvmValueHandle wasEmpty = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, length, LlvmApi.ConstInt(state.I64, 0, 0), "dir_create_was_empty");
        LlvmValueHandle storedStatus = LlvmApi.BuildLoad2(builder, state.I32, statusSlot, "dir_create_stored_status");
        LlvmValueHandle status = LlvmApi.BuildSelect(builder, wasEmpty, LlvmApi.ConstInt(state.I32, unchecked((uint)-1), 1), storedStatus, "dir_create_status_value");
        return EmitFilesystemStatusResult(state, status, DirectoryCreateFailedMessage, "dir_create_result");
    }

    private static DirectoryCreateBlocks CreateDirectoryCreateBlocks(LlvmCodegenState state)
        => new(
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_create_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_create_byte"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_create_component"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_create_advance"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_create_final"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_create_done"));

    private readonly record struct DirectoryCreateBlocks(
        LlvmBasicBlockHandle Check,
        LlvmBasicBlockHandle Byte,
        LlvmBasicBlockHandle Component,
        LlvmBasicBlockHandle Advance,
        LlvmBasicBlockHandle Final,
        LlvmBasicBlockHandle Done);

    private static LlvmValueHandle EmitLinuxMkdirExistingOk(LlvmCodegenState state, LlvmValueHandle path, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle mkdirType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I32]);
        LlvmValueHandle mkdirStatus = EmitLinuxImportedCall(state, "mkdir", mkdirType, [path, LlvmApi.ConstInt(state.I32, 0x1ff, 0)], prefix + "_mkdir");
        LlvmTypeHandle openType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I32]);
        LlvmValueHandle openStatus = EmitLinuxImportedCall(state, "open", openType, [path, LlvmApi.ConstInt(state.I32, 0xB0000, 0)], prefix + "_open");
        LlvmValueHandle opened = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, openStatus, LlvmApi.ConstInt(state.I32, 0, 0), prefix + "_opened");
        LlvmBasicBlockHandle closeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_close");
        LlvmBasicBlockHandle continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_continue");
        LlvmApi.BuildCondBr(builder, opened, closeBlock, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeBlock);
        LlvmTypeHandle closeType = LlvmApi.FunctionType(state.I32, [state.I32]);
        EmitLinuxImportedCall(state, "close", closeType, [openStatus], prefix + "_close");
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        LlvmValueHandle closeTypeStatus = LlvmApi.BuildSelect(
            builder,
            opened,
            LlvmApi.ConstInt(state.I32, 0, 0),
            LlvmApi.ConstInt(state.I32, unchecked((uint)-1), 1),
            prefix + "_existing_status");
        LlvmValueHandle mkdirSucceeded = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, mkdirStatus, LlvmApi.ConstInt(state.I32, 0, 0), prefix + "_created");
        return LlvmApi.BuildSelect(builder, mkdirSucceeded, LlvmApi.ConstInt(state.I32, 0, 0), closeTypeStatus, prefix + "_status");
    }

    private static LlvmValueHandle EmitLinuxDirectoryRemoveTree(LlvmCodegenState state, LlvmValueHandle pathRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle path = EmitStringToCString(state, pathRef, "dir_remove_path");
        LlvmValueHandle callback = EmitOrGetLinuxRemoveTreeCallback(state);
        LlvmTypeHandle statBufferType = LlvmApi.ArrayType2(state.I8, 256);
        LlvmValueHandle statBuffer = LlvmApi.BuildAlloca(builder, statBufferType, "dir_remove_stat");
        LlvmTypeHandle lstatType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle lstatStatus = EmitLinuxImportedCall(state, "lstat", lstatType, [path, LlvmApi.BuildBitCast(builder, statBuffer, state.I8Ptr, "dir_remove_stat_ptr")], "dir_remove_lstat");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "dir_remove_result");
        LlvmBasicBlockHandle missingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_remove_missing");
        LlvmBasicBlockHandle walkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_remove_walk");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dir_remove_continue");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, lstatStatus, LlvmApi.ConstInt(state.I32, 0, 0), "dir_remove_is_missing"), missingBlock, walkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, missingBlock);
        LlvmValueHandle errno = LlvmApi.BuildLoad2(builder, state.I32, EmitLinuxErrnoPointer(state, "dir_remove_errno"), "dir_remove_errno_value");
        LlvmValueHandle isMissing = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, errno, LlvmApi.ConstInt(state.I32, 2, 0), "dir_remove_enoent");
        LlvmValueHandle missingStatus = LlvmApi.BuildSelect(builder, isMissing, LlvmApi.ConstInt(state.I32, 0, 0), LlvmApi.ConstInt(state.I32, unchecked((uint)-1), 1), "dir_remove_missing_status");
        LlvmApi.BuildStore(builder, EmitFilesystemStatusResult(state, missingStatus, DirectoryRemoveFailedMessage, "dir_remove_probe_result"), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, walkBlock);
        LlvmTypeHandle nftwType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, state.I32, state.I32]);
        LlvmValueHandle status = EmitLinuxImportedCall(state, "nftw", nftwType, [path, callback, LlvmApi.ConstInt(state.I32, 32, 0), LlvmApi.ConstInt(state.I32, 9, 0)], "dir_remove_nftw");
        LlvmValueHandle result = EmitFilesystemStatusResult(state, status, DirectoryRemoveFailedMessage, "dir_remove_walk_result");
        LlvmApi.BuildStore(builder, result, resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "dir_remove_result_value");
    }

    private static LlvmValueHandle EmitOrGetLinuxRemoveTreeCallback(LlvmCodegenState state)
    {
        const string symbol = "__ashes_remove_tree_visit";
        LlvmValueHandle existing = LlvmApi.GetNamedFunction(state.Target.Module, symbol);
        if (existing.Ptr != 0)
        {
            return existing;
        }

        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmBasicBlockHandle saved = LlvmApi.GetInsertBlock(builder);
        LlvmTypeHandle callbackType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, state.I32, state.I8Ptr]);
        LlvmValueHandle callback = LlvmApi.AddFunction(state.Target.Module, symbol, callbackType);
        LlvmBasicBlockHandle entry = LlvmApi.AppendBasicBlockInContext(state.Target.Context, callback, "entry");
        LlvmApi.PositionBuilderAtEnd(builder, entry);
        LlvmTypeHandle removeType = LlvmApi.FunctionType(state.I32, [state.I8Ptr]);
        LlvmValueHandle remove = EmitOrDeclareExternalFunction(state, "remove", removeType);
        LlvmValueHandle status = LlvmApi.BuildCall2(builder, removeType, remove, [LlvmApi.GetParam(callback, 0)], "remove_tree_entry");
        LlvmApi.BuildRet(builder, status);
        LlvmApi.PositionBuilderAtEnd(builder, saved);
        return callback;
    }

    private static LlvmValueHandle EmitLinuxDirectoryEntries(LlvmCodegenState state, LlvmValueHandle pathRef)
    {
        return EmitDirectoryEntriesWithPlatformLoop(state, pathRef, windows: false);
    }

    private static LlvmValueHandle EmitWindowsDirectoryEntries(LlvmCodegenState state, LlvmValueHandle pathRef)
    {
        return EmitDirectoryEntriesWithPlatformLoop(state, pathRef, windows: true);
    }

    private static LlvmValueHandle EmitDirectoryEntriesWithPlatformLoop(LlvmCodegenState state, LlvmValueHandle pathRef, bool windows)
    {
        // Platform traversal and deterministic collection are emitted below. Keeping the public
        // dispatch here ensures every filesystem primitive remains direct backend codegen.
        return windows
            ? EmitWindowsDirectoryEntriesCore(state, pathRef)
            : EmitLinuxDirectoryEntriesCore(state, pathRef);
    }

    private static LlvmValueHandle EmitWindowsFileReplace(LlvmCodegenState state, LlvmValueHandle sourceRef, LlvmValueHandle destinationRef)
        => EmitWindowsFileReplaceCore(state, sourceRef, destinationRef);

    private static LlvmValueHandle EmitWindowsDirectoryCreateAll(LlvmCodegenState state, LlvmValueHandle pathRef)
        => EmitWindowsDirectoryCreateAllCore(state, pathRef);

    private static LlvmValueHandle EmitWindowsDirectoryRemoveTree(LlvmCodegenState state, LlvmValueHandle pathRef)
        => EmitWindowsDirectoryRemoveTreeCore(state, pathRef);
}
