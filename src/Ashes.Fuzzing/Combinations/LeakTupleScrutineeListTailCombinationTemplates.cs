using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop matching a tuple of its list parameter and a record parameter, passing the list's tail
/// on: the tail is the list parameter's consumed tail, so each iteration releases the list it was
/// given.
/// </summary>
internal sealed class ListParameterTailMatchedInTupleTemplate() : LeakCombinationTemplate(
    "leak.list-parameter-tail-matched-in-tuple", "listParameterTailMatchedInTuple", GeneratedFeature.Record, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string item = names.Of("LeakClose");
        string value = names.Of("LeakLowered");
        string close = names.Of("close");
        return $$"""
            type {{item}} =
                | span: Int
                | outer: List(Int)

            type {{value}} =
                | state: List(Int)
                | temp: Int

            let recursive {{close}} (pending: List({{item}})) (lowered: {{value}}) =
                match (pending, lowered) with
                    | ([], _lowered) -> lowered
                    | (item :: rest, {{value}} { state = bodyState, temp = temp }) -> {{close}}(rest)({{value}}(state = item.span :: bodyState, temp = temp + item.span))

            let {{names.Entry}} (seed: Int) =
                match {{close}}([{{item}}(span = seed, outer = [seed]), {{item}}(span = {{names.Between(1, 9)}}, outer = []), {{item}}(span = 3, outer = [seed, seed])])({{value}}(state = [], temp = seed)) with
                    | {{value}} { state = st, temp = t } -> t + Ashes.Collection.List.length(st)
            """;
    }
}
