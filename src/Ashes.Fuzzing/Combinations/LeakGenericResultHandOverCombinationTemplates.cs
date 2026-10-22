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
