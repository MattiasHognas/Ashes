using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A recursive function generic in its tree's type that joins the keys it reads with `+` around its
/// own recursive call: `key + keys(left)`. Both operands of the `+` are still type variables when it
/// is lowered, so the operator is deferred until the body pins them; a body lowered only once
/// against the open types asked the recursive call for an arena result and never released the
/// reference-counted copy it made of it, one string per level.
/// </summary>
internal sealed class DeferredGenericStringAddTemplate() : LeakCombinationTemplate(
    "leak.deferred-generic-string-add", "deferredGenericAdd", GeneratedFeature.Adt, GeneratedFeature.Match, GeneratedFeature.RecursiveFunction)
{
    protected override string Declarations(LeakNames names)
    {
        string tree = names.Of("Tree");
        string empty = names.Of("Empty");
        string node = names.Of("Node");
        string keys = names.Of("keys");
        string build = names.Of("build");
        string key = names.Pick("key", "label", "name");
        string left = names.Pick("left", "rest", "below");
        bool nodeArmFirst = names.Coin("node-arm-first");
        bool keyFirst = names.Coin("key-first");
        string join = keyFirst ? $"{key} + {keys}({left})" : $"{keys}({left}) + {key}";
        string nodeArm = $"| {node}({left}, {key}, _) -> {join}";
        string emptyArm = $"| {empty} -> {names.Word()}";
        string arms = nodeArmFirst
            ? $"{nodeArm}\n        {emptyArm}"
            : $"{emptyArm}\n        {nodeArm}";
        return $$"""
            type {{tree}}(K, V) =
                | {{empty}}
                | {{node}}({{tree}}(K, V), K, V)

            let recursive {{keys}} map =
                match map with
                    {{arms}}

            let recursive {{build}} (n: Int) =
                if n == 0
                then {{empty}}
                else {{node}}({{build}}(n - 1), Ashes.Text.fromInt(n), n)

            let {{names.Entry}} (seed: Int) = seed + Ashes.Text.byteLength({{keys}}({{build}}({{names.Between(2, 6)}})))
            """;
    }
}
