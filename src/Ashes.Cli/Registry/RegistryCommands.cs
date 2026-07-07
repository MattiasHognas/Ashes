using System.Text.Json;
using Ashes.Semantics;
using Spectre.Console;

namespace Ashes.Cli.Registry;

/// <summary>The registry-facing CLI verbs: login, publish, yank, search, info.</summary>
internal static class RegistryCommands
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<int> LoginAsync(string[] args, CancellationToken ct)
    {
        var opts = ArgScanner.Parse(args);
        var baseUrl = RegistryConfig.ResolveBaseUrl(opts.Value("registry"));

        var token = opts.Value("token");
        if (token is null)
        {
            var account = opts.Value("as")
                ?? throw new CliUsageException("Provide --token <token> or --as <account> to log in.");
            using var client = new RegistryClient();
            token = await client.MintTokenAsync(baseUrl, account, ct).ConfigureAwait(false);
        }

        RegistryConfig.SetToken(baseUrl, token);
        AnsiConsole.MarkupLine($"[green]Logged in[/] to {baseUrl}");
        return 0;
    }

    public static async Task<int> PublishAsync(string[] args, CancellationToken ct)
    {
        var opts = ArgScanner.Parse(args);
        var (project, manifestPath) = LoadProject(opts.Value("project"));
        var manifest = Manifest.Read(manifestPath);

        var ns = ProjectPackager.DeriveNamespace(manifest.Namespace, project.Name);
        var version = opts.Value("version") ?? manifest.Version
            ?? throw new CliUsageException("No version: set \"version\" in ashes.json or pass --version <v>.");

        var files = ProjectPackager.GatherFiles(project);
        if (files.Count == 0)
        {
            throw new CliUserException("Nothing to publish: no .ash sources found under the source roots.");
        }

        var hash = SourceHasher.Compute(files);
        var tarball = ProjectPackager.BuildTarball(files);
        var metadata = JsonSerializer.Serialize(
            new { description = manifest.Description, keywords = manifest.Keywords, dependencies = manifest.Dependencies, hash },
            Json);

        var baseUrl = RegistryConfig.ResolveBaseUrl(opts.Value("registry"));
        var token = RegistryConfig.GetToken(baseUrl)
            ?? throw new CliUserException($"Not logged in to {baseUrl}. Run `ashes login` first.");

        using var client = new RegistryClient();
        await client.PublishAsync(baseUrl, token, ns, version, metadata, tarball, ct).ConfigureAwait(false);
        AnsiConsole.MarkupLine($"[green]Published[/] {ns}@{version} ({files.Count} files) to {baseUrl}");
        return 0;
    }

    public static async Task<int> YankAsync(string[] args, CancellationToken ct)
    {
        var opts = ArgScanner.Parse(args);
        if (opts.Positionals.Count < 2)
        {
            throw new CliUsageException("Usage: ashes yank <namespace> <version> [--undo] [--registry <name>]");
        }

        var ns = opts.Positionals[0];
        var version = opts.Positionals[1];
        var undo = opts.Flag("undo");
        var baseUrl = RegistryConfig.ResolveBaseUrl(opts.Value("registry"));
        var token = RegistryConfig.GetToken(baseUrl)
            ?? throw new CliUserException($"Not logged in to {baseUrl}. Run `ashes login` first.");

        using var client = new RegistryClient();
        await client.SetYankAsync(baseUrl, token, ns, version, yanked: !undo, ct).ConfigureAwait(false);
        AnsiConsole.MarkupLine($"[green]{(undo ? "Unyanked" : "Yanked")}[/] {ns}@{version}");
        return 0;
    }

    public static async Task<int> SearchAsync(string[] args, CancellationToken ct)
    {
        var opts = ArgScanner.Parse(args);
        if (opts.Positionals.Count == 0)
        {
            throw new CliUsageException("Usage: ashes search <query> [--registry <name>]");
        }

        var baseUrl = RegistryConfig.ResolveBaseUrl(opts.Value("registry"));
        using var client = new RegistryClient();
        var results = await client.SearchAsync(baseUrl, string.Join(' ', opts.Positionals), ct).ConfigureAwait(false);

        if (results.Results.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No matches.[/]");
            return 0;
        }

        var table = new Table().AddColumn("Namespace").AddColumn("Latest").AddColumn("Description");
        foreach (var r in results.Results)
        {
            table.AddRow(Markup.Escape(r.Namespace), Markup.Escape(r.Latest ?? "-"), Markup.Escape(r.Description));
        }

        AnsiConsole.Write(table);
        return 0;
    }

    public static async Task<int> InfoAsync(string[] args, CancellationToken ct)
    {
        var opts = ArgScanner.Parse(args);
        if (opts.Positionals.Count == 0)
        {
            throw new CliUsageException("Usage: ashes info <namespace>[@<version>] [--registry <name>]");
        }

        var (ns, wantedVersion) = SplitVersion(opts.Positionals[0]);
        var baseUrl = RegistryConfig.ResolveBaseUrl(opts.Value("registry"));
        using var client = new RegistryClient();
        var package = await client.GetPackageAsync(baseUrl, ns, ct).ConfigureAwait(false)
            ?? throw new CliUserException($"No package '{ns}' on {baseUrl}.");

        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(package.Namespace)}[/] — {Markup.Escape(package.Description)}");
        AnsiConsole.MarkupLine($"Owners: {Markup.Escape(string.Join(", ", package.Owners))}");

        var version = wantedVersion is null
            ? package.Versions.OrderByDescending(v => v.Version, StringComparer.Ordinal).FirstOrDefault()
            : package.Versions.FirstOrDefault(v => string.Equals(v.Version, wantedVersion, StringComparison.Ordinal));

        if (version is null)
        {
            AnsiConsole.MarkupLine("[yellow]No matching version.[/]");
            return 0;
        }

        var caps = version.Capabilities.Count == 0 ? "pure" : $"needs {{{string.Join(", ", version.Capabilities)}}}";
        AnsiConsole.MarkupLine($"Version {Markup.Escape(version.Version)}{(version.Yanked ? " [red](yanked)[/]" : "")} — {Markup.Escape(caps)}");
        if (version.Dependencies.Count > 0)
        {
            AnsiConsole.MarkupLine($"Dependencies: {Markup.Escape(string.Join(", ", version.Dependencies.Select(d => $"{d.Namespace} {d.Req}")))}");
        }

        return 0;
    }

    private static (string Namespace, string? Version) SplitVersion(string spec)
    {
        var at = spec.IndexOf('@', StringComparison.Ordinal);
        return at < 0 ? (spec, null) : (spec[..at], spec[(at + 1)..]);
    }

    private static (AshesProject Project, string ManifestPath) LoadProject(string? projectOption)
    {
        var path = projectOption ?? Path.Combine(Directory.GetCurrentDirectory(), "ashes.json");
        if (!File.Exists(path))
        {
            throw new CliUserException("No ashes.json found; run inside a project or pass --project <path>.");
        }

        return (ProjectSupport.LoadProject(path), path);
    }
}
