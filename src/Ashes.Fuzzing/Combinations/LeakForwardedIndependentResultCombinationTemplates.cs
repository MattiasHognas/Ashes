using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop whose next record result comes from a generic helper that returns, as it is, the result
/// of a closure parameter, the closure being a function of a record type holding a recursive
/// variant that an arena copy cannot reproduce (<c>state |> loadField(index)(pattern)(lower) |>
/// lowerFields(rest)(index + 1)(lower)</c>, the helper ending in <c>lower(pattern)(temp)(state)</c>):
/// the shape of stage 1's tuple pattern field lowering. The helper asks the closure for an arena
/// result, the closure keeps its owned reference-counted one and only says it is independent, and
/// the loop copied that result and left the closure's reference unreleased.
/// </summary>
internal sealed class ForwardedIndependentResultTemplate() : LeakCombinationTemplate(
    "leak.forwarded-independent-result", "forwardedIndependent", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string ty = names.Of("Ty");
        string leaf = names.Of("Leaf");
        string nested = names.Of("Nested");
        string state = names.Of("LowerState");
        string result = names.Of("LowerResult");
        string pattern = names.Of("Pattern");
        string variable = names.Of("PatternVariable");
        string tuple = names.Of("PatternTuple");
        string emit = names.Of("emitItem");
        string fresh = names.Of("freshTemp");
        string lowerVar = names.Of("lowerVariable");
        string lowerLoaded = names.Of("lowerLoaded");
        string loadField = names.Of("loadField");
        string lowerFields = names.Of("lowerFields");
        string lowerPattern = names.Of("lowerPattern");
        bool listOfKnown = names.Coin("known-as-list");
        string knownType = listOfKnown ? $"List({ty})" : $"Maybe({ty})";
        string knownValue = listOfKnown ? $"[{nested}({leaf})]" : $"Some({nested}({leaf}))";
        string load = names.Coin("load-through-lambda")
            ? $$"""
                state
                |> {{fresh}}
                |> (given (pair) ->
                    match pair with
                        | (loadState, temp) -> ({{emit}}(temp + index)(loadState), temp))
                |> {{lowerLoaded}}(pat)(lower)
            """
            : $"    {lowerLoaded}(pat)(lower)({fresh}(state))";
        string fields = string.Join(", ", names.DistinctWords(names.Between(2, 5)).Select(word => $"{variable}({word})"));
        return $$"""
            type {{ty}} =
                | {{leaf}}
                | {{nested}}({{ty}})

            type {{state}} =
                | items: List(Int)
                | known: {{knownType}}
                | next: Int

            type {{result}} =
                | state: {{state}}
                | error: Maybe(Str)

            type {{pattern}} =
                | {{variable}}(Str)
                | {{tuple}}(List({{pattern}}))

            let {{emit}} (item: Int) (state: {{state}}) = state with items = item :: state.items

            let {{fresh}} (state: {{state}}) = ((state with next = state.next + 1), state.next)

            let {{lowerVar}} (name: Str) (temp: Int) (state: {{state}}) =
                if name == ""
                then {{result}}(state = state, error = Some(name))
                else {{result}}(state = {{emit}}(temp)(state), error = None)

            let {{lowerLoaded}} pat lower loaded =
                match loaded with
                    | (state, temp) -> lower(pat)(temp)(state)

            let {{loadField}} (index: Int) pat lower (state: {{state}}) =
            {{load}}

            let recursive {{lowerFields}} (pats: List({{pattern}})) (index: Int) lower (result: {{result}}) =
                match result with
                    | {{result}} { error = Some(_error) } -> result
                    | {{result}} { state = state } ->
                        match pats with
                            | [] -> result
                            | pat :: rest ->
                                state
                                |> {{loadField}}(index)(pat)(lower)
                                |> {{lowerFields}}(rest)(index + 1)(lower)

            let recursive {{lowerPattern}} (pat: {{pattern}}) (temp: Int) (state: {{state}}) =
                match pat with
                    | {{variable}}(name) -> {{lowerVar}}(name)(temp)(state)
                    | {{tuple}}(parts) -> {{lowerFields}}(parts)(0)({{lowerPattern}})({{result}}(state = state, error = None))

            let {{names.Entry}} (seed: Int) =
                match {{lowerPattern}}({{tuple}}([{{fields}}]))(0)({{state}}(items = [seed], known = {{knownValue}}, next = seed)) with
                    | {{result}} { state = {{state}} { next = next } } -> next
            """;
    }
}
