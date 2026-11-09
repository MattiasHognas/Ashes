using System.Text.RegularExpressions;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop keeps its known table in the arena: it conses onto the table, and at a label takes the
/// table saved for it out of a guarded lookup's result. The join of that part with the table was
/// copied onto the reference-counted heap, which the arena parameter never releases; the part is
/// copied into the arena instead and its own reference released.
/// </summary>
internal sealed partial class ArenaSuccessorJoinTemplate() : LeakCombinationTemplate(
    "leak.arena-successor-join", "arenaSuccessorJoin", GeneratedFeature.Match, GeneratedFeature.TailCall)
{
    private static readonly string[] Identifiers =
    [
        "Inst", "ConstInt", "Label", "lookupSaved", "reduceLoop", "build", "rounds",
    ];

    protected override string Declarations(LeakNames names)
    {
        string program = Program.Replace("ENTRY", names.Entry, StringComparison.Ordinal)
            .Replace("WIDTH", names.Between(2, 6).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
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
        type Inst =
            | ConstInt(Int, Int)
            | Label(Str)

        let recursive lookupSaved (key: Str) (entries: List((Str, List((Int, Int))))) =
            match entries with
                | [] -> None
                | (k, v) :: tail ->
                    if k == key
                    then Some(v)
                    else lookupSaved(key)(tail)

        let recursive reduceLoop insts knownInts savedInts prevTerm =
            match insts with
                | [] -> Ashes.Collection.List.length(knownInts)
                | inst :: tail ->
                    match inst with
                        | ConstInt(dest, v) -> reduceLoop(tail)((dest, v) :: knownInts)(savedInts)(false)
                        | Label(name) ->
                            let nextInts =
                                if prevTerm
                                then
                                    match lookupSaved(name)(savedInts) with
                                        | Some(si) when Ashes.Collection.List.length(si) >= 0 -> si
                                        | _ -> []
                                else knownInts
                            in reduceLoop(tail)(nextInts)(savedInts)(false)

        let recursive build i acc =
            if i == 0
            then acc
            else build(i - 1)(ConstInt(i)(i * 2) :: Label("L") :: acc)

        let recursive rounds n total =
            if n == 0
            then total
            else
                rounds(n - 1)(total + reduceLoop(build(40)([]))([])([])(false))

        let ENTRY (seed: Int) = rounds(WIDTH)(seed)
        """;
}
