using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{
    // ── Structured parallelism (Ashes.Parallel.both) ────────────────────────────────────────
    //
    // `both(left)(right)` runs `right(Unit)` on a worker thread (linux-x64) while `left(Unit)`
    // runs inline, then joins and returns the pair. The worker gets its own per-thread bump arena
    // (its own GS-based TCB), so it never races the parent allocator. Its result is deep-copied
    // into the parent arena before the worker arena is freed (the deep copy is emitted in lowering
    // at the concrete result type). When the worker budget is exhausted, `right` runs inline — a
    // correct, sequential fallback. Non-linux-x64 targets always take the inline path.

    // Task descriptor layout (bytes). Shared between the spawning thread and the worker (same
    // address space). Allocated in the parent arena by EmitParallelFork.
    private const int ParallelDescDone = 0;          // futex word: 0 = running, 1 = done
    private const int ParallelDescResult = 8;        // worker's raw result pointer/value
    private const int ParallelDescMode = 16;         // 0 = ran inline, 1 = ran on a worker thread
    private const int ParallelDescRightClosure = 24; // closure the worker applies to Unit
    private const int ParallelDescWorkerStack = 32;  // mmap'd worker stack base (for munmap)
    private const int ParallelDescWorkerTcb = 40;    // mmap'd worker TCB base (for munmap + arena walk)
    private const int ParallelDescSizeBytes = 48;

    private const long ParallelStackBytes = 1L * 1024 * 1024; // 1 MiB worker stack
    private const long ParallelWorkerCap = 8;                  // max concurrent workers
    // CLONE_VM | CLONE_FS | CLONE_FILES | CLONE_SIGHAND | CLONE_THREAD | CLONE_SYSVSEM — a thread
    // sharing the address space and fds, auto-reaped on exit (no SIGCHLD, no zombie).
    private const long ParallelCloneFlags = 0x100 | 0x200 | 0x400 | 0x800 | 0x10000 | 0x40000;
    private const string ParallelWorkerFnName = "__ashes_parallel_worker";
    private const string ParallelActiveCounterName = "__ashes_parallel_active";

    /// <summary>
    /// Emits the parallelism runtime (the shared active-worker counter and the worker trampoline)
    /// once per module. Only on linux-x64; other flavors always run `both` inline.
    /// </summary>
    private static void EmitParallelRuntime(LlvmTargetContext target, LlvmCodegenFlavor flavor, LlvmAttributeHandle nounwindAttr)
    {
        if (flavor != LlvmCodegenFlavor.LinuxX64 && flavor != LlvmCodegenFlavor.WindowsX64)
        {
            return;
        }

        LlvmTypeHandle i64 = LlvmApi.Int64TypeInContext(target.Context);
        LlvmValueHandle counter = LlvmApi.AddGlobal(target.Module, i64, ParallelActiveCounterName);
        LlvmApi.SetLinkage(counter, LlvmLinkage.Internal);
        LlvmApi.SetInitializer(counter, LlvmApi.ConstInt(i64, 0, 0));

        EmitParallelWorkerTrampoline(target, flavor, nounwindAttr);
    }

    // Calls a kernel32 function imported by name (the __imp_* global holds the IAT slot pointer).
    private static LlvmValueHandle EmitWindowsImportCall(LlvmCodegenState state, string importName, LlvmTypeHandle fnType, LlvmValueHandle[] args, string name)
    {
        LlvmValueHandle imp = LlvmApi.GetNamedGlobal(state.Target.Module, importName);
        LlvmValueHandle fnPtr = LlvmApi.BuildLoad2(state.Target.Builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0), imp, name + "_ptr");
        return LlvmApi.BuildCall2(state.Target.Builder, fnType, fnPtr, args, name);
    }

    /// <summary>
    /// The function each worker thread runs (linux-x64): point GS at the worker's TCB, evaluate the
    /// right thunk in the worker arena, publish the result, and wake the joining parent via futex.
    /// It returns normally; the clone wrapper performs the thread exit.
    /// </summary>
    private static void EmitParallelWorkerTrampoline(LlvmTargetContext target, LlvmCodegenFlavor flavor, LlvmAttributeHandle nounwindAttr)
    {
        LlvmTypeHandle i64 = LlvmApi.Int64TypeInContext(target.Context);
        LlvmTypeHandle voidType = LlvmApi.VoidTypeInContext(target.Context);
        LlvmValueHandle fn = LlvmApi.AddFunction(target.Module, ParallelWorkerFnName, LlvmApi.FunctionType(voidType, [i64]));
        LlvmApi.SetLinkage(fn, LlvmLinkage.Internal);
        LlvmApi.AddAttributeAtIndex(fn, LlvmApi.AttributeIndexFunction, nounwindAttr);

        LlvmBasicBlockHandle entry = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "entry");
        LlvmApi.PositionBuilderAtEnd(target.Builder, entry);

        LlvmValueHandle desc = LlvmApi.GetParam(fn, 0);
        LlvmCodegenState state = CreateBareRuntimeState(target, fn, flavor);

        if (flavor == LlvmCodegenFlavor.WindowsX64)
        {
            // The bare runtime state has null Windows-import handles; the worker's own arena
            // grow/free (EmitHeapGrow/EmitFreeOsMemory) needs VirtualAlloc/VirtualFree, which are
            // always created for win-x64 — look them up by name (linux grows via the mmap syscall).
            state = state with
            {
                WindowsVirtualAllocImport = LlvmApi.GetNamedGlobal(target.Module, "__imp_VirtualAlloc"),
                WindowsVirtualFreeImport = LlvmApi.GetNamedGlobal(target.Module, "__imp_VirtualFree"),
                WindowsExitProcessImport = LlvmApi.GetNamedGlobal(target.Module, "__imp_ExitProcess"),
            };
        }

        // Point this thread's per-thread arena at the worker TCB the parent prepared (cursor/end
        // already written), then address the arena through it like any other function. linux: GS via
        // arch_prctl; win-x64: publish the TCB pointer into TEB+0x28 (the OS provides the GS-based TEB).
        LlvmValueHandle tcb = LoadMemory(state, desc, ParallelDescWorkerTcb, "worker_tcb");
        if (flavor == LlvmCodegenFlavor.LinuxX64)
        {
            EmitLinuxSyscall(state, SyscallArchPrctl,
                LlvmApi.ConstInt(state.I64, (ulong)ArchSetGs, 0), tcb, LlvmApi.ConstInt(state.I64, 0, 0), "worker_set_gs");
        }
        else
        {
            EmitWriteTcbBaseToTeb(state, tcb);
        }

        state = WithLinuxThreadArena(state);

        LlvmValueHandle unit = EmitUnitValue(state);
        LlvmValueHandle rightClosure = LoadMemory(state, desc, ParallelDescRightClosure, "worker_right");
        LlvmValueHandle result = EmitCallClosure(state, rightClosure, unit);
        StoreMemory(state, desc, ParallelDescResult, result, "worker_result");

        if (flavor == LlvmCodegenFlavor.LinuxX64)
        {
            // Publish done=1 (release; x86 stores are ordered) then wake the joining parent.
            StoreMemory(state, desc, ParallelDescDone, LlvmApi.ConstInt(state.I64, 1, 0), "worker_done");
            EmitLinuxSyscall6(state, SyscallFutex,
                desc,
                LlvmApi.ConstInt(state.I64, (ulong)FutexWakePrivate, 0),
                LlvmApi.ConstInt(state.I64, 1, 0),
                LlvmApi.ConstInt(state.I64, 0, 0),
                LlvmApi.ConstInt(state.I64, 0, 0),
                LlvmApi.ConstInt(state.I64, 0, 0),
                "worker_wake");
        }

        // win-x64 needs no done flag / wake: the parent joins with WaitForSingleObject on the thread
        // handle, which returns only after this function returns and the thread fully exits (a barrier),
        // so the result store above is visible.
        LlvmApi.BuildRetVoid(target.Builder);
    }

    private static LlvmValueHandle EmitParallelFork(LlvmCodegenState state, LlvmValueHandle rightClosure)
    {
        LlvmValueHandle desc = EmitAlloc(state, ParallelDescSizeBytes);
        StoreMemory(state, desc, ParallelDescRightClosure, rightClosure, "par_desc_right");
        StoreMemory(state, desc, ParallelDescDone, LlvmApi.ConstInt(state.I64, 0, 0), "par_desc_done0");

        if (state.Flavor != LlvmCodegenFlavor.LinuxX64 && state.Flavor != LlvmCodegenFlavor.WindowsX64)
        {
            EmitParallelForkInline(state, desc, rightClosure);
            return desc;
        }

        LlvmBuilderHandle builder = state.Target.Builder;
        // Try to claim a worker slot. old = atomic_fetch_add(&active, 1); spawn iff old < CAP.
        LlvmValueHandle counterAddr = LlvmApi.BuildPtrToInt(builder,
            LlvmApi.GetNamedGlobal(state.Target.Module, ParallelActiveCounterName), state.I64, "par_counter_addr");
        LlvmValueHandle prevActive = EmitAtomicFetchAdd(state, counterAddr, 1, "par_claim");
        LlvmValueHandle canSpawn = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt,
            prevActive, LlvmApi.ConstInt(state.I64, (ulong)ParallelWorkerCap, 0), "par_can_spawn");

        var spawnBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "par_spawn");
        var inlineBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "par_inline");
        var mergeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "par_fork_done");
        LlvmApi.BuildCondBr(builder, canSpawn, spawnBlock, inlineBlock);

        // ── Spawn path ──────────────────────────────────────────────────────────────────────
        LlvmApi.PositionBuilderAtEnd(builder, spawnBlock);
        LlvmValueHandle workerTcb = EmitAllocateOsMemory(state, LlvmApi.ConstInt(state.I64, (ulong)MainTcbSizeBytes, 0), "par_tcb");
        LlvmValueHandle chunk = EmitAllocateOsMemory(state, LlvmApi.ConstInt(state.I64, HeapChunkBytes, 0), "par_chunk");
        // Worker TCB: self-pointer, cursor (chunk+8 past the prev-base header), end.
        StoreMemory(state, workerTcb, (int)TcbSelfOffset, workerTcb, "par_tcb_self");
        StoreMemory(state, workerTcb, (int)TcbHeapCursorOffset,
            LlvmApi.BuildAdd(builder, chunk, LlvmApi.ConstInt(state.I64, 8, 0), "par_chunk_cursor"), "par_tcb_cursor");
        StoreMemory(state, workerTcb, (int)TcbHeapEndOffset,
            LlvmApi.BuildAdd(builder, chunk, LlvmApi.ConstInt(state.I64, HeapChunkBytes, 0), "par_chunk_end"), "par_tcb_end");
        StoreMemory(state, chunk, 0, LlvmApi.ConstInt(state.I64, 0, 0), "par_chunk_prevbase");
        StoreMemory(state, desc, ParallelDescWorkerTcb, workerTcb, "par_desc_tcb");
        StoreMemory(state, desc, ParallelDescMode, LlvmApi.ConstInt(state.I64, 1, 0), "par_desc_mode_worker");

        if (state.Flavor == LlvmCodegenFlavor.LinuxX64)
        {
            LlvmValueHandle stack = EmitAllocateOsMemory(state, LlvmApi.ConstInt(state.I64, (ulong)ParallelStackBytes, 0), "par_stack");
            StoreMemory(state, desc, ParallelDescWorkerStack, stack, "par_desc_stack");
            LlvmValueHandle stackTop = LlvmApi.BuildAdd(builder, stack, LlvmApi.ConstInt(state.I64, (ulong)ParallelStackBytes, 0), "par_stack_top");
            EmitCloneWorker(state, desc, stackTop);
        }
        else
        {
            // win-x64: CreateThread allocates and manages the worker stack; the returned HANDLE is
            // stored in the (otherwise-unused-on-win) worker-stack desc field for join + CloseHandle.
            // HANDLE CreateThread(attrs=0, stackSize=0, start=__ashes_parallel_worker, param=desc, flags=0, tidOut=0).
            LlvmValueHandle workerFn = LlvmApi.GetNamedFunction(state.Target.Module, ParallelWorkerFnName);
            LlvmTypeHandle createThreadType = LlvmApi.FunctionType(state.I64, [state.I64, state.I64, state.I8Ptr, state.I8Ptr, state.I64, state.I64]);
            LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
            LlvmValueHandle handle = EmitWindowsImportCall(state, "__imp_CreateThread", createThreadType,
                [zero, zero, workerFn, LlvmApi.BuildIntToPtr(builder, desc, state.I8Ptr, "par_desc_ptr"), zero, zero], "par_create_thread");
            StoreMemory(state, desc, ParallelDescWorkerStack, handle, "par_desc_handle");
        }

        LlvmApi.BuildBr(builder, mergeBlock);

        // ── Inline fallback path ────────────────────────────────────────────────────────────
        LlvmApi.PositionBuilderAtEnd(builder, inlineBlock);
        // Release the slot we speculatively claimed.
        EmitAtomicFetchAdd(state, counterAddr, unchecked((ulong)-1L), "par_release");
        EmitParallelForkInline(state, desc, rightClosure);
        LlvmApi.BuildBr(builder, mergeBlock);

        LlvmApi.PositionBuilderAtEnd(builder, mergeBlock);
        return desc;
    }

    /// <summary>Runs the right thunk inline and records its result + done flag in the descriptor.</summary>
    private static void EmitParallelForkInline(LlvmCodegenState state, LlvmValueHandle desc, LlvmValueHandle rightClosure)
    {
        LlvmValueHandle unit = EmitUnitValue(state);
        LlvmValueHandle result = EmitCallClosure(state, rightClosure, unit);
        StoreMemory(state, desc, ParallelDescResult, result, "par_inline_result");
        StoreMemory(state, desc, ParallelDescDone, LlvmApi.ConstInt(state.I64, 1, 0), "par_inline_done");
        StoreMemory(state, desc, ParallelDescMode, LlvmApi.ConstInt(state.I64, 0, 0), "par_inline_mode");
    }

    private static LlvmValueHandle EmitParallelJoin(LlvmCodegenState state, LlvmValueHandle desc)
    {
        if (state.Flavor == LlvmCodegenFlavor.WindowsX64)
        {
            LlvmBuilderHandle winBuilder = state.Target.Builder;
            // If the right thunk ran on a worker (mode != 0), block until the thread exits — that both
            // synchronizes and makes the worker's result store visible; inline runs need no wait.
            LlvmValueHandle mode = LoadMemory(state, desc, ParallelDescMode, "par_join_mode");
            LlvmValueHandle isWorker = LlvmApi.BuildICmp(winBuilder, LlvmIntPredicate.Ne, mode, LlvmApi.ConstInt(state.I64, 0, 0), "par_join_is_worker");
            var winWaitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "par_win_join_wait");
            var winDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "par_win_join_done");
            LlvmApi.BuildCondBr(winBuilder, isWorker, winWaitBlock, winDoneBlock);

            LlvmApi.PositionBuilderAtEnd(winBuilder, winWaitBlock);
            LlvmValueHandle handle = LoadMemory(state, desc, ParallelDescWorkerStack, "par_join_handle");
            LlvmTypeHandle waitType = LlvmApi.FunctionType(state.I32, [state.I64, state.I32]);
            EmitWindowsImportCall(state, "__imp_WaitForSingleObject", waitType,
                [handle, LlvmApi.ConstInt(state.I32, 0xFFFFFFFFUL, 0)], "par_wait");
            LlvmApi.BuildBr(winBuilder, winDoneBlock);

            LlvmApi.PositionBuilderAtEnd(winBuilder, winDoneBlock);
            return LoadMemory(state, desc, ParallelDescResult, "par_join_result");
        }

        if (state.Flavor != LlvmCodegenFlavor.LinuxX64)
        {
            return LoadMemory(state, desc, ParallelDescResult, "par_join_result");
        }

        LlvmBuilderHandle builder = state.Target.Builder;
        var checkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "par_join_check");
        var waitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "par_join_wait");
        var doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "par_join_done");
        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkBlock);
        LlvmValueHandle done = LoadMemory(state, desc, ParallelDescDone, "par_join_done_val");
        LlvmValueHandle isDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, done, LlvmApi.ConstInt(state.I64, 0, 0), "par_join_is_done");
        LlvmApi.BuildCondBr(builder, isDone, doneBlock, waitBlock);

        // futex(&done, FUTEX_WAIT_PRIVATE, 0, NULL, NULL, 0). Re-checks done atomically, so a wake
        // that races the check is never lost.
        LlvmApi.PositionBuilderAtEnd(builder, waitBlock);
        EmitLinuxSyscall6(state, SyscallFutex,
            desc,
            LlvmApi.ConstInt(state.I64, (ulong)FutexWaitPrivate, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "par_join_wait_call");
        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LoadMemory(state, desc, ParallelDescResult, "par_join_result");
    }

    private static bool EmitParallelCleanup(LlvmCodegenState state, LlvmValueHandle desc)
    {
        if (state.Flavor != LlvmCodegenFlavor.LinuxX64 && state.Flavor != LlvmCodegenFlavor.WindowsX64)
        {
            return false;
        }

        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle mode = LoadMemory(state, desc, ParallelDescMode, "par_cleanup_mode");
        LlvmValueHandle isWorker = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, mode, LlvmApi.ConstInt(state.I64, 0, 0), "par_cleanup_is_worker");

        var freeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "par_cleanup_free");
        var doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "par_cleanup_done");
        LlvmApi.BuildCondBr(builder, isWorker, freeBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, freeBlock);
        // Release the worker slot.
        LlvmValueHandle counterAddr = LlvmApi.BuildPtrToInt(builder,
            LlvmApi.GetNamedGlobal(state.Target.Module, ParallelActiveCounterName), state.I64, "par_cleanup_counter_addr");
        EmitAtomicFetchAdd(state, counterAddr, unchecked((ulong)-1L), "par_cleanup_release");
        // Reclaim the worker thread. linux: free the mmap'd stack; win-x64: close the thread HANDLE
        // (the OS frees the CreateThread stack). Both then walk and free the worker's arena chunks + TCB.
        if (state.Flavor == LlvmCodegenFlavor.LinuxX64)
        {
            EmitFreeOsMemory(state, LoadMemory(state, desc, ParallelDescWorkerStack, "par_cleanup_stack"), ParallelStackBytes, "par_cleanup_stack");
        }
        else
        {
            LlvmValueHandle handle = LoadMemory(state, desc, ParallelDescWorkerStack, "par_cleanup_handle");
            LlvmTypeHandle closeHandleType = LlvmApi.FunctionType(state.I32, [state.I64]);
            EmitWindowsImportCall(state, "__imp_CloseHandle", closeHandleType, [handle], "par_cleanup_close");
        }

        LlvmValueHandle workerTcb = LoadMemory(state, desc, ParallelDescWorkerTcb, "par_cleanup_tcb");
        EmitFreeWorkerArenaChunks(state, LoadMemory(state, workerTcb, (int)TcbHeapEndOffset, "par_cleanup_arena_end"));
        EmitFreeOsMemory(state, workerTcb, MainTcbSizeBytes, "par_cleanup_tcb");
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return false;
    }

    /// <summary>
    /// munmaps every arena chunk of a finished worker. The current chunk's end is
    /// <paramref name="arenaEnd"/>; each chunk's first word links to the previous chunk's base
    /// (0 for the first chunk), matching the main allocator's chunk header.
    /// </summary>
    private static void EmitFreeWorkerArenaChunks(LlvmCodegenState state, LlvmValueHandle arenaEnd)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle curEndSlot = LlvmApi.BuildAlloca(builder, state.I64, "par_free_cur_end");
        LlvmApi.BuildStore(builder, arenaEnd, curEndSlot);

        var loopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "par_free_loop");
        var doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "par_free_done");
        LlvmApi.BuildBr(builder, loopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBlock);
        LlvmValueHandle curEnd = LlvmApi.BuildLoad2(builder, state.I64, curEndSlot, "par_free_cur_end_val");
        LlvmValueHandle base_ = LlvmApi.BuildSub(builder, curEnd, LlvmApi.ConstInt(state.I64, HeapChunkBytes, 0), "par_free_base");
        LlvmValueHandle prevBase = LoadMemory(state, base_, 0, "par_free_prev_base");
        EmitFreeOsMemory(state, base_, HeapChunkBytes, "par_free_chunk");
        LlvmValueHandle isFirst = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, prevBase, LlvmApi.ConstInt(state.I64, 0, 0), "par_free_is_first");
        LlvmValueHandle nextEnd = LlvmApi.BuildAdd(builder, prevBase, LlvmApi.ConstInt(state.I64, HeapChunkBytes, 0), "par_free_next_end");
        LlvmApi.BuildStore(builder, nextEnd, curEndSlot);
        LlvmApi.BuildCondBr(builder, isFirst, doneBlock, loopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
    }

    /// <summary>Atomic <c>old = *addr; *addr += delta; return old</c> via <c>lock xadd</c>.</summary>
    private static LlvmValueHandle EmitAtomicFetchAdd(LlvmCodegenState state, LlvmValueHandle addr, ulong delta, string name)
    {
        // Early-clobber (&) on the result keeps the address operand in a different register;
        // otherwise xadd's value and address registers can alias and corrupt the access.
        LlvmTypeHandle fnType = LlvmApi.FunctionType(state.I64, [state.I64, state.I64]);
        // $0 = result reg, $1 = tied delta input (same reg as $0), $2 = address.
        LlvmValueHandle asm = LlvmApi.GetInlineAsm(fnType, "lock xaddq $0, ($2)", "=&r,0,r,~{memory}", true, false);
        return LlvmApi.BuildCall2(state.Target.Builder, fnType, asm,
            [LlvmApi.ConstInt(state.I64, delta, 1), addr], name);
    }

    /// <summary>
    /// Spawns a worker via raw <c>clone(2)</c> (musl-style): pushes the descriptor onto the child
    /// stack, keeps the trampoline pointer in r9 (preserved across the syscall), and in the child
    /// pops the descriptor and calls the trampoline; the child exits when it returns.
    /// </summary>
    private static void EmitCloneWorker(LlvmCodegenState state, LlvmValueHandle desc, LlvmValueHandle stackTop)
    {
        LlvmValueHandle workerFn = LlvmApi.BuildPtrToInt(state.Target.Builder,
            LlvmApi.GetNamedFunction(state.Target.Module, ParallelWorkerFnName), state.I64, "par_worker_fn");

        // Operands ($0 stack, $1 desc, $2 fn, $3 flags) are read into scratch registers up front;
        // they live in callee-saved registers (everything they would otherwise share is clobbered),
        // so the order of the moves can't trample an operand. fn is parked in r9, which the syscall
        // instruction preserves, so the child still has it after clone returns.
        const string asmText =
            "mov $0, %rsi\n\t" +        // rsi = child stack
            "and $$-16, %rsi\n\t" +    // align
            "sub $$8, %rsi\n\t" +
            "mov $1, (%rsi)\n\t" +     // push desc onto child stack
            "mov $2, %r9\n\t" +        // r9 = trampoline
            "mov $3, %rdi\n\t" +       // clone arg1 = flags
            "xor %edx, %edx\n\t" +     // arg3 ptid = 0
            "xor %r10d, %r10d\n\t" +   // arg4 ctid = 0
            "xor %r8d, %r8d\n\t" +     // arg5 tls = 0
            "mov $$56, %eax\n\t" +     // SYS_clone (arg2 = rsi = child stack)
            "syscall\n\t" +
            "test %rax, %rax\n\t" +
            "jnz 1f\n\t" +             // parent: return
            "xor %ebp, %ebp\n\t" +     // child:
            "pop %rdi\n\t" +           // rdi = desc
            "call *%r9\n\t" +          // trampoline(desc)
            "mov $$60, %eax\n\t" +     // SYS_exit
            "xor %edi, %edi\n\t" +
            "syscall\n\t" +
            "1:\n\t";

        LlvmTypeHandle fnType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context),
            [state.I64, state.I64, state.I64, state.I64]);
        LlvmValueHandle asm = LlvmApi.GetInlineAsm(fnType, asmText,
            "r,r,r,r,~{rax},~{rcx},~{r11},~{rdi},~{rsi},~{rdx},~{r8},~{r9},~{r10},~{memory},~{cc}",
            true, false);
        LlvmApi.BuildCall2(state.Target.Builder, fnType, asm, [stackTop, desc, workerFn,
            LlvmApi.ConstInt(state.I64, (ulong)ParallelCloneFlags, 0)], "");
    }

    /// <summary>
    /// Builds a minimal <see cref="LlvmCodegenState"/> for a standalone runtime function (no IR
    /// temps/locals, no Windows imports). Arena slots start unset; the caller installs them after
    /// any GS setup (e.g. via <see cref="WithLinuxThreadArena"/>).
    /// </summary>
    private static LlvmCodegenState CreateBareRuntimeState(LlvmTargetContext target, LlvmValueHandle function, LlvmCodegenFlavor flavor)
    {
        LlvmTypeHandle i64 = LlvmApi.Int64TypeInContext(target.Context);
        LlvmTypeHandle i32 = LlvmApi.Int32TypeInContext(target.Context);
        LlvmTypeHandle i8 = LlvmApi.Int8TypeInContext(target.Context);
        LlvmTypeHandle f64 = LlvmApi.DoubleTypeInContext(target.Context);
        LlvmTypeHandle i8Ptr = LlvmApi.PointerTypeInContext(target.Context, 0);
        LlvmValueHandle programArgsSlot = LlvmApi.BuildAlloca(target.Builder, i64, "rt_program_args");
        LlvmApi.BuildStore(target.Builder, LlvmApi.ConstInt(i64, 0, 0), programArgsSlot);

        return new LlvmCodegenState(
            target,
            function,
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, LlvmValueHandle>(StringComparer.Ordinal),
            programArgsSlot,
            [],
            [],
            default,
            default,
            new Dictionary<string, LlvmBasicBlockHandle>(StringComparer.Ordinal),
            new Dictionary<int, LlvmBasicBlockHandle>(),
            i64,
            i32,
            i8,
            f64,
            i8Ptr,
            i8Ptr,
            i8Ptr,
            default, // EntryStackPointer
                     // 41 Windows import handles (unused by linux runtime helpers).
            default, default, default, default, default, default, default, default, default,
            default, default, default, default, default, default, default, default, default,
            default, default, default, default, default, default, default, default, default,
            default, default, default, default, default, default, default, default, default,
            default, default, default, default, default,
            new Dictionary<string, LlvmValueHandle>(StringComparer.Ordinal),
            flavor,
            false,
            false);
    }
}
