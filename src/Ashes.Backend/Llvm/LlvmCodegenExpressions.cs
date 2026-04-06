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

    private static LlvmValueHandle EmitMakeClosure(LlvmCodegenState state, string funcLabel, LlvmValueHandle envPtr)
    {
        LlvmValueHandle closurePtr = EmitAlloc(state, 16);
        LlvmValueHandle codePtr = LlvmApi.BuildPtrToInt(state.Target.Builder, state.LiftedFunctions[funcLabel], state.I64, $"closure_code_{funcLabel}");
        StoreMemory(state, closurePtr, 0, codePtr, $"closure_code_store_{funcLabel}");
        StoreMemory(state, closurePtr, 8, envPtr, $"closure_env_store_{funcLabel}");
        return closurePtr;
    }

    private static LlvmValueHandle EmitCallClosure(LlvmCodegenState state, LlvmValueHandle closurePtr, LlvmValueHandle argValue)
    {
        LlvmValueHandle codePtr = LoadMemory(state, closurePtr, 0, "closure_code");
        LlvmValueHandle envPtr = LoadMemory(state, closurePtr, 8, "closure_env");
        LlvmTypeHandle closureFunctionType = LlvmApi.FunctionType(state.I64, [state.I64, state.I64]);
        LlvmTypeHandle closureFunctionPtrType = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmValueHandle typedCodePtr = LlvmApi.BuildIntToPtr(state.Target.Builder, codePtr, closureFunctionPtrType, "closure_code_ptr");
        return LlvmApi.BuildCall2(state.Target.Builder,
            closureFunctionType,
            typedCodePtr,
            new[] { envPtr, argValue },
            "closure_call");
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
            new[] { exitCode },
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

    // ── Async / Task support (Phase B) ──────────────────────────────────

    /// <summary>
    /// CreateTask: allocate a task/state struct and initialize it.
    /// Layout: [state_index(0), coroutine_fn, result(0), awaited_task(0), captures...]
    /// The closure temp is [fn_ptr, env_ptr]. We unpack it and copy captures.
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

        // Copy captured env variables from closure env into task struct
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

        return taskPtr;
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
        LlvmApi.BuildCondBr(builder, isDone, doneBlock, stepBlock);

        // --- Step block: call the coroutine ---
        LlvmApi.PositionBuilderAtEnd(builder, stepBlock);
        LlvmValueHandle coroutineFn = LoadMemory(state, taskPtr, TaskStructLayout.CoroutineFn, "run_coroutine_fn");
        LlvmTypeHandle coroutineFnType = LlvmApi.FunctionType(state.I64, [state.I64, state.I64]);
        LlvmTypeHandle coroutineFnPtrType = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmValueHandle typedFnPtr = LlvmApi.BuildIntToPtr(builder, coroutineFn, coroutineFnPtrType, "run_fn_ptr");
        LlvmValueHandle status = LlvmApi.BuildCall2(builder,
            coroutineFnType,
            typedFnPtr,
            new[] { taskPtr, LlvmApi.ConstInt(state.I64, 0, 0) },
            "run_status");

        // Check status: 0 = SUSPENDED, 1 = COMPLETED
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle isSuspended = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            status, zero, "run_is_suspended");
        LlvmApi.BuildCondBr(builder, isSuspended, suspendedBlock, doneBlock);

        // --- Suspended block: run the awaited sub-task, then resume ---
        LlvmApi.PositionBuilderAtEnd(builder, suspendedBlock);
        LlvmValueHandle awaitedTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, "run_awaited_task");

        // Recursively run the awaited sub-task to completion
        LlvmValueHandle subResult = EmitRunTaskRecursive(state, awaitedTask);

        // Store the sub-task's result into our task's result slot
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot, subResult, "run_sub_result_store");

        // Loop back to step the coroutine again
        LlvmApi.BuildBr(builder, stepBlock);

        // --- Done block: extract and return the result ---
        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LoadMemory(state, taskPtr, TaskStructLayout.ResultSlot, "run_task_result");
    }

    /// <summary>
    /// Helper: recursively run a task to completion. This is the same logic as EmitRunTask
    /// but implemented as a recursive call to a shared runtime function.
    /// For simplicity in Phase B, we inline the same pattern.
    /// </summary>
    private static LlvmValueHandle EmitRunTaskRecursive(LlvmCodegenState state, LlvmValueHandle taskPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        // Create blocks for the sub-task run loop
        LlvmBasicBlockHandle subCheckBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "sub_run_check");
        LlvmBasicBlockHandle subStepBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "sub_run_step");
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
        LlvmApi.BuildCondBr(builder, isDone, subDoneBlock, subStepBlock);

        // --- Step: call coroutine ---
        LlvmApi.PositionBuilderAtEnd(builder, subStepBlock);
        LlvmValueHandle coroutineFn = LoadMemory(state, taskPtr, TaskStructLayout.CoroutineFn, "sub_coroutine_fn");
        LlvmTypeHandle coroutineFnType = LlvmApi.FunctionType(state.I64, [state.I64, state.I64]);
        LlvmTypeHandle coroutineFnPtrType = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmValueHandle typedFnPtr = LlvmApi.BuildIntToPtr(builder, coroutineFn, coroutineFnPtrType, "sub_fn_ptr");
        LlvmValueHandle status = LlvmApi.BuildCall2(builder,
            coroutineFnType,
            typedFnPtr,
            new[] { taskPtr, LlvmApi.ConstInt(state.I64, 0, 0) },
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

        LlvmBasicBlockHandle nestedDoneBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "nested_done");
        LlvmBasicBlockHandle nestedStepBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "nested_step");

        LlvmApi.BuildCondBr(builder, nestedIsDone, nestedDoneBlock, nestedStepBlock);

        // --- Nested step: call nested coroutine in a loop ---
        LlvmApi.PositionBuilderAtEnd(builder, nestedStepBlock);
        LlvmValueHandle nestedFn = LoadMemory(state, awaitedTask, TaskStructLayout.CoroutineFn, "nested_fn");
        LlvmValueHandle nestedFnPtr = LlvmApi.BuildIntToPtr(builder, nestedFn, coroutineFnPtrType, "nested_fn_ptr");
        LlvmValueHandle nestedStatus = LlvmApi.BuildCall2(builder,
            coroutineFnType,
            nestedFnPtr,
            new[] { awaitedTask, LlvmApi.ConstInt(state.I64, 0, 0) },
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
}
