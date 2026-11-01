using System.Runtime.CompilerServices;
using System.Text;
using Shouldly;

namespace Ashes.Lsp.Tests;

// Hovers every word of each fixture under fixtures/hover and compares the result with the
// checked-in `<fixture>.hover.txt`. A mismatch writes `<fixture>.hover.received.txt` beside the
// fixture; set ASHES_ACCEPT_HOVER=1 to overwrite the expected files instead.
public sealed class LspHoverSweepTests
{
    [Test]
    [Arguments("basics")]
    [Arguments("traits")]
    [Arguments("capabilities")]
    [Arguments("async")]
    [Arguments("inline_module_import")]
    [Arguments("inline_module_qualified")]
    [Arguments("externals")]
    public async Task Hover_on_every_word_matches_the_expected_sweep(string fixture)
    {
        string source = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "hover", fixture + ".ash"));
        DocumentService.Analyze(source).ShouldBeEmpty($"fixture '{fixture}' must compile cleanly");

        IReadOnlyList<(int Line, int Column, int Offset, string Word)> words = CollectWords(source);
        IReadOnlyList<DocumentService.HoverItem?> hovers = DocumentService.GetHovers(source, [.. words.Select(word => word.Offset)]);

        var report = new StringBuilder();
        for (int index = 0; index < words.Count; index++)
        {
            (int line, int column, _, string word) = words[index];
            report.Append(System.Globalization.CultureInfo.InvariantCulture, $"{line}:{column} {word} -> {Render(source, hovers[index])}\n");
        }

        string actual = report.ToString();
        string directory = SourceFixtureDirectory();
        string expectedPath = Path.Combine(directory, fixture + ".hover.txt");
        if (string.Equals(Environment.GetEnvironmentVariable("ASHES_ACCEPT_HOVER"), "1", StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(expectedPath, actual);
            return;
        }

        string expected = File.Exists(expectedPath) ? await File.ReadAllTextAsync(expectedPath) : string.Empty;
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(Path.Combine(directory, fixture + ".hover.received.txt"), actual);
        }

        actual.ShouldBe(expected, $"hover sweep of '{fixture}' changed; see {fixture}.hover.received.txt");
    }

    // One position per identifier, keyword, or literal: the first character of each run of word
    // characters, outside comments. The sources are ASCII, so a character index is a byte offset.
    private static List<(int Line, int Column, int Offset, string Word)> CollectWords(string source)
    {
        var words = new List<(int Line, int Column, int Offset, string Word)>();
        string[] lines = source.Split('\n');
        int lineStart = 0;
        for (int line = 0; line < lines.Length; line++)
        {
            string text = lines[line];
            int comment = text.IndexOf("//", StringComparison.Ordinal);
            int end = comment < 0 ? text.Length : comment;
            int column = 0;
            while (column < end)
            {
                if (!IsWordCharacter(text[column]))
                {
                    column++;
                    continue;
                }

                int wordEnd = column;
                while (wordEnd < end && IsWordCharacter(text[wordEnd]))
                {
                    wordEnd++;
                }

                words.Add((line + 1, column, lineStart + column, text[column..wordEnd]));
                column = wordEnd;
            }

            lineStart += text.Length + 1;
        }

        return words;
    }

    private static bool IsWordCharacter(char character) => char.IsLetterOrDigit(character) || character == '_';

    // The hovered range's text and the hover's signature and kind; linked standard-library
    // documentation is left out so a documentation edit does not change the sweep.
    private static string Render(string source, DocumentService.HoverItem? hover)
    {
        if (hover is not { } item)
        {
            return "-";
        }

        string markdown = item.Contents;
        int documentation = markdown.IndexOf("\n\n---\n\n", StringComparison.Ordinal);
        if (documentation >= 0)
        {
            markdown = markdown[..documentation];
        }

        string body = markdown
            .Replace("```ashes\n", string.Empty, StringComparison.Ordinal)
            .Replace("\n```", string.Empty, StringComparison.Ordinal)
            .Replace("\n\n", " | ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        string range = source[item.Start..item.End].Replace("\n", " ", StringComparison.Ordinal);
        if (range.Length > 40)
        {
            range = range[..40] + "...";
        }

        return $"`{range}` {body}";
    }

    private static string SourceFixtureDirectory([CallerFilePath] string testFile = "") =>
        Path.Combine(Path.GetDirectoryName(testFile)!, "fixtures", "hover");
}
