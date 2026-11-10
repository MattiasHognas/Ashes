import AshesCompiler.Semantics.Types
export (
    value typeGraphAny,
)

// Work owns complete type roots rather than borrowed name strings.
type TypeGraphWork =
    | VisitType(SemanticType, List(SemanticType))

let recursive containsType (semanticType: SemanticType) (visited: List(SemanticType)) =
    match visited with
        | [] -> false
        | previous :: rest -> previous == semanticType || containsType(semanticType)(rest)

let recursive changesInstantiation (symbolId: Int) (name: Str) (semanticType: SemanticType) (path: List(SemanticType)) =
    match path with
        | [] -> false
        | (SemNamed(previousId, previousName, _arguments) as previous) :: rest ->
            if previousId == symbolId && previousName == name
            then previous != semanticType
            else changesInstantiation(symbolId)(name)(semanticType)(rest)
        | _ :: rest -> changesInstantiation(symbolId)(name)(semanticType)(rest)

let recursive prependWork (types: List(SemanticType)) (path: List(SemanticType)) (pending: List(TypeGraphWork)) =
    match types with
        | [] -> pending
        | head :: rest -> VisitType(head)(path) :: prependWork(rest)(path)(pending)

// This is a single existential reachability query, not a cache of false results for individual
// nodes. Every queued successor is still searched before returning Some(false). Distinct generic
// instantiations have distinct visited keys. A changing instantiation on an ancestor path asks
// the caller to use its existing name-cycle semantics instead of inventing a new classification.
let recursive search resolve matches children (pending: List(TypeGraphWork)) (visited: List(SemanticType)) =
    match pending with
        | [] -> Some(false)
        | VisitType(semanticType, path) :: rest ->
            let resolved = resolve(semanticType)
            in
                if matches(resolved)
                then Some(true)
                else
                    match resolved with
                        | SemNamed(symbolId, name, _arguments) ->
                            if changesInstantiation(symbolId)(name)(resolved)(path)
                            then None
                            else
                                if containsType(resolved)(visited)
                                then search(resolve)(matches)(children)(rest)(visited)
                                else
                                    search(resolve)(matches)(children)(prependWork(children(resolved))(resolved :: path)(rest))(resolved :: visited)
                        | _ ->
                            search(resolve)(matches)(children)(prependWork(children(resolved))(path)(rest))(visited)

let typeGraphAny resolve matches children semanticType = search(resolve)(matches)(children)([VisitType(semanticType)([])])([])
