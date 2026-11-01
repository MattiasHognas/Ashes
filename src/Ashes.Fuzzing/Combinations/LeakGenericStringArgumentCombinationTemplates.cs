using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop that inserts each matched name, or a string built from it, into a tree through a function
/// generic in its key, and hands the call's result on as its tree parameter. The key goes to the
/// generic parameter as an owned reference-counted reference that the caller settles against the
/// call's result, instead of a copy in the persistent blob region.
/// </summary>
internal sealed class GenericStringArgumentTemplate() : LeakCombinationTemplate(
    "leak.generic-string-argument", "genericStringArgument", GeneratedFeature.Adt, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string tree = names.Of("GenTree");
        string leaf = names.Of("GenLeaf");
        string node = names.Of("GenNode");
        string insert = names.Of("genInsert");
        string size = names.Of("genSize");
        string loop = names.Of("genLoop");
        string key = names.Coin("built-key") ? $"name + {names.Word()}" : "name";
        string recurse = names.Coin("right-leaning")
            ? $"{node}(left, k, v, {insert}(key)(value)(right))"
            : $"{node}({insert}(key)(value)(left), k, v, right)";
        string seen = names.Coin("annotated-tree") ? $"(seen: {tree}(Str, Int))" : "seen";
        string exit = names.Coin("sized-exit") ? $"count + {size}(seen)" : "count";
        string words = names.Words(names.Between(1, 4));
        return $$"""
            type {{tree}}(K, V) =
                | {{leaf}}
                | {{node}}({{tree}}(K, V), K, V, {{tree}}(K, V))

            let recursive {{insert}} key value tree =
                match tree with
                    | {{leaf}} -> {{node}}({{leaf}}, key, value, {{leaf}})
                    | {{node}}(left, k, v, right) -> {{recurse}}

            let recursive {{size}} tree =
                match tree with
                    | {{leaf}} -> 0
                    | {{node}}(left, _, _, right) -> 1 + {{size}}(left) + {{size}}(right)

            let recursive {{loop}} (items: List(Str)) {{seen}} (count: Int) =
                match items with
                    | [] -> {{exit}}
                    | name :: rest -> {{loop}}(rest)({{insert}}({{key}})(count)(seen))(count + 1)

            let {{names.Entry}} (seed: Int) =
                {{loop}}({{words}})({{leaf}})(seed)
            """;
    }
}
