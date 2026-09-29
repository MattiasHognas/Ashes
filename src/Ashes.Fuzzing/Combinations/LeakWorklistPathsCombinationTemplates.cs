using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A worklist of paths (<c>List(List(Str))</c>) the loop walks by its tail at one self-call and
/// refills from a call's result at another (<c>walk(append(rest)(more))</c>), the shape of stage 1's
/// module graph walk. Left in the arena, each refill's result was normalized onto the
/// reference-counted heap, copied back into the arena by the back edge, and neither copy was
/// released: the whole queue per iteration. A head matched out of the list may be returned.
/// </summary>
internal sealed class WorklistOfPathsWalkedAndRefilledTemplate() : LeakCombinationTemplate(
    "leak.worklist-of-paths-walked-and-refilled", "worklistPaths", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string walk = names.Of("walkPaths");
        string word = names.Word();
        bool returnsHead = names.Coin("returns-head");
        string exitArm = returnsHead
            ? "Ashes.Text.byteLength(Ashes.Text.join(\"\")(path))"
            : "n";
        return $$"""
            let recursive {{walk}} (queue: List(List(Str))) (seen: List(Str)) (n: Int) =
                match queue with
                    | [] -> n + Ashes.Collection.List.length(seen)
                    | path :: rest ->
                        if n == 5
                        then {{exitArm}}
                        else
                            if n > 2
                            then {{walk}}(rest)((Ashes.Text.fromInt(n) + "/") :: seen)(n + Ashes.Text.byteLength(Ashes.Text.join("/")(path)))
                            else {{walk}}(Ashes.Collection.List.append(rest)([[{{word}}, Ashes.Text.fromInt(n)], [{{word}}]]))(seen)(n + 1)

            let {{names.Entry}} (seed: Int) = {{walk}}([[{{word}}], [{{word}}, Ashes.Text.fromInt(seed)]])([])(0) + seed
            """;
    }
}
