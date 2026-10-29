using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop matching a tuple of a list and a single-constructor box parameter that rebuilds the box
/// around the value it bound out of it (<c>(n :: rest, Box(inner)) -> close(rest)(Box(inner))</c>).
/// The binder is a pattern owner, and the duplicate taken of its read is the reference the new box
/// carries; a transfer retain on top of it kept the value alive past every release.
/// </summary>
internal sealed class ConstructorAroundTupleBoundOwnerTemplate() : LeakCombinationTemplate(
    "leak.constructor-around-tuple-bound-owner", "constructorAroundTupleBoundOwner", GeneratedFeature.Adt, GeneratedFeature.Record, GeneratedFeature.Match, GeneratedFeature.Tuple)
{
    protected override string Declarations(LeakNames names)
    {
        string value = names.Of("LeakKept");
        string box = names.Of("LeakBox");
        string close = names.Of("close");
        string kept = $"{value}(state = [seed, {LeakNames.Text(names.Between(1, 9))}], temp = seed)";
        string pending = $"[{LeakNames.Text(names.Between(1, 9))}, seed, {LeakNames.Text(names.Between(1, 9))}]";
        return names.Variant("wrapped", 3) switch
        {
            0 => $$"""
                type {{value}} =
                    | state: List(Int)
                    | temp: Int

                type {{box}} =
                    | {{box}}({{value}})

                let recursive {{close}} (pending: List(Int)) (held: {{box}}) (acc: Int) =
                    match (pending, held) with
                        | ([], {{box}}({{value}} { state = st, temp = t })) -> acc + t + Ashes.Collection.List.length(st)
                        | (n :: rest, {{box}}(inner)) -> {{close}}(rest)({{box}}(inner))(acc + n)

                let {{names.Entry}} (seed: Int) =
                    {{close}}({{pending}})({{box}}({{kept}}))(seed)
                """,
            1 => $$"""
                type {{value}} =
                    | state: List(Int)
                    | temp: Int

                type {{box}} =
                    | {{box}}({{value}}, Int)

                let recursive {{close}} (pending: List(Int)) (held: {{box}}) =
                    match (pending, held) with
                        | ([], {{box}}({{value}} { state = st, temp = t }, c)) -> c + t + Ashes.Collection.List.length(st)
                        | (n :: rest, {{box}}(inner, c)) -> {{close}}(rest)({{box}}(inner, c + n))

                let {{names.Entry}} (seed: Int) =
                    {{close}}({{pending}})({{box}}({{kept}}, seed))
                """,
            _ => $$"""
                type {{box}} =
                    | {{box}}(List(Int))

                let recursive {{close}} (pending: List(Int)) (held: {{box}}) (acc: Int) =
                    match (pending, held) with
                        | ([], {{box}}(st)) -> acc + Ashes.Collection.List.length(st)
                        | (n :: rest, {{box}}(inner)) -> {{close}}(rest)({{box}}(inner))(acc + n)

                let {{names.Entry}} (seed: Int) =
                    {{close}}({{pending}})({{box}}([seed, {{LeakNames.Text(names.Between(1, 9))}}]))(seed)
                """,
        };
    }
}
