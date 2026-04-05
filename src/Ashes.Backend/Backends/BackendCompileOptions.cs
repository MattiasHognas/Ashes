namespace Ashes.Backend.Backends;

public sealed record BackendCompileOptions(
    BackendOptimizationLevel OptimizationLevel,
    bool EmitDebugInfo = false)
{
    public static BackendCompileOptions Default { get; } =
        new(BackendOptimizationLevel.O2);
}
