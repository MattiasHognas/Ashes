import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeGraphIndex
import Ashes.Collection.Map.MapTree
export (
    value typeGraphResolvedNode,
    value typeGraphResource,
    value typeGraphUnresolved,
    value typeGraphDroppable,
)

// Node identities belong to this index lineage. Resolve aliases before using an ID
// for query-local visitation; the source-key entry still retains the alias root.
let typeGraphResolvedNode (semanticType: SemanticType) (index: TypeGraphIndex) =
    match typeGraphNode(semanticType)(index) with
        | (node, next) ->
            if node.sourceMatchesResolved
            then (node, next)
            else typeGraphNode(node.resolvedType)(next)

type QueryPath =
    | byId: MapTree(Int, MapTree(Str, Int))
    | names: List(SemanticType)

let emptyQueryPath = QueryPath(byId = Ashes.Collection.Map.empty, names = [])

let pathNode (symbolId: Int) (name: Str) (path: QueryPath) =
    match Ashes.Collection.Map.getInt(symbolId)(path.byId) with
        | None -> None
        | Some(byName) -> Ashes.Collection.Map.getStr(name)(byName)

let extendPath (node: TypeGraphNode) (path: QueryPath) =
    match node.resolvedType with
        | SemNamed(symbolId, name, _) ->
            let byName =
                match Ashes.Collection.Map.getInt(symbolId)(path.byId) with
                    | None -> Ashes.Collection.Map.empty
                    | Some(found) -> found
            in
                QueryPath(byId = Ashes.Collection.Map.setInt(symbolId)(Ashes.Collection.Map.setStr(name)(node.nodeId)(byName))(path.byId), names = node.resolvedType :: path.names)
        | _ -> path

let recursive blockedSubset (names: List(SemanticType)) (path: QueryPath) =
    match names with
        | [] -> true
        | SemNamed(symbolId, name, _) :: rest ->
            match pathNode(symbolId)(name)(path) with
                | None -> false
                | Some(_) -> blockedSubset(rest)(path)
        | _ :: rest -> blockedSubset(rest)(path)

let recursive coveredPath (path: QueryPath) (previous: List(QueryPath)) =
    match previous with
        | [] -> false
        | head :: rest -> blockedSubset(head.names)(path) || coveredPath(path)(rest)

type QueryWork =
    | VisitIndexedType(SemanticType, QueryPath)

let recursive prependWork (types: List(SemanticType)) (path: QueryPath) (pending: List(QueryWork)) =
    match types with
        | [] -> pending
        | head :: rest -> VisitIndexedType(head)(path) :: prependWork(rest)(path)(pending)

let matches kind (node: TypeGraphNode) =
    match kind with
        | ResourceFact -> node.directResource
        | UnresolvedFact -> node.directUnresolved

let children kind (node: TypeGraphNode) =
    match kind with
        | ResourceFact -> node.resourceChildren
        | UnresolvedFact -> node.unresolvedChildren

type QueryResult =
    | answer: Maybe(Bool)
    | completed: List(SemanticType)
    | index: TypeGraphIndex

// Direct matches precede cycle cuts, matching the existing regular/fallback walkers.
// Only exhausted negative searches publish their named closure. Positive or aborted
// searches never attach false facts to pending/visited nodes.
let recursive regularSearch kind (pending: List(QueryWork)) (visited: MapTree(Int, Bool)) (completed: List(SemanticType)) (index: TypeGraphIndex) =
    match pending with
        | [] -> QueryResult(answer = Some(false), completed = completed, index = index)
        | VisitIndexedType(semanticType, path) :: rest ->
            match typeGraphResolvedNode(semanticType)(index) with
                | (node, next) ->
                    if matches(kind)(node)
                    then QueryResult(answer = Some(true), completed = [], index = next)
                    else
                        match node.resolvedType with
                            | SemNamed(symbolId, name, _) ->
                                let changed =
                                    match pathNode(symbolId)(name)(path) with
                                        | None -> false
                                        | Some(previousId) -> previousId != node.nodeId
                                in
                                    if changed
                                    then QueryResult(answer = None, completed = [], index = next)
                                    else
                                        match Ashes.Collection.Map.getInt(node.nodeId)(visited) with
                                            | Some(_) -> regularSearch(kind)(rest)(visited)(completed)(next)
                                            | None ->
                                                regularSearch(kind)(prependWork(children(kind)(node))(extendPath(node)(path))(rest))(Ashes.Collection.Map.setInt(node.nodeId)(true)(visited))(node.resolvedType :: completed)(next)
                            | _ ->
                                regularSearch(kind)(prependWork(children(kind)(node))(path)(rest))(visited)(completed)(next)

// These blocked-path entries are query-local, never whole-root facts. An earlier
// less-blocked visit covers this visit only because all its successors stay pending.
let recursive pathSearch kind (pending: List(QueryWork)) (visited: MapTree(Int, List(QueryPath))) (index: TypeGraphIndex) =
    match pending with
        | [] -> (false, index)
        | VisitIndexedType(semanticType, path) :: rest ->
            match typeGraphResolvedNode(semanticType)(index) with
                | (node, next) ->
                    if matches(kind)(node)
                    then (true, next)
                    else
                        match node.resolvedType with
                            | SemNamed(symbolId, name, _) ->
                                match pathNode(symbolId)(name)(path) with
                                    | Some(_) -> pathSearch(kind)(rest)(visited)(next)
                                    | None ->
                                        let bucket =
                                            match Ashes.Collection.Map.getInt(node.nodeId)(visited) with
                                                | None -> []
                                                | Some(found) -> found
                                        in
                                            if coveredPath(path)(bucket)
                                            then pathSearch(kind)(rest)(visited)(next)
                                            else
                                                pathSearch(kind)(prependWork(children(kind)(node))(extendPath(node)(path))(rest))(Ashes.Collection.Map.setInt(node.nodeId)(path :: bucket)(visited))(next)
                            | _ ->
                                pathSearch(kind)(prependWork(children(kind)(node))(path)(rest))(visited)(next)

let graphQuery kind (semanticType: SemanticType) (index: TypeGraphIndex) =
    match typeGraphResolvedNode(semanticType)(index) with
        | (root, initial) ->
            match typeGraphRootFact(kind)(root.resolvedType)(initial) with
                | Some(answer) -> (answer, initial)
                | None ->
                    let work = [VisitIndexedType(root.resolvedType)(emptyQueryPath)]
                    in
                        let regular = regularSearch(kind)(work)(Ashes.Collection.Map.empty)([])(initial)
                        in
                            match regular.answer with
                                | Some(answer) ->
                                    let completed =
                                        if answer
                                        then regular.index
                                        else completeTypeGraphNegativeRoots(kind)(regular.completed)(regular.index)
                                    in (answer, completeTypeGraphRootFact(kind)(root.resolvedType)(answer)(completed))
                                | None ->
                                    match pathSearch(kind)(work)(Ashes.Collection.Map.empty)(regular.index) with
                                        | (answer, completed) -> (answer, completeTypeGraphRootFact(kind)(root.resolvedType)(answer)(completed))

let typeGraphResource (semanticType: SemanticType) (index: TypeGraphIndex) = graphQuery(ResourceFact)(semanticType)(index)

let typeGraphUnresolved (semanticType: SemanticType) (index: TypeGraphIndex) = graphQuery(UnresolvedFact)(semanticType)(index)

let dropLeaf (node: TypeGraphNode) =
    match node.resolvedType with
        | SemInt -> true
        | SemUInt(_) -> true
        | SemFloat -> true
        | SemRune -> true
        | SemBool -> true
        | SemNever -> true
        | SemOpaque(_) -> !node.directResource
        | SemString -> true
        | SemBytes -> true
        | SemBigInt -> true
        | SemFunction(_, _, _) -> true
        | _ -> false

let recursive dropSearch (pending: List(QueryWork)) (visited: MapTree(Int, List(QueryPath))) (index: TypeGraphIndex) (pathCuts: Bool) =
    match pending with
        | [] -> QueryResult(answer = Some(true), completed = [], index = index)
        | VisitIndexedType(semanticType, path) :: rest ->
            match typeGraphResolvedNode(semanticType)(index) with
                | (node, next) ->
                    if dropLeaf(node)
                    then dropSearch(rest)(visited)(next)(pathCuts)
                    else
                        match node.resolvedType with
                            | SemList(_) ->
                                dropSearch(prependWork(node.resourceChildren)(path)(rest))(visited)(next)(pathCuts)
                            | SemTuple(_) ->
                                dropSearch(prependWork(node.resourceChildren)(path)(rest))(visited)(next)(pathCuts)
                            | SemNamed(symbolId, name, _) ->
                                match typeGraphResource(node.resolvedType)(next) with
                                    | (true, afterResource) -> QueryResult(answer = Some(false), completed = [], index = afterResource)
                                    | (false, afterResource) ->
                                        let previous = pathNode(symbolId)(name)(path)
                                        in
                                            let changed =
                                                match previous with
                                                    | Some(previousId) -> previousId != node.nodeId
                                                    | None -> false
                                            in
                                                if !pathCuts && changed
                                                then QueryResult(answer = None, completed = [], index = afterResource)
                                                else
                                                    match previous with
                                                        | Some(_) -> dropSearch(rest)(visited)(afterResource)(pathCuts)
                                                        | None ->
                                                            let bucket =
                                                                match Ashes.Collection.Map.getInt(node.nodeId)(visited) with
                                                                    | None -> []
                                                                    | Some(found) -> found
                                                            in
                                                                let covered =
                                                                    if pathCuts
                                                                    then coveredPath(path)(bucket)
                                                                    else
                                                                        match bucket with
                                                                            | [] -> false
                                                                            | _ -> true
                                                                in
                                                                    if covered
                                                                    then dropSearch(rest)(visited)(afterResource)(pathCuts)
                                                                    else
                                                                        dropSearch(prependWork(node.resourceChildren)(extendPath(node)(path))(rest))(Ashes.Collection.Map.setInt(node.nodeId)(path :: bucket)(visited))(afterResource)(pathCuts)
                            | _ -> QueryResult(answer = Some(false), completed = [], index = next)

let typeGraphDroppable (semanticType: SemanticType) (index: TypeGraphIndex) =
    (let work = [VisitIndexedType(semanticType)(emptyQueryPath)]
    in
        let first = dropSearch(work)(Ashes.Collection.Map.empty)(index)(false)
        in
            match first.answer with
                | Some(answer) -> (answer, first.index)
                | None ->
                    let fallback = dropSearch(work)(Ashes.Collection.Map.empty)(first.index)(true)
                    in
                        match fallback.answer with
                            | Some(answer) -> (answer, fallback.index)
                            | None -> (false, fallback.index))
