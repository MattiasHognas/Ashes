import AshesCompiler.Semantics.HeapLayoutClassification
import AshesCompiler.Semantics.StructuralDroppers
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.Types
let named name = SemNamed(0)(name)([])

let definitions name =
    [ConstructorInferenceDefinition(name = name, scheme = TypeScheme(quantified = [], body = SemFunction(SemList(SemString))(named(name))(None), constraints = []), fieldNames = ["items"])]

let query n cached =
    (let name = "Plateau" + Ashes.Text.fromInt(n)
    in
        match openDropperBody(name
        |> definitions
        |> prepareDropperTypes)(emptyDropperLabelCache)(0)(0) with
            | (ids, body) ->
                let semanticType =
                    name
                    |> named
                    |> renumberType(ids)
                in
                    let prepared =
                        if cached
                        then body with layoutFacts = Ashes.Collection.Map.setInt(0)([(semanticType, classifyHeapLayout(semanticType)(body.environment))])(body.layoutFacts)
                        else body
                    in
                        match dropperAdmissibilityFacts(semanticType)(prepared) with
                            | HeapAdmissibilityFacts { containsResource = resource, containsUnresolvedType = unresolved, children = children } -> !resource && !unresolved && children != [])

let recursive rounds n cached =
    if n == 0
    then Ashes.IO.print("done")
    else
        if query(n)(cached)
        then rounds(n - 1)(cached)
        else Ashes.IO.panic("unexpected facts")

match Ashes.IO.args with
    | "cached" :: "1000" :: [] -> rounds(1000)(true)
    | "cached" :: "10000" :: [] -> rounds(10000)(true)
    | "miss" :: "1000" :: [] -> rounds(1000)(false)
    | "miss" :: "10000" :: [] -> rounds(10000)(false)
    | _ -> Ashes.IO.panic("mode count")
