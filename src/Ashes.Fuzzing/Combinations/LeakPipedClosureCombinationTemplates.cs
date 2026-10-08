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

/// <summary>
/// A record consed onto a function's reference-counted list result outside any recursion
/// (`Entry(...) :: build(n)`). The cell was built in the arena around the fresh list, and when the
/// caller normalized it the list itself was left with no owner.
/// </summary>
internal sealed class ConsOntoFreshCallResultTemplate() : LeakCombinationTemplate(
    "leak.cons-onto-fresh-call-result", "consFreshResult", GeneratedFeature.Record, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string entry = names.Of("LeakLive");
        string build = names.Of("buildLive");
        string wrap = names.Of("wrapLive");
        string count = names.Of("countLive");
        string head = names.Variant("head", 2) switch
        {
            0 => $"{entry}(block = n, liveIn = false)",
            _ => $"{entry}(block = n + {LeakNames.Text(names.Between(1, 3))}, liveIn = n % 2 == 0)",
        };
        int size = names.Between(2, 5);
        return $$"""
            type {{entry}} =
                | block: Int
                | liveIn: Bool

            let recursive {{build}} (n: Int) =
                if n <= 0
                then []
                else {{entry}}(block = n, liveIn = true) :: {{build}}(n - 1)

            let {{wrap}} (n: Int) = {{head}} :: {{build}}(n)

            let recursive {{count}} (entries: List({{entry}})) (total: Int) =
                match entries with
                    | [] -> total
                    | {{entry}} { liveIn = true } :: rest -> {{count}}(rest)(total + 1)
                    | _ :: rest -> {{count}}(rest)(total)

            let {{names.Entry}} (seed: Int) = {{count}}({{wrap}}({{LeakNames.Text(size)}}))(seed)
            """;
    }
}
