using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop whose accumulator one self-call starts afresh with a cell of its own
/// (<c>go(first :: [])(rest)</c>) while the others grow it by one cell or a chain
/// (<c>go(first :: "," :: acc)(rest)</c>), the interleave pass of the standard library's
/// <c>Text.join</c> as it was first written. The fresh start used to make the accumulator's shape
/// mixed, so it stayed in the arena and every head matched out of the walked list was retained
/// into an arena cell nothing released. A restart that drops a non-empty accumulator has its
/// predecessor released at the back edge.
/// </summary>
internal sealed class AccumulatorStartedAfreshTemplate() : LeakCombinationTemplate(
    "leak.accumulator-started-afresh", "accumulatorStartedAfresh", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string collect = names.Of("collectParts");
        string count = names.Of("countParts");
        string loop = names.Of("startAfresh");
        string separator = names.Word();
        string arms = names.Variant("start", 3) switch
        {
            0 => $$"""
                            match acc with
                                | [] -> {{loop}}(first :: [])(rest)
                                | _ -> {{loop}}(first :: acc)(rest)
                """,
            1 => $$"""
                            match acc with
                                | [] -> {{loop}}(first :: [])(rest)
                                | _ -> {{loop}}(first :: {{separator}} :: acc)(rest)
                """,
            _ => $$"""
                            if Ashes.Text.byteLength(first) > 1
                            then {{loop}}(first :: {{separator}} :: [])(rest)
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
