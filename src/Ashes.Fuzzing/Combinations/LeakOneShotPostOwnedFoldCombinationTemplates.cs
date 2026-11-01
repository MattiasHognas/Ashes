using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A one-shot operation arm (`let x = resume(v) in post`) under a handle whose value is a fresh
/// reference-counted string, built by the return arm or adopted from the perform, so the posts fold
/// starts from an owned value. Each post is applied under the owned-argument contract: the value is
/// retained only for a post that adopts it, a reference-counted post result is adopted and an arena
/// one copied out, and the value the post was given is released. A post that drops the resumed
/// value, returns it as is, or returns a literal covers each of those paths.
/// </summary>
internal sealed class OneShotPostOwnedFoldTemplate() : LeakCombinationTemplate(
    "leak.one-shot-post-owned-fold", "postOwnedFold", GeneratedFeature.Capability, GeneratedFeature.Handler)
{
    protected override string Declarations(LeakNames names)
    {
        string capability = names.Of("LeakOwned");
        string probe = names.Of("probe");
        string loop = names.Of("loop");
        string resumed = names.Pick("x", "rest", "after");
        string word = names.Word();
        string post = names.Variant("post", 4) switch
        {
            0 => $"{resumed} + {word}",
            1 => resumed,
            2 => names.Word(),
            _ => $"{resumed} + Ashes.Text.fromInt(k)",
        };
        string arm = $"let k = n + {names.Between(1, 9)} in let {resumed} = resume({word}) in {post}";
        bool adopted = names.Coin("adopted-start");
        string operand = adopted ? "n" : "Unit";
        string operation = adopted ? "Int -> Str" : "Unit -> Str";
        string resumeArgument = adopted ? "Ashes.Text.fromInt(m)" : word;
        string parameter = adopted ? "m" : "u";
        string returnArm = adopted ? string.Empty : "\n        | return(r) -> r + Ashes.Text.fromInt(n)";
        return $$"""
            capability {{capability}} =
                | get : {{operation}}

            let {{probe}} (n: Int) =
                handle perform {{capability}}.get({{operand}}) with
                    | {{capability}}.get({{parameter}}) -> {{arm.Replace($"resume({word})", $"resume({resumeArgument})", StringComparison.Ordinal)}}{{returnArm}}

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else {{loop}}(n - 1)(acc + Ashes.Text.byteLength({{probe}}(n)))

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(seed)
            """;
    }
}
