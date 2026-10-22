using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A function hands an owned record a call built to a function returning an opaque foreign handle:
/// the handle, or a pair of them, cannot hold the record, which is released at the return.
/// </summary>
internal sealed class OpaqueHandleResultOfOwnedRecordTemplate() : LeakCombinationTemplate(
    "leak.opaque-handle-result-of-owned-record", "opaqueHandleResultOfOwnedRecord", GeneratedFeature.Record)
{
    protected override string Declarations(LeakNames names)
    {
        string handle = names.Of("LeakHandle");
        string handleOf = names.Of("handleOf");
        string box = names.Of("LeakBox");
        string make = names.Of("make");
        string register = names.Of("register");
        string registerNew = names.Of("registerNew");
        string body = names.Coin("handle-pair")
            ? $"({handleOf}(box.name), {handleOf}({names.Word()}))"
            : $"{handleOf}(box.name)";
        return $$"""
            external type {{handle}}

            external {{handleOf}}(Str) -> {{handle}} = "strlen"

            type {{box}} =
                | name: Str
                | items: List(Str)

            let {{make}} (n: Int) = {{box}}(name = {{names.Word()}} + Ashes.Text.fromInt(n), items = [{{names.Word()}} + Ashes.Text.fromInt(n), {{names.Word()}}])

            let {{register}} (box: {{box}}) = {{body}}

            let {{registerNew}} (n: Int) = {{register}}({{make}}(n))

            let {{names.Entry}} (seed: Int) =
                match {{registerNew}}(seed) with
                    | _handle -> seed
            """;
    }
}
