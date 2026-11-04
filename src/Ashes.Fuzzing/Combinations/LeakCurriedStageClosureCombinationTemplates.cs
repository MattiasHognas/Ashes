using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A local `let recursive` capturing a reference-counted list is applied to all its arguments in
/// one call. Each stage but the last returns a closure on the reference-counted heap that only the
/// call holds, so the call releases the stages once its result is settled.
/// </summary>
internal sealed class CurriedStageClosureTemplate() : LeakCombinationTemplate(
    "leak.curried-stage-closure", "curriedStage", GeneratedFeature.ClosureCapture, GeneratedFeature.RecursiveFunction, GeneratedFeature.TailCall, GeneratedFeature.List, GeneratedFeature.Let)
{
    protected override string Declarations(LeakNames names)
    {
        string build = names.Of("build");
        string member = names.Of("member");
        string probe = names.Of("probe");
        string loop = names.Of("loop");
        string next = names.Coin("step-two") ? "(idx + 2)" : "(idx + 1)";
        string hit = names.Coin("list-result") ? "idx :: acc" : "acc + idx";
        string start = hit.Contains("::", StringComparison.Ordinal) ? "[]" : "0";
        string finish = hit.Contains("::", StringComparison.Ordinal) ? "size(found)" : "found";
        return $$"""
            let recursive {{build}} (n: Int) =
                if n == 0
                then []
                else n :: {{build}}(n - 1)

            let recursive {{member}} (x: Int) (xs: List(Int)) =
                match xs with
                    | [] -> false
                    | y :: rest -> y == x || {{member}}(x)(rest)

            let recursive size (xs: List(Int)) =
                match xs with
                    | [] -> 0
                    | _ :: rest -> 1 + size(rest)

            let {{probe}} (n: Int) (xs: List(Int)) =
                (let seen = {{build}}({{names.Between(2, 5)}})
                in
                    let recursive find items idx acc =
                        match items with
                            | [] -> acc
                            | x :: tail ->
                                if {{member}}(x)(seen)
                                then find(tail){{next}}({{hit}})
                                else find(tail){{next}}(acc)
                    in
                        let found = find(xs)(0)({{start}})
                        in {{finish}})

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else {{loop}}(n - 1)(acc + {{probe}}(n)([n, 2]))

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(seed)
            """;
    }
}
