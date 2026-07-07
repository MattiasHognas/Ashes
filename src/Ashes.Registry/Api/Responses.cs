using Ashes.Registry.Storage;

namespace Ashes.Registry.Api;

// Response DTOs. Minimal API serializes with web defaults (camelCase), so these PascalCase members
// render as the lowerCamel fields documented in REGISTRY_API §3.

public sealed record IndexResponse(string Name, string ApiVersion, LimitsResponse Limits);

public sealed record LimitsResponse(long MaxFileBytes, long MaxTotalBytes, int MaxFileCount);

public sealed record DependencyResponse(string Namespace, string Req);

public sealed record VersionResponse(
    string Version,
    string Hash,
    bool Yanked,
    IReadOnlyList<DependencyResponse> Dependencies,
    IReadOnlyList<string> Capabilities,
    long Size,
    DateTimeOffset PublishedAt);

public sealed record PackageResponse(
    string Namespace,
    string Description,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Owners,
    IReadOnlyList<VersionResponse> Versions);

public sealed record SearchResultResponse(
    string Namespace,
    string Description,
    string? Latest,
    long Downloads,
    double Score);

public sealed record SearchResponse(IReadOnlyList<SearchResultResponse> Results, string? NextCursor);

public sealed record BrowseItemResponse(string Namespace, string Description, string? Latest, long Downloads);

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
