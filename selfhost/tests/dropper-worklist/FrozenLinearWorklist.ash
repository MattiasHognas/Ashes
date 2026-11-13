// Query-local linear visited worklist frozen from 5344d284; only identifiers prefixed.
import ProbeDroppers
import FrozenDecision
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.HeapLayoutClassification
export (
    value linearGeneralRcAdmissible,
    value linearDropperAdmissibilityVisitStatus,
)

let linearSameTypeKey (left: SemanticType) (right: SemanticType) =
    match (left, right) with
        | (SemNamed(_leftId, leftName, []), SemNamed(_rightId, rightName, [])) -> leftName == rightName
        | (SemNamed(_leftId, _leftName, []), _) -> false
        | (_, SemNamed(_rightId, _rightName, [])) -> false
        | _ -> formatSemanticType(left) == formatSemanticType(right)

type LinearDropperAdmissibilityWork =
    | LinearVisitDropperAdmissibilityType(SemanticType)
    | LinearCheckDropperAdmissibilityChild(SemanticType, HeapChildDropKind)

let recursive linearPrependDropperAdmissibilityTypes types pending =
    match types with
        | [] -> pending
        | semanticType :: rest -> LinearVisitDropperAdmissibilityType(semanticType) :: linearPrependDropperAdmissibilityTypes(rest)(pending)

let recursive linearPrependDropperAdmissibilityChildren children pending =
    match children with
        | [] -> pending
        | (semanticType, dropKind) :: rest -> LinearCheckDropperAdmissibilityChild(semanticType)(dropKind) :: linearPrependDropperAdmissibilityChildren(rest)(pending)

let recursive linearDropperAdmissibilityVisitStatus semanticType visited =
    match visited with
        | [] -> Some(false)
        | previous :: rest ->
            if linearSameTypeKey(previous)(semanticType)
            then
                if previous == semanticType
                then Some(true)
                else None
            else linearDropperAdmissibilityVisitStatus(semanticType)(rest)

let recursive linearSearchDropperAdmissibility pending visited body =
    match pending with
        | [] -> (Some(true), body)
        | LinearCheckDropperAdmissibilityChild(_semanticType, DropClosure) :: _rest -> (Some(false), body)
        | LinearCheckDropperAdmissibilityChild(_semanticType, UnsupportedChildDrop) :: _rest -> (Some(false), body)
        | LinearCheckDropperAdmissibilityChild(semanticType, _dropKind) :: rest -> linearSearchDropperAdmissibility(LinearVisitDropperAdmissibilityType(semanticType) :: rest)(visited)(body)
        | LinearVisitDropperAdmissibilityType(semanticType) :: rest ->
            match semanticType with
                | SemString -> linearSearchDropperAdmissibility(rest)(visited)(body)
                | SemBytes -> linearSearchDropperAdmissibility(rest)(visited)(body)
                | SemBigInt -> linearSearchDropperAdmissibility(rest)(visited)(body)
                | SemList(element) -> linearSearchDropperAdmissibility(LinearVisitDropperAdmissibilityType(element) :: rest)(visited)(body)
                | SemTuple(elements) ->
                    linearSearchDropperAdmissibility(linearPrependDropperAdmissibilityTypes(elements)(rest))(visited)(body)
                | SemNamed(_symbolId, name, _arguments) ->
                    // Match the dropper's cycle check before inspecting constructors
                    // or resource facts; its contract differs from Core's predicate.
                    match linearDropperAdmissibilityVisitStatus(semanticType)(visited) with
                        | None -> (None, body)
                        | Some(true) -> linearSearchDropperAdmissibility(rest)(visited)(body)
                        | Some(false) ->
                            match dropperAdmissibilityFactsIndexed(semanticType)(body) with
                                | (facts, queried) ->
                                    match (dropperConstructorsOfType(name)(queried), facts) with
                                        | ([], _facts) -> (Some(false), queried)
                                        | (_constructors, HeapAdmissibilityFacts { containsResource = true }) -> (Some(false), queried)
                                        | (_constructors, HeapAdmissibilityFacts { containsUnresolvedType = true }) -> (Some(false), queried)
                                        | (_constructors, HeapAdmissibilityFacts { children = children }) ->
                                            linearSearchDropperAdmissibility(linearPrependDropperAdmissibilityChildren(children)(rest))(semanticType :: visited)(queried)
                | other ->
                    if canArenaResetLayout(other)
                    then linearSearchDropperAdmissibility(rest)(visited)(body)
                    else (Some(false), body)

let linearGeneralRcAdmissible (semanticType: SemanticType) (path: List(SemanticType)) (body: DropperBody) =
    match linearSearchDropperAdmissibility([LinearVisitDropperAdmissibilityType(semanticType)])(path)(body) with
        | (Some(answer), queried) -> (answer, queried)
        | (None, queried) -> frozenGeneralRcAdmissible(semanticType)(path)(queried)
