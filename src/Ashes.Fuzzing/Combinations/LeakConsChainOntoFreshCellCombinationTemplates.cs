using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A recursive function consing two or three heads onto its own recursive result
/// (`x :: s :: keep(rest)(more)`), not a tail-recursion-modulo-cons loop. The innermost cell takes
/// the fresh result over; each cell around it conses onto that fresh reference-counted cell.
/// </summary>
internal sealed class ConsChainOntoFreshCellTemplate() : LeakCombinationTemplate(
    "leak.cons-chain-onto-fresh-cell", "consChainOntoFreshCell", GeneratedFeature.Match, GeneratedFeature.List, GeneratedFeature.If)
{
    protected override string Declarations(LeakNames names)
    {
        string keep = names.Of("keep");
        string build = names.Of("build");
        string measure = names.Of("measure");
        bool text = names.Coin("text-elements");
        string element = text ? "Str" : "Int";
        string produced = text ? "(\"w\" + Ashes.Text.fromInt(n))" : "n";
        string size = text ? "Ashes.Text.byteLength(x)" : "x";
        string chain = (names.Coin("three-cells") ? "x :: s :: x :: " : "x :: s :: ") + $"{keep}(rest)(more)";
        bool dropping = names.Coin("dropping-branch");
        string scrutinee = names.Coin("tuple-scrutinee")
            ? $"""
                match (xs, seen) with
                        | (x :: rest, s :: more) ->
                            {Arm(dropping, chain, keep, 12)}
                        | _ -> seen
                """
            : $"""
                match xs with
                        | x :: rest ->
                            match seen with
                                | s :: more ->
                                    {Arm(dropping, chain, keep, 20)}
                                | [] -> seen
                        | [] -> seen
                """;
        int left = names.Between(1, 4);
        return $$"""
            let recursive {{keep}} (xs: List({{element}})) (seen: List({{element}})) =
                {{scrutinee}}

            let recursive {{build}} (n: Int) =
                if n == 0
                then []
                else {{produced}} :: {{build}}(n - 1)

            let recursive {{measure}} (xs: List({{element}})) =
                match xs with
                    | [] -> 0
                    | x :: rest -> {{size}} + {{measure}}(rest)

            let {{names.Entry}} (seed: Int) =
                let right = {{build}}(seed % 4 + 2)
                in {{measure}}({{keep}}({{build}}({{LeakNames.Text(left)}}))(right)) + {{measure}}(right)
            """;
    }

    private static string Arm(bool dropping, string chain, string keep, int indent)
    {
        if (!dropping)
        {
            return chain;
        }

        string pad = new(' ', indent + 4);
        return $"if x == s\n{pad}then {keep}(rest)(more)\n{pad}else {chain}";
    }
}
