using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>A shared call-result wrapper keeps its field reference when a match adopts that field.</summary>
internal sealed class SharedCallResultFieldTemplate() : LeakCombinationTemplate(
    "leak.shared-call-result-field", "sharedCallResultField", GeneratedFeature.Match, GeneratedFeature.TailCall)
{
    protected override IReadOnlyList<string> RequiredImports => ["Ashes.Collection.Map"];

    protected override string Declarations(LeakNames names)
    {
        string lookup = names.Of("Lookup");
        string handoff = names.Of("Handoff");
        string walk = names.Of("Walk");
        string key = names.Word();
        return $$"""
            type {{names.Of("Ownership")}} =
                | {{names.Of("Borrowed")}}
                | {{names.Of("Consumed")}}

            let {{lookup}} (name: Str) (index: MapTree(Str, Maybe(List((Str, {{names.Of("Ownership")}}))))) =
                match Ashes.Collection.Map.getStr(name)(index) with
                    | Some(value) -> value
                    | None -> None

            let {{handoff}} (index: MapTree(Str, Maybe(List((Str, {{names.Of("Ownership")}}))))) (shadowed: List(Str)) (name: Str) (count: Int) =
                if Ashes.Collection.List.length(shadowed) != 0
                then None
                else
                    match {{lookup}}(name)(index) with
                        | Some(values) ->
                            if Ashes.Collection.List.length(values) == count
                            then Some(values)
                            else None
                        | None -> None

            let recursive {{walk}} remaining index total =
                if remaining == 0
                then total
                else
                    match {{handoff}}(index)([])({{key}})(1) with
                        | Some(values) -> {{walk}}(remaining - 1)(index)(total + Ashes.Collection.List.length(values))
                        | None -> total

            let {{names.Entry}} (seed: Int) =
                {{walk}}(3)(Ashes.Collection.Map.setStr({{key}})(Some([({{names.Word()}}, {{names.Of("Borrowed")}})]))(Ashes.Collection.Map.empty))(seed)
            """;
    }
}
