import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.HeapLayoutClassification
type CoreLoweringState =
    | graph: Int
    | forbidden: Int
    | mode: Int
    | cached: Maybe((Bool, Bool))

let resolveType _state semanticType = semanticType

let resultSurvivesReset semanticType _state = semanticType == SemInt || semanticType == SemNamed(90)("Scalar")([])

let recordedGeneralRcTypeFacts _name state = state.cached

let stateConstructorLayouts _state = [0]

let constructorLayoutsOfType _name layouts = layouts

let stateConstructorsOfType name state = constructorLayoutsOfType(name)(stateConstructorLayouts(state))

let recursive power n =
    if n == 0
    then 1
    else 2 * power(n - 1)

let node n = SemNamed(n)("Node" + Ashes.Text.fromInt(n))([])

let edge graph from to = graph / power(from * 3 + to) % 2 == 1

let fields id state =
    if state.mode == 3
    then
        if id == 0
        then []
        else [(node(id - 1), DropAdt), (node(id - 1), DropAdt)]
    else
        if state.mode == 2
        then
            if id == 0
            then [(SemNamed(1)("Shared")([]), DropAdt)]
            else []
        else
            (if edge(state.graph)(id)(2)
            then [(node(2), DropAdt)]
            else [])
            |> Ashes.Collection.List.append(if edge(state.graph)(id)(1)
            then [(node(1), DropAdt)]
            else [])
            |> Ashes.Collection.List.append(if edge(state.graph)(id)(0)
            then [(node(0), DropAdt)]
            else [])

let heapAdmissibilityOf semanticType state =
    match semanticType with
        | SemNamed(id, _name, _arguments) -> HeapAdmissibilityFacts(containsResource = state.forbidden / power(id) % 2 == 1, containsUnresolvedType = false, children = fields(id)(state))
        | _ -> HeapAdmissibilityFacts(containsResource = false, containsUnresolvedType = false, children = [])

