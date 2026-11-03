using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A recursive function returning a pair whose base arm pairs a nullary constructor with its list
/// parameter, while the arm through its own call builds the pair over that call's result, the way
/// stage 1's <c>buildLineTree</c> returns <c>(LineLeaf, starts)</c>. The nullary constructor is an
/// immortal cell; counted as a reference-counted element it put the base arm's pair, and the copy of
/// the list it took, on the reference-counted heap beside an arena arm, and the caller released
/// neither.
/// </summary>
internal sealed class NullaryTupleElementTemplate() : LeakCombinationTemplate(
    "leak.nullary-tuple-element", "nullaryTupleElement", GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string tree = names.Of("Tree");
        string leaf = names.Of("Leaf");
        string node = names.Of("Node");
        string build = names.Of("build");
        string size = names.Of("size");
        bool balanced = names.Coin("balanced");
        int count = names.Between(0, 4);
        int width = names.Between(2, 6);
        string items = "[" + string.Join(", ", Enumerable.Range(0, width).Select(index => index == 0 ? "seed" : "seed + " + index.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";
        string recursiveArm = balanced
            ? string.Join(
                "\n",
                $"match {build}(starts)(count / 2) with",
                "            | (left, start :: afterLeft) ->",
                $"                match {build}(afterLeft)(count - count / 2 - 1) with",
                $"                    | (right, afterRight) -> ({node}(left)(start)(right), afterRight)",
                "            | (left, []) -> (left, [])")
            : string.Join(
                "\n",
                $"match {build}(starts)(count - 1) with",
                $"            | (left, rest) -> ({node}(left)(count)({leaf}), rest)");
        return $$"""
            type {{tree}} =
                | {{leaf}}
                | {{node}}({{tree}}, Int, {{tree}})

            let recursive {{build}} (starts: List(Int)) (count: Int) =
                if count <= 0
                then ({{leaf}}, starts)
                else
                    {{recursiveArm}}

            let recursive {{size}} (tree: {{tree}}) =
                match tree with
                    | {{leaf}} -> 0
                    | {{node}}(left, value, right) -> 1 + value + {{size}}(left) + {{size}}(right)

            let {{names.Entry}} (seed: Int) =
                match {{build}}({{items}})({{count}}) with
                    | (tree, _rest) -> {{size}}(tree)
            """;
    }
}
