using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop over a list of lists, matched in a tuple beside a record parameter, that takes the head
/// off the first inner list and conses what is left back onto the outer list's consumed tail
/// (<c>((x :: xs) :: rest, same) -> walk(xs :: rest)(same)</c>). Left in the arena, the rebuilt
/// cell kept the references its parts were retained with for it, and no later iteration released
/// them; placed on the reference-counted heap like a choice accumulator, the successor is copied at
/// the back edge and the list each iteration was given is released.
/// </summary>
internal sealed class ConsOntoConsumedTailTemplate() : LeakCombinationTemplate(
    "leak.cons-onto-consumed-tail", "consOntoConsumedTail", GeneratedFeature.Record, GeneratedFeature.Match, GeneratedFeature.Tuple)
{
    protected override string Declarations(LeakNames names)
    {
        string value = names.Of("LeakKept");
        string walk = names.Of("walk");
        bool text = names.Coin("text");
        string element = text ? "Str" : "Int";
        string headValue = text ? "Ashes.Text.length(x)" : "x";
        string Item(string number) => text ? $"Ashes.Text.fromInt({number})" : number;
        string done = "acc + k + Ashes.Collection.List.length(st)";
        string arms = names.Coin("emptiedArm")
            ? $$"""
                        | ([], {{value}} { state = st, temp = k }) -> {{done}}
                        | ([] :: rest, same) -> {{walk}}(rest)(same)(acc)
                        | ((x :: xs) :: rest, same) -> {{walk}}(xs :: rest)(same)(acc + {{headValue}})
                """
            : $$"""
                        | ((x :: xs) :: rest, same) -> {{walk}}(xs :: rest)(same)(acc + {{headValue}})
                        | (_, {{value}} { state = st, temp = k }) -> {{done}}
                """;
        string first = $"[{Item("seed")}, {Item(LeakNames.Text(names.Between(1, 9)))}, {Item("seed + 1")}]";
        string second = $"[{Item(LeakNames.Text(names.Between(1, 9)))}]";
        return $$"""
            type {{value}} =
                | state: List(Int)
                | temp: Int

            let recursive {{walk}} (items: List(List({{element}}))) (kept: {{value}}) (acc: Int) =
                match (items, kept) with
            {{arms}}

            let {{names.Entry}} (seed: Int) =
                {{walk}}([{{first}}, {{second}}])({{value}}(state = [seed, {{LeakNames.Text(names.Between(1, 9))}}], temp = seed))(seed)
            """;
    }
}
