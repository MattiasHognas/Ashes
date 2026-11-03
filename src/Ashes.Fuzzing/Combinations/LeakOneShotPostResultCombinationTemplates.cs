using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A handler whose operation arm resumes once and then builds the handle's value from the resumed
/// one (`let x = resume(v) in post`), behind an optional `let` prefix, over a body that turns the
/// operation's result into a fresh string each time a loop calls it. A fold that starts from an
/// arena value asks each post for an arena result, so a post that produces a reference-counted
/// string copies it out and releases the original instead of handing the fold a reference it
/// never releases.
/// </summary>
internal sealed class OneShotPostResultTemplate() : LeakCombinationTemplate(
    "leak.one-shot-post-result", "oneShotPostResult", GeneratedFeature.Capability, GeneratedFeature.Handler)
{
    protected override string Declarations(LeakNames names)
    {
        string capability = names.Of("LeakTick");
        string probe = names.Of("probe");
        string loop = names.Of("loop");
        string resumed = names.Pick("x", "rest", "after");
        string word = names.Word();
        string post = names.Variant("post", 3) switch
        {
            0 => $"{resumed} + {word}",
            1 => $"{word} + {resumed}",
            _ => $"{resumed} + Ashes.Text.fromInt(n)",
        };
        string body = $"let {resumed} = resume(n) in {post}";
        string arm = names.Coin("let-prefix")
            ? $"let k = n + {names.Between(1, 9)} in {body.Replace("resume(n)", "resume(k)", StringComparison.Ordinal)}"
            : body;
        string parameter = names.Coin("wildcard") ? "_" : "u";
        string returnArm = names.Coin("return-arm") ? "\n        | return(r) -> r" : string.Empty;
        return $$"""
            capability {{capability}} =
                | get : Unit -> Int

            let {{probe}} (n: Int) =
                handle Ashes.Text.fromInt(perform {{capability}}.get(Unit)) with
                    | {{capability}}.get({{parameter}}) -> {{arm}}{{returnArm}}

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else {{loop}}(n - 1)(acc + Ashes.Text.byteLength({{probe}}(n)))

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(seed)
            """;
    }
}
