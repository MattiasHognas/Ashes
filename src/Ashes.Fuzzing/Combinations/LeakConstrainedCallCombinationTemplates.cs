using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A call's owned result passed to a callee that takes a trait dictionary (a generic lookup
/// comparing keys with <c>==</c>) and only borrows it: the caller's reference goes to an owned
/// slot and is released, as it is for an ordinary callee.
/// </summary>
internal sealed class ConstrainedCallFreshArgumentTemplate() : LeakCombinationTemplate(
    "leak.constrained-call-fresh-argument", "constrainedCallFreshArgument", GeneratedFeature.Record, GeneratedFeature.Match, GeneratedFeature.Call)
{
    protected override string Declarations(LeakNames names)
    {
        string record = names.Of("LeakFn");
        string label = names.Of("functionLabel");
        string lookup = names.Of("lookupAssociation");
        string word = names.Word();
        return $$"""
            type {{record}} =
                | label: Str
                | size: Int

            let {{label}} (fn: {{record}}) = fn.label

            let recursive {{lookup}} key entries =
                match entries with
                    | [] -> None
                    | (k, v) :: tail ->
                        if k == key
                        then Some(v)
                        else {{lookup}}(key)(tail)

            let {{names.Entry}} (seed: Int) =
                match {{lookup}}({{label}}({{record}}(label = {{word}} + Ashes.Text.fromInt(seed), size = seed)))([({{word}} + Ashes.Text.fromInt({{names.Between(1, 9)}}), seed)]) with
                    | Some(found) -> found
                    | None -> seed + 1
            """;
    }
}

/// <summary>
/// A string a call returns compared with <c>==</c> or <c>!=</c> straight away (the parser's
/// <c>parserCurrentText(state) == "out"</c>): the primitive comparison borrows it, and the caller
/// releases it after the comparison.
/// </summary>
internal sealed class StringCallResultComparedTemplate() : LeakCombinationTemplate(
    "leak.string-call-result-compared", "stringCallResultCompared", GeneratedFeature.Record, GeneratedFeature.Comparison, GeneratedFeature.Call)
{
    protected override string Declarations(LeakNames names)
    {
        string token = names.Of("LeakToken");
        string current = names.Of("currentToken");
        string text = names.Of("currentText");
        string word = names.Word();
        string comparison = names.Coin("negated") ? "!=" : "==";
        return $$"""
            type {{token}} =
                | kind: Int
                | text: Str

            let {{current}} (tokens: List({{token}})) =
                match tokens with
                    | first :: _ -> first
                    | [] -> {{token}}(kind = 0, text = "")

            let {{text}} (tokens: List({{token}})) =
                (let found = {{current}}(tokens)
                in found.text)

            let {{names.Entry}} (seed: Int) =
                (let tokens = [{{token}}(kind = seed, text = {{word}} + Ashes.Text.fromInt(seed))]
                in
                    if {{text}}(tokens) {{comparison}} {{word}}
                    then seed
                    else seed + {{names.Between(1, 9)}})
            """;
    }
}
