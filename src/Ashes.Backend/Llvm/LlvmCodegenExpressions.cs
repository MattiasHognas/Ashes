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

    private static LlvmValueHandle EmitShiftInt(LlvmCodegenState state, LlvmValueHandle value, LlvmValueHandle amount, bool left, string name)
    {
        var builder = state.Target.Builder;
        var maskedAmount = LlvmApi.BuildAnd(builder, amount, LlvmApi.ConstInt(state.I64, 63, 0), name + "_amount");
        return left
            ? LlvmApi.BuildShl(builder, value, maskedAmount, name)
            : LlvmApi.BuildLShr(builder, value, maskedAmount, name);
    }

    // Closure layout: {code@0, env@8, env_size@16, dropper@24}. The dropper is a code pointer that
    // closes resources moved into the closure's env (set only when a captured resource escapes with
    // the closure — see SetClosureDropper); 0 for ordinary closures. Invoked when the closure is
    // cleaned up (CleanupResource "Function").
    private const int ClosureSizeBytes = 32;

    private static LlvmValueHandle EmitMakeClosure(
        LlvmCodegenState state,
        string funcLabel,
        LlvmValueHandle envPtr,
        int envSizeBytes,
        bool runtimeManaged = false)
    {
        LlvmValueHandle closurePtr = runtimeManaged
            ? EmitRuntimeRcAlloc(state, ClosureSizeBytes, "rc_closure")
            : EmitAlloc(state, ClosureSizeBytes);
        LlvmValueHandle codePtr = LlvmApi.BuildPtrToInt(state.Target.Builder, state.LiftedFunctions[funcLabel], state.I64, $"closure_code_{funcLabel}");
        StoreMemory(state, closurePtr, 0, codePtr, $"closure_code_store_{funcLabel}");
        StoreMemory(state, closurePtr, 8, envPtr, $"closure_env_store_{funcLabel}");
        StoreMemory(state, closurePtr, 16, LlvmApi.ConstInt(state.I64, (ulong)envSizeBytes, 0), $"closure_env_size_store_{funcLabel}");
        StoreMemory(state, closurePtr, 24, LlvmApi.ConstInt(state.I64, 0, 0), $"closure_dropper_store_{funcLabel}");
        return closurePtr;
    }

    private static bool EmitRuntimeRcClosureDrop(LlvmCodegenState state, LlvmValueHandle closurePtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle envSize = LoadMemory(state, closurePtr, 16, "rc_closure_env_size");
        LlvmValueHandle hasEnv = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, envSize,
            LlvmApi.ConstInt(state.I64, 0, 0), "rc_closure_has_env");
        LlvmBasicBlockHandle dropEnvBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "rc_closure_drop_env");
        LlvmBasicBlockHandle dropClosureBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "rc_closure_drop_value");
        LlvmApi.BuildCondBr(builder, hasEnv, dropEnvBlock, dropClosureBlock);

        LlvmApi.PositionBuilderAtEnd(builder, dropEnvBlock);
        LlvmValueHandle envPtr = LoadMemory(state, closurePtr, 8, "rc_closure_env");
        EmitRuntimeRcDrop(state, envPtr);
        LlvmApi.BuildBr(state.Target.Builder, dropClosureBlock);

        LlvmApi.PositionBuilderAtEnd(state.Target.Builder, dropClosureBlock);
        return EmitRuntimeRcDrop(state, closurePtr);
    }

    private static LlvmValueHandle EmitMakeClosureStack(LlvmCodegenState state, string funcLabel, LlvmValueHandle envPtr, int envSizeBytes)
    {
        LlvmValueHandle closurePtr = EmitStackAlloc(state, ClosureSizeBytes, $"closure_stack_{funcLabel}");
        LlvmValueHandle codePtr = LlvmApi.BuildPtrToInt(state.Target.Builder, state.LiftedFunctions[funcLabel], state.I64, $"closure_stack_code_{funcLabel}");
        StoreMemory(state, closurePtr, 0, codePtr, $"closure_stack_code_store_{funcLabel}");
        StoreMemory(state, closurePtr, 8, envPtr, $"closure_stack_env_store_{funcLabel}");
        StoreMemory(state, closurePtr, 16, LlvmApi.ConstInt(state.I64, (ulong)envSizeBytes, 0), $"closure_stack_env_size_store_{funcLabel}");
        StoreMemory(state, closurePtr, 24, LlvmApi.ConstInt(state.I64, 0, 0), $"closure_stack_dropper_store_{funcLabel}");
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

    // Direct call of a statically-known closure body (IrInst.CallKnown): same (env, arg) → i64
    // convention as EmitCallClosure, but the callee is the lifted function itself, so LLVM's
    // inliner can see (and inline) it. The env value is the one the closure would have captured.
    private static LlvmValueHandle EmitCallKnown(LlvmCodegenState state, string funcLabel, LlvmValueHandle envValue, LlvmValueHandle argValue, bool isTailCall = false)
    {
        LlvmTypeHandle closureFunctionType = LlvmApi.FunctionType(state.I64, [state.I64, state.I64]);
        LlvmValueHandle callee = state.LiftedFunctions[funcLabel];
        LlvmValueHandle callInst = LlvmApi.BuildCall2(state.Target.Builder,
            closureFunctionType,
            callee,
            [envValue, argValue],
            "known_call");
        if (isTailCall)
        {
            LlvmApi.SetTailCall(callInst, 1);
        }

        return callInst;
    }

    private static LlvmValueHandle EmitToCString(LlvmCodegenState state, LlvmValueHandle stringRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle length = LoadStringLength(state, stringRef, "ffi_cstr_len");
        LlvmValueHandle sizeWithTerminator = LlvmApi.BuildAdd(builder, length, LlvmApi.ConstInt(state.I64, 1, 0), "ffi_cstr_size");
        LlvmValueHandle destRef = EmitAllocDynamic(state, sizeWithTerminator);
        LlvmValueHandle destBytes = LlvmApi.BuildIntToPtr(builder, destRef, state.I8Ptr, "ffi_cstr_dest");
        LlvmValueHandle sourceBytes = GetStringBytesPointer(state, stringRef, "ffi_cstr_src");
        EmitCopyBytes(state, destBytes, sourceBytes, length, "ffi_cstr_copy");
        LlvmValueHandle terminatorPtr = LlvmApi.BuildGEP2(builder, state.I8, destBytes, [length], "ffi_cstr_terminator_ptr");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I8, 0, 0), terminatorPtr);
        return destRef;
    }

    private static LlvmValueHandle EmitCallExternal(
        LlvmCodegenState state,
        string symbolName,
        string? libraryName,
        IReadOnlyList<int> argTemps,
        IReadOnlyList<FfiType> parameterTypes,
        FfiType returnType)
    {
        LlvmTypeHandle llvmReturnType = GetLlvmFfiType(state, returnType);
        var llvmParameterTypes = parameterTypes.Select(type => GetLlvmFfiType(state, type)).ToArray();
        LlvmTypeHandle functionType = LlvmApi.FunctionType(llvmReturnType, llvmParameterTypes);
        LlvmValueHandle function;
        if (IsWindowsFlavor(state.Flavor))
        {
            if (string.IsNullOrWhiteSpace(libraryName))
            {
                throw new InvalidOperationException($"Windows external symbol '{symbolName}' requires an explicit DLL name using symbol@library.");
            }

            LlvmValueHandle import = GetOrAddWindowsExternalImport(state, symbolName);
            function = LlvmApi.BuildLoad2(state.Target.Builder, state.I8Ptr, import, "ffi_import");
        }
        else
        {
            function = LlvmApi.GetNamedFunction(state.Target.Module, symbolName);
            if (function.Ptr == 0)
            {
                function = LlvmApi.AddFunction(state.Target.Module, symbolName, functionType);
            }
        }

        var args = new LlvmValueHandle[argTemps.Count];
        for (int i = 0; i < argTemps.Count; i++)
        {
            args[i] = ConvertFfiArgument(state, LoadTemp(state, argTemps[i]), parameterTypes[i]);
        }

        LlvmValueHandle result = LlvmApi.BuildCall2(
            state.Target.Builder,
            functionType,
            function,
            args,
            returnType is FfiType.Void ? string.Empty : "ffi_call");
        return returnType is FfiType.Void
            ? LlvmApi.ConstInt(state.I64, 0, 0)
            : returnType is FfiType.Float32
                ? LlvmApi.BuildFPExt(state.Target.Builder, result, state.F64, "ffi_ret_f32")
            : result;
    }

    private static LlvmValueHandle GetOrAddWindowsExternalImport(LlvmCodegenState state, string symbolName)
    {
        if (state.WindowsExternalImports.TryGetValue(symbolName, out LlvmValueHandle import))
        {
            return import;
        }

        import = LlvmApi.AddGlobal(state.Target.Module, LlvmApi.PointerTypeInContext(state.Target.Context, 0), "__imp_" + symbolName);
        LlvmApi.SetLinkage(import, LlvmLinkage.External);
        state.WindowsExternalImports[symbolName] = import;
        return import;
    }

    private static LlvmTypeHandle GetLlvmFfiType(LlvmCodegenState state, FfiType type)
    {
        return type switch
        {
            FfiType.Int => state.I64,
            FfiType.UInt { Bits: 8 } => state.I8,
            FfiType.UInt { Bits: 16 } => LlvmApi.Int16TypeInContext(state.Target.Context),
            FfiType.UInt { Bits: 32 } => state.I32,
            FfiType.UInt { Bits: 64 } => state.I64,
            FfiType.Float => state.F64,
            FfiType.Float32 => state.F32,
            FfiType.Bool => state.I8,
            FfiType.Str => state.I8Ptr,
            FfiType.Opaque { } or FfiType.Ptr { } => state.I8Ptr,
            FfiType.Void => LlvmApi.VoidTypeInContext(state.Target.Context),
            FfiType.UInt uintType => throw new InvalidOperationException($"Unsupported unsigned FFI width '{uintType.Bits}'."),
            _ => throw new InvalidOperationException($"Unknown FFI type '{type.GetType().Name}'.")
        };
    }

    private static LlvmValueHandle ConvertFfiArgument(LlvmCodegenState state, LlvmValueHandle value, FfiType type)
    {
        return type switch
        {
            FfiType.Float => LlvmApi.BuildBitCast(state.Target.Builder, value, state.F64, "ffi_arg_float"),
            FfiType.Float32 => LlvmApi.BuildFPTrunc(state.Target.Builder, LlvmApi.BuildBitCast(state.Target.Builder, value, state.F64, "ffi_arg_f32_source"), state.F32, "ffi_arg_f32"),
            FfiType.Bool => LlvmApi.BuildTrunc(state.Target.Builder, value, state.I8, "ffi_arg_bool"),
            FfiType.UInt { Bits: 8 or 16 or 32 } => LlvmApi.BuildTrunc(state.Target.Builder, value, GetLlvmFfiType(state, type), "ffi_arg_uint"),
            FfiType.Str => LlvmApi.BuildIntToPtr(state.Target.Builder, value, state.I8Ptr, "ffi_arg_str"),
            FfiType.Opaque { } or FfiType.Ptr { } => LlvmApi.BuildIntToPtr(state.Target.Builder, value, state.I8Ptr, "ffi_arg_ptr"),
            _ => value
        };
    }

    private static bool EmitJump(LlvmCodegenState state, string targetLabel)
    {
        LlvmApi.BuildBr(state.Target.Builder, state.GetLabelBlock(targetLabel));
        return true;
    }

    private static bool EmitSwitchTag(
        LlvmCodegenState state,
        LlvmValueHandle tagValue,
        IReadOnlyList<(long Tag, string Label)> cases,
        string defaultLabel)
    {
        LlvmValueHandle switchInst = LlvmApi.BuildSwitch(
            state.Target.Builder,
            tagValue,
            state.GetLabelBlock(defaultLabel),
            (uint)cases.Count);

        foreach (var (tag, label) in cases)
        {
            LlvmValueHandle onValue = LlvmApi.ConstInt(state.I64, unchecked((ulong)tag), 1);
            LlvmApi.AddCase(switchInst, onValue, state.GetLabelBlock(label));
        }

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

    // Async / Task support

    /// <summary>
    /// Zero the run-queue scheduler header slots (<c>ReadyNext</c> / <c>Waiter</c> / <c>ArenaOwner</c>
    /// / <c>LoopResetOk</c>) of a freshly allocated task. These are not part of the legacy layout, so
    /// without this a newly created task carries garbage in them — harmless at -O0 (stack incidentally
    /// zero) but a wild pointer at -O2, where the scheduler reads a bogus Waiter/ArenaOwner and crashes.
    /// </summary>
    private static void EmitZeroSchedulerFields(LlvmCodegenState state, LlvmValueHandle taskPtr)
    {
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        StoreMemory(state, taskPtr, TaskStructLayout.ReadyNext, zero, "task_ready_next_zero");
        StoreMemory(state, taskPtr, TaskStructLayout.Waiter, zero, "task_waiter_zero");
        StoreMemory(state, taskPtr, TaskStructLayout.ArenaOwner, zero, "task_arena_owner_zero");
        StoreMemory(state, taskPtr, TaskStructLayout.LoopResetOk, zero, "task_loop_reset_zero");
    }

    /// <summary>
    /// CreateTask: allocate a task/state struct and initialize it.
    /// Layout: [state_index(0), coroutine_fn, result(0), awaited_task(0), next_task(0), sleep_duration_ms(0), captures...]
    /// The closure temp is [fn_ptr, env_ptr]. We unpack it and copy captures starting at <see cref="TaskStructLayout.HeaderSize"/>.
    /// An async-loop coroutine eligible for the back-edge arena reset gets <c>LoopResetOk</c> = 1
    /// (re-cleared by the scheduler when a composite ancestor shares the arena).
    /// </summary>
    private static LlvmValueHandle EmitCreateTask(LlvmCodegenState state, LlvmValueHandle closurePtr,
        int stateStructSize, int captureCount, bool loopResetEligible = false)
    {
        // Allocate task/state struct
        LlvmValueHandle taskPtr = EmitAlloc(state, stateStructSize);
        EmitCreateTaskInitHeader(state, taskPtr, closurePtr);
        EmitCreateTaskInitScheduler(state, taskPtr, stateStructSize, loopResetEligible);
        EmitCreateTaskCopyCaptures(state, taskPtr, closurePtr, captureCount);
        return taskPtr;
    }

    private static void EmitCreateTaskInitHeader(LlvmCodegenState state, LlvmValueHandle taskPtr, LlvmValueHandle closurePtr)
    {
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
    }

    private static void EmitCreateTaskInitScheduler(LlvmCodegenState state, LlvmValueHandle taskPtr, int stateStructSize, bool loopResetEligible)
    {
        // Copy captured env variables from closure env into task struct
        StoreMemory(state, taskPtr, TaskStructLayout.WaitKind,
            LlvmApi.ConstInt(state.I64, 0, 0), "task_wait_kind_zero");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitHandle,
            LlvmApi.ConstInt(state.I64, 0, 0), "task_wait_handle_zero");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0,
            LlvmApi.ConstInt(state.I64, 0, 0), "task_wait_data0_zero");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData1,
            LlvmApi.ConstInt(state.I64, 0, 0), "task_wait_data1_zero");
        StoreMemory(state, taskPtr, TaskStructLayout.FrameSizeBytes,
            LlvmApi.ConstInt(state.I64, (ulong)stateStructSize, 0), "task_frame_size");
        StoreMemory(state, taskPtr, TaskStructLayout.ArenaCursor,
            LlvmApi.ConstInt(state.I64, 0, 0), "task_arena_cursor_zero");
        StoreMemory(state, taskPtr, TaskStructLayout.ArenaEnd,
            LlvmApi.ConstInt(state.I64, 0, 0), "task_arena_end_zero");
        EmitZeroSchedulerFields(state, taskPtr);
        if (loopResetEligible)
        {
            StoreMemory(state, taskPtr, TaskStructLayout.LoopResetOk,
                LlvmApi.ConstInt(state.I64, 1, 0), "task_loop_reset_ok");
        }
    }

    private static void EmitCreateTaskCopyCaptures(LlvmCodegenState state, LlvmValueHandle taskPtr, LlvmValueHandle closurePtr, int captureCount)
    {
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
        StoreMemory(state, taskPtr, TaskStructLayout.FrameSizeBytes,
            LlvmApi.ConstInt(state.I64, TaskStructLayout.HeaderSize, 0), "ctask_frame_size");
        StoreMemory(state, taskPtr, TaskStructLayout.ArenaCursor,
            LlvmApi.ConstInt(state.I64, 0, 0), "ctask_arena_cursor_zero");
        StoreMemory(state, taskPtr, TaskStructLayout.ArenaEnd,
            LlvmApi.ConstInt(state.I64, 0, 0), "ctask_arena_end_zero");
        EmitZeroSchedulerFields(state, taskPtr);

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
        StoreMemory(state, taskPtr, TaskStructLayout.FrameSizeBytes,
            LlvmApi.ConstInt(state.I64, TaskStructLayout.HeaderSize, 0), prefix + "_frame_size");
        StoreMemory(state, taskPtr, TaskStructLayout.ArenaCursor,
            LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_arena_cursor");
        StoreMemory(state, taskPtr, TaskStructLayout.ArenaEnd,
            LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_arena_end");
        EmitZeroSchedulerFields(state, taskPtr);

        return taskPtr;
    }

    // Shared context for the EmitStepLeafTask networking dispatch cases.
    private readonly record struct StepLeafContext(
        LlvmValueHandle StateIdx,
        LlvmValueHandle StatusSlot,
        LlvmValueHandle TaskPtr,
        LlvmBasicBlockHandle ContinueBlock,
        string Prefix);

    // Up-front basic blocks for the sleep dispatch and TCP-family cases of EmitStepLeafTask.
    private readonly record struct StepLeafBlocksA(
        LlvmBasicBlockHandle SleepBlock,
        LlvmBasicBlockHandle SleepElapsedBlock,
        LlvmBasicBlockHandle SleepPendingBlock,
        LlvmBasicBlockHandle CheckTcpConnectBlock,
        LlvmBasicBlockHandle TcpConnectBlock,
        LlvmBasicBlockHandle CheckTcpSendBlock,
        LlvmBasicBlockHandle TcpSendBlock,
        LlvmBasicBlockHandle CheckTcpReceiveBlock,
        LlvmBasicBlockHandle TcpReceiveBlock,
        LlvmBasicBlockHandle CheckTcpCloseBlock,
        LlvmBasicBlockHandle TcpCloseBlock,
        LlvmBasicBlockHandle CheckTcpListenBlock,
        LlvmBasicBlockHandle TcpListenBlock,
        LlvmBasicBlockHandle CheckTcpAcceptBlock,
        LlvmBasicBlockHandle CheckForkWorkersBlock,
        LlvmBasicBlockHandle ForkWorkersBlock,
        LlvmBasicBlockHandle TcpAcceptBlock);

    // Up-front basic blocks for the TLS/HTTP cases and the tail of EmitStepLeafTask.
    private readonly record struct StepLeafBlocksB(
        LlvmBasicBlockHandle CheckTlsConnectBlock,
        LlvmBasicBlockHandle TlsConnectBlock,
        LlvmBasicBlockHandle CheckTlsHandshakeBlock,
        LlvmBasicBlockHandle TlsHandshakeBlock,
        LlvmBasicBlockHandle CheckTlsServerHandshakeBlock,
        LlvmBasicBlockHandle TlsServerHandshakeBlock,
        LlvmBasicBlockHandle CheckTlsSendBlock,
        LlvmBasicBlockHandle TlsSendBlock,
        LlvmBasicBlockHandle CheckTlsReceiveBlock,
        LlvmBasicBlockHandle TlsReceiveBlock,
        LlvmBasicBlockHandle CheckTlsCloseBlock,
        LlvmBasicBlockHandle TlsCloseBlock,
        LlvmBasicBlockHandle CheckHttpGetBlock,
        LlvmBasicBlockHandle HttpGetBlock,
        LlvmBasicBlockHandle CheckHttpPostBlock,
        LlvmBasicBlockHandle HttpPostBlock,
        LlvmBasicBlockHandle InvalidBlock,
        LlvmBasicBlockHandle ContinueBlock);

    private static StepLeafBlocksA EmitStepLeafBlocksA(LlvmCodegenState state, string prefix)
    {
        return new StepLeafBlocksA(
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_sleep"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_sleep_elapsed"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_sleep_pending"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tcp_connect"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tcp_connect"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tcp_send"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tcp_send"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tcp_receive"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tcp_receive"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tcp_close"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tcp_close"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tcp_listen"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tcp_listen"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tcp_accept"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_fork_workers"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_fork_workers"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tcp_accept"));
    }

    private static StepLeafBlocksB EmitStepLeafBlocksB(LlvmCodegenState state, string prefix)
    {
        return new StepLeafBlocksB(
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tls_connect"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tls_connect"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tls_handshake"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tls_handshake"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tls_server_handshake"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tls_server_handshake"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tls_send"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tls_send"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tls_receive"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tls_receive"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tls_close"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tls_close"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_http_get"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_http_get"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_http_post"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_http_post"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_invalid"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_continue"));
    }

    // Emits one leaf networking case: a check block (state match) and its action block (runtime step call).
    private static void EmitStepLeafNetworkingCase(LlvmCodegenState state, in StepLeafContext ctx,
        LlvmBasicBlockHandle checkBlock, long taskState, LlvmBasicBlockHandle actionBlock,
        LlvmBasicBlockHandle nextBlock, string runtimeFn, string tag)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, checkBlock);
        LlvmValueHandle isMatch = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            ctx.StateIdx,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)taskState), 1),
            ctx.Prefix + "_is_" + tag);
        LlvmApi.BuildCondBr(builder, isMatch, actionBlock, nextBlock);

        LlvmApi.PositionBuilderAtEnd(builder, actionBlock);
        LlvmApi.BuildStore(builder,
            EmitNetworkingRuntimeCall(state, runtimeFn, [ctx.TaskPtr], ctx.Prefix + "_" + tag + "_status"),
            ctx.StatusSlot);
        LlvmApi.BuildBr(builder, ctx.ContinueBlock);
    }

    private static LlvmValueHandle EmitStepLeafTask(LlvmCodegenState state, LlvmValueHandle taskPtr, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle stateIdx = LoadMemory(state, taskPtr, TaskStructLayout.StateIndex, prefix + "_state_idx");
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_status_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), statusSlot);

        StepLeafBlocksA a = EmitStepLeafBlocksA(state, prefix);
        StepLeafBlocksB b = EmitStepLeafBlocksB(state, prefix);

        LlvmValueHandle isSleep = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            stateIdx,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateSleeping), 1),
            prefix + "_is_sleep");
        LlvmApi.BuildCondBr(builder, isSleep, a.SleepBlock, a.CheckTcpConnectBlock);

        EmitStepLeafSleepPhase(state, taskPtr, statusSlot, a, b.ContinueBlock, prefix);

        var ctx = new StepLeafContext(stateIdx, statusSlot, taskPtr, b.ContinueBlock, prefix);
        EmitStepLeafTcpCases(state, ctx, a, b);
        EmitStepLeafTlsHttpCases(state, ctx, b);
        EmitStepLeafInvalidPhase(state, taskPtr, statusSlot, b.InvalidBlock, b.ContinueBlock, prefix);

        LlvmApi.PositionBuilderAtEnd(builder, b.ContinueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, statusSlot, prefix + "_status");
    }

    private static void EmitStepLeafSleepPhase(LlvmCodegenState state, LlvmValueHandle taskPtr,
        LlvmValueHandle statusSlot, in StepLeafBlocksA a, LlvmBasicBlockHandle continueBlock, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        // Cooperative sleep: instead of blocking the whole thread on nanosleep, a sleeping leaf yields.
        // SleepDurationMs holds the remaining milliseconds. While > 0 the leaf stays pending with
        // WaitKind = WaitTimer, so the scheduler advances sibling tasks and waits only until the
        // earliest deadline (decrementing SleepDurationMs there). Once the remaining time has elapsed,
        // the leaf completes with Ok(0) — matching the old blocking result.
        LlvmApi.PositionBuilderAtEnd(builder, a.SleepBlock);
        LlvmValueHandle sleepMs = LoadMemory(state, taskPtr, TaskStructLayout.SleepDurationMs, prefix + "_sleep_ms");
        LlvmValueHandle sleepElapsed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, sleepMs, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_sleep_elapsed_cmp");
        LlvmApi.BuildCondBr(builder, sleepElapsed, a.SleepElapsedBlock, a.SleepPendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, a.SleepElapsedBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot,
            EmitResultOk(state, LlvmApi.ConstInt(state.I64, 0, 0)), prefix + "_sleep_result");
        StoreMemory(state, taskPtr, TaskStructLayout.StateIndex,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateCompleted), 1), prefix + "_sleep_done");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitKind,
            LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitNone, 0), prefix + "_sleep_clear_wait");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), statusSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, a.SleepPendingBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.WaitKind,
            LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTimer, 0), prefix + "_sleep_mark_timer");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), statusSlot);
        LlvmApi.BuildBr(builder, continueBlock);
    }

    private static void EmitStepLeafTcpCases(LlvmCodegenState state, in StepLeafContext ctx, in StepLeafBlocksA a, in StepLeafBlocksB b)
    {
        EmitStepLeafNetworkingCase(state, ctx, a.CheckTcpConnectBlock, TaskStructLayout.StateTcpConnect, a.TcpConnectBlock, a.CheckTcpSendBlock, "ashes_step_tcp_connect_task", "tcp_connect");
        EmitStepLeafNetworkingCase(state, ctx, a.CheckTcpSendBlock, TaskStructLayout.StateTcpSend, a.TcpSendBlock, a.CheckTcpReceiveBlock, "ashes_step_tcp_send_task", "tcp_send");
        EmitStepLeafNetworkingCase(state, ctx, a.CheckTcpReceiveBlock, TaskStructLayout.StateTcpReceive, a.TcpReceiveBlock, a.CheckTcpCloseBlock, "ashes_step_tcp_receive_task", "tcp_receive");
        EmitStepLeafNetworkingCase(state, ctx, a.CheckTcpCloseBlock, TaskStructLayout.StateTcpClose, a.TcpCloseBlock, a.CheckTcpListenBlock, "ashes_step_tcp_close_task", "tcp_close");
        EmitStepLeafNetworkingCase(state, ctx, a.CheckTcpListenBlock, TaskStructLayout.StateTcpListen, a.TcpListenBlock, a.CheckTcpAcceptBlock, "ashes_step_tcp_listen_task", "tcp_listen");
        EmitStepLeafNetworkingCase(state, ctx, a.CheckTcpAcceptBlock, TaskStructLayout.StateTcpAccept, a.TcpAcceptBlock, a.CheckForkWorkersBlock, "ashes_step_tcp_accept_task", "tcp_accept");
        EmitStepLeafNetworkingCase(state, ctx, a.CheckForkWorkersBlock, TaskStructLayout.StateForkWorkers, a.ForkWorkersBlock, b.CheckTlsConnectBlock, "ashes_step_fork_workers_task", "fork_workers");
    }

    private static void EmitStepLeafTlsHttpCases(LlvmCodegenState state, in StepLeafContext ctx, in StepLeafBlocksB b)
    {
        EmitStepLeafNetworkingCase(state, ctx, b.CheckTlsConnectBlock, TaskStructLayout.StateTlsConnect, b.TlsConnectBlock, b.CheckTlsHandshakeBlock, "ashes_step_tls_connect_task", "tls_connect");
        EmitStepLeafNetworkingCase(state, ctx, b.CheckTlsHandshakeBlock, TaskStructLayout.StateTlsHandshake, b.TlsHandshakeBlock, b.CheckTlsServerHandshakeBlock, "ashes_step_tls_handshake_task", "tls_handshake");
        EmitStepLeafNetworkingCase(state, ctx, b.CheckTlsServerHandshakeBlock, TaskStructLayout.StateTlsServerHandshake, b.TlsServerHandshakeBlock, b.CheckTlsSendBlock, "ashes_step_tls_server_handshake_task", "tls_server_handshake");
        EmitStepLeafNetworkingCase(state, ctx, b.CheckTlsSendBlock, TaskStructLayout.StateTlsSend, b.TlsSendBlock, b.CheckTlsReceiveBlock, "ashes_step_tls_send_task", "tls_send");
        EmitStepLeafNetworkingCase(state, ctx, b.CheckTlsReceiveBlock, TaskStructLayout.StateTlsReceive, b.TlsReceiveBlock, b.CheckTlsCloseBlock, "ashes_step_tls_receive_task", "tls_receive");
        EmitStepLeafNetworkingCase(state, ctx, b.CheckTlsCloseBlock, TaskStructLayout.StateTlsClose, b.TlsCloseBlock, b.CheckHttpGetBlock, "ashes_step_tls_close_task", "tls_close");
        EmitStepLeafNetworkingCase(state, ctx, b.CheckHttpGetBlock, TaskStructLayout.StateHttpGet, b.HttpGetBlock, b.CheckHttpPostBlock, "ashes_step_http_get_task", "http_get");
        EmitStepLeafNetworkingCase(state, ctx, b.CheckHttpPostBlock, TaskStructLayout.StateHttpPost, b.HttpPostBlock, b.InvalidBlock, "ashes_step_http_post_task", "http_post");
    }

    private static void EmitStepLeafInvalidPhase(LlvmCodegenState state, LlvmValueHandle taskPtr,
        LlvmValueHandle statusSlot, LlvmBasicBlockHandle invalidBlock, LlvmBasicBlockHandle continueBlock, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, invalidBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot,
            EmitResultError(state, EmitHeapStringLiteral(state, "unknown leaf task state")),
            prefix + "_invalid_result");
        StoreMemory(state, taskPtr, TaskStructLayout.StateIndex,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateCompleted), 1), prefix + "_invalid_done");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), statusSlot);
        LlvmApi.BuildBr(builder, continueBlock);
    }

    // Up-front slot + basic blocks for EmitStepTaskUntilPendingOrDone.
    private readonly record struct StepUntilLayout(
        LlvmValueHandle StatusSlot,
        LlvmBasicBlockHandle CheckBlock,
        LlvmBasicBlockHandle NotDoneBlock,
        LlvmBasicBlockHandle LeafBlock,
        LlvmBasicBlockHandle ResolveAwaitBlock,
        LlvmBasicBlockHandle StepAwaitedBlock,
        LlvmBasicBlockHandle StepBlock,
        LlvmBasicBlockHandle AwaitedDoneBlock,
        LlvmBasicBlockHandle LeafPendingBlock,
        LlvmBasicBlockHandle AwaitedPendingBlock,
        LlvmBasicBlockHandle DoneBlock);

    private static StepUntilLayout EmitStepUntilPrologue(LlvmCodegenState state, string prefix)
    {
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(state.Target.Builder, state.I64, prefix + "_status_slot");
        LlvmApi.BuildStore(state.Target.Builder, LlvmApi.ConstInt(state.I64, 0, 0), statusSlot);
        return new StepUntilLayout(
            statusSlot,
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_not_done"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_leaf"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_resolve_await"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_step_awaited"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_step"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_awaited_done"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_leaf_pending"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_awaited_pending"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done"));
    }

    private static LlvmValueHandle EmitStepTaskUntilPendingOrDone(LlvmCodegenState state, LlvmValueHandle taskPtr, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        StepUntilLayout layout = EmitStepUntilPrologue(state, prefix);
        LlvmApi.BuildBr(builder, layout.CheckBlock);
        EmitStepUntilCheckPhase(state, taskPtr, layout, prefix);
        LlvmValueHandle awaitedTask = EmitStepUntilResolvePhase(state, taskPtr, layout, prefix);
        return EmitStepUntilStepPhase(state, taskPtr, awaitedTask, layout, prefix);
    }

    private static void EmitStepUntilCheckPhase(LlvmCodegenState state, LlvmValueHandle taskPtr, in StepUntilLayout layout, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, layout.CheckBlock);
        LlvmValueHandle stateIdx = LoadMemory(state, taskPtr, TaskStructLayout.StateIndex, prefix + "_state_idx");
        LlvmValueHandle completedConst = LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateCompleted), 1);
        LlvmValueHandle isDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, stateIdx, completedConst, prefix + "_is_done");
        LlvmApi.BuildCondBr(builder, isDone, layout.DoneBlock, layout.NotDoneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.NotDoneBlock);
        LlvmValueHandle isLeaf = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, stateIdx, completedConst, prefix + "_is_leaf");
        LlvmApi.BuildCondBr(builder, isLeaf, layout.LeafBlock, layout.ResolveAwaitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.LeafBlock);
        LlvmValueHandle leafStatus = EmitStepLeafTask(state, taskPtr, prefix + "_leaf_step");
        LlvmValueHandle leafCompleted = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, leafStatus, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_leaf_completed");
        LlvmApi.BuildCondBr(builder, leafCompleted, layout.DoneBlock, layout.LeafPendingBlock);
    }

    private static LlvmValueHandle EmitStepUntilResolvePhase(LlvmCodegenState state, LlvmValueHandle taskPtr, in StepUntilLayout layout, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        // Coroutine: if it is parked on an awaited sub-task from a previous suspend, resolve that
        // sub-task BEFORE resuming — the coroutine's resume reads the awaited result out of ResultSlot
        // blindly, so it must not run until the result is actually ready. (The single-task RunTask
        // driver enforces this by looping on the leaf; the list driver returns to the scheduler between
        // steps, so it re-checks the awaited task on every re-entry via AwaitedTask != 0.)
        LlvmApi.PositionBuilderAtEnd(builder, layout.ResolveAwaitBlock);
        LlvmValueHandle awaitedTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_awaited_task");
        LlvmValueHandle hasAwaited = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, awaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_awaited");
        LlvmApi.BuildCondBr(builder, hasAwaited, layout.StepAwaitedBlock, layout.StepBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.StepAwaitedBlock);
        LlvmValueHandle awaitedStatus = EmitNetworkingRuntimeCall(state, "ashes_step_task_until_wait_or_done", [awaitedTask], prefix + "_awaited_status");
        LlvmValueHandle awaitedCompleted = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, awaitedStatus, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_awaited_completed");
        LlvmApi.BuildCondBr(builder, awaitedCompleted, layout.AwaitedDoneBlock, layout.AwaitedPendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.AwaitedDoneBlock);
        EmitClearLeafTaskWait(state, taskPtr, prefix + "_clear_wait_after_await");
        LlvmValueHandle awaitedResult = LoadMemory(state, awaitedTask, TaskStructLayout.ResultSlot, prefix + "_awaited_result");
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot, awaitedResult, prefix + "_awaited_result_store");
        // Consume the awaited task so the next resume runs the coroutine rather than re-stepping it.
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_awaited_consumed");
        LlvmApi.BuildBr(builder, layout.StepBlock);
        return awaitedTask;
    }

    private static LlvmValueHandle EmitStepUntilStepPhase(LlvmCodegenState state, LlvmValueHandle taskPtr, LlvmValueHandle awaitedTask, in StepUntilLayout layout, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, layout.StepBlock);
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
        // On suspend the coroutine has stored a fresh AwaitedTask; loop back so that awaited task is
        // resolved (and, if already complete, the coroutine immediately resumes).
        LlvmApi.BuildCondBr(builder, suspended, layout.CheckBlock, layout.DoneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.LeafPendingBlock);
        LlvmApi.BuildBr(builder, layout.DoneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.AwaitedPendingBlock);
        LlvmValueHandle waitKind = LoadMemory(state, awaitedTask, TaskStructLayout.WaitKind, prefix + "_awaited_wait_kind");
        LlvmValueHandle waitHandle = LoadMemory(state, awaitedTask, TaskStructLayout.WaitHandle, prefix + "_awaited_wait_handle");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitKind, waitKind, prefix + "_mirror_wait_kind");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitHandle, waitHandle, prefix + "_mirror_wait_handle");
        LlvmApi.BuildBr(builder, layout.DoneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.DoneBlock);
        LlvmValueHandle completedConst = LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateCompleted), 1);
        LlvmValueHandle finalStateIdx = LoadMemory(state, taskPtr, TaskStructLayout.StateIndex, prefix + "_final_state_idx");
        LlvmValueHandle finalDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, finalStateIdx, completedConst, prefix + "_final_done");
        LlvmApi.BuildStore(builder, LlvmApi.BuildZExt(builder, finalDone, state.I64, prefix + "_final_done_i64"), layout.StatusSlot);
        return LlvmApi.BuildLoad2(builder, state.I64, layout.StatusSlot, prefix + "_status");
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

    // Up-front slots + basic blocks for EmitWindowsWaitForPendingSocketTaskList.
    private readonly record struct WindowsSocketWaitLayout(
        LlvmValueHandle PollArrayPtr,
        LlvmValueHandle PollArrayAddress,
        LlvmValueHandle RegisterCursorSlot,
        LlvmValueHandle PollWritePtrSlot,
        LlvmValueHandle ScanIndexSlot,
        LlvmValueHandle ScanPtrSlot,
        LlvmBasicBlockHandle RegisterCheckBlock,
        LlvmBasicBlockHandle RegisterBodyBlock,
        LlvmBasicBlockHandle RegisterStoreBlock,
        LlvmBasicBlockHandle RegisterAdvanceBlock,
        LlvmBasicBlockHandle AfterRegisterBlock,
        LlvmBasicBlockHandle ScanCheckBlock,
        LlvmBasicBlockHandle ScanReadyBlock,
        LlvmBasicBlockHandle ScanAdvanceBlock,
        LlvmBasicBlockHandle DoneBlock);

    private static WindowsSocketWaitLayout EmitWindowsSocketWaitPrologue(LlvmCodegenState state, LlvmValueHandle taskListPtr, LlvmValueHandle totalPending, string prefix)
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
        return new WindowsSocketWaitLayout(
            pollArrayPtr, pollArrayAddress, registerCursorSlot, pollWritePtrSlot, scanIndexSlot, scanPtrSlot,
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_register_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_register_body"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_register_store"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_register_advance"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_after_register"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_scan_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_scan_ready"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_scan_advance"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done"));
    }

    private static void EmitWindowsWaitForPendingSocketTaskList(LlvmCodegenState state, LlvmValueHandle taskListPtr, LlvmValueHandle totalPending, LlvmValueHandle waitResultSlot, string prefix)
    {
        WindowsSocketWaitLayout layout = EmitWindowsSocketWaitPrologue(state, taskListPtr, totalPending, prefix);
        LlvmApi.BuildBr(state.Target.Builder, layout.RegisterCheckBlock);
        EmitWindowsSocketWaitRegisterLoop(state, layout, prefix);
        EmitWindowsSocketWaitScanLoop(state, layout, totalPending, waitResultSlot, prefix);
    }

    private static void EmitWindowsSocketWaitRegisterLoop(LlvmCodegenState state, in WindowsSocketWaitLayout layout, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, layout.RegisterCheckBlock);
        LlvmValueHandle registerCursor = LlvmApi.BuildLoad2(builder, state.I64, layout.RegisterCursorSlot, prefix + "_register_cursor");
        LlvmValueHandle registerDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, registerCursor, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_register_done");
        LlvmApi.BuildCondBr(builder, registerDone, layout.AfterRegisterBlock, layout.RegisterBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.RegisterBodyBlock);
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
        LlvmApi.BuildCondBr(builder, registerShould, layout.RegisterStoreBlock, layout.RegisterAdvanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.RegisterStoreBlock);
        LlvmValueHandle pollWritePtr = LlvmApi.BuildLoad2(builder, state.I64, layout.PollWritePtrSlot, prefix + "_poll_write_ptr");
        LlvmValueHandle eventMask = EmitWindowsPollEventMask(state, registerReadish, prefix + "_event_mask");
        EmitWindowsInitializePollFd(state, pollWritePtr, registerHandle, eventMask, prefix + "_pollfd");
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, pollWritePtr, LlvmApi.ConstInt(state.I64, WindowsPollFdSize, 0), prefix + "_poll_write_ptr_next"), layout.PollWritePtrSlot);
        LlvmApi.BuildBr(builder, layout.RegisterAdvanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.RegisterAdvanceBlock);
        LlvmApi.BuildStore(builder, registerTail, layout.RegisterCursorSlot);
        LlvmApi.BuildBr(builder, layout.RegisterCheckBlock);
    }

    private static void EmitWindowsSocketWaitScanLoop(LlvmCodegenState state, in WindowsSocketWaitLayout layout, LlvmValueHandle totalPending, LlvmValueHandle waitResultSlot, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, layout.AfterRegisterBlock);
        LlvmValueHandle pollResult = LlvmApi.BuildSExt(builder,
            EmitWindowsWsaPoll(state, layout.PollArrayPtr, totalPending, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), prefix + "_wsapoll"),
            state.I64,
            prefix + "_poll_result");
        LlvmValueHandle hasReady = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, pollResult, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_ready");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), layout.ScanIndexSlot);
        LlvmApi.BuildStore(builder, layout.PollArrayAddress, layout.ScanPtrSlot);
        LlvmApi.BuildCondBr(builder, hasReady, layout.ScanCheckBlock, layout.DoneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.ScanCheckBlock);
        LlvmValueHandle scanIndex = LlvmApi.BuildLoad2(builder, state.I64, layout.ScanIndexSlot, prefix + "_scan_index");
        LlvmValueHandle scanDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, scanIndex, totalPending, prefix + "_scan_done");
        LlvmBasicBlockHandle scanBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_scan_body");
        LlvmApi.BuildCondBr(builder, scanDone, layout.DoneBlock, scanBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, scanBodyBlock);
        LlvmValueHandle scanPtr = LlvmApi.BuildLoad2(builder, state.I64, layout.ScanPtrSlot, prefix + "_scan_ptr");
        LlvmValueHandle tailValue = LoadMemory(state, scanPtr, 8, prefix + "_scan_tail_value");
        LlvmValueHandle revents = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildLShr(builder, tailValue, LlvmApi.ConstInt(state.I64, 16, 0), prefix + "_scan_revents_shift"),
            LlvmApi.ConstInt(state.I64, 0xFFFF, 0),
            prefix + "_scan_revents");
        LlvmValueHandle entryReady = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, revents, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_scan_entry_ready");
        LlvmApi.BuildCondBr(builder, entryReady, layout.ScanReadyBlock, layout.ScanAdvanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.ScanReadyBlock);
        LlvmApi.BuildStore(builder, LoadMemory(state, scanPtr, 0, prefix + "_ready_handle"), waitResultSlot);
        LlvmApi.BuildBr(builder, layout.DoneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.ScanAdvanceBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, scanIndex, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_scan_index_next"), layout.ScanIndexSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, scanPtr, LlvmApi.ConstInt(state.I64, WindowsPollFdSize, 0), prefix + "_scan_ptr_next"), layout.ScanPtrSlot);
        LlvmApi.BuildBr(builder, layout.ScanCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.DoneBlock);
    }

    // Up-front slots + basic blocks for EmitWaitForPendingTaskList.
    private readonly record struct WaitPendingLayout(
        LlvmValueHandle CountSlot,
        LlvmValueHandle CursorSlot,
        LlvmValueHandle WaitResultSlot,
        LlvmValueHandle RunnableSlot,
        LlvmBasicBlockHandle CountCheckBlock,
        LlvmBasicBlockHandle CountBodyBlock,
        LlvmBasicBlockHandle CountIncrementBlock,
        LlvmBasicBlockHandle CountAdvanceBlock,
        LlvmBasicBlockHandle AfterCountBlock,
        LlvmBasicBlockHandle WaitBlock,
        LlvmBasicBlockHandle TimerCheckBlock,
        LlvmBasicBlockHandle DoneBlock);

    private static WaitPendingLayout EmitWaitPendingPrologue(LlvmCodegenState state, LlvmValueHandle taskListPtr, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle countSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_count_slot");
        LlvmValueHandle cursorSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_cursor_slot");
        LlvmValueHandle waitResultSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_wait_result_slot");
        // Tracks whether any pending task is immediately runnable (not blocked on a
        // socket). Such tasks have WaitKind == WaitNone and signal a cooperative
        // "yield" (e.g. an HTTP receive that just consumed a buffered chunk and wants
        // to read more). If one exists we must not block in epoll/WSAPoll on the other
        // tasks' sockets, or the runnable task would be starved until an unrelated
        // socket happens to become ready.
        LlvmValueHandle runnableSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_runnable_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), countSlot);
        LlvmApi.BuildStore(builder, taskListPtr, cursorSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), waitResultSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), runnableSlot);
        return new WaitPendingLayout(
            countSlot, cursorSlot, waitResultSlot, runnableSlot,
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_count_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_count_body"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_count_increment"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_count_advance"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_after_count"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_wait"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_timer_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done"));
    }

    private static LlvmValueHandle EmitWaitForPendingTaskList(LlvmCodegenState state, LlvmValueHandle taskListPtr, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        WaitPendingLayout layout = EmitWaitPendingPrologue(state, taskListPtr, prefix);
        LlvmApi.BuildBr(builder, layout.CountCheckBlock);
        EmitWaitPendingCountLoop(state, layout, prefix);

        LlvmApi.PositionBuilderAtEnd(builder, layout.AfterCountBlock);
        LlvmValueHandle totalPending = LlvmApi.BuildLoad2(builder, state.I64, layout.CountSlot, prefix + "_total_pending");
        LlvmValueHandle hasPending = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, totalPending, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_pending");
        // Skip the blocking wait when a runnable (WaitNone, not-completed) task exists:
        // returning immediately lets the caller's scheduler re-step it without waiting
        // on unrelated sockets. Otherwise an HTTP receive that consumed a buffered chunk
        // could be starved behind a peer task whose socket never becomes ready.
        LlvmValueHandle hasRunnable = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildLoad2(builder, state.I64, layout.RunnableSlot, prefix + "_runnable_value"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_runnable");
        // shouldWait = hasPending AND NOT hasRunnable, expressed as hasPending > hasRunnable
        // over the i1 values (1 > 0 only when hasPending is set and hasRunnable is clear).
        LlvmValueHandle shouldWait = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, hasPending, hasRunnable, prefix + "_should_wait");
        LlvmApi.BuildCondBr(builder, shouldWait, layout.WaitBlock, layout.TimerCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.WaitBlock);
        EmitWaitPendingSocketWait(state, taskListPtr, totalPending, layout.WaitResultSlot, prefix);
        LlvmApi.BuildBr(builder, layout.DoneBlock);

        // --- Timer path: cooperative sleep wait ---
        LlvmApi.PositionBuilderAtEnd(builder, layout.TimerCheckBlock);
        EmitCooperativeTimerWait(state, taskListPtr, hasRunnable, prefix + "_timer", layout.DoneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.DoneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, layout.WaitResultSlot, prefix + "_wait_result");
    }

    private static void EmitWaitPendingCountLoop(LlvmCodegenState state, in WaitPendingLayout layout, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, layout.CountCheckBlock);
        LlvmValueHandle countCursor = LlvmApi.BuildLoad2(builder, state.I64, layout.CursorSlot, prefix + "_count_cursor");
        LlvmValueHandle countDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, countCursor, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_count_done");
        LlvmApi.BuildCondBr(builder, countDone, layout.AfterCountBlock, layout.CountBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.CountBodyBlock);
        LlvmValueHandle countTask = LoadMemory(state, countCursor, 0, prefix + "_count_task");
        LlvmValueHandle countTail = LoadMemory(state, countCursor, 8, prefix + "_count_tail");
        LlvmValueHandle countWaitKind = LoadMemory(state, countTask, TaskStructLayout.WaitKind, prefix + "_count_wait_kind");
        LlvmValueHandle countState = LoadMemory(state, countTask, TaskStructLayout.StateIndex, prefix + "_count_state");
        LlvmValueHandle countIsRead = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, countWaitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitSocketRead, 0), prefix + "_count_is_read");
        LlvmValueHandle countIsWrite = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, countWaitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitSocketWrite, 0), prefix + "_count_is_write");
        LlvmValueHandle countIsTlsRead = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, countWaitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTlsWantRead, 0), prefix + "_count_is_tls_read");
        LlvmValueHandle countIsTlsWrite = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, countWaitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTlsWantWrite, 0), prefix + "_count_is_tls_write");
        LlvmValueHandle countShould = LlvmApi.BuildOr(builder, countIsRead, countIsWrite, prefix + "_count_should");
        countShould = LlvmApi.BuildOr(builder, countShould, countIsTlsRead, prefix + "_count_should_tls_read");
        countShould = LlvmApi.BuildOr(builder, countShould, countIsTlsWrite, prefix + "_count_should_tls_write");
        // A task that is not yet completed and is not parked on a socket wait is
        // immediately runnable; record it so we can skip the blocking wait below.
        LlvmValueHandle countNotCompleted = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, countState, LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateCompleted), 1), prefix + "_count_not_completed");
        LlvmValueHandle countWaitNone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, countWaitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitNone, 0), prefix + "_count_wait_none");
        LlvmValueHandle countRunnable = LlvmApi.BuildAnd(builder, countNotCompleted, countWaitNone, prefix + "_count_runnable");
        LlvmValueHandle priorRunnable = LlvmApi.BuildLoad2(builder, state.I64, layout.RunnableSlot, prefix + "_prior_runnable");
        LlvmApi.BuildStore(builder, LlvmApi.BuildOr(builder, priorRunnable, LlvmApi.BuildZExt(builder, countRunnable, state.I64, prefix + "_count_runnable_i64"), prefix + "_runnable_next"), layout.RunnableSlot);
        LlvmApi.BuildCondBr(builder, countShould, layout.CountIncrementBlock, layout.CountAdvanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.CountIncrementBlock);
        LlvmValueHandle pendingCount = LlvmApi.BuildLoad2(builder, state.I64, layout.CountSlot, prefix + "_pending_count");
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, pendingCount, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_pending_count_next"), layout.CountSlot);
        LlvmApi.BuildBr(builder, layout.CountAdvanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.CountAdvanceBlock);
        LlvmApi.BuildStore(builder, countTail, layout.CursorSlot);
        LlvmApi.BuildBr(builder, layout.CountCheckBlock);
    }

    // Blocking socket wait for the pending task list: epoll on Linux, WSAPoll on Windows.
    private static void EmitWaitPendingSocketWait(LlvmCodegenState state, LlvmValueHandle taskListPtr, LlvmValueHandle totalPending, LlvmValueHandle waitResultSlot, string prefix)
    {
        if (IsLinuxFlavor(state.Flavor))
        {
            EmitWaitPendingLinuxSocketWait(state, taskListPtr, prefix);
        }
        else
        {
            EmitWindowsWaitForPendingSocketTaskList(state, taskListPtr, totalPending, waitResultSlot, prefix + "_windows_poll");
        }
    }

    private static void EmitWaitPendingLinuxSocketWait(LlvmCodegenState state, LlvmValueHandle taskListPtr, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle epollEventType = LlvmApi.ArrayType2(state.I8, 16);
        LlvmValueHandle eventStorage = LlvmApi.BuildAlloca(builder, epollEventType, prefix + "_event_storage");
        LlvmValueHandle eventPtr = GetArrayElementPointer(state, epollEventType, eventStorage, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_event_ptr");
        LlvmValueHandle waitEventStorage = LlvmApi.BuildAlloca(builder, epollEventType, prefix + "_wait_event_storage");
        LlvmValueHandle waitEventPtr = GetArrayElementPointer(state, epollEventType, waitEventStorage, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_wait_event_ptr");
        LlvmValueHandle epollFd = EmitLinuxSyscall(state, SyscallEpollCreate1, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_epoll_create");
        LlvmBasicBlockHandle afterRegisterBlock = EmitWaitPendingEpollRegister(state, taskListPtr, eventPtr, epollFd, prefix);

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

    // Registers every socket-parked task in the given epoll set. Returns the after-register block
    // (already appended, not yet positioned) for the caller to emit the epoll_wait into.
    private static LlvmBasicBlockHandle EmitWaitPendingEpollRegister(LlvmCodegenState state, LlvmValueHandle taskListPtr, LlvmValueHandle eventPtr, LlvmValueHandle epollFd, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
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
        return afterRegisterBlock;
    }

    /// <summary>
    /// Cooperative sleep wait. Scans the pending task list for tasks parked on a sleep timer
    /// (<c>WaitKind == WaitTimer</c>) — the remaining milliseconds live in the sleeping leaf's
    /// <c>SleepDurationMs</c> (the task itself when it is a bare sleep leaf, or its <c>AwaitedTask</c>
    /// when a coroutine is suspended on one). If any exist and nothing is immediately runnable, it
    /// sleeps once (<see cref="EmitNanosleep"/>) until the earliest deadline, then decrements every
    /// timer task's remaining by that amount so the elapsed ones complete on the next scheduler step
    /// while the others keep counting down. All control-flow paths branch to <paramref name="continuation"/>.
    /// </summary>
    // Up-front slots + basic blocks for EmitCooperativeTimerWait.
    private readonly record struct TimerWaitLayout(
        LlvmValueHandle MinSlot,
        LlvmValueHandle CountSlot,
        LlvmValueHandle CursorSlot,
        LlvmBasicBlockHandle ScanCheck,
        LlvmBasicBlockHandle ScanBody,
        LlvmBasicBlockHandle ScanTimer,
        LlvmBasicBlockHandle AfterScan,
        LlvmBasicBlockHandle SleepBlock,
        LlvmBasicBlockHandle DecCheck,
        LlvmBasicBlockHandle DecBody,
        LlvmBasicBlockHandle DecTimer);

    private static TimerWaitLayout EmitTimerWaitPrologue(LlvmCodegenState state, LlvmValueHandle taskListPtr, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle minSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_min_slot");
        LlvmValueHandle countSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_count_slot");
        LlvmValueHandle cursorSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_cursor_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)long.MaxValue), 0), minSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), countSlot);
        LlvmApi.BuildStore(builder, taskListPtr, cursorSlot);
        return new TimerWaitLayout(
            minSlot, countSlot, cursorSlot,
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_scan_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_scan_body"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_scan_timer"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_after_scan"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_sleep"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_dec_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_dec_body"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_dec_timer"));
    }

    private static void EmitCooperativeTimerWait(
        LlvmCodegenState state,
        LlvmValueHandle taskListPtr,
        LlvmValueHandle hasRunnable,
        string prefix,
        LlvmBasicBlockHandle continuation)
    {
        TimerWaitLayout layout = EmitTimerWaitPrologue(state, taskListPtr, prefix);
        // Pass 1: find the minimum remaining sleep and count the timer tasks.
        LlvmApi.BuildBr(state.Target.Builder, layout.ScanCheck);
        EmitTimerWaitScanPass(state, layout, prefix);
        EmitTimerWaitSleepDecPass(state, taskListPtr, hasRunnable, continuation, layout, prefix);
    }

    private static void EmitTimerWaitScanPass(LlvmCodegenState state, in TimerWaitLayout layout, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle timerKind = LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTimer, 0);
        LlvmValueHandle sleepingState = LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateSleeping), 1);
        LlvmValueHandle completedState = LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateCompleted), 1);

        LlvmApi.PositionBuilderAtEnd(builder, layout.ScanCheck);
        LlvmValueHandle scanCursor = LlvmApi.BuildLoad2(builder, state.I64, layout.CursorSlot, prefix + "_scan_cursor");
        LlvmValueHandle scanDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, scanCursor, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_scan_done");
        LlvmApi.BuildCondBr(builder, scanDone, layout.AfterScan, layout.ScanBody);

        LlvmApi.PositionBuilderAtEnd(builder, layout.ScanBody);
        LlvmValueHandle scanTask = LoadMemory(state, scanCursor, 0, prefix + "_scan_task");
        LlvmValueHandle scanTail = LoadMemory(state, scanCursor, 8, prefix + "_scan_tail");
        LlvmApi.BuildStore(builder, scanTail, layout.CursorSlot);
        LlvmValueHandle scanWaitKind = LoadMemory(state, scanTask, TaskStructLayout.WaitKind, prefix + "_scan_wait_kind");
        LlvmValueHandle scanStateIdx = LoadMemory(state, scanTask, TaskStructLayout.StateIndex, prefix + "_scan_state");
        LlvmValueHandle scanIsTimerKind = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, scanWaitKind, timerKind, prefix + "_scan_is_timer_kind");
        // A completed task may still carry a stale WaitKind; exclude it so its zero remaining does not
        // poison the earliest-deadline computation into a no-op wait.
        LlvmValueHandle scanNotDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, scanStateIdx, completedState, prefix + "_scan_not_done");
        LlvmValueHandle scanIsTimer = LlvmApi.BuildAnd(builder, scanIsTimerKind, scanNotDone, prefix + "_scan_is_timer");
        LlvmApi.BuildCondBr(builder, scanIsTimer, layout.ScanTimer, layout.ScanCheck);

        LlvmApi.PositionBuilderAtEnd(builder, layout.ScanTimer);
        LlvmValueHandle scanIsDirect = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, scanStateIdx, sleepingState, prefix + "_scan_is_direct");
        LlvmValueHandle scanAwaited = LoadMemory(state, scanTask, TaskStructLayout.AwaitedTask, prefix + "_scan_awaited");
        LlvmValueHandle scanLeaf = LlvmApi.BuildSelect(builder, scanIsDirect, scanTask, scanAwaited, prefix + "_scan_leaf");
        LlvmValueHandle scanRem = LoadMemory(state, scanLeaf, TaskStructLayout.SleepDurationMs, prefix + "_scan_rem");
        LlvmValueHandle scanCurMin = LlvmApi.BuildLoad2(builder, state.I64, layout.MinSlot, prefix + "_scan_cur_min");
        LlvmValueHandle scanIsLess = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, scanRem, scanCurMin, prefix + "_scan_is_less");
        LlvmApi.BuildStore(builder, LlvmApi.BuildSelect(builder, scanIsLess, scanRem, scanCurMin, prefix + "_scan_new_min"), layout.MinSlot);
        LlvmValueHandle scanCount = LlvmApi.BuildLoad2(builder, state.I64, layout.CountSlot, prefix + "_scan_count");
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, scanCount, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_scan_count_next"), layout.CountSlot);
        LlvmApi.BuildBr(builder, layout.ScanCheck);
    }

    private static void EmitTimerWaitSleepDecPass(LlvmCodegenState state, LlvmValueHandle taskListPtr, LlvmValueHandle hasRunnable, LlvmBasicBlockHandle continuation, in TimerWaitLayout layout, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle timerKind = LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTimer, 0);
        LlvmValueHandle sleepingState = LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateSleeping), 1);
        LlvmValueHandle completedState = LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateCompleted), 1);

        // Decide whether to sleep: only when a timer task exists and nothing is immediately runnable
        // (1 > 0 over the i1 values, exactly as the socket-wait decision above).
        LlvmApi.PositionBuilderAtEnd(builder, layout.AfterScan);
        LlvmValueHandle timerCount = LlvmApi.BuildLoad2(builder, state.I64, layout.CountSlot, prefix + "_timer_count");
        LlvmValueHandle hasTimers = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, timerCount, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_timers");
        LlvmValueHandle shouldSleep = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, hasTimers, hasRunnable, prefix + "_should_sleep");
        LlvmApi.BuildCondBr(builder, shouldSleep, layout.SleepBlock, continuation);

        // Sleep once until the earliest deadline, then decrement every timer task's remaining by it.
        LlvmApi.PositionBuilderAtEnd(builder, layout.SleepBlock);
        LlvmValueHandle minRemaining = LlvmApi.BuildLoad2(builder, state.I64, layout.MinSlot, prefix + "_min_remaining");
        EmitNanosleep(state, minRemaining);
        LlvmApi.BuildStore(builder, taskListPtr, layout.CursorSlot);
        LlvmApi.BuildBr(builder, layout.DecCheck);

        LlvmApi.PositionBuilderAtEnd(builder, layout.DecCheck);
        LlvmValueHandle decCursor = LlvmApi.BuildLoad2(builder, state.I64, layout.CursorSlot, prefix + "_dec_cursor");
        LlvmValueHandle decDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, decCursor, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_dec_done");
        LlvmApi.BuildCondBr(builder, decDone, continuation, layout.DecBody);

        LlvmApi.PositionBuilderAtEnd(builder, layout.DecBody);
        LlvmValueHandle decTask = LoadMemory(state, decCursor, 0, prefix + "_dec_task");
        LlvmValueHandle decTail = LoadMemory(state, decCursor, 8, prefix + "_dec_tail");
        LlvmApi.BuildStore(builder, decTail, layout.CursorSlot);
        LlvmValueHandle decWaitKind = LoadMemory(state, decTask, TaskStructLayout.WaitKind, prefix + "_dec_wait_kind");
        LlvmValueHandle decStateIdx = LoadMemory(state, decTask, TaskStructLayout.StateIndex, prefix + "_dec_state");
        LlvmValueHandle decIsTimerKind = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, decWaitKind, timerKind, prefix + "_dec_is_timer_kind");
        LlvmValueHandle decNotDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, decStateIdx, completedState, prefix + "_dec_not_done");
        LlvmValueHandle decIsTimer = LlvmApi.BuildAnd(builder, decIsTimerKind, decNotDone, prefix + "_dec_is_timer");
        LlvmApi.BuildCondBr(builder, decIsTimer, layout.DecTimer, layout.DecCheck);

        LlvmApi.PositionBuilderAtEnd(builder, layout.DecTimer);
        LlvmValueHandle decIsDirect = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, decStateIdx, sleepingState, prefix + "_dec_is_direct");
        LlvmValueHandle decAwaited = LoadMemory(state, decTask, TaskStructLayout.AwaitedTask, prefix + "_dec_awaited");
        LlvmValueHandle decLeaf = LlvmApi.BuildSelect(builder, decIsDirect, decTask, decAwaited, prefix + "_dec_leaf");
        LlvmValueHandle decRem = LoadMemory(state, decLeaf, TaskStructLayout.SleepDurationMs, prefix + "_dec_rem");
        LlvmValueHandle decNewRem = LlvmApi.BuildSub(builder, decRem, minRemaining, prefix + "_dec_new_rem");
        StoreMemory(state, decLeaf, TaskStructLayout.SleepDurationMs, decNewRem, prefix + "_dec_store");
        LlvmApi.BuildBr(builder, layout.DecCheck);
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
    // Up-front basic blocks for EmitRunTask.
    private readonly record struct RunTaskLayout(
        LlvmBasicBlockHandle CheckBlock,
        LlvmBasicBlockHandle StepBlock,
        LlvmBasicBlockHandle NotDoneBlock,
        LlvmBasicBlockHandle LeafBlock,
        LlvmBasicBlockHandle LeafPendingBlock,
        LlvmBasicBlockHandle SuspendedBlock,
        LlvmBasicBlockHandle DoneBlock,
        LlvmBasicBlockHandle LeafHandleBlock,
        LlvmBasicBlockHandle LeafHandleDoneBlock,
        LlvmBasicBlockHandle LeafHandlePendingBlock,
        LlvmBasicBlockHandle NormalSubBlock,
        LlvmBasicBlockHandle AfterSubBlock);

    private static RunTaskLayout EmitRunTaskPrologue(LlvmCodegenState state)
    {
        return new RunTaskLayout(
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "run_task_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "run_task_step"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "run_task_not_done"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "run_task_leaf"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "run_task_leaf_pending"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "run_task_suspended"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "run_task_done"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "run_leaf_handle"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "run_leaf_handle_done"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "run_leaf_handle_pending"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "run_normal_sub"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "run_after_sub"));
    }

    private static LlvmValueHandle EmitRunTask(LlvmCodegenState state, LlvmValueHandle taskPtr)
    {
        RunTaskLayout layout = EmitRunTaskPrologue(state);
        // Jump to check block
        LlvmApi.BuildBr(state.Target.Builder, layout.CheckBlock);
        EmitRunTaskCheckPhase(state, taskPtr, layout);
        LlvmValueHandle awaitedTask = EmitRunTaskStepPhase(state, taskPtr, layout);
        EmitRunTaskSubtaskPhase(state, taskPtr, awaitedTask, layout);

        // --- Done block: extract and return the result ---
        LlvmApi.PositionBuilderAtEnd(state.Target.Builder, layout.DoneBlock);
        return LoadMemory(state, taskPtr, TaskStructLayout.ResultSlot, "run_task_result");
    }

    private static void EmitRunTaskCheckPhase(LlvmCodegenState state, LlvmValueHandle taskPtr, in RunTaskLayout layout)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // --- Check block: is task already completed? ---
        LlvmApi.PositionBuilderAtEnd(builder, layout.CheckBlock);
        LlvmValueHandle stateIdx = LoadMemory(state, taskPtr, TaskStructLayout.StateIndex, "run_state_idx");
        LlvmValueHandle minusOne = LlvmApi.ConstInt(state.I64, unchecked((ulong)-1), 1);
        LlvmValueHandle isDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            stateIdx, minusOne, "run_is_done");
        LlvmApi.BuildCondBr(builder, isDone, layout.DoneBlock, layout.NotDoneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.NotDoneBlock);
        LlvmValueHandle isLeaf = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt,
            stateIdx, minusOne, "run_is_leaf");
        LlvmApi.BuildCondBr(builder, isLeaf, layout.LeafBlock, layout.StepBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.LeafBlock);
        LlvmValueHandle leafStatus = EmitStepLeafTask(state, taskPtr, "run_leaf");
        LlvmValueHandle leafCompleted = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne,
            leafStatus, LlvmApi.ConstInt(state.I64, 0, 0), "run_leaf_completed");
        LlvmApi.BuildCondBr(builder, leafCompleted, layout.DoneBlock, layout.LeafPendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.LeafPendingBlock);
        EmitWaitForPendingLeafTask(state, taskPtr, "run_leaf_pending");
        LlvmApi.BuildBr(builder, layout.CheckBlock);
    }

    private static LlvmValueHandle EmitRunTaskStepPhase(LlvmCodegenState state, LlvmValueHandle taskPtr, in RunTaskLayout layout)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // --- Step block: call the coroutine ---
        LlvmApi.PositionBuilderAtEnd(builder, layout.StepBlock);
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
        LlvmApi.BuildCondBr(builder, isSuspended, layout.SuspendedBlock, layout.DoneBlock);

        // --- Suspended block: run the awaited sub-task, then resume ---
        LlvmApi.PositionBuilderAtEnd(builder, layout.SuspendedBlock);
        LlvmValueHandle awaitedTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, "run_awaited_task");
        LlvmValueHandle awaitedState = LoadMemory(state, awaitedTask, TaskStructLayout.StateIndex, "run_awaited_state");
        LlvmValueHandle minusOne = LlvmApi.ConstInt(state.I64, unchecked((ulong)-1), 1);
        LlvmValueHandle isLeafAwaited = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt,
            awaitedState, minusOne, "run_is_leaf_awaited");
        LlvmApi.BuildCondBr(builder, isLeafAwaited, layout.LeafHandleBlock, layout.NormalSubBlock);
        return awaitedTask;
    }

    private static void EmitRunTaskSubtaskPhase(LlvmCodegenState state, LlvmValueHandle taskPtr, LlvmValueHandle awaitedTask, in RunTaskLayout layout)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, layout.LeafHandleBlock);
        LlvmValueHandle awaitedLeafStatus = EmitStepLeafTask(state, awaitedTask, "run_awaited_leaf");
        LlvmValueHandle awaitedLeafCompleted = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne,
            awaitedLeafStatus, LlvmApi.ConstInt(state.I64, 0, 0), "run_awaited_leaf_completed");
        LlvmApi.BuildCondBr(builder, awaitedLeafCompleted, layout.LeafHandleDoneBlock, layout.LeafHandlePendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.LeafHandleDoneBlock);
        LlvmValueHandle leafResult = LoadMemory(state, awaitedTask, TaskStructLayout.ResultSlot, "run_leaf_result_load");
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot, leafResult, "run_leaf_sub_store");
        LlvmApi.BuildBr(builder, layout.AfterSubBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.LeafHandlePendingBlock);
        EmitWaitForPendingLeafTask(state, awaitedTask, "run_awaited_leaf_pending");
        LlvmApi.BuildBr(builder, layout.SuspendedBlock);

        // --- Normal sub-task: recursively run to completion ---
        LlvmApi.PositionBuilderAtEnd(builder, layout.NormalSubBlock);
        LlvmValueHandle subResult = EmitRunTaskRecursive(state, awaitedTask);
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot, subResult, "run_sub_result_store");
        LlvmApi.BuildBr(builder, layout.AfterSubBlock);

        // --- After sub-task: loop back to step the coroutine again ---
        LlvmApi.PositionBuilderAtEnd(builder, layout.AfterSubBlock);
        LlvmApi.BuildBr(builder, layout.StepBlock);
    }

    /// <summary>
    /// Helper: recursively run a task to completion. This is the same logic as EmitRunTask
    /// but implemented as a recursive call to a shared runtime function.
    /// For simplicity, we inline the same pattern.
    /// </summary>
    // Up-front basic blocks for EmitRunTaskRecursive.
    private readonly record struct RunTaskRecursiveLayout(
        LlvmBasicBlockHandle SubCheckBlock,
        LlvmBasicBlockHandle SubStepBlock,
        LlvmBasicBlockHandle SubNotDoneBlock,
        LlvmBasicBlockHandle SubLeafBlock,
        LlvmBasicBlockHandle SubLeafPendingBlock,
        LlvmBasicBlockHandle SubSuspendedBlock,
        LlvmBasicBlockHandle SubDoneBlock,
        LlvmBasicBlockHandle AwaitedDoneBlock,
        LlvmBasicBlockHandle AwaitedPendingBlock);

    private static RunTaskRecursiveLayout EmitRunTaskRecursivePrologue(LlvmCodegenState state)
    {
        return new RunTaskRecursiveLayout(
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sub_run_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sub_run_step"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sub_run_not_done"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sub_run_leaf"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sub_run_leaf_pending"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sub_run_suspended"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sub_run_done"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sub_awaited_done"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sub_awaited_pending"));
    }

    private static LlvmValueHandle EmitRunTaskRecursive(LlvmCodegenState state, LlvmValueHandle taskPtr)
    {
        RunTaskRecursiveLayout layout = EmitRunTaskRecursivePrologue(state);
        LlvmApi.BuildBr(state.Target.Builder, layout.SubCheckBlock);
        EmitRunTaskRecursiveCheckPhase(state, taskPtr, layout);
        return EmitRunTaskRecursiveStepPhase(state, taskPtr, layout);
    }

    private static void EmitRunTaskRecursiveCheckPhase(LlvmCodegenState state, LlvmValueHandle taskPtr, in RunTaskRecursiveLayout layout)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // --- Check: already completed? ---
        LlvmApi.PositionBuilderAtEnd(builder, layout.SubCheckBlock);
        LlvmValueHandle stateIdx = LoadMemory(state, taskPtr, TaskStructLayout.StateIndex, "sub_state_idx");
        LlvmValueHandle minusOne = LlvmApi.ConstInt(state.I64, unchecked((ulong)-1), 1);
        LlvmValueHandle isDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            stateIdx, minusOne, "sub_is_done");

        LlvmApi.BuildCondBr(builder, isDone, layout.SubDoneBlock, layout.SubNotDoneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.SubNotDoneBlock);
        LlvmValueHandle subIsLeaf = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt,
            stateIdx, minusOne, "sub_is_leaf");
        LlvmApi.BuildCondBr(builder, subIsLeaf, layout.SubLeafBlock, layout.SubStepBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.SubLeafBlock);
        LlvmValueHandle subLeafStatus = EmitStepLeafTask(state, taskPtr, "sub_leaf");
        LlvmValueHandle subLeafCompleted = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne,
            subLeafStatus, LlvmApi.ConstInt(state.I64, 0, 0), "sub_leaf_completed");
        LlvmApi.BuildCondBr(builder, subLeafCompleted, layout.SubDoneBlock, layout.SubLeafPendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.SubLeafPendingBlock);
        EmitWaitForPendingLeafTask(state, taskPtr, "sub_leaf_pending");
        LlvmApi.BuildBr(builder, layout.SubCheckBlock);
    }

    private static LlvmValueHandle EmitRunTaskRecursiveStepPhase(LlvmCodegenState state, LlvmValueHandle taskPtr, in RunTaskRecursiveLayout layout)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // --- Step: call coroutine ---
        LlvmApi.PositionBuilderAtEnd(builder, layout.SubStepBlock);
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
        LlvmApi.BuildCondBr(builder, isSuspended, layout.SubSuspendedBlock, layout.SubDoneBlock);

        // --- Suspended: handle nested await (run sub-sub-task) ---
        LlvmApi.PositionBuilderAtEnd(builder, layout.SubSuspendedBlock);
        LlvmValueHandle awaitedTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, "sub_awaited_task");
        LlvmValueHandle awaitedStatus = EmitNetworkingRuntimeCall(state, "ashes_step_task_until_wait_or_done", [awaitedTask], "sub_awaited_status");
        LlvmValueHandle awaitedCompleted = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne,
            awaitedStatus, LlvmApi.ConstInt(state.I64, 0, 0), "sub_awaited_completed");
        LlvmApi.BuildCondBr(builder, awaitedCompleted, layout.AwaitedDoneBlock, layout.AwaitedPendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.AwaitedDoneBlock);
        LlvmValueHandle awaitedResult = LoadMemory(state, awaitedTask, TaskStructLayout.ResultSlot, "sub_awaited_result");
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot, awaitedResult, "sub_awaited_result_store");
        LlvmApi.BuildBr(builder, layout.SubStepBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.AwaitedPendingBlock);
        EmitWaitForPendingLeafTask(state, awaitedTask, "sub_awaited_pending");
        LlvmApi.BuildBr(builder, layout.SubSuspendedBlock);

        // --- Done: extract result ---
        LlvmApi.PositionBuilderAtEnd(builder, layout.SubDoneBlock);
        return LoadMemory(state, taskPtr, TaskStructLayout.ResultSlot, "sub_task_result");
    }

    // Async Sleep

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
        StoreMemory(state, taskPtr, TaskStructLayout.FrameSizeBytes,
            LlvmApi.ConstInt(state.I64, TaskStructLayout.HeaderSize, 0), "sleep_frame_size");
        StoreMemory(state, taskPtr, TaskStructLayout.ArenaCursor,
            LlvmApi.ConstInt(state.I64, 0, 0), "sleep_arena_cursor");
        StoreMemory(state, taskPtr, TaskStructLayout.ArenaEnd,
            LlvmApi.ConstInt(state.I64, 0, 0), "sleep_arena_end");

        return taskPtr;
    }

    /// <summary>
    /// Creates a server-side TLS handshake leaf task: a networking leaf with the accepted socket
    /// in IoArg0, the certificate-chain PEM (Str) in IoArg1, and the private-key PEM (Str) in
    /// WaitData1 (unused by the handshake loop, which only caches its session in WaitData0).
    /// </summary>
    private static LlvmValueHandle EmitCreateTlsServerHandshakeTask(
        LlvmCodegenState state,
        LlvmValueHandle socket,
        LlvmValueHandle certPem,
        LlvmValueHandle keyPem)
    {
        LlvmValueHandle taskPtr = EmitCreateLeafNetworkingTask(state, TaskStructLayout.StateTlsServerHandshake, socket, certPem, "tls_server_handshake_task");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData1, keyPem, "tls_server_handshake_key_pem");
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

    // Detached tasks (Ashes.Task.spawn)
    //
    // A spawned task is fire-and-forget: its frame is copied into a private arena chunk (so it
    // survives the spawner's arena resets), it is chained into a global detached list via the
    // NextTask header field, and it advances whenever any driver blocks in
    // EmitWaitForPendingLeafTask. Each detached step runs with the task's private arena installed
    // in the thread's cursor/end slots, so its allocations never interleave with the spawner's;
    // on completion the whole private chunk chain is freed and the result is dropped.

    private static LlvmValueHandle DetachedTasksHeadGlobal(LlvmCodegenState state) =>
        ReadLineScratchGlobal(state, "__ashes_detached_head", state.I64);

    private static LlvmValueHandle DetachedStepGuardGlobal(LlvmCodegenState state) =>
        ReadLineScratchGlobal(state, "__ashes_detached_stepping", state.I64);

    private static bool DetachedRuntimeAvailable(LlvmCodegenState state) =>
        LlvmApi.GetNamedFunction(state.Target.Module, "ashes_run_detached").Ptr != 0;

    // Run-queue scheduler. The ready queue is an intrusive
    // FIFO of task structs linked through TaskStructLayout.ReadyNext, with head/tail globals.
    private static LlvmValueHandle ReadyQueueHeadGlobal(LlvmCodegenState state) =>
        ReadLineScratchGlobal(state, "__ashes_ready_head", state.I64);

    private static LlvmValueHandle ReadyQueueTailGlobal(LlvmCodegenState state) =>
        ReadLineScratchGlobal(state, "__ashes_ready_tail", state.I64);

    // Parked-on-leaf tasks (not runnable until their wait is satisfied), linked through ReadyNext — a
    // task is only ever in the ready queue OR the parked list, never both, so the link is shared.
    private static LlvmValueHandle ParkedLeavesHeadGlobal(LlvmCodegenState state) =>
        ReadLineScratchGlobal(state, "__ashes_parked_head", state.I64);

    /// <summary>
    /// Body of <c>ashes_ready_enqueue(task)</c>: append a task to the tail of the run queue. Sets the
    /// task's <c>ReadyNext</c> to 0 and links it after the current tail (or as the head when empty).
    /// </summary>
    private static LlvmValueHandle EmitReadyEnqueueBody(LlvmCodegenState state, LlvmValueHandle taskPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle headGlobal = ReadyQueueHeadGlobal(state);
        LlvmValueHandle tailGlobal = ReadyQueueTailGlobal(state);

        StoreMemory(state, taskPtr, TaskStructLayout.ReadyNext, LlvmApi.ConstInt(state.I64, 0, 0), "enq_clear_next");

        LlvmBasicBlockHandle emptyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "enq_empty");
        LlvmBasicBlockHandle appendBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "enq_append");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "enq_done");

        LlvmValueHandle tail = LlvmApi.BuildLoad2(builder, state.I64, tailGlobal, "enq_tail");
        LlvmValueHandle isEmpty = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, tail, LlvmApi.ConstInt(state.I64, 0, 0), "enq_is_empty");
        LlvmApi.BuildCondBr(builder, isEmpty, emptyBlock, appendBlock);

        LlvmApi.PositionBuilderAtEnd(builder, emptyBlock);
        LlvmApi.BuildStore(builder, taskPtr, headGlobal);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, appendBlock);
        StoreMemory(state, tail, TaskStructLayout.ReadyNext, taskPtr, "enq_link");
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        LlvmApi.BuildStore(builder, taskPtr, tailGlobal);
        return LlvmApi.ConstInt(state.I64, 0, 0);
    }

    /// <summary>
    /// Body of <c>ashes_ready_dequeue()</c>: pop and return the head task of the run queue, or 0 when
    /// the queue is empty. Clears the tail global when the queue becomes empty.
    /// </summary>
    private static LlvmValueHandle EmitReadyDequeueBody(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle headGlobal = ReadyQueueHeadGlobal(state);
        LlvmValueHandle tailGlobal = ReadyQueueTailGlobal(state);

        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "deq_result_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

        LlvmBasicBlockHandle popBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "deq_pop");
        LlvmBasicBlockHandle clearTailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "deq_clear_tail");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "deq_done");

        LlvmValueHandle head = LlvmApi.BuildLoad2(builder, state.I64, headGlobal, "deq_head");
        LlvmValueHandle isEmpty = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, head, LlvmApi.ConstInt(state.I64, 0, 0), "deq_is_empty");
        LlvmApi.BuildCondBr(builder, isEmpty, doneBlock, popBlock);

        LlvmApi.PositionBuilderAtEnd(builder, popBlock);
        LlvmApi.BuildStore(builder, head, resultSlot);
        LlvmValueHandle next = LoadMemory(state, head, TaskStructLayout.ReadyNext, "deq_next");
        LlvmApi.BuildStore(builder, next, headGlobal);
        LlvmValueHandle nextEmpty = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, next, LlvmApi.ConstInt(state.I64, 0, 0), "deq_next_empty");
        LlvmApi.BuildCondBr(builder, nextEmpty, clearTailBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, clearTailBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), tailGlobal);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "deq_result");
    }

    /// <summary>
    /// Installs a task's arena for the duration of one step: if the task has an <c>ArenaOwner</c> (a
    /// spawned task or a sub-task of one), the owner's private arena cursor/end is loaded into the
    /// global allocation slots so allocations during the step land in that arena; the previous global
    /// cursor/end are returned for restoration. Owner 0 (the main task and its sub-tasks) leaves the
    /// global arena in place. Pair with <see cref="EmitRestoreTaskArena"/>.
    /// </summary>
    private static (LlvmValueHandle owner, LlvmValueHandle savedCursor, LlvmValueHandle savedEnd) EmitInstallTaskArena(LlvmCodegenState state, LlvmValueHandle task, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle owner = LoadMemory(state, task, TaskStructLayout.ArenaOwner, prefix + "_owner");
        LlvmValueHandle savedCursor = LlvmApi.BuildLoad2(builder, state.I64, state.HeapCursorSlot, prefix + "_saved_cursor");
        LlvmValueHandle savedEnd = LlvmApi.BuildLoad2(builder, state.I64, state.HeapEndSlot, prefix + "_saved_end");
        LlvmBasicBlockHandle installBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_install");
        LlvmBasicBlockHandle afterBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_after_install");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, owner, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_owner"), installBlock, afterBlock);
        LlvmApi.PositionBuilderAtEnd(builder, installBlock);
        LlvmApi.BuildStore(builder, LoadMemory(state, owner, TaskStructLayout.ArenaCursor, prefix + "_owner_cursor"), state.HeapCursorSlot);
        LlvmApi.BuildStore(builder, LoadMemory(state, owner, TaskStructLayout.ArenaEnd, prefix + "_owner_end"), state.HeapEndSlot);
        LlvmApi.BuildBr(builder, afterBlock);
        LlvmApi.PositionBuilderAtEnd(builder, afterBlock);
        return (owner, savedCursor, savedEnd);
    }

    /// <summary>
    /// Writes the (possibly grown) arena cursor/end back to the task's <c>ArenaOwner</c> and restores
    /// the previous global cursor/end. Counterpart to <see cref="EmitInstallTaskArena"/>.
    /// </summary>
    private static void EmitRestoreTaskArena(LlvmCodegenState state, LlvmValueHandle owner, LlvmValueHandle savedCursor, LlvmValueHandle savedEnd, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmBasicBlockHandle wbBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_writeback");
        LlvmBasicBlockHandle afterBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_after_writeback");
        // Only touch the global cursor when an owner arena was installed. For owner 0 (main task and
        // its sub-tasks) the step allocated directly into the global arena and those allocations must
        // persist — restoring a saved cursor here would undo them.
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, owner, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_owner_wb"), wbBlock, afterBlock);
        LlvmApi.PositionBuilderAtEnd(builder, wbBlock);
        StoreMemory(state, owner, TaskStructLayout.ArenaCursor, LlvmApi.BuildLoad2(builder, state.I64, state.HeapCursorSlot, prefix + "_grown_cursor"), prefix + "_wb_cursor");
        StoreMemory(state, owner, TaskStructLayout.ArenaEnd, LlvmApi.BuildLoad2(builder, state.I64, state.HeapEndSlot, prefix + "_grown_end"), prefix + "_wb_end");
        LlvmApi.BuildStore(builder, savedCursor, state.HeapCursorSlot);
        LlvmApi.BuildStore(builder, savedEnd, state.HeapEndSlot);
        LlvmApi.BuildBr(builder, afterBlock);
        LlvmApi.PositionBuilderAtEnd(builder, afterBlock);
    }

    /// <summary>
    /// Frees a completed spawned task's private arena chunk chain (the task struct itself lives in the
    /// first chunk, so this must run after the last read of the task). Walks the chunks from the most
    /// recent (<c>ArenaEnd - HeapChunkBytes</c>) back through each chunk header's previous-chunk pointer.
    /// </summary>
    private static void EmitReapTaskArena(LlvmCodegenState state, LlvmValueHandle task, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle freeBaseSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_free_base_slot");
        LlvmApi.BuildStore(builder, LlvmApi.BuildSub(builder, LoadMemory(state, task, TaskStructLayout.ArenaEnd, prefix + "_reap_end"), LlvmApi.ConstInt(state.I64, HeapChunkBytes, 0), prefix + "_last_chunk"), freeBaseSlot);
        LlvmBasicBlockHandle checkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_free_check");
        LlvmBasicBlockHandle bodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_free_body");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_free_done");
        LlvmApi.BuildBr(builder, checkBlock);
        LlvmApi.PositionBuilderAtEnd(builder, checkBlock);
        LlvmValueHandle freeBase = LlvmApi.BuildLoad2(builder, state.I64, freeBaseSlot, prefix + "_free_base");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, freeBase, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_free_end"), doneBlock, bodyBlock);
        LlvmApi.PositionBuilderAtEnd(builder, bodyBlock);
        LlvmApi.BuildStore(builder, LoadMemory(state, freeBase, 0, prefix + "_prev_chunk"), freeBaseSlot);
        EmitFreeOsMemory(state, freeBase, HeapChunkBytes, prefix + "_free_chunk");
        LlvmApi.BuildBr(builder, checkBlock);
        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
    }

    /// <summary>
    /// Steps an <c>all</c> / <c>race</c> composite task. First step (phase 0): enqueue every child with
    /// <c>Waiter</c> = this composite and the composite's <c>ArenaOwner</c>, record the child count, and
    /// return pending (the scheduler drops the composite; each child completion decrements the counter —
    /// or, for race, delivers the first result — and re-enqueues the composite). Later step (phase 1):
    /// the composite is runnable because all children finished (all) or the first finished (race), so it
    /// collects the result and completes, delivering to its own waiter. Returns 1 = complete, 0 = pending.
    /// </summary>
    private static LlvmValueHandle EmitStepComposite(LlvmCodegenState state, LlvmValueHandle task, bool isRace, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_status");
        LlvmApi.BuildStore(builder, zero, statusSlot);

        LlvmBasicBlockHandle enqueueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_enqueue");
        LlvmBasicBlockHandle phase1Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_phase1");
        LlvmBasicBlockHandle emptyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_empty");
        LlvmBasicBlockHandle pendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_pending");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");

        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, LoadMemory(state, task, TaskStructLayout.IoArg1, prefix + "_phase"), zero, prefix + "_is_phase0"), enqueueBlock, phase1Block);

        EmitStepCompositeEnqueue(state, task, isRace, statusSlot, emptyBlock, pendingBlock, doneBlock, enqueueBlock, prefix);

        // Empty child list: collect immediately (Ok([]) for all, unit for race).
        LlvmApi.PositionBuilderAtEnd(builder, emptyBlock);
        LlvmValueHandle emptyResult = isRace
            ? LoadMemory(state, EmitAsyncRaceInline(state, LoadMemory(state, task, TaskStructLayout.IoArg0, prefix + "_empty_list")), TaskStructLayout.ResultSlot, prefix + "_empty_race_result")
            : LoadMemory(state, EmitAsyncAllInline(state, LoadMemory(state, task, TaskStructLayout.IoArg0, prefix + "_empty_list_all")), TaskStructLayout.ResultSlot, prefix + "_empty_all_result");
        StoreMemory(state, task, TaskStructLayout.ResultSlot, emptyResult, prefix + "_empty_store");
        StoreMemory(state, task, TaskStructLayout.StateIndex, LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateCompleted), 1), prefix + "_empty_done");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), statusSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingBlock);
        LlvmApi.BuildStore(builder, zero, statusSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        // Phase 1: children finished; collect. all -> rebuild the ordered result list; race -> the first
        // child's result is already in ResultSlot (delivered by the completion path).
        LlvmApi.PositionBuilderAtEnd(builder, phase1Block);
        if (!isRace)
        {
            StoreMemory(state, task, TaskStructLayout.ResultSlot, LoadMemory(state, EmitAsyncAllInline(state, LoadMemory(state, task, TaskStructLayout.IoArg0, prefix + "_p1_list")), TaskStructLayout.ResultSlot, prefix + "_p1_all_result"), prefix + "_p1_store");
        }
        StoreMemory(state, task, TaskStructLayout.StateIndex, LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateCompleted), 1), prefix + "_p1_done");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), statusSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, statusSlot, prefix + "_status_result");
    }

    // Phase 0 of EmitStepComposite: enqueue every child linked back to this composite, count them,
    // then branch to the empty (no children) or pending (children in flight) block.
    private static void EmitStepCompositeEnqueue(LlvmCodegenState state, LlvmValueHandle task, bool isRace, LlvmValueHandle statusSlot,
        LlvmBasicBlockHandle emptyBlock, LlvmBasicBlockHandle pendingBlock, LlvmBasicBlockHandle doneBlock, LlvmBasicBlockHandle enqueueBlock, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);

        // Phase 0: enqueue all children linked back to this composite.
        LlvmApi.PositionBuilderAtEnd(builder, enqueueBlock);
        StoreMemory(state, task, TaskStructLayout.IoArg1, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_set_phase1");
        LlvmValueHandle owner = LoadMemory(state, task, TaskStructLayout.ArenaOwner, prefix + "_owner");
        LlvmValueHandle cursorSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_cursor");
        LlvmValueHandle countSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_count");
        LlvmApi.BuildStore(builder, LoadMemory(state, task, TaskStructLayout.IoArg0, prefix + "_list"), cursorSlot);
        LlvmApi.BuildStore(builder, zero, countSlot);
        LlvmBasicBlockHandle loopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_loop");
        LlvmBasicBlockHandle bodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_body");
        LlvmBasicBlockHandle afterLoopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_after_loop");
        LlvmApi.BuildBr(builder, loopBlock);
        LlvmApi.PositionBuilderAtEnd(builder, loopBlock);
        LlvmValueHandle cur = LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, prefix + "_cur");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, cur, zero, prefix + "_loop_end"), afterLoopBlock, bodyBlock);
        LlvmApi.PositionBuilderAtEnd(builder, bodyBlock);
        LlvmValueHandle child = LoadMemory(state, cur, 0, prefix + "_child");
        LlvmApi.BuildStore(builder, LoadMemory(state, cur, 8, prefix + "_tail"), cursorSlot);
        StoreMemory(state, child, TaskStructLayout.Waiter, task, prefix + "_child_waiter");
        StoreMemory(state, child, TaskStructLayout.ArenaOwner, owner, prefix + "_child_owner");
        _ = EmitNetworkingRuntimeCall(state, "ashes_ready_enqueue", [child], prefix + "_enqueue_child");
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, LlvmApi.BuildLoad2(builder, state.I64, countSlot, prefix + "_count_cur"), LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_count_inc"), countSlot);
        LlvmApi.BuildBr(builder, loopBlock);
        LlvmApi.PositionBuilderAtEnd(builder, afterLoopBlock);
        LlvmValueHandle count = LlvmApi.BuildLoad2(builder, state.I64, countSlot, prefix + "_final_count");
        // all: WaitData0 is the pending-child counter. race: it is the resolved flag, initially 0.
        StoreMemory(state, task, TaskStructLayout.WaitData0, isRace ? zero : count, prefix + "_store_counter");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, count, zero, prefix + "_is_empty"), emptyBlock, pendingBlock);
    }

    /// <summary>
    /// Body of <c>ashes_scheduler_run(mainTask)</c>: the flat run-queue loop. Seeds the queue with the
    /// main task, then repeatedly pops a ready task and steps it once. A leaf that completes, or a
    /// coroutine that returns COMPLETED, delivers its result to its <c>Waiter</c> (clearing the waiter's
    /// <c>AwaitedTask</c>) and re-enqueues the waiter. A coroutine that SUSPENDS links its fresh
    /// <c>AwaitedTask</c> back to itself via <c>Waiter</c> and enqueues that sub-task, then parks. A leaf
    /// that stays pending joins the parked list. When the ready queue drains, it blocks in the
    /// aggregate wait (timers + socket readiness, see <see cref="EmitSchedulerAggregateWait"/>) until a
    /// parked leaf is ready and re-queues the parked leaves; the loop ends when the main task has
    /// completed. Returns the main task's result. Every async program on every target runs on this
    /// scheduler; tasks with a private arena (spawn) get it installed around each step.
    /// </summary>
    // Up-front basic blocks for EmitSchedulerRunBody.
    private readonly record struct SchedulerRunLayout(
        LlvmBasicBlockHandle LoopBlock,
        LlvmBasicBlockHandle EmptyBlock,
        LlvmBasicBlockHandle WaitBlock,
        LlvmBasicBlockHandle ReturnBlock,
        LlvmBasicBlockHandle HaveTaskBlock,
        LlvmBasicBlockHandle NotDoneBlock,
        LlvmBasicBlockHandle LeafBlock,
        LlvmBasicBlockHandle ParkBlock,
        LlvmBasicBlockHandle CoroBlock,
        LlvmBasicBlockHandle SuspendBlock,
        LlvmBasicBlockHandle CompleteBlock,
        LlvmBasicBlockHandle DeliverBlock,
        LlvmBasicBlockHandle NoWaiterBlock,
        LlvmBasicBlockHandle ReapBlock,
        LlvmBasicBlockHandle LeafCoroBlock,
        LlvmBasicBlockHandle CompositeBlock,
        LlvmBasicBlockHandle AllStepBlock,
        LlvmBasicBlockHandle RaceStepBlock,
        LlvmBasicBlockHandle CompAfterBlock,
        LlvmBasicBlockHandle AllWaiterBlock,
        LlvmBasicBlockHandle NotAllWaiterBlock,
        LlvmBasicBlockHandle EnqueueCompositeBlock,
        LlvmBasicBlockHandle RaceWaiterBlock,
        LlvmBasicBlockHandle RaceFirstBlock,
        LlvmBasicBlockHandle NormalWaiterBlock);

    private static SchedulerRunLayout EmitSchedulerRunPrologue(LlvmCodegenState state)
    {
        return new SchedulerRunLayout(
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_loop"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_empty"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_wait"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_return"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_have_task"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_not_done"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_leaf"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_park"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_coro"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_suspend"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_complete"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_deliver"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_no_waiter"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_reap"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_leaf_coro"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_composite"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_all_step"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_race_step"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_comp_after"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_all_waiter"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_not_all_waiter"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_enqueue_composite"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_race_waiter"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_race_first"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_normal_waiter"));
    }

    private static LlvmValueHandle EmitSchedulerRunBody(LlvmCodegenState state, LlvmValueHandle mainTask)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle taskSlot = LlvmApi.BuildAlloca(builder, state.I64, "sched_task_slot");
        // The main task is created by EmitCreateTask, which does not initialize the run-queue header
        // fields; zero them so the root task has no stale Waiter (delivered-to on completion) or
        // ArenaOwner. Sub-tasks get these set by the scheduler when they are enqueued.
        StoreMemory(state, mainTask, TaskStructLayout.Waiter, LlvmApi.ConstInt(state.I64, 0, 0), "sched_main_no_waiter");
        StoreMemory(state, mainTask, TaskStructLayout.ArenaOwner, LlvmApi.ConstInt(state.I64, 0, 0), "sched_main_no_owner");
        _ = EmitNetworkingRuntimeCall(state, "ashes_ready_enqueue", [mainTask], "sched_seed");

        SchedulerRunLayout layout = EmitSchedulerRunPrologue(state);
        LlvmApi.BuildBr(builder, layout.LoopBlock);

        (LlvmValueHandle task, LlvmValueHandle isRaceComposite) = EmitSchedulerLoopDispatch(state, mainTask, taskSlot, layout);
        EmitSchedulerCompositePhase(state, task, isRaceComposite, layout);
        EmitSchedulerLeafCoroPhase(state, task, layout);
        EmitSchedulerSuspendPhase(state, task, layout);
        EmitSchedulerCompletePhase(state, taskSlot, layout);

        LlvmApi.PositionBuilderAtEnd(builder, layout.NoWaiterBlock);
        LlvmValueHandle completedTask = LlvmApi.BuildLoad2(builder, state.I64, taskSlot, "sched_completed_nw");
        // No waiter: a fire-and-forget spawned root task (its own ArenaOwner) is reaped; everything else
        // (the main task, or a sub-task whose owner is still live) just drops.
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, LoadMemory(state, completedTask, TaskStructLayout.ArenaOwner, "sched_completed_owner"), completedTask, "sched_is_root_spawn"), layout.ReapBlock, layout.LoopBlock);
        LlvmApi.PositionBuilderAtEnd(builder, layout.ReapBlock);
        EmitReapTaskArena(state, completedTask, "sched_reap");
        LlvmValueHandle liveGlobal = LiveSpawnedGlobal(state);
        LlvmApi.BuildStore(builder,
            LlvmApi.BuildSub(builder, LlvmApi.BuildLoad2(builder, state.I64, liveGlobal, "sched_reap_live"), LlvmApi.ConstInt(state.I64, 1, 0), "sched_reap_live_dec"),
            liveGlobal);
        LlvmApi.BuildBr(builder, layout.LoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.ReturnBlock);
        return LoadMemory(state, mainTask, TaskStructLayout.ResultSlot, "sched_result");
    }

    private static (LlvmValueHandle Task, LlvmValueHandle IsRaceComposite) EmitSchedulerLoopDispatch(LlvmCodegenState state, LlvmValueHandle mainTask, LlvmValueHandle taskSlot, in SchedulerRunLayout layout)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle completedConst = LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateCompleted), 1);
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);

        // Pop the next ready task.
        LlvmApi.PositionBuilderAtEnd(builder, layout.LoopBlock);
        LlvmValueHandle popped = EmitNetworkingRuntimeCall(state, "ashes_ready_dequeue", [], "sched_pop");
        LlvmApi.BuildStore(builder, popped, taskSlot);
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, popped, zero, "sched_queue_empty"), layout.EmptyBlock, layout.HaveTaskBlock);

        // Queue empty: finished if the main task is done, else block until a parked leaf is ready.
        LlvmApi.PositionBuilderAtEnd(builder, layout.EmptyBlock);
        LlvmValueHandle mainState = LoadMemory(state, mainTask, TaskStructLayout.StateIndex, "sched_main_state");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, mainState, completedConst, "sched_main_done"), layout.ReturnBlock, layout.WaitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.WaitBlock);
        EmitSchedulerAggregateWait(state);
        LlvmApi.BuildBr(builder, layout.LoopBlock);

        // Step the popped task.
        LlvmApi.PositionBuilderAtEnd(builder, layout.HaveTaskBlock);
        LlvmValueHandle task = LlvmApi.BuildLoad2(builder, state.I64, taskSlot, "sched_task");
        LlvmValueHandle stateIdx = LoadMemory(state, task, TaskStructLayout.StateIndex, "sched_state_idx");
        // An already-completed task delivers to its waiter (e.g. an immediately-ready awaited value).
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, stateIdx, completedConst, "sched_is_done"), layout.CompleteBlock, layout.NotDoneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.NotDoneBlock);
        LlvmValueHandle isRaceComposite = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, stateIdx, LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateRaceComposite), 1), "sched_is_race_comp");
        LlvmValueHandle isComposite = LlvmApi.BuildOr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, stateIdx, LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateAllComposite), 1), "sched_is_all_comp"),
            isRaceComposite, "sched_is_composite");
        LlvmApi.BuildCondBr(builder, isComposite, layout.CompositeBlock, layout.LeafCoroBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.LeafCoroBlock);
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, stateIdx, completedConst, "sched_is_leaf"), layout.LeafBlock, layout.CoroBlock);
        return (task, isRaceComposite);
    }

    private static void EmitSchedulerCompositePhase(LlvmCodegenState state, LlvmValueHandle task, LlvmValueHandle isRaceComposite, in SchedulerRunLayout layout)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);

        // Composite (all/race): install its arena, step it, and complete or drop (a child completion
        // re-enqueues it) — see EmitStepComposite.
        LlvmApi.PositionBuilderAtEnd(builder, layout.CompositeBlock);
        LlvmValueHandle compStatusSlot = LlvmApi.BuildAlloca(builder, state.I64, "sched_comp_status_slot");
        (LlvmValueHandle compOwner, LlvmValueHandle compSavedCursor, LlvmValueHandle compSavedEnd) = EmitInstallTaskArena(state, task, "sched_comp");
        LlvmApi.BuildCondBr(builder, isRaceComposite, layout.RaceStepBlock, layout.AllStepBlock);
        LlvmApi.PositionBuilderAtEnd(builder, layout.AllStepBlock);
        LlvmApi.BuildStore(builder, EmitStepComposite(state, task, isRace: false, "sched_all"), compStatusSlot);
        LlvmApi.BuildBr(builder, layout.CompAfterBlock);
        LlvmApi.PositionBuilderAtEnd(builder, layout.RaceStepBlock);
        LlvmApi.BuildStore(builder, EmitStepComposite(state, task, isRace: true, "sched_race"), compStatusSlot);
        LlvmApi.BuildBr(builder, layout.CompAfterBlock);
        LlvmApi.PositionBuilderAtEnd(builder, layout.CompAfterBlock);
        EmitRestoreTaskArena(state, compOwner, compSavedCursor, compSavedEnd, "sched_comp");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildLoad2(builder, state.I64, compStatusSlot, "sched_comp_status"), zero, "sched_comp_complete"), layout.CompleteBlock, layout.LoopBlock);
    }

    private static void EmitSchedulerLeafCoroPhase(LlvmCodegenState state, LlvmValueHandle task, in SchedulerRunLayout layout)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);

        LlvmApi.PositionBuilderAtEnd(builder, layout.LeafBlock);
        (LlvmValueHandle leafOwner, LlvmValueHandle leafSavedCursor, LlvmValueHandle leafSavedEnd) = EmitInstallTaskArena(state, task, "sched_leaf");
        LlvmValueHandle leafStatus = EmitStepLeafTask(state, task, "sched_leaf_step");
        EmitRestoreTaskArena(state, leafOwner, leafSavedCursor, leafSavedEnd, "sched_leaf");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, leafStatus, zero, "sched_leaf_done"), layout.CompleteBlock, layout.ParkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.ParkBlock);
        LlvmValueHandle parkedHeadGlobal = ParkedLeavesHeadGlobal(state);
        StoreMemory(state, task, TaskStructLayout.ReadyNext, LlvmApi.BuildLoad2(builder, state.I64, parkedHeadGlobal, "sched_parked_head"), "sched_park_link");
        LlvmApi.BuildStore(builder, task, parkedHeadGlobal);
        LlvmApi.BuildBr(builder, layout.LoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.CoroBlock);
        (LlvmValueHandle coroOwner, LlvmValueHandle coroSavedCursor, LlvmValueHandle coroSavedEnd) = EmitInstallTaskArena(state, task, "sched_coro");
        LlvmValueHandle coroutineFn = LoadMemory(state, task, TaskStructLayout.CoroutineFn, "sched_coro_fn");
        LlvmTypeHandle coroutineFnType = LlvmApi.FunctionType(state.I64, [state.I64, state.I64]);
        LlvmValueHandle typedFnPtr = LlvmApi.BuildIntToPtr(builder, coroutineFn, LlvmApi.PointerTypeInContext(state.Target.Context, 0), "sched_coro_ptr");
        LlvmValueHandle coroStatus = LlvmApi.BuildCall2(builder, coroutineFnType, typedFnPtr, [task, zero], "sched_coro_status");
        EmitRestoreTaskArena(state, coroOwner, coroSavedCursor, coroSavedEnd, "sched_coro");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, coroStatus, zero, "sched_suspended"), layout.SuspendBlock, layout.CompleteBlock);
    }

    private static void EmitSchedulerSuspendPhase(LlvmCodegenState state, LlvmValueHandle task, in SchedulerRunLayout layout)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);

        // Suspended on a fresh AwaitedTask: schedule it, link it back to this task, and park this task.
        LlvmApi.PositionBuilderAtEnd(builder, layout.SuspendBlock);
        LlvmValueHandle awaited = LoadMemory(state, task, TaskStructLayout.AwaitedTask, "sched_awaited");
        StoreMemory(state, awaited, TaskStructLayout.Waiter, task, "sched_set_waiter");
        StoreMemory(state, awaited, TaskStructLayout.ArenaOwner, LoadMemory(state, task, TaskStructLayout.ArenaOwner, "sched_owner"), "sched_inherit_owner");

        // Async-loop arena-reset veto: a loop coroutine (LoopResetOk = 1) awaited under a composite
        // (all/race) ancestor shares its arena with the composite's other children, which run
        // interleaved with the loop's iterations — resetting to a stale watermark could free a
        // sibling's live allocations. Walk the waiter chain (fixed for the helper's lifetime; it
        // ends at a spawned root or the main task, never crossing an arena boundary) and clear the
        // flag if any ancestor is a composite. Skipped entirely for ordinary tasks (flag already 0).
        LlvmBasicBlockHandle walkInitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_reset_walk_init");
        LlvmBasicBlockHandle walkLoopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_reset_walk_loop");
        LlvmBasicBlockHandle walkBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_reset_walk_body");
        LlvmBasicBlockHandle walkClearBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_reset_walk_clear");
        LlvmBasicBlockHandle walkNextBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_reset_walk_next");
        LlvmBasicBlockHandle suspendEnqueueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_suspend_enqueue");
        LlvmValueHandle awaitedResetOk = LoadMemory(state, awaited, TaskStructLayout.LoopResetOk, "sched_awaited_reset_ok");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, awaitedResetOk, zero, "sched_has_reset_ok"), walkInitBlock, suspendEnqueueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, walkInitBlock);
        LlvmValueHandle walkCurSlot = LlvmApi.BuildAlloca(builder, state.I64, "sched_reset_walk_cur");
        LlvmApi.BuildStore(builder, task, walkCurSlot);
        LlvmApi.BuildBr(builder, walkLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, walkLoopBlock);
        LlvmValueHandle walkCur = LlvmApi.BuildLoad2(builder, state.I64, walkCurSlot, "sched_reset_walk_cur_load");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, walkCur, zero, "sched_reset_walk_end"), suspendEnqueueBlock, walkBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, walkBodyBlock);
        LlvmValueHandle walkState = LoadMemory(state, walkCur, TaskStructLayout.StateIndex, "sched_reset_walk_state");
        LlvmValueHandle walkIsComposite = LlvmApi.BuildOr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, walkState, LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateAllComposite), 1), "sched_reset_walk_is_all"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, walkState, LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateRaceComposite), 1), "sched_reset_walk_is_race"),
            "sched_reset_walk_is_comp");
        LlvmApi.BuildCondBr(builder, walkIsComposite, walkClearBlock, walkNextBlock);

        LlvmApi.PositionBuilderAtEnd(builder, walkClearBlock);
        StoreMemory(state, awaited, TaskStructLayout.LoopResetOk, zero, "sched_reset_veto");
        LlvmApi.BuildBr(builder, suspendEnqueueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, walkNextBlock);
        LlvmApi.BuildStore(builder, LoadMemory(state, walkCur, TaskStructLayout.Waiter, "sched_reset_walk_up"), walkCurSlot);
        LlvmApi.BuildBr(builder, walkLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, suspendEnqueueBlock);
        _ = EmitNetworkingRuntimeCall(state, "ashes_ready_enqueue", [awaited], "sched_enqueue_sub");
        LlvmApi.BuildBr(builder, layout.LoopBlock);
    }

    private static void EmitSchedulerCompletePhase(LlvmCodegenState state, LlvmValueHandle taskSlot, in SchedulerRunLayout layout)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);

        // Completed: hand the result to the waiter (if any) and re-enqueue it.
        LlvmApi.PositionBuilderAtEnd(builder, layout.CompleteBlock);
        LlvmValueHandle completedTask = LlvmApi.BuildLoad2(builder, state.I64, taskSlot, "sched_completed");
        LlvmValueHandle waiter = LoadMemory(state, completedTask, TaskStructLayout.Waiter, "sched_waiter");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, waiter, zero, "sched_has_waiter"), layout.DeliverBlock, layout.NoWaiterBlock);

        // Deliver to the waiter. A normal coroutine waiter resumes with the result. A composite waiter
        // is different: an all-composite only decrements its pending counter (and is enqueued once, when
        // it reaches 0 — it reads its children directly); a race-composite takes the first child's result
        // and is enqueued once. (A spawned root task delivered here keeps its arena — no copy-out yet —
        // so an awaited spawn currently leaks; fire-and-forget spawns take the no-waiter path and reap.)
        LlvmApi.PositionBuilderAtEnd(builder, layout.DeliverBlock);
        LlvmValueHandle waiterState = LoadMemory(state, waiter, TaskStructLayout.StateIndex, "sched_waiter_state");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, waiterState, LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateAllComposite), 1), "sched_waiter_is_all"), layout.AllWaiterBlock, layout.NotAllWaiterBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.AllWaiterBlock);
        LlvmValueHandle newCounter = LlvmApi.BuildSub(builder, LoadMemory(state, waiter, TaskStructLayout.WaitData0, "sched_all_counter"), LlvmApi.ConstInt(state.I64, 1, 0), "sched_all_counter_dec");
        StoreMemory(state, waiter, TaskStructLayout.WaitData0, newCounter, "sched_all_counter_store");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, newCounter, zero, "sched_all_ready"), layout.EnqueueCompositeBlock, layout.LoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.NotAllWaiterBlock);
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, waiterState, LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateRaceComposite), 1), "sched_waiter_is_race"), layout.RaceWaiterBlock, layout.NormalWaiterBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.RaceWaiterBlock);
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, LoadMemory(state, waiter, TaskStructLayout.WaitData0, "sched_race_resolved"), zero, "sched_race_unresolved"), layout.RaceFirstBlock, layout.LoopBlock);
        LlvmApi.PositionBuilderAtEnd(builder, layout.RaceFirstBlock);
        StoreMemory(state, waiter, TaskStructLayout.ResultSlot, LoadMemory(state, completedTask, TaskStructLayout.ResultSlot, "sched_race_result"), "sched_race_deliver");
        StoreMemory(state, waiter, TaskStructLayout.WaitData0, LlvmApi.ConstInt(state.I64, 1, 0), "sched_race_mark_resolved");
        _ = EmitNetworkingRuntimeCall(state, "ashes_ready_enqueue", [waiter], "sched_race_enqueue");
        LlvmApi.BuildBr(builder, layout.LoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.EnqueueCompositeBlock);
        _ = EmitNetworkingRuntimeCall(state, "ashes_ready_enqueue", [waiter], "sched_all_enqueue");
        LlvmApi.BuildBr(builder, layout.LoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.NormalWaiterBlock);
        StoreMemory(state, waiter, TaskStructLayout.ResultSlot, LoadMemory(state, completedTask, TaskStructLayout.ResultSlot, "sched_completed_result"), "sched_deliver_result");
        StoreMemory(state, waiter, TaskStructLayout.AwaitedTask, zero, "sched_clear_awaited");
        _ = EmitNetworkingRuntimeCall(state, "ashes_ready_enqueue", [waiter], "sched_enqueue_waiter");
        LlvmApi.BuildBr(builder, layout.LoopBlock);
    }

    /// <summary>
    /// Aggregate wait for the run-queue scheduler: with the ready queue empty, blocks until a parked
    /// leaf is ready, then re-queues every parked leaf so the loop re-steps them (a leaf whose I/O is
    /// still not ready re-parks on its next step). Timer leaves fold into the wait timeout and have
    /// their remaining decremented by the elapsed time; socket/TLS leaves register their fd in the
    /// persistent epoll set and the wait is an <c>epoll_wait</c> (Linux), or fill a pollfd scratch
    /// array and the wait is one <c>WSAPoll</c> over the parked set (Windows). With no socket leaves
    /// the wait is a cooperative sleep to the earliest timer deadline. Parked leaves link through
    /// <c>ReadyNext</c> off <c>__ashes_parked_head</c>. Requeue-all-on-wakeup is O(parked) per wakeup —
    /// correct with the level-triggered epoll set and the per-wait pollfd rebuild; per-fd wakeup
    /// targeting is a later refinement.
    /// </summary>
    // Shared slots + persistent handles for EmitSchedulerAggregateWait.
    private readonly record struct AggregateWaitContext(
        LlvmValueHandle MinSlot,
        LlvmValueHandle HasSocketSlot,
        LlvmValueHandle CursorSlot,
        LlvmValueHandle ElapsedSlot,
        LlvmValueHandle EpollFd,
        LlvmValueHandle PollArrayPtr,
        LlvmValueHandle PollArrayAddress,
        LlvmValueHandle PollCountSlot);

    private static void EmitSchedulerAggregateWait(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle maxVal = LlvmApi.ConstInt(state.I64, unchecked((ulong)long.MaxValue), 0);
        bool linux = IsLinuxFlavor(state.Flavor);
        // Socket waits on Windows need the WSAPoll import, which exists exactly when the program
        // uses the networking runtime; without it no socket leaf can ever park, so the timer-only
        // sleep below is complete.
        bool windowsSockets = IsWindowsFlavor(state.Flavor)
            && state.WindowsWsaPollImport.Ptr != 0;

        LlvmValueHandle minSlot = LlvmApi.BuildAlloca(builder, state.I64, "saw_min_slot");
        LlvmValueHandle hasSocketSlot = LlvmApi.BuildAlloca(builder, state.I64, "saw_has_socket_slot");
        LlvmValueHandle cursorSlot = LlvmApi.BuildAlloca(builder, state.I64, "saw_cursor_slot");
        LlvmValueHandle elapsedSlot = LlvmApi.BuildAlloca(builder, state.I64, "saw_elapsed_slot");
        LlvmApi.BuildStore(builder, maxVal, minSlot);
        LlvmApi.BuildStore(builder, zero, hasSocketSlot);
        LlvmApi.BuildStore(builder, zero, elapsedSlot);

        (LlvmValueHandle pollCountSlot, LlvmValueHandle pollArrayPtr, LlvmValueHandle pollArrayAddress) = EmitAggregateWaitWindowsPollSetup(state, windowsSockets, zero);
        LlvmValueHandle epollFd = EmitAggregateWaitEpollSetup(state, linux, zero);
        var ctx = new AggregateWaitContext(minSlot, hasSocketSlot, cursorSlot, elapsedSlot, epollFd, pollArrayPtr, pollArrayAddress, pollCountSlot);

        EmitAggregateWaitScan(state, ctx);
        EmitAggregateWaitBlockPhase(state, ctx);
        EmitAggregateWaitRequeue(state, ctx);
    }

    // Windows: pollfd scratch array (module-global, same shape as the legacy detached wait's) plus a
    // fill count, rebuilt on every wait from the parked list. Returns default handles off Windows.
    private static (LlvmValueHandle PollCountSlot, LlvmValueHandle PollArrayPtr, LlvmValueHandle PollArrayAddress) EmitAggregateWaitWindowsPollSetup(LlvmCodegenState state, bool windowsSockets, LlvmValueHandle zero)
    {
        if (!windowsSockets)
        {
            return (default, default, default);
        }

        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle pollArrayType = LlvmApi.ArrayType2(state.I8, (ulong)(WindowsPollFdSize * DetachedPollFdCapacity));
        LlvmValueHandle pollArrayGlobal = ReadLineScratchGlobal(state, "__ashes_sched_pollfds", pollArrayType);
        LlvmValueHandle pollArrayPtr = GetArrayElementPointer(state, pollArrayType, pollArrayGlobal, zero, "saw_poll_array_ptr");
        LlvmValueHandle pollArrayAddress = LlvmApi.BuildPtrToInt(builder, pollArrayPtr, state.I64, "saw_poll_array_address");
        LlvmValueHandle pollCountSlot = LlvmApi.BuildAlloca(builder, state.I64, "saw_poll_count_slot");
        LlvmApi.BuildStore(builder, zero, pollCountSlot);
        return (pollCountSlot, pollArrayPtr, pollArrayAddress);
    }

    // Persistent per-reactor epoll fd (created once, reused). Returns zero off Linux.
    private static LlvmValueHandle EmitAggregateWaitEpollSetup(LlvmCodegenState state, bool linux, LlvmValueHandle zero)
    {
        if (!linux)
        {
            return zero;
        }

        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle epollGlobal = EpollFdGlobal(state);
        LlvmValueHandle epollFdSlot = LlvmApi.BuildAlloca(builder, state.I64, "saw_epoll_fd_slot");
        LlvmValueHandle existingFd = LlvmApi.BuildLoad2(builder, state.I64, epollGlobal, "saw_epoll_existing");
        LlvmApi.BuildStore(builder, existingFd, epollFdSlot);
        LlvmBasicBlockHandle createEpollBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_epoll_create");
        LlvmBasicBlockHandle haveEpollBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_epoll_have");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, existingFd, zero, "saw_epoll_uncreated"), createEpollBlock, haveEpollBlock);
        LlvmApi.PositionBuilderAtEnd(builder, createEpollBlock);
        LlvmValueHandle newFd = EmitLinuxSyscall(state, SyscallEpollCreate1, zero, zero, zero, "saw_epoll_create1");
        LlvmApi.BuildStore(builder, newFd, epollGlobal);
        LlvmApi.BuildStore(builder, newFd, epollFdSlot);
        LlvmApi.BuildBr(builder, haveEpollBlock);
        LlvmApi.PositionBuilderAtEnd(builder, haveEpollBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, epollFdSlot, "saw_epoll_fd");
    }

    // Pass 1: minimum timer remaining + register socket leaves. Leaves the builder at the after-scan
    // block so the caller continues emitting the blocking wait there.
    private static void EmitAggregateWaitScan(LlvmCodegenState state, in AggregateWaitContext ctx)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle timerKind = LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTimer, 0);
        LlvmValueHandle parkedHeadGlobal = ParkedLeavesHeadGlobal(state);

        LlvmApi.BuildStore(builder, LlvmApi.BuildLoad2(builder, state.I64, parkedHeadGlobal, "saw_head0"), ctx.CursorSlot);
        LlvmBasicBlockHandle scanBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_scan");
        LlvmBasicBlockHandle scanBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_scan_body");
        LlvmBasicBlockHandle timerBranch = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_timer");
        LlvmBasicBlockHandle socketBranch = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_socket");
        LlvmBasicBlockHandle scanNextBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_scan_next");
        LlvmBasicBlockHandle afterScanBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_after_scan");
        LlvmApi.BuildBr(builder, scanBlock);
        LlvmApi.PositionBuilderAtEnd(builder, scanBlock);
        LlvmValueHandle scanCur = LlvmApi.BuildLoad2(builder, state.I64, ctx.CursorSlot, "saw_scan_cur");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, scanCur, zero, "saw_scan_end"), afterScanBlock, scanBodyBlock);
        LlvmApi.PositionBuilderAtEnd(builder, scanBodyBlock);
        LlvmValueHandle scanKind = LoadMemory(state, scanCur, TaskStructLayout.WaitKind, "saw_scan_kind");
        LlvmApi.BuildStore(builder, LoadMemory(state, scanCur, TaskStructLayout.ReadyNext, "saw_scan_next_load"), ctx.CursorSlot);
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, scanKind, timerKind, "saw_is_timer"), timerBranch, socketBranch);
        LlvmApi.PositionBuilderAtEnd(builder, timerBranch);
        LlvmValueHandle rem = LoadMemory(state, scanCur, TaskStructLayout.SleepDurationMs, "saw_rem");
        LlvmValueHandle curMin = LlvmApi.BuildLoad2(builder, state.I64, ctx.MinSlot, "saw_cur_min");
        LlvmApi.BuildStore(builder, LlvmApi.BuildSelect(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, rem, curMin, "saw_lt"), rem, curMin, "saw_min_upd"), ctx.MinSlot);
        LlvmApi.BuildBr(builder, scanNextBlock);
        LlvmApi.PositionBuilderAtEnd(builder, socketBranch);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), ctx.HasSocketSlot);
        EmitAggregateWaitRegisterSocket(state, ctx, scanKind, scanCur);
        LlvmApi.BuildBr(builder, scanNextBlock);
        LlvmApi.PositionBuilderAtEnd(builder, scanNextBlock);
        LlvmApi.BuildBr(builder, scanBlock);
        LlvmApi.PositionBuilderAtEnd(builder, afterScanBlock);
    }

    // Registers one parked socket leaf: epoll_ctl on Linux, or a pollfd scratch slot on Windows.
    private static void EmitAggregateWaitRegisterSocket(LlvmCodegenState state, in AggregateWaitContext ctx, LlvmValueHandle scanKind, LlvmValueHandle scanCur)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        bool windowsSockets = IsWindowsFlavor(state.Flavor) && state.WindowsWsaPollImport.Ptr != 0;
        if (IsLinuxFlavor(state.Flavor))
        {
            LlvmValueHandle readish = LlvmApi.BuildOr(builder,
                LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, scanKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitSocketRead, 0), "saw_is_read1"),
                LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, scanKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTlsWantRead, 0), "saw_is_read3"),
                "saw_readish");
            LlvmValueHandle mask = LlvmApi.BuildSelect(builder, readish, LlvmApi.ConstInt(state.I64, 0x001, 0), LlvmApi.ConstInt(state.I64, 0x004, 0), "saw_mask");
            _ = EmitNetworkingRuntimeCall(state, "ashes_epoll_register", [ctx.EpollFd, LoadMemory(state, scanCur, TaskStructLayout.WaitHandle, "saw_wait_handle"), mask], "saw_register");
        }
        else if (windowsSockets)
        {
            // Fill the next pollfd slot, capped at capacity. An overflow leaf simply is not polled
            // this round — the requeue-all pass re-steps it, and it re-parks for the next wait.
            LlvmValueHandle readish = LlvmApi.BuildOr(builder,
                LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, scanKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitSocketRead, 0), "saw_is_read1"),
                LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, scanKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTlsWantRead, 0), "saw_is_read3"),
                "saw_readish");
            LlvmValueHandle fillCount = LlvmApi.BuildLoad2(builder, state.I64, ctx.PollCountSlot, "saw_poll_fill_count");
            LlvmBasicBlockHandle fillBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_poll_fill");
            LlvmBasicBlockHandle fillDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_poll_fill_done");
            LlvmApi.BuildCondBr(builder,
                LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, fillCount, LlvmApi.ConstInt(state.I64, DetachedPollFdCapacity, 0), "saw_poll_has_room"),
                fillBlock, fillDoneBlock);
            LlvmApi.PositionBuilderAtEnd(builder, fillBlock);
            LlvmValueHandle slotAddress = LlvmApi.BuildAdd(builder, ctx.PollArrayAddress,
                LlvmApi.BuildMul(builder, fillCount, LlvmApi.ConstInt(state.I64, WindowsPollFdSize, 0), "saw_poll_slot_offset"),
                "saw_poll_slot_address");
            LlvmValueHandle eventMask = EmitWindowsPollEventMask(state, readish, "saw_poll_event_mask");
            EmitWindowsInitializePollFd(state, slotAddress, LoadMemory(state, scanCur, TaskStructLayout.WaitHandle, "saw_wait_handle"), eventMask, "saw_pollfd");
            LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, fillCount, LlvmApi.ConstInt(state.I64, 1, 0), "saw_poll_count_next"), ctx.PollCountSlot);
            LlvmApi.BuildBr(builder, fillDoneBlock);
            LlvmApi.PositionBuilderAtEnd(builder, fillDoneBlock);
        }
    }

    // Block until ready. With sockets, epoll_wait/WSAPoll bounded by the earliest timer; else sleep.
    private static void EmitAggregateWaitBlockPhase(LlvmCodegenState state, in AggregateWaitContext ctx)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle maxVal = LlvmApi.ConstInt(state.I64, unchecked((ulong)long.MaxValue), 0);
        bool windowsSockets = IsWindowsFlavor(state.Flavor) && state.WindowsWsaPollImport.Ptr != 0;

        LlvmValueHandle minRem = LlvmApi.BuildLoad2(builder, state.I64, ctx.MinSlot, "saw_min");
        LlvmValueHandle sleepMs = LlvmApi.BuildSelect(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, minRem, maxVal, "saw_no_timer"), zero, minRem, "saw_sleep_ms");
        if (IsLinuxFlavor(state.Flavor))
        {
            EmitAggregateWaitLinuxWait(state, ctx, minRem, sleepMs);
        }
        else if (windowsSockets)
        {
            EmitAggregateWaitWindowsWait(state, ctx, minRem, sleepMs);
        }
        else
        {
            EmitNanosleep(state, sleepMs);
            LlvmApi.BuildStore(builder, sleepMs, ctx.ElapsedSlot);
        }
    }

    private static void EmitAggregateWaitLinuxWait(LlvmCodegenState state, in AggregateWaitContext ctx, LlvmValueHandle minRem, LlvmValueHandle sleepMs)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle maxVal = LlvmApi.ConstInt(state.I64, unchecked((ulong)long.MaxValue), 0);
        LlvmValueHandle hasSocket = LlvmApi.BuildLoad2(builder, state.I64, ctx.HasSocketSlot, "saw_has_socket");
        LlvmTypeHandle eventType = LlvmApi.ArrayType2(state.I8, 16);
        LlvmValueHandle eventOut = GetArrayElementPointer(state, eventType, LlvmApi.BuildAlloca(builder, eventType, "saw_event_out_storage"), zero, "saw_event_out");
        LlvmBasicBlockHandle socketWaitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_socket_wait");
        LlvmBasicBlockHandle timerWaitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_timer_wait");
        LlvmBasicBlockHandle afterWaitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_after_wait");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, hasSocket, zero, "saw_do_epoll"), socketWaitBlock, timerWaitBlock);
        LlvmApi.PositionBuilderAtEnd(builder, socketWaitBlock);
        LlvmValueHandle timeout = LlvmApi.BuildSelect(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, minRem, maxVal, "saw_epoll_no_timer"), LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), minRem, "saw_epoll_timeout");
        // If graceful shutdown was requested while a handler ran (Stop.stop, or a signal that
        // arrived between epoll waits), the flag is set but no fd wakes epoll. Cap the wait to 0
        // so the loop re-steps the parked accept leaf, which then arms the drain (a 50 ms timer
        // from there on). Without this a single reactor's self-requested stop would block here.
        LlvmValueHandle shutdownRequested = LlvmApi.BuildLoad2(builder, state.I64, ShutdownFlagGlobal(state), "saw_shutdown_flag");
        timeout = LlvmApi.BuildSelect(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, shutdownRequested, zero, "saw_shutdown_set"),
            zero, timeout, "saw_epoll_timeout_eff");
        LlvmValueHandle startMs = EmitMonotonicNowMs(state, "saw_wait_start");
        LlvmValueHandle eventArg = LlvmApi.BuildPtrToInt(builder, eventOut, state.I64, "saw_event_arg");
        if (IsLinuxArm64Flavor(state.Flavor))
        {
            EmitLinuxSyscall6(state, SyscallEpollWait, ctx.EpollFd, eventArg, LlvmApi.ConstInt(state.I64, 1, 0), timeout, zero, zero, "saw_epoll_wait");
        }
        else
        {
            EmitLinuxSyscall4(state, SyscallEpollWait, ctx.EpollFd, eventArg, LlvmApi.ConstInt(state.I64, 1, 0), timeout, "saw_epoll_wait");
        }
        LlvmApi.BuildStore(builder, LlvmApi.BuildSub(builder, EmitMonotonicNowMs(state, "saw_wait_end"), startMs, "saw_epoll_elapsed"), ctx.ElapsedSlot);
        LlvmApi.BuildBr(builder, afterWaitBlock);
        LlvmApi.PositionBuilderAtEnd(builder, timerWaitBlock);
        EmitNanosleep(state, sleepMs);
        LlvmApi.BuildStore(builder, sleepMs, ctx.ElapsedSlot);
        LlvmApi.BuildBr(builder, afterWaitBlock);
        LlvmApi.PositionBuilderAtEnd(builder, afterWaitBlock);
    }

    private static void EmitAggregateWaitWindowsWait(LlvmCodegenState state, in AggregateWaitContext ctx, LlvmValueHandle minRem, LlvmValueHandle sleepMs)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle maxVal = LlvmApi.ConstInt(state.I64, unchecked((ulong)long.MaxValue), 0);
        LlvmValueHandle hasSocket = LlvmApi.BuildLoad2(builder, state.I64, ctx.HasSocketSlot, "saw_has_socket");
        LlvmBasicBlockHandle socketWaitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_socket_wait");
        LlvmBasicBlockHandle timerWaitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_timer_wait");
        LlvmBasicBlockHandle afterWaitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_after_wait");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, hasSocket, zero, "saw_do_wsapoll"), socketWaitBlock, timerWaitBlock);
        LlvmApi.PositionBuilderAtEnd(builder, socketWaitBlock);
        // Cap the wait at 200 ms: the console-ctrl handler runs on another thread and cannot
        // interrupt a parked WSAPoll the way a signal EINTRs epoll_wait, so the loop must
        // re-observe the shutdown flag on a short bound (the accept step's drain re-checks on
        // every re-step). An idle server wakes 5x/s; the cost is negligible.
        LlvmValueHandle uncapped = LlvmApi.BuildSelect(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, minRem, maxVal, "saw_wsapoll_no_timer"), LlvmApi.ConstInt(state.I64, 200, 0), minRem, "saw_wsapoll_timeout_raw");
        LlvmValueHandle cap = LlvmApi.ConstInt(state.I64, 200, 0);
        LlvmValueHandle timeout = LlvmApi.BuildSelect(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, uncapped, cap, "saw_wsapoll_over_cap"), cap, uncapped, "saw_wsapoll_timeout");
        LlvmValueHandle startMs = EmitMonotonicNowMs(state, "saw_wait_start");
        LlvmValueHandle pollCount = LlvmApi.BuildLoad2(builder, state.I64, ctx.PollCountSlot, "saw_poll_count");
        _ = EmitWindowsWsaPoll(state, ctx.PollArrayPtr, pollCount, timeout, "saw_wsapoll_wait");
        LlvmApi.BuildStore(builder, LlvmApi.BuildSub(builder, EmitMonotonicNowMs(state, "saw_wait_end"), startMs, "saw_wsapoll_elapsed"), ctx.ElapsedSlot);
        LlvmApi.BuildBr(builder, afterWaitBlock);
        LlvmApi.PositionBuilderAtEnd(builder, timerWaitBlock);
        EmitNanosleep(state, sleepMs);
        LlvmApi.BuildStore(builder, sleepMs, ctx.ElapsedSlot);
        LlvmApi.BuildBr(builder, afterWaitBlock);
        LlvmApi.PositionBuilderAtEnd(builder, afterWaitBlock);
    }

    // Pass 2: re-queue every parked leaf; decrement timer leaves by the elapsed time.
    private static void EmitAggregateWaitRequeue(LlvmCodegenState state, in AggregateWaitContext ctx)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle timerKind = LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTimer, 0);
        LlvmValueHandle parkedHeadGlobal = ParkedLeavesHeadGlobal(state);
        LlvmValueHandle elapsed = LlvmApi.BuildLoad2(builder, state.I64, ctx.ElapsedSlot, "saw_elapsed");

        LlvmApi.BuildStore(builder, LlvmApi.BuildLoad2(builder, state.I64, parkedHeadGlobal, "saw_head1"), ctx.CursorSlot);
        LlvmBasicBlockHandle reqBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_requeue");
        LlvmBasicBlockHandle reqBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_requeue_body");
        LlvmBasicBlockHandle reqDecBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_requeue_dec");
        LlvmBasicBlockHandle reqEnqBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_requeue_enq");
        LlvmBasicBlockHandle reqDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_requeue_done");
        LlvmApi.BuildBr(builder, reqBlock);
        LlvmApi.PositionBuilderAtEnd(builder, reqBlock);
        LlvmValueHandle reqCur = LlvmApi.BuildLoad2(builder, state.I64, ctx.CursorSlot, "saw_req_cur");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, reqCur, zero, "saw_req_end"), reqDoneBlock, reqBodyBlock);
        LlvmApi.PositionBuilderAtEnd(builder, reqBodyBlock);
        LlvmValueHandle reqNext = LoadMemory(state, reqCur, TaskStructLayout.ReadyNext, "saw_req_next");
        LlvmApi.BuildStore(builder, reqNext, ctx.CursorSlot);
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, LoadMemory(state, reqCur, TaskStructLayout.WaitKind, "saw_req_kind"), timerKind, "saw_req_is_timer"), reqDecBlock, reqEnqBlock);
        LlvmApi.PositionBuilderAtEnd(builder, reqDecBlock);
        StoreMemory(state, reqCur, TaskStructLayout.SleepDurationMs, LlvmApi.BuildSub(builder, LoadMemory(state, reqCur, TaskStructLayout.SleepDurationMs, "saw_req_rem"), elapsed, "saw_req_new_rem"), "saw_req_store_rem");
        LlvmApi.BuildBr(builder, reqEnqBlock);
        LlvmApi.PositionBuilderAtEnd(builder, reqEnqBlock);
        _ = EmitNetworkingRuntimeCall(state, "ashes_ready_enqueue", [reqCur], "saw_req_enqueue");
        LlvmApi.BuildBr(builder, reqBlock);
        LlvmApi.PositionBuilderAtEnd(builder, reqDoneBlock);
        LlvmApi.BuildStore(builder, zero, parkedHeadGlobal);
    }

    private static LlvmValueHandle EmitSpawnTask(LlvmCodegenState state, LlvmValueHandle taskPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        // Private arena chunk for the task; header slot 0 = no previous chunk.
        LlvmValueHandle chunkBase = EmitAllocateOsMemory(state, LlvmApi.ConstInt(state.I64, HeapChunkBytes, 0), "spawn_chunk");
        EmitHeapChunkInitCheck(state, chunkBase);
        StoreMemory(state, chunkBase, 0, LlvmApi.ConstInt(state.I64, 0, 0), "spawn_chunk_prev");

        // Copy the task frame (header + captures + live slots) into the chunk.
        LlvmValueHandle frameSize = LoadMemory(state, taskPtr, TaskStructLayout.FrameSizeBytes, "spawn_frame_size");
        LlvmValueHandle copyPtr = LlvmApi.BuildAdd(builder, chunkBase, LlvmApi.ConstInt(state.I64, 8, 0), "spawn_copy_ptr");
        LlvmValueHandle destPtr = LlvmApi.BuildIntToPtr(builder, copyPtr, state.I8Ptr, "spawn_dest_ptr");
        LlvmValueHandle srcPtr = LlvmApi.BuildIntToPtr(builder, taskPtr, state.I8Ptr, "spawn_src_ptr");
        LlvmApi.BuildMemCpy(builder, destPtr, 1, srcPtr, 1, frameSize);

        // The copy's private arena starts right after the frame.
        LlvmValueHandle alignedSize = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildAdd(builder, frameSize, LlvmApi.ConstInt(state.I64, 7, 0), "spawn_size_plus7"),
            LlvmApi.ConstInt(state.I64, unchecked((ulong)~7L), 1), "spawn_size_aligned");
        StoreMemory(state, copyPtr, TaskStructLayout.ArenaCursor,
            LlvmApi.BuildAdd(builder, copyPtr, alignedSize, "spawn_arena_cursor"), "spawn_arena_cursor_store");
        StoreMemory(state, copyPtr, TaskStructLayout.ArenaEnd,
            LlvmApi.BuildAdd(builder, chunkBase, LlvmApi.ConstInt(state.I64, HeapChunkBytes, 0), "spawn_arena_end"), "spawn_arena_end_store");

        if (state.UseRunQueueScheduler)
        {
            // Run-queue mode: the spawned task is its own arena owner (its sub-tasks inherit it), has no
            // waiter (fire-and-forget), and goes straight onto the ready queue. The live-spawned count
            // feeds the shutdown drain (decremented when the task's arena is reaped on completion).
            StoreMemory(state, copyPtr, TaskStructLayout.ArenaOwner, copyPtr, "spawn_arena_owner_self");
            StoreMemory(state, copyPtr, TaskStructLayout.Waiter, LlvmApi.ConstInt(state.I64, 0, 0), "spawn_no_waiter");
            LlvmValueHandle liveGlobal = LiveSpawnedGlobal(state);
            LlvmApi.BuildStore(builder,
                LlvmApi.BuildAdd(builder, LlvmApi.BuildLoad2(builder, state.I64, liveGlobal, "spawn_live"), LlvmApi.ConstInt(state.I64, 1, 0), "spawn_live_inc"),
                liveGlobal);
            _ = EmitNetworkingRuntimeCall(state, "ashes_ready_enqueue", [copyPtr], "spawn_enqueue");
            return LlvmApi.ConstInt(state.I64, 0, 0);
        }

        // Legacy mode: push onto the detached list.
        LlvmValueHandle headGlobal = DetachedTasksHeadGlobal(state);
        LlvmValueHandle head = LlvmApi.BuildLoad2(builder, state.I64, headGlobal, "spawn_head");
        StoreMemory(state, copyPtr, TaskStructLayout.NextTask, head, "spawn_next_store");
        LlvmApi.BuildStore(builder, copyPtr, headGlobal);

        return LlvmApi.ConstInt(state.I64, 0, 0);
    }

    /// <summary>
    /// Body of <c>ashes_run_detached()</c>: steps every detached task once (until it parks or
    /// completes), with the task's private arena installed while it runs. Completed tasks are
    /// unlinked and their private chunk chains freed. Re-entrancy (a detached step reaching a
    /// nested driver wait) is guarded by a flag. Returns the (possibly empty) list head.
    /// </summary>
    // Up-front slots + basic blocks for EmitRunDetachedBody.
    private readonly record struct RunDetachedLayout(
        LlvmValueHandle CurSlot,
        LlvmValueHandle PrevSlot,
        LlvmValueHandle NextSlot,
        LlvmValueHandle FreeBaseSlot,
        LlvmBasicBlockHandle StartBlock,
        LlvmBasicBlockHandle GuardedBlock,
        LlvmBasicBlockHandle CheckBlock,
        LlvmBasicBlockHandle BodyBlock,
        LlvmBasicBlockHandle ReapBlock,
        LlvmBasicBlockHandle UnlinkHeadBlock,
        LlvmBasicBlockHandle UnlinkMidBlock,
        LlvmBasicBlockHandle FreeCheckBlock,
        LlvmBasicBlockHandle FreeBodyBlock,
        LlvmBasicBlockHandle FreeDoneBlock,
        LlvmBasicBlockHandle KeepBlock,
        LlvmBasicBlockHandle DoneBlock);

    private static RunDetachedLayout EmitRunDetachedPrologue(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        return new RunDetachedLayout(
            LlvmApi.BuildAlloca(builder, state.I64, "rd_cur_slot"),
            LlvmApi.BuildAlloca(builder, state.I64, "rd_prev_slot"),
            LlvmApi.BuildAlloca(builder, state.I64, "rd_next_slot"),
            LlvmApi.BuildAlloca(builder, state.I64, "rd_free_base_slot"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rd_start"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rd_guarded"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rd_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rd_body"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rd_reap"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rd_unlink_head"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rd_unlink_mid"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rd_free_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rd_free_body"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rd_free_done"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rd_keep"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rd_done"));
    }

    private static LlvmValueHandle EmitRunDetachedBody(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle headGlobal = DetachedTasksHeadGlobal(state);
        LlvmValueHandle guardGlobal = DetachedStepGuardGlobal(state);
        RunDetachedLayout layout = EmitRunDetachedPrologue(state);

        LlvmValueHandle guard = LlvmApi.BuildLoad2(builder, state.I64, guardGlobal, "rd_guard");
        LlvmValueHandle reentered = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, guard, LlvmApi.ConstInt(state.I64, 0, 0), "rd_reentered");
        LlvmApi.BuildCondBr(builder, reentered, layout.DoneBlock, layout.StartBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.StartBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), guardGlobal);
        LlvmApi.BuildStore(builder, LlvmApi.BuildLoad2(builder, state.I64, headGlobal, "rd_head"), layout.CurSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), layout.PrevSlot);
        LlvmApi.BuildBr(builder, layout.CheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.CheckBlock);
        LlvmValueHandle cur = LlvmApi.BuildLoad2(builder, state.I64, layout.CurSlot, "rd_cur");
        LlvmValueHandle curDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, cur, LlvmApi.ConstInt(state.I64, 0, 0), "rd_cur_done");
        LlvmApi.BuildCondBr(builder, curDone, layout.GuardedBlock, layout.BodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.GuardedBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), guardGlobal);
        LlvmApi.BuildBr(builder, layout.DoneBlock);

        EmitRunDetachedBodyReap(state, headGlobal, layout);
        EmitRunDetachedFree(state, layout);

        LlvmApi.PositionBuilderAtEnd(builder, layout.DoneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, headGlobal, "rd_result_head");
    }

    private static void EmitRunDetachedBodyReap(LlvmCodegenState state, LlvmValueHandle headGlobal, in RunDetachedLayout layout)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, layout.BodyBlock);
        LlvmValueHandle curBody = LlvmApi.BuildLoad2(builder, state.I64, layout.CurSlot, "rd_cur_body");
        LlvmApi.BuildStore(builder, LoadMemory(state, curBody, TaskStructLayout.NextTask, "rd_next"), layout.NextSlot);
        // Install the task's private arena while it runs; capture growth afterwards.
        LlvmValueHandle savedCursor = LlvmApi.BuildLoad2(builder, state.I64, state.HeapCursorSlot, "rd_saved_cursor");
        LlvmValueHandle savedEnd = LlvmApi.BuildLoad2(builder, state.I64, state.HeapEndSlot, "rd_saved_end");
        LlvmApi.BuildStore(builder, LoadMemory(state, curBody, TaskStructLayout.ArenaCursor, "rd_task_cursor"), state.HeapCursorSlot);
        LlvmApi.BuildStore(builder, LoadMemory(state, curBody, TaskStructLayout.ArenaEnd, "rd_task_end"), state.HeapEndSlot);
        LlvmValueHandle status = EmitNetworkingRuntimeCall(state, "ashes_step_task_until_wait_or_done", [curBody], "rd_step");
        StoreMemory(state, curBody, TaskStructLayout.ArenaCursor,
            LlvmApi.BuildLoad2(builder, state.I64, state.HeapCursorSlot, "rd_grown_cursor"), "rd_task_cursor_back");
        StoreMemory(state, curBody, TaskStructLayout.ArenaEnd,
            LlvmApi.BuildLoad2(builder, state.I64, state.HeapEndSlot, "rd_grown_end"), "rd_task_end_back");
        LlvmApi.BuildStore(builder, savedCursor, state.HeapCursorSlot);
        LlvmApi.BuildStore(builder, savedEnd, state.HeapEndSlot);
        LlvmValueHandle completed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, status, LlvmApi.ConstInt(state.I64, 0, 0), "rd_completed");
        LlvmApi.BuildCondBr(builder, completed, layout.ReapBlock, layout.KeepBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.ReapBlock);
        // Capture the arena end BEFORE freeing (the task lives inside its first chunk).
        LlvmValueHandle reapCur = LlvmApi.BuildLoad2(builder, state.I64, layout.CurSlot, "rd_reap_cur");
        LlvmValueHandle reapEnd = LoadMemory(state, reapCur, TaskStructLayout.ArenaEnd, "rd_reap_end");
        LlvmApi.BuildStore(builder, LlvmApi.BuildSub(builder, reapEnd,
            LlvmApi.ConstInt(state.I64, HeapChunkBytes, 0), "rd_last_chunk_base"), layout.FreeBaseSlot);
        LlvmValueHandle reapPrev = LlvmApi.BuildLoad2(builder, state.I64, layout.PrevSlot, "rd_reap_prev");
        LlvmValueHandle prevIsHead = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, reapPrev, LlvmApi.ConstInt(state.I64, 0, 0), "rd_prev_is_head");
        LlvmApi.BuildCondBr(builder, prevIsHead, layout.UnlinkHeadBlock, layout.UnlinkMidBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.UnlinkHeadBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildLoad2(builder, state.I64, layout.NextSlot, "rd_next_for_head"), headGlobal);
        LlvmApi.BuildBr(builder, layout.FreeCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.UnlinkMidBlock);
        StoreMemory(state, reapPrev, TaskStructLayout.NextTask,
            LlvmApi.BuildLoad2(builder, state.I64, layout.NextSlot, "rd_next_for_mid"), "rd_unlink_mid_store");
        LlvmApi.BuildBr(builder, layout.FreeCheckBlock);
    }

    private static void EmitRunDetachedFree(LlvmCodegenState state, in RunDetachedLayout layout)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, layout.FreeCheckBlock);
        LlvmValueHandle freeBase = LlvmApi.BuildLoad2(builder, state.I64, layout.FreeBaseSlot, "rd_free_base");
        LlvmValueHandle freeDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, freeBase, LlvmApi.ConstInt(state.I64, 0, 0), "rd_free_done_check");
        LlvmApi.BuildCondBr(builder, freeDone, layout.FreeDoneBlock, layout.FreeBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.FreeBodyBlock);
        LlvmValueHandle freeBaseBody = LlvmApi.BuildLoad2(builder, state.I64, layout.FreeBaseSlot, "rd_free_base_body");
        LlvmApi.BuildStore(builder, LoadMemory(state, freeBaseBody, 0, "rd_prev_chunk"), layout.FreeBaseSlot);
        EmitFreeOsMemory(state, freeBaseBody, HeapChunkBytes, "rd_free_chunk");
        LlvmApi.BuildBr(builder, layout.FreeCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.FreeDoneBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildLoad2(builder, state.I64, layout.NextSlot, "rd_advance_after_reap"), layout.CurSlot);
        LlvmApi.BuildBr(builder, layout.CheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.KeepBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildLoad2(builder, state.I64, layout.CurSlot, "rd_keep_cur"), layout.PrevSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildLoad2(builder, state.I64, layout.NextSlot, "rd_advance_after_keep"), layout.CurSlot);
        LlvmApi.BuildBr(builder, layout.CheckBlock);
    }

    /// <summary>
    /// Body of <c>ashes_detached_wait_meta()</c>: scans the detached list and packs
    /// (hasRunnable &lt;&lt; 32) | (minTimerMs + 1) — low 32 bits 0 when no timer-parked task.
    /// A runnable task (not completed, WaitKind == WaitNone) means blocking waits must not block.
    /// </summary>
    // Up-front slots + loop blocks for the scan of EmitDetachedWaitMetaBody.
    private readonly record struct DetachedMetaLayout(
        LlvmValueHandle CurSlot,
        LlvmValueHandle RunnableSlot,
        LlvmValueHandle MinTimerSlot,
        LlvmBasicBlockHandle CheckBlock,
        LlvmBasicBlockHandle BodyBlock,
        LlvmBasicBlockHandle TimerBlock,
        LlvmBasicBlockHandle TimerMinBlock,
        LlvmBasicBlockHandle AdvanceBlock,
        LlvmBasicBlockHandle DoneBlock);

    private static DetachedMetaLayout EmitDetachedMetaPrologue(LlvmCodegenState state, LlvmValueHandle headGlobal)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle curSlot = LlvmApi.BuildAlloca(builder, state.I64, "dm_cur_slot");
        LlvmValueHandle runnableSlot = LlvmApi.BuildAlloca(builder, state.I64, "dm_runnable_slot");
        LlvmValueHandle minTimerSlot = LlvmApi.BuildAlloca(builder, state.I64, "dm_min_timer_slot");
        LlvmApi.BuildStore(builder, LlvmApi.BuildLoad2(builder, state.I64, headGlobal, "dm_head"), curSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), runnableSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), minTimerSlot);
        return new DetachedMetaLayout(
            curSlot, runnableSlot, minTimerSlot,
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dm_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dm_body"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dm_timer"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dm_timer_min"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dm_advance"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dm_done"));
    }

    private static LlvmValueHandle EmitDetachedWaitMetaBody(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle headGlobal = DetachedTasksHeadGlobal(state);

        // When a detached task is itself being stepped (the guard is set), a wait inside that step
        // must not consult the detached list: the "runnable" task the scan would find is the one
        // currently executing (still WaitNone because it has not parked yet), which would force a
        // non-blocking poll and spin the driver, leaking per-wait stack scratch until the stack
        // overflows. Report "nothing pending" so the wait blocks normally on its own leaf. Mirrors
        // the guard in ashes_run_detached.
        LlvmBasicBlockHandle guardedReturnBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dm_guarded_return");
        LlvmBasicBlockHandle scanBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dm_scan");
        LlvmValueHandle guard = LlvmApi.BuildLoad2(builder, state.I64, DetachedStepGuardGlobal(state), "dm_guard");
        LlvmApi.BuildCondBr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, guard, LlvmApi.ConstInt(state.I64, 0, 0), "dm_stepping"),
            guardedReturnBlock, scanBlock);
        LlvmApi.PositionBuilderAtEnd(builder, guardedReturnBlock);
        LlvmApi.BuildRet(builder, LlvmApi.ConstInt(state.I64, 0, 0));
        LlvmApi.PositionBuilderAtEnd(builder, scanBlock);

        DetachedMetaLayout layout = EmitDetachedMetaPrologue(state, headGlobal);
        LlvmApi.BuildBr(builder, layout.CheckBlock);
        EmitDetachedMetaScan(state, layout);

        LlvmApi.PositionBuilderAtEnd(builder, layout.DoneBlock);
        LlvmValueHandle runnableFinal = LlvmApi.BuildLoad2(builder, state.I64, layout.RunnableSlot, "dm_runnable_final");
        LlvmValueHandle minTimerFinal = LlvmApi.BuildLoad2(builder, state.I64, layout.MinTimerSlot, "dm_min_timer_final");
        // Clamp minTimer+1 into 32 bits so the pack below cannot collide with the runnable bit.
        LlvmValueHandle clamped = LlvmApi.BuildSelect(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, minTimerFinal, LlvmApi.ConstInt(state.I64, 0x7FFFFFFF, 0), "dm_clamp_check"),
            LlvmApi.ConstInt(state.I64, 0x7FFFFFFF, 0), minTimerFinal, "dm_clamped");
        return LlvmApi.BuildOr(builder,
            LlvmApi.BuildShl(builder, runnableFinal, LlvmApi.ConstInt(state.I64, 32, 0), "dm_runnable_shifted"),
            clamped, "dm_packed");
    }

    private static void EmitDetachedMetaScan(LlvmCodegenState state, in DetachedMetaLayout layout)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, layout.CheckBlock);
        LlvmValueHandle cur = LlvmApi.BuildLoad2(builder, state.I64, layout.CurSlot, "dm_cur");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, cur, LlvmApi.ConstInt(state.I64, 0, 0), "dm_done_check");
        LlvmApi.BuildCondBr(builder, done, layout.DoneBlock, layout.BodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.BodyBlock);
        LlvmValueHandle stateIdx = LoadMemory(state, cur, TaskStructLayout.StateIndex, "dm_state");
        LlvmValueHandle waitKind = LoadMemory(state, cur, TaskStructLayout.WaitKind, "dm_wait_kind");
        LlvmValueHandle notCompleted = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, stateIdx,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateCompleted), 1), "dm_not_completed");
        LlvmValueHandle waitNone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, waitKind,
            LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitNone, 0), "dm_wait_none");
        LlvmValueHandle runnable = LlvmApi.BuildAnd(builder, notCompleted, waitNone, "dm_runnable");
        LlvmValueHandle priorRunnable = LlvmApi.BuildLoad2(builder, state.I64, layout.RunnableSlot, "dm_prior_runnable");
        LlvmApi.BuildStore(builder, LlvmApi.BuildOr(builder, priorRunnable,
            LlvmApi.BuildZExt(builder, runnable, state.I64, "dm_runnable_i64"), "dm_runnable_or"), layout.RunnableSlot);
        LlvmValueHandle isTimer = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, waitKind,
            LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTimer, 0), "dm_is_timer");
        LlvmApi.BuildCondBr(builder, isTimer, layout.TimerBlock, layout.AdvanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.TimerBlock);
        // Remaining ms live in the sleeping leaf: the task itself, or its awaited sub-task.
        LlvmValueHandle awaited = LoadMemory(state, cur, TaskStructLayout.AwaitedTask, "dm_awaited");
        LlvmValueHandle hasAwaited = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, awaited, LlvmApi.ConstInt(state.I64, 0, 0), "dm_has_awaited");
        LlvmValueHandle sleeper = LlvmApi.BuildSelect(builder, hasAwaited, awaited, cur, "dm_sleeper");
        LlvmValueHandle remaining = LoadMemory(state, sleeper, TaskStructLayout.SleepDurationMs, "dm_remaining");
        LlvmValueHandle minTimer = LlvmApi.BuildLoad2(builder, state.I64, layout.MinTimerSlot, "dm_min_timer");
        LlvmValueHandle noMinYet = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, minTimer, LlvmApi.ConstInt(state.I64, 0, 0), "dm_no_min_yet");
        LlvmValueHandle remainingPlus1 = LlvmApi.BuildAdd(builder, remaining, LlvmApi.ConstInt(state.I64, 1, 0), "dm_remaining_plus1");
        LlvmValueHandle lower = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, remainingPlus1, minTimer, "dm_lower");
        LlvmValueHandle shouldUpdate = LlvmApi.BuildOr(builder, noMinYet, lower, "dm_should_update");
        LlvmApi.BuildCondBr(builder, shouldUpdate, layout.TimerMinBlock, layout.AdvanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.TimerMinBlock);
        LlvmApi.BuildStore(builder, remainingPlus1, layout.MinTimerSlot);
        LlvmApi.BuildBr(builder, layout.AdvanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.AdvanceBlock);
        LlvmValueHandle curAdvance = LlvmApi.BuildLoad2(builder, state.I64, layout.CurSlot, "dm_cur_advance");
        LlvmApi.BuildStore(builder, LoadMemory(state, curAdvance, TaskStructLayout.NextTask, "dm_next"), layout.CurSlot);
        LlvmApi.BuildBr(builder, layout.CheckBlock);
    }

    /// <summary>
    /// Body of <c>ashes_detached_advance_timers(ms)</c>: subtracts <paramref name="state"/>'s first
    /// argument from every detached timer-parked leaf's remaining sleep (clamped at 0), mirroring the
    /// cooperative sleep bookkeeping of the task-list scheduler.
    /// </summary>
    private static LlvmValueHandle EmitDetachedAdvanceTimersBody(LlvmCodegenState state, LlvmValueHandle elapsedMs)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle headGlobal = DetachedTasksHeadGlobal(state);

        LlvmBasicBlockHandle checkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dat_check");
        LlvmBasicBlockHandle bodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dat_body");
        LlvmBasicBlockHandle timerBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dat_timer");
        LlvmBasicBlockHandle advanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dat_advance");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dat_done");

        LlvmValueHandle curSlot = LlvmApi.BuildAlloca(builder, state.I64, "dat_cur_slot");
        LlvmApi.BuildStore(builder, LlvmApi.BuildLoad2(builder, state.I64, headGlobal, "dat_head"), curSlot);
        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkBlock);
        LlvmValueHandle cur = LlvmApi.BuildLoad2(builder, state.I64, curSlot, "dat_cur");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, cur, LlvmApi.ConstInt(state.I64, 0, 0), "dat_done_check");
        LlvmApi.BuildCondBr(builder, done, doneBlock, bodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, bodyBlock);
        LlvmValueHandle waitKind = LoadMemory(state, cur, TaskStructLayout.WaitKind, "dat_wait_kind");
        LlvmValueHandle isTimer = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, waitKind,
            LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTimer, 0), "dat_is_timer");
        LlvmApi.BuildCondBr(builder, isTimer, timerBlock, advanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, timerBlock);
        LlvmValueHandle awaited = LoadMemory(state, cur, TaskStructLayout.AwaitedTask, "dat_awaited");
        LlvmValueHandle hasAwaited = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, awaited, LlvmApi.ConstInt(state.I64, 0, 0), "dat_has_awaited");
        LlvmValueHandle sleeper = LlvmApi.BuildSelect(builder, hasAwaited, awaited, cur, "dat_sleeper");
        LlvmValueHandle remaining = LoadMemory(state, sleeper, TaskStructLayout.SleepDurationMs, "dat_remaining");
        LlvmValueHandle reduced = LlvmApi.BuildSub(builder, remaining, elapsedMs, "dat_reduced");
        LlvmValueHandle negative = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, reduced, LlvmApi.ConstInt(state.I64, 0, 0), "dat_negative");
        LlvmValueHandle clamped = LlvmApi.BuildSelect(builder, negative, LlvmApi.ConstInt(state.I64, 0, 0), reduced, "dat_clamped");
        StoreMemory(state, sleeper, TaskStructLayout.SleepDurationMs, clamped, "dat_remaining_store");
        LlvmApi.BuildBr(builder, advanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, advanceBlock);
        LlvmValueHandle curAdvance = LlvmApi.BuildLoad2(builder, state.I64, curSlot, "dat_cur_advance");
        LlvmApi.BuildStore(builder, LoadMemory(state, curAdvance, TaskStructLayout.NextTask, "dat_next"), curSlot);
        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.ConstInt(state.I64, 0, 0);
    }

    /// <summary>
    /// Body of <c>ashes_detached_register_epoll(epollFd)</c> (Linux flavors): registers every
    /// socket-parked detached task's wait handle in the given epoll set.
    /// </summary>
    private static LlvmValueHandle EmitDetachedRegisterEpollBody(LlvmCodegenState state, LlvmValueHandle epollFd)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle headGlobal = DetachedTasksHeadGlobal(state);

        LlvmBasicBlockHandle checkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dr_check");
        LlvmBasicBlockHandle bodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dr_body");
        LlvmBasicBlockHandle registerBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dr_register");
        LlvmBasicBlockHandle advanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dr_advance");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dr_done");

        LlvmValueHandle curSlot = LlvmApi.BuildAlloca(builder, state.I64, "dr_cur_slot");
        LlvmApi.BuildStore(builder, LlvmApi.BuildLoad2(builder, state.I64, headGlobal, "dr_head"), curSlot);
        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkBlock);
        LlvmValueHandle cur = LlvmApi.BuildLoad2(builder, state.I64, curSlot, "dr_cur");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, cur, LlvmApi.ConstInt(state.I64, 0, 0), "dr_done_check");
        LlvmApi.BuildCondBr(builder, done, doneBlock, bodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, bodyBlock);
        LlvmValueHandle waitKind = LoadMemory(state, cur, TaskStructLayout.WaitKind, "dr_wait_kind");
        LlvmValueHandle isRead = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, waitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitSocketRead, 0), "dr_is_read");
        LlvmValueHandle isTlsRead = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, waitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTlsWantRead, 0), "dr_is_tls_read");
        LlvmValueHandle isWrite = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, waitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitSocketWrite, 0), "dr_is_write");
        LlvmValueHandle isTlsWrite = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, waitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTlsWantWrite, 0), "dr_is_tls_write");
        LlvmValueHandle readish = LlvmApi.BuildOr(builder, isRead, isTlsRead, "dr_readish");
        LlvmValueHandle writeish = LlvmApi.BuildOr(builder, isWrite, isTlsWrite, "dr_writeish");
        LlvmValueHandle should = LlvmApi.BuildOr(builder, readish, writeish, "dr_should");
        LlvmApi.BuildCondBr(builder, should, registerBlock, advanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, registerBlock);
        LlvmValueHandle handle = LoadMemory(state, cur, TaskStructLayout.WaitHandle, "dr_handle");
        LlvmValueHandle eventMask = LlvmApi.BuildSelect(builder, readish,
            LlvmApi.ConstInt(state.I64, 0x001, 0), LlvmApi.ConstInt(state.I64, 0x004, 0), "dr_event_mask");
        // Incremental: registers only if the socket is new or its mask changed (per-fd mask table).
        _ = EmitNetworkingRuntimeCall(state, "ashes_epoll_register", [epollFd, handle, eventMask], "dr_register");
        LlvmApi.BuildBr(builder, advanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, advanceBlock);
        LlvmValueHandle curAdvance = LlvmApi.BuildLoad2(builder, state.I64, curSlot, "dr_cur_advance");
        LlvmApi.BuildStore(builder, LoadMemory(state, curAdvance, TaskStructLayout.NextTask, "dr_next"), curSlot);
        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.ConstInt(state.I64, 0, 0);
    }

    /// <summary>
    /// Body of <c>ashes_detached_fill_pollfds(arrayPtr, capacity)</c> (Windows flavor): fills
    /// WSAPoll pollfd entries for every socket-parked detached task, up to capacity. Returns the
    /// number of entries written; overflow tasks simply are not polled this round (they are stepped
    /// again on the next wait).
    /// </summary>
    // Up-front slots + basic blocks for EmitDetachedFillPollFdsBody.
    private readonly record struct DetachedFillLayout(
        LlvmValueHandle CurSlot,
        LlvmValueHandle CountSlot,
        LlvmBasicBlockHandle CheckBlock,
        LlvmBasicBlockHandle BodyBlock,
        LlvmBasicBlockHandle FillBlock,
        LlvmBasicBlockHandle AdvanceBlock,
        LlvmBasicBlockHandle DoneBlock);

    private static LlvmValueHandle EmitDetachedFillPollFdsBody(LlvmCodegenState state, LlvmValueHandle arrayPtr, LlvmValueHandle capacity)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle headGlobal = DetachedTasksHeadGlobal(state);

        LlvmValueHandle curSlot = LlvmApi.BuildAlloca(builder, state.I64, "df_cur_slot");
        LlvmValueHandle countSlot = LlvmApi.BuildAlloca(builder, state.I64, "df_count_slot");
        var layout = new DetachedFillLayout(
            curSlot, countSlot,
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "df_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "df_body"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "df_fill"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "df_advance"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "df_done"));
        LlvmApi.BuildStore(builder, LlvmApi.BuildLoad2(builder, state.I64, headGlobal, "df_head"), curSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), countSlot);
        LlvmApi.BuildBr(builder, layout.CheckBlock);

        EmitDetachedFillScan(state, arrayPtr, capacity, layout);

        LlvmApi.PositionBuilderAtEnd(builder, layout.DoneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, countSlot, "df_final_count");
    }

    private static void EmitDetachedFillScan(LlvmCodegenState state, LlvmValueHandle arrayPtr, LlvmValueHandle capacity, in DetachedFillLayout layout)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, layout.CheckBlock);
        LlvmValueHandle cur = LlvmApi.BuildLoad2(builder, state.I64, layout.CurSlot, "df_cur");
        LlvmValueHandle count = LlvmApi.BuildLoad2(builder, state.I64, layout.CountSlot, "df_count");
        LlvmValueHandle listDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, cur, LlvmApi.ConstInt(state.I64, 0, 0), "df_list_done");
        LlvmValueHandle atCapacity = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, count, capacity, "df_at_capacity");
        LlvmValueHandle stop = LlvmApi.BuildOr(builder, listDone, atCapacity, "df_stop");
        LlvmApi.BuildCondBr(builder, stop, layout.DoneBlock, layout.BodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.BodyBlock);
        LlvmValueHandle waitKind = LoadMemory(state, cur, TaskStructLayout.WaitKind, "df_wait_kind");
        LlvmValueHandle isRead = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, waitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitSocketRead, 0), "df_is_read");
        LlvmValueHandle isTlsRead = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, waitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTlsWantRead, 0), "df_is_tls_read");
        LlvmValueHandle isWrite = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, waitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitSocketWrite, 0), "df_is_write");
        LlvmValueHandle isTlsWrite = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, waitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTlsWantWrite, 0), "df_is_tls_write");
        LlvmValueHandle readish = LlvmApi.BuildOr(builder, isRead, isTlsRead, "df_readish");
        LlvmValueHandle writeish = LlvmApi.BuildOr(builder, isWrite, isTlsWrite, "df_writeish");
        LlvmValueHandle should = LlvmApi.BuildOr(builder, readish, writeish, "df_should");
        LlvmApi.BuildCondBr(builder, should, layout.FillBlock, layout.AdvanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.FillBlock);
        LlvmValueHandle handle = LoadMemory(state, cur, TaskStructLayout.WaitHandle, "df_handle");
        LlvmValueHandle fillCount = LlvmApi.BuildLoad2(builder, state.I64, layout.CountSlot, "df_fill_count");
        LlvmValueHandle slotAddress = LlvmApi.BuildAdd(builder, arrayPtr,
            LlvmApi.BuildMul(builder, fillCount, LlvmApi.ConstInt(state.I64, WindowsPollFdSize, 0), "df_slot_offset"),
            "df_slot_address");
        LlvmValueHandle eventMask = EmitWindowsPollEventMask(state, readish, "df_poll_event_mask");
        EmitWindowsInitializePollFd(state, slotAddress, handle, eventMask, "df_pollfd");
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, fillCount, LlvmApi.ConstInt(state.I64, 1, 0), "df_count_next"), layout.CountSlot);
        LlvmApi.BuildBr(builder, layout.AdvanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.AdvanceBlock);
        LlvmValueHandle curAdvance = LlvmApi.BuildLoad2(builder, state.I64, layout.CurSlot, "df_cur_advance");
        LlvmApi.BuildStore(builder, LoadMemory(state, curAdvance, TaskStructLayout.NextTask, "df_next"), layout.CurSlot);
        LlvmApi.BuildBr(builder, layout.CheckBlock);
    }

    // Shared detached-task metadata + control blocks for EmitWaitForPendingLeafTask.
    private readonly record struct LeafWaitContext(
        bool Detached,
        LlvmValueHandle DetachedHead,
        LlvmValueHandle DetachedRunnable,
        LlvmValueHandle DetachedMinTimerPlus1,
        LlvmValueHandle WaitHandle,
        LlvmValueHandle ReadishWait,
        LlvmBasicBlockHandle WaitBlock,
        LlvmBasicBlockHandle TimerBlock,
        LlvmBasicBlockHandle ContinueBlock);

    private static void EmitWaitForPendingLeafTask(LlvmCodegenState state, LlvmValueHandle taskPtr, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        (bool detached, LlvmValueHandle detachedHead, LlvmValueHandle detachedRunnable, LlvmValueHandle detachedMinTimerPlus1) = EmitLeafWaitDetachedSetup(state, prefix);

        LlvmValueHandle waitKind = LoadMemory(state, taskPtr, TaskStructLayout.WaitKind, prefix + "_wait_kind");
        LlvmValueHandle waitHandle = LoadMemory(state, taskPtr, TaskStructLayout.WaitHandle, prefix + "_wait_handle");
        LlvmValueHandle isReadWait = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, waitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitSocketRead, 0), prefix + "_is_read_wait");
        LlvmValueHandle isWriteWait = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, waitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitSocketWrite, 0), prefix + "_is_write_wait");
        LlvmValueHandle isTlsReadWait = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, waitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTlsWantRead, 0), prefix + "_is_tls_read_wait");
        LlvmValueHandle isTlsWriteWait = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, waitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTlsWantWrite, 0), prefix + "_is_tls_write_wait");
        LlvmValueHandle readishWait = LlvmApi.BuildOr(builder, isReadWait, isTlsReadWait, prefix + "_readish_wait");
        LlvmValueHandle writeishWait = LlvmApi.BuildOr(builder, isWriteWait, isTlsWriteWait, prefix + "_writeish_wait");
        LlvmValueHandle shouldWait = LlvmApi.BuildOr(builder, readishWait, writeishWait, prefix + "_should_wait");
        LlvmValueHandle isTimerWait = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, waitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTimer, 0), prefix + "_is_timer_wait");

        LlvmBasicBlockHandle waitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_wait_block");
        // A lone sleeping leaf (no sibling to interleave with) simply sleeps its full remaining time,
        // then zeroes it so the next step completes it. This preserves the old blocking behavior for a
        // single task while the cooperative list scheduler handles interleaving siblings.
        LlvmBasicBlockHandle timerCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_timer_check");
        LlvmBasicBlockHandle timerBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_timer_block");
        LlvmBasicBlockHandle continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_continue");
        LlvmApi.BuildCondBr(builder, shouldWait, waitBlock, timerCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, timerCheckBlock);
        LlvmApi.BuildCondBr(builder, isTimerWait, timerBlock, continueBlock);

        var ctx = new LeafWaitContext(detached, detachedHead, detachedRunnable, detachedMinTimerPlus1, waitHandle, readishWait, waitBlock, timerBlock, continueBlock);
        EmitLeafWaitTimerPhase(state, taskPtr, ctx, prefix);
        EmitLeafWaitPhase(state, taskPtr, ctx, prefix);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
    }

    // Advance detached (spawned) tasks before blocking, and gather what the combined wait must respect:
    // a runnable detached task forbids blocking; a timer-parked one bounds it.
    private static (bool Detached, LlvmValueHandle Head, LlvmValueHandle Runnable, LlvmValueHandle MinTimerPlus1) EmitLeafWaitDetachedSetup(LlvmCodegenState state, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        bool detached = DetachedRuntimeAvailable(state);
        LlvmValueHandle detachedHead = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle detachedRunnable = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle detachedMinTimerPlus1 = LlvmApi.ConstInt(state.I64, 0, 0);
        if (detached)
        {
            detachedHead = EmitNetworkingRuntimeCall(state, "ashes_run_detached", [], prefix + "_run_detached");
            LlvmValueHandle meta = EmitNetworkingRuntimeCall(state, "ashes_detached_wait_meta", [], prefix + "_detached_meta");
            detachedRunnable = LlvmApi.BuildLShr(builder, meta, LlvmApi.ConstInt(state.I64, 32, 0), prefix + "_detached_runnable");
            detachedMinTimerPlus1 = LlvmApi.BuildAnd(builder, meta, LlvmApi.ConstInt(state.I64, 0xFFFFFFFF, 0), prefix + "_detached_min_timer");
        }

        return (detached, detachedHead, detachedRunnable, detachedMinTimerPlus1);
    }

    private static void EmitLeafWaitTimerPhase(LlvmCodegenState state, LlvmValueHandle taskPtr, in LeafWaitContext ctx, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, ctx.TimerBlock);
        LlvmValueHandle leafRemaining = LoadMemory(state, taskPtr, TaskStructLayout.SleepDurationMs, prefix + "_timer_remaining");
        if (!ctx.Detached)
        {
            EmitNanosleep(state, leafRemaining);
            StoreMemory(state, taskPtr, TaskStructLayout.SleepDurationMs, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_timer_zero");
            LlvmApi.BuildBr(builder, ctx.ContinueBlock);
            return;
        }

        // With detached tasks in flight the sleep is chunked (10 ms ticks, or 0 when a detached
        // task is runnable) so spawned work keeps advancing; the driver loops back through this
        // wait, which re-steps the detached list, until the remaining time reaches zero.
        LlvmBasicBlockHandle timerFullBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_timer_full");
        LlvmBasicBlockHandle timerChunkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_timer_chunk");
        LlvmValueHandle noDetached = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, ctx.DetachedHead,
            LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_timer_no_detached");
        LlvmApi.BuildCondBr(builder, noDetached, timerFullBlock, timerChunkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, timerFullBlock);
        EmitNanosleep(state, leafRemaining);
        StoreMemory(state, taskPtr, TaskStructLayout.SleepDurationMs, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_timer_zero");
        LlvmApi.BuildBr(builder, ctx.ContinueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, timerChunkBlock);
        LlvmValueHandle tenMs = LlvmApi.ConstInt(state.I64, 10, 0);
        LlvmValueHandle overTen = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, leafRemaining, tenMs, prefix + "_timer_over_ten");
        LlvmValueHandle chunk = LlvmApi.BuildSelect(builder, overTen, tenMs, leafRemaining, prefix + "_timer_chunk_ms");
        LlvmValueHandle runnableNow = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, ctx.DetachedRunnable,
            LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_timer_runnable");
        chunk = LlvmApi.BuildSelect(builder, runnableNow, LlvmApi.ConstInt(state.I64, 0, 0), chunk, prefix + "_timer_chunk_eff");
        EmitNanosleep(state, chunk);
        StoreMemory(state, taskPtr, TaskStructLayout.SleepDurationMs,
            LlvmApi.BuildSub(builder, leafRemaining, chunk, prefix + "_timer_new_remaining"), prefix + "_timer_chunk_store");
        // The slept chunk elapses for detached sleepers too — advance their remaining time so
        // a spawned sleep completes while the driving task sleeps.
        _ = EmitNetworkingRuntimeCall(state, "ashes_detached_advance_timers", [chunk], prefix + "_timer_chunk_advance");
        LlvmApi.BuildBr(builder, ctx.ContinueBlock);
    }

    private static void EmitLeafWaitPhase(LlvmCodegenState state, LlvmValueHandle taskPtr, in LeafWaitContext ctx, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, ctx.WaitBlock);
        LlvmValueHandle waitTimeout = EmitLeafWaitTimeout(state, ctx, prefix);
        LlvmValueHandle waitStartMs = ctx.Detached ? EmitMonotonicNowMs(state, prefix + "_wait_start") : default;

        if (IsLinuxFlavor(state.Flavor))
        {
            EmitLeafWaitLinux(state, ctx, waitTimeout, prefix);
        }
        else if (!ctx.Detached)
        {
            EmitLeafWaitWindowsSingle(state, ctx, prefix);
        }
        else
        {
            EmitLeafWaitWindowsDetached(state, ctx, waitTimeout, prefix);
        }

        if (ctx.Detached)
        {
            // Charge detached sleepers with the ACTUAL time the wait took — an early fd wake
            // (e.g. a new connection) must not consume the whole timer bound.
            LlvmValueHandle waitEndMs = EmitMonotonicNowMs(state, prefix + "_wait_end");
            LlvmValueHandle elapsed = LlvmApi.BuildSub(builder, waitEndMs, waitStartMs, prefix + "_detached_elapsed");
            _ = EmitNetworkingRuntimeCall(state, "ashes_detached_advance_timers", [elapsed], prefix + "_detached_advance");
        }

        LlvmApi.BuildBr(builder, ctx.ContinueBlock);
    }

    // Combined wait bound: a runnable detached task means poll without blocking; a timer-parked one
    // caps the block at its remaining ms; otherwise block indefinitely on the fds.
    private static LlvmValueHandle EmitLeafWaitTimeout(LlvmCodegenState state, in LeafWaitContext ctx, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle waitTimeout = LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1);
        if (ctx.Detached)
        {
            LlvmValueHandle noTimer = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, ctx.DetachedMinTimerPlus1,
                LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_no_detached_timer");
            LlvmValueHandle timerBound = LlvmApi.BuildSub(builder, ctx.DetachedMinTimerPlus1,
                LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_detached_timer_bound");
            waitTimeout = LlvmApi.BuildSelect(builder, noTimer,
                LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), timerBound, prefix + "_timeout_timer");
            LlvmValueHandle runnableNow = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, ctx.DetachedRunnable,
                LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_wait_runnable");
            waitTimeout = LlvmApi.BuildSelect(builder, runnableNow,
                LlvmApi.ConstInt(state.I64, 0, 0), waitTimeout, prefix + "_timeout_eff");
        }

        return waitTimeout;
    }

    private static void EmitLeafWaitLinux(LlvmCodegenState state, in LeafWaitContext ctx, LlvmValueHandle waitTimeout, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle epollEventType = LlvmApi.ArrayType2(state.I8, 16);
        LlvmValueHandle epollEventOutStorage = LlvmApi.BuildAlloca(builder, epollEventType, prefix + "_epoll_event_out_storage");
        LlvmValueHandle epollEventOutPtr = GetArrayElementPointer(state, epollEventType, epollEventOutStorage, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_epoll_event_out_ptr");

        // Persistent per-reactor epoll fd: create it once, then reuse across every wait so a park no
        // longer pays an epoll_create1/close pair, and registrations persist between waits.
        LlvmValueHandle epollGlobal = EpollFdGlobal(state);
        LlvmValueHandle epollFdSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_epoll_fd_slot");
        LlvmValueHandle existingFd = LlvmApi.BuildLoad2(builder, state.I64, epollGlobal, prefix + "_epoll_fd_existing");
        LlvmApi.BuildStore(builder, existingFd, epollFdSlot);
        LlvmBasicBlockHandle createEpollBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_epoll_create");
        LlvmBasicBlockHandle haveEpollBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_epoll_have");
        LlvmApi.BuildCondBr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, existingFd, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_epoll_uncreated"),
            createEpollBlock, haveEpollBlock);
        LlvmApi.PositionBuilderAtEnd(builder, createEpollBlock);
        LlvmValueHandle newFd = EmitLinuxSyscall(state, SyscallEpollCreate1, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_epoll_create1");
        LlvmApi.BuildStore(builder, newFd, epollGlobal);
        LlvmApi.BuildStore(builder, newFd, epollFdSlot);
        LlvmApi.BuildBr(builder, haveEpollBlock);
        LlvmApi.PositionBuilderAtEnd(builder, haveEpollBlock);
        LlvmValueHandle epollFd = LlvmApi.BuildLoad2(builder, state.I64, epollFdSlot, prefix + "_epoll_fd");

        LlvmValueHandle eventMask = LlvmApi.BuildSelect(builder, ctx.ReadishWait, LlvmApi.ConstInt(state.I64, 0x001, 0), LlvmApi.ConstInt(state.I64, 0x004, 0), prefix + "_event_mask");
        _ = EmitNetworkingRuntimeCall(state, "ashes_epoll_register", [epollFd, ctx.WaitHandle, eventMask], prefix + "_epoll_register");
        if (ctx.Detached)
        {
            _ = EmitNetworkingRuntimeCall(state, "ashes_detached_register_epoll", [epollFd], prefix + "_detached_register");
        }
        if (IsLinuxArm64Flavor(state.Flavor))
        {
            EmitLinuxSyscall6(state, SyscallEpollWait,
                epollFd,
                LlvmApi.BuildPtrToInt(builder, epollEventOutPtr, state.I64, prefix + "_epoll_wait_events"),
                LlvmApi.ConstInt(state.I64, 1, 0),
                waitTimeout,
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
                waitTimeout,
                prefix + "_epoll_wait");
        }
        // The persistent fd is never closed here; sockets leave the set kernel-side on close.
    }

    private static void EmitLeafWaitWindowsSingle(LlvmCodegenState state, in LeafWaitContext ctx, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle pollFdType = LlvmApi.ArrayType2(state.I8, WindowsPollFdSize);
        LlvmValueHandle pollFdStorage = LlvmApi.BuildAlloca(builder, pollFdType, prefix + "_pollfd_storage");
        LlvmValueHandle pollFdPtr = GetArrayElementPointer(state, pollFdType, pollFdStorage, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_pollfd_ptr");
        LlvmValueHandle pollFdAddress = LlvmApi.BuildPtrToInt(builder, pollFdPtr, state.I64, prefix + "_pollfd_address");
        LlvmValueHandle eventMask = EmitWindowsPollEventMask(state, ctx.ReadishWait, prefix + "_poll_event_mask");
        EmitWindowsInitializePollFd(state, pollFdAddress, ctx.WaitHandle, eventMask, prefix + "_pollfd");
        _ = EmitWindowsWsaPoll(state, pollFdPtr, LlvmApi.ConstInt(state.I64, 1, 0), LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), prefix + "_wsapoll_wait");
    }

    private static void EmitLeafWaitWindowsDetached(LlvmCodegenState state, in LeafWaitContext ctx, LlvmValueHandle waitTimeout, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // Detached-aware Windows wait: the main task's pollfd goes in slot 0 of a shared
        // scratch array, detached socket waits fill the remaining slots, and one WSAPoll
        // covers them all.
        LlvmTypeHandle pollArrayType = LlvmApi.ArrayType2(state.I8, (ulong)(WindowsPollFdSize * DetachedPollFdCapacity));
        LlvmValueHandle pollArrayGlobal = ReadLineScratchGlobal(state, "__ashes_detached_pollfds", pollArrayType);
        LlvmValueHandle pollArrayPtr = GetArrayElementPointer(state, pollArrayType, pollArrayGlobal, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_poll_array_ptr");
        LlvmValueHandle pollArrayAddress = LlvmApi.BuildPtrToInt(builder, pollArrayPtr, state.I64, prefix + "_poll_array_address");
        LlvmValueHandle eventMask = EmitWindowsPollEventMask(state, ctx.ReadishWait, prefix + "_poll_event_mask");
        EmitWindowsInitializePollFd(state, pollArrayAddress, ctx.WaitHandle, eventMask, prefix + "_pollfd");
        LlvmValueHandle detachedCount = EmitNetworkingRuntimeCall(state, "ashes_detached_fill_pollfds",
            [
                LlvmApi.BuildAdd(builder, pollArrayAddress, LlvmApi.ConstInt(state.I64, WindowsPollFdSize, 0), prefix + "_poll_array_rest"),
                LlvmApi.ConstInt(state.I64, DetachedPollFdCapacity - 1, 0),
            ],
            prefix + "_detached_fill");
        LlvmValueHandle totalCount = LlvmApi.BuildAdd(builder, detachedCount, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_poll_total");
        _ = EmitWindowsWsaPoll(state, pollArrayPtr, totalCount, waitTimeout, prefix + "_wsapoll_wait");
    }

    // Async All

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
    // Allocates a run-queue composite task (StateAllComposite / StateRaceComposite) holding the child
    // task list in IoArg0; the scheduler drives it (enqueues children, collects on completion). The
    // ArenaOwner is left 0 here and set when the awaiting coroutine suspends on it.
    private static LlvmValueHandle EmitCreateCompositeTask(LlvmCodegenState state, LlvmValueHandle taskListPtr, long compositeState)
    {
        LlvmValueHandle taskPtr = EmitAlloc(state, TaskStructLayout.HeaderSize);
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        StoreMemory(state, taskPtr, TaskStructLayout.StateIndex, LlvmApi.ConstInt(state.I64, unchecked((ulong)compositeState), 1), "comp_state");
        StoreMemory(state, taskPtr, TaskStructLayout.CoroutineFn, zero, "comp_fn");
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot, zero, "comp_result");
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, zero, "comp_awaited");
        StoreMemory(state, taskPtr, TaskStructLayout.NextTask, zero, "comp_next");
        StoreMemory(state, taskPtr, TaskStructLayout.IoArg0, taskListPtr, "comp_list");
        StoreMemory(state, taskPtr, TaskStructLayout.IoArg1, zero, "comp_phase");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, zero, "comp_counter");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitKind, zero, "comp_wait_kind");
        StoreMemory(state, taskPtr, TaskStructLayout.FrameSizeBytes, LlvmApi.ConstInt(state.I64, TaskStructLayout.HeaderSize, 0), "comp_frame");
        StoreMemory(state, taskPtr, TaskStructLayout.ReadyNext, zero, "comp_ready_next");
        StoreMemory(state, taskPtr, TaskStructLayout.Waiter, zero, "comp_waiter");
        StoreMemory(state, taskPtr, TaskStructLayout.ArenaOwner, zero, "comp_owner");
        return taskPtr;
    }

    private static LlvmValueHandle EmitAsyncAll(LlvmCodegenState state, LlvmValueHandle taskListPtr)
    {
        // Run-queue mode: a parking composite so a spawned handler's Async.all does not block peers.
        if (state.UseRunQueueScheduler)
        {
            return EmitCreateCompositeTask(state, taskListPtr, TaskStructLayout.StateAllComposite);
        }

        return EmitAsyncAllInline(state, taskListPtr);
    }

    // Inline, blocking Async.all: drives all children to completion (ashes_wait_pending_task_list) and
    // returns a completed task holding Ok(list) or the first failure. Used off the run-queue scheduler
    // (legacy driver) and by the all-composite step to collect once every child has completed.
    // Up-front slots + basic blocks for EmitAsyncAllInline.
    private readonly record struct AsyncAllLayout(
        LlvmValueHandle ListSlot,
        LlvmValueHandle PendingCountSlot,
        LlvmValueHandle FailureSlot,
        LlvmValueHandle RevSrcSlot,
        LlvmValueHandle RevDstSlot,
        LlvmBasicBlockHandle ScanCheckBlock,
        LlvmBasicBlockHandle ScanBodyBlock,
        LlvmBasicBlockHandle PendingIncrementBlock,
        LlvmBasicBlockHandle InspectResultBlock,
        LlvmBasicBlockHandle FailureBlock,
        LlvmBasicBlockHandle AfterScanBlock,
        LlvmBasicBlockHandle WaitBlock,
        LlvmBasicBlockHandle BuildInitBlock,
        LlvmBasicBlockHandle BuildCheckBlock,
        LlvmBasicBlockHandle BuildBodyBlock,
        LlvmBasicBlockHandle ReverseInitBlock,
        LlvmBasicBlockHandle ReverseCheckBlock,
        LlvmBasicBlockHandle ReverseBodyBlock,
        LlvmBasicBlockHandle DoneBlock);

    private static AsyncAllLayout EmitAsyncAllPrologue(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        return new AsyncAllLayout(
            LlvmApi.BuildAlloca(builder, state.I64, "all_list"),
            LlvmApi.BuildAlloca(builder, state.I64, "all_pending_count"),
            LlvmApi.BuildAlloca(builder, state.I64, "all_failure"),
            LlvmApi.BuildAlloca(builder, state.I64, "all_rev_src"),
            LlvmApi.BuildAlloca(builder, state.I64, "all_rev_dst"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_scan_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_scan_body"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_pending_increment"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_inspect_result"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_failure_block"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_after_scan"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_wait"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_build_init"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_build_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_build_body"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_reverse_init"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_reverse_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_reverse_body"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "all_done"));
    }

    private static LlvmValueHandle EmitAsyncAllInline(LlvmCodegenState state, LlvmValueHandle taskListPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        AsyncAllLayout layout = EmitAsyncAllPrologue(state);
        LlvmApi.BuildStore(builder, taskListPtr, layout.ListSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), layout.PendingCountSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), layout.FailureSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), layout.RevDstSlot);
        LlvmApi.BuildBr(builder, layout.ScanCheckBlock);

        EmitAsyncAllScan(state, taskListPtr, layout);
        EmitAsyncAllBuildList(state, taskListPtr, layout);
        EmitAsyncAllReverse(state, layout);

        LlvmApi.PositionBuilderAtEnd(builder, layout.DoneBlock);
        LlvmValueHandle failureResult = LlvmApi.BuildLoad2(builder, state.I64, layout.FailureSlot, "all_failure_result");
        LlvmValueHandle hasFailure = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, failureResult, LlvmApi.ConstInt(state.I64, 0, 0), "all_has_failure");
        LlvmValueHandle finalList = LlvmApi.BuildLoad2(builder, state.I64, layout.RevDstSlot, "all_final_list");
        LlvmValueHandle successResult = EmitResultOk(state, finalList);
        LlvmValueHandle finalResult = LlvmApi.BuildSelect(builder, hasFailure, failureResult, successResult, "all_final_result");
        return EmitCreateCompletedTask(state, finalResult);
    }

    private static void EmitAsyncAllScan(LlvmCodegenState state, LlvmValueHandle taskListPtr, in AsyncAllLayout layout)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, layout.ScanCheckBlock);
        LlvmValueHandle scanCursor = LlvmApi.BuildLoad2(builder, state.I64, layout.ListSlot, "all_scan_cursor");
        LlvmValueHandle scanDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, scanCursor, LlvmApi.ConstInt(state.I64, 0, 0), "all_scan_done");
        LlvmApi.BuildCondBr(builder, scanDone, layout.AfterScanBlock, layout.ScanBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.ScanBodyBlock);
        LlvmValueHandle scanNode = LlvmApi.BuildLoad2(builder, state.I64, layout.ListSlot, "all_scan_node");
        LlvmValueHandle headTask = LoadListHead(state, scanNode, "all_head_task");
        LlvmValueHandle tailList = LoadListTail(state, scanNode, "all_tail_list");
        LlvmApi.BuildStore(builder, tailList, layout.ListSlot);
        EmitNetworkingRuntimeCall(state, "ashes_step_task_until_wait_or_done", [headTask], "all_step_task");
        LlvmValueHandle headState = LoadMemory(state, headTask, TaskStructLayout.StateIndex, "all_head_state");
        LlvmValueHandle isDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, headState, LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateCompleted), 1), "all_head_done");
        LlvmApi.BuildCondBr(builder, isDone, layout.InspectResultBlock, layout.PendingIncrementBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.PendingIncrementBlock);
        LlvmValueHandle pendingCount = LlvmApi.BuildLoad2(builder, state.I64, layout.PendingCountSlot, "all_pending_count_value");
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, pendingCount, LlvmApi.ConstInt(state.I64, 1, 0), "all_pending_count_next"), layout.PendingCountSlot);
        LlvmApi.BuildBr(builder, layout.ScanCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.InspectResultBlock);
        LlvmValueHandle taskResult = LoadMemory(state, headTask, TaskStructLayout.ResultSlot, "all_task_result");
        LlvmValueHandle taskTag = LoadAdtTag(state, taskResult, "all_task_tag");
        LlvmValueHandle isError = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, taskTag, LlvmApi.ConstInt(state.I64, 0, 0), "all_task_is_error");
        LlvmApi.BuildCondBr(builder, isError, layout.FailureBlock, layout.ScanCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.FailureBlock);
        LlvmApi.BuildStore(builder, taskResult, layout.FailureSlot);
        LlvmApi.BuildBr(builder, layout.DoneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.AfterScanBlock);
        LlvmValueHandle pendingAfterScan = LlvmApi.BuildLoad2(builder, state.I64, layout.PendingCountSlot, "all_pending_after_scan");
        LlvmValueHandle hasPending = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, pendingAfterScan, LlvmApi.ConstInt(state.I64, 0, 0), "all_has_pending");
        LlvmApi.BuildCondBr(builder, hasPending, layout.WaitBlock, layout.BuildInitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.WaitBlock);
        EmitNetworkingRuntimeCall(state, "ashes_wait_pending_task_list", [taskListPtr], "all_wait_pending");
        LlvmApi.BuildStore(builder, taskListPtr, layout.ListSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), layout.PendingCountSlot);
        LlvmApi.BuildBr(builder, layout.ScanCheckBlock);
    }

    // Builds the result list in reverse order from the completed tasks' Ok values.
    private static void EmitAsyncAllBuildList(LlvmCodegenState state, LlvmValueHandle taskListPtr, in AsyncAllLayout layout)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, layout.BuildInitBlock);
        LlvmApi.BuildStore(builder, taskListPtr, layout.RevSrcSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), layout.RevDstSlot);
        LlvmApi.BuildBr(builder, layout.BuildCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.BuildCheckBlock);
        LlvmValueHandle buildCursor = LlvmApi.BuildLoad2(builder, state.I64, layout.RevSrcSlot, "all_build_cursor");
        LlvmValueHandle buildDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, buildCursor, LlvmApi.ConstInt(state.I64, 0, 0), "all_build_done");
        LlvmApi.BuildCondBr(builder, buildDone, layout.ReverseInitBlock, layout.BuildBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.BuildBodyBlock);
        LlvmValueHandle buildNode = LlvmApi.BuildLoad2(builder, state.I64, layout.RevSrcSlot, "all_build_node");
        LlvmValueHandle buildTask = LoadListHead(state, buildNode, "all_build_task");
        LlvmValueHandle buildTail = LoadListTail(state, buildNode, "all_build_tail");
        LlvmValueHandle buildResult = LoadMemory(state, buildTask, TaskStructLayout.ResultSlot, "all_build_result");
        LlvmValueHandle buildValue = LoadAdtField(state, buildResult, 0, "all_build_value");
        LlvmValueHandle buildAcc = LlvmApi.BuildLoad2(builder, state.I64, layout.RevDstSlot, "all_build_acc");
        LlvmValueHandle buildCons = EmitAlloc(state, HeapLayouts.List.FixedAllocationSizeBytes);
        StoreListHead(state, buildCons, buildValue, "all_build_cons_head");
        StoreListTail(state, buildCons, buildAcc, "all_build_cons_tail");
        LlvmApi.BuildStore(builder, buildTail, layout.RevSrcSlot);
        LlvmApi.BuildStore(builder, buildCons, layout.RevDstSlot);
        LlvmApi.BuildBr(builder, layout.BuildCheckBlock);
    }

    // Reverses the accumulated list to restore original order.
    private static void EmitAsyncAllReverse(LlvmCodegenState state, in AsyncAllLayout layout)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, layout.ReverseInitBlock);
        LlvmValueHandle reverseSource = LlvmApi.BuildLoad2(builder, state.I64, layout.RevDstSlot, "all_reverse_source");
        LlvmApi.BuildStore(builder, reverseSource, layout.RevSrcSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), layout.RevDstSlot);
        LlvmApi.BuildBr(builder, layout.ReverseCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.ReverseCheckBlock);
        LlvmValueHandle reverseCursor = LlvmApi.BuildLoad2(builder, state.I64, layout.RevSrcSlot, "all_reverse_cursor");
        LlvmValueHandle reverseDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, reverseCursor, LlvmApi.ConstInt(state.I64, 0, 0), "all_reverse_done");
        LlvmApi.BuildCondBr(builder, reverseDone, layout.DoneBlock, layout.ReverseBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.ReverseBodyBlock);
        LlvmValueHandle reverseNode = LlvmApi.BuildLoad2(builder, state.I64, layout.RevSrcSlot, "all_reverse_node");
        LlvmValueHandle reverseHead = LoadListHead(state, reverseNode, "all_reverse_head");
        LlvmValueHandle reverseTail = LoadListTail(state, reverseNode, "all_reverse_tail");
        LlvmValueHandle reverseAcc = LlvmApi.BuildLoad2(builder, state.I64, layout.RevDstSlot, "all_reverse_acc");
        LlvmValueHandle reverseCons = EmitAlloc(state, HeapLayouts.List.FixedAllocationSizeBytes);
        StoreListHead(state, reverseCons, reverseHead, "all_reverse_cons_head");
        StoreListTail(state, reverseCons, reverseAcc, "all_reverse_cons_tail");
        LlvmApi.BuildStore(builder, reverseTail, layout.RevSrcSlot);
        LlvmApi.BuildStore(builder, reverseCons, layout.RevDstSlot);
        LlvmApi.BuildBr(builder, layout.ReverseCheckBlock);
    }

    // Async Race

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
        // Run-queue mode: a parking composite so a spawned handler's Async.race does not block peers.
        if (state.UseRunQueueScheduler)
        {
            return EmitCreateCompositeTask(state, taskListPtr, TaskStructLayout.StateRaceComposite);
        }

        return EmitAsyncRaceInline(state, taskListPtr);
    }

    // Up-front slots + basic blocks for EmitAsyncRaceInline.
    private readonly record struct AsyncRaceLayout(
        LlvmValueHandle ResultSlot,
        LlvmValueHandle ResultTaskSlot,
        LlvmValueHandle ListSlot,
        LlvmValueHandle PendingCountSlot,
        LlvmValueHandle PreferredWaitHandleSlot,
        LlvmValueHandle PreferredCursorSlot,
        LlvmValueHandle CancelCursorSlot,
        LlvmBasicBlockHandle PreferredCheckBlock,
        LlvmBasicBlockHandle PreferredSearchBlock,
        LlvmBasicBlockHandle PreferredStepBlock,
        LlvmBasicBlockHandle PreferredNotFoundBlock,
        LlvmBasicBlockHandle ScanCheckBlock,
        LlvmBasicBlockHandle ScanBodyBlock,
        LlvmBasicBlockHandle PendingIncrementBlock,
        LlvmBasicBlockHandle ResultBlock,
        LlvmBasicBlockHandle AfterScanBlock,
        LlvmBasicBlockHandle WaitBlock,
        LlvmBasicBlockHandle CancelInitBlock,
        LlvmBasicBlockHandle CancelCheckBlock,
        LlvmBasicBlockHandle CancelBodyBlock,
        LlvmBasicBlockHandle CancelOneBlock,
        LlvmBasicBlockHandle DoneBlock);

    private static AsyncRaceLayout EmitAsyncRacePrologue(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        return new AsyncRaceLayout(
            LlvmApi.BuildAlloca(builder, state.I64, "race_res"),
            LlvmApi.BuildAlloca(builder, state.I64, "race_result_task"),
            LlvmApi.BuildAlloca(builder, state.I64, "race_list"),
            LlvmApi.BuildAlloca(builder, state.I64, "race_pending_count"),
            LlvmApi.BuildAlloca(builder, state.I64, "race_preferred_wait_handle"),
            LlvmApi.BuildAlloca(builder, state.I64, "race_preferred_cursor"),
            LlvmApi.BuildAlloca(builder, state.I64, "race_cancel_cursor"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_preferred_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_preferred_search"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_preferred_step"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_preferred_not_found"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_scan_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_scan_body"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_pending_increment"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_result"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_after_scan"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_wait"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_cancel_init"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_cancel_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_cancel_body"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_cancel_one"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_done"));
    }

    private static LlvmValueHandle EmitAsyncRaceInline(LlvmCodegenState state, LlvmValueHandle taskListPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        AsyncRaceLayout layout = EmitAsyncRacePrologue(state);

        LlvmApi.BuildStore(builder, EmitResultOk(state, LlvmApi.ConstInt(state.I64, 0, 0)), layout.ResultSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), layout.ResultTaskSlot);
        LlvmApi.BuildStore(builder, taskListPtr, layout.ListSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), layout.PendingCountSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), layout.PreferredWaitHandleSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), layout.PreferredCursorSlot);
        LlvmApi.BuildBr(builder, layout.PreferredCheckBlock);

        EmitAsyncRacePreferred(state, layout);
        EmitAsyncRaceScan(state, layout);
        EmitAsyncRaceResultCancel(state, taskListPtr, layout);
        EmitAsyncRaceAfterWait(state, taskListPtr, layout);

        LlvmApi.PositionBuilderAtEnd(builder, layout.DoneBlock);
        LlvmValueHandle finalResult = LlvmApi.BuildLoad2(builder, state.I64, layout.ResultSlot, "race_final");
        return EmitCreateCompletedTask(state, finalResult);
    }

    // Fast path: re-step the task whose fd just woke (the wait returns its handle), before rescanning.
    private static void EmitAsyncRacePreferred(LlvmCodegenState state, in AsyncRaceLayout layout)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, layout.PreferredCheckBlock);
        LlvmValueHandle preferredWaitHandle = LlvmApi.BuildLoad2(builder, state.I64, layout.PreferredWaitHandleSlot, "race_preferred_wait_handle_value");
        LlvmValueHandle hasPreferredWaitHandle = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, preferredWaitHandle, LlvmApi.ConstInt(state.I64, 0, 0), "race_has_preferred_wait_handle");
        LlvmApi.BuildCondBr(builder, hasPreferredWaitHandle, layout.PreferredSearchBlock, layout.ScanCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.PreferredSearchBlock);
        LlvmValueHandle preferredCursor = LlvmApi.BuildLoad2(builder, state.I64, layout.PreferredCursorSlot, "race_preferred_cursor_value");
        LlvmValueHandle preferredSearchDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, preferredCursor, LlvmApi.ConstInt(state.I64, 0, 0), "race_preferred_search_done");
        LlvmBasicBlockHandle preferredBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_preferred_body");
        LlvmApi.BuildCondBr(builder, preferredSearchDone, layout.PreferredNotFoundBlock, preferredBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, preferredBodyBlock);
        LlvmValueHandle preferredNode = LlvmApi.BuildLoad2(builder, state.I64, layout.PreferredCursorSlot, "race_preferred_node");
        LlvmValueHandle preferredTask = LoadMemory(state, preferredNode, 0, "race_preferred_task");
        LlvmValueHandle preferredTail = LoadMemory(state, preferredNode, 8, "race_preferred_tail");
        LlvmValueHandle taskWaitHandle = LoadMemory(state, preferredTask, TaskStructLayout.WaitHandle, "race_preferred_task_wait_handle");
        LlvmValueHandle matchesPreferred = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, taskWaitHandle, preferredWaitHandle, "race_preferred_matches");
        LlvmBasicBlockHandle preferredAdvanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_preferred_advance");
        LlvmApi.BuildCondBr(builder, matchesPreferred, layout.PreferredStepBlock, preferredAdvanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, preferredAdvanceBlock);
        LlvmApi.BuildStore(builder, preferredTail, layout.PreferredCursorSlot);
        LlvmApi.BuildBr(builder, layout.PreferredSearchBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.PreferredStepBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), layout.PreferredWaitHandleSlot);
        LlvmValueHandle preferredStatus = EmitNetworkingRuntimeCall(state, "ashes_step_task_until_wait_or_done", [preferredTask], "race_preferred_step_task");
        LlvmValueHandle preferredDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, preferredStatus, LlvmApi.ConstInt(state.I64, 0, 0), "race_preferred_done");
        LlvmBasicBlockHandle preferredPendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_preferred_pending");
        LlvmApi.BuildStore(builder, preferredTask, layout.ResultTaskSlot);
        LlvmApi.BuildCondBr(builder, preferredDone, layout.ResultBlock, preferredPendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, preferredPendingBlock);
        LlvmApi.BuildStore(builder, preferredTail, layout.ListSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), layout.PendingCountSlot);
        LlvmApi.BuildBr(builder, layout.ScanCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.PreferredNotFoundBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), layout.PreferredWaitHandleSlot);
        LlvmApi.BuildBr(builder, layout.ScanCheckBlock);
    }

    private static void EmitAsyncRaceScan(LlvmCodegenState state, in AsyncRaceLayout layout)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, layout.ScanCheckBlock);
        LlvmValueHandle scanCursor = LlvmApi.BuildLoad2(builder, state.I64, layout.ListSlot, "race_scan_cursor");
        LlvmValueHandle scanDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, scanCursor, LlvmApi.ConstInt(state.I64, 0, 0), "race_scan_done");
        LlvmApi.BuildCondBr(builder, scanDone, layout.AfterScanBlock, layout.ScanBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.ScanBodyBlock);
        LlvmValueHandle scanNode = LlvmApi.BuildLoad2(builder, state.I64, layout.ListSlot, "race_scan_node");
        LlvmValueHandle raceTask = LoadMemory(state, scanNode, 0, "race_task");
        LlvmValueHandle raceTail = LoadMemory(state, scanNode, 8, "race_tail");
        LlvmApi.BuildStore(builder, raceTail, layout.ListSlot);
        EmitNetworkingRuntimeCall(state, "ashes_step_task_until_wait_or_done", [raceTask], "race_step_task");
        LlvmValueHandle raceState = LoadMemory(state, raceTask, TaskStructLayout.StateIndex, "race_state");
        LlvmValueHandle raceDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, raceState, LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateCompleted), 1), "race_task_done");
        LlvmApi.BuildStore(builder, raceTask, layout.ResultTaskSlot);
        LlvmApi.BuildCondBr(builder, raceDone, layout.ResultBlock, layout.PendingIncrementBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.PendingIncrementBlock);
        LlvmValueHandle pendingCount = LlvmApi.BuildLoad2(builder, state.I64, layout.PendingCountSlot, "race_pending_count_value");
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, pendingCount, LlvmApi.ConstInt(state.I64, 1, 0), "race_pending_count_next"), layout.PendingCountSlot);
        LlvmApi.BuildBr(builder, layout.ScanCheckBlock);
    }

    private static void EmitAsyncRaceResultCancel(LlvmCodegenState state, LlvmValueHandle taskListPtr, in AsyncRaceLayout layout)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, layout.ResultBlock);
        LlvmValueHandle resultTask = LlvmApi.BuildLoad2(builder, state.I64, layout.ResultTaskSlot, "race_result_task_value");
        LlvmValueHandle raceResult = LoadMemory(state, resultTask, TaskStructLayout.ResultSlot, "race_task_result");
        LlvmApi.BuildStore(builder, raceResult, layout.ResultSlot);
        LlvmApi.BuildBr(builder, layout.CancelInitBlock);

        // Walk the original input task list and cancel every entry that is
        // not the winning task by calling ashes_cancel_task (which internally
        // closes any OS socket the loser is parked on via EmitTcpClose and
        // recursively cancels awaited sub-tasks), so race releases resources
        // promptly per LANGUAGE_SPEC §19.7.3.
        LlvmApi.PositionBuilderAtEnd(builder, layout.CancelInitBlock);
        LlvmApi.BuildStore(builder, taskListPtr, layout.CancelCursorSlot);
        LlvmApi.BuildBr(builder, layout.CancelCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.CancelCheckBlock);
        LlvmValueHandle cancelCursor = LlvmApi.BuildLoad2(builder, state.I64, layout.CancelCursorSlot, "race_cancel_cursor_value");
        LlvmValueHandle cancelDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, cancelCursor, LlvmApi.ConstInt(state.I64, 0, 0), "race_cancel_done");
        LlvmApi.BuildCondBr(builder, cancelDone, layout.DoneBlock, layout.CancelBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.CancelBodyBlock);
        LlvmValueHandle cancelNode = LlvmApi.BuildLoad2(builder, state.I64, layout.CancelCursorSlot, "race_cancel_node");
        LlvmValueHandle cancelCandidate = LoadMemory(state, cancelNode, 0, "race_cancel_candidate");
        LlvmValueHandle cancelTail = LoadMemory(state, cancelNode, 8, "race_cancel_tail");
        LlvmApi.BuildStore(builder, cancelTail, layout.CancelCursorSlot);
        LlvmValueHandle isWinner = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, cancelCandidate, resultTask, "race_cancel_is_winner");
        LlvmApi.BuildCondBr(builder, isWinner, layout.CancelCheckBlock, layout.CancelOneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.CancelOneBlock);
        _ = EmitNetworkingRuntimeCall(state, "ashes_cancel_task", [cancelCandidate], "race_cancel_call");
        LlvmApi.BuildBr(builder, layout.CancelCheckBlock);
    }

    private static void EmitAsyncRaceAfterWait(LlvmCodegenState state, LlvmValueHandle taskListPtr, in AsyncRaceLayout layout)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, layout.AfterScanBlock);
        LlvmValueHandle pendingAfterScan = LlvmApi.BuildLoad2(builder, state.I64, layout.PendingCountSlot, "race_pending_after_scan");
        LlvmValueHandle hasPending = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, pendingAfterScan, LlvmApi.ConstInt(state.I64, 0, 0), "race_has_pending");
        LlvmApi.BuildCondBr(builder, hasPending, layout.WaitBlock, layout.DoneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, layout.WaitBlock);
        LlvmValueHandle preferredWaitHandleAfterWait = EmitNetworkingRuntimeCall(state, "ashes_wait_pending_task_list", [taskListPtr], "race_wait_pending");
        LlvmApi.BuildStore(builder, preferredWaitHandleAfterWait, layout.PreferredWaitHandleSlot);
        LlvmApi.BuildStore(builder, taskListPtr, layout.PreferredCursorSlot);
        LlvmApi.BuildStore(builder, taskListPtr, layout.ListSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), layout.PendingCountSlot);
        LlvmApi.BuildBr(builder, layout.PreferredCheckBlock);
    }
}
