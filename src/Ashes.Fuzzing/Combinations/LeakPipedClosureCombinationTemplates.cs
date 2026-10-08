using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A lambda applied in a pipe whose body returns a reference-counted list (a recursive producer's
/// result), handed on as the enclosing function's own result. The call of the lambda took the
/// result as an arena value, so the function returned it unowned and nothing released it.
/// </summary>
internal sealed class PipedLambdaReturnsCallResultTemplate() : LeakCombinationTemplate(
    "leak.piped-lambda-returns-call-result", "pipedLambdaResult", GeneratedFeature.Record, GeneratedFeature.Lambda, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string entry = names.Of("LeakLive");
        string build = names.Of("buildLive");
        string step = names.Of("stepLive");
        string count = names.Of("countLive");
        string items = names.Ints(names.Between(2, 4), 1, 5);
        string body = names.Variant("body", 2) switch
        {
            0 => $"block |> (given (m) -> {build}(m))",
            _ => $"block |> (given (m) -> {build}(m + {LeakNames.Text(names.Between(0, 2))}))",
        };
        return $$"""
            type {{entry}} =
                | block: Int
                | liveIn: Bool

            let recursive {{build}} (n: Int) =
                if n <= 0
                then []
                else {{entry}}(block = n, liveIn = n % 2 == 0) :: {{build}}(n - 1)

            let recursive {{step}} (remaining: List(Int)) =
                match remaining with
                    | [] -> []
                    | block :: rest -> {{body}}

            let recursive {{count}} (entries: List({{entry}})) (total: Int) =
                match entries with
                    | [] -> total
                    | {{entry}} { liveIn = true } :: rest -> {{count}}(rest)(total + 1)
                    | _ :: rest -> {{count}}(rest)(total)

            let {{names.Entry}} (seed: Int) = {{count}}({{step}}({{items}}))(seed)
            """;
    }
}
