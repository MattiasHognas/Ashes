using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>Nullary constructor tests do not make fresh successor constructors aliases of old lookup results.</summary>
internal sealed class ResolvedNullaryPatternBinderTemplate() : LeakCombinationTemplate(
    "leak.resolved-nullary-pattern-binder", "resolvedNullaryPatternBinder", GeneratedFeature.Match, GeneratedFeature.TailCall)
{
    protected override IReadOnlyList<string> RequiredImports => ["Ashes.Collection.Map"];

    protected override string Declarations(LeakNames names)
    {
        string build = names.Of("Build");
        return $$"""
            type {{names.Of("Ownership")}} =
                | {{names.Of("Borrowed")}}
                | {{names.Of("Consumed")}}

            let recursive {{build}} (entries: List((Str, List((Str, {{names.Of("Ownership")}}))))) (index: MapTree(Str, Maybe(List((Str, {{names.Of("Ownership")}}))))) =
                match entries with
                    | [] -> index
                    | (name, values) :: rest ->
                        let entry =
                            match Ashes.Collection.Map.getStr(name)(index) with
                                | None -> Some(values)
                                | Some(_previous) -> None
                        in {{build}}(rest)(Ashes.Collection.Map.setStr(name)(entry)(index))

            let {{names.Entry}} (seed: Int) =
                Ashes.Collection.Map.size({{build}}([({{names.Word()}}, [("h", {{names.Of("Borrowed")}})]), ({{names.Word()}}, [("h", {{names.Of("Consumed")}})]), ({{names.Word()}}, [("h", {{names.Of("Borrowed")}})])])(Ashes.Collection.Map.empty)) + seed
            """;
    }
}
