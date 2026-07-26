using Ashes.Registry.Api;
using Ashes.Registry.Publish;
using Ashes.Registry.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// `dotnet build` produces the Vite SPA under Web/dist. A published registry carries those same files
// under wwwroot, so both local and deployed hosts serve the UI from the same ASP.NET Core process.
var localSpaRoot = Path.Combine(builder.Environment.ContentRootPath, "Web", "dist");
var hasLocalSpa = Directory.Exists(localSpaRoot);
IFileProvider spaFileProvider;
if (hasLocalSpa)
{
    PhysicalFileProvider localSpaFileProvider = new(localSpaRoot);
    builder.Environment.WebRootPath = localSpaRoot;
    builder.Environment.WebRootFileProvider = localSpaFileProvider;
    spaFileProvider = localSpaFileProvider;
}
else
{
    spaFileProvider = builder.Environment.WebRootFileProvider;
}
var spaIndexPath = Path.Combine(hasLocalSpa ? localSpaRoot : builder.Environment.WebRootPath, "index.html");

// Resolve the --data directory (also honoured as Registry:DataDir) and make it the home for both the
// SQLite metadata database and the content-addressed blob tree.
var dataDir = Path.GetFullPath(
    builder.Configuration["data"]
    ?? builder.Configuration["Registry:DataDir"]
    ?? "data");
Directory.CreateDirectory(dataDir);

builder.Services.Configure<RegistryOptions>(builder.Configuration.GetSection("Registry"));
builder.Services.PostConfigure<RegistryOptions>(o => o.DataDir = dataDir);

// The database is configured by ConnectionStrings:Registry (appsettings / env / args). A relative SQLite
// Data Source is rebased under the --data dir so `--data <dir>` stays self-contained; an absolute path or
// a non-SQLite (e.g. PostgreSQL) connection string is used verbatim.
var connectionString = ResolveConnectionString(builder.Configuration.GetConnectionString("Registry"), dataDir);

builder.Services.AddDbContext<RegistryDbContext>(o => o.UseSqlite(connectionString));

static string ResolveConnectionString(string? configured, string dataDir)
{
    if (string.IsNullOrWhiteSpace(configured))
    {
        return $"Data Source={Path.Combine(dataDir, "registry.db")}";
    }

    try
    {
        var builder = new SqliteConnectionStringBuilder(configured);
        if (!string.IsNullOrEmpty(builder.DataSource) &&
            !string.Equals(builder.DataSource, ":memory:", StringComparison.Ordinal) &&
            !Path.IsPathRooted(builder.DataSource))
        {
            builder.DataSource = Path.Combine(dataDir, builder.DataSource);
            return builder.ConnectionString;
        }
    }
    catch (ArgumentException)
    {
        // Not a SQLite connection string (e.g. PostgreSQL): use it as configured.
    }

    return configured;
}

builder.Services.AddScoped<IMetadataStore, EfMetadataStore>();
builder.Services.AddScoped<ISearchIndex, EfSearchIndex>();
builder.Services.AddScoped<IAccountStore, EfAccountStore>();
builder.Services.AddSingleton<IBlobStore, FileSystemBlobStore>();

builder.Services.AddSingleton<IManifestValidator, SemanticManifestValidator>();
builder.Services.AddSingleton<ICapabilityExtractor, CompilerCapabilityExtractor>();
builder.Services.AddScoped<PublishPipeline>();

builder.Services.AddOpenApi();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RegistryDbContext>();
    db.Database.Migrate();
}

// The interactive API reference is a Development-only affordance, never exposed in production.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = spaFileProvider });
app.UseStaticFiles(new StaticFileOptions { FileProvider = spaFileProvider });
app.UseRouting();

app.MapReadEndpoints();
app.MapWriteEndpoints();
// Do not use the conventional `nonfile` constraint here: SemVer routes contain dots (for example
// `/packages/Json/1.2.0`) and must still reach the client router.
app.MapGet("/{*path}", async context =>
{
    var path = context.Request.Path;
    if (path.StartsWithSegments("/api", StringComparison.Ordinal) ||
        path.StartsWithSegments("/healthz", StringComparison.Ordinal) ||
        path.StartsWithSegments("/openapi", StringComparison.Ordinal) ||
        path.StartsWithSegments("/scalar", StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    if (!File.Exists(spaIndexPath))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(spaIndexPath, context.RequestAborted);
});

await app.RunAsync();

// Exposed so the test project's WebApplicationFactory<Program> can boot the real app in-memory.

/// <summary>The registry web application's entry-point class, made public so the test host can boot the
/// real app in-memory via <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program
{
    /// <summary>Prevents external instantiation; the class exists only as a test-host type marker.</summary>
    protected Program()
    {
    }
}
