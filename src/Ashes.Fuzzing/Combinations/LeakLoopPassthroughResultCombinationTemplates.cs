using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop returns a let-bound call result that may be its own list parameter handed straight
/// through. The result holds a retain of its own, so the loop's exit releases the parameter rather
/// than transferring its reference to the equal result.
/// </summary>
internal sealed class LoopPassthroughResultTemplate() : LeakCombinationTemplate(
    "leak.loop-passthrough-result", "loopPassthrough", GeneratedFeature.RecursiveFunction, GeneratedFeature.TailCall, GeneratedFeature.List, GeneratedFeature.Let)
{
    protected override string Declarations(LeakNames names)
    {
        string bump = names.Of("bump");
        string settle = names.Of("settle");
        string sum = names.Of("sum");
        string loop = names.Of("loop");
        string order = names.Coin("negative-order") ? "[1, -2]" : "[1, 2]";
        return $$"""
            let recursive {{bump}} (order: List(Int)) (xs: List(Int)) =
                match order with
                    | [] -> xs
                    | o :: rest ->
                        if o < 0
                        then {{bump}}(rest)(o :: xs)
                        else {{bump}}(rest)(xs)

            let recursive {{settle}} (k: Int) (xs: List(Int)) =
                (let next = {{bump}}({{order}})(xs)
                in
                    if k == 0
                    then next
                    else {{settle}}(k - 1)(next))

            let recursive {{sum}} (xs: List(Int)) =
                match xs with
                    | [] -> 0
                    | x :: rest -> x + {{sum}}(rest)

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else {{loop}}(n - 1)(acc + {{sum}}({{settle}}({{names.Between(0, 2)}})([n, 2, 3])))

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(seed)
            """;
    }
}
