using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop whose accumulator is started afresh with a list literal (<c>go([first])(rest)</c>) beside
/// self-calls growing it, or chosen inside a single self-call's argument
/// (<c>go(match acc with | [] -&gt; [first] | _ -&gt; first :: acc)(rest)</c>). A literal start made the
/// accumulator's shape mixed, and the cells a chosen argument built for a successor the back edge
/// copies retained heads matched out of the walked list that nothing released.
/// </summary>
internal sealed class AccumulatorResetByLiteralOrChoiceTemplate() : LeakCombinationTemplate(
    "leak.accumulator-reset-by-literal-or-choice", "accumulatorResetLiteral", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string collect = names.Of("collectParts");
        string count = names.Of("countParts");
        string loop = names.Of("startWithLiteral");
        string word = names.Word();
        string arms = names.Variant("start", 3) switch
        {
            0 => $$"""
                            match acc with
                                | [] -> {{loop}}([first])(rest)
                                | _ -> {{loop}}(first :: acc)(rest)
                """,
            1 => $$"""
                            {{loop}}(match acc with
                                | [] -> [first]
                                | _ -> first :: acc)(rest)
                """,
            _ => $$"""
                            if Ashes.Text.byteLength(first) > 1
                            then {{loop}}([first, {{word}}])(rest)
                            else {{loop}}(first :: acc)(rest)
                """,
        };
        return $$"""
            let recursive {{collect}} (n: Int) (acc: List(Str)) =
                if n <= 0
                then acc
                else {{collect}}(n - 1)(Ashes.Text.fromInt(n * 7) :: acc)

            let recursive {{count}} (items: List(Str)) (total: Int) =
                match items with
                    | [] -> total
                    | head :: rest -> {{count}}(rest)(total + Ashes.Text.byteLength(head))

            let recursive {{loop}} (acc: List(Str)) (ps: List(Str)) =
                match ps with
                    | [] -> acc
                    | first :: rest ->
            {{arms}}

            let {{names.Entry}} (seed: Int) = {{count}}({{loop}}([])({{collect}}(seed + 2)([])))(0) + seed
            """;
    }
}
