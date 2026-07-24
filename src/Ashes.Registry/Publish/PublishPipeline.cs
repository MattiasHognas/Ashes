using Ashes.Registry.Api;
using Ashes.Registry.Storage;
using Microsoft.Extensions.Options;

namespace Ashes.Registry.Publish;

/// <summary>
/// The ordered publish pipeline. Each stage's failure aborts with a typed error and
/// writes nothing; only a fully-validated upload reaches the atomic store stage. Authentication (stage 1)
/// happens at the endpoint, which hands the resolved <see cref="Account"/> to <see cref="RunAsync"/>.
/// </summary>
public sealed class PublishPipeline(
    IMetadataStore metadata,
    IBlobStore blobs,
    IManifestValidator validator,
    ICapabilityExtractor capabilities,
    IOptions<RegistryOptions> options)
{
    /// <summary>Runs the ordered publish stages for <paramref name="request"/> — unpack and limit-check,
    /// namespace authorization, SemVer and immutability checks, namespace lint, hash verification,
    /// capability extraction, and the atomic blob/package/version store — returning the stored version or a
    /// typed failure. An identical re-publish of an existing version is an idempotent success.</summary>
    public async Task<PublishResult> RunAsync(PublishRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var meta = request.Metadata;

        // 3. Unpack + enforce limits.
        var (files, limitError) = await SourceArchive.ExtractAsync(
            new MemoryStream(request.Source), options.Value.Limits, ct);
        if (limitError is not null)
        {
            return new PublishResult(null, limitError);
        }

        var sources = files!;

        // 4. Authorize the namespace: first publish claims it; later versions require ownership.
        var existingPackage = await metadata.GetPackageAsync(meta.Namespace, ct);
        if (existingPackage is not null &&
            !await metadata.IsOwnerAsync(meta.Namespace, request.Account.Id, ct))
        {
            return PublishResult.Fail(ErrorCodes.NamespaceOwnedByAnother,
                $"Namespace '{meta.Namespace}' is owned by another account.");
        }

        // 5. Validate SemVer + immutability (against the authoritative computed hash).
        if (!SemVer.TryParse(meta.Version, out _))
        {
            return PublishResult.Fail(ErrorCodes.InvalidVersion, $"'{meta.Version}' is not a valid SemVer version.");
        }

        var computedHash = ContentHash.Compute(sources);
        var existingVersion = await metadata.GetVersionAsync(meta.Namespace, meta.Version, ct);
        if (existingVersion is not null)
        {
            return string.Equals(existingVersion.Hash, computedHash, StringComparison.Ordinal)
                ? PublishResult.Ok(existingVersion) // idempotent re-publish of identical content
                : PublishResult.Fail(ErrorCodes.VersionExists,
                    $"Version {meta.Version} of '{meta.Namespace}' already exists.");
        }

        return await ValidateAndStoreAsync(request, sources, existingPackage, computedHash, ct);
    }

    private async Task<PublishResult> ValidateAndStoreAsync(
        PublishRequest request,
        IReadOnlyList<SourceFile> sources,
        PackageInfo? existingPackage,
        string computedHash,
        CancellationToken ct)
    {
        var meta = request.Metadata;

        // 6. Namespace lint.
        var lint = validator.Validate(sources, meta.Namespace);
        if (!lint.Ok)
        {
            return PublishResult.Fail(ErrorCodes.NamespaceLint, lint.Message!);
        }

        // 7. Verify the client's declared hash against the server-computed one.
        if (!string.Equals(meta.Hash, computedHash, StringComparison.Ordinal))
        {
            return PublishResult.Fail(ErrorCodes.HashMismatch,
                $"Declared hash '{meta.Hash}' does not match the computed '{computedHash}'.");
        }

        // 8. Extract the public capability rows.
        var caps = capabilities.PublicCapabilities(sources, meta.Namespace);

        // 9. Store atomically: blob, then package (owner claim), then the version (FK-ordered).
        var size = sources.Sum(f => (long)f.Bytes.Length);
        var now = DateTimeOffset.UtcNow;

        await blobs.PutAsync(computedHash, new MemoryStream(request.Source), ct);
        await metadata.UpsertPackageAsync(
            new PackageInfo(meta.Namespace, meta.Description, meta.Keywords, existingPackage?.Downloads ?? 0,
                existingPackage?.CreatedAt ?? now, now), ct);
        if (existingPackage is null)
        {
            await metadata.AddOwnerAsync(meta.Namespace, request.Account.Id, ct);
        }

        var version = new VersionInfo(
            meta.Namespace, meta.Version, computedHash, meta.Dependencies, caps, Yanked: false, size, now);
        await metadata.AddVersionAsync(version, ct);

        // 10. Done.
        return PublishResult.Ok(version);
    }
}
