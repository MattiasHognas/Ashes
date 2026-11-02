using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A perform whose result list is passed straight to a callee that returns one of its heads: the
/// caller owns the adopted list and releases it after the call. The call's string result is copied
/// out on the arena branch of its ownership flag, so the copy shares nothing with the list and the
/// list is released with its heads there; a spine-only release leaks the head the callee returned.
/// </summary>
internal sealed class PerformResultElementTemplate() : LeakCombinationTemplate(
    "leak.perform-result-element", "performResultElement", GeneratedFeature.Capability, GeneratedFeature.Handler, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string capability = names.Of("LeakList");
        string pick = names.Of("pick");
        string wrap = names.Of("wrap");
        string probe = names.Of("probe");
        string loop = names.Of("loop");
        string head = names.Pick("h", "first", "item");
        string fallback = names.Word();
        string perform = $"perform {capability}.get(Unit)";
        bool separator = names.Coin("separator-parameter");
        string pickParameters = separator ? "(sep: Str) (xs: List(Str))" : "(xs: List(Str))";
        string emptyArm = separator ? "sep" : fallback;
        string pickCall = separator ? $"{pick}({fallback})" : pick;
        string body = names.Variant("site", 3) switch
        {
            0 => $"{pickCall}({perform})",
            1 => $"{wrap}(Unit)",
            _ => $"{pickCall}({perform}) + Ashes.Text.fromInt(n)",
        };
        string resumed = names.Between(1, 3) switch
        {
            1 => $"[{names.Word()}]",
            2 => $"[{names.Word()}, {names.Word()}]",
            _ => $"[{names.Word()}, {names.Word()}, {names.Word()}]",
        };
        return $$"""
            capability {{capability}} =
                | get : Unit -> List(Str)

            let {{pick}} {{pickParameters}} =
                match xs with
                    | [] -> {{emptyArm}}
                    | {{head}} :: _ -> {{head}}

            let {{wrap}} (u: Unit) = {{pickCall}}({{perform}}) + {{names.Word()}}

            let {{probe}} (n: Int) =
                handle {{body}} with
                    | {{capability}}.get(u) -> resume({{resumed}})

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else {{loop}}(n - 1)(acc + Ashes.Text.byteLength({{probe}}(n)))

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(seed)
            """;
    }
}
