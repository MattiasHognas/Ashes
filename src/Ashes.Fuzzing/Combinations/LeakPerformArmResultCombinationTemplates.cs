using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A handler whose operation arm resumes with a string, a constructor holding one, or a result
/// over strings, consumed by the handled body each time a loop calls it, the body returning either
/// a count or a string read from the operation's result. The perform site owns the arm's result:
/// it adopts a reference-counted string and asks the arm for an arena copy of any other heap
/// result, and the handle's value keeps the ownership of the body's value, so nothing the arm or
/// the body built outlives the call.
/// </summary>
internal sealed class PerformArmResultTemplate() : LeakCombinationTemplate(
    "leak.perform-arm-result", "performArmResult", GeneratedFeature.Capability, GeneratedFeature.Handler, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string capability = names.Of("LeakSource");
        string box = names.Of("LeakBox");
        string probe = names.Of("probe");
        string loop = names.Of("loop");
        string text = names.Coin("fresh-text") ? "Ashes.Text.fromInt(n)" : names.Word();
        string result = names.Coin("error") ? $"Error({text})" : $"Ok({text})";
        string perform = $"perform {capability}.get(Unit)";
        (string consumed, string resumed, bool returnsText) = names.Variant("shape", 5) switch
        {
            0 => ($"Ashes.Text.byteLength({perform})", text, false),
            1 => ($"(match {perform} with\n        | {box}(t, k) -> Ashes.Text.byteLength(t) + k)", $"{box}({text}, n)", false),
            2 => ($"(match {perform} with\n        | Ok(t) -> Ashes.Text.byteLength(t)\n        | Error(_) -> 0)", result, false),
            3 => ($"(match {perform} with\n        | {box}(t, _) -> t)", $"{box}({text}, n)", true),
            _ => ($"(match {perform} with\n        | Ok(t) -> t\n        | Error(_) -> {names.Word()})", result, true),
        };
        string returnArm = names.Coin("return-arm") ? "\n        | return(r) -> r" : string.Empty;
        string probed = returnsText ? $"Ashes.Text.byteLength({probe}(n))" : $"{probe}(n)";
        return $$"""
            type {{box}} =
                | {{box}}(Str, Int)

            capability {{capability}}(a) =
                | get : Unit -> a

            let {{probe}} (n: Int) =
                handle {{consumed}} with
                    | {{capability}}.get(_) -> resume({{resumed}}){{returnArm}}

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else {{loop}}(n - 1)(acc + {{probed}})

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(seed)
            """;
    }
}
