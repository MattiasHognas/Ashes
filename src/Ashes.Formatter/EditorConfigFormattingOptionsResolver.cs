using System.Text.RegularExpressions;

namespace Ashes.Formatter;

/// <summary>
/// Derives <see cref="FormattingOptions"/> for a file by reading the <c>.editorconfig</c> chain above
/// it, honoring <c>indent_style</c>, <c>indent_size</c>, <c>tab_width</c>, and <c>end_of_line</c>.
/// </summary>
public static class EditorConfigFormattingOptionsResolver
{
    /// <summary>
    /// Resolves the effective <see cref="FormattingOptions"/> for <paramref name="filePath"/> by
    /// walking every <c>.editorconfig</c> from its directory up to the root-most one, with nearer
    /// files overriding. <paramref name="fallback"/> (or the defaults) supplies any value no config
    /// declares; a null or blank path returns the fallback unchanged.
    /// </summary>
    public static FormattingOptions ResolveForPath(string? filePath, FormattingOptions? fallback = null)
    {
        var options = (fallback ?? new FormattingOptions()).Normalize();
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return options;
        }

        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (directory is null)
        {
            return options;
        }

        var state = new EffectiveOptionsState
        {
            IndentSize = options.IndentSize,
            UseTabs = options.UseTabs,
            NewLine = options.NewLine
        };

        var configPaths = CollectEditorConfigPaths(directory);
        while (configPaths.Count > 0)
        {
            ParseAndApply(configPaths.Pop(), fullPath, state);
        }

        if (!state.HasIndentSize && state.TabWidth is int tabWidth && tabWidth > 0)
        {
            state.IndentSize = tabWidth;
        }

        return new FormattingOptions
        {
            IndentSize = state.IndentSize > 0 ? state.IndentSize : 4,
            UseTabs = state.UseTabs,
            NewLine = state.NewLine is "\n" or "\r\n" ? state.NewLine : Environment.NewLine
        };
    }

    // Mutable accumulator for the option values discovered while walking the .editorconfig chain
    // root-most to nearest (later files win).
    private sealed class EffectiveOptionsState
    {
        public int IndentSize;
        public bool UseTabs;
        public string NewLine = Environment.NewLine;
        public int? TabWidth;
        public bool HasIndentSize;
        public bool IndentSizeUsesTabWidth;
    }

    /// <summary>
    /// Walks from <paramref name="directory"/> upward collecting every <c>.editorconfig</c>, stopping
    /// at the first one that declares <c>root = true</c>. The stack pops root-most first so nearer
    /// files override.
    /// </summary>
    private static Stack<string> CollectEditorConfigPaths(string directory)
    {
        var configPaths = new Stack<string>();
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var editorConfigPath = Path.Combine(current.FullName, ".editorconfig");
            if (!File.Exists(editorConfigPath))
            {
                continue;
            }

            configPaths.Push(editorConfigPath);
            if (ContainsRootTrue(editorConfigPath))
            {
                break;
            }
        }

        return configPaths;
    }

    private static bool ContainsRootTrue(string editorConfigPath)
    {
        foreach (var rawLine in File.ReadLines(editorConfigPath))
        {
            var line = NormalizeLine(rawLine);
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                return false;
            }

            var equalsIndex = line.IndexOf('=');
            if (equalsIndex <= 0)
            {
                continue;
            }

            var key = line[..equalsIndex].Trim();
            var value = line[(equalsIndex + 1)..].Trim();
            if (key.Equals("root", StringComparison.OrdinalIgnoreCase) && value.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void ParseAndApply(string editorConfigPath, string filePath, EffectiveOptionsState state)
    {
        string? currentSection = null;
        var sectionDirectory = Path.GetDirectoryName(editorConfigPath) ?? string.Empty;
        var relativePath = Path.GetRelativePath(sectionDirectory, filePath).Replace('\\', '/');
        var fileName = Path.GetFileName(filePath);

        foreach (var rawLine in File.ReadLines(editorConfigPath))
        {
            var line = NormalizeLine(rawLine);
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                currentSection = line[1..^1].Trim();
                continue;
            }

            if (currentSection is null || !IsPatternMatch(currentSection, relativePath, fileName))
            {
                continue;
            }

            var equalsIndex = line.IndexOf('=');
            if (equalsIndex <= 0)
            {
                continue;
            }

            var key = line[..equalsIndex].Trim();
            var value = line[(equalsIndex + 1)..].Trim();
            ApplyProperty(key, value, state);
        }
    }

    private static void ApplyProperty(string key, string value, EffectiveOptionsState state)
    {
        if (key.Equals("indent_style", StringComparison.OrdinalIgnoreCase))
        {
            if (value.Equals("tab", StringComparison.OrdinalIgnoreCase))
            {
                state.UseTabs = true;
            }
            else if (value.Equals("space", StringComparison.OrdinalIgnoreCase))
            {
                state.UseTabs = false;
            }

            return;
        }

        if (key.Equals("indent_size", StringComparison.OrdinalIgnoreCase))
        {
            ApplyIndentSize(value, state);
            return;
        }

        if (key.Equals("tab_width", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsedTabWidth) && parsedTabWidth > 0)
            {
                state.TabWidth = parsedTabWidth;
                if (state.IndentSizeUsesTabWidth)
                {
                    state.IndentSize = parsedTabWidth;
                }
            }

            return;
        }

        if (key.Equals("end_of_line", StringComparison.OrdinalIgnoreCase))
        {
            if (value.Equals("lf", StringComparison.OrdinalIgnoreCase))
            {
                state.NewLine = "\n";
            }
            else if (value.Equals("crlf", StringComparison.OrdinalIgnoreCase))
            {
                state.NewLine = "\r\n";
            }
        }
    }

    private static void ApplyIndentSize(string value, EffectiveOptionsState state)
    {
        if (value.Equals("tab", StringComparison.OrdinalIgnoreCase))
        {
            state.HasIndentSize = true;
            state.IndentSizeUsesTabWidth = true;
            if (state.TabWidth is int width && width > 0)
            {
                state.IndentSize = width;
            }
        }
        else if (int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsedIndent) && parsedIndent > 0)
        {
            state.HasIndentSize = true;
            state.IndentSizeUsesTabWidth = false;
            state.IndentSize = parsedIndent;
        }
    }

    private static bool IsPatternMatch(string sectionPattern, string relativePath, string fileName)
    {
        if (sectionPattern.Length == 0)
        {
            return false;
        }

        var target = sectionPattern.Contains('/', StringComparison.Ordinal) ? relativePath : fileName;
        var regexPattern = "^" + Regex.Escape(sectionPattern)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", @"[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", @"[^/]", StringComparison.Ordinal) + "$";

        var regexOptions = RegexOptions.CultureInvariant;
        if (OperatingSystem.IsWindows())
        {
            regexOptions |= RegexOptions.IgnoreCase;
        }

        return Regex.IsMatch(target, regexPattern, regexOptions, TimeSpan.FromSeconds(1));
    }

    private static string NormalizeLine(string line)
    {
        return line.Trim().TrimEnd('\r');
    }
}
