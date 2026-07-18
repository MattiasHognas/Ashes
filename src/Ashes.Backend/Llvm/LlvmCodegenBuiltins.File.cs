using Ashes.Semantics;
using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{

    // rawBytes: read the file as raw Bytes — no UTF-8 validation, and (on Linux) no size cap. Used by
    // Ashes.File.readAllBytes. readText passes rawBytes: false (UTF-8-validated, capped).
    private static LlvmValueHandle EmitFileReadText(LlvmCodegenState state, LlvmValueHandle pathRef, bool rawBytes = false)
    {
        return IsLinuxFlavor(state.Flavor)
            ? EmitLinuxFileReadText(state, pathRef, rawBytes)
            : EmitWindowsFileReadText(state, pathRef, rawBytes);
    }

    // Ashes.File.mmap(path) : Result(Str, Bytes) — map the whole file read-only and return a zero-copy
    // Bytes view over the mapping (no read/copy). On Windows this falls back to the capped readAllBytes
    // read (no file mapping wired there yet).
    private static LlvmValueHandle EmitFileMmap(LlvmCodegenState state, LlvmValueHandle pathRef)
    {
        return IsLinuxFlavor(state.Flavor)
            ? EmitLinuxFileMmap(state, pathRef)
            : EmitWindowsFileReadText(state, pathRef, rawBytes: true);
    }

    private static LlvmValueHandle EmitLinuxFileMmap(LlvmCodegenState state, LlvmValueHandle pathRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle pathCstr = EmitStringToCString(state, pathRef, "fs_mmap_path");
        LlvmValueHandle fdSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_mmap_fd");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_mmap_result");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), fdSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

        var openBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_mmap_open");
        var seekBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_mmap_seek");
        var emptyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_mmap_empty");
        var emptyOkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_mmap_empty_ok");
        var mapBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_mmap_map");
        var okBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_mmap_ok");
        var closeErrBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_mmap_close_err");
        var errBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_mmap_err");
        var contBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_mmap_cont");

        LlvmApi.BuildBr(builder, openBlock);

        (LlvmValueHandle fd, LlvmValueHandle len) = EmitLinuxFileMmapProbe(state, pathCstr, fdSlot, openBlock, seekBlock, emptyBlock, emptyOkBlock, mapBlock, closeErrBlock, errBlock);
        EmitLinuxFileMmapFinish(state, fd, len, fdSlot, resultSlot, emptyOkBlock, mapBlock, okBlock, closeErrBlock, errBlock, contBlock);

        LlvmApi.PositionBuilderAtEnd(builder, contBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "fs_mmap_result_value");
    }

    private static (LlvmValueHandle Fd, LlvmValueHandle Len) EmitLinuxFileMmapProbe(
        LlvmCodegenState state,
        LlvmValueHandle pathCstr,
        LlvmValueHandle fdSlot,
        LlvmBasicBlockHandle openBlock,
        LlvmBasicBlockHandle seekBlock,
        LlvmBasicBlockHandle emptyBlock,
        LlvmBasicBlockHandle emptyOkBlock,
        LlvmBasicBlockHandle mapBlock,
        LlvmBasicBlockHandle closeErrBlock,
        LlvmBasicBlockHandle errBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        // open(path, O_RDONLY, 0)
        LlvmApi.PositionBuilderAtEnd(builder, openBlock);
        LlvmValueHandle fd = EmitLinuxSyscall(state, SyscallOpen,
            LlvmApi.BuildPtrToInt(builder, pathCstr, state.I64, "fs_mmap_path_ptr"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "fs_mmap_open_call");
        LlvmApi.BuildStore(builder, fd, fdSlot);
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, fd, LlvmApi.ConstInt(state.I64, 0, 0), "fs_mmap_open_failed"), errBlock, seekBlock);

        // lseek(fd, 0, SEEK_END) -> length
        LlvmApi.PositionBuilderAtEnd(builder, seekBlock);
        LlvmValueHandle len = EmitLinuxSyscall(state, SyscallLseek, fd, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 2, 0), "fs_mmap_seek_call");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, len, LlvmApi.ConstInt(state.I64, 0, 0), "fs_mmap_seek_failed"), closeErrBlock, emptyBlock);

        // An empty file can't be mmap'd (length 0) — return an empty owned Bytes.
        LlvmApi.PositionBuilderAtEnd(builder, emptyBlock);
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, len, LlvmApi.ConstInt(state.I64, 0, 0), "fs_mmap_is_empty"), emptyOkBlock, mapBlock);

        return (fd, len);
    }

    private static void EmitLinuxFileMmapFinish(
        LlvmCodegenState state,
        LlvmValueHandle fd,
        LlvmValueHandle len,
        LlvmValueHandle fdSlot,
        LlvmValueHandle resultSlot,
        LlvmBasicBlockHandle emptyOkBlock,
        LlvmBasicBlockHandle mapBlock,
        LlvmBasicBlockHandle okBlock,
        LlvmBasicBlockHandle closeErrBlock,
        LlvmBasicBlockHandle errBlock,
        LlvmBasicBlockHandle contBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        LlvmApi.PositionBuilderAtEnd(builder, emptyOkBlock);
        EmitLinuxSyscall(state, SyscallClose, fd, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), "fs_mmap_empty_close");
        LlvmApi.BuildStore(builder, EmitResultOk(state, EmitHeapStringLiteral(state, "")), resultSlot);
        LlvmApi.BuildBr(builder, contBlock);

        // mmap(NULL, len, PROT_READ, MAP_PRIVATE, fd, 0)
        LlvmApi.PositionBuilderAtEnd(builder, mapBlock);
        LlvmValueHandle ptr = EmitLinuxSyscall6(state, SyscallMmap,
            LlvmApi.ConstInt(state.I64, 0, 0),                          // addr = NULL
            len,                                                          // length
            LlvmApi.ConstInt(state.I64, 1, 0),                          // PROT_READ
            LlvmApi.ConstInt(state.I64, 2, 0),                          // MAP_PRIVATE
            fd,                                                           // fd
            LlvmApi.ConstInt(state.I64, 0, 0),                          // offset
            "fs_mmap_call");
        // The raw mmap syscall returns -errno in [-4095, -1] on failure (huge unsigned).
        LlvmValueHandle mapFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, ptr, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-4096L)), 0), "fs_mmap_map_failed");
        LlvmApi.BuildCondBr(builder, mapFailed, closeErrBlock, okBlock);

        LlvmApi.PositionBuilderAtEnd(builder, okBlock);
        // The mapping survives the fd being closed; keep the mapping for the program's lifetime.
        EmitLinuxSyscall(state, SyscallClose, fd, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), "fs_mmap_ok_close");
        LlvmValueHandle view = EmitStringView(state, LlvmApi.BuildIntToPtr(builder, ptr, state.I8Ptr, "fs_mmap_ptr"), len, "fs_mmap_view");
        LlvmApi.BuildStore(builder, EmitResultOk(state, view), resultSlot);
        LlvmApi.BuildBr(builder, contBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeErrBlock);
        EmitLinuxSyscall(state, SyscallClose, LlvmApi.BuildLoad2(builder, state.I64, fdSlot, "fs_mmap_err_fd"), LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), "fs_mmap_err_close");
        LlvmApi.BuildBr(builder, errBlock);

        LlvmApi.PositionBuilderAtEnd(builder, errBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, FileReadFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, contBlock);
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

    private static LlvmValueHandle EmitLinuxFileReadText(LlvmCodegenState state, LlvmValueHandle pathRef, bool rawBytes = false)
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

        LlvmValueHandle fileLength = EmitLinuxFileReadTextOpenAndMeasure(state, pathCstr, fdSlot, openBlock, seekEndBlock, seekStartBlock, allocBlock, maybeCloseErrorBlock, returnErrorBlock);
        EmitLinuxFileReadTextAllocate(state, rawBytes, fileLength, stringSlot, remainingSlot, cursorSlot, allocBlock, readCheckBlock, utf8CheckBlock, maybeCloseErrorBlock);
        EmitLinuxFileReadTextReadLoop(state, fdSlot, remainingSlot, cursorSlot, readCheckBlock, readBodyBlock, readDoneBlock, utf8CheckBlock, maybeCloseErrorBlock);
        EmitLinuxFileReadTextValidateAndClose(state, rawBytes, fdSlot, stringSlot, resultSlot, utf8CheckBlock, closeOkBlock, closeInvalidBlock, continueBlock);
        EmitLinuxFileReadTextErrorPaths(state, fdSlot, resultSlot, maybeCloseErrorBlock, closeHandleBlock, returnErrorBlock, closeErrorBlock, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "fs_read_result_value");
    }

    private static LlvmValueHandle EmitLinuxFileReadTextOpenAndMeasure(
        LlvmCodegenState state,
        LlvmValueHandle pathCstr,
        LlvmValueHandle fdSlot,
        LlvmBasicBlockHandle openBlock,
        LlvmBasicBlockHandle seekEndBlock,
        LlvmBasicBlockHandle seekStartBlock,
        LlvmBasicBlockHandle allocBlock,
        LlvmBasicBlockHandle maybeCloseErrorBlock,
        LlvmBasicBlockHandle returnErrorBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

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

        return fileLength;
    }

    private static void EmitLinuxFileReadTextAllocate(
        LlvmCodegenState state,
        bool rawBytes,
        LlvmValueHandle fileLength,
        LlvmValueHandle stringSlot,
        LlvmValueHandle remainingSlot,
        LlvmValueHandle cursorSlot,
        LlvmBasicBlockHandle allocBlock,
        LlvmBasicBlockHandle readCheckBlock,
        LlvmBasicBlockHandle utf8CheckBlock,
        LlvmBasicBlockHandle maybeCloseErrorBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        LlvmApi.PositionBuilderAtEnd(builder, allocBlock);
        var withinLimitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_within_limit");
        if (rawBytes)
        {
            // readAllBytes: no cap — the allocation is sized by the file, which the caller opted into.
            LlvmApi.BuildBr(builder, withinLimitBlock);
        }
        else
        {
            LlvmValueHandle exceedsLimit = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, fileLength, LlvmApi.ConstInt(state.I64, MaxFileReadBytes, 0), "fs_read_exceeds_limit");
            LlvmApi.BuildCondBr(builder, exceedsLimit, maybeCloseErrorBlock, withinLimitBlock);
        }

        LlvmApi.PositionBuilderAtEnd(builder, withinLimitBlock);
        LlvmValueHandle totalBytes = LlvmApi.BuildAdd(builder, fileLength, LlvmApi.ConstInt(state.I64, 8, 0), "fs_read_total_bytes");
        // readAllBytes may read a file far larger than one arena chunk (the bump arena's chunks are a
        // fixed size and a single allocation can't exceed one), so the whole-file buffer is a standalone
        // OS mapping (mmap) rather than an arena allocation. It is a read-only, program-lifetime buffer
        // (fields sliced out of it are copied into the arena as usual), so it is never arena-reclaimed.
        LlvmValueHandle stringRef = rawBytes
            ? EmitAllocateOsMemory(state, totalBytes, "fs_read_raw_buf")
            : EmitAllocDynamic(state, totalBytes);
        StoreMemory(state, stringRef, 0, fileLength, "fs_read_len");
        LlvmApi.BuildStore(builder, stringRef, stringSlot);
        LlvmApi.BuildStore(builder, fileLength, remainingSlot);
        LlvmApi.BuildStore(builder, GetStringBytesAddress(state, stringRef, "fs_read_cursor_start"), cursorSlot);
        LlvmValueHandle isEmpty = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, fileLength, LlvmApi.ConstInt(state.I64, 0, 0), "fs_read_empty");
        LlvmApi.BuildCondBr(builder, isEmpty, utf8CheckBlock, readCheckBlock);
    }

    private static void EmitLinuxFileReadTextReadLoop(
        LlvmCodegenState state,
        LlvmValueHandle fdSlot,
        LlvmValueHandle remainingSlot,
        LlvmValueHandle cursorSlot,
        LlvmBasicBlockHandle readCheckBlock,
        LlvmBasicBlockHandle readBodyBlock,
        LlvmBasicBlockHandle readDoneBlock,
        LlvmBasicBlockHandle utf8CheckBlock,
        LlvmBasicBlockHandle maybeCloseErrorBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

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
    }

    private static void EmitLinuxFileReadTextValidateAndClose(
        LlvmCodegenState state,
        bool rawBytes,
        LlvmValueHandle fdSlot,
        LlvmValueHandle stringSlot,
        LlvmValueHandle resultSlot,
        LlvmBasicBlockHandle utf8CheckBlock,
        LlvmBasicBlockHandle closeOkBlock,
        LlvmBasicBlockHandle closeInvalidBlock,
        LlvmBasicBlockHandle continueBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        LlvmApi.PositionBuilderAtEnd(builder, utf8CheckBlock);
        if (rawBytes)
        {
            // readAllBytes: arbitrary bytes are valid, no UTF-8 check.
            LlvmApi.BuildBr(builder, closeOkBlock);
        }
        else
        {
            LlvmValueHandle utf8Valid = EmitValidateUtf8(
                state,
                GetStringBytesPointer(state, LlvmApi.BuildLoad2(builder, state.I64, stringSlot, "fs_read_string_value"), "fs_read_utf8_ptr"),
                LoadStringLength(state, LlvmApi.BuildLoad2(builder, state.I64, stringSlot, "fs_read_string_len_value"), "fs_read_utf8_len"),
                "fs_read_utf8");
            LlvmValueHandle isUtf8Valid = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, utf8Valid, LlvmApi.ConstInt(state.I64, 0, 0), "fs_read_is_utf8_valid");
            LlvmApi.BuildCondBr(builder, isUtf8Valid, closeOkBlock, closeInvalidBlock);
        }

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
    }

    private static void EmitLinuxFileReadTextErrorPaths(
        LlvmCodegenState state,
        LlvmValueHandle fdSlot,
        LlvmValueHandle resultSlot,
        LlvmBasicBlockHandle maybeCloseErrorBlock,
        LlvmBasicBlockHandle closeHandleBlock,
        LlvmBasicBlockHandle returnErrorBlock,
        LlvmBasicBlockHandle closeErrorBlock,
        LlvmBasicBlockHandle continueBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

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

        EmitLinuxFileWriteTextLoop(state, pathCstr, textRef, fdSlot, remainingSlot, cursorSlot, openBlock, loopCheckBlock, loopBodyBlock, advanceBlock, closeOkBlock, maybeCloseErrorBlock, returnErrorBlock);
        EmitLinuxFileWriteTextFinish(state, fdSlot, resultSlot, closeOkBlock, maybeCloseErrorBlock, closeErrorBlock, returnErrorBlock, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "fs_write_result_value");
    }

    private static void EmitLinuxFileWriteTextLoop(
        LlvmCodegenState state,
        LlvmValueHandle pathCstr,
        LlvmValueHandle textRef,
        LlvmValueHandle fdSlot,
        LlvmValueHandle remainingSlot,
        LlvmValueHandle cursorSlot,
        LlvmBasicBlockHandle openBlock,
        LlvmBasicBlockHandle loopCheckBlock,
        LlvmBasicBlockHandle loopBodyBlock,
        LlvmBasicBlockHandle advanceBlock,
        LlvmBasicBlockHandle closeOkBlock,
        LlvmBasicBlockHandle maybeCloseErrorBlock,
        LlvmBasicBlockHandle returnErrorBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

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
    }

    private static void EmitLinuxFileWriteTextFinish(
        LlvmCodegenState state,
        LlvmValueHandle fdSlot,
        LlvmValueHandle resultSlot,
        LlvmBasicBlockHandle closeOkBlock,
        LlvmBasicBlockHandle maybeCloseErrorBlock,
        LlvmBasicBlockHandle closeErrorBlock,
        LlvmBasicBlockHandle returnErrorBlock,
        LlvmBasicBlockHandle continueBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

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
    }

    private static LlvmValueHandle EmitLinuxFileWriteBytes(LlvmCodegenState state, LlvmValueHandle pathRef, LlvmValueHandle bytesAddress, LlvmValueHandle byteCount)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle pathCstr = EmitStringToCString(state, pathRef, "fs_write_bytes_path");
        LlvmValueHandle fdSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_write_bytes_fd");
        LlvmValueHandle remainingSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_write_bytes_remaining");
        LlvmValueHandle cursorSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_write_bytes_cursor");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_write_bytes_result");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), fdSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

        var openBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_bytes_open");
        var loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_bytes_loop_check");
        var loopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_bytes_loop_body");
        var advanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_bytes_advance");
        var closeOkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_bytes_close_ok");
        var maybeCloseErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_bytes_maybe_close_error");
        var closeErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_bytes_close_error");
        var returnErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_bytes_return_error");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_bytes_continue");

        LlvmApi.BuildBr(builder, openBlock);

        EmitLinuxFileWriteBytesLoop(state, pathCstr, bytesAddress, byteCount, fdSlot, remainingSlot, cursorSlot, openBlock, loopCheckBlock, loopBodyBlock, advanceBlock, closeOkBlock, maybeCloseErrorBlock, returnErrorBlock);
        EmitLinuxFileWriteBytesFinish(state, fdSlot, resultSlot, closeOkBlock, maybeCloseErrorBlock, closeErrorBlock, returnErrorBlock, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "fs_write_bytes_result_value");
    }

    private static void EmitLinuxFileWriteBytesLoop(
        LlvmCodegenState state,
        LlvmValueHandle pathCstr,
        LlvmValueHandle bytesAddress,
        LlvmValueHandle byteCount,
        LlvmValueHandle fdSlot,
        LlvmValueHandle remainingSlot,
        LlvmValueHandle cursorSlot,
        LlvmBasicBlockHandle openBlock,
        LlvmBasicBlockHandle loopCheckBlock,
        LlvmBasicBlockHandle loopBodyBlock,
        LlvmBasicBlockHandle advanceBlock,
        LlvmBasicBlockHandle closeOkBlock,
        LlvmBasicBlockHandle maybeCloseErrorBlock,
        LlvmBasicBlockHandle returnErrorBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        LlvmApi.PositionBuilderAtEnd(builder, openBlock);
        LlvmValueHandle fd = EmitLinuxSyscall(
            state,
            SyscallOpen,
            LlvmApi.BuildPtrToInt(builder, pathCstr, state.I64, "fs_write_bytes_path_ptr"),
            LlvmApi.ConstInt(state.I64, 0x241, 0),
            LlvmApi.ConstInt(state.I64, 420, 0),
            "fs_write_bytes_open_call");
        LlvmApi.BuildStore(builder, fd, fdSlot);
        LlvmValueHandle openFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, fd, LlvmApi.ConstInt(state.I64, 0, 0), "fs_write_bytes_open_failed");
        LlvmApi.BuildStore(builder, byteCount, remainingSlot);
        LlvmApi.BuildStore(builder, bytesAddress, cursorSlot);
        LlvmApi.BuildCondBr(builder, openFailed, returnErrorBlock, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle remaining = LlvmApi.BuildLoad2(builder, state.I64, remainingSlot, "fs_write_bytes_remaining_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, remaining, LlvmApi.ConstInt(state.I64, 0, 0), "fs_write_bytes_done");
        LlvmApi.BuildCondBr(builder, done, closeOkBlock, loopBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBodyBlock);
        LlvmValueHandle cursorAddress = LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, "fs_write_bytes_cursor_value");
        LlvmValueHandle bytesWritten = EmitLinuxSyscall(
            state,
            SyscallWrite,
            LlvmApi.BuildLoad2(builder, state.I64, fdSlot, "fs_write_bytes_fd_value"),
            cursorAddress,
            remaining,
            "fs_write_bytes_write_call");
        LlvmValueHandle writeFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, bytesWritten, LlvmApi.ConstInt(state.I64, 0, 0), "fs_write_bytes_failed");
        LlvmApi.BuildCondBr(builder, writeFailed, maybeCloseErrorBlock, advanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, advanceBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildSub(builder, remaining, bytesWritten, "fs_write_bytes_remaining_next"), remainingSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, cursorAddress, bytesWritten, "fs_write_bytes_cursor_next"), cursorSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);
    }

    private static void EmitLinuxFileWriteBytesFinish(
        LlvmCodegenState state,
        LlvmValueHandle fdSlot,
        LlvmValueHandle resultSlot,
        LlvmBasicBlockHandle closeOkBlock,
        LlvmBasicBlockHandle maybeCloseErrorBlock,
        LlvmBasicBlockHandle closeErrorBlock,
        LlvmBasicBlockHandle returnErrorBlock,
        LlvmBasicBlockHandle continueBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        LlvmApi.PositionBuilderAtEnd(builder, closeOkBlock);
        EmitLinuxSyscall(
            state,
            SyscallClose,
            LlvmApi.BuildLoad2(builder, state.I64, fdSlot, "fs_write_bytes_close_fd"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "fs_write_bytes_close_ok_call");
        LlvmApi.BuildStore(builder, EmitResultOk(state, EmitUnitValue(state)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, maybeCloseErrorBlock);
        LlvmValueHandle fdValue = LlvmApi.BuildLoad2(builder, state.I64, fdSlot, "fs_write_bytes_error_fd");
        LlvmValueHandle shouldClose = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, fdValue, LlvmApi.ConstInt(state.I64, 0, 0), "fs_write_bytes_should_close");
        LlvmApi.BuildCondBr(builder, shouldClose, closeErrorBlock, returnErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeErrorBlock);
        EmitLinuxSyscall(
            state,
            SyscallClose,
            LlvmApi.BuildLoad2(builder, state.I64, fdSlot, "fs_write_bytes_close_error_fd"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "fs_write_bytes_close_error_call");
        LlvmApi.BuildBr(builder, returnErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, returnErrorBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, FileWriteFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);
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

    private static LlvmValueHandle EmitWindowsFileReadText(LlvmCodegenState state, LlvmValueHandle pathRef, bool rawBytes = false)
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

        LlvmValueHandle stringRef = EmitWindowsFileReadTextRead(state, handleSlot, bytesReadSlot, readBlock, utf8Block, closeErrorBlock);
        EmitWindowsFileReadTextFinish(state, rawBytes, stringRef, handleSlot, resultSlot, utf8Block, closeOkBlock, closeInvalidBlock, closeErrorBlock, returnErrorBlock, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "fs_read_win_result_value");
    }

    private static LlvmValueHandle EmitWindowsFileReadTextRead(
        LlvmCodegenState state,
        LlvmValueHandle handleSlot,
        LlvmValueHandle bytesReadSlot,
        LlvmBasicBlockHandle readBlock,
        LlvmBasicBlockHandle utf8Block,
        LlvmBasicBlockHandle closeErrorBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

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

        return stringRef;
    }

    private static void EmitWindowsFileReadTextFinish(
        LlvmCodegenState state,
        bool rawBytes,
        LlvmValueHandle stringRef,
        LlvmValueHandle handleSlot,
        LlvmValueHandle resultSlot,
        LlvmBasicBlockHandle utf8Block,
        LlvmBasicBlockHandle closeOkBlock,
        LlvmBasicBlockHandle closeInvalidBlock,
        LlvmBasicBlockHandle closeErrorBlock,
        LlvmBasicBlockHandle returnErrorBlock,
        LlvmBasicBlockHandle continueBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        LlvmApi.PositionBuilderAtEnd(builder, utf8Block);
        if (rawBytes)
        {
            // readAllBytes: no UTF-8 check (Windows still uses the fixed readText buffer, so it caps at
            // the same size — a linux-only uncapped path today).
            LlvmApi.BuildBr(builder, closeOkBlock);
        }
        else
        {
            LlvmValueHandle utf8Valid = EmitValidateUtf8(
                state,
                GetStringBytesPointer(state, stringRef, "fs_read_win_utf8_ptr"),
                LoadStringLength(state, stringRef, "fs_read_win_utf8_len"),
                "fs_read_win_utf8");
            LlvmValueHandle isUtf8Valid = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, utf8Valid, LlvmApi.ConstInt(state.I64, 0, 0), "fs_read_win_is_utf8_valid");
            LlvmApi.BuildCondBr(builder, isUtf8Valid, closeOkBlock, closeInvalidBlock);
        }

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

        EmitWindowsFileWriteTextLoop(state, handleSlot, remainingSlot, cursorSlot, bytesWrittenSlot, loopCheckBlock, loopBodyBlock, advanceBlock, closeOkBlock, closeErrorBlock);
        EmitWindowsFileWriteTextFinish(state, handleSlot, resultSlot, closeOkBlock, closeErrorBlock, returnErrorBlock, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "fs_write_win_result_value");
    }

    private static void EmitWindowsFileWriteTextLoop(
        LlvmCodegenState state,
        LlvmValueHandle handleSlot,
        LlvmValueHandle remainingSlot,
        LlvmValueHandle cursorSlot,
        LlvmValueHandle bytesWrittenSlot,
        LlvmBasicBlockHandle loopCheckBlock,
        LlvmBasicBlockHandle loopBodyBlock,
        LlvmBasicBlockHandle advanceBlock,
        LlvmBasicBlockHandle closeOkBlock,
        LlvmBasicBlockHandle closeErrorBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

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
    }

    private static void EmitWindowsFileWriteTextFinish(
        LlvmCodegenState state,
        LlvmValueHandle handleSlot,
        LlvmValueHandle resultSlot,
        LlvmBasicBlockHandle closeOkBlock,
        LlvmBasicBlockHandle closeErrorBlock,
        LlvmBasicBlockHandle returnErrorBlock,
        LlvmBasicBlockHandle continueBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

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
    }

    private static LlvmValueHandle EmitWindowsFileWriteBytes(LlvmCodegenState state, LlvmValueHandle pathRef, LlvmValueHandle bytesAddress, LlvmValueHandle byteCount)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle pathCstr = EmitStringToCString(state, pathRef, "fs_write_bytes_path");
        LlvmValueHandle handleSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_write_bytes_handle");
        LlvmValueHandle remainingSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_write_bytes_remaining");
        LlvmValueHandle cursorSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_write_bytes_cursor");
        LlvmValueHandle bytesWrittenSlot = LlvmApi.BuildAlloca(builder, state.I32, "fs_write_bytes_bytes_written");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_write_bytes_result");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), handleSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

        var openBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_bytes_win_open");
        var loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_bytes_win_loop_check");
        var loopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_bytes_win_loop_body");
        var advanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_bytes_win_advance");
        var closeOkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_bytes_win_close_ok");
        var closeErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_bytes_win_close_error");
        var returnErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_bytes_win_return_error");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_bytes_win_continue");

        LlvmApi.BuildBr(builder, openBlock);

        LlvmApi.PositionBuilderAtEnd(builder, openBlock);
        LlvmValueHandle handle = EmitWindowsCreateFile(
            state,
            pathCstr,
            0x40000000,
            0,
            2,
            "fs_write_bytes_create_file");
        LlvmApi.BuildStore(builder, handle, handleSlot);
        LlvmApi.BuildStore(builder, byteCount, remainingSlot);
        LlvmApi.BuildStore(builder, bytesAddress, cursorSlot);
        LlvmValueHandle openFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, handle, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), "fs_write_bytes_handle_invalid");
        LlvmApi.BuildCondBr(builder, openFailed, returnErrorBlock, loopCheckBlock);

        EmitWindowsFileWriteBytesLoop(state, handleSlot, remainingSlot, cursorSlot, bytesWrittenSlot, loopCheckBlock, loopBodyBlock, advanceBlock, closeOkBlock, closeErrorBlock);
        EmitWindowsFileWriteBytesFinish(state, handleSlot, resultSlot, closeOkBlock, closeErrorBlock, returnErrorBlock, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "fs_write_bytes_win_result_value");
    }

    private static void EmitWindowsFileWriteBytesLoop(
        LlvmCodegenState state,
        LlvmValueHandle handleSlot,
        LlvmValueHandle remainingSlot,
        LlvmValueHandle cursorSlot,
        LlvmValueHandle bytesWrittenSlot,
        LlvmBasicBlockHandle loopCheckBlock,
        LlvmBasicBlockHandle loopBodyBlock,
        LlvmBasicBlockHandle advanceBlock,
        LlvmBasicBlockHandle closeOkBlock,
        LlvmBasicBlockHandle closeErrorBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle remaining = LlvmApi.BuildLoad2(builder, state.I64, remainingSlot, "fs_write_bytes_win_remaining_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, remaining, LlvmApi.ConstInt(state.I64, 0, 0), "fs_write_bytes_win_done");
        LlvmApi.BuildCondBr(builder, done, closeOkBlock, loopBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBodyBlock);
        LlvmValueHandle chunkSize = LlvmApi.BuildSelect(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, remaining, LlvmApi.ConstInt(state.I64, uint.MaxValue, 0), "fs_write_bytes_win_chunk_gt"),
            LlvmApi.ConstInt(state.I64, uint.MaxValue, 0),
            remaining,
            "fs_write_bytes_win_chunk_size");
        LlvmValueHandle wrote = EmitWindowsWriteFile(
            state,
            LlvmApi.BuildLoad2(builder, state.I64, handleSlot, "fs_write_bytes_handle_value"),
            LlvmApi.BuildIntToPtr(builder, LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, "fs_write_bytes_cursor_value"), state.I8Ptr, "fs_write_bytes_cursor_ptr"),
            LlvmApi.BuildTrunc(builder, chunkSize, state.I32, "fs_write_bytes_chunk_i32"),
            bytesWrittenSlot,
            "fs_write_bytes_win_write_call");
        LlvmApi.BuildCondBr(builder, wrote, advanceBlock, closeErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, advanceBlock);
        LlvmValueHandle bytesWritten = LlvmApi.BuildZExt(builder, LlvmApi.BuildLoad2(builder, state.I32, bytesWrittenSlot, "fs_write_bytes_written_value"), state.I64, "fs_write_bytes_written_i64");
        LlvmValueHandle wroteZero = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, bytesWritten, LlvmApi.ConstInt(state.I64, 0, 0), "fs_write_bytes_wrote_zero");
        var zeroWriteBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_bytes_win_zero");
        var updateBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_bytes_win_update");
        LlvmApi.BuildCondBr(builder, wroteZero, zeroWriteBlock, updateBlock);

        LlvmApi.PositionBuilderAtEnd(builder, zeroWriteBlock);
        LlvmApi.BuildBr(builder, closeErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, updateBlock);
        LlvmValueHandle cursorValue = LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, "fs_write_bytes_cursor_current");
        LlvmApi.BuildStore(builder, LlvmApi.BuildSub(builder, remaining, bytesWritten, "fs_write_bytes_remaining_next"), remainingSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, cursorValue, bytesWritten, "fs_write_bytes_cursor_next"), cursorSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);
    }

    private static void EmitWindowsFileWriteBytesFinish(
        LlvmCodegenState state,
        LlvmValueHandle handleSlot,
        LlvmValueHandle resultSlot,
        LlvmBasicBlockHandle closeOkBlock,
        LlvmBasicBlockHandle closeErrorBlock,
        LlvmBasicBlockHandle returnErrorBlock,
        LlvmBasicBlockHandle continueBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        LlvmApi.PositionBuilderAtEnd(builder, closeOkBlock);
        EmitWindowsCloseHandle(state, LlvmApi.BuildLoad2(builder, state.I64, handleSlot, "fs_write_bytes_close_handle"), "fs_write_bytes_close_ok");
        LlvmApi.BuildStore(builder, EmitResultOk(state, EmitUnitValue(state)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeErrorBlock);
        EmitWindowsCloseHandle(state, LlvmApi.BuildLoad2(builder, state.I64, handleSlot, "fs_write_bytes_error_handle"), "fs_write_bytes_close_error");
        LlvmApi.BuildBr(builder, returnErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, returnErrorBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, FileWriteFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);
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

    /// <summary>
    /// Emits a Drop operation for deterministic cleanup of owned values.
    /// Resource types (Socket) route to platform-specific close functions.
    /// Other owned types (String, List, ADTs, Closures) are no-ops in the
    /// current linear allocator — the IR records the drop for correctness;
    /// actual deallocation is handled by arena-based memory reclamation.
    /// Returns false because Drop does not terminate the current basic block.
    /// </summary>
    // FileHandle resource value is the OS fd (Linux) / HANDLE (Windows), carried as i64.

    private static LlvmValueHandle EmitFileOpenForReading(LlvmCodegenState state, LlvmValueHandle pathRef, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle pathCstr = EmitStringToCString(state, pathRef, name + "_path");
        if (IsLinuxFlavor(state.Flavor))
        {
            // open(path, O_RDONLY=0, 0); EmitLinuxSyscall maps this to openat on arm64.
            return EmitLinuxSyscall(
                state,
                SyscallOpen,
                LlvmApi.BuildPtrToInt(builder, pathCstr, state.I64, name + "_path_ptr"),
                LlvmApi.ConstInt(state.I64, 0, 0),
                LlvmApi.ConstInt(state.I64, 0, 0),
                name + "_open");
        }

        // GENERIC_READ, FILE_SHARE_READ, OPEN_EXISTING.
        return EmitWindowsCreateFile(state, pathCstr, unchecked((int)0x80000000), 1, 3, name + "_create");
    }

    private static LlvmValueHandle EmitFileOpen(LlvmCodegenState state, LlvmValueHandle pathRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "fopen_result");
        LlvmValueHandle handle = EmitFileOpenForReading(state, pathRef, "fopen");

        var errBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fopen_err");
        var okBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fopen_ok");
        var contBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fopen_cont");

        // Linux open() returns a negative errno on failure; Windows CreateFile returns
        // INVALID_HANDLE_VALUE (-1). Valid handles are non-negative, so `< 0` covers both.
        LlvmValueHandle failed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, handle, LlvmApi.ConstInt(state.I64, 0, 0), "fopen_failed");
        LlvmApi.BuildCondBr(builder, failed, errBlock, okBlock);

        LlvmApi.PositionBuilderAtEnd(builder, okBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, handle), resultSlot);
        LlvmApi.BuildBr(builder, contBlock);

        LlvmApi.PositionBuilderAtEnd(builder, errBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, "Ashes.File.open: could not open file")), resultSlot);
        LlvmApi.BuildBr(builder, contBlock);

        LlvmApi.PositionBuilderAtEnd(builder, contBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "fopen_result_value");
    }

    private static LlvmValueHandle EmitFileReadChunk(LlvmCodegenState state, LlvmValueHandle handleVal, LlvmValueHandle countVal)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "fchunk_result");

        // Allocate a string with room for `count` bytes; the actual length is set after the read.
        LlvmValueHandle stringRef = EmitAllocDynamic(state, LlvmApi.BuildAdd(builder, countVal, LlvmApi.ConstInt(state.I64, 8, 0), "fchunk_total"));
        LlvmValueHandle destPtr = GetStringBytesPointer(state, stringRef, "fchunk_dest");

        LlvmValueHandle nRead;
        if (IsLinuxFlavor(state.Flavor))
        {
            nRead = EmitLinuxSyscall(state, SyscallRead, handleVal,
                LlvmApi.BuildPtrToInt(builder, destPtr, state.I64, "fchunk_dest_int"), countVal, "fchunk_read");
        }
        else
        {
            LlvmValueHandle bytesReadSlot = LlvmApi.BuildAlloca(builder, state.I32, "fchunk_bytes_read");
            LlvmValueHandle readOk = EmitWindowsReadFile(state, handleVal, destPtr,
                LlvmApi.BuildTrunc(builder, countVal, state.I32, "fchunk_count_i32"), bytesReadSlot, "fchunk_read");
            LlvmValueHandle bytesRead = LlvmApi.BuildZExt(builder, LlvmApi.BuildLoad2(builder, state.I32, bytesReadSlot, "fchunk_bytes_val"), state.I64, "fchunk_bytes_i64");
            nRead = LlvmApi.BuildSelect(builder, readOk, bytesRead, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), "fchunk_n");
        }

        var errBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fchunk_err");
        var okBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fchunk_ok");
        var contBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fchunk_cont");

        // n < 0 is an error; n == 0 is EOF and yields an empty-string Ok.
        LlvmValueHandle failed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, nRead, LlvmApi.ConstInt(state.I64, 0, 0), "fchunk_failed");
        LlvmApi.BuildCondBr(builder, failed, errBlock, okBlock);

        LlvmApi.PositionBuilderAtEnd(builder, okBlock);
        StoreMemory(state, stringRef, 0, nRead, "fchunk_len");
        LlvmApi.BuildStore(builder, EmitResultOk(state, stringRef), resultSlot);
        LlvmApi.BuildBr(builder, contBlock);

        LlvmApi.PositionBuilderAtEnd(builder, errBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, "Ashes.File.readChunk: read failed")), resultSlot);
        LlvmApi.BuildBr(builder, contBlock);

        LlvmApi.PositionBuilderAtEnd(builder, contBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "fchunk_result_value");
    }

    // Ashes.File.readLine(handle) : Maybe(Str). Reads one '\n'-terminated line (newline + a trailing
    // '\r' stripped) from the handle's fd through a refillable module-global buffer, returning None at
    // EOF (or on a read error, matching stdin readLine). Mirrors EmitReadLine but the read buffer is
    // guarded by the fd it currently holds: if readLine is called with a different handle than the one
    // buffered, the buffer is reset (any read-ahead for the previous fd is discarded — so this is for
    // reading one file to completion, not interleaving line-reads across handles). No per-call alloca:
    // the line buffer and scratch are .bss globals, so it is safe inside a TCO loop.
    private static LlvmValueHandle EmitFileReadLine(LlvmCodegenState state, LlvmValueHandle handleVal)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle lineBufType = LlvmApi.ArrayType2(state.I8, InputBufSize);
        LlvmValueHandle lineBuf = ReadLineScratchGlobal(state, "__ashes_fileline_buf", lineBufType);
        LlvmValueHandle lineBufPtr = GetArrayElementPointer(state, lineBufType, lineBuf, LlvmApi.ConstInt(state.I64, 0, 0), "fline_buf_ptr");
        LlvmValueHandle byteSlot = ReadLineScratchGlobal(state, "__ashes_fileline_byte", state.I8);
        LlvmValueHandle lenSlot = ReadLineScratchGlobal(state, "__ashes_fileline_len", state.I64);
        LlvmValueHandle resultSlot = ReadLineScratchGlobal(state, "__ashes_fileline_result", state.I64);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), lenSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

        LlvmTypeHandle fbufType = LlvmApi.ArrayType2(state.I8, StdinReadBufSize);
        LlvmValueHandle fbuf = ReadLineScratchGlobal(state, "__ashes_file_rbuf", fbufType);
        LlvmValueHandle fbufPtr = GetArrayElementPointer(state, fbufType, fbuf, LlvmApi.ConstInt(state.I64, 0, 0), "file_rbuf_ptr");
        LlvmValueHandle fposSlot = ReadLineScratchGlobal(state, "__ashes_file_rpos", state.I64);
        LlvmValueHandle flenSlot = ReadLineScratchGlobal(state, "__ashes_file_rlen", state.I64);
        LlvmValueHandle ffdSlot = ReadLineScratchGlobal(state, "__ashes_file_rfd", state.I64);

        EmitFileReadLineFdGuard(state, handleVal, ffdSlot, fposSlot, flenSlot);

        LlvmValueHandle bytesReadSlot = default;
        if (state.Flavor == LlvmCodegenFlavor.WindowsX64)
        {
            bytesReadSlot = ReadLineScratchGlobal(state, "__ashes_fileline_bytes_read", state.I32);
        }

        var loopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fline_loop");
        var refillBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fline_refill");
        var haveByteBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fline_have_byte");
        var inspectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fline_inspect");
        var skipCrBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fline_skip_cr");
        var storeByteBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fline_store_byte");
        var appendByteBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fline_append_byte");
        var eofBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fline_eof");
        var finishSomeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fline_finish_some");
        var returnNoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fline_return_none");
        var overflowBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fline_overflow");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fline_continue");

        LlvmApi.BuildBr(builder, loopBlock);

        EmitFileReadLineFill(state, handleVal, fbufPtr, fposSlot, flenSlot, bytesReadSlot, loopBlock, refillBlock, haveByteBlock, eofBlock);
        EmitFileReadLineScan(state, fbufPtr, lineBufPtr, byteSlot, lenSlot, fposSlot, loopBlock, haveByteBlock, inspectBlock, skipCrBlock, storeByteBlock, appendByteBlock, finishSomeBlock, overflowBlock);
        EmitFileReadLineFinish(state, lineBufPtr, lenSlot, resultSlot, eofBlock, finishSomeBlock, returnNoneBlock, overflowBlock, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "fline_result_value");
    }

    // fd guard: if the buffer currently holds a different handle, discard it and start fresh.
    private static void EmitFileReadLineFdGuard(
        LlvmCodegenState state,
        LlvmValueHandle handleVal,
        LlvmValueHandle ffdSlot,
        LlvmValueHandle fposSlot,
        LlvmValueHandle flenSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle bufferedFd = LlvmApi.BuildLoad2(builder, state.I64, ffdSlot, "fline_buffered_fd");
        LlvmValueHandle sameFd = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, bufferedFd, handleVal, "fline_same_fd");
        var resetFdBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fline_reset_fd");
        var afterFdBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fline_after_fd");
        LlvmApi.BuildCondBr(builder, sameFd, afterFdBlock, resetFdBlock);
        LlvmApi.PositionBuilderAtEnd(builder, resetFdBlock);
        LlvmApi.BuildStore(builder, handleVal, ffdSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), fposSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), flenSlot);
        LlvmApi.BuildBr(builder, afterFdBlock);
        LlvmApi.PositionBuilderAtEnd(builder, afterFdBlock);
    }

    private static void EmitFileReadLineFill(
        LlvmCodegenState state,
        LlvmValueHandle handleVal,
        LlvmValueHandle fbufPtr,
        LlvmValueHandle fposSlot,
        LlvmValueHandle flenSlot,
        LlvmValueHandle bytesReadSlot,
        LlvmBasicBlockHandle loopBlock,
        LlvmBasicBlockHandle refillBlock,
        LlvmBasicBlockHandle haveByteBlock,
        LlvmBasicBlockHandle eofBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        LlvmApi.PositionBuilderAtEnd(builder, loopBlock);
        LlvmValueHandle curPos = LlvmApi.BuildLoad2(builder, state.I64, fposSlot, "fline_rpos");
        LlvmValueHandle curLen = LlvmApi.BuildLoad2(builder, state.I64, flenSlot, "fline_rlen");
        LlvmValueHandle exhausted = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, curPos, curLen, "fline_buf_exhausted");
        LlvmApi.BuildCondBr(builder, exhausted, refillBlock, haveByteBlock);

        // refill: one block read into the shared buffer from the handle's fd. n <= 0 means EOF/error.
        LlvmApi.PositionBuilderAtEnd(builder, refillBlock);
        LlvmValueHandle refilled;
        if (IsLinuxFlavor(state.Flavor))
        {
            refilled = EmitLinuxSyscall(
                state,
                SyscallRead,
                handleVal,
                LlvmApi.BuildPtrToInt(builder, fbufPtr, state.I64, "fline_rbuf_ptr_int"),
                LlvmApi.ConstInt(state.I64, StdinReadBufSize, 0),
                "sys_fline_block");
        }
        else
        {
            LlvmValueHandle readOk = EmitWindowsReadFile(state, handleVal, fbufPtr, LlvmApi.ConstInt(state.I32, StdinReadBufSize, 0), bytesReadSlot, "fline_read");
            LlvmValueHandle bytesRead = LlvmApi.BuildZExt(builder, LlvmApi.BuildLoad2(builder, state.I32, bytesReadSlot, "fline_bytes_val"), state.I64, "fline_bytes_i64");
            refilled = LlvmApi.BuildSelect(builder, readOk, bytesRead, LlvmApi.ConstInt(state.I64, 0, 0), "fline_n");
        }

        LlvmApi.BuildStore(builder, refilled, flenSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), fposSlot);
        LlvmValueHandle refilledEmpty = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, refilled, LlvmApi.ConstInt(state.I64, 0, 0), "fline_refill_empty");
        LlvmApi.BuildCondBr(builder, refilledEmpty, eofBlock, haveByteBlock);
    }

    private static void EmitFileReadLineScan(
        LlvmCodegenState state,
        LlvmValueHandle fbufPtr,
        LlvmValueHandle lineBufPtr,
        LlvmValueHandle byteSlot,
        LlvmValueHandle lenSlot,
        LlvmValueHandle fposSlot,
        LlvmBasicBlockHandle loopBlock,
        LlvmBasicBlockHandle haveByteBlock,
        LlvmBasicBlockHandle inspectBlock,
        LlvmBasicBlockHandle skipCrBlock,
        LlvmBasicBlockHandle storeByteBlock,
        LlvmBasicBlockHandle appendByteBlock,
        LlvmBasicBlockHandle finishSomeBlock,
        LlvmBasicBlockHandle overflowBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        LlvmApi.PositionBuilderAtEnd(builder, haveByteBlock);
        LlvmValueHandle takePos = LlvmApi.BuildLoad2(builder, state.I64, fposSlot, "fline_take_pos");
        LlvmValueHandle takePtr = LlvmApi.BuildGEP2(builder, state.I8, fbufPtr, [takePos], "fline_take_ptr");
        LlvmValueHandle takenByte = LlvmApi.BuildLoad2(builder, state.I8, takePtr, "fline_taken_byte");
        LlvmApi.BuildStore(builder, takenByte, byteSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, takePos, LlvmApi.ConstInt(state.I64, 1, 0), "fline_take_pos_next"), fposSlot);
        LlvmApi.BuildBr(builder, inspectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, inspectBlock);
        LlvmValueHandle currentByte = LlvmApi.BuildLoad2(builder, state.I8, byteSlot, "fline_current_byte");
        LlvmValueHandle isLf = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, currentByte, LlvmApi.ConstInt(state.I8, 10, 0), "fline_is_lf");
        LlvmApi.BuildCondBr(builder, isLf, finishSomeBlock, skipCrBlock);

        LlvmApi.PositionBuilderAtEnd(builder, skipCrBlock);
        LlvmValueHandle isCr = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, currentByte, LlvmApi.ConstInt(state.I8, 13, 0), "fline_is_cr");
        LlvmApi.BuildCondBr(builder, isCr, loopBlock, storeByteBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storeByteBlock);
        LlvmValueHandle currentLen = LlvmApi.BuildLoad2(builder, state.I64, lenSlot, "fline_len_value");
        LlvmValueHandle atCapacity = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, currentLen, LlvmApi.ConstInt(state.I64, InputBufSize, 0), "fline_at_capacity");
        LlvmApi.BuildCondBr(builder, atCapacity, overflowBlock, appendByteBlock);

        LlvmApi.PositionBuilderAtEnd(builder, appendByteBlock);
        LlvmValueHandle destPtr = LlvmApi.BuildGEP2(builder, state.I8, lineBufPtr, [currentLen], "fline_dest_ptr");
        LlvmApi.BuildStore(builder, currentByte, destPtr);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, currentLen, LlvmApi.ConstInt(state.I64, 1, 0), "fline_len_next"), lenSlot);
        LlvmApi.BuildBr(builder, loopBlock);
    }

    private static void EmitFileReadLineFinish(
        LlvmCodegenState state,
        LlvmValueHandle lineBufPtr,
        LlvmValueHandle lenSlot,
        LlvmValueHandle resultSlot,
        LlvmBasicBlockHandle eofBlock,
        LlvmBasicBlockHandle finishSomeBlock,
        LlvmBasicBlockHandle returnNoneBlock,
        LlvmBasicBlockHandle overflowBlock,
        LlvmBasicBlockHandle continueBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        LlvmApi.PositionBuilderAtEnd(builder, eofBlock);
        LlvmValueHandle lenAtEof = LlvmApi.BuildLoad2(builder, state.I64, lenSlot, "fline_len_at_eof");
        LlvmValueHandle isEmpty = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, lenAtEof, LlvmApi.ConstInt(state.I64, 0, 0), "fline_is_empty");
        LlvmApi.BuildCondBr(builder, isEmpty, returnNoneBlock, finishSomeBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finishSomeBlock);
        LlvmValueHandle finalLen = LlvmApi.BuildLoad2(builder, state.I64, lenSlot, "fline_final_len");
        LlvmValueHandle stringRef = EmitAllocDynamic(state, LlvmApi.BuildAdd(builder, finalLen, LlvmApi.ConstInt(state.I64, 8, 0), "fline_string_bytes"));
        StoreMemory(state, stringRef, 0, finalLen, "fline_string_len");
        EmitCopyBytes(state, GetStringBytesPointer(state, stringRef, "fline_string_dest"), lineBufPtr, finalLen, "fline_copy_bytes");
        LlvmValueHandle someRef = EmitAllocAdt(state, 1, 1);
        StoreMemory(state, someRef, 8, stringRef, "fline_some_value");
        LlvmApi.BuildStore(builder, someRef, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, returnNoneBlock);
        LlvmApi.BuildStore(builder, EmitAllocAdt(state, 0, 0), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, overflowBlock);
        EmitPanic(state, EmitStackStringObject(state, "Ashes.File.readLine input line too long"));
    }

    private static LlvmValueHandle EmitFileClose(LlvmCodegenState state, LlvmValueHandle handleVal)
    {
        EmitFileHandleClose(state, handleVal);
        return EmitResultOk(state, EmitUnitValue(state));
    }

    // Fire-and-forget close of the underlying fd/HANDLE. Used by both Ashes.File.close and the
    // automatic resource Drop at scope exit.
    private static void EmitFileHandleClose(LlvmCodegenState state, LlvmValueHandle handleVal)
    {
        if (IsLinuxFlavor(state.Flavor))
        {
            EmitLinuxSyscall(state, SyscallClose, handleVal, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), "fclose_call");
        }
        else
        {
            EmitWindowsCloseHandle(state, handleVal, "fclose_call");
        }
    }

    private static LlvmValueHandle EmitFileWriteBytes(LlvmCodegenState state, LlvmValueHandle pathRef, LlvmValueHandle bytesRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle byteCount = LoadStringLength(state, bytesRef, "fwb_len");
        LlvmValueHandle dataAddress = LlvmApi.BuildAdd(builder, bytesRef, LlvmApi.ConstInt(state.I64, 8, 0), "fwb_data_addr");
        return IsLinuxFlavor(state.Flavor)
            ? EmitLinuxFileWriteBytes(state, pathRef, dataAddress, byteCount)
            : EmitWindowsFileWriteBytes(state, pathRef, dataAddress, byteCount);
    }
}
