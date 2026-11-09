using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop pushing a call's owned list of integers (a block's successors read out of a record found by
/// index) onto an arena worklist of lists, the shape of a control-flow graph's postorder walk. The
/// arena cell holding the list never releases it, and the owned slot the escaping cell's head took
/// kept only the last iteration's value; the cell stores an arena copy of the list and the original
/// is released right away.
/// </summary>
internal sealed class LoopCellFreshListHeadTemplate() : LeakCombinationTemplate(
    "leak.loop-cell-fresh-list-head", "loopCellFreshListHead", GeneratedFeature.Match, GeneratedFeature.List, GeneratedFeature.Record)
{
    protected override string Declarations(LeakNames names)
    {
        string block = names.Of("Block");
        string listAt = names.Of("listAt");
        string successors = names.Of("successorsOf");
        string walk = names.Of("postorder");
        string total = names.Of("total");
        int length = names.Between(3, 6);
        bool keepOrder = names.Coin("keep-order");
        string pushed = keepOrder ? "successor :: order" : "order";
        string blocks = string.Join(", ", Enumerable.Range(0, length).Select(index =>
            $"{block}(start = seed + {index}, successors = {(index + 1 < length ? $"[{index + 1}]" : "[]")})"));
        return $$"""
            type {{block}} =
                | start: Int
                | successors: List(Int)

            let recursive {{listAt}} (items: List({{block}})) (index: Int) =
                match items with
                    | [] -> None
                    | head :: rest ->
                        if index == 0
                        then Some(head)
                        else {{listAt}}(rest)(index - 1)

            let {{successors}} (blocks: List({{block}})) (index: Int) =
                match {{listAt}}(blocks)(index) with
                    | Some({{block}} { successors = found }) -> found
                    | None -> []

            let recursive {{walk}} (blocks: List({{block}})) (pending: List(List(Int))) (order: List(Int)) =
                match pending with
                    | [] :: pendingRest -> {{walk}}(blocks)(pendingRest)(order)
                    | (successor :: more) :: pendingRest -> {{walk}}(blocks)({{successors}}(blocks)(successor) :: more :: pendingRest)({{pushed}})
                    | _ -> order

            let recursive {{total}} (xs: List(Int)) =
                match xs with
                    | [] -> 0
                    | x :: rest -> x + {{total}}(rest)

            let {{names.Entry}} (seed: Int) =
                (let blocks = [{{blocks}}]
                in {{total}}({{walk}}(blocks)([{{successors}}(blocks)(0)])([seed])))
            """;
    }
}
