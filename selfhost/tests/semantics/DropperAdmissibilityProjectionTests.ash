import Ashes.Test as test
import AshesCompiler.Semantics.HeapLayoutClassification
import AshesCompiler.Semantics.StructuralDroppers
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.Types
export (
    value runDropperAdmissibilityProjectionTests,
)

let assertFacts (expected: HeapAdmissibilityFacts) (actual: HeapAdmissibilityFacts) =
    if expected == actual
    then Unit
    else test.fail("admissibility projection differs")

let box argument = SemNamed(0)("ProjectionBox")([argument])

let definitions =
    [ConstructorInferenceDefinition(name = "ProjectionBox", scheme = TypeScheme(quantified = [(7, "a")], body = SemFunction(SemVariable(7))(box(SemVariable(7)))(None), constraints = []), fieldNames = ["value"])]

let compareProjection (semanticType: SemanticType) (body: DropperBody) =
    (let expected = classifyHeapAdmissibility(semanticType)(body.environment)
    in
        body
        |> dropperAdmissibilityFacts(semanticType)
        |> assertFacts(expected)
        |> (given (_) ->
            match semanticType with
                | SemNamed(id, _name, _arguments) ->
                    let facts = classifyHeapLayout(semanticType)(body.environment)
                    in
                        let cached = body with layoutFacts = Ashes.Collection.Map.setInt(id)([(semanticType, facts)])(body.layoutFacts)
                        in
                            cached
                            |> dropperAdmissibilityFacts(semanticType)
                            |> assertFacts(expected)
                | _ -> Unit))

let recursive compareTypes types body =
    match types with
        | [] -> Unit
        | head :: rest ->
            body
            |> compareProjection(head)
            |> (given (_) -> compareTypes(rest)(body))

let runDropperAdmissibilityProjectionTests unit =
    match openDropperBody(prepareDropperTypes(definitions))(emptyDropperLabelCache)(0)(0) with
        | (ids, body) ->
            let scalar =
                SemInt
                |> box
                |> renumberType(ids)
            in
                let resource =
                    []
                    |> SemNamed(99)("FileHandle")
                    |> box
                    |> renumberType(ids)
                in
                    body
                    |> compareTypes([scalar, resource, SemList(SemString)
                    |> box
                    |> renumberType(ids), SemVariable(77)
                    |> box
                    |> renumberType(ids), None
                    |> SemFunction(SemInt)(SemInt)
                    |> box
                    |> renumberType(ids), SemTuple([SemString, SemList(SemInt)]), SemList(SemString)])
                    |> (given (_) ->
                        match scalar with
                            | SemNamed(id, _name, _args) ->
                                // A different instantiation in the same symbol bucket must miss.
                                let cached = body with layoutFacts = Ashes.Collection.Map.setInt(id)([(scalar, classifyHeapLayout(scalar)(body.environment))])(body.layoutFacts)
                                in
                                    cached
                                    |> dropperAdmissibilityFacts(resource)
                                    |> assertFacts(classifyHeapAdmissibility(resource)(body.environment))
                            | _ -> test.fail("expected named fixture"))
                    |> (given (_) -> Ashes.IO.print("dropper admissibility projection tests passed"))
