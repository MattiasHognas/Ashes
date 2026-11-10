import AshesCompiler.Semantics.StructuralDroppers
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.Types
let recursive constructorBody fields result =
    match fields with
        | [] -> result
        | field :: rest ->
            SemFunction(field)(constructorBody(rest)(result))(None)

let definition name fields result = ConstructorInferenceDefinition(name = name, scheme = TypeScheme(quantified = [], body = constructorBody(fields)(result), constraints = []), fieldNames = [])

let definitions =
    [
        []
        |> SemNamed(9)("B")
        |> definition("B0")([]),
        []
        |> SemNamed(9)("A")
        |> definition("A0")([SemInt]),
        []
        |> SemNamed(70)("B")
        |> definition("B1")([SemString]),
        [SemVariable(7)]
        |> SemNamed(9)("G")
        |> definition("G0")([SemNamed(9)("A")([]), SemList(SemVariable(7)), SemTuple([SemNamed(9)("B")([]), SemNamed(77)("Unknown")([])])]),
        definition("Invalid")([SemInt])(SemInt)
    ]

let idsCorrect (types: DropperTypes) =
    match types.dropperTypeIds with
        | ("B", 0) :: ("A", 1) :: ("G", 2) :: [] -> true
        | _ -> false

let bodyCorrect (semanticType: SemanticType) =
    match semanticType with
        | SemFunction(SemNamed(1, "A", []), SemFunction(SemList(SemVariable(7)), SemFunction(SemTuple(SemNamed(0, "B", []) :: SemNamed(77, "Unknown", []) :: []), SemNamed(2, "G", SemVariable(7) :: []), None), None), None) -> true
        | _ -> false

let definitionsCorrect (types: DropperTypes) =
    (let environment = types.dropperTypeEnvironment
    in
        match environment.constructors with
            | ConstructorInferenceDefinition { name = "B0" } :: ConstructorInferenceDefinition { name = "A0" } :: ConstructorInferenceDefinition { name = "B1" } :: ConstructorInferenceDefinition { name = "G0", scheme = TypeScheme { body = genericBody } } :: ConstructorInferenceDefinition { name = "Invalid", scheme = TypeScheme { body = SemFunction(SemInt, SemInt, None) } } :: [] -> bodyCorrect(genericBody)
            | _ -> false)

let namesCorrect (body: DropperBody) =
    match (namedTypeConstructors(SemNamed(999)("B")([SemString]))(body), namedTypeConstructors(SemNamed(999)("A")([]))(body), namedTypeConstructors(SemNamed(999)("Missing")([]))(body), namedTypeConstructors(SemInt)(body)) with
        | ("B0" :: "B1" :: [], "A0" :: [], [], []) -> true
        | _ -> false

let checks unit =
    (let types = prepareDropperTypes(definitions)
    in
        match openDropperBody(types)(emptyDropperLabelCache)(0)(0) with
            | (_ids, body) ->
                idsCorrect(types) && definitionsCorrect(types) && namesCorrect(body) && typeIsTagless(SemNamed(999)("A")([]))(body) && !typeIsTagless(SemNamed(999)("B")([]))(body) && !typeIsTagless(SemNamed(999)("Missing")([]))(body))

let freshBody seed =
    (let name = "Fresh" + Ashes.Text.fromInt(seed)
    in
        let types =
            prepareDropperTypes([[]
            |> SemNamed(seed)(name)
            |> definition(name)([SemInt])])
        in
            match openDropperBody(types)(emptyDropperLabelCache)(0)(0) with
                | (_ids, body) -> (name, body))

let freshCheck seed =
    match freshBody(seed) with
        | (name, body) ->
            match namedTypeConstructors(SemNamed(999)(name)([]))(body) with
                | actual :: [] ->
                    actual == name && typeIsTagless(SemNamed(999)(name)([]))(body)
                | _ -> false

let recursive run remaining =
    if remaining == 0
    then Ashes.IO.print("dropper index ok")
    else
        if freshCheck(remaining)
        then run(remaining - 1)
        else Ashes.IO.panic("fresh dropper index mismatch")

if !checks(Unit)
then Ashes.IO.panic("dropper index mismatch")
else
    match Ashes.IO.args with
        | "1000" :: [] -> run(1000)
        | "10000" :: [] -> run(10000)
        | _ -> Ashes.IO.print("dropper index ok")
