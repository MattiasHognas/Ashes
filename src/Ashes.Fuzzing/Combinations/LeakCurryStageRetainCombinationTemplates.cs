using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A let-bound association list handed to the first stage of a curried function whose final result
/// may keep parts of it, the stage closure then passed to a map, the way stage 1's optimizer applies
/// <c>devirtualizeReturnedClosureCallsInFunction(firstKnownLabels)</c> to every function. The stage
/// stores the list in an arena environment without adopting the reference the caller took for the
/// final result; with the call's result a closure, nothing settled that reference.
/// </summary>
internal sealed class CurryStageRetainTemplate() : LeakCombinationTemplate(
    "leak.curry-stage-retain", "curryStageRetain", GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string fn = names.Of("Fn");
        string pairsOf = names.Of("pairsOf");
        string lookup = names.Of("lookupLabel");
        string devirt = names.Of("devirt");
        string weight = names.Of("weight");
        string mapFns = names.Of("mapFns");
        int width = names.Between(1, 3);
        string suffix = names.Pick("_next", "_up", "_known");
        string items = "[" + string.Join(", ", Enumerable.Range(0, width).Select(i => i == 0
            ? $"{fn}(label = \"f\" + Ashes.Text.fromInt(seed), body = [1])"
            : $"{fn}(label = \"g{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}\", body = [])")) + "]";
        return $$"""
            type {{fn}} =
                | label: Str
                | body: List(Int)

            let recursive {{pairsOf}} (fns: List({{fn}})) acc =
                match fns with
                    | [] -> acc
                    | f :: rest -> {{pairsOf}}(rest)((f.label, f.label + "{{suffix}}") :: acc)

            let recursive {{lookup}} (key: Str) (entries: List((Str, Str))) =
                match entries with
                    | [] -> None
                    | (k, v) :: tail ->
                        if k == key
                        then Some(v)
                        else {{lookup}}(key)(tail)

            let {{devirt}} (known: List((Str, Str))) (f: {{fn}}) =
                match {{lookup}}(f.label)(known) with
                    | Some(next) -> f with label = next
                    | None -> f

            let recursive {{weight}} (fns: List({{fn}})) =
                match fns with
                    | [] -> 0
                    | f :: rest -> Ashes.Text.byteLength(f.label) + {{weight}}(rest)

            let recursive {{mapFns}} (g: {{fn}} -> {{fn}}) (fns: List({{fn}})) =
                match fns with
                    | [] -> []
                    | f :: rest -> g(f) :: {{mapFns}}(g)(rest)

            let {{names.Entry}} (seed: Int) =
                (let fns = {{items}}
                in
                    let known = {{pairsOf}}(fns)([])
                    in {{weight}}({{mapFns}}({{devirt}}(known))(fns)))
            """;
    }
}
