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
