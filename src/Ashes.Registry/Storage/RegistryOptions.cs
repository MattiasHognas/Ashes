namespace Ashes.Registry.Storage;

/// <summary>Deployment configuration bound from the <c>Registry</c> configuration section.</summary>
public sealed class RegistryOptions
{
    /// <summary>Root directory holding <c>blobs/</c> and <c>registry.db</c> (the <c>--data</c> dir).</summary>
    public string DataDir { get; set; } = "data";

    /// <summary>Human-readable name reported by <c>GET /api/v1/index</c>.</summary>
    public string Name { get; set; } = "Ashes registry";

    public RegistryLimits Limits { get; init; } = new();
}

/// <summary>
/// Publish-time size and abuse limits. Reported by the index endpoint so a client
/// can discover a registry's effective caps; enforced by the publish pipeline (a later step).
/// </summary>
public sealed class RegistryLimits
{
    public long MaxFileBytes { get; set; } = 1L * 1024 * 1024;          // 1 MiB per file

    public long MaxTotalBytes { get; set; } = 10L * 1024 * 1024;        // 10 MiB uncompressed

    public int MaxFileCount { get; set; } = 10_000;
}
