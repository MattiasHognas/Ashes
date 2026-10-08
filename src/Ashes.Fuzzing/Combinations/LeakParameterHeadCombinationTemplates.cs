using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop that walks one list by its tail and conses a string parameter it hands on unchanged onto
/// another (<c>stamp(separator)(separator :: acc)(rest)</c>): the walked list's predecessor is
/// released at each back edge, so the loop's entry has to take the list it is given.
/// </summary>
internal sealed class ParameterConsedOntoAccumulatorTemplate() : LeakCombinationTemplate(
    "leak.parameter-consed-onto-accumulator", "parameterConsed", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string collect = names.Of("collectWalked");
        string stamp = names.Of("stampSeparator");
        string separator = names.Word();
        bool counted = names.Coin("counted");
        string use = counted
            ? $"Ashes.Collection.List.length({stamp}({separator})([])({collect}(seed)([])))"
            : $"Ashes.Text.byteLength(Ashes.Text.join({separator})({stamp}({separator})([])({collect}(seed)([]))))";
        return $$"""
            let recursive {{collect}} (n: Int) (acc: List(Str)) =
                if n <= 0
                then acc
                else {{collect}}(n - 1)(Ashes.Text.fromInt(n) :: acc)

            let recursive {{stamp}} (separator: Str) (acc: List(Str)) (ps: List(Str)) =
                match ps with
                    | [] -> acc
                    | _ :: rest -> {{stamp}}(separator)(separator :: acc)(rest)

            let {{names.Entry}} (seed: Int) = {{use}} + seed
            """;
    }
}

/// <summary>
/// A pairwise loop handing the head it walked on as the next round's string parameter
/// (<c>pairs(first)(prev :: first :: acc)(rest)</c>): the head's pattern-owner duplicate becomes a
/// real retain once the walked list is placed on the reference-counted heap, so the back edge must
/// store it as the owned successor it is rather than retain it a second time.
/// </summary>
internal sealed class PairwiseHeadHandedOnTemplate() : LeakCombinationTemplate(
    "leak.pairwise-head-handed-on", "pairwiseHead", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string collect = names.Of("collectWalked");
        string pairs = names.Of("pairsHandedOn");
        string cons = names.Pick("prev :: first :: acc", "prev :: acc", "first :: prev :: acc");
        return $$"""
            let recursive {{collect}} (n: Int) (acc: List(Str)) =
                if n <= 0
                then acc
                else {{collect}}(n - 1)(Ashes.Text.fromInt(n) :: acc)

            let recursive {{pairs}} (prev: Str) (acc: List(Str)) (ps: List(Str)) =
                match ps with
                    | [] -> acc
                    | first :: rest -> {{pairs}}(first)({{cons}})(rest)

            let {{names.Entry}} (seed: Int) = Ashes.Collection.List.length({{pairs}}(Ashes.Text.fromInt(seed))([])({{collect}}(3)([]))) + seed
            """;
    }
}
