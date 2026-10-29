using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A function with a generic error type returns an owned call result as it is in its <c>Ok</c>
/// arm, called directly or through a function returning its result as it is: the caller, which
/// knows the concrete type, asks for the result and takes it over.
/// </summary>
internal sealed class GenericResultHandsOverOwnedCallResultTemplate() : LeakCombinationTemplate(
    "leak.generic-result-hands-over-owned-call-result", "genericResultHandsOverOwnedCallResult", GeneratedFeature.Record, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string collection = names.Of("LeakCollection");
        string loaded = names.Of("LeakLoaded");
        string make = names.Of("make");
        string complete = names.Of("complete");
        string finish = names.Of("finish");
        string finishAll = names.Of("finishAll");
        string totalOf = names.Of("totalOf");
        string callee = names.Coin("forwarded") ? finishAll : finish;
        return $$"""
            type {{collection}} =
                | items: List(Str)
                | count: Int

            type {{loaded}} =
                | names: List(Str)
                | total: Int

            let {{make}} (n: Int) = {{collection}}(items = [{{names.Word()}} + Ashes.Text.fromInt(n), {{names.Word()}}], count = n)

            let {{complete}} (collection: {{collection}}) =
                match collection with
                    | {{collection}} { items = items, count = count } ->
                        {{loaded}}(names = items, total = count)
                        |> Ashes.Internal.deepCopy
                        |> Ok

            let {{finish}} (collection: {{collection}}) (result: Result(e, Str)) =
                match result with
                    | Error(error) -> Error(error)
                    | Ok(_outer) -> {{complete}}(collection)

            let {{finishAll}} (collection: {{collection}}) (result: Result(e, Str)) = {{finish}}(collection)(result)

            let {{totalOf}} (result: Result(Str, {{loaded}})) =
                match result with
                    | Error(_) -> 0
                    | Ok({{loaded}} { total = total }) -> total

            let {{names.Entry}} (seed: Int) = {{totalOf}}({{callee}}({{make}}(seed))(Ok({{names.Word()}})))
            """;
    }
}

/// <summary>
/// A function with a generic error type wraps an owned call result in <c>Ok</c>: the caller, which
/// knows the concrete type, asks for the result and takes over a copy holding the owned value.
/// </summary>
internal sealed class GenericResultWrapsOwnedCallResultTemplate() : LeakCombinationTemplate(
    "leak.generic-result-wraps-owned-call-result", "genericResultWrapsOwnedCallResult", GeneratedFeature.Record, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string collection = names.Of("LeakCollection");
        string make = names.Of("make");
        string prepend = names.Of("prepend");
        string continueParsing = names.Of("continueParsing");
        string countOf = names.Of("countOf");
        return $$"""
            type {{collection}} =
                | items: List(Str)
                | count: Int

            let {{make}} (n: Int) = {{collection}}(items = [{{names.Word()}} + Ashes.Text.fromInt(n), {{names.Word()}}], count = n)

            let {{prepend}} (extra: Str) (collection: {{collection}}) =
                match collection with
                    | {{collection}} { items = items, count = count } -> {{collection}}(items = extra :: items, count = count + 1)

            let {{continueParsing}} (extra: Str) (tailResult: Result(e, {{collection}})) =
                match tailResult with
                    | Error(error) -> Error(error)
                    | Ok(collection) ->
                        collection
                        |> {{prepend}}(extra)
                        |> Ok

            let {{countOf}} (result: Result(Str, {{collection}})) =
                match result with
                    | Error(_) -> 0
                    | Ok({{collection}} { count = count }) -> count

            let {{names.Entry}} (seed: Int) = {{countOf}}({{continueParsing}}({{names.Word()}})(Ok({{make}}(seed))))
            """;
    }
}

/// <summary>
/// A function returns its contract-typed parameter as it is: its entry adopts the owned call result
/// its caller hands over, and its return hands that reference on rather than retaining it again.
/// </summary>
internal sealed class ContractParameterReturnedAsItIsTemplate() : LeakCombinationTemplate(
    "leak.contract-parameter-returned-as-is", "contractParameterReturnedAsItIs", GeneratedFeature.Record, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string collection = names.Of("LeakCollection");
        string make = names.Of("make");
        string wrap = names.Of("wrap");
        string pinned = names.Of("pinned");
        string countOf = names.Of("countOf");
        return $$"""
            type {{collection}} =
                | items: List(Str)
                | count: Int

            let {{make}} (n: Int) = {{collection}}(items = [{{names.Word()}} + Ashes.Text.fromInt(n), {{names.Word()}}], count = n)

            let {{wrap}} (n: Int) = Ok({{make}}(n))

            let {{pinned}} (result: Result(Str, {{collection}})) = result

            let {{countOf}} (result: Result(Str, {{collection}})) =
                match result with
                    | Error(_) -> 0
                    | Ok({{collection}} { count = count }) -> count

            let {{names.Entry}} (seed: Int) = {{countOf}}({{pinned}}({{wrap}}(seed)))
            """;
    }
}

/// <summary>
/// A function with a generic error type passes an owned value to a call of the contract's types and
/// returns that call's result: the result holds references of its own, so the owned value is
/// released at the return.
/// </summary>
internal sealed class OwnedValueBehindContractCallResultTemplate() : LeakCombinationTemplate(
    "leak.owned-value-behind-contract-call-result", "ownedValueBehindContractCallResult", GeneratedFeature.Record, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string collection = names.Of("LeakCollection");
        string loaded = names.Of("LeakLoaded");
        string make = names.Of("make");
        string prepend = names.Of("prepend");
        string complete = names.Of("complete");
        string finish = names.Of("finish");
        string totalOf = names.Of("totalOf");
        return $$"""
            type {{collection}} =
                | items: List(Str)
                | count: Int

            type {{loaded}} =
                | names: List(Str)
                | total: Int

            let {{make}} (n: Int) = {{collection}}(items = [{{names.Word()}} + Ashes.Text.fromInt(n), {{names.Word()}}], count = n)

            let {{prepend}} (extra: Str) (collection: {{collection}}) =
                match collection with
                    | {{collection}} { items = items, count = count } -> {{collection}}(items = extra :: items, count = count + 1)

            let {{complete}} (collection: {{collection}}) =
                match collection with
                    | {{collection}} { items = items, count = count } ->
                        {{loaded}}(names = items, total = count)
                        |> Ashes.Internal.deepCopy
                        |> Ok

            let {{finish}} (collection: {{collection}}) (result: Result(e, Str)) =
                match result with
                    | Error(error) -> Error(error)
                    | Ok(outer) ->
                        collection
                        |> {{prepend}}(outer)
                        |> {{complete}}

            let {{totalOf}} (result: Result(Str, {{loaded}})) =
                match result with
                    | Error(_) -> 0
                    | Ok({{loaded}} { total = total }) -> total

            let {{names.Entry}} (seed: Int) = {{totalOf}}({{finish}}({{make}}(seed))(Ok({{names.Word()}})))
            """;
    }
}

/// <summary>
/// A function returns, as it is, a generic call's result built from owned values it passed in: the
/// innermost function's contract result holds references of its own and says so through the
/// caller's mask, so every forwarding function releases the owned values behind it.
/// </summary>
internal sealed class OwnedValuesBehindForwardedContractResultTemplate() : LeakCombinationTemplate(
    "leak.owned-values-behind-forwarded-contract-result", "ownedValuesBehindForwardedContractResult", GeneratedFeature.Record, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string state = names.Of("LeakState");
        string fresh = names.Of("LeakFresh");
        string lowered = names.Of("LeakLowered");
        string freshTemp = names.Of("freshTemp");
        string loaded = names.Of("loaded");
        string loadField = names.Of("loadField");
        string lowerPattern = names.Of("lowerPattern");
        return $$"""
            type {{state}} =
                | items: List(Str)
                | n: Int

            type {{fresh}} =
                | state: {{state}}
                | temp: Int

            type {{lowered}} =
                | state: {{state}}
                | ok: Bool

            let {{freshTemp}} (state: {{state}}) =
                match state with
                    | {{state}} { items = items, n = n } -> {{fresh}}(state = {{state}}(items = items, n = n + 1), temp = n)

            let {{loaded}} pattern lower fresh =
                match fresh with
                    | {{fresh}} { state = state, temp = temp } -> lower(pattern)(temp)(state)

            let {{loadField}} pattern lower (state: {{state}}) =
                state
                |> {{freshTemp}}
                |> (given (fresh) ->
                    match fresh with
                        | {{fresh}} { state = {{state}} { items = items, n = n }, temp = temp } -> {{fresh}}(state = {{state}}(items = {{names.Word()}} :: items, n = n), temp = temp))
                |> {{loaded}}(pattern)(lower)

            let {{lowerPattern}} (pattern: Str) (temp: Int) (state: {{state}}) = {{lowered}}(state = state, ok = temp > 0)

            let {{names.Entry}} (seed: Int) =
                match {{loadField}}({{names.Word()}})({{lowerPattern}})({{state}}(items = [{{names.Word()}} + Ashes.Text.fromInt(seed)], n = seed)) with
                    | {{lowered}} { state = {{state}} { n = n } } -> n
            """;
    }
}
