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
    // dropped (EmitDrop "Function").
    private const int ClosureSizeBytes = 32;

    private static LlvmValueHandle EmitMakeClosure(LlvmCodegenState state, string funcLabel, LlvmValueHandle envPtr, int envSizeBytes)
    {
        LlvmValueHandle closurePtr = EmitAlloc(state, ClosureSizeBytes);
        LlvmValueHandle codePtr = LlvmApi.BuildPtrToInt(state.Target.Builder, state.LiftedFunctions[funcLabel], state.I64, $"closure_code_{funcLabel}");
        StoreMemory(state, closurePtr, 0, codePtr, $"closure_code_store_{funcLabel}");
        StoreMemory(state, closurePtr, 8, envPtr, $"closure_env_store_{funcLabel}");
        StoreMemory(state, closurePtr, 16, LlvmApi.ConstInt(state.I64, (ulong)envSizeBytes, 0), $"closure_env_size_store_{funcLabel}");
        StoreMemory(state, closurePtr, 24, LlvmApi.ConstInt(state.I64, 0, 0), $"closure_dropper_store_{funcLabel}");
        return closurePtr;
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
        if (state.Flavor == LlvmCodegenFlavor.WindowsX64)
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

    // ── Async / Task support ──────────────────────────────────────────

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

    private static LlvmValueHandle EmitStepLeafTask(LlvmCodegenState state, LlvmValueHandle taskPtr, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle stateIdx = LoadMemory(state, taskPtr, TaskStructLayout.StateIndex, prefix + "_state_idx");
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_status_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), statusSlot);

        LlvmBasicBlockHandle sleepBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_sleep");
        LlvmBasicBlockHandle sleepElapsedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_sleep_elapsed");
        LlvmBasicBlockHandle sleepPendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_sleep_pending");
        LlvmBasicBlockHandle checkTcpConnectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tcp_connect");
        LlvmBasicBlockHandle tcpConnectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tcp_connect");
        LlvmBasicBlockHandle checkTcpSendBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tcp_send");
        LlvmBasicBlockHandle tcpSendBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tcp_send");
        LlvmBasicBlockHandle checkTcpReceiveBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tcp_receive");
        LlvmBasicBlockHandle tcpReceiveBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tcp_receive");
        LlvmBasicBlockHandle checkTcpCloseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tcp_close");
        LlvmBasicBlockHandle tcpCloseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tcp_close");
        LlvmBasicBlockHandle checkTcpListenBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tcp_listen");
        LlvmBasicBlockHandle tcpListenBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tcp_listen");
        LlvmBasicBlockHandle checkTcpAcceptBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tcp_accept");
        LlvmBasicBlockHandle checkForkWorkersBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_fork_workers");
        LlvmBasicBlockHandle forkWorkersBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_fork_workers");
        LlvmBasicBlockHandle tcpAcceptBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tcp_accept");
        LlvmBasicBlockHandle checkTlsConnectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tls_connect");
        LlvmBasicBlockHandle tlsConnectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tls_connect");
        LlvmBasicBlockHandle checkTlsHandshakeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tls_handshake");
        LlvmBasicBlockHandle tlsHandshakeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tls_handshake");
        LlvmBasicBlockHandle checkTlsServerHandshakeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_tls_server_handshake");
        LlvmBasicBlockHandle tlsServerHandshakeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_tls_server_handshake");
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

        // Cooperative sleep: instead of blocking the whole thread on nanosleep, a sleeping leaf yields.
        // SleepDurationMs holds the remaining milliseconds. While > 0 the leaf stays pending with
        // WaitKind = WaitTimer, so the scheduler advances sibling tasks and waits only until the
        // earliest deadline (decrementing SleepDurationMs there). Once the remaining time has elapsed,
        // the leaf completes with Ok(0) — matching the old blocking result.
        LlvmApi.PositionBuilderAtEnd(builder, sleepBlock);
        LlvmValueHandle sleepMs = LoadMemory(state, taskPtr, TaskStructLayout.SleepDurationMs, prefix + "_sleep_ms");
        LlvmValueHandle sleepElapsed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, sleepMs, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_sleep_elapsed_cmp");
        LlvmApi.BuildCondBr(builder, sleepElapsed, sleepElapsedBlock, sleepPendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, sleepElapsedBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot,
            EmitResultOk(state, LlvmApi.ConstInt(state.I64, 0, 0)), prefix + "_sleep_result");
        StoreMemory(state, taskPtr, TaskStructLayout.StateIndex,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateCompleted), 1), prefix + "_sleep_done");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitKind,
            LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitNone, 0), prefix + "_sleep_clear_wait");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), statusSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, sleepPendingBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.WaitKind,
            LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTimer, 0), prefix + "_sleep_mark_timer");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), statusSlot);
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
        LlvmApi.BuildCondBr(builder, isTcpClose, tcpCloseBlock, checkTcpListenBlock);

        LlvmApi.PositionBuilderAtEnd(builder, tcpCloseBlock);
        LlvmApi.BuildStore(builder,
            EmitNetworkingRuntimeCall(state, "ashes_step_tcp_close_task", [taskPtr], prefix + "_tcp_close_status"),
            statusSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkTcpListenBlock);
        LlvmValueHandle isTcpListen = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            stateIdx,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateTcpListen), 1),
            prefix + "_is_tcp_listen");
        LlvmApi.BuildCondBr(builder, isTcpListen, tcpListenBlock, checkTcpAcceptBlock);

        LlvmApi.PositionBuilderAtEnd(builder, tcpListenBlock);
        LlvmApi.BuildStore(builder,
            EmitNetworkingRuntimeCall(state, "ashes_step_tcp_listen_task", [taskPtr], prefix + "_tcp_listen_status"),
            statusSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkTcpAcceptBlock);
        LlvmValueHandle isTcpAccept = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            stateIdx,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateTcpAccept), 1),
            prefix + "_is_tcp_accept");
        LlvmApi.BuildCondBr(builder, isTcpAccept, tcpAcceptBlock, checkForkWorkersBlock);

        LlvmApi.PositionBuilderAtEnd(builder, tcpAcceptBlock);
        LlvmApi.BuildStore(builder,
            EmitNetworkingRuntimeCall(state, "ashes_step_tcp_accept_task", [taskPtr], prefix + "_tcp_accept_status"),
            statusSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkForkWorkersBlock);
        LlvmValueHandle isForkWorkers = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            stateIdx,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateForkWorkers), 1),
            prefix + "_is_fork_workers");
        LlvmApi.BuildCondBr(builder, isForkWorkers, forkWorkersBlock, checkTlsConnectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, forkWorkersBlock);
        LlvmApi.BuildStore(builder,
            EmitNetworkingRuntimeCall(state, "ashes_step_fork_workers_task", [taskPtr], prefix + "_fork_workers_status"),
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
        LlvmApi.BuildCondBr(builder, isTlsHandshake, tlsHandshakeBlock, checkTlsServerHandshakeBlock);

        LlvmApi.PositionBuilderAtEnd(builder, tlsHandshakeBlock);
        LlvmApi.BuildStore(builder,
            EmitNetworkingRuntimeCall(state, "ashes_step_tls_handshake_task", [taskPtr], prefix + "_tls_handshake_status"),
            statusSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkTlsServerHandshakeBlock);
        LlvmValueHandle isTlsServerHandshake = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            stateIdx,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateTlsServerHandshake), 1),
            prefix + "_is_tls_server_handshake");
        LlvmApi.BuildCondBr(builder, isTlsServerHandshake, tlsServerHandshakeBlock, checkTlsSendBlock);

        LlvmApi.PositionBuilderAtEnd(builder, tlsServerHandshakeBlock);
        LlvmApi.BuildStore(builder,
            EmitNetworkingRuntimeCall(state, "ashes_step_tls_server_handshake_task", [taskPtr], prefix + "_tls_server_handshake_status"),
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
        LlvmBasicBlockHandle resolveAwaitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_resolve_await");
        LlvmBasicBlockHandle stepAwaitedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_step_awaited");
        LlvmBasicBlockHandle stepBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_step");
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
        LlvmApi.BuildCondBr(builder, isLeaf, leafBlock, resolveAwaitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, leafBlock);
        LlvmValueHandle leafStatus = EmitStepLeafTask(state, taskPtr, prefix + "_leaf_step");
        LlvmValueHandle leafCompleted = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, leafStatus, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_leaf_completed");
        LlvmApi.BuildCondBr(builder, leafCompleted, doneBlock, leafPendingBlock);

        // Coroutine: if it is parked on an awaited sub-task from a previous suspend, resolve that
        // sub-task BEFORE resuming — the coroutine's resume reads the awaited result out of ResultSlot
        // blindly, so it must not run until the result is actually ready. (The single-task RunTask
        // driver enforces this by looping on the leaf; the list driver returns to the scheduler between
        // steps, so it re-checks the awaited task on every re-entry via AwaitedTask != 0.)
        LlvmApi.PositionBuilderAtEnd(builder, resolveAwaitBlock);
        LlvmValueHandle awaitedTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_awaited_task");
        LlvmValueHandle hasAwaited = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, awaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_awaited");
        LlvmApi.BuildCondBr(builder, hasAwaited, stepAwaitedBlock, stepBlock);

        LlvmApi.PositionBuilderAtEnd(builder, stepAwaitedBlock);
        LlvmValueHandle awaitedStatus = EmitNetworkingRuntimeCall(state, "ashes_step_task_until_wait_or_done", [awaitedTask], prefix + "_awaited_status");
        LlvmValueHandle awaitedCompleted = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, awaitedStatus, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_awaited_completed");
        LlvmApi.BuildCondBr(builder, awaitedCompleted, awaitedDoneBlock, awaitedPendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, awaitedDoneBlock);
        EmitClearLeafTaskWait(state, taskPtr, prefix + "_clear_wait_after_await");
        LlvmValueHandle awaitedResult = LoadMemory(state, awaitedTask, TaskStructLayout.ResultSlot, prefix + "_awaited_result");
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot, awaitedResult, prefix + "_awaited_result_store");
        // Consume the awaited task so the next resume runs the coroutine rather than re-stepping it.
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_awaited_consumed");
        LlvmApi.BuildBr(builder, stepBlock);

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
        // On suspend the coroutine has stored a fresh AwaitedTask; loop back so that awaited task is
        // resolved (and, if already complete, the coroutine immediately resumes).
        LlvmApi.BuildCondBr(builder, suspended, checkBlock, doneBlock);

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
        LlvmValueHandle priorRunnable = LlvmApi.BuildLoad2(builder, state.I64, runnableSlot, prefix + "_prior_runnable");
        LlvmApi.BuildStore(builder, LlvmApi.BuildOr(builder, priorRunnable, LlvmApi.BuildZExt(builder, countRunnable, state.I64, prefix + "_count_runnable_i64"), prefix + "_runnable_next"), runnableSlot);
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
        // Skip the blocking wait when a runnable (WaitNone, not-completed) task exists:
        // returning immediately lets the caller's scheduler re-step it without waiting
        // on unrelated sockets. Otherwise an HTTP receive that consumed a buffered chunk
        // could be starved behind a peer task whose socket never becomes ready.
        LlvmValueHandle hasRunnable = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildLoad2(builder, state.I64, runnableSlot, prefix + "_runnable_value"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_runnable");
        // shouldWait = hasPending AND NOT hasRunnable, expressed as hasPending > hasRunnable
        // over the i1 values (1 > 0 only when hasPending is set and hasRunnable is clear).
        LlvmValueHandle shouldWait = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, hasPending, hasRunnable, prefix + "_should_wait");
        LlvmBasicBlockHandle waitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_wait");
        // When no socket wait is needed, fall through to the timer path: if any task is parked on a
        // cooperative sleep (WaitKind == WaitTimer) and nothing is immediately runnable, wait once until
        // the earliest sleep deadline rather than busy-looping. Socket-pending waits take priority (the
        // epoll/poll wait already bounds its own blocking); a mixed socket+timer wait uses the socket
        // path this round and re-evaluates timers on the next scan.
        LlvmBasicBlockHandle timerCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_timer_check");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");
        LlvmApi.BuildCondBr(builder, shouldWait, waitBlock, timerCheckBlock);

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

        // --- Timer path: cooperative sleep wait ---
        LlvmApi.PositionBuilderAtEnd(builder, timerCheckBlock);
        EmitCooperativeTimerWait(state, taskListPtr, hasRunnable, prefix + "_timer", doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, waitResultSlot, prefix + "_wait_result");
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
    private static void EmitCooperativeTimerWait(
        LlvmCodegenState state,
        LlvmValueHandle taskListPtr,
        LlvmValueHandle hasRunnable,
        string prefix,
        LlvmBasicBlockHandle continuation)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        LlvmValueHandle minSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_min_slot");
        LlvmValueHandle countSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_count_slot");
        LlvmValueHandle cursorSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_cursor_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)long.MaxValue), 0), minSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), countSlot);
        LlvmApi.BuildStore(builder, taskListPtr, cursorSlot);

        LlvmBasicBlockHandle scanCheck = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_scan_check");
        LlvmBasicBlockHandle scanBody = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_scan_body");
        LlvmBasicBlockHandle scanTimer = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_scan_timer");
        LlvmBasicBlockHandle afterScan = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_after_scan");
        LlvmBasicBlockHandle sleepBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_sleep");
        LlvmBasicBlockHandle decCheck = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_dec_check");
        LlvmBasicBlockHandle decBody = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_dec_body");
        LlvmBasicBlockHandle decTimer = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_dec_timer");

        LlvmValueHandle timerKind = LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTimer, 0);
        LlvmValueHandle sleepingState = LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateSleeping), 1);
        LlvmValueHandle completedState = LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateCompleted), 1);

        // Pass 1: find the minimum remaining sleep and count the timer tasks.
        LlvmApi.BuildBr(builder, scanCheck);

        LlvmApi.PositionBuilderAtEnd(builder, scanCheck);
        LlvmValueHandle scanCursor = LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, prefix + "_scan_cursor");
        LlvmValueHandle scanDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, scanCursor, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_scan_done");
        LlvmApi.BuildCondBr(builder, scanDone, afterScan, scanBody);

        LlvmApi.PositionBuilderAtEnd(builder, scanBody);
        LlvmValueHandle scanTask = LoadMemory(state, scanCursor, 0, prefix + "_scan_task");
        LlvmValueHandle scanTail = LoadMemory(state, scanCursor, 8, prefix + "_scan_tail");
        LlvmApi.BuildStore(builder, scanTail, cursorSlot);
        LlvmValueHandle scanWaitKind = LoadMemory(state, scanTask, TaskStructLayout.WaitKind, prefix + "_scan_wait_kind");
        LlvmValueHandle scanStateIdx = LoadMemory(state, scanTask, TaskStructLayout.StateIndex, prefix + "_scan_state");
        LlvmValueHandle scanIsTimerKind = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, scanWaitKind, timerKind, prefix + "_scan_is_timer_kind");
        // A completed task may still carry a stale WaitKind; exclude it so its zero remaining does not
        // poison the earliest-deadline computation into a no-op wait.
        LlvmValueHandle scanNotDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, scanStateIdx, completedState, prefix + "_scan_not_done");
        LlvmValueHandle scanIsTimer = LlvmApi.BuildAnd(builder, scanIsTimerKind, scanNotDone, prefix + "_scan_is_timer");
        LlvmApi.BuildCondBr(builder, scanIsTimer, scanTimer, scanCheck);

        LlvmApi.PositionBuilderAtEnd(builder, scanTimer);
        LlvmValueHandle scanIsDirect = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, scanStateIdx, sleepingState, prefix + "_scan_is_direct");
        LlvmValueHandle scanAwaited = LoadMemory(state, scanTask, TaskStructLayout.AwaitedTask, prefix + "_scan_awaited");
        LlvmValueHandle scanLeaf = LlvmApi.BuildSelect(builder, scanIsDirect, scanTask, scanAwaited, prefix + "_scan_leaf");
        LlvmValueHandle scanRem = LoadMemory(state, scanLeaf, TaskStructLayout.SleepDurationMs, prefix + "_scan_rem");
        LlvmValueHandle scanCurMin = LlvmApi.BuildLoad2(builder, state.I64, minSlot, prefix + "_scan_cur_min");
        LlvmValueHandle scanIsLess = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, scanRem, scanCurMin, prefix + "_scan_is_less");
        LlvmApi.BuildStore(builder, LlvmApi.BuildSelect(builder, scanIsLess, scanRem, scanCurMin, prefix + "_scan_new_min"), minSlot);
        LlvmValueHandle scanCount = LlvmApi.BuildLoad2(builder, state.I64, countSlot, prefix + "_scan_count");
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, scanCount, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_scan_count_next"), countSlot);
        LlvmApi.BuildBr(builder, scanCheck);

        // Decide whether to sleep: only when a timer task exists and nothing is immediately runnable
        // (1 > 0 over the i1 values, exactly as the socket-wait decision above).
        LlvmApi.PositionBuilderAtEnd(builder, afterScan);
        LlvmValueHandle timerCount = LlvmApi.BuildLoad2(builder, state.I64, countSlot, prefix + "_timer_count");
        LlvmValueHandle hasTimers = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, timerCount, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_timers");
        LlvmValueHandle shouldSleep = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, hasTimers, hasRunnable, prefix + "_should_sleep");
        LlvmApi.BuildCondBr(builder, shouldSleep, sleepBlock, continuation);

        // Sleep once until the earliest deadline, then decrement every timer task's remaining by it.
        LlvmApi.PositionBuilderAtEnd(builder, sleepBlock);
        LlvmValueHandle minRemaining = LlvmApi.BuildLoad2(builder, state.I64, minSlot, prefix + "_min_remaining");
        EmitNanosleep(state, minRemaining);
        LlvmApi.BuildStore(builder, taskListPtr, cursorSlot);
        LlvmApi.BuildBr(builder, decCheck);

        LlvmApi.PositionBuilderAtEnd(builder, decCheck);
        LlvmValueHandle decCursor = LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, prefix + "_dec_cursor");
        LlvmValueHandle decDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, decCursor, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_dec_done");
        LlvmApi.BuildCondBr(builder, decDone, continuation, decBody);

        LlvmApi.PositionBuilderAtEnd(builder, decBody);
        LlvmValueHandle decTask = LoadMemory(state, decCursor, 0, prefix + "_dec_task");
        LlvmValueHandle decTail = LoadMemory(state, decCursor, 8, prefix + "_dec_tail");
        LlvmApi.BuildStore(builder, decTail, cursorSlot);
        LlvmValueHandle decWaitKind = LoadMemory(state, decTask, TaskStructLayout.WaitKind, prefix + "_dec_wait_kind");
        LlvmValueHandle decStateIdx = LoadMemory(state, decTask, TaskStructLayout.StateIndex, prefix + "_dec_state");
        LlvmValueHandle decIsTimerKind = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, decWaitKind, timerKind, prefix + "_dec_is_timer_kind");
        LlvmValueHandle decNotDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, decStateIdx, completedState, prefix + "_dec_not_done");
        LlvmValueHandle decIsTimer = LlvmApi.BuildAnd(builder, decIsTimerKind, decNotDone, prefix + "_dec_is_timer");
        LlvmApi.BuildCondBr(builder, decIsTimer, decTimer, decCheck);

        LlvmApi.PositionBuilderAtEnd(builder, decTimer);
        LlvmValueHandle decIsDirect = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, decStateIdx, sleepingState, prefix + "_dec_is_direct");
        LlvmValueHandle decAwaited = LoadMemory(state, decTask, TaskStructLayout.AwaitedTask, prefix + "_dec_awaited");
        LlvmValueHandle decLeaf = LlvmApi.BuildSelect(builder, decIsDirect, decTask, decAwaited, prefix + "_dec_leaf");
        LlvmValueHandle decRem = LoadMemory(state, decLeaf, TaskStructLayout.SleepDurationMs, prefix + "_dec_rem");
        LlvmValueHandle decNewRem = LlvmApi.BuildSub(builder, decRem, minRemaining, prefix + "_dec_new_rem");
        StoreMemory(state, decLeaf, TaskStructLayout.SleepDurationMs, decNewRem, prefix + "_dec_store");
        LlvmApi.BuildBr(builder, decCheck);
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
        LlvmBasicBlockHandle awaitedDoneBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "sub_awaited_done");
        LlvmBasicBlockHandle awaitedPendingBlock = LlvmApi.AppendBasicBlockInContext(
            state.Target.Context, state.Function, "sub_awaited_pending");

        LlvmValueHandle awaitedStatus = EmitNetworkingRuntimeCall(state, "ashes_step_task_until_wait_or_done", [awaitedTask], "sub_awaited_status");
        LlvmValueHandle awaitedCompleted = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne,
            awaitedStatus, LlvmApi.ConstInt(state.I64, 0, 0), "sub_awaited_completed");
        LlvmApi.BuildCondBr(builder, awaitedCompleted, awaitedDoneBlock, awaitedPendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, awaitedDoneBlock);
        LlvmValueHandle awaitedResult = LoadMemory(state, awaitedTask, TaskStructLayout.ResultSlot, "sub_awaited_result");
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot, awaitedResult, "sub_awaited_result_store");
        LlvmApi.BuildBr(builder, subStepBlock);

        LlvmApi.PositionBuilderAtEnd(builder, awaitedPendingBlock);
        EmitWaitForPendingLeafTask(state, awaitedTask, "sub_awaited_pending");
        LlvmApi.BuildBr(builder, subSuspendedBlock);

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

    // ── Detached tasks (Ashes.Async.spawn) ─────────────────────────
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
    private static LlvmValueHandle EmitSchedulerRunBody(LlvmCodegenState state, LlvmValueHandle mainTask)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle completedConst = LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateCompleted), 1);
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);

        LlvmValueHandle taskSlot = LlvmApi.BuildAlloca(builder, state.I64, "sched_task_slot");
        // The main task is created by EmitCreateTask, which does not initialize the run-queue header
        // fields; zero them so the root task has no stale Waiter (delivered-to on completion) or
        // ArenaOwner. Sub-tasks get these set by the scheduler when they are enqueued.
        StoreMemory(state, mainTask, TaskStructLayout.Waiter, LlvmApi.ConstInt(state.I64, 0, 0), "sched_main_no_waiter");
        StoreMemory(state, mainTask, TaskStructLayout.ArenaOwner, LlvmApi.ConstInt(state.I64, 0, 0), "sched_main_no_owner");
        _ = EmitNetworkingRuntimeCall(state, "ashes_ready_enqueue", [mainTask], "sched_seed");

        LlvmBasicBlockHandle loopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_loop");
        LlvmBasicBlockHandle emptyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_empty");
        LlvmBasicBlockHandle waitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_wait");
        LlvmBasicBlockHandle returnBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_return");
        LlvmBasicBlockHandle haveTaskBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_have_task");
        LlvmBasicBlockHandle notDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_not_done");
        LlvmBasicBlockHandle leafBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_leaf");
        LlvmBasicBlockHandle parkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_park");
        LlvmBasicBlockHandle coroBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_coro");
        LlvmBasicBlockHandle suspendBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_suspend");
        LlvmBasicBlockHandle completeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_complete");
        LlvmBasicBlockHandle deliverBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_deliver");
        LlvmBasicBlockHandle noWaiterBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_no_waiter");
        LlvmBasicBlockHandle reapBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_reap");
        LlvmBasicBlockHandle leafCoroBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_leaf_coro");
        LlvmBasicBlockHandle compositeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_composite");
        LlvmBasicBlockHandle allStepBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_all_step");
        LlvmBasicBlockHandle raceStepBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_race_step");
        LlvmBasicBlockHandle compAfterBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_comp_after");
        LlvmBasicBlockHandle allWaiterBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_all_waiter");
        LlvmBasicBlockHandle notAllWaiterBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_not_all_waiter");
        LlvmBasicBlockHandle enqueueCompositeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_enqueue_composite");
        LlvmBasicBlockHandle raceWaiterBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_race_waiter");
        LlvmBasicBlockHandle raceFirstBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_race_first");
        LlvmBasicBlockHandle normalWaiterBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "sched_normal_waiter");

        LlvmApi.BuildBr(builder, loopBlock);

        // Pop the next ready task.
        LlvmApi.PositionBuilderAtEnd(builder, loopBlock);
        LlvmValueHandle popped = EmitNetworkingRuntimeCall(state, "ashes_ready_dequeue", [], "sched_pop");
        LlvmApi.BuildStore(builder, popped, taskSlot);
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, popped, zero, "sched_queue_empty"), emptyBlock, haveTaskBlock);

        // Queue empty: finished if the main task is done, else block until a parked leaf is ready.
        LlvmApi.PositionBuilderAtEnd(builder, emptyBlock);
        LlvmValueHandle mainState = LoadMemory(state, mainTask, TaskStructLayout.StateIndex, "sched_main_state");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, mainState, completedConst, "sched_main_done"), returnBlock, waitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, waitBlock);
        EmitSchedulerAggregateWait(state);
        LlvmApi.BuildBr(builder, loopBlock);

        // Step the popped task.
        LlvmApi.PositionBuilderAtEnd(builder, haveTaskBlock);
        LlvmValueHandle task = LlvmApi.BuildLoad2(builder, state.I64, taskSlot, "sched_task");
        LlvmValueHandle stateIdx = LoadMemory(state, task, TaskStructLayout.StateIndex, "sched_state_idx");
        // An already-completed task delivers to its waiter (e.g. an immediately-ready awaited value).
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, stateIdx, completedConst, "sched_is_done"), completeBlock, notDoneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, notDoneBlock);
        LlvmValueHandle isRaceComposite = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, stateIdx, LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateRaceComposite), 1), "sched_is_race_comp");
        LlvmValueHandle isComposite = LlvmApi.BuildOr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, stateIdx, LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateAllComposite), 1), "sched_is_all_comp"),
            isRaceComposite, "sched_is_composite");
        LlvmApi.BuildCondBr(builder, isComposite, compositeBlock, leafCoroBlock);

        LlvmApi.PositionBuilderAtEnd(builder, leafCoroBlock);
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, stateIdx, completedConst, "sched_is_leaf"), leafBlock, coroBlock);

        // Composite (all/race): install its arena, step it, and complete or drop (a child completion
        // re-enqueues it) — see EmitStepComposite.
        LlvmApi.PositionBuilderAtEnd(builder, compositeBlock);
        LlvmValueHandle compStatusSlot = LlvmApi.BuildAlloca(builder, state.I64, "sched_comp_status_slot");
        (LlvmValueHandle compOwner, LlvmValueHandle compSavedCursor, LlvmValueHandle compSavedEnd) = EmitInstallTaskArena(state, task, "sched_comp");
        LlvmApi.BuildCondBr(builder, isRaceComposite, raceStepBlock, allStepBlock);
        LlvmApi.PositionBuilderAtEnd(builder, allStepBlock);
        LlvmApi.BuildStore(builder, EmitStepComposite(state, task, isRace: false, "sched_all"), compStatusSlot);
        LlvmApi.BuildBr(builder, compAfterBlock);
        LlvmApi.PositionBuilderAtEnd(builder, raceStepBlock);
        LlvmApi.BuildStore(builder, EmitStepComposite(state, task, isRace: true, "sched_race"), compStatusSlot);
        LlvmApi.BuildBr(builder, compAfterBlock);
        LlvmApi.PositionBuilderAtEnd(builder, compAfterBlock);
        EmitRestoreTaskArena(state, compOwner, compSavedCursor, compSavedEnd, "sched_comp");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildLoad2(builder, state.I64, compStatusSlot, "sched_comp_status"), zero, "sched_comp_complete"), completeBlock, loopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, leafBlock);
        (LlvmValueHandle leafOwner, LlvmValueHandle leafSavedCursor, LlvmValueHandle leafSavedEnd) = EmitInstallTaskArena(state, task, "sched_leaf");
        LlvmValueHandle leafStatus = EmitStepLeafTask(state, task, "sched_leaf_step");
        EmitRestoreTaskArena(state, leafOwner, leafSavedCursor, leafSavedEnd, "sched_leaf");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, leafStatus, zero, "sched_leaf_done"), completeBlock, parkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parkBlock);
        LlvmValueHandle parkedHeadGlobal = ParkedLeavesHeadGlobal(state);
        StoreMemory(state, task, TaskStructLayout.ReadyNext, LlvmApi.BuildLoad2(builder, state.I64, parkedHeadGlobal, "sched_parked_head"), "sched_park_link");
        LlvmApi.BuildStore(builder, task, parkedHeadGlobal);
        LlvmApi.BuildBr(builder, loopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, coroBlock);
        (LlvmValueHandle coroOwner, LlvmValueHandle coroSavedCursor, LlvmValueHandle coroSavedEnd) = EmitInstallTaskArena(state, task, "sched_coro");
        LlvmValueHandle coroutineFn = LoadMemory(state, task, TaskStructLayout.CoroutineFn, "sched_coro_fn");
        LlvmTypeHandle coroutineFnType = LlvmApi.FunctionType(state.I64, [state.I64, state.I64]);
        LlvmValueHandle typedFnPtr = LlvmApi.BuildIntToPtr(builder, coroutineFn, LlvmApi.PointerTypeInContext(state.Target.Context, 0), "sched_coro_ptr");
        LlvmValueHandle coroStatus = LlvmApi.BuildCall2(builder, coroutineFnType, typedFnPtr, [task, zero], "sched_coro_status");
        EmitRestoreTaskArena(state, coroOwner, coroSavedCursor, coroSavedEnd, "sched_coro");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, coroStatus, zero, "sched_suspended"), suspendBlock, completeBlock);

        // Suspended on a fresh AwaitedTask: schedule it, link it back to this task, and park this task.
        LlvmApi.PositionBuilderAtEnd(builder, suspendBlock);
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
        LlvmApi.BuildBr(builder, loopBlock);

        // Completed: hand the result to the waiter (if any) and re-enqueue it.
        LlvmApi.PositionBuilderAtEnd(builder, completeBlock);
        LlvmValueHandle completedTask = LlvmApi.BuildLoad2(builder, state.I64, taskSlot, "sched_completed");
        LlvmValueHandle waiter = LoadMemory(state, completedTask, TaskStructLayout.Waiter, "sched_waiter");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, waiter, zero, "sched_has_waiter"), deliverBlock, noWaiterBlock);

        // Deliver to the waiter. A normal coroutine waiter resumes with the result. A composite waiter
        // is different: an all-composite only decrements its pending counter (and is enqueued once, when
        // it reaches 0 — it reads its children directly); a race-composite takes the first child's result
        // and is enqueued once. (A spawned root task delivered here keeps its arena — no copy-out yet —
        // so an awaited spawn currently leaks; fire-and-forget spawns take the no-waiter path and reap.)
        LlvmApi.PositionBuilderAtEnd(builder, deliverBlock);
        LlvmValueHandle waiterState = LoadMemory(state, waiter, TaskStructLayout.StateIndex, "sched_waiter_state");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, waiterState, LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateAllComposite), 1), "sched_waiter_is_all"), allWaiterBlock, notAllWaiterBlock);

        LlvmApi.PositionBuilderAtEnd(builder, allWaiterBlock);
        LlvmValueHandle newCounter = LlvmApi.BuildSub(builder, LoadMemory(state, waiter, TaskStructLayout.WaitData0, "sched_all_counter"), LlvmApi.ConstInt(state.I64, 1, 0), "sched_all_counter_dec");
        StoreMemory(state, waiter, TaskStructLayout.WaitData0, newCounter, "sched_all_counter_store");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, newCounter, zero, "sched_all_ready"), enqueueCompositeBlock, loopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, notAllWaiterBlock);
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, waiterState, LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateRaceComposite), 1), "sched_waiter_is_race"), raceWaiterBlock, normalWaiterBlock);

        LlvmApi.PositionBuilderAtEnd(builder, raceWaiterBlock);
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, LoadMemory(state, waiter, TaskStructLayout.WaitData0, "sched_race_resolved"), zero, "sched_race_unresolved"), raceFirstBlock, loopBlock);
        LlvmApi.PositionBuilderAtEnd(builder, raceFirstBlock);
        StoreMemory(state, waiter, TaskStructLayout.ResultSlot, LoadMemory(state, completedTask, TaskStructLayout.ResultSlot, "sched_race_result"), "sched_race_deliver");
        StoreMemory(state, waiter, TaskStructLayout.WaitData0, LlvmApi.ConstInt(state.I64, 1, 0), "sched_race_mark_resolved");
        _ = EmitNetworkingRuntimeCall(state, "ashes_ready_enqueue", [waiter], "sched_race_enqueue");
        LlvmApi.BuildBr(builder, loopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, enqueueCompositeBlock);
        _ = EmitNetworkingRuntimeCall(state, "ashes_ready_enqueue", [waiter], "sched_all_enqueue");
        LlvmApi.BuildBr(builder, loopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, normalWaiterBlock);
        StoreMemory(state, waiter, TaskStructLayout.ResultSlot, LoadMemory(state, completedTask, TaskStructLayout.ResultSlot, "sched_completed_result"), "sched_deliver_result");
        StoreMemory(state, waiter, TaskStructLayout.AwaitedTask, zero, "sched_clear_awaited");
        _ = EmitNetworkingRuntimeCall(state, "ashes_ready_enqueue", [waiter], "sched_enqueue_waiter");
        LlvmApi.BuildBr(builder, loopBlock);

        // No waiter: a fire-and-forget spawned root task (its own ArenaOwner) is reaped; everything else
        // (the main task, or a sub-task whose owner is still live) just drops.
        LlvmApi.PositionBuilderAtEnd(builder, noWaiterBlock);
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, LoadMemory(state, completedTask, TaskStructLayout.ArenaOwner, "sched_completed_owner"), completedTask, "sched_is_root_spawn"), reapBlock, loopBlock);
        LlvmApi.PositionBuilderAtEnd(builder, reapBlock);
        EmitReapTaskArena(state, completedTask, "sched_reap");
        LlvmValueHandle liveGlobal = LiveSpawnedGlobal(state);
        LlvmApi.BuildStore(builder,
            LlvmApi.BuildSub(builder, LlvmApi.BuildLoad2(builder, state.I64, liveGlobal, "sched_reap_live"), LlvmApi.ConstInt(state.I64, 1, 0), "sched_reap_live_dec"),
            liveGlobal);
        LlvmApi.BuildBr(builder, loopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, returnBlock);
        return LoadMemory(state, mainTask, TaskStructLayout.ResultSlot, "sched_result");
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
    private static void EmitSchedulerAggregateWait(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle parkedHeadGlobal = ParkedLeavesHeadGlobal(state);
        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle maxVal = LlvmApi.ConstInt(state.I64, unchecked((ulong)long.MaxValue), 0);
        LlvmValueHandle timerKind = LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTimer, 0);
        bool linux = IsLinuxFlavor(state.Flavor);
        // Socket waits on Windows need the WSAPoll import, which exists exactly when the program
        // uses the networking runtime; without it no socket leaf can ever park, so the timer-only
        // sleep below is complete.
        bool windowsSockets = state.Flavor == LlvmCodegenFlavor.WindowsX64
            && state.WindowsWsaPollImport.Ptr != 0;

        LlvmValueHandle minSlot = LlvmApi.BuildAlloca(builder, state.I64, "saw_min_slot");
        LlvmValueHandle hasSocketSlot = LlvmApi.BuildAlloca(builder, state.I64, "saw_has_socket_slot");
        LlvmValueHandle cursorSlot = LlvmApi.BuildAlloca(builder, state.I64, "saw_cursor_slot");
        LlvmValueHandle elapsedSlot = LlvmApi.BuildAlloca(builder, state.I64, "saw_elapsed_slot");
        LlvmApi.BuildStore(builder, maxVal, minSlot);
        LlvmApi.BuildStore(builder, zero, hasSocketSlot);
        LlvmApi.BuildStore(builder, zero, elapsedSlot);

        // Windows: pollfd scratch array (module-global, same shape as the legacy detached wait's)
        // plus a fill count, rebuilt on every wait from the parked list.
        LlvmValueHandle pollCountSlot = default;
        LlvmValueHandle pollArrayPtr = default;
        LlvmValueHandle pollArrayAddress = default;
        if (windowsSockets)
        {
            LlvmTypeHandle pollArrayType = LlvmApi.ArrayType2(state.I8, (ulong)(WindowsPollFdSize * DetachedPollFdCapacity));
            LlvmValueHandle pollArrayGlobal = ReadLineScratchGlobal(state, "__ashes_sched_pollfds", pollArrayType);
            pollArrayPtr = GetArrayElementPointer(state, pollArrayType, pollArrayGlobal, zero, "saw_poll_array_ptr");
            pollArrayAddress = LlvmApi.BuildPtrToInt(builder, pollArrayPtr, state.I64, "saw_poll_array_address");
            pollCountSlot = LlvmApi.BuildAlloca(builder, state.I64, "saw_poll_count_slot");
            LlvmApi.BuildStore(builder, zero, pollCountSlot);
        }

        // Persistent per-reactor epoll fd (created once, reused).
        LlvmValueHandle epollFd = zero;
        if (linux)
        {
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
            epollFd = LlvmApi.BuildLoad2(builder, state.I64, epollFdSlot, "saw_epoll_fd");
        }

        // Pass 1: minimum timer remaining + register socket leaves in the epoll set.
        LlvmApi.BuildStore(builder, LlvmApi.BuildLoad2(builder, state.I64, parkedHeadGlobal, "saw_head0"), cursorSlot);
        LlvmBasicBlockHandle scanBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_scan");
        LlvmBasicBlockHandle scanBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_scan_body");
        LlvmBasicBlockHandle timerBranch = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_timer");
        LlvmBasicBlockHandle socketBranch = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_socket");
        LlvmBasicBlockHandle scanNextBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_scan_next");
        LlvmBasicBlockHandle afterScanBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_after_scan");
        LlvmApi.BuildBr(builder, scanBlock);
        LlvmApi.PositionBuilderAtEnd(builder, scanBlock);
        LlvmValueHandle scanCur = LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, "saw_scan_cur");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, scanCur, zero, "saw_scan_end"), afterScanBlock, scanBodyBlock);
        LlvmApi.PositionBuilderAtEnd(builder, scanBodyBlock);
        LlvmValueHandle scanKind = LoadMemory(state, scanCur, TaskStructLayout.WaitKind, "saw_scan_kind");
        LlvmApi.BuildStore(builder, LoadMemory(state, scanCur, TaskStructLayout.ReadyNext, "saw_scan_next_load"), cursorSlot);
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, scanKind, timerKind, "saw_is_timer"), timerBranch, socketBranch);
        LlvmApi.PositionBuilderAtEnd(builder, timerBranch);
        LlvmValueHandle rem = LoadMemory(state, scanCur, TaskStructLayout.SleepDurationMs, "saw_rem");
        LlvmValueHandle curMin = LlvmApi.BuildLoad2(builder, state.I64, minSlot, "saw_cur_min");
        LlvmApi.BuildStore(builder, LlvmApi.BuildSelect(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, rem, curMin, "saw_lt"), rem, curMin, "saw_min_upd"), minSlot);
        LlvmApi.BuildBr(builder, scanNextBlock);
        LlvmApi.PositionBuilderAtEnd(builder, socketBranch);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), hasSocketSlot);
        if (linux)
        {
            LlvmValueHandle readish = LlvmApi.BuildOr(builder,
                LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, scanKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitSocketRead, 0), "saw_is_read1"),
                LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, scanKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTlsWantRead, 0), "saw_is_read3"),
                "saw_readish");
            LlvmValueHandle mask = LlvmApi.BuildSelect(builder, readish, LlvmApi.ConstInt(state.I64, 0x001, 0), LlvmApi.ConstInt(state.I64, 0x004, 0), "saw_mask");
            _ = EmitNetworkingRuntimeCall(state, "ashes_epoll_register", [epollFd, LoadMemory(state, scanCur, TaskStructLayout.WaitHandle, "saw_wait_handle"), mask], "saw_register");
        }
        else if (windowsSockets)
        {
            // Fill the next pollfd slot, capped at capacity. An overflow leaf simply is not polled
            // this round — the requeue-all pass re-steps it, and it re-parks for the next wait.
            LlvmValueHandle readish = LlvmApi.BuildOr(builder,
                LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, scanKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitSocketRead, 0), "saw_is_read1"),
                LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, scanKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTlsWantRead, 0), "saw_is_read3"),
                "saw_readish");
            LlvmValueHandle fillCount = LlvmApi.BuildLoad2(builder, state.I64, pollCountSlot, "saw_poll_fill_count");
            LlvmBasicBlockHandle fillBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_poll_fill");
            LlvmBasicBlockHandle fillDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_poll_fill_done");
            LlvmApi.BuildCondBr(builder,
                LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, fillCount, LlvmApi.ConstInt(state.I64, DetachedPollFdCapacity, 0), "saw_poll_has_room"),
                fillBlock, fillDoneBlock);
            LlvmApi.PositionBuilderAtEnd(builder, fillBlock);
            LlvmValueHandle slotAddress = LlvmApi.BuildAdd(builder, pollArrayAddress,
                LlvmApi.BuildMul(builder, fillCount, LlvmApi.ConstInt(state.I64, WindowsPollFdSize, 0), "saw_poll_slot_offset"),
                "saw_poll_slot_address");
            LlvmValueHandle eventMask = EmitWindowsPollEventMask(state, readish, "saw_poll_event_mask");
            EmitWindowsInitializePollFd(state, slotAddress, LoadMemory(state, scanCur, TaskStructLayout.WaitHandle, "saw_wait_handle"), eventMask, "saw_pollfd");
            LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, fillCount, LlvmApi.ConstInt(state.I64, 1, 0), "saw_poll_count_next"), pollCountSlot);
            LlvmApi.BuildBr(builder, fillDoneBlock);
            LlvmApi.PositionBuilderAtEnd(builder, fillDoneBlock);
        }
        LlvmApi.BuildBr(builder, scanNextBlock);
        LlvmApi.PositionBuilderAtEnd(builder, scanNextBlock);
        LlvmApi.BuildBr(builder, scanBlock);
        LlvmApi.PositionBuilderAtEnd(builder, afterScanBlock);

        // Block until ready. With sockets, epoll_wait bounded by the earliest timer; else sleep to it.
        LlvmValueHandle minRem = LlvmApi.BuildLoad2(builder, state.I64, minSlot, "saw_min");
        LlvmValueHandle sleepMs = LlvmApi.BuildSelect(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, minRem, maxVal, "saw_no_timer"), zero, minRem, "saw_sleep_ms");
        if (linux)
        {
            LlvmValueHandle hasSocket = LlvmApi.BuildLoad2(builder, state.I64, hasSocketSlot, "saw_has_socket");
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
                EmitLinuxSyscall6(state, SyscallEpollWait, epollFd, eventArg, LlvmApi.ConstInt(state.I64, 1, 0), timeout, zero, zero, "saw_epoll_wait");
            }
            else
            {
                EmitLinuxSyscall4(state, SyscallEpollWait, epollFd, eventArg, LlvmApi.ConstInt(state.I64, 1, 0), timeout, "saw_epoll_wait");
            }
            LlvmApi.BuildStore(builder, LlvmApi.BuildSub(builder, EmitMonotonicNowMs(state, "saw_wait_end"), startMs, "saw_epoll_elapsed"), elapsedSlot);
            LlvmApi.BuildBr(builder, afterWaitBlock);
            LlvmApi.PositionBuilderAtEnd(builder, timerWaitBlock);
            EmitNanosleep(state, sleepMs);
            LlvmApi.BuildStore(builder, sleepMs, elapsedSlot);
            LlvmApi.BuildBr(builder, afterWaitBlock);
            LlvmApi.PositionBuilderAtEnd(builder, afterWaitBlock);
        }
        else if (windowsSockets)
        {
            LlvmValueHandle hasSocket = LlvmApi.BuildLoad2(builder, state.I64, hasSocketSlot, "saw_has_socket");
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
            LlvmValueHandle pollCount = LlvmApi.BuildLoad2(builder, state.I64, pollCountSlot, "saw_poll_count");
            _ = EmitWindowsWsaPoll(state, pollArrayPtr, pollCount, timeout, "saw_wsapoll_wait");
            LlvmApi.BuildStore(builder, LlvmApi.BuildSub(builder, EmitMonotonicNowMs(state, "saw_wait_end"), startMs, "saw_wsapoll_elapsed"), elapsedSlot);
            LlvmApi.BuildBr(builder, afterWaitBlock);
            LlvmApi.PositionBuilderAtEnd(builder, timerWaitBlock);
            EmitNanosleep(state, sleepMs);
            LlvmApi.BuildStore(builder, sleepMs, elapsedSlot);
            LlvmApi.BuildBr(builder, afterWaitBlock);
            LlvmApi.PositionBuilderAtEnd(builder, afterWaitBlock);
        }
        else
        {
            EmitNanosleep(state, sleepMs);
            LlvmApi.BuildStore(builder, sleepMs, elapsedSlot);
        }
        LlvmValueHandle elapsed = LlvmApi.BuildLoad2(builder, state.I64, elapsedSlot, "saw_elapsed");

        // Pass 2: re-queue every parked leaf; decrement timer leaves by the elapsed time.
        LlvmApi.BuildStore(builder, LlvmApi.BuildLoad2(builder, state.I64, parkedHeadGlobal, "saw_head1"), cursorSlot);
        LlvmBasicBlockHandle reqBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_requeue");
        LlvmBasicBlockHandle reqBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_requeue_body");
        LlvmBasicBlockHandle reqDecBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_requeue_dec");
        LlvmBasicBlockHandle reqEnqBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_requeue_enq");
        LlvmBasicBlockHandle reqDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "saw_requeue_done");
        LlvmApi.BuildBr(builder, reqBlock);
        LlvmApi.PositionBuilderAtEnd(builder, reqBlock);
        LlvmValueHandle reqCur = LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, "saw_req_cur");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, reqCur, zero, "saw_req_end"), reqDoneBlock, reqBodyBlock);
        LlvmApi.PositionBuilderAtEnd(builder, reqBodyBlock);
        LlvmValueHandle reqNext = LoadMemory(state, reqCur, TaskStructLayout.ReadyNext, "saw_req_next");
        LlvmApi.BuildStore(builder, reqNext, cursorSlot);
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
    private static LlvmValueHandle EmitRunDetachedBody(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle headGlobal = DetachedTasksHeadGlobal(state);
        LlvmValueHandle guardGlobal = DetachedStepGuardGlobal(state);

        LlvmBasicBlockHandle startBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rd_start");
        LlvmBasicBlockHandle guardedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rd_guarded");
        LlvmBasicBlockHandle checkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rd_check");
        LlvmBasicBlockHandle bodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rd_body");
        LlvmBasicBlockHandle reapBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rd_reap");
        LlvmBasicBlockHandle unlinkHeadBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rd_unlink_head");
        LlvmBasicBlockHandle unlinkMidBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rd_unlink_mid");
        LlvmBasicBlockHandle freeCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rd_free_check");
        LlvmBasicBlockHandle freeBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rd_free_body");
        LlvmBasicBlockHandle freeDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rd_free_done");
        LlvmBasicBlockHandle keepBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rd_keep");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "rd_done");

        LlvmValueHandle curSlot = LlvmApi.BuildAlloca(builder, state.I64, "rd_cur_slot");
        LlvmValueHandle prevSlot = LlvmApi.BuildAlloca(builder, state.I64, "rd_prev_slot");
        LlvmValueHandle nextSlot = LlvmApi.BuildAlloca(builder, state.I64, "rd_next_slot");
        LlvmValueHandle freeBaseSlot = LlvmApi.BuildAlloca(builder, state.I64, "rd_free_base_slot");

        LlvmValueHandle guard = LlvmApi.BuildLoad2(builder, state.I64, guardGlobal, "rd_guard");
        LlvmValueHandle reentered = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, guard, LlvmApi.ConstInt(state.I64, 0, 0), "rd_reentered");
        LlvmApi.BuildCondBr(builder, reentered, doneBlock, startBlock);

        LlvmApi.PositionBuilderAtEnd(builder, startBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), guardGlobal);
        LlvmApi.BuildStore(builder, LlvmApi.BuildLoad2(builder, state.I64, headGlobal, "rd_head"), curSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), prevSlot);
        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkBlock);
        LlvmValueHandle cur = LlvmApi.BuildLoad2(builder, state.I64, curSlot, "rd_cur");
        LlvmValueHandle curDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, cur, LlvmApi.ConstInt(state.I64, 0, 0), "rd_cur_done");
        LlvmApi.BuildCondBr(builder, curDone, guardedBlock, bodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, guardedBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), guardGlobal);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, bodyBlock);
        LlvmValueHandle curBody = LlvmApi.BuildLoad2(builder, state.I64, curSlot, "rd_cur_body");
        LlvmApi.BuildStore(builder, LoadMemory(state, curBody, TaskStructLayout.NextTask, "rd_next"), nextSlot);
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
        LlvmApi.BuildCondBr(builder, completed, reapBlock, keepBlock);

        LlvmApi.PositionBuilderAtEnd(builder, reapBlock);
        // Capture the arena end BEFORE freeing (the task lives inside its first chunk).
        LlvmValueHandle reapCur = LlvmApi.BuildLoad2(builder, state.I64, curSlot, "rd_reap_cur");
        LlvmValueHandle reapEnd = LoadMemory(state, reapCur, TaskStructLayout.ArenaEnd, "rd_reap_end");
        LlvmApi.BuildStore(builder, LlvmApi.BuildSub(builder, reapEnd,
            LlvmApi.ConstInt(state.I64, HeapChunkBytes, 0), "rd_last_chunk_base"), freeBaseSlot);
        LlvmValueHandle reapPrev = LlvmApi.BuildLoad2(builder, state.I64, prevSlot, "rd_reap_prev");
        LlvmValueHandle prevIsHead = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, reapPrev, LlvmApi.ConstInt(state.I64, 0, 0), "rd_prev_is_head");
        LlvmApi.BuildCondBr(builder, prevIsHead, unlinkHeadBlock, unlinkMidBlock);

        LlvmApi.PositionBuilderAtEnd(builder, unlinkHeadBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildLoad2(builder, state.I64, nextSlot, "rd_next_for_head"), headGlobal);
        LlvmApi.BuildBr(builder, freeCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, unlinkMidBlock);
        StoreMemory(state, reapPrev, TaskStructLayout.NextTask,
            LlvmApi.BuildLoad2(builder, state.I64, nextSlot, "rd_next_for_mid"), "rd_unlink_mid_store");
        LlvmApi.BuildBr(builder, freeCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, freeCheckBlock);
        LlvmValueHandle freeBase = LlvmApi.BuildLoad2(builder, state.I64, freeBaseSlot, "rd_free_base");
        LlvmValueHandle freeDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, freeBase, LlvmApi.ConstInt(state.I64, 0, 0), "rd_free_done_check");
        LlvmApi.BuildCondBr(builder, freeDone, freeDoneBlock, freeBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, freeBodyBlock);
        LlvmValueHandle freeBaseBody = LlvmApi.BuildLoad2(builder, state.I64, freeBaseSlot, "rd_free_base_body");
        LlvmApi.BuildStore(builder, LoadMemory(state, freeBaseBody, 0, "rd_prev_chunk"), freeBaseSlot);
        EmitFreeOsMemory(state, freeBaseBody, HeapChunkBytes, "rd_free_chunk");
        LlvmApi.BuildBr(builder, freeCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, freeDoneBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildLoad2(builder, state.I64, nextSlot, "rd_advance_after_reap"), curSlot);
        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, keepBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildLoad2(builder, state.I64, curSlot, "rd_keep_cur"), prevSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildLoad2(builder, state.I64, nextSlot, "rd_advance_after_keep"), curSlot);
        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, headGlobal, "rd_result_head");
    }

    /// <summary>
    /// Body of <c>ashes_detached_wait_meta()</c>: scans the detached list and packs
    /// (hasRunnable &lt;&lt; 32) | (minTimerMs + 1) — low 32 bits 0 when no timer-parked task.
    /// A runnable task (not completed, WaitKind == WaitNone) means blocking waits must not block.
    /// </summary>
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

        LlvmBasicBlockHandle checkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dm_check");
        LlvmBasicBlockHandle bodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dm_body");
        LlvmBasicBlockHandle timerBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dm_timer");
        LlvmBasicBlockHandle timerMinBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dm_timer_min");
        LlvmBasicBlockHandle advanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dm_advance");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "dm_done");

        LlvmValueHandle curSlot = LlvmApi.BuildAlloca(builder, state.I64, "dm_cur_slot");
        LlvmValueHandle runnableSlot = LlvmApi.BuildAlloca(builder, state.I64, "dm_runnable_slot");
        LlvmValueHandle minTimerSlot = LlvmApi.BuildAlloca(builder, state.I64, "dm_min_timer_slot");
        LlvmApi.BuildStore(builder, LlvmApi.BuildLoad2(builder, state.I64, headGlobal, "dm_head"), curSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), runnableSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), minTimerSlot);
        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkBlock);
        LlvmValueHandle cur = LlvmApi.BuildLoad2(builder, state.I64, curSlot, "dm_cur");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, cur, LlvmApi.ConstInt(state.I64, 0, 0), "dm_done_check");
        LlvmApi.BuildCondBr(builder, done, doneBlock, bodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, bodyBlock);
        LlvmValueHandle stateIdx = LoadMemory(state, cur, TaskStructLayout.StateIndex, "dm_state");
        LlvmValueHandle waitKind = LoadMemory(state, cur, TaskStructLayout.WaitKind, "dm_wait_kind");
        LlvmValueHandle notCompleted = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, stateIdx,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateCompleted), 1), "dm_not_completed");
        LlvmValueHandle waitNone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, waitKind,
            LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitNone, 0), "dm_wait_none");
        LlvmValueHandle runnable = LlvmApi.BuildAnd(builder, notCompleted, waitNone, "dm_runnable");
        LlvmValueHandle priorRunnable = LlvmApi.BuildLoad2(builder, state.I64, runnableSlot, "dm_prior_runnable");
        LlvmApi.BuildStore(builder, LlvmApi.BuildOr(builder, priorRunnable,
            LlvmApi.BuildZExt(builder, runnable, state.I64, "dm_runnable_i64"), "dm_runnable_or"), runnableSlot);
        LlvmValueHandle isTimer = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, waitKind,
            LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTimer, 0), "dm_is_timer");
        LlvmApi.BuildCondBr(builder, isTimer, timerBlock, advanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, timerBlock);
        // Remaining ms live in the sleeping leaf: the task itself, or its awaited sub-task.
        LlvmValueHandle awaited = LoadMemory(state, cur, TaskStructLayout.AwaitedTask, "dm_awaited");
        LlvmValueHandle hasAwaited = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, awaited, LlvmApi.ConstInt(state.I64, 0, 0), "dm_has_awaited");
        LlvmValueHandle sleeper = LlvmApi.BuildSelect(builder, hasAwaited, awaited, cur, "dm_sleeper");
        LlvmValueHandle remaining = LoadMemory(state, sleeper, TaskStructLayout.SleepDurationMs, "dm_remaining");
        LlvmValueHandle minTimer = LlvmApi.BuildLoad2(builder, state.I64, minTimerSlot, "dm_min_timer");
        LlvmValueHandle noMinYet = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, minTimer, LlvmApi.ConstInt(state.I64, 0, 0), "dm_no_min_yet");
        LlvmValueHandle remainingPlus1 = LlvmApi.BuildAdd(builder, remaining, LlvmApi.ConstInt(state.I64, 1, 0), "dm_remaining_plus1");
        LlvmValueHandle lower = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, remainingPlus1, minTimer, "dm_lower");
        LlvmValueHandle shouldUpdate = LlvmApi.BuildOr(builder, noMinYet, lower, "dm_should_update");
        LlvmApi.BuildCondBr(builder, shouldUpdate, timerMinBlock, advanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, timerMinBlock);
        LlvmApi.BuildStore(builder, remainingPlus1, minTimerSlot);
        LlvmApi.BuildBr(builder, advanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, advanceBlock);
        LlvmValueHandle curAdvance = LlvmApi.BuildLoad2(builder, state.I64, curSlot, "dm_cur_advance");
        LlvmApi.BuildStore(builder, LoadMemory(state, curAdvance, TaskStructLayout.NextTask, "dm_next"), curSlot);
        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        LlvmValueHandle runnableFinal = LlvmApi.BuildLoad2(builder, state.I64, runnableSlot, "dm_runnable_final");
        LlvmValueHandle minTimerFinal = LlvmApi.BuildLoad2(builder, state.I64, minTimerSlot, "dm_min_timer_final");
        // Clamp minTimer+1 into 32 bits so the pack below cannot collide with the runnable bit.
        LlvmValueHandle clamped = LlvmApi.BuildSelect(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, minTimerFinal, LlvmApi.ConstInt(state.I64, 0x7FFFFFFF, 0), "dm_clamp_check"),
            LlvmApi.ConstInt(state.I64, 0x7FFFFFFF, 0), minTimerFinal, "dm_clamped");
        return LlvmApi.BuildOr(builder,
            LlvmApi.BuildShl(builder, runnableFinal, LlvmApi.ConstInt(state.I64, 32, 0), "dm_runnable_shifted"),
            clamped, "dm_packed");
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
    private static LlvmValueHandle EmitDetachedFillPollFdsBody(LlvmCodegenState state, LlvmValueHandle arrayPtr, LlvmValueHandle capacity)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle headGlobal = DetachedTasksHeadGlobal(state);

        LlvmBasicBlockHandle checkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "df_check");
        LlvmBasicBlockHandle bodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "df_body");
        LlvmBasicBlockHandle fillBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "df_fill");
        LlvmBasicBlockHandle advanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "df_advance");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "df_done");

        LlvmValueHandle curSlot = LlvmApi.BuildAlloca(builder, state.I64, "df_cur_slot");
        LlvmValueHandle countSlot = LlvmApi.BuildAlloca(builder, state.I64, "df_count_slot");
        LlvmApi.BuildStore(builder, LlvmApi.BuildLoad2(builder, state.I64, headGlobal, "df_head"), curSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), countSlot);
        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkBlock);
        LlvmValueHandle cur = LlvmApi.BuildLoad2(builder, state.I64, curSlot, "df_cur");
        LlvmValueHandle count = LlvmApi.BuildLoad2(builder, state.I64, countSlot, "df_count");
        LlvmValueHandle listDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, cur, LlvmApi.ConstInt(state.I64, 0, 0), "df_list_done");
        LlvmValueHandle atCapacity = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, count, capacity, "df_at_capacity");
        LlvmValueHandle stop = LlvmApi.BuildOr(builder, listDone, atCapacity, "df_stop");
        LlvmApi.BuildCondBr(builder, stop, doneBlock, bodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, bodyBlock);
        LlvmValueHandle waitKind = LoadMemory(state, cur, TaskStructLayout.WaitKind, "df_wait_kind");
        LlvmValueHandle isRead = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, waitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitSocketRead, 0), "df_is_read");
        LlvmValueHandle isTlsRead = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, waitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTlsWantRead, 0), "df_is_tls_read");
        LlvmValueHandle isWrite = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, waitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitSocketWrite, 0), "df_is_write");
        LlvmValueHandle isTlsWrite = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, waitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTlsWantWrite, 0), "df_is_tls_write");
        LlvmValueHandle readish = LlvmApi.BuildOr(builder, isRead, isTlsRead, "df_readish");
        LlvmValueHandle writeish = LlvmApi.BuildOr(builder, isWrite, isTlsWrite, "df_writeish");
        LlvmValueHandle should = LlvmApi.BuildOr(builder, readish, writeish, "df_should");
        LlvmApi.BuildCondBr(builder, should, fillBlock, advanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, fillBlock);
        LlvmValueHandle handle = LoadMemory(state, cur, TaskStructLayout.WaitHandle, "df_handle");
        LlvmValueHandle fillCount = LlvmApi.BuildLoad2(builder, state.I64, countSlot, "df_fill_count");
        LlvmValueHandle slotAddress = LlvmApi.BuildAdd(builder, arrayPtr,
            LlvmApi.BuildMul(builder, fillCount, LlvmApi.ConstInt(state.I64, WindowsPollFdSize, 0), "df_slot_offset"),
            "df_slot_address");
        LlvmValueHandle eventMask = EmitWindowsPollEventMask(state, readish, "df_poll_event_mask");
        EmitWindowsInitializePollFd(state, slotAddress, handle, eventMask, "df_pollfd");
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, fillCount, LlvmApi.ConstInt(state.I64, 1, 0), "df_count_next"), countSlot);
        LlvmApi.BuildBr(builder, advanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, advanceBlock);
        LlvmValueHandle curAdvance = LlvmApi.BuildLoad2(builder, state.I64, curSlot, "df_cur_advance");
        LlvmApi.BuildStore(builder, LoadMemory(state, curAdvance, TaskStructLayout.NextTask, "df_next"), curSlot);
        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, countSlot, "df_final_count");
    }

    private static void EmitWaitForPendingLeafTask(LlvmCodegenState state, LlvmValueHandle taskPtr, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        // Advance detached (spawned) tasks before blocking, and gather what the combined wait
        // must respect: a runnable detached task forbids blocking; a timer-parked one bounds it.
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
        LlvmValueHandle isTimerWait = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            waitKind,
            LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTimer, 0),
            prefix + "_is_timer_wait");

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

        LlvmApi.PositionBuilderAtEnd(builder, timerBlock);
        LlvmValueHandle leafRemaining = LoadMemory(state, taskPtr, TaskStructLayout.SleepDurationMs, prefix + "_timer_remaining");
        if (!detached)
        {
            EmitNanosleep(state, leafRemaining);
            StoreMemory(state, taskPtr, TaskStructLayout.SleepDurationMs, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_timer_zero");
            LlvmApi.BuildBr(builder, continueBlock);
        }
        else
        {
            // With detached tasks in flight the sleep is chunked (10 ms ticks, or 0 when a detached
            // task is runnable) so spawned work keeps advancing; the driver loops back through this
            // wait, which re-steps the detached list, until the remaining time reaches zero.
            LlvmBasicBlockHandle timerFullBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_timer_full");
            LlvmBasicBlockHandle timerChunkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_timer_chunk");
            LlvmValueHandle noDetached = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, detachedHead,
                LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_timer_no_detached");
            LlvmApi.BuildCondBr(builder, noDetached, timerFullBlock, timerChunkBlock);

            LlvmApi.PositionBuilderAtEnd(builder, timerFullBlock);
            EmitNanosleep(state, leafRemaining);
            StoreMemory(state, taskPtr, TaskStructLayout.SleepDurationMs, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_timer_zero");
            LlvmApi.BuildBr(builder, continueBlock);

            LlvmApi.PositionBuilderAtEnd(builder, timerChunkBlock);
            LlvmValueHandle tenMs = LlvmApi.ConstInt(state.I64, 10, 0);
            LlvmValueHandle overTen = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, leafRemaining, tenMs, prefix + "_timer_over_ten");
            LlvmValueHandle chunk = LlvmApi.BuildSelect(builder, overTen, tenMs, leafRemaining, prefix + "_timer_chunk_ms");
            LlvmValueHandle runnableNow = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, detachedRunnable,
                LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_timer_runnable");
            chunk = LlvmApi.BuildSelect(builder, runnableNow, LlvmApi.ConstInt(state.I64, 0, 0), chunk, prefix + "_timer_chunk_eff");
            EmitNanosleep(state, chunk);
            StoreMemory(state, taskPtr, TaskStructLayout.SleepDurationMs,
                LlvmApi.BuildSub(builder, leafRemaining, chunk, prefix + "_timer_new_remaining"), prefix + "_timer_chunk_store");
            // The slept chunk elapses for detached sleepers too — advance their remaining time so
            // a spawned sleep completes while the driving task sleeps.
            _ = EmitNetworkingRuntimeCall(state, "ashes_detached_advance_timers", [chunk], prefix + "_timer_chunk_advance");
            LlvmApi.BuildBr(builder, continueBlock);
        }

        LlvmApi.PositionBuilderAtEnd(builder, waitBlock);
        // Combined wait bound: a runnable detached task means poll without blocking; a timer-parked
        // one caps the block at its remaining ms; otherwise block indefinitely on the fds.
        LlvmValueHandle waitTimeout = LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1);
        if (detached)
        {
            LlvmValueHandle noTimer = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, detachedMinTimerPlus1,
                LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_no_detached_timer");
            LlvmValueHandle timerBound = LlvmApi.BuildSub(builder, detachedMinTimerPlus1,
                LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_detached_timer_bound");
            waitTimeout = LlvmApi.BuildSelect(builder, noTimer,
                LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), timerBound, prefix + "_timeout_timer");
            LlvmValueHandle runnableNow = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, detachedRunnable,
                LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_wait_runnable");
            waitTimeout = LlvmApi.BuildSelect(builder, runnableNow,
                LlvmApi.ConstInt(state.I64, 0, 0), waitTimeout, prefix + "_timeout_eff");
        }

        LlvmValueHandle waitStartMs = detached ? EmitMonotonicNowMs(state, prefix + "_wait_start") : default;

        if (IsLinuxFlavor(state.Flavor))
        {
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

            LlvmValueHandle eventMask = LlvmApi.BuildSelect(builder, readishWait, LlvmApi.ConstInt(state.I64, 0x001, 0), LlvmApi.ConstInt(state.I64, 0x004, 0), prefix + "_event_mask");
            _ = EmitNetworkingRuntimeCall(state, "ashes_epoll_register", [epollFd, waitHandle, eventMask], prefix + "_epoll_register");
            if (detached)
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
        else if (!detached)
        {
            LlvmTypeHandle pollFdType = LlvmApi.ArrayType2(state.I8, WindowsPollFdSize);
            LlvmValueHandle pollFdStorage = LlvmApi.BuildAlloca(builder, pollFdType, prefix + "_pollfd_storage");
            LlvmValueHandle pollFdPtr = GetArrayElementPointer(state, pollFdType, pollFdStorage, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_pollfd_ptr");
            LlvmValueHandle pollFdAddress = LlvmApi.BuildPtrToInt(builder, pollFdPtr, state.I64, prefix + "_pollfd_address");
            LlvmValueHandle eventMask = EmitWindowsPollEventMask(state, readishWait, prefix + "_poll_event_mask");
            EmitWindowsInitializePollFd(state, pollFdAddress, waitHandle, eventMask, prefix + "_pollfd");
            _ = EmitWindowsWsaPoll(state, pollFdPtr, LlvmApi.ConstInt(state.I64, 1, 0), LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), prefix + "_wsapoll_wait");
        }
        else
        {
            // Detached-aware Windows wait: the main task's pollfd goes in slot 0 of a shared
            // scratch array, detached socket waits fill the remaining slots, and one WSAPoll
            // covers them all.
            LlvmTypeHandle pollArrayType = LlvmApi.ArrayType2(state.I8, (ulong)(WindowsPollFdSize * DetachedPollFdCapacity));
            LlvmValueHandle pollArrayGlobal = ReadLineScratchGlobal(state, "__ashes_detached_pollfds", pollArrayType);
            LlvmValueHandle pollArrayPtr = GetArrayElementPointer(state, pollArrayType, pollArrayGlobal, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_poll_array_ptr");
            LlvmValueHandle pollArrayAddress = LlvmApi.BuildPtrToInt(builder, pollArrayPtr, state.I64, prefix + "_poll_array_address");
            LlvmValueHandle eventMask = EmitWindowsPollEventMask(state, readishWait, prefix + "_poll_event_mask");
            EmitWindowsInitializePollFd(state, pollArrayAddress, waitHandle, eventMask, prefix + "_pollfd");
            LlvmValueHandle detachedCount = EmitNetworkingRuntimeCall(state, "ashes_detached_fill_pollfds",
                [
                    LlvmApi.BuildAdd(builder, pollArrayAddress, LlvmApi.ConstInt(state.I64, WindowsPollFdSize, 0), prefix + "_poll_array_rest"),
                    LlvmApi.ConstInt(state.I64, DetachedPollFdCapacity - 1, 0),
                ],
                prefix + "_detached_fill");
            LlvmValueHandle totalCount = LlvmApi.BuildAdd(builder, detachedCount, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_poll_total");
            _ = EmitWindowsWsaPoll(state, pollArrayPtr, totalCount, waitTimeout, prefix + "_wsapoll_wait");
        }

        if (detached)
        {
            // Charge detached sleepers with the ACTUAL time the wait took — an early fd wake
            // (e.g. a new connection) must not consume the whole timer bound.
            LlvmValueHandle waitEndMs = EmitMonotonicNowMs(state, prefix + "_wait_end");
            LlvmValueHandle elapsed = LlvmApi.BuildSub(builder, waitEndMs, waitStartMs, prefix + "_detached_elapsed");
            _ = EmitNetworkingRuntimeCall(state, "ashes_detached_advance_timers", [elapsed], prefix + "_detached_advance");
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
    private static LlvmValueHandle EmitAsyncAllInline(LlvmCodegenState state, LlvmValueHandle taskListPtr)
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
        // Run-queue mode: a parking composite so a spawned handler's Async.race does not block peers.
        if (state.UseRunQueueScheduler)
        {
            return EmitCreateCompositeTask(state, taskListPtr, TaskStructLayout.StateRaceComposite);
        }

        return EmitAsyncRaceInline(state, taskListPtr);
    }

    private static LlvmValueHandle EmitAsyncRaceInline(LlvmCodegenState state, LlvmValueHandle taskListPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "race_res");
        LlvmValueHandle resultTaskSlot = LlvmApi.BuildAlloca(builder, state.I64, "race_result_task");
        LlvmValueHandle listSlot = LlvmApi.BuildAlloca(builder, state.I64, "race_list");
        LlvmValueHandle pendingCountSlot = LlvmApi.BuildAlloca(builder, state.I64, "race_pending_count");
        LlvmValueHandle preferredWaitHandleSlot = LlvmApi.BuildAlloca(builder, state.I64, "race_preferred_wait_handle");
        LlvmValueHandle preferredCursorSlot = LlvmApi.BuildAlloca(builder, state.I64, "race_preferred_cursor");
        LlvmValueHandle cancelCursorSlot = LlvmApi.BuildAlloca(builder, state.I64, "race_cancel_cursor");

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
        LlvmBasicBlockHandle cancelInitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_cancel_init");
        LlvmBasicBlockHandle cancelCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_cancel_check");
        LlvmBasicBlockHandle cancelBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_cancel_body");
        LlvmBasicBlockHandle cancelOneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "race_cancel_one");
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
        LlvmApi.BuildBr(builder, cancelInitBlock);

        // Walk the original input task list and cancel every entry that is
        // not the winning task by calling ashes_cancel_task (which internally
        // closes any OS socket the loser is parked on via EmitTcpClose and
        // recursively cancels awaited sub-tasks), so race releases resources
        // promptly per LANGUAGE_SPEC §19.7.3.
        LlvmApi.PositionBuilderAtEnd(builder, cancelInitBlock);
        LlvmApi.BuildStore(builder, taskListPtr, cancelCursorSlot);
        LlvmApi.BuildBr(builder, cancelCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, cancelCheckBlock);
        LlvmValueHandle cancelCursor = LlvmApi.BuildLoad2(builder, state.I64, cancelCursorSlot, "race_cancel_cursor_value");
        LlvmValueHandle cancelDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, cancelCursor, LlvmApi.ConstInt(state.I64, 0, 0), "race_cancel_done");
        LlvmApi.BuildCondBr(builder, cancelDone, doneBlock, cancelBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, cancelBodyBlock);
        LlvmValueHandle cancelNode = LlvmApi.BuildLoad2(builder, state.I64, cancelCursorSlot, "race_cancel_node");
        LlvmValueHandle cancelCandidate = LoadMemory(state, cancelNode, 0, "race_cancel_candidate");
        LlvmValueHandle cancelTail = LoadMemory(state, cancelNode, 8, "race_cancel_tail");
        LlvmApi.BuildStore(builder, cancelTail, cancelCursorSlot);
        LlvmValueHandle isWinner = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, cancelCandidate, resultTask, "race_cancel_is_winner");
        LlvmApi.BuildCondBr(builder, isWinner, cancelCheckBlock, cancelOneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, cancelOneBlock);
        _ = EmitNetworkingRuntimeCall(state, "ashes_cancel_task", [cancelCandidate], "race_cancel_call");
        LlvmApi.BuildBr(builder, cancelCheckBlock);

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
