using System.Text.RegularExpressions;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A list of switch cases and a facts record are bound with <c>let</c> and passed to a generic
/// <c>Eq</c>-constrained loop that records snapshots in an association list. The loop is lowered
/// again at its caller's types only when the call settles every parameter type, and a let-bound
/// argument settled none: the call stayed a dictionary call, whose lookup's <c>Some</c> and replaced
/// entries are arena values holding references nothing released. A let-bound value of a closed type
/// now settles its parameter as a function's parameter does.
/// </summary>
internal sealed partial class ConstrainedLoopLetBoundArgumentsTemplate() : LeakCombinationTemplate(
    "leak.constrained-loop-let-bound-arguments", "constrainedLoopLetBoundArguments", GeneratedFeature.Record, GeneratedFeature.Match, GeneratedFeature.TailCall)
{
    private static readonly string[] Identifiers =
    [
        "Facts", "Case", "lookupAssociation", "setAssociation", "recordEdgeSnapshot", "switchCaseLabel",
        "recordSwitchCaseSnapshots", "labelOf", "makeCases", "count", "once", "loop",
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
        type Facts =
            | ints: List((Int, Int))
            | bools: List((Int, Bool))

        let recursive lookupAssociation key entries =
            match entries with
                | [] -> None
                | (k, v) :: tail ->
                    if k == key
                    then Some(v)
                    else lookupAssociation(key)(tail)

        let recursive setAssociation key value entries =
            match entries with
                | [] -> [(key, value)]
                | (k, v) :: tail ->
                    if k == key
                    then (key, value) :: tail
                    else (k, v) :: setAssociation(key)(value)(tail)

        let recordEdgeSnapshot target facts saved =
            match lookupAssociation(target)(saved) with
                | Some(snapshots) ->
                    setAssociation(target)(Ashes.Collection.List.append(snapshots)([facts]))(saved)
                | None -> setAssociation(target)([facts])(saved)

        type Case =
            | tag: Int
            | label: Str
            deriving {Eq, Show}

        let switchCaseLabel (switchCase: Case) = switchCase.label

        let recursive recordSwitchCaseSnapshots cases facts saved =
            match cases with
                | [] -> saved
                | switchCase :: tail ->
                    saved
                    |> recordEdgeSnapshot(switchCaseLabel(switchCase))(facts)
                    |> recordSwitchCaseSnapshots(tail)(facts)

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
            else makeCases(n - 1)(Case(tag = n, label = labelOf(n)) :: acc)

        let recursive count (saved: List((Str, List(Facts)))) (acc: Int) =
            match saved with
                | [] -> acc
                | (_, snapshots) :: rest -> count(rest)(acc + Ashes.Collection.List.length(snapshots))

        let once (n: Int) =
            let cases = makeCases(n)([])
            in
                let facts = Facts(ints = [(n, n)], bools = [(n, true)])
                in
                    let saved = recordSwitchCaseSnapshots(cases)(facts)([])
                    in
                        count(saved)(0)

        let recursive loop (n: Int) (acc: Int) =
            if n == 0
            then acc
            else loop(n - 1)(acc + once(n + 4))

        let ENTRY (seed: Int) = loop(WIDTH)(seed)
        """;
}
