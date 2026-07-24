namespace Ashes.Registry.Storage;

/// <summary>
/// Content-addressed store for compressed source blobs. Blobs are immutable and shared across any
/// versions with identical source trees. The filesystem implementation is the self-hostable MVP;
/// an object-store implementation swaps in behind this interface without touching endpoints.
/// </summary>
public interface IBlobStore
{
    /// <summary>Whether a blob with the given content <paramref name="hash"/> is already stored.</summary>
    Task<bool> ExistsAsync(string hash, CancellationToken ct);

    /// <summary>Stores the <paramref name="compressed"/> blob under <paramref name="hash"/>; a re-put of
    /// identical content is idempotent.</summary>
    Task PutAsync(string hash, Stream compressed, CancellationToken ct);

    /// <summary>Opens the blob stored under <paramref name="hash"/> for reading, or null if it is absent.</summary>
    Task<Stream?> OpenAsync(string hash, CancellationToken ct);
}

/// <summary>
/// Packages and their versions. The EF Core / SQLite implementation is the MVP default; the same model
/// migrates to PostgreSQL by provider swap.
/// </summary>
public interface IMetadataStore
{
    /// <summary>The package for the namespace <paramref name="ns"/>, or null if it is not registered.</summary>
    Task<PackageInfo?> GetPackageAsync(string ns, CancellationToken ct);

    /// <summary>Every published version of <paramref name="ns"/> (empty if the package is unknown).</summary>
    Task<IReadOnlyList<VersionInfo>> GetVersionsAsync(string ns, CancellationToken ct);

    /// <summary>One version of <paramref name="ns"/> by its <paramref name="version"/> string, or null if absent.</summary>
    Task<VersionInfo?> GetVersionAsync(string ns, string version, CancellationToken ct);

    /// <summary>Inserts or updates the package-level metadata for a namespace.</summary>
    Task UpsertPackageAsync(PackageInfo pkg, CancellationToken ct);

    /// <summary>Append a version. Throws <see cref="VersionExistsException"/> if the pair already exists
    /// with a different hash; a re-add with the same hash is an idempotent no-op.</summary>
    Task AddVersionAsync(VersionInfo v, CancellationToken ct);

    /// <summary>Sets the yanked flag on one version of <paramref name="ns"/>.</summary>
    Task SetYankedAsync(string ns, string version, bool yanked, CancellationToken ct);

    /// <summary>Account names owning the namespace (empty if unclaimed).</summary>
    Task<IReadOnlyList<string>> GetOwnersAsync(string ns, CancellationToken ct);

    /// <summary>Whether the account <paramref name="accountId"/> owns the namespace <paramref name="ns"/>.</summary>
    Task<bool> IsOwnerAsync(string ns, string accountId, CancellationToken ct);

    /// <summary>Grants ownership of <paramref name="ns"/> to <paramref name="accountId"/>.</summary>
    Task AddOwnerAsync(string ns, string accountId, CancellationToken ct);

    /// <summary>Revokes ownership of <paramref name="ns"/> from <paramref name="accountId"/>.</summary>
    Task RemoveOwnerAsync(string ns, string accountId, CancellationToken ct);
}

/// <summary>An identity that can own namespaces and hold API tokens.</summary>
public sealed record Account(string Id, string Name, DateTimeOffset CreatedAt);

/// <summary>Accounts and their API tokens. Token secrets are stored only as hashes; the plaintext is
/// returned once, at mint time, and never again.</summary>
public interface IAccountStore
{
    /// <summary>Creates a new account with the given <paramref name="name"/> and returns it.</summary>
    Task<Account> CreateAccountAsync(string name, CancellationToken ct);

    /// <summary>The account with the given <paramref name="name"/>, or null if none exists.</summary>
    Task<Account?> GetByNameAsync(string name, CancellationToken ct);

    /// <summary>Mint a token for an account, returning the one-time plaintext secret alongside it.</summary>
    Task<(Account Account, string Secret)> CreateTokenAsync(string accountId, CancellationToken ct);

    /// <summary>Resolve a presented bearer secret to its account, or null if unknown.</summary>
    Task<Account?> ResolveTokenAsync(string presentedSecret, CancellationToken ct);
}

/// <summary>List + search over the package set. Lexical, name-first ranking; no semantic search.</summary>
public interface ISearchIndex
{
    /// <summary>Bring the index up to date for a package. A no-op when search reads live metadata.</summary>
    Task IndexAsync(PackageInfo pkg, CancellationToken ct);

    /// <summary>Returns up to <paramref name="limit"/> hits matching <paramref name="query"/>, continuing
    /// from <paramref name="cursor"/> when paging.</summary>
    Task<ResultPage> SearchAsync(string query, int limit, string? cursor, CancellationToken ct);

    /// <summary>Returns up to <paramref name="limit"/> packages in <paramref name="sort"/> order, continuing
    /// from <paramref name="cursor"/> when paging.</summary>
    Task<ResultPage> ListAsync(SortOrder sort, int limit, string? cursor, CancellationToken ct);
}

/// <summary>Raised when a publish would overwrite an existing (namespace, version) with a different hash.</summary>
public sealed class VersionExistsException(string ns, string version)
    : Exception($"Version {version} of '{ns}' already exists with a different hash.")
{
    /// <summary>The namespace whose version conflicted.</summary>
    public string Namespace { get; } = ns;

    /// <summary>The version string that already existed with different content.</summary>
    public string Version { get; } = version;
}
