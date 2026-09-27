using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A list mapped into an enum's nullary constructors and matched on: <c>map</c> specializes for the
/// element, whose cells are reference-counted, and the caller has no fixed copy-out for such a list.
/// </summary>
internal sealed class MappedNullaryEnumListTemplate() : LeakCombinationTemplate(
    "leak.mapped-nullary-enum-list", "mappedNullaryEnum", GeneratedFeature.Adt, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string type = names.Of("KindType");
        string first = names.Of("KindFirst");
        string second = names.Of("KindSecond");
        string third = names.Of("KindThird");
        string kindOf = names.Of("kindOf");
        string consume = names.Of("consumeKinds");
        bool counted = names.Coin("counted");
        string consumer = counted
            ? $$"""
                let recursive {{consume}} (kinds: List({{type}})) (total: Int) =
                    match kinds with
                        | [] -> total
                        | {{first}} :: rest -> {{consume}}(rest)(total + 1)
                        | _ :: rest -> {{consume}}(rest)(total)
                """
            : $$"""
                let {{consume}} (kinds: List({{type}})) (total: Int) =
                    match kinds with
                        | {{first}} :: _ -> total + 1
                        | {{second}} :: {{first}} :: _ -> total + 2
                        | [] -> total
                        | _ -> total + 3
                """;
        return $$"""
            type {{type}} =
                | {{first}}
                | {{second}}
                | {{third}}

            let {{kindOf}} (n: Int) =
                if n % 3 == 0
                then {{first}}
                else
                    if n % 3 == 1
                    then {{second}}
                    else {{third}}

            {{consumer}}

            let {{names.Entry}} (seed: Int) = {{consume}}(Ashes.Collection.List.map({{kindOf}})([seed, seed + 1, seed + 2, seed + 3]))(0) + seed
            """;
    }
}
