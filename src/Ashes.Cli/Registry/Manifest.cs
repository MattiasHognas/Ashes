using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ashes.Cli.Registry;

/// <summary>Publish-relevant fields read from <c>ashes.json</c> beyond what <c>AshesProject</c> models.</summary>
internal sealed record ManifestInfo(
    string? Namespace,
    string? Version,
    string Description,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<DependencyOut> Dependencies,
    IReadOnlyList<string> NonPortableDependencies,
    byte[] PublishedBytes);

/// <summary>A registry-form dependency for publish metadata: <c>{ "namespace", "req" }</c>.</summary>
internal sealed record DependencyOut(string Namespace, string Req);

internal static class Manifest
{
    public static void EnsurePublishable(ManifestInfo manifest)
    {
        if (manifest.NonPortableDependencies.Count == 0)
        {
            return;
        }

        throw new CliUserException(
            "Cannot publish with non-portable runtime dependencies: " +
            string.Join(", ", manifest.NonPortableDependencies.Order(StringComparer.Ordinal)) +
            ". Use registry version constraints in dependencies and put local paths in root-level overrides.");
    }

    public static ManifestInfo Read(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var dependencies = new List<DependencyOut>();
        var nonPortableDependencies = new List<string>();
        if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in deps.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.String)
                {
                    dependencies.Add(new DependencyOut(entry.Name, entry.Value.GetString() ?? "*"));
                }
                else
                {
                    nonPortableDependencies.Add(entry.Name);
                }
            }
        }

        var publishedRoot = JsonNode.Parse(root.GetRawText())!.AsObject();
        publishedRoot.Remove("overrides");
        publishedRoot.Remove("devDependencies");
        var publishedBytes = System.Text.Encoding.UTF8.GetBytes(
            publishedRoot.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);

        return new ManifestInfo(
            GetString(root, "namespace"),
            GetString(root, "version"),
            GetString(root, "description") ?? "",
            GetStringArray(root, "keywords"),
            dependencies,
            nonPortableDependencies,
            publishedBytes);
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
