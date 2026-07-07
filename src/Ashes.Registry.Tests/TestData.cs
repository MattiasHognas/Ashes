using Ashes.Registry.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ashes.Registry.Tests;

/// <summary>Shared builders for domain records and a disposable on-disk store, keeping tests terse.</summary>
internal static class TestData
{
    public static PackageInfo Package(
        string ns,
        string description = "",
        IReadOnlyList<string>? keywords = null,
        long downloads = 0) =>
        new(ns, description, keywords ?? [], downloads, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    public static VersionInfo Version(
        string ns,
        string version,
        string hash,
        bool yanked = false,
        long size = 0,
        IReadOnlyList<Dependency>? dependencies = null,
        IReadOnlyList<string>? capabilities = null) =>
        new(ns, version, hash, dependencies ?? [], capabilities ?? [], yanked, size, DateTimeOffset.UnixEpoch);

    /// <summary>A fresh temp-dir SQLite context + stores; disposing the handle deletes the temp dir.</summary>
    public static StoreHandle NewStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ashes-registry-tests", Guid.NewGuid().ToString("N"));
        return NewStoreAt(dir, ownsDir: true);
    }

    /// <summary>Open a store at a caller-owned directory (for reopen/persistence tests); the handle leaves
    /// the directory in place on dispose so the caller controls its lifetime.</summary>
    public static StoreHandle NewStoreAt(string dir) => NewStoreAt(dir, ownsDir: false);

    private static StoreHandle NewStoreAt(string dir, bool ownsDir)
    {
        Directory.CreateDirectory(dir);
        var options = new DbContextOptionsBuilder<RegistryDbContext>()
            .UseSqlite($"Data Source={Path.Combine(dir, "registry.db")}")
            .Options;
        var db = new RegistryDbContext(options);
        db.Database.EnsureCreated();
        return new StoreHandle(dir, db, ownsDir);
    }

    /// <summary>Seed a package plus one version and its source blob through the running app's stores;
    /// any named owners are created as accounts and linked.</summary>
    public static async Task SeedAsync(
        RegistryAppFactory factory,
        PackageInfo package,
        VersionInfo version,
        byte[] source,
        IReadOnlyList<string>? owners = null)
    {
        using var scope = factory.Services.CreateScope();
        var meta = scope.ServiceProvider.GetRequiredService<IMetadataStore>();
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();

        await meta.UpsertPackageAsync(package, CancellationToken.None);
        foreach (var owner in owners ?? [])
        {
            var account = await accounts.GetByNameAsync(owner, CancellationToken.None)
                ?? await accounts.CreateAccountAsync(owner, CancellationToken.None);
            await meta.AddOwnerAsync(package.Namespace, account.Id, CancellationToken.None);
        }

        await meta.AddVersionAsync(version, CancellationToken.None);
        using var ms = new MemoryStream(source);
        await blobs.PutAsync(version.Hash, ms, CancellationToken.None);
    }
}

internal sealed class StoreHandle(string dir, RegistryDbContext db, bool ownsDir) : IDisposable
{
    public RegistryDbContext Db { get; } = db;

    public EfMetadataStore Metadata { get; } = new(db);

    public EfSearchIndex Search { get; } = new(db);

    public void Dispose()
    {
        Db.Dispose();
        if (ownsDir && Directory.Exists(dir))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
