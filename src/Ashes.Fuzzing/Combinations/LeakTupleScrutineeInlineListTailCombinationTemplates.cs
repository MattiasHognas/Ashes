using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop matching a tuple of a list of integers and a record parameter, passing the list's tail
/// on: the tuple is built on the reference-counted heap beside the record, so the list of inline
/// elements takes the consumed-tail placement too and each iteration releases the list it was given.
/// </summary>
internal sealed class InlineListParameterTailMatchedInTupleTemplate() : LeakCombinationTemplate(
    "leak.inline-list-parameter-tail-matched-in-tuple", "inlineListParameterTailMatchedInTuple", GeneratedFeature.Record, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string value = names.Of("LeakLowered");
        string close = names.Of("close");
        return $$"""
            type {{value}} =
                | state: List(Int)
                | temp: Int

            let recursive {{close}} (pending: List(Int)) (lowered: {{value}}) =
                match (pending, lowered) with
                    | ([], _lowered) -> lowered
                    | (n :: rest, {{value}} { state = bodyState, temp = temp }) -> {{close}}(rest)({{value}}(state = bodyState, temp = temp + n))

            let {{names.Entry}} (seed: Int) =
                match {{close}}([seed, {{names.Between(1, 9)}}, 3, seed, {{names.Between(1, 9)}}])({{value}}(state = [], temp = seed)) with
                    | {{value}} { state = st, temp = t } -> t + Ashes.Collection.List.length(st)
            """;
    }
}
