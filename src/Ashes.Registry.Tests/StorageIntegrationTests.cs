using Ashes.Registry.Storage;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Ashes.Registry.Tests;

/// <summary>The real stores against a temp <c>--data</c> dir (REGISTRY_API §8, storage layer).</summary>
public sealed class StorageIntegrationTests
{
    [Test]
    public async Task BlobStore_roundtrips_and_dedups()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ashes-registry-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var blobs = new FileSystemBlobStore(Options.Create(new RegistryOptions { DataDir = dir }));
            var payload = new byte[] { 1, 2, 3, 4, 5 };
            const string hash = "ash1:deadbeef";

            (await blobs.ExistsAsync(hash, CancellationToken.None)).ShouldBeFalse();

            await blobs.PutAsync(hash, new MemoryStream(payload), CancellationToken.None);
            await blobs.PutAsync(hash, new MemoryStream(payload), CancellationToken.None); // idempotent

            (await blobs.ExistsAsync(hash, CancellationToken.None)).ShouldBeTrue();
            await using var read = await blobs.OpenAsync(hash, CancellationToken.None);
            read.ShouldNotBeNull();
            using var buffer = new MemoryStream();
            await read.CopyToAsync(buffer);
            buffer.ToArray().ShouldBe(payload);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Test]
    public async Task BlobStore_rejects_non_hex_hash()
    {
        var blobs = new FileSystemBlobStore(Options.Create(new RegistryOptions { DataDir = Path.GetTempPath() }));
        await Should.ThrowAsync<ArgumentException>(async () =>
            await blobs.PutAsync("ash1:not-hex!", new MemoryStream([0]), CancellationToken.None));
    }

    [Test]
    public async Task MetadataStore_adds_and_reads_back_a_version()
    {
        using var store = TestData.NewStore();
        await store.Metadata.UpsertPackageAsync(TestData.Package("Json", "A JSON parser."), CancellationToken.None);
        await store.Metadata.AddVersionAsync(TestData.Version("Json", "1.2.0", "ash1:aaa", size: 42), CancellationToken.None);

        var pkg = await store.Metadata.GetPackageAsync("Json", CancellationToken.None);
        pkg.ShouldNotBeNull();
        pkg.Description.ShouldBe("A JSON parser.");

        var v = await store.Metadata.GetVersionAsync("Json", "1.2.0", CancellationToken.None);
        v.ShouldNotBeNull();
        v.Hash.ShouldBe("ash1:aaa");
        v.Size.ShouldBe(42);
    }

    [Test]
    public async Task MetadataStore_rejects_overwriting_a_version_with_a_different_hash()
    {
        using var store = TestData.NewStore();
        await store.Metadata.UpsertPackageAsync(TestData.Package("Json"), CancellationToken.None);
        await store.Metadata.AddVersionAsync(TestData.Version("Json", "1.0.0", "ash1:aaa"), CancellationToken.None);

        await Should.ThrowAsync<VersionExistsException>(async () =>
            await store.Metadata.AddVersionAsync(TestData.Version("Json", "1.0.0", "ash1:bbb"), CancellationToken.None));
    }

    [Test]
    public async Task MetadataStore_reAdding_identical_version_is_idempotent()
    {
        using var store = TestData.NewStore();
        await store.Metadata.UpsertPackageAsync(TestData.Package("Json"), CancellationToken.None);
        await store.Metadata.AddVersionAsync(TestData.Version("Json", "1.0.0", "ash1:aaa"), CancellationToken.None);
        await store.Metadata.AddVersionAsync(TestData.Version("Json", "1.0.0", "ash1:aaa"), CancellationToken.None);

        var versions = await store.Metadata.GetVersionsAsync("Json", CancellationToken.None);
        versions.Count.ShouldBe(1);
    }

    [Test]
    public async Task MetadataStore_persists_across_a_reopen()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ashes-registry-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using (var store = TestData.NewStoreAt(dir))
            {
                await store.Metadata.UpsertPackageAsync(TestData.Package("Json"), CancellationToken.None);
                await store.Metadata.AddVersionAsync(TestData.Version("Json", "1.0.0", "ash1:aaa"), CancellationToken.None);
            }

            using var reopened = TestData.NewStoreAt(dir);
            var v = await reopened.Metadata.GetVersionAsync("Json", "1.0.0", CancellationToken.None);
            v.ShouldNotBeNull();
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Test]
    public async Task Search_ranks_name_first_and_computes_latest_excluding_yanked()
    {
        using var store = TestData.NewStore();
        await store.Metadata.UpsertPackageAsync(TestData.Package("Json", "JSON tools", downloads: 5), CancellationToken.None);
        await store.Metadata.AddVersionAsync(TestData.Version("Json", "1.2.0", "ash1:a"), CancellationToken.None);
        await store.Metadata.AddVersionAsync(TestData.Version("Json", "1.10.0", "ash1:b"), CancellationToken.None);
        await store.Metadata.AddVersionAsync(TestData.Version("Json", "2.0.0", "ash1:c", yanked: true), CancellationToken.None);
        await store.Metadata.UpsertPackageAsync(TestData.Package("JsonParser", "parser"), CancellationToken.None);
        await store.Metadata.UpsertPackageAsync(TestData.Package("Http", "http client"), CancellationToken.None);

        var page = await store.Search.SearchAsync("json", 20, cursor: null, CancellationToken.None);

        page.Results.Select(r => r.Namespace).ShouldBe(["Json", "JsonParser"]); // Http excluded; exact before prefix
        var json = page.Results[0];
        json.Latest.ShouldBe("1.10.0"); // numeric ordering (1.10 > 1.2) and the yanked 2.0.0 excluded
        json.Score.ShouldBe(1.0);
    }

    [Test]
    public async Task Browse_paginates_with_a_cursor()
    {
        using var store = TestData.NewStore();
        for (var i = 0; i < 3; i++)
        {
            await store.Metadata.UpsertPackageAsync(TestData.Package($"Pkg{i}"), CancellationToken.None);
        }

        var first = await store.Search.ListAsync(SortOrder.Name, limit: 2, cursor: null, CancellationToken.None);
        first.Results.Count.ShouldBe(2);
        first.NextCursor.ShouldNotBeNull();

        var second = await store.Search.ListAsync(SortOrder.Name, limit: 2, first.NextCursor, CancellationToken.None);
        second.Results.Count.ShouldBe(1);
        second.NextCursor.ShouldBeNull();
    }
}
