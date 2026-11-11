import AshesCompiler.Semantics.MatchArmOwnership
type alias SemanticType = Int

type CoreLoweringState =
    | backEdgeDummyTemps: List(Int)

let stateBackEdgeDummyTemps (state: CoreLoweringState) = state.backEdgeDummyTemps
let withStateBackEdgeDummyTemps values (state: CoreLoweringState) = state with backEdgeDummyTemps = values
let recursive containsInt value values =
    match values with
        | [] -> false
        | head :: tail -> head == value || containsInt(value)(tail)
// Narrow adapters for unrelated facts; the production arm builder below is extracted unchanged.
let branchIsEmptyListLiteral (_body: Int) = false
let resultTypeIsList (_type: Int) (_state: CoreLoweringState) = false
let isRuntimeTemp temp (_state: CoreLoweringState) = containsInt(temp)([7, 8, 90, 172])
let tempIsNewlyProduced temp state = isRuntimeTemp(temp)(state)
let statePatternOwnerResultTemps (_state: CoreLoweringState) = []
let forwardsPatternOwner (_body: Int) (_state: CoreLoweringState) = false
let constructsFreshCell (_body: Int) (_state: CoreLoweringState) = false
let isConsChainOntoSelfCall (_body: Int) (_state: CoreLoweringState) = false
let nestedJoinConsesOntoSelfResult (_body: Int) (_state: CoreLoweringState) = false
