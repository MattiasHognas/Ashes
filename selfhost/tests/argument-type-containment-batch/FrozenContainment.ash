import AshesCompiler.Semantics.Types
export (
    value mayContainArgumentType,
)

// Ancestry retains complete type roots so ownership stays with their RC-managed graphs.
type ContainmentWork =
    | InspectType(SemanticType, List(SemanticType))
    | ExpandNamedType(SemanticType, List(SemanticType))

let recursive hasName (name: Str) (names: List(Str)) =
    match names with
        | [] -> false
        | candidate :: rest -> candidate == name || hasName(name)(rest)

let recursive previouslyExpanded (semanticType: SemanticType) (expanded: List(SemanticType)) =
    match expanded with
        | [] -> false
        | previous :: rest -> previous == semanticType || previouslyExpanded(semanticType)(rest)

let recursive hasTypeName (name: Str) (path: List(SemanticType)) =
    match path with
        | [] -> false
        | SemNamed(_id, candidate, _arguments) :: rest -> candidate == name || hasTypeName(name)(rest)
        | _ :: rest -> hasTypeName(name)(rest)

let components (semanticType: SemanticType) =
    match semanticType with
        | SemList(element) -> [element]
        | SemPointer(pointee) -> [pointee]
        | SemTuple(elements) -> elements
        | SemNamed(_id, _name, arguments) -> arguments
        | SemCapability(_name, arguments) -> arguments
        | _ -> []

let recursive prependTypes (types: List(SemanticType)) (path: List(SemanticType)) (pending: List(ContainmentWork)) =
    match types with
        | [] -> pending
        | semanticType :: rest -> InspectType(semanticType)(path) :: prependTypes(rest)(path)(pending)

// One worklist carries the shared expansion set. Expanding fields is a separate work item so
// structural arguments are inspected first, including when this name has already been visited.
// Different sibling instantiations are searched separately. Only recursive expansion of the
// same name at changing arguments is conservatively unknown, to bound non-regular recursion.
let recursive search resolve fields (blocked: List(Str)) (argumentType: SemanticType) (pending: List(ContainmentWork)) (expanded: List(SemanticType)) =
    match pending with
        | [] -> false
        | InspectType(resultType, path) :: rest ->
            match resolve(resultType) with
                | SemVariable(_id) -> true
                | SemParameter(_id, _name) -> true
                | SemFunction(_parameter, _result, _row) -> true
                | resolved ->
                    if resolved == argumentType
                    then true
                    else
                        match resolved with
                            | SemNamed(_id, _name, _arguments) ->
                                search(resolve)(fields)(blocked)(argumentType)(prependTypes(components(resolved))(path)(ExpandNamedType(resolved)(path) :: rest))(expanded)
                            | _ ->
                                search(resolve)(fields)(blocked)(argumentType)(prependTypes(components(resolved))(path)(rest))(expanded)
        | ExpandNamedType(resolved, path) :: rest ->
            match resolved with
                | SemNamed(_id, name, _arguments) ->
                    if hasName(name)(blocked) || previouslyExpanded(resolved)(expanded)
                    then search(resolve)(fields)(blocked)(argumentType)(rest)(expanded)
                    else
                        if hasTypeName(name)(path)
                        then true
                        else
                            search(resolve)(fields)(blocked)(argumentType)(prependTypes(fields(resolved))(resolved :: path)(rest))(resolved :: expanded)
                | _ -> search(resolve)(fields)(blocked)(argumentType)(rest)(expanded)

let mayContainArgumentType resolve fields (blocked: List(Str)) (resultType: SemanticType) (argumentType: SemanticType) = search(resolve)(fields)(blocked)(argumentType)([InspectType(resultType)([])])([])
