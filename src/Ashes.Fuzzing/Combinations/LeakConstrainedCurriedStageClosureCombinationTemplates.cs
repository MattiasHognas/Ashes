using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A local `let recursive` capturing a reference-counted list, constrained by an operator on its
/// unannotated parameters, is applied to all its arguments in one call through the trait dictionary
/// call. The dictionary-applied stage owns the captured list and every later stage copies it; the
/// call releases all of them once its result is settled.
/// </summary>
internal sealed class ConstrainedCurriedStageClosureTemplate() : LeakCombinationTemplate(
    "leak.constrained-curried-stage-closure", "constrainedStage", GeneratedFeature.ClosureCapture, GeneratedFeature.RecursiveFunction, GeneratedFeature.TailCall, GeneratedFeature.List, GeneratedFeature.Let)
{
    protected override string Declarations(LeakNames names)
    {
        string build = names.Of("build");
        string member = names.Of("member");
        string probe = names.Of("probe");
        string loop = names.Of("loop");
        string step = names.Coin("step-two") ? "2" : "1";
        string extra = names.Coin("extra-element") ? ", 3" : "";
        return $$"""
            let recursive {{build}} (n: Int) =
                if n == 0
                then []
                else n :: {{build}}(n - 1)

            let recursive {{member}} (x: Int) (xs: List(Int)) =
                match xs with
                    | [] -> false
                    | y :: rest -> y == x || {{member}}(x)(rest)

            let {{probe}} (n: Int) (xs: List(Int)) =
                (let seen = {{build}}({{names.Between(2, 5)}})
                in
                    let recursive find items idx step acc =
                        match items with
                            | [] -> acc
                            | x :: tail ->
                                if {{member}}(x)(seen)
                                then find(tail)(idx + step)(step)(acc + idx)
                                else find(tail)(idx + step)(step)(acc)
                    in find(xs)(0)({{step}})(0))

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else {{loop}}(n - 1)(acc + {{probe}}(n)([n, 2{{extra}}]))

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(seed)
            """;
    }
}
