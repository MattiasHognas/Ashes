using System.Text.RegularExpressions;

namespace Ashes.Formatter;

public static class EditorConfigFormattingOptionsResolver
{
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

        var effectiveIndentSize = options.IndentSize;
        var effectiveUseTabs = options.UseTabs;
        var effectiveNewLine = options.NewLine;
        int? effectiveTabWidth = null;
        var hasIndentSize = false;
        var indentSizeUsesTabWidth = false;

        while (configPaths.Count > 0)
        {
            ParseAndApply(
                configPaths.Pop(),
                fullPath,
                ref effectiveIndentSize,
                ref effectiveUseTabs,
                ref effectiveNewLine,
                ref effectiveTabWidth,
                ref hasIndentSize,
                ref indentSizeUsesTabWidth);
        }

        if (!hasIndentSize && effectiveTabWidth is int tabWidth && tabWidth > 0)
        {
            effectiveIndentSize = tabWidth;
        }

        return new FormattingOptions
        {
            IndentSize = effectiveIndentSize > 0 ? effectiveIndentSize : 4,
            UseTabs = effectiveUseTabs,
            NewLine = effectiveNewLine is "\n" or "\r\n" ? effectiveNewLine : Environment.NewLine
        };
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

    private static void ParseAndApply(
        string editorConfigPath,
        string filePath,
        ref int indentSize,
        ref bool useTabs,
        ref string newLine,
        ref int? tabWidth,
        ref bool hasIndentSize,
        ref bool indentSizeUsesTabWidth)
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

            if (key.Equals("indent_style", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Equals("tab", StringComparison.OrdinalIgnoreCase))
                {
                    useTabs = true;
                }
                else if (value.Equals("space", StringComparison.OrdinalIgnoreCase))
                {
                    useTabs = false;
                }

                continue;
            }

            if (key.Equals("indent_size", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Equals("tab", StringComparison.OrdinalIgnoreCase))
                {
                    hasIndentSize = true;
                    indentSizeUsesTabWidth = true;
                    if (tabWidth is int width && width > 0)
                    {
                        indentSize = width;
                    }
                }
                else if (int.TryParse(value, out var parsedIndent) && parsedIndent > 0)
                {
                    hasIndentSize = true;
                    indentSizeUsesTabWidth = false;
                    indentSize = parsedIndent;
                }

                continue;
            }

            if (key.Equals("tab_width", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, out var parsedTabWidth) && parsedTabWidth > 0)
                {
                    tabWidth = parsedTabWidth;
                    if (indentSizeUsesTabWidth)
                    {
                        indentSize = parsedTabWidth;
                    }
                }

                continue;
            }

            if (key.Equals("end_of_line", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Equals("lf", StringComparison.OrdinalIgnoreCase))
                {
                    newLine = "\n";
                }
                else if (value.Equals("crlf", StringComparison.OrdinalIgnoreCase))
                {
                    newLine = "\r\n";
                }
            }
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

        return Regex.IsMatch(target, regexPattern, regexOptions);
    }

    private static string NormalizeLine(string line)
    {
        return line.Trim().TrimEnd('\r');
    }
}
