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

/// <summary>
/// A loop's accumulator replaced in every arm by a generic call's result (an association update
/// compared through <c>Eq</c>), one arm's key a name a pattern binds out of the loop's list
/// parameter. The reference the key takes for the call's arena result is released once the
/// successor holds its own references; left with the arena result, it leaks each iteration's key.
/// </summary>
internal sealed class PatternOwnerHandedToConstrainedCallTemplate() : LeakCombinationTemplate(
    "leak.pattern-owner-handed-to-constrained-call", "patternOwnerConstrainedCall", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string set = names.Of("setAssociation");
        string loop = names.Of("collectGroups");
        string total = names.Of("totalCounts");
        string word = names.Word();
        string value = LeakNames.Text(names.Between(1, 9));
        string someArm = names.Coin("piped-update")
            ? $$"""
                acc
                            |> {{set}}(label)({{value}})
                            |> {{loop}}(tail)
                """
            : $$"""{{loop}}(tail)({{set}}(label)({{value}})(acc))""";
        return $$"""
            let recursive {{set}} key value entries =
                match entries with
                    | [] -> [(key, value)]
                    | (k, v) :: rest ->
                        if k == key
                        then (key, value) :: rest
                        else (k, v) :: {{set}}(key)(value)(rest)

            let recursive {{loop}} (groups: List(Maybe(Str))) acc =
                match groups with
                    | [] -> acc
                    | Some(label) :: tail ->
                        {{someArm}}
                    | None :: tail -> {{loop}}(tail)({{set}}({{word}})(2)(acc))

            let recursive {{total}} (entries: List((Str, Int))) (sum: Int) =
                match entries with
                    | [] -> sum
                    | (_, count) :: rest -> {{total}}(rest)(sum + count)

            let {{names.Entry}} (seed: Int) = {{total}}({{loop}}([Some("a"), Some({{word}} + Ashes.Text.fromInt(seed)), None, Some("a"), Some("c")])([]))(0) + seed
            """;
    }
}

/// <summary>
/// A loop over instructions hands a list a pattern binds out of its list parameter to a nested
/// constrained helper (it compares labels through <c>Eq</c>), then replaces its accumulator with a
/// generic call's result over the helper's. The helper's result keeps the list's strings, never the
/// list; the reference the list takes for the call is released once the successor holds its own.
/// </summary>
internal sealed class NestedConstrainedHelperListArgumentTemplate() : LeakCombinationTemplate(
    "leak.nested-constrained-helper-list-argument", "nestedConstrainedHelperList", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string kind = names.Of("SwitchKind");
        string switchTag = names.Of("SwitchTag");
        string other = names.Of("OtherKind");
        string set = names.Of("setAssociation");
        string count = names.Of("countRefs");
        string total = names.Of("totalCounts");
        string word = names.Word();
        string value = LeakNames.Text(names.Between(1, 9));
        return $$"""
            type {{kind}} =
                | {{switchTag}}(List(Str), Str)
                | {{other}}(Int)

            let recursive {{set}} key value entries =
                match entries with
                    | [] -> [(key, value)]
                    | (k, v) :: rest ->
                        if k == key
                        then (key, value) :: rest
                        else (k, v) :: {{set}}(key)(value)(rest)

            let recursive {{count}} instructions acc =
                match instructions with
                    | [] -> acc
                    | inst :: tail ->
                        match inst with
                            | {{switchTag}}(cases, defaultLabel) ->
                                let recursive addCases cs entries =
                                    match cs with
                                        | [] -> entries
                                        | l :: cTail -> addCases(cTail)({{set}}(l)({{value}})(entries))
                                in
                                    let mapWithCases = addCases(cases)(acc)
                                    in {{count}}(tail)({{set}}(defaultLabel)(1)(mapWithCases))
                            | _ -> {{count}}(tail)(acc)

            let recursive {{total}} (entries: List((Str, Int))) (sum: Int) =
                match entries with
                    | [] -> sum
                    | (_, n) :: rest -> {{total}}(rest)(sum + n)

            let {{names.Entry}} (seed: Int) = {{total}}({{count}}([{{switchTag}}([{{word}}, {{word}} + Ashes.Text.fromInt(seed)])("c"), {{other}}(1), {{switchTag}}([])({{word}})])([]))(0) + seed
            """;
    }
}
