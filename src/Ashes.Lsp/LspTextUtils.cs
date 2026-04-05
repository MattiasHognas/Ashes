namespace Ashes.Lsp;

internal static class LspTextUtils
{
    internal static int[] GetLineStarts(string text)
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

    internal static (int line, int character) ToLineCharacter(int[] lineStarts, int textLength, int position)
    {
        if (lineStarts.Length == 0)
        {
            return (0, 0);
        }

        var clamped = Math.Clamp(position, 0, textLength);
        var line = Array.BinarySearch(lineStarts, clamped);
        if (line < 0)
        {
            line = ~line - 1;
        }

        var character = clamped - lineStarts[line];
        return (line, character);
    }

    internal static int FromLineCharacter(int[] lineStarts, int textLength, int line, int character)
    {
        if (lineStarts.Length == 0)
        {
            return 0;
        }

        var clampedLine = Math.Clamp(line, 0, lineStarts.Length - 1);
        var lineStart = lineStarts[clampedLine];
        var lineEnd = clampedLine + 1 < lineStarts.Length
            ? lineStarts[clampedLine + 1] - 1
            : textLength;

        var clampedCharacter = Math.Clamp(character, 0, Math.Max(lineEnd - lineStart, 0));
        return Math.Clamp(lineStart + clampedCharacter, 0, textLength);
    }
}
