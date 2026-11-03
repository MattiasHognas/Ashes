using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop over a tuple parameter bound by one irrefutable tuple pattern at the loop head, whose
/// back edge hands the bindings on in a new tuple from inside an <c>if</c>. The bindings take a
/// protective reference each iteration, and the back edge into the block that binds them ends their
/// lives there.
/// </summary>
internal sealed class TupleHeadOwnersAtBackEdgeTemplate() : LeakCombinationTemplate(
    "leak.tuple-head-owners-at-back-edge", "tupleHeadOwners", GeneratedFeature.Tuple, GeneratedFeature.TailCall, GeneratedFeature.If, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string go = names.Of("goPair");
        string left = names.Pick("xs", "items", "front");
        string right = names.Pick("extra", "rest", "names");
        string exit = names.Pick($"Ashes.Collection.List.length({left})", $"Ashes.Collection.List.length({right})", "n");
        int rounds = names.Between(2, 5);
        return $$"""
            let recursive {{go}} (n: Int) (pair: (List(Int), List(Str))) =
                match pair with
                    | ({{left}}, {{right}}) ->
                        if n == 0
                        then {{exit}}
                        else {{go}}(n - 1)(({{left}}, {{right}}))

            let {{names.Entry}} (seed: Int) = {{go}}({{LeakNames.Text(rounds)}})(([seed, seed + 1], {{names.Words(names.Between(1, 3))}}))
            """;
    }
}

/// <summary>
/// A loop over a tuple parameter whose successor is a tuple literal holding a cons built onto a
/// matched list. The cons is an arena cell that retained its head and tail; the back edge copies the
/// tuple with references of its own, so the dying cell's retains are released.
/// </summary>
internal sealed class ConsInTupleSuccessorTemplate() : LeakCombinationTemplate(
    "leak.cons-in-tuple-successor", "consInTuple", GeneratedFeature.Tuple, GeneratedFeature.List, GeneratedFeature.TailCall, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string go = names.Of("goReverse");
        string head = names.Pick("h", "first", "word");
        string tail = names.Pick("t", "rest", "remaining");
        string acc = names.Pick("acc", "done", "built");
        string consed = names.Coin("cons-matched-head") ? head : names.Word();
        return $$"""
            let recursive {{go}} (pair: (List(Str), List(Str))) =
                match pair with
                    | ([], {{acc}}) -> Ashes.Collection.List.length({{acc}})
                    | ({{head}} :: {{tail}}, {{acc}}) -> {{go}}(({{tail}}, {{consed}} :: {{acc}}))

            let {{names.Entry}} (seed: Int) = seed + {{go}}(({{names.Words(names.Between(2, 4))}}, {{names.Words(names.Between(1, 2))}}))
            """;
    }
}
