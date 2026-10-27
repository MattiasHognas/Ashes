using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A worklist loop that pushes two cells back onto the consumed tail of its list of lists
/// (<c>(name :: more) :: rest -&gt; walk(targetsOf(name) :: more :: rest)</c>), matched alone or in a
/// tuple. Taken for a cons onto the consumed tail, the chain placed the list on the
/// reference-counted heap, where its cells were never released.
/// </summary>
internal sealed class ChainOntoConsumedTailTemplate() : LeakCombinationTemplate(
    "leak.chain-onto-consumed-tail", "chainOntoConsumedTail", GeneratedFeature.Match, GeneratedFeature.Tuple)
{
    protected override string Declarations(LeakNames names)
    {
        string walk = names.Of("walk");
        string targets = names.Of("targetsOf");
        string push = names.Coin("freshFirst") ? $"{targets}(3)(name) :: more :: rest" : $"more :: {targets}(3)(name) :: rest";
        string width = LeakNames.Text(names.Between(2, 4));
        string body = names.Coin("tuple")
            ? $$"""
                match (stack, seen) with
                        | ([], _s) -> n
                        | ([] :: rest, s) -> {{walk}}(rest)(s)(n)
                        | ((name :: more) :: rest, s) -> {{walk}}({{push}})(name :: s)(n + 1)
                """
            : $$"""
                match stack with
                        | [] -> n
                        | [] :: rest -> {{walk}}(rest)(seen)(n)
                        | (name :: more) :: rest -> {{walk}}({{push}})(name :: seen)(n + 1)
                """;
        return $$"""
            let {{targets}} (width: Int) (name: Str) =
                if Ashes.Text.byteLength(name) < width
                then [name + "a", name + "b"]
                else []

            let recursive {{walk}} (stack: List(List(Str))) (seen: List(Str)) (n: Int) =
                {{body}}

            let {{names.Entry}} (seed: Int) =
                {{walk}}([["x" + Ashes.Text.fromInt(seed)], ["y"]])([])({{width}})
            """;
    }
}
