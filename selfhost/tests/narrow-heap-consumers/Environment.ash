let optionDecl unit =
    TypeDecl(name = "Option", typeParameters = [TypeParameter(name = "a")], constructors = [
        TypeConstructor(name = "None", parameters = [], fieldNames = []),
        TypeConstructor(name = "Some", parameters = [TypeNamed("a")], fieldNames = [])
    ], isRecord = false, derivingTraits = [])

let wrappedDecl unit =
    TypeDecl(name = "Wrapped", typeParameters = [], constructors = [
        TypeConstructor(name = "Wrap", parameters = [TypeNamed("Handle")], fieldNames = [])
    ], isRecord = false, derivingTraits = [])

let intListDecl unit =
    TypeDecl(name = "IntList", typeParameters = [], constructors = [
        TypeConstructor(name = "Nil", parameters = [], fieldNames = []),
        TypeConstructor(name = "Cons", parameters = [TypeNamed("Int"), TypeNamed("IntList")], fieldNames = [])
    ], isRecord = false, derivingTraits = [])

let pointDecl unit =
    TypeDecl(name = "Point", typeParameters = [], constructors = [
        TypeConstructor(name = "Point", parameters = [TypeNamed("Int"), TypeNamed("Int")], fieldNames = ["x", "y"])
    ], isRecord = true, derivingTraits = [])

let labelDecl unit =
    TypeDecl(name = "Label", typeParameters = [], constructors = [
        TypeConstructor(name = "Named", parameters = [TypeNamed("Str")], fieldNames = []),
        TypeConstructor(name = "Anonymous", parameters = [], fieldNames = [])
    ], isRecord = false, derivingTraits = [])

let callbackDecl unit =
    TypeDecl(name = "Callback", typeParameters = [], constructors = [
        TypeConstructor(name = "Call", parameters = [TypeArrow(TypeNamed("Int"))(TypeNamed("Int"))([])(None)], fieldNames = [])
    ], isRecord = false, derivingTraits = [])

let closeHandleDecl =
    ExternalFunction(
        "closeHandle",
        [
            ParsedNamed("Handle")
        ],
        ParsedNamed("void"),
        Some("close_handle@libresource.so"),
        [
            ExternalOwnershipConsume
        ],
        None
    )

let heapLayoutTestProgram unit =
    ProgramSyntax(
        items = [
            Some("closeHandle")
            |> ExternalOpaqueType("Handle")
            |> TopLevelExternal,
            TopLevelExternal(closeHandleDecl),
            None
            |> ExternalOpaqueType("View")
            |> TopLevelExternal,
            Unit
            |> optionDecl
            |> TopLevelType,
            Unit
            |> wrappedDecl
            |> TopLevelType,
            Unit
            |> intListDecl
            |> TopLevelType,
            Unit
            |> pointDecl
            |> TopLevelType,
            Unit
            |> labelDecl
            |> TopLevelType,
            Unit
            |> callbackDecl
            |> TopLevelType
        ],
        body = None
    )

let heapLayoutTestEnvironment unit =
    match Unit
    |> heapLayoutTestProgram
    |> inferProgram with
        | ProgramInferenceResult { environment = environment, error = None } -> environment
        | ProgramInferenceResult { error = Some(error) } -> test.fail("heap layout test program should infer: " + Ashes.Trait.Show.show(error))

let recursive unwrapConstructorResult (semanticType: SemanticType) =
    match semanticType with
        | SemFunction(_argument, result, _row) -> unwrapConstructorResult(result)
        | _ -> semanticType

let recursive findConstructorResultType (name: Str) (constructors: List(ConstructorInferenceDefinition)) =
    match constructors with
        | [] -> test.fail("no constructor named " + name)
        | ConstructorInferenceDefinition { name = candidateName, scheme = TypeScheme { body = body } } :: rest ->
            if candidateName == name
            then unwrapConstructorResult(body)
            else findConstructorResultType(name)(rest)

let namedTypeAt (constructorName: Str) (concreteArguments: List(SemanticType)) (environment: TypeEnvironment) =
    match environment with
        | TypeEnvironment { constructors = constructors } ->
            match findConstructorResultType(constructorName)(constructors) with
                | SemNamed(symbolId, name, _genericArguments) -> SemNamed(symbolId)(name)(concreteArguments)
                | _ -> test.fail("constructor " + constructorName + " does not resolve to a named type")

let admissibilityProjection (child: HeapLayoutChild) = (child.childType, child.dropKind)
