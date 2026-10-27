using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A mutually recursive pair lowered as closures (stage 1 always, stage 0 when a non-shared
/// parameter has no default and the group is not merged into one dispatch loop): one member conses
/// onto its accumulator and tail-calls its sibling, whose exit returns a tuple. The fused sibling
/// call has no window after it, so the fresh cell handed across it is never released.
/// </summary>
internal sealed class FusedSiblingTailFreshArgumentTemplate() : LeakCombinationTemplate(
    "leak.fused-sibling-tail-fresh-argument", "fusedSiblingTail", GeneratedFeature.MutualRecursion, GeneratedFeature.Tuple, GeneratedFeature.TailCall)
{
    protected override string Declarations(LeakNames names)
    {
        string count = names.Of("countItems");
        string step = names.Of("stepItem");
        int length = names.Between(2, 6);
        string items = "[" + string.Join(", ", Enumerable.Range(1, length).Select(LeakNames.Text)) + "]";
        string word = names.Word();
        bool strings = names.Coin("strings");
        string elementType = strings ? "Str" : "Int";
        string pushed = strings ? $"Ashes.Text.fromInt(value) + {word}" : "value";
        return $$"""
            let recursive {{count}} (acc: List({{elementType}})) (state: (List(Int), Str)) =
                match state with
                    | ([], _source) -> (Ashes.Collection.List.reverse(acc), state)
                    | (x :: rest, source) -> {{step}}(acc)((rest, source))(Some(x))
            and {{step}} acc state (x: Maybe(Int)) =
                match x with
                    | Some(value) -> {{count}}({{pushed}} :: acc)(state)
                    | None -> {{count}}(acc)(state)

            let {{names.Entry}} (seed: Int) =
                match {{count}}([])(({{items}}, {{word}} + Ashes.Text.fromInt(seed))) with
                    | (found, _) -> Ashes.Collection.List.length(found) + seed
            """;
    }
}
