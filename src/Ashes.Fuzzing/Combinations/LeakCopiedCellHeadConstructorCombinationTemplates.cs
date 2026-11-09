using System.Text.RegularExpressions;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// The devirtualizing rewrite of stage 1's optimizer: a label matched out of a lookup's owned result goes
/// into an instruction record written as the head of the cell consed onto the accumulator. The
/// back edge copies the cell with references of its own, so the retains the record's fields took
/// are released after the copy.
/// </summary>
internal sealed partial class CopiedCellHeadConstructorTemplate() : LeakCombinationTemplate(
    "leak.copied-cell-head-constructor", "copiedCellHeadConstructor", GeneratedFeature.Match, GeneratedFeature.TailCall)
{
    private static readonly string[] Identifiers =
    [
        "Kind", "CallClosure", "CallKnown", "LoadMem", "Other", "Inst", "lookupKind", "lookupLabel", "devirtualize", "name", "program", "defs", "labels", "labelBytes", "loop",
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
        type Kind =
            | CallClosure(Int, Int)
            | CallKnown(Int, Str)
            | LoadMem(Int, Int)
            | Other(Int)

        type Inst =
            | instruction: Kind
            | location: Int

        let recursive lookupKind (key: Int) (entries: List((Int, Kind))) =
            match entries with
                | [] -> None
                | (k, v) :: tail ->
                    if k == key
                    then Some(v)
                    else lookupKind(key)(tail)

        let recursive lookupLabel (key: Str) (entries: List((Str, Str))) =
            match entries with
                | [] -> None
                | (k, v) :: tail ->
                    if k == key
                    then Some(v)
                    else lookupLabel(key)(tail)

        let recursive devirtualize insts singleDefs knownLabels nextTemp acc changed =
            match insts with
                | [] -> (Ashes.Collection.List.Ashes.Collection.List.reverse(acc), nextTemp, changed)
                | (Inst { instruction = CallClosure(dest, closureTemp), location = loc } as inst) :: tail ->
                    match lookupKind(closureTemp)(singleDefs) with
                        | Some(CallKnown(_, calleeLabel)) ->
                            match lookupLabel(calleeLabel)(knownLabels) with
                                | Some(label) -> devirtualize(tail)(singleDefs)(knownLabels)(nextTemp + 1)(Inst(instruction = CallKnown(dest)(label), location = loc) :: Inst(instruction = LoadMem(nextTemp)(closureTemp), location = loc) :: acc)(true)
                                | None -> devirtualize(tail)(singleDefs)(knownLabels)(nextTemp)(inst :: acc)(changed)
                        | _ -> devirtualize(tail)(singleDefs)(knownLabels)(nextTemp)(inst :: acc)(changed)
                | head :: tail -> devirtualize(tail)(singleDefs)(knownLabels)(nextTemp)(head :: acc)(changed)

        let name (prefix: Str) (n: Int) = prefix + Ashes.Text.Ashes.Text.fromInt(n)

        let recursive program (n: Int) acc =
            if n == 0
            then acc
            else
                program(n - 1)(Inst(instruction = n
                |> name("f")
                |> CallKnown(n * 2), location = n) :: Inst(instruction = CallClosure(n * 2 + 1)(n * 2), location = n) :: acc)

        let recursive defs (n: Int) acc =
            if n == 0
            then acc
            else
                defs(n - 1)((n * 2, n
                |> name("f")
                |> CallKnown(n * 2)) :: acc)

        let recursive labels (n: Int) acc =
            if n == 0
            then acc
            else labels(n - 1)((name("f")(n), name("lambda_")(n)) :: acc)

        let recursive labelBytes (insts: List(Inst)) (total: Int) =
            match insts with
                | [] -> total
                | Inst { instruction = CallKnown(_, label) } :: rest -> labelBytes(rest)(total + Ashes.Text.byteLength(label))
                | _ :: rest -> labelBytes(rest)(total)

        let recursive loop (n: Int) (acc: Int) =
            if n == 0
            then acc
            else
                match devirtualize(program(6)([]))(defs(6)([]))(labels(6)([]))(100)([])(false) with
                    | (rewritten, _, _) -> loop(n - 1)(acc + labelBytes(rewritten)(0))

        let ENTRY (seed: Int) = loop(WIDTH)(seed)
        """;
}
