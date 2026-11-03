using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A tail-recursive loop appending a perform's result to its string accumulator in place. The
/// perform site adopts the result as an owned reference-counted string; the in-place append copies
/// its bytes onto the accumulator's reservation, so the append releases the result once it is
/// copied. Keeping it leaks one string per append.
/// </summary>
internal sealed class AccumulatorAppendPerformResultTemplate() : LeakCombinationTemplate(
    "leak.accumulator-append-perform-result", "accumulatorAppendPerformResult", GeneratedFeature.Capability, GeneratedFeature.Handler, GeneratedFeature.TailCall)
{
    protected override string Declarations(LeakNames names)
    {
        string capability = names.Of("LeakText");
        string gather = names.Of("gather");
        string probe = names.Of("probe");
        string loop = names.Of("loop");
        string accumulator = names.Pick("acc", "text", "sofar");
        string perform = $"perform {capability}.get(Unit)";
        string append = names.Variant("append", 3) switch
        {
            0 => $"{accumulator} + {perform}",
            1 => $"{accumulator} + {perform} + {names.Word()}",
            _ => $"{accumulator} + {names.Word()} + {perform}",
        };
        string arm = names.Coin("one-shot-post")
            ? $"let x = resume({names.Word()}) in x + {names.Word()}"
            : $"resume({names.Word()})";
        return $$"""
            capability {{capability}} =
                | get : Unit -> Str

            let recursive {{gather}} (i: Int) ({{accumulator}}: Str) =
                if i == 0
                then {{accumulator}}
                else {{gather}}(i - 1)({{append}})

            let {{probe}} (n: Int) =
                Ashes.Text.byteLength(handle {{gather}}({{names.Between(1, 4)}})(Ashes.Text.fromInt(n)) with
                    | {{capability}}.get(u) -> {{arm}}
                    | return(r) -> r + {{names.Word()}})

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else {{loop}}(n - 1)(acc + {{probe}}(n))

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(seed)
            """;
    }
}
