using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop over positions that replaces its list accumulator with a helper's rebuild of it in one
/// arm and hands it on in the other, the shape of stage 1's immediate-dominator round
/// (`idomRound` over `replaceIntAt`). Still open: the rebuilt lists leak.
/// </summary>
internal sealed class AccumulatorReplacedByHelperInOneArmTemplate() : LeakCombinationTemplate(
    "leak.accumulator-replaced-by-helper-in-one-arm", "replacedInOneArm", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.If)
{
    protected override string Declarations(LeakNames names)
    {
        string replace = names.Of("replaceAt");
        string round = names.Of("roundOver");
        string sum = names.Of("sumItems");
        int length = names.Between(3, 6);
        string order = "[" + string.Join(", ", Enumerable.Range(0, length).Select(LeakNames.Text)) + "]";
        string seeds = "[" + string.Join(", ", Enumerable.Range(0, length).Select(_ => "-1")) + "]";
        int skipped = names.Between(0, 1);
        return $$"""
            let recursive {{replace}} (items: List(Int)) (index: Int) (replacement: Int) =
                match items with
                    | [] -> []
                    | head :: rest ->
                        if index == 0
                        then replacement :: rest
                        else head :: {{replace}}(rest)(index - 1)(replacement)

            let recursive {{round}} (order: List(Int)) (values: List(Int)) =
                match order with
                    | [] -> values
                    | position :: rest ->
                        if position == {{LeakNames.Text(skipped)}}
                        then {{round}}(rest)(values)
                        else {{round}}(rest)({{replace}}(values)(position)(position - 1))

            let recursive {{sum}} (items: List(Int)) (total: Int) =
                match items with
                    | [] -> total
                    | head :: rest -> {{sum}}(rest)(total + head)

            let {{names.Entry}} (seed: Int) = {{sum}}({{round}}({{order}})({{seeds}}))(seed)
            """;
    }
}

/// <summary>
/// A loop consing the concatenation of two elements onto its accumulator, the pairwise pass of the
/// standard library's `Text.join` that stage 1's parser and project graph call for every qualified
/// name and path. Still open: a concatenation leaks.
/// </summary>
internal sealed class PairwiseConcatAccumulatorTemplate() : LeakCombinationTemplate(
    "leak.pairwise-concat-accumulator", "pairwiseConcat", GeneratedFeature.TailCall, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string pairwise = names.Of("pairwiseGo");
        string count = names.Of("countTexts");
        IReadOnlyList<string> words = names.DistinctWords(names.Between(3, 5));
        string texts = "[" + string.Join(", ", words) + "]";
        return $$"""
            let recursive {{pairwise}} (acc: List(Str)) (parts: List(Str)) =
                match parts with
                    | [] -> acc
                    | first :: rest ->
                        match rest with
                            | [] -> first :: acc
                            | second :: more -> {{pairwise}}((first + second) :: acc)(more)

            let recursive {{count}} (items: List(Str)) (total: Int) =
                match items with
                    | [] -> total
                    | head :: rest -> {{count}}(rest)(total + Ashes.Text.length(head))

            let {{names.Entry}} (seed: Int) = {{count}}({{pairwise}}([])({{texts}}))(seed)
            """;
    }
}

/// <summary>
/// A producer building each record through two closures applied in a pipe, the second consing the
/// record onto the recursive tail, the shape of stage 1's lifetime-placement liveness step
/// (`stepLiveness`). Still open.
/// </summary>
internal sealed class PipedClosuresConsRecordTemplate() : LeakCombinationTemplate(
    "leak.piped-closures-cons-record", "pipedClosuresCons", GeneratedFeature.Record, GeneratedFeature.RecordPattern, GeneratedFeature.Lambda, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string entry = names.Of("LeakLive");
        string lookup = names.Of("liveInOf");
        string step = names.Of("stepLive");
        string count = names.Of("countLive");
        string items = names.Ints(names.Between(3, 5), 0, 6);
        return $$"""
            type {{entry}} =
                | block: Int
                | liveIn: Bool
                | liveOut: Bool

            let recursive {{lookup}} (block: Int) (entries: List({{entry}})) =
                match entries with
                    | [] -> false
                    | {{entry}} { block = candidate, liveIn = liveIn } :: rest ->
                        if candidate == block
                        then liveIn
                        else {{lookup}}(block)(rest)

            let recursive {{step}} (entries: List({{entry}})) (remaining: List(Int)) =
                match remaining with
                    | [] -> []
                    | block :: rest ->
                        entries
                        |> {{lookup}}(block + 1)
                        |> (given (liveOut) -> {{entry}}(block = block, liveIn = liveOut || block == {{LeakNames.Text(names.Between(1, 4))}}, liveOut = liveOut))
                        |> (given (made) -> made :: {{step}}(entries)(rest))

            let recursive {{count}} (entries: List({{entry}})) (total: Int) =
                match entries with
                    | [] -> total
                    | {{entry}} { liveIn = true } :: rest -> {{count}}(rest)(total + 1)
                    | _ :: rest -> {{count}}(rest)(total)

            let {{names.Entry}} (seed: Int) = {{count}}({{step}}([])({{items}}))(seed)
            """;
    }
}
