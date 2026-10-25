using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop pushing a fresh value over a value matched out of the list it walks
/// (<c>fresh :: item :: acc</c>), of byte buffers, records holding one, or pairs with one. The inner
/// cell takes its own reference to the matched head and is reference-counted, so the fresh cell
/// over it is too and the caller releases the whole list.
/// </summary>
internal sealed class FreshHeadOverNamedHeadTemplate() : LeakCombinationTemplate(
    "leak.fresh-head-over-named-head", "freshHeadOverNamedHead", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string box = names.Of("LeakBox");
        string sources = names.Of("leakSources");
        string build = names.Of("leakBuild");
        string size = LeakNames.Text(names.Between(1, 9));
        (string element, string make, string fresh) = names.Variant("element", 3) switch
        {
            0 => ("Bytes", "Ashes.Byte.allocate(n)", $"Ashes.Byte.allocate({size})"),
            1 => (box, $"{box}(leakBoxed = Ashes.Byte.allocate(n))", $"{box}(leakBoxed = Ashes.Byte.allocate({size}))"),
            _ => ("(Int, Bytes)", "(n, Ashes.Byte.allocate(n))", $"({size}, Ashes.Byte.allocate({size}))"),
        };
        return $$"""
            type {{box}} =
                | leakBoxed: Bytes

            let recursive {{sources}} (n: Int) (acc: List({{element}})) =
                if n <= 0
                then acc
                else {{sources}}(n - 1)({{make}} :: acc)

            let recursive {{build}} (items: List({{element}})) (acc: List({{element}})) =
                match items with
                    | [] -> acc
                    | item :: rest -> {{build}}(rest)({{fresh}} :: item :: acc)

            let {{names.Entry}} (seed: Int) = Ashes.Collection.List.length({{build}}({{sources}}(seed % 5 + 3)([]))([])) + seed
            """;
    }
}
