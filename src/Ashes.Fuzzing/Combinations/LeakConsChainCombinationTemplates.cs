using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop consing a chain of two cells onto its list accumulator (<c>part :: "," :: acc</c>), each
/// part matched out of a list of fresh strings it walks, or those parts joined by
/// <c>Ashes.Text.join</c>, whose interleave pass has the same shape.
/// </summary>
internal sealed class ConsChainAccumulatorTemplate() : LeakCombinationTemplate(
    "leak.cons-chain-accumulator", "consChainAccumulator", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string collect = names.Of("collectParts");
        string interleave = names.Of("interleaveParts");
        bool joined = names.Coin("joined");
        string separator = names.Word();
        string use = joined
            ? $"Ashes.Text.byteLength(Ashes.Text.join({separator})({collect}(seed)([])))"
            : $"Ashes.Collection.List.length({interleave}([])({collect}(seed)([])))";
        return $$"""
            let recursive {{collect}} (n: Int) (acc: List(Str)) =
                if n <= 0
                then acc
                else {{collect}}(n - 1)(Ashes.Text.fromInt(n) :: acc)

            let recursive {{interleave}} (acc: List(Str)) (parts: List(Str)) =
                match parts with
                    | [] -> acc
                    | part :: rest -> {{interleave}}(part :: {{separator}} :: acc)(rest)

            let {{names.Entry}} (seed: Int) = {{use}} + seed
            """;
    }
}
