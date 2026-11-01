using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop whose successor is a record update (alone, or nested in a record literal) with a field
/// written as a match that conses onto the list it matches out of the loop's state. The match arm's
/// cons is an arena cell holding a duplicate of that list, copied onto the reference-counted heap at
/// the arm's scope, so the duplicate is released after the copy.
/// </summary>
internal sealed class UpdateFieldMatchJoinTemplate() : LeakCombinationTemplate(
    "leak.update-field-match-join", "updateFieldMatchJoin", GeneratedFeature.Record, GeneratedFeature.RecordPattern, GeneratedFeature.TailCall, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string state = names.Of("JoinState");
        string result = names.Of("JoinResult");
        string go = names.Of("goJoin");
        string items = names.Pick("instructions", "entries", "items");
        string next = names.Pick("next", "counter", "label");
        string inner = names.Pick("state", "inner", "current");
        string head = names.Coin("head-plus-one") ? "n + 1" : "n";
        bool nested = names.Coin("nested-in-literal");
        bool matchOuter = nested && names.Coin("match-outer");
        int rounds = names.Between(2, 4);
        string scrutinee = matchOuter ? "result" : "current";
        string pattern = matchOuter
            ? $"{result} {{ {inner} = {state} {{ {items} = is }} }}"
            : $"{state} {{ {items} = is }}";
        string update = $"(current with {items} = (match {scrutinee} with\n                    | {pattern} -> {head} :: is))";
        string successor = nested ? $"{result}({inner} = {update}, error = {(names.Coin("error-some") ? $"Some({names.Word()})" : "None")})" : update;
        string parameter = nested ? $"result: {result}" : $"current: {state}";
        string binding = nested
            ? $"match result with\n        | {result} {{ {inner} = current }} ->\n            if n == 0\n            then result\n            else {go}(n - 1)({successor})"
            : $"if n == 0\n    then current\n    else {go}(n - 1)({successor})";
        string start = $"{state}({items} = [seed], {next} = seed)";
        string argument = nested ? $"{result}({inner} = {start}, error = None)" : start;
        string resultPattern = nested
            ? $"{result} {{ {inner} = {state} {{ {next} = found, {items} = built }} }}"
            : $"{state} {{ {next} = found, {items} = built }}";
        return $$"""
            type {{state}} =
                | {{items}}: List(Int)
                | {{next}}: Int

            type {{result}} =
                | {{inner}}: {{state}}
                | error: Maybe(Str)

            let recursive {{go}} (n: Int) ({{parameter}}) =
                {{binding}}

            let {{names.Entry}} (seed: Int) =
                match {{go}}({{LeakNames.Text(rounds)}})({{argument}}) with
                    | {{resultPattern}} -> found + Ashes.Collection.List.length(built)
            """;
    }
}
