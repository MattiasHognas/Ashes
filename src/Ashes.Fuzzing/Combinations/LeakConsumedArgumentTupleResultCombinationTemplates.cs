using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A fresh list of fresh strings passed to a function whose tuple result keeps one of its heads:
/// the result is made owned before the list goes, so the list is released with all its heads.
/// </summary>
internal sealed class ConsumedArgumentHeadInTupleResultTemplate() : LeakCombinationTemplate(
    "leak.consumed-argument-head-in-tuple-result", "consumedArgumentHeadInTupleResult", GeneratedFeature.Tuple, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string split = names.Of("splitQualified");
        string parts = names.Of("qualifiedParts");
        bool keepsRest = names.Coin("keeps-rest");
        string resultArm = keepsRest ? "(name, rest)" : $"({names.Word()}, name)";
        string emptyArm = keepsRest ? "(\"\", [])" : "(\"\", \"\")";
        string measure = keepsRest
            ? "Ashes.Text.byteLength(a) + Ashes.Collection.List.length(b)"
            : "Ashes.Text.byteLength(a) + Ashes.Text.byteLength(b)";
        return $$"""
            let {{split}} (items: List(Str)) =
                match items with
                    | [] -> {{emptyArm}}
                    | name :: rest -> {{resultArm}}

            let {{parts}} (seed: Int) = [Ashes.Text.fromInt(seed), {{names.Word()}} + Ashes.Text.fromInt(seed + 1), Ashes.Text.fromInt(seed + 2)]

            let {{names.Entry}} (seed: Int) =
                match {{split}}({{parts}}(seed)) with
                    | (a, b) -> {{measure}}
            """;
    }
}
