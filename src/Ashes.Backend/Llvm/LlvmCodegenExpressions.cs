using Ashes.Backend.Llvm.Interop;
using Ashes.Semantics;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{
    private static LlvmValueHandle EmitIntComparison(LlvmCodegenState state, LlvmIntPredicate predicate, LlvmValueHandle left, LlvmValueHandle right, string name)
    {
        LlvmValueHandle cmp = LlvmApi.BuildICmp(state.Target.Builder, predicate, left, right, name);
        return LlvmApi.BuildZExt(state.Target.Builder, cmp, state.I64, name + "_zext");
    }

    private static LlvmValueHandle EmitFloatComparison(LlvmCodegenState state, LlvmRealPredicate predicate, LlvmValueHandle left, LlvmValueHandle right, string name)
    {
        LlvmValueHandle cmp = LlvmApi.BuildFCmp(state.Target.Builder, predicate, left, right, name);
        return LlvmApi.BuildZExt(state.Target.Builder, cmp, state.I64, name + "_zext");
    }

    private static LlvmValueHandle EmitInvertBool(LlvmCodegenState state, LlvmValueHandle value, string name)
    {
        return LlvmApi.BuildXor(state.Target.Builder, value, LlvmApi.ConstInt(state.I64, 1, 0), name);
    }

    private static LlvmValueHandle EmitMakeClosure(LlvmCodegenState state, string funcLabel, LlvmValueHandle envPtr, int envSizeBytes)
    {
        LlvmValueHandle closurePtr = EmitAlloc(state, 24); // {code, env, env_size}
        LlvmValueHandle codePtr = LlvmApi.BuildPtrToInt(state.Target.Builder, state.LiftedFunctions[funcLabel], state.I64, $"closure_code_{funcLabel}");
        StoreMemory(state, closurePtr, 0, codePtr, $"closure_code_store_{funcLabel}");
        StoreMemory(state, closurePtr, 8, envPtr, $"closure_env_store_{funcLabel}");
        StoreMemory(state, closurePtr, 16, LlvmApi.ConstInt(state.I64, (ulong)envSizeBytes, 0), $"closure_env_size_store_{funcLabel}");
        return closurePtr;
    }

    private static LlvmValueHandle EmitMakeClosureStack(LlvmCodegenState state, string funcLabel, LlvmValueHandle envPtr, int envSizeBytes)
    {
        LlvmValueHandle closurePtr = EmitStackAlloc(state, 24, $"closure_stack_{funcLabel}");
        LlvmValueHandle codePtr = LlvmApi.BuildPtrToInt(state.Target.Builder, state.LiftedFunctions[funcLabel], state.I64, $"closure_stack_code_{funcLabel}");
        StoreMemory(state, closurePtr, 0, codePtr, $"closure_stack_code_store_{funcLabel}");
        StoreMemory(state, closurePtr, 8, envPtr, $"closure_stack_env_store_{funcLabel}");
        StoreMemory(state, closurePtr, 16, LlvmApi.ConstInt(state.I64, (ulong)envSizeBytes, 0), $"closure_stack_env_size_store_{funcLabel}");
        return closurePtr;
    }

    private static LlvmValueHandle EmitCallClosure(LlvmCodegenState state, LlvmValueHandle closurePtr, LlvmValueHandle argValue, bool isTailCall = false)
    {
        LlvmValueHandle codePtr = LoadMemory(state, closurePtr, 0, "closure_code");
        LlvmValueHandle envPtr = LoadMemory(state, closurePtr, 8, "closure_env");
        LlvmTypeHandle closureFunctionType = LlvmApi.FunctionType(state.I64, [state.I64, state.I64]);
        LlvmTypeHandle closureFunctionPtrType = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmValueHandle typedCodePtr = LlvmApi.BuildIntToPtr(state.Target.Builder, codePtr, closureFunctionPtrType, "closure_code_ptr");
        LlvmValueHandle callInst = LlvmApi.BuildCall2(state.Target.Builder,
            closureFunctionType,
            typedCodePtr,
            [envPtr, argValue],
            "closure_call");
        if (isTailCall)
        {
            LlvmApi.SetTailCall(callInst, 1);
        }

        return callInst;
    }

    private static bool EmitJump(LlvmCodegenState state, string targetLabel)
    {
        LlvmApi.BuildBr(state.Target.Builder, state.GetLabelBlock(targetLabel));
        return true;
    }

    private static bool EmitJumpIfFalse(LlvmCodegenState state, LlvmValueHandle condValue, string targetLabel, int instructionIndex)
    {
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle cond = LlvmApi.BuildICmp(state.Target.Builder, LlvmIntPredicate.Ne, condValue, zero, $"cond_{instructionIndex}");
        LlvmBasicBlockHandle target = state.GetLabelBlock(targetLabel);
        LlvmBasicBlockHandle fallthrough = state.GetNextReachableBlock(instructionIndex);
        LlvmApi.BuildCondBr(state.Target.Builder, cond, fallthrough, target);
        LlvmApi.PositionBuilderAtEnd(state.Target.Builder, fallthrough);
        return false;
    }

    private static bool EmitReturn(LlvmCodegenState state, int source)
    {
        if (state.IsEntry)
        {
            if (IsLinuxFlavor(state.Flavor))
            {
                EmitExit(state, LlvmApi.ConstInt(state.I64, 0, 0));
            }
            else
            {
                LlvmApi.BuildRetVoid(state.Target.Builder);
            }
        }
        else
        {
            LlvmApi.BuildRet(state.Target.Builder, LoadTemp(state, source));
        }

        return true;
    }

    private static bool EmitPanic(LlvmCodegenState state, LlvmValueHandle stringRef)
    {
        EmitPrintStringFromTemp(state, stringRef, appendNewline: true);

        if (IsLinuxFlavor(state.Flavor))
        {
            EmitExit(state, LlvmApi.ConstInt(state.I64, 1, 0));
        }
        else
        {
            EmitWindowsExitProcess(state, LlvmApi.ConstInt(state.I32, 1, 0));
        }

        return true;
    }

    private static void EmitExit(LlvmCodegenState state, LlvmValueHandle exitCode)
    {
        EmitLinuxSyscall(state, SyscallExit, exitCode, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), "sys_exit");
        LlvmApi.BuildUnreachable(state.Target.Builder);
    }

    private static void EmitWindowsExitProcess(LlvmCodegenState state, LlvmValueHandle exitCode)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle exitProcessType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I32]);
        LlvmValueHandle exitProcessPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsExitProcessImport,
            "exit_process_ptr");
        LlvmApi.BuildCall2(builder,
            exitProcessType,
            exitProcessPtr,
            [exitCode],
            string.Empty);
        LlvmApi.BuildUnreachable(builder);
    }

    private static bool EmitPrintStringFromTemp(LlvmCodegenState state, LlvmValueHandle stringRef, bool appendNewline)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle basePtr = LlvmApi.BuildIntToPtr(builder, stringRef, state.I64Ptr, "str_len_ptr");
        LlvmValueHandle len = LlvmApi.BuildLoad2(builder, state.I64, basePtr, "str_len");
        LlvmValueHandle byteAddress = LlvmApi.BuildAdd(builder, stringRef, LlvmApi.ConstInt(state.I64, 8, 0), "str_bytes_addr");
        LlvmValueHandle bytePtr = LlvmApi.BuildIntToPtr(builder, byteAddress, state.I8Ptr, "str_bytes_ptr");
        EmitWriteBytes(state, bytePtr, len);
        if (appendNewline)
        {
            EmitWriteBytes(state, EmitStackByteArray(state, [10]), LlvmApi.ConstInt(state.I64, 1, 0));
        }

        return false;
    }

    private static bool EmitPrintBool(LlvmCodegenState state, LlvmValueHandle boolValue)
    {
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle isTrue = LlvmApi.BuildICmp(state.Target.Builder, LlvmIntPredicate.Ne, boolValue, zero, "bool_is_true");
        EmitConditionalWrite(state, isTrue, "true", "false", appendNewline: true);
        return false;
    }

    // ── Async / Task support ──────────────────────────────────────────

    /// <summary>
    /// CreateTask: allocate a task/state struct and initialize it.
    /// Layout: [state_index(0), coroutine_fn, result(0), awaited_task(0), next_task(0), sleep_duration_ms(0), captures...]
    /// The closure temp is [fn_ptr, env_ptr]. We unpack it and copy captures starting at <see cref="TaskStructLayout.HeaderSize"/>.
    /// </summary>
    private static LlvmValueHandle EmitCreateTask(LlvmCodegenState state, LlvmValueHandle closurePtr,
        int stateStructSize, int captureCount)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        // Allocate task/state struct
        LlvmValueHandle taskPtr = EmitAlloc(state, stateStructSize);

        // Initialize state_index = 0
        StoreMemory(state, taskPtr, TaskStructLayout.StateIndex,
            LlvmApi.ConstInt(state.I64, 0, 0), "task_state_init");

        // Extract coroutine function pointer from closure[0]
        LlvmValueHandle coroutineFn = LoadMemory(state, closurePtr, 0, "task_coroutine_fn");
        StoreMemory(state, taskPtr, TaskStructLayout.CoroutineFn,
            coroutineFn, "task_coroutine_fn_store");

        // Initialize result slot = 0
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot,
            LlvmApi.ConstInt(state.I64, 0, 0), "task_result_init");

        // Initialize awaited_task = 0
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask,
            LlvmApi.ConstInt(state.I64, 0, 0), "task_awaited_init");

        // Initialize next_task = 0 (linked-list pointer for scheduler/task chaining)
        StoreMemory(state, taskPtr, TaskStructLayout.NextTask,
            LlvmApi.ConstInt(state.I64, 0, 0), "task_next_init");

        // Initialize sleep_duration_ms = 0 (scheduler delay metadata)
        StoreMemory(state, taskPtr, TaskStructLayout.SleepDurationMs,
            LlvmApi.ConstInt(state.I64, 0, 0), "task_sleep_init");

        StoreMemory(state, taskPtr, TaskStructLayout.IoArg0,
            LlvmApi.ConstInt(state.I64, 0, 0), "task_io_arg0_init");
        StoreMemory(state, taskPtr, TaskStructLayout.IoArg1,
            LlvmApi.ConstInt(state.I64, 0, 0), "task_io_arg1_init");

        // Copy captured env variables from closure env into task struct
        StoreMemory(state, taskPtr, TaskStructLayout.WaitKind,
            LlvmApi.ConstInt(state.I64, 0, 0), "task_wait_kind_zero");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitHandle,
            LlvmApi.ConstInt(state.I64, 0, 0), "task_wait_handle_zero");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0,
            LlvmApi.ConstInt(state.I64, 0, 0), "task_wait_data0_zero");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData1,
            LlvmApi.ConstInt(state.I64, 0, 0), "task_wait_data1_zero");
        if (captureCount > 0)
        {
            LlvmValueHandle envPtr = LoadMemory(state, closurePtr, 8, "task_env_ptr");
            for (int i = 0; i < captureCount; i++)
            {
                int srcOffset = i * 8;
                int dstOffset = TaskStructLayout.HeaderSize + i * 8;
                LlvmValueHandle capVal = LoadMemory(state, envPtr, srcOffset, $"task_cap_{i}");
                StoreMemory(state, taskPtr, dstOffset, capVal, $"task_cap_{i}_store");
            }
        }

        return taskPtr;
    }

    /// <summary>
    /// CreateCompletedTask: allocate a minimal task struct that's already done.
    /// Layout: [state_index(-1), coroutine_fn(0), result, awaited_task(0)]
    /// </summary>
    private static LlvmValueHandle EmitCreateCompletedTask(LlvmCodegenState state, LlvmValueHandle resultValue)
    {
        LlvmValueHandle taskPtr = EmitAlloc(state, TaskStructLayout.HeaderSize);

        // state_index = -1 (COMPLETED)
        StoreMemory(state, taskPtr, TaskStructLayout.StateIndex,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)-1), 1), "ctask_state_done");

        // coroutine_fn = 0 (not needed)
        StoreMemory(state, taskPtr, TaskStructLayout.CoroutineFn,
            LlvmApi.ConstInt(state.I64, 0, 0), "ctask_fn_null");

        // result = the value
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot,
            resultValue, "ctask_result");

        // awaited_task = 0
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask,
            LlvmApi.ConstInt(state.I64, 0, 0), "ctask_awaited_null");

        StoreMemory(state, taskPtr, TaskStructLayout.NextTask,
            LlvmApi.ConstInt(state.I64, 0, 0), "ctask_next_null");
        StoreMemory(state, taskPtr, TaskStructLayout.SleepDurationMs,
            LlvmApi.ConstInt(state.I64, 0, 0), "ctask_sleep_zero");
        StoreMemory(state, taskPtr, TaskStructLayout.IoArg0,
            LlvmApi.ConstInt(state.I64, 0, 0), "ctask_io_arg0_zero");
        StoreMemory(state, taskPtr, TaskStructLayout.IoArg1,
            LlvmApi.ConstInt(state.I64, 0, 0), "ctask_io_arg1_zero");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitKind,
            LlvmApi.ConstInt(state.I64, 0, 0), "ctask_wait_kind_zero");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitHandle,
            LlvmApi.ConstInt(state.I64, 0, 0), "ctask_wait_handle_zero");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0,
            LlvmApi.ConstInt(state.I64, 0, 0), "ctask_wait_data0_zero");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData1,
            LlvmApi.ConstInt(state.I64, 0, 0), "ctask_wait_data1_zero");

        return taskPtr;
    }

    private static LlvmValueHandle EmitCreateLeafNetworkingTask(
        LlvmCodegenState state,
        long taskState,
        LlvmValueHandle arg0,
        LlvmValueHandle arg1,
        string prefix)
    {
        LlvmValueHandle taskPtr = EmitAlloc(state, TaskStructLayout.HeaderSize);

        StoreMemory(state, taskPtr, TaskStructLayout.StateIndex,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)taskState), 1), prefix + "_state");
        StoreMemory(state, taskPtr, TaskStructLayout.CoroutineFn,
            LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_coroutine_fn");
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot,
            LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_result");
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask,
            LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_awaited");
        StoreMemory(state, taskPtr, TaskStructLayout.NextTask,
            LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_next");
        StoreMemory(state, taskPtr, TaskStructLayout.SleepDurationMs,
            LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_sleep");
        StoreMemory(state, taskPtr, TaskStructLayout.IoArg0, arg0, prefix + "_arg0");
        StoreMemory(state, taskPtr, TaskStructLayout.IoArg1, arg1, prefix + "_arg1");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitKind,
            LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_wait_kind");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitHandle,
            LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_wait_handle");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0,
            LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_wait_data0");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData1,
            LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_wait_data1");

        return taskPtr;
    }

    private static LlvmValueHandle EmitStepLeafTask(LlvmCodegenState state, LlvmValueHandle taskPtr, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle stateIdx = LoadMemory(state, taskPtr, TaskStructLayout.StateIndex, prefix + "_state_idx");
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_status_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), statusSlot);

        LlvmBasicBlockHandle sleepBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_sleep");
        LlvmBasicBlockHandle checkTcpConnectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tcp_connect");
        LlvmBasicBlockHandle tcpConnectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tcp_connect");
        LlvmBasicBlockHandle checkTcpSendBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tcp_send");
        LlvmBasicBlockHandle tcpSendBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tcp_send");
        LlvmBasicBlockHandle checkTcpReceiveBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tcp_receive");
        LlvmBasicBlockHandle tcpReceiveBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tcp_receive");
        LlvmBasicBlockHandle checkTcpCloseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tcp_close");
        LlvmBasicBlockHandle tcpCloseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tcp_close");
        LlvmBasicBlockHandle checkTlsConnectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tls_connect");
        LlvmBasicBlockHandle tlsConnectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tls_connect");
        LlvmBasicBlockHandle checkTlsHandshakeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tls_handshake");
        LlvmBasicBlockHandle tlsHandshakeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tls_handshake");
        LlvmBasicBlockHandle checkTlsSendBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tls_send");
        LlvmBasicBlockHandle tlsSendBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tls_send");
        LlvmBasicBlockHandle checkTlsReceiveBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tls_receive");
        LlvmBasicBlockHandle tlsReceiveBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tls_receive");
        LlvmBasicBlockHandle checkTlsCloseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tls_close");
        LlvmBasicBlockHandle tlsCloseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tls_close");
        LlvmBasicBlockHandle checkHttpGetBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_http_get");
        LlvmBasicBlockHandle httpGetBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_http_get");
        LlvmBasicBlockHandle checkHttpPostBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_http_post");
        LlvmBasicBlockHandle httpPostBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_http_post");
        LlvmBasicBlockHandle invalidBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_invalid");
        LlvmBasicBlockHandle continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_continue");

        LlvmValueHandle isSleep = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            stateIdx,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateSleeping), 1),
            prefix + "_is_sleep");
        LlvmApi.BuildCondBr(builder, isSleep, sleepBlock, checkTcpConnectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, sleepBlock);
        LlvmValueHandle sleepMs = LoadMemory(state, taskPtr, TaskStructLayout.SleepDurationMs, prefix + "_sleep_ms");
        EmitNanosleep(state, sleepMs);
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot,
            EmitResultOk(state, LlvmApi.ConstInt(state.I64, 0, 0)), prefix + "_sleep_result");
        StoreMemory(state, taskPtr, TaskStructLayout.StateIndex,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateCompleted), 1), prefix + "_sleep_done");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), statusSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkTcpConnectBlock);
        LlvmValueHandle isTcpConnect = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            stateIdx,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateTcpConnect), 1),
            prefix + "_is_tcp_connect");
        LlvmApi.BuildCondBr(builder, isTcpConnect, tcpConnectBlock, checkTcpSendBlock);

        LlvmApi.PositionBuilderAtEnd(builder, tcpConnectBlock);
        LlvmApi.BuildStore(builder,
            EmitNetworkingRuntimeCall(state, "ashes_step_tcp_connect_task", [taskPtr], prefix + "_tcp_connect_status"),
            statusSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkTcpSendBlock);
        LlvmValueHandle isTcpSend = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            stateIdx,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateTcpSend), 1),
            prefix + "_is_tcp_send");
        LlvmApi.BuildCondBr(builder, isTcpSend, tcpSendBlock, checkTcpReceiveBlock);

        LlvmApi.PositionBuilderAtEnd(builder, tcpSendBlock);
        LlvmApi.BuildStore(builder,
            EmitNetworkingRuntimeCall(state, "ashes_step_tcp_send_task", [taskPtr], prefix + "_tcp_send_status"),
            statusSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkTcpReceiveBlock);
        LlvmValueHandle isTcpReceive = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            stateIdx,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateTcpReceive), 1),
            prefix + "_is_tcp_receive");
        LlvmApi.BuildCondBr(builder, isTcpReceive, tcpReceiveBlock, checkTcpCloseBlock);

        LlvmApi.PositionBuilderAtEnd(builder, tcpReceiveBlock);
        LlvmApi.BuildStore(builder,
            EmitNetworkingRuntimeCall(state, "ashes_step_tcp_receive_task", [taskPtr], prefix + "_tcp_receive_status"),
            statusSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkTcpCloseBlock);
        LlvmValueHandle isTcpClose = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            stateIdx,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateTcpClose), 1),
            prefix + "_is_tcp_close");
        LlvmApi.BuildCondBr(builder, isTcpClose, tcpCloseBlock, checkTlsConnectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, tcpCloseBlock);
        LlvmApi.BuildStore(builder,
            EmitNetworkingRuntimeCall(state, "ashes_step_tcp_close_task", [taskPtr], prefix + "_tcp_close_status"),
            statusSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkTlsConnectBlock);
        LlvmValueHandle isTlsConnect = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            stateIdx,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateTlsConnect), 1),
            prefix + "_is_tls_connect");
        LlvmApi.BuildCondBr(builder, isTlsConnect, tlsConnectBlock, checkTlsHandshakeBlock);

        LlvmApi.PositionBuilderAtEnd(builder, tlsConnectBlock);
        LlvmApi.BuildStore(builder,
            EmitNetworkingRuntimeCall(state, "ashes_step_tls_connect_task", [taskPtr], prefix + "_tls_connect_status"),
            statusSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkTlsHandshakeBlock);
        LlvmValueHandle isTlsHandshake = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            stateIdx,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateTlsHandshake), 1),
            prefix + "_is_tls_handshake");
        LlvmApi.BuildCondBr(builder, isTlsHandshake, tlsHandshakeBlock, checkTlsSendBlock);

        LlvmApi.PositionBuilderAtEnd(builder, tlsHandshakeBlock);
        LlvmApi.BuildStore(builder,
            EmitNetworkingRuntimeCall(state, "ashes_step_tls_handshake_task", [taskPtr], prefix + "_tls_handshake_status"),
            statusSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkTlsSendBlock);
        LlvmValueHandle isTlsSend = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            stateIdx,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateTlsSend), 1),
            prefix + "_is_tls_send");
        LlvmApi.BuildCondBr(builder, isTlsSend, tlsSendBlock, checkTlsReceiveBlock);

        LlvmApi.PositionBuilderAtEnd(builder, tlsSendBlock);
        LlvmApi.BuildStore(builder,
            EmitNetworkingRuntimeCall(state, "ashes_step_tls_send_task", [taskPtr], prefix + "_tls_send_status"),
            statusSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkTlsReceiveBlock);
        LlvmValueHandle isTlsReceive = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            stateIdx,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateTlsReceive), 1),
            prefix + "_is_tls_receive");
        LlvmApi.BuildCondBr(builder, isTlsReceive, tlsReceiveBlock, checkTlsCloseBlock);

        LlvmApi.PositionBuilderAtEnd(builder, tlsReceiveBlock);
        LlvmApi.BuildStore(builder,
            EmitNetworkingRuntimeCall(state, "ashes_step_tls_receive_task", [taskPtr], prefix + "_tls_receive_status"),
            statusSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkTlsCloseBlock);
        LlvmValueHandle isTlsClose = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            stateIdx,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateTlsClose), 1),
            prefix + "_is_tls_close");
        LlvmApi.BuildCondBr(builder, isTlsClose, tlsCloseBlock, checkHttpGetBlock);

        LlvmApi.PositionBuilderAtEnd(builder, tlsCloseBlock);
        LlvmApi.BuildStore(builder,
            EmitNetworkingRuntimeCall(state, "ashes_step_tls_close_task", [taskPtr], prefix + "_tls_close_status"),
            statusSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkHttpGetBlock);
        LlvmValueHandle isHttpGet = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            stateIdx,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateHttpGet), 1),
            prefix + "_is_http_get");
        LlvmApi.BuildCondBr(builder, isHttpGet, httpGetBlock, checkHttpPostBlock);

        LlvmApi.PositionBuilderAtEnd(builder, httpGetBlock);
        LlvmApi.BuildStore(builder,
            EmitNetworkingRuntimeCall(state, "ashes_step_http_get_task", [taskPtr], prefix + "_http_get_status"),
            statusSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkHttpPostBlock);
        LlvmValueHandle isHttpPost = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            stateIdx,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateHttpPost), 1),
            prefix + "_is_http_post");
        LlvmApi.BuildCondBr(builder, isHttpPost, httpPostBlock, invalidBlock);

        LlvmApi.PositionBuilderAtEnd(builder, httpPostBlock);
        LlvmApi.BuildStore(builder,
            EmitNetworkingRuntimeCall(state, "ashes_step_http_post_task", [taskPtr], prefix + "_http_post_status"),
            statusSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, invalidBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot,
            EmitResultError(state, EmitHeapStringLiteral(state, "unknown leaf task state")),
            prefix + "_invalid_result");
        StoreMemory(state, taskPtr, TaskStructLayout.StateIndex,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateCompleted), 1), prefix + "_invalid_done");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), statusSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, statusSlot, prefix + "_status");
    }

    private static LlvmValueHandle EmitStepTaskUntilPendingOrDone(LlvmCodegenState state, LlvmValueHandle taskPtr, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_status_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), statusSlot);

        LlvmBasicBlockHandle checkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check");
        LlvmBasicBlockHandle notDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_not_done");
        LlvmBasicBlockHandle leafBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_leaf");
        LlvmBasicBlockHandle stepBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_step");
        LlvmBasicBlockHandle suspendedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_suspended");
        LlvmBasicBlockHandle awaitedDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_awaited_done");
        LlvmBasicBlockHandle leafPendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_leaf_pending");
        LlvmBasicBlockHandle awaitedPendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_awaited_pending");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");

        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkBlock);
        LlvmValueHandle stateIdx = LoadMemory(state, taskPtr, TaskStructLayout.StateIndex, prefix + "_state_idx");
        LlvmValueHandle completedConst = LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateCompleted), 1);
        LlvmValueHandle isDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, stateIdx, completedConst, prefix + "_is_done");
        LlvmApi.BuildCondBr(builder, isDone, doneBlock, notDoneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, notDoneBlock);
        LlvmValueHandle isLeaf = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, stateIdx, completedConst, prefix + "_is_leaf");
        LlvmApi.BuildCondBr(builder, isLeaf, leafBlock, stepBlock);

        LlvmApi.PositionBuilderAtEnd(builder, leafBlock);
        LlvmValueHandle leafStatus = EmitStepLeafTask(state, taskPtr, prefix + "_leaf_step");
        LlvmValueHandle leafCompleted = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, leafStatus, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_leaf_completed");
        LlvmApi.BuildCondBr(builder, leafCompleted, doneBlock, leafPendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, stepBlock);
        EmitClearLeafTaskWait(state, taskPtr, prefix + "_clear_wait_before_step");
        LlvmValueHandle coroutineFn = LoadMemory(state, taskPtr, TaskStructLayout.CoroutineFn, prefix + "_coroutine_fn");
        LlvmTypeHandle coroutineFnType = LlvmApi.FunctionType(state.I64, [state.I64, state.I64]);
        LlvmTypeHandle coroutineFnPtrType = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmValueHandle typedFnPtr = LlvmApi.BuildIntToPtr(builder, coroutineFn, coroutineFnPtrType, prefix + "_fn_ptr");
        LlvmValueHandle stepStatus = LlvmApi.BuildCall2(
            builder,
            coroutineFnType,
            typedFnPtr,
            [taskPtr, LlvmApi.ConstInt(state.I64, 0, 0)],
            prefix + "_step_status");
        LlvmValueHandle suspended = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, stepStatus, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_is_suspended");
        LlvmApi.BuildCondBr(builder, suspended, suspendedBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, suspendedBlock);
        LlvmValueHandle awaitedTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_awaited_task");
        LlvmValueHandle awaitedStatus = EmitNetworkingRuntimeCall(state, "ashes_step_task_until_wait_or_done", [awaitedTask], prefix + "_awaited_status");
        LlvmValueHandle awaitedCompleted = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, awaitedStatus, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_awaited_completed");
        LlvmApi.BuildCondBr(builder, awaitedCompleted, awaitedDoneBlock, awaitedPendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, awaitedDoneBlock);
        EmitClearLeafTaskWait(state, taskPtr, prefix + "_clear_wait_after_await");
        LlvmValueHandle awaitedResult = LoadMemory(state, awaitedTask, TaskStructLayout.ResultSlot, prefix + "_awaited_result");
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot, awaitedResult, prefix + "_awaited_result_store");
        LlvmApi.BuildBr(builder, stepBlock);

        LlvmApi.PositionBuilderAtEnd(builder, leafPendingBlock);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, awaitedPendingBlock);
        LlvmValueHandle waitKind = LoadMemory(state, awaitedTask, TaskStructLayout.WaitKind, prefix + "_awaited_wait_kind");
        LlvmValueHandle waitHandle = LoadMemory(state, awaitedTask, TaskStructLayout.WaitHandle, prefix + "_awaited_wait_handle");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitKind, waitKind, prefix + "_mirror_wait_kind");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitHandle, waitHandle, prefix + "_mirror_wait_handle");
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        LlvmValueHandle finalStateIdx = LoadMemory(state, taskPtr, TaskStructLayout.StateIndex, prefix + "_final_state_idx");
        LlvmValueHandle finalDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, finalStateIdx, completedConst, prefix + "_final_done");
        LlvmApi.BuildStore(builder, LlvmApi.BuildZExt(builder, finalDone, state.I64, prefix + "_final_done_i64"), statusSlot);
        return LlvmApi.BuildLoad2(builder, state.I64, statusSlot, prefix + "_status");
    }

    private static LlvmValueHandle EmitWindowsPollEventMask(LlvmCodegenState state, LlvmValueHandle readishWait, string name)
    {
        LlvmTypeHandle i16 = LlvmApi.Int16TypeInContext(state.Target.Context);
        return LlvmApi.BuildSelect(state.Target.Builder,
            readishWait,
            LlvmApi.ConstInt(i16, WindowsPollReadNormal, 0),
            LlvmApi.ConstInt(i16, WindowsPollWriteNormal, 0),
            name);
    }

    private static void EmitWindowsInitializePollFd(LlvmCodegenState state, LlvmValueHandle pollFdAddress, LlvmValueHandle socket, LlvmValueHandle eventMask, string prefix)
    {
        StoreMemory(state, pollFdAddress, 0, socket, prefix + "_socket_store");
        StoreMemory(state,
            pollFdAddress,
            8,
            LlvmApi.BuildZExt(state.Target.Builder, eventMask, state.I64, prefix + "_event_mask_i64"),
            prefix + "_event_store");
    }

    private static void EmitWindowsWaitForPendingSocketTaskList(LlvmCodegenState state, LlvmValueHandle taskListPtr, LlvmValueHandle totalPending, LlvmValueHandle waitResultSlot, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle pollArrayBytes = LlvmApi.BuildMul(builder, totalPending, LlvmApi.ConstInt(state.I64, WindowsPollFdSize, 0), prefix + "_poll_array_bytes");
        LlvmValueHandle pollArrayAddress = EmitAllocDynamic(state, pollArrayBytes);
        LlvmValueHandle pollArrayPtr = LlvmApi.BuildIntToPtr(builder, pollArrayAddress, state.I8Ptr, prefix + "_poll_array_ptr");
        LlvmValueHandle registerCursorSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_register_cursor_slot");
        LlvmValueHandle pollWritePtrSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_poll_write_ptr_slot");
        LlvmValueHandle scanIndexSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_scan_index_slot");
        LlvmValueHandle scanPtrSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_scan_ptr_slot");
        LlvmApi.BuildStore(builder, taskListPtr, registerCursorSlot);
        LlvmApi.BuildStore(builder, pollArrayAddress, pollWritePtrSlot);

        LlvmBasicBlockHandle registerCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_register_check");
        LlvmBasicBlockHandle registerBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_register_body");
        LlvmBasicBlockHandle registerStoreBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_register_store");
        LlvmBasicBlockHandle registerAdvanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_register_advance");
        LlvmBasicBlockHandle afterRegisterBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_after_register");
        LlvmBasicBlockHandle scanCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_scan_check");
        LlvmBasicBlockHandle scanReadyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_scan_ready");
        LlvmBasicBlockHandle scanAdvanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_scan_advance");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");
        LlvmApi.BuildBr(builder, registerCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, registerCheckBlock);
        LlvmValueHandle registerCursor = LlvmApi.BuildLoad2(builder, state.I64, registerCursorSlot, prefix + "_register_cursor");
        LlvmValueHandle registerDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, registerCursor, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_register_done");
        LlvmApi.BuildCondBr(builder, registerDone, afterRegisterBlock, registerBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, registerBodyBlock);
        LlvmValueHandle registerTask = LoadMemory(state, registerCursor, 0, prefix + "_register_task");
        LlvmValueHandle registerTail = LoadMemory(state, registerCursor, 8, prefix + "_register_tail");
        LlvmValueHandle registerWaitKind = LoadMemory(state, registerTask, TaskStructLayout.WaitKind, prefix + "_register_wait_kind");
        LlvmValueHandle registerHandle = LoadMemory(state, registerTask, TaskStructLayout.WaitHandle, prefix + "_register_wait_handle");
        LlvmValueHandle registerIsRead = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, registerWaitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitSocketRead, 0), prefix + "_register_is_read");
        LlvmValueHandle registerIsWrite = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, registerWaitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitSocketWrite, 0), prefix + "_register_is_write");
        LlvmValueHandle registerIsTlsRead = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, registerWaitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTlsWantRead, 0), prefix + "_register_is_tls_read");
        LlvmValueHandle registerIsTlsWrite = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, registerWaitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTlsWantWrite, 0), prefix + "_register_is_tls_write");
        LlvmValueHandle registerReadish = LlvmApi.BuildOr(builder, registerIsRead, registerIsTlsRead, prefix + "_register_readish");
        LlvmValueHandle registerWriteish = LlvmApi.BuildOr(builder, registerIsWrite, registerIsTlsWrite, prefix + "_register_writeish");
        LlvmValueHandle registerShould = LlvmApi.BuildOr(builder, registerReadish, registerWriteish, prefix + "_register_should");
        LlvmApi.BuildCondBr(builder, registerShould, registerStoreBlock, registerAdvanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, registerStoreBlock);
        LlvmValueHandle pollWritePtr = LlvmApi.BuildLoad2(builder, state.I64, pollWritePtrSlot, prefix + "_poll_write_ptr");
        LlvmValueHandle eventMask = EmitWindowsPollEventMask(state, registerReadish, prefix + "_event_mask");
        EmitWindowsInitializePollFd(state, pollWritePtr, registerHandle, eventMask, prefix + "_pollfd");
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, pollWritePtr, LlvmApi.ConstInt(state.I64, WindowsPollFdSize, 0), prefix + "_poll_write_ptr_next"), pollWritePtrSlot);
        LlvmApi.BuildBr(builder, registerAdvanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, registerAdvanceBlock);
        LlvmApi.BuildStore(builder, registerTail, registerCursorSlot);
        LlvmApi.BuildBr(builder, registerCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, afterRegisterBlock);
        LlvmValueHandle pollResult = LlvmApi.BuildSExt(builder,
            EmitWindowsWsaPoll(state, pollArrayPtr, totalPending, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), prefix + "_wsapoll"),
            state.I64,
            prefix + "_poll_result");
        LlvmValueHandle hasReady = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, pollResult, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_ready");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), scanIndexSlot);
        LlvmApi.BuildStore(builder, pollArrayAddress, scanPtrSlot);
        LlvmApi.BuildCondBr(builder, hasReady, scanCheckBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, scanCheckBlock);
        LlvmValueHandle scanIndex = LlvmApi.BuildLoad2(builder, state.I64, scanIndexSlot, prefix + "_scan_index");
        LlvmValueHandle scanDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, scanIndex, totalPending, prefix + "_scan_done");
        LlvmBasicBlockHandle scanBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_scan_body");
        LlvmApi.BuildCondBr(builder, scanDone, doneBlock, scanBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, scanBodyBlock);
        LlvmValueHandle scanPtr = LlvmApi.BuildLoad2(builder, state.I64, scanPtrSlot, prefix + "_scan_ptr");
        LlvmValueHandle tailValue = LoadMemory(state, scanPtr, 8, prefix + "_scan_tail_value");
        LlvmValueHandle revents = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildLShr(builder, tailValue, LlvmApi.ConstInt(state.I64, 16, 0), prefix + "_scan_revents_shift"),
            LlvmApi.ConstInt(state.I64, 0xFFFF, 0),
            prefix + "_scan_revents");
        LlvmValueHandle entryReady = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, revents, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_scan_entry_ready");
        LlvmApi.BuildCondBr(builder, entryReady, scanReadyBlock, scanAdvanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, scanReadyBlock);
        LlvmApi.BuildStore(builder, LoadMemory(state, scanPtr, 0, prefix + "_ready_handle"), waitResultSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, scanAdvanceBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, scanIndex, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_scan_index_next"), scanIndexSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, scanPtr, LlvmApi.ConstInt(state.I64, WindowsPollFdSize, 0), prefix + "_scan_ptr_next"), scanPtrSlot);
        LlvmApi.BuildBr(builder, scanCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
    }

    private static LlvmValueHandle EmitWaitForPendingTaskList(LlvmCodegenState state, LlvmValueHandle taskListPtr, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle countSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_count_slot");
        LlvmValueHandle cursorSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_cursor_slot");
        LlvmValueHandle waitResultSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_wait_result_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), countSlot);
        LlvmApi.BuildStore(builder, taskListPtr, cursorSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), waitResultSlot);

        LlvmBasicBlockHandle countCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_count_check");
        LlvmBasicBlockHandle countBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_count_body");
        LlvmBasicBlockHandle countIncrementBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_count_increment");
        LlvmBasicBlockHandle countAdvanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_count_advance");
        LlvmBasicBlockHandle afterCountBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_after_count");
        LlvmApi.BuildBr(builder, countCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, countCheckBlock);
        LlvmValueHandle countCursor = LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, prefix + "_count_cursor");
        LlvmValueHandle countDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, countCursor, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_count_done");
        LlvmApi.BuildCondBr(builder, countDone, afterCountBlock, countBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, countBodyBlock);
        LlvmValueHandle countTask = LoadMemory(state, countCursor, 0, prefix + "_count_task");
        LlvmValueHandle countTail = LoadMemory(state, countCursor, 8, prefix + "_count_tail");
        LlvmValueHandle countWaitKind = LoadMemory(state, countTask, TaskStructLayout.WaitKind, prefix + "_count_wait_kind");
        LlvmValueHandle countIsRead = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, countWaitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitSocketRead, 0), prefix + "_count_is_read");
        LlvmValueHandle countIsWrite = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, countWaitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitSocketWrite, 0), prefix + "_count_is_write");
        LlvmValueHandle countIsTlsRead = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, countWaitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTlsWantRead, 0), prefix + "_count_is_tls_read");
        LlvmValueHandle countIsTlsWrite = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, countWaitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTlsWantWrite, 0), prefix + "_count_is_tls_write");
        LlvmValueHandle countShould = LlvmApi.BuildOr(builder, countIsRead, countIsWrite, prefix + "_count_should");
        countShould = LlvmApi.BuildOr(builder, countShould, countIsTlsRead, prefix + "_count_should_tls_read");
        countShould = LlvmApi.BuildOr(builder, countShould, countIsTlsWrite, prefix + "_count_should_tls_write");
        LlvmApi.BuildCondBr(builder, countShould, countIncrementBlock, countAdvanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, countIncrementBlock);
        LlvmValueHandle pendingCount = LlvmApi.BuildLoad2(builder, state.I64, countSlot, prefix + "_pending_count");
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, pendingCount, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_pending_count_next"), countSlot);
        LlvmApi.BuildBr(builder, countAdvanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, countAdvanceBlock);
        LlvmApi.BuildStore(builder, countTail, cursorSlot);
        LlvmApi.BuildBr(builder, countCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, afterCountBlock);
        LlvmValueHandle totalPending = LlvmApi.BuildLoad2(builder, state.I64, countSlot, prefix + "_total_pending");
        LlvmValueHandle hasPending = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, totalPending, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_pending");
        LlvmBasicBlockHandle waitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_wait");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");
        LlvmApi.BuildCondBr(builder, hasPending, waitBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, waitBlock);
        if (IsLinuxFlavor(state.Flavor))
        {
            LlvmTypeHandle epollEventType = LlvmApi.ArrayType2(state.I8, 16);
            LlvmValueHandle eventStorage = LlvmApi.BuildAlloca(builder, epollEventType, prefix + "_event_storage");
            LlvmValueHandle eventPtr = GetArrayElementPointer(state, epollEventType, eventStorage, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_event_ptr");
            LlvmValueHandle waitEventStorage = LlvmApi.BuildAlloca(builder, epollEventType, prefix + "_wait_event_storage");
            LlvmValueHandle waitEventPtr = GetArrayElementPointer(state, epollEventType, waitEventStorage, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_wait_event_ptr");
            LlvmValueHandle epollFd = EmitLinuxSyscall(state, SyscallEpollCreate1, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_epoll_create");
            LlvmValueHandle registerCursorSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_register_cursor_slot");
            LlvmApi.BuildStore(builder, taskListPtr, registerCursorSlot);
            LlvmBasicBlockHandle registerCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_register_check");
            LlvmBasicBlockHandle registerBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_register_body");
            LlvmBasicBlockHandle registerStoreBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_register_store");
            LlvmBasicBlockHandle registerAdvanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_register_advance");
            LlvmBasicBlockHandle afterRegisterBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_after_register");
            LlvmApi.BuildBr(builder, registerCheckBlock);

            LlvmApi.PositionBuilderAtEnd(builder, registerCheckBlock);
            LlvmValueHandle registerCursor = LlvmApi.BuildLoad2(builder, state.I64, registerCursorSlot, prefix + "_register_cursor");
            LlvmValueHandle registerDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, registerCursor, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_register_done");
            LlvmApi.BuildCondBr(builder, registerDone, afterRegisterBlock, registerBodyBlock);

            LlvmApi.PositionBuilderAtEnd(builder, registerBodyBlock);
            LlvmValueHandle registerTask = LoadMemory(state, registerCursor, 0, prefix + "_register_task");
            LlvmValueHandle registerTail = LoadMemory(state, registerCursor, 8, prefix + "_register_tail");
            LlvmValueHandle registerWaitKind = LoadMemory(state, registerTask, TaskStructLayout.WaitKind, prefix + "_register_wait_kind");
            LlvmValueHandle registerHandle = LoadMemory(state, registerTask, TaskStructLayout.WaitHandle, prefix + "_register_wait_handle");
            LlvmValueHandle registerIsRead = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, registerWaitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitSocketRead, 0), prefix + "_register_is_read");
            LlvmValueHandle registerIsWrite = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, registerWaitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitSocketWrite, 0), prefix + "_register_is_write");
            LlvmValueHandle registerIsTlsRead = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, registerWaitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTlsWantRead, 0), prefix + "_register_is_tls_read");
            LlvmValueHandle registerIsTlsWrite = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, registerWaitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTlsWantWrite, 0), prefix + "_register_is_tls_write");
            LlvmValueHandle registerReadish = LlvmApi.BuildOr(builder, registerIsRead, registerIsTlsRead, prefix + "_register_readish");
            LlvmValueHandle registerWriteish = LlvmApi.BuildOr(builder, registerIsWrite, registerIsTlsWrite, prefix + "_register_writeish");
            LlvmValueHandle registerShould = LlvmApi.BuildOr(builder, registerReadish, registerWriteish, prefix + "_register_should");
            LlvmApi.BuildCondBr(builder, registerShould, registerStoreBlock, registerAdvanceBlock);

            LlvmApi.PositionBuilderAtEnd(builder, registerStoreBlock);
            LlvmValueHandle eventMask = LlvmApi.BuildSelect(builder,
                registerReadish,
                LlvmApi.ConstInt(state.I32, 0x001, 0),
                LlvmApi.ConstInt(state.I32, 0x004, 0),
                prefix + "_event_mask");
            LlvmApi.BuildStore(builder, eventMask, LlvmApi.BuildBitCast(builder, eventPtr, state.I32Ptr, prefix + "_event_mask_ptr"));
            LlvmApi.BuildStore(builder,
                registerHandle,
                LlvmApi.BuildBitCast(builder,
                    LlvmApi.BuildGEP2(builder, state.I8, eventPtr, [LlvmApi.ConstInt(state.I64, 8, 0)], prefix + "_event_data_byte"),
                    state.I64Ptr,
                    prefix + "_event_data_ptr"));
            EmitLinuxSyscall4(state,
                SyscallEpollCtl,
                epollFd,
                LlvmApi.ConstInt(state.I64, 1, 0),
                registerHandle,
                LlvmApi.BuildPtrToInt(builder, eventPtr, state.I64, prefix + "_event_arg"),
                prefix + "_epoll_ctl");
            LlvmApi.BuildBr(builder, registerAdvanceBlock);

            LlvmApi.PositionBuilderAtEnd(builder, registerAdvanceBlock);
            LlvmApi.BuildStore(builder, registerTail, registerCursorSlot);
            LlvmApi.BuildBr(builder, registerCheckBlock);

            LlvmApi.PositionBuilderAtEnd(builder, afterRegisterBlock);
            if (IsLinuxArm64Flavor(state.Flavor))
            {
                EmitLinuxSyscall6(state,
                    SyscallEpollWait,
                    epollFd,
                    LlvmApi.BuildPtrToInt(builder, waitEventPtr, state.I64, prefix + "_wait_event_arg"),
                    LlvmApi.ConstInt(state.I64, 1, 0),
                    LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1),
                    LlvmApi.ConstInt(state.I64, 0, 0),
                    LlvmApi.ConstInt(state.I64, 0, 0),
                    prefix + "_epoll_wait");
            }
            else
            {
                EmitLinuxSyscall4(state,
                    SyscallEpollWait,
                    epollFd,
                    LlvmApi.BuildPtrToInt(builder, waitEventPtr, state.I64, prefix + "_wait_event_arg"),
                    LlvmApi.ConstInt(state.I64, 1, 0),
                    LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1),
                    prefix + "_epoll_wait");
            }

            EmitLinuxSyscall(state, SyscallClose, epollFd, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_epoll_close");
        }
        else
        {
            EmitWindowsWaitForPendingSocketTaskList(state, taskListPtr, totalPending, waitResultSlot, prefix + "_windows_poll");
        }

        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, waitResultSlot, prefix + "_wait_result");
    }

    /// <summary>
    /// RunTask: synchronously drive a task to completion.
    /// Algorithm:
    /// 1. Check if task is already completed (state_index == -1) → return result
    /// 2. Call the coroutine function: status = coroutine(task_ptr, 0)
    /// 3. If SUSPENDED: recursively run the awaited sub-task,
    ///    store its result in the task's result slot, loop back to step 2
    /// 4. If COMPLETED: return result from task's result slot
    /// </summary>
    private static LlvmValueHandle EmitRunTask(LlvmCodegenState state, LlvmValueHandle taskPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        // Create basic blocks for the run loop
        LlvmBasicBlockHandle checkBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "run_task_check");
        LlvmBasicBlockHandle stepBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "run_task_step");
        LlvmBasicBlockHandle notDoneBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "run_task_not_done");
        LlvmBasicBlockHandle leafBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "run_task_leaf");
        LlvmBasicBlockHandle leafPendingBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "run_task_leaf_pending");
        LlvmBasicBlockHandle suspendedBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "run_task_suspended");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "run_task_done");

        // Jump to check block
        LlvmApi.BuildBr(builder, checkBlock);

        // --- Check block: is task already completed? ---
        LlvmApi.PositionBuilderAtEnd(builder, checkBlock);
        LlvmValueHandle stateIdx = LoadMemory(state, taskPtr, TaskStructLayout.StateIndex, "run_state_idx");
        LlvmValueHandle minusOne = LlvmApi.ConstInt(state.I64, unchecked((ulong)-1), 1);
        LlvmValueHandle isDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            stateIdx, minusOne, "run_is_done");
        LlvmApi.BuildCondBr(builder, isDone, doneBlock, notDoneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, notDoneBlock);
        LlvmValueHandle isLeaf = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt,
            stateIdx, minusOne, "run_is_leaf");
        LlvmApi.BuildCondBr(builder, isLeaf, leafBlock, stepBlock);

        LlvmApi.PositionBuilderAtEnd(builder, leafBlock);
        LlvmValueHandle leafStatus = EmitStepLeafTask(state, taskPtr, "run_leaf");
        LlvmValueHandle leafCompleted = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne,
            leafStatus, LlvmApi.ConstInt(state.I64, 0, 0), "run_leaf_completed");
        LlvmApi.BuildCondBr(builder, leafCompleted, doneBlock, leafPendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, leafPendingBlock);
        EmitWaitForPendingLeafTask(state, taskPtr, "run_leaf_pending");
        LlvmApi.BuildBr(builder, checkBlock);

        // --- Step block: call the coroutine ---
        LlvmApi.PositionBuilderAtEnd(builder, stepBlock);
        LlvmValueHandle coroutineFn = LoadMemory(state, taskPtr, TaskStructLayout.CoroutineFn, "run_coroutine_fn");
        LlvmTypeHandle coroutineFnType = LlvmApi.FunctionType(state.I64, [state.I64, state.I64]);
        LlvmTypeHandle coroutineFnPtrType = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmValueHandle typedFnPtr = LlvmApi.BuildIntToPtr(builder, coroutineFn, coroutineFnPtrType, "run_fn_ptr");
        LlvmValueHandle status = LlvmApi.BuildCall2(builder,
            coroutineFnType,
            typedFnPtr,
            [taskPtr, LlvmApi.ConstInt(state.I64, 0, 0)],
            "run_status");

        // Check status: 0 = SUSPENDED, 1 = COMPLETED
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle isSuspended = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            status, zero, "run_is_suspended");
        LlvmApi.BuildCondBr(builder, isSuspended, suspendedBlock, doneBlock);

        // --- Suspended block: run the awaited sub-task, then resume ---
        LlvmApi.PositionBuilderAtEnd(builder, suspendedBlock);
        LlvmValueHandle awaitedTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, "run_awaited_task");
        LlvmValueHandle awaitedState = LoadMemory(state, awaitedTask, TaskStructLayout.StateIndex, "run_awaited_state");
        LlvmValueHandle isLeafAwaited = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt,
            awaitedState, minusOne, "run_is_leaf_awaited");
        LlvmBasicBlockHandle leafHandleBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "run_leaf_handle");
        LlvmBasicBlockHandle leafHandleDoneBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "run_leaf_handle_done");
        LlvmBasicBlockHandle leafHandlePendingBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "run_leaf_handle_pending");
        LlvmBasicBlockHandle normalSubBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "run_normal_sub");
        LlvmBasicBlockHandle afterSubBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "run_after_sub");

        LlvmApi.BuildCondBr(builder, isLeafAwaited, leafHandleBlock, normalSubBlock);

        LlvmApi.PositionBuilderAtEnd(builder, leafHandleBlock);
        LlvmValueHandle awaitedLeafStatus = EmitStepLeafTask(state, awaitedTask, "run_awaited_leaf");
        LlvmValueHandle awaitedLeafCompleted = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne,
            awaitedLeafStatus, LlvmApi.ConstInt(state.I64, 0, 0), "run_awaited_leaf_completed");
        LlvmApi.BuildCondBr(builder, awaitedLeafCompleted, leafHandleDoneBlock, leafHandlePendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, leafHandleDoneBlock);
        LlvmValueHandle leafResult = LoadMemory(state, awaitedTask, TaskStructLayout.ResultSlot, "run_leaf_result_load");
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot, leafResult, "run_leaf_sub_store");
        LlvmApi.BuildBr(builder, afterSubBlock);

        LlvmApi.PositionBuilderAtEnd(builder, leafHandlePendingBlock);
        EmitWaitForPendingLeafTask(state, awaitedTask, "run_awaited_leaf_pending");
        LlvmApi.BuildBr(builder, suspendedBlock);

        // --- Normal sub-task: recursively run to completion ---
        LlvmApi.PositionBuilderAtEnd(builder, normalSubBlock);
        LlvmValueHandle subResult = EmitRunTaskRecursive(state, awaitedTask);
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot, subResult, "run_sub_result_store");
        LlvmApi.BuildBr(builder, afterSubBlock);

        // --- After sub-task: loop back to step the coroutine again ---
        LlvmApi.PositionBuilderAtEnd(builder, afterSubBlock);
        LlvmApi.BuildBr(builder, stepBlock);

        // --- Done block: extract and return the result ---
        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LoadMemory(state, taskPtr, TaskStructLayout.ResultSlot, "run_task_result");
    }

    /// <summary>
    /// Helper: recursively run a task to completion. This is the same logic as EmitRunTask
    /// but implemented as a recursive call to a shared runtime function.
    /// For simplicity, we inline the same pattern.
    /// </summary>
    private static LlvmValueHandle EmitRunTaskRecursive(LlvmCodegenState state, LlvmValueHandle taskPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        // Create blocks for the sub-task run loop
        LlvmBasicBlockHandle subCheckBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "sub_run_check");
        LlvmBasicBlockHandle subStepBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "sub_run_step");
        LlvmBasicBlockHandle subNotDoneBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "sub_run_not_done");
        LlvmBasicBlockHandle subLeafBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "sub_run_leaf");
        LlvmBasicBlockHandle subLeafPendingBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "sub_run_leaf_pending");
        LlvmBasicBlockHandle subSuspendedBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "sub_run_suspended");
        LlvmBasicBlockHandle subDoneBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "sub_run_done");

        LlvmApi.BuildBr(builder, subCheckBlock);

        // --- Check: already completed? ---
        LlvmApi.PositionBuilderAtEnd(builder, subCheckBlock);
        LlvmValueHandle stateIdx = LoadMemory(state, taskPtr, TaskStructLayout.StateIndex, "sub_state_idx");
        LlvmValueHandle minusOne = LlvmApi.ConstInt(state.I64, unchecked((ulong)-1), 1);
        LlvmValueHandle isDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            stateIdx, minusOne, "sub_is_done");

        LlvmApi.BuildCondBr(builder, isDone, subDoneBlock, subNotDoneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, subNotDoneBlock);
        LlvmValueHandle subIsLeaf = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt,
            stateIdx, minusOne, "sub_is_leaf");
        LlvmApi.BuildCondBr(builder, subIsLeaf, subLeafBlock, subStepBlock);

        LlvmApi.PositionBuilderAtEnd(builder, subLeafBlock);
        LlvmValueHandle subLeafStatus = EmitStepLeafTask(state, taskPtr, "sub_leaf");
        LlvmValueHandle subLeafCompleted = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne,
            subLeafStatus, LlvmApi.ConstInt(state.I64, 0, 0), "sub_leaf_completed");
        LlvmApi.BuildCondBr(builder, subLeafCompleted, subDoneBlock, subLeafPendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, subLeafPendingBlock);
        EmitWaitForPendingLeafTask(state, taskPtr, "sub_leaf_pending");
        LlvmApi.BuildBr(builder, subCheckBlock);

        // --- Step: call coroutine ---
        LlvmApi.PositionBuilderAtEnd(builder, subStepBlock);
        LlvmValueHandle coroutineFn = LoadMemory(state, taskPtr, TaskStructLayout.CoroutineFn, "sub_coroutine_fn");
        LlvmTypeHandle coroutineFnType = LlvmApi.FunctionType(state.I64, [state.I64, state.I64]);
        LlvmTypeHandle coroutineFnPtrType = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmValueHandle typedFnPtr = LlvmApi.BuildIntToPtr(builder, coroutineFn, coroutineFnPtrType, "sub_fn_ptr");
        LlvmValueHandle status = LlvmApi.BuildCall2(builder,
            coroutineFnType,
            typedFnPtr,
            [taskPtr, LlvmApi.ConstInt(state.I64, 0, 0)],
            "sub_status");

        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle isSuspended = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            status, zero, "sub_is_suspended");
        LlvmApi.BuildCondBr(builder, isSuspended, subSuspendedBlock, subDoneBlock);

        // --- Suspended: handle nested await (run sub-sub-task) ---
        LlvmApi.PositionBuilderAtEnd(builder, subSuspendedBlock);
        LlvmValueHandle awaitedTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, "sub_awaited_task");
        // For nested tasks, we check if already completed and if not, step them inline.
        // Since we can't recurse infinitely at compile time, we handle one level:
        // the nested task's coroutine either completes immediately or we panic.
        // This handles the common case (fromResult creates completed tasks,
        // and inner async blocks have their own coroutines that are stepped).

        // Check if the nested awaited task is already completed
        LlvmValueHandle nestedStateIdx = LoadMemory(state, awaitedTask, TaskStructLayout.StateIndex, "nested_state_idx");
        LlvmValueHandle nestedIsDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            nestedStateIdx, minusOne, "nested_is_done");
        LlvmValueHandle nestedIsLeaf = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt,
            nestedStateIdx, minusOne, "nested_is_leaf");

        LlvmBasicBlockHandle nestedDoneBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "nested_done");
        LlvmBasicBlockHandle nestedLeafBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "nested_leaf");
        LlvmBasicBlockHandle nestedLeafDoneBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "nested_leaf_done");
        LlvmBasicBlockHandle nestedLeafPendingBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "nested_leaf_pending");
        LlvmBasicBlockHandle nestedStepBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "nested_step");

        LlvmBasicBlockHandle nestedNotDoneBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "nested_not_done");
        LlvmApi.BuildCondBr(builder, nestedIsDone, nestedDoneBlock, nestedNotDoneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, nestedNotDoneBlock);
        LlvmApi.BuildCondBr(builder, nestedIsLeaf, nestedLeafBlock, nestedStepBlock);

        LlvmApi.PositionBuilderAtEnd(builder, nestedLeafBlock);
        LlvmValueHandle nestedLeafStatus = EmitStepLeafTask(state, awaitedTask, "nested_leaf");
        LlvmValueHandle nestedLeafCompleted = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne,
            nestedLeafStatus, LlvmApi.ConstInt(state.I64, 0, 0), "nested_leaf_completed");
        LlvmApi.BuildCondBr(builder, nestedLeafCompleted, nestedLeafDoneBlock, nestedLeafPendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, nestedLeafDoneBlock);
        LlvmApi.BuildBr(builder, nestedDoneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, nestedLeafPendingBlock);
        LlvmApi.BuildBr(builder, nestedNotDoneBlock);

        // --- Nested step: call nested coroutine in a loop ---
        LlvmApi.PositionBuilderAtEnd(builder, nestedStepBlock);
        LlvmValueHandle nestedFn = LoadMemory(state, awaitedTask, TaskStructLayout.CoroutineFn, "nested_fn");
        LlvmValueHandle nestedFnPtr = LlvmApi.BuildIntToPtr(builder, nestedFn, coroutineFnPtrType, "nested_fn_ptr");
        LlvmValueHandle nestedStatus = LlvmApi.BuildCall2(builder,
            coroutineFnType,
            nestedFnPtr,
            [awaitedTask, LlvmApi.ConstInt(state.I64, 0, 0)],
            "nested_status");

        // If nested task completed, fall through to nestedDone.
        // If suspended, the nested task has its own awaited sub-task; loop.
        LlvmValueHandle nestedSuspended = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            nestedStatus, zero, "nested_suspended");
        LlvmApi.BuildCondBr(builder, nestedSuspended, nestedStepBlock, nestedDoneBlock);

        // --- Nested done: get result ---
        LlvmApi.PositionBuilderAtEnd(builder, nestedDoneBlock);
        LlvmValueHandle nestedResult = LoadMemory(state, awaitedTask, TaskStructLayout.ResultSlot, "nested_result");
        // Store into our task's result slot
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot, nestedResult, "sub_nested_result_store");
        // Loop back to step our coroutine
        LlvmApi.BuildBr(builder, subStepBlock);

        // --- Done: extract result ---
        LlvmApi.PositionBuilderAtEnd(builder, subDoneBlock);
        return LoadMemory(state, taskPtr, TaskStructLayout.ResultSlot, "sub_task_result");
    }

    // ── Async Sleep ────────────────────────────────────────────

    /// <summary>
    /// EmitAsyncSleep: Create a sleep task.
    /// The task struct has state_index = -2 (SLEEPING) and the sleep duration
    /// stored in SleepDurationMs as milliseconds (the runtime converts to nanoseconds).
    /// Layout: [state=-2, fn=0, result=0, awaited=0, next=0, sleep_ms=ms]
    /// </summary>
    private static LlvmValueHandle EmitAsyncSleep(LlvmCodegenState state, LlvmValueHandle millisecondsValue)
    {
        LlvmValueHandle taskPtr = EmitAlloc(state, TaskStructLayout.HeaderSize);

        // state_index = -2 (SLEEPING)
        StoreMemory(state, taskPtr, TaskStructLayout.StateIndex,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateSleeping), 1), "sleep_state");

        // coroutine_fn = 0 (not needed)
        StoreMemory(state, taskPtr, TaskStructLayout.CoroutineFn,
            LlvmApi.ConstInt(state.I64, 0, 0), "sleep_fn_null");

        // result = 0 (will hold the result after completion)
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot,
            LlvmApi.ConstInt(state.I64, 0, 0), "sleep_result_init");

        // awaited_task = 0
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask,
            LlvmApi.ConstInt(state.I64, 0, 0), "sleep_awaited_null");

        // next_task = 0
        StoreMemory(state, taskPtr, TaskStructLayout.NextTask,
            LlvmApi.ConstInt(state.I64, 0, 0), "sleep_next_null");

        // sleep_deadline_ns = milliseconds (runtime interprets as ms)
        StoreMemory(state, taskPtr, TaskStructLayout.SleepDurationMs,
            millisecondsValue, "sleep_ms");

        StoreMemory(state, taskPtr, TaskStructLayout.IoArg0,
            LlvmApi.ConstInt(state.I64, 0, 0), "sleep_io_arg0_zero");
        StoreMemory(state, taskPtr, TaskStructLayout.IoArg1,
            LlvmApi.ConstInt(state.I64, 0, 0), "sleep_io_arg1_zero");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitKind,
            LlvmApi.ConstInt(state.I64, 0, 0), "sleep_wait_kind_zero");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitHandle,
            LlvmApi.ConstInt(state.I64, 0, 0), "sleep_wait_handle_zero");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0,
            LlvmApi.ConstInt(state.I64, 0, 0), "sleep_wait_data0_zero");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData1,
            LlvmApi.ConstInt(state.I64, 0, 0), "sleep_wait_data1_zero");

        return taskPtr;
    }

    /// <summary>
    /// EmitNanosleep: perform a platform-specific sleep for the given milliseconds.
    /// On Linux: uses nanosleep syscall with a timespec struct.
    /// On Windows: uses Sleep() from kernel32.dll.
    /// </summary>
    private static void EmitNanosleep(LlvmCodegenState state, LlvmValueHandle milliseconds)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        if (IsLinuxFlavor(state.Flavor))
        {
            // Convert milliseconds to timespec {tv_sec, tv_nsec}
            // tv_sec = ms / 1000
            // tv_nsec = (ms % 1000) * 1000000
            LlvmValueHandle thousand = LlvmApi.ConstInt(state.I64, 1000, 0);
            LlvmValueHandle million = LlvmApi.ConstInt(state.I64, 1000000, 0);
            LlvmValueHandle tvSec = LlvmApi.BuildSDiv(builder, milliseconds, thousand, "sleep_sec");
            LlvmValueHandle msRem = LlvmApi.BuildSRem(builder, milliseconds, thousand, "sleep_ms_rem");
            LlvmValueHandle tvNsec = LlvmApi.BuildMul(builder, msRem, million, "sleep_nsec");

            // Allocate timespec on stack: {i64 tv_sec, i64 tv_nsec}
            LlvmTypeHandle timespecType = LlvmApi.ArrayType2(state.I64, 2);
            LlvmValueHandle timespecPtr = LlvmApi.BuildAlloca(builder, timespecType, "timespec");
            LlvmValueHandle secPtr = GetArrayElementPointer(state, timespecType, timespecPtr,
                LlvmApi.ConstInt(state.I64, 0, 0), "timespec_sec_ptr");
            LlvmApi.BuildStore(builder, tvSec, secPtr);
            LlvmValueHandle nsecPtr = GetArrayElementPointer(state, timespecType, timespecPtr,
                LlvmApi.ConstInt(state.I64, 1, 0), "timespec_nsec_ptr");
            LlvmApi.BuildStore(builder, tvNsec, nsecPtr);

            // nanosleep(&timespec, NULL)
            LlvmValueHandle timespecI64 = LlvmApi.BuildPtrToInt(builder, timespecPtr, state.I64, "timespec_addr");
            EmitLinuxSyscall(state, SyscallNanosleep,
                timespecI64,
                LlvmApi.ConstInt(state.I64, 0, 0),
                LlvmApi.ConstInt(state.I64, 0, 0),
                "sys_nanosleep");
        }
        else
        {
            // Windows: Sleep(milliseconds)
            EmitWindowsSleep(state, milliseconds);
        }
    }

    /// <summary>
    /// Handle a potentially sleeping sub-task in the event loop.
    /// If the task has state_index == -2 (SLEEPING), perform nanosleep and mark it complete.
    /// Otherwise, step the task normally.
    /// This is called from the run-task loop when a parent task suspends on a sub-task.
    /// </summary>
    private static void EmitHandleSubTask(LlvmCodegenState state, LlvmValueHandle taskPtr,
        LlvmBasicBlockHandle doneBlock, LlvmBasicBlockHandle stepBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        // Check if sub-task is SLEEPING (-2)
        LlvmValueHandle stateIdx = LoadMemory(state, taskPtr, TaskStructLayout.StateIndex, "sub_check_state");
        LlvmValueHandle sleepingConst = LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateSleeping), 1);
        LlvmValueHandle isSleeping = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            stateIdx, sleepingConst, "sub_is_sleeping");

        LlvmBasicBlockHandle sleepBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "sub_sleep_handle");

        LlvmApi.BuildCondBr(builder, isSleeping, sleepBlock, stepBlock);

        // --- Sleep handling block ---
        LlvmApi.PositionBuilderAtEnd(builder, sleepBlock);
        LlvmValueHandle sleepMs = LoadMemory(state, taskPtr, TaskStructLayout.SleepDurationMs, "sleep_ms_val");
        EmitNanosleep(state, sleepMs);

        // Mark the sleep task as completed with result = Ok(Unit)
        StoreMemory(state, taskPtr, TaskStructLayout.StateIndex,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateCompleted), 1), "sleep_mark_done");
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot,
            EmitResultOk(state, LlvmApi.ConstInt(state.I64, 0, 0)), "sleep_result_zero");

        LlvmApi.BuildBr(builder, doneBlock);
    }

    /// <summary>
    /// Windows Sleep(DWORD dwMilliseconds) — imported from kernel32.dll.
    /// </summary>
    private static void EmitWindowsSleep(LlvmCodegenState state, LlvmValueHandle milliseconds)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // Truncate i64 ms to i32 for Sleep(DWORD)
        LlvmValueHandle ms32 = LlvmApi.BuildTrunc(builder, milliseconds, state.I32, "sleep_ms32");
        LlvmTypeHandle sleepType = LlvmApi.FunctionType(
            LlvmApi.VoidTypeInContext(state.Target.Context), [state.I32]);
        LlvmValueHandle sleepFnPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsSleepImport,
            "sleep_fn_ptr");
        LlvmApi.BuildCall2(builder, sleepType, sleepFnPtr, [ms32], "");
    }

    private static void EmitWaitForPendingLeafTask(LlvmCodegenState state, LlvmValueHandle taskPtr, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle waitKind = LoadMemory(state, taskPtr, TaskStructLayout.WaitKind, prefix + "_wait_kind");
        LlvmValueHandle waitHandle = LoadMemory(state, taskPtr, TaskStructLayout.WaitHandle, prefix + "_wait_handle");
        LlvmValueHandle isReadWait = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            waitKind,
            LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitSocketRead, 0),
            prefix + "_is_read_wait");
        LlvmValueHandle isWriteWait = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            waitKind,
            LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitSocketWrite, 0),
            prefix + "_is_write_wait");
        LlvmValueHandle isTlsReadWait = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            waitKind,
            LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTlsWantRead, 0),
            prefix + "_is_tls_read_wait");
        LlvmValueHandle isTlsWriteWait = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            waitKind,
            LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTlsWantWrite, 0),
            prefix + "_is_tls_write_wait");
        LlvmValueHandle readishWait = LlvmApi.BuildOr(builder, isReadWait, isTlsReadWait, prefix + "_readish_wait");
        LlvmValueHandle writeishWait = LlvmApi.BuildOr(builder, isWriteWait, isTlsWriteWait, prefix + "_writeish_wait");
        LlvmValueHandle shouldWait = LlvmApi.BuildOr(builder, readishWait, writeishWait, prefix + "_should_wait");

        LlvmBasicBlockHandle waitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_wait_block");
        LlvmBasicBlockHandle continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_continue");
        LlvmApi.BuildCondBr(builder, shouldWait, waitBlock, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, waitBlock);
        if (IsLinuxFlavor(state.Flavor))
        {
            LlvmTypeHandle epollEventType = LlvmApi.ArrayType2(state.I8, 16);
            LlvmValueHandle epollEventStorage = LlvmApi.BuildAlloca(builder, epollEventType, prefix + "_epoll_event_storage");
            LlvmValueHandle epollEventPtr = GetArrayElementPointer(state, epollEventType, epollEventStorage, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_epoll_event_ptr");
            LlvmValueHandle epollEventOutStorage = LlvmApi.BuildAlloca(builder, epollEventType, prefix + "_epoll_event_out_storage");
            LlvmValueHandle epollEventOutPtr = GetArrayElementPointer(state, epollEventType, epollEventOutStorage, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_epoll_event_out_ptr");
            LlvmValueHandle epollFd = EmitLinuxSyscall(state, SyscallEpollCreate1, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_epoll_create1");
            LlvmValueHandle readMask = LlvmApi.ConstInt(state.I32, 0x001, 0);
            LlvmValueHandle writeMask = LlvmApi.ConstInt(state.I32, 0x004, 0);
            LlvmValueHandle eventMask = LlvmApi.BuildSelect(builder, readishWait, readMask, writeMask, prefix + "_event_mask");
            LlvmApi.BuildStore(builder, eventMask, LlvmApi.BuildBitCast(builder, epollEventPtr, state.I32Ptr, prefix + "_epoll_event_mask_ptr"));
            LlvmApi.BuildStore(builder, waitHandle, LlvmApi.BuildBitCast(builder,
                LlvmApi.BuildGEP2(builder, state.I8, epollEventPtr, [LlvmApi.ConstInt(state.I64, 8, 0)], prefix + "_epoll_event_data_byte"),
                state.I64Ptr,
                prefix + "_epoll_event_data_ptr"));

            EmitLinuxSyscall4(state, SyscallEpollCtl,
                epollFd,
                LlvmApi.ConstInt(state.I64, 1, 0),
                waitHandle,
                LlvmApi.BuildPtrToInt(builder, epollEventPtr, state.I64, prefix + "_epoll_event_arg"),
                prefix + "_epoll_ctl");
            if (IsLinuxArm64Flavor(state.Flavor))
            {
                EmitLinuxSyscall6(state, SyscallEpollWait,
                    epollFd,
                    LlvmApi.BuildPtrToInt(builder, epollEventOutPtr, state.I64, prefix + "_epoll_wait_events"),
                    LlvmApi.ConstInt(state.I64, 1, 0),
                    LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1),
                    LlvmApi.ConstInt(state.I64, 0, 0),
                    LlvmApi.ConstInt(state.I64, 0, 0),
                    prefix + "_epoll_wait");
            }
            else
            {
                EmitLinuxSyscall4(state, SyscallEpollWait,
                    epollFd,
                    LlvmApi.BuildPtrToInt(builder, epollEventOutPtr, state.I64, prefix + "_epoll_wait_events"),
                    LlvmApi.ConstInt(state.I64, 1, 0),
                    LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1),
                    prefix + "_epoll_wait");
            }
            EmitLinuxSyscall(state, SyscallClose, epollFd, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_epoll_close");
        }
        else
        {
            LlvmTypeHandle pollFdType = LlvmApi.ArrayType2(state.I8, WindowsPollFdSize);
            LlvmValueHandle pollFdStorage = LlvmApi.BuildAlloca(builder, pollFdType, prefix + "_pollfd_storage");
            LlvmValueHandle pollFdPtr = GetArrayElementPointer(state, pollFdType, pollFdStorage, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_pollfd_ptr");
            LlvmValueHandle pollFdAddress = LlvmApi.BuildPtrToInt(builder, pollFdPtr, state.I64, prefix + "_pollfd_address");
            LlvmValueHandle eventMask = EmitWindowsPollEventMask(state, readishWait, prefix + "_poll_event_mask");
            EmitWindowsInitializePollFd(state, pollFdAddress, waitHandle, eventMask, prefix + "_pollfd");
            _ = EmitWindowsWsaPoll(state, pollFdPtr, LlvmApi.ConstInt(state.I64, 1, 0), LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), prefix + "_wsapoll_wait");
        }

        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
    }

    // ── Async All ──────────────────────────────────────────────

    /// <summary>
    /// EmitAsyncAll: Run all tasks in a list and collect results into a list.
    /// Input: pointer to a list of tasks.
    ///   List representation: 0 = Nil (null), non-zero = Cons(head @offset 0, tail @offset 8).
    /// Output: completed task with a list of result values.
    /// Algorithm:
    ///   1. Walk the input list, run each task, push result onto a reversed list.
    ///   2. Reverse the accumulated list to restore original order.
    ///   3. Wrap the result list in a completed task.
    /// All blocks are inlined in the caller's function.
    /// </summary>
    private static LlvmValueHandle EmitAsyncAll(LlvmCodegenState state, LlvmValueHandle taskListPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        LlvmValueHandle listSlot = LlvmApi.BuildAlloca(builder, state.I64, "all_list");
        LlvmValueHandle pendingCountSlot = LlvmApi.BuildAlloca(builder, state.I64, "all_pending_count");
        LlvmValueHandle failureSlot = LlvmApi.BuildAlloca(builder, state.I64, "all_failure");
        LlvmValueHandle revSrcSlot = LlvmApi.BuildAlloca(builder, state.I64, "all_rev_src");
        LlvmValueHandle revDstSlot = LlvmApi.BuildAlloca(builder, state.I64, "all_rev_dst");

        LlvmApi.BuildStore(builder, taskListPtr, listSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), pendingCountSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), failureSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), revDstSlot);

        LlvmBasicBlockHandle scanCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_scan_check");
        LlvmBasicBlockHandle scanBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_scan_body");
        LlvmBasicBlockHandle pendingIncrementBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_pending_increment");
        LlvmBasicBlockHandle inspectResultBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_inspect_result");
        LlvmBasicBlockHandle failureBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_failure_block");
        LlvmBasicBlockHandle afterScanBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_after_scan");
        LlvmBasicBlockHandle waitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_wait");
        LlvmBasicBlockHandle buildInitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_build_init");
        LlvmBasicBlockHandle buildCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_build_check");
        LlvmBasicBlockHandle buildBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_build_body");
        LlvmBasicBlockHandle reverseInitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_reverse_init");
        LlvmBasicBlockHandle reverseCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_reverse_check");
        LlvmBasicBlockHandle reverseBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_reverse_body");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_done");

        LlvmApi.BuildBr(builder, scanCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, scanCheckBlock);
        LlvmValueHandle scanCursor = LlvmApi.BuildLoad2(builder, state.I64, listSlot, "all_scan_cursor");
        LlvmValueHandle scanDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, scanCursor, LlvmApi.ConstInt(state.I64, 0, 0), "all_scan_done");
        LlvmApi.BuildCondBr(builder, scanDone, afterScanBlock, scanBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, scanBodyBlock);
        LlvmValueHandle scanNode = LlvmApi.BuildLoad2(builder, state.I64, listSlot, "all_scan_node");
        LlvmValueHandle headTask = LoadMemory(state, scanNode, 0, "all_head_task");
        LlvmValueHandle tailList = LoadMemory(state, scanNode, 8, "all_tail_list");
        LlvmApi.BuildStore(builder, tailList, listSlot);
        EmitNetworkingRuntimeCall(state, "ashes_step_task_until_wait_or_done", [headTask], "all_step_task");
        LlvmValueHandle headState = LoadMemory(state, headTask, TaskStructLayout.StateIndex, "all_head_state");
        LlvmValueHandle isDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, headState, LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateCompleted), 1), "all_head_done");
        LlvmApi.BuildCondBr(builder, isDone, inspectResultBlock, pendingIncrementBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingIncrementBlock);
        LlvmValueHandle pendingCount = LlvmApi.BuildLoad2(builder, state.I64, pendingCountSlot, "all_pending_count_value");
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, pendingCount, LlvmApi.ConstInt(state.I64, 1, 0), "all_pending_count_next"), pendingCountSlot);
        LlvmApi.BuildBr(builder, scanCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, inspectResultBlock);
        LlvmValueHandle taskResult = LoadMemory(state, headTask, TaskStructLayout.ResultSlot, "all_task_result");
        LlvmValueHandle taskTag = LoadMemory(state, taskResult, 0, "all_task_tag");
        LlvmValueHandle isError = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, taskTag, LlvmApi.ConstInt(state.I64, 0, 0), "all_task_is_error");
        LlvmApi.BuildCondBr(builder, isError, failureBlock, scanCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failureBlock);
        LlvmApi.BuildStore(builder, taskResult, failureSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, afterScanBlock);
        LlvmValueHandle pendingAfterScan = LlvmApi.BuildLoad2(builder, state.I64, pendingCountSlot, "all_pending_after_scan");
        LlvmValueHandle hasPending = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, pendingAfterScan, LlvmApi.ConstInt(state.I64, 0, 0), "all_has_pending");
        LlvmApi.BuildCondBr(builder, hasPending, waitBlock, buildInitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, waitBlock);
        EmitNetworkingRuntimeCall(state, "ashes_wait_pending_task_list", [taskListPtr], "all_wait_pending");
        LlvmApi.BuildStore(builder, taskListPtr, listSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), pendingCountSlot);
        LlvmApi.BuildBr(builder, scanCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, buildInitBlock);
        LlvmApi.BuildStore(builder, taskListPtr, revSrcSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), revDstSlot);
        LlvmApi.BuildBr(builder, buildCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, buildCheckBlock);
        LlvmValueHandle buildCursor = LlvmApi.BuildLoad2(builder, state.I64, revSrcSlot, "all_build_cursor");
        LlvmValueHandle buildDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, buildCursor, LlvmApi.ConstInt(state.I64, 0, 0), "all_build_done");
        LlvmApi.BuildCondBr(builder, buildDone, reverseInitBlock, buildBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, buildBodyBlock);
        LlvmValueHandle buildNode = LlvmApi.BuildLoad2(builder, state.I64, revSrcSlot, "all_build_node");
        LlvmValueHandle buildTask = LoadMemory(state, buildNode, 0, "all_build_task");
        LlvmValueHandle buildTail = LoadMemory(state, buildNode, 8, "all_build_tail");
        LlvmValueHandle buildResult = LoadMemory(state, buildTask, TaskStructLayout.ResultSlot, "all_build_result");
        LlvmValueHandle buildValue = LoadMemory(state, buildResult, 8, "all_build_value");
        LlvmValueHandle buildAcc = LlvmApi.BuildLoad2(builder, state.I64, revDstSlot, "all_build_acc");
        LlvmValueHandle buildCons = EmitAlloc(state, 16);
        StoreMemory(state, buildCons, 0, buildValue, "all_build_cons_head");
        StoreMemory(state, buildCons, 8, buildAcc, "all_build_cons_tail");
        LlvmApi.BuildStore(builder, buildTail, revSrcSlot);
        LlvmApi.BuildStore(builder, buildCons, revDstSlot);
        LlvmApi.BuildBr(builder, buildCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, reverseInitBlock);
        LlvmValueHandle reverseSource = LlvmApi.BuildLoad2(builder, state.I64, revDstSlot, "all_reverse_source");
        LlvmApi.BuildStore(builder, reverseSource, revSrcSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), revDstSlot);
        LlvmApi.BuildBr(builder, reverseCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, reverseCheckBlock);
        LlvmValueHandle reverseCursor = LlvmApi.BuildLoad2(builder, state.I64, revSrcSlot, "all_reverse_cursor");
        LlvmValueHandle reverseDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, reverseCursor, LlvmApi.ConstInt(state.I64, 0, 0), "all_reverse_done");
        LlvmApi.BuildCondBr(builder, reverseDone, doneBlock, reverseBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, reverseBodyBlock);
        LlvmValueHandle reverseNode = LlvmApi.BuildLoad2(builder, state.I64, revSrcSlot, "all_reverse_node");
        LlvmValueHandle reverseHead = LoadMemory(state, reverseNode, 0, "all_reverse_head");
        LlvmValueHandle reverseTail = LoadMemory(state, reverseNode, 8, "all_reverse_tail");
        LlvmValueHandle reverseAcc = LlvmApi.BuildLoad2(builder, state.I64, revDstSlot, "all_reverse_acc");
        LlvmValueHandle reverseCons = EmitAlloc(state, 16);
        StoreMemory(state, reverseCons, 0, reverseHead, "all_reverse_cons_head");
        StoreMemory(state, reverseCons, 8, reverseAcc, "all_reverse_cons_tail");
        LlvmApi.BuildStore(builder, reverseTail, revSrcSlot);
        LlvmApi.BuildStore(builder, reverseCons, revDstSlot);
        LlvmApi.BuildBr(builder, reverseCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        LlvmValueHandle failureResult = LlvmApi.BuildLoad2(builder, state.I64, failureSlot, "all_failure_result");
        LlvmValueHandle hasFailure = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, failureResult, LlvmApi.ConstInt(state.I64, 0, 0), "all_has_failure");
        LlvmValueHandle finalList = LlvmApi.BuildLoad2(builder, state.I64, revDstSlot, "all_final_list");
        LlvmValueHandle successResult = EmitResultOk(state, finalList);
        LlvmValueHandle finalResult = LlvmApi.BuildSelect(builder, hasFailure, failureResult, successResult, "all_final_result");
        return EmitCreateCompletedTask(state, finalResult);
    }

    // ── Async Race ─────────────────────────────────────────────

    /// <summary>
    /// EmitAsyncRace: Run the first task in a list and return its result.
    /// Input: pointer to a list of tasks.
    ///   List representation: 0 = Nil (null), non-zero = Cons(head @offset 0, tail @offset 8).
    /// Output: completed task with the first task's result value.
    /// If the list is empty, returns a completed task with 0 (unit).
    /// All blocks are inlined in the caller's function.
    /// </summary>
    private static LlvmValueHandle EmitAsyncRace(LlvmCodegenState state, LlvmValueHandle taskListPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "race_res");
        LlvmValueHandle resultTaskSlot = LlvmApi.BuildAlloca(builder, state.I64, "race_result_task");
        LlvmValueHandle listSlot = LlvmApi.BuildAlloca(builder, state.I64, "race_list");
        LlvmValueHandle pendingCountSlot = LlvmApi.BuildAlloca(builder, state.I64, "race_pending_count");
        LlvmValueHandle preferredWaitHandleSlot = LlvmApi.BuildAlloca(builder, state.I64, "race_preferred_wait_handle");
        LlvmValueHandle preferredCursorSlot = LlvmApi.BuildAlloca(builder, state.I64, "race_preferred_cursor");

        LlvmApi.BuildStore(builder, EmitResultOk(state, LlvmApi.ConstInt(state.I64, 0, 0)), resultSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultTaskSlot);
        LlvmApi.BuildStore(builder, taskListPtr, listSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), pendingCountSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), preferredWaitHandleSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), preferredCursorSlot);

        LlvmBasicBlockHandle preferredCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_preferred_check");
        LlvmBasicBlockHandle preferredSearchBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_preferred_search");
        LlvmBasicBlockHandle preferredStepBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_preferred_step");
        LlvmBasicBlockHandle preferredNotFoundBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_preferred_not_found");
        LlvmBasicBlockHandle scanCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_scan_check");
        LlvmBasicBlockHandle scanBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_scan_body");
        LlvmBasicBlockHandle pendingIncrementBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_pending_increment");
        LlvmBasicBlockHandle resultBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_result");
        LlvmBasicBlockHandle afterScanBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_after_scan");
        LlvmBasicBlockHandle waitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_wait");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_done");

        LlvmApi.BuildBr(builder, preferredCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, preferredCheckBlock);
        LlvmValueHandle preferredWaitHandle = LlvmApi.BuildLoad2(builder, state.I64, preferredWaitHandleSlot, "race_preferred_wait_handle_value");
        LlvmValueHandle hasPreferredWaitHandle = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, preferredWaitHandle, LlvmApi.ConstInt(state.I64, 0, 0), "race_has_preferred_wait_handle");
        LlvmApi.BuildCondBr(builder, hasPreferredWaitHandle, preferredSearchBlock, scanCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, preferredSearchBlock);
        LlvmValueHandle preferredCursor = LlvmApi.BuildLoad2(builder, state.I64, preferredCursorSlot, "race_preferred_cursor_value");
        LlvmValueHandle preferredSearchDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, preferredCursor, LlvmApi.ConstInt(state.I64, 0, 0), "race_preferred_search_done");
        LlvmBasicBlockHandle preferredBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_preferred_body");
        LlvmApi.BuildCondBr(builder, preferredSearchDone, preferredNotFoundBlock, preferredBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, preferredBodyBlock);
        LlvmValueHandle preferredNode = LlvmApi.BuildLoad2(builder, state.I64, preferredCursorSlot, "race_preferred_node");
        LlvmValueHandle preferredTask = LoadMemory(state, preferredNode, 0, "race_preferred_task");
        LlvmValueHandle preferredTail = LoadMemory(state, preferredNode, 8, "race_preferred_tail");
        LlvmValueHandle taskWaitHandle = LoadMemory(state, preferredTask, TaskStructLayout.WaitHandle, "race_preferred_task_wait_handle");
        LlvmValueHandle matchesPreferred = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, taskWaitHandle, preferredWaitHandle, "race_preferred_matches");
        LlvmBasicBlockHandle preferredAdvanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_preferred_advance");
        LlvmApi.BuildCondBr(builder, matchesPreferred, preferredStepBlock, preferredAdvanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, preferredAdvanceBlock);
        LlvmApi.BuildStore(builder, preferredTail, preferredCursorSlot);
        LlvmApi.BuildBr(builder, preferredSearchBlock);

        LlvmApi.PositionBuilderAtEnd(builder, preferredStepBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), preferredWaitHandleSlot);
        LlvmValueHandle preferredStatus = EmitNetworkingRuntimeCall(state, "ashes_step_task_until_wait_or_done", [preferredTask], "race_preferred_step_task");
        LlvmValueHandle preferredDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, preferredStatus, LlvmApi.ConstInt(state.I64, 0, 0), "race_preferred_done");
        LlvmBasicBlockHandle preferredPendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_preferred_pending");
        LlvmApi.BuildStore(builder, preferredTask, resultTaskSlot);
        LlvmApi.BuildCondBr(builder, preferredDone, resultBlock, preferredPendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, preferredPendingBlock);
        LlvmApi.BuildStore(builder, preferredTail, listSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), pendingCountSlot);
        LlvmApi.BuildBr(builder, scanCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, preferredNotFoundBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), preferredWaitHandleSlot);
        LlvmApi.BuildBr(builder, scanCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, scanCheckBlock);
        LlvmValueHandle scanCursor = LlvmApi.BuildLoad2(builder, state.I64, listSlot, "race_scan_cursor");
        LlvmValueHandle scanDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, scanCursor, LlvmApi.ConstInt(state.I64, 0, 0), "race_scan_done");
        LlvmApi.BuildCondBr(builder, scanDone, afterScanBlock, scanBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, scanBodyBlock);
        LlvmValueHandle scanNode = LlvmApi.BuildLoad2(builder, state.I64, listSlot, "race_scan_node");
        LlvmValueHandle raceTask = LoadMemory(state, scanNode, 0, "race_task");
        LlvmValueHandle raceTail = LoadMemory(state, scanNode, 8, "race_tail");
        LlvmApi.BuildStore(builder, raceTail, listSlot);
        EmitNetworkingRuntimeCall(state, "ashes_step_task_until_wait_or_done", [raceTask], "race_step_task");
        LlvmValueHandle raceState = LoadMemory(state, raceTask, TaskStructLayout.StateIndex, "race_state");
        LlvmValueHandle raceDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, raceState, LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateCompleted), 1), "race_task_done");
        LlvmApi.BuildStore(builder, raceTask, resultTaskSlot);
        LlvmApi.BuildCondBr(builder, raceDone, resultBlock, pendingIncrementBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingIncrementBlock);
        LlvmValueHandle pendingCount = LlvmApi.BuildLoad2(builder, state.I64, pendingCountSlot, "race_pending_count_value");
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, pendingCount, LlvmApi.ConstInt(state.I64, 1, 0), "race_pending_count_next"), pendingCountSlot);
        LlvmApi.BuildBr(builder, scanCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, resultBlock);
        LlvmValueHandle resultTask = LlvmApi.BuildLoad2(builder, state.I64, resultTaskSlot, "race_result_task_value");
        LlvmValueHandle raceResult = LoadMemory(state, resultTask, TaskStructLayout.ResultSlot, "race_task_result");
        LlvmApi.BuildStore(builder, raceResult, resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, afterScanBlock);
        LlvmValueHandle pendingAfterScan = LlvmApi.BuildLoad2(builder, state.I64, pendingCountSlot, "race_pending_after_scan");
        LlvmValueHandle hasPending = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, pendingAfterScan, LlvmApi.ConstInt(state.I64, 0, 0), "race_has_pending");
        LlvmApi.BuildCondBr(builder, hasPending, waitBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, waitBlock);
        LlvmValueHandle preferredWaitHandleAfterWait = EmitNetworkingRuntimeCall(state, "ashes_wait_pending_task_list", [taskListPtr], "race_wait_pending");
        LlvmApi.BuildStore(builder, preferredWaitHandleAfterWait, preferredWaitHandleSlot);
        LlvmApi.BuildStore(builder, taskListPtr, preferredCursorSlot);
        LlvmApi.BuildStore(builder, taskListPtr, listSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), pendingCountSlot);
        LlvmApi.BuildBr(builder, preferredCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        LlvmValueHandle finalResult = LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "race_final");
        return EmitCreateCompletedTask(state, finalResult);
    }
}
