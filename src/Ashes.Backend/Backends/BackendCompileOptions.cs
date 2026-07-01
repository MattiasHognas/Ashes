namespace Ashes.Backend.Backends;

public sealed record BackendCompileOptions(
    BackendOptimizationLevel OptimizationLevel,
    bool EmitDebugInfo = false,
    string? TargetCpu = null,
    long? ParallelWorkerStackBytes = null)
{
    public static BackendCompileOptions Default { get; } =
        new(BackendOptimizationLevel.O2);
}
