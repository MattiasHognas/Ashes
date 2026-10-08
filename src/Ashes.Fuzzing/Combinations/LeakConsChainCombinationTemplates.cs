using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop consing a chain of two cells onto its list accumulator (<c>part :: "," :: acc</c>), each
/// part matched out of a list of fresh strings it walks, or those parts joined by
/// <c>Ashes.Text.join</c>, whose interleave pass has the same shape. A head may read a loop
/// parameter: a separator passed on unchanged (<c>part :: separator :: acc</c>) or the previous
/// part (<c>previous :: part :: acc</c>).
/// </summary>
internal sealed class ConsChainAccumulatorTemplate() : LeakCombinationTemplate(
    "leak.cons-chain-accumulator", "consChainAccumulator", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string collect = names.Of("collectParts");
        string interleave = names.Of("interleaveParts");
        string separator = names.Word();
        int variant = names.Variant("chain", 4);
        string use = variant switch
        {
            0 => $"Ashes.Text.byteLength(Ashes.Text.join({separator})({collect}(seed)([])))",
            1 => $"Ashes.Collection.List.length({interleave}([])({collect}(seed)([])))",
            _ => $"Ashes.Collection.List.length({interleave}({separator})([])({collect}(seed)([])))",
        };
        string loop = variant switch
        {
            2 => $$"""
                let recursive {{interleave}} (separator: Str) (acc: List(Str)) (parts: List(Str)) =
                    match parts with
                        | [] -> acc
                        | part :: rest -> {{interleave}}(separator)(part :: separator :: acc)(rest)
                """,
            3 => $$"""
                let recursive {{interleave}} (previous: Str) (acc: List(Str)) (parts: List(Str)) =
                    match parts with
                        | [] -> acc
                        | part :: rest -> {{interleave}}(part)(previous :: part :: acc)(rest)
                """,
            _ => $$"""
                let recursive {{interleave}} (acc: List(Str)) (parts: List(Str)) =
                    match parts with
                        | [] -> acc
                        | part :: rest -> {{interleave}}(part :: {{separator}} :: acc)(rest)
                """,
        };
        return $$"""
            let recursive {{collect}} (n: Int) (acc: List(Str)) =
                if n <= 0
                then acc
                else {{collect}}(n - 1)(Ashes.Text.fromInt(n) :: acc)

            {{loop}}

            let {{names.Entry}} (seed: Int) = {{use}} + seed
            """;
    }
}
