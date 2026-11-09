using System.Text.RegularExpressions;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// The branch-folding loop of stage 1's optimizer: the ints known at the current point and the ints
/// saved at each jump target are loop parameters, a label takes the saved table out of a lookup's
/// result, and a constant or a jump replaces a table with a helper's owned result. Left in the arena,
/// the tables kept every helper result unreleased; the lookup's part now counts as an accumulator
/// arm, so both tables are reference-counted and the back edge releases each predecessor.
/// </summary>
internal sealed partial class CallPartAccumulatorTemplate() : LeakCombinationTemplate(
    "leak.call-part-accumulator", "callPartAccumulator", GeneratedFeature.Match, GeneratedFeature.TailCall)
{
    private static readonly string[] Identifiers =
    [
        "Inst", "ConstInt", "Jump", "JumpIfFalse", "Label", "Other", "lookupAssociation", "setInt", "setSaved",
        "removeAssociation", "reduce", "reduceLoop", "build", "rounds",
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
            | Jump(Str)
            | JumpIfFalse(Int, Str)
            | Label(Str)
            | Other(Int)

        let recursive lookupAssociation (key: Str) (entries: List((Str, List((Int, Int))))) =
            match entries with
                | [] -> None
                | (k, v) :: tail ->
                    if k == key
                    then Some(v)
                    else lookupAssociation(key)(tail)

        let recursive setInt (key: Int) (value: Int) entries =
            match entries with
                | [] -> [(key, value)]
                | (k, v) :: tail ->
                    if k == key
                    then (key, value) :: tail
                    else (k, v) :: setInt(key)(value)(tail)

        let recursive setSaved (key: Str) (value: List((Int, Int))) entries =
            match entries with
                | [] -> [(key, value)]
                | (k, v) :: tail ->
                    if k == key
                    then (key, value) :: tail
                    else (k, v) :: setSaved(key)(value)(tail)

        let recursive removeAssociation (key: Str) (entries: List((Str, List((Int, Int))))) =
            match entries with
                | [] -> []
                | (k, v) :: tail ->
                    if k == key
                    then tail
                    else (k, v) :: removeAssociation(key)(tail)

        let reduce instructions =
            (let recursive reduceLoop insts knownInts savedInts prevTerm acc =
                match insts with
                    | [] -> Ashes.Collection.List.reverse(acc)
                    | inst :: tail ->
                        match inst with
                            | ConstInt(dest, v) ->
                                reduceLoop(tail)(setInt(dest)(v)(knownInts))(savedInts)(false)(inst :: acc)
                            | Label(name) ->
                                let nextInts =
                                    if prevTerm
                                    then
                                        match lookupAssociation(name)(savedInts) with
                                            | Some(si) -> si
                                            | None -> []
                                    else knownInts
                                in
                                    reduceLoop(tail)(nextInts)(removeAssociation(name)(savedInts))(false)(inst :: acc)
                            | Jump(target) ->
                                let nextSaved = setSaved(target)(knownInts)(savedInts)
                                in reduceLoop(tail)(knownInts)(nextSaved)(true)(inst :: acc)
                            | JumpIfFalse(_, target) ->
                                let nextSaved = setSaved(target)(knownInts)(savedInts)
                                in reduceLoop(tail)(knownInts)(nextSaved)(false)(inst :: acc)
                            | _ -> reduceLoop(tail)(knownInts)(savedInts)(false)(inst :: acc)
            in reduceLoop(instructions)([])([])(false)([]))

        let recursive build i acc =
            if i == 0
            then acc
            else
                let l = "L" + Ashes.Text.fromInt(i % 7)
                in build(i - 1)(ConstInt(i)(i * 2) :: JumpIfFalse(i)(l) :: Other(i) :: Jump(l) :: Label(l) :: acc)

        let recursive rounds n total =
            if n == 0
            then total
            else
                rounds(n - 1)(total + Ashes.Collection.List.length([]
                |> build(40)
                |> reduce))

        let ENTRY (seed: Int) = rounds(WIDTH)(seed)
        """;
}
