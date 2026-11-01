using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop whose successor is a record literal holding a record update of the state it matched out
/// of its parameter, the update consing onto the state's list. The update is an arena cell like the
/// literal, so the back edge's copy retains the list's old tail a second time, and the cell's own
/// retain of it is released through the update's fields.
/// </summary>
internal sealed class RecordUpdateInLiteralSuccessorTemplate() : LeakCombinationTemplate(
    "leak.record-update-in-literal-successor", "updateInLiteralSuccessor", GeneratedFeature.Record, GeneratedFeature.RecordPattern, GeneratedFeature.TailCall, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string state = names.Of("UpdateState");
        string result = names.Of("UpdateResult");
        string go = names.Of("goUpdate");
        string items = names.Pick("instructions", "entries", "items");
        string next = names.Pick("next", "counter", "label");
        string inner = names.Pick("state", "inner", "current");
        bool withNames = names.Coin("names-field");
        bool withExtra = names.Coin("extra-parameter");
        string namesField = withNames ? $"\n    | names: List(Str)" : string.Empty;
        string namesValue = withNames ? $", names = {names.Words(names.Between(1, 3))}" : string.Empty;
        string extraParameter = withExtra ? " (extra: List(Str))" : string.Empty;
        string extraArgument = withExtra ? "(extra)" : string.Empty;
        string extraValue = withExtra ? $"({names.Words(names.Between(1, 2))})" : string.Empty;
        string head = names.Coin("head-plus-one") ? "n + 1" : "n";
        string error = names.Coin("error-some") ? $"Some({names.Word()})" : "None";
        int rounds = names.Between(2, 4);
        return $$"""
            type {{state}} =
                | {{items}}: List(Int){{namesField}}
                | {{next}}: Int

            type {{result}} =
                | {{inner}}: {{state}}
                | error: Maybe(Str)

            let recursive {{go}} (n: Int){{extraParameter}} (result: {{result}}) =
                match result with
                    | {{result}} { {{inner}} = current } ->
                        if n == 0
                        then result
                        else {{go}}(n - 1){{extraArgument}}({{result}}({{inner}} = (current with {{items}} = {{head}} :: current.{{items}}), error = {{error}}))

            let {{names.Entry}} (seed: Int) =
                match {{go}}({{LeakNames.Text(rounds)}}){{extraValue}}({{result}}({{inner}} = {{state}}({{items}} = [seed]{{namesValue}}, {{next}} = seed), error = None)) with
                    | {{result}} { {{inner}} = {{state}} { {{next}} = found, {{items}} = built } } -> found + Ashes.Collection.List.length(built)
            """;
    }
}
