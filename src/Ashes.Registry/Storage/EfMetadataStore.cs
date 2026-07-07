using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Ashes.Registry.Storage;

/// <summary>EF Core / SQLite implementation of <see cref="IMetadataStore"/>.</summary>
internal sealed class EfMetadataStore(RegistryDbContext db) : IMetadataStore
{
    public async Task<PackageInfo?> GetPackageAsync(string ns, CancellationToken ct)
    {
        var e = await db.Packages.AsNoTracking().FirstOrDefaultAsync(p => p.Namespace == ns, ct);
        return e is null ? null : Map.ToPackage(e);
    }

    public async Task<IReadOnlyList<VersionInfo>> GetVersionsAsync(string ns, CancellationToken ct)
    {
        var rows = await db.Versions.AsNoTracking().Where(v => v.Namespace == ns).ToListAsync(ct);
        return rows.Select(Map.ToVersion).ToList();
    }

    public async Task<VersionInfo?> GetVersionAsync(string ns, string version, CancellationToken ct)
    {
        var e = await db.Versions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Namespace == ns && v.Version == version, ct);
        return e is null ? null : Map.ToVersion(e);
    }

    public async Task UpsertPackageAsync(PackageInfo pkg, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pkg);
        var e = await db.Packages.FirstOrDefaultAsync(p => p.Namespace == pkg.Namespace, ct);
        if (e is null)
        {
            db.Packages.Add(Map.ToEntity(pkg));
        }
        else
        {
            e.Description = pkg.Description;
            e.KeywordsJson = Map.ToJson(pkg.Keywords);
            e.Downloads = pkg.Downloads;
            e.UpdatedAt = pkg.UpdatedAt;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task AddVersionAsync(VersionInfo v, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(v);
        var existing = await db.Versions
            .FirstOrDefaultAsync(x => x.Namespace == v.Namespace && x.Version == v.Version, ct);
        if (existing is not null)
        {
            if (!string.Equals(existing.Hash, v.Hash, StringComparison.Ordinal))
            {
                throw new VersionExistsException(v.Namespace, v.Version);
            }

            return; // idempotent re-publish of identical content
        }

        db.Versions.Add(Map.ToEntity(v));
        await db.SaveChangesAsync(ct);
    }

    public async Task SetYankedAsync(string ns, string version, bool yanked, CancellationToken ct)
    {
        var e = await db.Versions.FirstOrDefaultAsync(x => x.Namespace == ns && x.Version == version, ct);
        if (e is null)
        {
            return;
        }

        e.Yanked = yanked;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetOwnersAsync(string ns, CancellationToken ct)
    {
        return await db.Owners.AsNoTracking()
            .Where(o => o.PackageNamespace == ns)
            .Join(db.Accounts.AsNoTracking(), o => o.AccountId, a => a.Id, (_, a) => a.Name)
            .OrderBy(name => name)
            .ToListAsync(ct);
    }

    public async Task<bool> IsOwnerAsync(string ns, string accountId, CancellationToken ct) =>
        await db.Owners.AsNoTracking().AnyAsync(o => o.PackageNamespace == ns && o.AccountId == accountId, ct);

    public async Task AddOwnerAsync(string ns, string accountId, CancellationToken ct)
    {
        if (await db.Owners.AnyAsync(o => o.PackageNamespace == ns && o.AccountId == accountId, ct))
        {
            return;
        }

        db.Owners.Add(new PackageOwnerEntity { PackageNamespace = ns, AccountId = accountId });
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveOwnerAsync(string ns, string accountId, CancellationToken ct)
    {
        var e = await db.Owners.FirstOrDefaultAsync(o => o.PackageNamespace == ns && o.AccountId == accountId, ct);
        if (e is null)
        {
            return;
        }

        db.Owners.Remove(e);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Entity ↔ domain-record mapping and the JSON column codec.</summary>
internal static class Map
{
    public static PackageInfo ToPackage(PackageEntity e) => new(
        e.Namespace, e.Description, FromJson(e.KeywordsJson), e.Downloads, e.CreatedAt, e.UpdatedAt);

    public static VersionInfo ToVersion(PackageVersionEntity e) => new(
        e.Namespace, e.Version, e.Hash, DepsFromJson(e.DependenciesJson), FromJson(e.CapabilitiesJson),
        e.Yanked, e.Size, e.PublishedAt);

    public static PackageEntity ToEntity(PackageInfo p) => new()
    {
        Namespace = p.Namespace,
        Description = p.Description,
        KeywordsJson = ToJson(p.Keywords),
        Downloads = p.Downloads,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
    };

    public static PackageVersionEntity ToEntity(VersionInfo v) => new()
    {
        Namespace = v.Namespace,
        Version = v.Version,
        Hash = v.Hash,
        DependenciesJson = JsonSerializer.Serialize(v.Dependencies),
        CapabilitiesJson = ToJson(v.Capabilities),
        Yanked = v.Yanked,
        Size = v.Size,
        PublishedAt = v.PublishedAt,
    };

    public static string ToJson(IReadOnlyList<string> values) => JsonSerializer.Serialize(values);

    private static IReadOnlyList<string> FromJson(string json) =>
        JsonSerializer.Deserialize<List<string>>(json) ?? [];

    private static IReadOnlyList<Dependency> DepsFromJson(string json) =>
        JsonSerializer.Deserialize<List<Dependency>>(json) ?? [];
}
