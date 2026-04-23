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
        LlvmApi.BuildCondBr(builder, isTcpClose, tcpCloseBlock, checkHttpGetBlock);

        LlvmApi.PositionBuilderAtEnd(builder, tcpCloseBlock);
        LlvmApi.BuildStore(builder,
            EmitNetworkingRuntimeCall(state, "ashes_step_tcp_close_task", [taskPtr], prefix + "_tcp_close_status"),
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
        LlvmValueHandle shouldWait = LlvmApi.BuildOr(builder, isReadWait, isWriteWait, prefix + "_should_wait");

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
            LlvmValueHandle eventMask = LlvmApi.BuildSelect(builder, isReadWait, readMask, writeMask, prefix + "_event_mask");
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
                EmitLinuxSyscall6(state, Arm64SyscallEpollPwait,
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
            LlvmTypeHandle pollfdType = LlvmApi.ArrayType2(state.I8, 16);
            LlvmValueHandle pollfdStorage = LlvmApi.BuildAlloca(builder, pollfdType, prefix + "_pollfd_storage");
            LlvmValueHandle pollfdPtr = GetArrayElementPointer(state, pollfdType, pollfdStorage, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_pollfd_ptr");
            LlvmApi.BuildStore(builder, waitHandle, LlvmApi.BuildBitCast(builder, pollfdPtr, state.I64Ptr, prefix + "_pollfd_socket_ptr"));
            LlvmValueHandle pollEventPtr = LlvmApi.BuildBitCast(builder,
                LlvmApi.BuildGEP2(builder, state.I8, pollfdPtr, [LlvmApi.ConstInt(state.I64, 8, 0)], prefix + "_pollfd_event_byte"),
                LlvmApi.PointerTypeInContext(state.Target.Context, 0),
                prefix + "_pollfd_event_ptr");
            LlvmTypeHandle i16 = LlvmApi.Int16TypeInContext(state.Target.Context);
            LlvmTypeHandle i16Ptr = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
            LlvmValueHandle pollMask = LlvmApi.BuildSelect(builder,
                isReadWait,
                LlvmApi.ConstInt(i16, 0x0100, 0),
                LlvmApi.ConstInt(i16, 0x0010, 0),
                prefix + "_poll_mask");
            LlvmApi.BuildStore(builder, pollMask, LlvmApi.BuildBitCast(builder, pollEventPtr, i16Ptr, prefix + "_poll_mask_ptr"));
            LlvmApi.BuildStore(builder, LlvmApi.ConstInt(i16, 0, 0), LlvmApi.BuildBitCast(builder,
                LlvmApi.BuildGEP2(builder, state.I8, pollfdPtr, [LlvmApi.ConstInt(state.I64, 10, 0)], prefix + "_pollfd_revents_byte"),
                i16Ptr,
                prefix + "_pollfd_revents_ptr"));
            EmitWindowsWsaPoll(state,
                LlvmApi.BuildBitCast(builder, pollfdPtr, state.I8Ptr, prefix + "_pollfd_i8"),
                LlvmApi.ConstInt(state.I64, 1, 0),
                LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1),
                prefix + "_wsapoll_wait");
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

        // Allocas for loop state
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "all_res");
        LlvmValueHandle listSlot = LlvmApi.BuildAlloca(builder, state.I64, "all_list");
        LlvmValueHandle revSrcSlot = LlvmApi.BuildAlloca(builder, state.I64, "all_rsrc");
        LlvmValueHandle revDstSlot = LlvmApi.BuildAlloca(builder, state.I64, "all_rdst");
        LlvmValueHandle taskResSlot = LlvmApi.BuildAlloca(builder, state.I64, "all_task_res");
        LlvmValueHandle failureSlot = LlvmApi.BuildAlloca(builder, state.I64, "all_failure");

        // Initialize: acc = 0 (Nil), list = input
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);
        LlvmApi.BuildStore(builder, taskListPtr, listSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), revDstSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), failureSlot);

        // Create all blocks
        LlvmBasicBlockHandle chkBlk = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_chk");
        LlvmBasicBlockHandle bodyBlk = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_body");
        LlvmBasicBlockHandle afterRunBlk = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_after_run");
        LlvmBasicBlockHandle errorBlk = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_error");
        LlvmBasicBlockHandle consBlk = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_cons");
        LlvmBasicBlockHandle revIBlk = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_revi");
        LlvmBasicBlockHandle revCBlk = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_revc");
        LlvmBasicBlockHandle revBBlk = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_revb");
        LlvmBasicBlockHandle doneBlk = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_done");

        LlvmApi.BuildBr(builder, chkBlk);

        // --- Check: is current list Nil (== 0)? ---
        LlvmApi.PositionBuilderAtEnd(builder, chkBlk);
        LlvmValueHandle cur = LlvmApi.BuildLoad2(builder, state.I64, listSlot, "all_cur");
        LlvmValueHandle isNil = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, cur, LlvmApi.ConstInt(state.I64, 0, 0), "all_is_nil");
        LlvmApi.BuildCondBr(builder, isNil, revIBlk, bodyBlk);

        // --- Body: extract head (offset 0) and tail (offset 8), run task ---
        LlvmApi.PositionBuilderAtEnd(builder, bodyBlk);
        LlvmValueHandle curBody = LlvmApi.BuildLoad2(builder, state.I64, listSlot, "all_cur_body");
        LlvmValueHandle headTask = LoadMemory(state, curBody, 0, "all_head");
        LlvmValueHandle tailList = LoadMemory(state, curBody, 8, "all_tail");
        LlvmApi.BuildStore(builder, tailList, listSlot);

        // Run the head task. EmitRunTask creates blocks and leaves builder at run_task_done.
        LlvmValueHandle taskResult = EmitRunTask(state, headTask);
        // Builder is now at run_task_done block. Store result and branch to afterRun.
        LlvmApi.BuildStore(builder, taskResult, taskResSlot);
        LlvmApi.BuildBr(builder, afterRunBlk);

        // --- After run: propagate Error immediately, otherwise cons the Ok payload ---
        LlvmApi.PositionBuilderAtEnd(builder, afterRunBlk);
        LlvmValueHandle taskRes = LlvmApi.BuildLoad2(builder, state.I64, taskResSlot, "all_task_res_val");
        LlvmValueHandle taskResTag = LoadMemory(state, taskRes, 0, "all_task_res_tag");
        LlvmValueHandle isError = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, taskResTag, LlvmApi.ConstInt(state.I64, 0, 0), "all_task_res_is_error");
        LlvmApi.BuildCondBr(builder, isError, errorBlk, consBlk);

        LlvmApi.PositionBuilderAtEnd(builder, errorBlk);
        LlvmApi.BuildStore(builder, taskRes, failureSlot);
        LlvmApi.BuildBr(builder, doneBlk);

        LlvmApi.PositionBuilderAtEnd(builder, consBlk);
        LlvmValueHandle taskResValue = LoadMemory(state, taskRes, 8, "all_task_res_ok_value");
        LlvmValueHandle prevAcc = LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "all_prev_acc");
        // Cons cell: [head @0, tail @8] = 16 bytes
        LlvmValueHandle consNode = EmitAlloc(state, 16);
        StoreMemory(state, consNode, 0, taskResValue, "all_cons_head");
        StoreMemory(state, consNode, 8, prevAcc, "all_cons_tail");
        LlvmApi.BuildStore(builder, consNode, resultSlot);
        LlvmApi.BuildBr(builder, chkBlk);

        // --- Rev init: start reversing the accumulated (reversed) list ---
        LlvmApi.PositionBuilderAtEnd(builder, revIBlk);
        LlvmValueHandle revSrc = LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "all_rev_src_init");
        LlvmApi.BuildStore(builder, revSrc, revSrcSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), revDstSlot); // Nil
        LlvmApi.BuildBr(builder, revCBlk);

        // --- Rev check: is source Nil (== 0)? ---
        LlvmApi.PositionBuilderAtEnd(builder, revCBlk);
        LlvmValueHandle rsCur = LlvmApi.BuildLoad2(builder, state.I64, revSrcSlot, "all_rs_cur");
        LlvmValueHandle rsIsNil = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, rsCur, LlvmApi.ConstInt(state.I64, 0, 0), "all_rs_nil");
        LlvmApi.BuildCondBr(builder, rsIsNil, doneBlk, revBBlk);

        // --- Rev body: move head from src to dst ---
        LlvmApi.PositionBuilderAtEnd(builder, revBBlk);
        LlvmValueHandle rbSrc = LlvmApi.BuildLoad2(builder, state.I64, revSrcSlot, "all_rb_src");
        LlvmValueHandle rbHead = LoadMemory(state, rbSrc, 0, "all_rb_head");
        LlvmValueHandle rbTail = LoadMemory(state, rbSrc, 8, "all_rb_tail");
        LlvmValueHandle rbDst = LlvmApi.BuildLoad2(builder, state.I64, revDstSlot, "all_rb_dst");
        LlvmValueHandle rbCons = EmitAlloc(state, 16);
        StoreMemory(state, rbCons, 0, rbHead, "all_rb_cons_head");
        StoreMemory(state, rbCons, 8, rbDst, "all_rb_cons_tail");
        LlvmApi.BuildStore(builder, rbTail, revSrcSlot);
        LlvmApi.BuildStore(builder, rbCons, revDstSlot);
        LlvmApi.BuildBr(builder, revCBlk);

        // --- Done: wrap reversed list in Ok(...) and return a completed task ---
        LlvmApi.PositionBuilderAtEnd(builder, doneBlk);
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

        // Alloca to hold the result across block boundaries
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "race_res");

        // Check if list is Nil (== 0)
        LlvmValueHandle isNil = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, taskListPtr, LlvmApi.ConstInt(state.I64, 0, 0), "race_is_nil");

        LlvmBasicBlockHandle emptyBlk = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_empty");
        LlvmBasicBlockHandle nonEmptyBlk = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_nonempty");
        LlvmBasicBlockHandle afterRunBlk = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_after_run");
        LlvmBasicBlockHandle doneBlk = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_done");

        LlvmApi.BuildCondBr(builder, isNil, emptyBlk, nonEmptyBlk);

        // --- Empty list: result = Ok(Unit) ---
        LlvmApi.PositionBuilderAtEnd(builder, emptyBlk);
        LlvmApi.BuildStore(builder, EmitResultOk(state, LlvmApi.ConstInt(state.I64, 0, 0)), resultSlot);
        LlvmApi.BuildBr(builder, doneBlk);

        // --- Non-empty: extract and run first task (head @offset 0) ---
        LlvmApi.PositionBuilderAtEnd(builder, nonEmptyBlk);
        LlvmValueHandle firstTask = LoadMemory(state, taskListPtr, 0, "race_first");
        LlvmValueHandle taskResult = EmitRunTask(state, firstTask);
        // Builder is now at run_task_done block
        LlvmApi.BuildStore(builder, taskResult, resultSlot);
        LlvmApi.BuildBr(builder, afterRunBlk);

        // --- After run: merge with empty path ---
        LlvmApi.PositionBuilderAtEnd(builder, afterRunBlk);
        LlvmApi.BuildBr(builder, doneBlk);

        // --- Done: wrap in completed task ---
        LlvmApi.PositionBuilderAtEnd(builder, doneBlk);
        LlvmValueHandle finalResult = LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "race_final");
        return EmitCreateCompletedTask(state, finalResult);
    }
}
