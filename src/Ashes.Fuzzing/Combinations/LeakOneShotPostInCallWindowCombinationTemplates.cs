using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A one-shot operation arm (`let x = resume(v) in post`) whose perform runs inside the arena
/// window of a call in the handled body: as the call's argument, through a helper that performs,
/// or inside the callee itself. The perform pushes its post cell and closure into that window, so
/// the window's exit must not rewind the arena while the post is pending; a rewind lets the next
/// allocation overwrite the post before the handle folds it.
/// </summary>
internal sealed class OneShotPostInCallWindowTemplate() : LeakCombinationTemplate(
    "leak.one-shot-post-in-call-window", "postInCallWindow", GeneratedFeature.Capability, GeneratedFeature.Handler)
{
    protected override string Declarations(LeakNames names)
    {
        string capability = names.Of("LeakFetch");
        string consume = names.Of("consume");
        string fetch = names.Of("fetch");
        string probe = names.Of("probe");
        string loop = names.Of("loop");
        string resumed = names.Pick("x", "rest", "after");
        string word = names.Word();
        string perform = $"perform {capability}.get(Unit)";
        (string consumeBody, string call) = names.Variant("window", 3) switch
        {
            0 => ($"a + {word}", $"{consume}({perform})"),
            1 => ($"a + {word}", $"{consume}({fetch}(Unit))"),
            _ => ($"a + {perform}", $"{consume}({word})"),
        };
        string body = names.Coin("trailing-concat") ? $"{call} + Ashes.Text.fromInt(n)" : call;
        string returnArm = names.Coin("return-arm") ? $"\n        | return(r) -> r + {names.Word()}" : string.Empty;
        return $$"""
            capability {{capability}} =
                | get : Unit -> Str

            let {{consume}} (a: Str) = {{consumeBody}}

            let {{fetch}} (u: Unit) = {{perform}}

            let {{probe}} (n: Int) =
                handle {{body}} with
                    | {{capability}}.get(u) -> let {{resumed}} = resume({{word}}) in {{resumed}} + {{word}}{{returnArm}}

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else {{loop}}(n - 1)(acc + Ashes.Text.byteLength({{probe}}(n)))

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(seed)
            """;
    }
}
