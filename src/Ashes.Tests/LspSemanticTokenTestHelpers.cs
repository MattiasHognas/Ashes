using Shouldly;

namespace Ashes.Tests;

internal static class LspSemanticTokenTestHelpers
{
    public static string ExtractTokenText(string source, int line, int character, int length)
    {
        var lineStarts = GetLineStarts(source);
        line.ShouldBeGreaterThanOrEqualTo(0);
        line.ShouldBeLessThan(lineStarts.Count);
        character.ShouldBeGreaterThanOrEqualTo(0);
        length.ShouldBeGreaterThanOrEqualTo(0);

        var absoluteStart = lineStarts[line] + character;
        absoluteStart.ShouldBeGreaterThanOrEqualTo(0);
        (absoluteStart + length).ShouldBeLessThanOrEqualTo(source.Length);

        return source.Substring(absoluteStart, length);
    }

    public static List<int> GetLineStarts(string source)
    {
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] == '\n')
            {
                lineStarts.Add(i + 1);
            }
        }

        return lineStarts;
    }
}
