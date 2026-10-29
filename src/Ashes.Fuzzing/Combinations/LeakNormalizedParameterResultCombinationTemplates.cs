using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A function normalizing its record parameter at entry that returns a record update of it behind a
/// generic wrapper (<c>Ok(state with ...)</c>): the wrapper is normalized into an owned value at the
/// return, and the parameter, which nothing else releases, is released after that normalization.
/// </summary>
internal sealed class NormalizedParameterBehindWrappedUpdateTemplate() : LeakCombinationTemplate(
    "leak.normalized-parameter-behind-wrapped-update", "normalizedParameterBehindWrappedUpdate", GeneratedFeature.Record, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string state = names.Of("LeakState");
        string register = names.Of("register");
        return $$"""
            type {{state}} =
                | layouts: List(Str)
                | supply: Int

            let {{register}} (name: Str) (state: {{state}}) =
                match state with
                    | {{state}} { supply = supply } -> Ok(state with layouts = [name], supply = supply + {{names.Between(1, 9)}})

            let {{names.Entry}} (seed: Int) =
                match {{register}}({{names.Word()}} + Ashes.Text.fromInt(seed))({{state}}(layouts = [{{names.Word()}}], supply = seed)) with
                    | Ok({{state}} { supply = supply, layouts = layouts }) -> supply + Ashes.Collection.List.length(layouts)
                    | Error(_e) -> 0
            """;
    }
}
