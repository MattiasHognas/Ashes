using System.Text.Json;
using Ashes.Registry.Publish;
using Ashes.Registry.Storage;

namespace Ashes.Registry.Api;

// Request/response DTOs for the write surface (camelCase via web defaults).
public sealed record TokenRequest(string Name);

public sealed record TokenResponse(string Account, string Token);

public sealed record OwnerRequest(string Name);

public sealed record OwnersResponse(IReadOnlyList<string> Owners);

public sealed record PublishMetadataDto(string? Description, string[]? Keywords, DependencyDto[]? Dependencies, string? Hash);

public sealed record DependencyDto(string Namespace, string Req);

/// <summary>The authenticated write surface: token minting, publish, yank/unyank, owner management.</summary>
public static class WriteEndpoints
{
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapWriteEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var api = app.MapGroup("/api/v1");

        // MVP bootstrap: create-or-get an account by name and mint a token. Production deployments front
        // this with the server CLI / disable it; see REGISTRY_API §3.2.
        api.MapPost("/tokens", async (TokenRequest body, IAccountStore accounts, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name))
            {
                return RegistryResults.Error(StatusCodes.Status400BadRequest, ErrorCodes.Unauthorized, "An account name is required.");
            }

            var account = await accounts.GetByNameAsync(body.Name, ct)
                ?? await accounts.CreateAccountAsync(body.Name, ct);
            var (_, secret) = await accounts.CreateTokenAsync(account.Id, ct);
            return Results.Ok(new TokenResponse(account.Name, secret));
        });

        api.MapPut("/packages/{ns}/{version}", PublishAsync);

        api.MapPost("/packages/{ns}/{version}/yank",
            (string ns, string version, HttpContext ctx, IAccountStore accounts, IMetadataStore store, CancellationToken ct) =>
                SetYankedAsync(ns, version, yanked: true, ctx, accounts, store, ct));

        api.MapPost("/packages/{ns}/{version}/unyank",
            (string ns, string version, HttpContext ctx, IAccountStore accounts, IMetadataStore store, CancellationToken ct) =>
                SetYankedAsync(ns, version, yanked: false, ctx, accounts, store, ct));

        api.MapGet("/packages/{ns}/owners", async (
            string ns, HttpContext ctx, IAccountStore accounts, IMetadataStore store, CancellationToken ct) =>
        {
            var auth = await RequireOwnerAsync(ns, ctx, accounts, store, ct);
            if (auth.Error is not null)
            {
                return auth.Error;
            }

            return Results.Ok(new OwnersResponse(await store.GetOwnersAsync(ns, ct)));
        });

        api.MapPost("/packages/{ns}/owners", async (
            string ns, OwnerRequest body, HttpContext ctx, IAccountStore accounts, IMetadataStore store, CancellationToken ct) =>
        {
            var auth = await RequireOwnerAsync(ns, ctx, accounts, store, ct);
            if (auth.Error is not null)
            {
                return auth.Error;
            }

            var target = await accounts.GetByNameAsync(body.Name, ct);
            if (target is null)
            {
                return RegistryResults.NotFound($"No account named '{body.Name}'.");
            }

            await store.AddOwnerAsync(ns, target.Id, ct);
            return Results.Ok(new OwnersResponse(await store.GetOwnersAsync(ns, ct)));
        });

        api.MapDelete("/packages/{ns}/owners", async (
            string ns,
            [Microsoft.AspNetCore.Mvc.FromBody] OwnerRequest body,
            HttpContext ctx,
            IAccountStore accounts,
            IMetadataStore store,
            CancellationToken ct) =>
        {
            var auth = await RequireOwnerAsync(ns, ctx, accounts, store, ct);
            if (auth.Error is not null)
            {
                return auth.Error;
            }

            var target = await accounts.GetByNameAsync(body.Name, ct);
            if (target is not null)
            {
                await store.RemoveOwnerAsync(ns, target.Id, ct);
            }

            return Results.Ok(new OwnersResponse(await store.GetOwnersAsync(ns, ct)));
        });

        return app;
    }

    private static async Task<IResult> PublishAsync(
        string ns,
        string version,
        HttpRequest request,
        IAccountStore accounts,
        PublishPipeline pipeline,
        Microsoft.Extensions.Options.IOptions<RegistryOptions> options,
        CancellationToken ct)
    {
        var account = await AuthenticateAsync(request.HttpContext, accounts, ct);
        if (account is null)
        {
            return Unauthorized();
        }

        if (!request.HasFormContentType)
        {
            return RegistryResults.Error(StatusCodes.Status400BadRequest, ErrorCodes.LimitExceeded,
                "Publish must be multipart form data (metadata + source).");
        }

        var form = await request.ReadFormAsync(ct);
        var metaJson = form["metadata"].ToString();
        var file = form.Files["source"];
        if (string.IsNullOrEmpty(metaJson) || file is null)
        {
            return RegistryResults.Error(StatusCodes.Status400BadRequest, ErrorCodes.LimitExceeded,
                "Publish requires a 'metadata' field and a 'source' file.");
        }

        var dto = JsonSerializer.Deserialize<PublishMetadataDto>(metaJson, JsonWeb);
        if (dto is null || string.IsNullOrEmpty(dto.Hash))
        {
            return RegistryResults.Error(StatusCodes.Status400BadRequest, ErrorCodes.HashMismatch,
                "Publish metadata must include the declared content hash.");
        }

        var source = await ReadCappedAsync(file, options.Value.Limits.MaxTotalBytes, ct);
        if (source is null)
        {
            return RegistryResults.Error(StatusCodes.Status413PayloadTooLarge, ErrorCodes.LimitExceeded,
                "The uploaded archive exceeds the size limit.");
        }

        var metadata = new PublishMetadata(
            ns, version, dto.Hash, dto.Description ?? "",
            dto.Keywords ?? [],
            (dto.Dependencies ?? []).Select(d => new Dependency(d.Namespace, d.Req)).ToList());

        var result = await pipeline.RunAsync(new PublishRequest(account, metadata, source), ct);
        if (!result.Succeeded)
        {
            return ErrorResult(result.Error!);
        }

        return Results.Json(Responses.ToResponse(result.Version!), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> SetYankedAsync(
        string ns, string version, bool yanked, HttpContext ctx, IAccountStore accounts, IMetadataStore store, CancellationToken ct)
    {
        var auth = await RequireOwnerAsync(ns, ctx, accounts, store, ct);
        if (auth.Error is not null)
        {
            return auth.Error;
        }

        if (await store.GetVersionAsync(ns, version, ct) is null)
        {
            return RegistryResults.NotFound($"No version {version} of '{ns}'.");
        }

        await store.SetYankedAsync(ns, version, yanked, ct);
        return Results.Ok(new { yanked });
    }

    private static async Task<(Account? Account, IResult? Error)> RequireOwnerAsync(
        string ns, HttpContext ctx, IAccountStore accounts, IMetadataStore store, CancellationToken ct)
    {
        var account = await AuthenticateAsync(ctx, accounts, ct);
        if (account is null)
        {
            return (null, Unauthorized());
        }

        var package = await store.GetPackageAsync(ns, ct);
        if (package is null)
        {
            return (null, RegistryResults.NotFound($"No package named '{ns}'."));
        }

        if (!await store.IsOwnerAsync(ns, account.Id, ct))
        {
            return (null, RegistryResults.Error(StatusCodes.Status403Forbidden, ErrorCodes.NamespaceOwnedByAnother,
                $"Account '{account.Name}' does not own '{ns}'."));
        }

        return (account, null);
    }

    private static async Task<Account?> AuthenticateAsync(HttpContext ctx, IAccountStore accounts, CancellationToken ct)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        return await accounts.ResolveTokenAsync(header[prefix.Length..].Trim(), ct);
    }

    private static async Task<byte[]?> ReadCappedAsync(IFormFile file, long cap, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > cap)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }

        return buffer.ToArray();
    }

    private static IResult Unauthorized() =>
        RegistryResults.Error(StatusCodes.Status401Unauthorized, ErrorCodes.Unauthorized, "A valid bearer token is required.");

    private static IResult ErrorResult(PublishError error)
    {
        var status = error.Code switch
        {
            ErrorCodes.NamespaceOwnedByAnother => StatusCodes.Status403Forbidden,
            ErrorCodes.VersionExists => StatusCodes.Status409Conflict,
            ErrorCodes.LimitExceeded => StatusCodes.Status413PayloadTooLarge,
            _ => StatusCodes.Status400BadRequest,
        };
        return RegistryResults.Error(status, error.Code, error.Message);
    }
}
