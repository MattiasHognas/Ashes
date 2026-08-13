using System.Text;
using Ashes.Frontend;
using Ashes.Lsp;
using Shouldly;

namespace Ashes.Lsp.Tests;

public sealed class LspTextUtilsTests
{
    [Test]
    public void Line_starts_are_canonical_utf8_offsets_for_all_newline_forms()
    {
        SourceTextIndex index = LspTextUtils.GetLineStarts("é\r\n😀\rZ\n");

        index.LineStarts.ShouldBe([0, 4, 9, 11]);
    }

    [Test]
    public void Utf16_position_converts_to_utf8_byte_offset()
    {
        SourceTextIndex index = LspTextUtils.GetLineStarts("a😀b");

        LspTextUtils.FromLineCharacter(index, 4, 0, 3, SourcePositionEncoding.Utf16)
            .ShouldBe(5);
    }

    [Test]
    public void Utf8_position_preserves_utf8_byte_offset()
    {
        SourceTextIndex index = LspTextUtils.GetLineStarts("a😀b");

        LspTextUtils.FromLineCharacter(index, 4, 0, 5, SourcePositionEncoding.Utf8)
            .ShouldBe(5);
    }

    [Test]
    public void Astral_byte_offset_converts_to_utf16_position()
    {
        SourceTextIndex index = LspTextUtils.GetLineStarts("a😀b");

        LspTextUtils.ToLineCharacter(index, 4, 5, SourcePositionEncoding.Utf16)
            .ShouldBe((0, 3));
    }

    [Test]
    public void Combining_marks_are_separate_unicode_scalars()
    {
        SourceTextIndex index = LspTextUtils.GetLineStarts("e\u0301x");

        index.ToPosition(3, SourcePositionEncoding.UnicodeScalar).ShouldBe((0, 2));
    }

    [Test]
    public void CrLf_interior_boundary_maps_to_preceding_line_end()
    {
        SourceTextIndex index = LspTextUtils.GetLineStarts("é\r\nZ");

        index.ToPosition(3, SourcePositionEncoding.Utf8).ShouldBe((0, 2));
        index.ToPosition(3, SourcePositionEncoding.Utf16).ShouldBe((0, 1));
    }

    [Test]
    public void Property_every_representable_boundary_round_trips_for_every_encoding()
    {
        SourceTextIndex index = LspTextUtils.GetLineStarts("é\r\n😀e\u0301\rZ");
        SourcePositionEncoding[] encodings =
        [
            SourcePositionEncoding.Utf8,
            SourcePositionEncoding.Utf16,
            SourcePositionEncoding.UnicodeScalar,
        ];

        for (int utf16 = 0; utf16 <= index.Text.Length; utf16++)
        {
            int byteOffset = index.ToUtf8Offset(utf16);
            if (index.ToUtf16Offset(byteOffset) != utf16)
            {
                continue;
            }

            if (utf16 > 0 && utf16 < index.Text.Length
                && index.Text[utf16 - 1] == '\r' && index.Text[utf16] == '\n')
            {
                continue;
            }

            foreach (SourcePositionEncoding encoding in encodings)
            {
                (int line, int character) = index.ToPosition(byteOffset, encoding);
                index.FromPosition(line, character, encoding).ShouldBe(byteOffset);
            }
        }
    }

    [Test]
    public void Property_positions_are_monotonic_at_scalar_boundaries()
    {
        SourceTextIndex index = LspTextUtils.GetLineStarts("aé😀e\u0301\nZ");
        foreach (SourcePositionEncoding encoding in Enum.GetValues<SourcePositionEncoding>())
        {
            (int Line, int Character) previous = (0, 0);
            for (int utf16 = 0; utf16 <= index.Text.Length; utf16++)
            {
                int offset = index.ToUtf8Offset(utf16);
                if (index.ToUtf16Offset(offset) != utf16)
                {
                    continue;
                }

                (int Line, int Character) current = index.ToPosition(offset, encoding);
                (current.Line > previous.Line
                    || current.Line == previous.Line && current.Character >= previous.Character).ShouldBeTrue();
                previous = current;
            }
        }
    }

    [Test]
    public void Invalid_positions_clamp_to_valid_boundaries()
    {
        SourceTextIndex index = LspTextUtils.GetLineStarts("😀x");

        index.ToUtf16Offset(3).ShouldBe(0);
        index.FromPosition(0, 3, SourcePositionEncoding.Utf8).ShouldBe(0);
        index.FromPosition(99, 99, SourcePositionEncoding.Utf16).ShouldBe(5);
    }

    [Test]
    public void Malformed_utf8_is_rejected()
    {
        Should.Throw<DecoderFallbackException>(() => SourceTextIndex.DecodeUtf8([0xC3, 0x28]));
    }
}
