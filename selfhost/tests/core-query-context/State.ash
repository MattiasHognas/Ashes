type CoreProgramState =
    | coverageTypes: TypeEnvironment
    | coverageEnvironmentVersion: Int
    | heapQueryContext: CoreHeapQueryContext
    | dropperTypes: DropperTypes
    | heapLayoutFactsByType: MapTree(Str, HeapLayoutFacts)
    | generalRcTypeFacts: MapTree(Str, (Bool, Bool))
    | contractRecordFacts: MapTree(Str, Bool)
    | constructors: List(CoreConstructorLayout)

type CoreFunctionState =
    | admissibilityAnswers: List((SemanticType, Bool))

type CoreLoweringState =
    | programState: CoreProgramState
    | functionState: CoreFunctionState
    | liveHandler: Bool
    | specialize: Maybe(List(Str))

let resolveType (_state: CoreLoweringState) semanticType = semanticType

let resultSurvivesReset semanticType _state = canArenaResetLayout(semanticType)

let canRuntimeManageRecursiveCopyAdt _semanticType _state = false

let programMayRunUnderLiveHandlerPost (state: CoreLoweringState) = state.liveHandler

let stateSpecializationFreshInputs (state: CoreLoweringState) = state.specialize

let clearAdmissibilityAnswers (state: CoreLoweringState) = state with functionState = CoreFunctionState(admissibilityAnswers = [])

let stateHeapLayoutFactsByType (state: CoreLoweringState) =
    (let group = state.programState
    in group.heapLayoutFactsByType)

let recordedGeneralRcTypeFacts name (state: CoreLoweringState) =
    (let group = state.programState
    in Ashes.Collection.Map.getStr(name)(group.generalRcTypeFacts))

let recordedContractRecord semanticType (state: CoreLoweringState) =
    match semanticType with
        | SemNamed(_id, name, []) ->
            let group = state.programState
            in Ashes.Collection.Map.getStr(name)(group.contractRecordFacts)
        | _ -> None

let recursive resultName semanticType =
    match semanticType with
        | SemFunction(_field, result, _row) -> resultName(result)
        | SemNamed(_id, name, _args) -> name
        | _ -> ""

let recursive fieldsCount semanticType =
    match semanticType with
        | SemFunction(_field, result, _row) -> 1 + fieldsCount(result)
        | _ -> 0

let stateConstructorsOfType name (state: CoreLoweringState) =
    (let group = state.programState
    in
        Ashes.Collection.List.filter(given (layout: CoreConstructorLayout) ->
            let scheme = layout.scheme
            in resultName(scheme.body) == name)(group.constructors))

let layoutAllocationSizeBytes (layout: CoreConstructorLayout) =
    (let scheme = layout.scheme
    in
        fieldsCount(scheme.body) * 8 + (if layout.tagless
        then 0
        else 8))

let shallowAdtCopySizeBytes name state =
    match stateConstructorsOfType(name)(state) with
        | head :: _rest -> layoutAllocationSizeBytes(head)
        | [] -> 8

let listHeadCopyKindOf element _state = listHeadCopyOf(element)

let bigIntCopySizeBytes = -2
