namespace Ashes.Registry.Storage;

/// <summary>
/// Content-addressed store for compressed source blobs. Blobs are immutable and shared across any
/// versions with identical source trees. The filesystem implementation is the self-hostable MVP;
/// an object-store implementation swaps in behind this interface without touching endpoints.
/// </summary>
public interface IBlobStore
{
    Task<bool> ExistsAsync(string hash, CancellationToken ct);

    Task PutAsync(string hash, Stream compressed, CancellationToken ct);

    Task<Stream?> OpenAsync(string hash, CancellationToken ct);
}

/// <summary>
/// Packages and their versions. The EF Core / SQLite implementation is the MVP default; the same model
/// migrates to PostgreSQL by provider swap.
/// </summary>
public interface IMetadataStore
{
    Task<PackageInfo?> GetPackageAsync(string ns, CancellationToken ct);

    Task<IReadOnlyList<VersionInfo>> GetVersionsAsync(string ns, CancellationToken ct);

    Task<VersionInfo?> GetVersionAsync(string ns, string version, CancellationToken ct);

    Task UpsertPackageAsync(PackageInfo pkg, CancellationToken ct);

    /// <summary>Append a version. Throws <see cref="VersionExistsException"/> if the pair already exists
    /// with a different hash; a re-add with the same hash is an idempotent no-op.</summary>
    Task AddVersionAsync(VersionInfo v, CancellationToken ct);

    Task SetYankedAsync(string ns, string version, bool yanked, CancellationToken ct);
}

/// <summary>List + search over the package set (§7). Lexical, name-first ranking; no semantic search.</summary>
public interface ISearchIndex
{
    /// <summary>Bring the index up to date for a package. A no-op when search reads live metadata.</summary>
    Task IndexAsync(PackageInfo pkg, CancellationToken ct);

    Task<ResultPage> SearchAsync(string query, int limit, string? cursor, CancellationToken ct);

    Task<ResultPage> ListAsync(SortOrder sort, int limit, string? cursor, CancellationToken ct);
}

/// <summary>Raised when a publish would overwrite an existing (namespace, version) with a different hash.</summary>
public sealed class VersionExistsException(string ns, string version)
    : Exception($"Version {version} of '{ns}' already exists with a different hash.")
{
    public string Namespace { get; } = ns;

    public string Version { get; } = version;
}
