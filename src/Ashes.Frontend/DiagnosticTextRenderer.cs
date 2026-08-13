using System.Text;

namespace Ashes.Frontend;

/// <summary>
/// Renders structured compiler diagnostics into human-readable, rustc-style text with a
/// <c>path:line:column</c> header and, when the source is available, a caret-underlined snippet of the
/// offending line.
/// </summary>
public static class DiagnosticTextRenderer
{
    /// <summary>
    /// Renders the diagnostics carried by <paramref name="exception"/> against
    /// <paramref name="source"/>, labelling locations with <paramref name="displayPath"/>.
    /// </summary>
    public static string RenderCompilerDiagnostics(CompileDiagnosticException exception, string? source, string displayPath)
    {
        return RenderCompilerDiagnostics(exception.StructuredErrors, source, displayPath);
    }

    /// <summary>
    /// Renders <paramref name="entries"/> sorted by position into text. When <paramref name="source"/>
    /// is non-null each entry gets a caret-underlined source snippet; otherwise only the
    /// <paramref name="displayPath"/> header and message are emitted. An empty list yields a generic
    /// unknown-error message.
    /// </summary>
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
                    var lineNumberText = location.Line.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    sb.Append(' ', lineNumberText.Length);
                    sb.AppendLine(" |");
                    sb.Append(lineNumberText);
                    sb.Append(" | ");
                    sb.AppendLine(lineText);
                    sb.Append(' ', lineNumberText.Length);
                    sb.Append(" | ");
                    sb.Append(' ', Math.Max(location.Column - 1, 0));
                    sb.AppendLine(new string('^', sourceView.ComputeUnderlineLength(entry, location, lineText)));
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

    /// <summary>
    /// Renders a standalone failure not tied to a source span: a <paramref name="kind"/>-prefixed
    /// <paramref name="message"/>, optionally followed by an <c>--&gt;</c> line naming
    /// <paramref name="displayPath"/>.
    /// </summary>
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

    private sealed class SourceView
    {
        private readonly string[] _lines;
        private readonly SourceTextIndex _index;

        public SourceView(string source)
        {
            _lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
            _index = new SourceTextIndex(source);
        }

        public SourceLocation GetLocation(int position)
        {
            (int line, int character) = _index.ToPosition(position, SourcePositionEncoding.UnicodeScalar);
            return new SourceLocation(line + 1, character + 1);
        }

        public string? GetLine(int line)
        {
            if (line <= 0 || line > _lines.Length)
            {
                return null;
            }

            return _lines[line - 1].TrimEnd('\r');
        }

        public int ComputeUnderlineLength(DiagnosticEntry entry, SourceLocation location, string lineText)
        {
            int lineLength = 0;
            foreach (Rune _ in lineText.EnumerateRunes())
            {
                lineLength++;
            }

            int availableOnLine = Math.Max(lineLength - (location.Column - 1), 0);
            (int endLine, int endCharacter) = _index.ToPosition(entry.End, SourcePositionEncoding.UnicodeScalar);
            int requested = endLine == location.Line - 1
                ? Math.Max(endCharacter - (location.Column - 1), 1)
                : Math.Max(availableOnLine, 1);
            return availableOnLine <= 0 ? 1 : Math.Clamp(requested, 1, availableOnLine);
        }
    }

    private readonly record struct SourceLocation(int Line, int Column);
}
