using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A perform's adopted result string consed as the head of an arena list cell, directly or through
/// a `let` binding. An arena cell never releases its head and the caller's copy-out copies the heads
/// without dropping them, so the cell stores an arena copy and the adopted string is released at
/// the cons; storing it leaks one string per cell.
/// </summary>
internal sealed class ArenaCellFreshHeadTemplate() : LeakCombinationTemplate(
    "leak.arena-cell-fresh-head", "arenaCellFreshHead", GeneratedFeature.Capability, GeneratedFeature.Handler, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string capability = names.Of("LeakHead");
        string gather = names.Of("gather");
        string count = names.Of("count");
        string probe = names.Of("probe");
        string loop = names.Of("loop");
        string perform = $"perform {capability}.get(Unit)";
        string cons = names.Variant("head", 3) switch
        {
            0 => $"{perform} :: acc",
            1 => $"let x = {perform}\n    in x :: acc",
            _ => $"{names.Word()} :: {perform} :: acc",
        };
        string arm = names.Coin("one-shot-post")
            ? $"let x = resume({names.Word()}) in {names.Word()} :: x"
            : $"resume({names.Word()})";
        return $$"""
            capability {{capability}} =
                | get : Unit -> Str

            let {{gather}} (acc: List(Str)) =
                {{cons}}

            let recursive {{count}} (xs: List(Str)) (n: Int) =
                match xs with
                    | [] -> n
                    | _ :: rest -> {{count}}(rest)(n + 1)

            let {{probe}} (n: Int) =
                {{count}}(handle {{gather}}([Ashes.Text.fromInt(n)]) with
                    | {{capability}}.get(u) -> {{arm}}
                    | return(r) -> r)(0)

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else {{loop}}(n - 1)(acc + {{probe}}(n))

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(seed)
            """;
    }
}
