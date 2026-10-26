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
