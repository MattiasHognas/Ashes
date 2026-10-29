using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A worklist of integers (<c>List(Int)</c>) the loop walks by its tail at some self-calls and
/// refills from a call's result at another, the refill built from a list read out of an optional
/// record a generic lookup returned (<c>match listAt(blocks)(current) with Some(Block { next =
/// next }) -> walk(append(next)(rest))(current :: seen)</c>), beside a list of the visited
/// integers consed from the worklist's heads: the shape of stage 1's block reachability walk. Left
/// in the arena, neither the earlier queue nor the lookup's optional result was ever released: the
/// result could still hold a part of the queue, and the visited list a part of the result, as far
/// as the back edge could tell.
/// </summary>
internal sealed class WorklistOfIndicesWalkedAndRefilledTemplate() : LeakCombinationTemplate(
    "leak.worklist-of-indices-walked-and-refilled", "worklistIndices", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string block = names.Of("Block");
        string listAt = names.Of("listAt");
        string contains = names.Of("containsIndex");
        string walk = names.Of("walkBlocks");
        string field = names.Pick("successors", "predecessors");
        string refill = names.Coin("refill-first")
            ? "Ashes.Collection.List.append(next)(rest)"
            : "Ashes.Collection.List.append(rest)(next)";
        string visitedOnRefill = names.Coin("visited-on-refill") ? "current :: seen" : "seen";
        int limit = names.Between(4, 9);
        return $$"""
            type {{block}} =
                | start: Int
                | successors: List(Int)
                | predecessors: List(Int)

            let recursive {{listAt}} items index =
                match items with
                    | [] -> None
                    | head :: rest ->
                        if index == 0
                        then Some(head)
                        else {{listAt}}(rest)(index - 1)

            let recursive {{contains}} (item: Int) (items: List(Int)) =
                match items with
                    | [] -> false
                    | head :: rest ->
                        if head == item
                        then true
                        else {{contains}}(item)(rest)

            let recursive {{walk}} (blocks: List({{block}})) (pending: List(Int)) (seen: List(Int)) (steps: Int) =
                match pending with
                    | [] -> steps + Ashes.Collection.List.length(seen)
                    | current :: rest ->
                        if steps > {{LeakNames.Text(limit)}} || {{contains}}(current)(seen)
                        then {{walk}}(blocks)(rest)(seen)(steps + 1)
                        else
                            match {{listAt}}(blocks)(current) with
                                | Some({{block}} { {{field}} = next }) -> {{walk}}(blocks)({{refill}})({{visitedOnRefill}})(steps + 1)
                                | None -> {{walk}}(blocks)(rest)(current :: seen)(steps + 1)

            let {{names.Entry}} (seed: Int) =
                [{{block}}(start = seed, successors = [1, 2], predecessors = [2]), {{block}}(start = 1, successors = [2], predecessors = [0, 1]), {{block}}(start = 2, successors = [], predecessors = [1])]
                |> (given (blocks) -> {{walk}}(blocks)([0, seed % 3])([])(0))
            """;
    }
}
