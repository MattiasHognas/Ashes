// Whether the type-directed drop walker can release every owned child of a named type's graph; a
// cycle back into a type already on the path is droppable by construction.
let recursive referenceCanDropAdtGraph (named: SemanticType) (environment: TypeEnvironment) (path: List((Int, Str))) =
    match named with
        | SemNamed(symbolId, name, _arguments) ->
            if heapResourceBearing(named)(environment)
            then false
            else
                if heapPathContains(symbolId)(name)(path)
                then true
                else
                    environment
                    |> heapNamedTypeConstructors(named)
                    |> heapAllGroupedFields(given (fieldType) ->
                        heapDroppableLeafField(fieldType)(environment)(given (child) -> referenceCanDropAdtGraph(child)(environment)((symbolId, name) :: path)))
        | _ -> false

let referenceCanDropValueGraph (semanticType: SemanticType) (environment: TypeEnvironment) =
    heapDroppableLeafField(semanticType)(environment)(given (named) -> referenceCanDropAdtGraph(named)(environment)([]))
