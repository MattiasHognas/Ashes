using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop binds a record's name and its list of uses, matches the list, and under a condition skips
/// the name. The release of the matched list's head binding splits the edges out of the
/// accumulator's release loop first, and the name's release must reach those split edges too, or
/// every skipped name leaks. The loop's types are written out or left to inference.
/// </summary>
internal sealed class OwnerReleasedOnEarlierSplitEdgeTemplate() : LeakCombinationTemplate(
    "leak.owner-released-on-earlier-split-edge", "splitEdgeOwner", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string fact = names.Of("Fact");
        string build = names.Of("withFacts");
        string keep = names.Of("addKnown");
        string total = names.Of("totalKnown");
        string word = names.Word();
        string parameters = names.Coin("annotated")
            ? $$"""(facts: List({{fact}})) (known: List((Str, Int)))"""
            : "facts known";
        string emptyArm = names.Coin("keep-empty")
            ? $$"""{{keep}}(rest)((name, 0) :: known)"""
            : $$"""{{keep}}(rest)(known)""";
        return $$"""
            type {{fact}} =
                | name: Str
                | uses: List(Int)

            let recursive {{build}} items acc =
                match items with
                    | [] -> acc
                    | item :: rest -> {{build}}(rest)({{fact}}(name = {{word}} + Ashes.Text.fromInt(item), uses = [item, item + 1]) :: acc)

            let recursive {{keep}} {{parameters}} =
                match facts with
                    | [] -> known
                    | {{fact}} { name = name, uses = uses } :: rest ->
                        match uses with
                            | first :: _ ->
                                if first > 1
                                then {{keep}}(rest)((name, first) :: known)
                                else {{keep}}(rest)(known)
                            | [] -> {{emptyArm}}

            let recursive {{total}} (entries: List((Str, Int))) (sum: Int) =
                match entries with
                    | [] -> sum
                    | (_, first) :: rest -> {{total}}(rest)(sum + first)

            let {{names.Entry}} (seed: Int) = {{total}}({{keep}}({{build}}([1, 2, seed + 3])([]))([]))(0) + seed
            """;
    }
}
