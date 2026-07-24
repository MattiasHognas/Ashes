using System.Text;
using Ashes.Frontend;

namespace Ashes.Formatter;

/// <summary>
/// Reinserts standalone <c>//</c> comment lines into formatted output. The canonical formatter is
/// AST-based and the AST carries no trivia, so comments would otherwise be dropped. Each standalone
/// comment line in the original source is anchored to the significant (non-comment, non-blank) lines
/// around it by a whitespace-insensitive token signature; after formatting, the comment is inserted
/// before the anchor line's new position. Comments whose anchors disappear (e.g. a line the formatter
/// merged away) fall back to the previous anchor, then to the top of the file — the text is never
/// silently dropped. Trailing (same-line) comments are not handled; that needs real trivia in the AST.
/// </summary>
public static class CommentReinserter
{
    private readonly record struct LineAnchor(string Signature, int Occurrence);

    private readonly record struct SignificantLine(int Index, LineAnchor Anchor);

    /// <summary>
    /// Reinserts the standalone <c>//</c> comment lines of <paramref name="originalSource"/> into
    /// <paramref name="formattedSource"/>, anchoring each to the significant lines around it and joining
    /// with <paramref name="lineEnding"/>. Comments whose anchors no longer exist fall back to the
    /// previous anchor, then to the top of the file, so no comment text is dropped.
    /// </summary>
    public static string ReinsertStandaloneCommentLines(string originalSource, string formattedSource, string lineEnding)
    {
        var originalLines = SplitLines(originalSource);
        if (originalLines.Count == 0)
        {
            return formattedSource;
        }

        var commentInsertions = CollectStandaloneCommentInsertions(originalLines);
        if (commentInsertions.Count == 0)
        {
            return formattedSource;
        }

        var formattedLines = SplitLines(formattedSource);
        var formattedSignificantLines = CollectSignificantLines(formattedLines);
        var formattedAnchorIndices = BuildAnchorIndexMap(formattedSignificantLines);
        var insertionsByPosition = new Dictionary<int, List<string>>();

        foreach (var insertion in commentInsertions)
        {
            var position = ResolveInsertionPosition(insertion.PreviousAnchor, insertion.NextAnchor, formattedAnchorIndices, formattedLines.Count);
            if (!insertionsByPosition.TryGetValue(position, out var linesAtPosition))
            {
                linesAtPosition = [];
                insertionsByPosition[position] = linesAtPosition;
            }

            linesAtPosition.Add(insertion.Text);
        }

        var mergedLines = new List<string>(formattedLines.Count + commentInsertions.Count);
        for (var i = 0; i < formattedLines.Count; i++)
        {
            if (insertionsByPosition.TryGetValue(i, out var linesBefore))
            {
                mergedLines.AddRange(linesBefore);
            }

            mergedLines.Add(formattedLines[i]);
        }

        if (insertionsByPosition.TryGetValue(formattedLines.Count, out var trailingLines))
        {
            mergedLines.AddRange(trailingLines);
        }

        var endsWithNewline = formattedSource.EndsWith("\n", StringComparison.Ordinal);
        var merged = string.Join(lineEnding, mergedLines);
        return endsWithNewline ? merged + lineEnding : merged;
    }

    private static IReadOnlyList<string> SplitLines(string source)
    {
        var lines = new List<string>();
        using var reader = new StringReader(source);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lines.Add(line);
        }

        return lines;
    }

    private static IReadOnlyList<SignificantLine> CollectSignificantLines(IReadOnlyList<string> lines)
    {
        var significantLines = new List<SignificantLine>();
        var occurrenceCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < lines.Count; i++)
        {
            if (IsStandaloneCommentLine(lines[i]))
            {
                continue;
            }

            var signature = GetLineSignature(lines[i]);
            if (signature.Length == 0)
            {
                continue;
            }

            occurrenceCounts.TryGetValue(signature, out var count);
            count++;
            occurrenceCounts[signature] = count;
            significantLines.Add(new SignificantLine(i, new LineAnchor(signature, count)));
        }

        return significantLines;
    }

    private static Dictionary<string, List<int>> BuildAnchorIndexMap(IReadOnlyList<SignificantLine> lines)
    {
        var map = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (!map.TryGetValue(line.Anchor.Signature, out var indices))
            {
                indices = [];
                map[line.Anchor.Signature] = indices;
            }

            indices.Add(line.Index);
        }

        return map;
    }

    private static List<(string Text, LineAnchor? PreviousAnchor, LineAnchor? NextAnchor)> CollectStandaloneCommentInsertions(IReadOnlyList<string> originalLines)
    {
        var significantLines = CollectSignificantLines(originalLines);
        var insertions = new List<(string Text, LineAnchor? PreviousAnchor, LineAnchor? NextAnchor)>();
        var significantIndex = 0;

        for (var i = 0; i < originalLines.Count; i++)
        {
            if (!IsStandaloneCommentLine(originalLines[i]))
            {
                if (significantIndex < significantLines.Count && significantLines[significantIndex].Index == i)
                {
                    significantIndex++;
                }

                continue;
            }

            LineAnchor? previousAnchor = significantIndex > 0 ? significantLines[significantIndex - 1].Anchor : null;
            LineAnchor? nextAnchor = significantIndex < significantLines.Count ? significantLines[significantIndex].Anchor : null;
            insertions.Add((originalLines[i], previousAnchor, nextAnchor));
        }

        return insertions;
    }

    private static int ResolveInsertionPosition(
        LineAnchor? previousAnchor,
        LineAnchor? nextAnchor,
        IReadOnlyDictionary<string, List<int>> formattedAnchorIndices,
        int formattedLineCount)
    {
        if (nextAnchor is not null && TryFindAnchorIndex(nextAnchor.Value, formattedAnchorIndices, out var nextIndex))
        {
            return nextIndex;
        }

        if (previousAnchor is not null && TryFindAnchorIndex(previousAnchor.Value, formattedAnchorIndices, out var previousIndex))
        {
            return previousIndex + 1;
        }

        return 0;
    }

    private static bool TryFindAnchorIndex(LineAnchor anchor, IReadOnlyDictionary<string, List<int>> formattedAnchorIndices, out int index)
    {
        if (formattedAnchorIndices.TryGetValue(anchor.Signature, out var indices)
            && anchor.Occurrence > 0
            && anchor.Occurrence <= indices.Count)
        {
            index = indices[anchor.Occurrence - 1];
            return true;
        }

        index = -1;
        return false;
    }

    private static bool IsStandaloneCommentLine(string line)
    {
        return line.TrimStart().StartsWith("//", StringComparison.Ordinal);
    }

    private static string GetLineSignature(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        var diag = new Diagnostics();
        var lexer = new Lexer(line, diag);
        var sb = new StringBuilder();
        while (true)
        {
            var token = lexer.Next();
            if (token.Kind is TokenKind.EOF or TokenKind.Bad)
            {
                break;
            }

            if (sb.Length > 0)
            {
                sb.Append('|');
            }

            sb.Append((int)token.Kind);
            sb.Append(':');
            sb.Append(token.Text);
        }

        return sb.ToString();
    }
}
