using Microsoft.EntityFrameworkCore;

namespace Ashes.Registry.Storage;

// Persistence entities. List-valued metadata (keywords, owners, dependencies, capabilities) is stored
// as JSON text columns; the store layer maps to/from the immutable domain records. Owners are denormalized
// to names for the read path; they become account references when auth lands.

internal sealed class PackageEntity
{
    public string Namespace { get; set; } = "";

    public string Description { get; set; } = "";

    public string KeywordsJson { get; set; } = "[]";

    public string OwnersJson { get; set; } = "[]";

    public long Downloads { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<PackageVersionEntity> Versions { get; } = [];
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

internal sealed class RegistryDbContext(DbContextOptions<RegistryDbContext> options) : DbContext(options)
{
    public DbSet<PackageEntity> Packages => Set<PackageEntity>();

    public DbSet<PackageVersionEntity> Versions => Set<PackageVersionEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PackageEntity>(e =>
        {
            e.HasKey(x => x.Namespace);
            e.HasMany(x => x.Versions).WithOne().HasForeignKey(v => v.Namespace).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PackageVersionEntity>(e => e.HasKey(x => new { x.Namespace, x.Version }));
    }
}
