using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A match on a fresh list joining a pattern-bound head with a literal, read by a byte length in a
/// loop body or through a helper. The literal arm is normalized beside the copied head, and the join
/// is newly produced, so the read releases it.
/// </summary>
internal sealed class NormalizedMatchJoinReadTemplate() : LeakCombinationTemplate(
    "leak.normalized-match-join-read", "normalizedMatchJoinRead", GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string probe = names.Of("probe");
        string loop = names.Of("loop");
        string head = names.Coin("fresh-head") ? "Ashes.Text.fromInt(n)" : $"{names.Word()} + Ashes.Text.fromInt(n)";
        string match = $"match [{head}, {names.Word()}] with\n        | x :: _ -> x\n        | [] -> {(names.Coin("empty-literal") ? "\"\"" : names.Word())}";
        string read = names.Coin("inline")
            ? $"Ashes.Text.byteLength(({match.Replace("\n        ", "\n            ", StringComparison.Ordinal)}))"
            : $"Ashes.Text.byteLength({probe}(n))";
        return $$"""
            let {{probe}} (n: Int) =
                {{match}}

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else {{loop}}(n - 1)(acc + {{read}})

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(seed)
            """;
    }
}
