using System.Text;

namespace Ashes.Fuzzing.Shrinking;

/// <summary>
/// The kinds of failure a message reports, with everything case-specific removed so two runs of the
/// same defect compare equal. Shrinking accepts a candidate only when it fails for the reasons the
/// original failed for; without that, a candidate whose simplification introduced an unrelated second
/// defect counts as "still failing" and minimization walks into different territory, leaving a
/// `minimized.ash` that no longer demonstrates the bug being reported.
/// </summary>
internal static class FuzzFailureSignature
{
    /// <summary>
    /// Whether <paramref name="candidateMessage"/> reports only failure kinds that
    /// <paramref name="originalMessage"/> also reported. Losing a kind is fine — a shrink that removes
    /// one cause while keeping another is still a smaller reproduction — but gaining one is not.
    /// </summary>
    internal static bool ReportsOnlyKindsOf(string candidateMessage, string originalMessage)
    {
        IReadOnlySet<string> candidate = Kinds(candidateMessage);
        if (candidate.Count == 0)
        {
            return true;
        }

        IReadOnlySet<string> original = Kinds(originalMessage);
        return candidate.IsSubsetOf(original);
    }

    /// <summary>
    /// The distinct failure kinds in a message. A compile diagnostic's stable code is the kind, which is
    /// what makes one diagnostic compare equal to itself however its message was worded for a particular
    /// case — `Capabilities 'A, B' are not permitted` and `Capability 'A' is not permitted` are one
    /// ASH018, and a shrink that reduces the former to the latter still reproduces. A message carrying no
    /// codes (a native execution or differential failure) falls back to its text with stack frames
    /// dropped and positions, quoted names, and numbers erased.
    /// </summary>
    internal static IReadOnlySet<string> Kinds(string message)
    {
        IReadOnlySet<string> codes = DiagnosticCodes(message);
        if (codes.Count != 0)
        {
            return codes;
        }

        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in message.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("at ", StringComparison.Ordinal))
            {
                continue;
            }

            string normalized = Normalize(trimmed);
            if (normalized.Length != 0)
            {
                kinds.Add(normalized);
            }
        }

        return kinds;
    }

    // Every `ASH####` token in the message. Codes are assigned per defect and never reworded, so they
    // are the most stable kind available.
    private static IReadOnlySet<string> DiagnosticCodes(string message)
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        for (int index = message.IndexOf("ASH", StringComparison.Ordinal); index >= 0;
             index = message.IndexOf("ASH", index + 1, StringComparison.Ordinal))
        {
            int digits = index + 3;
            while (digits < message.Length && char.IsAsciiDigit(message[digits]))
            {
                digits++;
            }

            if (digits > index + 3)
            {
                codes.Add(message[index..digits]);
            }
        }

        return codes;
    }

    private static string Normalize(string line)
    {
        var builder = new StringBuilder(line.Length);
        bool quoted = false;
        bool lastWasPlaceholder = false;
        foreach (char character in line)
        {
            if (character is '\'' or '"')
            {
                // A quoted name is whatever the generator happened to mint, so both halves of the pair
                // collapse to one placeholder rather than keeping the text between them.
                quoted = !quoted;
                AppendPlaceholder(builder, ref lastWasPlaceholder);
                continue;
            }

            if (quoted)
            {
                continue;
            }

            if (char.IsAsciiDigit(character))
            {
                AppendPlaceholder(builder, ref lastWasPlaceholder);
                continue;
            }

            lastWasPlaceholder = false;
            builder.Append(char.IsWhiteSpace(character) ? ' ' : character);
        }

        return builder.ToString().Trim();
    }

    private static void AppendPlaceholder(StringBuilder builder, ref bool lastWasPlaceholder)
    {
        if (!lastWasPlaceholder)
        {
            builder.Append('·');
            lastWasPlaceholder = true;
        }
    }
}
