using System.Text.RegularExpressions;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// Renames the declarations of a fixed program after the case's names, and binds its entry.
/// </summary>
internal static partial class FixedLeakProgram
{
    internal static string Render(LeakNames names, string program, IReadOnlyList<string> identifiers, string entryCall)
    {
        Dictionary<string, string> renamed = new(StringComparer.Ordinal);
        foreach (string identifier in identifiers)
        {
            renamed[identifier] = names.Of(identifier);
        }

        string body = IdentifierPattern().Replace(program, match => renamed.TryGetValue(match.Value, out string? name) ? name : match.Value);
        string entry = IdentifierPattern().Replace(entryCall, match => renamed.TryGetValue(match.Value, out string? name) ? name : match.Value);
        return body + "\n\nlet " + names.Entry + " (seed: Int) = " + entry + "\n";
    }

    [GeneratedRegex(@"\b[A-Za-z]+\b")]
    private static partial Regex IdentifierPattern();
}

/// <summary>
/// A loop rebuilds its accumulator of <c>Maybe(List(Str))</c> elements with a call's result. An
/// element holding a list kept the accumulator in the arena, where nothing released the results it
/// was rebuilt from; it is placed on the reference-counted heap.
/// </summary>
internal sealed class NestedListAccumulatorTemplate() : LeakCombinationTemplate(
    "leak.nested-list-accumulator", "nestedListAccumulator", GeneratedFeature.RecursiveFunction, GeneratedFeature.TailCall, GeneratedFeature.List, GeneratedFeature.Match)
{
    private static readonly string[] Identifiers = ["PipeNode", "PLeaf", "PPick", "PApply", "PLam", "binders", "applied", "derive", "total", "probe", "loop"];

    protected override string Declarations(LeakNames names)
        => FixedLeakProgram.Render(names, Program, Identifiers, "loop(" + names.Between(2, 5).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")(seed)");

    private const string Program = """
        type PipeNode =
            | PLeaf(Str)
            | PPick(PipeNode, List(Str))
            | PApply(PipeNode, List(PipeNode))
            | PLam(Str, PipeNode)

        let recursive binders (acc: List(Str)) (xs: List(Str)) =
            match xs with
                | [] -> acc
                | x :: rest -> binders(x :: acc)(rest)

        let recursive applied (root: PipeNode) (arguments: List(PipeNode)) (ds: List(Maybe(List(Str)))) =
            match (root, arguments) with
                | (PLam(parameter, body), _argument :: rest) -> applied(body)(rest)(Some([parameter]) :: ds)
                | _ -> ds

        let recursive derive (nodes: List(PipeNode)) (ds: List(Maybe(List(Str)))) =
            match nodes with
                | [] -> ds
                | PPick(_value, ns) :: rest -> derive(rest)(Some(binders([])(ns)) :: ds)
                | PApply(root, arguments) :: rest ->
                    ds
                    |> applied(root)(arguments)
                    |> derive(rest)
                | _other :: rest -> derive(rest)(ds)

        let recursive total (k: Int) (ds: List(Maybe(List(Str)))) =
            match ds with
                | [] -> k
                | Some(ns) :: rest -> total(k + Ashes.Collection.List.length(ns))(rest)
                | None :: rest -> total(k)(rest)

        let probe (n: Int) = derive([PPick(PLeaf("ash"))(["coal", Ashes.Text.fromInt(n)]), PApply(PLam("soot")(PLeaf("coal")))([PLeaf("spark")]), PPick(PLeaf(Ashes.Text.fromInt(n)))(["soot"])])([]) |> total(0)

        let recursive loop (n: Int) (acc: Int) =
            if n == 0
            then acc
            else loop(n - 1)(acc + probe(n))
        """;
}

/// <summary>
/// A loop hands a sibling parameter an inner list matched out of an element of its accumulator
/// (<c>Some(si) -&gt; si</c> from a lookup in <c>savedInts</c>) while calls rebuild the accumulator.
/// Kept in the arena, the sibling would hold that list without a reference of its own once the back
/// edge releases the accumulator, so the accumulator stays in the arena with it.
/// </summary>
internal sealed class SiblingReceivesAccumulatorPartTemplate() : LeakCombinationTemplate(
    "leak.sibling-receives-accumulator-part", "siblingReceivesAccumulatorPart", GeneratedFeature.RecursiveFunction, GeneratedFeature.TailCall, GeneratedFeature.List, GeneratedFeature.Match)
{
    private static readonly string[] Identifiers = ["Inst", "ILabel", "IJump", "IJumpIf", "IConst", "INop", "lookupAssociation", "setAssociation", "removeAssociation", "reduce", "program", "loop"];

    protected override string Declarations(LeakNames names)
        => FixedLeakProgram.Render(names, Program, Identifiers, "loop(" + names.Between(2, 5).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")(seed)");

    private const string Program = """
        type Inst =
            | ILabel(Str)
            | IJump(Str)
            | IJumpIf(Str)
            | IConst(Int, Int)
            | INop

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

        let recursive removeAssociation key entries =
            match entries with
                | [] -> []
                | (k, v) :: tail ->
                    if k == key
                    then tail
                    else (k, v) :: removeAssociation(key)(tail)

        let reduce (instructions: List(Inst)) =
            (let recursive reduceLoop insts knownInts savedInts prevTerm acc =
                match insts with
                    | [] -> Ashes.Collection.List.length(acc) + Ashes.Collection.List.length(savedInts)
                    | inst :: tail ->
                        match inst with
                            | IConst(dest, value) ->
                                let nextInts = setAssociation(dest)(value)(knownInts)
                                in reduceLoop(tail)(nextInts)(savedInts)(false)(inst :: acc)
                            | ILabel(name) ->
                                let branchCount = Ashes.Text.byteLength(name) - 1
                                in
                                let nextInts =
                                    if branchCount <= 1
                                    then
                                        if prevTerm
                                        then
                                            match lookupAssociation(name)(savedInts) with
                                                | Some(si) -> si
                                                | None -> []
                                        else
                                            if branchCount == 0
                                            then knownInts
                                            else []
                                    else []
                                in reduceLoop(tail)(nextInts)(removeAssociation(name)(savedInts))(false)(inst :: acc)
                            | IJump(target) ->
                                let nextSaved = setAssociation(target)(knownInts)(savedInts)
                                in reduceLoop(tail)(knownInts)(nextSaved)(true)(inst :: acc)
                            | IJumpIf(target) ->
                                let nextSaved = setAssociation(target)(knownInts)(savedInts)
                                in reduceLoop(tail)(knownInts)(nextSaved)(false)(inst :: acc)
                            | INop -> reduceLoop(tail)(knownInts)(savedInts)(false)(inst :: acc)
            in reduceLoop(instructions)([])([])(false)([]))

        let program (n: Int) =
            [IConst(1, n), IJumpIf("a" + Ashes.Text.fromInt(n % 3)), IConst(2, 5), IJump("b"), ILabel("a0"), IConst(3, 1), IJumpIf("b"), IJump("c"), ILabel("b"), INop, IJump("a1"), ILabel("c"), ILabel("a1")]

        let recursive loop (n: Int) (acc: Int) =
            if n == 0
            then acc
            else loop(n - 1)(acc + reduce(program(n)))
        """;
}
