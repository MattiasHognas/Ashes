import Ashes.Collection.Map.MapTree
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.Unification
import AshesCompiler.Semantics.TypeSchemes
import AshesCompiler.Frontend.Token
type CoreFunctionState =
    | admissibilityAnswers: List((SemanticType, Bool))
    | pendingTraitGoals: List((Str, SemanticType, List((Str, SemanticType))))
    | traitGoalsPinned: Bool
    | active: Bool
    | activeTraitMethods: List((Str, SemanticType, Str, Str))

type CoreTraitGoalAnswer =
    | ProvenOperandType(SemanticType)
    | ProvenAmbientMethod(Str)
    deriving {Eq}

type CoreTraitGoalProof =
    | SharedTraitGoalProof(CoreTraitGoalAnswer)
    | OrderedTraitGoalProofs(List(Maybe(CoreTraitGoalAnswer)))

type CoreProgramState =
    | provenTraitGoals: MapTree(Str, CoreTraitGoalProof)

type CoreLoweringState =
    | substitution: MapTree(Int, SemanticType)
    | functionState: CoreFunctionState
    | programState: CoreProgramState
    | currentSpan: Maybe(TextSpan)
    | nextTemp: Int

type CoreLoweringError =
    | CoreCallTypeMismatch(UnificationError, Unit)
    | CoreOperatorTypeMismatch(Str, SemanticType, SemanticType)

type LoweredCoreValue =
    | state: CoreLoweringState
    | temp: Int
    | semanticType: SemanticType
    | error: Maybe(CoreLoweringError)

let mismatchSiteOf _state = Unit

type Instruction =
    | LoadConstInt(Int, Int)
    | LoadConstBool(Int, Bool)
    | CmpIntEq(Int, Int, Int)

type ReservedCoreTemps =
    | state: CoreLoweringState
    | first: Int

let withNextTemp n (state: CoreLoweringState) = state with nextTemp = n

let emit _instruction state = state

let success temp semanticType state = LoweredCoreValue(state = state, temp = temp, semanticType = semanticType, error = None)

let lowerConstant _make semanticType (state: CoreLoweringState) = success(state.nextTemp)(semanticType)((state with nextTemp = state.nextTemp + 1))

let lowerString _text (state: CoreLoweringState) = success(state.nextTemp)(SemString)((state with nextTemp = state.nextTemp + 1))

let hasActiveTraitEvidence _traitName _operandType state =
    (let group = state.functionState
    in group.active)

let emitCoreTraitUnaryDispatch _traitName _methodName resultType _lower operandTemp _operandType state = success(operandTemp)(resultType)(state)

// Without a type table an open operand stays as lowering has it.
let recordedNotOperand operandType state = (state, operandType)

let noLower _expression state = success(0)(SemNever)(state)

let observePendingGoal _structural _key _operand state = state
