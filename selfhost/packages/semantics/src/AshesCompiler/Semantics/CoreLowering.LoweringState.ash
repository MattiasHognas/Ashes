// The lowering state: the request, loop, owner and reuse records it is built from, and the
// accessors that read and update each field. A part of `AshesCompiler.Semantics.CoreLowering`.

import Ashes.Collection.List.append
import Ashes.Collection.List.reverse
import Ashes.Collection.Map.MapTree
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.Pattern
import AshesCompiler.Frontend.Syntax.TypeExpr
import AshesCompiler.Frontend.Token.TextSpan
import AshesCompiler.Semantics.CallOwnership
import AshesCompiler.Semantics.CoreConstrainedSpecialization
import AshesCompiler.Semantics.CoreBuiltinLowering
import AshesCompiler.Semantics.CoreCapabilityLowering
import AshesCompiler.Semantics.CoreExternalLowering
import AshesCompiler.Semantics.ExternalAbi
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.HeapLayoutClassification
import AshesCompiler.Semantics.TypeGraphIndex
import AshesCompiler.Semantics.IrControlFlowGraph.containsInt
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.ResultReachSummaries.ResultReachState
import AshesCompiler.Semantics.ResultReachSummaries.ReachSummary
import AshesCompiler.Semantics.ResultReachSummaries.ReachRegistry
import AshesCompiler.Semantics.ProgramParameterOwnershipIndex
import AshesCompiler.Semantics.OwnershipInference.MoveCallSite
import AshesCompiler.Semantics.StructuralDroppers
import AshesCompiler.Semantics.SourceContext
import AshesCompiler.Semantics.TcoRuntimeManagedParams
import AshesCompiler.Semantics.PatternBindingOwnership
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.TypeSchemes
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.ReuseSpecialization
import AshesCompiler.Semantics.ReuseFunctionSpecialization
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.NodeTypeTable.NodeTypeTable
import AshesCompiler.Semantics.NodeTypeTable.settledInstantiationArguments
import AshesCompiler.Semantics.NodeTypeTable.nodeTypeAt
export (
    type ConsumerRequest(..),
    type RuntimeTempState(..),
    type CoreTcoLoop(..),
    type CoreTcoLoopFrame(..),
    type PatternOwnerSite(..),
    type CoreTcoReset(..),
    type CorePendingCallCopyOut(..),
    type CoreSettleKind(..),
    type CoreCallArgumentFrame(..),
    type CoreTakenOverSettle(..),
    type CoreSpecializationCandidate(..),
    type CoreTopLevelFunctionReference(..),
    type CoreHeapQueryFacts(..),
    type CoreHeapQueryContext(..),
    type CoreTypeObservation(..),
    type ObservedPendingGoal(..),
    type ShadowSite(..),
    value shadowing,
    value observeShadow,
    value observeShadowPending,
    value observeShadowUnrecorded,
    value withSingleLoweringMeasurement,
    value withShadowLambdaNode,
    value stateShadowLambdaNode,
    value stateSingleLowering,
    value withPendingGoalObservation,
    value observePendingGoal,
    value observeDeferredOperatorGoal,
    type CoreTraitGoalAnswer(..),
    type CoreTraitGoalProof(..),
    type CoreProgramState(..),
    type CoreFunctionState(..),
    type CoreOwnerState(..),
    type CoreTcoState(..),
    type CoreReuseState(..),
    type CoreLoweringState(..),
    type LoweredCoreValue(..),
    value emptyConsumerRequest,
    value lookupSpecializationCandidate,
    value specializationCandidateArity,
    value specializationCandidateValueOf,
    value lookupTopLevelFunctionRef,
    value emptyCoreHeapQueryContext,
    value clearAdmissibilityAnswers,
    value withTypeObservation,
    value observeTopLevelScope,
    value observeOperatorType,
    value stateTypeObservation,
    value withNodeTypes,
    value stateNodeTypes,
    value stateNodeInstantiation,
    value withStateNodeInstantiation,
    value stateSettledNodeVariables,
    value observeLoweringEvent,
    value carryTypeObservation,
    value stateConstructorLayouts,
    value withAppendedConstructorLayouts,
    value stateConstructorsOfType,
    value stateStableConstructorFields,
    value stateConstructorsOfField,
    value stateCoverageTypes,
    value withStateCoverageTypes,
    value withStateHeapQueryContext,
    value coreHeapQueryFacts,
    value queryCoreCopyPlan,
    value queryCoreAdmissibility,
    value queryCoreShallowCopy,
    value queryCoreChildren,
    value queryCoreChildTypes,
    value stateDropperTypes,
    value withStateDropperTypes,
    value withStateDropperContext,
    value stateExternalLayouts,
    value withStateExternalLayouts,
    value withStateDeclaredTypeNames,
    value stateTypeAliases,
    value withStateTypeAliases,
    value stateCapabilityLayouts,
    value withStateCapabilityLayouts,
    value stateStaticProviders,
    value withStateStaticProviders,
    value stateCapabilityGlobalCount,
    value withStateCapabilityGlobalCount,
    value stateCapabilityOperationSchemes,
    value withStateCapabilityOperationSchemes,
    value stateFailureSite,
    value withStateFailureSite,
    value stateHandledCapabilities,
    value withStateHandledCapabilities,
    value stateSourceContext,
    value withStateSourceContext,
    value withStateSourceRegions,
    value withStateSourceRegionOf,
    value stateTopLevelNames,
    value withStateTopLevelNames,
    value stateTraitEnvironment,
    value withStateTraitEnvironment,
    value stateTraitMethodLabels,
    value withStateTraitMethodLabels,
    value stateMoveFunctionTable,
    value withStateMoveFunctionTable,
    value stateMoveCallSites,
    value withStateMoveCallSites,
    value withStateLiveHandlerFunctions,
    value stateLiveHandlerFunctions,
    value stateEntryRunsUnderLiveHandlerPost,
    value programMayRunUnderLiveHandlerPost,
    value stateMoveNullaryConstructors,
    value withStateMoveNullaryConstructors,
    value stateTupleBinderParameters,
    value withStateTupleBinderParameters,
    value statePatternOwnerSites,
    value withStatePatternOwnerSites,
    value stateCellMarkerSlots,
    value withStateCellMarkerSlots,
    value stateReachSummaries,
    value withStateReachSummaries,
    value stateReachRegistry,
    value withStateReachRegistry,
    value stateReachTable,
    value withStateReachTable,
    value withStateReachByIdentity,
    value withStateReachByName,
    value withStateCallReachByKey,
    value stateProgramParameterOwnership,
    value withStateProgramParameterOwnership,
    value stateLetLambdas,
    value withStateLetLambdas,
    value stateLetLambdasByName,
    value withStateLetLambdasByName,
    value stateLetLambdaLabels,
    value withStateLetLambdaLabels,
    value stateLetLambdaIdentities,
    value withStateLetLambdaIdentities,
    value stateTopLevelFunctionRefIndex,
    value withRegisteredTopLevelFunctionRef,
    value stateInlinableHelpers,
    value withStateInlinableHelpers,
    value stateSpecializationCandidates,
    value stateSpecializationCandidateIndex,
    value withStateSpecializationCandidates,
    value stateGeneralRcOwnedResultLabels,
    value withStateGeneralRcOwnedResultLabels,
    value stateTupleResultPartLabels,
    value withStateTupleResultPartLabels,
    value stateHeapLayoutFactsByType,
    value withStateHeapLayoutFactsByType,
    value stateNormalizableRecordTypes,
    value withStateNormalizableRecordTypes,
    value stateBodyRuntimeManagedByLabel,
    value withStateBodyRuntimeManagedByLabel,
    value stateTrivialParameterFieldAccessorLabels,
    value withStateTrivialParameterFieldAccessorLabels,
    value stateDropperLabels,
    value withStateDropperLabels,
    value withStateDropperResult,
    value stateClosureDropperLabels,
    value withStateClosureDropperLabels,
    value stateResultRcEligibility,
    value withStateResultRcEligibility,
    value stateGeneralRcTypeFacts,
    value withStateGeneralRcTypeFacts,
    value withStateContractRecordFacts,
    value recordedContractRecord,
    value dropperTypesOf,
    value stateProvenTraitGoals,
    value withStateProvenTraitGoals,
    value stateProvenSiblingCallResults,
    value withStateProvenSiblingCallResults,
    value stateCurrentItem,
    value withStateCurrentItem,
    value statePendingStackClosure,
    value withStatePendingStackClosure,
    value stateCoroutineBody,
    value withStateCoroutineBody,
    value statePendingCoroutineBody,
    value stateHelperFactoryType,
    value withHelperFactoryType,
    value stateCompletedTaskFrameDropper,
    value withStatePendingCoroutineBody,
    value withCompletedTaskFrameDropper,
    value enterPendingCoroutineBody,
    value stateRuntimeAdtRequested,
    value withStateRuntimeAdtRequested,
    value statePendingOperatorDefaults,
    value withStatePendingOperatorDefaults,
    value stateEnclosingBindings,
    value withStateEnclosingBindings,
    value stateLetBoundFunctionLabels,
    value withStateLetBoundFunctionLabels,
    value stateInlineTypeVariables,
    value withStateInlineTypeVariables,
    value stateSealedOperatorDefaults,
    value withStateSealedOperatorDefaults,
    value statePendingSourceFunction,
    value withStatePendingSourceFunction,
    value stateActiveFunctionOrigin,
    value withStateActiveFunctionOrigin,
    value stateUnderLiveHandlerPost,
    value withStateUnderLiveHandlerPost,
    value withStateParallelWorkerFunctions,
    value stateParallelWorkerFunctions,
    value stateMayRunAsParallelWorker,
    value withStateMayRunAsParallelWorker,
    value stateHandlerArmOwnResult,
    value withStateHandlerArmOwnResult,
    value ordinaryRcPlacementAllowed,
    value asyncIndependentRcPlacementAllowed,
    value stateCoroutineFunctions,
    value withStateCoroutineFunctions,
    value stateMayRunInsideCoroutine,
    value withStateMayRunInsideCoroutine,
    value escapingRcPlacementAllowed,
    value generalRcEnabled,
    value statePendingClosureNormalizers,
    value withStatePendingClosureNormalizers,
    value stateResourceStates,
    value withStateResourceStates,
    value stateRecursiveDeclarationSpan,
    value withStateRecursiveDeclarationSpan,
    value stateRecursiveMemberStarts,
    value withStateRecursiveMemberStarts,
    value stateActiveTypeParameters,
    value withStateActiveTypeParameters,
    value stateActiveTraitMethods,
    value withStateActiveTraitMethods,
    value statePendingTraitGoals,
    value withStatePendingTraitGoals,
    value statePendingEqualityGoals,
    value withStatePendingEqualityGoals,
    value statePendingSiblingCallResults,
    value withStatePendingSiblingCallResults,
    value stateTraitGoalsPinned,
    value withStateTraitGoalsPinned,
    value stateDeferredLabelNext,
    value withStateDeferredLabelNext,
    value stateDeferredLabelGroups,
    value withStateDeferredLabelGroups,
    value withStateNextStringId,
    value stateBackEdgeDummyTemps,
    value withStateBackEdgeDummyTemps,
    value withStateReuseResultTemps,
    value markReuseResultTemp,
    value isReuseResultTemp,
    value statePatternOwnerCopyTemps,
    value withStatePatternOwnerCopyTemps,
    value stateReleasedPatternOwnerDuplicates,
    value withStateReleasedPatternOwnerDuplicates,
    value statePatternOwnerResultTemps,
    value withStatePatternOwnerResultTemps,
    value stateRuntimeOwners,
    value withStateRuntimeOwners,
    value stateUnresolvedCallResults,
    value withStateUnresolvedCallResults,
    value stateResolvingCallsAgain,
    value withStateResolvingCallsAgain,
    value stateRuntimeOwnerAliases,
    value withStateRuntimeOwnerAliases,
    value stateBorrowedOwners,
    value withStateBorrowedOwners,
    value statePendingBorrowedOwners,
    value withStatePendingBorrowedOwners,
    value statePendingRuntimeArgumentFlags,
    value statePendingAccessorResultRetains,
    value withStatePendingAccessorResultRetains,
    value withStatePendingRuntimeArgumentFlags,
    value stateGenericDeepCopiedListTemps,
    value withStateGenericDeepCopiedListTemps,
    value recordOwnedReferenceJoin,
    value recordRetainedArmJoin,
    value isRetainedArmJoin,
    value isOwnedReferenceJoin,
    value stateRetiredLocals,
    value withStateRetiredLocals,
    value stateOwnerReleasePlans,
    value withStateOwnerReleasePlans,
    value stateGeneralRcOwnedSlots,
    value withStateGeneralRcOwnedSlots,
    value stateGeneralRcPredecessorSlots,
    value withStateGeneralRcPredecessorSlots,
    value stateGeneralRcSlotBinders,
    value stateGeneralRcPredecessorOwners,
    value withStateGeneralRcPredecessorOwners,
    value stateGeneralRcFreshSlots,
    value withStateGeneralRcFreshSlots,
    value stateGeneralRcPartDuplicateSlots,
    value withStateGeneralRcPartDuplicateSlots,
    value stateGeneralRcAliasOwnerSlots,
    value withStateGeneralRcAliasOwnerSlots,
    value stateTakenOverConsumers,
    value withStateTakenOverConsumers,
    value stateTakenOverRecordNames,
    value withStateTakenOverRecordNames,
    value stateEscapingCaptureNames,
    value withStateEscapingCaptureNames,
    value stateTakenOverSlotSources,
    value withStateTakenOverSlotSources,
    value stateSpineFreedSpans,
    value withStateSpineFreedSpans,
    value stateOwnFieldRetainedParameters,
    value withStateOwnFieldRetainedParameters,
    value stateOwnFieldPendingParameters,
    value withStateOwnFieldPendingParameters,
    value stateLoopResultPartDuplicates,
    value withStateLoopResultPartDuplicates,
    value stateGeneralRcHandedOverPartsSlots,
    value withStateGeneralRcHandedOverPartsSlots,
    value withGeneralRcFinishedResult,
    value withClosedProducerChain,
    value isClosedProducerChain,
    value tcoExitTransferTemp,
    value isGeneralRcFinishedResult,
    value ownedSlotsOf,
    value ownedSlotsOfActiveFunction,
    value lowerInGeneralRcBranch,
    value recordGeneralRcSlotBranches,
    value slotsSharingIteration,
    value statePendingOwnerPlan,
    value withStatePendingOwnerPlan,
    value stateTcoLoop,
    value withStateTcoLoop,
    value stateTcoLoopFrame,
    value isOwnedSlotIn,
    value ownedSlotReadOf,
    value noteLoweredValueBinders,
    value matchCaseBinders,
    value withStateTcoLoopFrame,
    value stateTmcActivatedLabels,
    value withStateTmcActivatedLabels,
    value statePendingTcoResets,
    value withStatePendingTcoResets,
    value statePendingCallCopyOuts,
    value withStatePendingCallCopyOuts,
    value statePendingTakenOverSettles,
    value withStatePendingTakenOverSettles,
    value stateTakenOverHeldResults,
    value withStateTakenOverHeldResults,
    value stateTakenOverCallArguments,
    value withStateTakenOverCallArguments,
    value stateSettledBehindCopySpans,
    value withStateSettledBehindCopySpans,
    value stateWithdrawnTuplePartsRequests,
    value withStateWithdrawnTuplePartsRequests,
    value stateListHeadCells,
    value withStateListHeadCells,
    value stateSelfResultDeferredNames,
    value withStateSelfResultDeferredNames,
    value stateSiblingResultOpenNames,
    value withStateSiblingResultOpenNames,
    value stateSelfCallResultUnifications,
    value withStateSelfCallResultUnifications,
    value stateCallArgumentRecordRetains,
    value stateCollectingCallArgumentRetains,
    value withCallArgumentRetainFrame,
    value stateCallArgumentLabelStart,
    value stateCallArgumentFrameActive,
    value stateCallArgumentFrame,
    value stateCallArgumentFrameUnderBackEdge,
    value stateTcoParameterRetainSites,
    value withStateTcoParameterRetainSites,
    value stateArenaParameterCopies,
    value withStateArenaParameterCopies,
    value stateBorrowedScrutineeCells,
    value withStateBorrowedScrutineeCells,
    value stateTransientFlagClears,
    value noteTransientFlagClear,
    value stateBackEdgeArgumentSlot,
    value withStateBackEdgeArgumentSlot,
    value stateLateCopiedCellHeads,
    value withStateLateCopiedCellHeads,
    value stateArenaSuccessorLetLoop,
    value withStateArenaSuccessorLetLoop,
    value stateArenaKeptSuccessorLetLoop,
    value withStateArenaKeptSuccessorLetLoop,
    value stateArenaSuccessorJoin,
    value withStateArenaSuccessorJoin,
    value isArenaSuccessorJoin,
    value stateAffineAppendContext,
    value withStateAffineAppendContext,
    value stateAffineAppendReservation,
    value withStateAffineAppendReservation,
    value stateRecursiveProducerResultSlots,
    value withStateRecursiveProducerResultSlots,
    value stateRecursiveGroupNames,
    value withStateRecursiveGroupNames,
    value stateCurryStage,
    value withStateCurryStage,
    value stateFunctionReturnedClosureLabels,
    value withStateFunctionReturnedClosureLabels,
    value statePassthroughParameter,
    value withStatePassthroughParameter,
    value stateLoweringBodyLabel,
    value withStateLoweringBodyLabel,
    value statePredictedRuntimeManagedResultLabels,
    value withStatePredictedRuntimeManagedResultLabels,
    value stateNormalizedAlwaysReturnedParameter,
    value withStateNormalizedAlwaysReturnedParameter,
    value stateNormalizedParameterCloned,
    value withStateNormalizedParameterCloned,
    value stateEntryLambdaNode,
    value withStateEntryLambdaNode,
    value stateRecordedParameterScope,
    value withStateRecordedParameterScope,
    value stateRuntimeNormalizedArgumentLabels,
    value withStateRuntimeNormalizedArgumentLabels,
    value stateConcreteRegistry,
    value withStateConcreteRegistry,
    value stateConcreteDepth,
    value withStateConcreteDepth,
    value stateConcreteDeclines,
    value withStateConcreteDeclines,
    value stateAbstractElementTmcDeclines,
    value stateTmcDeclines,
    value recordTmcDecline,
    value carryTmcDeclines,
    value stateReuseTransferredNames,
    value withStateReuseTransferredNames,
    value stateReuseEnabled,
    value withStateReuseEnabled,
    value stateLinearReuseNames,
    value withStateLinearReuseNames,
    value stateDirectReuseCandidates,
    value withStateDirectReuseCandidates,
    value stateReuseSpecializations,
    value withStateReuseSpecializations,
    value stateSpecializingLinearParam,
    value withStateSpecializingLinearParam,
    value stateSpecializingInProgress,
    value withStateSpecializingInProgress,
    value stateSpecializationFreshInputs,
    value withStateSpecializationFreshInputs,
    value stateStackAllocateConstructor,
    value withStateStackAllocateConstructor,
    value stateScrutineeTuple,
    value withStateScrutineeTuple,
    value stateRuntimeClosureRequest,
    value withStateRuntimeClosureRequest,
    value takeRuntimeClosureRequest,
    value stateLinearSpecializationAccumulators,
    value withStateLinearSpecializationAccumulators,
    value stateRoutedSpecializationAccumulators,
    value withStateRoutedSpecializationAccumulators,
    value stateRoutedSpecializationCallees,
    value withStateRoutedSpecializationCallees,
    value stateSpecializingReuseLabel,
    value withStateSpecializingReuseLabel,
    value stateFullyReusingCallees,
    value withStateFullyReusingCallees,
    value stateInliningInProgress,
    value withStateInliningInProgress,
)

// What the context asks of the next expression lowered, stage 0's `LoweredValueRequest`: the type
// it expects, whether it wants a fresh string placed on the reference-counted heap rather than in
// the arena (`RuntimeRepresentation.String`), the owned `let` slot whose value the expression
// carries out as its result (`ConsumerCanOwn` for a tail-forwarded binding read), whether the
// expression sits in tail position of the enclosing TCO loop body (stage 0's
// `TcoContext.InTailPosition`, suppressed by `TcoTailPositionScope` under every operator operand,
// call argument, `let` value, condition, and scrutinee), and whether the expression's value
// leaves the scopes that own its parts — a tail self-call argument becoming the next iteration's
// parameter — so an aggregate it builds retains the owned bindings it stores
// (`TransfersRuntimeManagedChildren`).
// The four aggregate representations a consumer may ask for besides a string, stage 0's
// `LoweredValueRuntimeRepresentation.Adt`/`Record`/`List`/`Tuple`: a constructor application, a
// record literal, a list literal or cons cell, and a tuple placed on the reference-counted heap.
type ConsumerRequest =
    | expectedType: Maybe(SemanticType)
    // The call argument the expected type is a parameter type of: a mismatch the expected type
    // finds on the way is reported at that argument's call, as stage 0 reports it.
    | argumentSite: Maybe(CoreMismatchSite)
    | runtimeString: Bool
    | runtimeAdt: Bool
    | runtimeRecord: Bool
    | runtimeList: Bool
    | runtimeTuple: Bool
    | transferSlot: Maybe(Int)
    | tailPosition: Bool
    | transfersRuntimeManagedChildren: Bool
    // Whether the expression sits in tail position of any function body, loop or not, the
    // position where the backend fuses a self call into a jump and no copy-out block may follow
    // it; `tailPosition` above is the loop body's own flag.
    | tailCall: Bool
    // Like `tailPosition`, but a cons keeps it: a cons does not forward tail position to its
    // operands, yet the cons ITSELF stands where the function's result does, which is what makes it
    // the tail-modulo-constructor shape. Kept separate from `tailPosition` so recognizing that shape
    // cannot disturb the ownership and transfer decisions `tailPosition` drives.
    | consTailPosition: Bool
    // Stage 0's `RuntimeTcoListTailSlot`: the loop parameter slot a tail self-call's cons argument
    // extends. A cons whose tail reads that slot takes the parameter's own list over rather than
    // copying it, once the runtime says the list is reference-counted.
    | runtimeTcoListTailSlot: Maybe(Int)
    // Stage 0's `_backEdgeCopiedCell`: the cons a tail self call passes as the successor of a list
    // parameter whose back edge copies its successor with references of its own borrows its head
    // and tail, taking no pattern-owner duplicate of them.
    | borrowsCellParts: Bool
    // The same cons when the loop's placement of that parameter waits on the self call's argument
    // types (an accumulator whose type is left to inference): its cells borrow their parts once
    // those types say the back edge copies the successor.
    | lateCopiedCellParts: Bool
    // Stage 0's `CollectCopiedInnerListCells`: a cell of the inner list a copied successor cell of a
    // list of lists of copy-type values takes as its head (`walk((x - 1 :: xs) :: rest)`). The back edge
    // copies it with the outer cell and shares its reference-counted suffix with a reference of its
    // own, so it borrows its tail, and a cons tail of it is such a cell too.
    | copiedInnerCell: Bool
    // Stage 0's `CollectRecordFieldCells`: a record update or record literal a tail self call
    // passes as a successor it does not copy as a list. Its field values that are cons cells are
    // `recordFieldCell`s: a record parameter on the reference-counted heap copies the arena record
    // at the back edge with references of its own, so the duplicates those cells took are released
    // after that copy.
    | recordFieldCells: Bool
    | recordFieldCell: Bool
    // Stage 0's `_callArgumentCell`: the cons written as a call's argument and asked for as a loop's
    // exit cell (`withEscapingArgumentCell`). It reaches the cons itself, through its spans, and
    // nothing else.
    | escapingArgumentCell: Bool
    // Stage 0's `_loopChainTailCells`: the cons written as the tail of a cell of a chain consed onto a
    // loop's list parameter (`first :: separator :: acc`). It reaches the cons itself, through its
    // spans, and nothing else.
    | chainTailCell: Bool
    // Stage 0's `ConsumerCanOwn` left unset for an ordinary call's argument: the transfer asked of it
    // comes from the callee's result reaching it, not from a consumer that owns the value, so a
    // constructor built there borrows a pattern owner's read rather than taking a duplicate of it.
    | callArgument: Bool

// A temp holding a reference-counted heap value: newly produced by its instruction (the consumer
// may take the reference) or already handed on.
type RuntimeTempState =
    | RuntimeNewlyProduced
    | RuntimeTransferred

let emptyConsumerRequest =
    ConsumerRequest(
        expectedType = None,
        argumentSite = None,
        runtimeString = false,
        runtimeAdt = false,
        runtimeRecord = false,
        runtimeList = false,
        runtimeTuple = false,
        transferSlot = None,
        tailPosition = false,
        transfersRuntimeManagedChildren = false,
        tailCall = false,
        consTailPosition = false,
        runtimeTcoListTailSlot = None,
        borrowsCellParts = false,
        lateCopiedCellParts = false,
        copiedInnerCell = false,
        recordFieldCells = false,
        recordFieldCell = false,
        escapingArgumentCell = false,
        chainTailCell = false,
        callArgument = false
    )

// The self-recursive function whose innermost lambda body is lowered as stage 0's TCO loop
// (`TcoContext`): the binding's name, the number of curried parameters a tail self-call must apply,
// and how many of the curried lambdas between the binding and the loop body are still to be
// entered. A lambda entered once that count is zero is an ordinary nested closure, whose body
// leaves the loop.
type CoreTcoLoop =
    | selfName: Str
    | arity: Int
    | pendingCurried: Int
    | parameterNames: List(Str)
    | affineOrdinals: List(Int)
    | runtimeManagedOrdinals: List(Int)
    | argumentShapes: List(TcoArgumentShape)
    // The positions stage 0's `AccumulatorRebuild` holds at, no single shape describing them.
    | accumulatorRebuildOrdinals: List(Int)
    | patternFacts: List(PatternBindingFact)
    // Every parameter the loop body matches directly by a constructor pattern, with the first such
    // constructor, in the order the body first matches them (stage 0's
    // `CollectCtorMatchedScrutinees`).
    | matchedAccumulators: List((Str, Str))
    // Every parameter the loop body hands straight to a single-parameter specialization candidate
    // as its accumulator, with that callee's name (stage 0's `CollectSpecializableCallArgs`).
    | specializationAccumulators: List((Str, Str))
    // Whether the body contains a `head :: self(...)` cons in tail position, decided syntactically
    // before the body is lowered, because the spine slots have to be reserved at loop entry — ahead
    // of a filter-shaped body, whose ordinary tail self-call in one arm is reached before the
    // transformed cons in the other.
    | tmcShapePresent: Bool
    // For every parameter position of a self-call, the parameters whose value or a part of it the
    // position may receive; None when the body refers to the function other than by a full
    // self-call, or holds a node the walk does not follow (stage 0's `SelfCallParameterFlow`).
    | selfCallParameterFlow: Maybe(List(List(Int)))
    // The same flow followed through a list's spine alone (stage 0's `SelfCallSpineFlow`).
    | selfCallSpineFlow: Maybe(List(List(Int)))
    // The loop's body, the innermost lambda body of the binding, read by the back-edge rules that
    // follow the names a self call passes (stage 0's `TcoContext.Body`).
    | body: Expr
    // The call policy the flow was computed under (stage 0's `TcoContext.FlowPolicy`), keyed by a
    // call's callee name, argument count and accumulator key: every argument flows until the
    // specialization scan has run, when the policy is due (`callFlowPending`) and the body's
    // lowering computes it and the flow again.
    | callFlow: List((Str, Int, Str, List(Bool)))
    | callFlowPending: Bool
    | emitsLoop: Bool
    // Every chain parameter's type in parameter order, recorded as its lambda is entered (stage 0's
    // `TcoContext.ParamTypes`).
    | parameterTypes: List(SemanticType)
    // The synthetic slot of every parameter the body never reads, with that parameter's type (stage 0's
    // `TcoContext.UnreadParamSlots`): nothing captured it, so it holds no argument at the loop entry.
    | unreadSlots: List((Int, SemanticType))

// The emitted entry of the active loop body (stage 0's slot-level `TcoContext`): the label the
// back edge jumps to, the parameter slots it stores into (parameter order), the per-iteration
// watermark and stack-pointer slots it restores, the loop-entry slots the reset reads, the
// first local allocated after the entry, so a runtime owner registered at or above it is
// iteration-local, and the instruction count at the point right before the loop-entry brackets
// are emitted — the splice point runtime-managed parameter entry normalization is inserted at
// once the whole body has resolved which parameters are runtime-managed (stage 0's
// `LowerLambdaCoreSpliceRuntimeManagedTcoParams`, ported without a deferred placeholder
// instruction: `finalizeTcoRuntimeManagedParams` regenerates the entry block against an empty
// instruction list and reinserts it at this recorded depth).
// `listActiveSlots` pairs every parameter slot whose self-call shape could place a list on the
// reference-counted heap with the local that says, at runtime, whether the slot currently holds
// a reference of its own (stage 0's `RuntimeManagedParamActiveSlots`), allocated at the loop
// entry ahead of the body's own locals for a parameter whose type is resolved there, and at the
// back edge or the body's end that resolves it otherwise; `runtimeManagedListSlots` are the ones
// the resolved types admitted once the body was lowered, each with its active slot and element
// type.
// `affineReservationSlots` pairs every affine accumulator's parameter slot with the reservation
// start and end locals its in-place append grows (stage 0's `AffineResvSlots`), and
// `runtimeManagedStrSlots` are the `Str` parameters admitted to the affine placement once the
// body was lowered, each with its active slot.
type CoreTcoLoopFrame =
    | bodyLabel: Str
    | parameterSlots: List(Int)
    | arenaCursorSlot: Int
    | arenaEndSlot: Int
    | stackPointerSlot: Int
    | fixedCursorSlot: Int
    | fixedEndSlot: Int
    | compactionSizeSlot: Int
    | ownerDepth: Int
    | entrySpliceCount: Int
    | listActiveSlots: List((Int, Int))
    | affineReservationSlots: List((Int, Int, Int))
    | runtimeManagedListSlots: List((Int, Int, SemanticType))
    | runtimeManagedAdtSlots: List((Int, Int, SemanticType, Str))
    | runtimeManagedStrSlots: List((Int, Int))
    // The tail-modulo-constructor spine: the cell whose tail field is still nil and must receive the
    // next cell, and the first cell, which becomes the function's result. Both -1 when the loop has
    // no `head :: self(...)` cons.
    | tmcDestSlot: Int
    | tmcResultSlot: Int
    // Stage 0's `ExitParameterOwnFields`: every arena parameter whose constructor successor the
    // exit releases, as (slot, flag slot, ordinal), and the successor cells its self-calls built.
    | exitOwnFieldFlags: List((Int, Int, Int))
    | exitOwnFieldCells: List((Int, Int))

// A pattern owner joined to its emitted slot (stage 0's `PatternBindingPlacementSite`): the
// binder's slot, the loop parameter slot it was extracted from, the instruction count right
// after its arm's pattern was lowered (where a protective duplicate is spliced in once the root
// parameter's placement is known), its type, and its fact.
type PatternOwnerSite =
    | siteSlot: Int
    | siteRootSlot: Int
    | siteInsertCount: Int
    | siteType: SemanticType
    | siteFact: PatternBindingFact
    // Bound out of a tuple matched on the reference-counted heap, which holds its own counted copy of
    // each element (stage 0's `HeldByReferenceCountedTuple`).
    | siteHeldByTuple: Bool

// A back edge awaiting its arena block (stage 0's `PendingTcoReset`): the argument types decide
// the reset once the whole body is lowered, after the iteration-local owners are released. The
// argument temps, the parameters' old values, and whether each argument was produced on the
// reference-counted heap feed the runtime-managed reset, which restores the fixed loop-entry
// watermark and re-establishes every runtime-managed parameter's own reference.
type CoreTcoReset =
    | resetId: Int
    | argumentTypes: List(SemanticType)
    | ownedDrops: List((Int, Str))
    // The owned slots registered when the back edge was scheduled, stage 0's
    // `RecordGeneralRcBackEdgeSlots` (a slot another path stored reads empty here and is skipped):
    // the predecessor slots, each with the ordinals of the successors that may still name its value
    // unless they are placed on the reference-counted heap, and the ordinals of the successors
    // whose expression names nothing that can hold a part of it, on a path that did not normalize
    // the successors and on one that did (stage 0's `SuccessorsNamingNoPartOf`).
    | generalRcPredecessorDrops: List((Int, SemanticType, List(Int), List(Int), List(Int)))
    // The other owned slots, each with its fresh flag slot (-1 outside a loop) and the ordinals of
    // the successors whose type may hold a part of its value (stage 0's `NoSuccessorCanHoldPartOf`
    // type test); per successor, the owned slot it is a plain read of (-1 for none) and whether it
    // is a loop parameter or a part of one (stage 0's `IsLoopParameterPart`).
    | generalRcSlotFacts: List((Int, SemanticType, Int, List(Int)))
    // Per such slot holding a value bound to names, the successors naming nothing bound from it, on a
    // path that did not normalize the successors and on one that did (stage 0's `NoSuccessorNamesPartOf`).
    | generalRcSlotUnnamed: List((Int, List(Int), List(Int)))
    // Per such slot with a fresh flag, the successors whose type may hold a part of its value other
    // than a string: a string a successor keeps holds a reference of its own (stage 0's
    // `NoSuccessorCanHoldPartOf` on the fresh path).
    | generalRcFreshSlotBlockers: List((Int, List(Int)))
    | argumentOwnedSlots: List(Int)
    | argumentParameterParts: List(Bool)
    // Per successor, whether it is a loop parameter, a part of one, or inline heads consed onto one:
    // what a value this iteration produced is judged against (stage 0's strict parameter parts).
    | argumentStrictParameterParts: List(Bool)
    | arenaCursorSlot: Int
    | arenaEndSlot: Int
    | fixedCursorSlot: Int
    | fixedEndSlot: Int
    | argumentTemps: List(Int)
    | oldTemps: List(Int)
    | argumentRuntime: List(Bool)
    | argumentExpressions: List(Expr)
    // Per successor, whether it is written `head :: tail` over one of the loop's parameters (stage 0's
    // `SingleFreshCons`), decided while the body's bindings are in scope.
    | argumentSingleFreshCons: List(Bool)
    // Per successor, whether it hands on its own parameter as it is (`isTcoBackEdgeArgPassThrough`), decided
    // while the body's bindings are in scope: by the deferred reset a `let` shadowing the parameter's
    // name is out of scope, and its value would read as the parameter's own.
    | argumentPassThrough: List(Bool)
    // Per successor, whether it is a retain taken for the transfer out of an iteration-local owner
    // (stage 0's `TransferRetainedArgs`).
    | argumentTransferRetained: List(Bool)
    // The roots of pattern bindings handed on as their own parameter's successor, each loaded at
    // the self call with its type, released at the back edge once the other successors hold their
    // own references (stage 0's `TransferredRootDrops`).
    | transferredRootDrops: List((Int, SemanticType))
    // The cell-head duplicates the successors' arena cells took (`lateCopiedCellHeads`), released once
    // a successor whose parameter is a list on the reference-counted heap has been copied.
    | lateCopiedCellHeads: List((Int, Int, SemanticType))
    // Per owned drop, the ordinals of the successors whose type may hold a part of its value and
    // the ordinals of the successors that are a call's result reaching its slot (stage 0's
    // `ArenaSuccessorMayHoldPartOf`).
    | ownedDropFacts: List((Int, List(Int), List(Int)))

// A call result whose copy-out kind was undecidable when the call closed its window, stage 0's
// `PendingCallResultCopyOut`: the result travels through `resultSlot`, its reload is the one
// `LoadLocal(reloadTemp, resultSlot)` in the function, and the copy-out block resolved once the
// body is lowered belongs right before that reload (or, when the resolved type needs no
// copy-out, the store and the reload are removed and the reload's temp reads the result itself).
// The reload is found by that instruction and never by its position: entry normalizations and
// direct-reuse entry copies are spliced into the body after a copy-out is recorded, so any
// position recorded here is stale by the time the blocks resolve.
type CorePendingCallCopyOut =
    | deferredCursorSlot: Int
    | deferredEndSlot: Int
    | deferredPreRestoreSlot: Int
    | deferredResultSlot: Int
    | deferredResultTemp: Int
    | deferredReloadTemp: Int
    | deferredType: SemanticType
    // Whether the call is a self call whose result the recursive binding hands over owned, and
    // whether its type resolved to one of the contract's types, so the result slot is an owned
    // slot of the function and the call's arena-result request is withdrawn.
    | deferredSelfCall: Bool
    | deferredOwned: Bool
    // The slots of the `let` bindings of the reload that release a heap copy at their scope exit:
    // each scope exit holds a placeholder (`letCopyReleaseBase` plus the slot) resolved with this
    // copy-out once its type is known.
    | deferredLetReleaseSlots: List(Int)
    // How many of the loop's back edges were lowered before the call: a call no back edge follows
    // in lowering order is never followed by one at run time.
    | deferredBackEdgesBefore: Int
    // Whether the call sits in a loop body, for the step-8 shadow comparison only.
    | deferredInLoopBody: Bool

// What a settlement releases: the list record's tuple field (its type), or the own fields of an arena
// result cell (whether the type is tagless, and per constructor its tag, taglessness and heap fields).
// The call-argument frame of the call being lowered: none outside a call; an ordinary call's; or
// that of a call nested in a loop's back-edge argument (`lowerItems(rest)(prepareGroup([...])(total))`),
// whose argument's tuple owner duplicates the call releases as any call's, where the self-call's own
// argument records none.
type CoreCallArgumentFrame =
    | NoCallArgumentFrame
    | OrdinaryCallArgumentFrame
    | BackEdgeNestedCallArgumentFrame
    deriving {Eq}

type CoreSettleKind =
    | ListRecordKind(SemanticType)
    | OwnFieldKind(Bool, List((Int, Bool, List((Int, SemanticType)))))

// A settlement of a taken-over record (stage 0's `PendingTakenOverSettle`): the walk releasing what
// a callee's arena cells took over, at a placeholder resolved once the body is lowered.
type CoreTakenOverSettle =
    | settleId: Int
    | settleTemp: Int
    | settleCallee: Str
    | settleKind: CoreSettleKind

// Prepared function lookup indexes: declaration-first candidates and newest registered refs.
type CoreSpecializationCandidate =
    | parameters: List(Str)
    | value: Expr

type CoreTopLevelFunctionReference =
    | label: Str
    | scheme: TypeScheme

let recursive specializationCandidateIndexInto (candidates: List((Str, List(Str), Expr))) (index: MapTree(Str, CoreSpecializationCandidate)) =
    match candidates with
        | [] -> index
        | (name, parameters, value) :: rest ->
            match Ashes.Collection.Map.getStr(name)(index) with
                | Some(_earlier) -> specializationCandidateIndexInto(rest)(index)
                | None ->
                    index
                    |> Ashes.Collection.Map.setStr(name)(CoreSpecializationCandidate(parameters = parameters, value = value))
                    |> specializationCandidateIndexInto(rest)

let specializationCandidateIndex candidates = specializationCandidateIndexInto(candidates)(Ashes.Collection.Map.empty)

let lookupSpecializationCandidate (name: Str) (index: MapTree(Str, CoreSpecializationCandidate)) =
    match Ashes.Collection.Map.getStr(name)(index) with
        | Some(CoreSpecializationCandidate { parameters = parameters, value = value }) -> Some((parameters, value))
        | None -> None

let specializationCandidateArity (name: Str) (index: MapTree(Str, CoreSpecializationCandidate)) =
    match Ashes.Collection.Map.getStr(name)(index) with
        | Some(CoreSpecializationCandidate { parameters = parameters, value = value }) ->
            value
            |> specializationArity(parameters)
            |> Some
        | None -> None

let specializationCandidateValueOf (name: Str) (index: MapTree(Str, CoreSpecializationCandidate)) =
    match Ashes.Collection.Map.getStr(name)(index) with
        | Some(CoreSpecializationCandidate { value = value }) -> Some(value)
        | None -> None

let registerFunctionReference (name: Str) (label: Str) (scheme: TypeScheme) (index: MapTree(Str, CoreTopLevelFunctionReference)) = Ashes.Collection.Map.setStr(name)(CoreTopLevelFunctionReference(label = label, scheme = scheme))(index)

let lookupTopLevelFunctionRef (name: Str) (index: MapTree(Str, CoreTopLevelFunctionReference)) =
    match Ashes.Collection.Map.getStr(name)(index) with
        | Some(CoreTopLevelFunctionReference { label = label, scheme = scheme }) -> Some((label, scheme))
        | None -> None

// End prepared function lookup indexes.
// Completed context-free facts belong to this coverage environment and graph lineage.
// Dropper synthesis uses a separately renumbered environment and its own context.
type CoreHeapQueryFacts =
    | copyPlan: Maybe(HeapCopyPlanFacts)
    | admissibility: Maybe(HeapAdmissibilityFacts)
    | shallowCopy: Maybe(Bool)
    | children: Maybe(List(HeapLayoutChild))
    | childTypes: Maybe(List(SemanticType))

type CoreHeapQueryContext =
    | graphIndex: TypeGraphIndex
    | facts: MapTree(Int, CoreHeapQueryFacts)

let emptyCoreHeapQueryFacts = CoreHeapQueryFacts(copyPlan = None, admissibility = None, shallowCopy = None, children = None, childTypes = None)

let emptyCoreHeapQueryContext environment = CoreHeapQueryContext(graphIndex = emptyTypeGraphIndex(environment), facts = Ashes.Collection.Map.empty)

// What a lowering asked to observe its types records for a comparison with the standalone
// inference: the bindings in scope where the trailing expression is lowered, and every binary
// operator site lowered, by its span, with its trait and left operand type, most recent first.
// A goal site is a pending trait goal, a speculative equality (`structural`), or an operator whose
// two operands were both still open, emitted in its integer form for the finalization rewrite
// (`deferredOperator`).
type ObservedPendingGoal =
    | key: Str
    | structural: Bool
    | deferredOperator: Bool
    | region: Int
    | node: Maybe(Int)
    | operand: SemanticType
    | activeMethods: List((Str, SemanticType, Str, Str))

// A site where an order-dependent rule (numbered as in the step-8 plan) decided otherwise than the
// same rule over the final type would, or where no final type answers it. `final` is None when the
// table records nothing closed for the site. `calleeFacts` holds the callee of a call whose result
// was asked for in the arena because its body was not lowered yet; the comparison is made once
// every body is lowered.
type ShadowSite =
    | entry: Int
    | region: Int
    | node: Maybe(Int)
    | span: Maybe(TextSpan)
    | current: Str
    | final: Maybe(Str)
    // Why the table answers nothing for the site, when `final` is None.
    | reason: Str
    | calleeFacts: Maybe(CoreCalleeFacts)

type CoreTypeObservation =
    | goalSites: Maybe(List(ObservedPendingGoal))
    | topLevelScope: List(CoreBinding)
    | operators: List((Maybe(TextSpan), Str, SemanticType))
    // How many operator sites took their operand type from the recorded node types where lowering
    // would otherwise have deferred or pended the goal, how many deferred or pended one, and how
    // many function bodies were lowered again, in the lowering that was kept.
    | tableOperators: Int
    | deferredOperators: Int
    | relowerings: Int
    // How many calls took the type the context expects of their result from the recorded node types.
    | tableCallResults: Int
    // How many calls lowered against a recorded expected type failed and were lowered again without it.
    | tableFallbacks: Int
    // Concrete source-copy calls whose argument tree was skipped or inferred.
    | recordedCopyCalls: Int
    | inferredCopyCalls: Int
    // The step-8 shadow comparison: per order-dependent rule (1 to 11, and 12 and 13 for entry 8c), how many sites the rule
    // decided as the rule over the final type would, and every other site.
    | shadowAgree: List(Int)
    | shadowSites: List(ShadowSite)
    // A measurement of the re-lowering cost: no body is lowered a second time, whatever its first
    // lowering left open, so the time of a lowering without rules 4 and 10 can be set against the
    // ordinary one. The lowered program is not meant to be used.
    | singleLowering: Bool
    // The node of a lambda applied where it is written, whose own node the call spine passes over:
    // the step-8 shadow comparison reads its parameter bindings there.
    | shadowLambdaNode: Maybe(Int)

type CoreTraitGoalAnswer =
    | ProvenOperandType(SemanticType)
    | ProvenAmbientMethod(Str)
    deriving {Eq}

type CoreTraitGoalProof =
    | SharedTraitGoalProof(CoreTraitGoalAnswer)
    | OrderedTraitGoalProofs(List(Maybe(CoreTraitGoalAnswer)))

type CoreProgramState =
    | constructorLayouts: List(CoreConstructorLayout)
    | constructorIndex: CoreConstructorLookupIndex
    | coverageTypes: TypeEnvironment
    | coverageEnvironmentVersion: Int
    | coverageOpenSchemes: Maybe((Int, List(TypeScheme)))
    // The names among the coverage bindings whose scheme requires `Eq`, `Ord` or `Not`, for the
    // environment version they were read from.
    | coverageOperatorConstrainedNames: Maybe((Int, MapTree(Str, Bool)))
    | heapQueryContext: CoreHeapQueryContext
    | dropperTypes: DropperTypes
    | builtinLayouts: List(CoreBuiltinLayout)
    | externalLayouts: List(CoreExternalFunctionLayout)
    | declaredTypeNames: MapTree(Str, Bool)
    // Stage 0's `_typeAliases`: every type alias of the program by name, with its parameter names
    // and target, expanded wherever a type expression is converted.
    | typeAliases: MapTree(Str, (List(Str), TypeExpr))
    | capabilityLayouts: List(CoreCapabilityLayout)
    | staticProviders: List(CoreStaticProviderLayout)
    | capabilityGlobalCount: Int
    | capabilityOperationSchemes: List((Str, Str, TypeScheme))
    // The capabilities an enclosing `handle` discharges where lowering stands: an operation of one
    // resolves through handler evidence, never through a static provider.
    | handledCapabilities: List(Str)
    // The first lowering error that left an expression with a span, with the location of the
    // innermost such expression, where stage 0 reports it.
    | failureSite: Maybe((CoreLoweringError, IrSourceLocation))
    | sourceContext: Maybe(SourceContext)
    // For a stitched program, the source region (an item region of `sourceContext`) each top-level
    // binding was declared in, by compiler name, so its spans resolve against its own file.
    | sourceRegionOfName: MapTree(Str, Int)
    | topLevelNames: MapTree(Str, Bool)
    | letLambdas: List((Str, List(Str), Expr))
    | letLambdasByName: MapTree(Str, (List(Str), Expr))
    // The program's top-level functions, every call site among them, and the constructors that are the
    // only nullary one of their type (the nullary seeds), the census the
    // move-safety proof of a loop parameter is drawn from.
    | moveFunctionTable: List((Str, List(Str), Expr))
    | moveCallSites: List(MoveCallSite)
    | moveNullaryConstructors: List(Str)
    // Stage 0's `_maFunctionsMayExecuteUnderLiveHandlerPost`: the top-level functions that may run
    // while a one-shot handler's post is pending, and whether the entry scope may.
    | liveHandlerFunctions: MapTree(Str, Bool)
    // Stage 0's `_maFunctionsMayExecuteAsParallelWorker`: the top-level functions that may run on a
    // structured-parallelism worker thread.
    | parallelWorkerFunctions: MapTree(Str, Bool)
    // Stage 0's `_maFunctionsMayExecuteInsideCoroutine`: the top-level functions that may run inside
    // a coroutine, written in or reachable from an `async` body.
    | coroutineFunctions: MapTree(Str, Bool)
    | entryRunsUnderLiveHandlerPost: Bool
    | anyRunsUnderLiveHandlerPost: Bool
    | patternOwnerSites: List(PatternOwnerSite)
    // Arena cells' pattern-owner markers handed to owned slots: (slot, marker, zero temp the slot
    // holds until the marker is promoted).
    | cellMarkerSlots: List((Int, Int, Int))
    // The binders matched out of a reference-counted tuple scrutinee at a loop parameter's own
    // position, each with that parameter's slot (stage 0's `_tupleBinderParameterSlots`).
    | tupleBinderParameters: List((Int, Int))
    | bodyRuntimeManagedByLabel: MapTree(Str, Bool)
    // Stage 0's `_isTrivialParameterFieldAccessorByLabel`: the functions whose whole body is a
    // borrowed field read of their own parameter, of a type with a shallow copy-out.
    | trivialParameterFieldAccessorLabels: MapTree(Str, Bool)
    // The functions whose compiled result is owned under the contract: each normalizes a result of
    // the contract's types at its return, so a caller that names one takes the result over as it is.
    | generalRcOwnedResultLabels: List(Str)
    // The spans of calls whose result an applied lambda discards (`body(x) |> (given (_) -> ...)`),
    // noted when the lambda's call is lowered and taken by the discarded call itself.
    | discardedResultSpans: List(TextSpan)
    // The functions whose generic tuple result hands its contract elements over when a caller asks,
    // by label: the element indices whose references the caller takes.
    | tupleResultPartLabels: MapTree(Str, List(Int))
    | letLambdaLabels: MapTree(Str, Str)
    // The lambda identity (`lambdaIdentityOf`) recorded under each let-bound function's name,
    // resolving its whole-program result-reach summary among same-named functions.
    | letLambdaIdentities: MapTree(Str, Int)
    // The whole-program result-reach summaries of the program being lowered.
    | reachSummaries: List(ReachSummary)
    // The registry and the summaries table the taken-over record proof walks a function in the record
    // mode with; no registry for a lowering without a program.
    | reachRegistry: Maybe(ReachRegistry)
    | reachTable: MapTree(Str, ResultReachState)
    // The heap-layout facts of every declared argument-free named type, by the type's name,
    // recomputed whenever the constructor layouts change.
    | heapLayoutFactsByType: MapTree(Str, HeapLayoutFacts)
    // Whether a named type is a single-constructor record with no call copy-out that the entry
    // normalization's copy re-establishes, by the type's text: asked at every call whose result
    // ownership is settled at run time, and answered once per type.
    | normalizableRecordTypes: MapTree(Str, Bool)
    | reachByIdentity: MapTree(Str, ReachSummary)
    | reachByName: MapTree(Str, ReachSummary)
    // Query-only argument reach answers, frozen with the selected function summaries.
    | callReachByKey: MapTree(Str, PreparedCallReach)
    | programParameterOwnership: ProgramParameterOwnershipIndex
    | dropperLabels: DropperLabelCache
    | resultRcEligibility: Maybe(MapTree(Str, Bool))
    // Per argument-free named type of the program: whether it is admissible to the general contract and
    // whether it reaches a type the contract governs, each walked once when the program's lowering starts.
    | generalRcTypeFacts: MapTree(Str, (Bool, Bool))
    // Per argument-free named type of the program: whether it is a record that joins the contract,
    // classified once when the program's lowering starts, each type after the ones it holds.
    | contractRecordFacts: MapTree(Str, Bool)
    // Stage 0's `_inlinableFunctions`: the let-bound non-recursive functions whose body allocates
    // or calls, spliced into a call site while a reuse token is live or under a loop's back edge
    // when their result is fresh; and stage 0's `_inliningInProgress`, the helpers whose bodies
    // are being spliced, so a helper is never spliced into itself.
    | inlinableHelpers: List(Str)
    // Stage 0's `_specializableFunctions`: every self-recursive top-level function of the program
    // by name, with its curried parameter names and its whole lambda value, so a call passing a
    // provably unique last argument can be routed to an in-place-reuse specialization of it; the
    // specializations generated so far, by the name and accumulator type they were monomorphized
    // for; and stage 0's `_specializingLinearParam`, the parameter the specialization being
    // generated treats as a linear reuse root, consumed by the lambda that binds it.
    | specializationCandidates: List((Str, List(Str), Expr))
    | specializationCandidateByName: MapTree(Str, CoreSpecializationCandidate)
    // Stage 0's `_topLevelFunctionRefs`: each top-level `let` whose lambda captured nothing, by
    // name with its code label and generalized scheme, so a body spliced into a scope that never
    // captured it (an inlined helper) can rebuild its closure from the label with a null
    // environment.
    | topLevelFunctionRefs: List((Str, Str, TypeScheme))
    | topLevelInferenceBindings: List((Str, TypeScheme, Bool))
    // The same references as the inference environment reads them, so a query shares them instead
    // of copying them: the ones stored with a free variable, youngest first; whether a later
    // reference took the name of one of those; and every reference with its stored scheme over
    // the coverage bindings of the environment version it was built for.
    | topLevelInferenceOpen: List((Str, TypeScheme))
    | topLevelInferenceOpenShadowed: Bool
    | topLevelInferenceEnvironment: Maybe((Int, List((Str, TypeScheme))))
    | topLevelFunctionRefByName: MapTree(Str, CoreTopLevelFunctionReference)
    // The standard trait environment with the program's own implementations registered, the
    // evidence a trait-mapped operator at a concrete operand type resolves against.
    | traitEnvironment: TypeEnvironment
    // Stage 0's compiled-instance cache: each implementation method already lowered as
    // a capture-free closure helper, by trait, method, and operand type, with its label and
    // type, so a later site rebuilds the closure from the label instead of lowering the body
    // again.
    | traitMethodLabels: MapTree(Str, (Str, SemanticType))
    // Stage 0's `_runtimeManagedClosureDropperLabels`: the `__rc_cdrop_N` function synthesized
    // for each owned-capture layout (`offset:type;...`), shared by every closure with that layout.
    | closureDropperLabels: List((Str, Str))
    // Stage 0's `_provenAmbiguousTraitGoalTypes`: the operand types a first pass proved for those
    // sites, read by the pass that lowers with them pinned.
    | provenTraitGoals: MapTree(Str, CoreTraitGoalProof)
    // Stage 0's `_provenCallResultTypes` for the calls between the members of a recursive group: the
    // result type the group's first lowering settled for a call to a member whose result was still
    // open there, by call site, pinned at the same site when the group is lowered again.
    | provenSiblingCallResults: MapTree(Str, SemanticType)
    // None unless the lowering was asked to observe its types; nothing reads it while lowering.
    | typeObservation: Maybe(CoreTypeObservation)
    // The types the standalone inference recorded against the numbered program, when lowering was
    // given one.
    | nodeTypes: Maybe(NodeTypeTable)
    // The instantiation of the generic body being lowered at a concrete instance: each quantified
    // variable of the body's recorded scheme, by the inference's id, with its recorded type at that
    // instance. Empty outside such a body.
    | nodeInstantiation: List((Int, SemanticType))
    // The inference variables every reference to their binding instantiates at one closed type,
    // read off `nodeTypes` once when it is given.
    | settledNodeVariables: MapTree(Int, SemanticType)

type CoreFunctionState =
    | admissibilityAnswers: List((SemanticType, Bool))
    | deferredLabelNext: Int
    | deferredLabelGroups: List((Int, Int))
    | nextStringId: Int
    | currentItem: Int
    | pendingStackClosure: Bool
    // Whether the function being lowered is an `async` body, where an `await` suspends, and
    // whether the next lambda entered is one.
    | coroutineBody: Bool
    | pendingCoroutineBody: Bool
    | helperFactoryType: Maybe(SemanticType)
    | completedTaskFrameDropper: Maybe(Str)
    | runtimeAdtRequested: Bool
    | pendingOperatorDefaults: List((Int, SemanticType))
    // The bindings the function being lowered was created among, the environment its scheme is
    // generalized against, which its own bindings no longer hold (stage 0's `_enclosingScopesByLabel`).
    | enclosingBindings: List(CoreBinding)
    // The functions a `let` binds, with the curry stages each one returns (stage 0's
    // `_letBoundFunctionLabels`).
    | letBoundFunctionLabels: List(Str)
    // The type variables written in the inline parameter annotations of the function being
    // lowered, by name (stage 0's `_inlineAnnotationTypeVariables`).
    | inlineTypeVariables: List((Str, SemanticType))
    | sealedOperatorDefaults: List((Str, Int, SemanticType))
    | pendingSourceFunction: Maybe(SourceFunctionOrigin)
    | activeFunctionOrigin: Maybe(IrFunctionOrigin)
    // Stage 0's `OwnershipPlacementContext.MayExecuteUnderLiveHandlerPost` of the function being
    // lowered: it may run while a one-shot handler's post is pending, so its values stay off the
    // reference-counted heap. A nested function takes its enclosing function's answer.
    | underLiveHandlerPost: Bool
    // Stage 0's `OwnershipPlacementContext.MayExecuteAsParallelWorker` of the function being lowered:
    // a worker frees its own heap when it exits, so a value of its is copied out, never shared. A
    // nested function takes its enclosing function's answer.
    | mayRunAsParallelWorker: Bool
    // Stage 0's `MayExecuteInsideCoroutine` for the function being lowered; a nested function takes
    // its enclosing function's answer.
    | mayRunInsideCoroutine: Bool
    // Stage 0's `_loweringHandlerArmOwnResult`: an operation arm closure is being lowered, whose own
    // escaping result may reach the reference-counted heap under a live handler post, since the
    // perform site always asks for its arena form and releases the original.
    | handlerArmOwnResult: Bool
    | pendingClosureNormalizers: List((Str, IrFunctionOrigin, List(SemanticType), Maybe(IrSourceLocation)))
    | resourceStates: List((Int, ResourceReleaseKind))
    | recursiveDeclarationSpan: Maybe(TextSpan)
    // The later members of the top-level recursive group being lowered, each with where its `and`
    // starts, beside the group's own start.
    | recursiveMemberStarts: List((Str, Int, Int))
    // The requirement evidence of the generic implementation method being lowered, stage 0's
    // active trait dictionary parameters: for each required trait's method at the requirement's
    // type argument, the hidden parameter binding that carries its closure.
    | activeTraitMethods: List((Str, SemanticType, Str, Str))
    // The generic implementation head's type parameters by name, each standing for the fresh
    // variable its method body is lowered against, so an annotation inside the body (a recursive
    // let's declared type) names the same variables the active evidence carries.
    | activeTypeParameters: List((Str, SemanticType))
    // Stage 0's `_pendingAmbiguousTraitGoals`: a trait goal whose operand type was still a type
    // variable at the site dispatching on it, keyed by that site. Inference can learn the type
    // AFTER the site — a top-level binding whose parameter only a later use pins — so a pass
    // reaching the site cannot tell a genuinely ambiguous operand from one not determined yet.
    | pendingTraitGoals: List((Str, SemanticType, List((Str, SemanticType))))
    // The `==` and `!=` sites lowered as a speculative integer comparison because both operand
    // types were still variables there, keyed as a trait goal is, with the shared operand type.
    | pendingEqualityGoals: List((Str, SemanticType))
    // Stage 0's `_pendingCallResultTypes` for the calls between the members of a recursive group: a
    // call to a member whose result type was still open at the call, by call site.
    | pendingSiblingCallResults: List((Str, SemanticType))
    // Whether this pass lowers with a previous pass's proofs: a site the proofs do not answer is
    // ambiguous to both passes, which is the only case that is genuinely ambiguous.
    | traitGoalsPinned: Bool

type CoreOwnerState =
    | backEdgeDummyTemps: List(Int)
    // The temps holding a cell rebuilt in place or allocated into to-space, and a reuse
    // specialization's results of its own self call, stage 0's `_reuseResultTemps`: such a result
    // crosses its scope's reset as it is, never copied onto the reference-counted heap.
    | reuseResultTemps: List(Int)
    | patternOwnerCopyTemps: List(Int)
    // The pattern-owner duplicates an arena cell held and a scope's copy took over, each released
    // right after the copy by a marker promoted together with the duplicate (stage 0's
    // `_releasedPatternOwnerDuplicates`).
    | releasedPatternOwnerDuplicates: List(Int)
    | runtimeOwners: List((Int, Bool))
    // The temps holding a pattern owner's retained reference as a branch result, and the joins
    // every reaching branch stored one into: such a result crosses an arm's reset without a copy.
    | patternOwnerResultTemps: List(Int)
    | retiredLocals: List(Int)
    | ownerReleasePlans: List((Int, SemanticType, OwnedReleasePlan))
    // The slots holding a value of the contract's types the function owns (a call result), with the
    // function that made each: released once the function's result holds its own references.
    | generalRcOwnedSlots: List((Maybe(IrFunctionOrigin), Int, SemanticType))
    // The slots holding what an owned slot held before a loop iteration stored it again (stage 0's
    // predecessor slots): registered among the owned slots too, and released at a back edge by a rule
    // of their own.
    | generalRcPredecessorSlots: List((Maybe(IrFunctionOrigin), Int, SemanticType))
    // The source names bound to an owned slot's value or to its parts (the let it is bound by, the
    // pattern bindings of a match on it), stage 0's `GeneralRcOwnedSlot.Binders`; and each
    // predecessor slot with the owned slot whose binders it shares.
    | generalRcSlotBinders: List((Maybe(IrFunctionOrigin), Int, List(Str)))
    | generalRcPredecessorOwners: List((Maybe(IrFunctionOrigin), Int, Int))
    // Each owned slot stored inside a loop with the flag slot saying this iteration stored it.
    | generalRcFreshSlots: List((Maybe(IrFunctionOrigin), Int, Int))
    // The owned slots a handed-over argument was kept in for its parts (stage 0's
    // `GeneralRcOwnedSlot.HandedOverParts`): only these are released child-preserving at the return.
    | generalRcHandedOverPartsSlots: List((Maybe(IrFunctionOrigin), Int))
    // The owned slots a pattern owner's duplicate of a part of a loop parameter went to, kept for an
    // arena loop result cell (stage 0's `GeneralRcOwnedSlot.PartDuplicate`): withdrawn at the return
    // when the result is copied anyway.
    | generalRcPartDuplicateSlots: List((Maybe(IrFunctionOrigin), Int))
    // The owned slots holding the reference an escaping arena alias keeps of an owner it borrows,
    // each with the alias as it was stored (stage 0's `GeneralRcOwnedSlot.AliasTemp`): a result
    // reaching the alias reaches the slot.
    | generalRcAliasOwnerSlots: List((Maybe(IrFunctionOrigin), Int, Int))
    // The part duplicates the constructor whose arguments are being lowered took, kept once its cell is
    // known to stay in the arena.
    | loopResultPartDuplicates: List((Int, SemanticType))
    // The taken-over record (stage 0's `GRC_NO_TAKENOVERRECORD`): each owned call-result slot handed
    // to a named callee, with whether the callee holds it only as whole unshared list elements, and
    // the functions found to hand a record over.
    | takenOverConsumers: List((Maybe(IrFunctionOrigin), Int, Bool))
    | takenOverRecordNames: List(Str)
    // The `let` bindings in scope whose body hands a closure capturing them out of the binding's
    // scope, as its result or inside the aggregate it results in. Such a closure keeps an arena copy
    // of the binding's value in its environment, since the binding releases its own reference when
    // its scope ends.
    | escapingCaptureNames: List(Str)
    // The call each owned call-result slot holds the result of (stage 0's `_ownedSlotSources`), and the
    // calls whose result slot a loop or a return freed the spine of (`_spineFreedSources`).
    | takenOverSlotSources: List((Maybe(IrFunctionOrigin), Int, TextSpan))
    | spineFreedSpans: List(TextSpan)
    // The own-field record's notes (stage 0's `_ownFieldRetainedParameters` and `_ownFieldPendingParameters`):
    // (loop, parameter) a cell built in the loop retained outright, or left to the placement.
    | ownFieldRetainedParameters: List((Str, Str))
    | ownFieldPendingParameters: List((Str, Str))
    // The match arms and if branches being lowered, innermost first, each by its join label and a
    // label of its own; and, per owned slot, the ones it was stored inside.
    | generalRcBranches: List((Str, Str))
    | generalRcSlotBranches: List((Maybe(IrFunctionOrigin), Int, List((Str, Str))))
    // The result temp a function already made its own at its return, with that function: a loop
    // finishes its result ahead of its exit drops, and the return must not finish it again.
    | generalRcFinishedResult: Maybe((Maybe(IrFunctionOrigin), Int))
    // The result a function's tail-modulo-cons spine closed into, with that function: a spine its
    // own cells built, taken over at the return rather than retained again.
    | closedProducerChain: Maybe((Maybe(IrFunctionOrigin), Int, Int))
    | pendingOwnerPlan: Maybe(OwnedReleasePlan)
    // The result types of the calls lowered so far in the current function body whose layout was
    // still unresolved at the call (an arena result was requested in place of a placement
    // decision), in the current function body and the closures lowered inside it. Stage 0 infers
    // the whole program before lowering, so such a call never sees a variable a later expression
    // of the same body resolves; the body is lowered again once those types are known.
    | unresolvedCallResults: List(SemanticType)
    // Set while a function body is being lowered again with its call results resolved. Every
    // closure nested in that body starts from the same resolved substitution, so it is lowered
    // once: lowering each of them twice as well makes a body nested k lambdas deep cost 2^k
    // lowerings, and a curried function of k parameters is k nested lambdas.
    | resolvingCallsAgain: Bool
    // Stage 0's `_ownershipAliases` for a match arm: each heap-typed binding an arm's pattern
    // bound out of a fresh reference-counted scrutinee, by its slot, and the slot of the owner
    // whose release covers it. A read of the binding counts as a read of the owner: an aggregate
    // or call that carries it past the owner's release retains it.
    | runtimeOwnerAliases: List((Int, Int))
    // The runtime-managed owners each arena binding's aggregate borrows a child of, by the
    // binding's slot (stage 0's `OwnershipInfo.BorrowedRuntimeOwners`), and the owners a `let`
    // being lowered collected from its value ahead of the store, by the binding's name.
    | borrowedOwners: List((Int, List((Int, SemanticType))))
    | pendingBorrowedOwners: List((Str, List((Int, SemanticType))))
    // Stage 0's `_pendingRuntimeArgumentFlags`: the ownership flag temp of each call argument
    // that reads a loop parameter (or a pattern binding extracted from one) whose placement the
    // finalize pass still decides, with that parameter's slot; the flag is zeroed at finalize
    // when the frame does not admit the parameter to the reference-counted heap.
    | pendingRuntimeArgumentFlags: List((Int, Int))
    // Stage 0's `_pendingAccessorResultRetains`: the retain of a trivial accessor call's result
    // whose argument reads a loop parameter not yet placed, with that parameter's slot; finalize
    // makes it a reference-counted retain when the frame admits the parameter.
    | pendingAccessorResultRetains: List((Int, Int))
    // Stage 0's `_genericDeepCopiedListTemps`: the result temps of this function's calls whose
    // generic list result was deep-copied out of the call window. Consumed by a later call whose
    // result reach is unknown, such a list is left to that callee's arena result rather than
    // released after the call.
    | genericDeepCopiedListTemps: List(Int)
    // Stage 0's `_ownedScopeJoinTempsByBody`: the joins every reaching arm of which hands over a
    // reference of its own, borrowing nothing from a loop parameter. A loop exit returning one
    // releases every parameter rather than transferring the one it equals.
    | ownedReferenceJoins: List((Maybe(IrFunctionOrigin), Int))
    // Stage 0's `_retainedArmJoinTempsByBody`: the joins every reaching arm of which stores a retain
    // it just took, even of a value a loop parameter owns. A loop exit returning one releases every
    // parameter rather than transferring the one it equals.
    | retainedArmJoins: List((Maybe(IrFunctionOrigin), Int))

type CoreTcoState =
    // The identity duplicates of loop-parameter reads stored into constructor cells, with the
    // parameter slot and the read's type; promoted to real retains once the frame's placements
    // are known.
    | tcoParameterRetainSites: List((Int, Int, SemanticType))
    // The loop-parameter retain markers stored into an arena tuple while the parameter's layout
    // was unresolved, with the read value and the parameter slot: each becomes an arena copy once
    // the frame places the parameter on the reference-counted heap.
    | arenaParameterCopies: List((Int, Int, Int, SemanticType))
    // The arena cells a loop body built only to match them, with no arm binding a heap-typed part:
    // the retains of loop-parameter reads they were built with are withdrawn once the loop's
    // placements are final (stage 0's `_borrowedScrutinees`).
    | borrowedScrutineeCells: List(Int)
    // The active-flag clears a tail self-call emits for a root it releases while its pattern-bound
    // successor takes the parameter's place, with the function they belong to: the back edge sets
    // the flag again, so the parameter is still held to the loop's end (stage 0's
    // `_successorResetActiveFlagClearsByBody`).
    | transientFlagClears: List((Maybe(IrFunctionOrigin), Int))
    // The parameter slot whose tail self-call argument is being lowered, stage 0's
    // `_loweringTcoBackEdgeArgumentSlot`: a read of that parameter inside its own successor is
    // the reference the back edge moves, never retained.
    | backEdgeArgumentSlot: Maybe(Int)
    // The duplicates the heads of arena cells built for a back edge's successor took, each with its
    // parameter slot and type (stage 0's `RecordLateCopiedCellHeadDuplicate`): the next scheduled
    // reset takes them.
    | lateCopiedCellHeads: List((Int, Int, SemanticType))
    // The loop whose arena successor a `let` value being lowered becomes (stage 0's
    // `_arenaSuccessorLetLoop`): its back edge balances the retains the cells it builds hold.
    | arenaSuccessorLetLoop: Maybe(Str)
    // The loop whose arena successor keeps the `let` value being lowered through a later binding
    // (stage 0's `_arenaKeptSuccessorLetLoop`): an aggregate built there stores an arena copy of an
    // owned binding instead of a retain.
    | arenaKeptSuccessorLetLoop: Maybe(Str)
    // The span of the `if` or `match` whose value becomes a loop parameter left in the arena (stage 0's
    // `_arenaSuccessorJoin`): a mixed join there copies its reference-counted arms into the arena.
    | arenaSuccessorJoin: Maybe(TextSpan)
    // The affine accumulator whose own tail self-call argument is being lowered, stage 0's
    // `_affineAppendCtx`: its name, its parameter slot, and the loop's reservation start and end
    // slots. A `+` chain rooted at that parameter grows the reservation in place.
    | affineAppendContext: Maybe((Str, Int, Int, Int))
    // The reservation slots of the `+` whose operands were just lowered and whose leftmost leaf
    // is the armed accumulator, read by the string concatenation emitter and cleared after it.
    | affineAppendReservation: Maybe((Int, Int))
    | recursiveProducerResultSlots: List(Int)
    // The parameter of the function being lowered that its entry copies into an owned
    // runtime-managed value because the result always reaches it (stage 0's
    // `_normalizedAlwaysReturnedParameter`): its name, its slot, and its type. A read of it
    // counts as a fresh owned child of the aggregate storing it.
    | normalizedAlwaysReturnedParameter: Maybe((Str, Int, SemanticType))
    // Whether an escaping arena tuple of the function being lowered stored an independent copy of
    // its entry-normalized string parameter (stage 0's `_normalizedParameterClonedIntoArenaTuple`).
    | normalizedParameterCloned: Bool
    // The source node of the lambda whose body is being entered, or -1: a recursive member's
    // lambda is lowered from its parts, without its node in effect.
    | entryLambdaNode: Int
    // The recorded types of the enclosing lambdas' parameters, innermost first.
    | recordedParameterScope: List((Str, SemanticType))
    | runtimeNormalizedArgumentLabels: List(Str)
    | recursiveGroupNames: List(Str)
    | tcoLoop: Maybe(CoreTcoLoop)
    | functionReturnedClosureLabels: MapTree(Str, Str)
    // The parameter of the plain function being lowered whose body returns it on some arms and
    // a fresh value on the others (stage 0's `_passthroughParameter`): its name, its slot, and
    // its type. The arm returning it copies it into an owned reference-counted graph.
    | passthroughParameter: Maybe((Str, Int, SemanticType))
    // The label of the function body being lowered (stage 0's `_loweringFunctionLabels` top),
    // empty outside one.
    | loweringBodyLabel: Str
    // The labels whose result was promised reference-counted before their body was lowered, so
    // their own recursive call sites hand the result over as an owned value; the promise is
    // kept at the return.
    | predictedRuntimeManagedResultLabels: List(Str)
    // The curry stage whose body is the lambda about to be lowered, as the stage's label and
    // the lambda's parameter name, so the returned-closure link is recorded before that body's
    // own recursive calls walk it.
    | curryStage: Maybe((Str, Str))
    | tcoLoopFrame: Maybe(CoreTcoLoopFrame)
    // The body labels whose tail-modulo-constructor spine a cons actually linked a cell onto
    // (stage 0's `TmcActivated`). The spine slots are reserved from the body's shape alone, before
    // any head is lowered; a head the cell cannot own as a reference hands its cons back to the
    // ordinary path, and then there is no spine to close at the return. Kept beside the frame
    // rather than in it because a frame refreshed from a copy taken before the cons would drop it.
    | tmcActivatedLabels: List(Str)
    | pendingTcoResets: List(CoreTcoReset)
    // The call results of this function whose copy-out kind was undecidable when their window
    // closed, resolved with the pending resets once the body is lowered.
    | pendingCallCopyOuts: List(CorePendingCallCopyOut)
    // The taken-over record settlements of this function's calls, the call results it holds that a
    // later scalar consumer settles (result temp, callee, element), and the call sites a settlement
    // behind the copy already covers.
    | pendingTakenOverSettles: List(CoreTakenOverSettle)
    | takenOverHeldResults: List((Int, Str, CoreSettleKind, Maybe(TextSpan)))
    // Each named call's arguments by the call's span: index, whether it is an empty list literal, type.
    | takenOverCallArguments: List((TextSpan, Int, Bool, SemanticType))
    | settledBehindCopySpans: List(TextSpan)
    // The tuple-parts requests withdrawn from this function's calls, by result temp: the request
    // constant and the parts asked for (stage 0's `_withdrawnTupleResultPartsRequests`).
    | withdrawnTuplePartsRequests: List((Int, Int, List(Int)))
    // The arena cells of a generic list builder this function built (stage 0's `_listHeadCells`),
    // most recent first: the cell, its head, the closure the head was called through, and the
    // recursive call its tail is (-1 for the empty list).
    | listHeadCells: List((Int, Int, Int, Int))
    // The recursive bindings whose result type was still unresolved when the body being lowered
    // began: a call to one of them sees a fresh result type, stage 0's lowering-local self arrow.
    | selfResultDeferredNames: List(Str)
    // The other members of the recursive group whose result type was still unresolved when the body
    // being lowered began: stage 0 lowers the body before those members close their arrows.
    | siblingResultOpenNames: List(Str)
    // Each such call's fresh result type beside the binding's own, unified once the body is done.
    | selfCallResultUnifications: List((SemanticType, SemanticType))
    // Stage 0's `CallArgumentRetainFrame`: the retains of a record update's unchanged loop-parameter
    // fields taken while an argument the callee's result may reach is lowered, with the parameter
    // slot and the field type, released after the call once its result holds its own references;
    // the transfer retains an arena aggregate in the argument made of a live owner's read, with no
    // slot (-1); whether the argument being lowered collects them; and the label count when it
    // began, since stage 0 collects a transfer retain only on the argument's straight-line path.
    | callArgumentRecordRetains: List((Int, Int, SemanticType))
    | collectingCallArgumentRetains: Bool
    | callArgumentLabelStart: Int
    | callArgumentFrame: CoreCallArgumentFrame

type CoreReuseState =
    | reuseTransferredNames: List(Str)
    | reuseEnabled: Bool
    // The loop parameters stage 0's `_linearReuseNames` holds: accumulators the loop body matches
    // by a constructor pattern, whose dead matched cell each arm hands to a rebuild as an arena
    // reuse token.
    | linearReuseNames: List(Str)
    // Each linear accumulator's slot, type, name, and whether the whole-program move analysis
    // proves it uniquely owned at every call of the loop function, which elides its entry deep copy.
    // Each entry-copy candidate: the loop parameter's slot, its type, its name, whether the move
    // analysis proved it already unique (so the copy is elided), and whether it was admitted for a
    // specialization rather than for direct in-place reuse in this body — a specialization's own
    // reuse happens in the generated function, so the "did the body rebuild structurally" gate
    // that governs a direct candidate must not govern it.
    | directReuseCandidates: List((Int, SemanticType, Str, Bool, Bool))
    | inliningInProgress: List(Str)
    | reuseSpecializations: List((Str, Str))
    | specializingLinearParam: Maybe(Str)
    | specializingInProgress: List(Str)
    // Stage 0's `_specFreshInputNames`: while a specialization's body is being lowered, the names
    // whose values came in from the caller's per-iteration arena scratch — the specialization's own
    // parameters, plus any inlined helper parameter bound to one of them or to a value the arm
    // computed. A constructor field built from one of these dangles past the loop's arena reset
    // unless it is materialized into to-space first. `None` outside a specialization.
    | specializationFreshInputs: Maybe(List(Str))
    // Stage 0's `stackAllocate` argument to `LowerConstructorApplication`, carried here rather than
    // through every constructor-lowering signature: a one-shot request from the `let` or `match`
    // that is about to lower a constructor expression it has proven dead at the end of the frame.
    // `lowerConstructor` takes it and clears it, so a nested constructor argument never claims it.
    | stackAllocateConstructor: Bool
    // A one-shot mark from `lowerMatch` that the tuple literal about to be lowered is the match's
    // scrutinee, which no consumer keeps: `lowerTuple` takes it and clears it.
    | scrutineeTuple: Bool
    // Stage 0's `LoweredValueRuntimeRepresentation.Closure` request: a one-shot mark from a
    // constructor argument that is a closure literal owning its captures (or a choice between such
    // literals), placed on the reference-counted heap. An `if` hands it to each branch and takes it
    // from its condition; `lowerLambda` takes it, so the lambda's body never sees it.
    | runtimeClosureRequest: Bool
    // Stage 0's `_linearSpecializationAccumulators`: the loop parameters this body hands straight
    // to a single-parameter specialization candidate, deep-copied once at loop entry so the
    // specialization may rewrite them in place. The only route by which a call whose accumulator
    // is a bare name — rather than a provably fresh call result — reaches a specialization.
    | linearSpecializationAccumulators: List(Str)
    // Stage 0's `_routedSpecializationAccumulators`: the accumulators of the function being
    // lowered, and of the lambdas nested in it, whose call was routed to a reuse specialization.
    | routedSpecializationAccumulators: List(Str)
    // Stage 0's `LowerReuseSpecializedCall` context: the callees whose call the ordinary call path is
    // lowering as a routed call to their reuse specialization, the specialization's closure bound
    // under the callee's name.
    | routedSpecializationCallees: List(Str)
    // Stage 0's `_specializingReuseLabel`: the label of the function that bound the linear
    // parameter while a specialization was being generated, so the reset-safety scan runs over the
    // body that actually rebuilds the accumulator; and its `_fullyReusingLabels`/
    // `_resetSafeAccumulators` verdict, kept by the name of the callee whose specialization the
    // scan accepted, so a back edge threading that call's result knows the value is the same cell
    // rewritten in place.
    | specializingReuseLabel: Maybe(Str)
    | fullyReusingCallees: List(Str)
    | concreteRegistry: CoreConcreteRegistry
    | concreteDepth: Int
    | concreteDeclines: Int
    // Stage 0's `_abstractElementTmcDeclines`: tail-modulo-constructor cells declined because their
    // element type was unresolved, so a top-level function whose lowering moved it is an element
    // specialization candidate; and `_elementSpecializationTmcDeclines`, every declined cell, which a
    // copy must leave unmoved to be kept.
    | abstractElementTmcDeclines: Int
    | tmcDeclines: Int

type CoreLoweringState =
    | reversedInstructions: List(IrInstruction)
    | functions: List(IrFunction)
    | bindings: List(CoreBinding)
    | externalFunctions: List(ExternalFunctionAbi)
    | externalOpaqueTypes: List(Str)
    | nextTemp: Int
    | nextLocal: Int
    | nextLambdaId: Int
    | nextLabelId: Int
    | stringLiterals: List(IrStringLiteral)
    | typeSupply: TypeVariableSupply
    // Variable-keyed rather than an association list: a whole program's substitution reaches tens
    // of thousands of entries and every type resolved walks it once per variable.
    | substitution: MapTree(Int, SemanticType)
    | currentSpan: Maybe(TextSpan)
    // The identity of the innermost numbered node being lowered; None for an unnumbered tree.
    | currentNode: Maybe(Int)
    | consumerRequest: ConsumerRequest
    | runtimeTemps: MapTree(Int, RuntimeTempState)
    // The live arena/RC in-place-reuse tokens a match's dead scrutinee cell has published
    // for a same-name rebuild in its own arm, most recently produced first.
    | reuseTokens: List(CoreReuseToken)
    | valuePlacements: List((Int, Maybe(IrFunctionOrigin), SemanticType))
    // The representation the lowering itself decided for a control-flow join's result, by the
    // function it belongs to and the temp the join reloads. The `memory` report's post-hoc walk
    // over the emitted instructions cannot recover this: every arm of a match stores into one
    // result slot, and the unreachable no-match default stores into it last, so the reload's
    // representation read back off the slot is the default's rather than the arms'.
    | joinRepresentations: List((Maybe(IrFunctionOrigin), Int, Bool, Bool))
    // The closure calls that only borrow their argument, by the label of the function holding
    // the call and the call's result temp: lifetime placement gives them no compensating
    // duplicate of an owner that stays live past the call, which nothing would release.
    | borrowedCalls: List((Str, Int))
    | programState: CoreProgramState
    | functionState: CoreFunctionState
    | ownerState: CoreOwnerState
    | tcoState: CoreTcoState
    | reuseState: CoreReuseState

// Branch helpers below construct and match this private record; declare it before those uses
// so sequential module stitching can resolve its constructor name.
type LoweredCoreValue =
    | state: CoreLoweringState
    | temp: Int
    | semanticType: SemanticType
    | error: Maybe(CoreLoweringError)

let clearAdmissibilityAnswers (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with admissibilityAnswers = []))

let withTypeObservation (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with typeObservation = Some(CoreTypeObservation(goalSites = None, topLevelScope = [], operators = [], tableOperators = 0, deferredOperators = 0, relowerings = 0, tableCallResults = 0, tableFallbacks = 0, recordedCopyCalls = 0, inferredCopyCalls = 0, shadowAgree = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], shadowSites = [], singleLowering = false, shadowLambdaNode = None))))

let stateTypeObservation (state: CoreLoweringState) =
    (let group = state.programState
    in group.typeObservation)

let withNodeTypes (table: NodeTypeTable) (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with nodeTypes = Some(table), settledNodeVariables = settledInstantiationArguments(table)))

let stateNodeTypes (state: CoreLoweringState) =
    (let group = state.programState
    in group.nodeTypes)

let stateNodeInstantiation (state: CoreLoweringState) =
    (let group = state.programState
    in group.nodeInstantiation)

let withStateNodeInstantiation (instantiation: List((Int, SemanticType))) (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with nodeInstantiation = instantiation))

let stateSettledNodeVariables (state: CoreLoweringState) =
    (let group = state.programState
    in group.settledNodeVariables)

// Both observations leave the state as it is unless the lowering was asked to observe.
let observeTopLevelScope (state: CoreLoweringState) =
    (let group = state.programState
    in
        match group.typeObservation with
            | None -> state
            | Some(observation) -> state with programState = (group with typeObservation = Some((observation with topLevelScope = Ashes.Internal.deepCopy(state.bindings)))))

let observeOperatorType (traitName: Str) (operandType: SemanticType) (state: CoreLoweringState) =
    (let group = state.programState
    in
        match group.typeObservation with
            | None -> state
            | Some(observation) -> state with programState = (group with typeObservation = Some((observation with operators = (Ashes.Internal.deepCopy(state.currentSpan), traitName, Ashes.Internal.deepCopy(operandType)) :: observation.operators))))

// Counts one operator site answered from the recorded node types (0), one deferred or pended
// operator goal (1), one function body lowered again (2), or one call whose expected result type
// came from the recorded node types (3), or one such call lowered again without it after it failed
// (4). Concrete source-copy calls count a recorded proof (5) or speculative inference (6).
let observeLoweringEvent (event: Int) (state: CoreLoweringState) =
    (let group = state.programState
    in
        match group.typeObservation with
            | None -> state
            | Some(observation) ->
                let counted =
                    match event with
                        | 0 -> observation with tableOperators = observation.tableOperators + 1
                        | 1 -> observation with deferredOperators = observation.deferredOperators + 1
                        | 2 -> observation with relowerings = observation.relowerings + 1
                        | 3 -> observation with tableCallResults = observation.tableCallResults + 1
                        | 5 -> observation with recordedCopyCalls = observation.recordedCopyCalls + 1
                        | 6 -> observation with inferredCopyCalls = observation.inferredCopyCalls + 1
                        | _ -> observation with tableFallbacks = observation.tableFallbacks + 1
                in state with programState = (group with typeObservation = Some(counted)))

// A frame lowered over a copy of the state hands back what it observed.
let carryTypeObservation (bodyState: CoreLoweringState) (state: CoreLoweringState) =
    match stateTypeObservation(bodyState) with
        | None -> state
        | Some(observation) ->
            let group = state.programState
            in state with programState = (group with typeObservation = Some(observation))

let stateConstructorLayouts (state: CoreLoweringState) =
    (let group = state.programState
    in group.constructorLayouts)

// A type declaration's layouts follow every registered layout, so the lookup index is extended
// rather than rebuilt.
let withAppendedConstructorLayouts layouts (state: CoreLoweringState) =
    (let group = state.programState
    in clearAdmissibilityAnswers((state with programState = (group with constructorLayouts = append(group.constructorLayouts)(layouts), constructorIndex = extendConstructorLookupIndex(layouts)(group.constructorIndex)))))

let stateConstructorsOfType (name: Str) (state: CoreLoweringState) =
    (let group = state.programState
    in lookupResultConstructors(name)(group.constructorIndex))

let stateStableConstructorFields name (state: CoreLoweringState) =
    (let group = state.programState
    in lookupStableConstructorFields(name)(group.constructorIndex))

let stateConstructorsOfField (name: Str) (state: CoreLoweringState) =
    (let group = state.programState
    in lookupFieldConstructors(name)(group.constructorIndex))

let stateCoverageTypes (state: CoreLoweringState) =
    (let group = state.programState
    in group.coverageTypes)

let withStateCoverageTypes value (state: CoreLoweringState) =
    (let group = state.programState
    in clearAdmissibilityAnswers((state with programState = (group with coverageTypes = value, coverageEnvironmentVersion = group.coverageEnvironmentVersion + 1, heapQueryContext = emptyCoreHeapQueryContext(value)))))

let stateHeapQueryContext (state: CoreLoweringState) =
    (let group = state.programState
    in group.heapQueryContext)

let withStateHeapQueryContext context (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with heapQueryContext = context))

let coreHeapQueryFacts semanticType (state: CoreLoweringState) =
    (let context = stateHeapQueryContext(state)
    in
        match typeGraphLookupNode(semanticType)(context.graphIndex) with
            | None -> emptyCoreHeapQueryFacts
            | Some(node) ->
                match Ashes.Collection.Map.getInt(node.nodeId)(context.facts) with
                    | Some(facts) -> facts
                    | None -> emptyCoreHeapQueryFacts)

let queryCoreCopyPlan (semanticType: SemanticType) (state: CoreLoweringState) =
    (let context = stateHeapQueryContext(state)
    in
        match typeGraphNode(semanticType)(context.graphIndex) with
            | (node, graphIndex) ->
                let previous =
                    match Ashes.Collection.Map.getInt(node.nodeId)(context.facts) with
                        | Some(facts) -> facts
                        | None -> emptyCoreHeapQueryFacts
                in
                    match previous.copyPlan with
                        | Some(answer) -> (answer, withStateHeapQueryContext((context with graphIndex = graphIndex))(state))
                        | None ->
                            match classifyHeapCopyPlanIndexed(semanticType)(graphIndex) with
                                | (answer, discovered) -> (answer, withStateHeapQueryContext((context with graphIndex = discovered, facts = Ashes.Collection.Map.setInt(node.nodeId)((previous with copyPlan = Some(answer)))(context.facts)))(state)))

let queryCoreAdmissibility (semanticType: SemanticType) (state: CoreLoweringState) =
    (let context = stateHeapQueryContext(state)
    in
        match typeGraphNode(semanticType)(context.graphIndex) with
            | (node, graphIndex) ->
                let previous =
                    match Ashes.Collection.Map.getInt(node.nodeId)(context.facts) with
                        | Some(facts) -> facts
                        | None -> emptyCoreHeapQueryFacts
                in
                    match previous.admissibility with
                        | Some(answer) -> (answer, withStateHeapQueryContext((context with graphIndex = graphIndex))(state))
                        | None ->
                            match classifyHeapAdmissibilityIndexed(semanticType)(graphIndex) with
                                | (answer, discovered) -> (answer, withStateHeapQueryContext((context with graphIndex = discovered, facts = Ashes.Collection.Map.setInt(node.nodeId)((previous with admissibility = Some(answer)))(context.facts)))(state)))

let queryCoreShallowCopy (semanticType: SemanticType) (state: CoreLoweringState) =
    (let context = stateHeapQueryContext(state)
    in
        match typeGraphNode(semanticType)(context.graphIndex) with
            | (node, graphIndex) ->
                let previous =
                    match Ashes.Collection.Map.getInt(node.nodeId)(context.facts) with
                        | Some(facts) -> facts
                        | None -> emptyCoreHeapQueryFacts
                in
                    match previous.shallowCopy with
                        | Some(answer) -> (answer, withStateHeapQueryContext((context with graphIndex = graphIndex))(state))
                        | None ->
                            match classifyHeapShallowCopyIndexed(semanticType)(graphIndex) with
                                | (answer, discovered) -> (answer, withStateHeapQueryContext((context with graphIndex = discovered, facts = Ashes.Collection.Map.setInt(node.nodeId)((previous with shallowCopy = Some(answer)))(context.facts)))(state)))

let queryCoreChildren (semanticType: SemanticType) (state: CoreLoweringState) =
    (let context = stateHeapQueryContext(state)
    in
        match typeGraphNode(semanticType)(context.graphIndex) with
            | (node, graphIndex) ->
                let previous =
                    match Ashes.Collection.Map.getInt(node.nodeId)(context.facts) with
                        | Some(facts) -> facts
                        | None -> emptyCoreHeapQueryFacts
                in
                    match previous.children with
                        | Some(answer) -> (answer, withStateHeapQueryContext((context with graphIndex = graphIndex))(state))
                        | None ->
                            match classifyHeapChildrenIndexed(semanticType)(graphIndex) with
                                | (answer, discovered) -> (answer, withStateHeapQueryContext((context with graphIndex = discovered, facts = Ashes.Collection.Map.setInt(node.nodeId)((previous with children = Some(answer)))(context.facts)))(state)))

let queryCoreChildTypes (semanticType: SemanticType) (state: CoreLoweringState) =
    (let context = stateHeapQueryContext(state)
    in
        match typeGraphNode(semanticType)(context.graphIndex) with
            | (node, graphIndex) ->
                let previous =
                    match Ashes.Collection.Map.getInt(node.nodeId)(context.facts) with
                        | Some(facts) -> facts
                        | None -> emptyCoreHeapQueryFacts
                in
                    match previous.childTypes with
                        | Some(answer) -> (answer, withStateHeapQueryContext((context with graphIndex = graphIndex))(state))
                        | None ->
                            match classifyHeapChildTypesIndexed(semanticType)(graphIndex) with
                                | (answer, discovered) -> (answer, withStateHeapQueryContext((context with graphIndex = discovered, facts = Ashes.Collection.Map.setInt(node.nodeId)((previous with childTypes = Some(answer)))(context.facts)))(state)))

let stateDropperTypes (state: CoreLoweringState) =
    (let group = state.programState
    in group.dropperTypes)

let withStateDropperTypes value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with dropperTypes = value))

// A synthesis returns graph nodes and completed views for the same prepared environment.
// Carry both so subsequent requests reuse exact types and their context-free facts.
let withStateDropperContext value (state: CoreLoweringState) =
    (let types = stateDropperTypes(state)
    in
        withStateDropperTypes(withDropperQueryContext(value)(types))(state))

let stateBuiltinLayouts (state: CoreLoweringState) =
    (let group = state.programState
    in group.builtinLayouts)

let withStateBuiltinLayouts value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with builtinLayouts = value))

let stateExternalLayouts (state: CoreLoweringState) =
    (let group = state.programState
    in group.externalLayouts)

let withStateExternalLayouts value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with externalLayouts = value))

let stateDeclaredTypeNames (state: CoreLoweringState) =
    (let group = state.programState
    in group.declaredTypeNames)

let withStateDeclaredTypeNames value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with declaredTypeNames = value))

let stateTypeAliases (state: CoreLoweringState) =
    (let group = state.programState
    in group.typeAliases)

let withStateTypeAliases value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with typeAliases = value))

let stateCapabilityLayouts (state: CoreLoweringState) =
    (let group = state.programState
    in group.capabilityLayouts)

let withStateCapabilityLayouts value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with capabilityLayouts = value))

let stateStaticProviders (state: CoreLoweringState) =
    (let group = state.programState
    in group.staticProviders)

let withStateStaticProviders value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with staticProviders = value))

let stateCapabilityGlobalCount (state: CoreLoweringState) =
    (let group = state.programState
    in group.capabilityGlobalCount)

let withStateCapabilityGlobalCount value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with capabilityGlobalCount = value))

let stateCapabilityOperationSchemes (state: CoreLoweringState) =
    (let group = state.programState
    in group.capabilityOperationSchemes)

let withStateCapabilityOperationSchemes value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with capabilityOperationSchemes = value))

let stateFailureSite (state: CoreLoweringState) =
    (let group = state.programState
    in group.failureSite)

let withStateFailureSite value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with failureSite = value))

let stateHandledCapabilities (state: CoreLoweringState) =
    (let group = state.programState
    in group.handledCapabilities)

let withStateHandledCapabilities value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with handledCapabilities = value))

let stateSourceContext (state: CoreLoweringState) =
    (let group = state.programState
    in group.sourceContext)

let withStateSourceContext value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with sourceContext = value))

let withStateSourceRegions (regionOfName: MapTree(Str, Int)) (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with sourceRegionOfName = regionOfName))

let stateTopLevelNames (state: CoreLoweringState) =
    (let group = state.programState
    in group.topLevelNames)

let withStateTopLevelNames value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with topLevelNames = value))

let stateTraitEnvironment (state: CoreLoweringState) =
    (let group = state.programState
    in group.traitEnvironment)

let withStateTraitEnvironment value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with traitEnvironment = value))

let stateTraitMethodLabels (state: CoreLoweringState) =
    (let group = state.programState
    in group.traitMethodLabels)

let withStateTraitMethodLabels value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with traitMethodLabels = value))

let stateMoveFunctionTable (state: CoreLoweringState) =
    (let group = state.programState
    in group.moveFunctionTable)

let withStateMoveFunctionTable value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with moveFunctionTable = value))

let stateMoveCallSites (state: CoreLoweringState) =
    (let group = state.programState
    in group.moveCallSites)

let withStateMoveCallSites value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with moveCallSites = value))

let withStateLiveHandlerFunctions value (state: CoreLoweringState) =
    match value with
        | (functions, entryLive, anyLive) ->
            let group = state.programState
            in state with programState = (group with liveHandlerFunctions = functions, entryRunsUnderLiveHandlerPost = entryLive, anyRunsUnderLiveHandlerPost = anyLive)

let stateLiveHandlerFunctions (state: CoreLoweringState) =
    (let group = state.programState
    in group.liveHandlerFunctions)

let stateEntryRunsUnderLiveHandlerPost (state: CoreLoweringState) =
    (let group = state.programState
    in group.entryRunsUnderLiveHandlerPost)

// Stage 0's `ProgramMayRunUnderLiveHandlerPost`: a capability in the program and some function, or
// the entry, that may run while a one-shot post is pending. The general reference-counting
// contract is one calling convention for the whole program, so such a program keeps it off.
let programMayRunUnderLiveHandlerPost (state: CoreLoweringState) =
    (let group = state.programState
    in group.capabilityGlobalCount > 0 && group.anyRunsUnderLiveHandlerPost)

let stateMoveNullaryConstructors (state: CoreLoweringState) =
    (let group = state.programState
    in group.moveNullaryConstructors)

let withStateMoveNullaryConstructors value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with moveNullaryConstructors = value))

let stateTupleBinderParameters (state: CoreLoweringState) =
    (let group = state.programState
    in group.tupleBinderParameters)

let withStateTupleBinderParameters value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with tupleBinderParameters = value))

let statePatternOwnerSites (state: CoreLoweringState) =
    (let group = state.programState
    in group.patternOwnerSites)

let withStatePatternOwnerSites value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with patternOwnerSites = value))

let stateCellMarkerSlots (state: CoreLoweringState) =
    (let group = state.programState
    in group.cellMarkerSlots)

let withStateCellMarkerSlots value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with cellMarkerSlots = value))

let stateReachSummaries (state: CoreLoweringState) =
    (let group = state.programState
    in group.reachSummaries)

let withStateReachSummaries value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with reachSummaries = value))

let stateReachRegistry (state: CoreLoweringState) =
    (let group = state.programState
    in group.reachRegistry)

let withStateReachRegistry value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with reachRegistry = value))

let stateReachTable (state: CoreLoweringState) =
    (let group = state.programState
    in group.reachTable)

let withStateReachTable value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with reachTable = value))

let stateReachByIdentity (state: CoreLoweringState) =
    (let group = state.programState
    in group.reachByIdentity)

let withStateReachByIdentity value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with reachByIdentity = value))

let stateReachByName (state: CoreLoweringState) =
    (let group = state.programState
    in group.reachByName)

let withStateReachByName value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with reachByName = value))

let withStateCallReachByKey value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with callReachByKey = value))

let stateProgramParameterOwnership (state: CoreLoweringState) =
    (let group = state.programState
    in group.programParameterOwnership)

let withStateProgramParameterOwnership value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with programParameterOwnership = indexProgramParameterOwnership(value)))

let stateLetLambdas (state: CoreLoweringState) =
    (let group = state.programState
    in group.letLambdas)

let withStateLetLambdas value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with letLambdas = value))

let stateLetLambdasByName (state: CoreLoweringState) =
    (let group = state.programState
    in group.letLambdasByName)

let withStateLetLambdasByName value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with letLambdasByName = value))

let stateLetLambdaLabels (state: CoreLoweringState) =
    (let group = state.programState
    in group.letLambdaLabels)

let withStateLetLambdaLabels value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with letLambdaLabels = value))

let stateLetLambdaIdentities (state: CoreLoweringState) =
    (let group = state.programState
    in group.letLambdaIdentities)

let withStateLetLambdaIdentities value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with letLambdaIdentities = value))

let stateTopLevelFunctionRefIndex (state: CoreLoweringState) =
    (let group = state.programState
    in group.topLevelFunctionRefByName)

let recursive namesInferenceBinding (name: Str) (bindings: List((Str, TypeScheme))) =
    match bindings with
        | [] -> false
        | (candidate, _scheme) :: rest ->
            if candidate == name
            then true
            else namesInferenceBinding(name)(rest)

let recursive storedInferenceBindings (bindings: List((Str, TypeScheme, Bool))) (tail: List((Str, TypeScheme))) =
    match bindings with
        | [] -> tail
        | (name, scheme, _hasFreeVariables) :: rest -> (name, scheme) :: storedInferenceBindings(rest)(tail)

// The global references as inference bindings over the coverage bindings, extended by one cell per
// registration and rebuilt only when the coverage environment was replaced since the last one.
let registeredInferenceEnvironment (name: Str) (scheme: TypeScheme) (group: CoreProgramState) =
    (let coverage = group.coverageTypes
    in
        match group.topLevelInferenceEnvironment with
            | Some((version, bindings)) ->
                if version == group.coverageEnvironmentVersion
                then Some((version, (name, scheme) :: bindings))
                else Some((group.coverageEnvironmentVersion, (name, scheme) :: storedInferenceBindings(group.topLevelInferenceBindings)(coverage.bindings)))
            | None -> Some((group.coverageEnvironmentVersion, (name, scheme) :: storedInferenceBindings(group.topLevelInferenceBindings)(coverage.bindings))))

let withRegisteredTopLevelFunctionRef name label scheme (state: CoreLoweringState) =
    (let group = state.programState
    in
        let open =
            if schemeIsClosed(scheme)
            then group.topLevelInferenceOpen
            else (name, scheme) :: group.topLevelInferenceOpen
        in state with programState = (group with topLevelFunctionRefs = (name, label, scheme) :: group.topLevelFunctionRefs, topLevelInferenceBindings = (name, scheme, schemeIsClosed(scheme) == false) :: group.topLevelInferenceBindings, topLevelInferenceOpen = open, topLevelInferenceOpenShadowed = group.topLevelInferenceOpenShadowed || namesInferenceBinding(name)(group.topLevelInferenceOpen), topLevelInferenceEnvironment = registeredInferenceEnvironment(name)(scheme)(group), topLevelFunctionRefByName = registerFunctionReference(name)(label)(scheme)(group.topLevelFunctionRefByName)))

let stateInlinableHelpers (state: CoreLoweringState) =
    (let group = state.programState
    in group.inlinableHelpers)

let withStateInlinableHelpers value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with inlinableHelpers = value))

let stateSpecializationCandidates (state: CoreLoweringState) =
    (let group = state.programState
    in group.specializationCandidates)

let stateSpecializationCandidateIndex (state: CoreLoweringState) =
    (let group = state.programState
    in group.specializationCandidateByName)

let withStateSpecializationCandidates value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with specializationCandidates = value, specializationCandidateByName = specializationCandidateIndex(value)))

let stateGeneralRcOwnedResultLabels (state: CoreLoweringState) =
    (let group = state.programState
    in group.generalRcOwnedResultLabels)

let withStateGeneralRcOwnedResultLabels value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with generalRcOwnedResultLabels = value))

let stateTupleResultPartLabels (state: CoreLoweringState) =
    (let group = state.programState
    in group.tupleResultPartLabels)

let withStateTupleResultPartLabels value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with tupleResultPartLabels = value))

let stateHeapLayoutFactsByType (state: CoreLoweringState) =
    (let group = state.programState
    in group.heapLayoutFactsByType)

let withStateHeapLayoutFactsByType value (state: CoreLoweringState) =
    (let group = state.programState
    in clearAdmissibilityAnswers((state with programState = (group with heapLayoutFactsByType = value))))

let stateNormalizableRecordTypes (state: CoreLoweringState) =
    (let group = state.programState
    in group.normalizableRecordTypes)

let withStateNormalizableRecordTypes value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with normalizableRecordTypes = value))

let stateBodyRuntimeManagedByLabel (state: CoreLoweringState) =
    (let group = state.programState
    in group.bodyRuntimeManagedByLabel)

let withStateBodyRuntimeManagedByLabel value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with bodyRuntimeManagedByLabel = value))

let stateTrivialParameterFieldAccessorLabels (state: CoreLoweringState) =
    (let group = state.programState
    in group.trivialParameterFieldAccessorLabels)

let withStateTrivialParameterFieldAccessorLabels value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with trivialParameterFieldAccessorLabels = value))

let stateDropperLabels (state: CoreLoweringState) =
    (let group = state.programState
    in group.dropperLabels)

let withStateDropperLabels value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with dropperLabels = value))

let withStateDropperResult cache queryContext state =
    state
    |> withStateDropperContext(queryContext)
    |> withStateDropperLabels(cache)

let stateClosureDropperLabels (state: CoreLoweringState) =
    (let group = state.programState
    in group.closureDropperLabels)

let withStateClosureDropperLabels value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with closureDropperLabels = value))

let stateResultRcEligibility (state: CoreLoweringState) =
    (let group = state.programState
    in group.resultRcEligibility)

let withStateResultRcEligibility value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with resultRcEligibility = value))

let stateGeneralRcTypeFacts (state: CoreLoweringState) =
    (let group = state.programState
    in group.generalRcTypeFacts)

let withStateGeneralRcTypeFacts value (state: CoreLoweringState) =
    (let group = state.programState
    in clearAdmissibilityAnswers((state with programState = (group with generalRcTypeFacts = value))))

let withStateContractRecordFacts value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with contractRecordFacts = value))

// The recorded answer for an argument-free named type, when the program's start classified it.
let recordedContractRecord (semanticType: SemanticType) (state: CoreLoweringState) =
    match semanticType with
        | SemNamed(_symbolId, name, []) ->
            let group = state.programState
            in Ashes.Collection.Map.getStr(name)(group.contractRecordFacts)
        | _ -> None

// The drop synthesis's view of the program's types, answering from this state whether a record
// joins the ownership contract.
// A reuse specialization keeps its values in the persistent region and takes no part in the
// contract.
let dropperTypesOf (state: CoreLoweringState) =
    (let group = state.programState
    in
        let reuse = state.reuseState
        in
            match reuse.specializationFreshInputs with
                | None -> group.dropperTypes with contractRecords = group.contractRecordFacts
                | Some(_inputs) -> group.dropperTypes)

let stateProvenTraitGoals (state: CoreLoweringState) =
    (let group = state.programState
    in group.provenTraitGoals)

let withStateProvenTraitGoals value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with provenTraitGoals = value))

let stateProvenSiblingCallResults (state: CoreLoweringState) =
    (let group = state.programState
    in group.provenSiblingCallResults)

let withStateProvenSiblingCallResults value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with provenSiblingCallResults = value))

let stateCurrentItem (state: CoreLoweringState) =
    (let group = state.functionState
    in group.currentItem)

let withStateCurrentItem value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with currentItem = value))

// Spans lowered from here on resolve in the source region the top-level binding `name` was
// declared in; a name no region records keeps the current region.
let withStateSourceRegionOf (name: Str) (state: CoreLoweringState) =
    (let group = state.programState
    in
        match Ashes.Collection.Map.getStr(name)(group.sourceRegionOfName) with
            | Some(region) -> withStateCurrentItem(region)(state)
            | None -> state)

let statePendingStackClosure (state: CoreLoweringState) =
    (let group = state.functionState
    in group.pendingStackClosure)

let withStatePendingStackClosure value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with pendingStackClosure = value))

let stateCoroutineBody (state: CoreLoweringState) =
    (let group = state.functionState
    in group.coroutineBody)

let withStateCoroutineBody value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with coroutineBody = value))

let stateHelperFactoryType (state: CoreLoweringState) =
    (let group = state.functionState
    in group.helperFactoryType)

let withHelperFactoryType value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with helperFactoryType = value))

let statePendingCoroutineBody (state: CoreLoweringState) =
    (let group = state.functionState
    in group.pendingCoroutineBody)

let stateCompletedTaskFrameDropper (state: CoreLoweringState) =
    (let group = state.functionState
    in group.completedTaskFrameDropper)

let withCompletedTaskFrameDropper value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with completedTaskFrameDropper = value))

let withStatePendingCoroutineBody value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with pendingCoroutineBody = value))

// A lambda's body is an `async` body exactly when the lambda was entered for one; a lambda nested
// in an `async` body is an ordinary function, where an `await` runs its task to completion.
let enterPendingCoroutineBody (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with coroutineBody = group.pendingCoroutineBody, pendingCoroutineBody = false))

let stateRuntimeAdtRequested (state: CoreLoweringState) =
    (let group = state.functionState
    in group.runtimeAdtRequested)

let withStateRuntimeAdtRequested value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with runtimeAdtRequested = value))

let statePendingOperatorDefaults (state: CoreLoweringState) =
    (let group = state.functionState
    in group.pendingOperatorDefaults)

let withStatePendingOperatorDefaults value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with pendingOperatorDefaults = value, admissibilityAnswers = []))

let stateEnclosingBindings (state: CoreLoweringState) =
    (let group = state.functionState
    in group.enclosingBindings)

let withStateEnclosingBindings (value: List(CoreBinding)) (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with enclosingBindings = value))

let stateLetBoundFunctionLabels (state: CoreLoweringState) =
    (let group = state.functionState
    in group.letBoundFunctionLabels)

let withStateLetBoundFunctionLabels (value: List(Str)) (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with letBoundFunctionLabels = value))

let stateInlineTypeVariables (state: CoreLoweringState) =
    (let group = state.functionState
    in group.inlineTypeVariables)

let withStateInlineTypeVariables (value: List((Str, SemanticType))) (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with inlineTypeVariables = value))

let stateSealedOperatorDefaults (state: CoreLoweringState) =
    (let group = state.functionState
    in group.sealedOperatorDefaults)

let withStateSealedOperatorDefaults value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with sealedOperatorDefaults = value, admissibilityAnswers = []))

let statePendingSourceFunction (state: CoreLoweringState) =
    (let group = state.functionState
    in group.pendingSourceFunction)

let withStatePendingSourceFunction value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with pendingSourceFunction = value))

let stateActiveFunctionOrigin (state: CoreLoweringState) =
    (let group = state.functionState
    in group.activeFunctionOrigin)

let withStateActiveFunctionOrigin value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with activeFunctionOrigin = value))

let stateUnderLiveHandlerPost (state: CoreLoweringState) =
    (let group = state.functionState
    in group.underLiveHandlerPost)

let withStateUnderLiveHandlerPost value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with underLiveHandlerPost = value))

let stateHandlerArmOwnResult (state: CoreLoweringState) =
    (let group = state.functionState
    in group.handlerArmOwnResult)

let withStateHandlerArmOwnResult value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with handlerArmOwnResult = value))

let stateCoroutineFunctions (state: CoreLoweringState) =
    (let group = state.programState
    in group.coroutineFunctions)

let withStateCoroutineFunctions value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with coroutineFunctions = value))

let stateMayRunInsideCoroutine (state: CoreLoweringState) =
    (let group = state.functionState
    in group.mayRunInsideCoroutine)

let withStateMayRunInsideCoroutine value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with mayRunInsideCoroutine = value))

// Stage 0's `AllowsAsyncIndependentRcPlacement` (`!MayExecuteInsideCoroutine`) and its
// `_inCoroutineBody`: a coroutine body keeps every value in the arena, its call results copied out
// at their call boundary and its loop's arena reset at the loop head after a resume, so nothing it
// holds across a suspension is reference-counted; so does every function that may run inside one.
let asyncIndependentRcPlacementAllowed (state: CoreLoweringState) = stateCoroutineBody(state) == false && stateMayRunInsideCoroutine(state) == false

// Stage 0's `AllowsAsyncIndependentRcPlacement && AllowsOrdinaryRcPlacement`, the pair every stage-0
// placement reads together: the function being lowered is no coroutine body and cannot run while a
// one-shot post is pending, so its values may be placed on the reference-counted heap.
let ordinaryRcPlacementAllowed (state: CoreLoweringState) = stateUnderLiveHandlerPost(state) == false && asyncIndependentRcPlacementAllowed(state)

// Stage 0's `AllowsAsyncIndependentRcPlacement && (AllowsOrdinaryRcPlacement ||
// _loweringHandlerArmOwnResult)`, the gate of an escaping result's placement.
let escapingRcPlacementAllowed (state: CoreLoweringState) = asyncIndependentRcPlacementAllowed(state) && (stateUnderLiveHandlerPost(state) == false || stateHandlerArmOwnResult(state))

// Stage 0's `GeneralRcEnabled` as far as the live-handler and coroutine contexts decide it: the
// function is no coroutine body (its `!_inCoroutineBody` and `AllowsAsyncIndependentRcPlacement`
// terms, through `ordinaryRcPlacementAllowed`), may place ordinary values on the reference-counted
// heap, and no function of the program may run while a one-shot post is pending.
let generalRcEnabled (state: CoreLoweringState) = ordinaryRcPlacementAllowed(state) && programMayRunUnderLiveHandlerPost(state) == false

let statePendingClosureNormalizers (state: CoreLoweringState) =
    (let group = state.functionState
    in group.pendingClosureNormalizers)

let withStatePendingClosureNormalizers value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with pendingClosureNormalizers = value))

let stateResourceStates (state: CoreLoweringState) =
    (let group = state.functionState
    in group.resourceStates)

let withStateResourceStates value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with resourceStates = value))

let stateRecursiveDeclarationSpan (state: CoreLoweringState) =
    (let group = state.functionState
    in group.recursiveDeclarationSpan)

let withStateRecursiveDeclarationSpan value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with recursiveDeclarationSpan = value))

let stateRecursiveMemberStarts (state: CoreLoweringState) =
    (let group = state.functionState
    in group.recursiveMemberStarts)

let withStateRecursiveMemberStarts value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with recursiveMemberStarts = value))

let stateActiveTypeParameters (state: CoreLoweringState) =
    (let group = state.functionState
    in group.activeTypeParameters)

let withStateActiveTypeParameters value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with activeTypeParameters = value))

let stateActiveTraitMethods (state: CoreLoweringState) =
    (let group = state.functionState
    in group.activeTraitMethods)

let withStateActiveTraitMethods value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with activeTraitMethods = value))

let statePendingTraitGoals (state: CoreLoweringState) =
    (let group = state.functionState
    in group.pendingTraitGoals)

let withStatePendingTraitGoals value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with pendingTraitGoals = value))

let statePendingEqualityGoals (state: CoreLoweringState) =
    (let group = state.functionState
    in group.pendingEqualityGoals)

let withStatePendingEqualityGoals value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with pendingEqualityGoals = value))

let statePendingSiblingCallResults (state: CoreLoweringState) =
    (let group = state.functionState
    in group.pendingSiblingCallResults)

let withStatePendingSiblingCallResults value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with pendingSiblingCallResults = value))

let stateTraitGoalsPinned (state: CoreLoweringState) =
    (let group = state.functionState
    in group.traitGoalsPinned)

let withStateTraitGoalsPinned value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with traitGoalsPinned = value))

let stateDeferredLabelNext (state: CoreLoweringState) =
    (let group = state.functionState
    in group.deferredLabelNext)

let withStateDeferredLabelNext value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with deferredLabelNext = value))

let stateDeferredLabelGroups (state: CoreLoweringState) =
    (let group = state.functionState
    in group.deferredLabelGroups)

let withStateDeferredLabelGroups value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with deferredLabelGroups = value))

let stateNextStringId (state: CoreLoweringState) =
    (let group = state.functionState
    in group.nextStringId)

let withStateNextStringId value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with nextStringId = value))

let stateBackEdgeDummyTemps (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.backEdgeDummyTemps)

let withStateBackEdgeDummyTemps value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with backEdgeDummyTemps = value))

let stateReuseResultTemps (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.reuseResultTemps)

let withStateReuseResultTemps value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with reuseResultTemps = value))

let markReuseResultTemp (temp: Int) (state: CoreLoweringState) = withStateReuseResultTemps(temp :: stateReuseResultTemps(state))(state)

let isReuseResultTemp (temp: Int) (state: CoreLoweringState) =
    state
    |> stateReuseResultTemps
    |> containsInt(temp)

let statePatternOwnerCopyTemps (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.patternOwnerCopyTemps)

let withStatePatternOwnerCopyTemps value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with patternOwnerCopyTemps = value))

let stateReleasedPatternOwnerDuplicates (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.releasedPatternOwnerDuplicates)

let withStateReleasedPatternOwnerDuplicates value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with releasedPatternOwnerDuplicates = value))

let statePatternOwnerResultTemps (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.patternOwnerResultTemps)

let withStatePatternOwnerResultTemps value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with patternOwnerResultTemps = value))

let stateRuntimeOwners (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.runtimeOwners)

let withStateRuntimeOwners value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with runtimeOwners = value))

let stateUnresolvedCallResults (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.unresolvedCallResults)

let withStateUnresolvedCallResults value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with unresolvedCallResults = value))

let stateResolvingCallsAgain (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.resolvingCallsAgain)

let withStateResolvingCallsAgain (value: Bool) (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with resolvingCallsAgain = value))

let stateRuntimeOwnerAliases (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.runtimeOwnerAliases)

let withStateRuntimeOwnerAliases value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with runtimeOwnerAliases = value))

let stateBorrowedOwners (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.borrowedOwners)

let withStateBorrowedOwners value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with borrowedOwners = value))

let statePendingBorrowedOwners (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.pendingBorrowedOwners)

let withStatePendingBorrowedOwners value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with pendingBorrowedOwners = value))

let statePendingRuntimeArgumentFlags (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.pendingRuntimeArgumentFlags)

let withStatePendingRuntimeArgumentFlags value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with pendingRuntimeArgumentFlags = value))

let statePendingAccessorResultRetains (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.pendingAccessorResultRetains)

let withStatePendingAccessorResultRetains value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with pendingAccessorResultRetains = value))

let stateGenericDeepCopiedListTemps (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.genericDeepCopiedListTemps)

let withStateGenericDeepCopiedListTemps value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with genericDeepCopiedListTemps = value))

let recordOwnedReferenceJoin (temp: Int) (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with ownedReferenceJoins = (stateActiveFunctionOrigin(state), temp) :: group.ownedReferenceJoins))

let recursive containsOwnedReferenceJoin (origin: Maybe(IrFunctionOrigin)) (temp: Int) (joins: List((Maybe(IrFunctionOrigin), Int))) =
    match joins with
        | [] -> false
        | (joinOrigin, joinTemp) :: rest -> joinTemp == temp && joinOrigin == origin || containsOwnedReferenceJoin(origin)(temp)(rest)

let recordRetainedArmJoin (temp: Int) (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with retainedArmJoins = (stateActiveFunctionOrigin(state), temp) :: group.retainedArmJoins))

let isRetainedArmJoin (temp: Int) (state: CoreLoweringState) =
    (let group = state.ownerState
    in
        containsOwnedReferenceJoin(stateActiveFunctionOrigin(state))(temp)(group.retainedArmJoins))

// Whether `temp` is a join of the function being lowered that holds a reference of its own.
let isOwnedReferenceJoin (temp: Int) (state: CoreLoweringState) =
    (let group = state.ownerState
    in
        containsOwnedReferenceJoin(stateActiveFunctionOrigin(state))(temp)(group.ownedReferenceJoins))

let stateRetiredLocals (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.retiredLocals)

let withStateRetiredLocals value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with retiredLocals = value))

let stateOwnerReleasePlans (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.ownerReleasePlans)

let withStateOwnerReleasePlans value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with ownerReleasePlans = value))

let stateGeneralRcOwnedSlots (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.generalRcOwnedSlots)

let withStateGeneralRcOwnedSlots value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with generalRcOwnedSlots = value))

let stateGeneralRcPredecessorSlots (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.generalRcPredecessorSlots)

let withStateGeneralRcPredecessorSlots value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with generalRcPredecessorSlots = value))

let stateGeneralRcSlotBinders (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.generalRcSlotBinders)

let withStateGeneralRcSlotBinders value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with generalRcSlotBinders = value))

let stateGeneralRcPredecessorOwners (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.generalRcPredecessorOwners)

let withStateGeneralRcPredecessorOwners value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with generalRcPredecessorOwners = value))

let stateGeneralRcFreshSlots (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.generalRcFreshSlots)

let withStateGeneralRcFreshSlots value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with generalRcFreshSlots = value))

let stateGeneralRcPartDuplicateSlots (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.generalRcPartDuplicateSlots)

let withStateGeneralRcPartDuplicateSlots value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with generalRcPartDuplicateSlots = value))

let stateGeneralRcAliasOwnerSlots (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.generalRcAliasOwnerSlots)

let withStateGeneralRcAliasOwnerSlots value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with generalRcAliasOwnerSlots = value))

let stateTakenOverConsumers (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.takenOverConsumers)

let withStateTakenOverConsumers value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with takenOverConsumers = value))

let stateTakenOverRecordNames (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.takenOverRecordNames)

let withStateTakenOverRecordNames value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with takenOverRecordNames = value))

let stateEscapingCaptureNames (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.escapingCaptureNames)

let withStateEscapingCaptureNames value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with escapingCaptureNames = value))

let stateTakenOverSlotSources (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.takenOverSlotSources)

let withStateTakenOverSlotSources value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with takenOverSlotSources = value))

let stateSpineFreedSpans (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.spineFreedSpans)

let withStateSpineFreedSpans value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with spineFreedSpans = value))

let stateOwnFieldRetainedParameters (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.ownFieldRetainedParameters)

let withStateOwnFieldRetainedParameters value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with ownFieldRetainedParameters = value))

let stateOwnFieldPendingParameters (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.ownFieldPendingParameters)

let withStateOwnFieldPendingParameters value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with ownFieldPendingParameters = value))

let stateLoopResultPartDuplicates (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.loopResultPartDuplicates)

let withStateLoopResultPartDuplicates value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with loopResultPartDuplicates = value))

let stateGeneralRcHandedOverPartsSlots (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.generalRcHandedOverPartsSlots)

let withStateGeneralRcHandedOverPartsSlots value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with generalRcHandedOverPartsSlots = value))

let withGeneralRcFinishedResult (temp: Int) (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with generalRcFinishedResult = Some((stateActiveFunctionOrigin(state), temp))))

let withClosedProducerChain (temp: Int) (transferTemp: Int) (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with closedProducerChain = Some((stateActiveFunctionOrigin(state), temp, transferTemp))))

// Whether `temp` is the spine the function being lowered closed its tail-modulo-cons chain into.
let isClosedProducerChain (temp: Int) (state: CoreLoweringState) =
    (let group = state.ownerState
    in
        match group.closedProducerChain with
            | Some((origin, closed, _transferTemp)) -> closed == temp && origin == stateActiveFunctionOrigin(state)
            | None -> false)

// The slot that supplied the base case hands its reference to the last cell of a closed chain.
let tcoExitTransferTemp (result: Int) (state: CoreLoweringState) =
    (let group = state.ownerState
    in
        match group.closedProducerChain with
            | Some((origin, closed, transferTemp)) ->
                if closed == result && origin == stateActiveFunctionOrigin(state)
                then transferTemp
                else result
            | None -> result)

// Whether `temp` is the result the function being lowered already made its own.
let isGeneralRcFinishedResult (temp: Int) (state: CoreLoweringState) =
    (let group = state.ownerState
    in
        match group.generalRcFinishedResult with
            | Some((origin, finished)) -> finished == temp && origin == stateActiveFunctionOrigin(state)
            | None -> false)

let recursive ownedSlotsOf (origin: Maybe(IrFunctionOrigin)) (slots: List((Maybe(IrFunctionOrigin), Int, SemanticType))) (collected: List((Int, SemanticType))) =
    match slots with
        | [] -> collected
        | (owner, slot, semanticType) :: rest ->
            if owner == origin
            then ownedSlotsOf(origin)(rest)((slot, semanticType) :: collected)
            else ownedSlotsOf(origin)(rest)(collected)

// The owned slots the function being lowered has registered so far, in registration order.
let ownedSlotsOfActiveFunction (state: CoreLoweringState) =
    ownedSlotsOf(stateActiveFunctionOrigin(state))(stateGeneralRcOwnedSlots(state))([])

// Stage 0's `EnterGeneralRcBranch` / `LeaveGeneralRcBranch`: the arm or branch of the join
// labelled `join` being lowered.
let enterGeneralRcBranch (join: Str) (arm: Str) (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with generalRcBranches = (join, arm) :: group.generalRcBranches))

let leaveGeneralRcBranch (state: CoreLoweringState) =
    (let group = state.ownerState
    in
        match group.generalRcBranches with
            | [] -> state
            | _innermost :: outer -> state with ownerState = (group with generalRcBranches = outer))

// A branch body lowered inside its arm of the join.
let lowerInGeneralRcBranch (join: Str) (arm: Str) lowerBranch (state: CoreLoweringState) =
    match state
    |> enterGeneralRcBranch(join)(arm)
    |> lowerBranch with
        | LoweredCoreValue { state = lowered, temp = temp, semanticType = semanticType, error = error } -> LoweredCoreValue(state = leaveGeneralRcBranch(lowered), temp = temp, semanticType = semanticType, error = error)

// Records the branches the owned slot just registered was stored inside.
let recordGeneralRcSlotBranches (slot: Int) (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with generalRcSlotBranches = (stateActiveFunctionOrigin(state), slot, group.generalRcBranches) :: group.generalRcSlotBranches))

let recursive branchesOfSlot (origin: Maybe(IrFunctionOrigin)) (slot: Int) (recorded: List((Maybe(IrFunctionOrigin), Int, List((Str, Str))))) =
    match recorded with
        | [] -> []
        | (owner, recordedSlot, branches) :: rest ->
            if recordedSlot == slot && owner == origin
            then branches
            else branchesOfSlot(origin)(slot)(rest)

// Compares two branch paths outermost first: they diverge harmlessly at a different join, and
// exclusively at two arms of the same one.
let recursive branchPathsShareIteration (slotPath: List((Str, Str))) (currentPath: List((Str, Str))) =
    match (slotPath, currentPath) with
        | ((slotJoin, slotArm) :: slotRest, (currentJoin, currentArm) :: currentRest) ->
            if slotJoin == currentJoin && slotArm == currentArm
            then branchPathsShareIteration(slotRest)(currentRest)
            else slotJoin != currentJoin
        | _ -> true

// Stage 0's `SharesIterationWith`: whether the owned slot can hold this iteration's value where
// the lowering is now. Not when the two sit in different arms of the same join, which one
// iteration never both runs; a value an earlier iteration left there is released at its own arm's
// back edge or at the loop's exit.
let slotSharesIteration (slot: Int) (state: CoreLoweringState) =
    (let group = state.ownerState
    in
        group.generalRcBranches
        |> reverse
        |> branchPathsShareIteration(group.generalRcSlotBranches
        |> branchesOfSlot(stateActiveFunctionOrigin(state))(slot)
        |> reverse))

let recursive slotsSharingIteration (slots: List((Int, SemanticType))) (state: CoreLoweringState) =
    match slots with
        | [] -> []
        | (slot, semanticType) :: rest ->
            if slotSharesIteration(slot)(state)
            then (slot, semanticType) :: slotsSharingIteration(rest)(state)
            else slotsSharingIteration(rest)(state)

let statePendingOwnerPlan (state: CoreLoweringState) =
    (let group = state.ownerState
    in group.pendingOwnerPlan)

let withStatePendingOwnerPlan value (state: CoreLoweringState) =
    (let group = state.ownerState
    in state with ownerState = (group with pendingOwnerPlan = value))

let stateTcoLoop (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.tcoLoop)

let withStateTcoLoop value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with tcoLoop = value))

let stateTcoLoopFrame (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.tcoLoopFrame)

let recursive isOwnedSlotIn (slot: Int) (ownedSlots: List((Int, SemanticType))) =
    match ownedSlots with
        | [] -> false
        | (candidate, _semanticType) :: rest -> candidate == slot || isOwnedSlotIn(slot)(rest)

// Stage 0's `OwnedSlotReadInto`: the owned slot a successor argument is a plain read of, or -1.
let recursive ownedSlotReadOf (temp: Int) (ownedSlots: List((Int, SemanticType))) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> -1
        | IrInstruction { instruction = LoadLocal(target, slot) } :: rest ->
            if target == temp
            then
                if isOwnedSlotIn(slot)(ownedSlots)
                then slot
                else -1
            else ownedSlotReadOf(temp)(ownedSlots)(rest)
        | _ :: rest -> ownedSlotReadOf(temp)(ownedSlots)(rest)

// Stage 0's `NoteGeneralRcOwnedSlotBinders`: the names a `let` or a match's patterns bind from a
// value just lowered, when that value is the read of an owned slot of the loop being lowered.
let noteGeneralRcOwnedSlotBinders (valueTemp: Int) (names: List(Str)) (state: CoreLoweringState) =
    match stateTcoLoopFrame(state) with
        | None -> state
        | Some(_frame) ->
            match ownedSlotReadOf(valueTemp)(ownedSlotsOfActiveFunction(state))(state.reversedInstructions) with
                | slot ->
                    if slot < 0
                    then state
                    else withStateGeneralRcSlotBinders((stateActiveFunctionOrigin(state), slot, names) :: stateGeneralRcSlotBinders(state))(state)

let noteLoweredValueBinders (names: List(Str)) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, temp = temp, error = None } -> lowered with state = noteGeneralRcOwnedSlotBinders(temp)(names)(state)
        | _ -> lowered

let recursive matchCaseBinders (cases: List((Pattern, Expr, Maybe(Expr)))) (names: List(Str)) =
    match cases with
        | [] -> names
        | (pattern, _body, _guard) :: rest ->
            names
            |> shapePatternNames(pattern)
            |> matchCaseBinders(rest)

let withStateTcoLoopFrame value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with tcoLoopFrame = value))

let stateTmcActivatedLabels (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.tmcActivatedLabels)

let withStateTmcActivatedLabels value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with tmcActivatedLabels = value))

let statePendingTcoResets (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.pendingTcoResets)

let withStatePendingTcoResets value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with pendingTcoResets = value))

let statePendingCallCopyOuts (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.pendingCallCopyOuts)

let withStatePendingCallCopyOuts value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with pendingCallCopyOuts = value))

let statePendingTakenOverSettles (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.pendingTakenOverSettles)

let withStatePendingTakenOverSettles value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with pendingTakenOverSettles = value))

let stateTakenOverHeldResults (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.takenOverHeldResults)

let withStateTakenOverHeldResults value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with takenOverHeldResults = value))

let stateTakenOverCallArguments (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.takenOverCallArguments)

let withStateTakenOverCallArguments value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with takenOverCallArguments = value))

let stateSettledBehindCopySpans (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.settledBehindCopySpans)

let withStateSettledBehindCopySpans value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with settledBehindCopySpans = value))

let stateWithdrawnTuplePartsRequests (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.withdrawnTuplePartsRequests)

let withStateWithdrawnTuplePartsRequests value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with withdrawnTuplePartsRequests = value))

let stateListHeadCells (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.listHeadCells)

let withStateListHeadCells value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with listHeadCells = value))

let stateSelfResultDeferredNames (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.selfResultDeferredNames)

let withStateSelfResultDeferredNames value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with selfResultDeferredNames = value))

let stateSiblingResultOpenNames (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.siblingResultOpenNames)

let withStateSiblingResultOpenNames value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with siblingResultOpenNames = value))

let stateSelfCallResultUnifications (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.selfCallResultUnifications)

let withStateSelfCallResultUnifications value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with selfCallResultUnifications = value))

let stateCallArgumentRecordRetains (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.callArgumentRecordRetains)

let stateCollectingCallArgumentRetains (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.collectingCallArgumentRetains)

let withCallArgumentRetainFrame (retains: List((Int, Int, SemanticType))) (collecting: Bool) (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with callArgumentRecordRetains = retains, collectingCallArgumentRetains = collecting))

let stateCallArgumentLabelStart (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.callArgumentLabelStart)

let stateCallArgumentFrame (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.callArgumentFrame)

let stateCallArgumentFrameActive (state: CoreLoweringState) = stateCallArgumentFrame(state) != NoCallArgumentFrame

let stateCallArgumentFrameUnderBackEdge (state: CoreLoweringState) = stateCallArgumentFrame(state) == BackEdgeNestedCallArgumentFrame

let stateTcoParameterRetainSites (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.tcoParameterRetainSites)

let withStateTcoParameterRetainSites value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with tcoParameterRetainSites = value))

let stateArenaParameterCopies (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.arenaParameterCopies)

let withStateArenaParameterCopies value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with arenaParameterCopies = value))

let stateBorrowedScrutineeCells (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.borrowedScrutineeCells)

let withStateBorrowedScrutineeCells value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with borrowedScrutineeCells = value))

let recursive transientFlagClearsOf (origin: Maybe(IrFunctionOrigin)) (clears: List((Maybe(IrFunctionOrigin), Int))) =
    match clears with
        | [] -> []
        | (owner, temp) :: rest ->
            if owner == origin
            then temp :: transientFlagClearsOf(origin)(rest)
            else transientFlagClearsOf(origin)(rest)

// The active-flag clears of the function being lowered that its back edges set again.
let stateTransientFlagClears (state: CoreLoweringState) =
    (let group = state.tcoState
    in
        transientFlagClearsOf(stateActiveFunctionOrigin(state))(group.transientFlagClears))

let noteTransientFlagClear (temp: Int) (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with transientFlagClears = (stateActiveFunctionOrigin(state), temp) :: group.transientFlagClears))

let stateBackEdgeArgumentSlot (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.backEdgeArgumentSlot)

let withStateBackEdgeArgumentSlot value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with backEdgeArgumentSlot = value))

let stateLateCopiedCellHeads (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.lateCopiedCellHeads)

let withStateLateCopiedCellHeads value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with lateCopiedCellHeads = value))

let stateArenaSuccessorLetLoop (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.arenaSuccessorLetLoop)

let withStateArenaSuccessorLetLoop value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with arenaSuccessorLetLoop = value))

let stateArenaKeptSuccessorLetLoop (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.arenaKeptSuccessorLetLoop)

let withStateArenaKeptSuccessorLetLoop value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with arenaKeptSuccessorLetLoop = value))

let stateArenaSuccessorJoin (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.arenaSuccessorJoin)

let withStateArenaSuccessorJoin value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with arenaSuccessorJoin = value))

// Whether the `if` or `match` being lowered at the current span is the arena successor join.
let isArenaSuccessorJoin (state: CoreLoweringState) =
    match (stateArenaSuccessorJoin(state), state.currentSpan) with
        | (Some(join), Some(span)) -> join == span
        | _ -> false

let stateAffineAppendContext (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.affineAppendContext)

let withStateAffineAppendContext value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with affineAppendContext = value))

let stateAffineAppendReservation (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.affineAppendReservation)

let withStateAffineAppendReservation value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with affineAppendReservation = value))

let stateRecursiveProducerResultSlots (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.recursiveProducerResultSlots)

let withStateRecursiveProducerResultSlots value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with recursiveProducerResultSlots = value))

let stateRecursiveGroupNames (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.recursiveGroupNames)

let withStateRecursiveGroupNames value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with recursiveGroupNames = value))

let stateCurryStage (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.curryStage)

let withStateCurryStage value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with curryStage = value))

let stateFunctionReturnedClosureLabels (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.functionReturnedClosureLabels)

let withStateFunctionReturnedClosureLabels value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with functionReturnedClosureLabels = value))

let statePassthroughParameter (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.passthroughParameter)

let withStatePassthroughParameter value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with passthroughParameter = value))

let stateLoweringBodyLabel (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.loweringBodyLabel)

let withStateLoweringBodyLabel (value: Str) (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with loweringBodyLabel = value))

let statePredictedRuntimeManagedResultLabels (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.predictedRuntimeManagedResultLabels)

let withStatePredictedRuntimeManagedResultLabels value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with predictedRuntimeManagedResultLabels = value))

let stateNormalizedAlwaysReturnedParameter (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.normalizedAlwaysReturnedParameter)

let withStateNormalizedAlwaysReturnedParameter value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with normalizedAlwaysReturnedParameter = value))

let stateNormalizedParameterCloned (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.normalizedParameterCloned)

let withStateNormalizedParameterCloned value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with normalizedParameterCloned = value))

let stateEntryLambdaNode (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.entryLambdaNode)

let withStateEntryLambdaNode (node: Int) (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with entryLambdaNode = node))

let stateRecordedParameterScope (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.recordedParameterScope)

let withStateRecordedParameterScope (scope: List((Str, SemanticType))) (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with recordedParameterScope = scope))

let stateRuntimeNormalizedArgumentLabels (state: CoreLoweringState) =
    (let group = state.tcoState
    in group.runtimeNormalizedArgumentLabels)

let withStateRuntimeNormalizedArgumentLabels value (state: CoreLoweringState) =
    (let group = state.tcoState
    in state with tcoState = (group with runtimeNormalizedArgumentLabels = value))

let stateConcreteRegistry (state: CoreLoweringState) =
    (let group = state.reuseState
    in group.concreteRegistry)

let withStateConcreteRegistry value (state: CoreLoweringState) =
    (let group = state.reuseState
    in state with reuseState = (group with concreteRegistry = value))

let stateConcreteDepth (state: CoreLoweringState) =
    (let group = state.reuseState
    in group.concreteDepth)

let withStateConcreteDepth value (state: CoreLoweringState) =
    (let group = state.reuseState
    in state with reuseState = (group with concreteDepth = value))

let stateConcreteDeclines (state: CoreLoweringState) =
    (let group = state.reuseState
    in group.concreteDeclines)

let withStateConcreteDeclines value (state: CoreLoweringState) =
    (let group = state.reuseState
    in state with reuseState = (group with concreteDeclines = value))

let stateAbstractElementTmcDeclines (state: CoreLoweringState) =
    (let group = state.reuseState
    in group.abstractElementTmcDeclines)

let stateTmcDeclines (state: CoreLoweringState) =
    (let group = state.reuseState
    in group.tmcDeclines)

// The declined-cell counts of `source`, kept by `state` across a frame restore.
let carryTmcDeclines (source: CoreLoweringState) (state: CoreLoweringState) =
    (let group = state.reuseState
    in state with reuseState = (group with tmcDeclines = stateTmcDeclines(source), abstractElementTmcDeclines = stateAbstractElementTmcDeclines(source)))

// Stage 0's `RecordTmcDecline`: one declined tail-modulo-constructor cell, counted as abstract when
// its element type is still unresolved.
let recordTmcDecline (abstractElement: Bool) (state: CoreLoweringState) =
    (let group = state.reuseState
    in
        state with reuseState = (group with tmcDeclines = group.tmcDeclines + 1, abstractElementTmcDeclines = (if abstractElement
        then group.abstractElementTmcDeclines + 1
        else group.abstractElementTmcDeclines)))

let stateReuseTransferredNames (state: CoreLoweringState) =
    (let group = state.reuseState
    in group.reuseTransferredNames)

let withStateReuseTransferredNames value (state: CoreLoweringState) =
    (let group = state.reuseState
    in state with reuseState = (group with reuseTransferredNames = value))

let stateReuseEnabled (state: CoreLoweringState) =
    (let group = state.reuseState
    in group.reuseEnabled)

let withStateReuseEnabled value (state: CoreLoweringState) =
    (let group = state.reuseState
    in state with reuseState = (group with reuseEnabled = value))

let stateLinearReuseNames (state: CoreLoweringState) =
    (let group = state.reuseState
    in group.linearReuseNames)

let withStateLinearReuseNames value (state: CoreLoweringState) =
    (let group = state.reuseState
    in state with reuseState = (group with linearReuseNames = value))

let stateDirectReuseCandidates (state: CoreLoweringState) =
    (let group = state.reuseState
    in group.directReuseCandidates)

let withStateDirectReuseCandidates value (state: CoreLoweringState) =
    (let group = state.reuseState
    in state with reuseState = (group with directReuseCandidates = value))

let stateReuseSpecializations (state: CoreLoweringState) =
    (let group = state.reuseState
    in group.reuseSpecializations)

let withStateReuseSpecializations value (state: CoreLoweringState) =
    (let group = state.reuseState
    in state with reuseState = (group with reuseSpecializations = value))

let stateSpecializingLinearParam (state: CoreLoweringState) =
    (let group = state.reuseState
    in group.specializingLinearParam)

let withStateSpecializingLinearParam value (state: CoreLoweringState) =
    (let group = state.reuseState
    in state with reuseState = (group with specializingLinearParam = value))

let stateSpecializingInProgress (state: CoreLoweringState) =
    (let group = state.reuseState
    in group.specializingInProgress)

let withStateSpecializingInProgress value (state: CoreLoweringState) =
    (let group = state.reuseState
    in state with reuseState = (group with specializingInProgress = value))

let stateSpecializationFreshInputs (state: CoreLoweringState) =
    (let group = state.reuseState
    in group.specializationFreshInputs)

let withStateSpecializationFreshInputs value (state: CoreLoweringState) =
    (let group = state.reuseState
    in state with reuseState = (group with specializationFreshInputs = value))

let stateStackAllocateConstructor (state: CoreLoweringState) =
    (let group = state.reuseState
    in group.stackAllocateConstructor)

let withStateStackAllocateConstructor value (state: CoreLoweringState) =
    (let group = state.reuseState
    in state with reuseState = (group with stackAllocateConstructor = value))

let stateLinearSpecializationAccumulators (state: CoreLoweringState) =
    (let group = state.reuseState
    in group.linearSpecializationAccumulators)

let withStateLinearSpecializationAccumulators value (state: CoreLoweringState) =
    (let group = state.reuseState
    in state with reuseState = (group with linearSpecializationAccumulators = value))

let stateRoutedSpecializationAccumulators (state: CoreLoweringState) =
    (let group = state.reuseState
    in group.routedSpecializationAccumulators)

let withStateRoutedSpecializationAccumulators value (state: CoreLoweringState) =
    (let group = state.reuseState
    in state with reuseState = (group with routedSpecializationAccumulators = value))

let stateRoutedSpecializationCallees (state: CoreLoweringState) =
    (let group = state.reuseState
    in group.routedSpecializationCallees)

let withStateRoutedSpecializationCallees value (state: CoreLoweringState) =
    (let group = state.reuseState
    in state with reuseState = (group with routedSpecializationCallees = value))

let stateSpecializingReuseLabel (state: CoreLoweringState) =
    (let group = state.reuseState
    in group.specializingReuseLabel)

let withStateSpecializingReuseLabel value (state: CoreLoweringState) =
    (let group = state.reuseState
    in state with reuseState = (group with specializingReuseLabel = value))

let stateFullyReusingCallees (state: CoreLoweringState) =
    (let group = state.reuseState
    in group.fullyReusingCallees)

let withStateFullyReusingCallees value (state: CoreLoweringState) =
    (let group = state.reuseState
    in state with reuseState = (group with fullyReusingCallees = value))

let stateInliningInProgress (state: CoreLoweringState) =
    (let group = state.reuseState
    in group.inliningInProgress)

let withStateInliningInProgress value (state: CoreLoweringState) =
    (let group = state.reuseState
    in state with reuseState = (group with inliningInProgress = value))

let stateScrutineeTuple (state: CoreLoweringState) =
    (let group = state.reuseState
    in group.scrutineeTuple)

let withStateScrutineeTuple value (state: CoreLoweringState) =
    (let group = state.reuseState
    in state with reuseState = (group with scrutineeTuple = value))

let stateRuntimeClosureRequest (state: CoreLoweringState) =
    (let group = state.reuseState
    in group.runtimeClosureRequest)

let withStateRuntimeClosureRequest value (state: CoreLoweringState) =
    (let group = state.reuseState
    in state with reuseState = (group with runtimeClosureRequest = value))

let takeRuntimeClosureRequest (state: CoreLoweringState) =
    if stateRuntimeClosureRequest(state)
    then withStateRuntimeClosureRequest(false)(state)
    else state

let withStateParallelWorkerFunctions value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with parallelWorkerFunctions = value))

let stateParallelWorkerFunctions (state: CoreLoweringState) =
    (let group = state.programState
    in group.parallelWorkerFunctions)

let stateMayRunAsParallelWorker (state: CoreLoweringState) =
    (let group = state.functionState
    in group.mayRunAsParallelWorker)

let withStateMayRunAsParallelWorker value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with mayRunAsParallelWorker = value))

let withPendingGoalObservation regionOfName (state: CoreLoweringState) =
    (let configured =
        state
        |> withStateSourceRegions(regionOfName)
        |> withStateSourceRegionOf("")
    in
        let group = configured.programState
        in
            match group.typeObservation with
                | None -> configured
                | Some(observation) -> configured with programState = (group with typeObservation = Some((observation with goalSites = Some([])))))

let observeGoalSite structural deferredOperator key operand (state: CoreLoweringState) =
    (let group = state.programState
    in
        match group.typeObservation with
            | Some(CoreTypeObservation { goalSites = Some(sites) } as observation) ->
                let site = ObservedPendingGoal(key = key, structural = structural, deferredOperator = deferredOperator, region = stateCurrentItem(state), node = state.currentNode, operand = operand, activeMethods = stateActiveTraitMethods(state))
                in state with programState = (group with typeObservation = Some((observation with goalSites = Some(Ashes.Internal.deepCopy(site) :: sites))))
            | _ -> state)

let observePendingGoal structural key operand (state: CoreLoweringState) = observeGoalSite(structural)(false)(key)(operand)(state)

// Records an operator site lowering deferred because both operands were still open, when the
// lowering observes its goal sites; otherwise the state is unchanged.
let observeDeferredOperatorGoal key operand (state: CoreLoweringState) = observeGoalSite(false)(true)(key)(operand)(state)

// Whether this lowering records the step-8 shadow comparison; a caller computes the final-type
// answer only when it does, so an ordinary compile does no extra work.
let shadowing (state: CoreLoweringState) =
    (let group = state.programState
    in
        match group.typeObservation with
            | Some(_observation) -> true
            | None -> false)

let recursive incrementAt (index: Int) (counts: List(Int)) =
    match counts with
        | [] -> []
        | head :: rest ->
            if index == 0
            then head + 1 :: rest
            else head :: incrementAt(index - 1)(rest)

let recordShadowSite (site: ShadowSite) (state: CoreLoweringState) =
    (let group = state.programState
    in
        match group.typeObservation with
            | None -> state
            | Some(observation) -> state with programState = (group with typeObservation = Some((observation with shadowSites = Ashes.Internal.deepCopy(site) :: observation.shadowSites))))

// Why a site's final-type answer is missing: no node is in effect (code lowering synthesized
// with no identity), the node is one lowering synthesized from a source node, or the recorded type
// is open after the instantiation and the settled variables, or does not translate.
let shadowUnrecordedReason (state: CoreLoweringState) =
    match state.currentNode with
        | None -> "no node"
        | Some(node) ->
            if node < 0
            then "synthesized node"
            else
                match stateNodeTypes(state) with
                    | Some(table) ->
                        match nodeTypeAt(node)(table) with
                            | Some(_recorded) -> "open or untranslatable type"
                            | None -> "no type recorded at the node"
                    | None -> "no node table"

// A decision whose final-type answer is missing for a reason the caller knows.
let observeShadowUnrecorded (entry: Int) (current: Str) (reason: Str) (state: CoreLoweringState) = recordShadowSite(ShadowSite(entry = entry, region = stateCurrentItem(state), node = state.currentNode, span = state.currentSpan, current = current, final = None, reason = reason, calleeFacts = None))(state)

// One decision of order-dependent rule `entry`: what the rule decided at the site and what the rule
// over the final type decides there (None when the table records nothing closed for it). An
// agreeing site is only counted; any other is kept with its location.
let observeShadow (entry: Int) (current: Str) (final: Maybe(Str)) (state: CoreLoweringState) =
    (let group = state.programState
    in
        match group.typeObservation with
            | None -> state
            | Some(observation) ->
                if final == Some(current)
                then state with programState = (group with typeObservation = Some((observation with shadowAgree = incrementAt(entry - 1)(observation.shadowAgree))))
                else recordShadowSite(ShadowSite(entry = entry, region = stateCurrentItem(state), node = state.currentNode, span = state.currentSpan, current = current, final = final, reason = shadowUnrecordedReason(state), calleeFacts = None))(state))

// Whether this lowering measures the cost of re-lowering by lowering every body once.
let stateSingleLowering (state: CoreLoweringState) =
    (let group = state.programState
    in
        match group.typeObservation with
            | Some(CoreTypeObservation { singleLowering = single }) -> single
            | None -> false)

// The node of an applied lambda, kept only while the lowering observes its types.
let withShadowLambdaNode (node: Int) (state: CoreLoweringState) =
    (let group = state.programState
    in
        match group.typeObservation with
            | None -> state
            | Some(observation) -> state with programState = (group with typeObservation = Some((observation with shadowLambdaNode = Some(node)))))

let stateShadowLambdaNode (state: CoreLoweringState) =
    (let group = state.programState
    in
        match group.typeObservation with
            | Some(CoreTypeObservation { shadowLambdaNode = node }) -> node
            | None -> None)

let withSingleLoweringMeasurement (single: Bool) (state: CoreLoweringState) =
    (let group = state.programState
    in
        match group.typeObservation with
            | None -> state
            | Some(observation) -> state with programState = (group with typeObservation = Some((observation with singleLowering = single))))

// A decision of rule 9 whose final answer is known only once every body is lowered.
let observeShadowPending (entry: Int) (current: Str) (reason: Str) (facts: Maybe(CoreCalleeFacts)) (state: CoreLoweringState) = recordShadowSite(ShadowSite(entry = entry, region = stateCurrentItem(state), node = state.currentNode, span = state.currentSpan, current = current, final = None, reason = reason, calleeFacts = facts))(state)
