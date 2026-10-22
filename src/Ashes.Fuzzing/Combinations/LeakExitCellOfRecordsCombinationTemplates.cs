using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop over a list of records whose exit passes a cell consed onto its accumulator to a call
/// (<c>| [] -&gt; reverseValues(eof :: tokens)([])</c>), the shape of the self-hosted lexer's exit. The
/// cell is reference-counted and takes a reference to the accumulator, and a list of the contract's
/// types is passed borrowed, so no consumed-argument release followed the call: the cell, and with
/// it the whole accumulator, was never released.
/// </summary>
internal sealed class ExitCellOfRecordsPassedToCallTemplate() : LeakCombinationTemplate(
    "leak.exit-cell-of-records-passed-to-call", "exitCellRecords", GeneratedFeature.Record, GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string token = names.Of("LeakToken");
        string read = names.Of("readToken");
        string reverse = names.Of("reverseTokens");
        string scan = names.Of("scanTokens");
        string measure = names.Of("measureTokens");
        int size = names.Between(2, 5);
        bool resultFirst = names.Coin("resultFirst");
        string reverseDeclaration = resultFirst switch
        {
            false => $$"""
                let recursive {{reverse}} remaining result =
                    match remaining with
                        | [] -> result
                        | head :: tail -> {{reverse}}(tail)(head :: result)
                """,
            true => $$"""
                let recursive {{reverse}} (result: List({{token}})) (remaining: List({{token}})) =
                    match remaining with
                        | [] -> result
                        | head :: tail -> {{reverse}}(head :: result)(tail)
                """,
        };
        string exitHead = names.Coin("exitHead")
            ? $"{read}(position)"
            : $"{token}(text = {names.Word()}, position = position)";
        string exitCall = resultFirst
            ? $"{reverse}([])({exitHead} :: tokens)"
            : $"{reverse}({exitHead} :: tokens)([])";

        return $$"""
            type {{token}} =
                | text: Str
                | position: Int

            {{reverseDeclaration}}

            let {{read}} (position: Int) = {{token}}(text = Ashes.Text.fromInt(position), position = position)

            let recursive {{scan}} (n: Int) (position: Int) (tokens: List({{token}})) =
                if position >= n
                then {{exitCall}}
                else
                    let next = {{read}}(position)
                    in {{scan}}(n)(next.position + 1)(next :: tokens)

            let recursive {{measure}} (tokens: List({{token}})) (total: Int) =
                match tokens with
                    | [] -> total
                    | head :: rest -> {{measure}}(rest)(total + Ashes.Text.byteLength(head.text) + head.position)

            let {{names.Entry}} (seed: Int) = {{measure}}({{scan}}({{size}})(0)([]))(seed)
            """;
    }
}
