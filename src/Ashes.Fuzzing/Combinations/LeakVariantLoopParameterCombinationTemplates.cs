using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop over a variant parameter that peels its function type into a list of parameter types
/// (each a pattern field consed onto the accumulator) and returns the rest beside that list in a
/// tuple: each field and the result take one reference, not a pattern owner's duplicate beside the
/// copy that the tuple or the back edge takes of its own.
/// </summary>
internal sealed class VariantLoopParameterSplitTemplate() : LeakCombinationTemplate(
    "leak.variant-loop-parameter-split", "variantLoopParameterSplit", GeneratedFeature.Tuple, GeneratedFeature.TailCall, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string type = names.Of("LeakSem");
        string variable = names.Of("LeakVariable");
        string function = names.Of("LeakFunction");
        string named = names.Of("LeakNamed");
        string substitute = names.Of("substitute");
        string split = names.Of("splitType");
        string nameLength = names.Of("nameLength");
        bool usesResult = names.Coin("uses-result");
        string measure = usesResult
            ? $"Ashes.Collection.List.length(fields) + {nameLength}(result)"
            : "Ashes.Collection.List.length(fields)";
        return $$"""
            type {{type}} =
                | {{variable}}(Int)
                | {{function}}({{type}}, {{type}})
                | {{named}}(Int, Str, List({{type}}))

            let recursive {{substitute}} (shift: Int) (value: {{type}}) =
                match value with
                    | {{variable}}(id) -> {{variable}}(id + shift)
                    | {{function}}(argument, result) -> {{function}}({{substitute}}(shift)(argument))({{substitute}}(shift)(result))
                    | {{named}}(id, name, arguments) -> {{named}}(id)(name)(arguments)

            let recursive {{split}} value reversed =
                match value with
                    | {{function}}(parameterType, resultType) -> {{split}}(resultType)(parameterType :: reversed)
                    | resultType -> (Ashes.Collection.List.reverse(reversed), resultType)

            let {{nameLength}} (value: {{type}}) =
                match value with
                    | {{named}}(_id, name, _arguments) -> Ashes.Text.byteLength(name)
                    | _ -> 0

            let {{names.Entry}} (seed: Int) =
                match {{split}}({{substitute}}(seed)({{function}}({{variable}}(1))({{named}}(0)({{names.Word()}} + Ashes.Text.fromInt(seed))([{{variable}}(2)]))))([]) with
                    | (fields, result) -> {{measure}}
            """;
    }
}
