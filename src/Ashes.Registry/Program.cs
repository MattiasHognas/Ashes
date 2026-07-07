using Ashes.Registry.Api;
using Ashes.Registry.Publish;
using Ashes.Registry.Storage;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Resolve the --data directory (also honoured as Registry:DataDir) and make it the home for both the
// SQLite metadata database and the content-addressed blob tree.
var dataDir = Path.GetFullPath(
    builder.Configuration["Registry:DataDir"]
    ?? builder.Configuration["data"]
    ?? "data");
Directory.CreateDirectory(dataDir);

builder.Services.Configure<RegistryOptions>(builder.Configuration.GetSection("Registry"));
builder.Services.PostConfigure<RegistryOptions>(o => o.DataDir = dataDir);

builder.Services.AddDbContext<RegistryDbContext>(o =>
    o.UseSqlite($"Data Source={Path.Combine(dataDir, "registry.db")}"));

builder.Services.AddScoped<IMetadataStore, EfMetadataStore>();
builder.Services.AddScoped<ISearchIndex, EfSearchIndex>();
builder.Services.AddScoped<IAccountStore, EfAccountStore>();
builder.Services.AddSingleton<IBlobStore, FileSystemBlobStore>();

builder.Services.AddSingleton<IManifestValidator, StructuralManifestValidator>();
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

app.MapReadEndpoints();
app.MapWriteEndpoints();

await app.RunAsync();

// Exposed so the test project's WebApplicationFactory<Program> can boot the real app in-memory.
public partial class Program
{
    protected Program()
    {
    }
}
