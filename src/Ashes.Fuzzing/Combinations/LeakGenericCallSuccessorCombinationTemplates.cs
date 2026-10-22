using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop walking its list parameter's consumed tail hands the head to a generic call (an
/// association update compared through <c>Eq</c>) whose arena result, the other parameter's
/// successor, keeps the head. The old list is released at the back edge only after that successor
/// holds its own references: released at the self call, it frees the head the successor names.
/// </summary>
internal sealed class ConsumedHeadInGenericCallSuccessorTemplate() : LeakCombinationTemplate(
    "leak.consumed-head-in-generic-call-successor", "consumedHeadGenericSuccessor", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string set = names.Of("setAssociation");
        string add = names.Of("addCases");
        string total = names.Of("totalCounts");
        string word = names.Word();
        string pipeline = names.Coin("piped-update")
            ? $$"""
                entries
                            |> {{set}}(label)({{LeakNames.Text(names.Between(1, 9))}})
                            |> {{add}}(rest)
                """
            : $$"""{{add}}(rest)({{set}}(label)({{LeakNames.Text(names.Between(1, 9))}})(entries))""";
        return $$"""
            let recursive {{set}} key value entries =
                match entries with
                    | [] -> [(key, value)]
                    | (k, v) :: tail ->
                        if k == key
                        then (key, value) :: tail
                        else (k, v) :: {{set}}(key)(value)(tail)

            let recursive {{add}} (labels: List(Str)) entries =
                match labels with
                    | [] -> entries
                    | label :: rest ->
                        {{pipeline}}

            let recursive {{total}} (entries: List((Str, Int))) (sum: Int) =
                match entries with
                    | [] -> sum
                    | (_, count) :: rest -> {{total}}(rest)(sum + count)

            let {{names.Entry}} (seed: Int) = {{total}}({{add}}([{{word}}, {{word}} + Ashes.Text.fromInt(seed), "a", {{word}}])([]))(0) + seed
            """;
    }
}
