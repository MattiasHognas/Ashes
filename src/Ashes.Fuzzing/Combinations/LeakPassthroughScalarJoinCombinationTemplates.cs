using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A <c>(Bool, Int)</c> guard threaded through helpers that return it unchanged on some arms and a
/// fresh tuple on the others, the way stage 1's call lowering resolves a handed-over release guard.
/// Each helper's passthrough arm retained the parameter into an owned tuple, but the join of a
/// scalar-only tuple was not normalized, so the result the function promised reference-counted
/// retained the joined value a second time at the return, and one reference leaked per helper.
/// </summary>
internal sealed class PassthroughScalarJoinTemplate() : LeakCombinationTemplate(
    "leak.passthrough-scalar-join", "passthroughScalarJoin", GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string makeGuard = names.Of("makeGuard");
        string severs = names.Of("severs");
        string normalized = names.Of("normalized");
        int limit = names.Between(2, 9);
        string limitText = LeakNames.Text(limit);
        string seversBody = names.Coin("match-helper")
            ? $"    match guard with\n        | (false, _flag) ->\n            if seed > {limitText}\n            then (true, seed)\n            else guard\n        | _ -> guard"
            : $"    if seed > {limitText}\n    then (true, -1)\n    else guard";
        return $$"""
            let {{makeGuard}} (seed: Int) =
                if seed > 100
                then (true, -1)
                else (seed > 1, seed)

            let {{severs}} (seed: Int) (guard: (Bool, Int)) =
            {{seversBody}}

            let {{normalized}} (flag: Bool) (guard: (Bool, Int)) =
                if flag
                then (true, -1)
                else guard

            let {{names.Entry}} (seed: Int) =
                match seed
                |> {{makeGuard}}
                |> {{severs}}(seed)
                |> {{normalized}}(seed > 1000) with
                    | (cut, flag) ->
                        if cut
                        then flag + 2
                        else flag
            """;
    }
}
