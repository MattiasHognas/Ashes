using System.Text.RegularExpressions;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A walk over instructions with string labels records snapshots in an association list through
/// <c>Eq</c>-constrained helpers: a lookup and an update, a constrained wrapper over both, and a
/// generic loop over switch cases calling the wrapper. Lowered generically, the lookup's
/// <c>Some</c> and the replaced entries were arena values holding handed-over references that
/// nothing released; each helper is now lowered again at the walk's concrete types.
/// </summary>
internal sealed partial class ConstrainedLoopAtCallerTypesTemplate() : LeakCombinationTemplate(
    "leak.constrained-loop-at-caller-types", "constrainedLoopAtCallerTypes", GeneratedFeature.Record, GeneratedFeature.Match, GeneratedFeature.TailCall)
{
    private static readonly string[] Identifiers =
    [
        "Facts", "Case", "Inst", "Switch", "Goto", "Nop", "lookupAssociation", "setAssociation",
        "recordEdgeSnapshot", "switchCaseLabel", "recordSwitchCaseSnapshots", "walk", "count", "programOf", "loop",
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
                | Some(snapshots) -> setAssociation(target)(Ashes.Collection.List.append(snapshots)([facts]))(saved)
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

        type Inst =
            | Switch(Int, List(Case), Str)
            | Goto(Str)
            | Nop

        let recursive walk (insts: List(Inst)) (n: Int) saved =
            match insts with
                | [] -> saved
                | inst :: rest ->
                    let facts = Facts(ints = [(n, n)], bools = [(n, true)])
                    in
                        match inst with
                            | Switch(_tagTemp, cases, defaultLabel) ->
                                walk(rest)(n + 1)(saved
                                |> recordSwitchCaseSnapshots(cases)(facts)
                                |> recordEdgeSnapshot(defaultLabel)(facts))
                            | Goto(target) -> walk(rest)(n + 1)(recordEdgeSnapshot(target)(facts)(saved))
                            | Nop -> walk(rest)(n + 1)(saved)

        let recursive count (saved: List((Str, List(Facts)))) (acc: Int) =
            match saved with
                | [] -> acc
                | (_, snapshots) :: rest -> count(rest)(acc + Ashes.Collection.List.length(snapshots))

        let programOf (n: Int) = [Goto("a"), Switch(1, [Case(tag = 0, label = "a" + Ashes.Text.fromInt(n % 1)), Case(tag = 1, label = "b"), Case(tag = 2, label = "b")], "c"), Nop, Switch(2, [Case(tag = 0, label = "c"), Case(tag = 1, label = "a")], "a"), Goto("b")]

        let recursive loop (n: Int) (acc: Int) =
            if n == 0
            then acc
            else loop(n - 1)(acc + count(walk(programOf(n))(0)([]))(0))

        let ENTRY (seed: Int) = loop(WIDTH)(seed)
        """;
}
