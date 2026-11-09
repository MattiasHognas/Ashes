using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop follows a chain of labels and returns <c>Some</c> of its own string parameter at its exit,
/// and its caller reads the found string and drops the result. The arena <c>Some</c> took its own
/// reference to the parameter, which nothing released; the own-field record releases it at the end
/// of the caller's match on the result.
/// </summary>
internal sealed class OwnFieldRecordTemplate() : LeakCombinationTemplate(
    "leak.own-field-record", "ownField", GeneratedFeature.RecursiveFunction, GeneratedFeature.TailCall, GeneratedFeature.List, GeneratedFeature.Adt)
{
    protected override string Declarations(LeakNames names)
    {
        string lookup = names.Of("lookupNext");
        string stage = names.Of("innermostStage");
        string measure = names.Of("stageLength");
        string loop = names.Of("loop");
        bool hops = names.Coin("follow-chain");
        string start = hops ? "1" : "0";
        return $$"""
            let recursive {{lookup}} (label: Str) (labels: List((Str, Str))) =
                match labels with
                    | [] -> None
                    | (candidate, next) :: rest ->
                        if candidate == label
                        then Some(next)
                        else {{lookup}}(label)(rest)

            let recursive {{stage}} (hops: Int) (label: Str) (labels: List((Str, Str))) =
                if hops <= 0
                then Some(label)
                else
                    match {{lookup}}(label)(labels) with
                        | Some(next) -> {{stage}}(hops - 1)(next)(labels)
                        | None -> None

            let {{measure}} (labels: List((Str, Str))) (start: Str) =
                match {{stage}}({{start}})(start)(labels) with
                    | Some(found) -> Ashes.Text.length(found)
                    | None -> 0

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else {{loop}}(n - 1)(acc + {{measure}}([("lambda_" + Ashes.Text.fromInt(n), "lambda_" + Ashes.Text.fromInt(n + 1))])("lambda_" + Ashes.Text.fromInt(n)))

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(0) + seed
            """;
    }
}
