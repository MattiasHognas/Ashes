namespace Ashes.Registry.Storage;

/// <summary>Deployment configuration bound from the <c>Registry</c> configuration section.</summary>
public sealed class RegistryOptions
{
    /// <summary>Root directory holding <c>blobs/</c> and <c>registry.db</c> (the <c>--data</c> dir).</summary>
    public string DataDir { get; set; } = "data";

    /// <summary>Human-readable name reported by <c>GET /api/v1/index</c>.</summary>
    public string Name { get; set; } = "Ashes registry";

    /// <summary>
    /// Whether <c>POST /api/v1/tokens</c> may create a new account and mint a token for anyone. Convenient
    /// for a self-hosted bootstrap; set <c>false</c> on a public/production instance so tokens are
    /// provisioned out of band instead of open self-registration.
    /// </summary>
    public bool AllowOpenRegistration { get; set; } = true;

    /// <summary>The publish-time size and abuse limits this registry enforces and advertises.</summary>
    public RegistryLimits Limits { get; init; } = new();
}

/// <summary>
/// Publish-time size and abuse limits. Reported by the index endpoint so a client can discover a
/// registry's effective caps, and enforced by the publish pipeline when unpacking an upload.
/// </summary>
public sealed class RegistryLimits
{
    /// <summary>Maximum size, in bytes, of any single file in an upload.</summary>
    public long MaxFileBytes { get; set; } = 1L * 1024 * 1024;          // 1 MiB per file

    /// <summary>Maximum total uncompressed size, in bytes, of an upload's source tree.</summary>
    public long MaxTotalBytes { get; set; } = 10L * 1024 * 1024;        // 10 MiB uncompressed

    /// <summary>Maximum number of files an upload may contain.</summary>
    public int MaxFileCount { get; set; } = 10_000;
}
