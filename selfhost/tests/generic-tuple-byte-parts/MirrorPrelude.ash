import Ashes.Collection.List.append
import Ashes.Collection.List.reverse
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrControlFlowGraph.containsInt
import AshesCompiler.Semantics.StateMachineTransform.getDefinedTemps
import AshesCompiler.Semantics.ConstructionRetains.countTempUses
type CoreLoweringState =
    | reversedInstructions: List(IrInstruction)
    | nextTemp: Int
    | nextLabelId: Int
    | freshTemps: List(Int)

type RuntimeTempState =
    | RuntimeNewlyProduced
    | RuntimeBorrowed

type FreshTemp =
    | state: CoreLoweringState
    | temp: Int

type FreshLabel =
    | state: CoreLoweringState
    | label: Str

let resolveType (_state: CoreLoweringState) semanticType = semanticType

let runtimeTempStateOf temp (state: CoreLoweringState) =
    if containsInt(temp)(state.freshTemps)
    then Some(RuntimeNewlyProduced)
    else Some(RuntimeBorrowed)

let freshTempRun count (state: CoreLoweringState) = FreshTemp(state = (state with nextTemp = state.nextTemp + count), temp = state.nextTemp)

let freshLabel prefix (state: CoreLoweringState) = FreshLabel(state = (state with nextLabelId = state.nextLabelId + 1), label = prefix + "_" + Ashes.Text.fromInt(state.nextLabelId))

let emit kind (state: CoreLoweringState) = state with reversedInstructions = IrInstruction(instruction = kind, location = None) :: state.reversedInstructions
