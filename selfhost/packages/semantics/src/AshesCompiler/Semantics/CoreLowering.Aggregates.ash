// Tuples, list literals and cons cells: element retains, copied-cell markers, loop exit tails and
// reused constructor cells. A part of `AshesCompiler.Semantics.CoreLowering`.

import Ashes.Collection.List.append
import Ashes.Collection.List.length
import Ashes.Collection.List.reverse
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Semantics.AggregateOwnership
import AshesCompiler.Semantics.ConstructionRetains
import AshesCompiler.Semantics.HeapLayoutClassification
import AshesCompiler.Semantics.IrControlFlowGraph.containsInt
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.OwnershipSummary
import AshesCompiler.Semantics.ToSpaceCopiers
import AshesCompiler.Semantics.TcoRuntimeManagedParams
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.ReuseSpecialization
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.CoreLowering.LoweringState
import AshesCompiler.Semantics.CoreLowering.Emit
import AshesCompiler.Semantics.CoreLowering.Owners
import AshesCompiler.Semantics.CoreLowering.CopyPlans
import AshesCompiler.Semantics.CoreLowering.TcoReset
import AshesCompiler.Semantics.CoreLowering.ResultHandOver
import AshesCompiler.Semantics.CoreLowering.Bindings
import AshesCompiler.Semantics.CoreLowering.LoopPlacement
import AshesCompiler.Semantics.CoreLowering.LoopFrames
import AshesCompiler.Semantics.CoreLowering.CallArguments
import AshesCompiler.Semantics.CoreLowering.CallResults
import AshesCompiler.Semantics.CoreLowering.Matches
import AshesCompiler.Semantics.CoreLowering.RecursiveGroups
export (
    value recordArenaCellTransferRetain,
    value neutralizeElementDuplicate,
    value isPromotableCellMarker,
    value lowerTuple,
    value allocateListCell,
    value listElementRequest,
    value requestsRuntimeList,
    value retainListElement,
    value normalizePatternOwnerHead,
    value cellIsRuntimeManaged,
    value backEdgeCopiesListSuccessor,
    value backEdgeCopiesLiteralSuccessor,
    value finishConsTail,
    value expectedListElementType,
    value listTransfers,
    value normalizeRuntimeManagedConsHead,
    value listCellReuseTokenAvailable,
    value ownBorrowedHeadForProducerCell,
    value isProducerCellHeadType,
    value lowerCons,
    value lowerListLiteral,
    value allCopyTypeElements,
    value specializationFieldIsAccumulatorSelf,
    value finishConstructorAllocation,
)

// Stage 0's `IsRuntimeManageableListElement`: a scalar, or a runtime-managed heap value.
let isRuntimeManageableListElement (child: Expr) (temp: Int) (semanticType: SemanticType) (state: CoreLoweringState) = resultSurvivesReset(semanticType)(state) || childIsRuntimeManaged(child)(temp)(state) && isRuntimeManageableHeapChild(semanticType)(false)(state)

let recursive allRuntimeManageableTupleElements (elements: List(Expr)) (temps: List(Int)) (semanticTypes: List(SemanticType)) (state: CoreLoweringState) =
    match (elements, temps, semanticTypes) with
        | ([], [], []) -> true
        | (element :: restElements, temp :: restTemps, semanticType :: restTypes) -> isRuntimeManageableTupleElement(element)(temp)(semanticType)(state) && allRuntimeManageableTupleElements(restElements)(restTemps)(restTypes)(state)
        | _ -> false

// Stage 0's `IsFreshNullaryConstructor`: a tuple element written as a nullary constructor's name.
let isFreshNullaryConstructor (element: Expr) (state: CoreLoweringState) =
    match unspanArgument(element) with
        | ExprVar(name) -> isNullaryConstructorPatternName(name)(state)
        | _ -> false

// A read of a pattern owner needing a protective reference, or that read's identity duplicate, not
// copied onto the heap: stage 0 tracks such an owner with no runtime representation until the
// loop's finalize places it (`TrackOwnedValue` from the pattern binding sites, runtimeManaged
// false), so the element is not `RuntimeRc` when `HoldsReferenceCountedElement` asks.
let unplacedPatternOwnerElement (element: Expr) (temp: Int) (state: CoreLoweringState) =
    isPatternOwnerRead(element)(state) && !containsInt(temp)(statePatternOwnerCopyTemps(state))

// Stage 0's `HoldsReferenceCountedElement`. A tuple's store takes a reference to a
// reference-counted element, and an arena tuple can release nothing, so that reference would
// outlive the tuple. On the reference-counted heap the tuple owns the element and whoever matches
// on it releases both. A nullary constructor is an immortal cell an arena tuple strands nothing
// of, so it does not count. Neither does a read of a list loop parameter a sibling keeps in the
// arena, in the tuple literal a match is on, which no consumer keeps: stage 0 does not
// read such a parameter as reference-counted (`IsRuntimeManagedTcoParamSlot`), and the tuple
// takes no reference of it, so on the reference-counted heap it would release, as a counted
// list, a list that may be the arena cells the loop's own back edge built. With `placedOnly` (a
// tuple that is neither a loop successor nor a match scrutinee, and was not asked for on the heap:
// `(name, value)` of pattern parts in a list literal passed to a call), an unplaced pattern owner's
// read does not count either: stage 0 keeps that tuple in the arena and the call's frame releases
// the owner duplicates it holds. A successor's or a spine head's tuple is placed by its request.
let recursive holdsReferenceCountedElement (scrutinee: Bool) (placedOnly: Bool) (elements: List(Expr)) (temps: List(Int)) (semanticTypes: List(SemanticType)) (state: CoreLoweringState) =
    match (elements, temps, semanticTypes) with
        | (element :: restElements, temp :: restTemps, semanticType :: restTypes) -> childIsRuntimeManaged(element)(temp)(state) && (placedOnly == false || unplacedPatternOwnerElement(element)(temp)(state) == false) && resultSurvivesReset(semanticType)(state) == false && isFreshNullaryConstructor(element)(state) == false && (scrutinee == false || readsDemotedLoopList(element)(state) == false) || holdsReferenceCountedElement(scrutinee)(placedOnly)(restElements)(restTemps)(restTypes)(state)
        | _ -> false

let recursive withoutTransferRetain (retained: Int) (retains: List((Int, Int, SemanticType))) =
    match retains with
        | [] -> []
        | (temp, slot, semanticType) :: rest ->
            if temp == retained && slot == -1
            then withoutTransferRetain(retained)(rest)
            else (temp, slot, semanticType) :: withoutTransferRetain(retained)(rest)

// Stage 0's `ForgetCallArgumentArenaRetain`: a collected retain released where it was taken (an
// arena cell that stores a copy of its head instead) leaves the frame, so the call does not release
// it a second time.
let forgetCallArgumentTransferRetain (retained: Int) (state: CoreLoweringState) =
    withCallArgumentRetainFrame(state
    |> stateCallArgumentRecordRetains
    |> withoutTransferRetain(retained))(stateCollectingCallArgumentRetains(state))(state)

// Stage 0's `RecordCallArgumentArenaRetain` for the transfer retain an arena aggregate took of a
// live owner's read: collected while an argument the callee's result may reach is lowered, and
// only when no label was allocated since the argument began, so the release after the call never
// reads a retain one branch did not take. It is released unconditionally, carrying no loop slot.
let recordCallArgumentTransferRetain (original: Int) (retained: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    if retained != original && stateCollectingCallArgumentRetains(state) && state.nextLabelId == stateCallArgumentLabelStart(state)
    then withCallArgumentRetainFrame((retained, -1, semanticType) :: stateCallArgumentRecordRetains(state))(true)(state)
    else state

let newestIsRuntimeRetain (retained: Int) (state: CoreLoweringState) =
    match state.reversedInstructions with
        | IrInstruction { instruction = RcDup(target, _source, true, _mayBeEmpty) } :: _rest -> target == retained
        | _ -> false

// Stage 0's `KeepEscapingArenaCellTailRetainOwned`: the retain an escaping arena cell took of its
// tail, when no call argument frame collects it (the cell is a function's result), goes to an owned
// slot, released once the function's result holds references of its own; the arena cell never
// releases it.
let keepArenaCellTailRetainOwned (original: Int) (retained: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemList(element) ->
            if retained != original && newestIsRuntimeRetain(retained)(state) && !containsUnresolvedLayout(SemList(element))(state)
            then
                match freshLocal(state) with
                    | FreshLocal { state = allocated, local = ownedSlot } ->
                        allocated
                        |> emit(StoreLocal(ownedSlot)(retained))
                        |> registerGeneralRcOwnedSlot(ownedSlot)(SemList(element))
            else state
        | _ -> state

let recordArenaCellTransferRetain (arenaCell: Bool) (original: Int) (retained: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    if arenaCell
    then recordCallArgumentTransferRetain(original)(retained)(semanticType)(state)
    else state

// An escaping arena cell's tail retain: collected for the call the cell is an argument of, or else
// kept in an owned slot.
let recordArenaCellTailRetain (arenaCell: Bool) (original: Int) (retained: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    if arenaCell
    then
        if retained != original && stateCollectingCallArgumentRetains(state) && state.nextLabelId == stateCallArgumentLabelStart(state)
        then recordCallArgumentTransferRetain(original)(retained)(semanticType)(state)
        else keepArenaCellTailRetainOwned(original)(retained)(semanticType)(state)
    else state

// Stage 0's `KeepAliasOwnerRetainOwned`: the reference an escaping arena alias took of an owner it
// borrows is never released by the arena aggregate, and a result normalized onto the
// reference-counted heap retains the owner's value again. It is collected for the call the alias is
// an argument of, or goes to an owned slot released once the function's result holds references of
// its own. The slot is recorded with the alias it keeps the owner alive for: a result reaching the
// alias reaches the slot.
let keepAliasOwnerRetainOwned (ownerTemp: Int) (retained: Int) (semanticType: SemanticType) (aliasTemp: Int) (state: CoreLoweringState) =
    if resultSurvivesReset(semanticType)(state) || containsUnresolvedLayout(semanticType)(state) || !newestIsRuntimeRetain(retained)(state)
    then state
    else
        if stateCollectingCallArgumentRetains(state) && state.nextLabelId == stateCallArgumentLabelStart(state)
        then recordCallArgumentTransferRetain(ownerTemp)(retained)(semanticType)(state)
        else
            match freshLocal(state) with
                | FreshLocal { state = allocated, local = ownedSlot } ->
                    allocated
                    |> emit(StoreLocal(ownedSlot)(retained))
                    |> registerGeneralRcOwnedSlot(ownedSlot)(semanticType)
                    |> (given (registered: CoreLoweringState) -> withStateGeneralRcAliasOwnerSlots((stateActiveFunctionOrigin(registered), ownedSlot, aliasTemp) :: stateGeneralRcAliasOwnerSlots(registered))(registered))

// `retainBorrowedOwners` for an aggregate that stores the alias: each owner's retain is kept by
// `keepAliasOwnerRetainOwned`.
let recursive retainAndKeepBorrowedOwners (owners: List((Int, SemanticType))) (aliasTemp: Int) (state: CoreLoweringState) =
    match owners with
        | [] -> state
        | (slot, ownerType) :: rest ->
            match runtimeOwnerStateOf(slot)(state) with
                | Some(true) ->
                    match freshTemp(state) with
                        | FreshTemp { state = loaded, temp = ownerTemp } ->
                            match freshTemp(loaded) with
                                | FreshTemp { state = allocated, temp = duplicate } ->
                                    allocated
                                    |> emit(LoadLocal(ownerTemp)(slot))
                                    |> emit(ownerType
                                    |> resolveType(allocated)
                                    |> mayBeEmptyList
                                    |> RcDup(duplicate)(ownerTemp)(true))
                                    |> markRuntimeTemp(duplicate)(RuntimeNewlyProduced)
                                    |> keepAliasOwnerRetainOwned(ownerTemp)(duplicate)(ownerType)(aliasTemp)
                                    |> (given (retained: CoreLoweringState) ->
                                        withStateOwnerReleasePlans(retained
                                        |> stateOwnerReleasePlans
                                        |> shareOwnerReleasePlan(slot))(retained))
                                    |> retainAndKeepBorrowedOwners(rest)(aliasTemp)
                | _ -> retainAndKeepBorrowedOwners(rest)(aliasTemp)(state)

let retainAndKeepAliasBorrowedOwners (name: Str) (aliasTemp: Int) (state: CoreLoweringState) =
    match lookupBinding(name)(state.bindings) with
        | Some(CoreBinding { location = CoreLocal(slot) }) ->
            match runtimeOwnerStateOf(slot)(state) with
                | Some(_owned) -> state
                | None ->
                    retainAndKeepBorrowedOwners(state
                    |> stateBorrowedOwners
                    |> borrowedOwnersOfSlot(slot))(aliasTemp)(state)
        | _ -> state

// Whether the newest instruction is the loop-parameter retain marker `retainLoopParameterChild` just
// emitted for this read.
let newestIsParameterRetainMarker (duplicate: Int) (source: Int) (state: CoreLoweringState) =
    match state.reversedInstructions with
        | IrInstruction { instruction = RcDup(target, marked, false, _mayBeEmpty) } :: _rest -> target == duplicate && marked == source
        | _ -> false

// The same collection for the retain marker of a loop parameter's read (or a field read out of one)
// stored into an arena cell: stage 0 retains a parameter already placed on the reference-counted
// heap outright and collects that retain; here the marker is collected with the parameter's slot,
// so its release after the call joins the flag the loop's placement decides, as a record update's
// unchanged field does.
let recordArenaCellParameterRetain (arenaCell: Bool) (element: Expr) (source: Int) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            if arenaCell && temp != source && !resultSurvivesReset(semanticType)(state) && stateCollectingCallArgumentRetains(state) && state.nextLabelId == stateCallArgumentLabelStart(state) && newestIsParameterRetainMarker(temp)(source)(state)
            then
                match loopParameterReadSlot(element)(state) with
                    | Some(slot) -> lowered with state = withCallArgumentRetainFrame((temp, slot, semanticType) :: stateCallArgumentRecordRetains(state))(true)(state)
                    | None -> lowered
            else lowered
        | _ -> lowered

let recursive collectsCallArgumentRetain (temp: Int) (retains: List((Int, Int, SemanticType))) =
    match retains with
        | [] -> false
        | (retained, _slot, _semanticType) :: rest -> retained == temp || collectsCallArgumentRetain(temp)(rest)

// Stage 0's `KeepEscapingArenaCellTailRetainOwned` for a loop parameter's list read as an escaping
// arena cell's tail when no call argument frame collects its retain (the cell is the function's
// result, as `(cell :: toks, notes)` at a loop's exit): stage 0 retains a parameter placed on the
// reference-counted heap outright and keeps that retain in an owned slot, released once the
// function's result holds references of its own. Here the retain is the parameter's marker, which
// the loop's placement turns into a real retain only for a parameter placed on the
// reference-counted heap; the slot's release tests the representation, so a marker left an
// identity releases nothing.
let keepArenaCellParameterRetainOwned (arenaCell: Bool) (source: Int) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            if arenaCell && temp != source && generalRcEnabled(state) && newestIsParameterRetainMarker(temp)(source)(state) && !collectsCallArgumentRetain(temp)(stateCallArgumentRecordRetains(state)) && !containsUnresolvedLayout(semanticType)(state) && (match resolveType(state)(semanticType) with
                | SemList(_element) -> true
                | _ -> false)
            then
                match freshLocal(state) with
                    | FreshLocal { state = allocated, local = ownedSlot } ->
                        lowered with state = allocated |> emit(StoreLocal(ownedSlot)(temp)) |> registerGeneralRcOwnedSlot(ownedSlot)(resolveType(state)(semanticType))
            else lowered
        | _ -> lowered

// Stage 0's `DeferArenaCopyOfUnresolvedParameter`: the retain marker of a loop parameter read whole
// into an arena tuple, and not collected for a release after a call, is recorded so the loop's
// finalize pass replaces it with an arena copy: an arena tuple never releases what it holds. Stage 0
// records only a read whose layout is still unresolved there; this lowering resolves the read's type
// from the expected type first, so the tuple is in the arena with the read resolved.
let recordArenaParameterCopy (arenaCell: Bool) (element: Expr) (source: Int) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            match (unspanArgument(element), loopParameterReadSlot(element)(state)) with
                | (ExprVar(_name), Some(slot)) ->
                    if arenaCell && generalRcEnabled(state) && temp != source && newestIsParameterRetainMarker(temp)(source)(state) && !collectsCallArgumentRetain(temp)(stateCallArgumentRecordRetains(state))
                    then lowered with state = withStateArenaParameterCopies((temp, source, slot, semanticType) :: stateArenaParameterCopies(state))(state)
                    else lowered
                | _ -> lowered
        | _ -> lowered

// Stage 0's `RetainRuntimeManagedAggregateChild` on an already lowered child: the read of a
// binding that still owns its reference is retained, the read of an arena binding that borrows
// owners retains those owners, and any other child is stored as lowered. A pattern owner is not
// (stage 0's `DuplicateRuntimeManagedOwnedValueForTransfer` skips it): its identity marker,
// promoted once its root parameter is placed, is its retain. The owner is read
// whole or a heap-typed field is taken out of it, and both retain alike: the field is a borrow of
// a child the owner releases with itself, so a cell storing it needs a reference of its own.
let retainAggregateChildTemp (child: Expr) (temp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match unspanArgument(child) with
        | ExprVar(name) ->
            match (liveRuntimeOwnerSlot(name)(state), patternOwnerBinding(name)(state)) with
                | (Some(_slot), None) ->
                    match keptSuccessorTransferRetain(temp)(semanticType)(state) with
                        | LoweredCoreValue { state = retained, temp = duplicate } -> (retained, duplicate)
                | _ -> (retainAndKeepAliasBorrowedOwners(name)(temp)(state), temp)
        | ExprQualifiedVar(owner, _field) ->
            if resultSurvivesReset(semanticType)(state)
            then (state, temp)
            else
                match (liveRuntimeOwnerSlot(owner)(state), patternOwnerBinding(owner)(state)) with
                    | (Some(_slot), None) ->
                        match keptSuccessorTransferRetain(temp)(semanticType)(state) with
                            | LoweredCoreValue { state = retained, temp = duplicate } -> (retained, duplicate)
                    | _ -> (state, temp)
        | _ -> (state, temp)

let firstDemoted (demoted: List(Bool)) =
    match demoted with
        | first :: _rest -> first
        | [] -> false

let restDemoted (demoted: List(Bool)) =
    match demoted with
        | _first :: rest -> rest
        | [] -> []

// A reference-counted cell releases every child with itself, so a binding read it stores that no
// rule above retained (a pattern binding that only borrows an arena-placed loop parameter's
// child) takes a retain when it is reference-counted; an arena value is stored as it is.
let retainUnownedRuntimeCellChild (arenaCell: Bool) (child: Expr) (original: Int) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            if arenaCell || temp != original || resultSurvivesReset(semanticType)(state) || isRuntimeTemp(temp)(state)
            then lowered
            else
                match unspanArgument(child) with
                    | ExprVar(name) ->
                        if state
                        |> patternOwnerBinding(name)
                        |> patternOwnerFlag
                        then lowered
                        else
                            match freshTempRun(2)(state) with
                                | FreshTemp { state = allocated, temp = countedTemp } ->
                                    match freshLabel("rc_cell_child_retain_skip")(allocated) with
                                        | FreshLabel { state = labelled, label = skipLabel } ->
                                            labelled
                                            |> emit(IsReferenceCounted(countedTemp)(temp))
                                            |> emit(JumpIfFalse(countedTemp)(skipLabel))
                                            |> emit(RcDup(countedTemp + 1)(temp)(true)(false))
                                            |> emit(Label(skipLabel))
                                            |> success(temp)(semanticType)
                    | _ -> lowered
        | _ -> lowered

// Stage 0's `RetainRuntimeManagedTupleChildren`: a tuple element read from a live owner or a
// loop parameter is retained for the cell, except one the cell holds an arena copy of.
let recursive retainAggregateChildTemps (elements: List(Expr)) (temps: List(Int)) (semanticTypes: List(SemanticType)) (demoted: List(Bool)) (arenaCell: Bool) (state: CoreLoweringState) (reversed: List(Int)) =
    match (elements, temps, semanticTypes) with
        | (element :: restElements, temp :: restTemps, semanticType :: restTypes) ->
            if firstDemoted(demoted)
            then
                retainAggregateChildTemps(restElements)(restTemps)(restTypes)(restDemoted(demoted))(arenaCell)(state)(temp :: reversed)
            else
                match retainAggregateChildTemp(element)(temp)(semanticType)(state) with
                    | (retained, retainedTemp) ->
                        match retained
                        |> recordArenaCellTransferRetain(arenaCell)(temp)(retainedTemp)(semanticType)
                        |> success(retainedTemp)(semanticType)
                        |> retainLoopParameterChild(element)(temp)
                        |> recordArenaCellParameterRetain(arenaCell)(element)(temp)
                        |> recordArenaParameterCopy(arenaCell)(element)(temp)
                        |> retainUnownedRuntimeCellChild(arenaCell)(element)(temp) with
                            | LoweredCoreValue { state = marked, temp = markedTemp } ->
                                retainAggregateChildTemps(restElements)(restTemps)(restTypes)(restDemoted(demoted))(arenaCell)(marked)(markedTemp :: reversed)
        | _ -> (state, reverse(reversed))

// The request each tuple element is lowered under, stage 0's `LowerTupleElement`: the tuple's
// own flags stay, and a runtime tuple asks a fresh string producer for a runtime string, a fresh
// list construction for a runtime list, and a nested tuple literal, constructor application, or
// record literal for its own runtime representation.
let tupleElementRequest (request: ConsumerRequest) (runtimeTuple: Bool) (transfers: Bool) (element: Expr) (state: CoreLoweringState) =
    match request with
        | ConsumerRequest { runtimeString = parentString, runtimeList = parentList, runtimeAdt = parentAdt, runtimeRecord = parentRecord } -> emptyConsumerRequest with runtimeString = parentString || runtimeTuple && isFreshStringChild(element)(state), runtimeList = parentList || runtimeTuple && isFreshListConstruction(element), runtimeTuple = runtimeTuple, runtimeAdt = parentAdt || runtimeTuple && isConstructorExpression(element)(state), runtimeRecord = parentRecord || runtimeTuple && isRecordLiteral(element), transfersRuntimeManagedChildren = transfers

// The element types the tuple's expected type asks for, or none when it asks for no tuple of this
// size.
let expectedTupleElements (request: ConsumerRequest) (count: Int) (state: CoreLoweringState) =
    match request.expectedType with
        | Some(expected) ->
            match resolveType(state)(expected) with
                | SemTuple(expectedElements) ->
                    if coreListLength(expectedElements) == count
                    then expectedElements
                    else []
                | _ -> []
        | None -> []

// Stage 0's `UnifyTupleElementWithExpected`: an element meets its own part of the tuple's expected
// type as soon as it is lowered, so a constructor whose type arguments only the expected type
// fixes (a None beside a sibling arm's Some) has a resolved layout when the tuple is placed. A
// mismatch is left to the unification of the whole tuple to report.
let unifyTupleElementWithExpected (expectedElements: List(SemanticType)) (semanticType: SemanticType) (state: CoreLoweringState) =
    match expectedElements with
        | expectedElement :: _rest ->
            match bindType(expectedElement)(semanticType)(state) with
                | (typedState, None) -> typedState
                | (_failedState, Some(_error)) -> state
        | [] -> state

// The whole tuple meets its expected type once it is built, which reports any mismatch the
// elements' own unifications left unreported.
let unifyTupleWithExpected (request: ConsumerRequest) (lowered: LoweredCoreValue) =
    match request.expectedType with
        | Some(expectedType) ->
            lowered
            |> unifyExpectedResult(expectedType)
            |> locateLoweredMismatch(request.argumentSite)
        | None -> lowered

let remainingExpectedElements (expectedElements: List(SemanticType)) =
    match expectedElements with
        | _first :: rest -> rest
        | [] -> []

// Stage 0's `MaterializeEscapingStringTupleElement`: inside a loop, a bare string variable placed
// into a reference-counted tuple is copied into an owned string the tuple stores, unless it reads a
// binding that owns a runtime-managed reference itself; a match-bound string of the loop's
// parameter would otherwise be retained into an arena tuple that the cell then copies again.
let materializeEscapingStringTupleElement (runtimeTuple: Bool) (element: Expr) (lowered: LoweredCoreValue) =
    match (lowered, unspanArgument(element)) with
        | (LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None }, ExprVar(name)) ->
            match (runtimeTuple, stateTcoLoop(state), resolveType(state)(semanticType), liveRuntimeOwnerSlot(name)(state), patternOwnerBinding(name)(state)) with
                | (true, Some(_loop), SemString, Some(_slot), None) -> lowered
                | (true, Some(_loop), SemString, _ownerSlot, _patternOwner) ->
                    match freshTemp(state) with
                        | FreshTemp { state = allocated, temp = copyTemp } ->
                            allocated
                            |> emit(CopyOutArena(copyTemp)(temp)(-1)(true)(RcNormalization)(None))
                            |> markRuntimeTemp(copyTemp)(RuntimeNewlyProduced)
                            |> success(copyTemp)(semanticType)
                | _ -> lowered
        | _ -> lowered

let recursive lowerTupleElementsInto (request: ConsumerRequest) (runtimeTuple: Bool) (transfers: Bool) (expectedElements: List(SemanticType)) elements lower reversedTemps reversedTypes state =
    match elements with
        | [] -> finishCoreValues(state)(reversedTemps)(reversedTypes)
        | element :: rest ->
            match state
            |> withConsumerRequest(tupleElementRequest(request)(runtimeTuple)(transfers)(element)(state))
            |> lower(element)
            |> materializeEscapingStringTupleElement(runtimeTuple)(element)
            |> duplicatePatternOwnerChild(element) with
                | LoweredCoreValue { state = failedState, error = Some(error) } -> failedCoreValues(failedState)(error)
                | LoweredCoreValue { state = nextState, temp = temp, semanticType = semanticType, error = None } ->
                    lowerTupleElementsInto(
                        request,
                        runtimeTuple,
                        transfers,
                        remainingExpectedElements(expectedElements),
                        rest,
                        lower,
                        temp :: reversedTemps,
                        semanticType :: reversedTypes,
                        unifyTupleElementWithExpected(expectedElements)(semanticType)(nextState)
                    )

let retainTupleChildren (elements: List(Expr)) (temps: List(Int)) (semanticTypes: List(SemanticType)) (demoted: List(Bool)) (runtimeManaged: Bool) (transfers: Bool) (state: CoreLoweringState) =
    if runtimeManaged || transfers
    then retainAggregateChildTemps(elements)(temps)(semanticTypes)(demoted)(!runtimeManaged)(state)([])
    else (state, temps)

let markAggregateRuntimeManaged (temp: Int) (runtimeManaged: Bool) (state: CoreLoweringState) =
    if runtimeManaged
    then markRuntimeTemp(temp)(RuntimeNewlyProduced)(state)
    else state

// The tuple's cell: on the reference-counted heap when the tuple was requested there and every
// element is runtime-manageable (stage 0's `LowerTupleLit`), otherwise in the arena; a runtime
// tuple owns its children and an escaping arena tuple carries them out of the scopes that own
// them, so both retain a child read from a live owner (`RetainRuntimeManagedTupleChildren`),
// after the tuple temp is allocated and before the cell is.
// Stage 0's `IsNormalizableSiblingElement`: an arena element of a tuple placed on the
// reference-counted heap that a copy turns into an owned value, so the tuple can own every element
// instead of stranding the reference-counted ones in the arena.
let isNormalizableSiblingElement (semanticType: SemanticType) (state: CoreLoweringState) =
    containsUnresolvedLayout(semanticType)(state) == false && (match ownedResultPlanOf(semanticType)(state) with
        | Some(_plan) -> true
        | None -> false)

let recursive allTupleElementsPlaceable (copiesSiblings: Bool) (elements: List(Expr)) (temps: List(Int)) (semanticTypes: List(SemanticType)) (state: CoreLoweringState) =
    match (elements, temps, semanticTypes) with
        | ([], [], []) -> true
        | (element :: restElements, temp :: restTemps, semanticType :: restTypes) -> (isRuntimeManageableTupleElement(element)(temp)(semanticType)(state) || isGeneralRcValueType(semanticType)(state) || copiesSiblings && isNormalizableSiblingElement(semanticType)(state)) && allTupleElementsPlaceable(copiesSiblings)(restElements)(restTemps)(restTypes)(state)
        | _ -> false

// Whether the tuple's element is made its own on the reference-counted heap: one of the contract's
// types, or an arena sibling of a reference-counted element.
let normalizesTupleElement (copiesSiblings: Bool) (semanticType: SemanticType) (state: CoreLoweringState) =
    isGeneralRcValueType(semanticType)(state) || copiesSiblings && (match ownedResultPlanOf(semanticType)(state) with
        | Some(_plan) -> true
        | None -> false)

// The most recent identity duplicate a pattern-owner read took of a tuple element, made a plain
// borrow: the cell holds a copy instead, and a marker left behind could still be promoted to a
// retain nothing releases. Yields the value the element was read as, or none when no marker made it.
let recursive findElementDuplicate (temp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> None
        | IrInstruction { instruction = RcDup(target, source, runtimeManaged, _mayBeEmpty) } :: rest ->
            match (target == temp, runtimeManaged) with
                | (true, false) -> Some(source)
                | (true, true) -> None
                | _ -> findElementDuplicate(temp)(rest)
        | _instruction :: rest -> findElementDuplicate(temp)(rest)

let recursive borrowElementDuplicate (temp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> []
        | (IrInstruction { instruction = RcDup(target, source, _runtimeManaged, _mayBeEmpty) } as instruction) :: rest ->
            if target == temp
            then (instruction with instruction = Borrow(target)(source)) :: rest
            else instruction :: borrowElementDuplicate(temp)(rest)
        | instruction :: rest -> instruction :: borrowElementDuplicate(temp)(rest)

let neutralizeElementDuplicate (temp: Int) (state: CoreLoweringState) =
    match findElementDuplicate(temp)(state.reversedInstructions) with
        | Some(source) -> ((state with reversedInstructions = borrowElementDuplicate(temp)(state.reversedInstructions)), source)
        | None -> (state, temp)

let recursive isPatternOwnerSiteSlot (slot: Int) (sites: List(PatternOwnerSite)) =
    match sites with
        | [] -> false
        | site :: rest -> site.siteSlot == slot || isPatternOwnerSiteSlot(slot)(rest)

let recursive isTcoParameterRetainSite (temp: Int) (sites: List((Int, Int, SemanticType))) =
    match sites with
        | [] -> false
        | (duplicate, _slot, _semanticType) :: rest -> duplicate == temp || isTcoParameterRetainSite(temp)(rest)

let recursive isArenaParameterCopy (temp: Int) (copies: List((Int, Int, Int, SemanticType))) =
    match copies with
        | [] -> false
        | (duplicate, _source, _slot, _semanticType) :: rest -> duplicate == temp || isArenaParameterCopy(temp)(rest)

// Whether the identity duplicate `temp` is one finalization promotes to a real retain: a read of a
// pattern owner bound out of a loop parameter, or a loop parameter's retain marker, that no release
// after a call or arena copy was recorded for.
let isPromotableCellMarker (temp: Int) (state: CoreLoweringState) =
    match findElementDuplicate(temp)(state.reversedInstructions) with
        | None -> false
        | Some(source) ->
            (isPatternOwnerSiteSlot(loadedSlotThroughBorrows(source)(state.reversedInstructions))(statePatternOwnerSites(state)) || isTcoParameterRetainSite(temp)(stateTcoParameterRetainSites(state))) && !collectsCallArgumentRetain(temp)(stateCallArgumentRecordRetains(state)) && !isArenaParameterCopy(temp)(stateArenaParameterCopies(state))

// Stage 0's `EmitRuntimeManagedTcoTupleSuccessorCopy`: a literal nested in a tuple successor (a
// cons, a record, a record update) that stays in the arena is copied into the tuple with
// references of its own, before the back edge releases the old parameters, and the arena never
// releases what the cell held. The duplicates its construction took of pattern owners and loop
// parameters, and those of the literals nested in it, become borrows; a duplicate with another
// reader is left alone.
let recursive withdrawCopiedCellMarkers (pending: List(Int)) (visited: List(Int)) (state: CoreLoweringState) =
    match pending with
        | [] -> state
        | temp :: rest ->
            if containsInt(temp)(visited)
            then withdrawCopiedCellMarkers(rest)(visited)(state)
            else
                if isArenaCell(temp)(state.reversedInstructions)
                then
                    withdrawCopiedCellMarkers(append(storedValuesOf(temp)(state.reversedInstructions)([]))(rest))(temp :: visited)(state)
                else
                    if isPromotableCellMarker(temp)(state) && countTempUses(temp)(state.reversedInstructions)(0) == 1
                    then withdrawCopiedCellMarkers(rest)(temp :: visited)((state with reversedInstructions = borrowElementDuplicate(temp)(state.reversedInstructions)))
                    else withdrawCopiedCellMarkers(rest)(temp :: visited)(state)

let isRecordUpdateExpression (expression: Expr) =
    match unspanArgument(expression) with
        | ExprRecordUpdate(_target, _fields) -> true
        | _ -> false

let withdrawSuccessorLiteralMarkers (successor: Bool) (element: Expr) (temp: Int) (state: CoreLoweringState) =
    if successor && (isFreshAggregateLiteral(element)(state) || isRecordUpdateExpression(element)) && isArenaCell(temp)(state.reversedInstructions)
    then withdrawCopiedCellMarkers([temp])([])(state)
    else state

// Stage 0's `NormalizeGeneralRcTupleElements`: each element of the contract's types, and each arena
// sibling of a reference-counted element, is retained or copied into an owned value, and the tuple
// stores that. The flags mark the elements normalized: each already holds the reference the
// tuple stores, so none is retained again for the cell. `successor` marks a loop's tuple successor.
let recursive normalizeTupleSiblings (successor: Bool) (copiesSiblings: Bool) (elements: List(Expr)) (temps: List(Int)) (semanticTypes: List(SemanticType)) (state: CoreLoweringState) =
    match (elements, temps, semanticTypes) with
        | (element :: restElements, temp :: restTemps, semanticType :: restTypes) ->
            match (isRuntimeManageableTupleElement(element)(temp)(semanticType)(state) || !normalizesTupleElement(copiesSiblings)(semanticType)(state), generalCopyPlanOf(semanticType)(state)) with
                | (false, Some(plan)) ->
                    match state
                    |> withdrawSuccessorLiteralMarkers(successor)(element)(temp)
                    |> emitGuardedDeepCopy(temp)(plan) with
                        | (copied, copyTemp) ->
                            match copied
                            |> markRuntimeTemp(copyTemp)(RuntimeNewlyProduced)
                            |> normalizeTupleSiblings(successor)(copiesSiblings)(restElements)(restTemps)(restTypes) with
                                | (normalized, rest, restFlags) -> (normalized, copyTemp :: rest, true :: restFlags)
                | _ ->
                    match normalizeTupleSiblings(successor)(copiesSiblings)(restElements)(restTemps)(restTypes)(state) with
                        | (normalized, rest, restFlags) -> (normalized, temp :: rest, false :: restFlags)
        | _ -> (state, [], [])

// `runtimeOnly` limits the copies to the elements held as reference-counted values.
let recursive demoteTupleElements (runtimeOnly: Bool) (elements: List(Expr)) (temps: List(Int)) (semanticTypes: List(SemanticType)) (state: CoreLoweringState) =
    match (elements, temps, semanticTypes) with
        | (element :: restElements, temp :: restTemps, semanticType :: restTypes) ->
            if (runtimeOnly == false || childIsRuntimeManaged(element)(temp)(state)) && demotesTupleElement(semanticType)(state)
            then
                match neutralizeElementDuplicate(temp)(state) with
                    | (neutralized, sourceTemp) ->
                        match emitArenaCopyOfReferenceCounted(sourceTemp)(semanticType)(tempIsNewlyProduced(sourceTemp)(neutralized))(neutralized) with
                            | (copied, copyTemp) ->
                                match demoteTupleElements(runtimeOnly)(restElements)(restTemps)(restTypes)(copied) with
                                    | (demotedState, restCopies, restFlags) -> (demotedState, copyTemp :: restCopies, true :: restFlags)
            else
                match demoteTupleElements(runtimeOnly)(restElements)(restTemps)(restTypes)(state) with
                    | (demotedState, restCopies, restFlags) -> (demotedState, temp :: restCopies, false :: restFlags)
        | _ -> (state, temps, [])

// Stage 0's `DemoteElementsBesideAbstractSibling`: a tuple beside an element of unresolved layout
// (a generic value) stays in the arena, where nothing releases what it holds, so it never holds
// the only reference to a reference-counted value. Such an element is copied into the arena, a
// fresh one released after the copy, and none is retained for the tuple. A tuple kept in the
// arena by a sibling the reference-counted heap cannot take (a borrowed tuple parameter) is in the
// same place beside a reference-counted element: that element alone is copied.
let demoteArenaTupleElements (elements: List(Expr)) (holdsReferenceCounted: Bool) (temps: List(Int)) (semanticTypes: List(SemanticType)) (state: CoreLoweringState) =
    match stateSpecializationFreshInputs(state) with
        | None ->
            if generalRcEnabled(state) && anyUnresolvedLayout(semanticTypes)(state)
            then demoteTupleElements(false)(elements)(temps)(semanticTypes)(state)
            else
                if holdsReferenceCounted && generalRcEnabled(state)
                then demoteTupleElements(true)(elements)(temps)(semanticTypes)(state)
                else (state, temps, [])
        | Some(_inputs) -> (state, temps, [])

let finishPlacedTuple elements (temps: List(Int)) (semanticTypes: List(SemanticType)) (demoted: List(Bool)) (tupleTemp: Int) (runtimeManaged: Bool) (transfers: Bool) (state: CoreLoweringState) =
    match retainTupleChildren(elements)(temps)(semanticTypes)(demoted)(runtimeManaged)(transfers)(state) with
        | (retainedState, storedTemps) ->
            retainedState
            |> emit(Alloc(tupleTemp)(coreListLength(storedTemps) * 8)(runtimeManaged))
            |> emitTupleFields(tupleTemp)(0)(storedTemps)
            |> markAggregateRuntimeManaged(tupleTemp)(runtimeManaged)
            |> success(tupleTemp)(SemTuple(semanticTypes))

// Stage 0's `MaterializeEscapingArenaTupleElements` for the read of the entry-normalized string
// parameter: an escaping tuple that stays in the arena stores an independent copy of the string,
// so the owned parameter is referenced by nothing once the tuple is built and is released behind
// the result (`reachesResultOnlyAsClonedTupleElement`).
let recursive cloneNormalizedStringParameterElements (runtimeTuple: Bool) (elements: List(Expr)) (temps: List(Int)) (state: CoreLoweringState) =
    match (elements, temps) with
        | (element :: restElements, temp :: restTemps) ->
            if runtimeTuple && isNormalizedAlwaysReturnedStringParameterRead(element)(state)
            then
                match freshTemp(state) with
                    | FreshTemp { state = allocated, temp = copyTemp } ->
                        match allocated
                        |> emit(CopyOutArena(copyTemp)(temp)(-1)(false)(IndependentClone)(None))
                        |> withStateNormalizedParameterCloned(true)
                        |> cloneNormalizedStringParameterElements(runtimeTuple)(restElements)(restTemps) with
                            | (cloned, rest) -> (cloned, copyTemp :: rest)
            else
                match cloneNormalizedStringParameterElements(runtimeTuple)(restElements)(restTemps)(state) with
                    | (cloned, rest) -> (cloned, temp :: rest)
        | _ -> (state, temps)

// Whether the back edge copies the tuple successor being lowered: its loop parameter is on the
// reference-counted heap, so the arena tuple is copied with references of its own.
let backEdgeCopiesTupleSuccessor (successor: Bool) (state: CoreLoweringState) =
    match (successor, stateBackEdgeArgumentSlot(state), stateTcoLoop(state), stateTcoLoopFrame(state)) with
        | (true, Some(slot), Some(loop), Some(frame)) -> loopSlotIsRuntimeManaged(slot)(frame)(loop)(state)
        | _ -> false

// An arena tuple successor the back edge copies: the literals nested in it borrow what they
// store, as they do in a reference-counted tuple successor that copies them while it is built.
let recursive withdrawCopiedTupleLiterals (elements: List(Expr)) (temps: List(Int)) (state: CoreLoweringState) =
    match (elements, temps) with
        | (element :: restElements, temp :: restTemps) ->
            state
            |> withdrawSuccessorLiteralMarkers(true)(element)(temp)
            |> withdrawCopiedTupleLiterals(restElements)(restTemps)
        | _ -> state

// Stage 0's `CopyFreshFieldsIntoArenaCell`: without general reference counting (code that may run
// while a handler's one-shot post is pending) an arena constructor cell releases nothing it holds,
// so a newly produced reference-counted string field, or a list field over heads the list copy-out
// reproduces, is stored as an arena copy and the original is released right away, as an arena cons
// cell does with such a head.
let recursive arenaCellFieldTemps (types: List(SemanticType)) (temps: List(Int)) (copied: List(Int)) (state: CoreLoweringState) =
    match (types, temps) with
        | (fieldType :: restTypes, temp :: restTemps) ->
            match (resolveType(state)(fieldType), runtimeTempStateOf(temp)(state)) with
                | (SemString, Some(RuntimeNewlyProduced)) ->
                    match freshTemp(state) with
                        | FreshTemp { state = allocated, temp = copyTemp } ->
                            allocated
                            |> emit(CopyOutArena(copyTemp)(temp)(-1)(false)(IndependentClone)(None))
                            |> releaseConsumedOperand(temp)
                            |> arenaCellFieldTemps(restTypes)(restTemps)(copyTemp :: copied)
                | (SemList(element), Some(RuntimeNewlyProduced)) ->
                    match listHeadCopyKindOf(element)(state) with
                        | Some(headCopy) ->
                            match freshTemp(state) with
                                | FreshTemp { state = allocated, temp = copyTemp } ->
                                    allocated
                                    |> emit(CopyOutList(copyTemp)(temp)(headCopy)(false)(IndependentClone))
                                    |> emitOwnedValueRelease(emit)(temp)(SemList(element))
                                    |> arenaCellFieldTemps(restTypes)(restTemps)(copyTemp :: copied)
                        | None -> arenaCellFieldTemps(restTypes)(restTemps)(temp :: copied)(state)
                | _ -> arenaCellFieldTemps(restTypes)(restTemps)(temp :: copied)(state)
        | _ ->
            (state, append(reverse(copied))(temps))

let arenaCellFields (types: List(SemanticType)) (runtimeManaged: Bool) (temps: List(Int)) (state: CoreLoweringState) =
    if runtimeManaged || generalRcEnabled(state)
    then (state, temps)
    else arenaCellFieldTemps(types)(temps)([])(state)

// Stage 0's `RecordCallArgumentTupleOwnerDuplicates`: an arena tuple built inside a call's argument
// holds the duplicates its pattern owners took, and the arena cell never releases them, so the
// call's frame releases each after the call. Only on the argument's straight-line path, outside a
// loop's successor.
let recursive tupleOwnerDuplicateEntries (temps: List(Int)) (semanticTypes: List(SemanticType)) (slot: Int) (state: CoreLoweringState) (entries: List((Int, Int, SemanticType))) =
    match (temps, semanticTypes) with
        | (temp :: restTemps, semanticType :: restTypes) ->
            if isDuplicateMarker(temp)(state.reversedInstructions)
            then tupleOwnerDuplicateEntries(restTemps)(restTypes)(slot)(state)((temp, slot, semanticType) :: entries)
            else tupleOwnerDuplicateEntries(restTemps)(restTypes)(slot)(state)(entries)
        | _ -> entries

let recordCallArgumentTupleOwnerDuplicates (successor: Bool) (temps: List(Int)) (semanticTypes: List(SemanticType)) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, error = None } ->
            match stateBackEdgeArgumentSlot(state) with
                | Some(_slot) -> lowered
                | None ->
                    if backEdgeCopiesTupleSuccessor(successor)(state) || state.nextLabelId != stateCallArgumentLabelStart(state)
                    then lowered
                    else
                        let slot =
                            if stateCollectingCallArgumentRetains(state)
                            then -2
                            else -3
                        in
                            lowered with state = withCallArgumentRetainFrame(state
                            |> stateCallArgumentRecordRetains
                            |> tupleOwnerDuplicateEntries(temps)(semanticTypes)(slot)(state))(stateCollectingCallArgumentRetains(state))(state)
        | _ -> lowered

let finishTupleLowering (successor: Bool) (scrutinee: Bool) elements (runtimeTuple: Bool) (transfers: Bool) lowered =
    match lowered with
        | LoweredCoreValues { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValues { state = loweredState, temps = freshTemps, semanticTypes = semanticTypes, error = None } ->
            // Stage 0's `CopyFreshElementsIntoArenaTuple`: without general reference counting a fresh
            // reference-counted string element is replaced by an arena copy first, so the tuple stays
            // in the arena its caller takes it as.
            match arenaCellFields(semanticTypes)(runtimeTuple)(freshTemps)(loweredState) with
                | (state, loweredTemps) ->
                    match (freshTemp(state), holdsReferenceCountedElement(scrutinee)(successor == false && scrutinee == false)(elements)(loweredTemps)(semanticTypes)(state)) with
                        | (FreshTemp { state = reservedState, temp = tupleTemp }, holdsReferenceCounted) ->
                            if (runtimeTuple || holdsReferenceCounted) && allTupleElementsPlaceable(holdsReferenceCounted)(elements)(loweredTemps)(semanticTypes)(state)
                            then
                                match normalizeTupleSiblings(successor)(holdsReferenceCounted)(elements)(loweredTemps)(semanticTypes)(reservedState) with
                                    | (normalizedState, temps, normalized) -> finishPlacedTuple(elements)(temps)(semanticTypes)(normalized)(tupleTemp)(true)(transfers)(normalizedState)
                            else
                                match reservedState
                                |> (given (arenaState) ->
                                    if backEdgeCopiesTupleSuccessor(successor)(arenaState)
                                    then withdrawCopiedTupleLiterals(elements)(loweredTemps)(arenaState)
                                    else arenaState)
                                |> demoteArenaTupleElements(elements)(holdsReferenceCounted)(loweredTemps)(semanticTypes) with
                                    | (demotedState, temps, demoted) ->
                                        match cloneNormalizedStringParameterElements(runtimeTuple)(elements)(temps)(demotedState) with
                                            | (clonedState, clonedTemps) ->
                                                match arenaCellFields(semanticTypes)(false)(clonedTemps)(clonedState) with
                                                    | (copiedState, copiedTemps) ->
                                                        copiedState
                                                        |> finishPlacedTuple(elements)(copiedTemps)(semanticTypes)(demoted)(tupleTemp)(false)(transfers)
                                                        |> recordCallArgumentTupleOwnerDuplicates(successor)(clonedTemps)(semanticTypes)

let inLoopTailPosition (request: ConsumerRequest) (state: CoreLoweringState) =
    match (request, stateTcoLoop(state)) with
        | (ConsumerRequest { tailPosition = true }, Some(_loop)) -> true
        | _ -> false

let tupleTransfers (request: ConsumerRequest) (state: CoreLoweringState) =
    match request with
        | ConsumerRequest { runtimeTuple = runtimeTuple, transfersRuntimeManagedChildren = transfersRequested } -> transfersRequested || inLoopTailPosition(request)(state) || runtimeTuple

// A tuple a loop's tail self call passes as its successor.
let isTupleSuccessorRequest (request: ConsumerRequest) (state: CoreLoweringState) =
    match (request.argumentSite, stateTcoLoop(state)) with
        | (Some(_site), Some(_loop)) -> true
        | _ -> false

// A resource stored into a tuple, list, cons cell, or constructor moves into it. The tuple
// carries its children out of the scopes that own them when it is a function result (the
// transfer flag, or the tail position of a loop body) or when it will own them as a runtime
// tuple; the decision is made before the elements are lowered so a nested aggregate retains its
// own children the same way.
let lowerTuple elements lower state =
    match consumerRequestOf(state) with
        | ConsumerRequest { runtimeTuple = runtimeTuple } as request ->
            state
            |> withStateScrutineeTuple(false)
            |> markResourceArgumentsMoved(elements)
            |> lowerTupleElementsInto(request)(runtimeTuple)(tupleTransfers(request)(state))(expectedTupleElements(request)(coreListLength(elements))(state))(elements)(lower)([])([])
            |> finishTupleLowering(isTupleSuccessorRequest(request)(state))(stateScrutineeTuple(state))(elements)(runtimeTuple)(tupleTransfers(request)(state))
            |> unifyTupleWithExpected(request)

// The live arena list-cell token, if one was published by a cons arm of the match this cell is
// rebuilt in. A reference-counted cell never takes one: its token would carry transferred children
// needing the runtime nullness guard only the constructor path emits.
let recursive takeListCellReuseToken (tokens: List(CoreReuseToken)) =
    match tokens with
        | [] -> None
        | (CoreReuseToken { constructorName = candidateName, runtimeManaged = candidateManaged } as token) :: rest ->
            if candidateName == listCellReuseConstructorName && candidateManaged == false
            then Some((token, rest))
            else
                match takeListCellReuseToken(rest) with
                    | Some((found, remaining)) -> Some((found, token :: remaining))
                    | None -> None

let consumeListCellReuseToken (runtimeManaged: Bool) (state: CoreLoweringState) =
    if runtimeManaged
    then None
    else
        match takeListCellReuseToken(state.reuseTokens) with
            | Some((token, remaining)) -> Some((token, (state with reuseTokens = remaining)))
            | None -> None

let allocateListCell headTemp tailTemp elementType (runtimeManaged: Bool) state =
    match consumeListCellReuseToken(runtimeManaged)(state) with
        | Some((token, consumedState)) ->
            match freshTemp(consumedState) with
                | FreshTemp { state = allocatedState, temp = cellTemp } ->
                    allocatedState
                    |> emit(AllocReusing(cellTemp)(0)(2)(token.temp)(false)(true)(false))
                    |> markReuseResultTemp(cellTemp)
                    |> emit(StoreMemOffset(cellTemp)(0)(headTemp))
                    |> emit(StoreMemOffset(cellTemp)(8)(tailTemp))
                    |> markAggregateRuntimeManaged(cellTemp)(false)
                    |> success(cellTemp)(elementType
                    |> resolveType(allocatedState)
                    |> SemList)
        | None ->
            match freshTemp(state) with
                | FreshTemp { state = allocatedState, temp = cellTemp } ->
                    allocatedState
                    |> emit(Alloc(cellTemp)(16)(runtimeManaged))
                    |> emit(StoreMemOffset(cellTemp)(0)(headTemp))
                    |> emit(StoreMemOffset(cellTemp)(8)(tailTemp))
                    |> markAggregateRuntimeManaged(cellTemp)(runtimeManaged)
                    |> success(cellTemp)(elementType
                    |> resolveType(allocatedState)
                    |> SemList)

// The request a list element or cons head is lowered under, stage 0's
// `LowerRuntimeManagedListElement`: the list's own flags stay, the element type is expected, and
// a runtime list asks a fresh string producer for a runtime string and a tuple literal for a
// runtime tuple. An arena list drops the tuple flag it inherits: the tuple enclosing the list
// asked for it, and a reference-counted tuple in an arena cons cell is reclaimed unreleased.
let listElementRequest (request: ConsumerRequest) (elementType: Maybe(SemanticType)) (transfers: Bool) (element: Expr) (state: CoreLoweringState) =
    match request with
        | ConsumerRequest { runtimeString = parentString, runtimeList = runtimeList, runtimeTuple = parentTuple, runtimeAdt = parentAdt, runtimeRecord = parentRecord } -> emptyConsumerRequest with expectedType = elementType, runtimeString = parentString || runtimeList && isFreshStringChild(element)(state), runtimeList = runtimeList, runtimeTuple = runtimeList && (parentTuple || isTupleLiteral(element)), runtimeAdt = parentAdt || runtimeList && isFreshRuntimeManageableAdtExpression(element)(state), runtimeRecord = parentRecord || runtimeList && isRecordLiteral(element) && isFreshRuntimeManageableRecordTree(element)(state), transfersRuntimeManagedChildren = transfers

let requestsRuntimeList (request: ConsumerRequest) =
    match request with
        | ConsumerRequest { runtimeList = runtimeList } -> runtimeList

// A list element or cons head retained when the cell will own it or carry it out of the scope
// that owns it, stage 0's retain in `LowerRuntimeManagedListElement`.
// A loop-parameter head the cons head normalization just copied into a fresh reference-counted value
// for a runtime cell (a parameter read is never newly produced itself): the cell takes the copy
// over as it is, the way stage 0's normalized head takes no duplicate. Retaining it as the
// parameter read it came from would leave a temp that is not runtime-managed, and the cell built
// on it in the arena.
let headIsFreshRuntimeCopy (request: ConsumerRequest) (element: Expr) (temp: Int) (state: CoreLoweringState) =
    requestsRuntimeList(request) && loopParameterReadSlot(element)(state) != None && (match runtimeTempStateOf(temp)(state) with
        | Some(RuntimeNewlyProduced) -> true
        | _ -> false)

let retainListElement (request: ConsumerRequest) (transfers: Bool) (element: Expr) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            if headIsFreshRuntimeCopy(request)(element)(temp)(state)
            then lowered
            else
                if requestsRuntimeList(request) || transfers
                then
                    match retainAggregateChildTemp(element)(temp)(semanticType)(state) with
                        | (retained, retainedTemp) ->
                            retained
                            |> recordArenaCellTransferRetain(!requestsRuntimeList(request))(temp)(retainedTemp)(semanticType)
                            |> success(retainedTemp)(semanticType)
                            |> retainLoopParameterChild(element)(temp)
                            |> recordArenaCellParameterRetain(!requestsRuntimeList(request))(element)(temp)
                else lowered
        | _ -> lowered

// Stage 0's `NormalizePatternOwnerElement`: a string, or a list of scalars or strings, read out
// of a pattern owner is a borrowed read of a value the pattern's root owns, so on its own it never
// counts as a runtime-managed element, and the cell built around it would stay in the arena,
// where a non-tail producer copies its whole partial result out of every level's window. Under a
// runtime list request the head takes an owned reference-counted copy instead, recorded so the
// pattern-owner duplicate is not emitted on top of it.
let patternOwnerHeadCopy (copyTemp: Int) (sourceTemp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemString ->
            None
            |> CopyOutArena(copyTemp)(sourceTemp)(-1)(true)(RcNormalization)
            |> Some
        | SemList(element) ->
            match listHeadCopyKindOf(element)(state) with
                | Some(InlineListHead) ->
                    RcNormalization
                    |> CopyOutList(copyTemp)(sourceTemp)(InlineListHead)(true)
                    |> Some
                | Some(StringListHead) ->
                    RcNormalization
                    |> CopyOutList(copyTemp)(sourceTemp)(StringListHead)(true)
                    |> Some
                | _ -> None
        | _ -> None

let normalizePatternOwnerHead (request: ConsumerRequest) (head: Expr) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            if requestsRuntimeList(request) && isPatternOwnerRead(head)(state) && !isRuntimeTemp(temp)(state)
            then
                match freshTemp(state) with
                    | FreshTemp { state = allocated, temp = copyTemp } ->
                        match patternOwnerHeadCopy(copyTemp)(temp)(semanticType)(allocated) with
                            | Some(copy) ->
                                allocated
                                |> emit(copy)
                                |> markRuntimeTemp(copyTemp)(RuntimeNewlyProduced)
                                |> (given (marked: CoreLoweringState) -> withStatePatternOwnerCopyTemps(copyTemp :: statePatternOwnerCopyTemps(marked))(marked))
                                |> success(copyTemp)(semanticType)
                            | None -> lowered
            else lowered
        | _ -> lowered

let isFreshOrBorrowedBytes (provenance: BytesOwnershipProvenance) =
    match provenance with
        | BytesProvenanceFreshOwnedBuffer -> true
        | BytesProvenanceBorrowedView -> true
        | _ -> false

// Stage 0 leaves a head that may hold Bytes as it is, with no temp reserved, unless its Bytes are a
// fresh buffer or a borrowed view: a cell cannot own Bytes of unknown origin.
let consHeadBytesNormalizable (head: Expr) (semanticType: SemanticType) (state: CoreLoweringState) =
    !containsBytesLayout(semanticType)(state) || isFreshOrBorrowedBytes(builtinBytesProvenanceOf(head)(state))

// The read of a loop parameter the frame places on the reference-counted heap by now, or of a
// field of one, is reference-counted as stage 0 represents it (the parameter is a runtime-managed
// owner there), so it is retained rather than copied.
let headReadsAdmittedLoopParameter (head: Expr) (state: CoreLoweringState) =
    match (stateTcoLoopFrame(state), stateTcoLoop(state), loopParameterReadSlot(head)(state)) with
        | (Some(frame), Some(loop), Some(slot)) -> loopSlotIsRuntimeManaged(slot)(frame)(loop)(state)
        | _ -> false

// The cell of a cons or list literal is runtime-managed when a runtime list was requested and its
// head is runtime-manageable, stage 0's `LowerConsCell`. Under a loop's list-slot request a head
// holding Bytes of unknown origin is left as it was lowered (stage 0's
// `NormalizeRuntimeManagedListElement` takes no copy of it), so only a head lowered to a
// reference-counted value or a read of a loop parameter placed there makes the cell one; a read of a
// pattern binding that owns a reference is not one.
let cellHeadIsRuntimeManageable (request: ConsumerRequest) (headExpression: Expr) (headTemp: Int) (headType: SemanticType) (state: CoreLoweringState) =
    if request.runtimeTcoListTailSlot != None && !consHeadBytesNormalizable(headExpression)(headType)(state) && !resultSurvivesReset(headType)(state)
    then (isRuntimeTemp(headTemp)(state) || headReadsAdmittedLoopParameter(headExpression)(state)) && isRuntimeManageableHeapChild(headType)(false)(state)
    else isRuntimeManageableListElement(headExpression)(headTemp)(headType)(state)

let cellIsRuntimeManaged (request: ConsumerRequest) (headExpression: Expr) (headTemp: Int) (headType: SemanticType) (state: CoreLoweringState) = requestsRuntimeList(request) && cellHeadIsRuntimeManageable(request)(headExpression)(headTemp)(headType)(state)

// Stage 0's `ConsTailKeepsRequest` for a literal chain: a reference-counted cell over the rest of a
// chain bottoming out in a list literal (`1 :: 2 :: []` as a function's result) is followed by
// reference-counted cells too, as a list literal's are: an arena cell there is reclaimed with the
// call window while the head cell still points at it.
let literalChainTailKeepsRequest (request: ConsumerRequest) (tailExpression: Expr) =
    match (unspanArgument(tailExpression), request.runtimeTcoListTailSlot) with
        | (ExprCons(_head, _tail), None) -> requestsRuntimeList(request) && isFreshListConstruction(tailExpression)
        | _ -> false

// A reference-counted cell never points at an arena one: a cell of a chain consed onto a loop's
// list parameter or of a reference-counted literal chain whose tail cell stayed in the arena (its
// head could not be reference-counted) stays there too, stage 0's guard in `LowerConsTail`.
let chainTailKeepsCellInArena (request: ConsumerRequest) (tailExpression: Expr) (tailTemp: Int) (state: CoreLoweringState) =
    match (unspanArgument(tailExpression), request.runtimeTcoListTailSlot) with
        | (ExprCons(_head, _tail), Some(_slot)) -> !isRuntimeTemp(tailTemp)(state)
        | (ExprCons(_head, _tail), None) -> literalChainTailKeepsRequest(request)(tailExpression) && !isRuntimeTemp(tailTemp)(state)
        | _ -> false

// Stage 0's `CopiesFreshScalarListHeadIntoLoopCell`: inside a loop an owned slot holds one
// iteration's value and loses the others to the successors, so a fresh reference-counted list of
// scalars an arena cell would hold is copied into the arena instead.
let copiesFreshScalarListHeadIntoLoopCell (headTemp: Int) (resolved: SemanticType) (state: CoreLoweringState) =
    match (stateTcoLoopFrame(state), resolved) with
        | (Some(_frame), SemList(element)) -> resultSurvivesReset(element)(state) && isFreshRuntimeTemp(headTemp)(state)
        | _ -> false

// Stage 0's `CopyFreshStringHeadIntoArenaCell`: an arena cell never releases its head, so a newly
// produced reference-counted string, or a reference the cell took on a binding's string, is stored
// as an arena copy and released right away. A cell whose parts the back edge copies, or whose head
// is a pattern owner's duplicate a late release still names (stage 0's `lateDuplicate`), keeps its
// head as it is; a fresh head of a cell the back edge may copy later (a self call's successor under
// a pending one-shot post) is copied like any other. A retain the call the cell is an argument of
// collected is released here, so it leaves the frame either way.
let arenaCellHeadOf (request: ConsumerRequest) (headTemp: Int) (headType: SemanticType) (state: CoreLoweringState) =
    (let definition = definitionOfTemp(headTemp)(state.reversedInstructions)
    in
        let retained =
            match definition with
                | Some(RcDup(_target, _source, true, _mayBeEmpty)) -> true
                | _ -> false
        in
            let lateDuplicate =
                match definition with
                    | Some(RcDup(_target, _source, false, _mayBeEmpty)) -> request.lateCopiedCellParts
                    | _ -> false
            in
                let fresh = isFreshRuntimeTemp(headTemp)(state)
                in
                    if resolveType(state)(headType) != SemString || request.borrowsCellParts || lateDuplicate || request.recordFieldCell || !(fresh || retained)
                    then (state, headTemp)
                    else
                        match freshTemp(state) with
                            | FreshTemp { state = allocated, temp = copyTemp } ->
                                let copied =
                                    emit(CopyOutArena(copyTemp)(headTemp)(-1)(false)(IndependentClone)(None))(allocated)
                                in
                                    if fresh
                                    then
                                        (copied
                                        |> releaseConsumedOperand(headTemp)
                                        |> forgetCallArgumentTransferRetain(headTemp), copyTemp)
                                    else
                                        (copied
                                        |> emit(RcDrop(headTemp)("String")(-1)(true)(false)(None))
                                        |> forgetCallArgumentTransferRetain(headTemp), copyTemp))

// Stage 0's `TryCopyFreshScalarListHeadIntoArenaCell`: inside a loop, a newly produced
// reference-counted list of scalars (a call's owned `List(Int)` result pushed onto an arena worklist
// of lists) is stored as an arena copy of its spine and the original is released right away, where
// the arena cell would otherwise be its only holder and never release it.
let copyFreshScalarListHeadIntoArenaCell (headTemp: Int) (headType: SemanticType) (state: CoreLoweringState) =
    match freshTempRun(4)(state) with
        | FreshTemp { state = reserved, temp = copyTemp } ->
            match freshLabel("arena_cell_list_head_released")(reserved) with
                | FreshLabel { state = labelled, label = skipLabel } ->
                    (labelled
                    |> emit(CopyOutList(copyTemp)(headTemp)(InlineListHead)(false)(IndependentClone))
                    |> emit(LoadConstInt(copyTemp + 1)(0))
                    |> emit(CmpIntNe(copyTemp + 2)(headTemp)(copyTemp + 1))
                    |> emit(JumpIfFalse(copyTemp + 2)(skipLabel))
                    |> emit(IsReferenceCounted(copyTemp + 3)(headTemp))
                    |> emit(JumpIfFalse(copyTemp + 3)(skipLabel))
                    |> emitRuntimeChildDrop(headTemp)(headType)
                    |> emit(Label(skipLabel))
                    |> markRuntimeTemp(headTemp)(RuntimeTransferred), copyTemp)

// The head an arena cell stores: a fresh scalar list inside a loop as its arena copy, a string by
// `arenaCellHeadOf`'s rule, anything else as it is.
let arenaCellHeadFor (request: ConsumerRequest) (headTemp: Int) (headType: SemanticType) (state: CoreLoweringState) =
    if copiesFreshScalarListHeadIntoLoopCell(headTemp)(resolveType(state)(headType))(state) && !request.borrowsCellParts && !request.recordFieldCell
    then
        copyFreshScalarListHeadIntoArenaCell(headTemp)(resolveType(state)(headType))(state)
    else arenaCellHeadOf(request)(headTemp)(headType)(state)

let finishCons (request: ConsumerRequest) (headExpression: Expr) (tailExpression: Expr) head tail =
    match (head, tail) with
        | (LoweredCoreValue { state = failedState, error = Some(error) }, _tail) -> failure(failedState)(error)
        | (_head, LoweredCoreValue { state = failedState, error = Some(error) }) -> failure(failedState)(error)
        | (LoweredCoreValue { temp = headTemp, semanticType = headType }, LoweredCoreValue { state = tailState, temp = tailTemp, semanticType = tailType }) ->
            match bindType(SemList(headType))(tailType)(tailState) with
                | (failedState, Some(error)) -> failure(failedState)(error)
                | (typedState, None) ->
                    if cellIsRuntimeManaged(request)(headExpression)(headTemp)(headType)(typedState) && !chainTailKeepsCellInArena(request)(tailExpression)(tailTemp)(typedState)
                    then allocateListCell(headTemp)(tailTemp)(headType)(true)(typedState)
                    else
                        match arenaCellHeadFor(request)(headTemp)(headType)(typedState) with
                            | (headedState, cellHeadTemp) -> allocateListCell(cellHeadTemp)(tailTemp)(headType)(false)(headedState)

// Stage 0's `EmitReferenceCountedListTail`: the tail a reference-counted cell takes over from the
// accumulator parameter it extends. The parameter's first value is whatever the caller passed, so
// a tail outside the reference-counted heap is copied into it; one inside is moved into the cell
// exactly as it was, with no reference taken, because the cell replaces the parameter as its
// holder. The empty list is the null pointer, which points nowhere and is kept as it is.
let emitReferenceCountedListTail (tailTemp: Int) (plan: ArgumentCopyPlan) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = tested, temp = referenceCountedTemp } ->
            match freshLocal(tested) with
                | FreshLocal { state = slotted, local = resultSlot } ->
                    match freshLabel("rc_tail_done")(slotted) with
                        | FreshLabel { state = doneLabelled, label = doneLabel } ->
                            match doneLabelled
                            |> emit(IsReferenceCounted(referenceCountedTemp)(tailTemp))
                            |> emit(StoreLocal(resultSlot)(tailTemp))
                            |> freshTemp with
                                | FreshTemp { state = zeroed, temp = zeroTemp } ->
                                    match zeroed
                                    |> emit(LoadConstInt(zeroTemp)(0))
                                    |> freshTemp with
                                        | FreshTemp { state = compared, temp = emptyTemp } ->
                                            match compared
                                            |> emit(CmpIntEq(emptyTemp)(tailTemp)(zeroTemp))
                                            |> freshTemp with
                                                | FreshTemp { state = joined, temp = keepTemp } ->
                                                    match joined
                                                    |> emit(OrInt(keepTemp)(referenceCountedTemp)(emptyTemp))
                                                    |> freshLabel("rc_tail_copy") with
                                                        | FreshLabel { state = copyLabelled, label = copyLabel } ->
                                                            match copyLabelled
                                                            |> emit(JumpIfFalse(keepTemp)(copyLabel))
                                                            |> emit(Jump(doneLabel))
                                                            |> emit(Label(copyLabel))
                                                            |> emitArgumentCopyByCopy(tailTemp)(plan) with
                                                                | (copied, copyTemp) ->
                                                                    match copied
                                                                    |> emit(StoreLocal(resultSlot)(copyTemp))
                                                                    |> emit(Label(doneLabel))
                                                                    |> freshTemp with
                                                                        | FreshTemp { state = resulted, temp = resultTemp } ->
                                                                            (emit(LoadLocal(resultTemp)(resultSlot))(resulted), resultTemp)

// Whether an arena cell the loop returned over the parameter would hold a reference nothing
// releases: to the accumulator its back edges grow on the reference-counted heap, or to a head that
// is not a scalar.
let loopParameterIsGrownAccumulator (slot: Int) (frame: CoreTcoLoopFrame) (loop: CoreTcoLoop) =
    match ordinalOfSlot(slot)(frame.parameterSlots)(0) with
        | Some(ordinal) -> shapeAtOrdinal(ordinal)(loop.argumentShapes) == TcoGrownConsShape
        | None -> false

let listBindingHoldsNonScalars (name: Str) (state: CoreLoweringState) =
    match lookupBinding(name)(state.bindings) with
        | Some(CoreBinding { scheme = TypeScheme { body = body } }) ->
            match resolveType(state)(body) with
                | SemList(element) -> !resultSurvivesReset(element)(state)
                | _ -> true
        | None -> true

let loopExitCellHoldsReference (name: Str) (slot: Int) (frame: CoreTcoLoopFrame) (loop: CoreTcoLoop) (state: CoreLoweringState) = loopParameterIsGrownAccumulator(slot)(frame)(loop) || listBindingHoldsNonScalars(name)(state)

// Stage 0's `IsLoopExitCellOverParameter`: the cell a loop returns consed onto one of its list
// parameters (`| [] -> first :: acc`) is reference-counted, like the cell a back edge conses onto
// an accumulator, when an arena cell there would hold a reference nothing releases. Neither the
// caller, which sees an arena result, nor the join that copies it beside a reference-counted arm
// releases it. A cell of scalars over a parameter handed on unchanged stays in the arena.
let loopExitCellSlot (request: ConsumerRequest) (tailExpression: Expr) (state: CoreLoweringState) =
    match (canTestRepresentation(state) && request.consTailPosition, stateTcoLoop(state), stateTcoLoopFrame(state), unspanArgument(tailExpression)) with
        | (true, Some(loop), Some(frame), ExprVar(name)) ->
            match parameterSlotOfName(name)(frame)(state) with
                | Some(slot) ->
                    if loopExitCellHoldsReference(name)(slot)(frame)(loop)(state)
                    then Some(slot)
                    else None
                | None -> None
        | _ -> None

let loopExitCellRequest (tailExpression: Expr) (state: CoreLoweringState) (request: ConsumerRequest) =
    match loopExitCellSlot(request)(tailExpression)(state) with
        | Some(slot) -> request with runtimeList = true, runtimeTcoListTailSlot = Some(slot)
        | None -> request

// A back edge's cell takes the accumulator over from the iteration it replaces. The cell the loop
// returns shares the parameter with the loop, which still releases it at its exit, or with the
// caller that lent it, so it takes a reference of its own when the list is reference-counted and a
// copy when it is not, whatever the parameter's placement.
let referenceOrCopyLoopExitTail (tailTemp: Int) (tailType: SemanticType) (state: CoreLoweringState) =
    match argumentCopyPlanOf(tailType)(state) with
        | Some(plan) ->
            emitReferenceOrCopy(tailTemp)(state
            |> entryCopyPlanOf(tailType)(plan)
            |> emitArgumentCopyByCopy(tailTemp))(state)
        | None -> (state, tailTemp)

// Stage 0's `PrepareRuntimeRcListTail`: the cons argument of a tail self-call whose tail reads the
// loop's own list accumulator takes that list over rather than copying it.
let prepareRuntimeRcListTail (request: ConsumerRequest) (tailExpression: Expr) (tailTemp: Int) (tailType: SemanticType) (state: CoreLoweringState) =
    match (canTestRepresentation(state), request.runtimeTcoListTailSlot, unspanArgument(tailExpression)) with
        | (true, Some(tailSlot), ExprVar(name)) ->
            match lookupBinding(name)(state.bindings) with
                | Some(CoreBinding { location = CoreLocal(boundSlot) }) ->
                    if boundSlot == tailSlot
                    then
                        match argumentCopyPlanOf(tailType)(state) with
                            | Some(plan) ->
                                emitReferenceCountedListTail(tailTemp)(entryCopyPlanOf(tailType)(plan)(state))(state)
                            | None -> (state, tailTemp)
                    else (state, tailTemp)
                | _ -> (state, tailTemp)
        | _ -> (state, tailTemp)

// Stage 0's `IsUnplacedLetBoundTail`: a recursive producer's result read from the let it was bound
// to, with no owner here that the reference-counted cell could retain (a self call's result of the
// binding's own, still unresolved type asks for an arena copy), takes a reference of its own when
// the list is in the reference-counted region and a copy onto it when the list is in the arena,
// which the enclosing scope reclaims under the cell.
let referenceOrCopyUnplacedLetTail (applies: Bool) (tailExpression: Expr) (tailTemp: Int) (tailType: SemanticType) (state: CoreLoweringState) =
    match (applies && canTestRepresentation(state) && isRuntimeTemp(tailTemp)(state) == false, unspanArgument(tailExpression), argumentCopyPlanOf(tailType)(state)) with
        | (true, ExprVar(_name), Some(plan)) ->
            emitReferenceOrCopy(tailTemp)(state
            |> entryCopyPlanOf(tailType)(plan)
            |> emitArgumentCopyByCopy(tailTemp))(state)
        | _ -> (state, tailTemp)

// A list bound out of a pattern is whatever the matched value's lists are, and the matched value may
// be released before the cell (a loop's predecessor at its back edge): the cell takes a reference
// of its own when the list is reference-counted and a copy when it is not (stage 0's
// `PatternBoundTailsOwnedEnabled` branch of `PrepareRuntimeRcConsTail`).
let isPatternBoundTail (tailExpression: Expr) (state: CoreLoweringState) =
    match unspanArgument(tailExpression) with
        | ExprVar(name) ->
            match patternOwnerBinding(name)(state) with
                | Some(_fact) -> true
                | None -> false
        | _ -> false

let prepareRuntimeRcConsTail (request: ConsumerRequest) (tailExpression: Expr) (tailTemp: Int) (tailType: SemanticType) (state: CoreLoweringState) =
    match loopExitCellSlot(request)(tailExpression)(state) with
        | Some(_slot) -> referenceOrCopyLoopExitTail(tailTemp)(tailType)(state)
        | None ->
            if isPatternBoundTail(tailExpression)(state)
            then referenceOrCopyUnplacedLetTail(true)(tailExpression)(tailTemp)(tailType)(state)
            else prepareRuntimeRcListTail(request)(tailExpression)(tailTemp)(tailType)(state)

// Stage 0's `KeepEscapingArenaCellFreshHeadOwned`: an escaping arena cell (a call's argument the
// callee's result may reach) is the only holder of a value produced right there, a fresh tuple
// consed onto an accumulator passed to a recursive call, and an arena cell releases nothing. The
// value goes to an owned slot, released once the function's result holds references of its own.
// A head that is a retain the call's argument frame already collected (an alias's owner retained
// for the cell) is released after the call, so it does not also go to a slot.
// Stage 0's `IsCopiedOutFreshHeadType`: a list, tuple or data type the function's result copies out
// rather than owns is copied before the owned slots are released, so such a head is kept the same
// way.
let isCopiedOutFreshHeadType (semanticType: SemanticType) (state: CoreLoweringState) =
    match semanticType with
        | SemList(_element) -> true
        | SemTuple(_elements) -> true
        | SemNamed(_symbolId, name, _typeArguments) -> !isResourceTypeNameIn(name)(state)
        | _ -> false

// A tail self-call's argument is the next iteration's parameter, which keeps the cell and the head it
// holds: a copied-out head handed to an owned slot there was released while the successor still read
// it, so only a head of the contract's own types goes to the slot on a back edge.
let lowersBackEdgeArgument (state: CoreLoweringState) =
    match stateBackEdgeArgumentSlot(state) with
        | Some(_slot) -> true
        | None -> false

let keepEscapingArenaCellFreshHeadOwned (arenaCell: Bool) (headTemp: Int) (headType: SemanticType) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, error = None } ->
            let resolved = resolveType(state)(headType)
            in
                if arenaCell && isFreshRuntimeTemp(headTemp)(state) && !collectsCallArgumentRetain(headTemp)(stateCallArgumentRecordRetains(state)) && !copiesFreshScalarListHeadIntoLoopCell(headTemp)(resolved)(state) && !containsUnresolvedLayout(resolved)(state) && (isGeneralRcValueType(resolved)(state) || isCopiedOutFreshHeadType(resolved)(state) && !lowersBackEdgeArgument(state))
                then
                    match freshLocal(state) with
                        | FreshLocal { state = allocated, local = ownedSlot } ->
                            let kept =
                                allocated
                                |> emit(StoreLocal(ownedSlot)(headTemp))
                                |> registerGeneralRcOwnedSlot(ownedSlot)(resolved)
                            in lowered with state = kept
                else lowered
        | _ -> lowered

// The tail of an escaping arena cell carries a runtime-managed owner out of the scope that owns
// it exactly like an escaping tuple element, and is retained. A recursive producer result stored
// in a runtime cell also needs its own reference when an existing local owns the tail.
let retainConsTail (request: ConsumerRequest) (transfers: Bool) (headExpression: Expr) (tailExpression: Expr) head tail =
    match (head, tail) with
        | (LoweredCoreValue { temp = headTemp, semanticType = headType, error = None }, LoweredCoreValue { state = tailState, temp = tailTemp, semanticType = tailType, error = None }) ->
            let managed = cellIsRuntimeManaged(request)(headExpression)(headTemp)(headType)(tailState)
            in
                match if managed
                then prepareRuntimeRcConsTail(request)(tailExpression)(tailTemp)(tailType)(tailState)
                else (tailState, tailTemp) with
                    | (preparedState, preparedTemp) ->
                        if transfers && managed == false || managed && recursiveProducerResult(tailExpression)(preparedState)
                        then
                            match retainAggregateChildTemp(tailExpression)(preparedTemp)(tailType)(preparedState) with
                                | (retained, retainedTemp) ->
                                    retained
                                    |> recordArenaCellTailRetain(!managed)(preparedTemp)(retainedTemp)(tailType)
                                    |> referenceOrCopyUnplacedLetTail(managed && retainedTemp == preparedTemp)(tailExpression)(retainedTemp)(tailType)
                                    |> (given (owned: (CoreLoweringState, Int)) ->
                                        match owned with
                                            | (ownedState, ownedTemp) -> success(ownedTemp)(tailType)(ownedState))
                                    |> retainLoopParameterChild(tailExpression)(preparedTemp)
                                    |> recordArenaCellParameterRetain(!managed)(tailExpression)(preparedTemp)
                                    |> keepArenaCellParameterRetainOwned(!managed)(preparedTemp)
                                    |> keepEscapingArenaCellFreshHeadOwned(transfers && !managed)(headTemp)(headType)
                        else tail with state = preparedState, temp = preparedTemp
        | _ -> tail

// Stage 0's `LowerCons` order once both parts are lowered: the head's pattern-owner duplicate,
// then the tail's, before the tail's own transfer retain.
let duplicatePatternOwnerConsParts (headExpression: Expr) (tailExpression: Expr) (head: LoweredCoreValue) (tail: LoweredCoreValue) =
    match (head, tail) with
        | (LoweredCoreValue { temp = headTemp, semanticType = headType, error = None }, LoweredCoreValue { state = tailState, temp = tailTemp, semanticType = tailType, error = None }) ->
            match duplicatePatternOwnerTemp(headExpression)(headTemp)(tailState) with
                | (headDuplicated, headDuplicate) ->
                    match duplicatePatternOwnerTemp(tailExpression)(tailTemp)(headDuplicated) with
                        | (tailDuplicated, tailDuplicate) -> (LoweredCoreValue(state = tailDuplicated, temp = headDuplicate, semanticType = headType, error = None), LoweredCoreValue(state = tailDuplicated, temp = tailDuplicate, semanticType = tailType, error = None))
        | _ -> (head, tail)

// Stage 0's `KeepCopiedCellFreshHeadOwned`: the head of a successor cell the back edge copies, when
// it is a value produced right there (a call's owned result) rather than a read of an owner, keeps
// an owner of its own, released once the successor holds its references, as a `let` binding of it
// would be. The cell only borrows it and the copy takes a reference of its own.
let keepCopiedCellFreshHeadOwned (headExpression: Expr) (headTemp: Int) (headType: SemanticType) (state: CoreLoweringState) =
    match (unspanArgument(headExpression), if generalRcEnabled(state)
    then runtimeTempStateOf(headTemp)(state)
    else None) with
        | (ExprVar(_name), _) -> state
        | (ExprQualifiedVar(_owner, _field), _) -> state
        | (_, Some(RuntimeNewlyProduced)) ->
            match freshLocal(state) with
                | FreshLocal { state = allocated, local = ownedSlot } ->
                    allocated
                    |> emit(StoreLocal(ownedSlot)(headTemp))
                    |> registerGeneralRcOwnedSlot(ownedSlot)(resolveType(state)(headType))
        | _ -> state

// Stage 0's `CollectCopiedHeadTuple`: a tuple written as the head of a successor cell the back edge
// copies, and each tuple written inside it, is copied with the cell, the copy taking references of
// its own. Left in the arena it borrows its parts like the cell does: a duplicate one of them took
// of a pattern owner would be promoted to a retain the arena never releases. A tuple placed on the
// reference-counted heap owns its duplicates and keeps them, and a duplicate with another reader is
// left alone.
let recursive withdrawCopiedHeadTupleMarkers (head: Expr) (temp: Int) (state: CoreLoweringState) =
    match unspanArgument(head) with
        | ExprTuple(elements) ->
            if isArenaCell(temp)(state.reversedInstructions)
            then
                withdrawCopiedHeadTupleElementMarkers(elements)(storedValuesOf(temp)(state.reversedInstructions)([]))(state)
            else state
        | _ -> state
and withdrawCopiedHeadTupleElementMarkers (elements: List(Expr)) (temps: List(Int)) (state: CoreLoweringState) =
    match (elements, temps) with
        | (element :: restElements, temp :: restTemps) ->
            if isPromotableCellMarker(temp)(state) && countTempUses(temp)(state.reversedInstructions)(0) == 1
            then withdrawCopiedHeadTupleElementMarkers(restElements)(restTemps)((state with reversedInstructions = borrowElementDuplicate(temp)(state.reversedInstructions)))
            else
                state
                |> withdrawCopiedHeadTupleMarkers(element)(temp)
                |> withdrawCopiedHeadTupleElementMarkers(restElements)(restTemps)
        | _ -> state

// A successor cell the back edge copies borrows its parts: the head keeps the tail's state, neither
// takes a duplicate, and a head tuple left in the arena gives back the ones it took.
let borrowedConsParts (headExpression: Expr) (head: LoweredCoreValue) (tail: LoweredCoreValue) =
    match (head, tail) with
        | (LoweredCoreValue { temp = headTemp, semanticType = headType, error = None }, LoweredCoreValue { state = tailState, error = None }) ->
            let kept =
                tailState
                |> keepCopiedCellFreshHeadOwned(headExpression)(headTemp)(headType)
                |> withdrawCopiedHeadTupleMarkers(headExpression)(headTemp)
            in ((head with state = kept), (tail with state = kept))
        | _ -> (head, tail)

// Stage 0's `BackEdgeCopiesListSuccessor`: a choice accumulator already placed on the
// reference-counted heap has its successor copied at the back edge with references of its own,
// before the iteration's owners are released, so an arena cell built for it borrows its children;
// a retain stored in that cell would never be released.
// An accumulator-rebuild list refilled with a call's result (`ds |> applied(root)(arguments)`) beside
// arms that cons onto it is one too: stage 0 asks only that the list is not grown in place nor walked
// by its consumed tail.
let backEdgeCopiesListSuccessor (slot: Maybe(Int)) (shape: TcoArgumentShape) (loop: CoreTcoLoop) (state: CoreLoweringState) =
    match (shape, slot, stateTcoLoopFrame(state)) with
        | (TcoChoiceAccumulatorShape, Some(parameterSlot), Some(frame)) -> loopSlotIsRuntimeManaged(parameterSlot)(frame)(loop)(state)
        | (_shape, Some(parameterSlot), Some(frame)) ->
            if accumulatorRouteShape(shape) || shape == TcoWalkedOrRefilledShape
            then
                match ordinalOfSlot(parameterSlot)(frame.parameterSlots)(0) with
                    | Some(ordinal) -> isAccumulatorRebuildListSlot(ordinal)(parameterSlot)(loop)(state) && loopSlotIsRuntimeManaged(parameterSlot)(frame)(loop)(state)
                    | None -> false
            else false
        | _ -> false

// Stage 0's `BackEdgeCopiesLiteralSuccessor`: a list literal of heap elements written as the
// successor of a list parameter on the reference-counted heap has its arena cells copied at the
// back edge, the copy taking a reference of its own to each element, so the cells are copied cells.
let backEdgeCopiesLiteralSuccessor (slot: Maybe(Int)) (shape: TcoArgumentShape) (parameterType: SemanticType) (argument: Expr) (loop: CoreTcoLoop) (state: CoreLoweringState) =
    match (unspanArgument(argument), resolveType(state)(parameterType), slot, stateTcoLoopFrame(state)) with
        | (ExprList(_first :: _rest, _isMultiline), SemList(element), Some(parameterSlot), Some(frame)) -> shape != TcoGrownConsShape && !resultSurvivesReset(element)(state) && loopSlotIsRuntimeManaged(parameterSlot)(frame)(loop)(state)
        | _ -> false

// Stage 0's `NoteLateCopiedHeadTuples`: whether the back edge now copies the successor of the
// self-call argument being lowered, once the cell's own types have resolved the parameter's.
let backEdgeArgumentNowCopied (state: CoreLoweringState) =
    match (stateBackEdgeArgumentSlot(state), stateTcoLoop(state), stateTcoLoopFrame(state)) with
        | (Some(slot), Some(loop), Some(frame)) ->
            match ordinalOfSlot(slot)(frame.parameterSlots)(0) with
                | Some(ordinal) ->
                    backEdgeCopiesListSuccessor(Some(slot))(shapeAtOrdinal(ordinal)(loop.argumentShapes))(loop)(state)
                | None -> false
        | _ -> false

let tailStateOf (tail: LoweredCoreValue) =
    match tail with
        | LoweredCoreValue { state = state } -> state

let consPartsFor (request: ConsumerRequest) (headExpression: Expr) (tailExpression: Expr) (head: LoweredCoreValue) (tail: LoweredCoreValue) =
    if request.borrowsCellParts || request.lateCopiedCellParts && backEdgeArgumentNowCopied(tailStateOf(tail))
    then borrowedConsParts(headExpression)(head)(tail)
    else duplicatePatternOwnerConsParts(headExpression)(tailExpression)(head)(tail)

let listTransfers (request: ConsumerRequest) (state: CoreLoweringState) =
    match request with
        | ConsumerRequest { transfersRuntimeManagedChildren = transfersRequested } -> transfersRequested || inLoopTailPosition(request)(state)

// Stage 0's cons tail request: a cell of a chain consed onto a loop's list parameter
// (`first :: separator :: acc`) takes a tail built the same way, so every cell of the chain is
// reference-counted, not only the first. A tail cell of a cell whose children escape
// (`first :: second :: more` as a result) transfers its children too, stage 0's
// `EscapingTailCellRequest`, so its head takes a reference of its own instead of borrowing a
// binding whose scope ends first. Any other tail is lowered with its expected type alone.
let consTailRequest (request: ConsumerRequest) (tailExpression: Expr) (headType: SemanticType) (state: CoreLoweringState) =
    match (unspanArgument(tailExpression), request.runtimeTcoListTailSlot) with
        | (ExprCons(_head, _tail), Some(_slot)) -> withConsumerRequest((request with expectedType = Some(SemList(headType)), chainTailCell = true))(state)
        | (ExprCons(_head, _tail), None) ->
            if request.copiedInnerCell
            then withConsumerRequest((emptyConsumerRequest with expectedType = Some(SemList(headType)), borrowsCellParts = true, copiedInnerCell = true))(state)
            else
                if literalChainTailKeepsRequest(request)(tailExpression)
                then withConsumerRequest((request with expectedType = Some(SemList(headType))))(state)
                else
                    if listTransfers(request)(state)
                    then withConsumerRequest((emptyConsumerRequest with expectedType = Some(SemList(headType)), transfersRuntimeManagedChildren = true))(state)
                    else withOnlyExpectedType(Some(SemList(headType)))(state)
        | _ -> withOnlyExpectedType(Some(SemList(headType)))(state)

// Stage 0's `OwnHeadOfCellOverFreshTail`: a cons onto a fresh reference-counted tail (a call's owned
// result, `entry :: build(n)`, or a cons cell that took such a tail over, `x :: y :: build(n)`)
// builds a reference-counted cell, the list counterpart of a
// constructor owning a fresh child. The cell takes the tail over, and the head takes a reference
// of its own when it is reference-counted or a copy when it lies in the arena. A live arena
// list-cell reuse token beats it, as it beats the recursive producer's request: a reuse
// specialization's self call hands back a result stage 0 does not count as fresh, and its cell
// is rebuilt in place.
let noListCellReuseToken (tokens: List(CoreReuseToken)) =
    match takeListCellReuseToken(tokens) with
        | Some(_found) -> false
        | None -> true

let freshProducedTail (tailExpression: Expr) (tailTemp: Int) (state: CoreLoweringState) =
    (match unspanArgument(tailExpression) with
        | ExprCall(_function, _argument, _isSugar, _layout) -> true
        | ExprCons(_head, _tail) -> true
        | _ -> false) && (match runtimeTempStateOf(tailTemp)(state) with
        | Some(RuntimeNewlyProduced) -> true
        | _ -> false)

let cellRequestOf (request: ConsumerRequest) = request with runtimeList = true

let ownHeadOfCellOverFreshTail (request: ConsumerRequest) (tailExpression: Expr) (head: LoweredCoreValue) (tail: LoweredCoreValue) =
    match (head, tail) with
        | (LoweredCoreValue { temp = headTemp, semanticType = headType, error = None }, LoweredCoreValue { state = tailState, temp = tailTemp, semanticType = tailType, error = None }) ->
            if generalRcEnabled(tailState) && canTestRepresentation(tailState) && requestsRuntimeList(request) == false && request.borrowsCellParts == false && noListCellReuseToken(tailState.reuseTokens) && freshProducedTail(tailExpression)(tailTemp)(tailState) && containsUnresolvedLayout(tailType)(tailState) == false && containsUnresolvedLayout(headType)(tailState) == false
            then
                if headType
                |> resolveType(tailState)
                |> isCopyTypeSemantic
                then (cellRequestOf(request), head, tail)
                else
                    match generalCopyPlanOf(headType)(tailState) with
                        | Some(plan) ->
                            match emitGuardedDeepCopy(headTemp)(plan)(tailState) with
                                | (copied, ownedTemp) ->
                                    let marked = markRuntimeTemp(ownedTemp)(RuntimeNewlyProduced)(copied)
                                    in
                                        let ownedHead = head with state = marked, temp = ownedTemp
                                        in (cellRequestOf(request), ownedHead, (tail with state = marked))
                        | None -> (request, head, tail)
            else (request, head, tail)
        | _ -> (request, head, tail)

// Stage 0's `KeepArgumentExitCellOwned`: the cell built for a call's argument as a loop's exit cell
// (a cons onto a loop parameter asked for by `withEscapingArgumentCell`) is reference-counted, and
// a list of the contract's types is passed borrowed, so no consumed-argument release follows the
// call. The cell goes to an owned slot instead, released once the call's result holds references
// of its own.
let keepArgumentExitCellOwned (request: ConsumerRequest) (tailExpression: Expr) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, temp = cellTemp, semanticType = cellType, error = None } ->
            match (request.escapingArgumentCell, loopExitCellSlot(request)(tailExpression)(state)) with
                | (true, Some(_slot)) ->
                    if isRuntimeTemp(cellTemp)(state) && isGeneralRcValueType(resolveType(state)(cellType))(state)
                    then
                        match freshLocal(state) with
                            | FreshLocal { state = allocated, local = ownedSlot } ->
                                match allocated
                                |> emit(StoreLocal(ownedSlot)(cellTemp))
                                |> registerGeneralRcOwnedSlot(ownedSlot)(resolveType(state)(cellType))
                                |> freshTemp with
                                    | FreshTemp { state = registered, temp = ownedTemp } ->
                                        lowered with state = emit(LoadLocal(ownedTemp)(ownedSlot))(registered), temp = ownedTemp
                    else lowered
                | _ -> lowered
        | _ -> lowered

// Stage 0's `NoteListHeadCell`: an arena cell consing a closure call's result of unresolved type
// onto the empty list or onto the enclosing function's own recursive call is recorded.
let noteListHeadCell (tailExpression: Expr) (head: LoweredCoreValue) (tail: LoweredCoreValue) (cell: LoweredCoreValue) =
    match (head, tail, cell) with
        | (LoweredCoreValue { temp = headTemp, semanticType = headType }, LoweredCoreValue { temp = tailTemp }, LoweredCoreValue { state = state, temp = cellTemp, error = None }) ->
            let forward = reverse(state.reversedInstructions)
            in
                let headCall = soleCallSourceThroughLocals(headTemp)(16)(forward)(state)
                in
                    let tailCall =
                        match definitionOfTemp(tailTemp)(state.reversedInstructions) with
                            | Some(LoadConstInt(_target, 0)) -> -1
                            | _ ->
                                match unspanArgument(tailExpression) with
                                    | ExprCall(_function, _argument, _sugar, _layout) ->
                                        if recursiveProducerResult(tailExpression)(state)
                                        then
                                            let call = soleCallSourceThroughLocals(tailTemp)(16)(forward)(state)
                                            in
                                                if call < 0
                                                then -2
                                                else call
                                        else -2
                                    | _ -> -2
                    in
                        let generic =
                            match resolveType(state)(headType) with
                                | SemVariable(_id) -> true
                                | _ -> false
                        in
                            match (definitionOfTemp(cellTemp)(state.reversedInstructions), closureOfCallResult(headCall)(1000000)(state.reversedInstructions)) with
                                | (Some(Alloc(_target, _size, false)), Some(closureTemp)) ->
                                    if generalRcEnabled(state) && generic && headCall >= 0 && tailCall != -2
                                    then cell with state = withStateListHeadCells((cellTemp, headTemp, closureTemp, tailCall) :: stateListHeadCells(state))(state)
                                    else cell
                                | _ -> cell
        | _ -> cell

let isChainOwnableHeadType (semanticType: SemanticType) =
    match semanticType with
        | SemTuple(_elements) -> true
        | SemString -> true
        | SemBytes -> true
        | SemBigInt -> true
        | SemList(_element) -> true
        | SemNamed(_id, _name, _arguments) -> true
        | _ -> false

let isChainTail (expression: Expr) =
    match unspanArgument(expression) with
        | ExprCons(_head, _tail) -> true
        | _ -> false

// Stage 0's `OwnChainHeadOverReferenceCountedTail`: a cell of a chain consed onto a loop's list
// parameter over a reference-counted cell is reference-counted too. An arena cell there would be
// the only holder of the cells after it: the next iteration copies the arena cell's spine onto the
// heap as the parameter, and nothing releases the reference-counted cells it copied. A head the
// arena would keep (a pattern-bound or borrowed value) takes a reference of its own when it is
// reference-counted and a copy when it is not, recorded so no pattern-owner duplicate follows, and
// its cell follows. The innermost cell, whose tail is the parameter itself placed on the
// reference-counted heap, is one too: kept in the arena, it would keep every cell before it there.
let ownChainHeadOverReferenceCountedTail (request: ConsumerRequest) (headExpression: Expr) (tailExpression: Expr) (head: LoweredCoreValue) (tail: LoweredCoreValue) =
    match (head, tail) with
        | (LoweredCoreValue { temp = headTemp, semanticType = headType, error = None }, LoweredCoreValue { state = tailState, temp = tailTemp, error = None }) ->
            if canTestRepresentation(tailState) && (isChainTail(tailExpression) || request.chainTailCell) && request.runtimeTcoListTailSlot != None && requestsRuntimeList(request) && !request.borrowsCellParts && (isRuntimeTemp(tailTemp)(tailState) || headReadsAdmittedLoopParameter(tailExpression)(tailState)) && !cellHeadIsRuntimeManageable(request)(headExpression)(headTemp)(headType)(tailState) && isChainOwnableHeadType(resolveType(tailState)(headType))
            then
                match argumentCopyPlanOf(headType)(tailState) with
                    | Some(plan) ->
                        match emitReferenceOrCopy(headTemp)(tailState
                        |> entryCopyPlanOf(headType)(plan)
                        |> emitArgumentCopyByCopy(headTemp))(tailState) with
                            | (owned, ownedTemp) ->
                                let marked =
                                    owned
                                    |> markRuntimeTemp(ownedTemp)(RuntimeNewlyProduced)
                                    |> withStatePatternOwnerCopyTemps(ownedTemp :: statePatternOwnerCopyTemps(owned))
                                in ((head with state = marked, temp = ownedTemp), (tail with state = marked))
                    | None -> (head, tail)
            else (head, tail)
        | _ -> (head, tail)

// Stage 0's `RecordLateCopiedCellHeadDuplicate`: an arena cell of a successor the back edge may
// still copy keeps its head's and its tail's duplicates only until that copy, so each duplicate is
// noted for the back edge to release once the parameter turns out a list on the reference-counted
// heap, or, for a cell written as a record successor's field value, a record there. A
// reference-counted cell owns the duplicate and keeps it.
let recordLateCopiedCellPart (request: ConsumerRequest) (owned: LoweredCoreValue) (duplicated: LoweredCoreValue) (cell: LoweredCoreValue) =
    match (cell, owned, duplicated) with
        | (LoweredCoreValue { state = state, temp = cellTemp, error = None }, LoweredCoreValue { temp = ownedTemp }, LoweredCoreValue { temp = duplicateTemp, semanticType = partType }) ->
            match stateBackEdgeArgumentSlot(state) with
                | Some(slot) ->
                    if (request.lateCopiedCellParts || request.recordFieldCell) && duplicateTemp != ownedTemp && !isRuntimeTemp(cellTemp)(state)
                    then cell with state = withStateLateCopiedCellHeads((slot, duplicateTemp, partType) :: stateLateCopiedCellHeads(state))(state)
                    else cell
                | None -> cell
        | _ -> cell

let recordLateCopiedCellHead (request: ConsumerRequest) (ownedHead: LoweredCoreValue) (duplicatedHead: LoweredCoreValue) (ownedTail: LoweredCoreValue) (duplicatedTail: LoweredCoreValue) (cell: LoweredCoreValue) =
    cell
    |> recordLateCopiedCellPart(request)(ownedHead)(duplicatedHead)
    |> recordLateCopiedCellPart(request)(ownedTail)(duplicatedTail)

// Stage 0's `RecordCallArgumentCellOwnerDuplicates`: an arena cons in an argument
// never releases its pattern-owner references. The existing call-frame release takes
// them back only when the result owns itself, or cannot reach the argument.
// Successor cells retain their existing back-edge cleanup instead.
let recordCallArgumentCellOwnerPart (owned: LoweredCoreValue) (duplicated: LoweredCoreValue) (state: CoreLoweringState) =
    match (owned, duplicated) with
        | (LoweredCoreValue { temp = original }, LoweredCoreValue { temp = duplicate, semanticType = semanticType }) ->
            if original == duplicate
            then state
            else
                let slot =
                    if stateCollectingCallArgumentRetains(state)
                    then -2
                    else -3
                in
                    withCallArgumentRetainFrame((duplicate, slot, semanticType) :: stateCallArgumentRecordRetains(state))(stateCollectingCallArgumentRetains(state))(state)

let recordCallArgumentCellOwnerDuplicates (request: ConsumerRequest) (ownedHead: LoweredCoreValue) (duplicatedHead: LoweredCoreValue) (ownedTail: LoweredCoreValue) (duplicatedTail: LoweredCoreValue) (cell: LoweredCoreValue) =
    match cell with
        | LoweredCoreValue { state = state, temp = temp, error = None } ->
            if !stateCallArgumentFrameActive(state) || state.nextLabelId != stateCallArgumentLabelStart(state) || isRuntimeTemp(temp)(state) || request.borrowsCellParts || request.lateCopiedCellParts || request.recordFieldCell
            then cell
            else
                cell with state = state |> recordCallArgumentCellOwnerPart(ownedHead)(duplicatedHead) |> recordCallArgumentCellOwnerPart(ownedTail)(duplicatedTail)
        | _ -> cell

// The head an arena cell stored, the most recent store first.
let recursive storedCellHead (cell: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> None
        | IrInstruction { instruction = StoreMemOffset(basePtr, 0, source) } :: rest ->
            if basePtr == cell
            then Some(source)
            else storedCellHead(cell)(rest)
        | _instruction :: rest -> storedCellHead(cell)(rest)

// Stage 0's `RecordArmCellHeadSlot`: an arm's own arena cell over the function's recursive call,
// holding a duplicate of its pattern-owned head, gets an owned slot registered and zeroed right
// behind the cell's stores, before any exit of the function is emitted; the join writes the
// duplicate into it behind its copy of that arm's cell (`armCellHeadSlotOf` finds the slot by the
// two instructions this emits). A slot no join writes stays zero and releases nothing.
let reserveArmCellHeadSlot (request: ConsumerRequest) (tailExpression: Expr) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, temp = cellTemp, semanticType = listType, error = None } ->
            if generalRcEnabled(state) && request.callArgument == false && (match request.transferSlot with
                | Some(_slot) -> false
                | None -> true) && !lowersBackEdgeArgument(state) && (recursiveProducerResult(tailExpression)(state) || isConsChainOntoSelfCall(ExprCons(ExprVar("_"))(tailExpression))(state)) && (match definitionOfTemp(cellTemp)(state.reversedInstructions) with
                | Some(Alloc(_cell, 16, false)) -> true
                | _ -> false) && (match storedCellHead(cellTemp)(state.reversedInstructions) with
                | Some(head) ->
                    match definitionOfTemp(head)(state.reversedInstructions) with
                        | Some(RcDup(_target, _source, _runtimeManaged, _mayBeEmpty)) -> true
                        | _ -> false
                | None -> false)
            then
                match (resolveType(state)(listType), freshLocal(state)) with
                    | (SemList(element), FreshLocal { state = allocated, local = ownedSlot }) ->
                        if containsUnresolvedLayout(element)(allocated)
                        then lowered
                        else
                            match freshTemp(allocated) with
                                | FreshTemp { state = withTemp, temp = zeroTemp } ->
                                    lowered with state = withTemp |> emit(LoadConstInt(zeroTemp)(0)) |> emit(StoreLocal(ownedSlot)(zeroTemp)) |> registerGeneralRcOwnedSlot(ownedSlot)(resolveType(allocated)(element))
                    | _ -> lowered
            else lowered
        | _ -> lowered

// Stage 0's `KeepArmCellTailMarkerOwned`: a loop's result cell built in tail position over a tail
// matched out of a loop parameter (`(key, value) :: tail`) takes the pattern owner's duplicate of
// the tail, still a marker while the cell is built; the copy that normalizes the result holds a
// reference of its own to whatever it keeps of that tail, so once promoted the duplicate is a
// retain the arena cell holds with nothing to release it. It goes to an owned slot registered and
// zeroed at the cons, which `settleCellMarkerSlots` fills once the marker is promoted, as a
// constructor's arena cell does with such a marker.
let keepArmCellTailMarkerOwned (request: ConsumerRequest) (tailExpression: Expr) (ownedTail: LoweredCoreValue) (duplicatedTail: LoweredCoreValue) (lowered: LoweredCoreValue) =
    match (lowered, ownedTail, duplicatedTail) with
        | (LoweredCoreValue { state = state, temp = cellTemp, error = None }, LoweredCoreValue { temp = original }, LoweredCoreValue { temp = marker, semanticType = tailType }) ->
            if generalRcEnabled(state) && request.tailPosition && request.callArgument == false && request.lateCopiedCellParts == false && request.recordFieldCell == false && (match request.transferSlot with
                | Some(_slot) -> false
                | None -> true) && (match stateTcoLoop(state) with
                | Some(_loop) -> true
                | None -> false) && !lowersBackEdgeArgument(state) && original != marker && (match unspanArgument(tailExpression) with
                | ExprVar(_name) -> true
                | _ -> false) && (match definitionOfTemp(cellTemp)(state.reversedInstructions) with
                | Some(Alloc(_cell, 16, false)) -> true
                | _ -> false) && !isRuntimeTemp(cellTemp)(state) && isPromotableCellMarker(marker)(state) && isBorrowedRetainableParameterType(tailType)(state) && !containsUnresolvedLayout(tailType)(state)
            then
                match freshLocal(state) with
                    | FreshLocal { state = allocated, local = ownedSlot } ->
                        match freshTemp(allocated) with
                            | FreshTemp { state = withTemp, temp = zeroTemp } ->
                                lowered with state = withTemp |> emit(LoadConstInt(zeroTemp)(0)) |> emit(StoreLocal(ownedSlot)(zeroTemp)) |> registerGeneralRcOwnedSlot(ownedSlot)(resolveType(withTemp)(tailType)) |> (given (registered: CoreLoweringState) -> withStateCellMarkerSlots((ownedSlot, marker, zeroTemp) :: stateCellMarkerSlots(registered))(registered))
            else lowered
        | _ -> lowered

let finishConsTail (request: ConsumerRequest) (transfers: Bool) lower headExpression tailExpression head =
    match head with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValue { state = headState, semanticType = headType } ->
            match headState
            |> consTailRequest(request)(tailExpression)(headType)
            |> lower(tailExpression)
            |> ownHeadOfCellOverFreshTail(request)(tailExpression)(head) with
                | (cellRequest, freshOwnedHead, loweredTail) ->
                    match ownChainHeadOverReferenceCountedTail(cellRequest)(headExpression)(tailExpression)(freshOwnedHead)(loweredTail) with
                        | (ownedHead, ownedTail) ->
                            match consPartsFor(cellRequest)(headExpression)(tailExpression)(ownedHead)(ownedTail) with
                                | (duplicatedHead, duplicatedTail) ->
                                    duplicatedTail
                                    |> retainConsTail(cellRequest)(transfers)(headExpression)(tailExpression)(duplicatedHead)
                                    |> finishCons(cellRequest)(headExpression)(tailExpression)(duplicatedHead)
                                    |> reserveArmCellHeadSlot(cellRequest)(tailExpression)
                                    |> keepArmCellTailMarkerOwned(request)(tailExpression)(ownedTail)(duplicatedTail)
                                    |> recordLateCopiedCellHead(cellRequest)(ownedHead)(duplicatedHead)(ownedTail)(duplicatedTail)
                                    |> recordCallArgumentCellOwnerDuplicates(cellRequest)(ownedHead)(duplicatedHead)(ownedTail)(duplicatedTail)
                                    |> noteListHeadCell(tailExpression)(duplicatedHead)(duplicatedTail)
                                    |> keepArgumentExitCellOwned(request)(tailExpression)

// The element type an expected list type asks of a list literal's elements or a cons head.
let expectedListElementType state =
    match expectedTypeOf(state) with
        | None -> None
        | Some(expected) ->
            match resolveType(state)(expected) with
                | SemList(elementType) -> Some(elementType)
                | _ -> None

// The owned children of a copied cell re-established under the current span: the field loads
// and stores carry it, the child copy-outs none, as stage 0's deep-copy emitter leaves them.
let recursive emitLocatedChildCopies (sourceTemp: Int) (copyTemp: Int) (tagless: Bool) (children: List((Int, SemanticType))) (state: CoreLoweringState) =
    match children with
        | [] -> state
        | (index, childType) :: rest ->
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = childTemp } ->
                    match allocated
                    |> emit(GetAdtField(childTemp)(sourceTemp)(index)(tagless))
                    |> emitChildReferenceOrCopy(childTemp)(childCopyPlanOf(childType)(state)) with
                        | (copied, copiedChild) ->
                            copied
                            |> emit(SetAdtField(copyTemp)(index)(copiedChild)(tagless))
                            |> emitLocatedChildCopies(sourceTemp)(copyTemp)(tagless)(rest)

// Stage 0's `NormalizeRuntimeManagedListElement`: the head of a reference-counted cons cell
// whose tail is a loop's list parameter takes an independent reference-counted copy when it
// is not one already (a borrowed pattern owner of the consumed sibling list, whose old root the
// back edge walks), a string or a list over copyable heads by its copy-out, a record by the
// constructor deep copy behind the two temps stage 0's emitters burn ahead of it.
// A head read through a binding that still owns its reference, or through a pattern binding of a
// scrutinee the match adopted: the value is reference-counted already, stage 0's `RuntimeRc`
// representation of the lowered read, and the cell retains it rather than copying it.
let headReadsLiveRuntimeOwner (head: Expr) (state: CoreLoweringState) =
    match unspanArgument(head) with
        | ExprVar(name) ->
            match liveRuntimeOwnerSlot(name)(state) with
                | Some(_slot) -> true
                | None -> false
        | _ -> false

// The cell copy of an arena constructor head: the cell whole, then its owned children, with one
// emitter's unused result temp burned ahead of it as stage 0 burns it.
let emitConsHeadCopy (sourceTemp: Int) (sizeBytes: Int) (tagless: Bool) (children: List((Int, SemanticType))) (state: CoreLoweringState) =
    match freshTempRun(2)(state) with
        | FreshTemp { state = allocated, temp = firstTemp } ->
            let copyTemp = firstTemp + 1
            in
                allocated
                |> unlocatedInstruction(CopyOutArena(copyTemp)(sourceTemp)(sizeBytes)(true)(RcNormalization)(None))
                |> emitLocatedChildCopies(sourceTemp)(copyTemp)(tagless)(children)
                |> (given (copied: CoreLoweringState) -> (copied, copyTemp))

// Stage 0's `NormalizeRuntimeManagedListElement` takes the temp for the normalized head before it
// knows whether the head's type has a normalization at all, so a head it leaves as it is still
// costs that one temp.
let reserveUnusedHeadTemp (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, error = None } ->
            match freshTemp(state) with
                | FreshTemp { state = reserved } -> lowered with state = reserved
        | _ -> lowered

// The last tail of a cons chain written out (`separator :: acc` under `first`).
let recursive consChainLastTail (expression: Expr) =
    match unspanArgument(expression) with
        | ExprCons(_head, tail) -> consChainLastTail(tail)
        | other -> other

// A cell's tail that ends in the loop parameter: the parameter's own read, or, for a tail self-call
// argument consing a chain onto its parameter (stage 0's cons tail request), a chain of cells over it.
let consTailReachesLoopParameter (request: ConsumerRequest) (tail: Expr) (state: CoreLoweringState) =
    if loopParameterReadSlot(tail)(state) != None
    then true
    else
        match (unspanArgument(tail), request.runtimeTcoListTailSlot) with
            | (ExprCons(_head, _tail), Some(_slot)) ->
                loopParameterReadSlot(consChainLastTail(tail))(state) != None
            | _ -> false

// Stage 0's `NormalizeRuntimeManagedListElement` normalizes the head of every cell built under a
// loop's list-slot request: a cell over the parameter, or a grown accumulator's fresh start
// (`first :: []`) whose tail is a new list.
let consHeadNormalizedForTailSlot (request: ConsumerRequest) (tail: Expr) (state: CoreLoweringState) = consTailReachesLoopParameter(request)(tail)(state) || request.runtimeTcoListTailSlot != None && startsListAfresh(tail)

// Stage 0's `NormalizeRuntimeManagedBytesValue`: a Bytes head of a reference-counted list cell
// whose buffer is a fresh one or a view borrowed from another is copied onto the reference-counted
// heap, unless it is already a fresh reference-counted buffer, so the cell owns what it points at.
// A head of unknown origin, or a view of a mapping that lives as long as the program, stays as it is.
let bytesHeadNeedsOwnedCopy (head: Expr) (temp: Int) (state: CoreLoweringState) =
    match builtinBytesProvenanceOf(head)(state) with
        | BytesProvenanceFreshOwnedBuffer -> !isRuntimeTemp(temp)(state)
        | BytesProvenanceBorrowedView -> true
        | _ -> false

let normalizeRuntimeManagedBytesHead (request: ConsumerRequest) (head: Expr) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            if requestsRuntimeList(request) && resolveType(state)(semanticType) == SemBytes && bytesHeadNeedsOwnedCopy(head)(temp)(state)
            then
                match freshTemp(state) with
                    | FreshTemp { state = allocated, temp = copyTemp } ->
                        allocated
                        |> emit(CopyOutArena(copyTemp)(temp)(-1)(true)(RcNormalization)(None))
                        |> markRuntimeTemp(copyTemp)(RuntimeNewlyProduced)
                        |> success(copyTemp)(semanticType)
            else lowered
        | _ -> lowered

let normalizeRuntimeManagedHeapConsHead (request: ConsumerRequest) (head: Expr) (tail: Expr) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            if requestsRuntimeList(request) && consHeadNormalizedForTailSlot(request)(tail)(state) && !isRuntimeTemp(temp)(state) && !headReadsLiveRuntimeOwner(head)(state) && !resultSurvivesReset(semanticType)(state) && !headReadsAdmittedLoopParameter(head)(state) && consHeadBytesNormalizable(head)(semanticType)(state)
            then
                match resolveType(state)(semanticType) with
                    | SemString ->
                        match freshTemp(state) with
                            | FreshTemp { state = allocated, temp = copyTemp } ->
                                allocated
                                |> emit(CopyOutArena(copyTemp)(temp)(-1)(true)(RcNormalization)(None))
                                |> markRuntimeTemp(copyTemp)(RuntimeNewlyProduced)
                                |> success(copyTemp)(semanticType)
                    | SemList(element) ->
                        match listHeadCopyKindOf(element)(state) with
                            | Some(headCopy) ->
                                match freshTemp(state) with
                                    | FreshTemp { state = allocated, temp = copyTemp } ->
                                        allocated
                                        |> emit(CopyOutList(copyTemp)(temp)(headCopy)(true)(RcNormalization))
                                        |> markRuntimeTemp(copyTemp)(RuntimeNewlyProduced)
                                        |> success(copyTemp)(semanticType)
                            | None -> reserveUnusedHeadTemp(lowered)
                    | resolved ->
                        if tcoListElementSupported(resolved)(state)
                        then
                            match tcoAdtCopyPlanOf(resolved)(state) with
                                | ConstructorArgumentCopy((_tag, sizeBytes, tagless, _childPlans)) ->
                                    // The head is taken by reference when the runtime answers that
                                    // it is already reference-counted; one emitter's unused result
                                    // temp is burned ahead of the test, as stage 0 burns it.
                                    match freshTemp(state) with
                                        | FreshTemp { state = burned, temp = _unused } ->
                                            match emitReferenceOrCopy(temp)(state
                                            |> ownedChildrenOfNamed(resolved)
                                            |> emitConsHeadCopy(temp)(sizeBytes)(tagless))(burned) with
                                                | (copied, copiedTemp) ->
                                                    copied
                                                    |> markRuntimeTemp(copiedTemp)(RuntimeNewlyProduced)
                                                    |> success(copiedTemp)(semanticType)
                                // A record of the contract's types is copied by its normalization
                                // helper when it is not reference-counted already.
                                | NormalizerArgumentCopy(_named) as plan ->
                                    match freshTemp(state) with
                                        | FreshTemp { state = burned, temp = _unused } ->
                                            match emitReferenceOrCopy(temp)(emitArgumentDeepCopy(temp)(plan))(burned) with
                                                | (copied, copiedTemp) ->
                                                    copied
                                                    |> markRuntimeTemp(copiedTemp)(RuntimeNewlyProduced)
                                                    |> success(copiedTemp)(semanticType)
                                | _ -> reserveUnusedHeadTemp(lowered)
                        else reserveUnusedHeadTemp(lowered)
            else lowered
        | _ -> lowered

// An inline head of a reference-counted cell over a loop's list parameter, which stage 0 leaves as
// it is, costs that unused temp as well.
let normalizeRuntimeManagedConsHead (request: ConsumerRequest) (head: Expr) (tail: Expr) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            if requestsRuntimeList(request) && consHeadNormalizedForTailSlot(request)(tail)(state) && !isRuntimeTemp(temp)(state) && isCopyTypeSemantic(resolveType(state)(semanticType))
            then reserveUnusedHeadTemp(lowered)
            else normalizeRuntimeManagedHeapConsHead(request)(head)(tail)(lowered)
        | _ -> lowered

// Stage 0's `IsRecursiveProducerTail`: a cons whose tail calls a member of the enclosing recursive
// group is the spine of a non-tail recursive producer, one cell per level. Placing those cells in
// the arena makes every level's own call window copy the whole result out before reclaiming it —
// quadratic in the finished list's length. The call site's copy-out is already conditional on the
// callee's own returns bit, which the backfill above now reports accurately, so a cell requested
// here on the reference-counted heap skips that copy instead: the value is reference-counted on
// both sides of that branch either way, so the cell never mixes an arena tail into an RC spine.
let isRecursiveProducerTail (tail: Expr) (state: CoreLoweringState) = recursiveProducerResult(tail)(state)

// A live arena list-cell reuse token beats the rule above: it rebuilds this exact cell in place,
// for zero allocation, where the reuse rule exists precisely for the case a reuse token is NOT
// available — OPT-42's specialized body still rebuilds its accumulator's spine in place, not on
// the reference-counted heap, and `consumeListCellReuseToken` itself declines a token whenever
// the request already asks for a runtime-managed cell, so forcing one here would silently defeat
// the reuse this exact program shape is the reason `f$reuse` specialization exists for.
let listCellReuseTokenAvailable (tokens: List(CoreReuseToken)) =
    match takeListCellReuseToken(tokens) with
        | Some(_found) -> true
        | None -> false

// A cell whose tail is a recursive producer's result is reference-counted when no arena cell can be
// reused for it.
let producerConsRequest (tail: Expr) (state: CoreLoweringState) (request: ConsumerRequest) =
    if isRecursiveProducerTail(tail)(state) && listCellReuseTokenAvailable(state.reuseTokens) == false
    then request with runtimeList = true
    else request

// Stage 0's `EmitClosureResultOrCopy`: a closure's reference-counted result is handed over owned,
// so it moves into the producer's cell as it is, and only an arena result is copied onto the
// reference-counted heap.
let emitClosureResultOrCopy (resultTemp: Int) (plan: ArgumentCopyPlan) (state: CoreLoweringState) =
    match freshLocal(state) with
        | FreshLocal { state = slotted, local = resultSlot } ->
            match freshLabel("producer_head_copy")(slotted) with
                | FreshLabel { state = copyLabelled, label = copyLabel } ->
                    match freshLabel("producer_head_done")(copyLabelled) with
                        | FreshLabel { state = doneLabelled, label = doneLabel } ->
                            match freshTemp(doneLabelled) with
                                | FreshTemp { state = tested, temp = referenceCountedTemp } ->
                                    match tested
                                    |> emit(IsReferenceCounted(referenceCountedTemp)(resultTemp))
                                    |> emit(JumpIfFalse(referenceCountedTemp)(copyLabel))
                                    |> emit(StoreLocal(resultSlot)(resultTemp))
                                    |> emit(Jump(doneLabel))
                                    |> emit(Label(copyLabel))
                                    |> emitGuardedDeepCopy(resultTemp)(plan) with
                                        | (copied, copyTemp) ->
                                            match copied
                                            |> emit(StoreLocal(resultSlot)(copyTemp))
                                            |> emit(Label(doneLabel))
                                            |> freshTemp with
                                                | FreshTemp { state = resulted, temp = ownedTemp } ->
                                                    (emit(LoadLocal(ownedTemp)(resultSlot))(resulted), ownedTemp)

// The owned reference-counted head of a producer's cell: a closure call's result is taken over or
// copied, any other head takes a reference or a copy of its own.
let emitProducerHeadCopy (headTemp: Int) (plan: ArgumentCopyPlan) (state: CoreLoweringState) =
    match definitionOfTemp(headTemp)(state.reversedInstructions) with
        | Some(CallClosure(_target, _closure, _argument, _flag)) -> emitClosureResultOrCopy(headTemp)(plan)(state)
        | _ -> emitGuardedDeepCopy(headTemp)(plan)(state)

let isProducerCellHeadType (semanticType: SemanticType) =
    match semanticType with
        | SemTuple(_elements) -> true
        | SemString -> true
        | _ -> false

// Stage 0's `OwnBorrowedHeadForProducerCell`: the tail of a recursive producer's cell is
// reference-counted on both sides of the call site's copy-out branch, so a tuple or string head
// the cell cannot count (a borrowed read out of an arena-placed parameter, a string literal, a
// binding whose value may live in the arena) takes an owned reference-counted copy, and the cell
// is reference-counted too instead of an arena cell nothing releases that tail from.
let ownBorrowedHeadForProducerCell (producerCell: Bool) (request: ConsumerRequest) (headExpression: Expr) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            let resolved = resolveType(state)(semanticType)
            in
                if producerCell && generalRcEnabled(state) && requestsRuntimeList(request) && isProducerCellHeadType(resolved) && !isRuntimeManageableListElement(headExpression)(temp)(resolved)(state)
                then
                    match generalCopyPlanOf(resolved)(state) with
                        | Some(plan) ->
                            match emitProducerHeadCopy(temp)(plan)(state) with
                                | (copied, ownedTemp) ->
                                    copied
                                    |> markRuntimeTemp(ownedTemp)(RuntimeNewlyProduced)
                                    |> success(ownedTemp)(semanticType)
                        | None -> lowered
                else lowered
        | _ -> lowered

let recursive consChainEndsInVariable (expression: Expr) =
    match unspanArgument(expression) with
        | ExprCons(_head, tail) -> consChainEndsInVariable(tail)
        | ExprVar(_name) -> true
        | _ -> false

// Stage 0's `CollectCopiedInnerListCells`: the head of a copied successor cell that is itself a
// chain of cons cells over a variable, in a list of lists of copy-type values.
let isCopiedInnerListCell (request: ConsumerRequest) (elementType: Maybe(SemanticType)) (head: Expr) (state: CoreLoweringState) =
    match (request.borrowsCellParts, unspanArgument(head), elementType) with
        | (true, ExprCons(_innerHead, _innerTail), Some(element)) ->
            match resolveType(state)(element) with
                | SemList(innerElement) -> resultSurvivesReset(innerElement)(state) && consChainEndsInVariable(head)
                | _ -> false
        | _ -> false

let consHeadRequest (request: ConsumerRequest) (elementType: Maybe(SemanticType)) (transfers: Bool) (head: Expr) (state: CoreLoweringState) =
    (let headRequest = listElementRequest(request)(elementType)(transfers)(head)(state)
    in
        if isCopiedInnerListCell(request)(elementType)(head)(state)
        then headRequest with borrowsCellParts = true, copiedInnerCell = true
        else headRequest)

let lowerCons head tail lower state =
    match consumerRequestOf(state) with
        | outerRequest ->
            let request =
                outerRequest
                |> producerConsRequest(tail)(state)
                |> loopExitCellRequest(tail)(state)
            in
                state
                |> markResourceArgumentsMoved([head, tail])
                |> withConsumerRequest(consHeadRequest(request)(expectedListElementType(state))(listTransfers(request)(state))(head)(state))
                |> lower(head)
                |> normalizeRuntimeManagedBytesHead(request)(head)
                |> normalizeRuntimeManagedConsHead(request)(head)(tail)
                |> normalizePatternOwnerHead(request)(head)
                |> retainListElement(request)(listTransfers(request)(state))(head)
                |> ownBorrowedHeadForProducerCell(isRecursiveProducerTail(tail)(state))(request)(head)
                |> finishConsTail(request)(listTransfers(request)(state))(lower)(head)(tail)

let emptyList state =
    match freshType(state) with
        | FreshType { state = typedState, semanticType = elementType } ->
            lowerConstant(given (target) -> LoadConstInt(target)(0))(SemList(elementType))(typedState)

// Stage 0's `IsFreshReferenceCountedCell`: whether the list cell just emitted into `cellTemp` was
// allocated in the reference-counted region. A cell that reused a token, or whose head kept it in
// the arena, was not, and the cell that takes it as a tail has to test it.
let recursive freshReferenceCountedCellIn (cellTemp: Int) (remaining: Int) (instructions: List(IrInstruction)) =
    if remaining == 0
    then false
    else
        match instructions with
            | [] -> false
            | IrInstruction { instruction = Alloc(target, _sizeBytes, runtimeManaged) } :: rest ->
                if target == cellTemp
                then runtimeManaged
                else freshReferenceCountedCellIn(cellTemp)(remaining - 1)(rest)
            | _ :: rest -> freshReferenceCountedCellIn(cellTemp)(remaining - 1)(rest)

let isFreshReferenceCountedCell (cellTemp: Int) (state: CoreLoweringState) = freshReferenceCountedCellIn(cellTemp)(4)(state.reversedInstructions)

// Stage 0's `ownTail` half of `LowerConsCell`. A reference-counted cell never points at arena
// memory: a literal's later cells are placed by their own heads, so a tail left in the arena (a
// static string's cell) is copied in. The literal's own tail is the empty list, then the cell
// before it, so only a cell that was left in the arena needs its representation tested.
let ownedLiteralTail (runtimeManaged: Bool) (tailIsReferenceCounted: Bool) (tailTemp: Int) elementType (state: CoreLoweringState) =
    if canTestRepresentation(state) && runtimeManaged && tailIsReferenceCounted == false
    then
        match argumentCopyPlanOf(elementType
        |> resolveType(state)
        |> SemList)(state) with
            | Some(plan) ->
                emitReferenceCountedListTail(tailTemp)(entryCopyPlanOf(elementType
                |> resolveType(state)
                |> SemList)(plan)(state))(state)
            | None -> (state, tailTemp)
    else (state, tailTemp)

// The tail a list literal's element is normalized against: stage 0's
// `NormalizeRuntimeManagedListElement` reads the request alone, so an element of a literal a grown
// accumulator is started afresh with is normalized as the head of `first :: []` is.
let literalElementTail = ExprList([])(false)

// Stage 0's `CopyArenaHeadOverReferenceCountedTail`: a literal's cell over a reference-counted
// cell is reference-counted too. An arena cell would be the only holder of the cell after it, and
// the arena is reclaimed without releasing what its cells point at, so a head the arena would keep
// (a static string, a constructor over one) is copied onto the reference-counted heap, which lets
// its cell follow.
let copyArenaHeadOverReferenceCountedTail (request: ConsumerRequest) (tailIsFreshCell: Bool) (expression: Expr) (headTemp: Int) (headType: SemanticType) (state: CoreLoweringState) =
    if tailIsFreshCell && requestsRuntimeList(request) && !isRuntimeManageableListElement(expression)(headTemp)(headType)(state) && isRuntimeManageableHeapChild(headType)(false)(state)
    then
        match argumentCopyPlanOf(resolveType(state)(headType))(state) with
            | Some(plan) ->
                match emitArgumentDeepCopy(headTemp)(entryCopyPlanOf(resolveType(state)(headType))(plan)(state))(state) with
                    | (copied, copiedTemp) -> (markRuntimeTemp(copiedTemp)(RuntimeNewlyProduced)(copied), copiedTemp)
            | None -> (state, headTemp)
    else (state, headTemp)

// A literal the back edge copies (`[first]` started afresh as a successor) borrows its heads like
// a copied cons cell, so a head produced right there keeps an owner of its own.
let keepCopiedLiteralHeadOwned (request: ConsumerRequest) (expression: Expr) (headTemp: Int) (headType: SemanticType) (state: CoreLoweringState) =
    if request.borrowsCellParts
    then keepCopiedCellFreshHeadOwned(expression)(headTemp)(headType)(state)
    else state

// Stage 0's `KeepArenaListLiteralFreshHeadOwned`: a literal's arena cell holding a value produced right
// there (a constructor placed on the reference-counted heap because it owns a fresh string) is that
// value's only holder, and an arena cell releases nothing, so its reference goes to an owned slot.
// A fresh string is retained wherever a callee keeps it, so its cell's reference goes to an owned
// slot too (stage 0's `IsLiteralFreshStringHead`). A retain the call the literal is an argument of
// collected is released by that call, and takes no slot.
let keepArenaListLiteralFreshHeadOwned (cellTemp: Int) (headTemp: Int) (cellHeadTemp: Int) (headType: SemanticType) (state: CoreLoweringState) =
    if cellHeadTemp == headTemp && !isFreshReferenceCountedCell(cellTemp)(state)
    then
        if resolveType(state)(headType) == SemString
        then
            if isFreshRuntimeTemp(headTemp)(state) && !collectsCallArgumentRetain(headTemp)(stateCallArgumentRecordRetains(state))
            then
                match freshLocal(state) with
                    | FreshLocal { state = allocated, local = ownedSlot } ->
                        allocated
                        |> emit(StoreLocal(ownedSlot)(headTemp))
                        |> registerGeneralRcOwnedSlot(ownedSlot)(SemString)
            else state
        else
            match state
            |> success(cellTemp)(headType)
            |> keepEscapingArenaCellFreshHeadOwned(true)(headTemp)(headType) with
                | LoweredCoreValue { state = kept } -> kept
    else state

let recursive lowerListElements (request: ConsumerRequest) (transfers: Bool) elements elementType tailTemp (tailIsReferenceCounted: Bool) (tailIsFreshCell: Bool) lower state =
    match elements with
        | [] ->
            success(tailTemp)(elementType
            |> resolveType(state)
            |> SemList)(state)
        | expression :: rest ->
            match state
            |> withConsumerRequest(listElementRequest(request)(Some(elementType))(transfers)(expression)(state))
            |> lower(expression)
            |> normalizeRuntimeManagedBytesHead(request)(expression)
            |> normalizeRuntimeManagedConsHead(request)(expression)(literalElementTail)
            |> normalizePatternOwnerHead(request)(expression)
            |> retainListElement(request)(transfers)(expression) with
                | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                | LoweredCoreValue { state = valueState, temp = headTemp, semanticType = headType, error = None } ->
                    match valueState
                    |> keepCopiedLiteralHeadOwned(request)(expression)(headTemp)(headType)
                    |> bindType(elementType)(headType) with
                        | (failedState, Some(error)) -> failure(failedState)(error)
                        | (boundState, None) ->
                            match copyArenaHeadOverReferenceCountedTail(request)(tailIsFreshCell)(expression)(headTemp)(headType)(boundState) with
                                | (typedState, cellHeadTemp) ->
                                    let runtimeManaged = cellIsRuntimeManaged(request)(expression)(cellHeadTemp)(headType)(typedState)
                                    in
                                        match ownedLiteralTail(runtimeManaged)(tailIsReferenceCounted)(tailTemp)(elementType)(typedState) with
                                            | (tailState, ownedTailTemp) ->
                                                match allocateListCell(cellHeadTemp)(ownedTailTemp)(elementType)(runtimeManaged)(tailState) with
                                                    | LoweredCoreValue { state = cellState, temp = cellTemp, error = None } ->
                                                        lowerListElements(
                                                            request,
                                                            transfers,
                                                            rest,
                                                            elementType,
                                                            cellTemp,
                                                            isFreshReferenceCountedCell(cellTemp)(cellState),
                                                            isFreshReferenceCountedCell(cellTemp)(cellState),
                                                            lower,
                                                            keepArenaListLiteralFreshHeadOwned(cellTemp)(headTemp)(cellHeadTemp)(headType)(cellState)
                                                        )
                                                    | failed -> failed

let finishListLiteral (request: ConsumerRequest) (transfers: Bool) elements lower empty =
    match empty with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValue { state = emptyState, temp = emptyTemp, semanticType = SemList(elementType), error = None } ->
            lowerListElements(request)(transfers)(reverse(elements))(elementType)(emptyTemp)(true)(false)(lower)(emptyState)
        | LoweredCoreValue { state = failedState } ->
            failure(
                failedState,
                UnsupportedCoreLoweringExpression("invalid empty list type")
            )

let expectedOrFreshEmptyList state =
    match expectedListElementType(state) with
        | Some(elementType) ->
            state
            |> clearConsumerRequest
            |> lowerConstant(given (target) -> LoadConstInt(target)(0))(SemList(elementType))
        | None ->
            state
            |> clearConsumerRequest
            |> emptyList

let lowerListLiteral elements lower state =
    match consumerRequestOf(state) with
        | request ->
            state
            |> markResourceArgumentsMoved(elements)
            |> expectedOrFreshEmptyList
            |> finishListLiteral(request)(listTransfers(request)(state))(elements)(lower)

let recursive emitAdtFields baseTemp index tagless temps state =
    match temps with
        | [] -> state
        | temp :: rest ->
            state
            |> emit(SetAdtField(baseTemp)(index)(temp)(tagless))
            |> emitAdtFields(baseTemp)(index + 1)(tagless)(rest)

// OPT-42 consumer: a same-name rebuild of a dead matched cell reuses it in place instead of
// allocating fresh, when a compatible token is live and the consumer's own placement request
// already asked for a reference-counted cell (`runtimeManaged`) — `AshesCompiler.Semantics.
// ReuseSpecialization`'s `reuseCaseSafe` is the sole gate deciding whether that token exists at
// all, so a rebuild reaching this function's fallback path never carries one.
let recursive reuseFindMatchingToken (ctorName: Str) (fieldCount: Int) (tagless: Bool) (tokens: List(CoreReuseToken)) =
    match tokens with
        | [] -> None
        | (CoreReuseToken { constructorName = candidateName, fieldCount = candidateFieldCount, tagless = candidateTagless } as token) :: rest ->
            if candidateName == ctorName && candidateFieldCount == fieldCount && candidateTagless == tagless
            then Some((token, rest))
            else
                match reuseFindMatchingToken(ctorName)(fieldCount)(tagless)(rest) with
                    | Some((found, remaining)) -> Some((found, token :: remaining))
                    | None -> None

let reuseConsumeToken (ctorName: Str) (fieldCount: Int) (tagless: Bool) (state: CoreLoweringState) =
    match reuseFindMatchingToken(ctorName)(fieldCount)(tagless)(state.reuseTokens) with
        | Some((token, remaining)) -> Some((token, (state with reuseTokens = remaining)))
        | None -> None

// Passes a rebuild field straight through when the token's cell was reused in place (the runtime
// `DropReuse` found it unique), or duplicates it when `DropReuse` instead found the matched cell
// shared and only decremented it — the transferred field is then a NEW alias the surviving old
// cell does not already account for. Stage 0's `EmitRuntimeReuseTransferredChild`.
let reuseEmitTransferredChild (fieldTemp: Int) (tokenTemp: Int) (state: CoreLoweringState) =
    match freshLocal(state) with
        | FreshLocal { state = afterSlot, local = resultSlot } ->
            match freshTemp(afterSlot) with
                | FreshTemp { state = afterZero, temp = zeroTemp } ->
                    match afterZero
                    |> emit(LoadConstInt(zeroTemp)(0))
                    |> freshTemp with
                        | FreshTemp { state = afterHasToken, temp = hasTokenTemp } ->
                            match afterHasToken
                            |> emit(CmpIntNe(hasTokenTemp)(tokenTemp)(zeroTemp))
                            |> freshLabel("reuse_child_duplicate") with
                                | FreshLabel { state = afterDupLabel, label = duplicateLabel } ->
                                    match freshLabel("reuse_child_continue")(afterDupLabel) with
                                        | FreshLabel { state = afterContLabel, label = continueLabel } ->
                                            match afterContLabel
                                            |> emit(JumpIfFalse(hasTokenTemp)(duplicateLabel))
                                            |> emit(StoreLocal(resultSlot)(fieldTemp))
                                            |> emit(Jump(continueLabel))
                                            |> emit(Label(duplicateLabel))
                                            |> freshTemp with
                                                | FreshTemp { state = afterDupTemp, temp = duplicatedTemp } ->
                                                    match afterDupTemp
                                                    |> emit(RcDup(duplicatedTemp)(fieldTemp)(true)(false))
                                                    |> emit(StoreLocal(resultSlot)(duplicatedTemp))
                                                    |> emit(Label(continueLabel))
                                                    |> freshTemp with
                                                        | FreshTemp { state = finalState, temp = resultTemp } ->
                                                            (emit(LoadLocal(resultTemp)(resultSlot))(finalState), resultTemp)

let recursive reuseApplyTransferredChildren (index: Int) (pointerIndices: List(Int)) (tokenTemp: Int) (temps: List(Int)) (state: CoreLoweringState) =
    match temps with
        | [] -> (state, [])
        | fieldTemp :: rest ->
            if containsInt(index)(pointerIndices)
            then
                match reuseEmitTransferredChild(fieldTemp)(tokenTemp)(state) with
                    | (nextState, transferredTemp) ->
                        match reuseApplyTransferredChildren(index + 1)(pointerIndices)(tokenTemp)(rest)(nextState) with
                            | (finalState, restTemps) -> (finalState, transferredTemp :: restTemps)
            else
                match reuseApplyTransferredChildren(index + 1)(pointerIndices)(tokenTemp)(rest)(state) with
                    | (finalState, restTemps) -> (finalState, fieldTemp :: restTemps)

let recursive allCopyTypeElements (elements: List(SemanticType)) =
    match elements with
        | [] -> true
        | element :: rest -> canArenaResetLayout(element) && allCopyTypeElements(rest)

// A field whose argument is a name the enclosing specialization did not bring in as a fresh input
// was matched out of the accumulator, so it already lives where the accumulator does and needs no
// relocation. Every other shape — a fresh input by name, or any expression the arm computed — is
// per-iteration arena scratch. Over-materializing an already-persistent value costs a copy, never
// correctness.
let specializationFieldIsPersistent (argument: Expr) (freshInputs: List(Str)) =
    match unspanArgument(argument) with
        | ExprVar(name) -> containsName(name)(freshInputs) == false
        | _ -> false

// Relocates a value into to-space, the region the loop's watermark reset never rewinds. A negative
// size copies a length-prefixed blob (a string or bytes value); a positive one a fixed-size cell.
let emitSpecializationToSpaceCopy (fieldTemp: Int) (sizeBytes: Int) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = copyState, temp = destinationTemp } ->
            (emit(CopyOutArenaToSpace(destinationTemp)(fieldTemp)(sizeBytes))(copyState), destinationTemp)

// Stage 0's `MaterializeSpecializationStringField`/`MaterializeSpecializationTupleField` update
// half: the reused cell's old field is unreferenced, so the new value overwrites the old blob in
// place when the backend's runtime check finds it persistent, and materializes fresh otherwise.
// This bounds blob growth to the largest value a cell ever held instead of leaving one blob per
// rewrite behind. `oldCellTemp` is the cell whose field still holds the old value
// (`reusedCellOldFieldSource`).
let emitSpecializationInPlaceCopy (oldCellTemp: Int) (fieldIndex: Int) (tagless: Bool) (fieldTemp: Int) (sizeBytes: Int) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = oldState, temp = oldFieldTemp } ->
            match oldState
            |> emit(GetAdtField(oldFieldTemp)(oldCellTemp)(fieldIndex)(tagless))
            |> freshTemp with
                | FreshTemp { state = copyState, temp = destinationTemp } ->
                    if sizeBytes < 0
                    then
                        (emit(CopyStringIntoOrFresh(destinationTemp)(oldFieldTemp)(fieldTemp))(copyState), destinationTemp)
                    else
                        (emit(CopyFixedIntoOrFresh(destinationTemp)(oldFieldTemp)(fieldTemp)(sizeBytes))(copyState), destinationTemp)

let recursive fieldIndexIsUnbound (fieldIndex: Int) (fieldBindings: List((Int, Str))) =
    match fieldBindings with
        | [] -> true
        | (boundIndex, _name) :: rest -> boundIndex != fieldIndex && fieldIndexIsUnbound(fieldIndex)(rest)

// Stage 0's `ReuseTokenFieldIsDead`, restricted to the half a name alone decides: a field the
// matched pattern bound to no name can no longer be referenced on this path, so the reused cell's
// old blob is free to overwrite. Stage 0 additionally clears a field whose bound name has had every
// arm reference lowered already, which needs the per-binding reference tally this lowering does not
// keep; without it a bound field takes the fresh path, which costs a blob and never correctness.
let specializationFieldSlotIsDead (fieldIndex: Int) (token: Maybe(CoreReuseToken)) =
    match token with
        | None -> false
        | Some(CoreReuseToken { fieldBindings = fieldBindings }) -> fieldIndexIsUnbound(fieldIndex)(fieldBindings)

// Stage 0's `ReusedCellOldFieldSource`: the cell an update reads its dead old value from. A reused
// arena cell is its token, but the backend leaves a token that turns out reference-counted alone
// and returns a fresh cell, whose fields hold nothing yet; the token's fields are valid either
// way. A runtime-managed reuse may have no token at all, and keeps reading the cell it produced.
let reusedCellOldFieldSource (cellTemp: Int) (token: Maybe(CoreReuseToken)) =
    match token with
        | None -> cellTemp
        | Some(CoreReuseToken { temp = tokenTemp, runtimeManaged = runtimeManaged }) ->
            if runtimeManaged
            then cellTemp
            else tokenTemp

// A field whose own type is the type being rebuilt is a recursive child of the accumulator: the
// recursive call that produced it is part of the specialization, so it is already a reuse-managed
// cell. Relocating it would be redundant, and — since the copier it needs is self-recursive — would
// leave the reset-safety scan looking at an escaping closure, defeating the very reset this
// mechanism exists to enable. Stage 0 compares the two type symbols, which the name and its symbol
// id are here.
let specializationFieldIsAccumulatorSelf (fieldType: SemanticType) (resultType: SemanticType) =
    match (fieldType, resultType) with
        | (SemNamed(fieldSymbol, fieldName, _fieldArguments), SemNamed(resultSymbol, resultName, _resultArguments)) -> fieldSymbol == resultSymbol && fieldName == resultName
        | _ -> false

// A list or ADT leaf has no in-place update primitive, so it is always rebuilt fresh through the
// synthesized to-space copier rather than over an update's dead cell — stage 0 makes the same
// choice for the same reason.
let relocateSpecializationField (fieldType: SemanticType) (fieldTemp: Int) (state: CoreLoweringState) =
    if specializationFieldRelocatable(fieldType)(state)
    then
        match state with
            | CoreLoweringState { nextTemp = nextTemp, nextLocal = nextLocal, nextLambdaId = lambdaId, nextLabelId = labelId, programState = CoreProgramState { dropperLabels = cache } } ->
                match synthesizeToSpaceCopy(fieldTemp)(resolveType(state)(fieldType))(dropperTypesOf(state))(cache)(nextTemp)(nextLocal)(lambdaId)(labelId) with
                    | (synthesis, resultTemp) -> (spliceInlineReleaseWith(unlocatedInstruction)(synthesis)(state), resultTemp)
    else (state, fieldTemp)

// Stage 0's `MaterializeSpecializationField`. A constructor field built from one of the
// specialization's fresh inputs points into the arena scratch the loop's per-iteration reset
// reclaims, so the rebuilt cell would outlive its own field; the copy relocates the value into
// to-space, which the reset never touches. A copy type is inline and needs nothing. A string, bytes
// or copy-tuple leaf is a direct blob copy; a list of strings and a non-self ADT go through the
// copiers `ToSpaceCopiers.ash` synthesizes for them.
let materializeSpecializationField (argument: Expr) (fieldType: SemanticType) (resultType: SemanticType) (token: Maybe(CoreReuseToken)) (cellTemp: Int) (fieldIndex: Int) (tagless: Bool) (fieldTemp: Int) (state: CoreLoweringState) =
    match stateSpecializationFreshInputs(state) with
        | None -> (state, fieldTemp)
        | Some(freshInputs) ->
            if specializationFieldIsPersistent(argument)(freshInputs)
            then (state, fieldTemp)
            else
                let materialize =
                    if specializationFieldSlotIsDead(fieldIndex)(token)
                    then
                        emitSpecializationInPlaceCopy(reusedCellOldFieldSource(cellTemp)(token))(fieldIndex)(tagless)(fieldTemp)
                    else emitSpecializationToSpaceCopy(fieldTemp)
                in
                    match resolveType(state)(fieldType) with
                        | SemString -> materialize(-1)(state)
                        | SemBytes -> materialize(-1)(state)
                        | SemTuple(elements) ->
                            if allCopyTypeElements(elements)
                            then materialize(8 * length(elements))(state)
                            else (state, fieldTemp)
                        | SemList(element) -> relocateSpecializationField(SemList(element))(fieldTemp)(state)
                        | SemNamed(_symbolId, _typeName, _arguments) as namedFieldType ->
                            if resultType
                            |> resolveType(state)
                            |> specializationFieldIsAccumulatorSelf(namedFieldType)
                            then (state, fieldTemp)
                            else relocateSpecializationField(namedFieldType)(fieldTemp)(state)
                        | _ -> (state, fieldTemp)

let recursive materializeSpecializationFields (arguments: List(Expr)) (fieldTypes: List(SemanticType)) (resultType: SemanticType) (token: Maybe(CoreReuseToken)) (cellTemp: Int) (fieldIndex: Int) (tagless: Bool) (temps: List(Int)) (reversed: List(Int)) (state: CoreLoweringState) =
    match (arguments, fieldTypes, temps) with
        | (argument :: restArguments, fieldType :: restTypes, temp :: restTemps) ->
            match materializeSpecializationField(argument)(fieldType)(resultType)(token)(cellTemp)(fieldIndex)(tagless)(temp)(state) with
                | (materialized, materializedTemp) -> materializeSpecializationFields(restArguments)(restTypes)(resultType)(token)(cellTemp)(fieldIndex + 1)(tagless)(restTemps)(materializedTemp :: reversed)(materialized)
        | _ ->
            (state, append(reverse(reversed))(temps))

// The materialization runs only inside a specialization; outside one every field passes through and
// the walk is skipped so no ordinary constructor pays for it.
let materializeConstructorFields (arguments: List(Expr)) (fieldTypes: List(SemanticType)) (resultType: SemanticType) (token: Maybe(CoreReuseToken)) (cellTemp: Int) (tagless: Bool) (temps: List(Int)) (state: CoreLoweringState) =
    match stateSpecializationFreshInputs(state) with
        | None -> (state, temps)
        | Some(_freshInputs) -> materializeSpecializationFields(arguments)(fieldTypes)(resultType)(token)(cellTemp)(0)(tagless)(temps)([])(state)

// Stage 0's `_inSpecialization`: a cell built while a reuse specialization's body is being lowered
// belongs to the accumulator the loop rewrites in place, and the loop's per-iteration reset must not
// reclaim it.
let specializationCellsArePersistent (state: CoreLoweringState) =
    match stateSpecializationFreshInputs(state) with
        | None -> false
        | Some(_freshInputs) -> true

// Stage 0's `EmitFreshConstructorCell`: a cell a rebuild could not take from a reuse token is fresh.
// A `stackAllocate` request comes from a scope that proved the cell dead at the end of the frame, so
// it is built in the frame itself and nothing reclaims it. Inside a specialization the cell goes into
// the never-reset to-space, so the accumulator it becomes part of survives the loop's own arena
// reset; that placement is also what keeps the body's reset safe, since the scan rejects a plain
// `AllocAdt` and never a to-space cell. Otherwise the cell takes the consumer's placement request.
let emitFreshConstructorCell (resultTemp: Int) (tag: Int) (fieldCount: Int) (stackAllocate: Bool) (runtimeManaged: Bool) (tagless: Bool) (state: CoreLoweringState) =
    if stackAllocate
    then
        emit(AllocAdtStack(resultTemp)(tag)(fieldCount)(tagless))(state)
    else
        if specializationCellsArePersistent(state)
        then
            state
            |> emit(AllocAdtToSpace(resultTemp)(tag)(fieldCount)(tagless))
            |> markReuseResultTemp(resultTemp)
        else
            emit(AllocAdt(resultTemp)(tag)(fieldCount)(runtimeManaged)(tagless))(state)

// The cell's temp is taken before the transferred children are guarded, stage 0's order. Only a
// reference-counted token's transferred children are guarded; an arena token's cell keeps the
// arena and its fields pass straight through. The fresh-input fields are materialized after the
// cell is claimed and before the stores, so the update path can read the old field off it.
let allocateReusedConstructorCell (arguments: List(Expr)) (argumentTypes: List(SemanticType)) (token: CoreReuseToken) (tag: Int) (fieldCount: Int) (tagless: Bool) (layout: CoreConstructorLayout) (resultType: SemanticType) (temps: List(Int)) (state: CoreLoweringState) =
    match (layoutFieldTypes(layout)(state), freshTemp(state)) with
        | ((fieldTypes, _fieldsResultType), FreshTemp { state = allocatedState, temp = resultTemp }) ->
            match reuseApplyTransferredChildren(0)(if token.runtimeManaged
            then reusePointerFieldIndices(fieldTypes)(allocatedState)
            else [])(token.temp)(temps)(allocatedState) with
                | (transferredState, transferredTemps) ->
                    match transferredState
                    |> emit(AllocReusing(resultTemp)(tag)(fieldCount)(token.temp)(token.runtimeManaged)(false)(tagless))
                    |> markReuseResultTemp(resultTemp)
                    |> materializeConstructorFields(arguments)(argumentTypes)(resultType)(Some(token))(resultTemp)(tagless)(transferredTemps) with
                        | (materializedState, materializedTemps) ->
                            materializedState
                            |> emitAdtFields(resultTemp)(0)(tagless)(materializedTemps)
                            |> markAggregateRuntimeManaged(resultTemp)(token.runtimeManaged)
                            |> success(resultTemp)(resolveType(materializedState)(resultType))

// A live token of the rebuilt constructor's layout is consumed whatever the consumer's own
// placement request: the token was published by the arm that matched this same constructor
// (`reuseCaseSafe`), and its cell keeps the matched value's reference-counted placement.
let allocateOrReuseConstructorCell (arguments: List(Expr)) (argumentTypes: List(SemanticType)) (stackAllocate: Bool) (ctorName: Str) (tag: Int) (tagless: Bool) (layout: CoreConstructorLayout) (resultType: SemanticType) (runtimeManaged: Bool) (temps: List(Int)) (state: CoreLoweringState) =
    (let fieldCount = coreListLength(temps)
    in
        match arenaCellFields(argumentTypes)(runtimeManaged)(temps)(state) with
            | (fieldState, fieldTemps) ->
                match if stackAllocate
                then None
                else reuseConsumeToken(ctorName)(fieldCount)(tagless)(fieldState) with
                    | Some((token, consumedState)) -> allocateReusedConstructorCell(arguments)(argumentTypes)(token)(tag)(fieldCount)(tagless)(layout)(resultType)(fieldTemps)(consumedState)
                    | None ->
                        match freshTemp(fieldState) with
                            | FreshTemp { state = allocatedState, temp = resultTemp } ->
                                // `runtimeManaged` is the consumer's placement request: the dead top-level
                                // `let` path's flag carried by the constructor shape, or a request the
                                // application's shape can honor (`isRuntimeManagedConstructorCandidate`);
                                // without one the cell is arena-placed.
                                match allocatedState
                                |> emitFreshConstructorCell(resultTemp)(tag)(fieldCount)(stackAllocate)(runtimeManaged)(tagless)
                                |> materializeConstructorFields(arguments)(argumentTypes)(resultType)(None)(resultTemp)(tagless)(fieldTemps) with
                                    | (materializedState, materializedTemps) ->
                                        materializedState
                                        |> emitAdtFields(resultTemp)(0)(tagless)(materializedTemps)
                                        |> markAggregateRuntimeManaged(resultTemp)(stackAllocate == false && specializationCellsArePersistent(state) == false && runtimeManaged)
                                        |> success(resultTemp)(resolveType(materializedState)(resultType)))

let finishConstructorAllocation arguments (stackAllocate: Bool) layout resultType runtimeManaged lowered =
    match (layout, lowered) with
        | (_layout, LoweredCoreValues { state = failedState, error = Some(error) }) -> failure(failedState)(error)
        | (CoreConstructorLayout { isZeroCost = true }, LoweredCoreValues { state = state, temps = temp :: [], error = None }) ->
            success(temp)(resolveType(state)(resultType))(state)
        | (CoreConstructorLayout { name = name, isZeroCost = true }, LoweredCoreValues { state = state, temps = temps, error = None }) ->
            temps
            |> coreListLength
            |> CoreConstructorArityMismatch(name)(1)
            |> failure(state)
        | (CoreConstructorLayout { name = ctorName, tag = tag, tagless = tagless } as ctorLayout, LoweredCoreValues { state = state, temps = temps, semanticTypes = semanticTypes, error = None }) -> allocateOrReuseConstructorCell(arguments)(semanticTypes)(stackAllocate)(ctorName)(tag)(tagless)(ctorLayout)(resultType)(runtimeManaged)(temps)(state)
