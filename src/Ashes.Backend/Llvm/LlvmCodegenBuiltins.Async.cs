using Ashes.Semantics;
using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{

    /// <summary>
    /// The shared listener handle for the Windows fork-based multi-reactor (0 = none). The parent sets
    /// it to the socket it bound; a relaunched worker sets it to the inherited handle. When non-zero,
    /// <c>listen</c> returns it instead of binding, so every worker accepts on the one shared listener.
    /// </summary>
    private static LlvmValueHandle WorkerListenerGlobal(LlvmCodegenState state) =>
        state.Target.GetOrAddNamedGlobal("__ashes_worker_listener", () =>
        {
            LlvmValueHandle global = LlvmApi.AddGlobal(state.Target.Module, state.I64, "__ashes_worker_listener");
            LlvmApi.SetInitializer(global, LlvmApi.ConstInt(state.I64, 0, 0));
            LlvmApi.SetLinkage(global, LlvmLinkage.Internal);
            return global;
        });

    private static LlvmValueHandle EpollFdGlobal(LlvmCodegenState state) =>
        state.Target.GetOrAddNamedGlobal("__ashes_epoll_fd", () =>
        {
            LlvmValueHandle global = LlvmApi.AddGlobal(state.Target.Module, state.I64, "__ashes_epoll_fd");
            LlvmApi.SetInitializer(global, LlvmApi.ConstInt(state.I64, 0, 0));
            LlvmApi.SetLinkage(global, LlvmLinkage.Internal);
            return global;
        });

    // One byte per fd: 0 = not in the epoll set, else the registered event mask. Lets ashes_epoll_register
    // skip the epoll_ctl syscall when a socket is already registered with the wanted mask.
    private static LlvmValueHandle EpollMasksGlobal(LlvmCodegenState state) =>
        ReadLineScratchGlobal(state, "__ashes_epoll_masks", LlvmApi.ArrayType2(state.I8, EpollMaskTableSize));

    /// <summary>
    /// ashes_epoll_register(epollFd, handle, mask): ensures fd `handle` is in the persistent epoll set
    /// with event mask `mask` (EPOLLIN=1 / EPOLLOUT=4). The per-fd mask table means EPOLL_CTL_ADD only
    /// for a newly-parked socket, EPOLL_CTL_MOD only when the mask changed, nothing when already correct
    /// — a wait over N parked sockets costs O(newly-parked) syscalls, not O(N).
    /// </summary>
    private static LlvmValueHandle EmitEpollRegisterBody(LlvmCodegenState state, LlvmValueHandle epollFd, LlvmValueHandle handle, LlvmValueHandle mask)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle masksBase = GetArrayElementPointer(state, LlvmApi.ArrayType2(state.I8, EpollMaskTableSize), EpollMasksGlobal(state), LlvmApi.ConstInt(state.I64, 0, 0), "epr_masks_base");

        LlvmTypeHandle eventType = LlvmApi.ArrayType2(state.I8, 16);
        LlvmValueHandle eventStorage = LlvmApi.BuildAlloca(builder, eventType, "epr_event");
        LlvmValueHandle eventPtr = GetArrayElementPointer(state, eventType, eventStorage, LlvmApi.ConstInt(state.I64, 0, 0), "epr_event_ptr");
        LlvmApi.BuildStore(builder, LlvmApi.BuildTrunc(builder, mask, state.I32, "epr_mask32"), LlvmApi.BuildBitCast(builder, eventPtr, state.I32Ptr, "epr_event_mask_ptr"));
        LlvmApi.BuildStore(builder, handle, LlvmApi.BuildBitCast(builder, LlvmApi.BuildGEP2(builder, state.I8, eventPtr, [LlvmApi.ConstInt(state.I64, 8, 0)], "epr_event_data_byte"), state.I64Ptr, "epr_event_data_ptr"));
        LlvmValueHandle eventArg = LlvmApi.BuildPtrToInt(builder, eventPtr, state.I64, "epr_event_arg");
        LlvmValueHandle maskByte = LlvmApi.BuildTrunc(builder, mask, state.I8, "epr_mask_byte");

        LlvmBasicBlockHandle inRangeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "epr_in_range");
        LlvmBasicBlockHandle oorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "epr_oor");
        LlvmBasicBlockHandle ctlBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "epr_ctl");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "epr_done");
        LlvmValueHandle inRange = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, handle, LlvmApi.ConstInt(state.I64, 0, 0), "epr_ge0"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, handle, LlvmApi.ConstInt(state.I64, EpollMaskTableSize, 0), "epr_lt_max"),
            "epr_in_range_cond");
        LlvmApi.BuildCondBr(builder, inRange, inRangeBlock, oorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, oorBlock);
        _ = EmitLinuxSyscall4(state, SyscallEpollCtl, epollFd, LlvmApi.ConstInt(state.I64, 1, 0), handle, eventArg, "epr_oor_add");
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, inRangeBlock);
        LlvmValueHandle slot = LlvmApi.BuildGEP2(builder, state.I8, masksBase, [handle], "epr_slot");
        LlvmValueHandle cur = LlvmApi.BuildLoad2(builder, state.I8, slot, "epr_cur");
        LlvmApi.BuildCondBr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, cur, maskByte, "epr_same"),
            doneBlock, ctlBlock);

        LlvmApi.PositionBuilderAtEnd(builder, ctlBlock);
        LlvmValueHandle op = LlvmApi.BuildSelect(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, cur, LlvmApi.ConstInt(state.I8, 0, 0), "epr_new"),
            LlvmApi.ConstInt(state.I64, 1, 0), LlvmApi.ConstInt(state.I64, 3, 0), "epr_op");
        _ = EmitLinuxSyscall4(state, SyscallEpollCtl, epollFd, op, handle, eventArg, "epr_ctl_call");
        LlvmApi.BuildStore(builder, maskByte, slot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.ConstInt(state.I64, 0, 0);
    }

    /// <summary>
    /// ashes_epoll_forget(handle): clears the per-fd mask entry when a socket is closed. Closing an fd
    /// already removes it from the epoll set kernel-side; this just lets a reused fd number re-register.
    /// </summary>
    private static LlvmValueHandle EmitEpollForgetBody(LlvmCodegenState state, LlvmValueHandle handle)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle masksBase = GetArrayElementPointer(state, LlvmApi.ArrayType2(state.I8, EpollMaskTableSize), EpollMasksGlobal(state), LlvmApi.ConstInt(state.I64, 0, 0), "epf_masks_base");
        LlvmBasicBlockHandle clearBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "epf_clear");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "epf_done");
        LlvmValueHandle inRange = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, handle, LlvmApi.ConstInt(state.I64, 0, 0), "epf_ge0"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, handle, LlvmApi.ConstInt(state.I64, EpollMaskTableSize, 0), "epf_lt_max"),
            "epf_in_range");
        LlvmApi.BuildCondBr(builder, inRange, clearBlock, doneBlock);
        LlvmApi.PositionBuilderAtEnd(builder, clearBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I8, 0, 0), LlvmApi.BuildGEP2(builder, state.I8, masksBase, [handle], "epf_slot"));
        LlvmApi.BuildBr(builder, doneBlock);
        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.ConstInt(state.I64, 0, 0);
    }

    private static LlvmValueHandle ShutdownFlagGlobal(LlvmCodegenState state) =>
        state.Target.GetOrAddNamedGlobal("__ashes_shutdown_requested", () =>
        {
            LlvmValueHandle global = LlvmApi.AddGlobal(state.Target.Module, state.I64, "__ashes_shutdown_requested");
            LlvmApi.SetInitializer(global, LlvmApi.ConstInt(state.I64, 0, 0));
            LlvmApi.SetLinkage(global, LlvmLinkage.Internal);
            return global;
        });

    // Live spawned-handler count for the shutdown drain: incremented when a spawned task is
    // enqueued, decremented when its arena is reaped on completion. Single-threaded per reactor,
    // so plain loads/stores suffice.
    private static LlvmValueHandle LiveSpawnedGlobal(LlvmCodegenState state) =>
        state.Target.GetOrAddNamedGlobal("__ashes_live_spawned", () =>
        {
            LlvmValueHandle global = LlvmApi.AddGlobal(state.Target.Module, state.I64, "__ashes_live_spawned");
            LlvmApi.SetInitializer(global, LlvmApi.ConstInt(state.I64, 0, 0));
            LlvmApi.SetLinkage(global, LlvmLinkage.Internal);
            return global;
        });

    // Drain deadline (monotonic ms, 0 = drain not started) and drain bound (ms, default 10 s).
    private static LlvmValueHandle DrainDeadlineGlobal(LlvmCodegenState state) =>
        state.Target.GetOrAddNamedGlobal("__ashes_drain_deadline", () =>
        {
            LlvmValueHandle global = LlvmApi.AddGlobal(state.Target.Module, state.I64, "__ashes_drain_deadline");
            LlvmApi.SetInitializer(global, LlvmApi.ConstInt(state.I64, 0, 0));
            LlvmApi.SetLinkage(global, LlvmLinkage.Internal);
            return global;
        });

    private static LlvmValueHandle DrainTimeoutGlobal(LlvmCodegenState state) =>
        state.Target.GetOrAddNamedGlobal("__ashes_drain_timeout_ms", () =>
        {
            LlvmValueHandle global = LlvmApi.AddGlobal(state.Target.Module, state.I64, "__ashes_drain_timeout_ms");
            LlvmApi.SetInitializer(global, LlvmApi.ConstInt(state.I64, 10000, 0));
            LlvmApi.SetLinkage(global, LlvmLinkage.Internal);
            return global;
        });

    // Forked worker pids (Linux multi-reactor parent only; workers and single-reactor processes
    // keep the count at 0). The parent forwards the shutdown signal to these and reaps them as
    // part of its drain, so it cannot exit (and PDEATHSIG-cut them) mid-request.
    private const int MaxTrackedChildren = 1024;

    private static LlvmValueHandle ChildPidsGlobal(LlvmCodegenState state) =>
        state.Target.GetOrAddNamedGlobal("__ashes_child_pids", () =>
        {
            LlvmTypeHandle arrType = LlvmApi.ArrayType2(state.I64, MaxTrackedChildren);
            LlvmValueHandle global = LlvmApi.AddGlobal(state.Target.Module, arrType, "__ashes_child_pids");
            LlvmApi.SetInitializer(global, LlvmApi.ConstNull(arrType));
            LlvmApi.SetLinkage(global, LlvmLinkage.Internal);
            return global;
        });

    private static LlvmValueHandle ChildCountGlobal(LlvmCodegenState state) =>
        state.Target.GetOrAddNamedGlobal("__ashes_child_count", () =>
        {
            LlvmValueHandle global = LlvmApi.AddGlobal(state.Target.Module, state.I64, "__ashes_child_count");
            LlvmApi.SetInitializer(global, LlvmApi.ConstInt(state.I64, 0, 0));
            LlvmApi.SetLinkage(global, LlvmLinkage.Internal);
            return global;
        });

    /// <summary>
    /// Installs SIGINT/SIGTERM handlers (Linux) that set the shutdown flag, so a parked accept is
    /// interrupted (EINTR — the handler does not set SA_RESTART) and re-steps into the flag check.
    /// Emits the handler (stores 1 to the flag) and a naked restorer (rt_sigreturn) once, then
    /// rt_sigaction's both signals. Called once per reactor process from forkWorkers.
    /// </summary>
    private static void EmitInstallShutdownHandlers(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        (LlvmValueHandle handlerFn, LlvmValueHandle restorerFn) = EmitEnsureShutdownSignalFns(state);

        // struct kernel_sigaction { handler@0; flags@8; restorer@16; mask@24 } (32 bytes).
        // flags = SA_RESTORER(0x04000000); NOT SA_RESTART, so syscalls return EINTR.
        LlvmTypeHandle saType = LlvmApi.ArrayType2(state.I8, 32);
        LlvmValueHandle sa = LlvmApi.BuildAlloca(builder, saType, "shutdown_sa");
        LlvmValueHandle saPtr = GetArrayElementPointer(state, saType, sa, LlvmApi.ConstInt(state.I64, 0, 0), "shutdown_sa_ptr");
        StoreMemory(state, saPtr, 0, LlvmApi.BuildPtrToInt(builder, handlerFn, state.I64, "shutdown_handler_i64"), "shutdown_sa_handler");
        StoreMemory(state, saPtr, 8, LlvmApi.ConstInt(state.I64, 0x04000000, 0), "shutdown_sa_flags");
        StoreMemory(state, saPtr, 16, LlvmApi.BuildPtrToInt(builder, restorerFn, state.I64, "shutdown_restorer_i64"), "shutdown_sa_restorer");
        StoreMemory(state, saPtr, 24, LlvmApi.ConstInt(state.I64, 0, 0), "shutdown_sa_mask");
        LlvmValueHandle saAddr = LlvmApi.BuildPtrToInt(builder, saPtr, state.I64, "shutdown_sa_addr");
        // rt_sigaction(signum, &act, NULL, sigsetsize=8) for SIGINT(2) and SIGTERM(15).
        _ = EmitLinuxSyscall4(state, SyscallRtSigaction, LlvmApi.ConstInt(state.I64, 2, 0), saAddr, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 8, 0), "shutdown_sigint");
        _ = EmitLinuxSyscall4(state, SyscallRtSigaction, LlvmApi.ConstInt(state.I64, 15, 0), saAddr, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 8, 0), "shutdown_sigterm");
    }

    // Defines the internal __ashes_sig_handler and __ashes_sig_restorer functions once and returns
    // both handles. Later calls find them already present and just return the existing handles.
    private static (LlvmValueHandle Handler, LlvmValueHandle Restorer) EmitEnsureShutdownSignalFns(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle voidTy = LlvmApi.VoidTypeInContext(state.Target.Context);
        LlvmValueHandle handlerFn = LlvmApi.GetNamedFunction(state.Target.Module, "__ashes_sig_handler");
        LlvmValueHandle restorerFn = LlvmApi.GetNamedFunction(state.Target.Module, "__ashes_sig_restorer");
        if (handlerFn.Ptr != 0)
        {
            return (handlerFn, restorerFn);
        }

        LlvmBasicBlockHandle savedBlock = LlvmApi.GetInsertBlock(builder);

        handlerFn = LlvmApi.AddFunction(state.Target.Module, "__ashes_sig_handler", LlvmApi.FunctionType(voidTy, [state.I32]));
        LlvmApi.SetLinkage(handlerFn, LlvmLinkage.Internal);
        LlvmBasicBlockHandle he = LlvmApi.AppendBasicBlockInContext(state.Target.Context, handlerFn, "entry");
        // First signal starts the drain (sets the flag; the accept step turns it into the
        // sentinel after draining). A second signal during the drain forces an immediate,
        // still-clean exit(0) right here in the handler.
        LlvmBasicBlockHandle forceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, handlerFn, "force_exit");
        LlvmBasicBlockHandle firstBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, handlerFn, "first_signal");
        LlvmApi.PositionBuilderAtEnd(builder, he);
        LlvmValueHandle alreadyRequested = LlvmApi.BuildLoad2(builder, state.I64, ShutdownFlagGlobal(state), "sig_already");
        LlvmApi.BuildCondBr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, alreadyRequested, LlvmApi.ConstInt(state.I64, 0, 0), "sig_is_second"),
            forceBlock, firstBlock);
        LlvmApi.PositionBuilderAtEnd(builder, forceBlock);
        _ = EmitLinuxSyscall(state, SyscallExit, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), "sig_force_exit");
        LlvmApi.BuildUnreachable(builder);
        LlvmApi.PositionBuilderAtEnd(builder, firstBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), ShutdownFlagGlobal(state));
        LlvmApi.BuildRetVoid(builder);

        // Naked restorer: on return from the handler the kernel jumps here, which must invoke
        // rt_sigreturn to restore the interrupted context. No prologue may run first.
        restorerFn = LlvmApi.AddFunction(state.Target.Module, "__ashes_sig_restorer", LlvmApi.FunctionType(voidTy, []));
        LlvmApi.SetLinkage(restorerFn, LlvmLinkage.Internal);
        uint nakedKind = LlvmApi.GetEnumAttributeKindForName("naked");
        LlvmApi.AddAttributeAtIndex(restorerFn, LlvmApi.AttributeIndexFunction, LlvmApi.CreateEnumAttribute(state.Target.Context, nakedKind, 0));
        LlvmBasicBlockHandle re = LlvmApi.AppendBasicBlockInContext(state.Target.Context, restorerFn, "entry");
        LlvmApi.PositionBuilderAtEnd(builder, re);
        string restoreAsm = state.Flavor == LlvmCodegenFlavor.LinuxArm64
            ? "mov x8, #139\n\tsvc #0"
            : "movq $$15, %rax\n\tsyscall";
        LlvmValueHandle asm = LlvmApi.GetInlineAsm(LlvmApi.FunctionType(voidTy, []), restoreAsm, "~{memory}", true, false);
        LlvmApi.BuildCall2(builder, LlvmApi.FunctionType(voidTy, []), asm, [], "");
        LlvmApi.BuildUnreachable(builder);

        LlvmApi.PositionBuilderAtEnd(builder, savedBlock);
        return (handlerFn, restorerFn);
    }

    private static LlvmValueHandle EmitLeafTaskCompletedStatus(LlvmCodegenState state)
        => LlvmApi.ConstInt(state.I64, 1, 0);

    private static LlvmValueHandle EmitLeafTaskPendingStatus(LlvmCodegenState state)
        => LlvmApi.ConstInt(state.I64, 0, 0);

    private static void EmitClearLeafTaskWait(LlvmCodegenState state, LlvmValueHandle taskPtr, string prefix)
    {
        StoreMemory(state, taskPtr, TaskStructLayout.WaitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitNone, 0), prefix + "_wait_kind_clear");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitHandle, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_wait_handle_clear");
    }

    private static LlvmValueHandle EmitCompleteLeafTask(LlvmCodegenState state, LlvmValueHandle taskPtr, LlvmValueHandle result, string prefix)
    {
        EmitClearLeafTaskWait(state, taskPtr, prefix);
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot, result, prefix + "_result");
        StoreMemory(state, taskPtr, TaskStructLayout.StateIndex,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateCompleted), 1), prefix + "_done");
        return EmitLeafTaskCompletedStatus(state);
    }

    /// <summary>
    /// Emits the body of <c>ashes_cancel_task(taskPtr) -> i64</c>.
    /// Cancellation closes any OS socket the task is currently parked on
    /// (via <see cref="EmitTcpClose"/>, which routes to the platform close),
    /// recursively cancels any awaited sub-task, and marks the task
    /// <see cref="TaskStructLayout.StateCompleted"/> with <c>Ok(0)</c>.
    /// Already-completed tasks are a no-op.
    /// Always returns 0.
    /// Used by <c>Ashes.Task.race</c> to release resources held by losers
    /// as soon as the first task in the race completes.
    /// Known limitations: the userspace TLS session memory held by TLS leaf
    /// tasks is not freed (released at process exit); leaf tasks not yet
    /// parked on a wait (no <see cref="TaskStructLayout.WaitHandle"/>) leak
    /// any socket they hold in <see cref="TaskStructLayout.IoArg0"/>. The
    /// latter case is unreachable from <c>race</c> with the current scheduler
    /// because <c>ashes_step_task_until_wait_or_done</c> only surfaces tasks
    /// at wait points or completion; the limitation is recorded here for any
    /// future scheduler that exposes mid-step tasks to cancellation.
    /// </summary>
    private static LlvmValueHandle EmitCancelTask(LlvmCodegenState state, LlvmValueHandle taskPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        LlvmBasicBlockHandle workBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "cancel_work");
        LlvmBasicBlockHandle closeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "cancel_close");
        LlvmBasicBlockHandle afterCloseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "cancel_after_close");
        LlvmBasicBlockHandle cancelAwaitedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "cancel_awaited");
        LlvmBasicBlockHandle afterAwaitedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "cancel_after_awaited");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "cancel_done");

        LlvmValueHandle stateIdx = LoadMemory(state, taskPtr, TaskStructLayout.StateIndex, "cancel_state");
        LlvmValueHandle alreadyDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, stateIdx,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateCompleted), 1), "cancel_already_done");
        LlvmApi.BuildCondBr(builder, alreadyDone, doneBlock, workBlock);

        LlvmApi.PositionBuilderAtEnd(builder, workBlock);
        LlvmValueHandle waitHandle = LoadMemory(state, taskPtr, TaskStructLayout.WaitHandle, "cancel_wait_handle");
        LlvmValueHandle hasSocket = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, waitHandle, LlvmApi.ConstInt(state.I64, 0, 0), "cancel_has_socket");
        LlvmApi.BuildCondBr(builder, hasSocket, closeBlock, afterCloseBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeBlock);
        _ = EmitTcpClose(state, waitHandle);
        StoreMemory(state, taskPtr, TaskStructLayout.WaitHandle, LlvmApi.ConstInt(state.I64, 0, 0), "cancel_clear_wait_handle");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitNone, 0), "cancel_clear_wait_kind");
        LlvmApi.BuildBr(builder, afterCloseBlock);

        LlvmApi.PositionBuilderAtEnd(builder, afterCloseBlock);
        LlvmValueHandle awaited = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, "cancel_awaited_value");
        LlvmValueHandle hasAwaited = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, awaited, LlvmApi.ConstInt(state.I64, 0, 0), "cancel_has_awaited");
        LlvmApi.BuildCondBr(builder, hasAwaited, cancelAwaitedBlock, afterAwaitedBlock);

        LlvmApi.PositionBuilderAtEnd(builder, cancelAwaitedBlock);
        _ = EmitNetworkingRuntimeCall(state, "ashes_cancel_task", [awaited], "cancel_recurse");
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), "cancel_clear_awaited");
        LlvmApi.BuildBr(builder, afterAwaitedBlock);

        LlvmApi.PositionBuilderAtEnd(builder, afterAwaitedBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot,
            EmitResultOk(state, LlvmApi.ConstInt(state.I64, 0, 0)), "cancel_result");
        StoreMemory(state, taskPtr, TaskStructLayout.StateIndex,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)TaskStructLayout.StateCompleted), 1), "cancel_mark_done");
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.ConstInt(state.I64, 0, 0);
    }

    private static LlvmValueHandle EmitPendingLeafTask(LlvmCodegenState state, LlvmValueHandle taskPtr, long waitKind, LlvmValueHandle waitHandle, string prefix)
    {
        StoreMemory(state, taskPtr, TaskStructLayout.WaitKind, LlvmApi.ConstInt(state.I64, unchecked((ulong)waitKind), 0), prefix + "_wait_kind");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitHandle, waitHandle, prefix + "_wait_handle");
        return EmitLeafTaskPendingStatus(state);
    }

    private static LlvmValueHandle EmitStepTcpConnectTask(LlvmCodegenState state, LlvmValueHandle taskPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle hostRef = LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, "step_tcp_connect_host");
        LlvmValueHandle port = LoadMemory(state, taskPtr, TaskStructLayout.IoArg1, "step_tcp_connect_port");
        LlvmValueHandle socketSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_connect_socket_slot");
        LlvmValueHandle addrSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_connect_addr_slot");
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_connect_status_slot");
        LlvmApi.BuildStore(builder, LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tcp_connect_socket_cached"), socketSlot);
        LlvmApi.BuildStore(builder, LoadMemory(state, taskPtr, TaskStructLayout.WaitData1, "step_tcp_connect_addr_cached"), addrSlot);
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);

        LlvmValueHandle socketKnown = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne,
            LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "step_tcp_connect_socket_known"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "step_tcp_connect_has_socket");
        LlvmBasicBlockHandle reuseSocketBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_reuse_socket");
        LlvmBasicBlockHandle setupSocketBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_setup_socket");
        LlvmBasicBlockHandle connectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_connect");
        LlvmBasicBlockHandle pendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_pending");
        LlvmBasicBlockHandle failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_fail");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_done_block");
        LlvmApi.BuildCondBr(builder, socketKnown, reuseSocketBlock, setupSocketBlock);

        EmitStepTcpConnectSetup(state, taskPtr, hostRef, port, socketSlot, addrSlot, setupSocketBlock, connectBlock, failBlock);
        LlvmValueHandle sockaddrBytes = EmitStepTcpConnectSockaddr(state, addrSlot, port, reuseSocketBlock, connectBlock);
        EmitStepTcpConnectDoConnect(state, taskPtr, socketSlot, statusSlot, sockaddrBytes, pendingBlock, failBlock, doneBlock);
        EmitStepTcpConnectTail(state, taskPtr, socketSlot, statusSlot, pendingBlock, failBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, statusSlot, "step_tcp_connect_status");
    }

    private static void EmitStepTcpConnectSetup(LlvmCodegenState state, LlvmValueHandle taskPtr, LlvmValueHandle hostRef, LlvmValueHandle port, LlvmValueHandle socketSlot, LlvmValueHandle addrSlot, LlvmBasicBlockHandle setupSocketBlock, LlvmBasicBlockHandle connectBlock, LlvmBasicBlockHandle failBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, setupSocketBlock);
        LlvmValueHandle resolveResult = EmitResolveHostIpv4OrLocalhost(state, hostRef, "step_tcp_connect_resolve");
        LlvmValueHandle resolveTag = LoadMemory(state, resolveResult, 0, "step_tcp_connect_resolve_tag");
        LlvmValueHandle resolveFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, resolveTag, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_connect_resolve_failed");
        LlvmBasicBlockHandle validatePortBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_validate_port");
        LlvmBasicBlockHandle openSocketBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_open_socket");
        LlvmApi.BuildCondBr(builder, resolveFailed, failBlock, validatePortBlock);

        LlvmApi.PositionBuilderAtEnd(builder, validatePortBlock);
        LlvmValueHandle validPort = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, port, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_connect_port_gt_zero"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, port, LlvmApi.ConstInt(state.I64, 65535, 0), "step_tcp_connect_port_le_max"),
            "step_tcp_connect_port_valid");
        LlvmApi.BuildCondBr(builder, validPort, openSocketBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, openSocketBlock);
        LlvmValueHandle openedSocket;
        if (IsLinuxFlavor(state.Flavor))
        {
            openedSocket = EmitLinuxSyscall(
                state,
                SyscallSocket,
                LlvmApi.ConstInt(state.I64, 2, 0),
                LlvmApi.ConstInt(state.I64, 1, 0),
                LlvmApi.ConstInt(state.I64, 0, 0),
                "step_tcp_connect_socket_call");
        }
        else
        {
            LlvmTypeHandle wsadataType = LlvmApi.ArrayType2(state.I8, 512);
            LlvmValueHandle wsadata = LlvmApi.BuildAlloca(builder, wsadataType, "step_tcp_connect_wsadata");
            EmitWindowsWsaStartup(state, GetArrayElementPointer(state, wsadataType, wsadata, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_connect_wsadata_ptr"), "step_tcp_connect_wsastartup");
            openedSocket = EmitWindowsSocket(state, 2, 1, 6, "step_tcp_connect_socket_call");
        }
        LlvmApi.BuildStore(builder, openedSocket, socketSlot);
        LlvmApi.BuildStore(builder, LoadMemory(state, resolveResult, 8, "step_tcp_connect_addr_value"), addrSlot);
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, openedSocket, "step_tcp_connect_store_socket");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData1, LoadMemory(state, resolveResult, 8, "step_tcp_connect_store_addr_value"), "step_tcp_connect_store_addr");
        EmitSetSocketNonBlocking(state, openedSocket, "step_tcp_connect_nonblocking");
        LlvmApi.BuildBr(builder, connectBlock);
    }

    private static LlvmValueHandle EmitStepTcpConnectSockaddr(LlvmCodegenState state, LlvmValueHandle addrSlot, LlvmValueHandle port, LlvmBasicBlockHandle reuseSocketBlock, LlvmBasicBlockHandle connectBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, reuseSocketBlock);
        LlvmApi.BuildBr(builder, connectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectBlock);
        LlvmTypeHandle sockaddrType = LlvmApi.ArrayType2(state.I8, 16);
        LlvmValueHandle sockaddrStorage = LlvmApi.BuildAlloca(builder, sockaddrType, "step_tcp_connect_sockaddr");
        LlvmValueHandle sockaddrBytes = GetArrayElementPointer(state, sockaddrType, sockaddrStorage, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_connect_sockaddr_bytes");
        LlvmTypeHandle i16 = LlvmApi.Int16TypeInContext(state.Target.Context);
        LlvmTypeHandle i16Ptr = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.BuildBitCast(builder, sockaddrBytes, state.I64Ptr, "step_tcp_connect_sockaddr_i64"));
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.BuildBitCast(builder,
            LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, [LlvmApi.ConstInt(state.I64, 8, 0)], "step_tcp_connect_sockaddr_tail"),
            state.I64Ptr,
            "step_tcp_connect_sockaddr_tail_i64"));
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(i16, 2, 0), LlvmApi.BuildBitCast(builder, sockaddrBytes, i16Ptr, "step_tcp_connect_family_ptr"));
        LlvmValueHandle portPtr = LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, [LlvmApi.ConstInt(state.I64, 2, 0)], "step_tcp_connect_port_ptr_byte");
        LlvmApi.BuildStore(builder,
            LlvmApi.BuildTrunc(builder, EmitByteSwap16(state, port, "step_tcp_connect_port_network"), i16, "step_tcp_connect_port_i16"),
            LlvmApi.BuildBitCast(builder, portPtr, i16Ptr, "step_tcp_connect_port_ptr"));
        LlvmValueHandle addrPtr = LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, [LlvmApi.ConstInt(state.I64, 4, 0)], "step_tcp_connect_addr_ptr_byte");
        LlvmApi.BuildStore(builder,
            LlvmApi.BuildTrunc(builder, LlvmApi.BuildLoad2(builder, state.I64, addrSlot, "step_tcp_connect_addr_loaded"), state.I32, "step_tcp_connect_addr_i32"),
            LlvmApi.BuildBitCast(builder, addrPtr, state.I32Ptr, "step_tcp_connect_addr_ptr"));
        return sockaddrBytes;
    }

    private static void EmitStepTcpConnectDoConnect(LlvmCodegenState state, LlvmValueHandle taskPtr, LlvmValueHandle socketSlot, LlvmValueHandle statusSlot, LlvmValueHandle sockaddrBytes, LlvmBasicBlockHandle pendingBlock, LlvmBasicBlockHandle failBlock, LlvmBasicBlockHandle doneBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle connectSucceeded;
        LlvmValueHandle connectPending;
        if (IsLinuxFlavor(state.Flavor))
        {
            LlvmValueHandle connectResult = EmitLinuxSyscall(
                state,
                SyscallConnect,
                LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "step_tcp_connect_socket_value"),
                LlvmApi.BuildPtrToInt(builder, sockaddrBytes, state.I64, "step_tcp_connect_sockaddr_ptr"),
                LlvmApi.ConstInt(state.I64, 16, 0),
                "step_tcp_connect_call");
            connectSucceeded = LlvmApi.BuildOr(builder,
                LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, connectResult, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_connect_ok"),
                LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, connectResult, LlvmApi.ConstInt(state.I64, unchecked((ulong)LinuxErrIsConnected), 1), "step_tcp_connect_is_connected"),
                "step_tcp_connect_succeeded");
            connectPending = LlvmApi.BuildOr(builder,
                LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, connectResult, LlvmApi.ConstInt(state.I64, unchecked((ulong)LinuxErrInProgress), 1), "step_tcp_connect_in_progress"),
                LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, connectResult, LlvmApi.ConstInt(state.I64, unchecked((ulong)LinuxErrAlready), 1), "step_tcp_connect_already"),
                "step_tcp_connect_pending");
        }
        else
        {
            LlvmValueHandle connectOk = EmitWindowsConnect(state, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "step_tcp_connect_socket_value"), sockaddrBytes, "step_tcp_connect_call");
            LlvmValueHandle wsaError = LlvmApi.BuildSExt(builder, EmitWindowsWsaGetLastError(state, "step_tcp_connect_error"), state.I64, "step_tcp_connect_error_i64");
            connectSucceeded = LlvmApi.BuildOr(builder,
                connectOk,
                LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, wsaError, LlvmApi.ConstInt(state.I64, WindowsWsaErrorIsConnected, 0), "step_tcp_connect_is_connected"),
                "step_tcp_connect_succeeded");
            connectPending = LlvmApi.BuildOr(builder,
                LlvmApi.BuildOr(builder,
                    LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, wsaError, LlvmApi.ConstInt(state.I64, WindowsWsaErrorWouldBlock, 0), "step_tcp_connect_would_block"),
                    LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, wsaError, LlvmApi.ConstInt(state.I64, WindowsWsaErrorInProgress, 0), "step_tcp_connect_in_progress"),
                    "step_tcp_connect_pending_pair"),
                LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, wsaError, LlvmApi.ConstInt(state.I64, WindowsWsaErrorAlready, 0), "step_tcp_connect_already"),
                "step_tcp_connect_pending");
        }
        LlvmBasicBlockHandle successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_success");
        LlvmBasicBlockHandle pendingCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_pending_check");
        LlvmApi.BuildCondBr(builder, connectSucceeded, successBlock, pendingCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingCheckBlock);
        LlvmApi.BuildCondBr(builder, connectPending, pendingBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        LlvmApi.BuildStore(builder,
            EmitCompleteLeafTask(state, taskPtr, EmitResultOk(state, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "step_tcp_connect_socket_ok")), "step_tcp_connect_complete"),
            statusSlot);
        LlvmApi.BuildBr(builder, doneBlock);
    }

    private static void EmitStepTcpConnectTail(LlvmCodegenState state, LlvmValueHandle taskPtr, LlvmValueHandle socketSlot, LlvmValueHandle statusSlot, LlvmBasicBlockHandle pendingBlock, LlvmBasicBlockHandle failBlock, LlvmBasicBlockHandle doneBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, pendingBlock);
        LlvmApi.BuildStore(builder,
            EmitPendingLeafTask(state, taskPtr, TaskStructLayout.WaitSocketWrite, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "step_tcp_connect_pending_socket"), "step_tcp_connect_pending_store"),
            statusSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildStore(builder,
            EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TcpConnectFailedMessage)), "step_tcp_connect_fail_complete"),
            statusSlot);
        LlvmApi.BuildBr(builder, doneBlock);
    }

    private static LlvmValueHandle EmitStepTcpSendTask(LlvmCodegenState state, LlvmValueHandle taskPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle socket = LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, "step_tcp_send_socket");
        LlvmValueHandle textRef = LoadMemory(state, taskPtr, TaskStructLayout.IoArg1, "step_tcp_send_text");
        LlvmValueHandle sentSoFar = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tcp_send_sent_so_far");
        LlvmValueHandle totalLen = LoadStringLength(state, textRef, "step_tcp_send_total_len");
        LlvmValueHandle remaining = LlvmApi.BuildSub(builder, totalLen, sentSoFar, "step_tcp_send_remaining");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, remaining, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_send_done_bool");
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_send_status_slot");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmBasicBlockHandle alreadyDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_already_done");
        LlvmBasicBlockHandle sendBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_send");
        LlvmBasicBlockHandle finishBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_finish");
        LlvmBasicBlockHandle pendingCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_pending_check");
        LlvmBasicBlockHandle failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_fail");
        LlvmApi.BuildCondBr(builder, done, alreadyDoneBlock, sendBlock);

        LlvmApi.PositionBuilderAtEnd(builder, alreadyDoneBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultOk(state, totalLen), "step_tcp_send_already_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmValueHandle sentRaw = EmitStepTcpSendPerform(state, taskPtr, socket, textRef, sentSoFar, remaining, totalLen, statusSlot, sendBlock, finishBlock, pendingCheckBlock, failBlock);
        EmitStepTcpSendPendingCheck(state, taskPtr, socket, sentRaw, statusSlot, pendingCheckBlock, failBlock, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, statusSlot, "step_tcp_send_status");
    }

    private static LlvmValueHandle EmitStepTcpSendPerform(LlvmCodegenState state, LlvmValueHandle taskPtr, LlvmValueHandle socket, LlvmValueHandle textRef, LlvmValueHandle sentSoFar, LlvmValueHandle remaining, LlvmValueHandle totalLen, LlvmValueHandle statusSlot, LlvmBasicBlockHandle sendBlock, LlvmBasicBlockHandle finishBlock, LlvmBasicBlockHandle pendingCheckBlock, LlvmBasicBlockHandle failBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, sendBlock);
        LlvmValueHandle cursorPtr = LlvmApi.BuildIntToPtr(builder,
            LlvmApi.BuildAdd(builder, GetStringBytesAddress(state, textRef, "step_tcp_send_base"), sentSoFar, "step_tcp_send_cursor_addr"),
            state.I8Ptr,
            "step_tcp_send_cursor_ptr");
        LlvmValueHandle sentRaw = IsLinuxFlavor(state.Flavor)
            ? EmitLinuxSyscall(state, SyscallWrite, socket, LlvmApi.BuildPtrToInt(builder, cursorPtr, state.I64, "step_tcp_send_cursor_i64"), remaining, "step_tcp_send_call")
            : LlvmApi.BuildSExt(builder,
                EmitWindowsSend(state, socket, cursorPtr,
                    LlvmApi.BuildTrunc(builder,
                        LlvmApi.BuildSelect(builder,
                            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, remaining, LlvmApi.ConstInt(state.I64, int.MaxValue, 0), "step_tcp_send_limit_gt"),
                            LlvmApi.ConstInt(state.I64, int.MaxValue, 0),
                            remaining,
                            "step_tcp_send_chunk_len"),
                        state.I32,
                        "step_tcp_send_chunk_i32"),
                    "step_tcp_send_call"),
                state.I64,
                "step_tcp_send_sent_raw");
        LlvmValueHandle sentPositive = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, sentRaw, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_send_sent_positive");
        LlvmBasicBlockHandle sentBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_sent_block");
        LlvmApi.BuildCondBr(builder, sentPositive, sentBlock, pendingCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, sentBlock);
        LlvmValueHandle nextSent = LlvmApi.BuildAdd(builder, sentSoFar, sentRaw, "step_tcp_send_next_sent");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, nextSent, "step_tcp_send_store_sent");
        LlvmValueHandle sendDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, nextSent, totalLen, "step_tcp_send_send_done");
        LlvmBasicBlockHandle sendDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_send_done_block");
        LlvmBasicBlockHandle sendPendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_send_pending_block");
        LlvmApi.BuildCondBr(builder, sendDone, sendDoneBlock, sendPendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, sendDoneBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultOk(state, totalLen), "step_tcp_send_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, sendPendingBlock);
        LlvmApi.BuildStore(builder, EmitPendingLeafTask(state, taskPtr, TaskStructLayout.WaitSocketWrite, socket, "step_tcp_send_more_pending"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
        return sentRaw;
    }

    private static void EmitStepTcpSendPendingCheck(LlvmCodegenState state, LlvmValueHandle taskPtr, LlvmValueHandle socket, LlvmValueHandle sentRaw, LlvmValueHandle statusSlot, LlvmBasicBlockHandle pendingCheckBlock, LlvmBasicBlockHandle failBlock, LlvmBasicBlockHandle finishBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, pendingCheckBlock);
        LlvmValueHandle isPending;
        if (IsLinuxFlavor(state.Flavor))
        {
            isPending = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, sentRaw, LlvmApi.ConstInt(state.I64, unchecked((ulong)LinuxErrWouldBlock), 1), "step_tcp_send_linux_pending");
        }
        else
        {
            LlvmValueHandle wsaError = LlvmApi.BuildSExt(builder, EmitWindowsWsaGetLastError(state, "step_tcp_send_error"), state.I64, "step_tcp_send_error_i64");
            isPending = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, wsaError, LlvmApi.ConstInt(state.I64, WindowsWsaErrorWouldBlock, 0), "step_tcp_send_windows_pending");
        }
        LlvmBasicBlockHandle pendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_pending_block");
        LlvmApi.BuildCondBr(builder, isPending, pendingBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingBlock);
        LlvmApi.BuildStore(builder, EmitPendingLeafTask(state, taskPtr, TaskStructLayout.WaitSocketWrite, socket, "step_tcp_send_pending"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TcpSendFailedMessage)), "step_tcp_send_fail_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static LlvmValueHandle EmitStepTcpReceiveTask(LlvmCodegenState state, LlvmValueHandle taskPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle socket = LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, "step_tcp_receive_socket");
        LlvmValueHandle maxBytes = LoadMemory(state, taskPtr, TaskStructLayout.IoArg1, "step_tcp_receive_max");
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_receive_status_slot");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmValueHandle positiveMax = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, maxBytes, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_receive_positive_max");
        LlvmBasicBlockHandle allocateBufferBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_allocate_buffer");
        LlvmBasicBlockHandle failMaxBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_fail_max");
        LlvmBasicBlockHandle readBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_read");
        LlvmBasicBlockHandle finishBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_finish");
        LlvmBasicBlockHandle handleReadBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_handle_read");
        LlvmBasicBlockHandle pendingCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_pending_check");
        LlvmApi.BuildCondBr(builder, positiveMax, allocateBufferBlock, failMaxBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failMaxBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TcpInvalidMaxBytesMessage)), "step_tcp_receive_invalid_max"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        (LlvmValueHandle activeBuffer, LlvmValueHandle readCount) = EmitStepTcpReceiveBuffer(state, taskPtr, socket, maxBytes, allocateBufferBlock, readBlock, handleReadBlock, pendingCheckBlock);
        EmitStepTcpReceiveHandleRead(state, taskPtr, activeBuffer, readCount, statusSlot, handleReadBlock, finishBlock);
        EmitStepTcpReceivePendingCheck(state, taskPtr, socket, readCount, statusSlot, pendingCheckBlock, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, statusSlot, "step_tcp_receive_status");
    }

    private static (LlvmValueHandle ActiveBuffer, LlvmValueHandle ReadCount) EmitStepTcpReceiveBuffer(LlvmCodegenState state, LlvmValueHandle taskPtr, LlvmValueHandle socket, LlvmValueHandle maxBytes, LlvmBasicBlockHandle allocateBufferBlock, LlvmBasicBlockHandle readBlock, LlvmBasicBlockHandle handleReadBlock, LlvmBasicBlockHandle pendingCheckBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, allocateBufferBlock);
        LlvmValueHandle bufferRef = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tcp_receive_buffer_ref");
        LlvmValueHandle hasBuffer = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, bufferRef, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_receive_has_buffer");
        LlvmBasicBlockHandle reuseBufferBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_reuse_buffer");
        LlvmBasicBlockHandle createBufferBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_create_buffer");
        LlvmApi.BuildCondBr(builder, hasBuffer, reuseBufferBlock, createBufferBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createBufferBlock);
        LlvmValueHandle newBufferRef = EmitAllocDynamic(state, LlvmApi.BuildAdd(builder, maxBytes, LlvmApi.ConstInt(state.I64, 8, 0), "step_tcp_receive_buffer_size"));
        StoreMemory(state, newBufferRef, 0, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_receive_buffer_len_init");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, newBufferRef, "step_tcp_receive_store_buffer");
        LlvmApi.BuildBr(builder, readBlock);

        LlvmApi.PositionBuilderAtEnd(builder, reuseBufferBlock);
        LlvmApi.BuildBr(builder, readBlock);

        LlvmApi.PositionBuilderAtEnd(builder, readBlock);
        LlvmValueHandle activeBuffer = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tcp_receive_active_buffer");
        LlvmValueHandle readCount = IsLinuxFlavor(state.Flavor)
            ? EmitLinuxSyscall(state, SyscallRead, socket, LlvmApi.BuildPtrToInt(builder, GetStringBytesPointer(state, activeBuffer, "step_tcp_receive_bytes"), state.I64, "step_tcp_receive_bytes_i64"), maxBytes, "step_tcp_receive_call")
            : LlvmApi.BuildSExt(builder, EmitWindowsRecv(state, socket, GetStringBytesPointer(state, activeBuffer, "step_tcp_receive_bytes"), LlvmApi.BuildTrunc(builder, maxBytes, state.I32, "step_tcp_receive_max_i32"), "step_tcp_receive_call"), state.I64, "step_tcp_receive_call_i64");
        LlvmValueHandle readOk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, readCount, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_receive_ok");
        LlvmApi.BuildCondBr(builder, readOk, handleReadBlock, pendingCheckBlock);
        return (activeBuffer, readCount);
    }

    private static void EmitStepTcpReceiveHandleRead(LlvmCodegenState state, LlvmValueHandle taskPtr, LlvmValueHandle activeBuffer, LlvmValueHandle readCount, LlvmValueHandle statusSlot, LlvmBasicBlockHandle handleReadBlock, LlvmBasicBlockHandle finishBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, handleReadBlock);
        StoreMemory(state, activeBuffer, 0, readCount, "step_tcp_receive_store_len");
        LlvmValueHandle emptyRead = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, readCount, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_receive_empty");
        LlvmBasicBlockHandle successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_success");
        LlvmBasicBlockHandle validateUtf8Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_validate_utf8");
        LlvmApi.BuildCondBr(builder, emptyRead, successBlock, validateUtf8Block);

        LlvmApi.PositionBuilderAtEnd(builder, validateUtf8Block);
        LlvmValueHandle utf8Valid = EmitValidateUtf8(state, GetStringBytesPointer(state, activeBuffer, "step_tcp_receive_validate_bytes"), readCount, "step_tcp_receive_utf8");
        LlvmValueHandle validUtf8 = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, utf8Valid, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_receive_valid_utf8");
        LlvmBasicBlockHandle invalidUtf8Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_invalid_utf8");
        LlvmApi.BuildCondBr(builder, validUtf8, successBlock, invalidUtf8Block);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultOk(state, activeBuffer), "step_tcp_receive_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, invalidUtf8Block);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TcpInvalidUtf8Message)), "step_tcp_receive_invalid_utf8_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static void EmitStepTcpReceivePendingCheck(LlvmCodegenState state, LlvmValueHandle taskPtr, LlvmValueHandle socket, LlvmValueHandle readCount, LlvmValueHandle statusSlot, LlvmBasicBlockHandle pendingCheckBlock, LlvmBasicBlockHandle finishBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, pendingCheckBlock);
        LlvmValueHandle isPending;
        if (IsLinuxFlavor(state.Flavor))
        {
            isPending = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, readCount, LlvmApi.ConstInt(state.I64, unchecked((ulong)LinuxErrWouldBlock), 1), "step_tcp_receive_linux_pending");
        }
        else
        {
            LlvmValueHandle wsaError = LlvmApi.BuildSExt(builder, EmitWindowsWsaGetLastError(state, "step_tcp_receive_error"), state.I64, "step_tcp_receive_error_i64");
            isPending = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, wsaError, LlvmApi.ConstInt(state.I64, WindowsWsaErrorWouldBlock, 0), "step_tcp_receive_windows_pending");
        }
        LlvmBasicBlockHandle pendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_pending_block");
        LlvmBasicBlockHandle failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_fail_block");
        LlvmApi.BuildCondBr(builder, isPending, pendingBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingBlock);
        LlvmApi.BuildStore(builder, EmitPendingLeafTask(state, taskPtr, TaskStructLayout.WaitSocketRead, socket, "step_tcp_receive_pending"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TcpReceiveFailedMessage)), "step_tcp_receive_fail_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static LlvmValueHandle EmitStepTcpCloseTask(LlvmCodegenState state, LlvmValueHandle taskPtr)
    {
        return EmitCompleteLeafTask(
            state,
            taskPtr,
            EmitTcpClose(state, LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, "step_tcp_close_socket")),
            "step_tcp_close_complete");
    }

    /// <summary>
    /// Leaf step for Ashes.Net.Tcp.Server.listen(port): open a TCP socket, set SO_REUSEADDR, bind
    /// INADDR_ANY:port, listen, mark non-blocking, and complete with Ok(socket). All operations are
    /// synchronous (bind/listen do not block), so this never parks. Linux (x64/arm64) uses raw
    /// syscalls; Windows uses WSAStartup + winsock socket/bind/listen.
    /// </summary>
    private static LlvmValueHandle EmitStepTcpListenTask(LlvmCodegenState state, LlvmValueHandle taskPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        bool linux = IsLinuxFlavor(state.Flavor);
        LlvmValueHandle port = LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, "step_tcp_listen_port");
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_listen_status_slot");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);

        LlvmBasicBlockHandle afterSocketBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_listen_after_socket");
        LlvmBasicBlockHandle afterBindBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_listen_after_bind");
        LlvmBasicBlockHandle successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_listen_success");
        LlvmBasicBlockHandle failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_listen_fail");
        LlvmBasicBlockHandle finishBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_listen_finish");

        // Windows fork-based multi-reactor: if a shared listener handle has been published (by
        // forkWorkers, in the parent that bound it or a worker that inherited it), reuse it instead of
        // binding a second socket to the same port — Windows has no SO_REUSEPORT, so all reactors must
        // accept on the one shared listener.
        if (!linux)
        {
            LlvmBasicBlockHandle bindNormallyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_listen_bind_normally");
            LlvmBasicBlockHandle reuseSharedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_listen_reuse_shared");
            LlvmValueHandle sharedListener = LlvmApi.BuildLoad2(builder, state.I64, WorkerListenerGlobal(state), "step_tcp_listen_shared");
            LlvmApi.BuildCondBr(builder,
                LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, sharedListener, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_listen_have_shared"),
                reuseSharedBlock, bindNormallyBlock);
            LlvmApi.PositionBuilderAtEnd(builder, reuseSharedBlock);
            LlvmApi.BuildStore(builder,
                EmitCompleteLeafTask(state, taskPtr, EmitResultOk(state, sharedListener), "step_tcp_listen_reuse_complete"),
                statusSlot);
            LlvmApi.BuildBr(builder, finishBlock);
            LlvmApi.PositionBuilderAtEnd(builder, bindNormallyBlock);
        }

        (LlvmValueHandle socketFd, LlvmValueHandle socketSlot) = EmitStepTcpListenOpenSocket(state, linux, afterSocketBlock, failBlock);
        EmitStepTcpListenAfterSocket(state, linux, socketFd, port, afterSocketBlock, afterBindBlock, failBlock);
        EmitStepTcpListenAfterBind(state, linux, taskPtr, socketFd, socketSlot, statusSlot, afterBindBlock, successBlock, failBlock, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, statusSlot, "step_tcp_listen_status");
    }

    private static (LlvmValueHandle SocketFd, LlvmValueHandle SocketSlot) EmitStepTcpListenOpenSocket(LlvmCodegenState state, bool linux, LlvmBasicBlockHandle afterSocketBlock, LlvmBasicBlockHandle failBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle socketFd;
        if (linux)
        {
            socketFd = EmitLinuxSyscall(
                state,
                SyscallSocket,
                LlvmApi.ConstInt(state.I64, 2, 0),
                LlvmApi.ConstInt(state.I64, 1, 0),
                LlvmApi.ConstInt(state.I64, 0, 0),
                "step_tcp_listen_socket_call");
        }
        else
        {
            LlvmTypeHandle wsadataType = LlvmApi.ArrayType2(state.I8, 512);
            LlvmValueHandle wsadata = LlvmApi.BuildAlloca(builder, wsadataType, "step_tcp_listen_wsadata");
            EmitWindowsWsaStartup(state, GetArrayElementPointer(state, wsadataType, wsadata, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_listen_wsadata_ptr"), "step_tcp_listen_wsastartup");
            socketFd = EmitWindowsSocket(state, 2, 1, 6, "step_tcp_listen_socket_call");
        }
        LlvmValueHandle socketSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_listen_socket_slot");
        LlvmApi.BuildStore(builder, socketFd, socketSlot);
        LlvmApi.BuildCondBr(builder,
            linux
                ? LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, socketFd, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_listen_socket_ok")
                : LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, socketFd, LlvmApi.ConstInt(state.I64, unchecked((ulong)-1L), 0), "step_tcp_listen_socket_ok"),
            afterSocketBlock, failBlock);
        return (socketFd, socketSlot);
    }

    private static void EmitStepTcpListenReuseOpts(LlvmCodegenState state, bool linux, LlvmValueHandle socketFd)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        if (linux)
        {
            // SO_REUSEADDR (optname=2) and SO_REUSEPORT (optname=15), level SOL_SOCKET=1, optval=1 —
            // best effort, Linux only. SO_REUSEPORT lets several processes each bind this port, which
            // is what the fork-based multi-reactor (serveParallel) relies on for kernel-side accept
            // load-balancing; it is harmless for the single-process serve path.
            LlvmValueHandle optvalSlot = LlvmApi.BuildAlloca(builder, state.I32, "step_tcp_listen_optval");
            LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 1, 0), optvalSlot);
            _ = EmitLinuxSyscall6(
                state,
                SyscallSetsockopt,
                socketFd,
                LlvmApi.ConstInt(state.I64, 1, 0),
                LlvmApi.ConstInt(state.I64, 2, 0),
                LlvmApi.BuildPtrToInt(builder, optvalSlot, state.I64, "step_tcp_listen_optval_ptr"),
                LlvmApi.ConstInt(state.I64, 4, 0),
                LlvmApi.ConstInt(state.I64, 0, 0),
                "step_tcp_listen_setsockopt");
            _ = EmitLinuxSyscall6(
                state,
                SyscallSetsockopt,
                socketFd,
                LlvmApi.ConstInt(state.I64, 1, 0),
                LlvmApi.ConstInt(state.I64, 15, 0),
                LlvmApi.BuildPtrToInt(builder, optvalSlot, state.I64, "step_tcp_listen_optval_ptr2"),
                LlvmApi.ConstInt(state.I64, 4, 0),
                LlvmApi.ConstInt(state.I64, 0, 0),
                "step_tcp_listen_setsockopt_reuseport");
        }
    }

    private static void EmitStepTcpListenAfterSocket(LlvmCodegenState state, bool linux, LlvmValueHandle socketFd, LlvmValueHandle port, LlvmBasicBlockHandle afterSocketBlock, LlvmBasicBlockHandle afterBindBlock, LlvmBasicBlockHandle failBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, afterSocketBlock);
        EmitStepTcpListenReuseOpts(state, linux, socketFd);
        // sockaddr_in { family=AF_INET(2), port=htons(port), addr=INADDR_ANY(0) }
        LlvmTypeHandle sockaddrType = LlvmApi.ArrayType2(state.I8, 16);
        LlvmValueHandle sockaddrStorage = LlvmApi.BuildAlloca(builder, sockaddrType, "step_tcp_listen_sockaddr");
        LlvmValueHandle sockaddrBytes = GetArrayElementPointer(state, sockaddrType, sockaddrStorage, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_listen_sockaddr_bytes");
        LlvmTypeHandle i16 = LlvmApi.Int16TypeInContext(state.Target.Context);
        LlvmTypeHandle i16Ptr = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.BuildBitCast(builder, sockaddrBytes, state.I64Ptr, "step_tcp_listen_sockaddr_i64"));
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.BuildBitCast(builder,
            LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, [LlvmApi.ConstInt(state.I64, 8, 0)], "step_tcp_listen_sockaddr_tail"),
            state.I64Ptr,
            "step_tcp_listen_sockaddr_tail_i64"));
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(i16, 2, 0), LlvmApi.BuildBitCast(builder, sockaddrBytes, i16Ptr, "step_tcp_listen_family_ptr"));
        LlvmValueHandle listenPortPtr = LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, [LlvmApi.ConstInt(state.I64, 2, 0)], "step_tcp_listen_port_ptr_byte");
        LlvmApi.BuildStore(builder,
            LlvmApi.BuildTrunc(builder, EmitByteSwap16(state, port, "step_tcp_listen_port_network"), i16, "step_tcp_listen_port_i16"),
            LlvmApi.BuildBitCast(builder, listenPortPtr, i16Ptr, "step_tcp_listen_port_ptr"));
        LlvmValueHandle bindOk;
        if (linux)
        {
            LlvmValueHandle bindResult = EmitLinuxSyscall(
                state,
                SyscallBind,
                socketFd,
                LlvmApi.BuildPtrToInt(builder, sockaddrBytes, state.I64, "step_tcp_listen_sockaddr_ptr"),
                LlvmApi.ConstInt(state.I64, 16, 0),
                "step_tcp_listen_bind_call");
            bindOk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, bindResult, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_listen_bind_ok");
        }
        else
        {
            LlvmValueHandle bindResult = EmitWindowsBind(state, socketFd, sockaddrBytes, 16, "step_tcp_listen_bind_call");
            bindOk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, bindResult, LlvmApi.ConstInt(state.I32, 0, 0), "step_tcp_listen_bind_ok");
        }
        LlvmApi.BuildCondBr(builder, bindOk, afterBindBlock, failBlock);
    }

    private static void EmitStepTcpListenAfterBind(LlvmCodegenState state, bool linux, LlvmValueHandle taskPtr, LlvmValueHandle socketFd, LlvmValueHandle socketSlot, LlvmValueHandle statusSlot, LlvmBasicBlockHandle afterBindBlock, LlvmBasicBlockHandle successBlock, LlvmBasicBlockHandle failBlock, LlvmBasicBlockHandle finishBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, afterBindBlock);
        LlvmValueHandle listenOk;
        if (linux)
        {
            LlvmValueHandle listenResult = EmitLinuxSyscall(
                state,
                SyscallListen,
                socketFd,
                LlvmApi.ConstInt(state.I64, 128, 0),
                LlvmApi.ConstInt(state.I64, 0, 0),
                "step_tcp_listen_listen_call");
            listenOk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, listenResult, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_listen_listen_ok");
        }
        else
        {
            LlvmValueHandle listenResult = EmitWindowsListen(state, socketFd, 128, "step_tcp_listen_listen_call");
            listenOk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, listenResult, LlvmApi.ConstInt(state.I32, 0, 0), "step_tcp_listen_listen_ok");
        }
        LlvmApi.BuildCondBr(builder, listenOk, successBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        EmitSetSocketNonBlocking(state, socketFd, "step_tcp_listen_nonblocking");
        LlvmApi.BuildStore(builder,
            EmitCompleteLeafTask(state, taskPtr, EmitResultOk(state, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "step_tcp_listen_socket_ok_value")), "step_tcp_listen_complete"),
            statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildStore(builder,
            EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TcpListenFailedMessage)), "step_tcp_listen_fail_complete"),
            statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    /// <summary>
    /// Leaf step for Ashes.Net.Tcp.Server.forkWorkers(count): the parent forks (count - 1) child
    /// processes so `count` processes total each run their own reactor (the fork-based multi-reactor,
    /// serveParallel). Completes with Ok(worker index): 0 in the parent, 1..count-1 in the children.
    /// Separate address spaces make each reactor's scheduler state independent, so purity keeps the
    /// connections genuinely isolated. Synchronous (fork does not block), so this never parks. Linux
    /// only; on other targets it is a single process (Ok(0)).
    /// </summary>
    private static LlvmValueHandle EmitStepForkWorkersTask(LlvmCodegenState state, LlvmValueHandle taskPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle port = LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, "step_fork_workers_port");
        LlvmValueHandle requested = LoadMemory(state, taskPtr, TaskStructLayout.IoArg1, "step_fork_workers_count");
        LlvmValueHandle idxSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_fork_workers_idx");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), idxSlot);

        if (!IsLinuxFlavor(state.Flavor))
        {
            return EmitStepForkWorkersTaskWindows(state, taskPtr, port, requested);
        }

        // Install SIGINT/SIGTERM handlers before forking so every reactor inherits them (graceful
        // shutdown: the signal interrupts the parked accept, which then stops and returns Ok(())).
        EmitInstallShutdownHandlers(state);

        if (IsLinuxFlavor(state.Flavor))
        {
            EmitStepForkWorkersLoop(state, requested, idxSlot);
        }

        LlvmValueHandle idx = LlvmApi.BuildLoad2(builder, state.I64, idxSlot, "step_fork_workers_idx_val");
        return EmitCompleteLeafTask(state, taskPtr, EmitResultOk(state, idx), "step_fork_workers_complete");
    }

    // Linux fork loop for forkWorkers: spawns workers 1..count-1, each child setting its worker index
    // into idxSlot and PR_SET_PDEATHSIG; the parent records child pids for the shutdown drain.
    private static void EmitStepForkWorkersLoop(LlvmCodegenState state, LlvmValueHandle requested, LlvmValueHandle idxSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // A count <= 0 means "auto": the shared effective worker cap, so serve honors the same
        // --parallel-workers compile cap and withWorkers runtime override as Ashes.Task.Parallel (one
        // worker per online CPU by default, narrowed by either). The cap globals/fn are emitted
        // for programs that use forkWorkers even without Ashes.Task.Parallel.
        LlvmValueHandle autoCount = EmitEffectiveWorkerCap(state, "step_fork_workers");
        LlvmValueHandle count = LlvmApi.BuildSelect(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, requested, LlvmApi.ConstInt(state.I64, 0, 0), "step_fork_workers_auto"),
            autoCount, requested, "step_fork_workers_effective");

        LlvmValueHandle iSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_fork_workers_i");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), iSlot);

        LlvmBasicBlockHandle loopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_fork_workers_loop");
        LlvmBasicBlockHandle bodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_fork_workers_body");
        LlvmBasicBlockHandle childBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_fork_workers_child");
        LlvmBasicBlockHandle parentBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_fork_workers_parent");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_fork_workers_done");
        LlvmApi.BuildBr(builder, loopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBlock);
        LlvmValueHandle i = LlvmApi.BuildLoad2(builder, state.I64, iSlot, "step_fork_workers_i_val");
        LlvmApi.BuildCondBr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, i, count, "step_fork_workers_more"),
            bodyBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, bodyBlock);
        // fork() on x86-64 / clone(SIGCHLD, 0, 0) on arm64 (flags=17 required on arm64, ignored on x64).
        LlvmValueHandle pid = EmitLinuxSyscall(state, SyscallFork,
            LlvmApi.ConstInt(state.I64, 17, 0), LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), "step_fork_workers_fork");
        LlvmApi.BuildCondBr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, pid, LlvmApi.ConstInt(state.I64, 0, 0), "step_fork_workers_is_child"),
            childBlock, parentBlock);

        // Child: this process is worker `i`; stop forking and run its reactor. PR_SET_PDEATHSIG(1)
        // = SIGTERM(15) so a worker dies with its parent instead of lingering as an orphan reactor
        // when the main process exits or is killed.
        LlvmApi.PositionBuilderAtEnd(builder, childBlock);
        _ = EmitLinuxSyscall(state, SyscallPrctl,
            LlvmApi.ConstInt(state.I64, 1, 0), LlvmApi.ConstInt(state.I64, 15, 0), LlvmApi.ConstInt(state.I64, 0, 0), "step_fork_workers_pdeathsig");
        // A child inherits the pids of earlier siblings recorded by the parent's copy of the
        // count; it has no children of its own to forward to or reap, so zero it.
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), ChildCountGlobal(state));
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), IsWorkerGlobal(state));
        LlvmApi.BuildStore(builder, i, idxSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        EmitStepForkWorkersParent(state, i, pid, iSlot, loopBlock, parentBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
    }

    // Parent side of the fork loop: records the freshly forked child pid, advances the loop counter,
    // and either loops for the next worker or stops on fork failure.
    private static void EmitStepForkWorkersParent(LlvmCodegenState state, LlvmValueHandle i, LlvmValueHandle pid, LlvmValueHandle iSlot, LlvmBasicBlockHandle loopBlock, LlvmBasicBlockHandle parentBlock, LlvmBasicBlockHandle doneBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // Parent: record the child pid (for shutdown forwarding + reaping in the drain), move to
        // the next worker on success; on fork failure (pid < 0) stop spawning.
        LlvmBasicBlockHandle recordBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_fork_workers_record");
        LlvmBasicBlockHandle recordedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_fork_workers_recorded");
        LlvmApi.PositionBuilderAtEnd(builder, parentBlock);
        LlvmValueHandle childCountGlobal = ChildCountGlobal(state);
        LlvmValueHandle curCount = LlvmApi.BuildLoad2(builder, state.I64, childCountGlobal, "step_fork_workers_child_count");
        LlvmValueHandle canRecord = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, pid, LlvmApi.ConstInt(state.I64, 0, 0), "step_fork_workers_pid_ok"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, curCount, LlvmApi.ConstInt(state.I64, MaxTrackedChildren, 0), "step_fork_workers_slot_ok"),
            "step_fork_workers_can_record");
        LlvmApi.BuildCondBr(builder, canRecord, recordBlock, recordedBlock);
        LlvmApi.PositionBuilderAtEnd(builder, recordBlock);
        LlvmTypeHandle pidArrType = LlvmApi.ArrayType2(state.I64, MaxTrackedChildren);
        LlvmValueHandle pidSlot = LlvmApi.BuildGEP2(builder, state.I64,
            GetArrayElementPointer(state, pidArrType, ChildPidsGlobal(state), LlvmApi.ConstInt(state.I64, 0, 0), "step_fork_workers_pids_base"),
            [curCount], "step_fork_workers_pid_slot");
        LlvmApi.BuildStore(builder, pid, pidSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, curCount, LlvmApi.ConstInt(state.I64, 1, 0), "step_fork_workers_child_count_inc"), childCountGlobal);
        LlvmApi.BuildBr(builder, recordedBlock);
        LlvmApi.PositionBuilderAtEnd(builder, recordedBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, i, LlvmApi.ConstInt(state.I64, 1, 0), "step_fork_workers_next"), iSlot);
        LlvmApi.BuildCondBr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, pid, LlvmApi.ConstInt(state.I64, 0, 0), "step_fork_workers_failed"),
            doneBlock, loopBlock);
    }

    /// <summary>Stores the graceful-shutdown drain bound for this process; returns unit (0).</summary>
    private static LlvmValueHandle EmitSetDrainTimeout(LlvmCodegenState state, LlvmValueHandle ms)
    {
        LlvmApi.BuildStore(state.Target.Builder, ms, DrainTimeoutGlobal(state));
        return LlvmApi.ConstInt(state.I64, 0, 0);
    }

    // 1 in a multi-reactor worker process (set on the fork/relaunch worker path), 0 in the parent
    // and in single-reactor processes. Stop.stop uses it to signal the right process.
    private static LlvmValueHandle IsWorkerGlobal(LlvmCodegenState state) =>
        state.Target.GetOrAddNamedGlobal("__ashes_is_worker", () =>
        {
            LlvmValueHandle global = LlvmApi.AddGlobal(state.Target.Module, state.I64, "__ashes_is_worker");
            LlvmApi.SetInitializer(global, LlvmApi.ConstInt(state.I64, 0, 0));
            LlvmApi.SetLinkage(global, LlvmLinkage.Internal);
            return global;
        });

    /// <summary>
    /// Body of <c>Stop.stop(Unit)</c>: request graceful whole-server shutdown. Linux: send SIGTERM
    /// to the parent when this is a multi-reactor worker (the parent's drain forwards to every
    /// worker), else to this process itself — so programmatic stop rides exactly the signal path
    /// (handler, EINTR accept wake, drain, second-signal force). Windows: set this reactor's
    /// shutdown flag directly (observed within the 200 ms poll cap); the parent cannot forward to
    /// workers on Windows — same parity as signal shutdown there. Returns unit (0).
    /// </summary>
    private static LlvmValueHandle EmitRequestServerStop(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        if (IsWindowsFlavor(state.Flavor))
        {
            LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), ShutdownFlagGlobal(state));
            return LlvmApi.ConstInt(state.I64, 0, 0);
        }

        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle isWorker = LlvmApi.BuildLoad2(builder, state.I64, IsWorkerGlobal(state), "stop_is_worker");
        LlvmValueHandle selfPid = EmitLinuxSyscall(state, SyscallGetpid, zero, zero, zero, "stop_getpid");
        LlvmValueHandle parentPid = EmitLinuxSyscall(state, SyscallGetppid, zero, zero, zero, "stop_getppid");
        LlvmValueHandle targetPid = LlvmApi.BuildSelect(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, isWorker, zero, "stop_worker_cmp"),
            parentPid, selfPid, "stop_target_pid");
        _ = EmitLinuxSyscall(state, SyscallKill, targetPid, LlvmApi.ConstInt(state.I64, 15, 0), zero, "stop_kill");
        return LlvmApi.ConstInt(state.I64, 0, 0);
    }

    // The Windows console-ctrl handler runs on a separate thread: first event sets the shutdown
    // flag (the accept step's drain observes it within the capped WSAPoll timeout); a second event
    // during the drain calls ExitProcess(0) via the pointer stashed at install time. Returns TRUE
    // so the default terminator never runs.
    private static LlvmValueHandle ExitProcessFnGlobal(LlvmCodegenState state) =>
        state.Target.GetOrAddNamedGlobal("__ashes_exitprocess_fn", () =>
        {
            LlvmValueHandle global = LlvmApi.AddGlobal(state.Target.Module, state.I64, "__ashes_exitprocess_fn");
            LlvmApi.SetInitializer(global, LlvmApi.ConstInt(state.I64, 0, 0));
            LlvmApi.SetLinkage(global, LlvmLinkage.Internal);
            return global;
        });

    private static LlvmValueHandle EmitWindowsConsoleCtrlHandlerFn(LlvmCodegenState state)
    {
        LlvmValueHandle handlerFn = LlvmApi.GetNamedFunction(state.Target.Module, "__ashes_console_ctrl_handler");
        if (handlerFn.Ptr != 0)
        {
            return handlerFn;
        }

        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmBasicBlockHandle savedBlock = LlvmApi.GetInsertBlock(builder);
        handlerFn = LlvmApi.AddFunction(state.Target.Module, "__ashes_console_ctrl_handler", LlvmApi.FunctionType(state.I32, [state.I32]));
        LlvmApi.SetLinkage(handlerFn, LlvmLinkage.Internal);
        LlvmBasicBlockHandle entry = LlvmApi.AppendBasicBlockInContext(state.Target.Context, handlerFn, "entry");
        LlvmBasicBlockHandle forceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, handlerFn, "force_exit");
        LlvmBasicBlockHandle firstBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, handlerFn, "first_event");
        LlvmApi.PositionBuilderAtEnd(builder, entry);
        LlvmValueHandle already = LlvmApi.BuildLoad2(builder, state.I64, ShutdownFlagGlobal(state), "ctrl_already");
        LlvmApi.BuildCondBr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, already, LlvmApi.ConstInt(state.I64, 0, 0), "ctrl_is_second"),
            forceBlock, firstBlock);
        LlvmApi.PositionBuilderAtEnd(builder, forceBlock);
        LlvmValueHandle exitFnAddr = LlvmApi.BuildLoad2(builder, state.I64, ExitProcessFnGlobal(state), "ctrl_exit_fn");
        LlvmTypeHandle exitType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I32]);
        LlvmApi.BuildCall2(builder, exitType,
            LlvmApi.BuildIntToPtr(builder, exitFnAddr, LlvmApi.PointerTypeInContext(state.Target.Context, 0), "ctrl_exit_ptr"),
            [LlvmApi.ConstInt(state.I32, 0, 0)], "");
        LlvmApi.BuildRet(builder, LlvmApi.ConstInt(state.I32, 1, 0));
        LlvmApi.PositionBuilderAtEnd(builder, firstBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), ShutdownFlagGlobal(state));
        LlvmApi.BuildRet(builder, LlvmApi.ConstInt(state.I32, 1, 0));
        LlvmApi.PositionBuilderAtEnd(builder, savedBlock);
        return handlerFn;
    }

    // Bundles the kernel32 entry points and env-probe results the Windows forkWorkers path resolves
    // once up front and threads through its parent-side phase helpers.
    private readonly record struct StepForkWorkersWinFns(
        LlvmValueHandle EnvLen,
        LlvmValueHandle EnvBufPtr,
        LlvmValueHandle EnvName,
        LlvmValueHandle SetEnvFn,
        LlvmValueHandle GetModFn,
        LlvmValueHandle CreateProcFn,
        LlvmValueHandle GetSysInfoFn,
        LlvmValueHandle CreateJobFn,
        LlvmValueHandle SetJobInfoFn,
        LlvmValueHandle AssignJobFn);

    private static LlvmValueHandle EmitStepForkWorkersTaskWindows(LlvmCodegenState state, LlvmValueHandle taskPtr, LlvmValueHandle port, LlvmValueHandle requested)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        const string p = "step_fork_workers_win";

        StepForkWorkersWinFns fns = EmitStepForkWorkersWinResolveFns(state, p);

        LlvmBasicBlockHandle workerBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, p + "_worker");
        LlvmBasicBlockHandle parentBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, p + "_parent");
        LlvmBasicBlockHandle failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, p + "_fail");
        LlvmBasicBlockHandle finishBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, p + "_finish");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, p + "_result");
        LlvmApi.BuildCondBr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, fns.EnvLen, LlvmApi.ConstInt(state.I32, 0, 0), p + "_is_worker"),
            workerBlock, parentBlock);

        // Worker: adopt the inherited listener handle (parsed from the env var) and do not spawn.
        LlvmApi.PositionBuilderAtEnd(builder, workerBlock);
        LlvmValueHandle inherited = EmitCStringToI64(state, fns.EnvBufPtr, p + "_parse");
        LlvmApi.BuildStore(builder, inherited, WorkerListenerGlobal(state));
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), IsWorkerGlobal(state));
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultOk(state, LlvmApi.ConstInt(state.I64, 1, 0)), p + "_worker_complete"), resultSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmValueHandle listener = EmitStepForkWorkersWinParentSetup(state, p, fns, port, parentBlock, failBlock);
        (LlvmValueHandle exeBufPtr, LlvmValueHandle suPtr, LlvmValueHandle piPtr, LlvmValueHandle count) =
            EmitStepForkWorkersWinParentPrepare(state, p, fns, listener, requested);
        EmitStepForkWorkersWinParentSpawn(state, p, fns, taskPtr, resultSlot, exeBufPtr, suPtr, piPtr, count, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TcpListenFailedMessage)), p + "_fail_complete"), resultSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, p + "_status");
    }

    // Resolves the kernel32 entry points, installs the console-ctrl handler, and probes
    // ASHES_WORKER_FD (a non-empty value marks a relaunched worker).
    private static StepForkWorkersWinFns EmitStepForkWorkersWinResolveFns(LlvmCodegenState state, string p)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // Resolve the kernel32 entry points we need dynamically (keeps this self-contained — no extra
        // import wiring beyond what socket/listen already pull in).
        LlvmValueHandle kernel32 = EmitWindowsLoadLibrary(state, EmitStringToCString(state, EmitHeapStringLiteral(state, "KERNEL32.DLL"), p + "_k32_name"), p + "_k32");

        // Graceful shutdown: stash ExitProcess for the handler's force path, then install the
        // console-ctrl handler. Runs in the parent and in every relaunched worker.
        LlvmValueHandle exitProcFn = EmitWindowsGetProcAddress(state, kernel32, EmitStringToCString(state, EmitHeapStringLiteral(state, "ExitProcess"), p + "_exit_sym"), p + "_exit");
        LlvmApi.BuildStore(builder, LlvmApi.BuildPtrToInt(builder, exitProcFn, state.I64, p + "_exit_i64"), ExitProcessFnGlobal(state));
        LlvmValueHandle setCtrlFn = EmitWindowsGetProcAddress(state, kernel32, EmitStringToCString(state, EmitHeapStringLiteral(state, "SetConsoleCtrlHandler"), p + "_scc_sym"), p + "_scc");
        LlvmTypeHandle setCtrlType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I32]);
        LlvmValueHandle ctrlHandler = EmitWindowsConsoleCtrlHandlerFn(state);
        _ = EmitCallFunctionAddress(state, setCtrlFn, setCtrlType,
            [LlvmApi.BuildBitCast(builder, ctrlHandler, state.I8Ptr, p + "_handler_ptr"), LlvmApi.ConstInt(state.I32, 1, 0)], p + "_scc_call");
        LlvmValueHandle getEnvFn = EmitWindowsGetProcAddress(state, kernel32, EmitStringToCString(state, EmitHeapStringLiteral(state, "GetEnvironmentVariableA"), p + "_getenv_sym"), p + "_getenv");
        LlvmValueHandle setEnvFn = EmitWindowsGetProcAddress(state, kernel32, EmitStringToCString(state, EmitHeapStringLiteral(state, "SetEnvironmentVariableA"), p + "_setenv_sym"), p + "_setenv");
        LlvmValueHandle getModFn = EmitWindowsGetProcAddress(state, kernel32, EmitStringToCString(state, EmitHeapStringLiteral(state, "GetModuleFileNameA"), p + "_getmod_sym"), p + "_getmod");
        LlvmValueHandle createProcFn = EmitWindowsGetProcAddress(state, kernel32, EmitStringToCString(state, EmitHeapStringLiteral(state, "CreateProcessA"), p + "_cp_sym"), p + "_cp");
        LlvmValueHandle getSysInfoFn = EmitWindowsGetProcAddress(state, kernel32, EmitStringToCString(state, EmitHeapStringLiteral(state, "GetSystemInfo"), p + "_gsi_sym"), p + "_gsi");
        LlvmValueHandle createJobFn = EmitWindowsGetProcAddress(state, kernel32, EmitStringToCString(state, EmitHeapStringLiteral(state, "CreateJobObjectA"), p + "_cj_sym"), p + "_cj");
        LlvmValueHandle setJobInfoFn = EmitWindowsGetProcAddress(state, kernel32, EmitStringToCString(state, EmitHeapStringLiteral(state, "SetInformationJobObject"), p + "_sij_sym"), p + "_sij");
        LlvmValueHandle assignJobFn = EmitWindowsGetProcAddress(state, kernel32, EmitStringToCString(state, EmitHeapStringLiteral(state, "AssignProcessToJobObject"), p + "_apj_sym"), p + "_apj");
        LlvmValueHandle envName = EmitStringToCString(state, EmitHeapStringLiteral(state, "ASHES_WORKER_FD"), p + "_env_name");

        // Read ASHES_WORKER_FD; a non-empty value means this is a relaunched worker.
        LlvmTypeHandle envBufType = LlvmApi.ArrayType2(state.I8, 32);
        LlvmValueHandle envBuf = LlvmApi.BuildAlloca(builder, envBufType, p + "_env_buf");
        LlvmValueHandle envBufPtr = GetArrayElementPointer(state, envBufType, envBuf, LlvmApi.ConstInt(state.I64, 0, 0), p + "_env_buf_ptr");
        LlvmTypeHandle getEnvType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, state.I32]);
        LlvmValueHandle envLen = EmitCallFunctionAddress(state, getEnvFn, getEnvType, [envName, envBufPtr, LlvmApi.ConstInt(state.I32, 32, 0)], p + "_getenv_call");
        return new StepForkWorkersWinFns(envLen, envBufPtr, envName, setEnvFn, getModFn, createProcFn, getSysInfoFn, createJobFn, setJobInfoFn, assignJobFn);
    }

    // Parent: create the shared listener, publish it into the worker-listener global, and place this
    // process in a kill-on-close Job Object so relaunched workers die with it. Returns the listener.
    private static LlvmValueHandle EmitStepForkWorkersWinParentSetup(LlvmCodegenState state, string p, StepForkWorkersWinFns fns, LlvmValueHandle port, LlvmBasicBlockHandle parentBlock, LlvmBasicBlockHandle failBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmBasicBlockHandle spawnBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, p + "_spawn");
        LlvmApi.PositionBuilderAtEnd(builder, parentBlock);
        LlvmValueHandle listener = EmitWindowsCreateListenerSocket(state, port, p + "_listener");
        LlvmApi.BuildCondBr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, listener, LlvmApi.ConstInt(state.I64, 0, 0), p + "_listener_bad"),
            failBlock, spawnBlock);
        LlvmApi.PositionBuilderAtEnd(builder, spawnBlock);
        LlvmApi.BuildStore(builder, listener, WorkerListenerGlobal(state));

        // Orphan cleanup: put this process in a Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        // (0x2000, LimitFlags at offset 16 of JOBOBJECT_EXTENDED_LIMIT_INFORMATION). Workers spawned
        // below inherit the job, so they are terminated when this process exits — Windows has no
        // PR_SET_PDEATHSIG. The job handle is intentionally leaked: it must stay open for the life of
        // the process (closing it would trigger the kill).
        LlvmValueHandle job = EmitCallFunctionAddress(state, fns.CreateJobFn, LlvmApi.FunctionType(state.I64, [state.I8Ptr, state.I8Ptr]), [LlvmApi.ConstNull(state.I8Ptr), LlvmApi.ConstNull(state.I8Ptr)], p + "_create_job");
        LlvmTypeHandle eliType = LlvmApi.ArrayType2(state.I8, 112);
        LlvmValueHandle eli = LlvmApi.BuildAlloca(builder, eliType, p + "_eli");
        LlvmValueHandle eliPtr = GetArrayElementPointer(state, eliType, eli, LlvmApi.ConstInt(state.I64, 0, 0), p + "_eli_ptr");
        for (int zi = 0; zi < 112 / 8; zi++)
        {
            StoreMemory(state, eliPtr, zi * 8, LlvmApi.ConstInt(state.I64, 0, 0), $"{p}_eli_zero_{zi}");
        }

        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0x2000, 0), LlvmApi.BuildBitCast(builder, LlvmApi.BuildGEP2(builder, state.I8, eliPtr, [LlvmApi.ConstInt(state.I64, 16, 0)], p + "_limitflags_off"), state.I32Ptr, p + "_limitflags"));
        EmitCallFunctionAddress(state, fns.SetJobInfoFn, LlvmApi.FunctionType(state.I32, [state.I64, state.I32, state.I8Ptr, state.I32]), [job, LlvmApi.ConstInt(state.I32, 9, 0), eliPtr, LlvmApi.ConstInt(state.I32, 112, 0)], p + "_set_job_info");
        EmitCallFunctionAddress(state, fns.AssignJobFn, LlvmApi.FunctionType(state.I32, [state.I64, state.I64]), [job, LlvmApi.ConstInt(state.I64, unchecked((ulong)-1L), 1)], p + "_assign_job");
        return listener;
    }

    // Parent: compute the worker count, publish the listener handle into the child env, and lay out
    // the exe path + STARTUPINFOA/PROCESS_INFORMATION buffers for the relaunch loop.
    private static (LlvmValueHandle ExeBufPtr, LlvmValueHandle SuPtr, LlvmValueHandle PiPtr, LlvmValueHandle Count) EmitStepForkWorkersWinParentPrepare(LlvmCodegenState state, string p, StepForkWorkersWinFns fns, LlvmValueHandle listener, LlvmValueHandle requested)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // count = requested > 0 ? requested : GetSystemInfo().dwNumberOfProcessors (offset 32), min 1.
        LlvmTypeHandle sysInfoType = LlvmApi.ArrayType2(state.I8, 64);
        LlvmValueHandle sysInfo = LlvmApi.BuildAlloca(builder, sysInfoType, p + "_sysinfo");
        LlvmValueHandle sysInfoPtr = GetArrayElementPointer(state, sysInfoType, sysInfo, LlvmApi.ConstInt(state.I64, 0, 0), p + "_sysinfo_ptr");
        EmitCallFunctionAddress(state, fns.GetSysInfoFn, LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr]), [sysInfoPtr], "");
        LlvmValueHandle detected = LlvmApi.BuildAnd(builder, LoadMemory(state, sysInfoPtr, 32, p + "_nproc_packed"), LlvmApi.ConstInt(state.I64, 0xFFFFFFFFUL, 0), p + "_nproc");
        // Honor the --parallel-workers compile cap (same as Linux via EmitEffectiveWorkerCap): a fixed
        // cap overrides detection; otherwise one worker per online CPU. Floor at 1.
        LlvmValueHandle capBase = state.Target.ParallelWorkerCap is { } fixedCap
            ? LlvmApi.ConstInt(state.I64, (ulong)fixedCap, 0)
            : detected;
        LlvmValueHandle autoCount = LlvmApi.BuildSelect(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, capBase, LlvmApi.ConstInt(state.I64, 0, 0), p + "_cap_zero"), LlvmApi.ConstInt(state.I64, 1, 0), capBase, p + "_auto");
        LlvmValueHandle count = LlvmApi.BuildSelect(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, requested, LlvmApi.ConstInt(state.I64, 0, 0), p + "_req_auto"), autoCount, requested, p + "_count");

        // Publish the handle for children (they inherit the parent env): SetEnvironmentVariableA.
        LlvmTypeHandle handleStrType = LlvmApi.ArrayType2(state.I8, 24);
        LlvmValueHandle handleStr = LlvmApi.BuildAlloca(builder, handleStrType, p + "_handle_str");
        LlvmValueHandle handleStrPtr = GetArrayElementPointer(state, handleStrType, handleStr, LlvmApi.ConstInt(state.I64, 0, 0), p + "_handle_str_ptr");
        EmitI64ToCString(state, listener, handleStrPtr, p + "_itoa");
        EmitCallFunctionAddress(state, fns.SetEnvFn, LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr]), [fns.EnvName, handleStrPtr], p + "_setenv_call");

        // exe path for the relaunch command line.
        LlvmTypeHandle exeType = LlvmApi.ArrayType2(state.I8, 520);
        LlvmValueHandle exeBuf = LlvmApi.BuildAlloca(builder, exeType, p + "_exe");
        LlvmValueHandle exeBufPtr = GetArrayElementPointer(state, exeType, exeBuf, LlvmApi.ConstInt(state.I64, 0, 0), p + "_exe_ptr");
        EmitCallFunctionAddress(state, fns.GetModFn, LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, state.I32]), [LlvmApi.ConstNull(state.I8Ptr), exeBufPtr, LlvmApi.ConstInt(state.I32, 520, 0)], p + "_getmod_call");

        // STARTUPINFOA (104 bytes, cb at 0) and PROCESS_INFORMATION (24 bytes), zeroed.
        LlvmTypeHandle suType = LlvmApi.ArrayType2(state.I8, 104);
        LlvmValueHandle suBuf = LlvmApi.BuildAlloca(builder, suType, p + "_startupinfo");
        LlvmValueHandle suPtr = GetArrayElementPointer(state, suType, suBuf, LlvmApi.ConstInt(state.I64, 0, 0), p + "_startupinfo_ptr");
        for (int zi = 0; zi < 104 / 8; zi++)
        {
            StoreMemory(state, suPtr, zi * 8, LlvmApi.ConstInt(state.I64, 0, 0), $"{p}_su_zero_{zi}");
        }

        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 104, 0), LlvmApi.BuildBitCast(builder, suPtr, state.I32Ptr, p + "_su_cb"));
        LlvmTypeHandle piType = LlvmApi.ArrayType2(state.I8, 24);
        LlvmValueHandle piBuf = LlvmApi.BuildAlloca(builder, piType, p + "_procinfo");
        LlvmValueHandle piPtr = GetArrayElementPointer(state, piType, piBuf, LlvmApi.ConstInt(state.I64, 0, 0), p + "_procinfo_ptr");
        return (exeBufPtr, suPtr, piPtr, count);
    }

    // Parent: relaunch (count - 1) worker processes via CreateProcessA, then complete with Ok(0).
    private static void EmitStepForkWorkersWinParentSpawn(LlvmCodegenState state, string p, StepForkWorkersWinFns fns, LlvmValueHandle taskPtr, LlvmValueHandle resultSlot, LlvmValueHandle exeBufPtr, LlvmValueHandle suPtr, LlvmValueHandle piPtr, LlvmValueHandle count, LlvmBasicBlockHandle finishBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // CreateProcessA(NULL, exe, NULL, NULL, TRUE, 0, NULL, NULL, &si, &pi) for i in 1..count-1.
        LlvmTypeHandle cpType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, state.I8Ptr, state.I8Ptr, state.I32, state.I32, state.I8Ptr, state.I8Ptr, state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle iSlot = LlvmApi.BuildAlloca(builder, state.I64, p + "_i");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), iSlot);
        LlvmBasicBlockHandle spawnLoop = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, p + "_spawn_loop");
        LlvmBasicBlockHandle spawnBody = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, p + "_spawn_body");
        LlvmBasicBlockHandle spawnDone = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, p + "_spawn_done");
        LlvmApi.BuildBr(builder, spawnLoop);
        LlvmApi.PositionBuilderAtEnd(builder, spawnLoop);
        LlvmValueHandle i = LlvmApi.BuildLoad2(builder, state.I64, iSlot, p + "_i_val");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, i, count, p + "_more"), spawnBody, spawnDone);
        LlvmApi.PositionBuilderAtEnd(builder, spawnBody);
        EmitCallFunctionAddress(state, fns.CreateProcFn, cpType,
            [LlvmApi.ConstNull(state.I8Ptr), exeBufPtr, LlvmApi.ConstNull(state.I8Ptr), LlvmApi.ConstNull(state.I8Ptr), LlvmApi.ConstInt(state.I32, 1, 0), LlvmApi.ConstInt(state.I32, 0, 0), LlvmApi.ConstNull(state.I8Ptr), LlvmApi.ConstNull(state.I8Ptr), suPtr, piPtr],
            p + "_cp_call");
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, i, LlvmApi.ConstInt(state.I64, 1, 0), p + "_i_inc"), iSlot);
        LlvmApi.BuildBr(builder, spawnLoop);
        LlvmApi.PositionBuilderAtEnd(builder, spawnDone);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultOk(state, LlvmApi.ConstInt(state.I64, 0, 0)), p + "_parent_complete"), resultSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    /// <summary>
    /// Leaf step for Ashes.Net.Tcp.Server.accept(listener): accept one connection (accept4 with
    /// SOCK_NONBLOCK). On success completes with Ok(client socket); when no connection is ready
    /// (EWOULDBLOCK) parks on WaitSocketRead of the listener; otherwise completes with an error.
    /// Linux uses accept4(SOCK_NONBLOCK); Windows uses winsock accept + WSAPoll readiness.
    /// </summary>
    private static LlvmValueHandle EmitStepTcpAcceptTask(LlvmCodegenState state, LlvmValueHandle taskPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        bool linux = IsLinuxFlavor(state.Flavor);
        LlvmValueHandle listener = LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, "step_tcp_accept_listener");
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_accept_status_slot");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);

        LlvmBasicBlockHandle successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_accept_success");
        LlvmBasicBlockHandle pendingCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_accept_pending_check");
        LlvmBasicBlockHandle pendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_accept_pending");
        LlvmBasicBlockHandle failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_accept_fail");
        LlvmBasicBlockHandle finishBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_accept_finish");

        // Graceful shutdown: a SIGINT/SIGTERM sets the flag and interrupts the parked accept (EINTR),
        // which re-steps here. The step then DRAINS instead of completing at once: accepting stops
        // (this path never reaches the accept call), and the sentinel completion is held back until
        // every live spawned handler has finished or the drain deadline (default 10 s) passes. In
        // between, the accept task parks as a short timer; each scheduler wakeup re-steps it (the
        // aggregate wait re-queues all parked leaves), so the counter is re-checked as handlers
        // complete. serve then stops and returns Ok(()).
        LlvmBasicBlockHandle shutdownBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_accept_shutdown");
        LlvmBasicBlockHandle drainArmBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_accept_drain_arm");
        LlvmBasicBlockHandle drainCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_accept_drain_check");
        LlvmBasicBlockHandle drainWaitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_accept_drain_wait");
        LlvmBasicBlockHandle shutdownDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_accept_shutdown_done");
        LlvmBasicBlockHandle acceptGoBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_accept_go");
        LlvmValueHandle shuttingDown = LlvmApi.BuildLoad2(builder, state.I64, ShutdownFlagGlobal(state), "step_tcp_accept_shutdown_flag");
        LlvmApi.BuildCondBr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, shuttingDown, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_accept_is_shutdown"),
            shutdownBlock, acceptGoBlock);

        LlvmApi.PositionBuilderAtEnd(builder, shutdownBlock);
        LlvmValueHandle nowMs = EmitMonotonicNowMs(state, "step_tcp_accept_drain_now");
        LlvmValueHandle deadline0 = LlvmApi.BuildLoad2(builder, state.I64, DrainDeadlineGlobal(state), "step_tcp_accept_deadline0");
        LlvmApi.BuildCondBr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, deadline0, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_accept_drain_unarmed"),
            drainArmBlock, drainCheckBlock);

        EmitStepTcpAcceptDrainArm(state, linux, nowMs, drainArmBlock, drainCheckBlock);
        EmitStepTcpAcceptDrainCheck(state, linux, nowMs, drainCheckBlock, shutdownDoneBlock, drainWaitBlock);
        EmitStepTcpAcceptShutdownComplete(state, taskPtr, statusSlot, drainWaitBlock, shutdownDoneBlock, finishBlock);
        LlvmValueHandle clientFd = EmitStepTcpAcceptGo(state, linux, listener, taskPtr, statusSlot, acceptGoBlock, successBlock, pendingCheckBlock, finishBlock);
        EmitStepTcpAcceptPending(state, linux, listener, taskPtr, statusSlot, clientFd, pendingCheckBlock, pendingBlock, failBlock, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, statusSlot, "step_tcp_accept_status");
    }

    // First observation of the shutdown flag: arm the drain deadline and, in the multi-reactor
    // parent, forward the signal to every recorded worker so they begin their own drains.
    private static void EmitStepTcpAcceptDrainArm(LlvmCodegenState state, bool linux, LlvmValueHandle nowMs, LlvmBasicBlockHandle drainArmBlock, LlvmBasicBlockHandle drainCheckBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // First observation of the flag: arm the deadline = now + bound, and (multi-reactor parent
        // only) forward the signal to every recorded worker so they begin their own drains instead
        // of being cut by the death signal when the parent exits.
        LlvmApi.PositionBuilderAtEnd(builder, drainArmBlock);
        LlvmValueHandle bound = LlvmApi.BuildLoad2(builder, state.I64, DrainTimeoutGlobal(state), "step_tcp_accept_drain_bound");
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, nowMs, bound, "step_tcp_accept_deadline_new"), DrainDeadlineGlobal(state));
        if (linux)
        {
            LlvmTypeHandle pidArrType = LlvmApi.ArrayType2(state.I64, MaxTrackedChildren);
            LlvmValueHandle pidsBase = GetArrayElementPointer(state, pidArrType, ChildPidsGlobal(state), LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_accept_fwd_base");
            LlvmValueHandle childCount = LlvmApi.BuildLoad2(builder, state.I64, ChildCountGlobal(state), "step_tcp_accept_fwd_count");
            LlvmValueHandle fwdISlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_accept_fwd_i");
            LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), fwdISlot);
            LlvmBasicBlockHandle fwdLoop = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_accept_fwd_loop");
            LlvmBasicBlockHandle fwdBody = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_accept_fwd_body");
            LlvmBasicBlockHandle fwdDone = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_accept_fwd_done");
            LlvmApi.BuildBr(builder, fwdLoop);
            LlvmApi.PositionBuilderAtEnd(builder, fwdLoop);
            LlvmValueHandle fwdI = LlvmApi.BuildLoad2(builder, state.I64, fwdISlot, "step_tcp_accept_fwd_i_val");
            LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, fwdI, childCount, "step_tcp_accept_fwd_more"), fwdBody, fwdDone);
            LlvmApi.PositionBuilderAtEnd(builder, fwdBody);
            LlvmValueHandle fwdPid = LlvmApi.BuildLoad2(builder, state.I64, LlvmApi.BuildGEP2(builder, state.I64, pidsBase, [fwdI], "step_tcp_accept_fwd_slot"), "step_tcp_accept_fwd_pid");
            _ = EmitLinuxSyscall(state, SyscallKill, fwdPid, LlvmApi.ConstInt(state.I64, 15, 0), LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_accept_fwd_kill");
            LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, fwdI, LlvmApi.ConstInt(state.I64, 1, 0), "step_tcp_accept_fwd_next"), fwdISlot);
            LlvmApi.BuildBr(builder, fwdLoop);
            LlvmApi.PositionBuilderAtEnd(builder, fwdDone);
        }
        LlvmApi.BuildBr(builder, drainCheckBlock);
    }

    // Drain check: reap exited workers and complete the shutdown once every live spawned handler and
    // worker has finished or the drain deadline passes; otherwise park briefly and re-check.
    private static void EmitStepTcpAcceptDrainCheck(LlvmCodegenState state, bool linux, LlvmValueHandle nowMs, LlvmBasicBlockHandle drainCheckBlock, LlvmBasicBlockHandle shutdownDoneBlock, LlvmBasicBlockHandle drainWaitBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, drainCheckBlock);
        // Multi-reactor parent: reap exited workers (waitpid WNOHANG), compacting by swapping the
        // last slot down; the drain also requires the recorded child count to reach zero.
        if (linux)
        {
            LlvmTypeHandle pidArrType = LlvmApi.ArrayType2(state.I64, MaxTrackedChildren);
            LlvmValueHandle pidsBase = GetArrayElementPointer(state, pidArrType, ChildPidsGlobal(state), LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_accept_reap_base");
            LlvmValueHandle reapISlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_accept_reap_i");
            LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), reapISlot);
            LlvmBasicBlockHandle reapLoop = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_accept_reap_loop");
            LlvmBasicBlockHandle reapBody = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_accept_reap_body");
            LlvmBasicBlockHandle reapHit = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_accept_reap_hit");
            LlvmBasicBlockHandle reapNext = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_accept_reap_next");
            LlvmBasicBlockHandle reapDone = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_accept_reap_done");
            LlvmApi.BuildBr(builder, reapLoop);
            LlvmApi.PositionBuilderAtEnd(builder, reapLoop);
            LlvmValueHandle reapI = LlvmApi.BuildLoad2(builder, state.I64, reapISlot, "step_tcp_accept_reap_i_val");
            LlvmValueHandle reapCount = LlvmApi.BuildLoad2(builder, state.I64, ChildCountGlobal(state), "step_tcp_accept_reap_count");
            LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, reapI, reapCount, "step_tcp_accept_reap_more"), reapBody, reapDone);
            LlvmApi.PositionBuilderAtEnd(builder, reapBody);
            LlvmValueHandle reapSlot = LlvmApi.BuildGEP2(builder, state.I64, pidsBase, [reapI], "step_tcp_accept_reap_slot");
            LlvmValueHandle reapPid = LlvmApi.BuildLoad2(builder, state.I64, reapSlot, "step_tcp_accept_reap_pid");
            // wait4(pid, NULL, WNOHANG=1, NULL); returns the pid when the child has exited.
            LlvmValueHandle waited = EmitLinuxSyscall4(state, SyscallWaitpid, reapPid, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 1, 0), LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_accept_reap_wait");
            LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, waited, reapPid, "step_tcp_accept_reap_exited"), reapHit, reapNext);
            LlvmApi.PositionBuilderAtEnd(builder, reapHit);
            LlvmValueHandle lastIdx = LlvmApi.BuildSub(builder, reapCount, LlvmApi.ConstInt(state.I64, 1, 0), "step_tcp_accept_reap_last");
            LlvmValueHandle lastPid = LlvmApi.BuildLoad2(builder, state.I64, LlvmApi.BuildGEP2(builder, state.I64, pidsBase, [lastIdx], "step_tcp_accept_reap_last_slot"), "step_tcp_accept_reap_last_pid");
            LlvmApi.BuildStore(builder, lastPid, reapSlot);
            LlvmApi.BuildStore(builder, lastIdx, ChildCountGlobal(state));
            // Re-check the same index (now holding the swapped-down pid) on the next pass.
            LlvmApi.BuildBr(builder, reapLoop);
            LlvmApi.PositionBuilderAtEnd(builder, reapNext);
            LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, reapI, LlvmApi.ConstInt(state.I64, 1, 0), "step_tcp_accept_reap_inc"), reapISlot);
            LlvmApi.BuildBr(builder, reapLoop);
            LlvmApi.PositionBuilderAtEnd(builder, reapDone);
        }
        LlvmValueHandle deadline = LlvmApi.BuildLoad2(builder, state.I64, DrainDeadlineGlobal(state), "step_tcp_accept_deadline");
        LlvmValueHandle live = LlvmApi.BuildLoad2(builder, state.I64, LiveSpawnedGlobal(state), "step_tcp_accept_live");
        LlvmValueHandle allWorkDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, live, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_accept_no_live");
        if (linux)
        {
            LlvmValueHandle remainingChildren = LlvmApi.BuildLoad2(builder, state.I64, ChildCountGlobal(state), "step_tcp_accept_children_left");
            allWorkDone = LlvmApi.BuildAnd(builder, allWorkDone,
                LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, remainingChildren, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_accept_no_children"),
                "step_tcp_accept_all_done");
        }
        LlvmValueHandle drained = LlvmApi.BuildOr(builder,
            allWorkDone,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, nowMs, deadline, "step_tcp_accept_deadline_hit"),
            "step_tcp_accept_drained");
        LlvmApi.BuildCondBr(builder, drained, shutdownDoneBlock, drainWaitBlock);
    }

    // Emits the drain-wait park (short timer re-check) and the shutdown-done sentinel completion.
    private static void EmitStepTcpAcceptShutdownComplete(LlvmCodegenState state, LlvmValueHandle taskPtr, LlvmValueHandle statusSlot, LlvmBasicBlockHandle drainWaitBlock, LlvmBasicBlockHandle shutdownDoneBlock, LlvmBasicBlockHandle finishBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // Still draining: park as a short timer so the counter is re-checked promptly even with no
        // other wakeups; any handler I/O or timer wakeup re-steps this leaf sooner.
        LlvmApi.PositionBuilderAtEnd(builder, drainWaitBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.WaitKind, LlvmApi.ConstInt(state.I64, TaskStructLayout.WaitTimer, 0), "step_tcp_accept_drain_wait_kind");
        StoreMemory(state, taskPtr, TaskStructLayout.SleepDurationMs, LlvmApi.ConstInt(state.I64, 50, 0), "step_tcp_accept_drain_wait_slice");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, shutdownDoneBlock);
        LlvmApi.BuildStore(builder,
            EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, ServerShutdownSentinel)), "step_tcp_accept_shutdown_complete"),
            statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    // Accept path: accept one connection; on success complete with Ok(clientFd), else fall through to
    // the pending/fail check. Returns the raw accept result for that check.
    private static LlvmValueHandle EmitStepTcpAcceptGo(LlvmCodegenState state, bool linux, LlvmValueHandle listener, LlvmValueHandle taskPtr, LlvmValueHandle statusSlot, LlvmBasicBlockHandle acceptGoBlock, LlvmBasicBlockHandle successBlock, LlvmBasicBlockHandle pendingCheckBlock, LlvmBasicBlockHandle finishBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, acceptGoBlock);
        LlvmValueHandle clientFd;
        LlvmValueHandle acceptOk;
        if (linux)
        {
            // accept4(listener, NULL, NULL, SOCK_NONBLOCK) — client inherits non-blocking.
            clientFd = EmitLinuxSyscall4(
                state,
                SyscallAccept4,
                listener,
                LlvmApi.ConstInt(state.I64, 0, 0),
                LlvmApi.ConstInt(state.I64, 0, 0),
                LlvmApi.ConstInt(state.I64, 2048, 0),
                "step_tcp_accept_call");
            acceptOk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, clientFd, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_accept_ok");
        }
        else
        {
            clientFd = EmitWindowsAccept(state, listener, "step_tcp_accept_call");
            acceptOk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, clientFd, LlvmApi.ConstInt(state.I64, unchecked((ulong)-1L), 0), "step_tcp_accept_ok");
        }
        LlvmApi.BuildCondBr(builder, acceptOk, successBlock, pendingCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        if (!linux)
        {
            // Windows accepted sockets do not reliably inherit non-blocking mode; set it explicitly.
            EmitSetSocketNonBlocking(state, clientFd, "step_tcp_accept_client_nonblocking");
        }
        LlvmApi.BuildStore(builder,
            EmitCompleteLeafTask(state, taskPtr, EmitResultOk(state, clientFd), "step_tcp_accept_complete"),
            statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
        return clientFd;
    }

    // Pending/fail check for accept: park on WaitSocketRead of the listener when the accept would
    // block, otherwise complete with an error.
    private static void EmitStepTcpAcceptPending(LlvmCodegenState state, bool linux, LlvmValueHandle listener, LlvmValueHandle taskPtr, LlvmValueHandle statusSlot, LlvmValueHandle clientFd, LlvmBasicBlockHandle pendingCheckBlock, LlvmBasicBlockHandle pendingBlock, LlvmBasicBlockHandle failBlock, LlvmBasicBlockHandle finishBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, pendingCheckBlock);
        LlvmValueHandle wouldBlock;
        if (linux)
        {
            wouldBlock = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, clientFd, LlvmApi.ConstInt(state.I64, unchecked((ulong)LinuxErrWouldBlock), 1), "step_tcp_accept_would_block");
        }
        else
        {
            LlvmValueHandle wsaError = LlvmApi.BuildSExt(builder, EmitWindowsWsaGetLastError(state, "step_tcp_accept_error"), state.I64, "step_tcp_accept_error_i64");
            wouldBlock = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, wsaError, LlvmApi.ConstInt(state.I64, WindowsWsaErrorWouldBlock, 0), "step_tcp_accept_would_block");
        }
        LlvmApi.BuildCondBr(builder, wouldBlock, pendingBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingBlock);
        LlvmApi.BuildStore(builder,
            EmitPendingLeafTask(state, taskPtr, TaskStructLayout.WaitSocketRead, listener, "step_tcp_accept_pending_store"),
            statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildStore(builder,
            EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TcpAcceptFailedMessage)), "step_tcp_accept_fail_complete"),
            statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static LlvmValueHandle EmitStepTlsConnectTask(LlvmCodegenState state, LlvmValueHandle taskPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tls_connect_status_slot");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);

        LlvmBasicBlockHandle connectStageBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_connect_connect_stage");
        LlvmBasicBlockHandle handshakeStageBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_connect_handshake_stage");
        LlvmBasicBlockHandle finishBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_connect_finish");

        LlvmValueHandle stage = LoadMemory(state, taskPtr, TaskStructLayout.WaitData1, "step_tls_connect_stage");
        LlvmValueHandle isHandshakeStage = LlvmApi.BuildICmp(
            builder,
            LlvmIntPredicate.Eq,
            stage,
            LlvmApi.ConstInt(state.I64, 1, 0),
            "step_tls_connect_is_handshake_stage");
        LlvmApi.BuildCondBr(builder, isHandshakeStage, handshakeStageBlock, connectStageBlock);

        EmitTlsConnectTcpStage(state, connectStageBlock, taskPtr, finishBlock, statusSlot);
        EmitTlsConnectHandshakeStage(state, handshakeStageBlock, taskPtr, finishBlock, statusSlot);

        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, statusSlot, "step_tls_connect_status");
    }

    /// <summary>
    /// Steps a TLS handshake leaf task. <paramref name="serverSide"/> selects which side's session
    /// is created on first entry: the client side builds a connection from the client context and
    /// the server name in IoArg1; the server side builds (and caches, process-wide) a server config
    /// from the certificate-chain PEM in IoArg1 and the private-key PEM in WaitData1, then creates a
    /// server connection. Everything after session creation — the write/read/process handshake loop,
    /// the WaitTlsWantRead/Write parking, and completion — is identical for both sides.
    /// </summary>
    // Slots and basic blocks shared across the TLS handshake step's phase helpers. Allocated once by
    // the prologue and threaded through the phases so each keeps its verbatim block body.
    private readonly record struct TlsHandshakeCtx(
        LlvmValueHandle InitStatus,
        LlvmValueHandle StatusSlot,
        LlvmValueHandle ErrorCodeSlot,
        LlvmValueHandle ErrorStageSlot,
        LlvmValueHandle CreateStatusSlot,
        LlvmValueHandle ConnectionHandleSlot,
        LlvmBasicBlockHandle InitFailBlock,
        LlvmBasicBlockHandle SessionCheckBlock,
        LlvmBasicBlockHandle CreateBlock,
        LlvmBasicBlockHandle StoreSessionBlock,
        LlvmBasicBlockHandle CreateSocketFailBlock,
        LlvmBasicBlockHandle WriteCheckBlock,
        LlvmBasicBlockHandle WriteBlock,
        LlvmBasicBlockHandle WriteRetryCheckBlock,
        LlvmBasicBlockHandle ReadCheckBlock,
        LlvmBasicBlockHandle ReadBlock,
        LlvmBasicBlockHandle ReadRetryCheckBlock,
        LlvmBasicBlockHandle ProcessBlock,
        LlvmBasicBlockHandle EvaluateBlock,
        LlvmBasicBlockHandle CompleteCheckBlock,
        LlvmBasicBlockHandle IncompleteCheckBlock,
        LlvmBasicBlockHandle ReadPendingCheckBlock,
        LlvmBasicBlockHandle PendingReadBlock,
        LlvmBasicBlockHandle PendingWriteBlock,
        LlvmBasicBlockHandle FailBlock,
        LlvmBasicBlockHandle SuccessBlock,
        LlvmBasicBlockHandle FinishBlock);

    private static TlsHandshakeCtx EmitStepTlsHandshakePrologue(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tls_handshake_status_slot");
        LlvmValueHandle errorCodeSlot = LlvmApi.BuildAlloca(builder, state.I32, "step_tls_handshake_error_code_slot");
        LlvmValueHandle errorStageSlot = LlvmApi.BuildAlloca(builder, state.I32, "step_tls_handshake_error_stage_slot");
        LlvmValueHandle createStatusSlot = LlvmApi.BuildAlloca(builder, state.I32, "step_tls_handshake_create_status_slot");
        LlvmValueHandle connectionHandleSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tls_handshake_connection_handle_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), createStatusSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), connectionHandleSlot);
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), errorCodeSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), errorStageSlot);

        LlvmValueHandle initStatus = EmitNetworkingRuntimeCall(state, "ashes_tls_runtime_init", Array.Empty<LlvmValueHandle>(), "step_tls_handshake_init");

        LlvmBasicBlockHandle initFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_init_fail");
        LlvmBasicBlockHandle sessionCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_session_check");
        LlvmBasicBlockHandle createBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_create");
        LlvmBasicBlockHandle storeSessionBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_store_session");
        LlvmBasicBlockHandle createSocketFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_create_socket_fail");
        LlvmBasicBlockHandle writeCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_write_check");
        LlvmBasicBlockHandle writeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_write");
        LlvmBasicBlockHandle writeRetryCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_write_retry_check");
        LlvmBasicBlockHandle readCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_read_check");
        LlvmBasicBlockHandle readBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_read");
        LlvmBasicBlockHandle readRetryCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_read_retry_check");
        LlvmBasicBlockHandle processBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_process_packets");
        LlvmBasicBlockHandle evaluateBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_evaluate");
        LlvmBasicBlockHandle completeCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_complete_check");
        LlvmBasicBlockHandle incompleteCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_incomplete_check");
        LlvmBasicBlockHandle readPendingCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_read_pending_check");
        LlvmBasicBlockHandle pendingReadBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_pending_read");
        LlvmBasicBlockHandle pendingWriteBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_pending_write");
        LlvmBasicBlockHandle failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_fail");
        LlvmBasicBlockHandle successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_success");
        LlvmBasicBlockHandle finishBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_finish");

        return new TlsHandshakeCtx(initStatus, statusSlot, errorCodeSlot, errorStageSlot, createStatusSlot, connectionHandleSlot, initFailBlock, sessionCheckBlock, createBlock, storeSessionBlock, createSocketFailBlock, writeCheckBlock, writeBlock, writeRetryCheckBlock, readCheckBlock, readBlock, readRetryCheckBlock, processBlock, evaluateBlock, completeCheckBlock, incompleteCheckBlock, readPendingCheckBlock, pendingReadBlock, pendingWriteBlock, failBlock, successBlock, finishBlock);
    }

    private static LlvmValueHandle EmitStepTlsHandshakeTask(LlvmCodegenState state, LlvmValueHandle taskPtr, LinuxTlsGlobals globals, bool serverSide = false)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        var (initStatus, statusSlot, errorCodeSlot, errorStageSlot, createStatusSlot, connectionHandleSlot,
            initFailBlock, sessionCheckBlock, createBlock, storeSessionBlock, createSocketFailBlock,
            writeCheckBlock, writeBlock, writeRetryCheckBlock, readCheckBlock, readBlock, readRetryCheckBlock,
            processBlock, evaluateBlock, completeCheckBlock, incompleteCheckBlock, readPendingCheckBlock,
            pendingReadBlock, pendingWriteBlock, failBlock, successBlock, finishBlock) = EmitStepTlsHandshakePrologue(state);

        LlvmApi.BuildCondBr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, initStatus, LlvmApi.ConstInt(state.I64, 1, 0), "step_tls_handshake_init_ready"),
            sessionCheckBlock, initFailBlock);

        EmitStepTlsHandshakeInitFail(state, taskPtr, initStatus, statusSlot, initFailBlock, finishBlock);
        EmitStepTlsHandshakeSessionCheck(state, taskPtr, sessionCheckBlock, writeCheckBlock, createBlock);
        EmitStepTlsHandshakeCreate(state, taskPtr, globals, serverSide, createStatusSlot, connectionHandleSlot, createBlock, storeSessionBlock, createSocketFailBlock);
        EmitStepTlsHandshakeStoreSession(state, taskPtr, globals, connectionHandleSlot, storeSessionBlock, processBlock);
        EmitStepTlsHandshakeCreateSocketFail(state, taskPtr, createStatusSlot, errorCodeSlot, errorStageSlot, createSocketFailBlock, failBlock);
        EmitStepTlsHandshakeWriteCheck(state, taskPtr, writeCheckBlock, writeBlock, readCheckBlock);
        EmitStepTlsHandshakeWrite(state, taskPtr, globals, errorCodeSlot, errorStageSlot, writeBlock, processBlock, writeRetryCheckBlock, pendingWriteBlock, failBlock);
        EmitStepTlsHandshakeReadCheck(state, taskPtr, readCheckBlock, readBlock, evaluateBlock);
        EmitStepTlsHandshakeRead(state, taskPtr, globals, errorCodeSlot, errorStageSlot, readBlock, processBlock, readRetryCheckBlock, pendingReadBlock, failBlock);
        EmitStepTlsHandshakeProcess(state, taskPtr, errorCodeSlot, errorStageSlot, processBlock, evaluateBlock, failBlock);
        EmitStepTlsHandshakeEvaluate(state, taskPtr, evaluateBlock, completeCheckBlock, incompleteCheckBlock, readPendingCheckBlock, pendingWriteBlock, pendingReadBlock, successBlock, failBlock);
        EmitStepTlsHandshakePending(state, taskPtr, statusSlot, pendingReadBlock, pendingWriteBlock, finishBlock);
        EmitStepTlsHandshakeFail(state, taskPtr, globals, statusSlot, errorCodeSlot, errorStageSlot, failBlock, finishBlock);
        EmitStepTlsHandshakeSuccess(state, taskPtr, statusSlot, successBlock, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, statusSlot, "step_tls_handshake_status");
    }

    private static void EmitStepTlsHandshakeInitFail(LlvmCodegenState state, LlvmValueHandle taskPtr, LlvmValueHandle initStatus, LlvmValueHandle statusSlot, LlvmBasicBlockHandle initFailBlock, LlvmBasicBlockHandle finishBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, initFailBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitTlsInitFailureResult(state, initStatus), "step_tls_handshake_init_fail_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static void EmitStepTlsHandshakeSessionCheck(LlvmCodegenState state, LlvmValueHandle taskPtr, LlvmBasicBlockHandle sessionCheckBlock, LlvmBasicBlockHandle writeCheckBlock, LlvmBasicBlockHandle createBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, sessionCheckBlock);
        LlvmValueHandle existingSession = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tls_handshake_existing_session");
        LlvmValueHandle hasSession = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, existingSession, LlvmApi.ConstInt(state.I64, 0, 0), "step_tls_handshake_has_session");
        LlvmApi.BuildCondBr(builder, hasSession, writeCheckBlock, createBlock);
    }

    private static void EmitStepTlsHandshakeCreate(LlvmCodegenState state, LlvmValueHandle taskPtr, LinuxTlsGlobals globals, bool serverSide, LlvmValueHandle createStatusSlot, LlvmValueHandle connectionHandleSlot, LlvmBasicBlockHandle createBlock, LlvmBasicBlockHandle storeSessionBlock, LlvmBasicBlockHandle createSocketFailBlock)
    {
        LlvmApi.PositionBuilderAtEnd(state.Target.Builder, createBlock);
        if (!serverSide)
        {
            EmitStepTlsHandshakeCreateClient(state, taskPtr, globals, createStatusSlot, connectionHandleSlot, storeSessionBlock, createSocketFailBlock);
        }
        else
        {
            EmitStepTlsHandshakeCreateServer(state, taskPtr, globals, createStatusSlot, connectionHandleSlot, storeSessionBlock, createSocketFailBlock);
        }
    }

    private static void EmitStepTlsHandshakeCreateClient(LlvmCodegenState state, LlvmValueHandle taskPtr, LinuxTlsGlobals globals, LlvmValueHandle createStatusSlot, LlvmValueHandle connectionHandleSlot, LlvmBasicBlockHandle storeSessionBlock, LlvmBasicBlockHandle createSocketFailBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle host = LoadMemory(state, taskPtr, TaskStructLayout.IoArg1, "step_tls_handshake_host");
        LlvmValueHandle ctxHandle = LlvmApi.BuildLoad2(builder, state.I64, globals.ContextGlobal, "step_tls_handshake_ctx_handle");
        LlvmValueHandle connectionSlot = LlvmApi.BuildAlloca(builder, state.I8Ptr, "step_tls_handshake_connection_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstNull(state.I8Ptr), connectionSlot);
        LlvmValueHandle createStatus = EmitTlsClientConnectionNew(
            state,
            ctxHandle,
            EmitStringToCString(state, host, "step_tls_handshake_host_cstr"),
            connectionSlot,
            "step_tls_handshake_connection_new");
        LlvmApi.BuildStore(builder, createStatus, createStatusSlot);
        LlvmValueHandle connectionHandle = LlvmApi.BuildPtrToInt(builder, LlvmApi.BuildLoad2(builder, state.I8Ptr, connectionSlot, "step_tls_handshake_connection_ptr"), state.I64, "step_tls_handshake_connection_handle");
        LlvmApi.BuildStore(builder, connectionHandle, connectionHandleSlot);
        LlvmValueHandle createOk = LlvmApi.BuildAnd(builder,
            EmitTlsResultIsOk(state, createStatus, "step_tls_handshake_create_ok"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, connectionHandle, LlvmApi.ConstInt(state.I64, 0, 0), "step_tls_handshake_have_connection"),
            "step_tls_handshake_create_connection_ok");
        LlvmApi.BuildCondBr(builder, createOk, storeSessionBlock, createSocketFailBlock);
    }

    private static void EmitStepTlsHandshakeCreateServer(LlvmCodegenState state, LlvmValueHandle taskPtr, LinuxTlsGlobals globals, LlvmValueHandle createStatusSlot, LlvmValueHandle connectionHandleSlot, LlvmBasicBlockHandle storeSessionBlock, LlvmBasicBlockHandle createSocketFailBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // Server session: get-or-build the process-wide server config from the PEM inputs
        // (cert chain in IoArg1, private key in WaitData1), then create a server connection.
        LlvmBasicBlockHandle buildConfigBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_build_config");
        LlvmBasicBlockHandle setKeysBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_set_keys");
        LlvmBasicBlockHandle buildBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_builder_build");
        LlvmBasicBlockHandle cacheConfigBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_cache_config");
        LlvmBasicBlockHandle connNewBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_server_conn_new");

        LlvmValueHandle cachedConfig = LlvmApi.BuildLoad2(builder, state.I64, globals.ServerConfigGlobal, "step_tls_handshake_cached_config");
        LlvmValueHandle haveConfig = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, cachedConfig, LlvmApi.ConstInt(state.I64, 0, 0), "step_tls_handshake_have_config");
        LlvmApi.BuildCondBr(builder, haveConfig, connNewBlock, buildConfigBlock);

        LlvmApi.PositionBuilderAtEnd(builder, buildConfigBlock);
        LlvmValueHandle certStr = LoadMemory(state, taskPtr, TaskStructLayout.IoArg1, "step_tls_handshake_cert_pem");
        LlvmValueHandle keyStr = LoadMemory(state, taskPtr, TaskStructLayout.WaitData1, "step_tls_handshake_key_pem");
        LlvmValueHandle certifiedKeySlot = LlvmApi.BuildAlloca(builder, state.I8Ptr, "step_tls_handshake_certified_key_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstNull(state.I8Ptr), certifiedKeySlot);
        LlvmValueHandle keyBuildStatus = EmitTlsCertifiedKeyBuild(
            state,
            GetStringBytesPointer(state, certStr, "step_tls_handshake_cert_bytes"),
            LoadStringLength(state, certStr, "step_tls_handshake_cert_len"),
            GetStringBytesPointer(state, keyStr, "step_tls_handshake_key_bytes"),
            LoadStringLength(state, keyStr, "step_tls_handshake_key_len"),
            certifiedKeySlot,
            "step_tls_handshake_certified_key_build");
        LlvmApi.BuildStore(builder, keyBuildStatus, createStatusSlot);
        LlvmApi.BuildCondBr(builder,
            EmitTlsResultIsOk(state, keyBuildStatus, "step_tls_handshake_key_build_ok"),
            setKeysBlock, createSocketFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, setKeysBlock);
        LlvmValueHandle builderHandle = EmitTlsServerConfigBuilderNew(state, "step_tls_handshake_config_builder_new");
        LlvmValueHandle setKeysStatus = EmitTlsServerConfigBuilderSetCertifiedKeys(
            state,
            builderHandle,
            certifiedKeySlot,
            LlvmApi.ConstInt(state.I64, 1, 0),
            "step_tls_handshake_set_certified_keys");
        LlvmApi.BuildStore(builder, setKeysStatus, createStatusSlot);
        LlvmApi.BuildCondBr(builder,
            EmitTlsResultIsOk(state, setKeysStatus, "step_tls_handshake_set_keys_ok"),
            buildBlock, createSocketFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, buildBlock);
        LlvmValueHandle configSlot = LlvmApi.BuildAlloca(builder, state.I8Ptr, "step_tls_handshake_config_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstNull(state.I8Ptr), configSlot);
        LlvmValueHandle buildStatus = EmitTlsServerConfigBuilderBuild(state, globals, builderHandle, configSlot, "step_tls_handshake_config_build");
        LlvmApi.BuildStore(builder, buildStatus, createStatusSlot);
        LlvmApi.BuildCondBr(builder,
            EmitTlsResultIsOk(state, buildStatus, "step_tls_handshake_config_build_ok"),
            cacheConfigBlock, createSocketFailBlock);

        EmitStepTlsHandshakeServerCacheAndConn(state, globals, createStatusSlot, connectionHandleSlot, configSlot, cacheConfigBlock, connNewBlock, storeSessionBlock, createSocketFailBlock);
    }

    private static void EmitStepTlsHandshakeServerCacheAndConn(LlvmCodegenState state, LinuxTlsGlobals globals, LlvmValueHandle createStatusSlot, LlvmValueHandle connectionHandleSlot, LlvmValueHandle configSlot, LlvmBasicBlockHandle cacheConfigBlock, LlvmBasicBlockHandle connNewBlock, LlvmBasicBlockHandle storeSessionBlock, LlvmBasicBlockHandle createSocketFailBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, cacheConfigBlock);
        // The certified key is intentionally NOT freed: it is built once per process and the
        // server config (also cached for the process lifetime) borrows it, so freeing it here
        // dangles the config and crashes the second connection. Leaking one key is negligible.
        LlvmApi.BuildStore(builder,
            LlvmApi.BuildPtrToInt(builder, LlvmApi.BuildLoad2(builder, state.I8Ptr, configSlot, "step_tls_handshake_config_ptr"), state.I64, "step_tls_handshake_config_handle"),
            globals.ServerConfigGlobal);
        LlvmApi.BuildBr(builder, connNewBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connNewBlock);
        LlvmValueHandle configHandle = LlvmApi.BuildLoad2(builder, state.I64, globals.ServerConfigGlobal, "step_tls_handshake_config_for_conn");
        LlvmValueHandle serverConnectionSlot = LlvmApi.BuildAlloca(builder, state.I8Ptr, "step_tls_handshake_server_connection_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstNull(state.I8Ptr), serverConnectionSlot);
        LlvmValueHandle connNewStatus = EmitTlsServerConnectionNew(state, configHandle, serverConnectionSlot, "step_tls_handshake_server_connection_new");
        LlvmApi.BuildStore(builder, connNewStatus, createStatusSlot);
        LlvmValueHandle serverConnectionHandle = LlvmApi.BuildPtrToInt(builder, LlvmApi.BuildLoad2(builder, state.I8Ptr, serverConnectionSlot, "step_tls_handshake_server_connection_ptr"), state.I64, "step_tls_handshake_server_connection_handle");
        LlvmApi.BuildStore(builder, serverConnectionHandle, connectionHandleSlot);
        LlvmValueHandle serverCreateOk = LlvmApi.BuildAnd(builder,
            EmitTlsResultIsOk(state, connNewStatus, "step_tls_handshake_server_create_ok"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, serverConnectionHandle, LlvmApi.ConstInt(state.I64, 0, 0), "step_tls_handshake_server_have_connection"),
            "step_tls_handshake_server_create_connection_ok");
        LlvmApi.BuildCondBr(builder, serverCreateOk, storeSessionBlock, createSocketFailBlock);
    }

    private static void EmitStepTlsHandshakeStoreSession(LlvmCodegenState state, LlvmValueHandle taskPtr, LinuxTlsGlobals globals, LlvmValueHandle connectionHandleSlot, LlvmBasicBlockHandle storeSessionBlock, LlvmBasicBlockHandle processBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, storeSessionBlock);
        LlvmValueHandle storeSocket = LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, "step_tls_handshake_store_socket");
        LlvmValueHandle storeConnection = LlvmApi.BuildLoad2(builder, state.I64, connectionHandleSlot, "step_tls_handshake_store_connection");
        EmitMbedTlsConnectionSetSocket(state, globals, storeConnection, storeSocket, "step_tls_handshake_set_bio");
        LlvmValueHandle session = EmitCreateTlsSession(state, storeSocket, storeConnection, "step_tls_handshake_session");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, session, "step_tls_handshake_store_session");
        LlvmApi.BuildBr(builder, processBlock);
    }

    private static void EmitStepTlsHandshakeCreateSocketFail(LlvmCodegenState state, LlvmValueHandle taskPtr, LlvmValueHandle createStatusSlot, LlvmValueHandle errorCodeSlot, LlvmValueHandle errorStageSlot, LlvmBasicBlockHandle createSocketFailBlock, LlvmBasicBlockHandle failBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, createSocketFailBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildLoad2(builder, state.I32, createStatusSlot, "step_tls_handshake_create_status_value"), errorCodeSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 1, 0), errorStageSlot);
        LlvmValueHandle failSocket = LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, "step_tls_handshake_fail_socket");
        _ = EmitTcpClose(state, failSocket);
        LlvmApi.BuildBr(builder, failBlock);
    }

    private static void EmitStepTlsHandshakeWriteCheck(LlvmCodegenState state, LlvmValueHandle taskPtr, LlvmBasicBlockHandle writeCheckBlock, LlvmBasicBlockHandle writeBlock, LlvmBasicBlockHandle readCheckBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, writeCheckBlock);
        LlvmValueHandle writeCheckSession = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tls_handshake_write_check_session");
        LlvmValueHandle writeCheckConnection = EmitLoadTlsSessionSsl(state, writeCheckSession, "step_tls_handshake_write_check_connection");
        LlvmValueHandle wantsWrite = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            EmitTlsConnectionWantsWrite(state, writeCheckConnection, "step_tls_handshake_wants_write"),
            LlvmApi.ConstInt(state.I8, 0, 0),
            "step_tls_handshake_has_pending_write");
        LlvmApi.BuildCondBr(builder, wantsWrite, writeBlock, readCheckBlock);
    }

    private static void EmitStepTlsHandshakeWrite(LlvmCodegenState state, LlvmValueHandle taskPtr, LinuxTlsGlobals globals, LlvmValueHandle errorCodeSlot, LlvmValueHandle errorStageSlot, LlvmBasicBlockHandle writeBlock, LlvmBasicBlockHandle processBlock, LlvmBasicBlockHandle writeRetryCheckBlock, LlvmBasicBlockHandle pendingWriteBlock, LlvmBasicBlockHandle failBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, writeBlock);
        LlvmValueHandle writeSession = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tls_handshake_write_session");
        LlvmValueHandle writeSocket = EmitLoadTlsSessionSocket(state, writeSession, "step_tls_handshake_write_socket");
        LlvmValueHandle writeConnection = EmitLoadTlsSessionSsl(state, writeSession, "step_tls_handshake_write_connection");
        LlvmValueHandle writeBytesSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tls_handshake_write_bytes_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), writeBytesSlot);
        LlvmValueHandle writeStatus = EmitTlsConnectionWriteTls(state, globals, writeConnection, writeSocket, writeBytesSlot, "step_tls_handshake_write_tls");
        LlvmValueHandle writeOk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, writeStatus, LlvmApi.ConstInt(state.I32, 0, 0), "step_tls_handshake_write_ok");
        LlvmApi.BuildCondBr(builder, writeOk, processBlock, writeRetryCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, writeRetryCheckBlock);
        LlvmValueHandle writeWouldBlock = EmitTlsIoResultIsWouldBlock(state, writeStatus, "step_tls_handshake_write_would_block");
        LlvmBasicBlockHandle writeFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_write_fail");
        LlvmApi.BuildCondBr(builder, writeWouldBlock, pendingWriteBlock, writeFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, writeFailBlock);
        LlvmApi.BuildStore(builder, writeStatus, errorCodeSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 2, 0), errorStageSlot);
        LlvmApi.BuildBr(builder, failBlock);
    }

    private static void EmitStepTlsHandshakeReadCheck(LlvmCodegenState state, LlvmValueHandle taskPtr, LlvmBasicBlockHandle readCheckBlock, LlvmBasicBlockHandle readBlock, LlvmBasicBlockHandle evaluateBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, readCheckBlock);
        LlvmValueHandle readCheckSession = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tls_handshake_read_check_session");
        LlvmValueHandle readCheckConnection = EmitLoadTlsSessionSsl(state, readCheckSession, "step_tls_handshake_read_check_connection");
        LlvmValueHandle wantsRead = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            EmitTlsConnectionWantsRead(state, readCheckConnection, "step_tls_handshake_wants_read"),
            LlvmApi.ConstInt(state.I8, 0, 0),
            "step_tls_handshake_has_pending_read");
        LlvmApi.BuildCondBr(builder, wantsRead, readBlock, evaluateBlock);
    }

    private static void EmitStepTlsHandshakeRead(LlvmCodegenState state, LlvmValueHandle taskPtr, LinuxTlsGlobals globals, LlvmValueHandle errorCodeSlot, LlvmValueHandle errorStageSlot, LlvmBasicBlockHandle readBlock, LlvmBasicBlockHandle processBlock, LlvmBasicBlockHandle readRetryCheckBlock, LlvmBasicBlockHandle pendingReadBlock, LlvmBasicBlockHandle failBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, readBlock);
        LlvmValueHandle readSession = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tls_handshake_read_session");
        LlvmValueHandle readSocket = EmitLoadTlsSessionSocket(state, readSession, "step_tls_handshake_read_socket");
        LlvmValueHandle readConnection = EmitLoadTlsSessionSsl(state, readSession, "step_tls_handshake_read_connection");
        LlvmValueHandle readBytesSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tls_handshake_read_bytes_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), readBytesSlot);
        LlvmValueHandle readStatus = EmitTlsConnectionReadTls(state, globals, readConnection, readSocket, readBytesSlot, "step_tls_handshake_read_tls");
        LlvmValueHandle readOk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, readStatus, LlvmApi.ConstInt(state.I32, 0, 0), "step_tls_handshake_read_ok");
        LlvmApi.BuildCondBr(builder, readOk, processBlock, readRetryCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, readRetryCheckBlock);
        LlvmValueHandle readWouldBlock = EmitTlsIoResultIsWouldBlock(state, readStatus, "step_tls_handshake_read_would_block");
        LlvmBasicBlockHandle readFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_read_fail");
        LlvmApi.BuildCondBr(builder, readWouldBlock, pendingReadBlock, readFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, readFailBlock);
        LlvmApi.BuildStore(builder, readStatus, errorCodeSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 3, 0), errorStageSlot);
        LlvmApi.BuildBr(builder, failBlock);
    }

    private static void EmitStepTlsHandshakeProcess(LlvmCodegenState state, LlvmValueHandle taskPtr, LlvmValueHandle errorCodeSlot, LlvmValueHandle errorStageSlot, LlvmBasicBlockHandle processBlock, LlvmBasicBlockHandle evaluateBlock, LlvmBasicBlockHandle failBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, processBlock);
        LlvmValueHandle processSession = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tls_handshake_process_session");
        LlvmValueHandle processConnection = EmitLoadTlsSessionSsl(state, processSession, "step_tls_handshake_process_connection");
        LlvmValueHandle processStatus = EmitTlsConnectionProcessNewPackets(state, processConnection, "step_tls_handshake_process_new_packets");
        LlvmValueHandle processOk = EmitTlsResultIsOk(state, processStatus, "step_tls_handshake_process_ok");
        LlvmBasicBlockHandle processFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_process_fail");
        LlvmApi.BuildCondBr(builder, processOk, evaluateBlock, processFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, processFailBlock);
        LlvmApi.BuildStore(builder, processStatus, errorCodeSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 1, 0), errorStageSlot);
        LlvmApi.BuildBr(builder, failBlock);
    }

    private static void EmitStepTlsHandshakeEvaluate(LlvmCodegenState state, LlvmValueHandle taskPtr, LlvmBasicBlockHandle evaluateBlock, LlvmBasicBlockHandle completeCheckBlock, LlvmBasicBlockHandle incompleteCheckBlock, LlvmBasicBlockHandle readPendingCheckBlock, LlvmBasicBlockHandle pendingWriteBlock, LlvmBasicBlockHandle pendingReadBlock, LlvmBasicBlockHandle successBlock, LlvmBasicBlockHandle failBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, evaluateBlock);
        LlvmValueHandle evaluateSession = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tls_handshake_evaluate_session");
        LlvmValueHandle evaluateConnection = EmitLoadTlsSessionSsl(state, evaluateSession, "step_tls_handshake_evaluate_connection");
        LlvmValueHandle handshakeComplete = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Eq,
            EmitTlsConnectionIsHandshaking(state, evaluateConnection, "step_tls_handshake_is_handshaking"),
            LlvmApi.ConstInt(state.I8, 0, 0),
            "step_tls_handshake_complete");
        LlvmApi.BuildCondBr(builder, handshakeComplete, completeCheckBlock, incompleteCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, completeCheckBlock);
        LlvmValueHandle wantsWriteAfterComplete = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            EmitTlsConnectionWantsWrite(state, evaluateConnection, "step_tls_handshake_complete_wants_write"),
            LlvmApi.ConstInt(state.I8, 0, 0),
            "step_tls_handshake_complete_has_write");
        LlvmApi.BuildCondBr(builder, wantsWriteAfterComplete, pendingWriteBlock, successBlock);

        LlvmApi.PositionBuilderAtEnd(builder, incompleteCheckBlock);
        LlvmValueHandle wantsWriteAfter = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            EmitTlsConnectionWantsWrite(state, evaluateConnection, "step_tls_handshake_incomplete_wants_write"),
            LlvmApi.ConstInt(state.I8, 0, 0),
            "step_tls_handshake_incomplete_has_write");
        LlvmApi.BuildCondBr(builder, wantsWriteAfter, pendingWriteBlock, readPendingCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, readPendingCheckBlock);
        LlvmValueHandle wantsReadAfter = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            EmitTlsConnectionWantsRead(state, evaluateConnection, "step_tls_handshake_incomplete_wants_read"),
            LlvmApi.ConstInt(state.I8, 0, 0),
            "step_tls_handshake_incomplete_has_read");
        LlvmApi.BuildCondBr(builder, wantsReadAfter, pendingReadBlock, failBlock);
    }

    private static void EmitStepTlsHandshakePending(LlvmCodegenState state, LlvmValueHandle taskPtr, LlvmValueHandle statusSlot, LlvmBasicBlockHandle pendingReadBlock, LlvmBasicBlockHandle pendingWriteBlock, LlvmBasicBlockHandle finishBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, pendingReadBlock);
        LlvmApi.BuildStore(builder, EmitPendingLeafTask(state, taskPtr, TaskStructLayout.WaitTlsWantRead, EmitLoadTlsSessionSocket(state, LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tls_handshake_pending_read_session"), "step_tls_handshake_pending_read_socket"), "step_tls_handshake_pending_read"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingWriteBlock);
        LlvmApi.BuildStore(builder, EmitPendingLeafTask(state, taskPtr, TaskStructLayout.WaitTlsWantWrite, EmitLoadTlsSessionSocket(state, LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tls_handshake_pending_write_session"), "step_tls_handshake_pending_write_socket"), "step_tls_handshake_pending_write"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static void EmitStepTlsHandshakeFail(LlvmCodegenState state, LlvmValueHandle taskPtr, LinuxTlsGlobals globals, LlvmValueHandle statusSlot, LlvmValueHandle errorCodeSlot, LlvmValueHandle errorStageSlot, LlvmBasicBlockHandle failBlock, LlvmBasicBlockHandle finishBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        EmitCleanupTlsSession(state, globals, LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tls_handshake_fail_session"), "step_tls_handshake_fail_cleanup");
        LlvmValueHandle errorCode = LlvmApi.BuildLoad2(builder, state.I32, errorCodeSlot, "step_tls_handshake_error_code");
        LlvmValueHandle errorStage = LlvmApi.BuildLoad2(builder, state.I32, errorStageSlot, "step_tls_handshake_error_stage");
        LlvmValueHandle hasDetailedError = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, errorCode, LlvmApi.ConstInt(state.I32, 0, 0), "step_tls_handshake_has_detailed_error");
        LlvmValueHandle isTlsError = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, errorStage, LlvmApi.ConstInt(state.I32, 1, 0), "step_tls_handshake_is_tls_error");
        LlvmBasicBlockHandle failWithErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_fail_with_error");
        LlvmBasicBlockHandle failDetailCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_fail_detail_check");
        LlvmBasicBlockHandle failWithIoErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_fail_with_io_error");
        LlvmBasicBlockHandle failWithWriteErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_fail_with_write_error");
        LlvmBasicBlockHandle failWithReadErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_fail_with_read_error");
        LlvmBasicBlockHandle failGenericBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_handshake_fail_generic");
        LlvmValueHandle hasTlsError = LlvmApi.BuildAnd(builder, hasDetailedError, isTlsError, "step_tls_handshake_has_tls_error");
        LlvmApi.BuildCondBr(builder, hasTlsError, failWithErrorBlock, failDetailCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failDetailCheckBlock);
        LlvmApi.BuildCondBr(builder, hasDetailedError, failWithIoErrorBlock, failGenericBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failWithErrorBlock);
        LlvmValueHandle detailedMessage = EmitStringConcat(
            state,
            EmitHeapStringLiteral(state, TlsHandshakeFailedMessage + ": "),
            EmitMbedTlsHandshakeErrorString(
                state,
                errorCode,
                EmitLoadTlsSessionSsl(state, LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tls_handshake_error_session"), "step_tls_handshake_error_connection"),
                "step_tls_handshake_error"));
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, detailedMessage), "step_tls_handshake_fail_with_error_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failWithIoErrorBlock);
        LlvmValueHandle isWriteError = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, errorStage, LlvmApi.ConstInt(state.I32, 2, 0), "step_tls_handshake_is_write_error");
        LlvmApi.BuildCondBr(builder, isWriteError, failWithWriteErrorBlock, failWithReadErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failWithWriteErrorBlock);
        LlvmValueHandle writeErrorMessage = EmitStringConcat(
            state,
            EmitHeapStringLiteral(state, TlsHandshakeFailedMessage + ": write_tls errno "),
            EmitNonNegativeIntToString(state, LlvmApi.BuildZExt(builder, errorCode, state.I64, "step_tls_handshake_write_error_i64"), "step_tls_handshake_write_error_text"));
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, writeErrorMessage), "step_tls_handshake_fail_with_write_error_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failWithReadErrorBlock);
        LlvmValueHandle readErrorMessage = EmitStringConcat(
            state,
            EmitHeapStringLiteral(state, TlsHandshakeFailedMessage + ": read_tls errno "),
            EmitNonNegativeIntToString(state, LlvmApi.BuildZExt(builder, errorCode, state.I64, "step_tls_handshake_read_error_i64"), "step_tls_handshake_read_error_text"));
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, readErrorMessage), "step_tls_handshake_fail_with_read_error_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failGenericBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TlsHandshakeFailedMessage)), "step_tls_handshake_fail_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static void EmitStepTlsHandshakeSuccess(LlvmCodegenState state, LlvmValueHandle taskPtr, LlvmValueHandle statusSlot, LlvmBasicBlockHandle successBlock, LlvmBasicBlockHandle finishBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultOk(state, LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tls_handshake_success_session")), "step_tls_handshake_success_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static LlvmValueHandle EmitStepTlsSendTask(LlvmCodegenState state, LlvmValueHandle taskPtr, LinuxTlsGlobals globals)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle session = LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, "step_tls_send_session");
        LlvmValueHandle textRef = LoadMemory(state, taskPtr, TaskStructLayout.IoArg1, "step_tls_send_text");
        LlvmValueHandle totalLen = LoadStringLength(state, textRef, "step_tls_send_total_len");
        LlvmValueHandle sentSoFar = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tls_send_sent_so_far");
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tls_send_status_slot");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);

        LlvmValueHandle alreadyDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, sentSoFar, totalLen, "step_tls_send_already_done");
        (LlvmBasicBlockHandle sendBlock, LlvmBasicBlockHandle applyBlock, LlvmBasicBlockHandle flushCheckBlock, LlvmBasicBlockHandle flushBlock, LlvmBasicBlockHandle flushRetryCheckBlock, LlvmBasicBlockHandle finalizeBlock, LlvmBasicBlockHandle partialReadCheckBlock, LlvmBasicBlockHandle pendingReadBlock, LlvmBasicBlockHandle pendingWriteBlock, LlvmBasicBlockHandle failBlock, LlvmBasicBlockHandle completeBlock, LlvmBasicBlockHandle finishBlock) = AppendTlsSendBlocks(state);
        LlvmApi.BuildCondBr(builder, alreadyDone, flushCheckBlock, sendBlock);

        LlvmApi.PositionBuilderAtEnd(builder, sendBlock);
        LlvmValueHandle remaining = LlvmApi.BuildSub(builder, totalLen, sentSoFar, "step_tls_send_remaining");
        LlvmValueHandle cursorPtr = LlvmApi.BuildGEP2(builder, state.I8, GetStringBytesPointer(state, textRef, "step_tls_send_bytes"), [sentSoFar], "step_tls_send_cursor_ptr");
        LlvmValueHandle acceptedBytesSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tls_send_accepted_bytes_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), acceptedBytesSlot);
        LlvmValueHandle writeStatus = EmitTlsConnectionWrite(
            state,
            EmitLoadTlsSessionSsl(state, session, "step_tls_send_connection"),
            cursorPtr,
            remaining,
            acceptedBytesSlot,
            "step_tls_send_write");
        LlvmValueHandle writeOk = EmitTlsResultIsOk(state, writeStatus, "step_tls_send_write_ok");
        LlvmApi.BuildCondBr(builder, writeOk, applyBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, applyBlock);
        LlvmValueHandle nextSent = LlvmApi.BuildAdd(builder, sentSoFar, LlvmApi.BuildLoad2(builder, state.I64, acceptedBytesSlot, "step_tls_send_accepted_bytes"), "step_tls_send_next_sent");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, nextSent, "step_tls_send_store_sent");
        LlvmApi.BuildBr(builder, flushCheckBlock);

        EmitStepTlsSendFlush(state, taskPtr, globals, session, totalLen, statusSlot, flushCheckBlock, flushBlock, flushRetryCheckBlock, finalizeBlock, partialReadCheckBlock, pendingReadBlock, pendingWriteBlock, failBlock, completeBlock, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, statusSlot, "step_tls_send_status");
    }

    private static (LlvmBasicBlockHandle Send, LlvmBasicBlockHandle Apply, LlvmBasicBlockHandle FlushCheck, LlvmBasicBlockHandle Flush, LlvmBasicBlockHandle FlushRetryCheck, LlvmBasicBlockHandle Finalize, LlvmBasicBlockHandle PartialReadCheck, LlvmBasicBlockHandle PendingRead, LlvmBasicBlockHandle PendingWrite, LlvmBasicBlockHandle Fail, LlvmBasicBlockHandle Complete, LlvmBasicBlockHandle Finish) AppendTlsSendBlocks(LlvmCodegenState state)
    {
        return (
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_send_send"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_send_apply"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_send_flush_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_send_flush"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_send_flush_retry_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_send_finalize"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_send_partial_read_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_send_pending_read"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_send_pending_write"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_send_fail"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_send_complete"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_send_finish"));
    }

    private static void EmitStepTlsSendFlush(
        LlvmCodegenState state,
        LlvmValueHandle taskPtr,
        LinuxTlsGlobals globals,
        LlvmValueHandle session,
        LlvmValueHandle totalLen,
        LlvmValueHandle statusSlot,
        LlvmBasicBlockHandle flushCheckBlock,
        LlvmBasicBlockHandle flushBlock,
        LlvmBasicBlockHandle flushRetryCheckBlock,
        LlvmBasicBlockHandle finalizeBlock,
        LlvmBasicBlockHandle partialReadCheckBlock,
        LlvmBasicBlockHandle pendingReadBlock,
        LlvmBasicBlockHandle pendingWriteBlock,
        LlvmBasicBlockHandle failBlock,
        LlvmBasicBlockHandle completeBlock,
        LlvmBasicBlockHandle finishBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, flushCheckBlock);
        LlvmValueHandle flushSession = LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, "step_tls_send_flush_session");
        LlvmValueHandle flushConnection = EmitLoadTlsSessionSsl(state, flushSession, "step_tls_send_flush_connection");
        LlvmValueHandle flushNeedsWrite = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            EmitTlsConnectionWantsWrite(state, flushConnection, "step_tls_send_wants_write"),
            LlvmApi.ConstInt(state.I8, 0, 0),
            "step_tls_send_has_pending_write");
        LlvmApi.BuildCondBr(builder, flushNeedsWrite, flushBlock, finalizeBlock);

        LlvmApi.PositionBuilderAtEnd(builder, flushBlock);
        LlvmValueHandle flushSocket = EmitLoadTlsSessionSocket(state, flushSession, "step_tls_send_flush_socket");
        LlvmValueHandle flushBytesSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tls_send_flush_bytes_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), flushBytesSlot);
        LlvmValueHandle flushStatus = EmitTlsConnectionWriteTls(state, globals, flushConnection, flushSocket, flushBytesSlot, "step_tls_send_write_tls");
        LlvmValueHandle flushOk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, flushStatus, LlvmApi.ConstInt(state.I32, 0, 0), "step_tls_send_flush_ok");
        LlvmApi.BuildCondBr(builder, flushOk, finalizeBlock, flushRetryCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, flushRetryCheckBlock);
        LlvmValueHandle flushWouldBlock = EmitTlsIoResultIsWouldBlock(state, flushStatus, "step_tls_send_flush_would_block");
        LlvmApi.BuildCondBr(builder, flushWouldBlock, pendingWriteBlock, failBlock);

        EmitStepTlsSendFinalize(state, taskPtr, session, totalLen, statusSlot, flushConnection, finalizeBlock, partialReadCheckBlock, pendingReadBlock, pendingWriteBlock, failBlock, completeBlock, finishBlock);
    }

    private static void EmitStepTlsSendFinalize(
        LlvmCodegenState state,
        LlvmValueHandle taskPtr,
        LlvmValueHandle session,
        LlvmValueHandle totalLen,
        LlvmValueHandle statusSlot,
        LlvmValueHandle flushConnection,
        LlvmBasicBlockHandle finalizeBlock,
        LlvmBasicBlockHandle partialReadCheckBlock,
        LlvmBasicBlockHandle pendingReadBlock,
        LlvmBasicBlockHandle pendingWriteBlock,
        LlvmBasicBlockHandle failBlock,
        LlvmBasicBlockHandle completeBlock,
        LlvmBasicBlockHandle finishBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, finalizeBlock);
        LlvmValueHandle sentNow = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tls_send_sent_now");
        LlvmValueHandle allSent = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, sentNow, totalLen, "step_tls_send_all_sent");
        LlvmValueHandle stillNeedsWrite = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            EmitTlsConnectionWantsWrite(state, flushConnection, "step_tls_send_finalize_wants_write"),
            LlvmApi.ConstInt(state.I8, 0, 0),
            "step_tls_send_finalize_has_write");
        LlvmBasicBlockHandle allSentCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_send_all_sent_check");
        LlvmApi.BuildCondBr(builder, allSent, allSentCheckBlock, partialReadCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, allSentCheckBlock);
        LlvmApi.BuildCondBr(builder, stillNeedsWrite, pendingWriteBlock, completeBlock);

        LlvmApi.PositionBuilderAtEnd(builder, partialReadCheckBlock);
        LlvmApi.BuildCondBr(builder, stillNeedsWrite, pendingWriteBlock, pendingReadBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingReadBlock);
        LlvmValueHandle wantsRead = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            EmitTlsConnectionWantsRead(state, flushConnection, "step_tls_send_finalize_wants_read"),
            LlvmApi.ConstInt(state.I8, 0, 0),
            "step_tls_send_finalize_has_read");
        LlvmBasicBlockHandle pendingReadStoreBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_send_pending_read_store");
        LlvmApi.BuildCondBr(builder, wantsRead, pendingReadStoreBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingReadStoreBlock);
        LlvmApi.BuildStore(builder, EmitPendingLeafTask(state, taskPtr, TaskStructLayout.WaitTlsWantRead, EmitLoadTlsSessionSocket(state, session, "step_tls_send_pending_read_socket"), "step_tls_send_pending_read"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingWriteBlock);
        LlvmApi.BuildStore(builder, EmitPendingLeafTask(state, taskPtr, TaskStructLayout.WaitTlsWantWrite, EmitLoadTlsSessionSocket(state, session, "step_tls_send_pending_write_socket"), "step_tls_send_pending_write"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TlsSendFailedMessage)), "step_tls_send_fail_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, completeBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultOk(state, totalLen), "step_tls_send_complete_result"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static LlvmValueHandle EmitStepTlsReceiveTask(LlvmCodegenState state, LlvmValueHandle taskPtr, LinuxTlsGlobals globals)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle session = LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, "step_tls_receive_session");
        LlvmValueHandle maxBytes = LoadMemory(state, taskPtr, TaskStructLayout.IoArg1, "step_tls_receive_max_bytes");
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tls_receive_status_slot");
        LlvmValueHandle errorCodeSlot = LlvmApi.BuildAlloca(builder, state.I32, "step_tls_receive_error_code_slot");
        LlvmValueHandle errorStageSlot = LlvmApi.BuildAlloca(builder, state.I32, "step_tls_receive_error_stage_slot");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), errorCodeSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), errorStageSlot);

        LlvmValueHandle readBytesSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tls_receive_read_bytes_slot");
        LlvmValueHandle positiveMax = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, maxBytes, LlvmApi.ConstInt(state.I64, 0, 0), "step_tls_receive_positive_max");
        (LlvmBasicBlockHandle initBufferBlock, LlvmBasicBlockHandle handleReadBlock, LlvmBasicBlockHandle flushCheckBlock, LlvmBasicBlockHandle readCheckBlock, LlvmBasicBlockHandle rereadBlock, LlvmBasicBlockHandle pendingReadBlock, LlvmBasicBlockHandle pendingWriteBlock, LlvmBasicBlockHandle failBlock, LlvmBasicBlockHandle finalizeEmptyBlock, LlvmBasicBlockHandle finishBlock) = (
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_init_buffer"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_handle_read"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_flush_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_read_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_reread_plaintext"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_pending_read"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_pending_write"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_fail"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_finalize_empty"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_finish"));

        LlvmApi.BuildCondBr(builder, positiveMax, initBufferBlock, failBlock);

        EmitStepTlsReceiveInit(state, taskPtr, session, maxBytes, readBytesSlot, errorCodeSlot, errorStageSlot, initBufferBlock, handleReadBlock, flushCheckBlock, failBlock);
        EmitStepTlsReceiveFlush(state, session, globals, errorCodeSlot, errorStageSlot, flushCheckBlock, readCheckBlock, pendingWriteBlock, failBlock);
        EmitStepTlsReceiveRead(state, session, globals, errorCodeSlot, errorStageSlot, readCheckBlock, rereadBlock, pendingReadBlock, finalizeEmptyBlock, failBlock);
        EmitStepTlsReceiveReread(state, taskPtr, session, maxBytes, readBytesSlot, errorCodeSlot, errorStageSlot, rereadBlock, handleReadBlock, finalizeEmptyBlock, failBlock);
        EmitStepTlsReceiveHandle(state, taskPtr, session, statusSlot, readBytesSlot, handleReadBlock, finalizeEmptyBlock, pendingReadBlock, pendingWriteBlock, finishBlock);
        EmitStepTlsReceiveFail(state, taskPtr, statusSlot, errorCodeSlot, errorStageSlot, failBlock, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, statusSlot, "step_tls_receive_status");
    }

    private static void EmitStepTlsReceiveInit(
        LlvmCodegenState state,
        LlvmValueHandle taskPtr,
        LlvmValueHandle session,
        LlvmValueHandle maxBytes,
        LlvmValueHandle readBytesSlot,
        LlvmValueHandle errorCodeSlot,
        LlvmValueHandle errorStageSlot,
        LlvmBasicBlockHandle initBufferBlock,
        LlvmBasicBlockHandle handleReadBlock,
        LlvmBasicBlockHandle flushCheckBlock,
        LlvmBasicBlockHandle failBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, initBufferBlock);
        LlvmValueHandle bufferRef = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tls_receive_buffer_ref");
        LlvmValueHandle hasBuffer = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, bufferRef, LlvmApi.ConstInt(state.I64, 0, 0), "step_tls_receive_has_buffer");
        LlvmBasicBlockHandle receiveBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_receive");
        LlvmBasicBlockHandle allocateBufferBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_allocate_buffer");
        LlvmApi.BuildCondBr(builder, hasBuffer, receiveBlock, allocateBufferBlock);

        LlvmApi.PositionBuilderAtEnd(builder, allocateBufferBlock);
        LlvmValueHandle newBufferRef = EmitAllocDynamic(state, LlvmApi.BuildAdd(builder, maxBytes, LlvmApi.ConstInt(state.I64, 8, 0), "step_tls_receive_buffer_size"));
        StoreMemory(state, newBufferRef, 0, LlvmApi.ConstInt(state.I64, 0, 0), "step_tls_receive_store_buffer_len");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, newBufferRef, "step_tls_receive_store_buffer_ref");
        LlvmApi.BuildBr(builder, receiveBlock);

        LlvmApi.PositionBuilderAtEnd(builder, receiveBlock);
        LlvmValueHandle activeBuffer = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tls_receive_active_buffer");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), readBytesSlot);
        LlvmValueHandle readStatus = EmitTlsConnectionRead(
            state,
            EmitLoadTlsSessionSsl(state, session, "step_tls_receive_connection"),
            GetStringBytesPointer(state, activeBuffer, "step_tls_receive_bytes"),
            maxBytes,
            readBytesSlot,
            "step_tls_receive_read_plaintext");
        LlvmValueHandle readOk = EmitTlsResultIsOk(state, readStatus, "step_tls_receive_read_ok");
        LlvmValueHandle readEmpty = EmitTlsResultIsPlaintextEmpty(state, readStatus, "step_tls_receive_read_empty");
        LlvmValueHandle readReady = LlvmApi.BuildOr(builder, readOk, readEmpty, "step_tls_receive_read_ready");
        LlvmBasicBlockHandle inspectPlaintextBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_inspect_plaintext");
        LlvmBasicBlockHandle initialReadFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_initial_read_fail");
        LlvmApi.BuildCondBr(builder, readReady, inspectPlaintextBlock, initialReadFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, initialReadFailBlock);
        LlvmApi.BuildStore(builder, readStatus, errorCodeSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 1, 0), errorStageSlot);
        LlvmApi.BuildBr(builder, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, inspectPlaintextBlock);
        LlvmValueHandle initialReadCount = LlvmApi.BuildLoad2(builder, state.I64, readBytesSlot, "step_tls_receive_initial_read_count");
        LlvmValueHandle initialReadPositive = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, initialReadCount, LlvmApi.ConstInt(state.I64, 0, 0), "step_tls_receive_initial_read_positive");
        LlvmApi.BuildCondBr(builder, initialReadPositive, handleReadBlock, flushCheckBlock);
    }

    private static void EmitStepTlsReceiveFlush(
        LlvmCodegenState state,
        LlvmValueHandle session,
        LinuxTlsGlobals globals,
        LlvmValueHandle errorCodeSlot,
        LlvmValueHandle errorStageSlot,
        LlvmBasicBlockHandle flushCheckBlock,
        LlvmBasicBlockHandle readCheckBlock,
        LlvmBasicBlockHandle pendingWriteBlock,
        LlvmBasicBlockHandle failBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, flushCheckBlock);
        LlvmValueHandle flushNeedsWrite = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            EmitTlsConnectionWantsWrite(state, EmitLoadTlsSessionSsl(state, session, "step_tls_receive_flush_connection"), "step_tls_receive_wants_write"),
            LlvmApi.ConstInt(state.I8, 0, 0),
            "step_tls_receive_has_pending_write");
        LlvmBasicBlockHandle flushBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_flush");
        LlvmApi.BuildCondBr(builder, flushNeedsWrite, flushBlock, readCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, flushBlock);
        LlvmValueHandle flushBytesSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tls_receive_flush_bytes_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), flushBytesSlot);
        LlvmValueHandle flushStatus = EmitTlsConnectionWriteTls(state, globals, EmitLoadTlsSessionSsl(state, session, "step_tls_receive_flush_write_connection"), EmitLoadTlsSessionSocket(state, session, "step_tls_receive_flush_socket"), flushBytesSlot, "step_tls_receive_write_tls");
        LlvmValueHandle flushOk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, flushStatus, LlvmApi.ConstInt(state.I32, 0, 0), "step_tls_receive_flush_ok");
        LlvmBasicBlockHandle flushRetryCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_flush_retry_check");
        LlvmApi.BuildCondBr(builder, flushOk, readCheckBlock, flushRetryCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, flushRetryCheckBlock);
        LlvmValueHandle flushWouldBlock = EmitTlsIoResultIsWouldBlock(state, flushStatus, "step_tls_receive_flush_would_block");
        LlvmBasicBlockHandle flushFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_flush_fail");
        LlvmApi.BuildCondBr(builder, flushWouldBlock, pendingWriteBlock, flushFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, flushFailBlock);
        LlvmApi.BuildStore(builder, flushStatus, errorCodeSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 2, 0), errorStageSlot);
        LlvmApi.BuildBr(builder, failBlock);
    }

    private static void EmitStepTlsReceiveRead(
        LlvmCodegenState state,
        LlvmValueHandle session,
        LinuxTlsGlobals globals,
        LlvmValueHandle errorCodeSlot,
        LlvmValueHandle errorStageSlot,
        LlvmBasicBlockHandle readCheckBlock,
        LlvmBasicBlockHandle rereadBlock,
        LlvmBasicBlockHandle pendingReadBlock,
        LlvmBasicBlockHandle finalizeEmptyBlock,
        LlvmBasicBlockHandle failBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, readCheckBlock);
        LlvmValueHandle networkNeedsRead = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            EmitTlsConnectionWantsRead(state, EmitLoadTlsSessionSsl(state, session, "step_tls_receive_read_check_connection"), "step_tls_receive_wants_read"),
            LlvmApi.ConstInt(state.I8, 0, 0),
            "step_tls_receive_has_pending_read");
        LlvmBasicBlockHandle readBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_read_tls");
        LlvmApi.BuildCondBr(builder, networkNeedsRead, readBlock, finalizeEmptyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, readBlock);
        LlvmValueHandle networkReadBytesSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tls_receive_network_read_bytes_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), networkReadBytesSlot);
        LlvmValueHandle networkReadStatus = EmitTlsConnectionReadTls(state, globals, EmitLoadTlsSessionSsl(state, session, "step_tls_receive_network_connection"), EmitLoadTlsSessionSocket(state, session, "step_tls_receive_network_socket"), networkReadBytesSlot, "step_tls_receive_read_tls");
        LlvmValueHandle networkReadOk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, networkReadStatus, LlvmApi.ConstInt(state.I32, 0, 0), "step_tls_receive_network_read_ok");
        LlvmBasicBlockHandle readRetryCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_read_retry_check");
        LlvmBasicBlockHandle processBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_process_packets");
        LlvmApi.BuildCondBr(builder, networkReadOk, processBlock, readRetryCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, readRetryCheckBlock);
        LlvmValueHandle networkReadWouldBlock = EmitTlsIoResultIsWouldBlock(state, networkReadStatus, "step_tls_receive_network_read_would_block");
        LlvmBasicBlockHandle networkReadFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_network_read_fail");
        LlvmApi.BuildCondBr(builder, networkReadWouldBlock, pendingReadBlock, networkReadFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, networkReadFailBlock);
        LlvmApi.BuildStore(builder, networkReadStatus, errorCodeSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 3, 0), errorStageSlot);
        LlvmApi.BuildBr(builder, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, processBlock);
        LlvmValueHandle processStatus = EmitTlsConnectionProcessNewPackets(state, EmitLoadTlsSessionSsl(state, session, "step_tls_receive_process_connection"), "step_tls_receive_process_packets");
        LlvmValueHandle processOk = EmitTlsResultIsOk(state, processStatus, "step_tls_receive_process_ok");
        LlvmBasicBlockHandle processFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_process_fail");
        LlvmApi.BuildCondBr(builder, processOk, rereadBlock, processFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, processFailBlock);
        LlvmApi.BuildStore(builder, processStatus, errorCodeSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 4, 0), errorStageSlot);
        LlvmApi.BuildBr(builder, failBlock);
    }

    private static void EmitStepTlsReceiveReread(
        LlvmCodegenState state,
        LlvmValueHandle taskPtr,
        LlvmValueHandle session,
        LlvmValueHandle maxBytes,
        LlvmValueHandle readBytesSlot,
        LlvmValueHandle errorCodeSlot,
        LlvmValueHandle errorStageSlot,
        LlvmBasicBlockHandle rereadBlock,
        LlvmBasicBlockHandle handleReadBlock,
        LlvmBasicBlockHandle finalizeEmptyBlock,
        LlvmBasicBlockHandle failBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, rereadBlock);
        LlvmValueHandle activeBuffer = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tls_receive_active_buffer");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), readBytesSlot);
        LlvmValueHandle rereadStatus = EmitTlsConnectionRead(
            state,
            EmitLoadTlsSessionSsl(state, session, "step_tls_receive_reread_connection"),
            GetStringBytesPointer(state, activeBuffer, "step_tls_receive_reread_bytes"),
            maxBytes,
            readBytesSlot,
            "step_tls_receive_reread_plaintext");
        LlvmValueHandle rereadOk = EmitTlsResultIsOk(state, rereadStatus, "step_tls_receive_reread_ok");
        LlvmValueHandle rereadEmpty = EmitTlsResultIsPlaintextEmpty(state, rereadStatus, "step_tls_receive_reread_empty");
        LlvmValueHandle rereadReady = LlvmApi.BuildOr(builder, rereadOk, rereadEmpty, "step_tls_receive_reread_ready");
        LlvmBasicBlockHandle inspectRereadBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_inspect_reread");
        LlvmBasicBlockHandle rereadFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_reread_fail");
        LlvmApi.BuildCondBr(builder, rereadReady, inspectRereadBlock, rereadFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, rereadFailBlock);
        LlvmApi.BuildStore(builder, rereadStatus, errorCodeSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 5, 0), errorStageSlot);
        LlvmApi.BuildBr(builder, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, inspectRereadBlock);
        LlvmValueHandle rereadCount = LlvmApi.BuildLoad2(builder, state.I64, readBytesSlot, "step_tls_receive_reread_count");
        LlvmValueHandle rereadPositive = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, rereadCount, LlvmApi.ConstInt(state.I64, 0, 0), "step_tls_receive_reread_positive");
        LlvmApi.BuildCondBr(builder, rereadPositive, handleReadBlock, finalizeEmptyBlock);
    }

    private static void EmitStepTlsReceiveHandle(
        LlvmCodegenState state,
        LlvmValueHandle taskPtr,
        LlvmValueHandle session,
        LlvmValueHandle statusSlot,
        LlvmValueHandle readBytesSlot,
        LlvmBasicBlockHandle handleReadBlock,
        LlvmBasicBlockHandle finalizeEmptyBlock,
        LlvmBasicBlockHandle pendingReadBlock,
        LlvmBasicBlockHandle pendingWriteBlock,
        LlvmBasicBlockHandle finishBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, handleReadBlock);
        LlvmValueHandle activeBuffer = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tls_receive_active_buffer");
        LlvmValueHandle readCount = LlvmApi.BuildLoad2(builder, state.I64, readBytesSlot, "step_tls_receive_read_count");
        StoreMemory(state, activeBuffer, 0, readCount, "step_tls_receive_store_read_len");
        LlvmBasicBlockHandle successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_success");
        LlvmBasicBlockHandle validateUtf8Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_validate_utf8");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, readCount, LlvmApi.ConstInt(state.I64, 0, 0), "step_tls_receive_empty_read"), successBlock, validateUtf8Block);

        LlvmApi.PositionBuilderAtEnd(builder, validateUtf8Block);
        LlvmValueHandle utf8Valid = EmitValidateUtf8(state, GetStringBytesPointer(state, activeBuffer, "step_tls_receive_validate_bytes"), readCount, "step_tls_receive_utf8");
        LlvmValueHandle validUtf8 = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, utf8Valid, LlvmApi.ConstInt(state.I64, 0, 0), "step_tls_receive_valid_utf8");
        LlvmBasicBlockHandle invalidUtf8Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_invalid_utf8");
        LlvmApi.BuildCondBr(builder, validUtf8, successBlock, invalidUtf8Block);

        LlvmApi.PositionBuilderAtEnd(builder, finalizeEmptyBlock);
        LlvmValueHandle finalizeBuffer = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tls_receive_finalize_buffer");
        StoreMemory(state, finalizeBuffer, 0, LlvmApi.ConstInt(state.I64, 0, 0), "step_tls_receive_store_empty_len");
        LlvmValueHandle wantsWriteAfter = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            EmitTlsConnectionWantsWrite(state, EmitLoadTlsSessionSsl(state, session, "step_tls_receive_finalize_connection"), "step_tls_receive_finalize_wants_write"),
            LlvmApi.ConstInt(state.I8, 0, 0),
            "step_tls_receive_finalize_has_write");
        LlvmBasicBlockHandle readPendingCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_read_pending_check");
        LlvmApi.BuildCondBr(builder, wantsWriteAfter, pendingWriteBlock, readPendingCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, readPendingCheckBlock);
        LlvmValueHandle wantsReadAfter = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            EmitTlsConnectionWantsRead(state, EmitLoadTlsSessionSsl(state, session, "step_tls_receive_finalize_read_connection"), "step_tls_receive_finalize_wants_read"),
            LlvmApi.ConstInt(state.I8, 0, 0),
            "step_tls_receive_finalize_has_read");
        LlvmApi.BuildCondBr(builder, wantsReadAfter, pendingReadBlock, successBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingReadBlock);
        LlvmApi.BuildStore(builder, EmitPendingLeafTask(state, taskPtr, TaskStructLayout.WaitTlsWantRead, EmitLoadTlsSessionSocket(state, session, "step_tls_receive_pending_read_socket"), "step_tls_receive_pending_read"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingWriteBlock);
        LlvmApi.BuildStore(builder, EmitPendingLeafTask(state, taskPtr, TaskStructLayout.WaitTlsWantWrite, EmitLoadTlsSessionSocket(state, session, "step_tls_receive_pending_write_socket"), "step_tls_receive_pending_write"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultOk(state, LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tls_receive_success_buffer")), "step_tls_receive_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, invalidUtf8Block);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TlsInvalidUtf8Message)), "step_tls_receive_invalid_utf8_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static void EmitStepTlsReceiveFail(
        LlvmCodegenState state,
        LlvmValueHandle taskPtr,
        LlvmValueHandle statusSlot,
        LlvmValueHandle errorCodeSlot,
        LlvmValueHandle errorStageSlot,
        LlvmBasicBlockHandle failBlock,
        LlvmBasicBlockHandle finishBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmValueHandle errorCode = LlvmApi.BuildLoad2(builder, state.I32, errorCodeSlot, "step_tls_receive_error_code");
        LlvmValueHandle errorStage = LlvmApi.BuildLoad2(builder, state.I32, errorStageSlot, "step_tls_receive_error_stage");
        LlvmValueHandle hasDetailedError = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, errorCode, LlvmApi.ConstInt(state.I32, 0, 0), "step_tls_receive_has_detailed_error");
        LlvmValueHandle isIoError = LlvmApi.BuildOr(
            builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, errorStage, LlvmApi.ConstInt(state.I32, 2, 0), "step_tls_receive_is_flush_io_error"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, errorStage, LlvmApi.ConstInt(state.I32, 3, 0), "step_tls_receive_is_network_io_error"),
            "step_tls_receive_is_io_error");
        LlvmBasicBlockHandle failWithTlsErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_fail_with_tls_error");
        LlvmBasicBlockHandle failDetailCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_fail_detail_check");
        LlvmBasicBlockHandle failWithIoErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_fail_with_io_error");
        LlvmBasicBlockHandle failGenericBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_receive_fail_generic");
        LlvmValueHandle isNotIoError = LlvmApi.BuildICmp(
            builder,
            LlvmIntPredicate.Eq,
            isIoError,
            LlvmApi.ConstNull(LlvmApi.TypeOf(isIoError)),
            "step_tls_receive_not_io_error");
        LlvmValueHandle hasTlsError = LlvmApi.BuildAnd(builder, hasDetailedError, isNotIoError, "step_tls_receive_has_tls_error");
        LlvmApi.BuildCondBr(builder, hasTlsError, failWithTlsErrorBlock, failDetailCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failWithTlsErrorBlock);
        LlvmValueHandle tlsErrorMessage = EmitStringConcat(
            state,
            EmitHeapStringLiteral(state, TlsReceiveFailedMessage + ": "),
            EmitTlsErrorString(state, errorCode, "step_tls_receive_error"));
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, tlsErrorMessage), "step_tls_receive_fail_with_tls_error_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failDetailCheckBlock);
        LlvmApi.BuildCondBr(builder, hasDetailedError, failWithIoErrorBlock, failGenericBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failWithIoErrorBlock);
        LlvmValueHandle ioErrorMessage = EmitStringConcat(
            state,
            EmitHeapStringLiteral(state, TlsReceiveFailedMessage + ": errno "),
            EmitNonNegativeIntToString(state, LlvmApi.BuildZExt(builder, errorCode, state.I64, "step_tls_receive_error_i64"), "step_tls_receive_error_text"));
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, ioErrorMessage), "step_tls_receive_fail_with_io_error_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failGenericBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TlsReceiveFailedMessage)), "step_tls_receive_fail_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static LlvmValueHandle EmitStepTlsCloseTask(LlvmCodegenState state, LlvmValueHandle taskPtr, LinuxTlsGlobals globals)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle session = LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, "step_tls_close_session");
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tls_close_status_slot");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);

        LlvmBasicBlockHandle startBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_close_start");
        LlvmBasicBlockHandle flushCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_close_flush_check");
        LlvmBasicBlockHandle flushBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_close_flush");
        LlvmBasicBlockHandle flushRetryCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_close_flush_retry_check");
        LlvmBasicBlockHandle cleanupBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_close_cleanup");
        LlvmBasicBlockHandle pendingWriteBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_close_pending_write");
        LlvmBasicBlockHandle finishBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tls_close_finish");

        LlvmValueHandle closeStarted = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tls_close_started_flag"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "step_tls_close_started");
        LlvmApi.BuildCondBr(builder, closeStarted, flushCheckBlock, startBlock);

        LlvmApi.PositionBuilderAtEnd(builder, startBlock);
        EmitTlsConnectionSendCloseNotify(state, EmitLoadTlsSessionSsl(state, session, "step_tls_close_start_connection"), "step_tls_close_send_close_notify");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, LlvmApi.ConstInt(state.I64, 1, 0), "step_tls_close_store_started_flag");
        LlvmApi.BuildBr(builder, flushCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, flushCheckBlock);
        LlvmValueHandle wantsWrite = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            EmitTlsConnectionWantsWrite(state, EmitLoadTlsSessionSsl(state, session, "step_tls_close_flush_check_connection"), "step_tls_close_wants_write"),
            LlvmApi.ConstInt(state.I8, 0, 0),
            "step_tls_close_has_pending_write");
        LlvmApi.BuildCondBr(builder, wantsWrite, flushBlock, cleanupBlock);

        LlvmApi.PositionBuilderAtEnd(builder, flushBlock);
        LlvmValueHandle flushBytesSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tls_close_flush_bytes_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), flushBytesSlot);
        LlvmValueHandle flushStatus = EmitTlsConnectionWriteTls(state, globals, EmitLoadTlsSessionSsl(state, session, "step_tls_close_flush_connection"), EmitLoadTlsSessionSocket(state, session, "step_tls_close_flush_socket"), flushBytesSlot, "step_tls_close_write_tls");
        LlvmValueHandle flushOk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, flushStatus, LlvmApi.ConstInt(state.I32, 0, 0), "step_tls_close_flush_ok");
        LlvmApi.BuildCondBr(builder, flushOk, flushCheckBlock, flushRetryCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, flushRetryCheckBlock);
        LlvmValueHandle flushWouldBlock = EmitTlsIoResultIsWouldBlock(state, flushStatus, "step_tls_close_flush_would_block");
        LlvmApi.BuildCondBr(builder, flushWouldBlock, pendingWriteBlock, cleanupBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingWriteBlock);
        LlvmApi.BuildStore(builder, EmitPendingLeafTask(state, taskPtr, TaskStructLayout.WaitTlsWantWrite, EmitLoadTlsSessionSocket(state, session, "step_tls_close_pending_write_socket"), "step_tls_close_pending_write"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, cleanupBlock);
        EmitCleanupTlsSession(state, globals, session, "step_tls_close_cleanup");
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(
            state,
            taskPtr,
            EmitResultOk(state, EmitUnitValue(state)),
            "step_tls_close_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, statusSlot, "step_tls_close_status");
    }

    private static LlvmValueHandle EmitStepTcpConnectTaskWindowsIocp(LlvmCodegenState state, LlvmValueHandle taskPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_connect_win_status_slot");
        LlvmValueHandle errorRefSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_connect_win_error_ref_slot");
        LlvmValueHandle socketSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_connect_win_socket_slot");
        LlvmValueHandle addrSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_connect_win_addr_slot");
        LlvmValueHandle pendingContextSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_connect_win_pending_context_slot");
        LlvmValueHandle waitContext = LoadMemory(state, taskPtr, TaskStructLayout.WaitHandle, "step_tcp_connect_win_wait_context");
        LlvmValueHandle hostRef = LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, "step_tcp_connect_win_host");
        LlvmValueHandle port = LoadMemory(state, taskPtr, TaskStructLayout.IoArg1, "step_tcp_connect_win_port");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildStore(builder, EmitHeapStringLiteral(state, TcpConnectFailedMessage), errorRefSlot);
        LlvmApi.BuildStore(builder, LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tcp_connect_win_cached_socket"), socketSlot);
        LlvmApi.BuildStore(builder, LoadMemory(state, taskPtr, TaskStructLayout.WaitData1, "step_tcp_connect_win_cached_addr"), addrSlot);
        LlvmApi.BuildStore(builder, waitContext, pendingContextSlot);

        (LlvmBasicBlockHandle resumeBlock, LlvmBasicBlockHandle setupBlock, LlvmBasicBlockHandle doneBlock, LlvmBasicBlockHandle failBlock, LlvmBasicBlockHandle pendingBlock, LlvmBasicBlockHandle successBlock, LlvmBasicBlockHandle bindBlock, LlvmBasicBlockHandle missingConnectExBlock) = (
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_resume"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_setup"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_done"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_fail"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_pending"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_success"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_bind_any"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_missing_connectex"));
        LlvmApi.BuildCondBr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, waitContext, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_connect_win_has_context"),
            resumeBlock,
            setupBlock);

        EmitStepTcpConnectWinResume(state, waitContext, errorRefSlot, resumeBlock, pendingBlock, successBlock, failBlock);
        EmitStepTcpConnectWinSetup(state, taskPtr, hostRef, port, errorRefSlot, socketSlot, addrSlot, pendingContextSlot, setupBlock, failBlock, bindBlock, missingConnectExBlock, successBlock, pendingBlock);
        EmitStepTcpConnectWinFinish(state, taskPtr, statusSlot, socketSlot, pendingContextSlot, errorRefSlot, successBlock, pendingBlock, failBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, statusSlot, "step_tcp_connect_win_status");
    }

    private static void EmitStepTcpConnectWinResume(
        LlvmCodegenState state,
        LlvmValueHandle waitContext,
        LlvmValueHandle errorRefSlot,
        LlvmBasicBlockHandle resumeBlock,
        LlvmBasicBlockHandle pendingBlock,
        LlvmBasicBlockHandle successBlock,
        LlvmBasicBlockHandle failBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, resumeBlock);
        LlvmValueHandle resumeStatus = EmitWindowsIocpOperationStatus(state, waitContext, "step_tcp_connect_win_resume");
        LlvmValueHandle resumePending = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, resumeStatus, LlvmApi.ConstInt(state.I64, WindowsIocpOperationLayout.StatePending, 0), "step_tcp_connect_win_resume_pending");
        LlvmBasicBlockHandle resumeCompletedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_resume_completed");
        LlvmApi.BuildCondBr(builder, resumePending, pendingBlock, resumeCompletedBlock);

        LlvmApi.PositionBuilderAtEnd(builder, resumeCompletedBlock);
        LlvmValueHandle resumeSucceeded = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, resumeStatus, LlvmApi.ConstInt(state.I64, WindowsIocpOperationLayout.StateCompleted, 0), "step_tcp_connect_win_resume_succeeded");
        LlvmBasicBlockHandle resumeFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_resume_fail");
        LlvmApi.BuildCondBr(builder, resumeSucceeded, successBlock, resumeFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, resumeFailBlock);
        LlvmApi.BuildStore(builder, EmitHeapStringLiteral(state, "Ashes.Net.Tcp.connect() failed: IOCP completion failed"), errorRefSlot);
        LlvmApi.BuildBr(builder, failBlock);
    }

    private static void EmitStepTcpConnectWinSetup(
        LlvmCodegenState state,
        LlvmValueHandle taskPtr,
        LlvmValueHandle hostRef,
        LlvmValueHandle port,
        LlvmValueHandle errorRefSlot,
        LlvmValueHandle socketSlot,
        LlvmValueHandle addrSlot,
        LlvmValueHandle pendingContextSlot,
        LlvmBasicBlockHandle setupBlock,
        LlvmBasicBlockHandle failBlock,
        LlvmBasicBlockHandle bindBlock,
        LlvmBasicBlockHandle missingConnectExBlock,
        LlvmBasicBlockHandle successBlock,
        LlvmBasicBlockHandle pendingBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, setupBlock);
        LlvmValueHandle resolveResult = EmitResolveHostIpv4OrLocalhost(state, hostRef, "step_tcp_connect_win_resolve");
        LlvmValueHandle resolveTag = LoadMemory(state, resolveResult, 0, "step_tcp_connect_win_resolve_tag");
        LlvmValueHandle resolveFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, resolveTag, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_connect_win_resolve_failed");
        LlvmBasicBlockHandle validatePortBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_validate_port");
        LlvmApi.BuildCondBr(builder, resolveFailed, failBlock, validatePortBlock);

        LlvmApi.PositionBuilderAtEnd(builder, validatePortBlock);
        LlvmValueHandle validPort = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, port, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_connect_win_port_gt_zero"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, port, LlvmApi.ConstInt(state.I64, 65535, 0), "step_tcp_connect_win_port_le_max"),
            "step_tcp_connect_win_port_valid");
        LlvmBasicBlockHandle openSocketBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_open_socket");
        LlvmApi.BuildCondBr(builder, validPort, openSocketBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, openSocketBlock);
        LlvmTypeHandle wsadataType = LlvmApi.ArrayType2(state.I8, 512);
        LlvmValueHandle wsadata = LlvmApi.BuildAlloca(builder, wsadataType, "step_tcp_connect_win_wsadata");
        EmitWindowsWsaStartup(state, GetArrayElementPointer(state, wsadataType, wsadata, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_connect_win_wsadata_ptr"), "step_tcp_connect_win_wsastartup");
        LlvmValueHandle socket = EmitWindowsSocket(state, 2, 1, 6, "step_tcp_connect_win_socket_call");
        LlvmApi.BuildStore(builder, socket, socketSlot);
        LlvmApi.BuildStore(builder, LoadMemory(state, resolveResult, 8, "step_tcp_connect_win_addr_value"), addrSlot);
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, socket, "step_tcp_connect_win_store_socket");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData1, LoadMemory(state, resolveResult, 8, "step_tcp_connect_win_store_addr_value"), "step_tcp_connect_win_store_addr");

        LlvmTypeHandle sockaddrType = LlvmApi.ArrayType2(state.I8, 16);
        LlvmValueHandle sockaddrStorage = LlvmApi.BuildAlloca(builder, sockaddrType, "step_tcp_connect_win_sockaddr");
        LlvmValueHandle sockaddrBytes = GetArrayElementPointer(state, sockaddrType, sockaddrStorage, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_connect_win_sockaddr_bytes");
        LlvmTypeHandle i16 = LlvmApi.Int16TypeInContext(state.Target.Context);
        LlvmTypeHandle i16Ptr = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.BuildBitCast(builder, sockaddrBytes, state.I64Ptr, "step_tcp_connect_win_sockaddr_i64"));
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.BuildBitCast(builder,
            LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, [LlvmApi.ConstInt(state.I64, 8, 0)], "step_tcp_connect_win_sockaddr_tail"),
            state.I64Ptr,
            "step_tcp_connect_win_sockaddr_tail_i64"));
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(i16, 2, 0), LlvmApi.BuildBitCast(builder, sockaddrBytes, i16Ptr, "step_tcp_connect_win_family_ptr"));
        LlvmValueHandle portPtr = LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, [LlvmApi.ConstInt(state.I64, 2, 0)], "step_tcp_connect_win_port_ptr_byte");
        LlvmApi.BuildStore(builder,
            LlvmApi.BuildTrunc(builder, EmitByteSwap16(state, port, "step_tcp_connect_win_port_network"), i16, "step_tcp_connect_win_port_i16"),
            LlvmApi.BuildBitCast(builder, portPtr, i16Ptr, "step_tcp_connect_win_port_ptr"));
        LlvmValueHandle addrPtr = LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, [LlvmApi.ConstInt(state.I64, 4, 0)], "step_tcp_connect_win_addr_ptr_byte");
        LlvmApi.BuildStore(builder,
            LlvmApi.BuildTrunc(builder, LlvmApi.BuildLoad2(builder, state.I64, addrSlot, "step_tcp_connect_win_addr_loaded"), state.I32, "step_tcp_connect_win_addr_i32"),
            LlvmApi.BuildBitCast(builder, addrPtr, state.I32Ptr, "step_tcp_connect_win_addr_ptr"));

        EmitWindowsAssociateSocketWithIocp(state, socket, "step_tcp_connect_win_associate");
        LlvmValueHandle connectExPtr = EmitWindowsLoadConnectExPointer(state, socket, "step_tcp_connect_win_connectex");
        LlvmValueHandle hasConnectEx = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, connectExPtr, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_connect_win_has_connectex");
        LlvmApi.BuildCondBr(builder, hasConnectEx, bindBlock, missingConnectExBlock);

        EmitStepTcpConnectWinConnect(state, socket, connectExPtr, sockaddrBytes, errorRefSlot, pendingContextSlot, bindBlock, missingConnectExBlock, successBlock, failBlock, pendingBlock);
    }

    private static void EmitStepTcpConnectWinConnect(
        LlvmCodegenState state,
        LlvmValueHandle socket,
        LlvmValueHandle connectExPtr,
        LlvmValueHandle sockaddrBytes,
        LlvmValueHandle errorRefSlot,
        LlvmValueHandle pendingContextSlot,
        LlvmBasicBlockHandle bindBlock,
        LlvmBasicBlockHandle missingConnectExBlock,
        LlvmBasicBlockHandle successBlock,
        LlvmBasicBlockHandle failBlock,
        LlvmBasicBlockHandle pendingBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, missingConnectExBlock);
        LlvmApi.BuildStore(builder, EmitHeapStringLiteral(state, "Ashes.Net.Tcp.connect() failed: ConnectEx unavailable"), errorRefSlot);
        LlvmApi.BuildBr(builder, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, bindBlock);
        LlvmValueHandle bindResult = EmitWindowsBindIpv4Any(state, socket, "step_tcp_connect_win_bind_any");
        LlvmValueHandle bindSucceeded = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, bindResult, LlvmApi.ConstInt(state.I32, 0, 0), "step_tcp_connect_win_bind_ok");
        LlvmBasicBlockHandle connectIssueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_issue_connect");
        LlvmBasicBlockHandle bindFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_bind_fail");
        LlvmApi.BuildCondBr(builder, bindSucceeded, connectIssueBlock, bindFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, bindFailBlock);
        LlvmValueHandle bindError = LlvmApi.BuildZExt(builder, EmitWindowsWsaGetLastError(state, "step_tcp_connect_win_bind_error_code"), state.I64, "step_tcp_connect_win_bind_error_i64");
        LlvmValueHandle bindErrorText = EmitStringConcat(
            state,
            EmitHeapStringLiteral(state, "Ashes.Net.Tcp.connect() failed: bind "),
            EmitNonNegativeIntToString(state, bindError, "step_tcp_connect_win_bind_error_text"));
        LlvmApi.BuildStore(builder, bindErrorText, errorRefSlot);
        LlvmApi.BuildBr(builder, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectIssueBlock);
        LlvmValueHandle operationContext = EmitWindowsCreateIocpOperationContext(state, "step_tcp_connect_win_op_context");
        LlvmValueHandle bytesSentSlot = LlvmApi.BuildAlloca(builder, state.I32, "step_tcp_connect_win_bytes_sent_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), bytesSentSlot);
        LlvmValueHandle connectResult = EmitWindowsConnectEx(state, connectExPtr, socket, sockaddrBytes, EmitWindowsIocpOverlappedPtr(state, operationContext, "step_tcp_connect_win_overlapped"), bytesSentSlot, "step_tcp_connect_win_connectex_call");
        LlvmValueHandle connectImmediate = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, connectResult, LlvmApi.ConstInt(state.I32, 0, 0), "step_tcp_connect_win_connect_immediate");
        LlvmBasicBlockHandle connectErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_connect_error");
        LlvmApi.BuildCondBr(builder, connectImmediate, successBlock, connectErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectErrorBlock);
        LlvmValueHandle connectError = LlvmApi.BuildZExt(builder, EmitWindowsWsaGetLastError(state, "step_tcp_connect_win_connect_error_code"), state.I64, "step_tcp_connect_win_connect_error_i64");
        LlvmValueHandle isIoPending = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, connectError, LlvmApi.ConstInt(state.I64, WindowsErrorIoPending, 0), "step_tcp_connect_win_is_io_pending");
        LlvmBasicBlockHandle queuePendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_queue_pending");
        LlvmBasicBlockHandle connectImmediateFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_connect_win_connect_immediate_fail");
        LlvmApi.BuildCondBr(builder, isIoPending, queuePendingBlock, connectImmediateFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectImmediateFailBlock);
        LlvmValueHandle connectErrorText = EmitStringConcat(
            state,
            EmitHeapStringLiteral(state, "Ashes.Net.Tcp.connect() failed: ConnectEx "),
            EmitNonNegativeIntToString(state, connectError, "step_tcp_connect_win_connect_error_text"));
        LlvmApi.BuildStore(builder, connectErrorText, errorRefSlot);
        LlvmApi.BuildBr(builder, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, queuePendingBlock);
        LlvmApi.BuildStore(builder, operationContext, pendingContextSlot);
        LlvmApi.BuildBr(builder, pendingBlock);
    }

    private static void EmitStepTcpConnectWinFinish(
        LlvmCodegenState state,
        LlvmValueHandle taskPtr,
        LlvmValueHandle statusSlot,
        LlvmValueHandle socketSlot,
        LlvmValueHandle pendingContextSlot,
        LlvmValueHandle errorRefSlot,
        LlvmBasicBlockHandle successBlock,
        LlvmBasicBlockHandle pendingBlock,
        LlvmBasicBlockHandle failBlock,
        LlvmBasicBlockHandle doneBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        LlvmValueHandle connectedSocket = LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "step_tcp_connect_win_socket_ok");
        EmitWindowsUpdateConnectContext(state, connectedSocket, "step_tcp_connect_win_update_context");
        EmitSetSocketNonBlocking(state, connectedSocket, "step_tcp_connect_win_nonblocking");
        LlvmApi.BuildStore(builder,
            EmitCompleteLeafTask(state, taskPtr, EmitResultOk(state, connectedSocket), "step_tcp_connect_win_complete"),
            statusSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingBlock);
        LlvmApi.BuildStore(builder,
            EmitPendingLeafTask(state, taskPtr, TaskStructLayout.WaitSocketWrite, LlvmApi.BuildLoad2(builder, state.I64, pendingContextSlot, "step_tcp_connect_win_pending_context"), "step_tcp_connect_win_pending_store"),
            statusSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildStore(builder,
            EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, LlvmApi.BuildLoad2(builder, state.I64, errorRefSlot, "step_tcp_connect_win_error_ref")), "step_tcp_connect_win_fail_complete"),
            statusSlot);
        LlvmApi.BuildBr(builder, doneBlock);
    }

    private static LlvmValueHandle EmitStepTcpSendTaskWindowsIocp(LlvmCodegenState state, LlvmValueHandle taskPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle socket = LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, "step_tcp_send_win_socket");
        LlvmValueHandle textRef = LoadMemory(state, taskPtr, TaskStructLayout.IoArg1, "step_tcp_send_win_text");
        LlvmValueHandle totalLen = LoadStringLength(state, textRef, "step_tcp_send_win_total_len");
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_send_win_status_slot");
        LlvmValueHandle sentSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_send_win_sent_slot");
        LlvmValueHandle pendingContextSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_send_win_pending_context_slot");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildStore(builder, LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tcp_send_win_sent_cached"), sentSlot);
        LlvmValueHandle waitContext = LoadMemory(state, taskPtr, TaskStructLayout.WaitHandle, "step_tcp_send_win_wait_context");
        LlvmApi.BuildStore(builder, waitContext, pendingContextSlot);

        (LlvmBasicBlockHandle resumeBlock, LlvmBasicBlockHandle loopCheckBlock, LlvmBasicBlockHandle doneBlock, LlvmBasicBlockHandle failBlock, LlvmBasicBlockHandle pendingBlock, LlvmBasicBlockHandle completeBlock) = (
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_resume"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_loop_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_done"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_fail"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_pending"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_complete"));
        LlvmApi.BuildCondBr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, waitContext, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_send_win_has_context"),
            resumeBlock,
            loopCheckBlock);

        EmitStepTcpSendWinResume(state, taskPtr, waitContext, sentSlot, resumeBlock, pendingBlock, failBlock, loopCheckBlock);
        EmitStepTcpSendWinLoop(state, taskPtr, socket, textRef, totalLen, sentSlot, pendingContextSlot, loopCheckBlock, completeBlock, failBlock, pendingBlock);
        EmitStepTcpSendWinFinish(state, taskPtr, statusSlot, totalLen, pendingContextSlot, completeBlock, pendingBlock, failBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, statusSlot, "step_tcp_send_win_status");
    }

    private static void EmitStepTcpSendWinResume(
        LlvmCodegenState state,
        LlvmValueHandle taskPtr,
        LlvmValueHandle waitContext,
        LlvmValueHandle sentSlot,
        LlvmBasicBlockHandle resumeBlock,
        LlvmBasicBlockHandle pendingBlock,
        LlvmBasicBlockHandle failBlock,
        LlvmBasicBlockHandle loopCheckBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, resumeBlock);
        LlvmValueHandle resumeStatus = EmitWindowsIocpOperationStatus(state, waitContext, "step_tcp_send_win_resume");
        LlvmValueHandle resumePending = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, resumeStatus, LlvmApi.ConstInt(state.I64, WindowsIocpOperationLayout.StatePending, 0), "step_tcp_send_win_resume_pending");
        LlvmBasicBlockHandle resumeCompletedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_resume_completed");
        LlvmApi.BuildCondBr(builder, resumePending, pendingBlock, resumeCompletedBlock);

        LlvmApi.PositionBuilderAtEnd(builder, resumeCompletedBlock);
        LlvmValueHandle resumeSucceeded = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, resumeStatus, LlvmApi.ConstInt(state.I64, WindowsIocpOperationLayout.StateCompleted, 0), "step_tcp_send_win_resume_succeeded");
        LlvmBasicBlockHandle applyCompletionBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_apply_completion");
        LlvmApi.BuildCondBr(builder, resumeSucceeded, applyCompletionBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, applyCompletionBlock);
        LlvmValueHandle completedBytes = EmitWindowsIocpBytesTransferred(state, waitContext, "step_tcp_send_win_resume");
        LlvmValueHandle completedPositive = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, completedBytes, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_send_win_completed_positive");
        LlvmBasicBlockHandle applyPositiveBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_apply_positive");
        LlvmApi.BuildCondBr(builder, completedPositive, applyPositiveBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, applyPositiveBlock);
        LlvmValueHandle resumedSent = LlvmApi.BuildAdd(builder, LlvmApi.BuildLoad2(builder, state.I64, sentSlot, "step_tcp_send_win_sent_value"), completedBytes, "step_tcp_send_win_sent_after_resume");
        LlvmApi.BuildStore(builder, resumedSent, sentSlot);
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, resumedSent, "step_tcp_send_win_store_sent_after_resume");
        EmitClearLeafTaskWait(state, taskPtr, "step_tcp_send_win_clear_wait_after_resume");
        LlvmApi.BuildBr(builder, loopCheckBlock);
    }

    private static void EmitStepTcpSendWinLoop(
        LlvmCodegenState state,
        LlvmValueHandle taskPtr,
        LlvmValueHandle socket,
        LlvmValueHandle textRef,
        LlvmValueHandle totalLen,
        LlvmValueHandle sentSlot,
        LlvmValueHandle pendingContextSlot,
        LlvmBasicBlockHandle loopCheckBlock,
        LlvmBasicBlockHandle completeBlock,
        LlvmBasicBlockHandle failBlock,
        LlvmBasicBlockHandle pendingBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmBasicBlockHandle issueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_issue");

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle currentSent = LlvmApi.BuildLoad2(builder, state.I64, sentSlot, "step_tcp_send_win_current_sent");
        LlvmValueHandle remaining = LlvmApi.BuildSub(builder, totalLen, currentSent, "step_tcp_send_win_remaining");
        LlvmValueHandle isDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, remaining, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_send_win_is_done");
        LlvmApi.BuildCondBr(builder, isDone, completeBlock, issueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, issueBlock);
        LlvmValueHandle cursorPtr = LlvmApi.BuildIntToPtr(builder,
            LlvmApi.BuildAdd(builder, GetStringBytesAddress(state, textRef, "step_tcp_send_win_base"), currentSent, "step_tcp_send_win_cursor_addr"),
            state.I8Ptr,
            "step_tcp_send_win_cursor_ptr");
        LlvmValueHandle chunkLen = LlvmApi.BuildSelect(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, remaining, LlvmApi.ConstInt(state.I64, int.MaxValue, 0), "step_tcp_send_win_chunk_gt_max"),
            LlvmApi.ConstInt(state.I64, int.MaxValue, 0),
            remaining,
            "step_tcp_send_win_chunk_len");
        LlvmValueHandle sentRaw = LlvmApi.BuildSExt(builder,
            EmitWindowsSend(state, socket, cursorPtr, LlvmApi.BuildTrunc(builder, chunkLen, state.I32, "step_tcp_send_win_chunk_i32"), "step_tcp_send_win_sync_send"),
            state.I64,
            "step_tcp_send_win_sync_sent_raw");
        LlvmValueHandle sentPositive = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, sentRaw, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_send_win_sync_positive");
        LlvmBasicBlockHandle syncSentBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_sync_sent");
        LlvmBasicBlockHandle syncPendingCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_sync_pending_check");
        LlvmApi.BuildCondBr(builder, sentPositive, syncSentBlock, syncPendingCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, syncSentBlock);
        LlvmValueHandle nextSent = LlvmApi.BuildAdd(builder, currentSent, sentRaw, "step_tcp_send_win_next_sent");
        LlvmApi.BuildStore(builder, nextSent, sentSlot);
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, nextSent, "step_tcp_send_win_store_sent_sync");
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, syncPendingCheckBlock);
        LlvmValueHandle syncError = LlvmApi.BuildSExt(builder, EmitWindowsWsaGetLastError(state, "step_tcp_send_win_sync_error"), state.I64, "step_tcp_send_win_sync_error_i64");
        LlvmValueHandle syncWouldBlock = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, syncError, LlvmApi.ConstInt(state.I64, WindowsWsaErrorWouldBlock, 0), "step_tcp_send_win_sync_would_block");
        LlvmBasicBlockHandle issueOverlappedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_issue_overlapped");
        LlvmApi.BuildCondBr(builder, syncWouldBlock, issueOverlappedBlock, failBlock);

        EmitStepTcpSendWinOverlapped(state, taskPtr, socket, currentSent, cursorPtr, chunkLen, sentSlot, pendingContextSlot, issueOverlappedBlock, loopCheckBlock, failBlock, pendingBlock);
    }

    private static void EmitStepTcpSendWinOverlapped(
        LlvmCodegenState state,
        LlvmValueHandle taskPtr,
        LlvmValueHandle socket,
        LlvmValueHandle currentSent,
        LlvmValueHandle cursorPtr,
        LlvmValueHandle chunkLen,
        LlvmValueHandle sentSlot,
        LlvmValueHandle pendingContextSlot,
        LlvmBasicBlockHandle issueOverlappedBlock,
        LlvmBasicBlockHandle loopCheckBlock,
        LlvmBasicBlockHandle failBlock,
        LlvmBasicBlockHandle pendingBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, issueOverlappedBlock);
        EmitWindowsAssociateSocketWithIocp(state, socket, "step_tcp_send_win_associate");
        LlvmValueHandle operationContext = EmitWindowsCreateIocpOperationContext(state, "step_tcp_send_win_op_context");
        LlvmValueHandle bytesSentSlot = LlvmApi.BuildAlloca(builder, state.I32, "step_tcp_send_win_bytes_sent_slot");
        LlvmValueHandle overlappedResult = EmitWindowsIssueWsaSend(state, socket, cursorPtr, chunkLen, EmitWindowsIocpOverlappedPtr(state, operationContext, "step_tcp_send_win_overlapped"), bytesSentSlot, "step_tcp_send_win_issue_wsa_send");
        LlvmValueHandle overlappedImmediate = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, overlappedResult, LlvmApi.ConstInt(state.I32, 0, 0), "step_tcp_send_win_overlapped_immediate");
        LlvmBasicBlockHandle overlappedImmediateBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_overlapped_immediate");
        LlvmBasicBlockHandle overlappedErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_overlapped_error");
        LlvmApi.BuildCondBr(builder, overlappedImmediate, overlappedImmediateBlock, overlappedErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, overlappedImmediateBlock);
        LlvmValueHandle immediateBytes = LlvmApi.BuildZExt(builder, LlvmApi.BuildLoad2(builder, state.I32, bytesSentSlot, "step_tcp_send_win_immediate_bytes"), state.I64, "step_tcp_send_win_immediate_bytes_i64");
        LlvmValueHandle immediatePositive = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, immediateBytes, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_send_win_immediate_positive");
        LlvmBasicBlockHandle applyImmediateBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_apply_immediate");
        LlvmApi.BuildCondBr(builder, immediatePositive, applyImmediateBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, applyImmediateBlock);
        LlvmValueHandle nextImmediateSent = LlvmApi.BuildAdd(builder, currentSent, immediateBytes, "step_tcp_send_win_next_immediate_sent");
        LlvmApi.BuildStore(builder, nextImmediateSent, sentSlot);
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, nextImmediateSent, "step_tcp_send_win_store_sent_immediate");
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, overlappedErrorBlock);
        LlvmValueHandle overlappedError = LlvmApi.BuildSExt(builder, EmitWindowsWsaGetLastError(state, "step_tcp_send_win_overlapped_error_code"), state.I64, "step_tcp_send_win_overlapped_error_i64");
        LlvmValueHandle overlappedPending = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, overlappedError, LlvmApi.ConstInt(state.I64, WindowsErrorIoPending, 0), "step_tcp_send_win_overlapped_pending");
        LlvmBasicBlockHandle storePendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_send_win_store_pending");
        LlvmApi.BuildCondBr(builder, overlappedPending, storePendingBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storePendingBlock);
        LlvmApi.BuildStore(builder, operationContext, pendingContextSlot);
        LlvmApi.BuildBr(builder, pendingBlock);
    }

    private static void EmitStepTcpSendWinFinish(
        LlvmCodegenState state,
        LlvmValueHandle taskPtr,
        LlvmValueHandle statusSlot,
        LlvmValueHandle totalLen,
        LlvmValueHandle pendingContextSlot,
        LlvmBasicBlockHandle completeBlock,
        LlvmBasicBlockHandle pendingBlock,
        LlvmBasicBlockHandle failBlock,
        LlvmBasicBlockHandle doneBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, completeBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultOk(state, totalLen), "step_tcp_send_win_complete_result"), statusSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingBlock);
        LlvmApi.BuildStore(builder, EmitPendingLeafTask(state, taskPtr, TaskStructLayout.WaitSocketWrite, LlvmApi.BuildLoad2(builder, state.I64, pendingContextSlot, "step_tcp_send_win_pending_context"), "step_tcp_send_win_pending_store"), statusSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TcpSendFailedMessage)), "step_tcp_send_win_fail_complete"), statusSlot);
        LlvmApi.BuildBr(builder, doneBlock);
    }

    private static LlvmValueHandle EmitStepTcpReceiveTaskWindowsIocp(LlvmCodegenState state, LlvmValueHandle taskPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle socket = LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, "step_tcp_receive_win_socket");
        LlvmValueHandle maxBytes = LoadMemory(state, taskPtr, TaskStructLayout.IoArg1, "step_tcp_receive_win_max");
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_receive_win_status_slot");
        LlvmValueHandle readCountSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_receive_win_read_count_slot");
        LlvmValueHandle pendingContextSlot = LlvmApi.BuildAlloca(builder, state.I64, "step_tcp_receive_win_pending_context_slot");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), readCountSlot);

        LlvmValueHandle positiveMax = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, maxBytes, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_receive_win_positive_max");
        (LlvmBasicBlockHandle allocateBufferBlock, LlvmBasicBlockHandle failMaxBlock, LlvmBasicBlockHandle handleReadBlock, LlvmBasicBlockHandle finishBlock, LlvmBasicBlockHandle failBlock, LlvmBasicBlockHandle pendingBlock, LlvmBasicBlockHandle resumeBlock, LlvmBasicBlockHandle readBlock) = (
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_allocate_buffer"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_fail_max"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_handle_read"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_finish"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_fail"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_pending"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_resume"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_read_block"));
        LlvmApi.BuildCondBr(builder, positiveMax, allocateBufferBlock, failMaxBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failMaxBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TcpInvalidMaxBytesMessage)), "step_tcp_receive_win_invalid_max"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        EmitStepTcpReceiveWinBufferSetup(state, taskPtr, maxBytes, pendingContextSlot, allocateBufferBlock, resumeBlock, readBlock);
        EmitStepTcpReceiveWinResume(state, taskPtr, readCountSlot, resumeBlock, handleReadBlock, failBlock, pendingBlock);
        EmitStepTcpReceiveWinRead(state, taskPtr, socket, maxBytes, readCountSlot, pendingContextSlot, readBlock, handleReadBlock, failBlock, pendingBlock);
        EmitStepTcpReceiveWinHandle(state, taskPtr, statusSlot, readCountSlot, pendingContextSlot, handleReadBlock, finishBlock, failBlock, pendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finishBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, statusSlot, "step_tcp_receive_win_status");
    }

    private static void EmitStepTcpReceiveWinBufferSetup(
        LlvmCodegenState state,
        LlvmValueHandle taskPtr,
        LlvmValueHandle maxBytes,
        LlvmValueHandle pendingContextSlot,
        LlvmBasicBlockHandle allocateBufferBlock,
        LlvmBasicBlockHandle resumeBlock,
        LlvmBasicBlockHandle readBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, allocateBufferBlock);
        LlvmValueHandle bufferRef = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tcp_receive_win_buffer_ref");
        LlvmValueHandle hasBuffer = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, bufferRef, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_receive_win_has_buffer");
        LlvmBasicBlockHandle reuseBufferBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_reuse_buffer");
        LlvmBasicBlockHandle createBufferBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_create_buffer");
        LlvmBasicBlockHandle afterBufferBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_after_buffer");
        LlvmApi.BuildCondBr(builder, hasBuffer, reuseBufferBlock, createBufferBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createBufferBlock);
        LlvmValueHandle newBufferRef = EmitAllocDynamic(state, LlvmApi.BuildAdd(builder, maxBytes, LlvmApi.ConstInt(state.I64, 8, 0), "step_tcp_receive_win_buffer_size"));
        StoreMemory(state, newBufferRef, 0, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_receive_win_buffer_len_init");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, newBufferRef, "step_tcp_receive_win_store_buffer");
        LlvmApi.BuildBr(builder, afterBufferBlock);

        LlvmApi.PositionBuilderAtEnd(builder, reuseBufferBlock);
        LlvmApi.BuildBr(builder, afterBufferBlock);

        LlvmApi.PositionBuilderAtEnd(builder, afterBufferBlock);
        LlvmValueHandle waitContext = LoadMemory(state, taskPtr, TaskStructLayout.WaitHandle, "step_tcp_receive_win_wait_context");
        LlvmApi.BuildStore(builder, waitContext, pendingContextSlot);
        LlvmApi.BuildCondBr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, waitContext, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_receive_win_has_context"),
            resumeBlock,
            readBlock);
    }

    private static void EmitStepTcpReceiveWinResume(
        LlvmCodegenState state,
        LlvmValueHandle taskPtr,
        LlvmValueHandle readCountSlot,
        LlvmBasicBlockHandle resumeBlock,
        LlvmBasicBlockHandle handleReadBlock,
        LlvmBasicBlockHandle failBlock,
        LlvmBasicBlockHandle pendingBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, resumeBlock);
        LlvmValueHandle waitContext = LoadMemory(state, taskPtr, TaskStructLayout.WaitHandle, "step_tcp_receive_win_wait_context");
        LlvmValueHandle resumeStatus = EmitWindowsIocpOperationStatus(state, waitContext, "step_tcp_receive_win_resume");
        LlvmValueHandle resumePending = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, resumeStatus, LlvmApi.ConstInt(state.I64, WindowsIocpOperationLayout.StatePending, 0), "step_tcp_receive_win_resume_pending");
        LlvmBasicBlockHandle resumeCompletedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_resume_completed");
        LlvmApi.BuildCondBr(builder, resumePending, pendingBlock, resumeCompletedBlock);

        LlvmApi.PositionBuilderAtEnd(builder, resumeCompletedBlock);
        LlvmValueHandle resumeSucceeded = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, resumeStatus, LlvmApi.ConstInt(state.I64, WindowsIocpOperationLayout.StateCompleted, 0), "step_tcp_receive_win_resume_succeeded");
        LlvmBasicBlockHandle applyCompletionBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_apply_completion");
        LlvmApi.BuildCondBr(builder, resumeSucceeded, applyCompletionBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, applyCompletionBlock);
        LlvmApi.BuildStore(builder, EmitWindowsIocpBytesTransferred(state, waitContext, "step_tcp_receive_win_resume"), readCountSlot);
        EmitClearLeafTaskWait(state, taskPtr, "step_tcp_receive_win_clear_wait_after_resume");
        LlvmApi.BuildBr(builder, handleReadBlock);
    }

    private static void EmitStepTcpReceiveWinRead(
        LlvmCodegenState state,
        LlvmValueHandle taskPtr,
        LlvmValueHandle socket,
        LlvmValueHandle maxBytes,
        LlvmValueHandle readCountSlot,
        LlvmValueHandle pendingContextSlot,
        LlvmBasicBlockHandle readBlock,
        LlvmBasicBlockHandle handleReadBlock,
        LlvmBasicBlockHandle failBlock,
        LlvmBasicBlockHandle pendingBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, readBlock);
        LlvmValueHandle activeBuffer = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tcp_receive_win_active_buffer");
        LlvmValueHandle syncReadCount = LlvmApi.BuildSExt(builder, EmitWindowsRecv(state, socket, GetStringBytesPointer(state, activeBuffer, "step_tcp_receive_win_bytes"), LlvmApi.BuildTrunc(builder, maxBytes, state.I32, "step_tcp_receive_win_max_i32"), "step_tcp_receive_win_sync_recv"), state.I64, "step_tcp_receive_win_sync_read_count");
        LlvmValueHandle readOk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, syncReadCount, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_receive_win_sync_ok");
        LlvmBasicBlockHandle syncPendingCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_sync_pending_check");
        LlvmBasicBlockHandle syncReadSuccessBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_sync_read_success");
        LlvmApi.BuildCondBr(builder, readOk, syncReadSuccessBlock, syncPendingCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, syncReadSuccessBlock);
        LlvmApi.BuildStore(builder, syncReadCount, readCountSlot);
        LlvmApi.BuildBr(builder, handleReadBlock);

        LlvmApi.PositionBuilderAtEnd(builder, syncPendingCheckBlock);
        LlvmValueHandle syncError = LlvmApi.BuildSExt(builder, EmitWindowsWsaGetLastError(state, "step_tcp_receive_win_sync_error"), state.I64, "step_tcp_receive_win_sync_error_i64");
        LlvmValueHandle syncWouldBlock = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, syncError, LlvmApi.ConstInt(state.I64, WindowsWsaErrorWouldBlock, 0), "step_tcp_receive_win_sync_would_block");
        LlvmBasicBlockHandle issueOverlappedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_issue_overlapped");
        LlvmApi.BuildCondBr(builder, syncWouldBlock, issueOverlappedBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, issueOverlappedBlock);
        EmitWindowsAssociateSocketWithIocp(state, socket, "step_tcp_receive_win_associate");
        LlvmValueHandle operationContext = EmitWindowsCreateIocpOperationContext(state, "step_tcp_receive_win_op_context");
        LlvmValueHandle bytesReceivedSlot = LlvmApi.BuildAlloca(builder, state.I32, "step_tcp_receive_win_bytes_received_slot");
        LlvmValueHandle overlappedResult = EmitWindowsIssueWsaRecv(state, socket, GetStringBytesPointer(state, activeBuffer, "step_tcp_receive_win_overlapped_bytes"), maxBytes, EmitWindowsIocpOverlappedPtr(state, operationContext, "step_tcp_receive_win_overlapped"), bytesReceivedSlot, "step_tcp_receive_win_issue_wsa_recv");
        LlvmValueHandle overlappedImmediate = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, overlappedResult, LlvmApi.ConstInt(state.I32, 0, 0), "step_tcp_receive_win_overlapped_immediate");
        LlvmBasicBlockHandle overlappedImmediateBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_overlapped_immediate");
        LlvmBasicBlockHandle overlappedErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_overlapped_error");
        LlvmApi.BuildCondBr(builder, overlappedImmediate, overlappedImmediateBlock, overlappedErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, overlappedImmediateBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildZExt(builder, LlvmApi.BuildLoad2(builder, state.I32, bytesReceivedSlot, "step_tcp_receive_win_immediate_bytes"), state.I64, "step_tcp_receive_win_immediate_bytes_i64"), readCountSlot);
        LlvmApi.BuildBr(builder, handleReadBlock);

        LlvmApi.PositionBuilderAtEnd(builder, overlappedErrorBlock);
        LlvmValueHandle overlappedError = LlvmApi.BuildSExt(builder, EmitWindowsWsaGetLastError(state, "step_tcp_receive_win_overlapped_error_code"), state.I64, "step_tcp_receive_win_overlapped_error_i64");
        LlvmValueHandle overlappedPending = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, overlappedError, LlvmApi.ConstInt(state.I64, WindowsErrorIoPending, 0), "step_tcp_receive_win_overlapped_pending");
        LlvmBasicBlockHandle storePendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_store_pending");
        LlvmApi.BuildCondBr(builder, overlappedPending, storePendingBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storePendingBlock);
        LlvmApi.BuildStore(builder, operationContext, pendingContextSlot);
        LlvmApi.BuildBr(builder, pendingBlock);
    }

    private static void EmitStepTcpReceiveWinHandle(
        LlvmCodegenState state,
        LlvmValueHandle taskPtr,
        LlvmValueHandle statusSlot,
        LlvmValueHandle readCountSlot,
        LlvmValueHandle pendingContextSlot,
        LlvmBasicBlockHandle handleReadBlock,
        LlvmBasicBlockHandle finishBlock,
        LlvmBasicBlockHandle failBlock,
        LlvmBasicBlockHandle pendingBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, handleReadBlock);
        LlvmValueHandle activeBuffer = LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, "step_tcp_receive_win_active_buffer");
        LlvmValueHandle readCount = LlvmApi.BuildLoad2(builder, state.I64, readCountSlot, "step_tcp_receive_win_read_count_value");
        StoreMemory(state, activeBuffer, 0, readCount, "step_tcp_receive_win_store_len");
        LlvmValueHandle emptyRead = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, readCount, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_receive_win_empty");
        LlvmBasicBlockHandle successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_success");
        LlvmBasicBlockHandle validateUtf8Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_validate_utf8");
        LlvmApi.BuildCondBr(builder, emptyRead, successBlock, validateUtf8Block);

        LlvmApi.PositionBuilderAtEnd(builder, validateUtf8Block);
        LlvmValueHandle utf8Valid = EmitValidateUtf8(state, GetStringBytesPointer(state, activeBuffer, "step_tcp_receive_win_validate_bytes"), readCount, "step_tcp_receive_win_utf8");
        LlvmValueHandle validUtf8 = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, utf8Valid, LlvmApi.ConstInt(state.I64, 0, 0), "step_tcp_receive_win_valid_utf8");
        LlvmBasicBlockHandle invalidUtf8Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "step_tcp_receive_win_invalid_utf8");
        LlvmApi.BuildCondBr(builder, validUtf8, successBlock, invalidUtf8Block);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultOk(state, activeBuffer), "step_tcp_receive_win_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, invalidUtf8Block);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TcpInvalidUtf8Message)), "step_tcp_receive_win_invalid_utf8_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingBlock);
        LlvmApi.BuildStore(builder, EmitPendingLeafTask(state, taskPtr, TaskStructLayout.WaitSocketRead, LlvmApi.BuildLoad2(builder, state.I64, pendingContextSlot, "step_tcp_receive_win_pending_context"), "step_tcp_receive_win_pending_store"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, TcpReceiveFailedMessage)), "step_tcp_receive_win_fail_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static LlvmValueHandle EmitStepHttpGetTask(LlvmCodegenState state, LlvmValueHandle taskPtr)
        => EmitStepHttpTask(state, taskPtr, hasBody: false, "step_http_get");

    private static LlvmValueHandle EmitStepHttpPostTask(LlvmCodegenState state, LlvmValueHandle taskPtr)
        => EmitStepHttpTask(state, taskPtr, hasBody: true, "step_http_post");

    private static void EmitTlsConnectTcpStage(
        LlvmCodegenState state,
        LlvmBasicBlockHandle stageBlock,
        LlvmValueHandle taskPtr,
        LlvmBasicBlockHandle finishBlock,
        LlvmValueHandle statusSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        const string prefix = "step_tls_connect_tcp";
        LlvmBasicBlockHandle createBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create");
        LlvmBasicBlockHandle stepBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_step");
        LlvmBasicBlockHandle pendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_pending");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");
        LlvmBasicBlockHandle successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_success");
        LlvmBasicBlockHandle errorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_error");

        LlvmApi.PositionBuilderAtEnd(builder, stageBlock);
        LlvmValueHandle awaitedTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_awaited_task");
        LlvmValueHandle hasAwaitedTask = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, awaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_awaited_task");
        LlvmApi.BuildCondBr(builder, hasAwaitedTask, stepBlock, createBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createBlock);
        LlvmValueHandle connectTask = EmitCreateLeafNetworkingTask(
            state,
            TaskStructLayout.StateTcpConnect,
            LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, prefix + "_host"),
            LoadMemory(state, taskPtr, TaskStructLayout.IoArg1, prefix + "_port"),
            prefix + "_child");
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, connectTask, prefix + "_store_child");
        LlvmApi.BuildBr(builder, stepBlock);

        LlvmApi.PositionBuilderAtEnd(builder, stepBlock);
        LlvmValueHandle stepTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_step_task");
        LlvmValueHandle childStatus = EmitNetworkingRuntimeCall(state, "ashes_step_task_until_wait_or_done", [stepTask], prefix + "_child_status");
        LlvmValueHandle childDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, childStatus, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_child_done");
        LlvmApi.BuildCondBr(builder, childDone, doneBlock, pendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingBlock);
        LlvmApi.BuildStore(builder, EmitPendingAwaitedHttpTask(state, taskPtr, stepTask, prefix + "_pending_status"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        EmitClearLeafTaskWait(state, taskPtr, prefix + "_clear_wait");
        LlvmValueHandle childResult = LoadMemory(state, stepTask, TaskStructLayout.ResultSlot, prefix + "_child_result");
        LlvmValueHandle childFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LoadMemory(state, childResult, 0, prefix + "_child_tag"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_child_failed");
        LlvmApi.BuildCondBr(builder, childFailed, errorBlock, successBlock);

        LlvmApi.PositionBuilderAtEnd(builder, errorBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_child_error");
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, childResult, prefix + "_error_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_child_success");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, LoadMemory(state, childResult, 8, prefix + "_socket_value"), prefix + "_store_socket");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData1, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_advance_stage");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static void EmitTlsConnectHandshakeStage(
        LlvmCodegenState state,
        LlvmBasicBlockHandle stageBlock,
        LlvmValueHandle taskPtr,
        LlvmBasicBlockHandle finishBlock,
        LlvmValueHandle statusSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        const string prefix = "step_tls_connect_handshake";
        LlvmBasicBlockHandle createBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create");
        LlvmBasicBlockHandle stepBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_step");
        LlvmBasicBlockHandle pendingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_pending");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");
        LlvmBasicBlockHandle successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_success");
        LlvmBasicBlockHandle errorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_error");

        LlvmApi.PositionBuilderAtEnd(builder, stageBlock);
        LlvmValueHandle awaitedTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_awaited_task");
        LlvmValueHandle hasAwaitedTask = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, awaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_awaited_task");
        LlvmApi.BuildCondBr(builder, hasAwaitedTask, stepBlock, createBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createBlock);
        LlvmValueHandle handshakeTask = EmitCreateLeafNetworkingTask(
            state,
            TaskStructLayout.StateTlsHandshake,
            LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, prefix + "_socket"),
            LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, prefix + "_host"),
            prefix + "_child");
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, handshakeTask, prefix + "_store_child");
        LlvmApi.BuildBr(builder, stepBlock);

        LlvmApi.PositionBuilderAtEnd(builder, stepBlock);
        LlvmValueHandle stepTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_step_task");
        LlvmValueHandle childStatus = EmitNetworkingRuntimeCall(state, "ashes_step_task_until_wait_or_done", [stepTask], prefix + "_child_status");
        LlvmValueHandle childDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, childStatus, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_child_done");
        LlvmApi.BuildCondBr(builder, childDone, doneBlock, pendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingBlock);
        LlvmApi.BuildStore(builder, EmitPendingAwaitedHttpTask(state, taskPtr, stepTask, prefix + "_pending_status"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        EmitClearLeafTaskWait(state, taskPtr, prefix + "_clear_wait");
        LlvmValueHandle childResult = LoadMemory(state, stepTask, TaskStructLayout.ResultSlot, prefix + "_child_result");
        LlvmValueHandle childFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LoadMemory(state, childResult, 0, prefix + "_child_tag"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_child_failed");
        LlvmApi.BuildCondBr(builder, childFailed, errorBlock, successBlock);

        LlvmApi.PositionBuilderAtEnd(builder, errorBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_child_error");
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, childResult, prefix + "_error_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_child_success");
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, childResult, prefix + "_success_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static LlvmValueHandle EmitStepTlsTodoTask(LlvmCodegenState state, LlvmValueHandle taskPtr, string message, string prefix)
    {
        return EmitCompleteLeafTask(
            state,
            taskPtr,
            EmitResultError(state, EmitHeapStringLiteral(state, message)),
            prefix + "_complete");
    }

    private readonly record struct HttpTaskBlocks(
        LlvmBasicBlockHandle Dispatch,
        LlvmBasicBlockHandle Connect,
        LlvmBasicBlockHandle HandshakeCheck,
        LlvmBasicBlockHandle Handshake,
        LlvmBasicBlockHandle SendCheck,
        LlvmBasicBlockHandle Send,
        LlvmBasicBlockHandle ReceiveCheck,
        LlvmBasicBlockHandle Receive,
        LlvmBasicBlockHandle CloseSuccessCheck,
        LlvmBasicBlockHandle CloseSuccess,
        LlvmBasicBlockHandle CloseErrorCheck,
        LlvmBasicBlockHandle CloseError,
        LlvmBasicBlockHandle Invalid,
        LlvmBasicBlockHandle Finish);

    private static HttpTaskBlocks AppendHttpTaskBlocks(LlvmCodegenState state, string prefix)
    {
        return new HttpTaskBlocks(
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_dispatch"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_connect"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_handshake_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_handshake"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_send_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_send"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_receive_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_receive"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_close_success_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_close_success"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_close_error_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_close_error"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_invalid"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_finish"));
    }

    private static LlvmValueHandle EmitStepHttpTask(LlvmCodegenState state, LlvmValueHandle taskPtr, bool hasBody, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle statusSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_status_slot");
        LlvmValueHandle hostSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_host_slot");
        LlvmValueHandle pathSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_path_slot");
        LlvmValueHandle portSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_port_slot");
        LlvmValueHandle schemeSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_scheme_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), statusSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), hostSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), pathSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 80, 0), portSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), schemeSlot);

        HttpTaskBlocks blocks = AppendHttpTaskBlocks(state, prefix);
        LlvmApi.BuildBr(builder, blocks.Dispatch);

        EmitStepHttpTaskDispatch(state, taskPtr, hasBody, hostSlot, pathSlot, portSlot, schemeSlot, statusSlot, prefix, blocks);

        LlvmApi.PositionBuilderAtEnd(builder, blocks.Finish);
        return LlvmApi.BuildLoad2(builder, state.I64, statusSlot, prefix + "_status");
    }

    private static void EmitStepHttpTaskDispatch(
        LlvmCodegenState state,
        LlvmValueHandle taskPtr,
        bool hasBody,
        LlvmValueHandle hostSlot,
        LlvmValueHandle pathSlot,
        LlvmValueHandle portSlot,
        LlvmValueHandle schemeSlot,
        LlvmValueHandle statusSlot,
        string prefix,
        HttpTaskBlocks blocks)
    {
        const long StageConnect = 0;
        const long StageTlsHandshake = 1;
        const long StageSend = 2;
        const long StageReceive = 3;
        const long StageCloseSuccess = 4;
        const long StageCloseError = 5;
        const long StageMask = 7;
        const long StageTlsFlag = 8;

        LlvmBuilderHandle builder = state.Target.Builder;
        (LlvmBasicBlockHandle dispatchBlock, LlvmBasicBlockHandle connectBlock, LlvmBasicBlockHandle handshakeCheckBlock, LlvmBasicBlockHandle handshakeBlock, LlvmBasicBlockHandle sendCheckBlock, LlvmBasicBlockHandle sendBlock, LlvmBasicBlockHandle receiveCheckBlock, LlvmBasicBlockHandle receiveBlock, LlvmBasicBlockHandle closeSuccessCheckBlock, LlvmBasicBlockHandle closeSuccessBlock, LlvmBasicBlockHandle closeErrorCheckBlock, LlvmBasicBlockHandle closeErrorBlock, LlvmBasicBlockHandle invalidBlock, LlvmBasicBlockHandle finishBlock) = blocks;

        LlvmApi.PositionBuilderAtEnd(builder, dispatchBlock);
        LlvmValueHandle stageValue = LoadMemory(state, taskPtr, TaskStructLayout.WaitData1, prefix + "_stage_value");
        LlvmValueHandle stageKind = LlvmApi.BuildAnd(builder, stageValue, LlvmApi.ConstInt(state.I64, StageMask, 0), prefix + "_stage_kind");
        LlvmValueHandle isTlsStage = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            LlvmApi.BuildAnd(builder, stageValue, LlvmApi.ConstInt(state.I64, StageTlsFlag, 0), prefix + "_stage_tls_bits"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            prefix + "_is_tls_stage");
        LlvmValueHandle isConnectStage = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, stageKind, LlvmApi.ConstInt(state.I64, StageConnect, 0), prefix + "_is_connect_stage");
        LlvmApi.BuildCondBr(builder, isConnectStage, connectBlock, handshakeCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, handshakeCheckBlock);
        LlvmValueHandle isHandshakeStage = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, stageKind, LlvmApi.ConstInt(state.I64, StageTlsHandshake, 0), prefix + "_is_handshake_stage");
        LlvmApi.BuildCondBr(builder, isHandshakeStage, handshakeBlock, sendCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, sendCheckBlock);
        LlvmValueHandle isSendStage = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, stageKind, LlvmApi.ConstInt(state.I64, StageSend, 0), prefix + "_is_send_stage");
        LlvmApi.BuildCondBr(builder, isSendStage, sendBlock, receiveCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, receiveCheckBlock);
        LlvmValueHandle isReceiveStage = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, stageKind, LlvmApi.ConstInt(state.I64, StageReceive, 0), prefix + "_is_receive_stage");
        LlvmApi.BuildCondBr(builder, isReceiveStage, receiveBlock, closeSuccessCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeSuccessCheckBlock);
        LlvmValueHandle isCloseSuccessStage = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, stageKind, LlvmApi.ConstInt(state.I64, StageCloseSuccess, 0), prefix + "_is_close_success_stage");
        LlvmApi.BuildCondBr(builder, isCloseSuccessStage, closeSuccessBlock, closeErrorCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeErrorCheckBlock);
        LlvmValueHandle isCloseErrorStage = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, stageKind, LlvmApi.ConstInt(state.I64, StageCloseError, 0), prefix + "_is_close_error_stage");
        LlvmApi.BuildCondBr(builder, isCloseErrorStage, closeErrorBlock, invalidBlock);

        EmitStepHttpTaskStages(state, taskPtr, hasBody, isTlsStage, hostSlot, pathSlot, portSlot, schemeSlot, statusSlot, prefix, connectBlock, handshakeBlock, sendBlock, receiveBlock, closeSuccessBlock, closeErrorBlock, invalidBlock, finishBlock);
    }

    private static void EmitStepHttpTaskStages(
        LlvmCodegenState state,
        LlvmValueHandle taskPtr,
        bool hasBody,
        LlvmValueHandle isTlsStage,
        LlvmValueHandle hostSlot,
        LlvmValueHandle pathSlot,
        LlvmValueHandle portSlot,
        LlvmValueHandle schemeSlot,
        LlvmValueHandle statusSlot,
        string prefix,
        LlvmBasicBlockHandle connectBlock,
        LlvmBasicBlockHandle handshakeBlock,
        LlvmBasicBlockHandle sendBlock,
        LlvmBasicBlockHandle receiveBlock,
        LlvmBasicBlockHandle closeSuccessBlock,
        LlvmBasicBlockHandle closeErrorBlock,
        LlvmBasicBlockHandle invalidBlock,
        LlvmBasicBlockHandle finishBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        EmitHttpStageConnect(state, connectBlock, taskPtr, hostSlot, pathSlot, portSlot, schemeSlot, hasBody, prefix + "_connect_stage", finishBlock, statusSlot);
        EmitHttpStageTlsHandshake(state, handshakeBlock, taskPtr, hostSlot, pathSlot, portSlot, schemeSlot, prefix + "_handshake_stage", finishBlock, statusSlot);
        EmitHttpStageSend(state, sendBlock, taskPtr, hostSlot, pathSlot, portSlot, schemeSlot, isTlsStage, hasBody, prefix + "_send_stage", finishBlock, statusSlot);
        EmitHttpStageReceive(state, receiveBlock, taskPtr, isTlsStage, prefix + "_receive_stage", finishBlock, statusSlot);
        EmitHttpStageClose(state, closeSuccessBlock, taskPtr, isTlsStage, closeOnError: false, prefix + "_close_success_stage", finishBlock, statusSlot);
        EmitHttpStageClose(state, closeErrorBlock, taskPtr, isTlsStage, closeOnError: true, prefix + "_close_error_stage", finishBlock, statusSlot);

        LlvmApi.PositionBuilderAtEnd(builder, invalidBlock);
        LlvmApi.BuildStore(builder,
            EmitCompleteLeafTask(state, taskPtr, EmitResultError(state, EmitHeapStringLiteral(state, "unknown http task stage")), prefix + "_invalid_complete"),
            statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static void EmitHttpStageConnect(
        LlvmCodegenState state,
        LlvmBasicBlockHandle stageBlock,
        LlvmValueHandle taskPtr,
        LlvmValueHandle hostSlot,
        LlvmValueHandle pathSlot,
        LlvmValueHandle portSlot,
        LlvmValueHandle schemeSlot,
        bool hasBody,
        string prefix,
        LlvmBasicBlockHandle finishBlock,
        LlvmValueHandle statusSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        (LlvmBasicBlockHandle createBlock,
            LlvmBasicBlockHandle stepBlock,
            LlvmBasicBlockHandle pendingBlock,
            LlvmBasicBlockHandle doneBlock,
            LlvmBasicBlockHandle successBlock,
            LlvmBasicBlockHandle errorBlock) = (
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_step"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_pending"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_success"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_error"));

        LlvmApi.PositionBuilderAtEnd(builder, stageBlock);
        LlvmValueHandle awaitedTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_awaited_task");
        LlvmValueHandle hasAwaitedTask = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, awaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_awaited_task");
        LlvmApi.BuildCondBr(builder, hasAwaitedTask, stepBlock, createBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createBlock);
        LlvmValueHandle parseError = EmitParseHttpUrl(state, LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, prefix + "_url"), hostSlot, pathSlot, portSlot, schemeSlot, prefix + "_parse_url");
        LlvmValueHandle parseFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, parseError, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_parse_failed");
        LlvmBasicBlockHandle parseFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_fail");
        LlvmBasicBlockHandle parseSuccessBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_success");
        LlvmApi.BuildCondBr(builder, parseFailed, parseFailBlock, parseSuccessBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseFailBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, parseError, prefix + "_parse_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseSuccessBlock);
        LlvmValueHandle isHttpsUrl = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildLoad2(builder, state.I64, schemeSlot, prefix + "_parsed_scheme_value"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_parsed_is_https");
        StoreMemory(state,
            taskPtr,
            TaskStructLayout.WaitData1,
            LlvmApi.BuildSelect(builder, isHttpsUrl, LlvmApi.ConstInt(state.I64, 8, 0), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_connect_stage_value"),
            prefix + "_store_connect_stage_value");
        LlvmValueHandle connectTask = EmitCreateLeafNetworkingTask(state,
            TaskStructLayout.StateTcpConnect,
            LlvmApi.BuildLoad2(builder, state.I64, hostSlot, prefix + "_host_value"),
            LlvmApi.BuildLoad2(builder, state.I64, portSlot, prefix + "_port_value"),
            prefix + "_child");
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, connectTask, prefix + "_store_child");
        LlvmApi.BuildBr(builder, stepBlock);

        EmitHttpStageConnectComplete(state, taskPtr, prefix, stepBlock, pendingBlock, doneBlock, successBlock, errorBlock, finishBlock, statusSlot);
    }

    private static void EmitHttpStageConnectComplete(
        LlvmCodegenState state,
        LlvmValueHandle taskPtr,
        string prefix,
        LlvmBasicBlockHandle stepBlock,
        LlvmBasicBlockHandle pendingBlock,
        LlvmBasicBlockHandle doneBlock,
        LlvmBasicBlockHandle successBlock,
        LlvmBasicBlockHandle errorBlock,
        LlvmBasicBlockHandle finishBlock,
        LlvmValueHandle statusSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, stepBlock);
        LlvmValueHandle stepTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_step_task");
        LlvmValueHandle childStatus = EmitNetworkingRuntimeCall(state, "ashes_step_task_until_wait_or_done", [stepTask], prefix + "_child_status");
        LlvmValueHandle childDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, childStatus, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_child_done");
        LlvmApi.BuildCondBr(builder, childDone, doneBlock, pendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingBlock);
        LlvmApi.BuildStore(builder, EmitPendingAwaitedHttpTask(state, taskPtr, stepTask, prefix + "_pending_status"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        EmitClearLeafTaskWait(state, taskPtr, prefix + "_clear_wait");
        LlvmValueHandle childResult = LoadMemory(state, stepTask, TaskStructLayout.ResultSlot, prefix + "_child_result");
        LlvmValueHandle childFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LoadMemory(state, childResult, 0, prefix + "_child_tag"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_child_failed");
        LlvmApi.BuildCondBr(builder, childFailed, errorBlock, successBlock);

        LlvmApi.PositionBuilderAtEnd(builder, errorBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, childResult, prefix + "_error_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        LlvmValueHandle isHttps = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            LlvmApi.BuildAnd(builder, LoadMemory(state, taskPtr, TaskStructLayout.WaitData1, prefix + "_current_stage_value"), LlvmApi.ConstInt(state.I64, 8, 0), prefix + "_current_stage_tls_bits"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            prefix + "_is_https");
        LlvmBasicBlockHandle handshakeStageBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_handshake_stage");
        LlvmBasicBlockHandle sendStageBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_send_stage");
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_child");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, LoadMemory(state, childResult, 8, prefix + "_socket_value"), prefix + "_socket_store");
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_response");
        LlvmApi.BuildCondBr(builder, isHttps, handshakeStageBlock, sendStageBlock);

        LlvmApi.PositionBuilderAtEnd(builder, handshakeStageBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData1, LlvmApi.ConstInt(state.I64, 9, 0), prefix + "_advance_tls_handshake_stage");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, sendStageBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData1, LlvmApi.ConstInt(state.I64, 2, 0), prefix + "_advance_send_stage");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static void EmitHttpStageTlsHandshake(
        LlvmCodegenState state,
        LlvmBasicBlockHandle stageBlock,
        LlvmValueHandle taskPtr,
        LlvmValueHandle hostSlot,
        LlvmValueHandle pathSlot,
        LlvmValueHandle portSlot,
        LlvmValueHandle schemeSlot,
        string prefix,
        LlvmBasicBlockHandle finishBlock,
        LlvmValueHandle statusSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        (LlvmBasicBlockHandle createBlock,
            LlvmBasicBlockHandle stepBlock,
            LlvmBasicBlockHandle pendingBlock,
            LlvmBasicBlockHandle doneBlock,
            LlvmBasicBlockHandle successBlock,
            LlvmBasicBlockHandle errorBlock) = (
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_step"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_pending"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_success"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_error"));

        LlvmApi.PositionBuilderAtEnd(builder, stageBlock);
        LlvmValueHandle awaitedTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_awaited_task");
        LlvmValueHandle hasAwaitedTask = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, awaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_awaited_task");
        LlvmApi.BuildCondBr(builder, hasAwaitedTask, stepBlock, createBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createBlock);
        LlvmValueHandle parseError = EmitParseHttpUrl(state, LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, prefix + "_url"), hostSlot, pathSlot, portSlot, schemeSlot, prefix + "_parse_url");
        LlvmValueHandle parseFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, parseError, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_parse_failed");
        LlvmBasicBlockHandle parseFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_fail");
        LlvmBasicBlockHandle parseSuccessBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_success");
        LlvmApi.BuildCondBr(builder, parseFailed, parseFailBlock, parseSuccessBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseFailBlock);
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, parseError, prefix + "_parse_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseSuccessBlock);
        LlvmValueHandle handshakeTask = EmitCreateLeafNetworkingTask(state,
            TaskStructLayout.StateTlsHandshake,
            LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, prefix + "_socket_value"),
            LlvmApi.BuildLoad2(builder, state.I64, hostSlot, prefix + "_host_value"),
            prefix + "_child");
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, handshakeTask, prefix + "_store_child");
        LlvmApi.BuildBr(builder, stepBlock);

        EmitHttpStageTlsHandshakeComplete(state, taskPtr, prefix, stepBlock, pendingBlock, doneBlock, successBlock, errorBlock, finishBlock, statusSlot);
    }

    private static void EmitHttpStageTlsHandshakeComplete(
        LlvmCodegenState state,
        LlvmValueHandle taskPtr,
        string prefix,
        LlvmBasicBlockHandle stepBlock,
        LlvmBasicBlockHandle pendingBlock,
        LlvmBasicBlockHandle doneBlock,
        LlvmBasicBlockHandle successBlock,
        LlvmBasicBlockHandle errorBlock,
        LlvmBasicBlockHandle finishBlock,
        LlvmValueHandle statusSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, stepBlock);
        LlvmValueHandle stepTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_step_task");
        LlvmValueHandle childStatus = EmitNetworkingRuntimeCall(state, "ashes_step_task_until_wait_or_done", [stepTask], prefix + "_child_status");
        LlvmValueHandle childDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, childStatus, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_child_done");
        LlvmApi.BuildCondBr(builder, childDone, doneBlock, pendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingBlock);
        LlvmApi.BuildStore(builder, EmitPendingAwaitedHttpTask(state, taskPtr, stepTask, prefix + "_pending_status"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        EmitClearLeafTaskWait(state, taskPtr, prefix + "_clear_wait");
        LlvmValueHandle childResult = LoadMemory(state, stepTask, TaskStructLayout.ResultSlot, prefix + "_child_result");
        LlvmValueHandle childFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LoadMemory(state, childResult, 0, prefix + "_child_tag"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_child_failed");
        LlvmApi.BuildCondBr(builder, childFailed, errorBlock, successBlock);

        LlvmApi.PositionBuilderAtEnd(builder, errorBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_child_error");
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, childResult, prefix + "_error_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_child");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData0, LoadMemory(state, childResult, 8, prefix + "_session_value"), prefix + "_session_store");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitData1, LlvmApi.ConstInt(state.I64, 10, 0), prefix + "_advance_send_stage");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static void EmitHttpStageSend(
        LlvmCodegenState state,
        LlvmBasicBlockHandle stageBlock,
        LlvmValueHandle taskPtr,
        LlvmValueHandle hostSlot,
        LlvmValueHandle pathSlot,
        LlvmValueHandle portSlot,
        LlvmValueHandle schemeSlot,
        LlvmValueHandle isTlsStage,
        bool hasBody,
        string prefix,
        LlvmBasicBlockHandle finishBlock,
        LlvmValueHandle statusSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        (LlvmBasicBlockHandle createBlock,
            LlvmBasicBlockHandle stepBlock,
            LlvmBasicBlockHandle pendingBlock,
            LlvmBasicBlockHandle doneBlock,
            LlvmBasicBlockHandle successBlock,
            LlvmBasicBlockHandle errorBlock) = (
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_step"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_pending"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_success"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_error"));

        LlvmApi.PositionBuilderAtEnd(builder, stageBlock);
        LlvmValueHandle awaitedTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_awaited_task");
        LlvmValueHandle hasAwaitedTask = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, awaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_awaited_task");
        LlvmApi.BuildCondBr(builder, hasAwaitedTask, stepBlock, createBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createBlock);
        LlvmValueHandle parseError = EmitParseHttpUrl(state, LoadMemory(state, taskPtr, TaskStructLayout.IoArg0, prefix + "_url"), hostSlot, pathSlot, portSlot, schemeSlot, prefix + "_parse_url");
        LlvmValueHandle parseFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, parseError, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_parse_failed");
        LlvmBasicBlockHandle parseFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_fail");
        LlvmBasicBlockHandle parseSuccessBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_success");
        LlvmApi.BuildCondBr(builder, parseFailed, parseFailBlock, parseSuccessBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseFailBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot, parseError, prefix + "_store_parse_error");
        StoreMemory(state, taskPtr,
            TaskStructLayout.WaitData1,
            LlvmApi.BuildSelect(builder, isTlsStage, LlvmApi.ConstInt(state.I64, 13, 0), LlvmApi.ConstInt(state.I64, 5, 0), prefix + "_close_error_stage"),
            prefix + "_advance_close_error");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        EmitHttpStageSendCreate(state, taskPtr, hostSlot, pathSlot, isTlsStage, hasBody, prefix, parseSuccessBlock, stepBlock);
        EmitHttpStageSendComplete(state, taskPtr, isTlsStage, prefix, stepBlock, pendingBlock, doneBlock, successBlock, errorBlock, finishBlock, statusSlot);
    }

    private static void EmitHttpStageSendCreate(
        LlvmCodegenState state,
        LlvmValueHandle taskPtr,
        LlvmValueHandle hostSlot,
        LlvmValueHandle pathSlot,
        LlvmValueHandle isTlsStage,
        bool hasBody,
        string prefix,
        LlvmBasicBlockHandle parseSuccessBlock,
        LlvmBasicBlockHandle stepBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, parseSuccessBlock);
        LlvmValueHandle requestRef = EmitHttpRequestString(
            state,
            LlvmApi.BuildLoad2(builder, state.I64, pathSlot, prefix + "_path_value"),
            LlvmApi.BuildLoad2(builder, state.I64, hostSlot, prefix + "_host_value"),
            LoadMemory(state, taskPtr, TaskStructLayout.IoArg1, prefix + "_body_value"),
            hasBody);
        LlvmBasicBlockHandle createTlsChildBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create_tls_child");
        LlvmBasicBlockHandle createTcpChildBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create_tcp_child");
        LlvmBasicBlockHandle createdChildBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_created_child");
        LlvmApi.BuildCondBr(builder, isTlsStage, createTlsChildBlock, createTcpChildBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createTlsChildBlock);
        StoreMemory(state,
            taskPtr,
            TaskStructLayout.AwaitedTask,
            EmitCreateLeafNetworkingTask(state,
                TaskStructLayout.StateTlsSend,
                LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, prefix + "_tls_socket_value"),
                requestRef,
                prefix + "_tls_child"),
            prefix + "_store_tls_child");
        LlvmApi.BuildBr(builder, createdChildBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createTcpChildBlock);
        StoreMemory(state,
            taskPtr,
            TaskStructLayout.AwaitedTask,
            EmitCreateLeafNetworkingTask(state,
                TaskStructLayout.StateTcpSend,
                LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, prefix + "_tcp_socket_value"),
                requestRef,
                prefix + "_tcp_child"),
            prefix + "_store_tcp_child");
        LlvmApi.BuildBr(builder, createdChildBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createdChildBlock);
        LlvmApi.BuildBr(builder, stepBlock);
    }

    private static void EmitHttpStageSendComplete(
        LlvmCodegenState state,
        LlvmValueHandle taskPtr,
        LlvmValueHandle isTlsStage,
        string prefix,
        LlvmBasicBlockHandle stepBlock,
        LlvmBasicBlockHandle pendingBlock,
        LlvmBasicBlockHandle doneBlock,
        LlvmBasicBlockHandle successBlock,
        LlvmBasicBlockHandle errorBlock,
        LlvmBasicBlockHandle finishBlock,
        LlvmValueHandle statusSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, stepBlock);
        LlvmValueHandle stepTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_step_task");
        LlvmValueHandle childStatus = EmitNetworkingRuntimeCall(state, "ashes_step_task_until_wait_or_done", [stepTask], prefix + "_child_status");
        LlvmValueHandle childDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, childStatus, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_child_done");
        LlvmApi.BuildCondBr(builder, childDone, doneBlock, pendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingBlock);
        LlvmApi.BuildStore(builder, EmitPendingAwaitedHttpTask(state, taskPtr, stepTask, prefix + "_pending_status"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        EmitClearLeafTaskWait(state, taskPtr, prefix + "_clear_wait");
        LlvmValueHandle childResult = LoadMemory(state, stepTask, TaskStructLayout.ResultSlot, prefix + "_child_result");
        LlvmValueHandle childFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LoadMemory(state, childResult, 0, prefix + "_child_tag"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_child_failed");
        LlvmApi.BuildCondBr(builder, childFailed, errorBlock, successBlock);

        LlvmApi.PositionBuilderAtEnd(builder, errorBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_child_error");
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot, childResult, prefix + "_store_error");
        StoreMemory(state, taskPtr,
            TaskStructLayout.WaitData1,
            LlvmApi.BuildSelect(builder, isTlsStage, LlvmApi.ConstInt(state.I64, 13, 0), LlvmApi.ConstInt(state.I64, 5, 0), prefix + "_error_stage"),
            prefix + "_advance_close_error");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_child");
        StoreMemory(state, taskPtr,
            TaskStructLayout.WaitData1,
            LlvmApi.BuildSelect(builder, isTlsStage, LlvmApi.ConstInt(state.I64, 11, 0), LlvmApi.ConstInt(state.I64, 3, 0), prefix + "_receive_stage"),
            prefix + "_advance_stage");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static void EmitHttpStageReceive(
        LlvmCodegenState state,
        LlvmBasicBlockHandle stageBlock,
        LlvmValueHandle taskPtr,
        LlvmValueHandle isTlsStage,
        string prefix,
        LlvmBasicBlockHandle finishBlock,
        LlvmValueHandle statusSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        (LlvmBasicBlockHandle createBlock,
            LlvmBasicBlockHandle stepBlock,
            LlvmBasicBlockHandle pendingBlock,
            LlvmBasicBlockHandle doneBlock,
            LlvmBasicBlockHandle errorBlock,
            LlvmBasicBlockHandle inspectBlock,
            LlvmBasicBlockHandle chunkEmptyBlock,
            LlvmBasicBlockHandle appendBlock) = (
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_step"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_pending"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_error"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_inspect"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_chunk_empty"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_append"));

        LlvmApi.PositionBuilderAtEnd(builder, stageBlock);
        LlvmValueHandle awaitedTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_awaited_task");
        LlvmValueHandle hasAwaitedTask = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, awaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_awaited_task");
        LlvmApi.BuildCondBr(builder, hasAwaitedTask, stepBlock, createBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createBlock);
        LlvmBasicBlockHandle createTlsChildBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create_tls_child");
        LlvmBasicBlockHandle createTcpChildBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create_tcp_child");
        LlvmBasicBlockHandle createdChildBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_created_child");
        LlvmApi.BuildCondBr(builder, isTlsStage, createTlsChildBlock, createTcpChildBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createTlsChildBlock);
        StoreMemory(state,
            taskPtr,
            TaskStructLayout.AwaitedTask,
            EmitCreateLeafNetworkingTask(state,
                TaskStructLayout.StateTlsReceive,
                LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, prefix + "_tls_socket_value"),
                LlvmApi.ConstInt(state.I64, 65536, 0),
                prefix + "_tls_child"),
            prefix + "_store_tls_child");
        LlvmApi.BuildBr(builder, createdChildBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createTcpChildBlock);
        StoreMemory(state,
            taskPtr,
            TaskStructLayout.AwaitedTask,
            EmitCreateLeafNetworkingTask(state,
                TaskStructLayout.StateTcpReceive,
                LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, prefix + "_tcp_socket_value"),
                LlvmApi.ConstInt(state.I64, 65536, 0),
                prefix + "_tcp_child"),
            prefix + "_store_tcp_child");
        LlvmApi.BuildBr(builder, createdChildBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createdChildBlock);
        LlvmApi.BuildBr(builder, stepBlock);

        EmitHttpStageReceiveComplete(state, taskPtr, isTlsStage, prefix, stepBlock, pendingBlock, doneBlock, errorBlock, inspectBlock, chunkEmptyBlock, appendBlock, finishBlock, statusSlot);
    }

    private static void EmitHttpStageReceiveComplete(
        LlvmCodegenState state,
        LlvmValueHandle taskPtr,
        LlvmValueHandle isTlsStage,
        string prefix,
        LlvmBasicBlockHandle stepBlock,
        LlvmBasicBlockHandle pendingBlock,
        LlvmBasicBlockHandle doneBlock,
        LlvmBasicBlockHandle errorBlock,
        LlvmBasicBlockHandle inspectBlock,
        LlvmBasicBlockHandle chunkEmptyBlock,
        LlvmBasicBlockHandle appendBlock,
        LlvmBasicBlockHandle finishBlock,
        LlvmValueHandle statusSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, stepBlock);
        LlvmValueHandle stepTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_step_task");
        LlvmValueHandle childStatus = EmitNetworkingRuntimeCall(state, "ashes_step_task_until_wait_or_done", [stepTask], prefix + "_child_status");
        LlvmValueHandle childDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, childStatus, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_child_done");
        LlvmApi.BuildCondBr(builder, childDone, doneBlock, pendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingBlock);
        LlvmApi.BuildStore(builder, EmitPendingAwaitedHttpTask(state, taskPtr, stepTask, prefix + "_pending_status"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        EmitClearLeafTaskWait(state, taskPtr, prefix + "_clear_wait");
        LlvmValueHandle childResult = LoadMemory(state, stepTask, TaskStructLayout.ResultSlot, prefix + "_child_result");
        LlvmValueHandle childFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LoadMemory(state, childResult, 0, prefix + "_child_tag"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_child_failed");
        LlvmApi.BuildCondBr(builder, childFailed, errorBlock, inspectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, errorBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_child_error");
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot, childResult, prefix + "_store_error");
        StoreMemory(state, taskPtr,
            TaskStructLayout.WaitData1,
            LlvmApi.BuildSelect(builder, isTlsStage, LlvmApi.ConstInt(state.I64, 13, 0), LlvmApi.ConstInt(state.I64, 5, 0), prefix + "_error_stage"),
            prefix + "_advance_close_error");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, inspectBlock);
        LlvmValueHandle chunkRef = LoadMemory(state, childResult, 8, prefix + "_chunk_ref");
        LlvmValueHandle chunkLen = LoadStringLength(state, chunkRef, prefix + "_chunk_len");
        LlvmValueHandle chunkEmpty = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, chunkLen, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_chunk_empty_bool");
        LlvmApi.BuildCondBr(builder, chunkEmpty, chunkEmptyBlock, appendBlock);

        LlvmApi.PositionBuilderAtEnd(builder, chunkEmptyBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_child_empty");
        StoreMemory(state, taskPtr,
            TaskStructLayout.WaitData1,
            LlvmApi.BuildSelect(builder, isTlsStage, LlvmApi.ConstInt(state.I64, 12, 0), LlvmApi.ConstInt(state.I64, 4, 0), prefix + "_close_success_stage"),
            prefix + "_advance_close_success");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        EmitHttpStageReceiveAppend(state, taskPtr, prefix, chunkRef, appendBlock, finishBlock, statusSlot);
    }

    private static void EmitHttpStageReceiveAppend(
        LlvmCodegenState state,
        LlvmValueHandle taskPtr,
        string prefix,
        LlvmValueHandle chunkRef,
        LlvmBasicBlockHandle appendBlock,
        LlvmBasicBlockHandle finishBlock,
        LlvmValueHandle statusSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, appendBlock);
        LlvmValueHandle currentResponse = LoadMemory(state, taskPtr, TaskStructLayout.ResultSlot, prefix + "_current_response");
        LlvmValueHandle hasResponse = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, currentResponse, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_response");
        LlvmBasicBlockHandle concatBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_concat_response");
        LlvmBasicBlockHandle firstChunkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_first_chunk");
        LlvmApi.BuildCondBr(builder, hasResponse, concatBlock, firstChunkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, concatBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot, EmitStringConcat(state, currentResponse, chunkRef), prefix + "_store_concat_response");
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_child_concat");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, firstChunkBlock);
        StoreMemory(state, taskPtr, TaskStructLayout.ResultSlot, chunkRef, prefix + "_store_first_chunk");
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_child_first_chunk");
        LlvmApi.BuildStore(builder, EmitLeafTaskPendingStatus(state), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static void EmitHttpStageClose(
        LlvmCodegenState state,
        LlvmBasicBlockHandle stageBlock,
        LlvmValueHandle taskPtr,
        LlvmValueHandle isTlsStage,
        bool closeOnError,
        string prefix,
        LlvmBasicBlockHandle finishBlock,
        LlvmValueHandle statusSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        (LlvmBasicBlockHandle createBlock,
            LlvmBasicBlockHandle stepBlock,
            LlvmBasicBlockHandle pendingBlock,
            LlvmBasicBlockHandle doneBlock,
            LlvmBasicBlockHandle closeSuccessBlock,
            LlvmBasicBlockHandle closeFailureBlock) = (
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_step"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_pending"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_close_success"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_close_failure"));

        LlvmApi.PositionBuilderAtEnd(builder, stageBlock);
        LlvmValueHandle awaitedTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_awaited_task");
        LlvmValueHandle hasAwaitedTask = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, awaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_awaited_task");
        LlvmApi.BuildCondBr(builder, hasAwaitedTask, stepBlock, createBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createBlock);
        LlvmBasicBlockHandle createTlsChildBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create_tls_child");
        LlvmBasicBlockHandle createTcpChildBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create_tcp_child");
        LlvmBasicBlockHandle createdChildBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_created_child");
        LlvmApi.BuildCondBr(builder, isTlsStage, createTlsChildBlock, createTcpChildBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createTlsChildBlock);
        StoreMemory(state,
            taskPtr,
            TaskStructLayout.AwaitedTask,
            EmitCreateLeafNetworkingTask(state,
                TaskStructLayout.StateTlsClose,
                LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, prefix + "_tls_socket_value"),
                LlvmApi.ConstInt(state.I64, 0, 0),
                prefix + "_tls_child"),
            prefix + "_store_tls_child");
        LlvmApi.BuildBr(builder, createdChildBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createTcpChildBlock);
        StoreMemory(state,
            taskPtr,
            TaskStructLayout.AwaitedTask,
            EmitCreateLeafNetworkingTask(state,
                TaskStructLayout.StateTcpClose,
                LoadMemory(state, taskPtr, TaskStructLayout.WaitData0, prefix + "_tcp_socket_value"),
                LlvmApi.ConstInt(state.I64, 0, 0),
                prefix + "_tcp_child"),
            prefix + "_store_tcp_child");
        LlvmApi.BuildBr(builder, createdChildBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createdChildBlock);
        LlvmApi.BuildBr(builder, stepBlock);

        EmitHttpStageCloseComplete(state, taskPtr, closeOnError, prefix, stepBlock, pendingBlock, doneBlock, closeSuccessBlock, closeFailureBlock, finishBlock, statusSlot);
    }

    private static void EmitHttpStageCloseComplete(
        LlvmCodegenState state,
        LlvmValueHandle taskPtr,
        bool closeOnError,
        string prefix,
        LlvmBasicBlockHandle stepBlock,
        LlvmBasicBlockHandle pendingBlock,
        LlvmBasicBlockHandle doneBlock,
        LlvmBasicBlockHandle closeSuccessBlock,
        LlvmBasicBlockHandle closeFailureBlock,
        LlvmBasicBlockHandle finishBlock,
        LlvmValueHandle statusSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, stepBlock);
        LlvmValueHandle stepTask = LoadMemory(state, taskPtr, TaskStructLayout.AwaitedTask, prefix + "_step_task");
        LlvmValueHandle childStatus = EmitNetworkingRuntimeCall(state, "ashes_step_task_until_wait_or_done", [stepTask], prefix + "_child_status");
        LlvmValueHandle childDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, childStatus, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_child_done");
        LlvmApi.BuildCondBr(builder, childDone, doneBlock, pendingBlock);

        LlvmApi.PositionBuilderAtEnd(builder, pendingBlock);
        LlvmApi.BuildStore(builder, EmitPendingAwaitedHttpTask(state, taskPtr, stepTask, prefix + "_pending_status"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        EmitClearLeafTaskWait(state, taskPtr, prefix + "_clear_wait");
        LlvmValueHandle childResult = LoadMemory(state, stepTask, TaskStructLayout.ResultSlot, prefix + "_child_result");
        StoreMemory(state, taskPtr, TaskStructLayout.AwaitedTask, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_child");
        LlvmValueHandle childFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LoadMemory(state, childResult, 0, prefix + "_child_tag"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_child_failed");
        LlvmApi.BuildCondBr(builder, childFailed, closeFailureBlock, closeSuccessBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeFailureBlock);
        LlvmValueHandle failureResult = closeOnError
            ? LoadMemory(state, taskPtr, TaskStructLayout.ResultSlot, prefix + "_stored_failure")
            : childResult;
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, failureResult, prefix + "_failure_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeSuccessBlock);
        LlvmValueHandle finalResult = closeOnError
            ? LoadMemory(state, taskPtr, TaskStructLayout.ResultSlot, prefix + "_error_result")
            : EmitParseHttpResponseResult(state, LoadMemory(state, taskPtr, TaskStructLayout.ResultSlot, prefix + "_response_ref"), prefix + "_parse_response");
        LlvmApi.BuildStore(builder, EmitCompleteLeafTask(state, taskPtr, finalResult, prefix + "_success_complete"), statusSlot);
        LlvmApi.BuildBr(builder, finishBlock);
    }

    private static LlvmValueHandle EmitPendingAwaitedHttpTask(LlvmCodegenState state, LlvmValueHandle taskPtr, LlvmValueHandle awaitedTask, string prefix)
    {
        StoreMemory(state, taskPtr, TaskStructLayout.WaitKind, LoadMemory(state, awaitedTask, TaskStructLayout.WaitKind, prefix + "_wait_kind"), prefix + "_store_wait_kind");
        StoreMemory(state, taskPtr, TaskStructLayout.WaitHandle, LoadMemory(state, awaitedTask, TaskStructLayout.WaitHandle, prefix + "_wait_handle"), prefix + "_store_wait_handle");
        return EmitLeafTaskPendingStatus(state);
    }

    private static LlvmValueHandle EmitParseHttpUrl(LlvmCodegenState state, LlvmValueHandle urlRef, LlvmValueHandle hostSlot, LlvmValueHandle pathSlot, LlvmValueHandle portSlot, LlvmValueHandle schemeSlot, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_result_slot");
        LlvmValueHandle portValueSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_port_value_slot");
        LlvmValueHandle portIndexSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_port_index_slot");
        LlvmValueHandle hostEndSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_host_end_slot");
        LlvmValueHandle schemeOffsetSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_scheme_offset_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 80, 0), portSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 80, 0), portValueSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), portIndexSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), hostEndSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), schemeOffsetSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), schemeSlot);

        LlvmValueHandle urlLen = LoadStringLength(state, urlRef, prefix + "_len");
        LlvmValueHandle urlBytes = GetStringBytesPointer(state, urlRef, prefix + "_bytes");
        LlvmValueHandle httpsPrefix = EmitHeapStringLiteral(state, "https://");
        LlvmValueHandle httpPrefix = EmitHeapStringLiteral(state, "http://");

        (LlvmBasicBlockHandle httpsCheckBlock,
            LlvmBasicBlockHandle findSlashBlock,
            LlvmBasicBlockHandle parsePortCheckBlock,
            LlvmBasicBlockHandle buildPathBlock,
            LlvmBasicBlockHandle malformedBlock,
            LlvmBasicBlockHandle doneBlock) = (
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_https_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_find_slash"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_port_check"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_build_path"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_malformed"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done"));

        LlvmApi.BuildBr(builder, httpsCheckBlock);

        EmitParseHttpUrlScheme(state, prefix, urlRef, httpsPrefix, httpPrefix, portSlot, schemeSlot, portValueSlot, schemeOffsetSlot, httpsCheckBlock, findSlashBlock, malformedBlock);
        EmitParseHttpUrlHost(state, prefix, urlBytes, urlLen, hostSlot, pathSlot, portSlot, schemeSlot, resultSlot, portValueSlot, portIndexSlot, hostEndSlot, schemeOffsetSlot, findSlashBlock, parsePortCheckBlock, buildPathBlock, malformedBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, malformedBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, HttpMalformedUrlMessage)), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, prefix + "_result");
    }

    private static void EmitParseHttpUrlScheme(
        LlvmCodegenState state,
        string prefix,
        LlvmValueHandle urlRef,
        LlvmValueHandle httpsPrefix,
        LlvmValueHandle httpPrefix,
        LlvmValueHandle portSlot,
        LlvmValueHandle schemeSlot,
        LlvmValueHandle portValueSlot,
        LlvmValueHandle schemeOffsetSlot,
        LlvmBasicBlockHandle httpsCheckBlock,
        LlvmBasicBlockHandle findSlashBlock,
        LlvmBasicBlockHandle malformedBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmBasicBlockHandle httpsBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_https");
        LlvmBasicBlockHandle httpCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_http_check");
        LlvmBasicBlockHandle httpBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_http");

        LlvmApi.PositionBuilderAtEnd(builder, httpsCheckBlock);
        LlvmValueHandle isHttps = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, EmitStartsWith(state, urlRef, httpsPrefix, prefix + "_is_https"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_https_bool");
        LlvmApi.BuildCondBr(builder, isHttps, httpsBlock, httpCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, httpsBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), schemeSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 443, 0), portSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 443, 0), portValueSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 8, 0), schemeOffsetSlot);
        LlvmApi.BuildBr(builder, findSlashBlock);

        LlvmApi.PositionBuilderAtEnd(builder, httpCheckBlock);
        LlvmValueHandle isHttp = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, EmitStartsWith(state, urlRef, httpPrefix, prefix + "_is_http"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_http_bool");
        LlvmApi.BuildCondBr(builder, isHttp, httpBlock, malformedBlock);

        LlvmApi.PositionBuilderAtEnd(builder, httpBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), schemeSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 80, 0), portSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 80, 0), portValueSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 7, 0), schemeOffsetSlot);
        LlvmApi.BuildBr(builder, findSlashBlock);
    }

    private static void EmitParseHttpUrlHost(
        LlvmCodegenState state,
        string prefix,
        LlvmValueHandle urlBytes,
        LlvmValueHandle urlLen,
        LlvmValueHandle hostSlot,
        LlvmValueHandle pathSlot,
        LlvmValueHandle portSlot,
        LlvmValueHandle schemeSlot,
        LlvmValueHandle resultSlot,
        LlvmValueHandle portValueSlot,
        LlvmValueHandle portIndexSlot,
        LlvmValueHandle hostEndSlot,
        LlvmValueHandle schemeOffsetSlot,
        LlvmBasicBlockHandle findSlashBlock,
        LlvmBasicBlockHandle parsePortCheckBlock,
        LlvmBasicBlockHandle buildPathBlock,
        LlvmBasicBlockHandle malformedBlock,
        LlvmBasicBlockHandle doneBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, findSlashBlock);
        LlvmValueHandle schemeOffset = LlvmApi.BuildLoad2(builder, state.I64, schemeOffsetSlot, prefix + "_scheme_offset");
        LlvmValueHandle isHttpsScheme = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildLoad2(builder, state.I64, schemeSlot, prefix + "_scheme_value"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_scheme_is_https");
        LlvmValueHandle slashIndexHttp = EmitFindByte(state, urlBytes, urlLen, 7, (byte)'/', prefix + "_slash_index_http");
        LlvmValueHandle slashIndexHttps = EmitFindByte(state, urlBytes, urlLen, 8, (byte)'/', prefix + "_slash_index_https");
        LlvmValueHandle slashIndex = LlvmApi.BuildSelect(builder, isHttpsScheme, slashIndexHttps, slashIndexHttp, prefix + "_slash_index");
        LlvmValueHandle hasSlash = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, slashIndex, LlvmApi.ConstInt(state.I64, 0, 1), prefix + "_has_slash");
        LlvmValueHandle hostSearchEnd = LlvmApi.BuildSelect(builder, hasSlash, slashIndex, urlLen, prefix + "_host_search_end");
        LlvmValueHandle colonIndexHttp = EmitFindByte(state, urlBytes, hostSearchEnd, 7, (byte)':', prefix + "_colon_index_http");
        LlvmValueHandle colonIndexHttps = EmitFindByte(state, urlBytes, hostSearchEnd, 8, (byte)':', prefix + "_colon_index_https");
        LlvmValueHandle colonIndex = LlvmApi.BuildSelect(builder, isHttpsScheme, colonIndexHttps, colonIndexHttp, prefix + "_colon_index");
        LlvmValueHandle hasColon = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, colonIndex, LlvmApi.ConstInt(state.I64, 0, 1), prefix + "_has_colon");
        LlvmValueHandle hostEnd = LlvmApi.BuildSelect(builder, hasColon, colonIndex, hostSearchEnd, prefix + "_host_end");
        LlvmValueHandle hostLen = LlvmApi.BuildSub(builder, hostEnd, schemeOffset, prefix + "_host_len");
        LlvmValueHandle missingHost = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, hostLen, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_missing_host");
        LlvmBasicBlockHandle storeHostBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_store_host");
        LlvmApi.BuildCondBr(builder, missingHost, malformedBlock, storeHostBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storeHostBlock);
        LlvmApi.BuildStore(builder, hostEnd, hostEndSlot);
        LlvmApi.BuildStore(builder, EmitHeapStringSliceFromBytesPointer(state, LlvmApi.BuildGEP2(builder, state.I8, urlBytes, [schemeOffset], prefix + "_host_ptr"), hostLen, prefix + "_host"), hostSlot);
        LlvmApi.BuildCondBr(builder, hasColon, parsePortCheckBlock, buildPathBlock);

        EmitParseHttpUrlPort(state, prefix, urlBytes, urlLen, pathSlot, portSlot, resultSlot, portValueSlot, portIndexSlot, colonIndex, hostSearchEnd, hasSlash, slashIndex, parsePortCheckBlock, buildPathBlock, malformedBlock, doneBlock);
    }

    private static void EmitParseHttpUrlPort(
        LlvmCodegenState state,
        string prefix,
        LlvmValueHandle urlBytes,
        LlvmValueHandle urlLen,
        LlvmValueHandle pathSlot,
        LlvmValueHandle portSlot,
        LlvmValueHandle resultSlot,
        LlvmValueHandle portValueSlot,
        LlvmValueHandle portIndexSlot,
        LlvmValueHandle colonIndex,
        LlvmValueHandle hostSearchEnd,
        LlvmValueHandle hasSlash,
        LlvmValueHandle slashIndex,
        LlvmBasicBlockHandle parsePortCheckBlock,
        LlvmBasicBlockHandle buildPathBlock,
        LlvmBasicBlockHandle malformedBlock,
        LlvmBasicBlockHandle doneBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmBasicBlockHandle parsePortLoopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_port_loop");
        LlvmBasicBlockHandle parsePortBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_port_body");

        LlvmApi.PositionBuilderAtEnd(builder, parsePortCheckBlock);
        LlvmValueHandle portStart = LlvmApi.BuildAdd(builder, colonIndex, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_port_start");
        LlvmValueHandle emptyPort = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, portStart, hostSearchEnd, prefix + "_empty_port");
        LlvmBasicBlockHandle parsePortInitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_port_init");
        LlvmApi.BuildCondBr(builder, emptyPort, malformedBlock, parsePortInitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parsePortInitBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), portValueSlot);
        LlvmApi.BuildStore(builder, portStart, portIndexSlot);
        LlvmApi.BuildBr(builder, parsePortLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parsePortLoopBlock);
        LlvmValueHandle portIndex = LlvmApi.BuildLoad2(builder, state.I64, portIndexSlot, prefix + "_port_index");
        LlvmValueHandle portDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, portIndex, hostSearchEnd, prefix + "_port_done");
        LlvmApi.BuildCondBr(builder, portDone, buildPathBlock, parsePortBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parsePortBodyBlock);
        LlvmValueHandle portByte = LoadByteAt(state, urlBytes, portIndex, prefix + "_port_byte");
        LlvmValueHandle digitValue = LlvmApi.BuildZExt(builder, portByte, state.I64, prefix + "_digit_value");
        LlvmValueHandle isDigit = BuildByteRangeCheck(state, digitValue, (byte)'0', (byte)'9', prefix + "_is_digit");
        LlvmBasicBlockHandle storeDigitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_store_digit");
        LlvmApi.BuildCondBr(builder, isDigit, storeDigitBlock, malformedBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storeDigitBlock);
        LlvmValueHandle currentPort = LlvmApi.BuildLoad2(builder, state.I64, portValueSlot, prefix + "_current_port");
        LlvmValueHandle parsedDigit = LlvmApi.BuildSub(builder, digitValue, LlvmApi.ConstInt(state.I64, (byte)'0', 0), prefix + "_parsed_digit");
        LlvmValueHandle nextPort = LlvmApi.BuildAdd(builder, LlvmApi.BuildMul(builder, currentPort, LlvmApi.ConstInt(state.I64, 10, 0), prefix + "_port_mul"), parsedDigit, prefix + "_next_port");
        LlvmValueHandle portTooLarge = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, nextPort, LlvmApi.ConstInt(state.I64, 65535, 0), prefix + "_port_too_large");
        LlvmBasicBlockHandle storePortBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_store_port");
        LlvmApi.BuildCondBr(builder, portTooLarge, malformedBlock, storePortBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storePortBlock);
        LlvmApi.BuildStore(builder, nextPort, portValueSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, portIndex, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_port_index_next"), portIndexSlot);
        LlvmApi.BuildBr(builder, parsePortLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, buildPathBlock);
        LlvmValueHandle finalPort = LlvmApi.BuildLoad2(builder, state.I64, portValueSlot, prefix + "_final_port");
        LlvmApi.BuildStore(builder, finalPort, portSlot);
        LlvmValueHandle pathRef = LlvmApi.BuildSelect(builder,
            hasSlash,
            EmitHeapStringSliceFromBytesPointer(state, LlvmApi.BuildGEP2(builder, state.I8, urlBytes, [slashIndex], prefix + "_path_ptr"), LlvmApi.BuildSub(builder, urlLen, slashIndex, prefix + "_path_len"), prefix + "_path"),
            EmitHeapStringLiteral(state, "/"),
            prefix + "_path_ref");
        LlvmApi.BuildStore(builder, pathRef, pathSlot);
        LlvmApi.BuildBr(builder, doneBlock);
    }

    private static LlvmValueHandle EmitParseHttpResponseResult(LlvmCodegenState state, LlvmValueHandle responseRef, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_result_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

        (LlvmBasicBlockHandle prepareBlock,
            LlvmBasicBlockHandle parseBlock,
            LlvmBasicBlockHandle parseStatusBlock,
            LlvmBasicBlockHandle parseDigitsBlock,
            LlvmBasicBlockHandle detectChunkedBlock,
            LlvmBasicBlockHandle buildBodyBlock,
            LlvmBasicBlockHandle chunkedBlock,
            LlvmBasicBlockHandle successBlock,
            LlvmBasicBlockHandle errorBlock,
            LlvmBasicBlockHandle malformedBlock,
            LlvmBasicBlockHandle doneBlock) = (
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_prepare"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_status"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_digits"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_detect_chunked"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_build_body"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_chunked"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_success"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_error"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_malformed"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done"));

        LlvmApi.BuildBr(builder, prepareBlock);

        LlvmApi.PositionBuilderAtEnd(builder, prepareBlock);
        LlvmValueHandle effectiveResponse = LlvmApi.BuildSelect(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, responseRef, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_is_null_response"),
            EmitHeapStringLiteral(state, string.Empty),
            responseRef,
            prefix + "_effective_response");
        LlvmValueHandle responseLen = LoadStringLength(state, effectiveResponse, prefix + "_len");
        LlvmValueHandle tooShort = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, responseLen, LlvmApi.ConstInt(state.I64, 12, 0), prefix + "_too_short");
        LlvmApi.BuildCondBr(builder, tooShort, malformedBlock, parseBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseBlock);
        LlvmValueHandle responseBytes = GetStringBytesPointer(state, effectiveResponse, prefix + "_bytes");
        LlvmValueHandle separatorIndex = EmitFindByteSequence(state, responseBytes, responseLen, "\r\n\r\n"u8.ToArray(), prefix + "_separator");
        LlvmValueHandle hasSeparator = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, separatorIndex, LlvmApi.ConstInt(state.I64, 0, 1), prefix + "_has_separator");
        LlvmApi.BuildCondBr(builder, hasSeparator, parseStatusBlock, malformedBlock);

        EmitParseHttpResponseHeaders(state, prefix, responseBytes, separatorIndex, responseLen, resultSlot, parseStatusBlock, parseDigitsBlock, detectChunkedBlock, buildBodyBlock, chunkedBlock, successBlock, errorBlock, malformedBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, malformedBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, HttpMalformedResponseMessage)), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, prefix + "_result");
    }

    private static void EmitParseHttpResponseHeaders(
        LlvmCodegenState state,
        string prefix,
        LlvmValueHandle responseBytes,
        LlvmValueHandle separatorIndex,
        LlvmValueHandle responseLen,
        LlvmValueHandle resultSlot,
        LlvmBasicBlockHandle parseStatusBlock,
        LlvmBasicBlockHandle parseDigitsBlock,
        LlvmBasicBlockHandle detectChunkedBlock,
        LlvmBasicBlockHandle buildBodyBlock,
        LlvmBasicBlockHandle chunkedBlock,
        LlvmBasicBlockHandle successBlock,
        LlvmBasicBlockHandle errorBlock,
        LlvmBasicBlockHandle malformedBlock,
        LlvmBasicBlockHandle doneBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, parseStatusBlock);
        LlvmValueHandle statusSpaceIndex = EmitFindByte(state, responseBytes, separatorIndex, 0, (byte)' ', prefix + "_status_space");
        LlvmValueHandle hasStatusSpace = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, statusSpaceIndex, LlvmApi.ConstInt(state.I64, 0, 1), prefix + "_has_status_space");
        LlvmApi.BuildCondBr(builder, hasStatusSpace, parseDigitsBlock, malformedBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseDigitsBlock);
        LlvmValueHandle statusEnd = LlvmApi.BuildAdd(builder, statusSpaceIndex, LlvmApi.ConstInt(state.I64, 3, 0), prefix + "_status_end");
        LlvmValueHandle digitsInRange = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, statusEnd, separatorIndex, prefix + "_digits_in_range");
        LlvmApi.BuildCondBr(builder, digitsInRange, detectChunkedBlock, malformedBlock);

        LlvmApi.PositionBuilderAtEnd(builder, detectChunkedBlock);
        LlvmValueHandle hundredsByte = LoadByteAt(state, responseBytes, LlvmApi.BuildAdd(builder, statusSpaceIndex, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_hundreds_index"), prefix + "_hundreds_byte");
        LlvmValueHandle tensByte = LoadByteAt(state, responseBytes, LlvmApi.BuildAdd(builder, statusSpaceIndex, LlvmApi.ConstInt(state.I64, 2, 0), prefix + "_tens_index"), prefix + "_tens_byte");
        LlvmValueHandle onesByte = LoadByteAt(state, responseBytes, LlvmApi.BuildAdd(builder, statusSpaceIndex, LlvmApi.ConstInt(state.I64, 3, 0), prefix + "_ones_index"), prefix + "_ones_byte");
        LlvmValueHandle digitsValid = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildAnd(builder,
                BuildByteRangeCheck(state, LlvmApi.BuildZExt(builder, hundredsByte, state.I64, prefix + "_hundreds_i64"), (byte)'0', (byte)'9', prefix + "_hundreds_range"),
                BuildByteRangeCheck(state, LlvmApi.BuildZExt(builder, tensByte, state.I64, prefix + "_tens_i64"), (byte)'0', (byte)'9', prefix + "_tens_range"),
                prefix + "_first_digits_valid"),
            BuildByteRangeCheck(state, LlvmApi.BuildZExt(builder, onesByte, state.I64, prefix + "_ones_i64"), (byte)'0', (byte)'9', prefix + "_ones_range"),
            prefix + "_digits_valid");
        LlvmApi.BuildCondBr(builder, digitsValid, buildBodyBlock, malformedBlock);

        LlvmApi.PositionBuilderAtEnd(builder, buildBodyBlock);
        LlvmValueHandle chunkedHeaderIndex = EmitFindByteSequence(state, responseBytes, separatorIndex, "Transfer-Encoding: chunked"u8.ToArray(), prefix + "_chunked_header");
        LlvmValueHandle hasChunkedHeader = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, chunkedHeaderIndex, LlvmApi.ConstInt(state.I64, 0, 1), prefix + "_has_chunked_header");
        LlvmApi.BuildCondBr(builder, hasChunkedHeader, chunkedBlock, successBlock);

        EmitParseHttpResponseSuccess(state, prefix, responseBytes, separatorIndex, responseLen, hundredsByte, tensByte, onesByte, resultSlot, chunkedBlock, successBlock, errorBlock, doneBlock);
    }

    private static void EmitParseHttpResponseSuccess(
        LlvmCodegenState state,
        string prefix,
        LlvmValueHandle responseBytes,
        LlvmValueHandle separatorIndex,
        LlvmValueHandle responseLen,
        LlvmValueHandle hundredsByte,
        LlvmValueHandle tensByte,
        LlvmValueHandle onesByte,
        LlvmValueHandle resultSlot,
        LlvmBasicBlockHandle chunkedBlock,
        LlvmBasicBlockHandle successBlock,
        LlvmBasicBlockHandle errorBlock,
        LlvmBasicBlockHandle doneBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, chunkedBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, HttpUnsupportedTransferEncodingMessage)), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        LlvmValueHandle statusCode = LlvmApi.BuildAdd(builder,
            LlvmApi.BuildAdd(builder,
                LlvmApi.BuildMul(builder, LlvmApi.BuildSub(builder, LlvmApi.BuildZExt(builder, hundredsByte, state.I64, prefix + "_hundreds_code"), LlvmApi.ConstInt(state.I64, (byte)'0', 0), prefix + "_hundreds_digit"), LlvmApi.ConstInt(state.I64, 100, 0), prefix + "_hundreds_mul"),
                LlvmApi.BuildMul(builder, LlvmApi.BuildSub(builder, LlvmApi.BuildZExt(builder, tensByte, state.I64, prefix + "_tens_code"), LlvmApi.ConstInt(state.I64, (byte)'0', 0), prefix + "_tens_digit"), LlvmApi.ConstInt(state.I64, 10, 0), prefix + "_tens_mul"),
                prefix + "_status_sum"),
            LlvmApi.BuildSub(builder, LlvmApi.BuildZExt(builder, onesByte, state.I64, prefix + "_ones_code"), LlvmApi.ConstInt(state.I64, (byte)'0', 0), prefix + "_ones_digit"),
            prefix + "_status_code");
        LlvmValueHandle bodyStart = LlvmApi.BuildAdd(builder, separatorIndex, LlvmApi.ConstInt(state.I64, 4, 0), prefix + "_body_start");
        LlvmValueHandle bodyLength = LlvmApi.BuildSub(builder, responseLen, bodyStart, prefix + "_body_length");
        LlvmValueHandle bodyRef = EmitHeapStringSliceFromBytesPointer(state, LlvmApi.BuildGEP2(builder, state.I8, responseBytes, [bodyStart], prefix + "_body_ptr"), bodyLength, prefix + "_body");
        LlvmValueHandle isSuccess = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, statusCode, LlvmApi.ConstInt(state.I64, 200, 0), prefix + "_status_ge_200"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ule, statusCode, LlvmApi.ConstInt(state.I64, 299, 0), prefix + "_status_le_299"),
            prefix + "_status_is_success");
        LlvmApi.BuildCondBr(builder, isSuccess, errorBlock, errorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, errorBlock);
        LlvmValueHandle finalResult = LlvmApi.BuildSelect(builder,
            isSuccess,
            EmitResultOk(state, bodyRef),
            EmitResultError(state, EmitHttpStatusErrorString(state, statusCode, prefix + "_status_error")),
            prefix + "_final_result");
        LlvmApi.BuildStore(builder, finalResult, resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);
    }
}
