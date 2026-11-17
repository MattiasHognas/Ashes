// Closure emission and frames, heap admissibility queries, argument copy plans and the deep-copy
// emitters. A part of `AshesCompiler.Semantics.CoreLowering`.

import Ashes.Collection.List.append
import Ashes.Collection.List.map
import Ashes.Collection.List.reverse
import Ashes.Collection.Map.MapTree
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.HeapLayoutClassification
import AshesCompiler.Semantics.AdmissibilityCache
import AshesCompiler.Semantics.IrControlFlowGraph.containsInt
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.FunctionOrigins.createAdtNormalizerOrigin
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.StructuralDroppers
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.CoreLowering.LoweringState
import AshesCompiler.Semantics.CoreLowering.Emit
import AshesCompiler.Semantics.CoreLowering.Owners
export (
    type ArgumentCopyPlan(..),
    value restoreOuterFrame,
    value emitClosure,
    value prepareLambdaBodyState,
    value freshTempRun,
    value listHeadCopyKindOf,
    value emitListSpineWalkBody,
    value scalarCaptureText,
    value scalarCaptureLayout,
    value captureTypes,
    value captureLayoutText,
    value locatedInstruction,
    value unlocatedInstruction,
    value emitReferenceOrCopy,
    value emitBackEdgeReferenceOrCopy,
    value emitResolvedOwnedDrops,
    value emitPlainTcoReset,
    value unreadLoopSlotType,
    value slotResolvedType,
    value isTcoRuntimeManagedStrSlot,
    value lookupRuntimeManagedListSlot,
    value ownedConstructorChildren,
    value runtimeManagedAdtLayout,
    value isGeneralRcAdmissibleIndexed,
    value isGeneralRcAdmissible,
    value prepareGeneralRcAdmissibility,
    value anyChildSurvivesNoReset,
    value argumentCopyPlanOf,
    value generalCopyPlanOf,
    value heapChildrenOfNamed,
    value heapChildTypesOfNamed,
    value inlineAdtCopyPlanOf,
    value entryCopyPlanOf,
    value reachesGeneralRcNamedTypeIndexed,
    value isGeneralRcNamedType,
    value recordGeneralRcTypeFacts,
    value recordContractRecordFacts,
    value ownedResultPlanOf,
    value entryNormalizationPlanOf,
    value rcNormalizationCopyOut,
    value emitArgumentDeepCopy,
    value emitGuardedListDeepCopy,
    value emitListDeepCopy,
    value emitListDeepCopyWith,
    value emitArgumentCopyByCopy,
    value emitArgumentCopy,
    value emitGuardedDeepCopy,
    value emitOwnedResultOrCopy,
)

// A function frame inherits these environments. Only top-level type declarations
// replace them. restoreOuterFrame receives a descendant body, so equal versions
// mean no replacement along that lineage; they do not identify independent states.
// A changed version must not carry either graph lineage back out.
let restoreHeapQueryContexts (body: CoreLoweringState) (outer: CoreLoweringState) =
    (let innerProgram = body.programState
    in
        let outerProgram = outer.programState
        in
            if innerProgram.coverageEnvironmentVersion == outerProgram.coverageEnvironmentVersion
            then
                outer
                |> withStateHeapQueryContext(innerProgram.heapQueryContext)
                |> withStateDropperContext(dropperQueryContextOf(innerProgram.dropperTypes))
            else outer)

let restoreOuterFrame outer bodyState =
    match bodyState with
        | CoreLoweringState { functions = functions, nextLambdaId = nextLambdaId, nextLabelId = nextLabelId, stringLiterals = stringLiterals, typeSupply = typeSupply, substitution = substitution, functionState = CoreFunctionState { nextStringId = nextStringId, sealedOperatorDefaults = sealedOperatorDefaults }, tcoState = CoreTcoState { runtimeNormalizedArgumentLabels = normalizedLabels } } ->
            outer
            |> restoreHeapQueryContexts(bodyState)
            |> withStateConcreteRegistry(stateConcreteRegistry(bodyState))
            |> withStateConcreteDeclines(stateConcreteDeclines(bodyState))
            |> (given (current: CoreLoweringState) -> current with functions = functions)
            |> (given (current: CoreLoweringState) -> withStateRuntimeNormalizedArgumentLabels(normalizedLabels)(current))
            |> (given (current: CoreLoweringState) -> current with nextLambdaId = nextLambdaId)
            |> (given (current: CoreLoweringState) -> current with nextLabelId = nextLabelId)
            |> (given (current: CoreLoweringState) ->
                current
                |> withStateDeferredLabelNext(stateDeferredLabelNext(bodyState))
                |> withStateDeferredLabelGroups(stateDeferredLabelGroups(bodyState)))
            |> (given (current: CoreLoweringState) -> withStateNextStringId(nextStringId)(current))
            |> (given (current: CoreLoweringState) -> current with stringLiterals = stringLiterals)
            |> (given (current: CoreLoweringState) -> current with typeSupply = typeSupply)
            |> withSubstitution(substitution)
            |> (given (current: CoreLoweringState) -> withStateSealedOperatorDefaults(sealedOperatorDefaults)(current))
            |> (given (current: CoreLoweringState) ->
                withStatePendingClosureNormalizers(statePendingClosureNormalizers(bodyState))(current))
            |> (given (current: CoreLoweringState) ->
                withStateBodyRuntimeManagedByLabel(stateBodyRuntimeManagedByLabel(bodyState))(current))
            |> (given (current: CoreLoweringState) ->
                withStateGeneralRcOwnedResultLabels(stateGeneralRcOwnedResultLabels(bodyState))(current))
            |> (given (current: CoreLoweringState) ->
                withStateTupleResultPartLabels(stateTupleResultPartLabels(bodyState))(current))
            |> (given (current: CoreLoweringState) ->
                withStateFunctionReturnedClosureLabels(stateFunctionReturnedClosureLabels(bodyState))(current))
            |> (given (current: CoreLoweringState) ->
                current
                |> withStatePredictedRuntimeManagedResultLabels(statePredictedRuntimeManagedResultLabels(bodyState))
                |> withStateCurryStage(stateCurryStage(bodyState)))
            |> (given (current: CoreLoweringState) -> current with valuePlacements = bodyState.valuePlacements, joinRepresentations = bodyState.joinRepresentations, borrowedCalls = bodyState.borrowedCalls)
            |> (given (current: CoreLoweringState) ->
                current
                |> withStateSpecializingReuseLabel(stateSpecializingReuseLabel(bodyState))
                |> withStateFullyReusingCallees(stateFullyReusingCallees(bodyState))
                |> withStateReuseSpecializations(stateReuseSpecializations(bodyState)))
            // The label that bound a specialization's linear parameter is decided inside the body
            // being generated and read once it is finished, so it leaves the frame with it. The
            // specializations generated in the body are program-wide functions, so their cache
            // leaves the frame too: a later frame reuses one instead of generating it again under
            // the same label.
            |> (given (current: CoreLoweringState) ->
                withStateUnresolvedCallResults(outer
                |> stateUnresolvedCallResults
                |> append(stateUnresolvedCallResults(bodyState)))(current))
            |> (given (current: CoreLoweringState) ->
                withStateRoutedSpecializationAccumulators(outer
                |> stateRoutedSpecializationAccumulators
                |> append(stateRoutedSpecializationAccumulators(bodyState)))(current))
            |> (given (current: CoreLoweringState) ->
                current
                |> withStateClosureDropperLabels(stateClosureDropperLabels(bodyState))
                |> withStateDropperLabels(stateDropperLabels(bodyState)))
            |> (given (current: CoreLoweringState) ->
                withStateTraitMethodLabels(stateTraitMethodLabels(bodyState))(current))
            |> (given (current: CoreLoweringState) ->
                withStatePendingTraitGoals(statePendingTraitGoals(bodyState))(current))
            |> (given (current: CoreLoweringState) ->
                withStatePendingEqualityGoals(statePendingEqualityGoals(bodyState))(current))
            |> (given (current: CoreLoweringState) ->
                withStatePendingSiblingCallResults(statePendingSiblingCallResults(bodyState))(current))
            |> (given (current: CoreLoweringState) ->
                withStateTakenOverRecordNames(stateTakenOverRecordNames(bodyState))(current))
            |> (given (current: CoreLoweringState) ->
                withStateSpineFreedSpans(stateSpineFreedSpans(bodyState))(current))
            |> (given (current: CoreLoweringState) ->
                current
                |> withStateOwnFieldRetainedParameters(stateOwnFieldRetainedParameters(bodyState))
                |> withStateOwnFieldPendingParameters(stateOwnFieldPendingParameters(bodyState)))
            |> carryTypeObservation(bodyState)

let emitClosure label environmentTemp captureTotal stackAllocate state =
    match freshTemp(state) with
        | FreshTemp { state = tempState, temp = closureTemp } ->
            let byteCount = captureTotal * 8
            in
                let returnsRuntimeManaged = bodyReturnsRuntimeManaged(label)(tempState)
                in
                    let acceptsRuntimeManaged = acceptsRuntimeManagedArgument(label)(tempState)
                    in
                        // A closure over a function that normalizes its result at its return says so
                        // in its header, so a caller that cannot name the callee takes such a result
                        // over owned instead of retaining it.
                        let returnsGeneralRcOwned =
                            tempState
                            |> stateGeneralRcOwnedResultLabels
                            |> containsLabel(label)
                        in
                            let closureState =
                                if stackAllocate
                                then
                                    emit(MakeClosureStack(closureTemp)(label)(environmentTemp)(byteCount)(returnsRuntimeManaged)(acceptsRuntimeManaged)(returnsGeneralRcOwned))(tempState)
                                else
                                    emit(MakeClosure(closureTemp)(label)(environmentTemp)(byteCount)(false)(returnsRuntimeManaged)(acceptsRuntimeManaged)(returnsGeneralRcOwned))(tempState)
                            in (closureState, closureTemp)

let prepareLambdaBodyState parameter parameterType captures lambdaId origin (state: CoreLoweringState) =
    (let functionBindings =
        CoreBinding(
            name = parameter,
            scheme = emptyScheme(parameterType),
            location = CoreLocal(1),
            ownedRead = false,
            patternOwner = None,
            closedScheme = false
        ) :: capturedScope(captures)(0)
    in
        state
        |> enterFunctionOrigin(origin)
        |> (given (current: CoreLoweringState) -> current with reversedInstructions = [], tcoState = (current.tcoState with callArgumentFrameActive = false))
        |> (given (current: CoreLoweringState) -> current with bindings = functionBindings)
        |> withStateEnclosingBindings(state.bindings)
        |> (given (current: CoreLoweringState) -> current with nextTemp = 0)
        |> (given (current: CoreLoweringState) -> current with nextLocal = 2)
        |> (given (current: CoreLoweringState) -> withStatePendingOperatorDefaults([])(current))
        |> (given (current: CoreLoweringState) -> withStateResourceStates([])(current))
        |> (given (current: CoreLoweringState) ->
            (current with runtimeTemps = Ashes.Collection.Map.empty)
            |> withStateRecursiveProducerResultSlots([])
            |> withStateBackEdgeDummyTemps([])
            |> withStateReuseResultTemps([])
            |> withStatePatternOwnerCopyTemps([])
            |> withStateReleasedPatternOwnerDuplicates([])
            |> withStateRuntimeOwners([])
            |> withStateBorrowedOwners([])
            |> withStatePendingBorrowedOwners([])
            |> withStateRuntimeOwnerAliases([])
            |> withStateLinearReuseNames([])
            |> withStateLinearSpecializationAccumulators([])
            |> withStateRoutedSpecializationAccumulators([])
            |> withStateDirectReuseCandidates([])
            |> withStatePatternOwnerSites([])
            |> withStateTupleBinderParameters([])
            |> withStatePatternOwnerResultTemps([])
            |> withStateTcoParameterRetainSites([])
            |> withStateArenaParameterCopies([])
            |> withStateBorrowedScrutineeCells([])
            |> withStatePendingRuntimeArgumentFlags([])
            |> withStateBackEdgeArgumentSlot(None)
            |> withStateAffineAppendContext(None)
            |> withStateAffineAppendReservation(None))
        |> (given (current: CoreLoweringState) ->
            current
            |> withStateTcoLoopFrame(None)
            |> withStatePendingTcoResets([])
            |> withStatePendingCallCopyOuts([])
            |> withStatePendingTakenOverSettles([])
            |> withStateTakenOverHeldResults([])
            |> withStateTakenOverCallArguments([])
            |> withStateSettledBehindCopySpans([])
            |> withStateWithdrawnTuplePartsRequests([])
            |> withStateListHeadCells([])
            |> withStateSelfCallResultUnifications([])
            |> withStateRetiredLocals([]))
        |> (given (current: CoreLoweringState) ->
            current
            |> withStateUnresolvedCallResults([])
            |> withStateGenericDeepCopiedListTemps([]))
        |> armSpecializationLinearParameter(parameter)(origin)
        |> (given (current: CoreLoweringState) -> current with nextLambdaId = lambdaId + 1))

let bigIntCopySizeBytes = -2

// Reserves `count` consecutive temps and returns the first; the run is `temp .. temp + count - 1`.
let freshTempRun (count: Int) (state: CoreLoweringState) =
    match state with
        | CoreLoweringState { nextTemp = base } ->
            FreshTemp(
                state = withNextTemp(base + count)(state),
                temp = base
            )

let listHeadCopyKindOf (element: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(element) with
        | SemString -> Some(StringListHead)
        | SemList(inner) ->
            if inner
            |> resolveType(state)
            |> canArenaResetLayout
            then Some(InnerListHead)
            else None
        | resolved ->
            if resultSurvivesReset(resolved)(state)
            then Some(InlineListHead)
            else None

// The iterative `rcdrop_list_N` walk of a runtime-managed list over scalars held in a cursor
// slot: a unique cell releases its tail and frees itself, a shared cell is decremented once and
// keeps the rest of the spine.
let emitListSpineWalkBody (cursorSlot: Int) (loopLabel: Str) (sharedLabel: Str) (endLabel: Str) (firstTemp: Int) (state: CoreLoweringState) =
    state
    |> emit(Label(loopLabel))
    |> emit(LoadLocal(firstTemp)(cursorSlot))
    |> emit(LoadConstInt(firstTemp + 1)(0))
    |> emit(CmpIntNe(firstTemp + 2)(firstTemp)(firstTemp + 1))
    |> emit(JumpIfFalse(firstTemp + 2)(endLabel))
    |> emit(RcIsUnique(firstTemp + 3)(firstTemp))
    |> emit(JumpIfFalse(firstTemp + 3)(sharedLabel))
    |> emit(LoadMemOffset(firstTemp + 4)(firstTemp)(8))
    |> emit(RcDrop(firstTemp)("List")(-1)(true)(false)(None))
    |> emit(StoreLocal(cursorSlot)(firstTemp + 4))
    |> emit(Jump(loopLabel))
    |> emit(Label(sharedLabel))
    |> emit(RcDrop(firstTemp)("List")(-1)(true)(false)(None))
    |> emit(Jump(endLabel))
    |> emit(Label(endLabel))

// A capture that a runtime-managed copy of the closure environment re-establishes by copying its
// word: the scalars, the only captures a closure recorded for the deferred decision can still
// qualify with.
let scalarCaptureText (semanticType: SemanticType) =
    match semanticType with
        | SemInt -> Some("Int")
        | SemUInt(bits) -> Some("u" + Ashes.Text.fromInt(bits))
        | SemFloat -> Some("Float")
        | SemRune -> Some("Rune")
        | SemBool -> Some("Bool")
        | _ -> None

// `(environment offset, type text)` per capture, in environment order, when every capture is a
// scalar.
let finalCaptureType (defaulted: List(Int)) (state: CoreLoweringState) (captureType: SemanticType) =
    match resolveType(state)(captureType) with
        | SemVariable(id) ->
            if containsInt(id)(defaulted)
            then SemInt
            else SemVariable(id)
        | resolved -> resolved

// `(environment offset, type text)` per capture, in environment order, when every capture is a
// scalar.
let recursive scalarCaptureLayout (types: List(SemanticType)) (index: Int) (defaulted: List(Int)) (state: CoreLoweringState) =
    match types with
        | [] -> Some([])
        | captureType :: rest ->
            match (captureType
            |> finalCaptureType(defaulted)(state)
            |> scalarCaptureText, scalarCaptureLayout(rest)(index + 1)(defaulted)(state)) with
                | (Some(text), Some(layout)) -> Some((index * 8, text) :: layout)
                | _ -> None

let recursive captureTypes (captures: List(CoreBinding)) =
    match captures with
        | [] -> []
        | CoreBinding { scheme = TypeScheme { body = captureType } } :: rest -> captureType :: captureTypes(rest)

let recursive captureLayoutText (layout: List((Int, Str))) =
    match layout with
        | [] -> ""
        | (offset, text) :: rest ->
            match rest with
                | [] -> Ashes.Text.fromInt(offset) + ":" + text
                | _ -> Ashes.Text.fromInt(offset) + ":" + text + ";" + captureLayoutText(rest)

// The normalizer's instructions carry the location of the closure they belong to, the ambient
// span stage 0 synthesizes them under.
let locatedInstruction (location: Maybe(IrSourceLocation)) kind = IrInstruction(instruction = kind, location = location)

let unlocatedInstruction (kind: IrInstructionKind) (state: CoreLoweringState) = state with reversedInstructions = IrInstruction(instruction = kind, location = None) :: state.reversedInstructions

// Stage 0's `EmitReferenceOrCopy`: an owned reference to a value whose representation this site
// cannot see. A value the runtime answers `IsReferenceCounted` for is retained, because a
// reference-counted block's children are reference-counted by construction and the reference is as
// good as the copy it replaces; anything else takes the copy the site would have made anyway. Both
// arms store into one slot, since this IR has no phi, and the joined value carries the copy's
// reference-counted fact, since either arm leaves an owned reference-counted value when it does.
let joinedCopyFact (copyTemp: Int) (resultTemp: Int) (state: CoreLoweringState) =
    match runtimeTempStateOf(copyTemp)(state) with
        | Some(runtimeState) -> markRuntimeTemp(resultTemp)(runtimeState)(state)
        | None -> state

let emitReferenceOrCopy (sourceTemp: Int) emitCopy (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = tested, temp = referenceCountedTemp } ->
            match freshLocal(tested) with
                | FreshLocal { state = slotted, local = resultSlot } ->
                    match freshLabel("rc_representation_copy")(slotted) with
                        | FreshLabel { state = copyLabelled, label = copyLabel } ->
                            match freshLabel("rc_representation_done")(copyLabelled) with
                                | FreshLabel { state = doneLabelled, label = doneLabel } ->
                                    match doneLabelled
                                    |> emit(IsReferenceCounted(referenceCountedTemp)(sourceTemp))
                                    |> emit(JumpIfFalse(referenceCountedTemp)(copyLabel))
                                    |> freshTemp with
                                        | FreshTemp { state = retained, temp = retainedTemp } ->
                                            match retained
                                            |> emit(RcDup(retainedTemp)(sourceTemp)(true)(false))
                                            |> emit(StoreLocal(resultSlot)(retainedTemp))
                                            |> emit(Jump(doneLabel))
                                            |> emit(Label(copyLabel))
                                            |> emitCopy with
                                                | (copied, copyTemp) ->
                                                    match copied
                                                    |> emit(StoreLocal(resultSlot)(copyTemp))
                                                    |> emit(Label(doneLabel))
                                                    |> freshTemp with
                                                        | FreshTemp { state = resulted, temp = resultTemp } ->
                                                            (resulted
                                                            |> emit(LoadLocal(resultTemp)(resultSlot))
                                                            |> joinedCopyFact(copyTemp)(resultTemp), resultTemp)

// Stage 0's guard on `TcoBackEdgeNormalizeRuntimeManagedArg`: the successor a back edge stores
// into a runtime-managed slot is the value itself when the runtime answers that it is already
// reference-counted, and the copy otherwise. A value that survives the reset needs neither. The
// test carries no location, as the rest of the reset does.
let emitBackEdgeReferenceOrCopy (sourceTemp: Int) (semanticType: SemanticType) emitCopy (state: CoreLoweringState) =
    if canTestRepresentation(state) == false || resultSurvivesReset(semanticType)(state)
    then emitCopy(state)
    else
        match emitReferenceOrCopy(sourceTemp)(emitCopy)((state with currentSpan = None)) with
            | (copied, copyTemp) -> ((copied with currentSpan = state.currentSpan), copyTemp)

// The iteration-local owners released at a back edge, stage 0's `EmitOwnedValueDrop` for each:
// an owner whose release plan reaches past its own cell walks its owned children inline, any
// other owner releases as one drop naming its slot. The deferred reset carries no location, so
// the inline walk runs with the current span cleared and the span is put back afterwards.
let recursive emitResolvedOwnedDrops (drops: List((Int, Str))) (state: CoreLoweringState) =
    match drops with
        | [] -> state
        | (slot, typeName) :: rest ->
            match freshTemp(state) with
                | FreshTemp { state = tempState, temp = temp } ->
                    match unlocatedInstruction(LoadLocal(temp)(slot))(tempState) with
                        | loaded ->
                            emitResolvedOwnedDrops(rest)((match emitInlineOwnerRelease(temp)(slot)((loaded with currentSpan = None)) with
                                | (Some(released), _queryContext) -> released with currentSpan = loaded.currentSpan
                                | (None, queryContext) ->
                                    loaded
                                    |> withStateDropperContext(queryContext)
                                    |> unlocatedInstruction(RcDrop(temp)(typeName)(slot)(true)(false)(None))))

let recursive argumentsSurviveReset (types: List(SemanticType)) (state: CoreLoweringState) =
    match types with
        | [] -> true
        | semanticType :: rest -> backEdgeArgumentSurvivesReset(semanticType)(state) && argumentsSurviveReset(rest)(state)

// Stage 0's `EmitTcoBackEdgeArenaBlock` for the plain reset: the pre-restore slot is allocated
// first, the iteration-local owners are released, and when every argument's resolved type
// survives a reset the per-iteration watermark is restored and the chunks above it reclaimed.
// An argument of any other type leaves the iteration's allocations in place (its copy-out
// paths are not ported).
let emitPlainTcoReset (reset: CoreTcoReset) (state: CoreLoweringState) =
    match freshLocal(state) with
        | FreshLocal { state = allocated, local = preRestoreSlot } ->
            allocated
            |> emitResolvedOwnedDrops(reset.ownedDrops)
            |> (given (released: CoreLoweringState) ->
                if argumentsSurviveReset(reset.argumentTypes)(released)
                then emitGuardedRestoreAndReclaimBy(unlocatedInstruction)(reset.arenaCursorSlot)(reset.arenaEndSlot)(preRestoreSlot)(released)
                else released)

let recursive unreadLoopSlotType (slot: Int) (unreadSlots: List((Int, SemanticType))) =
    match unreadSlots with
        | [] -> None
        | (candidate, parameterType) :: rest ->
            if candidate == slot
            then Some(parameterType)
            else unreadLoopSlotType(slot)(rest)

// The resolved type of a local slot: its binding's, or for a loop slot no binding names (a
// parameter the body never reads) its parameter's.
let recursive slotResolvedType (slot: Int) (bindings: List(CoreBinding)) (state: CoreLoweringState) =
    match bindings with
        | [] ->
            match stateTcoLoop(state) with
                | Some(loop) ->
                    match unreadLoopSlotType(slot)(loop.unreadSlots) with
                        | Some(parameterType) ->
                            parameterType
                            |> resolveType(state)
                            |> Some
                        | None -> None
                | None -> None
        | CoreBinding { location = CoreLocal(candidate), scheme = TypeScheme { body = bindingType } } :: rest ->
            if candidate == slot
            then
                bindingType
                |> resolveType(state)
                |> Some
            else slotResolvedType(slot)(rest)(state)
        | _ :: rest -> slotResolvedType(slot)(rest)(state)

let isTcoRuntimeManagedStrSlot (slot: Int) (state: CoreLoweringState) =
    match slotResolvedType(slot)(state.bindings)(state) with
        | Some(SemString) -> true
        | _ -> false

let recursive lookupRuntimeManagedListSlot (slot: Int) (entries: List((Int, Int, SemanticType))) =
    match entries with
        | [] -> None
        | (candidate, activeSlot, elementType) :: rest ->
            if candidate == slot
            then Some((activeSlot, elementType))
            else lookupRuntimeManagedListSlot(slot)(rest)

// The copy an entry normalization makes of a borrowed argument, decided from the parameter's
// resolved type the way stage 0's `EmitRuntimeManagedTcoParamCopy` and its deep-copy walk decide
// theirs: a scalar is kept as is, a string/bytes/bigint leaf and a same-arity scalar-field ADT
// are copied out in one piece, a list over copyable heads copies its spine, and a tuple or a
// runtime-managed ADT copies its owned children recursively. A constructor plan carries the
// constructor's tag, its cell size, whether the cell is tagless, and its owned children by field
// index.
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

// The cell size of one constructor: one word per field, plus the tag word unless tagless.
let layoutAllocationSizeBytes (layout: CoreConstructorLayout) =
    match layout with
        | CoreConstructorLayout { tagless = tagless } ->
            if tagless
            then 8 * constructorArity(layout)
            else 8 + 8 * constructorArity(layout)

// The described children of one constructor that own a reference, by field index and type.
let recursive ownedConstructorChildren (constructorName: Str) (children: List(HeapLayoutChild)) =
    match children with
        | [] -> []
        | HeapLayoutChild { dropKind = NoChildDrop } :: rest -> ownedConstructorChildren(constructorName)(rest)
        | HeapLayoutChild { constructorName = Some(owner), fieldIndex = index, childType = childType } :: rest ->
            if owner == constructorName
            then (index, childType) :: ownedConstructorChildren(constructorName)(rest)
            else ownedConstructorChildren(constructorName)(rest)
        | _ :: rest -> ownedConstructorChildren(constructorName)(rest)

// The list-head copy kind of a list argument: scalar heads copy inline, string heads and heads
// that are lists of scalars copy with the spine; any other element type has no spine copy.
let runtimeManagedAdtLayout (facts: HeapLayoutFacts) =
    match facts with
        | HeapLayoutFacts { runtimeRecordAdtSupported = record, runtimeOwnedChildAdtSupported = ownedChild, runtimeTcoOwnedChildAdtSupported = tcoOwnedChild, runtimePositionalAdtSupported = positional } -> record || ownedChild || tcoOwnedChild || positional

// Stage 0's `IsGeneralRcAdmissible`: a value that can be normalized whole into the
// reference-counted heap, a resolved graph of inline values, strings, byte buffers, big integers,
// lists, tuples and algebraic data types, recursive and generic ones included. Closures,
// resources, tasks and unresolved types are excluded. A named type already on the walk's path is
// taken as admissible, which is what lets a recursive type through.
let recordedGeneralRcTypeFacts (name: Str) (state: CoreLoweringState) =
    state
    |> stateGeneralRcTypeFacts
    |> Ashes.Collection.Map.getStr(name)

let admissibilityChildOf (child: HeapLayoutChild) = (child.childType, child.dropKind)

// Reuse settled monomorphic facts without reclassifying them. Generic and uncached types ask
// only for the containment and child-drop view, avoiding unrelated copy/reuse graph walks.
let cachedCoreAdmissibility semanticType (state: CoreLoweringState) =
    (let facts = coreHeapQueryFacts(semanticType)(state)
    in
        match facts.admissibility with
            | Some(answer) -> answer
            | None ->
                state
                |> stateCoverageTypes
                |> classifyHeapAdmissibility(semanticType))

let heapAdmissibilityOfIndexed (semanticType: SemanticType) (state: CoreLoweringState) =
    (let resolved = resolveType(state)(semanticType)
    in
        match resolved with
            | SemNamed(_symbolId, name, []) ->
                match state
                |> stateHeapLayoutFactsByType
                |> Ashes.Collection.Map.getStr(name) with
                    | Some(facts) -> (HeapAdmissibilityFacts(containsResource = facts.containsResource, containsUnresolvedType = facts.containsUnresolvedType, children = map(admissibilityChildOf)(facts.children)), state)
                    | None -> queryCoreAdmissibility(resolved)(state)
            | _ -> queryCoreAdmissibility(resolved)(state))

// Pure readers reuse completed views; stateful consumers retain cold query updates.
let heapAdmissibilityOf (semanticType: SemanticType) (state: CoreLoweringState) =
    (let resolved = resolveType(state)(semanticType)
    in
        match resolved with
            | SemNamed(_symbolId, name, []) ->
                match state
                |> stateHeapLayoutFactsByType
                |> Ashes.Collection.Map.getStr(name) with
                    | Some(facts) -> HeapAdmissibilityFacts(containsResource = facts.containsResource, containsUnresolvedType = facts.containsUnresolvedType, children = map(admissibilityChildOf)(facts.children))
                    | None -> cachedCoreAdmissibility(resolved)(state)
            | _ -> cachedCoreAdmissibility(resolved)(state))

type GeneralRcAdmissibilityWork =
    | VisitAdmissibilityType(SemanticType, Bool)
    | CheckAdmissibilityChild(SemanticType, HeapChildDropKind)

let recursive prependAdmissibilityTypes types allowRootCache pending =
    match types with
        | [] -> pending
        | head :: rest -> VisitAdmissibilityType(head)(allowRootCache) :: prependAdmissibilityTypes(rest)(allowRootCache)(pending)

let recursive prependAdmissibilityChildren children pending =
    match children with
        | [] -> pending
        | (childType, dropKind) :: rest -> CheckAdmissibilityChild(childType)(dropKind) :: prependAdmissibilityChildren(rest)(pending)

// Visited belongs to this whole universal query. Every expanded named type's children
// remain pending; encountering it again never stores an independently reusable true fact.
// Existing recursion keys can identify distinct types. Sharing a key across siblings
// then falls back to the original path-only walk rather than changing its answers.
let recursive admissibilityVisitStatus semanticType visited =
    match visited with
        | [] -> Some(false)
        | previous :: rest ->
            if containsType(semanticType)([previous])
            then
                if semanticType == previous
                then Some(true)
                else None
            else admissibilityVisitStatus(semanticType)(rest)

let recursive originalGeneralRcAdmissibleIndexed (semanticType: SemanticType) (path: List(SemanticType)) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemString -> (true, state)
        | SemBytes -> (true, state)
        | SemBigInt -> (true, state)
        | SemList(element) -> originalGeneralRcAdmissibleIndexed(element)(path)(state)
        | SemTuple(elements) -> originalAllGeneralRcAdmissibleIndexed(elements)(path)(state)
        | SemNamed(_symbolId, name, arguments) as named ->
            match (path, arguments, recordedGeneralRcTypeFacts(name)(state)) with
                | ([], [], Some((admissible, _reaches))) -> (admissible, state)
                | _ ->
                    if resultSurvivesReset(named)(state)
                    then (true, state)
                    else
                        if name == "Task" || containsType(named)(path)
                        then (name != "Task", state)
                        else
                            match heapAdmissibilityOfIndexed(named)(state) with
                                | (facts, queried) ->
                                    match (stateConstructorsOfType(name)(queried), facts) with
                                        | ([], _facts) -> (false, queried)
                                        | (_layouts, HeapAdmissibilityFacts { containsResource = true }) -> (false, queried)
                                        | (_layouts, HeapAdmissibilityFacts { containsUnresolvedType = true }) -> (false, queried)
                                        | (_layouts, HeapAdmissibilityFacts { children = children }) -> originalAllChildrenGeneralRcAdmissibleIndexed(children)(named :: path)(queried)
        | resolved -> (resultSurvivesReset(resolved)(state), state)
and originalAllGeneralRcAdmissibleIndexed (types: List(SemanticType)) (path: List(SemanticType)) (state: CoreLoweringState) =
    match types with
        | [] -> (true, state)
        | semanticType :: rest ->
            match originalGeneralRcAdmissibleIndexed(semanticType)(path)(state) with
                | (false, queried) -> (false, queried)
                | (true, queried) -> originalAllGeneralRcAdmissibleIndexed(rest)(path)(queried)
and originalAllChildrenGeneralRcAdmissibleIndexed (children: List((SemanticType, HeapChildDropKind))) (path: List(SemanticType)) (state: CoreLoweringState) =
    match children with
        | [] -> (true, state)
        | (_childType, DropClosure) :: _rest -> (false, state)
        | (_childType, UnsupportedChildDrop) :: _rest -> (false, state)
        | (childType, _dropKind) :: rest ->
            match originalGeneralRcAdmissibleIndexed(childType)(path)(state) with
                | (false, queried) -> (false, queried)
                | (true, queried) -> originalAllChildrenGeneralRcAdmissibleIndexed(rest)(path)(queried)

let recursive searchGeneralRcAdmissibilityIndexed pending visited state =
    match pending with
        | [] -> (Some(true), state)
        | CheckAdmissibilityChild(_childType, DropClosure) :: _rest -> (Some(false), state)
        | CheckAdmissibilityChild(_childType, UnsupportedChildDrop) :: _rest -> (Some(false), state)
        | CheckAdmissibilityChild(childType, _dropKind) :: rest -> searchGeneralRcAdmissibilityIndexed(VisitAdmissibilityType(childType)(false) :: rest)(visited)(state)
        | VisitAdmissibilityType(semanticType, allowRootCache) :: rest ->
            match resolveType(state)(semanticType) with
                | SemString -> searchGeneralRcAdmissibilityIndexed(rest)(visited)(state)
                | SemBytes -> searchGeneralRcAdmissibilityIndexed(rest)(visited)(state)
                | SemBigInt -> searchGeneralRcAdmissibilityIndexed(rest)(visited)(state)
                | SemList(element) -> searchGeneralRcAdmissibilityIndexed(VisitAdmissibilityType(element)(allowRootCache) :: rest)(visited)(state)
                | SemTuple(elements) ->
                    searchGeneralRcAdmissibilityIndexed(prependAdmissibilityTypes(elements)(allowRootCache)(rest))(visited)(state)
                | SemNamed(_symbolId, name, arguments) as named ->
                    match (allowRootCache, arguments, recordedGeneralRcTypeFacts(name)(state)) with
                        | (true, [], Some((admissible, _reaches))) ->
                            if admissible
                            then searchGeneralRcAdmissibilityIndexed(rest)(visited)(state)
                            else (Some(false), state)
                        | _ ->
                            if resultSurvivesReset(named)(state)
                            then searchGeneralRcAdmissibilityIndexed(rest)(visited)(state)
                            else
                                if name == "Task"
                                then (Some(false), state)
                                else
                                    match admissibilityVisitStatus(named)(visited) with
                                        | None -> (None, state)
                                        | Some(true) -> searchGeneralRcAdmissibilityIndexed(rest)(visited)(state)
                                        | Some(false) ->
                                            match heapAdmissibilityOfIndexed(named)(state) with
                                                | (facts, queried) ->
                                                    match (stateConstructorsOfType(name)(queried), facts) with
                                                        | ([], _facts) -> (Some(false), queried)
                                                        | (_layouts, HeapAdmissibilityFacts { containsResource = true }) -> (Some(false), queried)
                                                        | (_layouts, HeapAdmissibilityFacts { containsUnresolvedType = true }) -> (Some(false), queried)
                                                        | (_layouts, HeapAdmissibilityFacts { children = children }) ->
                                                            searchGeneralRcAdmissibilityIndexed(prependAdmissibilityChildren(children)(rest))(named :: visited)(queried)
                | resolved ->
                    if resultSurvivesReset(resolved)(state)
                    then searchGeneralRcAdmissibilityIndexed(rest)(visited)(state)
                    else (Some(false), state)

let queryGeneralRcAdmissibilityIndexed (semanticType: SemanticType) (path: List(SemanticType)) (state: CoreLoweringState) =
    match searchGeneralRcAdmissibilityIndexed([VisitAdmissibilityType(semanticType)(path == [])])(path)(state) with
        | (Some(answer), queried) -> (answer, queried)
        | (None, queried) -> originalGeneralRcAdmissibleIndexed(semanticType)(path)(queried)

let isGeneralRcAdmissibleIndexed (semanticType: SemanticType) (path: List(SemanticType)) (state: CoreLoweringState) =
    match path with
        | [] ->
            let group = state.functionState
            in
                match lookupAdmissibilityAnswer(resolveType(state)(semanticType))(group.admissibilityAnswers) with
                    | Some(answer) -> (answer, state)
                    | None -> queryGeneralRcAdmissibilityIndexed(semanticType)(path)(state)
        | _ -> queryGeneralRcAdmissibilityIndexed(semanticType)(path)(state)

// Compatibility readers expose the answer; lowering transitions use the indexed
// entry points so the same engine's completed views persist across requests.
let originalGeneralRcAdmissible semanticType path state =
    match originalGeneralRcAdmissibleIndexed(semanticType)(path)(state) with
        | (answer, _queried) -> answer

let searchGeneralRcAdmissibility pending visited state =
    match searchGeneralRcAdmissibilityIndexed(pending)(visited)(state) with
        | (answer, _queried) -> answer

let queryGeneralRcAdmissibility semanticType path state =
    match queryGeneralRcAdmissibilityIndexed(semanticType)(path)(state) with
        | (answer, _queried) -> answer

let isGeneralRcAdmissible semanticType path state =
    match isGeneralRcAdmissibleIndexed(semanticType)(path)(state) with
        | (answer, _queried) -> answer

let prepareGeneralRcAdmissibility semanticType (state: CoreLoweringState) =
    (let resolved = resolveType(state)(semanticType)
    in
        if !concreteAdmissibilityType(resolved)
        then state
        else
            let group = state.functionState
            in
                match lookupAdmissibilityAnswer(resolved)(group.admissibilityAnswers) with
                    | Some(_answer) -> state
                    | None ->
                        match queryGeneralRcAdmissibilityIndexed(resolved)([])(state) with
                            | (answer, queried) -> queried with functionState = (group with admissibilityAnswers = rememberAdmissibilityAnswer(resolved)(answer)(group.admissibilityAnswers)))

let recursive anyChildSurvivesNoReset (children: List(HeapLayoutChild)) (state: CoreLoweringState) =
    match children with
        | [] -> false
        | HeapLayoutChild { childType = childType } :: rest -> !resultSurvivesReset(childType)(state) || anyChildSurvivesNoReset(rest)(state)

// A settled monomorphic layout remains authoritative, including inferred contract flags.
// Only an uncached copy-plan query computes the narrower view from the environment.
let cachedCoreCopyPlan semanticType (state: CoreLoweringState) =
    (let facts = coreHeapQueryFacts(semanticType)(state)
    in
        match facts.copyPlan with
            | Some(answer) -> answer
            | None ->
                state
                |> stateCoverageTypes
                |> classifyHeapCopyPlan(semanticType))

let heapCopyPlanFactsOfIndexed (semanticType: SemanticType) (state: CoreLoweringState) =
    (let resolved = resolveType(state)(semanticType)
    in
        match resolved with
            | SemNamed(_symbolId, name, []) ->
                match state
                |> stateHeapLayoutFactsByType
                |> Ashes.Collection.Map.getStr(name) with
                    | Some(facts) -> (projectHeapCopyPlanFacts(facts), state)
                    | None -> queryCoreCopyPlan(resolved)(state)
            | _ -> queryCoreCopyPlan(resolved)(state))

// Pure readers reuse completed views; stateful consumers retain cold query updates.
let heapCopyPlanFactsOf (semanticType: SemanticType) (state: CoreLoweringState) =
    (let resolved = resolveType(state)(semanticType)
    in
        match resolved with
            | SemNamed(_symbolId, name, []) ->
                match state
                |> stateHeapLayoutFactsByType
                |> Ashes.Collection.Map.getStr(name) with
                    | Some(facts) -> projectHeapCopyPlanFacts(facts)
                    | None -> cachedCoreCopyPlan(resolved)(state)
            | _ -> cachedCoreCopyPlan(resolved)(state))

let runtimeManagedCopyPlanLayout (facts: HeapCopyPlanFacts) = facts.runtimeRecordAdtSupported || facts.runtimeOwnedChildAdtSupported || facts.runtimeTcoOwnedChildAdtSupported || facts.runtimePositionalAdtSupported

let recursive ownedCopyPlanChildren (constructorName: Str) (children: List(HeapCopyPlanChild)) =
    match children with
        | [] -> []
        | HeapCopyPlanChild { dropKind = NoChildDrop } :: rest -> ownedCopyPlanChildren(constructorName)(rest)
        | HeapCopyPlanChild { constructorName = Some(name), fieldIndex = index, childType = childType } :: rest ->
            if name == constructorName
            then (index, childType) :: ownedCopyPlanChildren(constructorName)(rest)
            else ownedCopyPlanChildren(constructorName)(rest)
        | _ :: rest -> ownedCopyPlanChildren(constructorName)(rest)

let recursive anyCopyPlanChildSurvivesNoReset (children: List(HeapCopyPlanChild)) (state: CoreLoweringState) =
    match children with
        | [] -> false
        | HeapCopyPlanChild { childType = childType } :: rest -> !resultSurvivesReset(childType)(state) || anyCopyPlanChildSurvivesNoReset(rest)(state)

let recursive copyPlanOfIndexed (general: Bool) (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemString -> (Some(LeafArgumentCopy(-1)), state)
        | SemBytes -> (Some(LeafArgumentCopy(-1)), state)
        | SemBigInt -> (Some(LeafArgumentCopy(bigIntCopySizeBytes)), state)
        | SemList(element) ->
            match listHeadCopyKindOf(element)(state) with
                | Some(headCopy) -> (Some(ListHeadArgumentCopy(headCopy)), state)
                | None ->
                    match copyPlanOfIndexed(general)(element)(state) with
                        | (Some(elementPlan), queried) -> (Some(ListDeepArgumentCopy(elementPlan)), queried)
                        | (None, queried) -> (None, queried)
        | SemTuple(elements) ->
            match copyPlansOfIndexed(general)(elements)(state) with
                | (Some(plans), queried) -> (Some(TupleArgumentCopy(plans)), queried)
                | (None, queried) -> (None, queried)
        | SemNamed(_symbolId, name, _arguments) as named ->
            match heapCopyPlanFactsOfIndexed(named)(state) with
                | (HeapCopyPlanFacts { shallowCopy = true }, queried) ->
                    (queried
                    |> shallowAdtCopySizeBytes(name)
                    |> ShallowAdtArgumentCopy
                    |> Some, queried)
                | (HeapCopyPlanFacts { children = children } as facts, queried) ->
                    if runtimeManagedCopyPlanLayout(facts)
                    then
                        let layouts = stateConstructorsOfType(name)(queried)
                        in
                            match isInlineCopiedContractRecordIndexed(named)(layouts)(facts)(queried) with
                                | (true, classified) -> (Some(NormalizerArgumentCopy(named)), classified)
                                | (false, classified) -> adtCopyPlanOfWithIndexed(general)(layouts)(children)(classified)
                    else
                        if general
                        then
                            match isGeneralRcAdmissibleIndexed(named)([])(queried) with
                                | (true, classified) -> (Some(NormalizerArgumentCopy(named)), classified)
                                | (false, classified) -> (None, classified)
                        else (None, queried)
        | resolved ->
            if resultSurvivesReset(resolved)(state)
            then (Some(ScalarArgumentCopy), state)
            else (None, state)
and copyPlansOfIndexed (general: Bool) (types: List(SemanticType)) (state: CoreLoweringState) =
    match types with
        | [] -> (Some([]), state)
        | semanticType :: rest ->
            match copyPlanOfIndexed(general)(semanticType)(state) with
                | (plan, queried) ->
                    match copyPlansOfIndexed(general)(rest)(queried) with
                        | (plans, completed) ->
                            match (plan, plans) with
                                | (Some(head), Some(tail)) -> (Some(head :: tail), completed)
                                | _ -> (None, completed)
and childCopyPlansOfWithIndexed (general: Bool) (children: List((Int, SemanticType))) (state: CoreLoweringState) =
    match children with
        | [] -> (Some([]), state)
        | (index, childType) :: rest ->
            match copyPlanOfIndexed(general)(childType)(state) with
                | (plan, queried) ->
                    match childCopyPlansOfWithIndexed(general)(rest)(queried) with
                        | (plans, completed) ->
                            match (plan, plans) with
                                | (Some(head), Some(tail)) -> (Some((index, head) :: tail), completed)
                                | _ -> (None, completed)
and constructorCopyPlansOfWithIndexed (general: Bool) (layouts: List(CoreConstructorLayout)) (children: List(HeapCopyPlanChild)) (state: CoreLoweringState) =
    match layouts with
        | [] -> (Some([]), state)
        | (CoreConstructorLayout { name = constructorName, tag = tag, tagless = tagless } as layout) :: rest ->
            match childCopyPlansOfWithIndexed(general)(ownedCopyPlanChildren(constructorName)(children))(state) with
                | (plans, queried) ->
                    match constructorCopyPlansOfWithIndexed(general)(rest)(children)(queried) with
                        | (restPlans, completed) ->
                            match (plans, restPlans) with
                                | (Some(head), Some(tail)) -> (Some((tag, layoutAllocationSizeBytes(layout), tagless, head) :: tail), completed)
                                | _ -> (None, completed)
and adtCopyPlanOfWithIndexed (general: Bool) (layouts: List(CoreConstructorLayout)) (children: List(HeapCopyPlanChild)) (state: CoreLoweringState) =
    match constructorCopyPlansOfWithIndexed(general)(layouts)(children)(state) with
        | (Some(single :: []), queried) -> (Some(ConstructorArgumentCopy(single)), queried)
        | (Some([]), queried) -> (None, queried)
        | (Some(plans), queried) -> (Some(SwitchArgumentCopy(plans)), queried)
        | (None, queried) -> (None, queried)
and isInlineCopiedContractRecordIndexed (named: SemanticType) (layouts: List(CoreConstructorLayout)) (facts: HeapCopyPlanFacts) (state: CoreLoweringState) =
    match recordedContractRecord(named)(state) with
        | Some(answer) -> (answer, state)
        | None -> classifyCopyPlanContractRecordIndexed(named)(layouts)(facts)(state)
and classifyCopyPlanContractRecordIndexed (named: SemanticType) (layouts: List(CoreConstructorLayout)) (facts: HeapCopyPlanFacts) (state: CoreLoweringState) =
    match (layouts, facts) with
        | (_layout :: [], HeapCopyPlanFacts { runtimeOwnedChildAdtSupported = false, arenaDeepCopySupported = true, children = children }) ->
            if anyCopyPlanChildSurvivesNoReset(children)(state) && !canRuntimeManageRecursiveCopyAdt(named)(state)
            then
                match adtCopyPlanOfWithIndexed(false)(layouts)(children)(state) with
                    | (Some(_plan), queried) -> (true, queried)
                    | (None, queried) -> (false, queried)
            else (false, state)
        | _ -> (false, state)

let argumentCopyPlanOfIndexed semanticType state = copyPlanOfIndexed(false)(semanticType)(state)

let generalCopyPlanOfIndexed semanticType state = copyPlanOfIndexed(true)(semanticType)(state)

let copyPlanOf general semanticType state =
    match copyPlanOfIndexed(general)(semanticType)(state) with
        | (answer, _queried) -> answer

let constructorCopyPlansOfWith general layouts children state =
    match constructorCopyPlansOfWithIndexed(general)(layouts)(children)(state) with
        | (answer, _queried) -> answer

let adtCopyPlanOfWith general layouts children state =
    match adtCopyPlanOfWithIndexed(general)(layouts)(children)(state) with
        | (answer, _queried) -> answer

let classifyCopyPlanContractRecord named layouts facts state =
    match classifyCopyPlanContractRecordIndexed(named)(layouts)(facts)(state) with
        | (answer, _queried) -> answer

let classifyInlineCopiedContractRecord (named: SemanticType) (layouts: List(CoreConstructorLayout)) (facts: HeapLayoutFacts) (state: CoreLoweringState) =
    classifyCopyPlanContractRecord(named)(layouts)(projectHeapCopyPlanFacts(facts))(state)

// The copy of a value into the reference-counted heap by the inline copies alone, which is also
// how the placement rules ask whether a type has one.
let argumentCopyPlanOf (semanticType: SemanticType) (state: CoreLoweringState) = copyPlanOf(false)(semanticType)(state)

// The same copy with the synthesized normalization helper standing in for every named type the
// inline copies do not express, stage 0's `EmitRuntimeManagedTcoDeepCopy` in full.
let generalCopyPlanOf (semanticType: SemanticType) (state: CoreLoweringState) = copyPlanOf(true)(semanticType)(state)

let cachedCoreChildren semanticType (state: CoreLoweringState) =
    (let facts = coreHeapQueryFacts(semanticType)(state)
    in
        match facts.children with
            | Some(answer) -> answer
            | None ->
                state
                |> stateCoverageTypes
                |> classifyHeapChildren(semanticType))

let heapChildrenOfNamedIndexed (semanticType: SemanticType) (state: CoreLoweringState) =
    (let resolved = resolveType(state)(semanticType)
    in
        match resolved with
            | SemNamed(_symbolId, name, []) ->
                match state
                |> stateHeapLayoutFactsByType
                |> Ashes.Collection.Map.getStr(name) with
                    | Some(facts) -> (facts.children, state)
                    | None -> queryCoreChildren(resolved)(state)
            | _ -> queryCoreChildren(resolved)(state))

// Pure readers reuse completed views; stateful consumers retain cold query updates.
let heapChildrenOfNamed (semanticType: SemanticType) (state: CoreLoweringState) =
    (let resolved = resolveType(state)(semanticType)
    in
        match resolved with
            | SemNamed(_symbolId, name, []) ->
                match state
                |> stateHeapLayoutFactsByType
                |> Ashes.Collection.Map.getStr(name) with
                    | Some(facts) -> facts.children
                    | None -> cachedCoreChildren(resolved)(state)
            | _ -> cachedCoreChildren(resolved)(state))

let cachedCoreChildTypes semanticType (state: CoreLoweringState) =
    (let facts = coreHeapQueryFacts(semanticType)(state)
    in
        match facts.childTypes with
            | Some(answer) -> answer
            | None ->
                state
                |> stateCoverageTypes
                |> classifyHeapChildTypes(semanticType))

let heapChildTypesOfNamedIndexed (semanticType: SemanticType) (state: CoreLoweringState) =
    (let resolved = resolveType(state)(semanticType)
    in
        match resolved with
            | SemNamed(_symbolId, name, []) ->
                match state
                |> stateHeapLayoutFactsByType
                |> Ashes.Collection.Map.getStr(name) with
                    | Some(facts) ->
                        (map(given (child: HeapLayoutChild) -> child.childType)(facts.children), state)
                    | None -> queryCoreChildTypes(resolved)(state)
            | _ -> queryCoreChildTypes(resolved)(state))

// Pure readers reuse completed views; stateful consumers retain cold query updates.
let heapChildTypesOfNamed (semanticType: SemanticType) (state: CoreLoweringState) =
    (let resolved = resolveType(state)(semanticType)
    in
        match resolved with
            | SemNamed(_symbolId, name, []) ->
                match state
                |> stateHeapLayoutFactsByType
                |> Ashes.Collection.Map.getStr(name) with
                    | Some(facts) ->
                        map(given (child: HeapLayoutChild) -> child.childType)(facts.children)
                    | None -> cachedCoreChildTypes(resolved)(state)
            | _ -> cachedCoreChildTypes(resolved)(state))

// The cell-by-cell copy of a named type written inline, a record of the contract's types included:
// stage 0's constructor deep copy, which the normalization helper's body and a successor built at
// the self-call use.
let inlineAdtCopyPlanOf (named: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(named) with
        | SemNamed(_symbolId, name, _arguments) as resolved ->
            adtCopyPlanOfWith(false)(state |> stateConstructorsOfType(name))(state
            |> heapChildrenOfNamed(resolved)
            |> projectHeapCopyPlanChildren)(state)
        | _ -> None

// Stage 0's `EmitRuntimeManagedTcoParamCopy` and `EmitRuntimeManagedTcoDeepCopy`: the copy of a value
// whose inline plan decided that it is copied at all, a record of the contract's types copied by its
// normalization helper at any depth.
let entryCopyPlanOf (semanticType: SemanticType) (plan: ArgumentCopyPlan) (state: CoreLoweringState) =
    match generalCopyPlanOf(semanticType)(state) with
        | Some(general) -> general
        | None -> plan

// Stage 0's `IsInlineCopiedContractRecord` for a named type with these layout facts, under the
// guards every question of it sits behind: an aggregate the runtime manages that a shallow copy
// does not express.
let contractRecordAnswer (named: SemanticType) (name: Str) (facts: HeapLayoutFacts) (state: CoreLoweringState) =
    match facts with
        | HeapLayoutFacts { structuralCopy = ShallowCopy } -> false
        | _ ->
            runtimeManagedAdtLayout(facts) && classifyInlineCopiedContractRecord(named)(state |> stateConstructorsOfType(name))(facts)(state)

// Stage 0's `IsGeneralRcNamedType`: a named type the contract governs, one only the normalization
// helper expresses, that owns a heap child, and that the recursive-copy path does not already
// manage on its own terms.
let isGeneralRcNamedTypeIndexed (named: SemanticType) (state: CoreLoweringState) =
    match generalCopyPlanOfIndexed(named)(state) with
        | (Some(NormalizerArgumentCopy(_type)), queried) ->
            if programMayRunUnderLiveHandlerPost(queried) == false && !canRuntimeManageRecursiveCopyAdt(named)(queried)
            then
                match heapChildrenOfNamedIndexed(named)(queried) with
                    | (children, completed) -> (anyChildSurvivesNoReset(children)(completed), completed)
            else (false, queried)
        | (_plan, queried) -> (false, queried)

let recursive reachesGeneralRcNamedTypeIndexed (semanticType: SemanticType) (path: List(SemanticType)) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemList(element) -> reachesGeneralRcNamedTypeIndexed(element)(path)(state)
        | SemTuple(elements) -> anyReachesGeneralRcNamedTypeIndexed(elements)(path)(state)
        | SemNamed(_symbolId, name, arguments) as named ->
            match (path, arguments, recordedGeneralRcTypeFacts(name)(state)) with
                | ([], [], Some((_admissible, reaches))) -> (reaches, state)
                | _ ->
                    match isGeneralRcNamedTypeIndexed(named)(state) with
                        | (true, queried) -> (true, queried)
                        | (false, queried) ->
                            if containsType(named)(path)
                            then (false, queried)
                            else
                                match heapChildTypesOfNamedIndexed(named)(queried) with
                                    | (children, completed) -> anyReachesGeneralRcNamedTypeIndexed(children)(named :: path)(completed)
        | _ -> (false, state)
and anyReachesGeneralRcNamedTypeIndexed (types: List(SemanticType)) (path: List(SemanticType)) (state: CoreLoweringState) =
    match types with
        | [] -> (false, state)
        | semanticType :: rest ->
            match reachesGeneralRcNamedTypeIndexed(semanticType)(path)(state) with
                | (true, queried) -> (true, queried)
                | (false, queried) -> anyReachesGeneralRcNamedTypeIndexed(rest)(path)(queried)

let isGeneralRcNamedType named state =
    match isGeneralRcNamedTypeIndexed(named)(state) with
        | (answer, _queried) -> answer

let reachesGeneralRcNamedType semanticType path state =
    match reachesGeneralRcNamedTypeIndexed(semanticType)(path)(state) with
        | (answer, _queried) -> answer

let anyReachesGeneralRcNamedType types path state =
    match anyReachesGeneralRcNamedTypeIndexed(types)(path)(state) with
        | (answer, _queried) -> answer

let recursive generalRcTypeFactsOf (types: List((Str, SemanticType))) (state: CoreLoweringState) (table: MapTree(Str, (Bool, Bool))) =
    match types with
        | [] -> table
        | (name, named) :: rest ->
            match state
            |> stateHeapLayoutFactsByType
            |> Ashes.Collection.Map.getStr(name) with
                | Some(_facts) ->
                    table
                    |> Ashes.Collection.Map.setStr(name)((isGeneralRcAdmissible(named)([])(state), reachesGeneralRcNamedType(named)([])(state)))
                    |> generalRcTypeFactsOf(rest)(state)
                | None -> generalRcTypeFactsOf(rest)(state)(table)

let recordGeneralRcTypeFacts (state: CoreLoweringState) =
    withStateGeneralRcTypeFacts(generalRcTypeFactsOf(argumentFreeLayoutTypes(stateConstructorLayouts(state))([]))(state)(Ashes.Collection.Map.empty))(state)

// Classifies an argument-free type for the contract-record table after the argument-free types its
// children name, so each classification reads its nested records' answers instead of walking them
// again. A type already on the walk's path is left to the walk that reached it first.
let recursive recordContractRecordOf (name: Str) (named: SemanticType) (visiting: List(Str)) (state: CoreLoweringState) =
    (let group = state.programState
    in
        match (Ashes.Collection.Map.getStr(name)(group.contractRecordFacts), seenLayoutTypeName(name)(visiting), Ashes.Collection.Map.getStr(name)(group.heapLayoutFactsByType)) with
            | (None, false, Some(HeapLayoutFacts { children = children } as facts)) ->
                let withChildren = recordChildContractRecords(children)(name :: visiting)(state)
                in
                    let answer = contractRecordAnswer(named)(name)(facts)(withChildren)
                    in
                        let recorded = withChildren.programState
                        in
                            withStateContractRecordFacts(Ashes.Collection.Map.setStr(name)(answer)(recorded.contractRecordFacts))(withChildren)
            | _ -> state)
and recordChildContractRecords (children: List(HeapLayoutChild)) (visiting: List(Str)) (state: CoreLoweringState) =
    match children with
        | [] -> state
        | HeapLayoutChild { childType = childType } :: rest ->
            state
            |> recordTypeContractRecords(childType)(visiting)
            |> recordChildContractRecords(rest)(visiting)
// The argument-free types a child's type names, through the lists and tuples holding them.
and recordTypeContractRecords (semanticType: SemanticType) (visiting: List(Str)) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemNamed(_symbolId, name, []) as named -> recordContractRecordOf(name)(named)(visiting)(state)
        | SemList(element) -> recordTypeContractRecords(element)(visiting)(state)
        | SemTuple(elements) -> recordTypesContractRecords(elements)(visiting)(state)
        | _ -> state
and recordTypesContractRecords (types: List(SemanticType)) (visiting: List(Str)) (state: CoreLoweringState) =
    match types with
        | [] -> state
        | semanticType :: rest ->
            state
            |> recordTypeContractRecords(semanticType)(visiting)
            |> recordTypesContractRecords(rest)(visiting)

let recursive recordContractRecordFactsOf (types: List((Str, SemanticType))) (state: CoreLoweringState) =
    match types with
        | [] -> state
        | (name, named) :: rest ->
            state
            |> recordContractRecordOf(name)(named)([])
            |> recordContractRecordFactsOf(rest)

// Stage 0's `IsInlineCopiedContractRecord` for every argument-free type the program declares whose
// layout facts are settled, recorded once when the program's lowering starts.
let recordContractRecordFacts (state: CoreLoweringState) =
    recordContractRecordFactsOf(argumentFreeLayoutTypes(stateConstructorLayouts(state))([]))(state)

// Stage 0's `CanNormalizeIntoOwnedRuntimeValue`: the result types the guarded copy turns into an
// owned reference-counted value. A list whose heads have no fixed copy is one too when its
// elements can be copied one by one (a table of string pairs).
let ownedResultPlanOf (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemString -> argumentCopyPlanOf(semanticType)(state)
        | SemList(element) ->
            match listHeadCopyKindOf(element)(state) with
                | Some(headCopy) -> Some(ListHeadArgumentCopy(headCopy))
                | None -> argumentCopyPlanOf(semanticType)(state)
        | SemNamed(_symbolId, _name, _arguments) -> argumentCopyPlanOf(semanticType)(state)
        | _ -> None

// The parameter types an entry normalization turns into an owned runtime-managed value, stage
// 0's `IsRuntimeNormalizableParameterType`: strings, and the ADTs the runtime copies out or
// deep-copies.
let entryNormalizationPlanOf (parameterType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(parameterType) with
        | SemString -> argumentCopyPlanOf(parameterType)(state)
        | SemNamed(_symbolId, _name, _arguments) -> argumentCopyPlanOf(parameterType)(state)
        | _ -> None

let rcNormalizationCopyOut copyTemp sourceTemp sizeBytes state =
    state
    |> emit(CopyOutArena(copyTemp)(sourceTemp)(sizeBytes)(true)(RcNormalization)(None))
    |> markRuntimeTemp(copyTemp)(RuntimeNewlyProduced)

let rcNormalizationListCopyOut copyTemp sourceTemp headCopy state =
    state
    |> emit(CopyOutList(copyTemp)(sourceTemp)(headCopy)(true)(RcNormalization))
    |> markRuntimeTemp(copyTemp)(RuntimeNewlyProduced)

let firstSwitchLabel (cases: List(IrSwitchCase)) =
    match cases with
        | IrSwitchCase { label = label } :: _rest -> label
        | [] -> ""

let normalizerLabelsOf (state: CoreLoweringState) =
    match stateDropperLabels(state) with
        | DropperLabelCache { normalizerLabels = labels } -> labels

let withNormalizerLabel (key: Str) (label: Str) (cache: DropperLabelCache) =
    match cache with
        | DropperLabelCache { normalizerLabels = labels } -> cache with normalizerLabels = (key, label) :: labels

// Stage 0's `EmitRuntimeManagedTcoDeepCopy`: a scalar is returned as is; every other plan copies
// into a fresh temp, the constructor and switch walks allocating their own result temps after it.
let recursive emitArgumentDeepCopy (sourceTemp: Int) (plan: ArgumentCopyPlan) (state: CoreLoweringState) =
    match plan with
        | ScalarArgumentCopy -> (state, sourceTemp)
        | ListDeepArgumentCopy(elementPlan) -> emitGuardedListDeepCopy(sourceTemp)(elementPlan)(state)
        | _ ->
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = resultTemp } ->
                    match plan with
                        | LeafArgumentCopy(sizeBytes) -> (rcNormalizationCopyOut(resultTemp)(sourceTemp)(sizeBytes)(allocated), resultTemp)
                        | ShallowAdtArgumentCopy(sizeBytes) -> (rcNormalizationCopyOut(resultTemp)(sourceTemp)(sizeBytes)(allocated), resultTemp)
                        | ListHeadArgumentCopy(headCopy) -> (rcNormalizationListCopyOut(resultTemp)(sourceTemp)(headCopy)(allocated), resultTemp)
                        | ListDeepArgumentCopy(elementPlan) -> emitGuardedListDeepCopy(sourceTemp)(elementPlan)(allocated)
                        | TupleArgumentCopy(elements) ->
                            allocated
                            |> emit(Alloc(resultTemp)(8 * coreListLength(elements))(true))
                            |> emitTupleElementCopies(sourceTemp)(resultTemp)(0)(elements)
                            |> markRuntimeTemp(resultTemp)(RuntimeNewlyProduced)
                            |> (given (copied) -> (copied, resultTemp))
                        | ConstructorArgumentCopy(constructorPlan) -> emitConstructorDeepCopy(sourceTemp)(constructorPlan)(allocated)
                        | SwitchArgumentCopy(constructorPlans) -> emitAdtSwitchDeepCopy(sourceTemp)(constructorPlans)(allocated)
                        | NormalizerArgumentCopy(named) -> emitNormalizerCall(sourceTemp)(named)(allocated)
                        | ScalarArgumentCopy -> (allocated, sourceTemp)
// Stage 0's guard on `EmitRuntimeManagedTcoListDeepCopy`: a list whose representation this site
// cannot see is taken by reference when the runtime answers that it is already reference-counted,
// and copied spine and heads otherwise. The entry copy reaches the walk below directly, since it
// tests the whole argument itself.
and emitGuardedListDeepCopy (sourceTemp: Int) (elementPlan: ArgumentCopyPlan) (state: CoreLoweringState) =
    if canTestRepresentation(state)
    then
        emitReferenceOrCopy(sourceTemp)(emitListDeepCopyWith(true)(false)(-1)(sourceTemp)(elementPlan))(state)
    else emitListDeepCopy(sourceTemp)(elementPlan)(state)
// Stage 0's `EmitRuntimeManagedTcoListDeepCopy`: a list whose heads have no spine copy is walked
// cell by cell, each head deep-copied into a fresh reference-counted cons cell appended behind
// the last one; the first cell is the copy.
and emitListDeepCopy (sourceTemp: Int) (elementPlan: ArgumentCopyPlan) (state: CoreLoweringState) = emitListDeepCopyWith(false)(false)(-1)(sourceTemp)(elementPlan)(state)
// With `shareSuffix` the copy stops at the first reference-counted cell and links it, retained, as
// the tail: a reference-counted cell's tail is reference-counted, so everything from there on is
// already owned whole. With `moveSuffix` the link takes over the reference the source's last arena
// cell held instead of retaining the suffix; when that reference is kept in `ownedSlot`, the slot is
// cleared, and a suffix the slot does not hold is retained.
and emitListDeepCopyWith (shareSuffix: Bool) (moveSuffix: Bool) (ownedSlot: Int) (sourceTemp: Int) (elementPlan: ArgumentCopyPlan) (state: CoreLoweringState) =
    match freshLocal(state) with
        | FreshLocal { state = currentState, local = currentSlot } ->
            match freshLocal(currentState) with
                | FreshLocal { state = firstState, local = firstSlot } ->
                    match freshLocal(firstState) with
                        | FreshLocal { state = lastState, local = lastSlot } ->
                            match freshTemp(lastState) with
                                | FreshTemp { state = zeroState, temp = zeroTemp } ->
                                    match freshLabel("rc_normalize_list")(zeroState) with
                                        | FreshLabel { state = loopState, label = loopLabel } ->
                                            match freshLabel("rc_normalize_list_end")(loopState) with
                                                | FreshLabel { state = endState, label = endLabel } ->
                                                    match endState
                                                    |> emit(LoadConstInt(zeroTemp)(0))
                                                    |> emit(StoreLocal(currentSlot)(sourceTemp))
                                                    |> emit(StoreLocal(firstSlot)(zeroTemp))
                                                    |> emit(StoreLocal(lastSlot)(zeroTemp))
                                                    |> emit(Label(loopLabel))
                                                    |> emitListDeepCopyCell(shareSuffix)(moveSuffix)(ownedSlot)(currentSlot)(firstSlot)(lastSlot)(zeroTemp)(loopLabel)(endLabel)(elementPlan)
                                                    |> emit(Label(endLabel))
                                                    |> freshTemp with
                                                        | FreshTemp { state = resultState, temp = resultTemp } ->
                                                            resultState
                                                            |> emit(LoadLocal(resultTemp)(firstSlot))
                                                            |> markRuntimeTemp(resultTemp)(RuntimeNewlyProduced)
                                                            |> (given (copied) -> (copied, resultTemp))
and emitListDeepCopyCell (shareSuffix: Bool) (moveSuffix: Bool) (ownedSlot: Int) (currentSlot: Int) (firstSlot: Int) (lastSlot: Int) (zeroTemp: Int) (loopLabel: Str) (endLabel: Str) (elementPlan: ArgumentCopyPlan) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = currentState, temp = currentTemp } ->
            match freshTemp(currentState) with
                | FreshTemp { state = nonEmptyState, temp = nonEmptyTemp } ->
                    match nonEmptyState
                    |> emit(LoadLocal(currentTemp)(currentSlot))
                    |> emit(CmpIntNe(nonEmptyTemp)(currentTemp)(zeroTemp))
                    |> emit(JumpIfFalse(nonEmptyTemp)(endLabel))
                    |> emitListDeepCopyShareSuffix(shareSuffix)(moveSuffix)(ownedSlot)(currentTemp)(firstSlot)(lastSlot)(zeroTemp)(endLabel)
                    |> freshTemp with
                        | FreshTemp { state = headState, temp = headTemp } ->
                            match freshTemp(headState) with
                                | FreshTemp { state = tailState, temp = tailTemp } ->
                                    match tailState
                                    |> emit(LoadMemOffset(headTemp)(currentTemp)(0))
                                    |> emit(LoadMemOffset(tailTemp)(currentTemp)(8))
                                    |> emitChildDeepCopy(headTemp)(elementPlan) with
                                        | (copied, copiedHead) ->
                                            match freshTemp(copied) with
                                                | FreshTemp { state = cellState, temp = cellTemp } ->
                                                    cellState
                                                    |> emit(Alloc(cellTemp)(16)(true))
                                                    |> emit(StoreMemOffset(cellTemp)(0)(copiedHead))
                                                    |> emit(StoreMemOffset(cellTemp)(8)(zeroTemp))
                                                    |> emitListDeepCopyAppend(firstSlot)(lastSlot)(zeroTemp)(cellTemp)
                                                    |> emit(StoreLocal(currentSlot)(tailTemp))
                                                    |> emit(Jump(loopLabel))
// Stage 0's `EmitRuntimeManagedTcoListShareSuffix`: a reference-counted cell ends the copy. It is
// retained, or taken over under `moveSuffix`, and appended behind the cells copied so far, and the
// walk jumps to its end.
and emitListDeepCopyShareSuffix (shareSuffix: Bool) (moveSuffix: Bool) (ownedSlot: Int) (currentTemp: Int) (firstSlot: Int) (lastSlot: Int) (zeroTemp: Int) (endLabel: Str) (state: CoreLoweringState) =
    if shareSuffix == false
    then state
    else
        match freshTemp(state) with
            | FreshTemp { state = testedState, temp = referenceCountedTemp } ->
                match testedState
                |> emit(IsReferenceCounted(referenceCountedTemp)(currentTemp))
                |> freshLabel("rc_normalize_list_cell") with
                    | FreshLabel { state = labelledState, label = copyCellLabel } ->
                        match labelledState
                        |> emit(JumpIfFalse(referenceCountedTemp)(copyCellLabel))
                        |> emitListDeepCopySuffixLink(moveSuffix)(ownedSlot)(zeroTemp)(currentTemp) with
                            | (linkedState, linkedTemp) ->
                                linkedState
                                |> emitListDeepCopyAppend(firstSlot)(lastSlot)(zeroTemp)(linkedTemp)
                                |> emit(Jump(endLabel))
                                |> emit(Label(copyCellLabel))
// The suffix the copy links: a retain of it, the suffix itself when the copy takes over the
// reference to it, or the suffix taken over from the owned slot keeping it.
and emitListDeepCopySuffixLink (moveSuffix: Bool) (ownedSlot: Int) (zeroTemp: Int) (suffixTemp: Int) (state: CoreLoweringState) =
    if moveSuffix
    then
        if ownedSlot < 0
        then (state, suffixTemp)
        else emitTakeOverOwnedSlotSuffix(suffixTemp)(ownedSlot)(zeroTemp)(state)
    else
        match freshTemp(state) with
            | FreshTemp { state = retainedState, temp = retainedTemp } ->
                (emit(RcDup(retainedTemp)(suffixTemp)(true)(false))(retainedState), retainedTemp)
// Stage 0's `EmitTakeOverOwnedSlotSuffix`: the suffix is taken over from the owned slot that holds
// it, the slot cleared so its release skips it; a suffix the slot does not hold is retained.
and emitTakeOverOwnedSlotSuffix (suffixTemp: Int) (ownedSlot: Int) (zeroTemp: Int) (state: CoreLoweringState) =
    match freshLocal(state) with
        | FreshLocal { state = linkedState, local = linkedSlot } ->
            match freshTemp(linkedState) with
                | FreshTemp { state = heldState, temp = heldTemp } ->
                    match freshTemp(heldState) with
                        | FreshTemp { state = comparedState, temp = heldHereTemp } ->
                            match freshLabel("rc_normalize_list_retain_suffix")(comparedState) with
                                | FreshLabel { state = retainLabelState, label = retainLabel } ->
                                    match freshLabel("rc_normalize_list_link_suffix")(retainLabelState) with
                                        | FreshLabel { state = linkLabelState, label = linkLabel } ->
                                            match freshTemp(linkLabelState) with
                                                | FreshTemp { state = retainedState, temp = retainedTemp } ->
                                                    match freshTemp(retainedState) with
                                                        | FreshTemp { state = loadedState, temp = linkedTemp } ->
                                                            (loadedState
                                                            |> emit(StoreLocal(linkedSlot)(suffixTemp))
                                                            |> emit(LoadLocal(heldTemp)(ownedSlot))
                                                            |> emit(CmpIntEq(heldHereTemp)(heldTemp)(suffixTemp))
                                                            |> emit(JumpIfFalse(heldHereTemp)(retainLabel))
                                                            |> emit(StoreLocal(ownedSlot)(zeroTemp))
                                                            |> emit(Jump(linkLabel))
                                                            |> emit(Label(retainLabel))
                                                            |> emit(RcDup(retainedTemp)(suffixTemp)(true)(false))
                                                            |> emit(StoreLocal(linkedSlot)(retainedTemp))
                                                            |> emit(Label(linkLabel))
                                                            |> emit(LoadLocal(linkedTemp)(linkedSlot)), linkedTemp)
// Stage 0's `EmitRuntimeManagedTcoListAppendCell`: the fresh cell becomes the first cell of the
// copy or the tail of the last one, and is the last one from then on.
and emitListDeepCopyAppend (firstSlot: Int) (lastSlot: Int) (zeroTemp: Int) (cellTemp: Int) (state: CoreLoweringState) =
    match freshLabel("rc_normalize_list_first")(state) with
        | FreshLabel { state = firstLabelState, label = firstLabel } ->
            match freshLabel("rc_normalize_list_linked")(firstLabelState) with
                | FreshLabel { state = linkedLabelState, label = linkedLabel } ->
                    match freshTemp(linkedLabelState) with
                        | FreshTemp { state = firstState, temp = firstTemp } ->
                            match freshTemp(firstState) with
                                | FreshTemp { state = hasFirstState, temp = hasFirstTemp } ->
                                    match freshTemp(hasFirstState) with
                                        | FreshTemp { state = lastState, temp = lastTemp } ->
                                            lastState
                                            |> emit(LoadLocal(firstTemp)(firstSlot))
                                            |> emit(CmpIntNe(hasFirstTemp)(firstTemp)(zeroTemp))
                                            |> emit(JumpIfFalse(hasFirstTemp)(firstLabel))
                                            |> emit(LoadLocal(lastTemp)(lastSlot))
                                            |> emit(StoreMemOffset(lastTemp)(8)(cellTemp))
                                            |> emit(Jump(linkedLabel))
                                            |> emit(Label(firstLabel))
                                            |> emit(StoreLocal(firstSlot)(cellTemp))
                                            |> emit(Label(linkedLabel))
                                            |> emit(StoreLocal(lastSlot)(cellTemp))
and emitTupleElementCopies (sourceTemp: Int) (resultTemp: Int) (index: Int) (elements: List(ArgumentCopyPlan)) (state: CoreLoweringState) =
    match elements with
        | [] -> state
        | element :: rest ->
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = childTemp } ->
                    match allocated
                    |> emit(LoadMemOffset(childTemp)(sourceTemp)(8 * index))
                    |> emitChildDeepCopy(childTemp)(element) with
                        | (copied, copiedChild) ->
                            copied
                            |> emit(StoreMemOffset(resultTemp)(8 * index)(copiedChild))
                            |> emitTupleElementCopies(sourceTemp)(resultTemp)(index + 1)(rest)
// Stage 0's `EmitRuntimeManagedTcoConstructorDeepCopy`: the cell is copied out whole, then every
// owned child is read from the source, deep-copied, and stored into the copy.
and emitConstructorDeepCopy (sourceTemp: Int) (constructorPlan: (Int, Int, Bool, List((Int, ArgumentCopyPlan)))) (state: CoreLoweringState) =
    match (constructorPlan, freshTemp(state)) with
        | ((_tag, sizeBytes, tagless, children), FreshTemp { state = allocated, temp = resultTemp }) ->
            allocated
            |> emit(CopyOutArena(resultTemp)(sourceTemp)(sizeBytes)(true)(RcNormalization)(None))
            |> emitConstructorChildCopies(sourceTemp)(resultTemp)(tagless)(children)
            |> markRuntimeTemp(resultTemp)(RuntimeNewlyProduced)
            |> (given (copied) -> (copied, resultTemp))
// Stage 0's `EmitRuntimeManagedTcoDeepCopy` reserves its result temp before a nested list's
// walk takes over, so a list child costs one unused temp ahead of its copy.
// The child a copied aggregate stores, stage 0's guard on `EmitRuntimeManagedTcoDeepCopy`: a
// reference to the original child when the runtime answers that it is already reference-counted,
// and the copy otherwise. A scalar child is its own word and has no representation to ask about.
and emitChildDeepCopy (sourceTemp: Int) (plan: ArgumentCopyPlan) (state: CoreLoweringState) =
    match (canTestRepresentation(state), plan) with
        | (false, _plan) -> emitChildDeepCopyByCopy(sourceTemp)(plan)(state)
        | (_tests, ScalarArgumentCopy) -> emitChildDeepCopyByCopy(sourceTemp)(plan)(state)
        | (_tests, _plan) ->
            emitReferenceOrCopy(sourceTemp)(emitChildDeepCopyByCopy(sourceTemp)(plan))(state)
and emitChildDeepCopyByCopy (sourceTemp: Int) (plan: ArgumentCopyPlan) (state: CoreLoweringState) =
    match plan with
        | ListDeepArgumentCopy(_elementPlan) ->
            match freshTemp(state) with
                | FreshTemp { state = reserved, temp = _unused } -> emitArgumentDeepCopy(sourceTemp)(plan)(reserved)
        | _ -> emitArgumentDeepCopy(sourceTemp)(plan)(state)
and emitConstructorChildCopies (sourceTemp: Int) (resultTemp: Int) (tagless: Bool) (children: List((Int, ArgumentCopyPlan))) (state: CoreLoweringState) =
    match children with
        | [] -> state
        | (index, plan) :: rest ->
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = childTemp } ->
                    match allocated
                    |> emit(GetAdtField(childTemp)(sourceTemp)(index)(tagless))
                    |> emitChildDeepCopy(childTemp)(plan) with
                        | (copied, copiedChild) ->
                            copied
                            |> emit(SetAdtField(resultTemp)(index)(copiedChild)(tagless))
                            |> emitConstructorChildCopies(sourceTemp)(resultTemp)(tagless)(rest)
// Stage 0's `EmitRuntimeManagedTcoAdtDeepCopy` for a type with several constructors: the tag
// selects the constructor walk, each branch storing its copy into a shared slot.
and emitAdtSwitchDeepCopy (sourceTemp: Int) (constructorPlans: List((Int, Int, Bool, List((Int, ArgumentCopyPlan))))) (state: CoreLoweringState) = emitAdtSwitchDeepCopyWith("rc_normalize_adt")("rc_normalize_adt_end")(sourceTemp)(constructorPlans)(state)
and emitAdtSwitchDeepCopyWith (caseLabel: Str) (endLabelName: Str) (sourceTemp: Int) (constructorPlans: List((Int, Int, Bool, List((Int, ArgumentCopyPlan))))) (state: CoreLoweringState) =
    match freshLocal(state) with
        | FreshLocal { state = slotState, local = resultSlot } ->
            match freshTemp(slotState) with
                | FreshTemp { state = tagState, temp = tagTemp } ->
                    match tagState
                    |> emit(GetAdtTag(tagTemp)(sourceTemp))
                    |> switchCasesOf(caseLabel)(constructorPlans) with
                        | (casesState, cases) ->
                            match freshLabel(endLabelName)(casesState) with
                                | FreshLabel { state = endState, label = endLabel } ->
                                    match endState
                                    |> emit(cases
                                    |> firstSwitchLabel
                                    |> SwitchTag(tagTemp)(cases))
                                    |> emitSwitchBranches(sourceTemp)(resultSlot)(endLabel)(cases)(constructorPlans)
                                    |> emit(Label(endLabel))
                                    |> freshTemp with
                                        | FreshTemp { state = resultState, temp = resultTemp } ->
                                            resultState
                                            |> emit(LoadLocal(resultTemp)(resultSlot))
                                            |> markRuntimeTemp(resultTemp)(RuntimeNewlyProduced)
                                            |> (given (loaded) -> (loaded, resultTemp))
and switchCasesOf (caseLabel: Str) (constructorPlans: List((Int, Int, Bool, List((Int, ArgumentCopyPlan))))) (state: CoreLoweringState) =
    match constructorPlans with
        | [] -> (state, [])
        | (tag, _sizeBytes, _tagless, _children) :: rest ->
            match freshLabel(caseLabel)(state) with
                | FreshLabel { state = labelState, label = label } ->
                    match switchCasesOf(caseLabel)(rest)(labelState) with
                        | (casesState, cases) -> (casesState, IrSwitchCase(tag = tag, label = label) :: cases)
and emitSwitchBranches (sourceTemp: Int) (resultSlot: Int) (endLabel: Str) (cases: List(IrSwitchCase)) (constructorPlans: List((Int, Int, Bool, List((Int, ArgumentCopyPlan))))) (state: CoreLoweringState) =
    match (cases, constructorPlans) with
        | (IrSwitchCase { label = label } :: caseRest, constructorPlan :: planRest) ->
            match state
            |> emit(Label(label))
            |> emitConstructorDeepCopy(sourceTemp)(constructorPlan) with
                | (copied, branchTemp) ->
                    copied
                    |> emit(StoreLocal(resultSlot)(branchTemp))
                    |> emit(Jump(endLabel))
                    |> emitSwitchBranches(sourceTemp)(resultSlot)(endLabel)(caseRest)(planRest)
        | _ -> state
// Stage 0's `EmitRuntimeManagedAdtNormalizerCall`: the copy of a named type only the normalization
// helper expresses is a call to that helper, which answers an owned reference-counted value.
and emitNormalizerCall (sourceTemp: Int) (named: SemanticType) (state: CoreLoweringState) =
    match synthesizeAdtNormalizer(named)(state) with
        | (synthesized, label) ->
            match freshTemp(synthesized) with
                | FreshTemp { state = environmentState, temp = environmentTemp } ->
                    match environmentState
                    |> emit(LoadConstInt(environmentTemp)(0))
                    |> freshTemp with
                        | FreshTemp { state = callState, temp = resultTemp } ->
                            callState
                            |> emit(CallKnown(resultTemp)(label)(environmentTemp)(sourceTemp)(-1)(false))
                            |> markRuntimeTemp(resultTemp)(RuntimeNewlyProduced)
                            |> (given (called) -> (called, resultTemp))
// Stage 0's `SynthesizeRuntimeManagedAdtNormalizer`: one helper per pretty-printed type, its label
// registered before its body so a field of the same type calls it. The body is built on a scratch
// frame that keeps the label counter and takes a lambda id like any lifted function; only the
// function, the counters and the program-wide caches leave the frame, never its temps' facts.
and synthesizeAdtNormalizer (named: SemanticType) (state: CoreLoweringState) =
    (let key = formatSemanticType(named)
    in
        match state
        |> normalizerLabelsOf
        |> lookupLabel(key) with
            | Some(label) -> (state, label)
            | None ->
                let label = "__rcnorm_" + Ashes.Text.fromInt(state.nextLambdaId)
                in
                    let registered =
                        withStateDropperLabels(state
                        |> stateDropperLabels
                        |> withNormalizerLabel(key)(label))((state with nextLambdaId = state.nextLambdaId + 1))
                    in
                        match freshLocal(withStateRetiredLocals([])((registered with reversedInstructions = [], nextTemp = 0, nextLocal = 0))) with
                            | FreshLocal { state = environmentSlotState } ->
                                match freshLocal(environmentSlotState) with
                                    | FreshLocal { state = argumentSlotState, local = argumentSlot } ->
                                        match freshTemp(argumentSlotState) with
                                            | FreshTemp { state = sourceState, temp = sourceTemp } ->
                                                match sourceState
                                                |> emit(LoadLocal(sourceTemp)(argumentSlot))
                                                |> emitReferenceOrCopy(sourceTemp)(emitNormalizerAdtCopy(sourceTemp)(named)) with
                                                    | (copied, resultTemp) ->
                                                        match emit(Return(resultTemp))(copied) with
                                                            | CoreLoweringState { reversedInstructions = instructions, nextTemp = tempCount, nextLocal = localCount, functions = functions, nextLambdaId = nextLambdaId, nextLabelId = nextLabelId, programState = programState } ->
                                                                ((registered with functions = IrFunction(
                                                                    label = label,
                                                                    instructions = reverse(instructions),
                                                                    localCount = localCount,
                                                                    tempCount = tempCount,
                                                                    hasEnvAndArgParams = true,
                                                                    coroutine = None,
                                                                    localNames = [],
                                                                    localTypes = [],
                                                                    origin = key
                                                                    |> createAdtNormalizerOrigin(label)
                                                                    |> Some,
                                                                    lifetimesPlaced = false
                                                                ) :: functions, nextLambdaId = nextLambdaId, nextLabelId = nextLabelId, programState = programState), label))
// Stage 0's `EmitRuntimeManagedAdtCopy`: the cell of the live constructor copied out whole, each of
// its heap fields normalized in turn; a type with several constructors selects the walk by tag.
and emitNormalizerAdtCopy (sourceTemp: Int) (named: SemanticType) (state: CoreLoweringState) =
    match named with
        | SemNamed(_symbolId, name, _arguments) ->
            match constructorCopyPlansOfWith(true)(state |> stateConstructorsOfType(name))(state
            |> heapChildrenOfNamed(named)
            |> projectHeapCopyPlanChildren)(state) with
                | Some(single :: []) -> emitConstructorDeepCopy(sourceTemp)(single)(state)
                | Some(_first :: _rest as plans) -> emitAdtSwitchDeepCopyWith("rcnorm_ctor")("rcnorm_end")(sourceTemp)(plans)(state)
                | _ -> (state, sourceTemp)
        | _ -> (state, sourceTemp)

// Stage 0's `EmitRuntimeManagedTcoParamCopy`: the copy of a borrowed argument. A string, a
// same-arity scalar-field ADT, and a list copy into the temp allocated here; a tuple and a
// runtime-managed ADT walk their children through the deep copy on temps of its own.
let emitArgumentCopyByCopy (sourceTemp: Int) (plan: ArgumentCopyPlan) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = allocated, temp = normalizedTemp } ->
            match plan with
                | ListHeadArgumentCopy(headCopy) ->
                    (emit(CopyOutList(normalizedTemp)(sourceTemp)(headCopy)(true)(RcNormalization))(allocated), normalizedTemp)
                // The whole argument was already tested, so its spine walk is the plain one.
                | ListDeepArgumentCopy(elementPlan) -> emitListDeepCopy(sourceTemp)(elementPlan)(allocated)
                | TupleArgumentCopy(_elements) -> emitArgumentDeepCopy(sourceTemp)(plan)(allocated)
                | ConstructorArgumentCopy(_constructorPlan) -> emitArgumentDeepCopy(sourceTemp)(plan)(allocated)
                | SwitchArgumentCopy(_constructorPlans) -> emitArgumentDeepCopy(sourceTemp)(plan)(allocated)
                | NormalizerArgumentCopy(_named) -> emitArgumentDeepCopy(sourceTemp)(plan)(allocated)
                | LeafArgumentCopy(sizeBytes) ->
                    (emit(CopyOutArena(normalizedTemp)(sourceTemp)(sizeBytes)(true)(RcNormalization)(None))(allocated), normalizedTemp)
                | ShallowAdtArgumentCopy(sizeBytes) ->
                    (emit(CopyOutArena(normalizedTemp)(sourceTemp)(sizeBytes)(true)(RcNormalization)(None))(allocated), normalizedTemp)
                | ScalarArgumentCopy -> (allocated, sourceTemp)

// The argument an entry normalization owns: the copy above, or a reference to the value the
// caller handed over when the runtime answers that it is already reference-counted. A scalar is
// the value itself and has no representation to ask about.
let emitArgumentCopy (sourceTemp: Int) (plan: ArgumentCopyPlan) (state: CoreLoweringState) =
    match (canTestRepresentation(state), plan) with
        | (false, _plan) -> emitArgumentCopyByCopy(sourceTemp)(plan)(state)
        | (_tests, ScalarArgumentCopy) -> emitArgumentCopyByCopy(sourceTemp)(plan)(state)
        | (_tests, _plan) ->
            emitReferenceOrCopy(sourceTemp)(emitArgumentCopyByCopy(sourceTemp)(plan))(state)

// Stage 0's `EmitRuntimeManagedTcoDeepCopy` of a whole value: a list copied cell by cell is
// tested again inside the first test and shares the reference-counted suffix it reaches, and the
// result temp stage 0 reserves before that walk takes over is reserved here too.
let emitGuardedDeepCopy (sourceTemp: Int) (plan: ArgumentCopyPlan) (state: CoreLoweringState) =
    match (canTestRepresentation(state), plan) with
        | (true, ListDeepArgumentCopy(elementPlan)) ->
            emitReferenceOrCopy(sourceTemp)(given (copying: CoreLoweringState) ->
                match freshTemp(copying) with
                    | FreshTemp { state = reserved } -> emitGuardedListDeepCopy(sourceTemp)(elementPlan)(reserved))(state)
        | (_tests, ScalarArgumentCopy) -> (state, sourceTemp)
        | (true, _plan) ->
            emitReferenceOrCopy(sourceTemp)(emitArgumentDeepCopy(sourceTemp)(plan))(state)
        | (false, _plan) -> emitArgumentDeepCopy(sourceTemp)(plan)(state)

// Stage 0's `EmitOwnedResultOrCopy`: a value the arm built itself is taken as it is when its cell
// turns out reference-counted, the reference being the arm's own, and copied when the cell is
// still in the arena.
let emitOwnedResultOrCopy (sourceTemp: Int) (plan: ArgumentCopyPlan) (state: CoreLoweringState) =
    match freshLocal(state) with
        | FreshLocal { state = slotted, local = resultSlot } ->
            match slotted
            |> emit(StoreLocal(resultSlot)(sourceTemp))
            |> freshTemp with
                | FreshTemp { state = tested, temp = referenceCountedTemp } ->
                    match tested
                    |> emit(IsReferenceCounted(referenceCountedTemp)(sourceTemp))
                    |> freshLabel("general_rc_result_arena_copy") with
                        | FreshLabel { state = copyLabelled, label = copyLabel } ->
                            match freshLabel("general_rc_result_already_owned")(copyLabelled) with
                                | FreshLabel { state = ownedLabelled, label = ownedLabel } ->
                                    match ownedLabelled
                                    |> emit(JumpIfFalse(referenceCountedTemp)(copyLabel))
                                    |> emit(Jump(ownedLabel))
                                    |> emit(Label(copyLabel))
                                    |> emitGuardedDeepCopy(sourceTemp)(plan) with
                                        | (copied, copyTemp) ->
                                            match copied
                                            |> emit(StoreLocal(resultSlot)(copyTemp))
                                            |> emit(Label(ownedLabel))
                                            |> freshTemp with
                                                | FreshTemp { state = resulted, temp = resultTemp } ->
                                                    (emit(LoadLocal(resultTemp)(resultSlot))(resulted), resultTemp)
