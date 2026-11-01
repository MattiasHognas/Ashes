using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop consing constructors of different sizes holding only scalars (`TyInt | TyVar(Int)`) onto
/// its accumulator: its callers reclaim such a list with the arena, so its cells stay there instead
/// of being built on the reference-counted heap and stranded.
/// </summary>
internal sealed class MixedScalarConstructorAccumulatorTemplate() : LeakCombinationTemplate(
    "leak.mixed-scalar-constructor-accumulator", "mixedScalarConstructorAccumulator", GeneratedFeature.Adt, GeneratedFeature.TailCall, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string type = names.Of("LeakTy");
        string fresh = names.Of("freshTypes");
        return $$"""
            type {{type}} =
                | TyInt
                | TyVar(Int)

            let recursive {{fresh}} (count: Int) (acc: List({{type}})) =
                if count == 0
                then acc
                else {{fresh}}(count - 1)(TyVar(count) :: acc)

            let {{names.Entry}} (seed: Int) =
                match {{fresh}}({{names.Between(1, 9)}})([TyInt]) with
                    | [] -> seed
                    | _ :: rest -> seed + Ashes.Collection.List.length(rest)
            """;
    }
}
