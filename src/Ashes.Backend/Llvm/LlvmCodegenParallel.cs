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
    private const int ParallelDescWorkerStack = 32;  // mmap'd worker stack base (for munmap); win-x64: thread HANDLE
    private const int ParallelDescWorkerTcb = 40;    // worker TCB / TLS block base (for munmap + arena walk)
    private const int ParallelDescWorkerArenaEnd = 48; // arm64: worker's heap-arena end (worker-written; parent walks chunks on cleanup)
    // CLONE_CHILD_CLEARTID word: set to 1 before clone; the kernel zeroes it and futex-wakes it only
    // once the worker thread has FULLY exited (its stack is no longer in use). The parent waits on this
    // before reclaiming the worker stack, closing a race where the worker's return epilogue still reads
    // its stack after publishing the result — distinct from the Done word (result-ready) so the parent
    // can consume the result immediately and only the stack free blocks on true exit. (linux only.)
    private const int ParallelDescExited = 56;
    private const int ParallelDescSizeBytes = 64;

    // Default per-worker stack size when the --parallel-stack-size tunable is unset. On linux this
    // is the mmap'd worker-stack length; on win-x64 CreateThread gets the OS default (dwStackSize=0)
    // unless the tunable is set.
    internal const long DefaultParallelWorkerStackBytes = 1L * 1024 * 1024; // 1 MiB worker stack
    // Fallback worker cap when runtime core-count detection fails (and the historical fixed cap).
    private const long ParallelWorkerCapFallback = 8;

    /// <summary>Resolved per-worker stack size (bytes) for the linux mmap'd worker stack.</summary>
    private static long ParallelStackBytesFor(LlvmCodegenState state) =>
        state.Target.ParallelWorkerStackBytes ?? DefaultParallelWorkerStackBytes;
    // CLONE_VM | CLONE_FS | CLONE_FILES | CLONE_SIGHAND | CLONE_THREAD | CLONE_SYSVSEM — a thread
    // sharing the address space and fds, auto-reaped on exit (no SIGCHLD, no zombie). CLONE_CHILD_CLEARTID
    // (0x200000) makes the kernel zero the ctid word (ParallelDescExited) and futex-wake it once the
    // thread has fully exited, so the parent can safely reclaim the worker stack (see EmitParallelCleanup).
    private const long ParallelCloneFlags = 0x100 | 0x200 | 0x400 | 0x800 | 0x10000 | 0x40000 | 0x200000;
    private const string ParallelWorkerFnName = "__ashes_parallel_worker";
    private const string ParallelActiveCounterName = "__ashes_parallel_active";
    private const string ParallelCapGlobalName = "__ashes_parallel_cap";
    private const string ParallelCapFnName = "__ashes_parallel_cap_get";

    /// <summary>
    /// Emits the parallelism runtime (the shared active-worker counter and the worker trampoline)
    /// once per module. Only on linux-x64; other flavors always run `both` inline.
    /// </summary>
    private static void EmitParallelRuntime(LlvmTargetContext target, LlvmCodegenFlavor flavor, LlvmAttributeHandle nounwindAttr)
    {
        if (flavor != LlvmCodegenFlavor.LinuxX64 && flavor != LlvmCodegenFlavor.WindowsX64 && flavor != LlvmCodegenFlavor.LinuxArm64)
        {
            return;
        }

        LlvmTypeHandle i64 = LlvmApi.Int64TypeInContext(target.Context);
        LlvmValueHandle counter = LlvmApi.AddGlobal(target.Module, i64, ParallelActiveCounterName);
        LlvmApi.SetLinkage(counter, LlvmLinkage.Internal);
        LlvmApi.SetInitializer(counter, LlvmApi.ConstInt(i64, 0, 0));

        EmitParallelWorkerTrampoline(target, flavor, nounwindAttr);
        EmitParallelWorkerCapFn(target, flavor, nounwindAttr);
    }

    /// <summary>
    /// Emits <c>__ashes_parallel_cap_get() : i64</c> — the max-concurrent-workers cap used by the
    /// fork gate. Detects the machine's core count once (lazily, cached in a global): on linux a
    /// <c>sched_getaffinity</c> popcount (respects taskset/cgroup masks), on win-x64
    /// <c>GetSystemInfo().dwNumberOfProcessors</c>. Falls back to the historical cap of 8 when
    /// detection reports nothing. A fixed <c>--parallel-workers</c> value bypasses this entirely
    /// (the fork gate compares against the constant instead).
    /// </summary>
    private static void EmitParallelWorkerCapFn(LlvmTargetContext target, LlvmCodegenFlavor flavor, LlvmAttributeHandle nounwindAttr)
    {
        LlvmTypeHandle i64 = LlvmApi.Int64TypeInContext(target.Context);
        LlvmValueHandle capGlobal = LlvmApi.AddGlobal(target.Module, i64, ParallelCapGlobalName);
        LlvmApi.SetLinkage(capGlobal, LlvmLinkage.Internal);
        LlvmApi.SetInitializer(capGlobal, LlvmApi.ConstInt(i64, 0, 0));

        LlvmValueHandle fn = LlvmApi.AddFunction(target.Module, ParallelCapFnName, LlvmApi.FunctionType(i64, []));
        LlvmApi.SetLinkage(fn, LlvmLinkage.Internal);
        LlvmApi.AddAttributeAtIndex(fn, LlvmApi.AttributeIndexFunction, nounwindAttr);

        LlvmBasicBlockHandle entry = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "entry");
        LlvmBasicBlockHandle detectBlock = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "detect");
        LlvmBasicBlockHandle cachedBlock = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "cached");
        LlvmBuilderHandle builder = target.Builder;

        LlvmApi.PositionBuilderAtEnd(builder, entry);
        LlvmCodegenState state = CreateBareRuntimeState(target, fn, flavor);
        LlvmValueHandle cached = LlvmApi.BuildLoad2(builder, i64, capGlobal, "cap_cached");
        LlvmValueHandle isSet = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, cached, LlvmApi.ConstInt(i64, 0, 0), "cap_is_set");
        LlvmApi.BuildCondBr(builder, isSet, cachedBlock, detectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, cachedBlock);
        LlvmApi.BuildRet(builder, cached);

        LlvmApi.PositionBuilderAtEnd(builder, detectBlock);
        LlvmValueHandle detected;
        if (flavor == LlvmCodegenFlavor.WindowsX64)
        {
            // SYSTEM_INFO is 48 bytes; dwNumberOfProcessors is the DWORD at offset 32.
            LlvmValueHandle infoBuf = EmitStackAlloc(state, 64, "cap_sysinfo");
            LlvmTypeHandle getSystemInfoType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(target.Context), [state.I8Ptr]);
            // void-returning call: LLVM verification rejects a named instruction with a void result.
            EmitWindowsImportCall(state, "__imp_GetSystemInfo", getSystemInfoType,
                [LlvmApi.BuildIntToPtr(builder, infoBuf, state.I8Ptr, "cap_sysinfo_ptr")], "");
            LlvmValueHandle packed = LoadMemory(state, infoBuf, 32, "cap_nproc_packed");
            detected = LlvmApi.BuildAnd(builder, packed, LlvmApi.ConstInt(i64, 0xFFFFFFFFUL, 0), "cap_nproc");
        }
        else
        {
            // sched_getaffinity(0, 128, mask): the kernel writes the calling thread's allowed-CPU
            // mask (128 bytes covers 1024 CPUs). The buffer is pre-zeroed, and on failure nothing
            // is written, so a straight popcount of all 16 words yields 0 → the fallback below.
            const int maskBytes = 128;
            LlvmValueHandle maskBuf = EmitStackAlloc(state, maskBytes, "cap_cpu_mask");
            for (int w = 0; w < maskBytes / 8; w++)
            {
                StoreMemory(state, maskBuf, w * 8, LlvmApi.ConstInt(i64, 0, 0), $"cap_mask_zero_{w}");
            }

            EmitLinuxSyscall(state, SyscallSchedGetaffinity,
                LlvmApi.ConstInt(i64, 0, 0),
                LlvmApi.ConstInt(i64, maskBytes, 0),
                maskBuf,
                "cap_getaffinity");

            // SWAR popcount of each mask word, summed.
            LlvmValueHandle c55 = LlvmApi.ConstInt(i64, 0x5555555555555555UL, 0);
            LlvmValueHandle c33 = LlvmApi.ConstInt(i64, 0x3333333333333333UL, 0);
            LlvmValueHandle c0F = LlvmApi.ConstInt(i64, 0x0F0F0F0F0F0F0F0FUL, 0);
            LlvmValueHandle c01 = LlvmApi.ConstInt(i64, 0x0101010101010101UL, 0);
            LlvmValueHandle total = LlvmApi.ConstInt(i64, 0, 0);
            for (int w = 0; w < maskBytes / 8; w++)
            {
                LlvmValueHandle x = LoadMemory(state, maskBuf, w * 8, $"cap_mask_{w}");
                LlvmValueHandle sh1 = LlvmApi.BuildAnd(builder, LlvmApi.BuildLShr(builder, x, LlvmApi.ConstInt(i64, 1, 0), $"cap_p1s_{w}"), c55, $"cap_p1a_{w}");
                x = LlvmApi.BuildSub(builder, x, sh1, $"cap_p1_{w}");
                LlvmValueHandle lo2 = LlvmApi.BuildAnd(builder, x, c33, $"cap_p2l_{w}");
                LlvmValueHandle hi2 = LlvmApi.BuildAnd(builder, LlvmApi.BuildLShr(builder, x, LlvmApi.ConstInt(i64, 2, 0), $"cap_p2s_{w}"), c33, $"cap_p2h_{w}");
                x = LlvmApi.BuildAdd(builder, lo2, hi2, $"cap_p2_{w}");
                x = LlvmApi.BuildAnd(builder, LlvmApi.BuildAdd(builder, x, LlvmApi.BuildLShr(builder, x, LlvmApi.ConstInt(i64, 4, 0), $"cap_p4s_{w}"), $"cap_p4a_{w}"), c0F, $"cap_p4_{w}");
                x = LlvmApi.BuildLShr(builder, LlvmApi.BuildMul(builder, x, c01, $"cap_p8m_{w}"), LlvmApi.ConstInt(i64, 56, 0), $"cap_pc_{w}");
                total = LlvmApi.BuildAdd(builder, total, x, $"cap_total_{w}");
            }

            detected = total;
        }

        // Detection reporting zero (failed syscall, empty mask) falls back to the historical cap.
        LlvmValueHandle isZero = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, detected, LlvmApi.ConstInt(i64, 0, 0), "cap_detect_zero");
        LlvmValueHandle resolved = LlvmApi.BuildSelect(builder, isZero,
            LlvmApi.ConstInt(i64, (ulong)ParallelWorkerCapFallback, 0), detected, "cap_resolved");
        LlvmApi.BuildStore(builder, resolved, capGlobal);
        LlvmApi.BuildRet(builder, resolved);
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
    /// right thunk in the worker arena, publish the result, release the worker slot, and wake the
    /// joining parent via futex. It returns normally; the clone wrapper performs the thread exit.
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

        if (flavor == LlvmCodegenFlavor.LinuxArm64)
        {
            // Point TPIDR_EL0 at the worker's own zeroed TLS block (the parent mmap'd it) so the
            // thread-local arena cursors resolve to this thread's block, then address them through the
            // thread-local globals. Unlike x64 (where the parent pre-writes the worker TCB's cursor/end),
            // the worker initializes its own arena chunk here — its TLS block starts zeroed.
            LlvmValueHandle armTlsBlock = LoadMemory(state, desc, ParallelDescWorkerTcb, "worker_tls_block");
            EmitArm64SetThreadPointer(state, armTlsBlock);
            state = WithArm64ThreadLocalArenaSlots(state);
            EmitHeapChunkInit(state);

            LlvmValueHandle armUnit = EmitUnitValue(state);
            LlvmValueHandle armRight = LoadMemory(state, desc, ParallelDescRightClosure, "worker_right");
            LlvmValueHandle armResult = EmitCallClosure(state, armRight, armUnit);
            StoreMemory(state, desc, ParallelDescResult, armResult, "worker_result");
            // Publish the worker's heap-arena end so the parent can walk+free its chunks on cleanup
            // (it can't read it from the TLS block without the link-time tprel offset).
            LlvmValueHandle armHeapEnd = LlvmApi.BuildLoad2(target.Builder, state.I64, state.HeapEndSlot, "worker_heap_end");
            StoreMemory(state, desc, ParallelDescWorkerArenaEnd, armHeapEnd, "worker_arena_end");
            StoreMemory(state, desc, ParallelDescDone, LlvmApi.ConstInt(state.I64, 1, 0), "worker_done");
            // Release the worker slot now that this worker's user work is complete. The cap bounds
            // RUNNING workers, not un-joined descriptors: a fork attempted while this result still
            // awaits its join may reuse the slot. Releasing here (instead of at join cleanup) lets
            // cap-saturated callers that fell back to inline evaluation hand later work to fresh
            // workers as soon as capacity frees, rather than only after their own join runs.
            LlvmValueHandle armActiveAddr = LlvmApi.BuildPtrToInt(target.Builder,
                LlvmApi.GetNamedGlobal(target.Module, ParallelActiveCounterName), i64, "worker_counter_addr");
            EmitAtomicFetchAdd(state, armActiveAddr, unchecked((ulong)-1L), "worker_release");
            EmitLinuxSyscall6(state, SyscallFutex,
                desc,
                LlvmApi.ConstInt(state.I64, (ulong)FutexWakePrivate, 0),
                LlvmApi.ConstInt(state.I64, 1, 0),
                LlvmApi.ConstInt(state.I64, 0, 0),
                LlvmApi.ConstInt(state.I64, 0, 0),
                LlvmApi.ConstInt(state.I64, 0, 0),
                "worker_wake");
            LlvmApi.BuildRetVoid(target.Builder);
            return;
        }

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
        // Release the worker slot now that this worker's user work is complete (see the arm64 branch
        // above): the cap bounds running workers, so the slot may be reused before this result is joined.
        LlvmValueHandle activeAddr = LlvmApi.BuildPtrToInt(target.Builder,
            LlvmApi.GetNamedGlobal(target.Module, ParallelActiveCounterName), i64, "worker_counter_addr");
        EmitAtomicFetchAdd(state, activeAddr, unchecked((ulong)-1L), "worker_release");

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

        // The parallel runtime (worker trampoline + active counter) is emitted only for supported
        // flavors — and on arm64 only when the program uses the TLS arena (non-networking). If it
        // wasn't emitted, run the right thunk inline (a correct sequential fallback).
        bool runtimeEmitted = LlvmApi.GetNamedFunction(state.Target.Module, ParallelWorkerFnName) != default;
        if (!runtimeEmitted
            || (state.Flavor != LlvmCodegenFlavor.LinuxX64
                && state.Flavor != LlvmCodegenFlavor.WindowsX64
                && state.Flavor != LlvmCodegenFlavor.LinuxArm64))
        {
            EmitParallelForkInline(state, desc, rightClosure);
            return desc;
        }

        LlvmBuilderHandle builder = state.Target.Builder;
        // Try to claim a worker slot. old = atomic_fetch_add(&active, 1); spawn iff old < CAP.
        LlvmValueHandle counterAddr = LlvmApi.BuildPtrToInt(builder,
            LlvmApi.GetNamedGlobal(state.Target.Module, ParallelActiveCounterName), state.I64, "par_counter_addr");
        LlvmValueHandle prevActive = EmitAtomicFetchAdd(state, counterAddr, 1, "par_claim");
        // Cap: a fixed --parallel-workers value compares against a constant; otherwise the cap
        // is the machine's detected core count (cached in a global by __ashes_parallel_cap_get).
        LlvmValueHandle capValue = state.Target.ParallelWorkerCap is { } fixedCap
            ? LlvmApi.ConstInt(state.I64, (ulong)fixedCap, 0)
            : LlvmApi.BuildCall2(builder, LlvmApi.FunctionType(state.I64, []),
                LlvmApi.GetNamedFunction(state.Target.Module, ParallelCapFnName), [], "par_cap");
        LlvmValueHandle canSpawn = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt,
            prevActive, capValue, "par_can_spawn");

        var spawnBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "par_spawn");
        var inlineBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "par_inline");
        var mergeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "par_fork_done");
        LlvmApi.BuildCondBr(builder, canSpawn, spawnBlock, inlineBlock);

        // ── Spawn path ──────────────────────────────────────────────────────────────────────
        LlvmApi.PositionBuilderAtEnd(builder, spawnBlock);
        // The worker's per-thread control region: on x64/win it's the TCB (parent pre-writes cursor/end
        // from a fresh chunk); on arm64 it's a zeroed TLS block the worker msr's into TPIDR_EL0 and then
        // initializes its own chunk against. mmap/VirtualAlloc zero-fill, so the arm64 block starts clean.
        LlvmValueHandle workerTcb = EmitAllocateOsMemory(state, LlvmApi.ConstInt(state.I64, (ulong)MainTcbSizeBytes, 0), "par_tcb");
        if (state.Flavor != LlvmCodegenFlavor.LinuxArm64)
        {
            LlvmValueHandle chunk = EmitAllocateOsMemory(state, LlvmApi.ConstInt(state.I64, HeapChunkBytes, 0), "par_chunk");
            // Worker TCB: self-pointer, cursor (chunk+8 past the prev-base header), end.
            StoreMemory(state, workerTcb, (int)TcbSelfOffset, workerTcb, "par_tcb_self");
            StoreMemory(state, workerTcb, (int)TcbHeapCursorOffset,
                LlvmApi.BuildAdd(builder, chunk, LlvmApi.ConstInt(state.I64, 8, 0), "par_chunk_cursor"), "par_tcb_cursor");
            StoreMemory(state, workerTcb, (int)TcbHeapEndOffset,
                LlvmApi.BuildAdd(builder, chunk, LlvmApi.ConstInt(state.I64, HeapChunkBytes, 0), "par_chunk_end"), "par_tcb_end");
            StoreMemory(state, chunk, 0, LlvmApi.ConstInt(state.I64, 0, 0), "par_chunk_prevbase");
        }

        StoreMemory(state, desc, ParallelDescWorkerTcb, workerTcb, "par_desc_tcb");
        StoreMemory(state, desc, ParallelDescMode, LlvmApi.ConstInt(state.I64, 1, 0), "par_desc_mode_worker");

        if (state.Flavor == LlvmCodegenFlavor.LinuxX64 || state.Flavor == LlvmCodegenFlavor.LinuxArm64)
        {
            long parallelStackBytes = ParallelStackBytesFor(state);
            LlvmValueHandle stack = EmitAllocateOsMemory(state, LlvmApi.ConstInt(state.I64, (ulong)parallelStackBytes, 0), "par_stack");
            StoreMemory(state, desc, ParallelDescWorkerStack, stack, "par_desc_stack");
            // Mark the ctid/exited word running (1); the kernel zeroes it + futex-wakes on true thread
            // exit (CLONE_CHILD_CLEARTID). Must be set before clone so the kernel's clear can't be missed.
            StoreMemory(state, desc, ParallelDescExited, LlvmApi.ConstInt(state.I64, 1, 0), "par_desc_exited1");
            LlvmValueHandle stackTop = LlvmApi.BuildAdd(builder, stack, LlvmApi.ConstInt(state.I64, (ulong)parallelStackBytes, 0), "par_stack_top");
            EmitCloneWorker(state, desc, stackTop); // EmitCloneWorker branches on flavor (x86 vs aarch64 asm)
        }
        else
        {
            // win-x64: CreateThread allocates and manages the worker stack; the returned HANDLE is
            // stored in the (otherwise-unused-on-win) worker-stack desc field for join + CloseHandle.
            // HANDLE CreateThread(attrs=0, stackSize=0, start=__ashes_parallel_worker, param=desc, flags=0, tidOut=0).
            LlvmValueHandle workerFn = LlvmApi.GetNamedFunction(state.Target.Module, ParallelWorkerFnName);
            LlvmTypeHandle createThreadType = LlvmApi.FunctionType(state.I64, [state.I64, state.I64, state.I8Ptr, state.I8Ptr, state.I64, state.I64]);
            LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
            // dwStackSize: the configured per-worker stack size, or 0 (OS default) when unset.
            LlvmValueHandle stackSize = LlvmApi.ConstInt(state.I64, (ulong)(state.Target.ParallelWorkerStackBytes ?? 0), 0);
            LlvmValueHandle handle = EmitWindowsImportCall(state, "__imp_CreateThread", createThreadType,
                [zero, stackSize, workerFn, LlvmApi.BuildIntToPtr(builder, desc, state.I8Ptr, "par_desc_ptr"), zero, zero], "par_create_thread");
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

        // linux-x64 and arm64 both join via the futex done-flag loop (EmitLinuxSyscall6 is arch-aware).
        if (state.Flavor != LlvmCodegenFlavor.LinuxX64 && state.Flavor != LlvmCodegenFlavor.LinuxArm64)
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
        if (state.Flavor != LlvmCodegenFlavor.LinuxX64 && state.Flavor != LlvmCodegenFlavor.WindowsX64 && state.Flavor != LlvmCodegenFlavor.LinuxArm64)
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
        // The worker slot was already released by the worker trampoline when its user work completed;
        // cleanup only reclaims the exited worker's OS resources.

        // linux: the result (Done word) is published before the worker returns, but the worker still
        // runs its return epilogue on its stack afterwards. Wait for true thread exit — the kernel zeroes
        // the ctid/exited word and futex-wakes it in mm_release, after the stack is no longer used — before
        // reclaiming the stack/TCB/arena. Non-private FUTEX_WAIT to match the kernel's clear_child_tid wake.
        // (win-x64 reclaims via CloseHandle after WaitForSingleObject, which already waits for full exit.)
        if (state.Flavor == LlvmCodegenFlavor.LinuxX64 || state.Flavor == LlvmCodegenFlavor.LinuxArm64)
        {
            var exitCheck = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "par_cleanup_exit_check");
            var exitWait = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "par_cleanup_exit_wait");
            var exitDone = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "par_cleanup_exited");
            LlvmValueHandle exitedAddr = LlvmApi.BuildAdd(builder, desc, LlvmApi.ConstInt(state.I64, ParallelDescExited, 0), "par_exited_addr");
            LlvmApi.BuildBr(builder, exitCheck);

            LlvmApi.PositionBuilderAtEnd(builder, exitCheck);
            LlvmValueHandle exited = LoadMemory(state, desc, ParallelDescExited, "par_exited_val");
            LlvmValueHandle stillRunning = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, exited, LlvmApi.ConstInt(state.I64, 0, 0), "par_still_running");
            LlvmApi.BuildCondBr(builder, stillRunning, exitWait, exitDone);

            // futex(&exited, FUTEX_WAIT, 1, NULL, NULL, 0) — non-private to match the kernel wake. Loops
            // and re-checks, so a clear that races the check is never lost.
            LlvmApi.PositionBuilderAtEnd(builder, exitWait);
            EmitLinuxSyscall6(state, SyscallFutex,
                exitedAddr,
                LlvmApi.ConstInt(state.I64, 0, 0),
                LlvmApi.ConstInt(state.I64, 1, 0),
                LlvmApi.ConstInt(state.I64, 0, 0),
                LlvmApi.ConstInt(state.I64, 0, 0),
                LlvmApi.ConstInt(state.I64, 0, 0),
                "par_cleanup_exit_wait_call");
            LlvmApi.BuildBr(builder, exitCheck);

            LlvmApi.PositionBuilderAtEnd(builder, exitDone);
        }
        // Reclaim the worker thread's memory. linux: free the mmap'd stack; win-x64: close the thread
        // HANDLE (the OS frees the CreateThread stack). Then free the worker's arena chunks + TCB/TLS
        // block. The arena end comes from the TCB on x64/win (parent-written), or from the descriptor
        // on arm64 (the worker wrote it — its end lives in the TLS block at a link-time tprel offset).
        LlvmValueHandle workerTcb = LoadMemory(state, desc, ParallelDescWorkerTcb, "par_cleanup_tcb");
        LlvmValueHandle arenaEnd;
        if (state.Flavor == LlvmCodegenFlavor.WindowsX64)
        {
            LlvmValueHandle handle = LoadMemory(state, desc, ParallelDescWorkerStack, "par_cleanup_handle");
            LlvmTypeHandle closeHandleType = LlvmApi.FunctionType(state.I32, [state.I64]);
            EmitWindowsImportCall(state, "__imp_CloseHandle", closeHandleType, [handle], "par_cleanup_close");
            arenaEnd = LoadMemory(state, workerTcb, (int)TcbHeapEndOffset, "par_cleanup_arena_end");
        }
        else
        {
            EmitFreeOsMemory(state, LoadMemory(state, desc, ParallelDescWorkerStack, "par_cleanup_stack"), ParallelStackBytesFor(state), "par_cleanup_stack");
            arenaEnd = state.Flavor == LlvmCodegenFlavor.LinuxArm64
                ? LoadMemory(state, desc, ParallelDescWorkerArenaEnd, "par_cleanup_arena_end")
                : LoadMemory(state, workerTcb, (int)TcbHeapEndOffset, "par_cleanup_arena_end");
        }

        EmitFreeWorkerArenaChunks(state, arenaEnd);
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
        LlvmBuilderHandle builder = state.Target.Builder;
        if (state.Flavor == LlvmCodegenFlavor.LinuxArm64)
        {
            // ldxr/stxr acquire-release CAS loop (portable across armv8; the 'generic' CPU has no LSE
            // ldaddal). $0 = old (result), $1 = delta, $2 = address; x9 holds new, w10 the store status.
            LlvmTypeHandle armFnType = LlvmApi.FunctionType(state.I64, [state.I64, state.I64]);
            LlvmValueHandle armAsm = LlvmApi.GetInlineAsm(armFnType,
                "1:\n\tldaxr $0, [$2]\n\tadd x9, $0, $1\n\tstlxr w10, x9, [$2]\n\tcbnz w10, 1b",
                "=&r,r,r,~{x9},~{x10},~{memory},~{cc}", true, false);
            return LlvmApi.BuildCall2(builder, armFnType, armAsm,
                [LlvmApi.ConstInt(state.I64, delta, 1), addr], name);
        }

        // x86-64: Early-clobber (&) on the result keeps the address operand in a different register;
        // otherwise xadd's value and address registers can alias and corrupt the access.
        LlvmTypeHandle fnType = LlvmApi.FunctionType(state.I64, [state.I64, state.I64]);
        // $0 = result reg, $1 = tied delta input (same reg as $0), $2 = address.
        LlvmValueHandle asm = LlvmApi.GetInlineAsm(fnType, "lock xaddq $0, ($2)", "=&r,0,r,~{memory}", true, false);
        return LlvmApi.BuildCall2(builder, fnType, asm,
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

        if (state.Flavor == LlvmCodegenFlavor.LinuxArm64)
        {
            // AArch64 raw clone(2): x0=flags, x1=child stack, x2/x3/x4=ptid/tls/ctid=0, x8=220, svc.
            // Operands ($0 stack, $1 desc, $2 fn, $3 flags) are captured into x9-x12 first. fn+desc are
            // stored on the child stack; the child (x0==0) shares the parent's register state, sets sp to
            // the child stack, pops fn+desc, calls the trampoline, then exits (svc, x8=93).
            const string armAsm =
                "mov x9, $0\n\t" +          // child stack top
                "mov x10, $1\n\t" +        // desc
                "mov x11, $2\n\t" +        // trampoline fn
                "mov x12, $3\n\t" +        // clone flags
                "and x9, x9, #-16\n\t" +   // align
                "stp x11, x10, [x9, #-16]!\n\t" + // [sp]=fn, [sp+8]=desc; x9 -= 16
                "mov x0, x12\n\t" +        // arg0 = flags
                "mov x1, x9\n\t" +         // arg1 = child stack
                "mov x2, xzr\n\t" +        // arg2 ptid = 0
                "mov x3, xzr\n\t" +        // arg3 tls = 0
                "add x4, x10, #56\n\t" +   // arg4 ctid = &desc.exited (CLONE_CHILD_CLEARTID target)
                "mov x8, #220\n\t" +       // SYS_clone
                "svc #0\n\t" +
                "cbz x0, 1f\n\t" +         // child if x0==0
                "b 2f\n\t" +               // parent: done
                "1:\n\t" +                 // child:
                "ldp x11, x10, [sp], #16\n\t" + // x11=fn, x10=desc
                "mov x0, x10\n\t" +        // arg0 = desc
                "blr x11\n\t" +            // trampoline(desc)
                "mov x8, #93\n\t" +        // SYS_exit
                "mov x0, xzr\n\t" +
                "svc #0\n\t" +
                "2:\n\t";

            LlvmTypeHandle armFnType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context),
                [state.I64, state.I64, state.I64, state.I64]);
            LlvmValueHandle armInlineAsm = LlvmApi.GetInlineAsm(armFnType, armAsm,
                "r,r,r,r,~{x0},~{x1},~{x2},~{x3},~{x4},~{x8},~{x9},~{x10},~{x11},~{x12},~{x30},~{memory},~{cc}",
                true, false);
            LlvmApi.BuildCall2(state.Target.Builder, armFnType, armInlineAsm, [stackTop, desc, workerFn,
                LlvmApi.ConstInt(state.I64, (ulong)ParallelCloneFlags, 0)], "");
            return;
        }

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
            "mov $1, %r10\n\t" +       // arg4 ctid = &desc.exited (CLONE_CHILD_CLEARTID target)
            "add $$56, %r10\n\t" +
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
