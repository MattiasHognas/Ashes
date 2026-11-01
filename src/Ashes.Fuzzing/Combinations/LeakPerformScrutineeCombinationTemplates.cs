using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A handled body that matches a perform result directly, a list of integers or strings, taking
/// the head apart or returning it, with or without a return arm, called by a loop. The perform site
/// adopts the arm's result as an owned value, and the match takes it over as a binding of the
/// result would, so the list outlives no call; a return arm releases a body value it does not hand
/// on.
/// </summary>
internal sealed class PerformScrutineeTemplate() : LeakCombinationTemplate(
    "leak.perform-scrutinee", "performScrutinee", GeneratedFeature.Capability, GeneratedFeature.Handler, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string capability = names.Of("LeakSource");
        string probe = names.Of("probe");
        string loop = names.Of("loop");
        string perform = $"perform {capability}.get(Unit)";
        string text = names.Coin("fresh-text") ? "Ashes.Text.fromInt(n)" : names.Word();
        (string body, string resumed, bool returnsText) = names.Variant("shape", 4) switch
        {
            0 => ($"(match {perform} with\n        | x :: _ -> x\n        | [] -> 0)", "[n, n]", false),
            1 => ($"(match {perform} with\n        | x :: _ -> Ashes.Text.byteLength(x)\n        | [] -> 0)", $"[{text}, {names.Word()}]", false),
            2 => ($"(match {perform} with\n        | x :: _ -> x\n        | [] -> \"\")", $"[{text}, {names.Word()}]", true),
            _ => ($"(match {perform} with\n        | 0 -> \"\"\n        | k -> Ashes.Text.fromInt(k))", "n", true),
        };
        string returnArm = names.Coin("return-arm") ? "\n        | return(r) -> r" : string.Empty;
        string probed = returnsText ? $"Ashes.Text.byteLength({probe}(n))" : $"{probe}(n)";
        return $$"""
            capability {{capability}}(a) =
                | get : Unit -> a

            let {{probe}} (n: Int) =
                handle {{body}} with
                    | {{capability}}.get(_) -> resume({{resumed}}){{returnArm}}

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else {{loop}}(n - 1)(acc + {{probed}})

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(seed)
            """;
    }
}
