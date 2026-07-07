using Ashes.Registry.Publish;
using Ashes.Registry.Storage;
using Imposter.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Ashes.Registry.Tests;

/// <summary>The publish pipeline, one test per stage outcome (REGISTRY_API §4, §8). Real temp-SQLite
/// stores drive most stages; the namespace-lint seam is forced with an Imposter mock.</summary>
public sealed class PublishPipelineTests
{
    private static readonly Account Alice = new("acct-a", "alice", DateTimeOffset.UnixEpoch);
    private static readonly Account Bob = new("acct-b", "bob", DateTimeOffset.UnixEpoch);

    private static readonly (string Path, byte[] Bytes)[] JsonSource =
        [("src/Json.ash", "module Json"u8.ToArray())];

    [Test]
    public async Task Publishing_a_new_namespace_claims_it_and_stores_the_version()
    {
        using var store = TestData.NewStore();
        var blobs = new InMemoryBlobStore();
        var pipeline = Pipeline(store, blobs);

        var result = await pipeline.RunAsync(Request(Alice, "Json", "1.0.0"), CancellationToken.None);

        result.Succeeded.ShouldBeTrue();
        result.Version!.Hash.ShouldBe(ExpectedHash(JsonSource));
        blobs.Count.ShouldBe(1);
        (await store.Metadata.IsOwnerAsync("Json", Alice.Id, CancellationToken.None)).ShouldBeTrue();
        (await store.Metadata.GetVersionAsync("Json", "1.0.0", CancellationToken.None)).ShouldNotBeNull();
    }

    [Test]
    public async Task Publishing_a_namespace_owned_by_another_account_is_rejected()
    {
        using var store = TestData.NewStore();
        await store.Metadata.UpsertPackageAsync(TestData.Package("Json"), CancellationToken.None);
        await store.Metadata.AddOwnerAsync("Json", Bob.Id, CancellationToken.None);

        var result = await Pipeline(store, new InMemoryBlobStore())
            .RunAsync(Request(Alice, "Json", "1.0.0"), CancellationToken.None);

        result.Error!.Code.ShouldBe("namespace_owned_by_another");
    }

    [Test]
    public async Task Republishing_a_version_with_different_content_is_rejected()
    {
        using var store = TestData.NewStore();
        await store.Metadata.UpsertPackageAsync(TestData.Package("Json"), CancellationToken.None);
        await store.Metadata.AddOwnerAsync("Json", Alice.Id, CancellationToken.None);
        await store.Metadata.AddVersionAsync(TestData.Version("Json", "1.0.0", "ash1:seeded"), CancellationToken.None);

        var result = await Pipeline(store, new InMemoryBlobStore())
            .RunAsync(Request(Alice, "Json", "1.0.0"), CancellationToken.None);

        result.Error!.Code.ShouldBe("version_exists");
    }

    [Test]
    public async Task Republishing_identical_content_is_idempotent()
    {
        using var store = TestData.NewStore();
        var pipeline = Pipeline(store, new InMemoryBlobStore());

        (await pipeline.RunAsync(Request(Alice, "Json", "1.0.0"), CancellationToken.None)).Succeeded.ShouldBeTrue();
        var again = await pipeline.RunAsync(Request(Alice, "Json", "1.0.0"), CancellationToken.None);

        again.Succeeded.ShouldBeTrue();
        (await store.Metadata.GetVersionsAsync("Json", CancellationToken.None)).Count.ShouldBe(1);
    }

    [Test]
    public async Task A_non_semver_version_is_rejected()
    {
        using var store = TestData.NewStore();

        var result = await Pipeline(store, new InMemoryBlobStore())
            .RunAsync(Request(Alice, "Json", "not-semver"), CancellationToken.None);

        result.Error!.Code.ShouldBe("invalid_version");
    }

    [Test]
    public async Task A_declared_hash_that_does_not_match_is_rejected()
    {
        using var store = TestData.NewStore();
        var meta = new PublishMetadata("Json", "1.0.0", "ash1:wrong", "", [], []);

        var result = await Pipeline(store, new InMemoryBlobStore())
            .RunAsync(new PublishRequest(Alice, meta, TestArchives.Tarball(JsonSource)), CancellationToken.None);

        result.Error!.Code.ShouldBe("hash_mismatch");
    }

    [Test]
    public async Task Exceeding_the_total_size_limit_is_rejected()
    {
        using var store = TestData.NewStore();
        var tiny = new RegistryLimits { MaxTotalBytes = 3 };

        var result = await Pipeline(store, new InMemoryBlobStore(), tiny)
            .RunAsync(Request(Alice, "Json", "1.0.0"), CancellationToken.None);

        result.Error!.Code.ShouldBe("limit_exceeded");
    }

    [Test]
    public async Task A_namespace_lint_failure_aborts_the_publish()
    {
        using var store = TestData.NewStore();
        var imposter = new IManifestValidatorImposter();
        imposter.Validate(Arg<IReadOnlyList<SourceFile>>.Any(), Arg<string>.Any())
            .Returns(ValidationResult.Invalid("module outside namespace"));

        var result = await Pipeline(store, new InMemoryBlobStore(), validator: imposter.Instance())
            .RunAsync(Request(Alice, "Json", "1.0.0"), CancellationToken.None);

        result.Error!.Code.ShouldBe("namespace_lint");
    }

    private static PublishPipeline Pipeline(
        StoreHandle store, InMemoryBlobStore blobs, RegistryLimits? limits = null, IManifestValidator? validator = null) =>
        new(
            store.Metadata,
            blobs,
            validator ?? new StructuralManifestValidator(),
            new EmptyCapabilityExtractor(),
            Options.Create(new RegistryOptions { Limits = limits ?? new RegistryLimits() }));

    private static PublishRequest Request(Account account, string ns, string version) =>
        new(account, new PublishMetadata(ns, version, ExpectedHash(JsonSource), "desc", ["json"], []),
            TestArchives.Tarball(JsonSource));

    private static string ExpectedHash((string Path, byte[] Bytes)[] files) =>
        ContentHash.Compute(files.Select(f => new SourceFile(f.Path, f.Bytes)).ToList());
}
