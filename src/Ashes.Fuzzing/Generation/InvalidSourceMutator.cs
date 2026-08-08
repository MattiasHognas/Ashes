namespace Ashes.Fuzzing.Generation;

internal enum InvalidSourceMutation
{
    TokenDeletion,
    TokenDuplication,
    DelimiterChange,
    KeywordInsertion,
    Truncation,
    MalformedLiteral,
    IndentationChange,
    UnicodeInsertion,
}

internal sealed class InvalidSourceMutator
{
    internal static int MutationCount => Enum.GetValues<InvalidSourceMutation>().Length;

    internal string Mutate(string source, ulong seed)
    {
        FuzzRandom random = new(seed);
        InvalidSourceMutation mutation = (InvalidSourceMutation)random.Next(MutationCount);
        return Mutate(source, random, mutation);
    }

    internal string Mutate(string source, ulong seed, InvalidSourceMutation mutation) =>
        Mutate(source, new FuzzRandom(seed), mutation);

    private static string Mutate(string source, FuzzRandom random, InvalidSourceMutation mutation)
    {
        if (source.Length == 0)
        {
            return "\u0000";
        }

        IReadOnlyList<TokenSpan> tokens = FindTokens(source);
        TokenSpan token = tokens[random.Next(tokens.Count)];
        int position = random.Next(source.Length);
        return mutation switch
        {
            InvalidSourceMutation.TokenDeletion => source.Remove(token.Start, token.Length),
            InvalidSourceMutation.TokenDuplication => source.Insert(token.Start, source.Substring(token.Start, token.Length)),
            InvalidSourceMutation.DelimiterChange => ChangeDelimiter(source, random),
            InvalidSourceMutation.KeywordInsertion => source.Insert(position, "match "),
            InvalidSourceMutation.Truncation => source[..position],
            InvalidSourceMutation.MalformedLiteral => source.Insert(position, "\"\\u{"),
            InvalidSourceMutation.IndentationChange => ChangeIndentation(source, random),
            InvalidSourceMutation.UnicodeInsertion => source.Insert(position, "\u2603\u0301"),
            _ => throw new InvalidOperationException($"Unknown invalid-source mutation '{mutation}'."),
        };
    }

    private static string ChangeDelimiter(string source, FuzzRandom random)
    {
        int[] positions = source.Select((character, index) => (character, index))
            .Where(pair => pair.character is '(' or ')' or '[' or ']' or '{' or '}')
            .Select(pair => pair.index)
            .ToArray();
        if (positions.Length == 0)
        {
            return source.Insert(random.Next(source.Length), ")]}");
        }

        int position = positions[random.Next(positions.Length)];
        char replacement = source[position] switch
        {
            '(' => ']',
            ')' => '[',
            '[' => '}',
            ']' => '{',
            '{' => ')',
            _ => '(',
        };
        return source.Remove(position, 1).Insert(position, replacement.ToString());
    }

    private static string ChangeIndentation(string source, FuzzRandom random)
    {
        int[] newlines = source.Select((character, index) => (character, index))
            .Where(pair => pair.character == '\n')
            .Select(pair => pair.index)
            .ToArray();
        return newlines.Length == 0
            ? "    " + source
            : source.Insert(newlines[random.Next(newlines.Length)] + 1, "        ");
    }

    private static IReadOnlyList<TokenSpan> FindTokens(string source)
    {
        var tokens = new List<TokenSpan>();
        int start = 0;
        while (start < source.Length)
        {
            if (char.IsWhiteSpace(source[start]))
            {
                start++;
                continue;
            }

            int end = start + 1;
            if (char.IsLetterOrDigit(source[start]) || source[start] == '_')
            {
                while (end < source.Length && (char.IsLetterOrDigit(source[end]) || source[end] == '_'))
                {
                    end++;
                }
            }
            tokens.Add(new TokenSpan(start, end - start));
            start = end;
        }

        return tokens.Count == 0 ? [new TokenSpan(0, 1)] : tokens;
    }

    private sealed record TokenSpan(int Start, int Length);
}
