using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop that builds a fresh syntax tree every round and hands it to the unannotated parameter of a
/// generic walker (<c>walk(items)(trailing)(0)</c>), whose result is an integer. The tree is a value
/// of the contract's types, so it goes to the quantified parameter as an owned reference-counted
/// reference the caller releases after the call, instead of a deep copy into the persistent to-space
/// region, which nothing reclaims: one copy of the whole tree per round.
/// </summary>
internal sealed class GenericParameterAdtArgumentTemplate() : LeakCombinationTemplate(
    "leak.generic-parameter-adt-argument", "genericParameterAdtArgument", GeneratedFeature.Adt, GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string expr = names.Of("Syntax");
        string at = names.Of("SyntaxAt");
        string name = names.Of("SyntaxName");
        string call = names.Of("SyntaxCall");
        string walk = names.Of("walkItems");
        string build = names.Of("buildTree");
        string loop = names.Of("loopRounds");
        string word = names.Word();
        int depth = names.Between(1, 4);
        string items = string.Join(", ", Enumerable.Range(1, names.Between(1, 4)));
        string leaf = names.Coin("call-leaf")
            ? $"{call}({name}({word}), {name}({word}))"
            : $"{name}({word})";
        string second = names.Coin("second-generic")
            ? $"{walk}(rest)(trailing)(total + item)"
            : $"{walk}(rest)(trailing)(total + item + item)";
        return $$"""
            type {{expr}} =
                | {{at}}(Int, {{expr}})
                | {{name}}(Str)
                | {{call}}({{expr}}, {{expr}})

            let recursive {{walk}} (items: List(Int)) trailing (total: Int) =
                match items with
                    | [] -> total
                    | item :: rest -> {{second}}

            let recursive {{build}} (k: Int) (n: Int) =
                if k == 0
                then {{leaf}}
                else {{at}}(n, {{build}}(k - 1)(n))

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else
                    let trailing = {{build}}({{depth}})(n)
                    in {{loop}}(n - 1)(acc + {{walk}}([{{items}}])(trailing)(0))

            let {{names.Entry}} (seed: Int) = {{loop}}(3)(seed)
            """;
    }
}
