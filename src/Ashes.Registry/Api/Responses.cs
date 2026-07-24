using Ashes.Registry.Storage;

namespace Ashes.Registry.Api;

// Response DTOs. Minimal API serializes with web defaults (camelCase), so these PascalCase members
// render as the documented lowerCamel fields.

/// <summary>Body of <c>GET /api/v1/index</c>: the registry's self-description a client reads before publishing.</summary>
/// <param name="Name">Human-readable registry name for display.</param>
/// <param name="ApiVersion">The API version this registry speaks (currently <c>v1</c>).</param>
/// <param name="Limits">The publish-time size and count caps this registry enforces.</param>
public sealed record IndexResponse(string Name, string ApiVersion, LimitsResponse Limits);

/// <summary>The effective publish limits advertised in <see cref="IndexResponse"/>.</summary>
/// <param name="MaxFileBytes">Maximum size, in bytes, of any single file in an upload.</param>
/// <param name="MaxTotalBytes">Maximum total uncompressed size, in bytes, of an upload's source tree.</param>
/// <param name="MaxFileCount">Maximum number of files an upload may contain.</param>
public sealed record LimitsResponse(long MaxFileBytes, long MaxTotalBytes, int MaxFileCount);

/// <summary>A dependency edge in a version's response, projected from <see cref="Storage.Dependency"/>.</summary>
/// <param name="Namespace">The depended-on package's namespace.</param>
/// <param name="Req">The SemVer requirement string constraining acceptable versions.</param>
public sealed record DependencyResponse(string Namespace, string Req);

/// <summary>One published version as returned by the version and publish endpoints.</summary>
/// <param name="Version">The SemVer version string.</param>
/// <param name="Hash">The canonical <c>ash1:</c> content hash of the source tree.</param>
/// <param name="Yanked">Whether the version has been yanked (still resolvable, not selected for new installs).</param>
/// <param name="Dependencies">The version's declared dependency edges.</param>
/// <param name="Capabilities">The public-API capability rows extracted at publish time.</param>
/// <param name="Size">Total uncompressed source size in bytes.</param>
/// <param name="PublishedAt">When the version was published.</param>
public sealed record VersionResponse(
    string Version,
    string Hash,
    bool Yanked,
    IReadOnlyList<DependencyResponse> Dependencies,
    IReadOnlyList<string> Capabilities,
    long Size,
    DateTimeOffset PublishedAt);

/// <summary>A package with all of its versions, as returned by <c>GET /api/v1/packages/{ns}</c>.</summary>
/// <param name="Namespace">The package's namespace (its unique name).</param>
/// <param name="Description">Free-text package description.</param>
/// <param name="Keywords">Discovery keywords.</param>
/// <param name="Owners">Names of the accounts that own the namespace.</param>
/// <param name="Versions">Every published version of the package.</param>
public sealed record PackageResponse(
    string Namespace,
    string Description,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Owners,
    IReadOnlyList<VersionResponse> Versions);

/// <summary>A single hit in a <c>GET /api/v1/search</c> result page.</summary>
/// <param name="Namespace">The matched package's namespace.</param>
/// <param name="Description">The package's description.</param>
/// <param name="Latest">The latest non-yanked version string, or null if none.</param>
/// <param name="Downloads">Cumulative download count.</param>
/// <param name="Score">The lexical relevance score for the query.</param>
public sealed record SearchResultResponse(
    string Namespace,
    string Description,
    string? Latest,
    long Downloads,
    double Score);

/// <summary>A page of search hits with an opaque continuation cursor.</summary>
/// <param name="Results">The hits on this page.</param>
/// <param name="NextCursor">Cursor to pass for the next page, or null when the listing is exhausted.</param>
public sealed record SearchResponse(IReadOnlyList<SearchResultResponse> Results, string? NextCursor);

/// <summary>A single entry in the <c>GET /api/v1/packages</c> browse listing.</summary>
/// <param name="Namespace">The package's namespace.</param>
/// <param name="Description">The package's description.</param>
/// <param name="Latest">The latest non-yanked version string, or null if none.</param>
/// <param name="Downloads">Cumulative download count.</param>
public sealed record BrowseItemResponse(string Namespace, string Description, string? Latest, long Downloads);

/// <summary>A page of browse entries with an opaque continuation cursor.</summary>
/// <param name="Packages">The entries on this page.</param>
/// <param name="NextCursor">Cursor to pass for the next page, or null when the listing is exhausted.</param>
public sealed record BrowseResponse(IReadOnlyList<BrowseItemResponse> Packages, string? NextCursor);

/// <summary>Domain-record → response-DTO projection.</summary>
internal static class Responses
{
    public static VersionResponse ToResponse(VersionInfo v) => new(
        v.Version, v.Hash, v.Yanked,
        v.Dependencies.Select(d => new DependencyResponse(d.Namespace, d.Req)).ToList(),
        v.Capabilities, v.Size, v.PublishedAt);

    public static PackageResponse ToResponse(
        PackageInfo p, IReadOnlyList<VersionInfo> versions, IReadOnlyList<string> owners) => new(
        p.Namespace, p.Description, p.Keywords, owners, versions.Select(ToResponse).ToList());

    public static SearchResponse ToSearch(ResultPage page) => new(
        page.Results.Select(r => new SearchResultResponse(r.Namespace, r.Description, r.Latest, r.Downloads, r.Score)).ToList(),
        page.NextCursor);

    public static BrowseResponse ToBrowse(ResultPage page) => new(
        page.Results.Select(r => new BrowseItemResponse(r.Namespace, r.Description, r.Latest, r.Downloads)).ToList(),
        page.NextCursor);
}
