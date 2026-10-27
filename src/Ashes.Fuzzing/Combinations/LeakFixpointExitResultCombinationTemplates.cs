using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A fixpoint loop over a helper loop that returns its list parameter inside a tuple: the
/// fixpoint's exit hands back the part it took out of the helper's result, a reference of its own
/// to the very list the fixpoint's parameter holds. The exit compared the two, found them equal,
/// and skipped releasing the parameter, so the final list leaked at every call.
/// </summary>
internal sealed class FixpointExitResultOwnsItsReferenceTemplate() : LeakCombinationTemplate(
    "leak.fixpoint-exit-result-owns-its-reference", "fixpointExitResult", GeneratedFeature.Tuple, GeneratedFeature.Match, GeneratedFeature.TailCall)
{
    protected override string Declarations(LeakNames names)
    {
        string grow = names.Of("growKnown");
        string fixpoint = names.Of("settleKnown");
        bool strings = names.Coin("stringElements");
        bool counted = names.Coin("countedResult");
        string elementType = strings ? "Str" : "Int";
        string pushed = strings ? $"Ashes.Text.fromInt(g) + {names.Word()}" : "g";
        string items = names.Ints(names.Between(2, 5), 1, 6);
        string result = counted ? "(known, changed, Ashes.Collection.List.length(known))" : "(known, changed)";
        string more = counted ? "(nextKnown, true, _)" : "(nextKnown, true)";
        string done = counted ? "(nextKnown, false, _)" : "(nextKnown, false)";
        return $$"""
            let recursive {{grow}} (groups: List(Int)) (known: List({{elementType}})) (changed: Bool) =
                match groups with
                    | [] -> {{result}}
                    | g :: tail ->
                        if Ashes.Collection.List.length(known) < g
                        then {{grow}}(tail)(({{pushed}}) :: known)(true)
                        else {{grow}}(tail)(known)(changed)

            let recursive {{fixpoint}} (groups: List(Int)) (known: List({{elementType}})) =
                match {{grow}}(groups)(known)(false) with
                    | {{more}} -> {{fixpoint}}(groups)(nextKnown)
                    | {{done}} -> nextKnown

            let {{names.Entry}} (seed: Int) = Ashes.Collection.List.length({{fixpoint}}(seed % 3 :: {{items}})([])) + seed
            """;
    }
}
