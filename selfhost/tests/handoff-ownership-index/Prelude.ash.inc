import AshesCompiler.Semantics.OwnershipSummary.ParameterOwnership
import AshesCompiler.Semantics.ProgramParameterOwnershipIndex
import Ashes.Collection.Map.MapTree

type CoreLoweringState =
    | oldTable: List((Str, List((Str, ParameterOwnership))))
    | ownershipIndex: ProgramParameterOwnershipIndex
    | names: MapTree(Str, Bool)

let stateProgramParameterOwnership (state: CoreLoweringState) = state.ownershipIndex
let stateTopLevelNames (state: CoreLoweringState) = state.names
