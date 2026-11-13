// The orchestration helper below is extracted unchanged from production Core.
// Its member lowerer is a callback that reports every invocation and models
// discovery of a trait goal or a settled sibling result.
type CoreLoweringState =
    | pinned: Bool
    | pending: List(Int)
    | siblings: List(Int)
    | proven: Int
    | mode: Int

type LoweredCoreValue =
    | state: CoreLoweringState
    | error: Maybe(Int)

let stateTraitGoalsPinned state = state.pinned

let withStateTraitGoalsPinned value (state: CoreLoweringState) = state with pinned = value

let statePendingTraitGoals state = state.pending

let statePendingSiblingCallResults state = state.siblings

let stateProvenSiblingCallResults state = state.proven

let withStateProvenSiblingCallResults value (state: CoreLoweringState) = state with proven = value

let coreListLength values = Ashes.Collection.List.length(values)

let settledSiblingCallResults pending count state proven settled =
    if count > 0
    then (1, true)
    else (proven, settled)

let lowerPreparedRecursiveMembers members selfBindings captures environmentTemp lower state = lower(state)

let memberLower (state: CoreLoweringState) =
    "member"
    |> Ashes.IO.print
    |> (given (_) ->
        match state.mode with
            | 1 ->
                if state.pinned
                then LoweredCoreValue(state = state, error = Some(1))
                else LoweredCoreValue(state = (state with pending = 1 :: state.pending), error = None)
            | 2 ->
                if state.proven == 0
                then LoweredCoreValue(state = (state with siblings = 1 :: state.siblings), error = None)
                else LoweredCoreValue(state = state, error = None)
            | _ -> LoweredCoreValue(state = state, error = None))
