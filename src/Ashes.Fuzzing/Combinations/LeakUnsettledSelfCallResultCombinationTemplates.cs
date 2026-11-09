using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A function's non-tail call to itself whose variant result goes into a match scrutinee tuple beside
/// a pipeline step, the way stage 1's <c>scalarCaptureLayout</c> builds its layout. The self call
/// resolves to the function's own label, whose result protocol is not settled while the body is
/// lowered, so the caller read the result as an arena value while the callee handed over a
/// reference-counted one: the base case's <c>Some([])</c> and every cell built on it were left behind.
/// </summary>
internal sealed class UnsettledSelfCallResultTemplate() : LeakCombinationTemplate(
    "leak.unsettled-self-call-result", "unsettledSelfCallResult", GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string sem = names.Of("Sem");
        string toSem = names.Of("toSem");
        string semText = names.Of("semText");
        string layout = names.Of("layout");
        int width = names.Between(1, 3);
        int stride = names.Pick("4", "8", "16") switch { "4" => 4, "8" => 8, _ => 16 };
        string items = "[" + string.Join(", ", Enumerable.Range(0, width).Select(i => i == 0 ? "seed" : i.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";
        string strideText = stride.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $$"""
            type {{sem}} =
                | SemInt
                | SemNamed(Str, List({{sem}}))

            let {{toSem}} (value: Int) =
                if value > 1000000000
                then SemNamed({{names.Word()}}, [])
                else SemInt

            let {{semText}} (semanticType: {{sem}}) =
                match semanticType with
                    | SemInt -> Some({{names.Word()}})
                    | _ -> None

            let recursive {{layout}} (values: List(Int)) (index: Int) =
                match values with
                    | [] -> Some([])
                    | value :: rest ->
                        match (value
                        |> {{toSem}}
                        |> {{semText}}, {{layout}}(rest)(index + 1)) with
                            | (Some(text), Some(found)) -> Some((index * {{strideText}}, text) :: found)
                            | _ -> None

            let {{names.Entry}} (seed: Int) =
                match {{layout}}({{items}})(0) with
                    | Some(found) -> Ashes.Collection.List.length(found)
                    | None -> 0
            """;
    }
}
