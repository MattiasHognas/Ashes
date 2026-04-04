namespace Ashes.Backend.Backends;

public sealed record BackendCompileOptions(
    BackendOptimizationLevel OptimizationLevel)
{
    public static BackendCompileOptions Default { get; } =
        new(BackendOptimizationLevel.O2);
}
