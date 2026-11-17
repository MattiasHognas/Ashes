using System.Text.RegularExpressions;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A generic loop that records one association per switch case through an <c>Eq</c>-constrained helper
/// is called with a literal and a call result as arguments. The loop captures the helper, so it has no
/// label of its own, and the discovery pass recorded call-site types only for labelled functions: with
/// no variable to carry the argument types either, the call settled nothing and stayed a dictionary
/// call, which keeps each case's label alive behind the association list. A call to a let-bound generic
/// function now records its parameter types too.
/// </summary>
internal sealed partial class ConstrainedLoopLiteralArgumentsTemplate() : LeakCombinationTemplate(
    "leak.constrained-loop-literal-arguments", "constrainedLoopLiteralArguments", GeneratedFeature.Record, GeneratedFeature.Match, GeneratedFeature.TailCall)
{
    private static readonly string[] Identifiers =
    [
        "Case", "setAssociation", "switchCaseLabel", "recordSwitchCases", "labelOf", "makeCases", "total",
        "once", "loop",
    ];

    protected override string Declarations(LeakNames names)
    {
        string program = Program.Replace("ENTRY", names.Entry, StringComparison.Ordinal)
            .Replace("WIDTH", names.Between(3, 9).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Dictionary<string, string> renamed = new(StringComparer.Ordinal);
        foreach (string identifier in Identifiers)
        {
            renamed[identifier] = names.Of(identifier);
        }

        return IdentifierPattern().Replace(program, match => renamed.TryGetValue(match.Value, out string? name) ? name : match.Value);
    }

    [GeneratedRegex(@"\b[A-Za-z]+\b")]
    private static partial Regex IdentifierPattern();

    private const string Program = """
        let recursive setAssociation key value entries =
            match entries with
                | [] -> [(key, value)]
                | (k, v) :: tail ->
                    if k == key
                    then (key, value) :: tail
                    else (k, v) :: setAssociation(key)(value)(tail)

        type Case =
            | tag: Int
            | label: Str

        let switchCaseLabel (switchCase: Case) = switchCase.label

        let recursive recordSwitchCases cases facts saved =
            match cases with
                | [] -> saved
                | switchCase :: tail ->
                    recordSwitchCases(tail)(facts)(setAssociation(switchCaseLabel(switchCase))(facts)(saved))

        let labelOf (n: Int) =
            if n % 3 == 0
            then "a"
            else
                if n % 3 == 1
                then "b"
                else "c"

        let recursive makeCases (n: Int) (acc: List(Case)) =
            if n == 0
            then acc
            else makeCases(n - 1)(Case(tag = n, label = labelOf(n) + "") :: acc)

        let recursive total (saved: List((Str, Int))) (acc: Int) =
            match saved with
                | [] -> acc
                | (_, value) :: rest -> total(rest)(acc + value)

        let once (n: Int) = total(recordSwitchCases(makeCases(n)([]))(7)([]))(0)

        let recursive loop (n: Int) (acc: Int) =
            if n == 0
            then acc
            else loop(n - 1)(acc + once(n + 4))

        let ENTRY (seed: Int) = loop(WIDTH)(seed)
        """;
}
