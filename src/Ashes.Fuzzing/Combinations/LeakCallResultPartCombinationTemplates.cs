using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop that hands a list-of-integers parameter a part matched out of a call's result
/// (<c>| Some((a, b)) -> go(b)(a)</c>), and in another arm hands it on unchanged. Left in the
/// arena, the parameter kept the reference the part was retained with for it, and no later
/// iteration released it; placed on the reference-counted heap like a call's result, each back
/// edge releases the list the iteration before was given.
/// </summary>
internal sealed class ScalarListHandedACallResultPartTemplate() : LeakCombinationTemplate(
    "leak.scalar-list-handed-call-result-part", "scalarListCallPart", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.Tuple)
{
    protected override string Declarations(LeakNames names)
    {
        string produce = names.Of("produce");
        string loop = names.Of("handOn");
        bool maybe = names.Coin("maybe");
        string wrapper = maybe ? "Some" : "Ok";
        string miss = maybe ? "None" : "Error(_)";
        string produced = maybe
            ? $"Some(([k, k + {LeakNames.Text(names.Between(1, 9))}], [k * 2]))"
            : $"""
                if k >= 0
                    then Ok(([k, k + {LeakNames.Text(names.Between(1, 9))}], [k * 2]))
                    else Error("negative")
                """;
        string successors = names.Coin("swap") ? "(b)(a)" : "(a)(b)";
        return $$"""
            let {{produce}} (k: Int) =
                {{produced}}

            let recursive {{loop}} (remaining: Int) (first: List(Int)) (second: List(Int)) (total: Int) =
                if remaining == 0
                then total + Ashes.Collection.List.length(first) + Ashes.Collection.List.length(second)
                else
                    match {{produce}}(remaining) with
                        | {{wrapper}}((a, b)) -> {{loop}}(remaining - 1){{successors}}(total + Ashes.Collection.List.length(first))
                        | {{miss}} -> {{loop}}(remaining - 1)(first)(second)(total)

            let {{names.Entry}} (seed: Int) = {{loop}}(seed % {{LeakNames.Text(names.Between(3, 7))}} + 2)([seed])([seed, 1])(seed)
            """;
    }
}
