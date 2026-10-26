using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop over a list of lists that puts a fresh head (or a chain of them) back onto what is left of
/// the first inner list (<c>((x :: xs) :: rest, same) -> walk((x - 1 :: xs) :: rest)(same)</c>). The
/// back edge copies the arena inner cells with the outer one and shares the counted inner tail with
/// a reference of its own; an inner cell that took a reference to that tail kept it, and no later
/// iteration released it.
/// </summary>
internal sealed class FreshInnerHeadInSuccessorTemplate() : LeakCombinationTemplate(
    "leak.fresh-inner-head-in-successor", "freshInnerHeadInSuccessor", GeneratedFeature.Record, GeneratedFeature.Match, GeneratedFeature.Tuple)
{
    protected override string Declarations(LeakNames names)
    {
        string value = names.Of("LeakKept");
        string walk = names.Of("walk");
        string inner = names.Coin("chain") ? "x - 1 :: 0 :: xs" : "x - 1 :: xs";
        string first = $"[{LeakNames.Text(names.Between(2, 4))}, {LeakNames.Text(names.Between(1, 9))}, seed]";
        string second = $"[{LeakNames.Text(names.Between(1, 9))}, seed + 1]";
        return $$"""
            type {{value}} =
                | state: List(Int)
                | temp: Int

            let recursive {{walk}} (items: List(List(Int))) (kept: {{value}}) (acc: Int) =
                match (items, kept) with
                    | ((x :: xs) :: rest, same) ->
                        if x > 0
                        then {{walk}}(({{inner}}) :: rest)(same)(acc + x)
                        else {{walk}}(xs :: rest)(same)(acc)
                    | (_, {{value}} { state = st, temp = k }) -> acc + k + Ashes.Collection.List.length(st)

            let {{names.Entry}} (seed: Int) =
                {{walk}}([{{first}}, {{second}}])({{value}}(state = [seed, {{LeakNames.Text(names.Between(1, 9))}}], temp = seed))(seed)
            """;
    }
}

/// <summary>
/// A loop matching its parameters as a tuple that hands one of them back to its own position
/// through the tuple's binder (<c>(all, Kept { .. }) -> walk(all)(..)</c>). Once a sibling puts the
/// tuple on the reference-counted heap, the binder is the tuple's counted copy of the parameter: a
/// list parameter left in the arena took that copy over with a reference nothing released.
/// </summary>
internal sealed class TupleBinderHandedBackTemplate() : LeakCombinationTemplate(
    "leak.tuple-binder-handed-back", "tupleBinderHandedBack", GeneratedFeature.Record, GeneratedFeature.Match, GeneratedFeature.Tuple)
{
    protected override string Declarations(LeakNames names)
    {
        string value = names.Of("LeakKept");
        string walk = names.Of("walk");
        if (names.Coin("consedTail"))
        {
            return $$"""
                let recursive {{walk}} (items: List(List(Int))) (kept: List(Int)) (acc: Int) =
                    match (items, kept) with
                        | ((x :: xs) :: rest, same) -> {{walk}}(xs :: rest)(same)(acc + x)
                        | (_, k) -> acc + Ashes.Collection.List.length(k)

                let {{names.Entry}} (seed: Int) =
                    {{walk}}([[seed, {{LeakNames.Text(names.Between(1, 9))}}], [seed + 1]])([seed, {{LeakNames.Text(names.Between(1, 9))}}])(seed)
                """;
        }

        return $$"""
            type {{value}} =
                | state: List(Int)
                | temp: Int

            let recursive {{walk}} (items: List(Int)) (kept: {{value}}) (acc: Int) =
                match (items, kept) with
                    | (all, {{value}} { state = st, temp = t }) ->
                        if t > 0
                        then {{walk}}(all)({{value}}(state = st, temp = t - 1))(acc + t)
                        else acc + Ashes.Collection.List.length(all) + Ashes.Collection.List.length(st)

            let {{names.Entry}} (seed: Int) =
                {{walk}}([seed, {{LeakNames.Text(names.Between(1, 9))}}, 3])({{value}}(state = [seed, 7], temp = {{LeakNames.Text(names.Between(2, 5))}}))(seed)
            """;
    }
}

/// <summary>
/// A loop that grows a list held in a record parameter by a cons written as the field's new value
/// (<c>walk(rest)(state with labels = name :: state.labels)</c>). The back edge normalizes the arena
/// record by copying it, string heads copied outright and other parts retained by the copy; the
/// references the arena cell took for its head and tail were never released.
/// </summary>
internal sealed class RecordFieldConsTemplate() : LeakCombinationTemplate(
    "leak.record-field-cons", "recordFieldCons", GeneratedFeature.Record, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string item = names.Of("LeakItem");
        string state = names.Of("LeakState");
        string build = names.Of("build");
        string walk = names.Of("walk");
        bool adt = names.Coin("adtElement");
        string element = adt ? item : "Str";
        string fieldValue = names.Coin("tailField") ? "name :: rest" : "name :: state.seen";
        string successor = names.Coin("countField")
            ? $"state with seen = {fieldValue}, count = state.count + 1"
            : $"state with seen = {fieldValue}";
        string first = adt ? $"{item}(\"coal\" + Ashes.Text.fromInt(n))" : "\"coal\" + Ashes.Text.fromInt(n)";
        string second = adt ? $"{item}(\"ember{LeakNames.Text(names.Between(1, 9))}\")" : $"\"ember\" + Ashes.Text.fromInt(n + {LeakNames.Text(names.Between(1, 9))})";
        return $$"""
            type {{item}} =
                | {{item}}(Str)

            type {{state}} =
                | seen: List({{element}})
                | count: Int

            let recursive {{walk}} (items: List({{element}})) (state: {{state}}) =
                match items with
                    | [] -> Ashes.Collection.List.length(state.seen) + state.count
                    | name :: rest -> {{walk}}(rest)({{successor}})

            let {{build}} (n: Int) = [{{first}}, {{second}}]

            let {{names.Entry}} (seed: Int) = {{walk}}({{build}}(seed))({{state}}(seen = [], count = seed))
            """;
    }
}
