using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A record literal holding a let-bound list, returned inside a tuple by a generic builder. The arena
/// record borrows the list, so storing it in the tuple retains the list's owner; the result's
/// normalization retains it again, and the first retain is released only from an owned slot once the
/// result holds its own references.
/// </summary>
internal sealed class ArenaHolderOwnerRetainTemplate() : LeakCombinationTemplate(
    "leak.arena-holder-owner-retain", "arenaHolderOwnerRetain", GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string op = names.Of("Op");
        string context = names.Of("Context");
        string collect = names.Of("collect");
        string blocks = names.Of("blocks");
        string build = names.Of("build");
        string size = names.Of("size");
        string label = names.Of("label");
        string ops = names.Of("ops");
        string prefix = names.Pick("end_", "shared_", "loop_", "next_");
        int count = names.Between(1, 4);
        string flagField = names.Coin("flag-field") ? "\n    | isEntry: Bool" : "";
        string flagValue = flagField.Length > 0 ? ", isEntry = isEntry" : "";
        return $$"""
            type {{op}} =
                | Label(Str)
                | Other(Int)

            type {{context}} =
                | width: Int
                | labelBlocks: List((Str, Int)){{flagField}}

            let recursive {{collect}} instructions =
                match instructions with
                    | [] -> []
                    | instruction :: rest ->
                        match instruction with
                            | Label(name) -> name :: {{collect}}(rest)
                            | _ -> {{collect}}(rest)

            let recursive {{blocks}} base names =
                match names with
                    | [] -> []
                    | name :: rest -> (name, base) :: {{blocks}}(base + 1)(rest)

            let {{build}} width isEntry instructions =
                (let labelBlocks =
                    instructions
                    |> {{collect}}
                    |> {{blocks}}(width)
                in
                    let cx = {{context}}(width = width, labelBlocks = labelBlocks{{flagValue}})
                    in (cx, instructions))

            let recursive {{size}} (entries: List((Str, Int))) (n: Int) =
                match entries with
                    | [] -> n
                    | (_name, b) :: rest -> {{size}}(rest)(n + b)

            let {{label}} (n: Int) = "{{prefix}}" + Ashes.Text.fromInt(n)

            let recursive {{ops}} (n: Int) (k: Int) (acc: List({{op}})) =
                if k == 0
                then acc
                else {{ops}}(n)(k - 1)(Label({{label}}(n + k)) :: Other(k) :: acc)

            let {{names.Entry}} (seed: Int) =
                match {{build}}(seed)(seed > 3)({{ops}}(seed)({{count}})([])) with
                    | (cx, _instructions) -> {{size}}(cx.labelBlocks)(0)
            """;
    }
}
