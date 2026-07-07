using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ashes.Registry.Storage;

/// <summary>
/// Design-time factory so <c>dotnet ef</c> can construct the context for migrations without booting the
/// web host. The connection string here is only used by the tooling; the running app supplies the real
/// <c>--data</c>-rooted SQLite path.
/// </summary>
internal sealed class RegistryDbContextFactory : IDesignTimeDbContextFactory<RegistryDbContext>
{
    public RegistryDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<RegistryDbContext>()
            .UseSqlite("Data Source=registry-design.db")
            .Options;
        return new RegistryDbContext(options);
    }
}
