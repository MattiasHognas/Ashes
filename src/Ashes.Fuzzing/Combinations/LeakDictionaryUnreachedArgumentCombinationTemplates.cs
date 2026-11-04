using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A generic loop that looks each site up in an association list through a constrained (Eq) lookup
/// and keeps the pair of the site and the value found, fed by a loop that hands its accumulated sites
/// out in a pair with another of its parameters, the way stage 1's optimizer fills
/// <c>computeKnownCapturedClosureLabels</c> from <c>knownReturned</c>. The lookup's result never holds
/// its key, so the reference the site's pattern owner took for that result is released after the
/// call; parked for the loop's successors, it stayed with nobody on the arm that kept the result.
/// </summary>
internal sealed class DictionaryUnreachedArgumentTemplate() : LeakCombinationTemplate(
    "leak.dictionary-unreached-argument", "dictionaryUnreached", GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string lookup = names.Of("lookup");
        string pairsOf = names.Of("pairsOf");
        string collectSites = names.Of("collectSites");
        string capture = names.Of("capture");
        string countOf = names.Of("countOf");
        int width = names.Between(2, 4);
        string word = names.Pick("ash", "ember", "coal");
        string items = "[" + string.Join(", ", Enumerable.Range(0, width).Select(i => "\"" + word + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"")) + "]";
        return $$"""
            let recursive {{lookup}} key entries =
                match entries with
                    | [] -> None
                    | (k, v) :: tail ->
                        if k == key
                        then Some(v)
                        else {{lookup}}(key)(tail)

            let recursive {{pairsOf}} (names: List(Str)) acc =
                match names with
                    | [] -> acc
                    | name :: rest -> {{pairsOf}}(rest)((name, name + "_next") :: acc)

            let recursive {{collectSites}} (names: List(Str)) (sites: List(Str)) (bad: List(Str)) =
                match names with
                    | [] -> (sites, bad)
                    | name :: rest -> {{collectSites}}(rest)(name :: sites)(bad)

            let recursive {{capture}} (sites: List(Str)) acc (bad: List(Str)) known =
                match sites with
                    | [] -> acc
                    | site :: rest ->
                        match {{lookup}}(site)(known) with
                            | Some(label) -> {{capture}}(rest)((site, label) :: acc)(bad)(known)
                            | None -> {{capture}}(rest)(acc)(bad)(known)

            let recursive {{countOf}} (xs: List((Str, Str))) =
                match xs with
                    | [] -> 0
                    | _ :: rest -> 1 + {{countOf}}(rest)

            let {{names.Entry}} (seed: Int) =
                (let all = ("e" + Ashes.Text.fromInt(seed)) :: {{items}}
                in
                    let known = {{pairsOf}}(all)([])
                    in
                        match {{collectSites}}(all)([])([]) with
                            | (sites, unresolvable) -> {{countOf}}({{capture}}(sites)([])(unresolvable)(known)))
            """;
    }
}
