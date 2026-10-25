using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// The standard library's <c>map</c> passed as a value, so the call reaches its generic list
/// builder rather than an element specialization, over a closure returning a fresh value of a
/// recursive type or its argument as it is: the builder's cells hand the heads over to the caller,
/// whose copy of the list releases them.
/// </summary>
internal sealed class GenericMapPassedAsValueKeepsClosureResultsTemplate() : LeakCombinationTemplate(
    "leak.generic-map-passed-as-value-keeps-closure-results", "genericMapPassedAsValue", GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string type = names.Of("LeakSem");
        string leaf = names.Of("LeakLeaf");
        string named = names.Of("LeakNamed");
        string renumber = names.Of("renumber");
        string viaMapper = names.Of("viaMapper");
        string measure = names.Of("measure");
        bool identity = names.Coin("identity-closure");
        string closure = identity ? "(given (value) -> value)" : $"{renumber}(1)";
        return $$"""
            type {{type}} =
                | {{leaf}}(Int)
                | {{named}}(Int, List({{type}}))

            let {{renumber}} (shift: Int) (value: {{type}}) =
                match value with
                    | {{named}}(id, arguments) -> {{named}}(id + shift)(arguments)
                    | other -> other

            let {{viaMapper}} mapper f xs = mapper(f)(xs)

            let recursive {{measure}} (values: List({{type}})) (total: Int) =
                match values with
                    | [] -> total
                    | {{leaf}}(v) :: rest -> {{measure}}(rest)(total + v)
                    | _ :: rest -> {{measure}}(rest)(total + 1)

            let {{names.Entry}} (seed: Int) = {{measure}}({{viaMapper}}(Ashes.Collection.List.map)({{closure}})([{{leaf}}(seed), {{named}}(seed)([{{leaf}}(seed + 1)])]))(0)
            """;
    }
}
