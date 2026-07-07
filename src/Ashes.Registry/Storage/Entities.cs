using Microsoft.EntityFrameworkCore;

namespace Ashes.Registry.Storage;

// Persistence entities. List-valued version metadata (keywords, dependencies, capabilities) is stored as
// JSON text columns; the store layer maps to/from the immutable domain records. Ownership is relational:
// a package is owned by one or more accounts via PackageOwnerEntity.

internal sealed class PackageEntity
{
    public string Namespace { get; set; } = "";

    public string Description { get; set; } = "";

    public string KeywordsJson { get; set; } = "[]";

    public long Downloads { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<PackageVersionEntity> Versions { get; } = [];

    public List<PackageOwnerEntity> Owners { get; } = [];
}

internal sealed class PackageVersionEntity
{
    public string Namespace { get; set; } = "";

    public string Version { get; set; } = "";

    public string Hash { get; set; } = "";

    public string DependenciesJson { get; set; } = "[]";

    public string CapabilitiesJson { get; set; } = "[]";

    public bool Yanked { get; set; }

    public long Size { get; set; }

    public DateTimeOffset PublishedAt { get; set; }
}

internal sealed class AccountEntity
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class ApiTokenEntity
{
    public string Id { get; set; } = "";

    public string AccountId { get; set; } = "";

    public string HashedSecret { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }
}

internal sealed class PackageOwnerEntity
{
    public string PackageNamespace { get; set; } = "";

    public string AccountId { get; set; } = "";
}

internal sealed class RegistryDbContext(DbContextOptions<RegistryDbContext> options) : DbContext(options)
{
    public DbSet<PackageEntity> Packages => Set<PackageEntity>();

    public DbSet<PackageVersionEntity> Versions => Set<PackageVersionEntity>();

    public DbSet<AccountEntity> Accounts => Set<AccountEntity>();

    public DbSet<ApiTokenEntity> Tokens => Set<ApiTokenEntity>();

    public DbSet<PackageOwnerEntity> Owners => Set<PackageOwnerEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PackageEntity>(e =>
        {
            e.HasKey(x => x.Namespace);
            e.HasMany(x => x.Versions).WithOne().HasForeignKey(v => v.Namespace).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Owners).WithOne().HasForeignKey(o => o.PackageNamespace).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PackageVersionEntity>(e => e.HasKey(x => new { x.Namespace, x.Version }));

        modelBuilder.Entity<AccountEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<ApiTokenEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.HashedSecret).IsUnique();
        });

        modelBuilder.Entity<PackageOwnerEntity>(e => e.HasKey(x => new { x.PackageNamespace, x.AccountId }));
    }
}
