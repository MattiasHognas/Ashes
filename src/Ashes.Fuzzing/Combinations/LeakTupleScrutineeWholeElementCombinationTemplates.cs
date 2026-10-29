using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop matching a tuple of a list and a record parameter that binds the record whole at its own
/// position and passes it back unchanged: the read out of the reference-counted tuple already
/// retains the record, so the self call hands it on without a same-parameter transfer of its own.
/// </summary>
internal sealed class WholeTupleElementHandedBackTemplate() : LeakCombinationTemplate(
    "leak.whole-tuple-element-handed-back", "wholeTupleElementHandedBack", GeneratedFeature.Record, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string value = names.Of("LeakKept");
        string close = names.Of("close");
        return $$"""
            type {{value}} =
                | state: List(Int)
                | temp: Int

            let recursive {{close}} (pending: List(Int)) (kept: {{value}}) (acc: Int) =
                match (pending, kept) with
                    | ([], {{value}} { state = st, temp = t }) -> acc + t + Ashes.Collection.List.length(st)
                    | (n :: rest, same) -> {{close}}(rest)(same)(acc + n)

            let {{names.Entry}} (seed: Int) =
                {{close}}([seed, {{names.Between(1, 9)}}, 3, seed])({{value}}(state = [seed, {{names.Between(1, 9)}}], temp = seed))(seed)
            """;
    }
}
