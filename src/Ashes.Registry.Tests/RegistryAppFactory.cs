using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ashes.Registry.Tests;

/// <summary>
/// Boots the real registry app in-memory over a fresh, disposable <c>--data</c> directory, so every
/// test is hermetic (its own SQLite database and blob tree) and nothing touches an ambient registry.
/// </summary>
internal sealed class RegistryAppFactory : WebApplicationFactory<Program>
{
    public string DataDir { get; } =
        Path.Combine(Path.GetTempPath(), "ashes-registry-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting("Registry:DataDir", DataDir);
        builder.UseEnvironment("Testing");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(DataDir))
        {
            try
            {
                Directory.Delete(DataDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; the OS temp sweeper reclaims anything left behind.
            }
        }
    }
}
