// The drop-graph classifier as it stood before the type-graph index replaced it (88aec017^),
// frozen as the reference the production `classifyHeapOwnedChildrenDroppable` must agree with.
// The helpers the classifier no longer has are kept here under a `reference` prefix; a named
// type's fields come from the production `heapNamedTypeConstructors`, which answers the same
// instantiated field lists the old `heapNamedTypeFields` did.
let referenceResolveLayoutType (semanticType: SemanticType) (environment: TypeEnvironment) =
    environment
    |> inferenceTypeResolutionContext
    |> semanticRuntimeRepresentation(semanticType)

let referenceCanArenaResetLayoutIn (semanticType: SemanticType) (environment: TypeEnvironment) =
    match semanticType with
        | SemOpaque(name) ->
            environment
            |> inferenceTypeResolutionContext
            |> externalTypeHasDestructor(name)
            |> (given (hasDestructor: Bool) -> hasDestructor == false)
        | other -> canArenaResetLayout(other)

let recursive referenceAllTypes predicate (types: List(SemanticType)) =
    match types with
        | [] -> true
        | head :: rest ->
            if predicate(head)
            then referenceAllTypes(predicate)(rest)
            else false

let recursive referenceAllGroupedFields predicate (grouped: List((Str, List(SemanticType)))) =
    match grouped with
        | [] -> true
        | (_constructorName, fieldTypes) :: rest ->
            if referenceAllTypes(predicate)(fieldTypes)
            then referenceAllGroupedFields(predicate)(rest)
            else false

let recursive referenceAnyResource (types: List(SemanticType)) (environment: TypeEnvironment) (path: List((Int, Str))) =
    match types with
        | [] -> false
        | head :: rest ->
            if referenceContainsResource(head)(environment)(path)
            then true
            else referenceAnyResource(rest)(environment)(path)
and referenceAnyGroupedResource (grouped: List((Str, List(SemanticType)))) (environment: TypeEnvironment) (path: List((Int, Str))) =
    match grouped with
        | [] -> false
        | (_constructorName, fieldTypes) :: rest ->
            if referenceAnyResource(fieldTypes)(environment)(path)
            then true
            else referenceAnyGroupedResource(rest)(environment)(path)
and referenceContainsResource (semanticType: SemanticType) (environment: TypeEnvironment) (path: List((Int, Str))) =
    (let resolved = referenceResolveLayoutType(semanticType)(environment)
    in
        match resolved with
            | SemOpaque(name) ->
                environment
                |> inferenceTypeResolutionContext
                |> externalTypeHasDestructor(name)
            | SemNamed(symbolId, name, _arguments) ->
                if heapBuiltinResourceTypeName(name)
                then true
                else
                    if heapPathContains(symbolId)(name)(path)
                    then false
                    else
                        referenceAnyGroupedResource(heapNamedTypeConstructors(resolved)(environment))(environment)((symbolId, name) :: path)
            | SemTuple(elements) -> referenceAnyResource(elements)(environment)(path)
            | SemList(element) -> referenceContainsResource(element)(environment)(path)
            | _ -> false)

let referenceResourceBearing (semanticType: SemanticType) (environment: TypeEnvironment) = referenceContainsResource(semanticType)(environment)([])

let recursive referenceDroppableLeafField (semanticType: SemanticType) (environment: TypeEnvironment) namedRule =
    (let resolved = referenceResolveLayoutType(semanticType)(environment)
    in
        if referenceCanArenaResetLayoutIn(resolved)(environment)
        then true
        else
            match resolved with
                | SemString -> true
                | SemBytes -> true
                | SemBigInt -> true
                | SemList(element) -> referenceDroppableLeafField(element)(environment)(namedRule)
                | SemTuple(elements) ->
                    referenceAllTypes(given (element) -> referenceDroppableLeafField(element)(environment)(namedRule))(elements)
                | SemFunction(_argument, _result, _capabilityRow) -> true
                | SemNamed(_symbolId, _name, _arguments) -> namedRule(resolved)
                | _ -> false)

// Whether the type-directed drop walker can release every owned child of a named type's graph; a
// cycle back into a type already on the path is droppable by construction.
let recursive referenceCanDropAdtGraph (named: SemanticType) (environment: TypeEnvironment) (path: List((Int, Str))) =
    match named with
        | SemNamed(symbolId, name, _arguments) ->
            if referenceResourceBearing(named)(environment)
            then false
            else
                if heapPathContains(symbolId)(name)(path)
                then true
                else
                    environment
                    |> heapNamedTypeConstructors(named)
                    |> referenceAllGroupedFields(given (fieldType) ->
                        referenceDroppableLeafField(fieldType)(environment)(given (child) -> referenceCanDropAdtGraph(child)(environment)((symbolId, name) :: path)))
        | _ -> false

let referenceCanDropValueGraph (semanticType: SemanticType) (environment: TypeEnvironment) =
    referenceDroppableLeafField(semanticType)(environment)(given (named) -> referenceCanDropAdtGraph(named)(environment)([]))
