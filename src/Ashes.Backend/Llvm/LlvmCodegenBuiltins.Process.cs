using Ashes.Semantics;
using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{

    // Process struct layout: {stdin_fd:i64@0, stdout_fd:i64@8, stderr_fd:i64@16, pid:i64@24}
    private static LlvmValueHandle LoadProcessField(LlvmCodegenState state, LlvmValueHandle processRef, int offset, string name)
    {
        return LoadMemory(state, processRef, offset, name);
    }

    /// <summary>
    /// Releases the OS resources a <c>Process</c> owns when it is dropped: closes the three pipe
    /// fds/handles and reaps the child. Linux uses a non-blocking <c>waitpid(WNOHANG)</c> so a
    /// still-running child is not waited on (it detaches and is reaped by init on program exit);
    /// already-exited children are reaped here so they don't linger as zombies.
    /// </summary>
    private static void EmitProcessDrop(LlvmCodegenState state, LlvmValueHandle processRef)
    {
        EmitFileHandleClose(state, LoadProcessField(state, processRef, 0, "proc_drop_stdin"));
        EmitFileHandleClose(state, LoadProcessField(state, processRef, 8, "proc_drop_stdout"));
        EmitFileHandleClose(state, LoadProcessField(state, processRef, 16, "proc_drop_stderr"));
        LlvmValueHandle pid = LoadProcessField(state, processRef, 24, "proc_drop_pid");

        if (IsLinuxFlavor(state.Flavor))
        {
            // waitpid(pid, NULL, WNOHANG=1, NULL) — non-blocking reap of an exited child.
            EmitLinuxSyscall4(state, SyscallWaitpid, pid,
                LlvmApi.ConstInt(state.I64, 0, 0),
                LlvmApi.ConstInt(state.I64, 1, 0),
                LlvmApi.ConstInt(state.I64, 0, 0), "proc_drop_reap");
        }
        else
        {
            // On Windows the pid field is the process HANDLE; closing it releases the kernel object.
            EmitWindowsCloseHandle(state, pid, "proc_drop_handle");
        }
    }

    private static LlvmValueHandle EmitAllocProcessStruct(LlvmCodegenState state)
    {
        return EmitAllocDynamic(state, LlvmApi.ConstInt(state.I64, 32, 0));
    }

    // Ashes.Process.spawn(exe)(args): Result(Str, Process)
    private static LlvmValueHandle EmitSpawnProcess(LlvmCodegenState state, LlvmValueHandle exeRef, LlvmValueHandle argsRef)
    {
        return IsLinuxFlavor(state.Flavor)
            ? EmitLinuxSpawnProcess(state, exeRef, argsRef)
            : EmitWindowsSpawnProcess(state, exeRef, argsRef);
    }

    private static LlvmValueHandle EmitLinuxSpawnProcess(LlvmCodegenState state, LlvmValueHandle exeRef, LlvmValueHandle argsRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        const int MaxArgs = 256;

        // Allocate pipe fd arrays on stack: each is 2 x i32 (pipe2 writes int[2])
        LlvmTypeHandle pipeArrayType = LlvmApi.ArrayType2(state.I32, 2);
        LlvmValueHandle stdinPipe = LlvmApi.BuildAlloca(builder, pipeArrayType, "spawn_stdin_pipe");
        LlvmValueHandle stdoutPipe = LlvmApi.BuildAlloca(builder, pipeArrayType, "spawn_stdout_pipe");
        LlvmValueHandle stderrPipe = LlvmApi.BuildAlloca(builder, pipeArrayType, "spawn_stderr_pipe");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "spawn_result");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

        // Build argv array: argv[0] = exe, argv[1..] = args list, argv[n] = null
        LlvmTypeHandle argvArrayType = LlvmApi.ArrayType2(state.I64, MaxArgs + 2);
        LlvmValueHandle argvArray = LlvmApi.BuildAlloca(builder, argvArrayType, "spawn_argv");
        LlvmValueHandle argcSlot = LlvmApi.BuildAlloca(builder, state.I64, "spawn_argc");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), argcSlot);

        // argv[0] = exe as cstring
        LlvmValueHandle exeCstr = EmitStringToCString(state, exeRef, "spawn_exe_cstr");
        LlvmValueHandle argvBase = LlvmApi.BuildPtrToInt(builder, argvArray, state.I64, "spawn_argv_base");
        StoreMemory(state, argvBase, 0, LlvmApi.BuildPtrToInt(builder, exeCstr, state.I64, "spawn_exe_ptr"), "spawn_argv0");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), argcSlot);

        // Traverse args list and append to argv
        LlvmValueHandle listCursorSlot = LlvmApi.BuildAlloca(builder, state.I64, "spawn_list_cursor");
        LlvmApi.BuildStore(builder, argsRef, listCursorSlot);

        var listLoopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "spawn_list_loop");
        var listBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "spawn_list_body");
        var listDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "spawn_list_done");
        var listTooManyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "spawn_list_too_many");

        LlvmApi.BuildBr(builder, listLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, listLoopBlock);
        LlvmValueHandle cursor = LlvmApi.BuildLoad2(builder, state.I64, listCursorSlot, "spawn_cursor");
        // List Nil = pointer 0; Cons cell = {head:i64@0, tail:i64@8}
        LlvmValueHandle isNil = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, cursor, LlvmApi.ConstInt(state.I64, 0, 0), "spawn_is_nil");
        LlvmApi.BuildCondBr(builder, isNil, listDoneBlock, listBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, listBodyBlock);
        LlvmValueHandle argc = LlvmApi.BuildLoad2(builder, state.I64, argcSlot, "spawn_argc_val");
        LlvmValueHandle tooMany = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, argc, LlvmApi.ConstInt(state.I64, MaxArgs, 0), "spawn_too_many");
        var listAppendBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "spawn_list_append");
        LlvmApi.BuildCondBr(builder, tooMany, listTooManyBlock, listAppendBlock);

        LlvmApi.PositionBuilderAtEnd(builder, listAppendBlock);
        LlvmValueHandle headRef = LoadMemory(state, cursor, 0, "spawn_head");
        LlvmValueHandle headCstr = EmitStringToCString(state, headRef, "spawn_arg_cstr");
        // argvArray[argc] = headCstr: store via base pointer + offset
        LlvmApi.BuildStore(builder, LlvmApi.BuildPtrToInt(builder, headCstr, state.I64, "spawn_arg_ptr"), LlvmApi.BuildIntToPtr(builder, LlvmApi.BuildAdd(builder, argvBase, LlvmApi.BuildMul(builder, argc, LlvmApi.ConstInt(state.I64, 8, 0), "spawn_off"), "spawn_slot_addr"), state.I8Ptr, "spawn_slot_ptr"));
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, argc, LlvmApi.ConstInt(state.I64, 1, 0), "spawn_argc_next"), argcSlot);
        LlvmValueHandle tail = LoadMemory(state, cursor, 8, "spawn_tail");
        LlvmApi.BuildStore(builder, tail, listCursorSlot);
        LlvmApi.BuildBr(builder, listLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, listTooManyBlock);
        EmitPanic(state, EmitStackStringObject(state, "Process.spawn: too many arguments"));

        LlvmApi.PositionBuilderAtEnd(builder, listDoneBlock);
        // argv[argc] = null
        LlvmValueHandle finalArgc = LlvmApi.BuildLoad2(builder, state.I64, argcSlot, "spawn_final_argc");
        // argv[finalArgc] = null sentinel
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.BuildIntToPtr(builder, LlvmApi.BuildAdd(builder, argvBase, LlvmApi.BuildMul(builder, finalArgc, LlvmApi.ConstInt(state.I64, 8, 0), "spawn_null_off"), "spawn_null_addr"), state.I8Ptr, "spawn_null_ptr"));

        // pipe2 for stdin, stdout, stderr
        LlvmValueHandle stdinPipePtr = LlvmApi.BuildPtrToInt(builder, stdinPipe, state.I64, "spawn_stdin_pipe_ptr");
        LlvmValueHandle stdoutPipePtr = LlvmApi.BuildPtrToInt(builder, stdoutPipe, state.I64, "spawn_stdout_pipe_ptr");
        LlvmValueHandle stderrPipePtr = LlvmApi.BuildPtrToInt(builder, stderrPipe, state.I64, "spawn_stderr_pipe_ptr");
        EmitLinuxSyscall(state, SyscallPipe2, stdinPipePtr, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), "spawn_pipe_stdin");
        EmitLinuxSyscall(state, SyscallPipe2, stdoutPipePtr, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), "spawn_pipe_stdout");
        EmitLinuxSyscall(state, SyscallPipe2, stderrPipePtr, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), "spawn_pipe_stderr");

        // Load pipe fds: pipe2 writes int[2] so each fd is i32; zero-extend to i64 for syscalls.
        LlvmValueHandle stdinReadFd = LlvmApi.BuildZExt(builder, LlvmApi.BuildLoad2(builder, state.I32, GetArrayElementPointer(state, pipeArrayType, stdinPipe, LlvmApi.ConstInt(state.I64, 0, 0), "spawn_stdin_r_ptr"), "spawn_stdin_read_fd_i32"), state.I64, "spawn_stdin_read_fd");
        LlvmValueHandle stdinWriteFd = LlvmApi.BuildZExt(builder, LlvmApi.BuildLoad2(builder, state.I32, GetArrayElementPointer(state, pipeArrayType, stdinPipe, LlvmApi.ConstInt(state.I64, 1, 0), "spawn_stdin_w_ptr"), "spawn_stdin_write_fd_i32"), state.I64, "spawn_stdin_write_fd");
        LlvmValueHandle stdoutReadFd = LlvmApi.BuildZExt(builder, LlvmApi.BuildLoad2(builder, state.I32, GetArrayElementPointer(state, pipeArrayType, stdoutPipe, LlvmApi.ConstInt(state.I64, 0, 0), "spawn_stdout_r_ptr"), "spawn_stdout_read_fd_i32"), state.I64, "spawn_stdout_read_fd");
        LlvmValueHandle stdoutWriteFd = LlvmApi.BuildZExt(builder, LlvmApi.BuildLoad2(builder, state.I32, GetArrayElementPointer(state, pipeArrayType, stdoutPipe, LlvmApi.ConstInt(state.I64, 1, 0), "spawn_stdout_w_ptr"), "spawn_stdout_write_fd_i32"), state.I64, "spawn_stdout_write_fd");
        LlvmValueHandle stderrReadFd = LlvmApi.BuildZExt(builder, LlvmApi.BuildLoad2(builder, state.I32, GetArrayElementPointer(state, pipeArrayType, stderrPipe, LlvmApi.ConstInt(state.I64, 0, 0), "spawn_stderr_r_ptr"), "spawn_stderr_read_fd_i32"), state.I64, "spawn_stderr_read_fd");
        LlvmValueHandle stderrWriteFd = LlvmApi.BuildZExt(builder, LlvmApi.BuildLoad2(builder, state.I32, GetArrayElementPointer(state, pipeArrayType, stderrPipe, LlvmApi.ConstInt(state.I64, 1, 0), "spawn_stderr_w_ptr"), "spawn_stderr_write_fd_i32"), state.I64, "spawn_stderr_write_fd");

        // fork() on x86-64 / clone(SIGCHLD, 0, 0) on arm64.
        // On arm64, SyscallFork maps to clone(); flags=SIGCHLD(17) is required for
        // the child to be wait()-able as a normal child process.
        // On x86-64, fork() ignores all argument registers, so passing 17 is harmless.
        LlvmValueHandle pid = EmitLinuxSyscall(state, SyscallFork, LlvmApi.ConstInt(state.I64, 17, 0), LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), "spawn_fork");
        LlvmValueHandle isChild = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, pid, LlvmApi.ConstInt(state.I64, 0, 0), "spawn_is_child");
        LlvmValueHandle forkFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, pid, LlvmApi.ConstInt(state.I64, 0, 0), "spawn_fork_failed");

        var childBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "spawn_child");
        var parentCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "spawn_parent_check");
        var parentBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "spawn_parent");
        var forkFailedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "spawn_fork_failed");
        var spawnDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "spawn_done");

        LlvmApi.BuildCondBr(builder, isChild, childBlock, parentCheckBlock);

        // Child process
        LlvmApi.PositionBuilderAtEnd(builder, childBlock);
        EmitLinuxSyscall(state, SyscallDup2, stdinReadFd, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), "spawn_child_dup_stdin");
        EmitLinuxSyscall(state, SyscallDup2, stdoutWriteFd, LlvmApi.ConstInt(state.I64, 1, 0), LlvmApi.ConstInt(state.I64, 0, 0), "spawn_child_dup_stdout");
        EmitLinuxSyscall(state, SyscallDup2, stderrWriteFd, LlvmApi.ConstInt(state.I64, 2, 0), LlvmApi.ConstInt(state.I64, 0, 0), "spawn_child_dup_stderr");
        EmitLinuxSyscall(state, SyscallClose, stdinReadFd, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), "spawn_child_close_stdin_r");
        EmitLinuxSyscall(state, SyscallClose, stdinWriteFd, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), "spawn_child_close_stdin_w");
        EmitLinuxSyscall(state, SyscallClose, stdoutReadFd, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), "spawn_child_close_stdout_r");
        EmitLinuxSyscall(state, SyscallClose, stdoutWriteFd, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), "spawn_child_close_stdout_w");
        EmitLinuxSyscall(state, SyscallClose, stderrReadFd, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), "spawn_child_close_stderr_r");
        EmitLinuxSyscall(state, SyscallClose, stderrWriteFd, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), "spawn_child_close_stderr_w");
        // execve(exe_cstr, argv_ptr, null_envp)
        LlvmValueHandle envpNull = LlvmApi.ConstInt(state.I64, 0, 0);
        EmitLinuxSyscall(state, SyscallExecve,
            LlvmApi.BuildPtrToInt(builder, exeCstr, state.I64, "spawn_exe_int"),
            argvBase,
            envpNull,
            "spawn_execve");
        // execve failed - _exit(1)
        EmitLinuxSyscall(state, SyscallExit, LlvmApi.ConstInt(state.I64, 1, 0), LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), "spawn_child_exit");
        LlvmApi.BuildUnreachable(builder);

        // Parent
        LlvmApi.PositionBuilderAtEnd(builder, parentCheckBlock);
        LlvmApi.BuildCondBr(builder, forkFailed, forkFailedBlock, parentBlock);

        LlvmApi.PositionBuilderAtEnd(builder, forkFailedBlock);
        LlvmValueHandle forkErrRef = EmitStackStringObject(state, "Process.spawn: fork failed");
        LlvmApi.BuildStore(builder, EmitResultError(state, forkErrRef), resultSlot);
        LlvmApi.BuildBr(builder, spawnDoneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parentBlock);
        // Close child-side pipe ends
        EmitLinuxSyscall(state, SyscallClose, stdinReadFd, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), "spawn_parent_close_stdin_r");
        EmitLinuxSyscall(state, SyscallClose, stdoutWriteFd, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), "spawn_parent_close_stdout_w");
        EmitLinuxSyscall(state, SyscallClose, stderrWriteFd, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), "spawn_parent_close_stderr_w");
        // Build process struct {stdinWriteFd, stdoutReadFd, stderrReadFd, pid}
        LlvmValueHandle procRef = EmitAllocProcessStruct(state);
        StoreMemory(state, procRef, 0, stdinWriteFd, "spawn_proc_stdin");
        StoreMemory(state, procRef, 8, stdoutReadFd, "spawn_proc_stdout");
        StoreMemory(state, procRef, 16, stderrReadFd, "spawn_proc_stderr");
        StoreMemory(state, procRef, 24, pid, "spawn_proc_pid");
        LlvmApi.BuildStore(builder, EmitResultOk(state, procRef), resultSlot);
        LlvmApi.BuildBr(builder, spawnDoneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, spawnDoneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "spawn_result_val");
    }

    private static LlvmValueHandle EmitWindowsSpawnProcess(LlvmCodegenState state, LlvmValueHandle exeRef, LlvmValueHandle argsRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        const int CmdBufSize = 4096;
        const int StartupInfoASize = 104;
        const int ProcessInfoSize = 24;
        // SECURITY_ATTRIBUTES: nLength(4)+pad(4)+lpSecurityDescriptor(8)+bInheritHandle(4)+pad(4) = 24
        const int SecurityAttrSize = 24;
        const uint StartfUsestdhandles = 0x100;

        LlvmValueHandle nullPtr = LlvmApi.ConstNull(state.I8Ptr);
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "spawn_w_result");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

        // SECURITY_ATTRIBUTES with bInheritHandle = TRUE for pipe handles
        LlvmTypeHandle saType = LlvmApi.ArrayType2(state.I8, SecurityAttrSize);
        LlvmValueHandle saBuf = LlvmApi.BuildAlloca(builder, saType, "spawn_w_sa");
        LlvmValueHandle saPtr = GetArrayElementPointer(state, saType, saBuf, LlvmApi.ConstInt(state.I64, 0, 0), "spawn_w_sa_ptr");
        for (int zi = 0; zi < SecurityAttrSize / 8; zi++)
            LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.BuildBitCast(builder, LlvmApi.BuildGEP2(builder, state.I8, saPtr, [LlvmApi.ConstInt(state.I64, (ulong)(zi * 8), 0)], $"spawn_w_sa_z{zi}"), state.I64Ptr, $"spawn_w_sa_z{zi}p"));
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 20, 0), LlvmApi.BuildBitCast(builder, saPtr, state.I32Ptr, "spawn_w_sa_len"));
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 1, 0), LlvmApi.BuildBitCast(builder, LlvmApi.BuildGEP2(builder, state.I8, saPtr, [LlvmApi.ConstInt(state.I64, 16, 0)], "spawn_w_sa_inh_off"), state.I32Ptr, "spawn_w_sa_inh"));

        // Pipe handle slots (i64 each = HANDLE)
        LlvmValueHandle stdinReadSlot = LlvmApi.BuildAlloca(builder, state.I64, "spawn_w_stdin_r");
        LlvmValueHandle stdinWriteSlot = LlvmApi.BuildAlloca(builder, state.I64, "spawn_w_stdin_w");
        LlvmValueHandle stdoutReadSlot = LlvmApi.BuildAlloca(builder, state.I64, "spawn_w_stdout_r");
        LlvmValueHandle stdoutWriteSlot = LlvmApi.BuildAlloca(builder, state.I64, "spawn_w_stdout_w");
        LlvmValueHandle stderrReadSlot = LlvmApi.BuildAlloca(builder, state.I64, "spawn_w_stderr_r");
        LlvmValueHandle stderrWriteSlot = LlvmApi.BuildAlloca(builder, state.I64, "spawn_w_stderr_w");

        // CreatePipe(hRead, hWrite, lpPipeAttributes, nSize)
        LlvmTypeHandle createPipeType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, state.I8Ptr, state.I32]);
        LlvmValueHandle createPipeFn = LlvmApi.BuildLoad2(builder, LlvmApi.PointerTypeInContext(state.Target.Context, 0), state.WindowsCreatePipeImport, "spawn_w_pipe_fn");
        LlvmApi.BuildCall2(builder, createPipeType, createPipeFn, [LlvmApi.BuildBitCast(builder, stdinReadSlot, state.I8Ptr, "spawn_w_sr_p"), LlvmApi.BuildBitCast(builder, stdinWriteSlot, state.I8Ptr, "spawn_w_sw_p"), saPtr, LlvmApi.ConstInt(state.I32, 0, 0)], "spawn_w_stdin_pipe");
        LlvmApi.BuildCall2(builder, createPipeType, createPipeFn, [LlvmApi.BuildBitCast(builder, stdoutReadSlot, state.I8Ptr, "spawn_w_or_p"), LlvmApi.BuildBitCast(builder, stdoutWriteSlot, state.I8Ptr, "spawn_w_ow_p"), saPtr, LlvmApi.ConstInt(state.I32, 0, 0)], "spawn_w_stdout_pipe");
        LlvmApi.BuildCall2(builder, createPipeType, createPipeFn, [LlvmApi.BuildBitCast(builder, stderrReadSlot, state.I8Ptr, "spawn_w_er_p"), LlvmApi.BuildBitCast(builder, stderrWriteSlot, state.I8Ptr, "spawn_w_ew_p"), saPtr, LlvmApi.ConstInt(state.I32, 0, 0)], "spawn_w_stderr_pipe");

        LlvmValueHandle stdinReadHandle = LlvmApi.BuildLoad2(builder, state.I64, stdinReadSlot, "spawn_w_stdin_rh");
        LlvmValueHandle stdinWriteHandle = LlvmApi.BuildLoad2(builder, state.I64, stdinWriteSlot, "spawn_w_stdin_wh");
        LlvmValueHandle stdoutReadHandle = LlvmApi.BuildLoad2(builder, state.I64, stdoutReadSlot, "spawn_w_stdout_rh");
        LlvmValueHandle stdoutWriteHandle = LlvmApi.BuildLoad2(builder, state.I64, stdoutWriteSlot, "spawn_w_stdout_wh");
        LlvmValueHandle stderrReadHandle = LlvmApi.BuildLoad2(builder, state.I64, stderrReadSlot, "spawn_w_stderr_rh");
        LlvmValueHandle stderrWriteHandle = LlvmApi.BuildLoad2(builder, state.I64, stderrWriteSlot, "spawn_w_stderr_wh");

        // Build command line: "exe arg1 arg2 ..." into a stack buffer
        LlvmTypeHandle cmdBufType = LlvmApi.ArrayType2(state.I8, CmdBufSize);
        LlvmValueHandle cmdBuf = LlvmApi.BuildAlloca(builder, cmdBufType, "spawn_w_cmd_buf");
        LlvmValueHandle cmdPtr = GetArrayElementPointer(state, cmdBufType, cmdBuf, LlvmApi.ConstInt(state.I64, 0, 0), "spawn_w_cmd_ptr");
        LlvmValueHandle cmdLenSlot = LlvmApi.BuildAlloca(builder, state.I64, "spawn_w_cmd_len");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), cmdLenSlot);
        EmitAppendToCmdBuf(state, cmdPtr, cmdLenSlot, exeRef, CmdBufSize);

        LlvmValueHandle argCursorSlot = LlvmApi.BuildAlloca(builder, state.I64, "spawn_w_arg_cursor");
        LlvmApi.BuildStore(builder, argsRef, argCursorSlot);
        var argLoopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "spawn_w_arg_loop");
        var argBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "spawn_w_arg_body");
        var argDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "spawn_w_arg_done");
        LlvmApi.BuildBr(builder, argLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, argLoopBlock);
        LlvmValueHandle argCursor = LlvmApi.BuildLoad2(builder, state.I64, argCursorSlot, "spawn_w_arg_cursor_val");
        // List Nil = pointer 0; Cons cell = {head:i64@0, tail:i64@8}
        LlvmValueHandle argIsNil = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, argCursor, LlvmApi.ConstInt(state.I64, 0, 0), "spawn_w_arg_nil");
        LlvmApi.BuildCondBr(builder, argIsNil, argDoneBlock, argBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, argBodyBlock);
        LlvmValueHandle argCmdLen = LlvmApi.BuildLoad2(builder, state.I64, cmdLenSlot, "spawn_w_arg_cmd_len");
        LlvmValueHandle spaceDestPtr = LlvmApi.BuildGEP2(builder, state.I8, cmdPtr, [argCmdLen], "spawn_w_space_ptr");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I8, (byte)' ', 0), spaceDestPtr);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, argCmdLen, LlvmApi.ConstInt(state.I64, 1, 0), "spawn_w_cmd_len_space"), cmdLenSlot);
        LlvmValueHandle argHead = LoadMemory(state, argCursor, 0, "spawn_w_arg_head");
        EmitAppendToCmdBuf(state, cmdPtr, cmdLenSlot, argHead, CmdBufSize);
        LlvmValueHandle argTail = LoadMemory(state, argCursor, 8, "spawn_w_arg_tail");
        LlvmApi.BuildStore(builder, argTail, argCursorSlot);
        LlvmApi.BuildBr(builder, argLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, argDoneBlock);
        LlvmValueHandle finalCmdLen = LlvmApi.BuildLoad2(builder, state.I64, cmdLenSlot, "spawn_w_final_cmd_len");
        LlvmValueHandle nullTermPtr = LlvmApi.BuildGEP2(builder, state.I8, cmdPtr, [finalCmdLen], "spawn_w_null_term_ptr");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I8, 0, 0), nullTermPtr);

        // STARTUPINFOA (104 bytes, zero-initialized)
        LlvmTypeHandle siType = LlvmApi.ArrayType2(state.I8, StartupInfoASize);
        LlvmValueHandle siBuf = LlvmApi.BuildAlloca(builder, siType, "spawn_w_si");
        LlvmValueHandle siPtr = GetArrayElementPointer(state, siType, siBuf, LlvmApi.ConstInt(state.I64, 0, 0), "spawn_w_si_ptr");
        for (int zi = 0; zi < StartupInfoASize / 8; zi++)
            LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.BuildBitCast(builder, LlvmApi.BuildGEP2(builder, state.I8, siPtr, [LlvmApi.ConstInt(state.I64, (ulong)(zi * 8), 0)], $"spawn_w_si_z{zi}"), state.I64Ptr, $"spawn_w_si_z{zi}p"));
        // cb = 104 at offset 0 (i32)
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, StartupInfoASize, 0), LlvmApi.BuildBitCast(builder, siPtr, state.I32Ptr, "spawn_w_si_cb"));
        // dwFlags = STARTF_USESTDHANDLES at offset 60 (i32)
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, StartfUsestdhandles, 0), LlvmApi.BuildBitCast(builder, LlvmApi.BuildGEP2(builder, state.I8, siPtr, [LlvmApi.ConstInt(state.I64, 60, 0)], "spawn_w_si_flags_off"), state.I32Ptr, "spawn_w_si_flags"));
        // hStdInput at offset 80 (i64)
        LlvmApi.BuildStore(builder, stdinReadHandle, LlvmApi.BuildBitCast(builder, LlvmApi.BuildGEP2(builder, state.I8, siPtr, [LlvmApi.ConstInt(state.I64, 80, 0)], "spawn_w_si_sin_off"), state.I64Ptr, "spawn_w_si_sin"));
        // hStdOutput at offset 88 (i64)
        LlvmApi.BuildStore(builder, stdoutWriteHandle, LlvmApi.BuildBitCast(builder, LlvmApi.BuildGEP2(builder, state.I8, siPtr, [LlvmApi.ConstInt(state.I64, 88, 0)], "spawn_w_si_sout_off"), state.I64Ptr, "spawn_w_si_sout"));
        // hStdError at offset 96 (i64)
        LlvmApi.BuildStore(builder, stderrWriteHandle, LlvmApi.BuildBitCast(builder, LlvmApi.BuildGEP2(builder, state.I8, siPtr, [LlvmApi.ConstInt(state.I64, 96, 0)], "spawn_w_si_serr_off"), state.I64Ptr, "spawn_w_si_serr"));

        // PROCESS_INFORMATION (24 bytes, zero-initialized)
        LlvmTypeHandle piType = LlvmApi.ArrayType2(state.I8, ProcessInfoSize);
        LlvmValueHandle piBuf = LlvmApi.BuildAlloca(builder, piType, "spawn_w_pi");
        LlvmValueHandle piPtr = GetArrayElementPointer(state, piType, piBuf, LlvmApi.ConstInt(state.I64, 0, 0), "spawn_w_pi_ptr");
        for (int zi = 0; zi < ProcessInfoSize / 8; zi++)
            LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.BuildBitCast(builder, LlvmApi.BuildGEP2(builder, state.I8, piPtr, [LlvmApi.ConstInt(state.I64, (ulong)(zi * 8), 0)], $"spawn_w_pi_z{zi}"), state.I64Ptr, $"spawn_w_pi_z{zi}p"));

        // exe as cstring for lpApplicationName
        LlvmValueHandle exeCstr = EmitStringToCString(state, exeRef, "spawn_w_exe");

        // CreateProcessA(lpApplicationName, lpCommandLine, procAttr, threadAttr,
        //                bInheritHandles, dwCreationFlags, lpEnv, lpCurDir,
        //                lpStartupInfo, lpProcessInfo)
        LlvmTypeHandle createProcessType = LlvmApi.FunctionType(state.I32, [
            state.I8Ptr, state.I8Ptr, state.I8Ptr, state.I8Ptr,
            state.I32, state.I32, state.I8Ptr, state.I8Ptr,
            state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle createProcessFn = LlvmApi.BuildLoad2(builder, LlvmApi.PointerTypeInContext(state.Target.Context, 0), state.WindowsCreateProcessAImport, "spawn_w_cp_fn");
        LlvmValueHandle createResult = LlvmApi.BuildCall2(builder, createProcessType, createProcessFn, [
            LlvmApi.BuildBitCast(builder, exeCstr, state.I8Ptr, "spawn_w_exe_p"),
            cmdPtr,
            nullPtr, nullPtr,
            LlvmApi.ConstInt(state.I32, 1, 0),
            LlvmApi.ConstInt(state.I32, 0, 0),
            nullPtr, nullPtr,
            siPtr, piPtr], "spawn_w_create_proc");

        LlvmValueHandle createFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, createResult, LlvmApi.ConstInt(state.I32, 0, 0), "spawn_w_failed");
        var spawnOkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "spawn_w_ok");
        var spawnFailedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "spawn_w_failed_blk");
        var spawnDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "spawn_w_done");
        LlvmApi.BuildCondBr(builder, createFailed, spawnFailedBlock, spawnOkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, spawnFailedBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitStackStringObject(state, "Process.spawn: CreateProcessA failed")), resultSlot);
        LlvmApi.BuildBr(builder, spawnDoneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, spawnOkBlock);
        // hProcess = PROCESS_INFORMATION.hProcess at offset 0
        LlvmValueHandle hProcess = LlvmApi.BuildLoad2(builder, state.I64, LlvmApi.BuildBitCast(builder, piPtr, state.I64Ptr, "spawn_w_pi_hp"), "spawn_w_hprocess");
        // Close hThread (PROCESS_INFORMATION.hThread at offset 8) — not needed
        LlvmValueHandle hThreadPtr = LlvmApi.BuildBitCast(builder, LlvmApi.BuildGEP2(builder, state.I8, piPtr, [LlvmApi.ConstInt(state.I64, 8, 0)], "spawn_w_ht_off"), state.I64Ptr, "spawn_w_ht_ptr");
        EmitWindowsCloseHandle(state, LlvmApi.BuildLoad2(builder, state.I64, hThreadPtr, "spawn_w_hthread"), "spawn_w_close_thread");
        // Close child-side pipe ends (child will get them via STARTUPINFO)
        EmitWindowsCloseHandle(state, stdinReadHandle, "spawn_w_close_stdin_r");
        EmitWindowsCloseHandle(state, stdoutWriteHandle, "spawn_w_close_stdout_w");
        EmitWindowsCloseHandle(state, stderrWriteHandle, "spawn_w_close_stderr_w");
        // Build Process struct: {stdinWrite, stdoutRead, stderrRead, hProcess}
        LlvmValueHandle procRef = EmitAllocProcessStruct(state);
        StoreMemory(state, procRef, 0, stdinWriteHandle, "spawn_w_proc_stdin");
        StoreMemory(state, procRef, 8, stdoutReadHandle, "spawn_w_proc_stdout");
        StoreMemory(state, procRef, 16, stderrReadHandle, "spawn_w_proc_stderr");
        StoreMemory(state, procRef, 24, hProcess, "spawn_w_proc_pid");
        LlvmApi.BuildStore(builder, EmitResultOk(state, procRef), resultSlot);
        LlvmApi.BuildBr(builder, spawnDoneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, spawnDoneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "spawn_w_result_val");
    }

    // Appends the bytes of strRef to cmdBufPtr[*cmdLenSlot..], advancing cmdLenSlot.
    private static void EmitAppendToCmdBuf(LlvmCodegenState state, LlvmValueHandle cmdBufPtr, LlvmValueHandle cmdLenSlot, LlvmValueHandle strRef, int bufSize)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle strLen = LoadStringLength(state, strRef, "acb_str_len");
        LlvmValueHandle strBytes = GetStringBytesPointer(state, strRef, "acb_str_bytes");
        LlvmValueHandle cmdLen = LlvmApi.BuildLoad2(builder, state.I64, cmdLenSlot, "acb_cmd_len");
        LlvmValueHandle destPtr = LlvmApi.BuildGEP2(builder, state.I8, cmdBufPtr, [cmdLen], "acb_dest_ptr");
        EmitCopyBytes(state, destPtr, strBytes, strLen, "acb_copy");
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, cmdLen, strLen, "acb_new_len"), cmdLenSlot);
    }

    // Ashes.Process.writeStdin(proc)(text): Unit
    private static LlvmValueHandle EmitProcessWriteStdin(LlvmCodegenState state, LlvmValueHandle processRef, LlvmValueHandle textRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle stdinFd = LoadProcessField(state, processRef, 0, "proc_stdin_fd");
        LlvmValueHandle textLen = LoadStringLength(state, textRef, "proc_write_len");
        LlvmValueHandle textPtr = GetStringBytesPointer(state, textRef, "proc_write_ptr");
        LlvmValueHandle writtenSlot = LlvmApi.BuildAlloca(builder, state.I64, "proc_write_written");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), writtenSlot);

        LlvmValueHandle stdinHandle = default;
        LlvmValueHandle bytesWrittenSlot = default;
        if (state.Flavor == LlvmCodegenFlavor.WindowsX64)
        {
            stdinHandle = stdinFd;
            bytesWrittenSlot = LlvmApi.BuildAlloca(builder, state.I32, "proc_bytes_written");
        }

        var writeLoopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "proc_write_loop");
        var writeBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "proc_write_body");
        var writeDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "proc_write_done");

        LlvmApi.BuildBr(builder, writeLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, writeLoopBlock);
        LlvmValueHandle written = LlvmApi.BuildLoad2(builder, state.I64, writtenSlot, "proc_written_val");
        LlvmValueHandle allWritten = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, written, textLen, "proc_all_written");
        LlvmApi.BuildCondBr(builder, allWritten, writeDoneBlock, writeBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, writeBodyBlock);
        LlvmValueHandle cursorPtr = LlvmApi.BuildGEP2(builder, state.I8, textPtr, [written], "proc_write_cursor");
        LlvmValueHandle remaining = LlvmApi.BuildSub(builder, textLen, written, "proc_write_remaining");
        LlvmValueHandle nWritten;
        if (IsLinuxFlavor(state.Flavor))
        {
            nWritten = EmitLinuxSyscall(state, SyscallWrite, stdinFd,
                LlvmApi.BuildPtrToInt(builder, cursorPtr, state.I64, "proc_write_ptr_int"), remaining, "proc_write_call");
        }
        else
        {
            // EmitWindowsWriteFile returns a success flag (i1); the actual byte count
            // is written to bytesWrittenSlot. A failed write yields a non-positive count
            // so the loop terminates.
            LlvmValueHandle writeOk = EmitWindowsWriteFile(state, stdinHandle, cursorPtr,
                LlvmApi.BuildTrunc(builder, remaining, state.I32, "proc_write_remaining_i32"), bytesWrittenSlot, "proc_write_call");
            LlvmValueHandle bytesWritten = LlvmApi.BuildZExt(builder,
                LlvmApi.BuildLoad2(builder, state.I32, bytesWrittenSlot, "proc_write_bytes_val"), state.I64, "proc_write_bytes_i64");
            nWritten = LlvmApi.BuildSelect(builder, writeOk, bytesWritten, LlvmApi.ConstInt(state.I64, 0, 0), "proc_write_i64");
        }

        LlvmValueHandle writeFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, nWritten, LlvmApi.ConstInt(state.I64, 0, 0), "proc_write_failed");
        var writeOkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "proc_write_ok");
        LlvmApi.BuildCondBr(builder, writeFailed, writeDoneBlock, writeOkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, writeOkBlock);
        LlvmValueHandle newWritten = LlvmApi.BuildAdd(builder, written, nWritten, "proc_write_new_written");
        LlvmApi.BuildStore(builder, newWritten, writtenSlot);
        LlvmApi.BuildBr(builder, writeLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, writeDoneBlock);
        return EmitUnitValue(state);
    }

    // Ashes.Process.readStdoutLine / readStderrLine: Process -> Maybe(Str)
    private static LlvmValueHandle EmitProcessReadLine(LlvmCodegenState state, LlvmValueHandle processRef, bool stdoutFd)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // stdout_fd @ offset 8, stderr_fd @ offset 16
        int fieldOffset = stdoutFd ? 8 : 16;
        string prefix = stdoutFd ? "proc_rdout" : "proc_rderr";
        LlvmValueHandle fd = LoadProcessField(state, processRef, fieldOffset, prefix + "_fd");

        LlvmTypeHandle inputBufType = LlvmApi.ArrayType2(state.I8, InputBufSize);
        LlvmValueHandle inputBuf = LlvmApi.BuildAlloca(builder, inputBufType, prefix + "_buf");
        LlvmValueHandle inputBufPtr = GetArrayElementPointer(state, inputBufType, inputBuf, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_buf_ptr");
        LlvmValueHandle byteSlot = LlvmApi.BuildAlloca(builder, state.I8, prefix + "_byte");
        LlvmValueHandle lenSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_len");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_result");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), lenSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

        LlvmValueHandle fileHandle = default;
        LlvmValueHandle bytesReadSlot = default;
        if (state.Flavor == LlvmCodegenFlavor.WindowsX64)
        {
            fileHandle = fd;
            bytesReadSlot = LlvmApi.BuildAlloca(builder, state.I32, prefix + "_bytes_read");
        }

        var loopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_loop");
        var inspectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_inspect");
        var skipCrBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_skip_cr");
        var storeByteBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_store_byte");
        var appendByteBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_append_byte");
        var eofBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_eof");
        var finishSomeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_finish_some");
        var returnNoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_return_none");
        var overflowBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_overflow");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_continue");

        LlvmApi.BuildBr(builder, loopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBlock);
        LlvmValueHandle bytesRead = IsLinuxFlavor(state.Flavor)
            ? EmitLinuxSyscall(state, SyscallRead, fd,
                LlvmApi.BuildPtrToInt(builder, byteSlot, state.I64, prefix + "_byte_ptr"),
                LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_read_call")
            : LlvmApi.BuildSExt(builder, EmitWindowsReadFile(state, fileHandle, byteSlot,
                LlvmApi.ConstInt(state.I32, 1, 0), bytesReadSlot, prefix + "_read_call"), state.I64, prefix + "_read_i64");

        LlvmValueHandle hasByte = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, bytesRead, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_byte");
        LlvmApi.BuildCondBr(builder, hasByte, inspectBlock, eofBlock);

        LlvmApi.PositionBuilderAtEnd(builder, inspectBlock);
        LlvmValueHandle currentByte = LlvmApi.BuildLoad2(builder, state.I8, byteSlot, prefix + "_current_byte");
        LlvmValueHandle isLf = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, currentByte, LlvmApi.ConstInt(state.I8, 10, 0), prefix + "_is_lf");
        LlvmApi.BuildCondBr(builder, isLf, finishSomeBlock, skipCrBlock);

        LlvmApi.PositionBuilderAtEnd(builder, skipCrBlock);
        LlvmValueHandle isCr = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, currentByte, LlvmApi.ConstInt(state.I8, 13, 0), prefix + "_is_cr");
        LlvmApi.BuildCondBr(builder, isCr, loopBlock, storeByteBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storeByteBlock);
        LlvmValueHandle currentLen = LlvmApi.BuildLoad2(builder, state.I64, lenSlot, prefix + "_len_value");
        LlvmValueHandle atCapacity = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, currentLen, LlvmApi.ConstInt(state.I64, InputBufSize, 0), prefix + "_at_capacity");
        LlvmApi.BuildCondBr(builder, atCapacity, overflowBlock, appendByteBlock);

        LlvmApi.PositionBuilderAtEnd(builder, appendByteBlock);
        LlvmValueHandle destPtr = LlvmApi.BuildGEP2(builder, state.I8, inputBufPtr, [currentLen], prefix + "_dest_ptr");
        LlvmApi.BuildStore(builder, currentByte, destPtr);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, currentLen, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_len_next"), lenSlot);
        LlvmApi.BuildBr(builder, loopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, eofBlock);
        LlvmValueHandle lenAtEof = LlvmApi.BuildLoad2(builder, state.I64, lenSlot, prefix + "_len_at_eof");
        LlvmValueHandle isEmpty = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, lenAtEof, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_is_empty");
        LlvmApi.BuildCondBr(builder, isEmpty, returnNoneBlock, finishSomeBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finishSomeBlock);
        LlvmValueHandle finalLen = LlvmApi.BuildLoad2(builder, state.I64, lenSlot, prefix + "_final_len");
        LlvmValueHandle stringRef = EmitAllocDynamic(state, LlvmApi.BuildAdd(builder, finalLen, LlvmApi.ConstInt(state.I64, 8, 0), prefix + "_string_bytes"));
        StoreMemory(state, stringRef, 0, finalLen, prefix + "_string_len");
        EmitCopyBytes(state, GetStringBytesPointer(state, stringRef, prefix + "_string_dest"), inputBufPtr, finalLen, prefix + "_copy_bytes");
        LlvmValueHandle someRef = EmitAllocAdt(state, 1, 1);
        StoreMemory(state, someRef, 8, stringRef, prefix + "_some_value");
        LlvmApi.BuildStore(builder, someRef, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, returnNoneBlock);
        LlvmApi.BuildStore(builder, EmitAllocAdt(state, 0, 0), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, overflowBlock);
        EmitPanic(state, EmitStackStringObject(state, "Process.readLine: line too long"));

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, prefix + "_result_value");
    }

    // Ashes.Process.waitForExit(proc): Int (exit code)
    private static LlvmValueHandle EmitProcessWaitForExit(LlvmCodegenState state, LlvmValueHandle processRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle pid = LoadProcessField(state, processRef, 24, "proc_wait_pid");

        if (IsLinuxFlavor(state.Flavor))
        {
            LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, "proc_wait_status");
            LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), statusSlot);
            // Use the 4-arg form so the rusage pointer (arg4) is explicitly NULL on
            // arm64 where SyscallWaitpid maps to wait4(pid, status, opts, rusage).
            EmitLinuxSyscall4(state, SyscallWaitpid, pid,
                LlvmApi.BuildPtrToInt(builder, statusSlot, state.I64, "proc_wait_status_ptr"),
                LlvmApi.ConstInt(state.I64, 0, 0),
                LlvmApi.ConstInt(state.I64, 0, 0), "proc_waitpid");
            LlvmValueHandle status = LlvmApi.BuildLoad2(builder, state.I64, statusSlot, "proc_wait_status_val");
            // WEXITSTATUS: (status >> 8) & 0xff
            LlvmValueHandle shifted = LlvmApi.BuildLShr(builder, status, LlvmApi.ConstInt(state.I64, 8, 0), "proc_exit_shifted");
            return LlvmApi.BuildAnd(builder, shifted, LlvmApi.ConstInt(state.I64, 0xff, 0), "proc_exit_code");
        }
        else
        {
            // Windows: WaitForSingleObject(handle, INFINITE=0xFFFFFFFF)
            LlvmTypeHandle waitType = LlvmApi.FunctionType(state.I32, [state.I64, state.I32]);
            LlvmValueHandle waitPtr = LlvmApi.BuildLoad2(builder,
                LlvmApi.PointerTypeInContext(state.Target.Context, 0),
                state.WindowsWaitForSingleObjectImport, "proc_wait_fn_ptr");
            LlvmApi.BuildCall2(builder, waitType, waitPtr,
                [pid, LlvmApi.ConstInt(state.I32, unchecked((uint)-1), 0)], "proc_wait_call");
            // GetExitCodeProcess(handle, &exitCode) -> exitCode (i32)
            LlvmValueHandle exitCodeSlot = LlvmApi.BuildAlloca(builder, state.I32, "proc_exit_code_slot");
            LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), exitCodeSlot);
            LlvmTypeHandle getExitCodeType = LlvmApi.FunctionType(state.I32, [state.I64, state.I32Ptr]);
            LlvmValueHandle getExitCodePtr = LlvmApi.BuildLoad2(builder,
                LlvmApi.PointerTypeInContext(state.Target.Context, 0),
                state.WindowsGetExitCodeProcessImport, "proc_exit_fn_ptr");
            LlvmApi.BuildCall2(builder, getExitCodeType, getExitCodePtr,
                [pid, exitCodeSlot], "proc_exit_call");
            LlvmValueHandle exitCode = LlvmApi.BuildLoad2(builder, state.I32, exitCodeSlot, "proc_exit_code_val");
            return LlvmApi.BuildZExt(builder, exitCode, state.I64, "proc_exit_code_i64");
        }
    }

    // Ashes.Process.kill(proc): Unit
    private static LlvmValueHandle EmitProcessKill(LlvmCodegenState state, LlvmValueHandle processRef)
    {
        LlvmValueHandle pid = LoadProcessField(state, processRef, 24, "proc_kill_pid");

        if (IsLinuxFlavor(state.Flavor))
        {
            // SIGTERM = 15
            EmitLinuxSyscall(state, SyscallKill, pid, LlvmApi.ConstInt(state.I64, 15, 0), LlvmApi.ConstInt(state.I64, 0, 0), "proc_kill_call");
        }
        else
        {
            LlvmBuilderHandle builder = state.Target.Builder;
            LlvmTypeHandle terminateType = LlvmApi.FunctionType(state.I32, [state.I64, state.I32]);
            LlvmValueHandle terminatePtr = LlvmApi.BuildLoad2(builder,
                LlvmApi.PointerTypeInContext(state.Target.Context, 0),
                state.WindowsTerminateProcessImport, "proc_kill_fn_ptr");
            LlvmApi.BuildCall2(builder, terminateType, terminatePtr,
                [pid, LlvmApi.ConstInt(state.I32, 1, 0)], "proc_kill_call");
        }
        return EmitUnitValue(state);
    }
}
