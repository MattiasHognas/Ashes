using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A record state threaded through a loop by a helper returning <c>(value, nextState)</c>, and
/// returned at the loop's exit beside the reversed, unannotated accumulator in a reference-counted
/// tuple. The tuple normalizes the record parameter into an owned reference; retaining it again as
/// a loop parameter left one reference to the final state per call.
/// </summary>
internal sealed class NormalizedTupleElementRetainedTwiceTemplate() : LeakCombinationTemplate(
    "leak.normalized-tuple-element-retained-twice", "normalizedTupleElement", GeneratedFeature.Record, GeneratedFeature.Tuple, GeneratedFeature.Match, GeneratedFeature.TailCall)
{
    protected override string Declarations(LeakNames names)
    {
        string state = names.Of("LeakThreadState");
        string item = names.Of("LeakItem");
        string one = names.Of("stepOne");
        string loop = names.Of("threadLoop");
        bool strings = names.Coin("stringNames");
        bool records = names.Coin("recordSibling");
        string nameType = strings ? "Str" : "Int";
        string named = strings ? $"Ashes.Text.fromInt(x) + {names.Word()}" : "x * 2";
        string element = records ? $"{item}(value = y, tag = {names.Word()})" : "y";
        string items = names.Ints(names.Between(2, 6), 1, 9);
        return $$"""
            type {{state}} =
                | names: List({{nameType}})
                | counter: Int

            type {{item}} =
                | value: Int
                | tag: Str

            let {{one}} (state: {{state}}) (x: Int) =
                if x % 2 == 0
                then (x + 1, state with names = ({{named}}) :: state.names, counter = state.counter + 1)
                else (x, state)

            let recursive {{loop}} (state: {{state}}) remaining acc =
                match remaining with
                    | [] -> (Ashes.Collection.List.reverse(acc), state)
                    | x :: tail ->
                        match {{one}}(state)(x) with
                            | (y, nextState) -> {{loop}}(nextState)(tail)({{element}} :: acc)

            let {{names.Entry}} (seed: Int) =
                match {{loop}}({{state}}(names = [], counter = seed))(seed :: {{items}})([]) with
                    | (built, _finalState) -> Ashes.Collection.List.length(built) + seed
            """;
    }
}
