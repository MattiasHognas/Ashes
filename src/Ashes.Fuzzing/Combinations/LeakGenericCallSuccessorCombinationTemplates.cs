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

/// <summary>
/// A loop's accumulator, handed on unchanged in one arm, is replaced in another by a generic call's
/// result built over an owned value of the iteration (a helper's result). Left in the arena, the
/// call's result shares the owned value's cells, which the back edge must not release under it.
/// </summary>
internal sealed class GenericCallSuccessorOverOwnedValueTemplate() : LeakCombinationTemplate(
    "leak.generic-call-successor-over-owned-value", "genericSuccessorOwnedValue", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string set = names.Of("setAssociation");
        string add = names.Of("addCases");
        string walk = names.Of("walkGroups");
        string total = names.Of("totalCounts");
        string word = names.Word();
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
                    | label :: rest -> {{add}}(rest)({{set}}(label)(1)(entries))

            let recursive {{walk}} (groups: List(Maybe(List(Str)))) entries =
                match groups with
                    | [] -> entries
                    | Some(labels) :: rest ->
                        let added = {{add}}(labels)(entries)
                        in {{walk}}(rest)({{set}}({{word}})({{LeakNames.Text(names.Between(1, 9))}})(added))
                    | None :: rest -> {{walk}}(rest)(entries)

            let recursive {{total}} (entries: List((Str, Int))) (sum: Int) =
                match entries with
                    | [] -> sum
                    | (_, count) :: rest -> {{total}}(rest)(sum + count)

            let {{names.Entry}} (seed: Int) = {{total}}({{walk}}([Some(["a", {{word}} + Ashes.Text.fromInt(seed)]), None, Some([])])([]))(0) + seed
            """;
    }
}
