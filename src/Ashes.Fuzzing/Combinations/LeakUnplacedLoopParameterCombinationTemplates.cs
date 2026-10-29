using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop whose record parameter's type is unresolved at loop entry, passing a record update of it
/// to a call: the unchanged fields' retain markers become real retains once the parameter is placed
/// on the reference-counted heap at a back edge, and are released after the call.
/// </summary>
internal sealed class RecordUpdateOfUnplacedLoopParameterTemplate() : LeakCombinationTemplate(
    "leak.record-update-of-unplaced-loop-parameter", "recordUpdateOfUnplacedLoopParameter", GeneratedFeature.Record, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string group = names.Of("LeakGroup");
        string state = names.Of("LeakState");
        string item = names.Of("LeakItem");
        string at = names.Of("LeakAt");
        string plain = names.Of("LeakPlain");
        string named = names.Of("LeakNamed");
        string withSpan = names.Of("withSpan");
        string addName = names.Of("addName");
        string finish = names.Of("finish");
        string walk = names.Of("walk");
        return $$"""
            type {{group}} =
                | span: Maybe(Int)
                | names: List(Str)

            type {{state}} =
                | labels: List(Str)
                | currentSpan: Maybe(Int)
                | inner: {{group}}

            type {{item}} =
                | {{at}}(Int, {{item}})
                | {{plain}}
                | {{named}}(Str)

            let {{withSpan}} value (state: {{state}}) =
                (let current = state.inner
                in state with inner = (current with span = value))

            let {{addName}} (name: Str) (state: {{state}}) = state with labels = Ashes.Collection.List.append(state.labels)([name])

            let {{finish}} (base: Int) (state: {{state}}) = base + Ashes.Collection.List.length(state.labels)

            let recursive {{walk}} items base state =
                match items with
                    | [] -> state |> {{finish}}(base)
                    | {{at}}(span, inner) :: rest ->
                        (state with currentSpan = Some(span))
                        |> {{withSpan}}(Some(span))
                        |> {{walk}}(inner :: rest)(base)
                    | {{plain}} :: rest -> {{walk}}(rest)(base)(state)
                    | {{named}}(name) :: rest ->
                        state
                        |> {{addName}}(name)
                        |> {{walk}}(rest)(base)

            let {{names.Entry}} (seed: Int) =
                {{walk}}([{{at}}({{names.Between(1, 9)}}, {{named}}({{names.Word()}} + Ashes.Text.fromInt(seed))), {{at}}(2, {{plain}}), {{named}}({{names.Word()}})])(0)({{state}}(labels = [{{names.Word()}} + Ashes.Text.fromInt(seed)], currentSpan = None, inner = {{group}}(span = None, names = [])))
            """;
    }
}
