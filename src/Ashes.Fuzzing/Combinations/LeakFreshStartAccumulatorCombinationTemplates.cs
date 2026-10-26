using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop whose list parameter one self-call starts afresh with a one-element list
/// (<c>[Some((a, b))]</c> or <c>Ok((a, b)) :: []</c>) built from the parts of a call's fresh result,
/// and another hands on unchanged. Left in the arena, each fresh start kept its reference-counted
/// element past the back edge with nothing to release it; as a choice accumulator the successor
/// is copied and the element its literal built keeps an owner of its own.
/// </summary>
internal sealed class FreshStartBesideHandedOnListTemplate() : LeakCombinationTemplate(
    "leak.fresh-start-beside-handed-on-list", "freshStartBesideHandedOn", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.Tuple)
{
    protected override string Declarations(LeakNames names)
    {
        string produce = names.Of("produce");
        string loop = names.Of("restart");
        bool maybe = names.Coin("maybe");
        string wrapper = maybe ? "Some" : "Ok";
        string element = maybe ? "Maybe((List(Int), List(Int)))" : "Result(Str, (List(Int), List(Int)))";
        string miss = maybe ? "None" : "Error(_)";
        string produced = maybe
            ? $"Some(([k, k + {LeakNames.Text(names.Between(1, 9))}], [k * 2]))"
            : $"""
                if k >= 0
                    then Ok(([k, k + {LeakNames.Text(names.Between(1, 9))}], [k * 2]))
                    else Error("negative")
                """;
        string start = names.Coin("literal") ? $"[{wrapper}((a, b))]" : $"{wrapper}((a, b)) :: []";
        return $$"""
            let {{produce}} (k: Int) =
                {{produced}}

            let recursive {{loop}} (remaining: Int) (acc: List({{element}})) (total: Int) =
                if remaining == 0
                then total + Ashes.Collection.List.length(acc)
                else
                    match {{produce}}(remaining) with
                        | {{wrapper}}((a, b)) -> {{loop}}(remaining - 1)({{start}})(total + Ashes.Collection.List.length(a))
                        | {{miss}} -> {{loop}}(remaining - 1)(acc)(total)

            let {{names.Entry}} (seed: Int) = {{loop}}(seed % {{LeakNames.Text(names.Between(3, 7))}} + 2)([])(seed)
            """;
    }
}
