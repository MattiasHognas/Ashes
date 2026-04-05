using Ashes.Lsp;
using Shouldly;

namespace Ashes.Lsp.Tests;

public sealed class LspTextUtilsTests
{
    [Test]
    public void GetLineStarts_should_return_single_zero_for_empty_text()
    {
        var starts = LspTextUtils.GetLineStarts("");

        starts.ShouldBe([0]);
    }

    [Test]
    public void GetLineStarts_should_return_single_zero_for_text_without_newlines()
    {
        var starts = LspTextUtils.GetLineStarts("hello world");

        starts.ShouldBe([0]);
    }

    [Test]
    public void GetLineStarts_should_return_starts_for_multiline_text()
    {
        var starts = LspTextUtils.GetLineStarts("abc\ndef\nghi");

        starts.ShouldBe([0, 4, 8]);
    }

    [Test]
    public void GetLineStarts_should_handle_trailing_newline()
    {
        var starts = LspTextUtils.GetLineStarts("abc\n");

        starts.ShouldBe([0, 4]);
    }

    [Test]
    public void GetLineStarts_should_handle_consecutive_newlines()
    {
        var starts = LspTextUtils.GetLineStarts("\n\n\n");

        starts.ShouldBe([0, 1, 2, 3]);
    }

    [Test]
    public void GetLineStarts_should_handle_only_newline()
    {
        var starts = LspTextUtils.GetLineStarts("\n");

        starts.ShouldBe([0, 1]);
    }

    [Test]
    public void ToLineCharacter_should_return_origin_for_empty_lineStarts()
    {
        var result = LspTextUtils.ToLineCharacter([], 0, 0);

        result.ShouldBe((0, 0));
    }

    [Test]
    public void ToLineCharacter_should_return_first_line_for_position_zero()
    {
        var starts = LspTextUtils.GetLineStarts("abc\ndef");
        var result = LspTextUtils.ToLineCharacter(starts, 7, 0);

        result.ShouldBe((0, 0));
    }

    [Test]
    public void ToLineCharacter_should_return_correct_position_mid_first_line()
    {
        var starts = LspTextUtils.GetLineStarts("abc\ndef");
        var result = LspTextUtils.ToLineCharacter(starts, 7, 2);

        result.ShouldBe((0, 2));
    }

    [Test]
    public void ToLineCharacter_should_return_second_line_for_position_after_newline()
    {
        var starts = LspTextUtils.GetLineStarts("abc\ndef");
        var result = LspTextUtils.ToLineCharacter(starts, 7, 4);

        result.ShouldBe((1, 0));
    }

    [Test]
    public void ToLineCharacter_should_return_second_line_mid_position()
    {
        var starts = LspTextUtils.GetLineStarts("abc\ndef");
        var result = LspTextUtils.ToLineCharacter(starts, 7, 5);

        result.ShouldBe((1, 1));
    }

    [Test]
    public void ToLineCharacter_should_clamp_negative_position_to_zero()
    {
        var starts = LspTextUtils.GetLineStarts("abc\ndef");
        var result = LspTextUtils.ToLineCharacter(starts, 7, -5);

        result.ShouldBe((0, 0));
    }

    [Test]
    public void ToLineCharacter_should_clamp_position_beyond_text_length()
    {
        var starts = LspTextUtils.GetLineStarts("abc\ndef");
        var result = LspTextUtils.ToLineCharacter(starts, 7, 100);

        result.ShouldBe((1, 3));
    }

    [Test]
    public void ToLineCharacter_should_handle_position_at_newline_character()
    {
        var starts = LspTextUtils.GetLineStarts("abc\ndef");
        var result = LspTextUtils.ToLineCharacter(starts, 7, 3);

        result.ShouldBe((0, 3));
    }

    [Test]
    public void FromLineCharacter_should_return_zero_for_empty_lineStarts()
    {
        var result = LspTextUtils.FromLineCharacter([], 0, 0, 0);

        result.ShouldBe(0);
    }

    [Test]
    public void FromLineCharacter_should_return_correct_position_for_first_line()
    {
        var starts = LspTextUtils.GetLineStarts("abc\ndef");
        var result = LspTextUtils.FromLineCharacter(starts, 7, 0, 2);

        result.ShouldBe(2);
    }

    [Test]
    public void FromLineCharacter_should_return_correct_position_for_second_line()
    {
        var starts = LspTextUtils.GetLineStarts("abc\ndef");
        var result = LspTextUtils.FromLineCharacter(starts, 7, 1, 1);

        result.ShouldBe(5);
    }

    [Test]
    public void FromLineCharacter_should_clamp_negative_line_to_zero()
    {
        var starts = LspTextUtils.GetLineStarts("abc\ndef");
        var result = LspTextUtils.FromLineCharacter(starts, 7, -1, 0);

        result.ShouldBe(0);
    }

    [Test]
    public void FromLineCharacter_should_clamp_line_beyond_array_length()
    {
        var starts = LspTextUtils.GetLineStarts("abc\ndef");
        var result = LspTextUtils.FromLineCharacter(starts, 7, 100, 0);

        result.ShouldBe(4);
    }

    [Test]
    public void FromLineCharacter_should_clamp_negative_character_to_zero()
    {
        var starts = LspTextUtils.GetLineStarts("abc\ndef");
        var result = LspTextUtils.FromLineCharacter(starts, 7, 0, -5);

        result.ShouldBe(0);
    }

    [Test]
    public void FromLineCharacter_should_clamp_character_beyond_line_length()
    {
        var starts = LspTextUtils.GetLineStarts("abc\ndef");
        var result = LspTextUtils.FromLineCharacter(starts, 7, 0, 100);

        result.ShouldBe(4);
    }

    [Test]
    public void ToLineCharacter_and_FromLineCharacter_should_round_trip()
    {
        var text = "first\nsecond\nthird";
        var starts = LspTextUtils.GetLineStarts(text);

        for (var pos = 0; pos < text.Length; pos++)
        {
            var (line, character) = LspTextUtils.ToLineCharacter(starts, text.Length, pos);
            var roundTripped = LspTextUtils.FromLineCharacter(starts, text.Length, line, character);
            roundTripped.ShouldBe(pos, $"Round-trip failed for position {pos}");
        }
    }

    [Test]
    public void GetLineStarts_should_handle_crlf_by_counting_lf_only()
    {
        var starts = LspTextUtils.GetLineStarts("ab\r\ncd\r\n");

        starts.ShouldBe([0, 4, 8]);
    }

    [Test]
    public void ToLineCharacter_should_handle_single_line_text()
    {
        var starts = LspTextUtils.GetLineStarts("hello");
        var result = LspTextUtils.ToLineCharacter(starts, 5, 3);

        result.ShouldBe((0, 3));
    }

    [Test]
    public void FromLineCharacter_should_handle_single_line_text()
    {
        var starts = LspTextUtils.GetLineStarts("hello");
        var result = LspTextUtils.FromLineCharacter(starts, 5, 0, 3);

        result.ShouldBe(3);
    }

    [Test]
    public void ToLineCharacter_should_handle_position_at_end_of_text()
    {
        var text = "abc\ndef";
        var starts = LspTextUtils.GetLineStarts(text);
        var result = LspTextUtils.ToLineCharacter(starts, text.Length, text.Length);

        result.ShouldBe((1, 3));
    }

    [Test]
    public void FromLineCharacter_should_return_text_length_for_last_position()
    {
        var text = "abc\ndef";
        var starts = LspTextUtils.GetLineStarts(text);
        var result = LspTextUtils.FromLineCharacter(starts, text.Length, 1, 3);

        result.ShouldBe(7);
    }
}
