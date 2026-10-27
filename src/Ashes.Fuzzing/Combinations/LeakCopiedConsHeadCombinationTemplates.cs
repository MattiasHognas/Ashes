using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop consing a record that holds one of its parameters onto a list it threads (the
/// optimizer's capture-site collection, <c>Site(creator = facts) :: sites</c>): the back edge copies
/// the arena record, so the retain its construction took of the parameter is withdrawn.
/// </summary>
internal sealed class CopiedConsHeadTemplate() : LeakCombinationTemplate(
    "leak.copied-cons-head", "copiedConsHead", GeneratedFeature.Record, GeneratedFeature.TailCall, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string facts = names.Of("LeakFacts");
        string site = names.Of("LeakSite");
        string add = names.Of("addSites");
        string body = names.Of("sitesInBody");
        string collect = names.Of("collectSites");
        string word = names.Word();
        int skipped = names.Between(1, 2);
        return $$"""
            type {{facts}} =
                | defs: List(Str)
                | names: List(Str)

            type {{site}} =
                | target: Str
                | index: Int
                | creator: {{facts}}

            let recursive {{add}} (label: Str) (index: Int) (count: Int) (facts: {{facts}}) sites unresolvable =
                if index >= count
                then (sites, unresolvable)
                else
                    if index == {{skipped}}
                    then {{add}}(label)(index + 1)(count)(facts)(sites)((label, index) :: unresolvable)
                    else {{add}}(label)(index + 1)(count)(facts)({{site}}(target = label, index = index, creator = facts) :: sites)(unresolvable)

            let recursive {{body}} (items: List(Int)) (label: Str) (facts: {{facts}}) sites unresolvable =
                match items with
                    | [] -> (sites, unresolvable)
                    | item :: rest ->
                        match {{add}}(label + Ashes.Text.fromInt(item))(0)(item)(facts)(sites)(unresolvable) with
                            | (nextSites, nextUnresolvable) -> {{body}}(rest)(label)(facts)(nextSites)(nextUnresolvable)

            let recursive {{collect}} (functions: List(List(Int))) sites unresolvable =
                match functions with
                    | [] -> (sites, unresolvable)
                    | fn :: tail ->
                        match {{body}}(fn)({{word}})({{facts}}(defs = Ashes.Collection.List.map(given (x) -> Ashes.Text.fromInt(x))(fn), names = [{{word}} + "!"]))(sites)(unresolvable) with
                            | (nextSites, nextUnresolvable) -> {{collect}}(tail)(nextSites)(nextUnresolvable)

            let {{names.Entry}} (seed: Int) =
                match {{collect}}([[1, 2, 3], [seed, 2]])([])([]) with
                    | ([], []) -> 0
                    | (found, _) -> Ashes.Collection.List.length(found)
            """;
    }
}
