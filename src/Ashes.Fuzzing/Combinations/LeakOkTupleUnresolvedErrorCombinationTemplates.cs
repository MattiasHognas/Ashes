using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A function whose first arm returns <c>Ok</c> of a fresh tuple of lists, before any arm has given
/// the result its error type: the tuple is a fresh reference-counted child of an arena <c>Ok</c>,
/// handed to an owned slot the function releases once its result has taken a reference of its own.
/// </summary>
internal sealed class OkTupleBeforeErrorTypeTemplate() : LeakCombinationTemplate(
    "leak.ok-tuple-before-error-type", "okTupleBeforeErrorType", GeneratedFeature.Tuple, GeneratedFeature.Match, GeneratedFeature.Adt)
{
    protected override string Declarations(LeakNames names)
    {
        string produce = names.Of("produce");
        string consume = names.Of("consume");
        string limit = LeakNames.Text(names.Between(50, 90));
        string okValue = $"Ok(([k, k + {LeakNames.Text(names.Between(1, 9))}], [k * 2]))";
        string body = names.Coin("if-first")
            ? $$"""
                if k < {{limit}}
                    then {{okValue}}
                    else Error("too large")
                """
            : $$"""
                match k % {{LeakNames.Text(names.Between(2, 5))}} with
                        | 0 -> {{okValue}}
                        | 1 -> Error("odd")
                        | _ -> {{okValue}}
                """;
        return $$"""
            let {{produce}} (k: Int) =
                {{body}}

            let {{consume}} (k: Int) =
                match {{produce}}(k) with
                    | Ok((first, second)) -> Ashes.Collection.List.length(first) + Ashes.Collection.List.length(second)
                    | Error(_) -> 0

            let {{names.Entry}} (seed: Int) = {{consume}}(seed % 40) + {{consume}}(seed % 40 + 1)
            """;
    }
}
