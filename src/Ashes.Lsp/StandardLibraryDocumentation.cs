using System.Reflection;
using System.Text.RegularExpressions;

namespace Ashes.Lsp;

/// <summary>
/// Offline documentation catalogue extracted from the same Markdown source published by VitePress.
/// The parser runs once per language-server process; signatures remain compiler-owned and are never
/// read from the prose document.
/// </summary>
internal static partial class StandardLibraryDocumentation
{
    private const string ResourceName = "Ashes.Lsp.Documentation.standard-library.md";
#pragma warning disable S1075 // Canonical public documentation site used only as the optional full-reference hover link.
    private const string ReferenceUrl = "https://mattiashognas.github.io/Ashes/reference/standard-library";
#pragma warning restore S1075

    internal readonly record struct Entry(string Summary, string Url);

    private static readonly Lazy<IReadOnlyDictionary<string, Entry>> Entries = new(LoadEntries);

    public static bool TryGet(string qualifiedName, out Entry entry) =>
        Entries.Value.TryGetValue(qualifiedName, out entry);

    public static string? ResolveUnqualified(
        string name,
        IReadOnlyList<DocumentService.ImportItem> imports)
    {
        string[] candidates = imports
            .Where(import => import.Selector is null
                && import.ModuleName.StartsWith("Ashes.", StringComparison.Ordinal))
            .Select(import => $"{import.ModuleName}.{name}")
            .Where(candidate => Entries.Value.ContainsKey(candidate))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static IReadOnlyDictionary<string, Entry> LoadEntries()
    {
        Assembly assembly = typeof(StandardLibraryDocumentation).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded documentation resource '{ResourceName}'.");
        using var reader = new StreamReader(stream);
        string[] lines = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        string? moduleName = null;

        for (int index = 0; index < lines.Length; index++)
        {
            Match heading = ModuleHeadingRegex().Match(lines[index]);
            if (heading.Success)
            {
                moduleName = heading.Groups[1].Value;
                continue;
            }

            if (moduleName is null || !lines[index].StartsWith("- ", StringComparison.Ordinal))
            {
                continue;
            }

            string item = lines[index][2..].Trim();
            while (index + 1 < lines.Length && ContinuationLineRegex().IsMatch(lines[index + 1]))
            {
                index++;
                item += " " + lines[index].Trim();
            }

            foreach (string memberName in ExtractMemberNames(item))
            {
                string? summary = ExtractSummary(item);
                if (string.IsNullOrWhiteSpace(summary))
                {
                    continue;
                }

                string qualifiedName = $"{moduleName}.{memberName}";
                entries[qualifiedName] = new Entry(
                    summary,
                    $"{ReferenceUrl}#{Slugify(moduleName)}");
            }
        }

        return entries;
    }

    private static IReadOnlyList<string> ExtractMemberNames(string item)
    {
        int separator = item.IndexOf('—');
        string declaration = separator < 0 ? item : item[..separator];
        Match match = CodeSpanRegex().Match(declaration);
        if (!match.Success)
        {
            return [];
        }

        string candidate = match.Groups[1].Value;
        int delimiter = candidate.IndexOfAny(['(', ' ', ':']);
        if (delimiter >= 0)
        {
            candidate = candidate[..delimiter];
        }
        return IdentifierRegex().IsMatch(candidate) ? [candidate] : [];
    }

    private static string? ExtractSummary(string item)
    {
        int separator = item.IndexOf('—');
        if (separator < 0)
        {
            return null;
        }

        string summary = item[(separator + 1)..].Trim();
        if (summary.StartsWith('`'))
        {
            int endOfType = summary.IndexOf('`', 1);
            if (endOfType > 0)
            {
                summary = summary[(endOfType + 1)..].TrimStart(' ', ',');
            }
        }
        if (summary.Length == 0)
        {
            return null;
        }

        return char.ToUpperInvariant(summary[0]) + summary[1..].TrimEnd('.') + ".";
    }

    private static string Slugify(string heading) =>
        SlugSeparatorRegex().Replace(heading.ToLowerInvariant(), "-").Trim('-');

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.None, 1000)]
    private static partial Regex SlugSeparatorRegex();

    [GeneratedRegex("^### `([^`]+)`", RegexOptions.None, 1000)]
    private static partial Regex ModuleHeadingRegex();

    [GeneratedRegex("^\\s{2,}\\S", RegexOptions.None, 1000)]
    private static partial Regex ContinuationLineRegex();

    [GeneratedRegex("`([^`]+)`", RegexOptions.None, 1000)]
    private static partial Regex CodeSpanRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.None, 1000)]
    private static partial Regex IdentifierRegex();
}
