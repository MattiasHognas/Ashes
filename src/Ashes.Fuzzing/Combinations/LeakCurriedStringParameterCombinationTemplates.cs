using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A curried helper whose last parameter is a string it stores in the aggregate it returns: a tuple
/// (an escaping arena tuple storing an independent copy of the string) or a record (normalized into
/// an owned value at the return). The helper's entry normalizes the string into an owned value that
/// the result does not keep, so it is released behind the result.
/// </summary>
internal sealed class CurriedStringParameterIntoAggregateTemplate() : LeakCombinationTemplate(
    "leak.curried-string-parameter-into-aggregate", "curriedStringParameterIntoAggregate", GeneratedFeature.Record, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string kind = names.Of("LeakKind");
        string token = names.Of("LeakToken");
        string wrap = names.Of("wrap");
        string count = names.Of("count");
        bool record = names.Coin("record");
        string keyType = names.Coin("intKey") ? "Int" : kind;
        string first = string.Equals(keyType, "Int", StringComparison.Ordinal) ? LeakNames.Text(names.Between(1, 9)) : kind + "A";
        string second = string.Equals(keyType, "Int", StringComparison.Ordinal) ? LeakNames.Text(names.Between(1, 9)) : kind + "B";
        string body = record ? $"{token}(kind = k, text = t)" : "(k, t)";
        string text = names.Coin("freshText") ? $"{names.Word()} + Ashes.Text.fromInt(seed)" : names.Word();
        return $$"""
            type {{kind}} =
                | {{kind}}A
                | {{kind}}B

            type {{token}} =
                | kind: {{keyType}}
                | text: Str

            let {{wrap}} (k: {{keyType}}) (t: Str) = {{body}}

            let {{count}} xs =
                match xs with
                    | [] -> 0
                    | _ :: rest -> 1 + Ashes.Collection.List.length(rest)

            let {{names.Entry}} (seed: Int) =
                [{{wrap}}({{first}})({{text}}), {{wrap}}({{second}})({{names.Word()}})]
                |> {{count}}
            """;
    }
}
