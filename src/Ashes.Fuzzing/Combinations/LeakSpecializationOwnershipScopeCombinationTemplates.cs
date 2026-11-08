using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A generic list walker whose parameter shares its name with a pattern binding of the caller: the
/// caller binds <c>parameters</c> out of a reference-counted tuple join, then calls the walker, whose
/// element specialization is lowered on demand right there, the way stage 1's parser hands a binding's
/// sugar parameters to <c>parserParameterNames</c>. The specialization resolved its own pattern
/// owners through the caller's ownership aliases, found the caller's reference-counted tuple, and
/// retained each tail at its back edge for an owner whose release it never emitted.
/// </summary>
internal sealed class SpecializationOwnershipScopeTemplate() : LeakCombinationTemplate(
    "leak.specialization-ownership-scope", "specializationOwnershipScope", GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string parse = names.Of("parseParams");
        string walker = names.Of("parameterNames");
        string mark = names.Pick("!", "?", "_t");
        string second = names.Word();
        return $$"""
            let recursive {{parse}} reversed (tokens: List(Str)) =
                match tokens with
                    | [] -> (Ashes.Collection.List.reverse(reversed), 0)
                    | parameter :: rest -> {{parse}}((parameter, Some(parameter + "{{mark}}")) :: reversed)(rest)

            let recursive {{walker}} parameters =
                match parameters with
                    | [] -> []
                    | (name, _annotation) :: tail -> name :: {{walker}}(tail)

            let {{names.Entry}} (seed: Int) =
                (let header =
                    if seed < 0
                    then ([], [seed])
                    else
                        match {{parse}}([])([{{names.Word()}} + Ashes.Text.fromInt(seed), {{second}}]) with
                            | (parameters, after) -> (parameters, [after])
                in
                    match header with
                        | (parameters, requirements) -> Ashes.Collection.List.length({{walker}}(parameters)) + Ashes.Collection.List.length(requirements))
            """;
    }
}
