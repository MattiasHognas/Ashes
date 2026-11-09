using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A fresh list of strings (the label names a producer copies out of a list of records) handed to a
/// closure the caller cannot name, whose result pairs each name with a number or a foreign handle.
/// The result's type has no place for the list itself but holds its parts, so the call released only
/// the list's spine; the record the result is stored in copies the names, and the spine's reference
/// to every name was never released. The list belongs in an owned slot released with its parts once
/// the function's result holds references of its own; a record holding a foreign handle is a
/// contract value like one of integers, so the slot is released behind it as well.
/// </summary>
internal sealed class HandedOverPartsSlotTemplate() : LeakCombinationTemplate(
    "leak.handed-over-parts-slot", "handedOverPartsSlot", GeneratedFeature.Match, GeneratedFeature.List, GeneratedFeature.Record)
{
    protected override string Declarations(LeakNames names)
    {
        string kind = names.Of("Kind");
        string instr = names.Of("Instr");
        string context = names.Of("Context");
        string handle = names.Of("Handle");
        string handleOf = names.Of("handleOf");
        string collect = names.Of("collectLabelNames");
        string blocks = names.Of("createLabelBlocks");
        string build = names.Of("buildContext");
        string size = names.Of("size");
        string ops = names.Of("ops");
        string prefix = names.Pick("end_", "copy_", "loop_", "done_");
        int count = names.Between(1, 4);
        bool foreignHandle = names.Coin("foreign-handle");
        bool stringFirst = names.Coin("string-first");
        string second = foreignHandle ? handle : "Int";
        string value = foreignHandle ? $"{handleOf}(name)" : "base";
        string tupleType = stringFirst ? $"(Str, {second})" : $"({second}, Str)";
        string pair = stringFirst ? $"(name, {value})" : $"({value}, name)";
        string pattern = stringFirst ? "(_name, b)" : "(b, _name)";
        string counted = foreignHandle ? "n + 1" : "n + b";
        string handleDeclarations = foreignHandle
            ? $"""
              external type {handle}
              external {handleOf}(Str) -> {handle} = "strlen"

              """
            : "";
        return $$"""
            {{handleDeclarations}}type {{kind}} =
                | Label(Str)
                | Other(Int)

            type {{instr}} =
                | instruction: {{kind}}
                | line: Int

            type {{context}} =
                | width: Int
                | labelBlocks: List({{tupleType}})

            let recursive {{collect}} (instructions: List({{instr}})) =
                match instructions with
                    | [] -> []
                    | instruction :: rest ->
                        match instruction with
                            | {{instr}} { instruction = kind } ->
                                match kind with
                                    | Label(name) -> name :: {{collect}}(rest)
                                    | _ -> {{collect}}(rest)

            let recursive {{blocks}} (base: Int) (names: List(Str)) =
                match names with
                    | [] -> []
                    | name :: rest -> {{pair}} :: {{blocks}}(base + 1)(rest)

            let {{build}} (width: Int) consume (instructions: List({{instr}})) =
                (let labelBlocks =
                    instructions
                    |> {{collect}}
                    |> consume
                in
                    let cx = {{context}}(width = width, labelBlocks = labelBlocks)
                    in (cx, instructions))

            let recursive {{size}} (entries: List({{tupleType}})) (n: Int) =
                match entries with
                    | [] -> n
                    | {{pattern}} :: rest -> {{size}}(rest)({{counted}})

            let recursive {{ops}} (seed: Int) (k: Int) (acc: List({{instr}})) =
                if k == 0
                then acc
                else {{ops}}(seed)(k - 1)({{instr}}(instruction = Label("{{prefix}}" + Ashes.Text.fromInt(seed + k)), line = k) :: {{instr}}(instruction = Other(k), line = k) :: acc)

            let {{names.Entry}} (seed: Int) =
                match []
                |> {{ops}}(seed)({{count}})
                |> {{build}}(seed)({{blocks}}(seed)) with
                    | (cx, instructions) -> {{size}}(cx.labelBlocks)(0) + Ashes.Collection.List.length(instructions)
            """;
    }
}
