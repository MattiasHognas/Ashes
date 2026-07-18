using Ashes.Registry.Storage;
using Microsoft.Extensions.Options;

namespace Ashes.Registry.Api;

/// <summary>The unauthenticated, cacheable read surface.</summary>
public static class ReadEndpoints
{
    public static IEndpointRouteBuilder MapReadEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

        var api = app.MapGroup("/api/v1");

        MapIndexAndListEndpoints(api);
        MapPackageEndpoints(api);

        return app;
    }

    private static void MapIndexAndListEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/index", (IOptions<RegistryOptions> options) =>
        {
            var o = options.Value;
            return Results.Ok(new IndexResponse(
                o.Name,
                "v1",
                new LimitsResponse(o.Limits.MaxFileBytes, o.Limits.MaxTotalBytes, o.Limits.MaxFileCount)));
        });

        api.MapGet("/packages", async (
            string? sort, int? limit, string? cursor, ISearchIndex search, CancellationToken ct) =>
        {
            var order = ParseSort(sort);
            var page = await search.ListAsync(order, limit ?? 20, cursor, ct);
            return Results.Ok(Responses.ToBrowse(page));
        });

        api.MapGet("/search", async (
            string? q, int? limit, string? cursor, ISearchIndex search, CancellationToken ct) =>
        {
            var page = await search.SearchAsync(q ?? "", limit ?? 20, cursor, ct);
            return Results.Ok(Responses.ToSearch(page));
        });
    }

    private static void MapPackageEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/packages/{ns}", async (string ns, IMetadataStore store, CancellationToken ct) =>
        {
            var pkg = await store.GetPackageAsync(ns, ct);
            if (pkg is null)
            {
                return RegistryResults.NotFound($"No package named '{ns}'.");
            }

            var versions = await store.GetVersionsAsync(ns, ct);
            var owners = await store.GetOwnersAsync(ns, ct);
            return Results.Ok(Responses.ToResponse(pkg, versions, owners));
        });

        api.MapGet("/packages/{ns}/{version}", async (
            string ns, string version, IMetadataStore store, CancellationToken ct) =>
        {
            var v = await store.GetVersionAsync(ns, version, ct);
            return v is null
                ? RegistryResults.NotFound($"No version {version} of '{ns}'.")
                : Results.Ok(Responses.ToResponse(v));
        });

        api.MapGet("/packages/{ns}/{version}/source", async (
            string ns, string version, IMetadataStore store, IBlobStore blobs, CancellationToken ct) =>
        {
            var v = await store.GetVersionAsync(ns, version, ct);
            if (v is null)
            {
                return RegistryResults.NotFound($"No version {version} of '{ns}'.");
            }

            var stream = await blobs.OpenAsync(v.Hash, ct);
            if (stream is null)
            {
                return RegistryResults.NotFound($"Source blob for {ns}@{version} is missing.");
            }

            return Results.Stream(stream, "application/gzip", $"{ns}-{version}.tar.gz");
        });
    }

    private static SortOrder ParseSort(string? sort) => sort?.ToLowerInvariant() switch
    {
        "name" => SortOrder.Name,
        "downloads" => SortOrder.Downloads,
        _ => SortOrder.Recent,
    };
}
