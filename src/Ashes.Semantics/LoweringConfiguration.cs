namespace Ashes.Semantics;

/// <summary>Internal compiler-analysis controls used by deterministic differential testing.</summary>
public sealed record LoweringConfiguration(bool EnableReuse = true)
{
    /// <summary>The normal production lowering configuration.</summary>
    public static LoweringConfiguration Default { get; } = new();
}
