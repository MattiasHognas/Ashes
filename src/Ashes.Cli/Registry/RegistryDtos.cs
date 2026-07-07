namespace Ashes.Cli.Registry;

// Wire DTOs for the registry API responses (camelCase via web defaults).
internal sealed record TokenResponseDto(string Account, string Token);

internal sealed record ErrorEnvelopeDto(ApiErrorDto? Error);

internal sealed record ApiErrorDto(string Code, string Message);

internal sealed record SearchResponseDto(IReadOnlyList<SearchResultDto> Results, string? NextCursor);

internal sealed record SearchResultDto(string Namespace, string Description, string? Latest, long Downloads, double Score);

internal sealed record PackageResponseDto(
    string Namespace,
    string Description,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Owners,
    IReadOnlyList<VersionDto> Versions);

internal sealed record VersionDto(
    string Version,
    string Hash,
    bool Yanked,
    IReadOnlyList<DependencyDto> Dependencies,
    IReadOnlyList<string> Capabilities,
    long Size,
    DateTimeOffset PublishedAt);

internal sealed record DependencyDto(string Namespace, string Req);
