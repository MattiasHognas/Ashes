// Decision and nominal-key semantics frozen from c0e23905. Only identifiers are prefixed.
import ProbeDroppers
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.HeapLayoutClassification
export (
    value frozenGeneralRcAdmissible,
)

let frozenSameTypeKey (left: SemanticType) (right: SemanticType) =
    match (left, right) with
        | (SemNamed(_leftId, leftName, []), SemNamed(_rightId, rightName, [])) -> leftName == rightName
        | (SemNamed(_leftId, _leftName, []), _) -> false
        | (_, SemNamed(_rightId, _rightName, [])) -> false
        | _ -> formatSemanticType(left) == formatSemanticType(right)

let recursive frozenContainsType (semanticType: SemanticType) (path: List(SemanticType)) =
    match path with
        | [] -> false
        | candidate :: rest -> frozenSameTypeKey(candidate)(semanticType) || frozenContainsType(semanticType)(rest)

// Stage 0's `IsGeneralRcAdmissible` over the synthesis environment: a resolved graph of inline
// values, strings, byte buffers, big integers, lists, tuples and algebraic data types. A named
// type already on the walk's path is taken as admissible, which lets a recursive type through.
let recursive frozenGeneralRcAdmissible (semanticType: SemanticType) (path: List(SemanticType)) (body: DropperBody) =
    match semanticType with
        | SemString -> (true, body)
        | SemBytes -> (true, body)
        | SemBigInt -> (true, body)
        | SemList(element) -> frozenGeneralRcAdmissible(element)(path)(body)
        | SemTuple(elements) -> frozenAllGeneralRcAdmissible(elements)(path)(body)
        | SemNamed(_symbolId, name, _arguments) ->
            if frozenContainsType(semanticType)(path)
            then (true, body)
            else
                match dropperAdmissibilityFactsIndexed(semanticType)(body) with
                    | (facts, queried) ->
                        match (dropperConstructorsOfType(name)(queried), facts) with
                            | ([], _facts) -> (false, queried)
                            | (_constructors, HeapAdmissibilityFacts { containsResource = true }) -> (false, queried)
                            | (_constructors, HeapAdmissibilityFacts { containsUnresolvedType = true }) -> (false, queried)
                            | (_constructors, HeapAdmissibilityFacts { children = children }) -> frozenAllChildrenGeneralRcAdmissible(children)(semanticType :: path)(queried)
        | other -> (canArenaResetLayout(other), body)
and frozenAllGeneralRcAdmissible (types: List(SemanticType)) (path: List(SemanticType)) (body: DropperBody) =
    match types with
        | [] -> (true, body)
        | semanticType :: rest ->
            match frozenGeneralRcAdmissible(semanticType)(path)(body) with
                | (false, queried) -> (false, queried)
                | (true, queried) -> frozenAllGeneralRcAdmissible(rest)(path)(queried)
and frozenAllChildrenGeneralRcAdmissible (children: List((SemanticType, HeapChildDropKind))) (path: List(SemanticType)) (body: DropperBody) =
    match children with
        | [] -> (true, body)
        | (_childType, DropClosure) :: _rest -> (false, body)
        | (_childType, UnsupportedChildDrop) :: _rest -> (false, body)
        | (childType, _dropKind) :: rest ->
            match frozenGeneralRcAdmissible(childType)(path)(body) with
                | (false, queried) -> (false, queried)
                | (true, queried) -> frozenAllChildrenGeneralRcAdmissible(rest)(path)(queried)
