using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop matching a tuple of its list of string-holding constructors and a record parameter,
/// passing the list's tail on: the list is copied into the reference-counted tuple and its tail
/// retained for the next iteration, so each iteration releases the list it was given.
/// </summary>
internal sealed class GeneralListTailMatchedInTupleTemplate() : LeakCombinationTemplate(
    "leak.general-list-tail-matched-in-tuple", "generalListTailMatchedInTuple", GeneratedFeature.Record, GeneratedFeature.Adt, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string pattern = names.Of("LeakPat");
        string result = names.Of("LeakWalked");
        string walk = names.Of("walk");
        return $$"""
            type {{pattern}} =
                | PVar(Str)
                | PTuple(List({{pattern}}))

            type {{result}} =
                | total: Int
                | names: List(Str)

            let recursive {{walk}} (pats: List({{pattern}})) (walked: {{result}}) =
                match (pats, walked) with
                    | ([], _walked) -> walked
                    | (_pat :: rest, {{result}} { total = total, names = known }) -> {{walk}}(rest)({{result}}(total = total + {{names.Between(1, 9)}}, names = known))

            let {{names.Entry}} (seed: Int) =
                match {{walk}}([PVar("a"), PTuple([PVar("b")]), PVar("c")])({{result}}(total = seed, names = ["x"])) with
                    | {{result}} { total = t } -> t
            """;
    }
}
