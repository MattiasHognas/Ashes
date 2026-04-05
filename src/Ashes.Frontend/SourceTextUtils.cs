namespace Ashes.Frontend;

/// <summary>
/// Line/column computation utilities shared between Semantics (IR tagging)
/// and LSP (editor integration). DWARF uses 1-based lines and columns.
/// </summary>
public static class SourceTextUtils
{
    /// <summary>
    /// Returns the character offsets of each line start in <paramref name="text"/>.
    /// The first entry is always 0 (start of line 1).
    /// </summary>
    public static int[] GetLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts.ToArray();
    }

    /// <summary>
    /// Converts an absolute <paramref name="position"/> in source text to a
    /// 1-based (line, column) pair suitable for DWARF debug info.
    /// </summary>
    public static (int Line, int Column) ToLineColumn(int[] lineStarts, int textLength, int position)
    {
        if (lineStarts.Length == 0)
        {
            return (1, 1);
        }

        var clamped = Math.Clamp(position, 0, textLength);
        var line = Array.BinarySearch(lineStarts, clamped);
        if (line < 0)
        {
            line = ~line - 1;
        }

        var column = clamped - lineStarts[line];
        return (line + 1, column + 1); // 1-based for DWARF
    }
}
