import AshesCompiler.Semantics.HeapLayoutClassification
import AshesCompiler.Semantics.StructuralDroppers
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.Types
export (
    value runDropperViewCacheTests,
)

let box argument = SemNamed(0)("CachedBox")([argument])

let definitions =
    [ConstructorInferenceDefinition(name = "CachedBox", scheme = TypeScheme(quantified = [(7, "a")], body = SemFunction(SemVariable(7))(box(SemVariable(7)))(None), constraints = []), fieldNames = ["value"])]

let check (condition: Bool) =
    if condition
    then Unit
    else Ashes.IO.panic("dropper view cache differs")

let runDropperViewCacheTests unit =
    match openDropperBody(prepareDropperTypes(definitions))(emptyDropperLabelCache)(0)(0) with
        | (ids, opened) ->
            let scalar =
                SemInt
                |> box
                |> renumberType(ids)
            in
                let owned =
                    SemList(SemString)
                    |> box
                    |> renumberType(ids)
                in
                    let cached = cacheDropperLayout(scalar)(opened)
                    in
                        let full = classifyHeapLayout(scalar)(opened.environment)
                        in
                            let authoritative = full with containsResource = !full.containsResource
                            in
                                let both = cached with layoutFacts = Ashes.Collection.Map.setInt(0)([(scalar, authoritative)])(cached.layoutFacts)
                                in
                                    dropperLayoutFacts(scalar)(cached) == projectHeapDropperFacts(full)
                                    |> check
                                    |> (given (_) ->
                                        check(dropperLayoutFacts(owned)(cached) == projectHeapDropperFacts(classifyHeapLayout(owned)(opened.environment))))
                                    |> (given (_) -> check(dropperLayoutFacts(scalar)(both) == projectHeapDropperFacts(authoritative)))
                                    |> (given (_) ->
                                        let admissibility = dropperAdmissibilityFacts(scalar)(both)
                                        in check(admissibility.containsResource == authoritative.containsResource))
                                    |> (given (_) ->
                                        match Ashes.Collection.Map.getInt(0)(cached.layoutFacts) with
                                            | None -> Unit
                                            | Some(_facts) -> Ashes.IO.panic("full cache populated"))
                                    |> (given (_) ->
                                        check(dropperLayoutFacts(scalar)(beginSynthesizedBody(cached)) == projectHeapDropperFacts(full)))
                                    |> (given (_) -> Ashes.IO.print("dropper cache tests passed"))
