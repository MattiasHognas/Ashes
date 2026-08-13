using System.Buffers;
using System.Text;

namespace Ashes.Frontend;

/// <summary>The coordinate encoding used for a line-relative character value.</summary>
public enum SourcePositionEncoding
{
    /// <summary>UTF-8 bytes.</summary>
    Utf8,
    /// <summary>UTF-16 code units.</summary>
    Utf16,
    /// <summary>Unicode scalar values.</summary>
    UnicodeScalar,
}

/// <summary>
/// Immutable index over valid Unicode source text. Absolute compiler offsets are UTF-8 byte offsets;
/// protocol and debug coordinates are derived at explicit boundaries.
/// </summary>
public sealed class SourceTextIndex
{
    private readonly int[] _utf16ToUtf8;
    private readonly int[] _utf8ToUtf16;
    private readonly int[] _lineStartsUtf8;
    private readonly int[] _lineStartsUtf16;
    private readonly int[] _lineEndsUtf8;
    private readonly int[] _lineEndsUtf16;

    /// <summary>Builds an index for valid Unicode <paramref name="text"/>.</summary>
    public SourceTextIndex(string text)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        _utf16ToUtf8 = new int[text.Length + 1];
        var utf8ToUtf16 = new List<int> { 0 };
        var lineStartsUtf8 = new List<int> { 0 };
        var lineStartsUtf16 = new List<int> { 0 };
        var lineEndsUtf8 = new List<int>();
        var lineEndsUtf16 = new List<int>();
        var utf16 = 0;
        var utf8 = 0;

        while (utf16 < text.Length)
        {
            OperationStatus status = Rune.DecodeFromUtf16(text.AsSpan(utf16), out Rune rune, out int consumed);
            if (status != OperationStatus.Done)
            {
                throw new ArgumentException("Source text contains an unpaired UTF-16 surrogate.", nameof(text));
            }

            int produced = rune.Utf8SequenceLength;
            _utf16ToUtf8[utf16] = utf8;
            if (consumed == 2)
            {
                _utf16ToUtf8[utf16 + 1] = -1;
            }

            for (int i = 1; i < produced; i++)
            {
                utf8ToUtf16.Add(-1);
            }

            utf16 += consumed;
            utf8 += produced;
            _utf16ToUtf8[utf16] = utf8;
            utf8ToUtf16.Add(utf16);

            AddLineBreak(
                rune,
                text,
                ref utf16,
                ref utf8,
                consumed,
                produced,
                _utf16ToUtf8,
                utf8ToUtf16,
                lineStartsUtf8,
                lineStartsUtf16,
                lineEndsUtf8,
                lineEndsUtf16);
        }

        _utf8ToUtf16 = [.. utf8ToUtf16];
        _lineStartsUtf8 = [.. lineStartsUtf8];
        _lineStartsUtf16 = [.. lineStartsUtf16];
        lineEndsUtf8.Add(utf8);
        lineEndsUtf16.Add(utf16);
        _lineEndsUtf8 = [.. lineEndsUtf8];
        _lineEndsUtf16 = [.. lineEndsUtf16];
    }

    private static void AddLineBreak(
        Rune rune,
        string text,
        ref int utf16,
        ref int utf8,
        int consumed,
        int produced,
        int[] utf16ToUtf8,
        List<int> utf8ToUtf16,
        List<int> lineStartsUtf8,
        List<int> lineStartsUtf16,
        List<int> lineEndsUtf8,
        List<int> lineEndsUtf16)
    {
        if (rune.Value is not ('\r' or '\n'))
        {
            return;
        }

        lineEndsUtf8.Add(utf8 - produced);
        lineEndsUtf16.Add(utf16 - consumed);
        if (rune.Value == '\r' && utf16 < text.Length && text[utf16] == '\n')
        {
            utf16ToUtf8[utf16] = utf8;
            utf16++;
            utf8++;
            utf16ToUtf8[utf16] = utf8;
            utf8ToUtf16.Add(utf16);
        }

        lineStartsUtf8.Add(utf8);
        lineStartsUtf16.Add(utf16);
    }

    /// <summary>The indexed source text.</summary>
    public string Text { get; }
    /// <summary>The source length in UTF-8 bytes.</summary>
    public int Utf8Length => _utf8ToUtf16.Length - 1;
    /// <summary>Zero-based UTF-8 byte offsets at which lines begin.</summary>
    public IReadOnlyList<int> LineStarts => _lineStartsUtf8;

    /// <summary>Strictly decodes UTF-8, throwing on malformed input.</summary>
    public static string DecodeUtf8(ReadOnlySpan<byte> bytes)
    {
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    /// <summary>Reads and strictly decodes a UTF-8 source file.</summary>
    public static string ReadUtf8File(string path) => DecodeUtf8(File.ReadAllBytes(path));

    /// <summary>Asynchronously reads and strictly decodes a UTF-8 source file.</summary>
    public static async Task<string> ReadUtf8FileAsync(string path, CancellationToken cancellationToken = default)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return DecodeUtf8(bytes);
    }

    /// <summary>Converts and clamps a UTF-16 string offset to a UTF-8 byte boundary.</summary>
    public int ToUtf8Offset(int utf16Offset)
    {
        int clamped = Math.Clamp(utf16Offset, 0, Text.Length);
        while (clamped > 0 && _utf16ToUtf8[clamped] < 0)
        {
            clamped--;
        }

        return _utf16ToUtf8[clamped];
    }

    /// <summary>Converts and clamps a UTF-8 byte offset to a UTF-16 string boundary.</summary>
    public int ToUtf16Offset(int utf8Offset)
    {
        int clamped = Math.Clamp(utf8Offset, 0, Utf8Length);
        while (clamped > 0 && _utf8ToUtf16[clamped] < 0)
        {
            clamped--;
        }

        return _utf8ToUtf16[clamped];
    }

    /// <summary>Returns whether an offset is an in-range UTF-8 scalar boundary.</summary>
    public bool IsUtf8Boundary(int utf8Offset) =>
        utf8Offset >= 0 && utf8Offset <= Utf8Length && _utf8ToUtf16[utf8Offset] >= 0;

    /// <summary>Converts an exact UTF-8 boundary, throwing when the offset is invalid.</summary>
    public int GetUtf16Offset(int utf8Offset)
    {
        if (!IsUtf8Boundary(utf8Offset))
        {
            throw new ArgumentOutOfRangeException(nameof(utf8Offset), "Offset is not a UTF-8 scalar boundary.");
        }

        return _utf8ToUtf16[utf8Offset];
    }

    /// <summary>Converts a canonical byte offset to a zero-based line and character.</summary>
    public (int Line, int Character) ToPosition(int utf8Offset, SourcePositionEncoding encoding)
    {
        int offset = Math.Clamp(utf8Offset, 0, Utf8Length);
        while (offset > 0 && _utf8ToUtf16[offset] < 0)
        {
            offset--;
        }

        int line = Array.BinarySearch(_lineStartsUtf8, offset);
        if (line < 0)
        {
            line = ~line - 1;
        }

        int lineByteStart = _lineStartsUtf8[line];
        int lineUtf16Start = _lineStartsUtf16[line];
        int coordinateOffset = Math.Min(offset, _lineEndsUtf8[line]);
        int utf16Offset = Math.Min(_utf8ToUtf16[offset], _lineEndsUtf16[line]);
        int character = encoding switch
        {
            SourcePositionEncoding.Utf8 => coordinateOffset - lineByteStart,
            SourcePositionEncoding.Utf16 => utf16Offset - lineUtf16Start,
            SourcePositionEncoding.UnicodeScalar => CountScalars(lineUtf16Start, utf16Offset),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
        };
        return (line, character);
    }

    /// <summary>Converts a zero-based line and character to a canonical byte offset.</summary>
    public int FromPosition(int line, int character, SourcePositionEncoding encoding)
    {
        int lineIndex = Math.Clamp(line, 0, _lineStartsUtf8.Length - 1);
        int lineByteStart = _lineStartsUtf8[lineIndex];
        int lineByteEnd = _lineEndsUtf8[lineIndex];
        int lineUtf16Start = _lineStartsUtf16[lineIndex];
        int lineUtf16End = _lineEndsUtf16[lineIndex];
        int requested = Math.Max(character, 0);

        if (encoding == SourcePositionEncoding.Utf8)
        {
            int result = Math.Min(lineByteStart + requested, lineByteEnd);
            while (result > lineByteStart && _utf8ToUtf16[result] < 0)
            {
                result--;
            }

            return result;
        }

        if (encoding == SourcePositionEncoding.Utf16)
        {
            int utf16 = Math.Min(lineUtf16Start + requested, lineUtf16End);
            return ToUtf8Offset(utf16);
        }

        int scalarCount = 0;
        int cursor = lineUtf16Start;
        while (cursor < lineUtf16End && scalarCount < requested)
        {
            OperationStatus status = Rune.DecodeFromUtf16(Text.AsSpan(cursor), out _, out int consumed);
            if (status != OperationStatus.Done)
            {
                break;
            }

            cursor += consumed;
            scalarCount++;
        }

        return ToUtf8Offset(cursor);
    }

    private int CountScalars(int utf16Start, int utf16End)
    {
        int count = 0;
        int cursor = utf16Start;
        while (cursor < utf16End)
        {
            Rune.DecodeFromUtf16(Text.AsSpan(cursor), out _, out int consumed);
            cursor += consumed;
            count++;
        }

        return count;
    }
}
