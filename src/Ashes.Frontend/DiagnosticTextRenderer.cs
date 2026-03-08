using System.Text;

namespace Ashes.Frontend;

public static class DiagnosticTextRenderer
{
    public static string RenderCompilerDiagnostics(CompileDiagnosticException exception, string? source, string displayPath)
    {
        return RenderCompilerDiagnostics(exception.StructuredErrors, source, displayPath);
    }

    public static string RenderCompilerDiagnostics(IReadOnlyList<DiagnosticEntry> entries, string? source, string displayPath)
    {
        var orderedEntries = entries
            .OrderBy(entry => entry.Start)
            .ThenBy(entry => entry.End)
            .ThenBy(entry => entry.Message, StringComparer.Ordinal)
            .ToArray();

        if (orderedEntries.Length == 0)
        {
            return RenderFailure("error", "Unknown compiler error.", displayPath);
        }

        var sourceView = source is null ? null : new SourceView(source);
        var sb = new StringBuilder();

        for (int i = 0; i < orderedEntries.Length; i++)
        {
            var entry = orderedEntries[i];
            if (sourceView is not null)
            {
                var location = sourceView.GetLocation(entry.Start);
                AppendHeader(sb, displayPath, location.Line, location.Column, entry);

                var lineText = sourceView.GetLine(location.Line);
                if (lineText is not null)
                {
                    var lineNumberText = location.Line.ToString();
                    sb.Append(' ', lineNumberText.Length);
                    sb.AppendLine(" |");
                    sb.Append(lineNumberText);
                    sb.Append(" | ");
                    sb.AppendLine(lineText);
                    sb.Append(' ', lineNumberText.Length);
                    sb.Append(" | ");
                    sb.Append(' ', Math.Max(location.Column - 1, 0));
                    sb.AppendLine(new string('^', ComputeUnderlineLength(entry, location, lineText.Length)));
                    sb.Append(' ', lineNumberText.Length);
                    sb.AppendLine(" |");
                }
            }
            else
            {
                AppendHeader(sb, displayPath, null, null, entry);
            }

            if (i < orderedEntries.Length - 1)
            {
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, string displayPath, int? line, int? column, DiagnosticEntry entry)
    {
        sb.Append(displayPath);
        if (line is not null && column is not null)
        {
            sb.Append(':');
            sb.Append(line.Value);
            sb.Append(':');
            sb.Append(column.Value);
        }

        if (!string.IsNullOrWhiteSpace(entry.Code))
        {
            sb.Append(' ');
            sb.Append(entry.Code);
        }

        sb.Append(' ');
        sb.AppendLine(entry.Message);
    }

    public static string RenderFailure(string kind, string message, string? displayPath = null)
    {
        var sb = new StringBuilder();
        sb.Append(kind);
        sb.Append(": ");
        sb.AppendLine(message);
        if (!string.IsNullOrWhiteSpace(displayPath))
        {
            sb.Append("  --> ");
            sb.AppendLine(displayPath);
        }

        return sb.ToString();
    }

    private static int ComputeUnderlineLength(DiagnosticEntry entry, SourceLocation location, int lineLength)
    {
        var availableOnLine = Math.Max(lineLength - (location.Column - 1), 0);
        var requested = Math.Max(entry.End - entry.Start, 1);
        if (availableOnLine <= 0)
        {
            return 1;
        }

        return Math.Clamp(requested, 1, availableOnLine);
    }

    private sealed class SourceView
    {
        private readonly string[] _lines;
        private readonly int[] _lineStarts;

        public SourceView(string source)
        {
            _lines = source.Split(["\r\n", "\n"], StringSplitOptions.None);
            var starts = new List<int> { 0 };
            for (var i = 0; i < source.Length; i++)
            {
                if (source[i] == '\n')
                {
                    starts.Add(i + 1);
                }
            }

            _lineStarts = [.. starts];
        }

        public SourceLocation GetLocation(int position)
        {
            for (var i = _lineStarts.Length - 1; i >= 0; i--)
            {
                if (_lineStarts[i] <= position)
                {
                    return new SourceLocation(i + 1, position - _lineStarts[i] + 1);
                }
            }

            return new SourceLocation(1, 1);
        }

        public string? GetLine(int line)
        {
            if (line <= 0 || line > _lines.Length)
            {
                return null;
            }

            return _lines[line - 1].TrimEnd('\r');
        }
    }

    private readonly record struct SourceLocation(int Line, int Column);
}
