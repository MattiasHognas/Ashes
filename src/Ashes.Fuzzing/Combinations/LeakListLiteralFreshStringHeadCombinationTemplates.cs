using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A list literal of strings a call just returned, passed to a builder whose result keeps them in
/// a record inside a tuple. The literal's arena cell holds the only reference the call handed back,
/// and an arena cell releases nothing, so that reference goes to an owned slot.
/// </summary>
internal sealed class ListLiteralFreshStringHeadTemplate() : LeakCombinationTemplate(
    "leak.list-literal-fresh-string-head", "listLiteralFreshStringHead", GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string context = names.Of("Context");
        string blocks = names.Of("blocks");
        string build = names.Of("build");
        string size = names.Of("size");
        string label = names.Of("label");
        string prefix = names.Pick("end_", "shared_", "loop_", "next_");
        int count = names.Between(1, 4);
        string items = "[" + string.Join(", ", Enumerable.Range(0, count).Select(index => $"{label}(seed + {index})")) + "]";
        string other = names.Coin("int-part") ? "width" : "instructions";
        return $$"""
            type {{context}} =
                | width: Int
                | labelBlocks: List((Str, Int))

            let recursive {{blocks}} (base: Int) (names: List(Str)) =
                match names with
                    | [] -> []
                    | name :: rest -> (name, base) :: {{blocks}}(base + 1)(rest)

            let {{build}} (width: Int) (names: List(Str)) (instructions: List(Int)) =
                (let labelBlocks = {{blocks}}(width)(names)
                in
                    let cx = {{context}}(width = width, labelBlocks = labelBlocks)
                    in (cx, {{other}}))

            let recursive {{size}} (entries: List((Str, Int))) (n: Int) =
                match entries with
                    | [] -> n
                    | (_name, b) :: rest -> {{size}}(rest)(n + b)

            let {{label}} (n: Int) = "{{prefix}}" + Ashes.Text.fromInt(n)

            let {{names.Entry}} (seed: Int) =
                match {{build}}(seed)({{items}})([seed]) with
                    | (cx, _other) -> {{size}}(cx.labelBlocks)(0)
            """;
    }
}
