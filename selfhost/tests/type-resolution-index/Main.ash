import AshesCompiler.Semantics.TypeResolution
import AshesCompiler.Semantics.Types
let resolved name arguments context = resolveSemanticTypeApplication(name)(arguments)(context)

let resultType (result: TypeResolutionResult) = result.semanticType

let resultError (result: TypeResolutionResult) = result.error

let provenance name = DeclarationProvenance(packageId = name)

let freshContext seed =
    (let name = "T" + Ashes.Text.fromInt(seed)
    in
        Unit
        |> emptyTypeResolutionContext
        |> addTypeDefinitionWithProvenance(seed)(name)(0)(provenance("old"))
        |> addExternalTypeDefinition(name)(Some("drop"))
        |> addZeroCostTypeDefinitionWithProvenance(seed)(name)([8])("a"
        |> SemParameter(8)
        |> SemList)(provenance("wrapper"))
        |> addTypeDefinitionWithProvenance(seed)(name)(0)(provenance("new")))

let freshCheck seed =
    (let name = "T" + Ashes.Text.fromInt(seed)
    in
        let context = freshContext(seed)
        in
            let parameterContext =
                context
                |> addTypeParameter(name)(SemInt)
                |> addTypeParameter(name)(SemString)
            in
                resultType(resolved(name)([])(context)) == SemNamed(seed)(name)([]) && nominalTypeProvenance(seed)(context) == Some(provenance("new")) && semanticRuntimeRepresentation(SemNamed(seed)(name)([SemInt]))(context) == SemList(SemInt) && externalTypeHasDestructor(name)(context) && !externalTypeHasDestructor(name)(addExternalTypeDefinition(name)(None)(context)) && nextTypeDefinitionSymbolId(context) == seed + 1 && resultType(resolved(name)([])(parameterContext)) == SemString && resultError(resolved(name)([SemInt])(parameterContext)) == Some(TypeNameArityMismatch(name)(0)(1)) && resultType(context
                |> addTypeAliasDefinition(name)([])(SemBool)
                |> resolved(name)([])) == SemBool)

let edgeCases unit =
    (let context =
        Unit
        |> emptyTypeResolutionContext
        |> addTypeDefinition(-10)("Negative")(0)
        |> addTypeDefinition(100)("Large")(0)
        |> addTypeDefinition(2)("Small")(0)
    in
        nextTypeDefinitionSymbolId(emptyTypeResolutionContext(Unit)) == 0 && nextTypeDefinitionSymbolId(context) == 101 && nominalTypeProvenance(99)(context) == None && !externalTypeHasDestructor("Missing")(context) && semanticRuntimeRepresentation(SemNamed(99)("Missing")([]))(context) == SemNamed(99)("Missing")([]) && resultError(resolved("Missing")([])(context)) == Some(UnknownTypeName("Missing")))

let recursive referenceDefinition name definitions =
    match definitions with
        | [] -> SemNever
        | NominalTypeDefinition(symbolId, candidate, _arity, _provenance) :: rest ->
            if candidate == name
            then SemNamed(symbolId)(name)([])
            else referenceDefinition(name)(rest)
        | ZeroCostTypeDefinition(symbolId, candidate, _parameters, _representation, _provenance) :: rest ->
            if candidate == name
            then SemNamed(symbolId)(name)([])
            else referenceDefinition(name)(rest)
        | AliasTypeDefinition(candidate, _parameters, target) :: rest ->
            if candidate == name
            then target
            else referenceDefinition(name)(rest)
        | ExternalTypeDefinition(candidate, _destructor) :: rest ->
            if candidate == name
            then SemOpaque(name)
            else referenceDefinition(name)(rest)

let recursive referenceProvenance symbolId definitions =
    match definitions with
        | [] -> None
        | NominalTypeDefinition(candidate, _name, _arity, provenance) :: rest ->
            if candidate == symbolId
            then Some(provenance)
            else referenceProvenance(symbolId)(rest)
        | ZeroCostTypeDefinition(candidate, _name, _parameters, _representation, provenance) :: rest ->
            if candidate == symbolId
            then Some(provenance)
            else referenceProvenance(symbolId)(rest)
        | _ :: rest -> referenceProvenance(symbolId)(rest)

let recursive referenceRepresentation symbolId name definitions =
    match definitions with
        | [] -> SemNamed(symbolId)(name)([])
        | ZeroCostTypeDefinition(candidate, _name, _parameters, representation, _provenance) :: rest ->
            if candidate == symbolId
            then representation
            else referenceRepresentation(symbolId)(name)(rest)
        | _ :: rest -> referenceRepresentation(symbolId)(name)(rest)

let recursive referenceDestructor name definitions =
    match definitions with
        | [] -> false
        | ExternalTypeDefinition(candidate, destructor) :: rest ->
            if candidate == name
            then destructor != None
            else referenceDestructor(name)(rest)
        | _ :: rest -> referenceDestructor(name)(rest)

let contextEquivalent (context: TypeResolutionContext) =
    (let definitions = context.definitions
    in
        resultType(resolved("A")([])(context)) == referenceDefinition("A")(definitions) && resultType(resolved("B")([])(context)) == referenceDefinition("B")(definitions) && nominalTypeProvenance(1)(context) == referenceProvenance(1)(definitions) && nominalTypeProvenance(2)(context) == referenceProvenance(2)(definitions) && semanticRuntimeRepresentation(SemNamed(1)("A")([]))(context) == referenceRepresentation(1)("A")(definitions) && semanticRuntimeRepresentation(SemNamed(2)("B")([]))(context) == referenceRepresentation(2)("B")(definitions) && externalTypeHasDestructor("A")(context) == referenceDestructor("A")(definitions) && externalTypeHasDestructor("B")(context) == referenceDestructor("B")(definitions))

let addChoice choice context =
    match choice with
        | 0 ->
            addTypeDefinitionWithProvenance(1)("A")(0)(provenance("nominal"))(context)
        | 1 ->
            addTypeDefinitionWithProvenance(1)("B")(0)(provenance("other"))(context)
        | 2 ->
            addZeroCostTypeDefinitionWithProvenance(1)("A")([])(SemBool)(provenance("zero"))(context)
        | 3 ->
            addZeroCostTypeDefinitionWithProvenance(2)("A")([])(SemString)(provenance("second"))(context)
        | 4 -> addTypeAliasDefinition("A")([])(SemInt)(context)
        | 5 -> addExternalTypeDefinition("A")(Some("drop"))(context)
        | _ -> addExternalTypeDefinition("A")(None)(context)

let recursive combinations depth context =
    if !contextEquivalent(context)
    then false
    else
        if depth == 0
        then true
        else choices(0)(depth)(context)
and choices choice depth context =
    if choice == 7
    then true
    else
        combinations(depth - 1)(addChoice(choice)(context)) && choices(choice + 1)(depth)(context)

let recursive run remaining =
    if remaining == 0
    then Ashes.IO.print("resolution index ok")
    else
        if freshCheck(remaining)
        then run(remaining - 1)
        else Ashes.IO.panic("resolution index mismatch")

if !edgeCases(Unit) || !combinations(4)(emptyTypeResolutionContext(Unit))
then Ashes.IO.panic("resolution edge mismatch")
else
    match Ashes.IO.args with
        | "1000" :: [] -> run(1000)
        | "10000" :: [] -> run(10000)
        | _ -> run(100)
