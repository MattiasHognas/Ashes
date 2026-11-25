// Constructor applications and their field ownership, and builtin calls. A part of
// `AshesCompiler.Semantics.CoreLowering`.

import Ashes.Collection.List.append
import Ashes.Collection.List.reverse
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Semantics.AggregateOwnership
import AshesCompiler.Semantics.CoreBuiltinLowering
import AshesCompiler.Semantics.HeapLayoutClassification
import AshesCompiler.Semantics.IrControlFlowGraph.containsInt
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.PatternBindingOwnership
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.CoreLowering.LoweringState
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.shadowSiteTypeText
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.shadowSiteType
import AshesCompiler.Semantics.CoreLowering.Emit
import AshesCompiler.Semantics.CoreLowering.Owners
import AshesCompiler.Semantics.CoreLowering.CopyPlans
import AshesCompiler.Semantics.CoreLowering.TcoReset
import AshesCompiler.Semantics.CoreLowering.ResultHandOver
import AshesCompiler.Semantics.CoreLowering.Bindings
import AshesCompiler.Semantics.CoreLowering.CallArguments
import AshesCompiler.Semantics.CoreLowering.CallResults
import AshesCompiler.Semantics.CoreLowering.RecursiveGroups
import AshesCompiler.Semantics.CoreLowering.Aggregates
export (
    value keepLoopResultFieldCellHeadDuplicateOwned,
    value retainEscapingArgumentRead,
    value constructorArgumentRequest,
    value handsFreshChildrenToOwnedSlots,
    value handFreshChildToOwnedSlot,
    value lowerConstructor,
    value lowerBuiltin,
    value collectCallSpine,
)

let nullaryConstructorRuntimeManageable (resultType: SemanticType) (state: CoreLoweringState) =
    match heapFactsOf(resultType)(state) with
        | facts -> copyAdtSupported(facts) || recordAdtSupported(facts) || ownedChildAdtSupported(facts) || canRuntimeManageRecursiveCopyAdt(resultType)(state)

let recursive allRecursiveCopyFields (arguments: List(Expr)) (fieldTypes: List(SemanticType)) (typeName: Str) (state: CoreLoweringState) =
    match (arguments, fieldTypes) with
        | ([], []) -> true
        | (argument :: restArguments, fieldType :: restTypes) -> (resultSurvivesReset(fieldType)(state) || isFreshConstructorTree(argument)(typeName)(state)) && allRecursiveCopyFields(restArguments)(restTypes)(typeName)(state)
        | _ -> false

// Stage 0's `CanRuntimeManageRecursiveAdtConstructorApplication` without tracked child bindings.
let canRuntimeManageRecursiveAdtApplication (layout: CoreConstructorLayout) (arguments: List(Expr)) (state: CoreLoweringState) =
    match layoutFieldTypes(layout)(state) with
        | (fieldTypes, resultType) ->
            match namedTypeNameOf(resultType) with
                | Some(name) -> canRuntimeManageRecursiveCopyAdt(resultType)(state) && allRecursiveCopyFields(arguments)(fieldTypes)(name)(state)
                | None -> false

let adtRequestHonored (layout: CoreConstructorLayout) (arguments: List(Expr)) (fieldTypes: List(SemanticType)) (resultType: SemanticType) (state: CoreLoweringState) =
    copyAdtSupported(heapFactsOf(resultType)(state)) || canRuntimeManageGenericCopyAdtApplication(layout)(arguments)(state) || canRuntimeManageFreshHeapChildAdtApplication(layout)(arguments)(state) || canRuntimeManageOwnedChildAdtApplication(layout)(arguments)(state) || tcoOwnedChildAdtSupported(heapFactsOf(resultType)(state)) && allFreshOwnedChildren(arguments)(fieldTypes)(state) || canRuntimeManageRecursiveAdtApplication(layout)(arguments)(state)

let recordRequestHonored (arguments: List(Expr)) (fieldTypes: List(SemanticType)) (resultType: SemanticType) (state: CoreLoweringState) =
    recordAdtSupported(heapFactsOf(resultType)(state)) && allFreshOwnedChildren(arguments)(fieldTypes)(state)

// Stage 0's `IsRuntimeManagedConstructorCandidate` and the nullary rule of
// `LowerNullaryConstructor`: a record request is honored when the type is a runtime record and
// every field receives a fresh owned value; an ADT request when the application is a copy ADT, a
// generic copy ADT over literals, a single-constructor cell over fresh heap children, an
// owned-child or accumulator-shaped ADT over fresh children, or a fresh recursive-copy tree.
let isRuntimeManagedConstructorCandidate (layout: CoreConstructorLayout) (arguments: List(Expr)) (request: ConsumerRequest) (state: CoreLoweringState) =
    match (layoutFieldTypes(layout)(state), request) with
        | ((fieldTypes, resultType), ConsumerRequest { runtimeAdt = runtimeAdt, runtimeRecord = runtimeRecord }) ->
            match (runtimeAdt || runtimeRecord, arguments) with
                | (false, _arguments) -> false
                | (true, []) -> nullaryConstructorRuntimeManageable(resultType)(state)
                | (true, _arguments) -> runtimeRecord && recordRequestHonored(arguments)(fieldTypes)(resultType)(state) || runtimeAdt && adtRequestHonored(layout)(arguments)(fieldTypes)(resultType)(state)

let fieldTypeIsVariable (fieldType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(fieldType) with
        | SemVariable(_id) -> true
        | _ -> false

let fieldTypeIs (fieldType: SemanticType) (state: CoreLoweringState) (accepts: SemanticType -> Bool) =
    accepts(resolveType(state)(fieldType)) || fieldTypeIsVariable(fieldType)(state)

let isStringType (semanticType: SemanticType) =
    match semanticType with
        | SemString -> true
        | _ -> false

let isTupleType (semanticType: SemanticType) =
    match semanticType with
        | SemTuple(_elements) -> true
        | _ -> false

// The request one constructor argument is lowered under, stage 0's
// `LowerRuntimeManagedConstructorArgument` with `LowerConstructorArguments`' child
// representations: the parent's flags minus the ADT and record ones, the ADT flag again when the
// runtime cell was asked for as an ADT, the record flag for any runtime cell, and — for a runtime
// cell — a runtime string for a fresh string producer in a string or type-parameter field, a
// runtime list for a fresh list construction in a list or type-parameter field, and a runtime
// tuple for a tuple literal in a tuple or type-parameter field; the transfer is forwarded.
let constructorArgumentRequest (request: ConsumerRequest) (runtimeManaged: Bool) (argument: Expr) (fieldType: SemanticType) (state: CoreLoweringState) =
    match request with
        | ConsumerRequest { runtimeString = parentString, runtimeList = parentList, runtimeTuple = parentTuple, runtimeAdt = parentAdt, transfersRuntimeManagedChildren = transfers, recordFieldCells = fieldCells, callArgument = callArgument } -> emptyConsumerRequest with runtimeString = parentString || runtimeManaged && fieldTypeIs(fieldType)(state)(isStringType) && isFreshStringChild(argument)(state), runtimeList = parentList || runtimeManaged && fieldTypeIs(fieldType)(state)(isListType) && isFreshListConstruction(argument), runtimeTuple = parentTuple || runtimeManaged && fieldTypeIs(fieldType)(state)(isTupleType) && isTupleLiteral(argument), runtimeAdt = runtimeManaged && parentAdt, runtimeRecord = runtimeManaged, transfersRuntimeManagedChildren = transfers, recordFieldCell = fieldCells && isConsValue(argument), callArgument = callArgument && !runtimeManaged

let emitListNormalizingCopy (temp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = allocated, temp = normalizedTemp } ->
            allocated
            |> emit(CopyOutList(normalizedTemp)(temp)(InlineListHead)(true)(RcNormalization))
            |> markRuntimeTemp(normalizedTemp)(RuntimeNewlyProduced)
            |> success(normalizedTemp)(semanticType)

// Stage 0's `RequiresRuntimeManagedChildCopy`: the field types whose non-reference-counted child
// a runtime-managed parent clones into an owned graph before storing it — a list, tuple, or
// aggregate the runtime-managed deep copy reproduces completely. A string child is retained
// instead, and a scalar is stored inline.
let requiresRuntimeManagedChildCopy (fieldType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(fieldType) with
        | SemList(element) -> tcoListElementSupported(element)(state)
        | SemTuple(elements) -> allRuntimeOwnedCopyElements(elements)(state)
        | SemNamed(_symbolId, _name, _arguments) as named ->
            match heapFactsOf(named)(state) with
                | HeapLayoutFacts { structuralCopy = ShallowCopy } -> true
                | facts -> runtimeManagedAdtLayout(facts)
        | _ -> false

// A runtime cell's child that is not already reference-counted (a list, tuple, or aggregate read
// out of a pattern owner, or an arena value) is cloned into an owned graph, stage 0's deep copy
// normalization in `LowerRuntimeManagedConstructorArgument`; the clone carries its own
// reference, so no pattern-owner duplicate is taken on top of it.
let normalizeConstructorChildArgument (runtimeManaged: Bool) (fieldType: SemanticType) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            if runtimeManaged && isRuntimeTemp(temp)(state) == false && requiresRuntimeManagedChildCopy(fieldType)(state)
            then
                match argumentCopyPlanOf(fieldType)(state) with
                    | Some(inlinePlan) ->
                        let plan = entryCopyPlanOf(fieldType)(inlinePlan)(state)
                        in
                            match emitGuardedDeepCopy(temp)(plan)(state) with
                                | (copied, copiedTemp) ->
                                    copied
                                    |> markRuntimeTemp(copiedTemp)(RuntimeNewlyProduced)
                                    |> (given (marked: CoreLoweringState) -> withStatePatternOwnerCopyTemps(copiedTemp :: statePatternOwnerCopyTemps(marked))(marked))
                                    |> success(copiedTemp)(semanticType)
                    | None -> lowered
            else lowered
        | _ -> lowered

// Stage 0's string branch of `LowerRuntimeManagedConstructorArgument`: a borrowed string a
// runtime cell stores is copied into an owned string the cell releases with itself.
let materializeStringChildArgument (runtimeManaged: Bool) (argument: Expr) (fieldType: SemanticType) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            if runtimeManaged && resolveType(state)(fieldType) == SemString && isRuntimeTemp(temp)(state) == false && isMaterializableStringChildRead(argument)(state)
            then
                match freshTemp(state) with
                    | FreshTemp { state = copyState, temp = copyTemp } ->
                        copyState
                        |> emit(CopyOutArena(copyTemp)(temp)(-1)(true)(RcNormalization)(None))
                        |> markRuntimeTemp(copyTemp)(RuntimeNewlyProduced)
                        |> success(copyTemp)(semanticType)
            else lowered
        | _ -> lowered

let emitOwnedStringCopy (sourceTemp: Int) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = allocated, temp = copyTemp } ->
            (emit(CopyOutArena(copyTemp)(sourceTemp)(-1)(true)(RcNormalization)(None))(allocated), copyTemp)

// Stage 0's `TryOwnPatternBoundStringChild`: the `Str` a runtime cell stores when it was bound out
// of a pattern is whatever the matched value's strings are, and retaining an arena one retains
// nothing — the cell would keep a pointer into the arena. The cell takes a reference to a
// reference-counted string and a copy of any other, which stands in for the pattern-owner
// duplicate the argument would otherwise take.
let ownPatternBoundStringChild (runtimeManaged: Bool) (argument: Expr) (fieldType: SemanticType) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            if canTestRepresentation(state) && runtimeManaged && resolveType(state)(fieldType) == SemString && isPatternOwnerRead(argument)(state) && containsInt(temp)(statePatternOwnerCopyTemps(state)) == false
            then
                match emitReferenceOrCopy(temp)(emitOwnedStringCopy(temp))(state) with
                    | (owned, ownedTemp) ->
                        owned
                        |> markRuntimeTemp(ownedTemp)(RuntimeNewlyProduced)
                        |> (given (marked: CoreLoweringState) -> withStatePatternOwnerCopyTemps(ownedTemp :: statePatternOwnerCopyTemps(marked))(marked))
                        |> success(ownedTemp)(semanticType)
            else lowered
        | _ -> lowered

// Stage 0's `RetainEscapingConstructorArgument`: an arena cell built under the transfer retains
// each argument read of a live owner as it is lowered; a runtime cell retains its owned children
// after all of its arguments (`RetainRuntimeManagedOwnedChildArguments`).
let transferSlotRequested (transferSlot: Maybe(Int)) =
    match transferSlot with
        | Some(_slot) -> true
        | None -> false

// Stage 0's `KeepLoopResultCellOwnerDuplicateOwned`: the duplicate a binding of the whole loop
// parameter (a catch-all arm's `root`) took for an arena cell built as a loop function's result
// goes to an owned slot, released once the function's result holds references of its own, since
// the return normalizes the cell into a copy that retains the field itself and the arena cell
// releases nothing. A part matched out of the parameter is released after the copy by its rules.
let keepLoopResultCellOwnerDuplicateOwned (arenaResultCell: Bool) (argument: Expr) (before: LoweredCoreValue) (after: LoweredCoreValue) =
    match (before, after) with
        | (LoweredCoreValue { temp = readTemp }, LoweredCoreValue { state = state, temp = duplicateTemp, semanticType = semanticType, error = None }) ->
            if arenaResultCell && duplicateTemp != readTemp && generalRcEnabled(state) && !containsUnresolvedLayout(resolveType(state)(semanticType))(state) && (match stateTcoLoopFrame(state) with
                | Some(_frame) -> true
                | None -> false) && (match unspanArgument(argument) with
                | ExprVar(name) ->
                    match patternOwnerBinding(name)(state) with
                        | Some(PatternBindingFact { extractionDepth = 0 }) -> true
                        | _ -> false
                | _ -> false)
            then
                match freshLocal(state) with
                    | FreshLocal { state = allocated, local = ownedSlot } ->
                        after with state = allocated |> emit(StoreLocal(ownedSlot)(duplicateTemp)) |> registerGeneralRcOwnedSlot(ownedSlot)(resolveType(state)(semanticType))
            else
                if arenaResultCell && duplicateTemp != readTemp && generalRcEnabled(state) && !containsUnresolvedLayout(resolveType(state)(semanticType))(state) && (match stateTcoLoopFrame(state) with
                    | Some(_frame) -> true
                    | None -> false)
                then after with state = withStateLoopResultPartDuplicates((duplicateTemp, resolveType(state)(semanticType)) :: stateLoopResultPartDuplicates(state))(state)
                else after
        | _ -> after

// Stage 0's `KeepLoopResultPartDuplicatesOfArenaCell`: the duplicate a pattern owner took for a part of
// a loop parameter stored into a loop result cell (`Some(label)` of a record matched out of the list
// parameter) goes to an owned slot when the cell stays in the arena, marked as a part's, which also
// makes the return copy the result and release it behind the copy. A cell placed on the
// reference-counted heap owns its field already. Scalar fields hold nothing.
let recursive keepLoopResultPartDuplicates (duplicates: List((Int, SemanticType))) (state: CoreLoweringState) =
    match duplicates with
        | [] -> state
        | (duplicateTemp, semanticType) :: rest ->
            if resultSurvivesReset(semanticType)(state)
            then keepLoopResultPartDuplicates(rest)(state)
            else
                match freshLocal(state) with
                    | FreshLocal { state = allocated, local = ownedSlot } ->
                        allocated
                        |> emit(StoreLocal(ownedSlot)(duplicateTemp))
                        |> registerGeneralRcOwnedSlot(ownedSlot)(semanticType)
                        |> (given (registered: CoreLoweringState) -> withStateGeneralRcPartDuplicateSlots((stateActiveFunctionOrigin(registered), ownedSlot) :: stateGeneralRcPartDuplicateSlots(registered))(registered))
                        |> keepLoopResultPartDuplicates(rest)

// After a constructor's placement is known: its arguments' part duplicates are kept for an arena cell
// and dropped for a reference-counted one, and the enclosing constructor's list is restored.
let keepLoopResultPartDuplicatesOfArenaCell (saved: List((Int, SemanticType))) (placed: (LoweredCoreValues, Bool)) =
    match placed with
        | (LoweredCoreValues { state = state } as values, cellRuntimeManaged) ->
            if cellRuntimeManaged
            then ((values with state = withStateLoopResultPartDuplicates(saved)(state)), cellRuntimeManaged)
            else
                ((values with state = keepLoopResultPartDuplicates(stateLoopResultPartDuplicates(state))(withStateLoopResultPartDuplicates(saved)(state))), cellRuntimeManaged)

// The temp a cell's construction stored at an offset, the most recent store first.
let recursive cellWordSource (cell: Int) (offset: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> None
        | IrInstruction { instruction = StoreMemOffset(basePtr, storedOffset, source) } :: rest ->
            if basePtr == cell && storedOffset == offset
            then Some(source)
            else cellWordSource(cell)(offset)(rest)
        | _instruction :: rest -> cellWordSource(cell)(offset)(rest)

// Stage 0's `KeepLoopResultFieldCellHeadDuplicateOwned`: an arena cons cell built as a constructor
// argument at a loop function's result (`Spine(root = root, arguments = argument :: arguments)`
// over a part of the loop parameter) holds the pattern-owner duplicate of its head; the return
// normalizes the result into a copy that retains the head itself and the arena cell releases
// nothing, so the duplicate goes to an owned slot released behind the result. The duplicate is a
// marker or already a real retain, as when the head's pattern owner is runtime-managed
// (`fn with calls = target :: fn.calls`, `target` matched out of a list parameter).
let keepLoopResultFieldCellHeadDuplicateOwned (request: ConsumerRequest) (runtimeManaged: Bool) (argument: Expr) (lowered: LoweredCoreValue) =
    match (lowered, unspanArgument(argument)) with
        | (LoweredCoreValue { state = state, temp = cellTemp, semanticType = listType, error = None }, ExprCons(headExpression, _tail)) ->
            match (cellWordSource(cellTemp)(0)(state.reversedInstructions), unspanArgument(headExpression), resolveType(state)(listType)) with
                | (Some(headTemp), ExprVar(name), SemList(headType)) ->
                    if !runtimeManaged && (request.tailPosition || request.consTailPosition) && generalRcEnabled(state) && (match (stateTcoLoopFrame(state), stateBackEdgeArgumentSlot(state), request.transferSlot, patternOwnerBinding(name)(state)) with
                        | (Some(_frame), None, None, Some(_fact)) -> true
                        | _ -> false) && (match (definitionOfTemp(headTemp)(state.reversedInstructions), definitionOfTemp(cellTemp)(state.reversedInstructions)) with
                        | (Some(RcDup(_target, _source, _runtimeManagedRetain, _mayBeEmpty)), Some(Alloc(_cell, 16, false))) -> true
                        | _ -> false)
                    then
                        match freshLocal(state) with
                            | FreshLocal { state = allocated, local = ownedSlot } ->
                                lowered with state = allocated |> emit(StoreLocal(ownedSlot)(headTemp)) |> registerGeneralRcOwnedSlot(ownedSlot)(resolveType(state)(headType))
                    else lowered
                | _ -> lowered
        | _ -> lowered

// Stage 0's `RetainEscapingConstructorArgument`: a constructor whose consumer owns the value (a
// runtime-managed candidate, a transfer slot, or the loop body's tail position) takes a
// pattern-owner duplicate of an argument that reads one, before the transfer retain; an
// argument neither retained reads takes the loop-parameter marker. An ordinary call's argument
// borrows the read whatever transfer the callee's result asks of it. A record update's updated
// field takes the same retains, as a constructor argument of a cell that is never a
// runtime-managed candidate.
let retainEscapingArgumentRead (request: ConsumerRequest) (runtimeManaged: Bool) (argument: Expr) (lowered: LoweredCoreValue) =
    match request with
        | ConsumerRequest { transfersRuntimeManagedChildren = transfers, transferSlot = transferSlot, tailPosition = tailPosition, callArgument = callArgument } ->
            lowered
            |> (given (value: LoweredCoreValue) ->
                if runtimeManaged || tailPosition || transferSlotRequested(transferSlot) || transfers && !callArgument
                then
                    value
                    |> duplicatePatternOwnerChild(argument)
                    |> keepLoopResultCellOwnerDuplicateOwned(runtimeManaged == false && tailPosition)(argument)(value)
                else value)
            |> retainSuccessorTransferredChild(argument)(runtimeManaged == false && transfers)

let retainEscapingConstructorArgument (request: ConsumerRequest) (runtimeManaged: Bool) (argument: Expr) (lowered: LoweredCoreValue) =
    lowered
    |> retainEscapingArgumentRead(request)(runtimeManaged)(argument)
    |> keepLoopResultFieldCellHeadDuplicateOwned(request)(runtimeManaged)(argument)

// Stage 0's `RetainRuntimeManagedTcoConstructorArguments`: after every argument is lowered, a
// loop-parameter read (or a heap-typed field read out of one) stored into the cell takes the
// retain marker, ahead of the owned-child retains of a runtime cell.
// Whether a loop parameter slot is admitted to the reference-counted heap at this point of the
// body (stage 0's `IsRuntimeManagedTcoParamSlot`): a copy-ADT slot by its resolved type and
// shape, a list slot by its shape and element type, a `Str` slot by the affine analysis.
let tcoSlotAdmittedNow (slot: Int) (state: CoreLoweringState) =
    match (stateTcoLoopFrame(state), stateTcoLoop(state)) with
        | (Some(frame), Some(loop)) -> loopSlotIsRuntimeManaged(slot)(frame)(loop)(state)
        | _ -> false

// Stage 0's `TryResolveTcoParameterRead` for a constructor argument: a loop parameter read of
// any type, or a heap-typed field read out of one, that is not a pattern owner.
let constructorArgumentParameterSlot (argument: Expr) (semanticType: SemanticType) (state: CoreLoweringState) =
    match unspanArgument(argument) with
        | ExprVar(name) ->
            match patternOwnerBinding(name)(state) with
                | Some(_fact) -> None
                | None -> loopParameterReadSlot(argument)(state)
        | ExprQualifiedVar(_owner, _field) ->
            if resultSurvivesReset(semanticType)(state)
            then None
            else loopParameterReadSlot(argument)(state)
        | _ -> None

// Stage 0's `EmitPendingRuntimeManagedConstructorFieldRetain`: the argument is routed through
// a slot the retain path overwrites with the duplicate; the flag defaults to one and finalize
// zeroes it for a root the frame does not admit.
let emitPendingConstructorFieldRetain (temp: Int) (slot: Int) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = flagState, temp = flagTemp } ->
            match flagState
            |> withStatePendingRuntimeArgumentFlags((flagTemp, slot) :: statePendingRuntimeArgumentFlags(flagState))
            |> freshLocal with
                | FreshLocal { state = allocated, local = resultSlot } ->
                    match allocated
                    |> emit(LoadConstInt(flagTemp)(1))
                    |> emit(StoreLocal(resultSlot)(temp))
                    |> freshLabel("rc_constructor_field_not_retained") with
                        | FreshLabel { state = labelled, label = doneLabel } ->
                            match freshTemp(labelled) with
                                | FreshTemp { state = dupState, temp = duplicate } ->
                                    match dupState
                                    |> emit(JumpIfFalse(flagTemp)(doneLabel))
                                    |> emit(RcDup(duplicate)(temp)(true)(true))
                                    |> emit(StoreLocal(resultSlot)(duplicate))
                                    |> emit(Label(doneLabel))
                                    |> freshTemp with
                                        | FreshTemp { state = resultState, temp = resultTemp } ->
                                            (emit(LoadLocal(resultTemp)(resultSlot))(resultState), resultTemp)

// Stage 0's `RetainRuntimeManagedTcoConstructorArguments` for one argument: a parameter read
// stored into the cell keeps a second reference to a value the parameter's own release walks at
// the back edge; a slot admitted by now retains outright, any other through the pending skeleton.
// A child the cell already made its own (retained or copied for a runtime cell) holds that
// reference.
let emitLoopParameterFieldRetain (temp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = allocated, temp = duplicate } ->
            (emit(semanticType
            |> resolveType(state)
            |> mayBeEmptyList
            |> RcDup(duplicate)(temp)(true))(allocated), duplicate)

// Stage 0's `TryGetArenaSuccessorDestination`: the loop parameter an arena cell under construction
// becomes part of, when it is built inside a tail self-call argument whose parameter is not on the
// reference-counted heap yet.
let arenaSuccessorDestination (runtimeManaged: Bool) (state: CoreLoweringState) =
    match (runtimeManaged, stateBackEdgeArgumentSlot(state)) with
        | (false, Some(slot)) ->
            if tcoSlotAdmittedNow(slot)(state)
            then None
            else Some(slot)
        | _ -> None

// Stage 0's `EmitRetainOrArenaCopyForDestination`: an arena cell holding a part of a
// reference-counted loop parameter keeps its own reference when its successor parameter ends up
// on the reference-counted heap, which takes that reference over, and an arena copy of the part
// when the parameter stays in the arena, where nothing releases what it holds.
let emitRetainOrArenaCopyForDestination (temp: Int) (semanticType: SemanticType) (destinationSlot: Int) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = flagState, temp = flagTemp } ->
            match flagState
            |> withStatePendingRuntimeArgumentFlags((flagTemp, destinationSlot) :: statePendingRuntimeArgumentFlags(flagState))
            |> emit(LoadConstInt(flagTemp)(1))
            |> freshLocal with
                | FreshLocal { state = slotted, local = resultSlot } ->
                    match freshLabel("rc_arena_cell_part_copy")(slotted) with
                        | FreshLabel { state = copyLabelled, label = copyLabel } ->
                            match freshLabel("rc_arena_cell_part_done")(copyLabelled) with
                                | FreshLabel { state = doneLabelled, label = doneLabel } ->
                                    match doneLabelled
                                    |> emit(JumpIfFalse(flagTemp)(copyLabel))
                                    |> emitLoopParameterFieldRetain(temp)(semanticType) with
                                        | (retained, duplicate) ->
                                            match retained
                                            |> emit(StoreLocal(resultSlot)(duplicate))
                                            |> emit(Jump(doneLabel))
                                            |> emit(Label(copyLabel))
                                            |> emitLocatedDeepCopy(temp)(semanticType) with
                                                | (copied, copyTemp) ->
                                                    match copied
                                                    |> emit(StoreLocal(resultSlot)(copyTemp))
                                                    |> emit(Label(doneLabel))
                                                    |> freshTemp with
                                                        | FreshTemp { state = loaded, temp = resultTemp } ->
                                                            (emit(LoadLocal(resultTemp)(resultSlot))(loaded), resultTemp)

// Stage 0's `NoteOwnFieldParameterRetain`: a whole loop parameter a cell built in the loop retained
// outright, or left to the loop's placement, noted under the loop's name.
let noteOwnFieldParameterRetain (argument: Expr) (retained: Bool) (state: CoreLoweringState) =
    match (unspanArgument(argument), stateTcoLoop(state)) with
        | (ExprVar(name), Some(CoreTcoLoop { selfName = selfName })) ->
            if retained
            then withStateOwnFieldRetainedParameters((selfName, name) :: stateOwnFieldRetainedParameters(state))(state)
            else withStateOwnFieldPendingParameters((selfName, name) :: stateOwnFieldPendingParameters(state))(state)
        | _ -> state

// An arena cell built as a call's argument is no successor cell, the call's result is: its retain
// of a parameter read is collected with the call argument's retains and released after the call
// (stage 0's `RecordCallArgumentArenaRetain` in `RetainRuntimeManagedTcoConstructorArguments`).
let keepLoopResultConstructorFieldRetainOwned (request: ConsumerRequest) (duplicate: Int) (fieldType: SemanticType) (state: CoreLoweringState) =
    if generalRcEnabled(state) && (request.tailPosition || request.consTailPosition) && !containsUnresolvedLayout(resolveType(state)(fieldType))(state) && (match (stateTcoLoopFrame(state), stateBackEdgeArgumentSlot(state), request.transferSlot) with
        | (Some(_frame), None, None) -> true
        | _ -> false)
    then
        match freshLocal(state) with
            | FreshLocal { state = allocated, local = ownedSlot } ->
                allocated
                |> emit(StoreLocal(ownedSlot)(duplicate))
                |> registerGeneralRcOwnedSlot(ownedSlot)(resolveType(state)(fieldType))
    else state

let retainConstructorLoopParameterArgument (request: ConsumerRequest) (runtimeManaged: Bool) (argument: Expr) (temp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match (state
    |> statePatternOwnerCopyTemps
    |> containsInt(temp), constructorArgumentParameterSlot(argument)(semanticType)(state)) with
        | (true, _slot) -> (state, temp)
        | (false, None) -> (state, temp)
        | (false, Some(slot)) ->
            if tcoSlotAdmittedNow(slot)(state)
            then
                match arenaSuccessorDestination(runtimeManaged)(state) with
                    | Some(destinationSlot) -> emitRetainOrArenaCopyForDestination(temp)(semanticType)(destinationSlot)(state)
                    | None ->
                        match emitLoopParameterFieldRetain(temp)(semanticType)(state) with
                            | (retained, duplicate) ->
                                let releasedAfterCall = !runtimeManaged && duplicate != temp && stateCollectingCallArgumentRetains(retained) && retained.nextLabelId == stateCallArgumentLabelStart(retained)
                                in
                                    retained
                                    |> recordArenaCellTransferRetain(!runtimeManaged)(temp)(duplicate)(semanticType)
                                    |> (given (recorded: CoreLoweringState) ->
                                        if releasedAfterCall
                                        then recorded
                                        else
                                            recorded
                                            |> noteOwnFieldParameterRetain(argument)(true)
                                            |> (given (noted: CoreLoweringState) ->
                                                if runtimeManaged
                                                then noted
                                                else keepLoopResultConstructorFieldRetainOwned(request)(duplicate)(semanticType)(noted)))
                                    |> (given (noted: CoreLoweringState) -> (noted, duplicate))
            else
                match emitPendingConstructorFieldRetain(temp)(slot)(state) with
                    | (pending, pendingTemp) -> (noteOwnFieldParameterRetain(argument)(false)(pending), pendingTemp)

let recursive retainLoopParameterArguments (request: ConsumerRequest) (runtimeManaged: Bool) (arguments: List(Expr)) (temps: List(Int)) (semanticTypes: List(SemanticType)) (state: CoreLoweringState) (reversed: List(Int)) =
    match (arguments, temps, semanticTypes) with
        | (argument :: restArguments, temp :: restTemps, semanticType :: restTypes) ->
            match retainConstructorLoopParameterArgument(request)(runtimeManaged)(argument)(temp)(semanticType)(state) with
                | (marked, markedTemp) -> retainLoopParameterArguments(request)(runtimeManaged)(restArguments)(restTemps)(restTypes)(marked)(markedTemp :: reversed)
        | _ -> (state, reverse(reversed))

let retainConstructorLoopParameterArguments (request: ConsumerRequest) (runtimeManaged: Bool) arguments lowered =
    match lowered with
        | LoweredCoreValues { state = state, temps = temps, semanticTypes = semanticTypes, error = None } ->
            match retainLoopParameterArguments(request)(runtimeManaged)(arguments)(temps)(semanticTypes)(state)([]) with
                | (marked, markedTemps) -> lowered with state = marked, temps = markedTemps
        | _ -> lowered

// Stage 0's `IsFreshOwnedConstructorArgument`: a value produced by the argument expression itself
// (not read from a binding) that is owned on the reference-counted heap. A self call's
// placeholder result decides nothing about ownership.
let recursive producesOwnValue (argument: Expr) =
    match unspanArgument(argument) with
        | ExprLet(_name, _value, body, _parameters, _annotation, _constraints) -> producesOwnValue(body)
        | ExprVar(_name) -> false
        | ExprQualifiedVar(_owner, _member) -> false
        | _ -> true

// Whether `temp` reloads a call result whose deferred copy-out has a kind once its type is known
// here: the block resolved after the body copies the result onto the reference-counted heap
// before that reload, so the reload is a fresh value of its own, as stage 0's copy-out pinned at
// the call is.
let recursive reloadsResolvedCallCopyOut (temp: Int) (copyOuts: List(CorePendingCallCopyOut)) (state: CoreLoweringState) =
    match copyOuts with
        | [] -> false
        | copyOut :: rest ->
            if copyOut.deferredReloadTemp == temp && copyOut.deferredOwned == false && containsUnresolvedLayout(copyOut.deferredType)(state) == false
            then
                match callCopyOutOf(copyOut.deferredType)(state) with
                    | Some(_copyOut) -> ordinaryRcPlacementAllowed(state)
                    | None -> false
            else reloadsResolvedCallCopyOut(temp)(rest)(state)

let isFreshOwnedConstructorArgument (argument: Expr) (temp: Int) (state: CoreLoweringState) =
    producesOwnValue(argument) && (isRuntimeTemp(temp)(state) || reloadsResolvedCallCopyOut(temp)(statePendingCallCopyOuts(state))(state)) && containsInt(temp)(stateBackEdgeDummyTemps(state)) == false

// Stage 0's `IsRetainedTailPatternOwner`: a loop's result built in tail position around a binding
// matched out of a reference-counted loop parameter takes a duplicate of the binding (the loop
// releases its parameters at the exit), and only a reference-counted cell releases it.
let retainedTailPatternOwner (request: ConsumerRequest) (argument: Expr) (state: CoreLoweringState) =
    match (request, unspanArgument(argument)) with
        | (ConsumerRequest { tailPosition = true }, ExprVar(name)) ->
            match patternOwnerBinding(name)(state) with
                | Some(_fact) -> loopParameterIsRuntimeManaged(name)(state)
                | None -> false
        | _ -> false

// Stage 0's `IsRetainTakenForCell`: a retain the preparation of an argument took for the cell (a
// transfer retain of an owned value the cell keeps), outside a loop's successor (a tail self call's
// argument or a `let` the self call passes on, whose back edge balances its cells' retains), where an
// arena cell would hold it with nothing to release it.
let inArenaSuccessorLet (state: CoreLoweringState) =
    match (stateArenaSuccessorLetLoop(state), stateTcoLoop(state)) with
        | (Some(name), Some(loop)) -> name == loop.selfName
        | _ -> false

let retainTakenForCell (temp: Int) (state: CoreLoweringState) = stateBackEdgeArgumentSlot(state) == None && inArenaSuccessorLet(state) == false && isTransferRetainIn(temp)(state.reversedInstructions)

let holdsReferenceTakenForCell (request: ConsumerRequest) (argument: Expr) (temp: Int) (state: CoreLoweringState) = retainedTailPatternOwner(request)(argument)(state) || retainTakenForCell(temp)(state)

// Stage 0's `CanNormalizeIntoOwnedRuntimeValue` for a constructor's own type.
let normalizesIntoOwnedValue (semanticType: SemanticType) (state: CoreLoweringState) =
    match ownedResultPlanOf(semanticType)(state) with
        | Some(ScalarArgumentCopy) -> false
        | Some(_plan) -> true
        | None -> false

// The records whose arena cell hands a fresh child over to an owned slot (stage 0's first branch
// of `PlaceConstructorOwningFreshChild`, and `HandFreshRecordUpdateChildToOwnedSlot`): a value of
// the contract's own types is passed borrowed, so its cell stays in the arena, and a record the
// runtime cannot normalize has no reference-counted placement either.
let handsFreshChildrenToOwnedSlots (resultType: SemanticType) (state: CoreLoweringState) = containsUnresolvedLayout(resultType)(state) == false && (isGeneralRcValueType(resultType)(state) || normalizesIntoOwnedValue(resultType)(state) == false)

// Whether a field's type mentions the variable, or is itself unresolved.
let recursive anyFieldMentions (variableId: Int) (fieldTypes: List(SemanticType)) (state: CoreLoweringState) =
    match fieldTypes with
        | [] -> false
        | fieldType :: rest ->
            occursInType(variableId)(resolveType(state)(fieldType)) || containsUnresolvedLayout(fieldType)(state) || anyFieldMentions(variableId)(rest)(state)

// Stage 0's `OnlyFieldlessTypeArgumentsUnresolved` for the type arguments: each is resolved, or
// a variable no field of the cell mentions (the error type of an `Ok`).
let recursive allFieldlessOrResolved (typeArguments: List(SemanticType)) (fieldTypes: List(SemanticType)) (state: CoreLoweringState) =
    match typeArguments with
        | [] -> true
        | argument :: rest ->
            (match resolveType(state)(argument) with
                | SemVariable(variableId) -> anyFieldMentions(variableId)(fieldTypes)(state) == false
                | resolved -> containsUnresolvedLayout(resolved)(state) == false) && allFieldlessOrResolved(rest)(fieldTypes)(state)

// Stage 0's `OnlyFieldlessTypeArgumentsUnresolved`: a cell whose type is unresolved only in type
// arguments no field mentions hands its fresh children to owned slots, as the contract's types
// do, and the return decides whether the result is normalized and the slots released.
let onlyFieldlessTypeArgumentsUnresolved (resultType: SemanticType) (fieldTypes: List(SemanticType)) (state: CoreLoweringState) =
    match resolveType(state)(resultType) with
        | SemNamed(_symbolId, _name, typeArguments) -> containsUnresolvedLayout(resultType)(state) && allFieldlessOrResolved(typeArguments)(fieldTypes)(state)
        | _ -> false

// Stage 0's `IsArenaCellRetainHandedToSlot`: a retain taken for the arena cell (a let-bound owned
// value the cell keeps) is the arena's to hold with nothing to release it, as a fresh value is.
let arenaCellOwnsArgumentReference (argument: Expr) (temp: Int) (state: CoreLoweringState) = isFreshOwnedConstructorArgument(argument)(temp)(state) || retainTakenForCell(temp)(state)

// A fresh value or a retain taken for the arena cell goes to an owned slot the function releases.
let handOwnedChildToSlot (argument: Expr) (temp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    if arenaCellOwnsArgumentReference(argument)(temp)(state) == false
    then state
    else
        match freshLocal(state) with
            | FreshLocal { state = allocated, local = ownedSlot } ->
                allocated
                |> emit(StoreLocal(ownedSlot)(temp))
                |> registerGeneralRcOwnedSlot(ownedSlot)(semanticType)

// Stage 0's `HandArenaCellMarkerToOwnedSlot`: a pattern owner's duplicate taken for the arena cell
// is still a marker while the cell is built (the `Some(head)` of a loop over a list normalized at
// its entry); promoted, it is a retain the arena cell holds with nothing to release it. Its slot
// holds zero until `settleCellMarkerSlots` sees the promotion.
let handCellMarkerToOwnedSlot (marker: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match freshLocal(state) with
        | FreshLocal { state = allocated, local = ownedSlot } ->
            match freshTemp(allocated) with
                | FreshTemp { state = withTemp, temp = zeroTemp } ->
                    withTemp
                    |> emit(LoadConstInt(zeroTemp)(0))
                    |> emit(StoreLocal(ownedSlot)(zeroTemp))
                    |> registerGeneralRcOwnedSlot(ownedSlot)(semanticType)
                    |> (given (registered: CoreLoweringState) -> withStateCellMarkerSlots((ownedSlot, marker, zeroTemp) :: stateCellMarkerSlots(registered))(registered))

// Stage 0's `HandFreshConstructorChildrenToOwnedSlots` for one argument: the arena cell never
// releases what it holds, so a fresh reference-counted value stored into it goes to an owned slot
// the function releases.
// A retain taken for the cell goes there the same way, and a pattern owner's marker the cell took.
let handFreshChildToOwnedSlot (argument: Expr) (temp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    if resultSurvivesReset(semanticType)(state) || containsUnresolvedLayout(semanticType)(state)
    then state
    else
        if isPromotableCellMarker(temp)(state) && isBorrowedRetainableParameterType(semanticType)(state)
        then handCellMarkerToOwnedSlot(temp)(semanticType)(state)
        else handOwnedChildToSlot(argument)(temp)(semanticType)(state)

let recursive handFreshChildrenToOwnedSlots (arguments: List(Expr)) (temps: List(Int)) (semanticTypes: List(SemanticType)) (state: CoreLoweringState) =
    match (arguments, temps, semanticTypes) with
        | (argument :: restArguments, temp :: restTemps, semanticType :: restTypes) ->
            state
            |> handFreshChildToOwnedSlot(argument)(temp)(resolveType(state)(semanticType))
            |> handFreshChildrenToOwnedSlots(restArguments)(restTemps)(restTypes)
        | _ -> state

let recursive anyArenaCellOwnedArgument (arguments: List(Expr)) (temps: List(Int)) (state: CoreLoweringState) =
    match (arguments, temps) with
        | (argument :: restArguments, temp :: restTemps) -> arenaCellOwnsArgumentReference(argument)(temp)(state) || anyArenaCellOwnedArgument(restArguments)(restTemps)(state)
        | _ -> false

// An argument whose value the cell would own outright: a fresh value, or a reference taken for the
// cell.
let recursive anyCellOwnedArgument (request: ConsumerRequest) (arguments: List(Expr)) (temps: List(Int)) (state: CoreLoweringState) =
    match (arguments, temps) with
        | (argument :: restArguments, temp :: restTemps) -> isFreshOwnedConstructorArgument(argument)(temp)(state) || holdsReferenceTakenForCell(request)(argument)(temp)(state) || anyCellOwnedArgument(request)(restArguments)(restTemps)(state)
        | _ -> false

let recursive anyPromotableCellMarker (temps: List(Int)) (state: CoreLoweringState) =
    match temps with
        | [] -> false
        | temp :: rest -> isPromotableCellMarker(temp)(state) || anyPromotableCellMarker(rest)(state)

// A constructor whose arena cell would hold a fresh reference-counted value hands it to an owned
// slot, ahead of the retains the constructor's preparation takes. A cell placed on the
// reference-counted heap or on the stack, and a zero-cost constructor, own nothing of the kind.
let handConstructorFreshChildren (layout: CoreConstructorLayout) arguments (resultType: SemanticType) (runtimeManaged: Bool) (stackAllocate: Bool) lowered =
    match (layout, lowered) with
        | (CoreConstructorLayout { isZeroCost = true }, _lowered) -> lowered
        | (_layout, LoweredCoreValues { state = state, temps = temps, semanticTypes = semanticTypes, error = None }) ->
            if generalRcEnabled(state) == false || runtimeManaged || stackAllocate || anyArenaCellOwnedArgument(arguments)(temps)(state) == false && anyPromotableCellMarker(temps)(state) == false || handsFreshChildrenToOwnedSlots(resultType)(state) == false && onlyFieldlessTypeArgumentsUnresolved(resultType)(semanticTypes)(state) == false
            then lowered
            else lowered with state = handFreshChildrenToOwnedSlots(arguments)(temps)(semanticTypes)(state)
        | _ -> lowered

// Stage 0's `IsPreparedConstructorArgument`: an argument the constructor's preparation retains
// itself, a binding that still owns its reference-counted value or a read of a loop parameter.
let isPreparedConstructorArgument (argument: Expr) (semanticType: SemanticType) (state: CoreLoweringState) =
    (match unspanArgument(argument) with
        | ExprVar(name) ->
            match liveRuntimeOwnerSlot(name)(state) with
                | Some(_slot) -> true
                | None -> false
        | _ -> false) || (match constructorArgumentParameterSlot(argument)(semanticType)(state) with
        | Some(_slot) -> true
        | None -> false)

// A field a reference-counted cell can hold: one the arena reset keeps, a fresh value handed
// over as it is, a prepared one keeping its own retain, or one normalized into an owned value.
let placesConstructorField (argument: Expr) (temp: Int) (semanticType: SemanticType) (state: CoreLoweringState) = resultSurvivesReset(semanticType)(state) || isFreshOwnedConstructorArgument(argument)(temp)(state) || isPreparedConstructorArgument(argument)(semanticType)(state) || containsUnresolvedLayout(semanticType)(state) == false && (normalizesIntoOwnedValue(semanticType)(state) || isGeneralRcValueType(semanticType)(state))

let recursive placesConstructorFields (arguments: List(Expr)) (temps: List(Int)) (semanticTypes: List(SemanticType)) (state: CoreLoweringState) =
    match (arguments, temps, semanticTypes) with
        | (argument :: restArguments, temp :: restTemps, semanticType :: restTypes) ->
            placesConstructorField(argument)(temp)(resolveType(state)(semanticType))(state) && placesConstructorFields(restArguments)(restTemps)(restTypes)(state)
        | _ -> true

// The owned value a reference-counted cell stores for a field that is neither fresh nor
// prepared, nor a retain taken for the cell: a reference of its own, or a copy of an arena value.
// A pattern-owner duplicate the argument took for an arena cell becomes a plain read, since the
// copy takes the cell's reference whatever the owner's root parameter is finally placed as.
let normalizePlacedConstructorField (request: ConsumerRequest) (argument: Expr) (temp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    if resultSurvivesReset(semanticType)(state) || isFreshOwnedConstructorArgument(argument)(temp)(state) || retainTakenForCell(temp)(state) || retainedTailPatternOwner(request)(argument)(state) == false && isPreparedConstructorArgument(argument)(semanticType)(state)
    then (state, temp)
    else
        match match ownedResultPlanOf(semanticType)(state) with
            | Some(plan) -> Some(plan)
            | None -> generalCopyPlanOf(semanticType)(state) with
            | None -> (state, temp)
            | Some(plan) ->
                match neutralizeElementDuplicate(temp)(state) with
                    | (withdrawn, source) ->
                        match emitGuardedDeepCopy(source)(plan)(withdrawn) with
                            | (copied, copyTemp) -> (markRuntimeTemp(copyTemp)(RuntimeNewlyProduced)(copied), copyTemp)

let recursive normalizePlacedConstructorFields (request: ConsumerRequest) (arguments: List(Expr)) (temps: List(Int)) (semanticTypes: List(SemanticType)) (state: CoreLoweringState) (reversed: List(Int)) =
    match (arguments, temps, semanticTypes) with
        | (argument :: restArguments, temp :: restTemps, semanticType :: restTypes) ->
            match normalizePlacedConstructorField(request)(argument)(temp)(resolveType(state)(semanticType))(state) with
                | (normalized, normalizedTemp) -> normalizePlacedConstructorFields(request)(restArguments)(restTemps)(restTypes)(normalized)(normalizedTemp :: reversed)
        | _ -> (state, reverse(reversed))

// Stage 0's `PlaceConstructorOwningFreshChild`: an arena cell never releases what it holds, so a
// constructor storing a fresh reference-counted value, or a reference taken for the cell, is built
// on the reference-counted heap instead, owning that value outright and a reference of its own to
// every other heap field. Answers the arguments and whether the cell is placed there. Without general
// reference counting the cell stays in the arena.
let placeConstructorOwningFreshChild (layout: CoreConstructorLayout) arguments (request: ConsumerRequest) (resultType: SemanticType) (runtimeManaged: Bool) (stackAllocate: Bool) lowered =
    match (layout, lowered) with
        | (CoreConstructorLayout { isZeroCost = true }, _lowered) -> (lowered, runtimeManaged)
        | (_layout, LoweredCoreValues { state = state, temps = temps, semanticTypes = semanticTypes, error = None }) ->
            if generalRcEnabled(state) == false || runtimeManaged || stackAllocate || containsUnresolvedLayout(resultType)(state) || handsFreshChildrenToOwnedSlots(resultType)(state) || anyCellOwnedArgument(request)(arguments)(temps)(state) == false || placesConstructorFields(arguments)(temps)(semanticTypes)(state) == false
            then (lowered, runtimeManaged)
            else
                match normalizePlacedConstructorFields(request)(arguments)(temps)(semanticTypes)(state)([]) with
                    | (normalized, normalizedTemps) -> ((lowered with state = normalized, temps = normalizedTemps), true)
        | _ -> (lowered, runtimeManaged)

let recursive lowerConstructorArgumentsInto (request: ConsumerRequest) (runtimeManaged: Bool) arguments fieldTypes lower state reversedTemps reversedTypes =
    match (arguments, fieldTypes) with
        | (argument :: restArguments, fieldType :: restTypes) ->
            match state
            |> withConsumerRequest(constructorArgumentRequest(request)(runtimeManaged)(argument)(fieldType)(state))
            |> lower(argument)
            |> materializeStringChildArgument(runtimeManaged)(argument)(fieldType)
            |> ownPatternBoundStringChild(runtimeManaged)(argument)(fieldType)
            |> normalizeConstructorChildArgument(runtimeManaged)(fieldType)
            |> retainEscapingConstructorArgument(request)(runtimeManaged)(argument) with
                | LoweredCoreValue { state = failedState, error = Some(error) } -> failedCoreValues(failedState)(error)
                | LoweredCoreValue { state = nextState, temp = temp, semanticType = semanticType, error = None } ->
                    lowerConstructorArgumentsInto(
                        request,
                        runtimeManaged,
                        restArguments,
                        restTypes,
                        lower,
                        nextState,
                        temp :: reversedTemps,
                        semanticType :: reversedTypes
                    )
        | _ -> finishCoreValues(state)(reversedTemps)(reversedTypes)

// A retain the argument's preparation already took for the cell is the cell's reference.
let retainOwnedChildArgument (argument: Expr) (temp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match unspanArgument(argument) with
        | ExprVar(name) ->
            match liveRuntimeOwnerSlot(name)(state) with
                | Some(slot) ->
                    match if retainTakenForCell(temp)(state)
                    then success(temp)(semanticType)(state)
                    else keptSuccessorTransferRetain(temp)(semanticType)(state) with
                        | LoweredCoreValue { state = retained, temp = duplicate } ->
                            (withStateOwnerReleasePlans(retained
                            |> stateOwnerReleasePlans
                            |> shareOwnerReleasePlan(slot))(retained), duplicate)
                | None -> (state, temp)
        | _ -> (state, temp)

let recursive retainOwnedChildArguments (arguments: List(Expr)) (temps: List(Int)) (semanticTypes: List(SemanticType)) (state: CoreLoweringState) (reversed: List(Int)) =
    match (arguments, temps, semanticTypes) with
        | (argument :: restArguments, temp :: restTemps, semanticType :: restTypes) ->
            match retainOwnedChildArgument(argument)(temp)(semanticType)(state) with
                | (retained, retainedTemp) -> retainOwnedChildArguments(restArguments)(restTemps)(restTypes)(retained)(retainedTemp :: reversed)
        | _ -> (state, reverse(reversed))

// Stage 0's `RetainRuntimeManagedOwnedChildArguments`: a runtime cell owns its children, so an
// argument read from a binding that still owns its reference is retained after every argument
// is lowered.
let retainRuntimeCellChildren (runtimeManaged: Bool) arguments lowered =
    match lowered with
        | LoweredCoreValues { state = state, temps = temps, semanticTypes = semanticTypes, error = None } ->
            if runtimeManaged
            then
                match retainOwnedChildArguments(arguments)(temps)(semanticTypes)(state)([]) with
                    | (retained, retainedTemps) -> lowered with state = retained, temps = retainedTemps
            else lowered
        | _ -> lowered

let finishConstructorArguments arguments (request: ConsumerRequest) (stackAllocate: Bool) lower shape =
    match shape with
        | CoreConstructorShape { state = state, layout = layout, parameterTypes = parameterTypes, resultType = resultType, constructorRuntimeManaged = requested } ->
            let expectedArity = coreListLength(parameterTypes)
            in
                let actualArity = coreListLength(arguments)
                in
                    if expectedArity != actualArity
                    then
                        match layout with
                            | CoreConstructorLayout { name = name } ->
                                actualArity
                                |> CoreConstructorArityMismatch(name)(expectedArity)
                                |> failure(state)
                    else
                        match requested || isRuntimeManagedConstructorCandidate(layout)(arguments)(request)(state) with
                            | runtimeManaged ->
                                match lowerConstructorArgumentsInto(request)(runtimeManaged)(arguments)(parameterTypes)(lower)(withStateLoopResultPartDuplicates([])(state))([])([]) with
                                    | LoweredCoreValues { state = failedState, error = Some(error) } -> failure(failedState)(error)
                                    | LoweredCoreValues { state = valuesState, semanticTypes = actualTypes, error = None } as lowered ->
                                        match bindCoreValueTypes(parameterTypes)(actualTypes)(valuesState) with
                                            | (failedState, Some(error)) -> failure(failedState)(error)
                                            | (typedState, None) ->
                                                match (lowered with state = typedState) |> placeConstructorOwningFreshChild(layout)(arguments)(request)(resultType)(runtimeManaged)(stackAllocate) |> keepLoopResultPartDuplicatesOfArenaCell(stateLoopResultPartDuplicates(state)) with
                                                    | (placed, placedRuntimeManaged) ->
                                                        placed
                                                        |> handConstructorFreshChildren(layout)(arguments)(resultType)(placedRuntimeManaged)(stackAllocate)
                                                        |> retainConstructorLoopParameterArguments(request)(placedRuntimeManaged)(arguments)
                                                        |> retainRuntimeCellChildren(placedRuntimeManaged)(arguments)
                                                        |> finishConstructorAllocation(arguments)(stackAllocate)(layout)(resultType)(placedRuntimeManaged)

// The stack request is taken and cleared here, so a nested constructor argument lowered further
// down never claims the one its parent's `let` or `match` made.
let lowerConstructor layout arguments (request: ConsumerRequest) lower state =
    (let stackAllocate = stateStackAllocateConstructor(state)
    in
        state
        |> withStateStackAllocateConstructor(false)
        |> markResourceArgumentsMoved(arguments)
        |> instantiateConstructor(layout)
        |> finishConstructorArguments(arguments)(request)(stackAllocate)(lower))

let recursive resolveCoreTypes state semanticTypes =
    match semanticTypes with
        | [] -> []
        | semanticType :: rest -> resolveType(state)(semanticType) :: resolveCoreTypes(state)(rest)

let recursive emitCoreInstructions instructions state =
    match instructions with
        | [] -> state
        | instruction :: rest ->
            state
            |> emit(instruction)
            |> emitCoreInstructions(rest)

let finishBuiltinUnit resultType lower state =
    match constructorLayout("Unit")(state) with
        | None -> failure(state)(UnknownLoweringBinding("Unit"))
        | Some(layout) ->
            match lowerConstructor(layout)([])(emptyConsumerRequest)(lower)(state) with
                | LoweredCoreValue { state = unitState, temp = temp, error = None } ->
                    success(temp)(resolveType(unitState)(resultType))(unitState)
                | failed -> failed

let finishBuiltinResult resultType lower result emittedState =
    match result with
        | CoreBuiltinTemp(temp) ->
            success(temp)(resolveType(emittedState)(resultType))(emittedState)
        | CoreBuiltinNever(temp) -> success(temp)(SemNever)(emittedState)
        | CoreBuiltinUnit -> finishBuiltinUnit(resultType)(lower)(emittedState)

// A consumer that keeps nothing of its string argument (`print`, `write`, `writeLine`,
// `writeError`, `Text.byteLength`) releases a newly produced reference-counted one right after
// the use, stage 0's `ReleaseConsumedOwnedOperand` at those sites.
let consumesStringOperand (kind: CoreBuiltinKind) =
    match kind with
        | CorePrint -> true
        | CoreWrite -> true
        | CoreWriteLine -> true
        | CoreWriteError(_newline) -> true
        | CoreTextByteLength -> true
        | _ -> false

let consumedBuiltinOperand (kind: CoreBuiltinKind) (temps: List(Int)) =
    match (consumesStringOperand(kind), temps) with
        | (true, operand :: []) -> Some(operand)
        | _ -> None

let releaseConsumedBuiltinOperand (consumedOperand: Maybe(Int)) (state: CoreLoweringState) =
    match consumedOperand with
        | Some(operand) -> releaseConsumedOperand(operand)(state)
        | None -> state

// A byte builtin that only reads its buffer (a length, element or integer read, a search, a
// comparison, a hash, a copied-out text, a copyRange source) releases a newly produced one right
// after the use, stage 0's `ReleaseConsumedOwnedBytesOperand` at those sites.
let consumedBytesOperands (kind: CoreBuiltinKind) (temps: List(Int)) =
    match (kind, temps) with
        | (CoreBytesLength, value :: []) -> [value]
        | (CoreBytesGet, value :: _rest) -> [value]
        | (CoreBytesGetU16Le, value :: _rest) -> [value]
        | (CoreBytesGetU32Le, value :: _rest) -> [value]
        | (CoreBytesGetU64Le, value :: _rest) -> [value]
        | (CoreBytesIndexOf, value :: _rest) -> [value]
        | (CoreBytesScanHash, value :: _rest) -> [value]
        | (CoreBytesHash, value :: []) -> [value]
        | (CoreBytesSubText, value :: _rest) -> [value]
        | (CoreBytesCompare, left :: right :: []) ->
            if left == right
            then [left]
            else [left, right]
        | (CoreBytesCopyRange, destination :: _offset :: source :: _rest) ->
            if source == destination
            then []
            else [source]
        | _ -> []

let recursive releaseConsumedBytesOperands (operands: List(Int)) (state: CoreLoweringState) =
    match operands with
        | [] -> state
        | operand :: rest ->
            state
            |> releaseConsumedOperandOf("Bytes")(operand)
            |> releaseConsumedBytesOperands(rest)

// A fresh-string builtin that honored the runtime request produced a reference-counted string;
// `Byte.allocate` and the pure byte updates always produce a reference-counted buffer, as stage 0
// marks them runtime-managed.
let markFreshBuiltinResult (kind: CoreBuiltinKind) (runtimeManaged: Bool) (lowered: LoweredCoreValue) =
    if runtimeManaged && isFreshStringBuiltinKind(kind) || isAlwaysRuntimeBytesBuiltinKind(kind)
    then markLoweredRuntimeTemp(lowered)
    else lowered

let finishBuiltinEmission resultType lower consumedOperand consumedBytes state emission =
    match emission with
        | CoreBuiltinEmission { error = Some(error) } -> failure(state)(UnsupportedCoreBuiltinLowering(error))
        | CoreBuiltinEmission { instructions = instructions, nextTemp = nextTemp, result = result, error = None } ->
            state
            |> withNextTemp(nextTemp)
            |> emitCoreInstructions(instructions)
            |> releaseConsumedBuiltinOperand(consumedOperand)
            |> releaseConsumedBytesOperands(consumedBytes)
            |> finishBuiltinResult(resultType)(lower)(result)

// Step-8 shadow comparison, rule 5: what a deep copy did at the site (passed a value whose type is
// still a variable through, or copied it at its type), against a copy at the recorded type.
let shadowDeepCopy (copied: Maybe(SemanticType)) (state: CoreLoweringState) =
    if shadowing(state)
    then
        observeShadow(5)(match copied with
            | Some(copiedType) -> "copy " + Ashes.Trait.Show.show(copiedType)
            | None -> "pass-through")(match shadowSiteTypeText(state) with
            | Some(recorded) -> Some("copy " + recorded)
            | None -> None)(state)
    else state

// The argument's independent clone at its resolved type. A value just produced on the
// reference-counted heap and handed only to the copy (a call's owned result) is released once it is
// copied: nothing else holds it.
let deepCopyAtType (temp: Int) (resolved: SemanticType) (state: CoreLoweringState) =
    match state
    |> shadowDeepCopy(Some(resolved))
    |> emitTcoDeepCopy(temp)(resolved) with
        | (copied, resultTemp) ->
            (if tempIsNewlyProduced(temp)(state)
            then emitArenaResultRelease(temp)(resolved)(copied)
            else copied) |> success(resultTemp)(resolved)

// An argument whose type is still a variable. Without a table it passes through as it is and its
// type is recorded, so the body is lowered again once the type resolves (stage 0 defers the copy to
// that point). Lowered from the type table, the copy is made at the type recorded for the call,
// closed by the body's instantiation and the settled variables, which is the type that later
// lowering would have resolved; a record that is open, missing or does not bind keeps the deferral.
let deepCopyOfOpenType (temp: Int) (unresolved: SemanticType) (state: CoreLoweringState) =
    (let deferred =
        given (unit: Unit) ->
            state
            |> withStateUnresolvedCallResults(unresolved :: stateUnresolvedCallResults(state))
            |> shadowDeepCopy(None)
            |> success(temp)(unresolved)
    in
        match (stateNodeTypes(state), shadowSiteType(state)) with
            | (Some(_table), Some(recorded)) ->
                match bindType(unresolved)(recorded)(state) with
                    | (bound, None) -> deepCopyAtType(temp)(recorded)(bound)
                    | (_failed, Some(_error)) -> deferred(Unit)
            | _ -> deferred(Unit))

// Stage 0's `LowerInternalDeepCopy`: the argument's independent clone, synthesized by its resolved
// type; an argument whose type is still a variable as deepCopyOfOpenType.
let lowerInternalDeepCopy (temp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemNever -> success(temp)(SemNever)(state)
        | SemVariable(_id) as unresolved -> deepCopyOfOpenType(temp)(unresolved)(state)
        | resolved -> deepCopyAtType(temp)(resolved)(state)

let emitBuiltin layout resultType lower runtimeManaged reuseInput lowered =
    match (layout, lowered) with
        | (_, LoweredCoreValues { state = failedState, error = Some(error) }) -> failure(failedState)(error)
        | (CoreBuiltinLayout { kind = CoreInternalDeepCopy }, LoweredCoreValues { state = state, temps = temp :: [], semanticTypes = semanticType :: [], error = None }) -> lowerInternalDeepCopy(temp)(semanticType)(state)
        | (CoreBuiltinLayout { kind = kind }, LoweredCoreValues { state = state, temps = temps, semanticTypes = semanticTypes, error = None }) ->
            match state with
                | CoreLoweringState { nextTemp = nextTemp } ->
                    semanticTypes
                    |> resolveCoreTypes(state)
                    |> emitCoreBuiltinReusing(kind)(runtimeManaged)(reuseInput)(nextTemp)(temps)
                    |> finishBuiltinEmission(resultType)(lower)(consumedBuiltinOperand(kind)(temps))(consumedBytesOperands(kind)(temps))(state)
                    |> markFreshBuiltinResult(kind)(runtimeManaged)

let emitTypedBuiltin layout resultType lower runtimeManaged reuseInput (lowered: LoweredCoreValues) typedState =
    emitBuiltin(
        layout,
        resultType,
        lower,
        runtimeManaged,
        reuseInput,
        lowered with state = typedState
    )

// Stage 0's LowerTextFromInt accepts an `Int` OR a `u8`/`u16`/`u32` argument through an ad-hoc
// rule rather than unification (every uN is stored widened, so the digit phases read the word
// directly). Mirror it by letting a matching small-unsigned actual stand in for the scheme's
// `Int` parameter before the types are bound.
let acceptBuiltinArgumentWidths kind expected actual state =
    match kind with
        | CoreTextFromInt ->
            match (expected, actual) with
                | (SemInt :: expectedTail, actualHead :: _actualTail) ->
                    match resolveType(state)(actualHead) with
                        | SemUInt(bits) ->
                            if bits <= 32
                            then actualHead :: expectedTail
                            else expected
                        | _ -> expected
                | _ -> expected
        | CoreTextToHex ->
            match (expected, actual) with
                | (SemInt :: expectedTail, actualHead :: _actualTail) ->
                    match resolveType(state)(actualHead) with
                        | SemUInt(_bits) -> actualHead :: expectedTail
                        | _ -> expected
                | _ -> expected
        | CoreUIntToInt ->
            match (expected, actual) with
                | (SemUInt(8) :: expectedTail, actualHead :: _actualTail) ->
                    match resolveType(state)(actualHead) with
                        | SemUInt(_bits) -> actualHead :: expectedTail
                        | _ -> expected
                | _ -> expected
        | _ -> expected

let isNewlyProducedRuntimeTemp (temp: Int) (state: CoreLoweringState) =
    match runtimeTempStateOf(temp)(state) with
        | Some(RuntimeNewlyProduced) -> true
        | _ -> false

// Stage 0's `NormalizeBytesForPureUpdate` and `CanReusePureBytesUpdateInput`: the buffer a pure
// byte update writes is copied onto the reference-counted heap unless it already lives there, and
// the update writes in place into a fresh buffer (a newly produced value no binding reads, or the
// copy itself), which it takes over. Answers the lowered buffer and whether the update reuses it.
let normalizeBytesUpdateInput (argument: Expr) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { error = Some(_error) } -> (lowered, false)
        | LoweredCoreValue { state = state, temp = temp } ->
            if childIsRuntimeManaged(argument)(temp)(state)
            then (lowered, isVariableArgument(argument) == false && isNewlyProducedRuntimeTemp(temp)(state))
            else
                match freshTemp(state) with
                    | FreshTemp { state = allocated, temp = copy } -> ((lowered with state = rcNormalizationCopyOut(copy)(temp)(-1)(allocated), temp = copy), true)

// A builtin's arguments lowered in order; a pure byte update normalizes its buffer right after
// lowering it, before its other arguments, where stage 0 normalizes it. Answers the lowered values
// and whether the update reuses its buffer.
let lowerBuiltinValues (kind: CoreBuiltinKind) (arguments: List(Expr)) lower (state: CoreLoweringState) =
    match (isBytesUpdateBuiltinKind(kind), arguments) with
        | (true, first :: rest) ->
            match state
            |> lower(first)
            |> normalizeBytesUpdateInput(first) with
                | (LoweredCoreValue { state = failedState, error = Some(error) }, _reuseInput) -> (failedCoreValues(failedState)(error), false)
                | (LoweredCoreValue { state = nextState, temp = temp, semanticType = semanticType, error = None }, reuseInput) -> (lowerCoreValuesInto(rest)(lower)(nextState)([temp])([semanticType]), reuseInput)
        | _ -> (lowerCoreValues(arguments)(lower)(state), false)

let finishBuiltinArity arguments lower runtimeManaged shape expectedArity actualArity =
    match shape with
        | CoreBuiltinShape { state = state, layout = layout, parameterTypes = parameterTypes, resultType = resultType } ->
            if actualArity != expectedArity
            then
                match layout with
                    | CoreBuiltinLayout { moduleName = moduleName, memberName = memberName } ->
                        actualArity
                        |> CoreBuiltinArityMismatch(
                            moduleName,
                            memberName,
                            expectedArity
                        )
                        |> failure(state)
            else
                match state
                |> clearConsumerRequest
                |> lowerBuiltinValues(layout.kind)(arguments)(lower) with
                    | (LoweredCoreValues { state = failedState, error = Some(error) }, _reuseInput) -> failure(failedState)(error)
                    | (LoweredCoreValues { state = valuesState, semanticTypes = actualTypes, error = None } as lowered, reuseInput) ->
                        let boundParameterTypes =
                            match layout with
                                | CoreBuiltinLayout { kind = builtinKind } -> acceptBuiltinArgumentWidths(builtinKind)(parameterTypes)(actualTypes)(valuesState)
                        in
                            match bindCoreValueTypes(boundParameterTypes)(actualTypes)(valuesState) with
                                | (failedState, Some(error)) -> failure(failedState)(error)
                                | (typedState, None) -> emitTypedBuiltin(layout)(resultType)(lower)(runtimeManaged)(reuseInput)(lowered)(typedState)

let finishBuiltinArguments arguments lower runtimeManaged shape =
    match shape with
        | CoreBuiltinShape { parameterTypes = parameterTypes } ->
            finishBuiltinArity(
                arguments,
                lower,
                runtimeManaged,
                shape,
                coreListLength(parameterTypes),
                coreListLength(arguments)
            )

// A builtin reading a resource argument is checked for use after its release before anything is
// lowered; a closing builtin releases the binding once the call is lowered. Stage 0's
// `LowerFileClose` checks nothing first: closing a handle already closed, or passed to a function,
// is not reported.
let builtinResourceArgument (arguments: List(Expr)) =
    match arguments with
        | first :: _rest -> Some(first)
        | [] -> None

let checkBuiltinResourceUse (kind: CoreBuiltinKind) (arguments: List(Expr)) (state: CoreLoweringState) =
    match (builtinResourceRole(kind), builtinResourceArgument(arguments)) with
        | (BuiltinUsesResource, Some(argument)) ->
            match checkResourceArgumentUse(argument)(state) with
                | Some(error) -> Some((error, argument))
                | None -> None
        | _ -> None

let releaseClosedBuiltinArgument (kind: CoreBuiltinKind) (arguments: List(Expr)) (lowered: LoweredCoreValue) =
    match (builtinResourceRole(kind), builtinResourceArgument(arguments), lowered) with
        | (BuiltinClosesResource, Some(argument), LoweredCoreValue { state = state, error = None }) -> lowered with state = markResourceArgumentClosed(argument)(state)
        | _ -> lowered

let lowerBuiltin (layout: CoreBuiltinLayout) arguments lower state =
    match checkBuiltinResourceUse(layout.kind)(arguments)(state) with
        | Some((error, argument)) -> failureAt(argument)(error)(state)
        | None ->
            state
            |> instantiateBuiltin(layout)
            |> finishBuiltinArguments(arguments)(lower)(runtimeStringRequested(state))
            |> releaseClosedBuiltinArgument(layout.kind)(arguments)

let recursive collectCallSpine expression =
    match expression with
        | ExprAt(_span, inner) -> collectCallSpine(inner)
        | ExprNode(_node, inner) -> collectCallSpine(inner)
        | ExprCall(function, argument, _whitespace, _layout) ->
            match collectCallSpine(function) with
                | CoreCallSpine { root = root, arguments = arguments } ->
                    CoreCallSpine(
                        root = root,
                        arguments = append(arguments)([argument])
                    )
        | root -> CoreCallSpine(root = root, arguments = [])
