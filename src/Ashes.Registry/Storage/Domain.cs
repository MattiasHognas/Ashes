namespace Ashes.Registry.Storage;

/// <summary>A dependency edge as recorded in a published version's metadata.</summary>
public sealed record Dependency(string Namespace, string Req);

/// <summary>Package-level metadata (one per namespace); the searchable/browsable unit. Owners are held
/// relationally and fetched via <see cref="IMetadataStore.GetOwnersAsync"/>, not carried here.</summary>
public sealed record PackageInfo(
    string Namespace,
    string Description,
    IReadOnlyList<string> Keywords,
    long Downloads,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>One immutable published version of a package.</summary>
public sealed record VersionInfo(
    string Namespace,
    string Version,
    string Hash,
    IReadOnlyList<Dependency> Dependencies,
    IReadOnlyList<string> Capabilities,
    bool Yanked,
    long Size,
    DateTimeOffset PublishedAt);

/// <summary>A single hit in a search or browse listing.</summary>
public sealed record PackageSummary(
    string Namespace,
    string Description,
    string? Latest,
    long Downloads,
    double Score);

/// <summary>A page of search/browse results with an opaque continuation cursor.</summary>
public sealed record ResultPage(IReadOnlyList<PackageSummary> Results, string? NextCursor);

/// <summary>Ordering for the browse endpoint.</summary>
public enum SortOrder
{
    /// <summary>Most recently updated packages first.</summary>
    Recent,

    /// <summary>Alphabetical by namespace.</summary>
    Name,

    /// <summary>Most downloaded packages first.</summary>
    Downloads,
}
