using System.Text.Json;

namespace Ashes.Cli.Registry;

/// <summary>Publish-relevant fields read from <c>ashes.json</c> beyond what <c>AshesProject</c> models.</summary>
internal sealed record ManifestInfo(
    string? Namespace,
    string? Version,
    string Description,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<DependencyOut> Dependencies);

/// <summary>A registry-form dependency for publish metadata: <c>{ "namespace", "req" }</c>.</summary>
internal sealed record DependencyOut(string Namespace, string Req);

internal static class Manifest
{
    public static ManifestInfo Read(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var dependencies = new List<DependencyOut>();
        if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
        {
            // Only registry (SemVer-string) dependencies are carried in publish metadata; path/git
            // (object-valued) entries are local build wiring, not part of the published surface.
            foreach (var entry in deps.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.String)
                {
                    dependencies.Add(new DependencyOut(entry.Name, entry.Value.GetString() ?? "*"));
                }
            }
        }

        return new ManifestInfo(
            GetString(root, "namespace"),
            GetString(root, "version"),
            GetString(root, "description") ?? "",
            GetStringArray(root, "keywords"),
            dependencies);
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> GetStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var items = new List<string>();
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String && element.GetString() is { } s)
            {
                items.Add(s);
            }
        }

        return items;
    }
}
