namespace Ashes.Backend.Backends;

/// <summary>
/// The knobs passed to <see cref="IBackend.Compile(Ashes.Semantics.IrProgram, BackendCompileOptions)"/>
/// controlling the LLVM pipeline, debug-info emission, target CPU tuning, and the parallel worker
/// runtime limits baked into the emitted executable.
/// </summary>
/// <param name="OptimizationLevel">The LLVM optimization pipeline to run.</param>
/// <param name="EmitDebugInfo">When true, emits DWARF/CodeView debug info so the binary is debuggable
/// under gdb/lldb.</param>
/// <param name="TargetCpu">An explicit LLVM target-CPU name to tune codegen for; null selects the
/// generic baseline for the target.</param>
/// <param name="ParallelWorkerStackBytes">Per-worker stack size, in bytes, for the parallel runtime;
/// null uses the built-in default.</param>
/// <param name="ParallelWorkerCap">Upper bound on the number of parallel workers the runtime spawns;
/// null uses the built-in default (derived from the host CPU count).</param>
public sealed record BackendCompileOptions(
    BackendOptimizationLevel OptimizationLevel,
    bool EmitDebugInfo = false,
    string? TargetCpu = null,
    long? ParallelWorkerStackBytes = null,
    long? ParallelWorkerCap = null)
{
    /// <summary>The default options: <see cref="BackendOptimizationLevel.O2"/> with no debug info and
    /// runtime defaults for every other knob.</summary>
    public static BackendCompileOptions Default { get; } =
        new(BackendOptimizationLevel.O2);
}
