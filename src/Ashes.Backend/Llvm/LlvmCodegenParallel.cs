using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{
    // Structured parallelism (Ashes.Task.Parallel.both)
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

    // Dynamically-scoped worker override set by Ashes.Task.Parallel.withWorkers; 0 = unset (use the
    // compiled max). The effective fork cap is min(override, compiledMax) when set.
    internal const string ParallelWorkerOverrideName = "__ashes_parallel_override";

    // Work-conserving parallel reduce (queued Ashes.Task.Parallel.reduce)
    //
    // One OS-allocated, zero-initialized region holds the whole queue: a fixed header, the
    // snapshotted list elements, an item slot and a publish-flag word for every node of the
    // merge tree, and one 64-byte record per spawned worker. Workers first pull element indexes
    // from the atomic next-index word and publish f(element) into items[0..n-1]; they then pull
    // merge tasks from the second counter and combine adjacent item pairs round by round (an odd
    // trailing item promotes) until the tree's root — items[S-1] — is published. The item arrays
    // are laid round-major (round 0 = the n leaves, then ceil(n/2) round-1 slots, and so on down
    // to 1), so the task order is topological and the tree shape depends only on n: the merge is
    // deterministic under reduce's associative-combine contract no matter which worker computes
    // what. The caller awaits only the root.
    //
    // Region layout: [header 64B][elems 8n][items 8S][flags 8S][worker records 64*W]
    // where S = n + ceil(n/2) + ceil(n/4) + ... + 1 (total merge-tree nodes; 0 when n = 0).
    private const int ParallelQueueNextIndex = 0;     // atomic: next element index to claim
    private const int ParallelQueueCount = 8;         // n = element count (read by lowering's empty check)
    private const int ParallelQueueClosure = 16;      // the mapper closure f
    private const int ParallelQueueWorkerCount = 24;  // W = workers actually spawned
    private const int ParallelQueueRegionBytes = 32;  // total region size (for the final free)
    private const int ParallelQueueCombine = 40;      // the combine closure
    private const int ParallelQueueNextMergeTask = 48; // atomic: next merge-tree task to claim
    private const int ParallelQueueTotalItems = 56;   // S = total merge-tree item count
    private const int ParallelQueueHeaderBytes = 64;
    // A worker record reuses the both-descriptor offsets 32..56 (worker stack / win HANDLE at
    // ParallelDescWorkerStack, TCB at ParallelDescWorkerTcb, arena end at
    // ParallelDescWorkerArenaEnd, exited/ctid word at ParallelDescExited) so EmitCloneWorker's
    // hardcoded ctid offset applies to both layouts; offset 0 holds the queue-region back-pointer.
    private const int ParallelQueueRecDesc = 0;
    private const int ParallelQueueRecordBytes = 64;

    private const string ParallelQueueWorkerFnName = "__ashes_parallel_queue_worker";
    private const string ParallelQueueDrainFnName = "__ashes_parallel_queue_drain";
    private const string ParallelQueueStartFnName = "__ashes_parallel_queue_start";
    private const string ParallelQueueAwaitFnName = "__ashes_parallel_queue_await";
    private const string ParallelQueueCleanupFnName = "__ashes_parallel_queue_cleanup";

    /// <summary>
    /// Emits the parallelism runtime (the shared active-worker counter and the worker trampoline)
    /// once per module. Only on linux-x64; other flavors always run `both` inline.
    /// </summary>
    private static void EmitParallelRuntime(LlvmTargetContext target, LlvmCodegenFlavor flavor, LlvmAttributeHandle nounwindAttr)
    {
        if (flavor != LlvmCodegenFlavor.LinuxX64 && !IsWindowsFlavor(flavor) && flavor != LlvmCodegenFlavor.LinuxArm64)
        {
            return;
        }

        LlvmTypeHandle i64 = LlvmApi.Int64TypeInContext(target.Context);
        LlvmValueHandle counter = LlvmApi.AddGlobal(target.Module, i64, ParallelActiveCounterName);
        LlvmApi.SetLinkage(counter, LlvmLinkage.Internal);
        LlvmApi.SetInitializer(counter, LlvmApi.ConstInt(i64, 0, 0));

        EmitWorkerCapInfrastructure(target, flavor, nounwindAttr);
        EmitParallelWorkerTrampoline(target, flavor, nounwindAttr);
    }

    /// <summary>
    /// Emits the shared worker-cap globals and detection function used by both Ashes.Task.Parallel and the
    /// server's fork-based multi-reactor (serveParallel): the withWorkers override slot and
    /// <c>__ashes_parallel_cap_get()</c>. Idempotent, so a program that uses both surfaces emits them
    /// once. This lets `serve` respect the same <c>--parallel-workers</c> cap and runtime override as
    /// parallel CPU work without pulling in the fork/join worker trampoline.
    /// </summary>
    private static void EmitWorkerCapInfrastructure(LlvmTargetContext target, LlvmCodegenFlavor flavor, LlvmAttributeHandle nounwindAttr)
    {
        if (LlvmApi.GetNamedFunction(target.Module, ParallelCapFnName).Ptr != 0)
        {
            return;
        }

        LlvmTypeHandle i64 = LlvmApi.Int64TypeInContext(target.Context);
        // The withWorkers override (0 = unset). Read on the same thread that set it (the one
        // running the scoped action, which then reaches the fork gate / queued-reduce cap / server).
        LlvmValueHandle overrideGlobal = LlvmApi.AddGlobal(target.Module, i64, ParallelWorkerOverrideName);
        LlvmApi.SetLinkage(overrideGlobal, LlvmLinkage.Internal);
        LlvmApi.SetInitializer(overrideGlobal, LlvmApi.ConstInt(i64, 0, 0));

        EmitParallelWorkerCapFn(target, flavor, nounwindAttr);
    }

    /// <summary>
    /// Emits the effective worker cap: the compiled maximum (a fixed <c>--parallel-workers</c>
    /// constant, else the detected core count) narrowed by the dynamically-scoped withWorkers
    /// override — <c>min(override, compiledMax)</c> when the override is set (non-zero), otherwise
    /// the compiled maximum unchanged.
    /// </summary>
    private static LlvmValueHandle EmitEffectiveWorkerCap(LlvmCodegenState state, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle i64 = state.I64;
        LlvmValueHandle compiledMax = state.Target.ParallelWorkerCap is { } fixedCap
            ? LlvmApi.ConstInt(i64, (ulong)fixedCap, 0)
            : LlvmApi.BuildCall2(builder, LlvmApi.FunctionType(i64, []),
                LlvmApi.GetNamedFunction(state.Target.Module, ParallelCapFnName), [], prefix + "_compiled_max");

        LlvmValueHandle overrideVal = LlvmApi.BuildLoad2(builder, i64,
            LlvmApi.GetNamedGlobal(state.Target.Module, ParallelWorkerOverrideName), prefix + "_override");
        LlvmValueHandle isSet = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, overrideVal, LlvmApi.ConstInt(i64, 0, 0), prefix + "_ovr_set");
        LlvmValueHandle belowMax = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, overrideVal, compiledMax, prefix + "_ovr_below");
        LlvmValueHandle useOverride = LlvmApi.BuildAnd(builder, isSet, belowMax, prefix + "_use_ovr");
        return LlvmApi.BuildSelect(builder, useOverride, overrideVal, compiledMax, prefix + "_eff_cap");
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
        LlvmValueHandle detected = EmitParallelWorkerCapDetect(state, flavor);

        // Detection reporting zero (failed syscall, empty mask) falls back to the historical cap.
        LlvmValueHandle isZero = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, detected, LlvmApi.ConstInt(i64, 0, 0), "cap_detect_zero");
        LlvmValueHandle resolved = LlvmApi.BuildSelect(builder, isZero,
            LlvmApi.ConstInt(i64, (ulong)ParallelWorkerCapFallback, 0), detected, "cap_resolved");
        LlvmApi.BuildStore(builder, resolved, capGlobal);
        LlvmApi.BuildRet(builder, resolved);
    }

    /// <summary>
    /// Detection half of <see cref="EmitParallelWorkerCapFn"/>: computes the raw core count for the
    /// current flavor at the builder's current position (win-x64 via GetSystemInfo, else a
    /// sched_getaffinity popcount). Zero on failure, which the caller maps to the fallback cap.
    /// </summary>
    private static LlvmValueHandle EmitParallelWorkerCapDetect(LlvmCodegenState state, LlvmCodegenFlavor flavor)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle i64 = state.I64;
        if (IsWindowsFlavor(flavor))
        {
            // SYSTEM_INFO is 48 bytes; dwNumberOfProcessors is the DWORD at offset 32.
            LlvmValueHandle infoBuf = EmitStackAlloc(state, 64, "cap_sysinfo");
            LlvmTypeHandle getSystemInfoType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr]);
            // void-returning call: LLVM verification rejects a named instruction with a void result.
            EmitWindowsImportCall(state, "__imp_GetSystemInfo", getSystemInfoType,
                [LlvmApi.BuildIntToPtr(builder, infoBuf, state.I8Ptr, "cap_sysinfo_ptr")], "");
            LlvmValueHandle packed = LoadMemory(state, infoBuf, 32, "cap_nproc_packed");
            return LlvmApi.BuildAnd(builder, packed, LlvmApi.ConstInt(i64, 0xFFFFFFFFUL, 0), "cap_nproc");
        }

        return EmitParallelWorkerCapAffinityCount(state);
    }

    /// <summary>
    /// linux core-count detection: <c>sched_getaffinity(0, 128, mask)</c> — the kernel writes the
    /// calling thread's allowed-CPU mask (128 bytes covers 1024 CPUs) — followed by a SWAR popcount
    /// of the 16 words. The buffer is pre-zeroed, and on failure nothing is written, so the popcount
    /// yields 0 → the caller's fallback cap.
    /// </summary>
    private static LlvmValueHandle EmitParallelWorkerCapAffinityCount(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle i64 = state.I64;
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

        return total;
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
            EmitParallelWorkerTrampolineArm64(state, desc);
            return;
        }

        EmitParallelWorkerTrampolineX64(state, desc, flavor);
    }

    /// <summary>
    /// arm64 worker body: points TPIDR_EL0 at the worker's own zeroed TLS block (the parent mmap'd
    /// it), initializes its own arena chunk, runs the right thunk in that arena, publishes the
    /// result + heap-arena end, releases the worker slot, and futex-wakes the joining parent.
    /// </summary>
    private static void EmitParallelWorkerTrampolineArm64(LlvmCodegenState state, LlvmValueHandle desc)
    {
        LlvmTargetContext target = state.Target;
        LlvmTypeHandle i64 = state.I64;
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
    }

    /// <summary>
    /// x64/win worker body: points this thread's per-thread arena at the worker TCB the parent
    /// prepared (linux: GS via arch_prctl; win-x64: TCB pointer into TEB+0x28), runs the right thunk,
    /// publishes the result, and releases the worker slot. linux-x64 also sets the done flag and
    /// futex-wakes the parent; win-x64 joins on the thread handle (a barrier), so no flag is needed.
    /// </summary>
    private static void EmitParallelWorkerTrampolineX64(LlvmCodegenState state, LlvmValueHandle desc, LlvmCodegenFlavor flavor)
    {
        LlvmTargetContext target = state.Target;
        LlvmTypeHandle i64 = state.I64;
        // The bare runtime state has null Windows-import handles; the worker's own arena
        // grow/free (EmitHeapGrow/EmitFreeOsMemory) needs VirtualAlloc/VirtualFree, which are
        // always created for win-x64 — look them up by name (linux grows via the mmap syscall).
        state = WithWindowsRuntimeImports(state);

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
                && !IsWindowsFlavor(state.Flavor)
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
        // Cap: a fixed --parallel-workers value or the machine's detected core count, narrowed by
        // any active withWorkers override (min(override, compiledMax)).
        LlvmValueHandle capValue = EmitEffectiveWorkerCap(state, "par_cap");
        LlvmValueHandle canSpawn = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt,
            prevActive, capValue, "par_can_spawn");

        var spawnBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "par_spawn");
        var inlineBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "par_inline");
        var mergeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "par_fork_done");
        LlvmApi.BuildCondBr(builder, canSpawn, spawnBlock, inlineBlock);

        // Spawn path
        LlvmApi.PositionBuilderAtEnd(builder, spawnBlock);
        EmitParallelForkSpawn(state, desc, mergeBlock);

        // Inline fallback path
        LlvmApi.PositionBuilderAtEnd(builder, inlineBlock);
        // Release the slot we speculatively claimed.
        EmitAtomicFetchAdd(state, counterAddr, unchecked((ulong)-1L), "par_release");
        EmitParallelForkInline(state, desc, rightClosure);
        LlvmApi.BuildBr(builder, mergeBlock);

        LlvmApi.PositionBuilderAtEnd(builder, mergeBlock);
        return desc;
    }

    /// <summary>
    /// Spawn-path body of <see cref="EmitParallelFork"/> (builder already positioned at the spawn
    /// block): allocates the worker's per-thread control region (a TCB pre-wired to a fresh arena
    /// chunk on x64/win; a zeroed TLS block the worker initializes on arm64), records it in the
    /// descriptor, launches the worker (linux clone / win-x64 CreateThread), then branches to
    /// <paramref name="mergeBlock"/>.
    /// </summary>
    private static void EmitParallelForkSpawn(LlvmCodegenState state, LlvmValueHandle desc, LlvmBasicBlockHandle mergeBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // The worker's per-thread control region: on x64/win it's the TCB (parent pre-writes cursor/end
        // from a fresh chunk); on arm64 it's a zeroed TLS block the worker msr's into TPIDR_EL0 and then
        // initializes its own chunk against. mmap/VirtualAlloc zero-fill, so the arm64 block starts clean.
        LlvmValueHandle workerTcb = EmitAllocateOsMemory(state, LlvmApi.ConstInt(state.I64, (ulong)MainTcbSizeBytes, 0), "par_tcb");
        if (state.Flavor != LlvmCodegenFlavor.LinuxArm64)
        {
            LlvmValueHandle chunk = EmitAllocateOsMemory(state, LlvmApi.ConstInt(state.I64, HeapChunkBytes, 0), "par_chunk");
            // Worker TCB: self-pointer, then set up the chunk's header/footer + cursor/end through
            // slot pointers into the TCB's arena fields (same chunk format as the main allocator).
            StoreMemory(state, workerTcb, (int)TcbSelfOffset, workerTcb, "par_tcb_self");
            var (parCursorSlot, parEndSlot) = BuildLinuxTcbSlots(state, workerTcb, TcbHeapCursorOffset, TcbHeapEndOffset);
            EmitHeapChunkSetup(state, chunk, LlvmApi.ConstInt(state.I64, HeapChunkBytes, 0),
                LlvmApi.ConstInt(state.I64, 0, 0), parCursorSlot, parEndSlot, "par_chunk");
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
            EmitCloneWorker(state, desc, stackTop, ParallelWorkerFnName); // EmitCloneWorker branches on flavor (x86 vs aarch64 asm)
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
        if (IsWindowsFlavor(state.Flavor))
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
        if (state.Flavor != LlvmCodegenFlavor.LinuxX64 && !IsWindowsFlavor(state.Flavor) && state.Flavor != LlvmCodegenFlavor.LinuxArm64)
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
            EmitParallelCleanupWaitExit(state, desc);
        }
        // Reclaim the worker thread's memory. linux: free the mmap'd stack; win-x64: close the thread
        // HANDLE (the OS frees the CreateThread stack). Then free the worker's arena chunks + TCB/TLS
        // block. The arena end comes from the TCB on x64/win (parent-written), or from the descriptor
        // on arm64 (the worker wrote it — its end lives in the TLS block at a link-time tprel offset).
        LlvmValueHandle workerTcb = LoadMemory(state, desc, ParallelDescWorkerTcb, "par_cleanup_tcb");
        LlvmValueHandle arenaEnd;
        if (IsWindowsFlavor(state.Flavor))
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
    /// Blocks until the worker thread whose descriptor is <paramref name="desc"/> has fully exited:
    /// futex-waits on the ctid/exited word the kernel zeroes and wakes in mm_release (after the stack
    /// is no longer used), so the caller can safely reclaim the stack/TCB/arena. Non-private
    /// FUTEX_WAIT matches the kernel's clear_child_tid wake; the loop re-checks so a racing clear is
    /// never lost. Leaves the builder in the post-wait block. (linux only.)
    /// </summary>
    private static void EmitParallelCleanupWaitExit(LlvmCodegenState state, LlvmValueHandle desc)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
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

    /// <summary>
    /// munmaps every arena chunk of a finished worker. <paramref name="arenaEnd"/> is the current
    /// chunk's end (its footer address); the footer records the chunk's own base and the header at
    /// that base links to the previous chunk's end (0 for the first chunk), matching the main
    /// allocator's variable-sized chunk format (see EmitHeapChunkSetup).
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
        // Footer at curEnd -> chunk base; header at base -> previous chunk's end. size = curEnd + footer - base.
        LlvmValueHandle base_ = LoadMemory(state, curEnd, 0, "par_free_base");
        LlvmValueHandle prevEnd = LoadMemory(state, base_, 0, "par_free_prev_end");
        LlvmValueHandle curSize = LlvmApi.BuildSub(builder,
            LlvmApi.BuildAdd(builder, curEnd, LlvmApi.ConstInt(state.I64, ChunkFooterBytes, 0), "par_free_cur_top"),
            base_, "par_free_cur_size");
        EmitFreeOsMemory(state, base_, curSize, "par_free_chunk");
        LlvmValueHandle isFirst = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, prevEnd, LlvmApi.ConstInt(state.I64, 0, 0), "par_free_is_first");
        LlvmApi.BuildStore(builder, prevEnd, curEndSlot);
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
    /// pops the descriptor and calls the trampoline; the child exits when it returns. The ctid
    /// (CLONE_CHILD_CLEARTID) word is hardcoded at descriptor offset 56 in the asm, so every
    /// trampoline argument struct must keep its exited word there.
    /// </summary>
    private static void EmitCloneWorker(LlvmCodegenState state, LlvmValueHandle desc, LlvmValueHandle stackTop, string workerFnName)
    {
        LlvmValueHandle workerFn = LlvmApi.BuildPtrToInt(state.Target.Builder,
            LlvmApi.GetNamedFunction(state.Target.Module, workerFnName), state.I64, "par_worker_fn");

        if (state.Flavor == LlvmCodegenFlavor.LinuxArm64)
        {
            EmitCloneWorkerArm64(state, desc, stackTop, workerFn);
            return;
        }

        EmitCloneWorkerX64(state, desc, stackTop, workerFn);
    }

    /// <summary>
    /// AArch64 raw clone(2) trampoline: captures operands into x9-x12, stores fn+desc on the child
    /// stack, issues SYS_clone (x8=220), and in the child (x0==0) sets sp, pops fn+desc, calls the
    /// trampoline, then SYS_exit (x8=93). The ctid word is &amp;desc+56 (CLONE_CHILD_CLEARTID).
    /// </summary>
    private static void EmitCloneWorkerArm64(LlvmCodegenState state, LlvmValueHandle desc, LlvmValueHandle stackTop, LlvmValueHandle workerFn)
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
    }

    /// <summary>
    /// x86-64 raw clone(2) trampoline: reads operands into scratch registers, parks the trampoline in
    /// r9 (preserved across the syscall), pushes desc on the child stack, issues SYS_clone (eax=56),
    /// and in the child pops desc, calls the trampoline, then SYS_exit. ctid = &amp;desc+56.
    /// </summary>
    private static void EmitCloneWorkerX64(LlvmCodegenState state, LlvmValueHandle desc, LlvmValueHandle stackTop, LlvmValueHandle workerFn)
    {
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

    // Queued reduce runtime

    /// <summary>
    /// Returns a bare runtime state with the win-x64 memory/exit import handles resolved by name
    /// (VirtualAlloc/VirtualFree/ExitProcess are created for every win-x64 program); unchanged on
    /// other flavors. Runtime helpers that allocate, free, or run user closures need these.
    /// </summary>
    private static LlvmCodegenState WithWindowsRuntimeImports(LlvmCodegenState state) =>
        !IsWindowsFlavor(state.Flavor) ? state : state with
        {
            WindowsVirtualAllocImport = LlvmApi.GetNamedGlobal(state.Target.Module, "__imp_VirtualAlloc"),
            WindowsVirtualFreeImport = LlvmApi.GetNamedGlobal(state.Target.Module, "__imp_VirtualFree"),
            WindowsExitProcessImport = LlvmApi.GetNamedGlobal(state.Target.Module, "__imp_ExitProcess"),
        };

    /// <summary>
    /// Adds an internal, nounwind runtime function taking <paramref name="paramCount"/> i64
    /// parameters, positions the builder at its fresh entry block, and returns the function.
    /// </summary>
    private static LlvmValueHandle AddQueueRuntimeFn(LlvmTargetContext target, LlvmAttributeHandle nounwindAttr,
        string name, int paramCount, bool returnsValue)
    {
        LlvmTypeHandle i64 = LlvmApi.Int64TypeInContext(target.Context);
        LlvmTypeHandle retType = returnsValue ? i64 : LlvmApi.VoidTypeInContext(target.Context);
        LlvmTypeHandle[] paramTypes = new LlvmTypeHandle[paramCount];
        for (int i = 0; i < paramCount; i++)
        {
            paramTypes[i] = i64;
        }

        LlvmValueHandle fn = LlvmApi.AddFunction(target.Module, name, LlvmApi.FunctionType(retType, paramTypes));
        LlvmApi.SetLinkage(fn, LlvmLinkage.Internal);
        LlvmApi.AddAttributeAtIndex(fn, LlvmApi.AttributeIndexFunction, nounwindAttr);
        LlvmBasicBlockHandle entry = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "entry");
        LlvmApi.PositionBuilderAtEnd(target.Builder, entry);
        return fn;
    }

    /// <summary>
    /// Emits the queued-reduce runtime once per module (requires the base parallel runtime — the
    /// active-worker counter and cap function — to have been emitted first). Five functions: the
    /// drain loop, the worker trampoline, and the start/await/cleanup entry points called from the
    /// ParallelQueueStart/Await/Cleanup IR instructions.
    /// </summary>
    private static void EmitParallelQueueRuntime(LlvmTargetContext target, LlvmCodegenFlavor flavor, LlvmAttributeHandle nounwindAttr)
    {
        EmitParallelQueueDrainFn(target, flavor, nounwindAttr);
        EmitParallelQueueWorkerTrampoline(target, flavor, nounwindAttr);
        EmitParallelQueueStartFn(target, flavor, nounwindAttr);
        EmitParallelQueueAwaitFn(target, flavor, nounwindAttr);
        EmitParallelQueueCleanupFn(target, flavor, nounwindAttr);
    }

    /// <summary>
    /// Blocks until the flag word at <paramref name="flagAddr"/> becomes nonzero. The read is an
    /// atomic acquire (a fetch-add of zero) pairing with the publisher's release increment, so the
    /// published item store is visible on return. linux sleeps on the flag's futex — each flag has
    /// at most one waiter (an item's unique consumer task, or the awaiting caller for the root) —
    /// and win-x64 polls with <c>Sleep(1)</c> (items arrive at chunk granularity, so the poll is
    /// cold).
    /// </summary>
    private static void EmitQueueFlagWait(LlvmCodegenState state, LlvmValueHandle flagAddr, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle i64 = state.I64;
        var checkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check");
        var waitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_wait");
        var readyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_ready");
        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkBlock);
        LlvmValueHandle flag = EmitAtomicFetchAdd(state, flagAddr, 0, prefix + "_flag");
        LlvmValueHandle isReady = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, flag, LlvmApi.ConstInt(i64, 0, 0), prefix + "_is_ready");
        LlvmApi.BuildCondBr(builder, isReady, readyBlock, waitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, waitBlock);
        if (IsLinuxFlavor(state.Flavor))
        {
            // futex(&flag, FUTEX_WAIT_PRIVATE, 0): re-checks the flag atomically, so a wake racing
            // the check is never lost.
            EmitLinuxSyscall6(state, SyscallFutex,
                flagAddr,
                LlvmApi.ConstInt(i64, (ulong)FutexWaitPrivate, 0),
                LlvmApi.ConstInt(i64, 0, 0),
                LlvmApi.ConstInt(i64, 0, 0),
                LlvmApi.ConstInt(i64, 0, 0),
                LlvmApi.ConstInt(i64, 0, 0),
                prefix + "_wait_call");
        }
        else
        {
            // void-returning import call: the instruction must be unnamed to pass LLVM verification.
            LlvmTypeHandle sleepType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I32]);
            EmitWindowsImportCall(state, "__imp_Sleep", sleepType, [LlvmApi.ConstInt(state.I32, 1, 0)], "");
        }

        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, readyBlock);
    }

    /// <summary>
    /// Publishes the item at <paramref name="itemAddr"/>: stores the value, then flips the item's
    /// flag with an atomic release increment and futex-wakes its (single) waiter on linux.
    /// </summary>
    private static void EmitQueueItemPublish(LlvmCodegenState state, LlvmValueHandle itemAddr, LlvmValueHandle flagAddr, LlvmValueHandle value, string prefix)
    {
        StoreMemory(state, itemAddr, 0, value, prefix + "_item");
        EmitAtomicFetchAdd(state, flagAddr, 1, prefix + "_publish");
        if (IsLinuxFlavor(state.Flavor))
        {
            EmitLinuxSyscall6(state, SyscallFutex,
                flagAddr,
                LlvmApi.ConstInt(state.I64, (ulong)FutexWakePrivate, 0),
                LlvmApi.ConstInt(state.I64, 1, 0),
                LlvmApi.ConstInt(state.I64, 0, 0),
                LlvmApi.ConstInt(state.I64, 0, 0),
                LlvmApi.ConstInt(state.I64, 0, 0),
                prefix + "_wake");
        }
    }

    /// <summary>
    /// <c>__ashes_parallel_queue_drain(desc)</c>: the whole per-thread work loop. Phase one claims
    /// element indexes from the shared counter and publishes <c>f(element)</c> into the leaf items;
    /// phase two claims merge-tree tasks (round-major, so claim order is topological) from the
    /// second counter, waits for the task's operand flags, and publishes <c>combine(left)(right)</c>
    /// — or promotes a lone trailing item — into the output slot. Claim order makes this
    /// deadlock-free: the earliest incomplete task always has published operands and a claimant.
    /// Runs on whichever thread calls it — a queue worker after its arena setup, or the
    /// queue-starting thread when no worker slot could be claimed — and allocates through that
    /// thread's own arena; merge results may reference operand items in other workers' arenas,
    /// which all stay live until cleanup.
    /// </summary>
    private static void EmitParallelQueueDrainFn(LlvmTargetContext target, LlvmCodegenFlavor flavor, LlvmAttributeHandle nounwindAttr)
    {
        LlvmBuilderHandle builder = target.Builder;
        LlvmValueHandle fn = AddQueueRuntimeFn(target, nounwindAttr, ParallelQueueDrainFnName, 1, returnsValue: false);
        LlvmCodegenState state = WithWindowsRuntimeImports(CreateBareRuntimeState(target, fn, flavor));
        state = flavor == LlvmCodegenFlavor.LinuxArm64 ? WithArm64ThreadLocalArenaSlots(state) : WithLinuxThreadArena(state);

        QueueDrainCtx ctx = EmitParallelQueueDrainPrologue(state, fn);
        LlvmBasicBlockHandle mergeLoop = EmitParallelQueueDrainFoldLeaves(state, fn, ctx);

        // Phase two: pairwise merges
        LlvmApi.PositionBuilderAtEnd(builder, mergeLoop);
        EmitParallelQueueDrainMerge(state, fn, ctx);
    }

    /// <summary>Shared values threaded through the drain phases: the descriptor, the region base
    /// pointers, and the merge-task locate/result alloca slots (all live in the entry block).</summary>
    private readonly record struct QueueDrainCtx(
        LlvmValueHandle Desc,
        LlvmValueHandle Count,
        LlvmValueHandle Mapper,
        LlvmValueHandle Combiner,
        LlvmValueHandle TotalItems,
        LlvmValueHandle ElemsBase,
        LlvmValueHandle ItemsBase,
        LlvmValueHandle FlagsBase,
        LlvmValueHandle RoundCountSlot,
        LlvmValueHandle PrevBaseSlot,
        LlvmValueHandle CurBaseSlot,
        LlvmValueHandle RemSlot,
        LlvmValueHandle ResSlot);

    /// <summary>Entry-block prologue: loads the descriptor fields, derives the elems/items/flags
    /// base pointers, and allocates the merge locate/result slots.</summary>
    private static QueueDrainCtx EmitParallelQueueDrainPrologue(LlvmCodegenState state, LlvmValueHandle fn)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle i64 = state.I64;
        LlvmValueHandle eight = LlvmApi.ConstInt(i64, 8, 0);
        LlvmValueHandle desc = LlvmApi.GetParam(fn, 0);
        LlvmValueHandle count = LoadMemory(state, desc, ParallelQueueCount, "parq_drain_n");
        LlvmValueHandle mapper = LoadMemory(state, desc, ParallelQueueClosure, "parq_drain_f");
        LlvmValueHandle combiner = LoadMemory(state, desc, ParallelQueueCombine, "parq_drain_combine");
        LlvmValueHandle totalItems = LoadMemory(state, desc, ParallelQueueTotalItems, "parq_drain_s");
        LlvmValueHandle elemsBase = LlvmApi.BuildAdd(builder, desc, LlvmApi.ConstInt(i64, ParallelQueueHeaderBytes, 0), "parq_drain_elems");
        LlvmValueHandle countBytes = LlvmApi.BuildMul(builder, count, eight, "parq_drain_nbytes");
        LlvmValueHandle itemBytes = LlvmApi.BuildMul(builder, totalItems, eight, "parq_drain_sbytes");
        LlvmValueHandle itemsBase = LlvmApi.BuildAdd(builder, elemsBase, countBytes, "parq_drain_items");
        LlvmValueHandle flagsBase = LlvmApi.BuildAdd(builder, itemsBase, itemBytes, "parq_drain_flags");
        // Merge-task locate state (see the merge phase below); allocas live in the entry block.
        LlvmValueHandle roundCountSlot = LlvmApi.BuildAlloca(builder, i64, "parq_merge_c");
        LlvmValueHandle prevBaseSlot = LlvmApi.BuildAlloca(builder, i64, "parq_merge_prev_base");
        LlvmValueHandle curBaseSlot = LlvmApi.BuildAlloca(builder, i64, "parq_merge_cur_base");
        LlvmValueHandle remSlot = LlvmApi.BuildAlloca(builder, i64, "parq_merge_rem");
        LlvmValueHandle resSlot = LlvmApi.BuildAlloca(builder, i64, "parq_merge_res");
        return new QueueDrainCtx(desc, count, mapper, combiner, totalItems, elemsBase, itemsBase, flagsBase,
            roundCountSlot, prevBaseSlot, curBaseSlot, remSlot, resSlot);
    }

    /// <summary>Phase one: each iteration claims an element index from the shared counter and
    /// publishes <c>f(element)</c> into the leaf item. Returns the merge-phase entry block, reached
    /// once the leaf work is exhausted.</summary>
    private static LlvmBasicBlockHandle EmitParallelQueueDrainFoldLeaves(LlvmCodegenState state, LlvmValueHandle fn, QueueDrainCtx ctx)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle i64 = state.I64;
        LlvmValueHandle eight = LlvmApi.ConstInt(i64, 8, 0);
        var loopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_drain_loop");
        var bodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_drain_body");
        var mergeLoop = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_merge_loop");
        LlvmApi.BuildBr(builder, loopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBlock);
        // The next-index word is the first header word, so the descriptor address doubles as its address.
        LlvmValueHandle idx = EmitAtomicFetchAdd(state, ctx.Desc, 1, "parq_drain_claim");
        LlvmValueHandle hasWork = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, idx, ctx.Count, "parq_drain_has_work");
        LlvmApi.BuildCondBr(builder, hasWork, bodyBlock, mergeLoop);

        LlvmApi.PositionBuilderAtEnd(builder, bodyBlock);
        LlvmValueHandle idxBytes = LlvmApi.BuildMul(builder, idx, eight, "parq_drain_idx_bytes");
        LlvmValueHandle elem = LoadMemory(state, LlvmApi.BuildAdd(builder, ctx.ElemsBase, idxBytes, "parq_drain_elem_addr"), 0, "parq_drain_elem");
        LlvmValueHandle result = EmitCallClosure(state, ctx.Mapper, elem);
        EmitQueueItemPublish(state,
            LlvmApi.BuildAdd(builder, ctx.ItemsBase, idxBytes, "parq_drain_result_addr"),
            LlvmApi.BuildAdd(builder, ctx.FlagsBase, idxBytes, "parq_drain_flag_addr"),
            result, "parq_drain");
        LlvmApi.BuildBr(builder, loopBlock);
        return mergeLoop;
    }

    /// <summary>Phase two: claims merge-tree tasks round-major, locates each task's round, waits on
    /// its operand flags, and publishes <c>combine(left)(right)</c> (or promotes a lone item). The
    /// builder must be positioned at the merge-phase entry block on entry.</summary>
    private static void EmitParallelQueueDrainMerge(LlvmCodegenState state, LlvmValueHandle fn, QueueDrainCtx ctx)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle i64 = state.I64;
        LlvmValueHandle eight = LlvmApi.ConstInt(i64, 8, 0);
        LlvmValueHandle taskTotal = LlvmApi.BuildSub(builder, ctx.TotalItems, ctx.Count, "parq_merge_task_total");
        LlvmValueHandle mergeCounterAddr = LlvmApi.BuildAdd(builder, ctx.Desc, LlvmApi.ConstInt(i64, ParallelQueueNextMergeTask, 0), "parq_merge_counter");
        var mergeClaim = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_merge_claim");
        var mergeDone = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_merge_done");
        LlvmApi.BuildBr(builder, mergeClaim);

        LlvmApi.PositionBuilderAtEnd(builder, mergeClaim);
        LlvmValueHandle task = EmitAtomicFetchAdd(state, mergeCounterAddr, 1, "parq_merge_claim_task");
        LlvmValueHandle taskInRange = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, task, taskTotal, "parq_merge_has_task");
        LlvmApi.BuildStore(builder, ctx.Count, ctx.RoundCountSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(i64, 0, 0), ctx.PrevBaseSlot);
        LlvmApi.BuildStore(builder, ctx.Count, ctx.CurBaseSlot);
        LlvmApi.BuildStore(builder, task, ctx.RemSlot);
        EmitParallelQueueDrainLocate(state, fn, ctx, taskInRange, mergeDone);

        // The locate helper leaves the builder in its located block; the values below dominate it.
        LlvmValueHandle prevCount = LlvmApi.BuildLoad2(builder, i64, ctx.RoundCountSlot, "parq_merge_prev_count");
        LlvmValueHandle j = LlvmApi.BuildLoad2(builder, i64, ctx.RemSlot, "parq_merge_j");
        LlvmValueHandle prevBase = LlvmApi.BuildLoad2(builder, i64, ctx.PrevBaseSlot, "parq_merge_prev_base_val");
        LlvmValueHandle outBase = LlvmApi.BuildLoad2(builder, i64, ctx.CurBaseSlot, "parq_merge_out_base");
        LlvmValueHandle leftOffset = LlvmApi.BuildMul(builder, j, LlvmApi.ConstInt(i64, 2, 0), "parq_merge_left_off");
        LlvmValueHandle leftItem = LlvmApi.BuildAdd(builder, prevBase, leftOffset, "parq_merge_left_item");
        LlvmValueHandle leftItemBytes = LlvmApi.BuildMul(builder, leftItem, eight, "parq_merge_left_bytes");
        LlvmValueHandle outItem = LlvmApi.BuildAdd(builder, outBase, j, "parq_merge_out_item");
        LlvmValueHandle outItemBytes = LlvmApi.BuildMul(builder, outItem, eight, "parq_merge_out_bytes");
        EmitQueueFlagWait(state, LlvmApi.BuildAdd(builder, ctx.FlagsBase, leftItemBytes, "parq_merge_left_flag"), "parq_merge_left");
        // The wait helper leaves the builder in its ready block; re-derive nothing — values above
        // dominate it. A right operand exists unless this is an odd round's promoted last item.
        LlvmValueHandle left = LoadMemory(state, LlvmApi.BuildAdd(builder, ctx.ItemsBase, leftItemBytes, "parq_merge_left_addr"), 0, "parq_merge_left_val");
        LlvmValueHandle rightOffset = LlvmApi.BuildAdd(builder, leftOffset, LlvmApi.ConstInt(i64, 1, 0), "parq_merge_right_off");
        LlvmValueHandle hasRight = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, rightOffset, prevCount, "parq_merge_has_right");
        EmitParallelQueueDrainCombine(state, fn, ctx, hasRight, leftItemBytes, left, outItemBytes, mergeClaim);

        LlvmApi.PositionBuilderAtEnd(builder, mergeDone);
        LlvmApi.BuildRetVoid(builder);
    }

    /// <summary>Locates a claimed merge task's round: peels whole rounds off <c>rem</c> (round-major
    /// task ids) until it indexes into the current round, tracking the previous round's item count
    /// and the previous/current rounds' first item indexes. Emits the in-range branch from the claim
    /// block and leaves the builder in the located block.</summary>
    private static void EmitParallelQueueDrainLocate(LlvmCodegenState state, LlvmValueHandle fn, QueueDrainCtx ctx, LlvmValueHandle taskInRange, LlvmBasicBlockHandle mergeDone)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle i64 = state.I64;
        LlvmValueHandle one = LlvmApi.ConstInt(i64, 1, 0);
        var locateLoop = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_merge_locate");
        var locateNext = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_merge_locate_next");
        var locateDone = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_merge_located");
        LlvmApi.BuildCondBr(builder, taskInRange, locateLoop, mergeDone);

        LlvmApi.PositionBuilderAtEnd(builder, locateLoop);
        LlvmValueHandle c = LlvmApi.BuildLoad2(builder, i64, ctx.RoundCountSlot, "parq_merge_c_val");
        LlvmValueHandle roundSize = LlvmApi.BuildLShr(builder, LlvmApi.BuildAdd(builder, c, one, "parq_merge_c1a"), one, "parq_merge_round_size");
        LlvmValueHandle rem = LlvmApi.BuildLoad2(builder, i64, ctx.RemSlot, "parq_merge_rem_val");
        LlvmValueHandle inRound = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, rem, roundSize, "parq_merge_in_round");
        LlvmApi.BuildCondBr(builder, inRound, locateDone, locateNext);

        LlvmApi.PositionBuilderAtEnd(builder, locateNext);
        LlvmApi.BuildStore(builder, LlvmApi.BuildSub(builder, rem, roundSize, "parq_merge_rem_next"), ctx.RemSlot);
        LlvmValueHandle curBase = LlvmApi.BuildLoad2(builder, i64, ctx.CurBaseSlot, "parq_merge_cur_base_val");
        LlvmApi.BuildStore(builder, curBase, ctx.PrevBaseSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, curBase, roundSize, "parq_merge_cur_base_next"), ctx.CurBaseSlot);
        LlvmApi.BuildStore(builder, roundSize, ctx.RoundCountSlot);
        LlvmApi.BuildBr(builder, locateLoop);

        LlvmApi.PositionBuilderAtEnd(builder, locateDone);
    }

    /// <summary>Combine/promote/publish tail of a merge task: waits on the right operand and stores
    /// <c>combine(left)(right)</c>, or promotes a lone <paramref name="left"/>, then publishes the
    /// output item and branches back to <paramref name="mergeClaim"/>.</summary>
    private static void EmitParallelQueueDrainCombine(LlvmCodegenState state, LlvmValueHandle fn, QueueDrainCtx ctx,
        LlvmValueHandle hasRight, LlvmValueHandle leftItemBytes, LlvmValueHandle left, LlvmValueHandle outItemBytes, LlvmBasicBlockHandle mergeClaim)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle i64 = state.I64;
        LlvmValueHandle eight = LlvmApi.ConstInt(i64, 8, 0);
        var combineBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_merge_combine");
        var promoteBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_merge_promote");
        var publishBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_merge_publish");
        LlvmApi.BuildCondBr(builder, hasRight, combineBlock, promoteBlock);

        LlvmApi.PositionBuilderAtEnd(builder, combineBlock);
        LlvmValueHandle rightItemBytes = LlvmApi.BuildAdd(builder, leftItemBytes, eight, "parq_merge_right_bytes");
        EmitQueueFlagWait(state, LlvmApi.BuildAdd(builder, ctx.FlagsBase, rightItemBytes, "parq_merge_right_flag"), "parq_merge_right");
        LlvmValueHandle right = LoadMemory(state, LlvmApi.BuildAdd(builder, ctx.ItemsBase, rightItemBytes, "parq_merge_right_addr"), 0, "parq_merge_right_val");
        LlvmValueHandle partial = EmitCallClosure(state, ctx.Combiner, left);
        LlvmValueHandle merged = EmitCallClosure(state, partial, right);
        LlvmApi.BuildStore(builder, merged, ctx.ResSlot);
        LlvmApi.BuildBr(builder, publishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, promoteBlock);
        LlvmApi.BuildStore(builder, left, ctx.ResSlot);
        LlvmApi.BuildBr(builder, publishBlock);

        LlvmApi.PositionBuilderAtEnd(builder, publishBlock);
        EmitQueueItemPublish(state,
            LlvmApi.BuildAdd(builder, ctx.ItemsBase, outItemBytes, "parq_merge_out_addr"),
            LlvmApi.BuildAdd(builder, ctx.FlagsBase, outItemBytes, "parq_merge_out_flag"),
            LlvmApi.BuildLoad2(builder, i64, ctx.ResSlot, "parq_merge_res_val"), "parq_merge");
        LlvmApi.BuildBr(builder, mergeClaim);
    }

    /// <summary>
    /// <c>__ashes_parallel_queue_worker(rec)</c>: the function each queue worker thread runs.
    /// Points the thread's arena at the TCB/TLS block the spawner prepared (exactly like the
    /// <c>both</c> trampoline), drains the queue, publishes the arm64 arena end for cleanup, and
    /// releases the worker slot. Per-element results were already published by the drain loop, so
    /// there is no done word here; the kernel's ctid clear signals thread exit to the cleanup.
    /// </summary>
    private static void EmitParallelQueueWorkerTrampoline(LlvmTargetContext target, LlvmCodegenFlavor flavor, LlvmAttributeHandle nounwindAttr)
    {
        LlvmTypeHandle i64 = LlvmApi.Int64TypeInContext(target.Context);
        LlvmBuilderHandle builder = target.Builder;
        LlvmValueHandle fn = AddQueueRuntimeFn(target, nounwindAttr, ParallelQueueWorkerFnName, 1, returnsValue: false);
        LlvmCodegenState state = WithWindowsRuntimeImports(CreateBareRuntimeState(target, fn, flavor));
        LlvmValueHandle rec = LlvmApi.GetParam(fn, 0);

        if (flavor == LlvmCodegenFlavor.LinuxArm64)
        {
            LlvmValueHandle tlsBlock = LoadMemory(state, rec, ParallelDescWorkerTcb, "parq_worker_tls");
            EmitArm64SetThreadPointer(state, tlsBlock);
            state = WithArm64ThreadLocalArenaSlots(state);
            EmitHeapChunkInit(state);
        }
        else
        {
            LlvmValueHandle tcb = LoadMemory(state, rec, ParallelDescWorkerTcb, "parq_worker_tcb");
            if (flavor == LlvmCodegenFlavor.LinuxX64)
            {
                EmitLinuxSyscall(state, SyscallArchPrctl,
                    LlvmApi.ConstInt(i64, (ulong)ArchSetGs, 0), tcb, LlvmApi.ConstInt(i64, 0, 0), "parq_worker_set_gs");
            }
            else
            {
                EmitWriteTcbBaseToTeb(state, tcb);
            }

            state = WithLinuxThreadArena(state);
        }

        LlvmValueHandle desc = LoadMemory(state, rec, ParallelQueueRecDesc, "parq_worker_desc");
        LlvmApi.BuildCall2(builder, LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(target.Context), [i64]),
            LlvmApi.GetNamedFunction(target.Module, ParallelQueueDrainFnName), [desc], "");

        if (flavor == LlvmCodegenFlavor.LinuxArm64)
        {
            // Publish the worker's heap-arena end so cleanup can walk+free its chunks (the parent
            // can't read it from the TLS block without the link-time tprel offset).
            LlvmValueHandle heapEnd = LlvmApi.BuildLoad2(builder, i64, state.HeapEndSlot, "parq_worker_heap_end");
            StoreMemory(state, rec, ParallelDescWorkerArenaEnd, heapEnd, "parq_worker_arena_end");
        }

        LlvmValueHandle activeAddr = LlvmApi.BuildPtrToInt(builder,
            LlvmApi.GetNamedGlobal(target.Module, ParallelActiveCounterName), i64, "parq_worker_counter_addr");
        EmitAtomicFetchAdd(state, activeAddr, unchecked((ulong)-1L), "parq_worker_release");
        LlvmApi.BuildRetVoid(builder);
    }

    /// <summary>
    /// <c>__ashes_parallel_queue_start(f, combine, list) -> desc</c>: counts the list, allocates
    /// the zero-initialized queue region (sized for the full merge tree), snapshots the elements
    /// into it, and spawns up to <c>min(cap, n)</c> workers (each claiming a slot in the shared
    /// active counter exactly like a <c>both</c> fork, with its own TCB, arena chunk, and stack).
    /// When not a single slot could be claimed, the caller drains the whole queue — folds and
    /// merges — inline before returning; the queue never blocks waiting for capacity, which also
    /// makes nested queued reduces deadlock-free.
    /// </summary>
    private static void EmitParallelQueueStartFn(LlvmTargetContext target, LlvmCodegenFlavor flavor, LlvmAttributeHandle nounwindAttr)
    {
        LlvmTypeHandle i64 = LlvmApi.Int64TypeInContext(target.Context);
        LlvmBuilderHandle builder = target.Builder;
        LlvmValueHandle fn = AddQueueRuntimeFn(target, nounwindAttr, ParallelQueueStartFnName, 3, returnsValue: true);
        LlvmCodegenState state = WithWindowsRuntimeImports(CreateBareRuntimeState(target, fn, flavor));
        LlvmValueHandle mapper = LlvmApi.GetParam(fn, 0);
        LlvmValueHandle combiner = LlvmApi.GetParam(fn, 1);
        LlvmValueHandle list = LlvmApi.GetParam(fn, 2);
        LlvmValueHandle zero = LlvmApi.ConstInt(i64, 0, 0);

        LlvmValueHandle curSlot = LlvmApi.BuildAlloca(builder, i64, "parq_start_cur");
        LlvmValueHandle countSlot = LlvmApi.BuildAlloca(builder, i64, "parq_start_count");
        LlvmValueHandle idxSlot = LlvmApi.BuildAlloca(builder, i64, "parq_start_idx");
        LlvmValueHandle spawnedSlot = LlvmApi.BuildAlloca(builder, i64, "parq_start_spawned");
        LlvmValueHandle roundSlot = LlvmApi.BuildAlloca(builder, i64, "parq_start_round");
        LlvmValueHandle totalSlot = LlvmApi.BuildAlloca(builder, i64, "parq_start_total");
        LlvmApi.BuildStore(builder, list, curSlot);
        LlvmApi.BuildStore(builder, zero, countSlot);

        LlvmValueHandle count = EmitParallelQueueStartCount(state, fn, curSlot, countSlot);
        LlvmValueHandle totalItems = EmitParallelQueueStartSizeTree(state, fn, count, roundSlot, totalSlot);
        QueueStartRegion region = EmitParallelQueueStartAllocRegion(state, count, totalItems, mapper, combiner);
        EmitParallelQueueStartSnapshot(state, fn, list, curSlot, idxSlot, region.ElemsBase);
        LlvmValueHandle spawnedTotal = EmitParallelQueueStartSpawn(state, fn, flavor, region, spawnedSlot);
        StoreMemory(state, region.Desc, ParallelQueueWorkerCount, spawnedTotal, "parq_desc_workers");
        EmitParallelQueueStartFinish(state, fn, region.Desc, spawnedTotal);
    }

    /// <summary>Region pointers/sizes threaded through the start phases after the queue region is
    /// allocated: the descriptor, the effective cap and worker ceiling, the payload span (used to
    /// locate the worker records), and the element-snapshot base.</summary>
    private readonly record struct QueueStartRegion(
        LlvmValueHandle Desc,
        LlvmValueHandle CapValue,
        LlvmValueHandle MaxWorkers,
        LlvmValueHandle PayloadBytes,
        LlvmValueHandle ElemsBase);

    /// <summary>Counts the input list into <paramref name="countSlot"/> (walking via
    /// <paramref name="curSlot"/>) and returns the loaded element count, leaving the builder past the
    /// count loop.</summary>
    private static LlvmValueHandle EmitParallelQueueStartCount(LlvmCodegenState state, LlvmValueHandle fn, LlvmValueHandle curSlot, LlvmValueHandle countSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle i64 = state.I64;
        LlvmValueHandle zero = LlvmApi.ConstInt(i64, 0, 0);
        var countLoop = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_count_loop");
        var countBody = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_count_body");
        var countDone = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_count_done");
        LlvmApi.BuildBr(builder, countLoop);

        LlvmApi.PositionBuilderAtEnd(builder, countLoop);
        LlvmValueHandle countCur = LlvmApi.BuildLoad2(builder, i64, curSlot, "parq_count_cur");
        LlvmValueHandle countIsNil = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, countCur, zero, "parq_count_is_nil");
        LlvmApi.BuildCondBr(builder, countIsNil, countDone, countBody);

        LlvmApi.PositionBuilderAtEnd(builder, countBody);
        LlvmValueHandle countVal = LlvmApi.BuildLoad2(builder, i64, countSlot, "parq_count_val");
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, countVal, LlvmApi.ConstInt(i64, 1, 0), "parq_count_next"), countSlot);
        LlvmApi.BuildStore(builder, LoadMemory(state, countCur, 8, "parq_count_tail"), curSlot);
        LlvmApi.BuildBr(builder, countLoop);

        LlvmApi.PositionBuilderAtEnd(builder, countDone);
        return LlvmApi.BuildLoad2(builder, i64, countSlot, "parq_n");
    }

    /// <summary>Sizes the merge tree <c>S = n + ceil(n/2) + ... + 1</c> (0 for an empty list) using
    /// <paramref name="roundSlot"/>/<paramref name="totalSlot"/>, returning the total item count.</summary>
    private static LlvmValueHandle EmitParallelQueueStartSizeTree(LlvmCodegenState state, LlvmValueHandle fn, LlvmValueHandle count, LlvmValueHandle roundSlot, LlvmValueHandle totalSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle i64 = state.I64;
        LlvmValueHandle one = LlvmApi.ConstInt(i64, 1, 0);
        LlvmApi.BuildStore(builder, count, roundSlot);
        LlvmApi.BuildStore(builder, count, totalSlot);
        var sizeLoop = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_size_loop");
        var sizeBody = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_size_body");
        var sizeDone = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_size_done");
        LlvmApi.BuildBr(builder, sizeLoop);

        LlvmApi.PositionBuilderAtEnd(builder, sizeLoop);
        LlvmValueHandle sizeRound = LlvmApi.BuildLoad2(builder, i64, roundSlot, "parq_size_round");
        LlvmValueHandle sizeMore = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, sizeRound, one, "parq_size_more");
        LlvmApi.BuildCondBr(builder, sizeMore, sizeBody, sizeDone);

        LlvmApi.PositionBuilderAtEnd(builder, sizeBody);
        LlvmValueHandle sizeNext = LlvmApi.BuildLShr(builder, LlvmApi.BuildAdd(builder, sizeRound, one, "parq_size_r1"), one, "parq_size_next");
        LlvmApi.BuildStore(builder, sizeNext, roundSlot);
        LlvmValueHandle sizeTotal = LlvmApi.BuildLoad2(builder, i64, totalSlot, "parq_size_total_val");
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, sizeTotal, sizeNext, "parq_size_total_next"), totalSlot);
        LlvmApi.BuildBr(builder, sizeLoop);

        LlvmApi.PositionBuilderAtEnd(builder, sizeDone);
        return LlvmApi.BuildLoad2(builder, i64, totalSlot, "parq_s");
    }

    /// <summary>Allocates the zero-initialized queue region (header + n elements + S item/flag words
    /// + one record per worker slot), writes the descriptor header fields, and returns the region
    /// pointers plus the effective worker cap/ceiling.</summary>
    private static QueueStartRegion EmitParallelQueueStartAllocRegion(LlvmCodegenState state, LlvmValueHandle count, LlvmValueHandle totalItems, LlvmValueHandle mapper, LlvmValueHandle combiner)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle i64 = state.I64;
        LlvmValueHandle eight = LlvmApi.ConstInt(i64, 8, 0);
        // Compiled max narrowed by any active withWorkers override, then by the element count.
        LlvmValueHandle capValue = EmitEffectiveWorkerCap(state, "parq_cap");
        LlvmValueHandle capBelowN = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, capValue, count, "parq_cap_below_n");
        LlvmValueHandle maxWorkers = LlvmApi.BuildSelect(builder, capBelowN, capValue, count, "parq_max_workers");
        // size = header + n element words + S item words + S flag words + one record per worker slot.
        LlvmValueHandle payloadBytes = LlvmApi.BuildAdd(builder,
            LlvmApi.BuildMul(builder, count, eight, "parq_elem_bytes"),
            LlvmApi.BuildMul(builder, totalItems, LlvmApi.ConstInt(i64, 16, 0), "parq_tree_bytes"),
            "parq_payload_bytes");
        LlvmValueHandle recordBytes = LlvmApi.BuildMul(builder, maxWorkers, LlvmApi.ConstInt(i64, ParallelQueueRecordBytes, 0), "parq_rec_bytes");
        LlvmValueHandle regionBytes = LlvmApi.BuildAdd(builder,
            LlvmApi.BuildAdd(builder, LlvmApi.ConstInt(i64, ParallelQueueHeaderBytes, 0), payloadBytes, "parq_header_payload"),
            recordBytes, "parq_region_bytes");
        LlvmValueHandle desc = EmitAllocateOsMemory(state, regionBytes, "parq_region");
        // mmap/VirtualAlloc zero-fill, so the two claim counters, flags, and worker count start 0.
        StoreMemory(state, desc, ParallelQueueCount, count, "parq_desc_n");
        StoreMemory(state, desc, ParallelQueueClosure, mapper, "parq_desc_f");
        StoreMemory(state, desc, ParallelQueueCombine, combiner, "parq_desc_combine");
        StoreMemory(state, desc, ParallelQueueTotalItems, totalItems, "parq_desc_s");
        StoreMemory(state, desc, ParallelQueueRegionBytes, regionBytes, "parq_desc_size");
        LlvmValueHandle elemsBase = LlvmApi.BuildAdd(builder, desc, LlvmApi.ConstInt(i64, ParallelQueueHeaderBytes, 0), "parq_elems_base");
        return new QueueStartRegion(desc, capValue, maxWorkers, payloadBytes, elemsBase);
    }

    /// <summary>Snapshots the list elements into the region (walking via <paramref name="curSlot"/>,
    /// index in <paramref name="idxSlot"/>, writing to <paramref name="elemsBase"/>).</summary>
    private static void EmitParallelQueueStartSnapshot(LlvmCodegenState state, LlvmValueHandle fn, LlvmValueHandle list, LlvmValueHandle curSlot, LlvmValueHandle idxSlot, LlvmValueHandle elemsBase)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle i64 = state.I64;
        LlvmValueHandle zero = LlvmApi.ConstInt(i64, 0, 0);
        LlvmValueHandle eight = LlvmApi.ConstInt(i64, 8, 0);
        LlvmApi.BuildStore(builder, list, curSlot);
        LlvmApi.BuildStore(builder, zero, idxSlot);
        var fillLoop = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_fill_loop");
        var fillBody = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_fill_body");
        var fillDone = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_fill_done");
        LlvmApi.BuildBr(builder, fillLoop);

        LlvmApi.PositionBuilderAtEnd(builder, fillLoop);
        LlvmValueHandle fillCur = LlvmApi.BuildLoad2(builder, i64, curSlot, "parq_fill_cur");
        LlvmValueHandle fillIsNil = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, fillCur, zero, "parq_fill_is_nil");
        LlvmApi.BuildCondBr(builder, fillIsNil, fillDone, fillBody);

        LlvmApi.PositionBuilderAtEnd(builder, fillBody);
        LlvmValueHandle fillIdx = LlvmApi.BuildLoad2(builder, i64, idxSlot, "parq_fill_idx");
        LlvmValueHandle head = LoadMemory(state, fillCur, 0, "parq_fill_head");
        LlvmValueHandle elemAddr = LlvmApi.BuildAdd(builder, elemsBase,
            LlvmApi.BuildMul(builder, fillIdx, eight, "parq_fill_off"), "parq_fill_elem_addr");
        StoreMemory(state, elemAddr, 0, head, "parq_fill_elem");
        LlvmApi.BuildStore(builder, LoadMemory(state, fillCur, 8, "parq_fill_tail"), curSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, fillIdx, LlvmApi.ConstInt(i64, 1, 0), "parq_fill_next"), idxSlot);
        LlvmApi.BuildBr(builder, fillLoop);

        LlvmApi.PositionBuilderAtEnd(builder, fillDone);
    }

    /// <summary>Spawn loop: each iteration claims a slot in the shared active counter (up to the cap
    /// or <c>maxWorkers</c>) and launches a queue worker over its own record. Returns the number of
    /// workers actually spawned, leaving the builder at the spawn-done block.</summary>
    private static LlvmValueHandle EmitParallelQueueStartSpawn(LlvmCodegenState state, LlvmValueHandle fn, LlvmCodegenFlavor flavor, QueueStartRegion region, LlvmValueHandle spawnedSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle i64 = state.I64;
        LlvmValueHandle zero = LlvmApi.ConstInt(i64, 0, 0);
        LlvmValueHandle recordsBase = LlvmApi.BuildAdd(builder, region.ElemsBase, region.PayloadBytes, "parq_recs_base");
        LlvmValueHandle counterAddr = LlvmApi.BuildPtrToInt(builder,
            LlvmApi.GetNamedGlobal(state.Target.Module, ParallelActiveCounterName), i64, "parq_counter_addr");
        LlvmApi.BuildStore(builder, zero, spawnedSlot);
        var spawnLoop = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_spawn_loop");
        var spawnTry = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_spawn_try");
        var spawnBody = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_spawn_body");
        var spawnAbort = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_spawn_abort");
        var spawnDone = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_spawn_done");
        LlvmApi.BuildBr(builder, spawnLoop);

        LlvmApi.PositionBuilderAtEnd(builder, spawnLoop);
        LlvmValueHandle spawned = LlvmApi.BuildLoad2(builder, i64, spawnedSlot, "parq_spawned");
        LlvmValueHandle wantMore = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, spawned, region.MaxWorkers, "parq_want_more");
        LlvmApi.BuildCondBr(builder, wantMore, spawnTry, spawnDone);

        LlvmApi.PositionBuilderAtEnd(builder, spawnTry);
        LlvmValueHandle prevActive = EmitAtomicFetchAdd(state, counterAddr, 1, "parq_claim");
        LlvmValueHandle canSpawn = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, prevActive, region.CapValue, "parq_can_spawn");
        LlvmApi.BuildCondBr(builder, canSpawn, spawnBody, spawnAbort);

        LlvmApi.PositionBuilderAtEnd(builder, spawnAbort);
        EmitAtomicFetchAdd(state, counterAddr, unchecked((ulong)-1L), "parq_unclaim");
        LlvmApi.BuildBr(builder, spawnDone);

        LlvmApi.PositionBuilderAtEnd(builder, spawnBody);
        LlvmValueHandle recordAddr = LlvmApi.BuildAdd(builder, recordsBase,
            LlvmApi.BuildMul(builder, spawned, LlvmApi.ConstInt(i64, ParallelQueueRecordBytes, 0), "parq_rec_off"), "parq_rec");
        StoreMemory(state, recordAddr, ParallelQueueRecDesc, region.Desc, "parq_rec_desc");
        EmitParallelQueueStartSpawnWorker(state, recordAddr, flavor);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, spawned, LlvmApi.ConstInt(i64, 1, 0), "parq_spawned_next"), spawnedSlot);
        LlvmApi.BuildBr(builder, spawnLoop);

        LlvmApi.PositionBuilderAtEnd(builder, spawnDone);
        return LlvmApi.BuildLoad2(builder, i64, spawnedSlot, "parq_spawned_total");
    }

    /// <summary>Prepares one queue worker's per-thread control region (exactly as in
    /// EmitParallelForkSpawn: a TCB pre-wired to a fresh arena chunk on x64/win; a zeroed TLS block
    /// on arm64) and launches it (linux clone / win-x64 CreateThread), recording the stack/HANDLE and
    /// ctid word in <paramref name="recordAddr"/>.</summary>
    private static void EmitParallelQueueStartSpawnWorker(LlvmCodegenState state, LlvmValueHandle recordAddr, LlvmCodegenFlavor flavor)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle i64 = state.I64;
        LlvmValueHandle zero = LlvmApi.ConstInt(i64, 0, 0);
        LlvmValueHandle workerTcb = EmitAllocateOsMemory(state, LlvmApi.ConstInt(i64, (ulong)MainTcbSizeBytes, 0), "parq_tcb");
        if (flavor != LlvmCodegenFlavor.LinuxArm64)
        {
            LlvmValueHandle chunk = EmitAllocateOsMemory(state, LlvmApi.ConstInt(i64, HeapChunkBytes, 0), "parq_chunk");
            StoreMemory(state, workerTcb, (int)TcbSelfOffset, workerTcb, "parq_tcb_self");
            var (parqCursorSlot, parqEndSlot) = BuildLinuxTcbSlots(state, workerTcb, TcbHeapCursorOffset, TcbHeapEndOffset);
            EmitHeapChunkSetup(state, chunk, LlvmApi.ConstInt(i64, HeapChunkBytes, 0), zero, parqCursorSlot, parqEndSlot, "parq_chunk");
        }

        StoreMemory(state, recordAddr, ParallelDescWorkerTcb, workerTcb, "parq_rec_tcb");
        if (IsLinuxFlavor(flavor))
        {
            long parallelStackBytes = ParallelStackBytesFor(state);
            LlvmValueHandle stack = EmitAllocateOsMemory(state, LlvmApi.ConstInt(i64, (ulong)parallelStackBytes, 0), "parq_stack");
            StoreMemory(state, recordAddr, ParallelDescWorkerStack, stack, "parq_rec_stack");
            StoreMemory(state, recordAddr, ParallelDescExited, LlvmApi.ConstInt(i64, 1, 0), "parq_rec_exited1");
            LlvmValueHandle stackTop = LlvmApi.BuildAdd(builder, stack, LlvmApi.ConstInt(i64, (ulong)parallelStackBytes, 0), "parq_stack_top");
            EmitCloneWorker(state, recordAddr, stackTop, ParallelQueueWorkerFnName);
        }
        else
        {
            LlvmValueHandle workerFn = LlvmApi.GetNamedFunction(state.Target.Module, ParallelQueueWorkerFnName);
            LlvmTypeHandle createThreadType = LlvmApi.FunctionType(state.I64, [state.I64, state.I64, state.I8Ptr, state.I8Ptr, state.I64, state.I64]);
            LlvmValueHandle stackSize = LlvmApi.ConstInt(state.I64, (ulong)(state.Target.ParallelWorkerStackBytes ?? 0), 0);
            LlvmValueHandle handle = EmitWindowsImportCall(state, "__imp_CreateThread", createThreadType,
                [zero, stackSize, workerFn, LlvmApi.BuildIntToPtr(builder, recordAddr, state.I8Ptr, "parq_rec_ptr"), zero, zero], "parq_create_thread");
            StoreMemory(state, recordAddr, ParallelDescWorkerStack, handle, "parq_rec_handle");
        }
    }

    /// <summary>Start-function tail: if no worker slot was claimed at all, drains the whole queue on
    /// this thread (correct and deadlock-free), then returns the descriptor.</summary>
    private static void EmitParallelQueueStartFinish(LlvmCodegenState state, LlvmValueHandle fn, LlvmValueHandle desc, LlvmValueHandle spawnedTotal)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle i64 = state.I64;
        LlvmValueHandle zero = LlvmApi.ConstInt(i64, 0, 0);
        var drainInline = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_drain_inline");
        var startRet = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_start_ret");
        LlvmValueHandle anySpawned = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, spawnedTotal, zero, "parq_any_spawned");
        LlvmApi.BuildCondBr(builder, anySpawned, startRet, drainInline);

        LlvmApi.PositionBuilderAtEnd(builder, drainInline);
        LlvmApi.BuildCall2(builder, LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [i64]),
            LlvmApi.GetNamedFunction(state.Target.Module, ParallelQueueDrainFnName), [desc], "");
        LlvmApi.BuildBr(builder, startRet);

        LlvmApi.PositionBuilderAtEnd(builder, startRet);
        LlvmApi.BuildRet(builder, desc);
    }

    /// <summary>
    /// <c>__ashes_parallel_queue_await(desc) -> raw root result</c>: blocks until the merge tree's
    /// root — the last item slot — has been published, then returns it (still in worker arenas;
    /// the caller deep-copies before cleanup). Must only be called for a non-empty element list.
    /// </summary>
    private static void EmitParallelQueueAwaitFn(LlvmTargetContext target, LlvmCodegenFlavor flavor, LlvmAttributeHandle nounwindAttr)
    {
        LlvmTypeHandle i64 = LlvmApi.Int64TypeInContext(target.Context);
        LlvmBuilderHandle builder = target.Builder;
        LlvmValueHandle fn = AddQueueRuntimeFn(target, nounwindAttr, ParallelQueueAwaitFnName, 1, returnsValue: true);
        LlvmCodegenState state = CreateBareRuntimeState(target, fn, flavor);
        LlvmValueHandle desc = LlvmApi.GetParam(fn, 0);
        LlvmValueHandle eight = LlvmApi.ConstInt(i64, 8, 0);

        LlvmValueHandle count = LoadMemory(state, desc, ParallelQueueCount, "parq_await_n");
        LlvmValueHandle totalItems = LoadMemory(state, desc, ParallelQueueTotalItems, "parq_await_s");
        LlvmValueHandle itemsBase = LlvmApi.BuildAdd(builder,
            LlvmApi.BuildAdd(builder, desc, LlvmApi.ConstInt(i64, ParallelQueueHeaderBytes, 0), "parq_await_elems"),
            LlvmApi.BuildMul(builder, count, eight, "parq_await_nbytes"), "parq_await_items");
        LlvmValueHandle itemBytes = LlvmApi.BuildMul(builder, totalItems, eight, "parq_await_sbytes");
        LlvmValueHandle rootBytes = LlvmApi.BuildSub(builder, itemBytes, eight, "parq_await_root_bytes");
        LlvmValueHandle rootAddr = LlvmApi.BuildAdd(builder, itemsBase, rootBytes, "parq_await_root_addr");
        LlvmValueHandle rootFlagAddr = LlvmApi.BuildAdd(builder,
            LlvmApi.BuildAdd(builder, itemsBase, itemBytes, "parq_await_flags"), rootBytes, "parq_await_root_flag");

        EmitQueueFlagWait(state, rootFlagAddr, "parq_await");
        LlvmApi.BuildRet(builder, LoadMemory(state, rootAddr, 0, "parq_await_result"));
    }

    /// <summary>
    /// <c>__ashes_parallel_queue_cleanup(desc)</c>: waits for every spawned worker thread to fully
    /// exit (kernel ctid clear on linux, thread handle on win-x64), frees each worker's stack,
    /// arena chunks, and TCB — mirroring <c>both</c>'s cleanup — then frees the queue region.
    /// </summary>
    private static void EmitParallelQueueCleanupFn(LlvmTargetContext target, LlvmCodegenFlavor flavor, LlvmAttributeHandle nounwindAttr)
    {
        LlvmTypeHandle i64 = LlvmApi.Int64TypeInContext(target.Context);
        LlvmBuilderHandle builder = target.Builder;
        LlvmValueHandle fn = AddQueueRuntimeFn(target, nounwindAttr, ParallelQueueCleanupFnName, 1, returnsValue: false);
        LlvmCodegenState state = WithWindowsRuntimeImports(CreateBareRuntimeState(target, fn, flavor));
        LlvmValueHandle desc = LlvmApi.GetParam(fn, 0);
        LlvmValueHandle zero = LlvmApi.ConstInt(i64, 0, 0);

        LlvmValueHandle count = LoadMemory(state, desc, ParallelQueueCount, "parq_cleanup_n");
        LlvmValueHandle totalItems = LoadMemory(state, desc, ParallelQueueTotalItems, "parq_cleanup_s");
        LlvmValueHandle workers = LoadMemory(state, desc, ParallelQueueWorkerCount, "parq_cleanup_workers");
        LlvmValueHandle regionBytes = LoadMemory(state, desc, ParallelQueueRegionBytes, "parq_cleanup_size");
        LlvmValueHandle payloadBytes = LlvmApi.BuildAdd(builder,
            LlvmApi.BuildMul(builder, count, LlvmApi.ConstInt(i64, 8, 0), "parq_cleanup_elem_bytes"),
            LlvmApi.BuildMul(builder, totalItems, LlvmApi.ConstInt(i64, 16, 0), "parq_cleanup_tree_bytes"), "parq_cleanup_payload_bytes");
        LlvmValueHandle recordsBase = LlvmApi.BuildAdd(builder,
            LlvmApi.BuildAdd(builder, desc, LlvmApi.ConstInt(i64, ParallelQueueHeaderBytes, 0), "parq_cleanup_payload"),
            payloadBytes, "parq_cleanup_recs");
        LlvmValueHandle wSlot = LlvmApi.BuildAlloca(builder, i64, "parq_cleanup_w");
        LlvmApi.BuildStore(builder, zero, wSlot);

        var loopBlock = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "parq_cleanup_loop");
        var bodyBlock = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "parq_cleanup_body");
        var regionBlock = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "parq_cleanup_region");
        LlvmApi.BuildBr(builder, loopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBlock);
        LlvmValueHandle w = LlvmApi.BuildLoad2(builder, i64, wSlot, "parq_cleanup_w_val");
        LlvmValueHandle hasMore = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, w, workers, "parq_cleanup_has_more");
        LlvmApi.BuildCondBr(builder, hasMore, bodyBlock, regionBlock);

        LlvmApi.PositionBuilderAtEnd(builder, bodyBlock);
        LlvmValueHandle recordAddr = LlvmApi.BuildAdd(builder, recordsBase,
            LlvmApi.BuildMul(builder, w, LlvmApi.ConstInt(i64, ParallelQueueRecordBytes, 0), "parq_cleanup_rec_off"), "parq_cleanup_rec");
        EmitParallelQueueCleanupWorker(state, fn, recordAddr, flavor);
        LlvmValueHandle wNext = LlvmApi.BuildAdd(builder,
            LlvmApi.BuildLoad2(builder, i64, wSlot, "parq_cleanup_w_reload"), LlvmApi.ConstInt(i64, 1, 0), "parq_cleanup_w_next");
        LlvmApi.BuildStore(builder, wNext, wSlot);
        LlvmApi.BuildBr(builder, loopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, regionBlock);
        if (IsLinuxFlavor(flavor))
        {
            // The region size is dynamic, so munmap directly (EmitFreeOsMemory takes a constant size).
            EmitLinuxSyscall(state, SyscallMunmap, desc, regionBytes, zero, "parq_cleanup_region_munmap");
        }
        else
        {
            // VirtualFree with MEM_RELEASE ignores the size.
            EmitFreeOsMemory(state, desc, 0, "parq_cleanup_region");
        }

        LlvmApi.BuildRetVoid(builder);
    }

    /// <summary>
    /// Reclaims one finished queue worker's OS resources: waits for true thread exit (linux ctid
    /// clear via futex; win-x64 WaitForSingleObject on the handle), frees its stack, then walks and
    /// frees its arena chunks and TCB. Reads the worker record at <paramref name="recordAddr"/>.
    /// </summary>
    private static void EmitParallelQueueCleanupWorker(LlvmCodegenState state, LlvmValueHandle fn, LlvmValueHandle recordAddr, LlvmCodegenFlavor flavor)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle i64 = state.I64;
        LlvmValueHandle zero = LlvmApi.ConstInt(i64, 0, 0);
        LlvmValueHandle workerTcb = LoadMemory(state, recordAddr, ParallelDescWorkerTcb, "parq_cleanup_tcb");
        LlvmValueHandle arenaEnd;
        if (IsLinuxFlavor(flavor))
        {
            // Wait for true thread exit (the kernel zeroes the ctid/exited word and futex-wakes it
            // in mm_release, after the worker stack is no longer used) before reclaiming the stack.
            // Non-private FUTEX_WAIT to match the kernel's clear_child_tid wake.
            var exitCheck = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_cleanup_exit_check");
            var exitWait = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_cleanup_exit_wait");
            var exitDone = LlvmApi.AppendBasicBlockInContext(state.Target.Context, fn, "parq_cleanup_exited");
            LlvmValueHandle exitedAddr = LlvmApi.BuildAdd(builder, recordAddr, LlvmApi.ConstInt(i64, ParallelDescExited, 0), "parq_cleanup_exited_addr");
            LlvmApi.BuildBr(builder, exitCheck);

            LlvmApi.PositionBuilderAtEnd(builder, exitCheck);
            LlvmValueHandle exited = LoadMemory(state, recordAddr, ParallelDescExited, "parq_cleanup_exited_val");
            LlvmValueHandle stillRunning = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, exited, zero, "parq_cleanup_still_running");
            LlvmApi.BuildCondBr(builder, stillRunning, exitWait, exitDone);

            LlvmApi.PositionBuilderAtEnd(builder, exitWait);
            EmitLinuxSyscall6(state, SyscallFutex,
                exitedAddr,
                zero,
                LlvmApi.ConstInt(i64, 1, 0),
                zero,
                zero,
                zero,
                "parq_cleanup_exit_wait_call");
            LlvmApi.BuildBr(builder, exitCheck);

            LlvmApi.PositionBuilderAtEnd(builder, exitDone);
            EmitFreeOsMemory(state, LoadMemory(state, recordAddr, ParallelDescWorkerStack, "parq_cleanup_stack"), ParallelStackBytesFor(state), "parq_cleanup_stack");
            arenaEnd = flavor == LlvmCodegenFlavor.LinuxArm64
                ? LoadMemory(state, recordAddr, ParallelDescWorkerArenaEnd, "parq_cleanup_arena_end")
                : LoadMemory(state, workerTcb, (int)TcbHeapEndOffset, "parq_cleanup_arena_end");
        }
        else
        {
            LlvmValueHandle handle = LoadMemory(state, recordAddr, ParallelDescWorkerStack, "parq_cleanup_handle");
            LlvmTypeHandle waitType = LlvmApi.FunctionType(state.I32, [state.I64, state.I32]);
            EmitWindowsImportCall(state, "__imp_WaitForSingleObject", waitType,
                [handle, LlvmApi.ConstInt(state.I32, 0xFFFFFFFFUL, 0)], "parq_cleanup_wait");
            LlvmTypeHandle closeHandleType = LlvmApi.FunctionType(state.I32, [state.I64]);
            EmitWindowsImportCall(state, "__imp_CloseHandle", closeHandleType, [handle], "parq_cleanup_close");
            arenaEnd = LoadMemory(state, workerTcb, (int)TcbHeapEndOffset, "parq_cleanup_arena_end");
        }

        EmitFreeWorkerArenaChunks(state, arenaEnd);
        EmitFreeOsMemory(state, workerTcb, MainTcbSizeBytes, "parq_cleanup_tcb");
    }

    /// <summary>Looks up a queued-reduce runtime function, asserting it was emitted (the module
    /// emission gate emits the queue runtime whenever a ParallelQueueStart instruction exists).</summary>
    private static LlvmValueHandle GetQueueRuntimeFn(LlvmCodegenState state, string name)
    {
        LlvmValueHandle fn = LlvmApi.GetNamedFunction(state.Target.Module, name);
        if (fn == default)
        {
            throw new InvalidOperationException($"Parallel queue runtime function '{name}' was not emitted for this module.");
        }

        return fn;
    }

    private static LlvmValueHandle EmitParallelQueueStart(LlvmCodegenState state, LlvmValueHandle mapperClosure, LlvmValueHandle combineClosure, LlvmValueHandle list)
    {
        LlvmTypeHandle i64 = state.I64;
        return LlvmApi.BuildCall2(state.Target.Builder, LlvmApi.FunctionType(i64, [i64, i64, i64]),
            GetQueueRuntimeFn(state, ParallelQueueStartFnName), [mapperClosure, combineClosure, list], "parq_start");
    }

    private static LlvmValueHandle EmitParallelQueueAwait(LlvmCodegenState state, LlvmValueHandle desc)
    {
        LlvmTypeHandle i64 = state.I64;
        return LlvmApi.BuildCall2(state.Target.Builder, LlvmApi.FunctionType(i64, [i64]),
            GetQueueRuntimeFn(state, ParallelQueueAwaitFnName), [desc], "parq_await");
    }

    private static bool EmitParallelQueueCleanup(LlvmCodegenState state, LlvmValueHandle desc)
    {
        LlvmApi.BuildCall2(state.Target.Builder,
            LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I64]),
            GetQueueRuntimeFn(state, ParallelQueueCleanupFnName), [desc], "");
        return false;
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
        LlvmTypeHandle f32 = LlvmApi.FloatTypeInContext(target.Context);
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
            f32,
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
