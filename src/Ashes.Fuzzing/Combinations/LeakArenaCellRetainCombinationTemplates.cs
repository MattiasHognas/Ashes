using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A let-bound owned list stored into a record of the contract's types that stays in the arena (a
/// sibling field it cannot place on the reference-counted heap) and returned: the retain the cell
/// took goes to an owned slot released once the result is normalized, which takes its own.
/// </summary>
internal sealed class LetBoundValueKeptByArenaRecordResultTemplate() : LeakCombinationTemplate(
    "leak.let-bound-value-kept-by-arena-record-result", "letBoundValueKeptByArenaRecordResult", GeneratedFeature.Record, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string environment = names.Of("LeakEnvironment");
        string holder = names.Of("LeakHolder");
        string numbered = names.Of("numbered");
        string empty = names.Of("emptyEnvironment");
        string prepare = names.Of("prepare");
        return $$"""
            type {{environment}} =
                | label: Str
                | items: List(Str)

            type {{holder}} =
                | ids: List((Str, Int))
                | environment: {{environment}}

            let recursive {{numbered}} (names: List(Str)) (reversed: List((Str, Int))) =
                match names with
                    | [] -> Ashes.Collection.List.reverse(reversed)
                    | name :: rest -> {{numbered}}(rest)((name, Ashes.Collection.List.length(reversed)) :: reversed)

            let {{empty}} (seed: Int) = {{environment}}(label = {{names.Word()}}, items = [])

            let {{prepare}} (names: List(Str)) (seed: Int) =
                (let ids = {{numbered}}(names)([])
                in {{holder}}(ids = ids, environment = {{empty}}(seed)))

            let {{names.Entry}} (seed: Int) =
                match {{prepare}}([{{names.Word()}} + Ashes.Text.fromInt(seed), {{names.Word()}}])(seed) with
                    | {{holder}} { ids = ids, environment = {{environment}} { items = items } } -> Ashes.Collection.List.length(ids) + Ashes.Collection.List.length(items)
            """;
    }
}
