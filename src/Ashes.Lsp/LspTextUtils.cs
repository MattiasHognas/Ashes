using Ashes.Frontend;

namespace Ashes.Lsp;

internal static class LspTextUtils
{
    internal static SourcePositionEncoding PositionEncoding { get; set; } = SourcePositionEncoding.Utf16;

    internal static SourceTextIndex GetLineStarts(string text) => new(text);

    internal static (int line, int character) ToLineCharacter(
        SourceTextIndex index,
        int textLength,
        int position,
        SourcePositionEncoding? encoding = null)
    {
        _ = textLength;
        return index.ToPosition(position, encoding ?? PositionEncoding);
    }

    internal static int FromLineCharacter(
        SourceTextIndex index,
        int textLength,
        int line,
        int character,
        SourcePositionEncoding? encoding = null)
    {
        _ = textLength;
        return index.FromPosition(line, character, encoding ?? PositionEncoding);
    }
}
