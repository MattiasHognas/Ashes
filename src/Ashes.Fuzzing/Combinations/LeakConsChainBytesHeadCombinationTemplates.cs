using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop consing a chain of three byte buffers onto its list accumulator, the middle one matched
/// out of the list it walks or a parameter passed on unchanged, the other two fresh. The middle
/// cell takes a reference to its head of its own, so it is reference-counted like the cells around
/// it and the accumulator is never an arena cell over reference-counted ones.
/// </summary>
internal sealed class ConsChainBorrowedBytesHeadTemplate() : LeakCombinationTemplate(
    "leak.cons-chain-borrowed-bytes-head", "consChainBorrowedBytesHead", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string sources = names.Of("sources");
        string pieces = names.Of("pieces");
        bool parameterHead = names.Coin("parameter-head");
        string middle = parameterHead ? "marker" : "payload";
        return $$"""
            let recursive {{sources}} (n: Int) (acc: List(Bytes)) =
                if n <= 0
                then acc
                else {{sources}}(n - 1)(Ashes.Byte.allocate(n) :: acc)

            let recursive {{pieces}} (marker: Bytes) (items: List(Bytes)) (acc: List(Bytes)) =
                match items with
                    | [] -> Ashes.Collection.List.reverse(acc)
                    | payload :: rest -> {{pieces}}(marker)(rest)(Ashes.Byte.allocate(Ashes.Byte.length(payload)) :: {{middle}} :: Ashes.Byte.allocate(3) :: acc)

            let {{names.Entry}} (seed: Int) = Ashes.Collection.List.length({{pieces}}(Ashes.Byte.allocate(1))({{sources}}(seed % 5 + 3)([]))([])) + seed
            """;
    }
}
