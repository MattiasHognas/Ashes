using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{
    // Task descriptor layout (bytes) for Ashes.Parallel.both. Shared between the spawning
    // thread and the worker (both run in the same address space). Allocated in the parent arena
    // by EmitParallelFork.
    private const int ParallelDescDone = 0;          // futex word: 0 = running, 1 = done
    private const int ParallelDescResult = 8;        // worker's raw result pointer/value
    private const int ParallelDescMode = 16;         // 0 = ran inline, 1 = ran on a worker thread
    private const int ParallelDescRightClosure = 24; // closure the worker applies to Unit
    private const int ParallelDescWorkerStack = 32;  // mmap'd worker stack base (for munmap)
    private const int ParallelDescWorkerTcb = 40;    // mmap'd worker TCB base (for munmap + arena walk)
    private const int ParallelDescSizeBytes = 48;

    /// <summary>
    /// Lowers <see cref="IrInst.ParallelFork"/>. For now this evaluates the right thunk inline
    /// (correct, sequential) and records the result in a freshly allocated descriptor; the real
    /// worker-thread spawn is layered on top in a later step. Returns the descriptor pointer.
    /// </summary>
    private static LlvmValueHandle EmitParallelFork(LlvmCodegenState state, LlvmValueHandle rightClosure)
    {
        LlvmValueHandle desc = EmitAlloc(state, ParallelDescSizeBytes);
        StoreMemory(state, desc, ParallelDescRightClosure, rightClosure, "par_desc_right");

        // Inline path: run right(Unit) on this thread, in this arena.
        LlvmValueHandle unit = EmitUnitValue(state);
        LlvmValueHandle result = EmitCallClosure(state, rightClosure, unit);
        StoreMemory(state, desc, ParallelDescResult, result, "par_desc_result");
        StoreMemory(state, desc, ParallelDescDone, LlvmApi.ConstInt(state.I64, 1, 0), "par_desc_done");
        StoreMemory(state, desc, ParallelDescMode, LlvmApi.ConstInt(state.I64, 0, 0), "par_desc_mode");
        return desc;
    }

    /// <summary>
    /// Lowers <see cref="IrInst.ParallelJoin"/>: waits for the worker (if any) and yields its raw
    /// result. With the inline fork above there is nothing to wait for, so it just reads the slot.
    /// </summary>
    private static LlvmValueHandle EmitParallelJoin(LlvmCodegenState state, LlvmValueHandle desc)
    {
        return LoadMemory(state, desc, ParallelDescResult, "par_join_result");
    }

    /// <summary>
    /// Lowers <see cref="IrInst.ParallelCleanup"/>: frees worker resources. No-op for the inline
    /// fork; the worker-thread spawn step adds the munmap path.
    /// </summary>
    private static bool EmitParallelCleanup(LlvmCodegenState state, LlvmValueHandle desc)
    {
        _ = state;
        _ = desc;
        return false;
    }
}
