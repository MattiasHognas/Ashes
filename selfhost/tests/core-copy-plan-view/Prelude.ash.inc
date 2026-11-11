import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.HeapLayoutClassification
import AshesCompiler.Semantics.IrInstructions
import Ashes.Collection.Map.MapTree
type CoreConstructorLayout =
    | name: Str
    | tag: Int
    | tagless: Bool
    | size: Int

type ArgumentCopyPlan =
    | ScalarArgumentCopy
    | LeafArgumentCopy(Int)
    | ListHeadArgumentCopy(ListHeadCopyKind)
    | ListDeepArgumentCopy(ArgumentCopyPlan)
    | TupleArgumentCopy(List(ArgumentCopyPlan))
    | ShallowAdtArgumentCopy(Int)
    | ConstructorArgumentCopy((Int, Int, Bool, List((Int, ArgumentCopyPlan))))
    | SwitchArgumentCopy(List((Int, Int, Bool, List((Int, ArgumentCopyPlan)))))
    | NormalizerArgumentCopy(SemanticType)

type CoreLoweringState =
    | environment: TypeEnvironment
    | facts: MapTree(Str, HeapLayoutFacts)
    | resolvedOverride: Maybe(SemanticType)
    | layouts: List(CoreConstructorLayout)
    | recorded: Maybe(Bool)
    | recursiveCopy: Bool
    | admissible: Bool
    | emittedPlan: Maybe(ArgumentCopyPlan)

// The plan implementation and query boundary are extracted verbatim. Surrounding policy
// answers are injected so every flag/fallback branch can be compared with the old algorithm.
let resolveType (state: CoreLoweringState) (semanticType: SemanticType) =
    match (semanticType, state.resolvedOverride) with
        | (SemVariable(_id), Some(resolved)) -> resolved
        | _ -> semanticType
let coverageEnvironment (state: CoreLoweringState) = state.environment
let stateHeapLayoutFactsByType (state: CoreLoweringState) = state.facts
let stateConstructorsOfType (_name: Str) (state: CoreLoweringState) = state.layouts
let resultSurvivesReset (semanticType: SemanticType) (_state: CoreLoweringState) = canArenaResetLayout(semanticType)
let recordedContractRecord (_named: SemanticType) (state: CoreLoweringState) = state.recorded
let canRuntimeManageRecursiveCopyAdt (_named: SemanticType) (state: CoreLoweringState) = state.recursiveCopy
let isGeneralRcAdmissible (_named: SemanticType) (_path: List(Str)) (state: CoreLoweringState) = state.admissible
let shallowAdtCopySizeBytes (_name: Str) (_state: CoreLoweringState) = 40
let layoutAllocationSizeBytes (layout: CoreConstructorLayout) = layout.size
let bigIntCopySizeBytes = 16
let listHeadCopyKindOf (element: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(element) with
        | SemString -> Some(StringListHead)
        | SemList(inner) -> if canArenaResetLayout(resolveType(state)(inner)) then Some(InnerListHead) else None
        | resolved -> if resultSurvivesReset(resolved)(state) then Some(InlineListHead) else None

let recursive samePlan (left: ArgumentCopyPlan) (right: ArgumentCopyPlan) =
    match (left, right) with
        | (ScalarArgumentCopy, ScalarArgumentCopy) -> true
        | (LeafArgumentCopy(a), LeafArgumentCopy(b)) -> a == b
        | (ListHeadArgumentCopy(a), ListHeadArgumentCopy(b)) -> a == b
        | (ListDeepArgumentCopy(a), ListDeepArgumentCopy(b)) -> samePlan(a)(b)
        | (TupleArgumentCopy(a), TupleArgumentCopy(b)) -> samePlans(a)(b)
        | (ShallowAdtArgumentCopy(a), ShallowAdtArgumentCopy(b)) -> a == b
        | (ConstructorArgumentCopy(a), ConstructorArgumentCopy(b)) -> sameConstructor(a)(b)
        | (SwitchArgumentCopy(a), SwitchArgumentCopy(b)) -> sameConstructors(a)(b)
        | (NormalizerArgumentCopy(a), NormalizerArgumentCopy(b)) -> a == b
        | _ -> false
and samePlans (left: List(ArgumentCopyPlan)) (right: List(ArgumentCopyPlan)) =
    match (left, right) with
        | ([], []) -> true
        | (a :: restA, b :: restB) -> samePlan(a)(b) && samePlans(restA)(restB)
        | _ -> false
and sameFields (left: List((Int, ArgumentCopyPlan))) (right: List((Int, ArgumentCopyPlan))) =
    match (left, right) with
        | ([], []) -> true
        | ((a, planA) :: restA, (b, planB) :: restB) -> a == b && samePlan(planA)(planB) && sameFields(restA)(restB)
        | _ -> false
and sameConstructor (left: (Int, Int, Bool, List((Int, ArgumentCopyPlan)))) (right: (Int, Int, Bool, List((Int, ArgumentCopyPlan)))) =
    match (left, right) with
        | ((tagA, sizeA, taglessA, fieldsA), (tagB, sizeB, taglessB, fieldsB)) -> tagA == tagB && sizeA == sizeB && taglessA == taglessB && sameFields(fieldsA)(fieldsB)
and sameConstructors (left: List((Int, Int, Bool, List((Int, ArgumentCopyPlan))))) (right: List((Int, Int, Bool, List((Int, ArgumentCopyPlan))))) =
    match (left, right) with
        | ([], []) -> true
        | (a :: restA, b :: restB) -> sameConstructor(a)(b) && sameConstructors(restA)(restB)
        | _ -> false

let sameMaybePlan (left: Maybe(ArgumentCopyPlan)) (right: Maybe(ArgumentCopyPlan)) =
    match (left, right) with
        | (None, None) -> true
        | (Some(a), Some(b)) -> samePlan(a)(b)
        | _ -> false
