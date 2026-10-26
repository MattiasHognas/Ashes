using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop matching a tuple of a tree parameter and a counter that binds a child of the tree's node
/// at the tree's own position and passes it back: the read out of the reference-counted tuple
/// already retains the child, so the self call hands it on without a same-parameter transfer of
/// its own.
/// </summary>
internal sealed class TupleConstructorChildHandedBackTemplate() : LeakCombinationTemplate(
    "leak.tuple-constructor-child-handed-back", "tupleConstructorChildHandedBack", GeneratedFeature.Adt, GeneratedFeature.Record, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string leafValue = names.Of("LeakLeafValue");
        string tree = names.Of("LeakTree");
        string build = names.Of("build");
        string descend = names.Of("descend");
        bool right = names.Coin("right");
        string node = right ? "Node(_, child)" : "Node(child, _)";
        return $$"""
            type {{leafValue}} =
                | state: List(Int)
                | temp: Int

            type {{tree}} =
                | Leaf({{leafValue}})
                | Node({{tree}}, {{tree}})

            let recursive {{build}} (depth: Int) (n: Int) =
                if depth == 0
                then Leaf({{leafValue}}(state = [n, depth], temp = n))
                else Node({{build}}(depth - 1)(n), {{build}}(depth - 1)(n + depth))

            let recursive {{descend}} (t: {{tree}}) (acc: Int) =
                match (t, acc) with
                    | (Leaf({{leafValue}} { state = st, temp = k }), a) -> a + k + Ashes.Collection.List.length(st)
                    | ({{node}}, a) -> {{descend}}(child)(a + 1)

            let {{names.Entry}} (seed: Int) =
                {{descend}}({{build}}({{names.Between(2, 4)}})(seed))(seed)
            """;
    }
}

/// <summary>
/// A loop matching a tuple of a list and a record parameter that takes two heads at once
/// (`a :: b :: rest`) and passes the tail back: the tail of the chain is the list's consumed tail,
/// placed as a single cons's would be, so the list each iteration was given is released.
/// </summary>
internal sealed class TupleConsChainTailHandedBackTemplate() : LeakCombinationTemplate(
    "leak.tuple-cons-chain-tail-handed-back", "tupleConsChainTailHandedBack", GeneratedFeature.Record, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string value = names.Of("LeakKept");
        string pairs = names.Of("pairs");
        return $$"""
            type {{value}} =
                | state: List(Int)
                | temp: Int

            let recursive {{pairs}} (items: List(Int)) (kept: {{value}}) (acc: Int) =
                match (items, kept) with
                    | (a :: b :: rest, same) -> {{pairs}}(rest)(same)(acc + a * b)
                    | (_, {{value}} { state = st, temp = k }) -> acc + k + Ashes.Collection.List.length(st)

            let {{names.Entry}} (seed: Int) =
                {{pairs}}([seed, {{names.Between(1, 9)}}, 3, seed, {{names.Between(1, 9)}}])({{value}}(state = [seed, {{names.Between(1, 9)}}], temp = seed))(seed)
            """;
    }
}
