// Deferred call copy-outs, taken-over settle walks, independent result copies, part hand-over and
// the finishing of a function's result. A part of `AshesCompiler.Semantics.CoreLowering`.

import Ashes.Collection.List.append
import Ashes.Collection.List.length
import Ashes.Collection.List.map
import Ashes.Collection.List.reverse
import Ashes.Collection.Map.MapTree
import AshesCompiler.Semantics.CallOwnership
import AshesCompiler.Semantics.ConstructionRetains
import AshesCompiler.Semantics.HeapLayoutClassification
import AshesCompiler.Semantics.IrControlFlowGraph.containsInt
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrInstructionTemps.mapInstructionTemps
import AshesCompiler.Semantics.StateMachineTransform.getDefinedTemps
import AshesCompiler.Semantics.ValueFlowSlots.localSlotsReachingTempsWithAliases
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.StructuralDroppers
import AshesCompiler.Semantics.StructuralCopiers
import AshesCompiler.Semantics.TypeSchemes
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.CoreLowering.LoweringState
import AshesCompiler.Semantics.CoreLowering.Emit
import AshesCompiler.Semantics.CoreLowering.Owners
import AshesCompiler.Semantics.CoreLowering.CopyPlans
import AshesCompiler.Semantics.CoreLowering.TcoReset
export (
    value cancelArenaResultRequest,
    value callRequestsArenaResult,
    value emitPlainCallCopyOut,
    value takenOverSettleBase,
    value letCopyReleaseBase,
    value emitOwnFieldWalk,
    value resolvePendingTcoResets,
    value resolveLoweredPendingBlocks,
    value arenaDeepCopySupported,
    value canNormalizeResultIntoArena,
    value emitArenaResultDeepCopy,
    value emitArenaResultRelease,
    value isOwnedSlotOf,
    value isScalarFieldVariant,
    value contractLayoutOrSelf,
    value variablesNotHeld,
    value joinVariables,
    value resolvePhantomResultLayout,
    value resolvePhantomNormalizedResultLayout,
    value allRuntimeOwnedCopyElements,
    value definitionOfTemp,
    value nthElementType,
    value contractElementIndices,
    value isFreshRuntimeTemp,
    value soleCallSourceThroughLocals,
    value callTuplePartsRequest,
    value wholeResultHandedOverBit,
    value wholeResultHandOverMarker,
    value independentResultBit,
    value emitClosureReturnsGeneralRcOwnedFlag,
    value listHeadsHandedOverBit,
    value listHeadHandOverMarker,
    value layoutInstantiatedFieldTypes,
    value emitRuntimeShallowAggregateDrop,
    value dropPartDuplicateSlotsWhenTheResultCopiesAnyway,
    value finishGeneralRcFunctionResultWithLayout,
    value tupleResultParts,
    value tempIsProducedOwned,
    value finishedResultRuntimeManaged,
    value normalizeFunctionResult,
)

let recursive lookupTcoReset (resetId: Int) (resets: List(CoreTcoReset)) =
    match resets with
        | [] -> None
        | (CoreTcoReset { resetId = candidate } as reset) :: rest ->
            if candidate == resetId
            then Some(reset)
            else lookupTcoReset(resetId)(rest)

let recursive pendingCopyOutReloading (target: Int) (slot: Int) (copyOuts: List(CorePendingCallCopyOut)) =
    match copyOuts with
        | [] -> None
        | (CorePendingCallCopyOut { deferredReloadTemp = reloadTemp, deferredResultSlot = resultSlot } as pending) :: rest ->
            if target == reloadTemp && slot == resultSlot
            then Some(pending)
            else pendingCopyOutReloading(target)(slot)(rest)

// The pending copy-out whose reload this instruction is: the reload temp is fresh and defined
// once, so the `LoadLocal` that reads the result slot into it names exactly one copy-out.
let pendingCopyOutOf (instruction: IrInstruction) (copyOuts: List(CorePendingCallCopyOut)) =
    match instruction.instruction with
        | LoadLocal(target, slot) -> pendingCopyOutReloading(target)(slot)(copyOuts)
        | _ -> None

let recursive renamedTemp (renames: List((Int, Int))) (temp: Int) =
    match renames with
        | [] -> temp
        | (from, to) :: rest ->
            if temp == from
            then to
            else renamedTemp(rest)(temp)

// The temps a removed slot round trip left behind read the result they stood for.
let renameInstructionTemps (renames: List((Int, Int))) (instruction: IrInstruction) =
    match renames with
        | [] -> instruction
        | _ ->
            instruction with instruction = mapInstructionTemps(renamedTemp(renames))(instruction.instruction)

// Stage 0's `LowerCallCopyOutResult` copy: reference-counted, or an arena copy where the function may
// run under a live handler post.
let plainCallCopyOutInstruction (copyOut: CallCopyOut) (runtime: Bool) (copyTemp: Int) (resultTemp: Int) =
    if runtime
    then callCopyOutInstruction(copyOut)(copyTemp)(resultTemp)
    else
        match copyOut with
            | ShallowCallCopyOut(staticSizeBytes) -> CopyOutArena(copyTemp)(resultTemp)(staticSizeBytes)(false)(ArenaCallBoundary)(None)
            | ListCallCopyOut(headCopy) -> CopyOutList(copyTemp)(resultTemp)(headCopy)(false)(ArenaCallBoundary)

// Stage 0's `LowerCallCopyOutResult` for a deferred block: the window reset, the copy of the
// resolved kind, and the reclaim, all unlocated. The conditional variant never reaches here: a
// result whose type was unresolved at the call read no returns flag.
let emitUnguardedCallCopyOut copyOut (resultTemp: Int) cursorSlot endSlot preRestoreSlot (state: CoreLoweringState) =
    match state
    |> unlocatedInstruction(RestoreArenaState(cursorSlot)(endSlot)(preRestoreSlot)(false))
    |> freshTemp with
        | FreshTemp { state = restored, temp = copiedTemp } ->
            (restored
            |> unlocatedInstruction(plainCallCopyOutInstruction(copyOut)(ordinaryRcPlacementAllowed(restored))(copiedTemp)(resultTemp))
            |> markCopyRuntime(ordinaryRcPlacementAllowed(restored))(copiedTemp)
            |> unlocatedInstruction(ReclaimArenaChunks(endSlot)(preRestoreSlot)(false)), copiedTemp)

// With a capability in the program the copy-out runs only while no one-shot post is pending: the
// result travels through a slot that the skipped path leaves holding the original pointer, and the
// slot's value is reloaded as the call's result.
let emitPlainCallCopyOut copyOut (resultTemp: Int) cursorSlot endSlot preRestoreSlot (state: CoreLoweringState) =
    if stateCapabilityGlobalCount(state) == 0
    then emitUnguardedCallCopyOut(copyOut)(resultTemp)(cursorSlot)(endSlot)(preRestoreSlot)(state)
    else
        match freshLocal(state) with
            | FreshLocal { state = allocated, local = guardSlot } ->
                match allocated
                |> unlocatedInstruction(StoreLocal(guardSlot)(resultTemp))
                |> beginLivePostsGuardBy(unlocatedInstruction) with
                    | (guarded, skipLabel) ->
                        match emitUnguardedCallCopyOut(copyOut)(resultTemp)(cursorSlot)(endSlot)(preRestoreSlot)(guarded) with
                            | (copied, copiedTemp) ->
                                match copied
                                |> unlocatedInstruction(StoreLocal(guardSlot)(copiedTemp))
                                |> endLivePostsGuardBy(unlocatedInstruction)(skipLabel)
                                |> freshTemp with
                                    | FreshTemp { state = joined, temp = reloadTemp } ->
                                        (unlocatedInstruction(LoadLocal(reloadTemp)(guardSlot))(joined), reloadTemp)

// Stage 0's `EmitDeferredCallResultCopyOut`: the copy-out block the call would have received had
// its type been known, storing the copy back into the slot the result travels through; nothing
// when the resolved type needs no copy-out.
let resolveDeferredCallCopyOutByKind (pending: CorePendingCallCopyOut) (state: CoreLoweringState) =
    match callCopyOutOf(pending.deferredType)(state) with
        | None -> (state, false)
        | Some(copyOut) ->
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = currentTemp } ->
                    match allocated
                    |> unlocatedInstruction(LoadLocal(currentTemp)(pending.deferredResultSlot))
                    |> emitPlainCallCopyOut(copyOut)(currentTemp)(pending.deferredCursorSlot)(pending.deferredEndSlot)(pending.deferredPreRestoreSlot) with
                        | (copied, copiedTemp) ->
                            (unlocatedInstruction(StoreLocal(pending.deferredResultSlot)(copiedTemp))(copied), true)

// The request word's arena-result constant, rewritten to ask for nothing. `temp` is the word
// the call passes; an `OrInt` joins the argument ownership flag to the request constant.
let recursive withdrawArenaResultRequest (temp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> []
        | (IrInstruction { instruction = OrInt(target, _flag, request) } as instruction) :: rest ->
            if target == temp
            then instruction :: withdrawArenaResultRequest(request)(rest)
            else instruction :: withdrawArenaResultRequest(temp)(rest)
        | (IrInstruction { instruction = LoadConstInt(target, value) } as instruction) :: rest ->
            if target == temp
            then
                if value == 2
                then (instruction with instruction = LoadConstInt(target)(0)) :: rest
                else instruction :: rest
            else instruction :: withdrawArenaResultRequest(temp)(rest)
        | instruction :: rest -> instruction :: withdrawArenaResultRequest(temp)(rest)

// Whether the word `temp` a call passes carries the arena-result request constant, through the
// `OrInt`s that join it to the argument ownership flag or the tuple parts request.
let recursive wordRequestsArenaResult (temp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = OrInt(target, _flag, request) } :: rest ->
            if target == temp
            then wordRequestsArenaResult(request)(rest)
            else wordRequestsArenaResult(temp)(rest)
        | IrInstruction { instruction = LoadConstInt(target, value) } :: rest ->
            if target == temp
            then value == 2
            else wordRequestsArenaResult(temp)(rest)
        | _instruction :: rest -> wordRequestsArenaResult(temp)(rest)

// Whether the call producing `resultTemp` asked its callee for an arena result: the callee then
// deep-copies its result into the caller's window whatever its compiled body returns.
let recursive callRequestsArenaResult (resultTemp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = CallClosure(target, _closure, _argument, word) } :: rest ->
            if target == resultTemp
            then word >= 0 && wordRequestsArenaResult(word)(rest)
            else callRequestsArenaResult(resultTemp)(rest)
        | _instruction :: rest -> callRequestsArenaResult(resultTemp)(rest)

let recursive cancelRequestOfCall (resultTemp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> []
        | (IrInstruction { instruction = CallClosure(target, _closure, _argument, word) } as instruction) :: rest ->
            if target == resultTemp
            then
                if word < 0
                then instructions
                else instruction :: withdrawArenaResultRequest(word)(rest)
            else instruction :: cancelRequestOfCall(resultTemp)(rest)
        | instruction :: rest -> instruction :: cancelRequestOfCall(resultTemp)(rest)

// Stage 0's `CancelArenaResultRequest`: a call whose result type was unresolved when it was
// emitted requested an arena result, for a caller that cannot own it. A result of the contract's
// types is owned by the caller, so the request word is rewritten to ask for nothing: the callee
// then hands over its reference-counted result instead of deep-copying it into the arena.
let cancelArenaResultRequest (resultTemp: Int) (state: CoreLoweringState) = state with reversedInstructions = cancelRequestOfCall(resultTemp)(state.reversedInstructions)

// A deferred self call whose result turned out to be of the contract's types takes stage 0's
// `LowerGeneralRcCallResult` block: the result slot is an owned slot of the function, the window
// is reset and reclaimed around it, and the arena-result request is withdrawn. A self call's
// result is handed over owned, so no normalization follows.
let resolveDeferredCallCopyOutUnobserved (pending: CorePendingCallCopyOut) (state: CoreLoweringState) =
    if pending.deferredOwned
    then
        (state
        |> cancelArenaResultRequest(pending.deferredResultTemp)
        |> emitGuardedRestoreAndReclaimBy(unlocatedInstruction)(pending.deferredCursorSlot)(pending.deferredEndSlot)(pending.deferredPreRestoreSlot), true)
    else resolveDeferredCallCopyOutByKind(pending)(state)

// Step-8 shadow comparison, entry 8c: how a deferred call copy-out resolved once its type settled:
// its result owned by the contract (the window reset at the call), copied out at the call, or left
// in the arena window with no copy-out, with the resolved type's head and whether the call sat in a
// loop body.
let shadowDeferredOutcome (pending: CorePendingCallCopyOut) (state: CoreLoweringState) =
    if shadowing(state)
    then
        let outcome =
            if pending.deferredOwned
            then "owned by the contract"
            else
                match callCopyOutOf(pending.deferredType)(state) with
                    | Some(_copyOut) -> "copied out at the call"
                    | None -> "no copy-out: window left open"
        in
            let head =
                match resolveType(state)(pending.deferredType) with
                    | SemNamed(_symbol, name, _arguments) -> name
                    | SemList(_element) -> "List"
                    | SemTuple(_elements) -> "tuple"
                    | SemFunction(_argument, _result, _row) -> "function"
                    | SemVariable(_id) -> "open"
                    | other -> Ashes.Trait.Show.show(other)
            in
                let placement =
                    if pending.deferredInLoopBody
                    then "loop body"
                    else "no loop"
                in observeShadowUnrecorded(13)("deferred")(outcome + "; " + head + "; " + placement)(state)
    else state

let resolveDeferredCallCopyOut (pending: CorePendingCallCopyOut) (state: CoreLoweringState) =
    state
    |> shadowDeferredOutcome(pending)
    |> resolveDeferredCallCopyOutUnobserved(pending)

// The store a deferred result travelled through, the instruction just re-emitted, when its block
// resolved to nothing: removed, so the reload's temp reads the result itself.
let dropDeferredStore (pending: CorePendingCallCopyOut) (state: CoreLoweringState) =
    match state.reversedInstructions with
        | IrInstruction { instruction = StoreLocal(slot, _source) } :: rest ->
            if slot == pending.deferredResultSlot
            then state with reversedInstructions = rest
            else state
        | _ -> state

let keepSplicedInstruction (renames: List((Int, Int))) (instruction: IrInstruction) (state: CoreLoweringState) = state with reversedInstructions = renameInstructionTemps(renames)(instruction) :: state.reversedInstructions

// The id range of the taken-over record settlement placeholders, apart from the reset ids.
let takenOverSettleBase = 3000000

let recursive lookupTakenOverSettle (id: Int) (settles: List(CoreTakenOverSettle)) =
    match settles with
        | [] -> None
        | (CoreTakenOverSettle { settleId = candidate } as settle) :: rest ->
            if candidate == id
            then Some(settle)
            else lookupTakenOverSettle(id)(rest)

let recursive renameTakenOverSettle (id: Int) (temp: Int) (settles: List(CoreTakenOverSettle)) =
    match settles with
        | [] -> []
        | settle :: rest ->
            if settle.settleId == id
            then (settle with settleTemp = temp) :: rest
            else settle :: renameTakenOverSettle(id)(temp)(rest)

// A settlement placeholder kept through the first splice round keeps its result temp under the
// round's renames.
let keepTakenOverSettleRenamed (renames: List((Int, Int))) (resetId: Int) (state: CoreLoweringState) =
    if resetId >= takenOverSettleBase
    then
        match state
        |> statePendingTakenOverSettles
        |> lookupTakenOverSettle(resetId - takenOverSettleBase) with
            | Some(settle) ->
                withStatePendingTakenOverSettles(state
                |> statePendingTakenOverSettles
                |> renameTakenOverSettle(settle.settleId)(renamedTemp(renames)(settle.settleTemp)))(state)
            | None -> state
    else state

// Every placeholder of a lowered body becomes its block in one walk, in instruction order, as
// stage 0's `ResolvePendingBlocks` does: a `TcoResetPending` its arena block, a deferred
// call-result copy-out (found by its own reload instruction) the copy-out block ahead of the
// reload, or, resolved to nothing, the removal of the store before it and of the reload itself,
// every later read of the reload's temp renamed to the result.
// The id range of the placeholders a `let` of a deferred call copy-out's reload leaves at its
// scope exit, apart from the reset and settlement ids: the base plus the binding's slot.
let letCopyReleaseBase = 4000000

// Whether a deferred copy-out, its type now settled, copies its result onto the reference-counted
// heap and no owned slot of the function takes the result over (a self call's result of the
// contract's types is one).
let copyOutResolvesToHeapCopy (copyOut: CorePendingCallCopyOut) (state: CoreLoweringState) =
    if copyOut.deferredOwned || containsUnresolvedLayout(copyOut.deferredType)(state) || isGeneralRcValueType(copyOut.deferredType)(state)
    then false
    else
        match callCopyOutOf(copyOut.deferredType)(state) with
            | Some(_copyOut) -> ordinaryRcPlacementAllowed(state)
            | None -> false

let recursive letReleaseCopyOut (slot: Int) (copyOuts: List(CorePendingCallCopyOut)) =
    match copyOuts with
        | [] -> None
        | copyOut :: rest ->
            if containsInt(slot)(copyOut.deferredLetReleaseSlots)
            then Some(copyOut)
            else letReleaseCopyOut(slot)(rest)

// The release of a `let` binding of a deferred copy-out's reload that the copy-out resolved to a
// copy onto the reference-counted heap: the binding owns that copy, released with the inline walk
// stage 0's binding takes; nothing for any other resolution.
let emitDeferredLetCopyRelease (slot: Int) (copyOuts: List(CorePendingCallCopyOut)) (state: CoreLoweringState) =
    match letReleaseCopyOut(slot)(copyOuts) with
        | Some(copyOut) ->
            if copyOutResolvesToHeapCopy(copyOut)(state)
            then
                let resolved = resolveType(state)(copyOut.deferredType)
                in
                    match stateOwnedTypeNameOf(resolved)(state) with
                        | Some(typeName) ->
                            (state with currentSpan = None)
                            |> withStateRuntimeOwners((slot, true) :: stateRuntimeOwners(state))
                            |> withStateOwnerReleasePlans((slot, resolved, OwnedReleasePlan(deepUnique = false, constructorName = None, matchFieldTransfer = None)) :: stateOwnerReleasePlans(state))
                            |> emitOwnedLetRelease(typeName)(slot)
                            |> retireRuntimeOwner(slot)
                            |> (given (released: CoreLoweringState) -> released with currentSpan = state.currentSpan)
                        | None -> state
            else state
        | None -> state

let recursive splicePendingBlocks (instructions: List(IrInstruction)) (resets: List(CoreTcoReset)) (copyOuts: List(CorePendingCallCopyOut)) (renames: List((Int, Int))) (state: CoreLoweringState) =
    match instructions with
        | [] -> state
        | instruction :: rest ->
            match pendingCopyOutOf(instruction)(copyOuts) with
                | Some(pending) ->
                    match resolveDeferredCallCopyOut(pending)(state) with
                        | (resolved, true) ->
                            resolved
                            |> keepSplicedInstruction(renames)(instruction)
                            |> splicePendingBlocks(rest)(resets)(copyOuts)(renames)
                        | (unchanged, false) ->
                            unchanged
                            |> dropDeferredStore(pending)
                            |> splicePendingBlocks(rest)(resets)(copyOuts)((pending.deferredReloadTemp, renamedTemp(renames)(pending.deferredResultTemp)) :: renames)
                | None ->
                    match instruction with
                        | IrInstruction { instruction = TcoResetPending(resetId, _usedTemps, _readLocals) } ->
                            if resetId >= letCopyReleaseBase
                            then
                                state
                                |> emitDeferredLetCopyRelease(resetId - letCopyReleaseBase)(copyOuts)
                                |> splicePendingBlocks(rest)(resets)(copyOuts)(renames)
                            else
                                match lookupTcoReset(resetId)(resets) with
                                    | Some(reset) ->
                                        state
                                        |> emitResolvedTcoReset(reset)
                                        |> splicePendingBlocks(rest)(resets)(copyOuts)(renames)
                                    | None ->
                                        state
                                        |> keepTakenOverSettleRenamed(renames)(resetId)
                                        |> keepSplicedInstruction(renames)(instruction)
                                        |> splicePendingBlocks(rest)(resets)(copyOuts)(renames)
                        | _ ->
                            state
                            |> keepSplicedInstruction(renames)(instruction)
                            |> splicePendingBlocks(rest)(resets)(copyOuts)(renames)

// Stage 0's `EmitTakenOverWalk`: releases the reference each arena cell's head tuple holds at its
// first field, up to the first reference-counted cell; a head tuple on the reference-counted heap
// owns its fields already.
let emitTakenOverWalk (resultTemp: Int) (element: SemanticType) (state: CoreLoweringState) =
    match freshLabel("taken_over_done")(state) with
        | FreshLabel { state = doneState, label = doneLabel } ->
            match freshLocal(doneState) with
                | FreshLocal { state = slotted, local = cursorSlot } ->
                    match slotted
                    |> emit(StoreLocal(cursorSlot)(resultTemp))
                    |> freshLabel("taken_over_cell") with
                        | FreshLabel { state = loopState, label = loopLabel } ->
                            match loopState
                            |> emit(Label(loopLabel))
                            |> freshTemp with
                                | FreshTemp { state = cellState, temp = cellTemp } ->
                                    match cellState
                                    |> emit(LoadLocal(cellTemp)(cursorSlot))
                                    |> emitSkipUnlessPresent(cellTemp)(doneLabel)
                                    |> freshTemp with
                                        | FreshTemp { state = countedState, temp = countedTemp } ->
                                            match countedState
                                            |> emit(IsReferenceCounted(countedTemp)(cellTemp))
                                            |> freshLabel("taken_over_arena_cell") with
                                                | FreshLabel { state = arenaState, label = arenaLabel } ->
                                                    match arenaState
                                                    |> emit(JumpIfFalse(countedTemp)(arenaLabel))
                                                    |> emit(Jump(doneLabel))
                                                    |> emit(Label(arenaLabel))
                                                    |> freshTemp with
                                                        | FreshTemp { state = headState, temp = headTemp } ->
                                                            match headState
                                                            |> emit(LoadMemOffset(headTemp)(cellTemp)(0))
                                                            |> freshLabel("taken_over_next") with
                                                                | FreshLabel { state = nextState, label = nextLabel } ->
                                                                    match freshTemp(nextState) with
                                                                        | FreshTemp { state = headCountedState, temp = headCountedTemp } ->
                                                                            match headCountedState
                                                                            |> emit(IsReferenceCounted(headCountedTemp)(headTemp))
                                                                            |> freshLabel("taken_over_arena_head") with
                                                                                | FreshLabel { state = headArenaState, label = headArenaLabel } ->
                                                                                    match headArenaState
                                                                                    |> emit(JumpIfFalse(headCountedTemp)(headArenaLabel))
                                                                                    |> emit(Jump(nextLabel))
                                                                                    |> emit(Label(headArenaLabel))
                                                                                    |> freshTemp with
                                                                                        | FreshTemp { state = fieldState, temp = fieldTemp } ->
                                                                                            match fieldState
                                                                                            |> emit(LoadMemOffset(fieldTemp)(headTemp)(0))
                                                                                            |> emitSkipUnlessReferenceCounted(fieldTemp)(nextLabel)
                                                                                            |> emitOwnedValueChildDrop(fieldTemp)(element)
                                                                                            |> emit(Label(nextLabel))
                                                                                            |> freshTemp with
                                                                                                | FreshTemp { state = tailState, temp = tailTemp } ->
                                                                                                    tailState
                                                                                                    |> emit(LoadMemOffset(tailTemp)(cellTemp)(8))
                                                                                                    |> emit(StoreLocal(cursorSlot)(tailTemp))
                                                                                                    |> emit(Jump(loopLabel))
                                                                                                    |> emit(Label(doneLabel))

let recursive freshOwnFieldCaseLabels (constructors: List((Int, Bool, List((Int, SemanticType))))) (state: CoreLoweringState) (cases: List(IrSwitchCase)) =
    match constructors with
        | [] -> (state, reverse(cases))
        | (tag, _tagless, _fields) :: rest ->
            match freshLabel("own_field_ctor")(state) with
                | FreshLabel { state = labelled, label = caseLabel } -> freshOwnFieldCaseLabels(rest)(labelled)(IrSwitchCase(tag = tag, label = caseLabel) :: cases)

let recursive emitOwnFieldReleases (resultTemp: Int) (tagless: Bool) (fields: List((Int, SemanticType))) (state: CoreLoweringState) =
    match fields with
        | [] -> state
        | (index, fieldType) :: rest ->
            match freshLabel("own_field_next")(state) with
                | FreshLabel { state = labelled, label = nextLabel } ->
                    match freshTemp(labelled) with
                        | FreshTemp { state = allocated, temp = childTemp } ->
                            allocated
                            |> emit(GetAdtField(childTemp)(resultTemp)(index)(tagless))
                            |> emitSkipUnlessReferenceCounted(childTemp)(nextLabel)
                            |> emitOwnedValueChildDrop(childTemp)(fieldType)
                            |> emit(Label(nextLabel))
                            |> emitOwnFieldReleases(resultTemp)(tagless)(rest)

let recursive emitOwnFieldCases (resultTemp: Int) (doneLabel: Str) (constructors: List((Int, Bool, List((Int, SemanticType))))) (cases: List(IrSwitchCase)) (state: CoreLoweringState) =
    match (constructors, cases) with
        | ((_tag, tagless, fields) :: rest, IrSwitchCase { label = caseLabel } :: restCases) ->
            state
            |> emit(Label(caseLabel))
            |> emitOwnFieldReleases(resultTemp)(tagless)(fields)
            |> emit(Jump(doneLabel))
            |> emitOwnFieldCases(resultTemp)(doneLabel)(rest)(restCases)
        | _ -> state

// Stage 0's `EmitOwnFieldWalk`: releases the reference each reference-counted direct field of an arena
// result cell holds; a cell on the reference-counted heap owns its fields already.
let emitOwnFieldWalk (resultTemp: Int) (taglessType: Bool) (constructors: List((Int, Bool, List((Int, SemanticType))))) (state: CoreLoweringState) =
    match freshLabel("own_field_done")(state) with
        | FreshLabel { state = doneState, label = doneLabel } ->
            match doneState
            |> emitSkipUnlessPresent(resultTemp)(doneLabel)
            |> freshTemp with
                | FreshTemp { state = countedState, temp = countedTemp } ->
                    match countedState
                    |> emit(IsReferenceCounted(countedTemp)(resultTemp))
                    |> freshLabel("own_field_arena_cell") with
                        | FreshLabel { state = arenaState, label = arenaLabel } ->
                            match arenaState
                            |> emit(JumpIfFalse(countedTemp)(arenaLabel))
                            |> emit(Jump(doneLabel))
                            |> emit(Label(arenaLabel))
                            |> freshTemp with
                                | FreshTemp { state = tagState, temp = tagTemp } ->
                                    let tagged =
                                        if taglessType
                                        then
                                            match constructors with
                                                | (tag, _tagless, _fields) :: _rest ->
                                                    emit(LoadConstInt(tagTemp)(tag))(tagState)
                                                | [] ->
                                                    emit(LoadConstInt(tagTemp)(0))(tagState)
                                        else
                                            emit(GetAdtTag(tagTemp)(resultTemp))(tagState)
                                    in
                                        match freshOwnFieldCaseLabels(constructors)(tagged)([]) with
                                            | (labelled, cases) ->
                                                labelled
                                                |> emit(SwitchTag(tagTemp)(cases)(doneLabel))
                                                |> emitOwnFieldCases(resultTemp)(doneLabel)(constructors)(cases)
                                                |> emit(Label(doneLabel))

let emitSettleWalk (temp: Int) (kind: CoreSettleKind) (state: CoreLoweringState) =
    match kind with
        | ListRecordKind(element) -> emitTakenOverWalk(temp)(element)(state)
        | OwnFieldKind(taglessType, constructors) -> emitOwnFieldWalk(temp)(taglessType)(constructors)(state)

// Stage 0's settlement round: each placeholder kept through the first round becomes the walk
// over its call's result, or nothing.
let recursive spliceTakenOverSettles (instructions: List(IrInstruction)) (state: CoreLoweringState) =
    match instructions with
        | [] -> state
        | (IrInstruction { instruction = TcoResetPending(resetId, _usedTemps, _readLocals) } as instruction) :: rest ->
            if resetId >= takenOverSettleBase
            then
                match state
                |> statePendingTakenOverSettles
                |> lookupTakenOverSettle(resetId - takenOverSettleBase) with
                    | Some(settle) ->
                        state
                        |> emitSettleWalk(settle.settleTemp)(settle.settleKind)
                        |> spliceTakenOverSettles(rest)
                    | None -> spliceTakenOverSettles(rest)(state)
            else spliceTakenOverSettles(rest)((state with reversedInstructions = instruction :: state.reversedInstructions))
        | instruction :: rest -> spliceTakenOverSettles(rest)((state with reversedInstructions = instruction :: state.reversedInstructions))

let resolveTakenOverSettles (state: CoreLoweringState) =
    match statePendingTakenOverSettles(state) with
        | [] -> state
        | _settles ->
            state.reversedInstructions
            |> reverse
            |> (given (instructions: List(IrInstruction)) -> spliceTakenOverSettles(instructions)((state with reversedInstructions = [])))
            |> withStatePendingTakenOverSettles([])

// Stage 0's `ResolveDeferredTcoResets` for one function, once its whole body is lowered: every
// `TcoResetPending` placeholder becomes its arena block and every deferred call-result copy-out
// its block, whose slots and temps are allocated after the body's own and carry no location.
// Stage 0 resolves the blocks once the whole program is lowered, so their labels are numbered
// after every later function's; the blocks take their labels from a separate high range here,
// recorded as one group per function, and `numberDeferredLabels` gives them stage 0's ids at
// the end.
let resolvePendingTcoResets (state: CoreLoweringState) =
    match (statePendingTcoResets(state), statePendingCallCopyOuts(state), statePendingTakenOverSettles(state)) with
        | ([], [], []) -> state
        | (resets, copyOuts, _settles) ->
            state.reversedInstructions
            |> reverse
            |> (given (instructions: List(IrInstruction)) ->
                (state with reversedInstructions = [], nextLabelId = stateDeferredLabelNext(state))
                |> withStatePendingTcoResets([])
                |> withStatePendingCallCopyOuts([])
                |> splicePendingBlocks(instructions)(resets)(copyOuts)([])
                |> resolveTakenOverSettles)
            |> (given (resolved: CoreLoweringState) ->
                (resolved with nextLabelId = state.nextLabelId)
                |> withStateDeferredLabelNext(resolved.nextLabelId)
                |> withStateDeferredLabelGroups((stateDeferredLabelNext(state), resolved.nextLabelId - stateDeferredLabelNext(state)) :: stateDeferredLabelGroups(resolved)))

// The program entry's own pending blocks, resolved as a lifted function's are.
let resolveLoweredPendingBlocks (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { error = Some(_error) } -> lowered
        | LoweredCoreValue { state = state } -> lowered with state = resolvePendingTcoResets(state)

let arenaDeepCopySupported (facts: HeapLayoutFacts) =
    match facts with
        | HeapLayoutFacts { arenaDeepCopySupported = supported } -> supported

// Stage 0's `CanNormalizeRuntimeManagedResultIntoArena`: the result types the arena deep copy
// clones completely, strings and bytes, tuples and lists over such values or copy types, and
// the ADTs with an arena deep copy.
let recursive canNormalizeResultIntoArena (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemString -> true
        | SemBytes -> true
        | SemTuple(elements) -> allElementsNormalizeIntoArena(elements)(state)
        | SemList(element) ->
            match resolveType(state)(element) with
                | SemString -> true
                | SemList(inner) -> resultSurvivesReset(inner)(state)
                | resolvedElement ->
                    resultSurvivesReset(resolvedElement)(state) || arenaDeepCopySupported(heapFactsOf(resolvedElement)(state))
        | SemNamed(_symbolId, name, _arguments) as named ->
            !isResourceTypeName(name) && arenaDeepCopySupported(heapFactsOf(named)(state))
        | _ -> false
and allElementsNormalizeIntoArena (elements: List(SemanticType)) (state: CoreLoweringState) =
    match elements with
        | [] -> true
        | element :: rest -> (resultSurvivesReset(element)(state) || canNormalizeResultIntoArena(element)(state)) && allElementsNormalizeIntoArena(rest)(state)

// The values the arena clone produces, recorded as placements the way stage 0's per-instruction
// temp facts record every allocation and copy-out it emits: the clone's cells, its leaf copies,
// and the copier closure and call of a named type.
let recursive recordArenaCopyPlacements (instructions: List(IrInstructionKind)) (semanticType: SemanticType) (state: CoreLoweringState) =
    match instructions with
        | [] -> state
        | instruction :: rest ->
            match instruction with
                | Alloc(target, _sizeBytes, _runtimeManaged) ->
                    state
                    |> recordValuePlacement(target)(semanticType)
                    |> recordArenaCopyPlacements(rest)(semanticType)
                | CopyOutArena(destTemp, _source, _size, _runtimeManaged, _purpose, _location) ->
                    state
                    |> recordValuePlacement(destTemp)(semanticType)
                    |> recordArenaCopyPlacements(rest)(semanticType)
                | CopyOutList(destTemp, _source, _headCopy, _runtimeManaged, _purpose) ->
                    state
                    |> recordValuePlacement(destTemp)(semanticType)
                    |> recordArenaCopyPlacements(rest)(semanticType)
                | MakeClosure(target, _label, _environment, _size, _runtimeManaged, _returnsRuntimeManaged, _acceptsRuntimeManaged, _returnsOwned) ->
                    state
                    |> recordValuePlacement(target)(semanticType)
                    |> recordArenaCopyPlacements(rest)(semanticType)
                | CallClosure(target, _closure, _argument, _flag) ->
                    state
                    |> recordValuePlacement(target)(semanticType)
                    |> recordArenaCopyPlacements(rest)(semanticType)
                | _ -> recordArenaCopyPlacements(rest)(semanticType)(state)

// The arena clone of a reference-counted result on its arena-result boundary, spliced in place.
let emitArenaResultDeepCopy (sourceTemp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match state with
        | CoreLoweringState { nextTemp = nextTemp, nextLocal = nextLocal, nextLambdaId = lambdaId, nextLabelId = labelId, programState = CoreProgramState { dropperLabels = cache } } ->
            match synthesizeDeepCopy(sourceTemp)(resolveType(state)(semanticType))(dropperTypesOf(state))(cache)(nextTemp)(nextLocal)(lambdaId)(labelId)(ArenaResultBoundary) with
                | (synthesis, resultTemp) ->
                    (state
                    |> spliceInlineReleaseWith(emit)(synthesis)
                    |> recordArenaCopyPlacements(synthesis.instructions)(semanticType), resultTemp)

// The type name of a single-allocation drop, stage 0's `TcoRuntimeManagedTypeName`.
let arenaResultDropTypeName (semanticType: SemanticType) =
    match semanticType with
        | SemBigInt -> "BigInt"
        | SemTuple(_elements) -> "Tuple"
        | SemNamed(_symbolId, name, _arguments) -> runtimeManagedAdtTypeName(name)
        | SemFunction(_argument, _result, _row) -> "Function"
        | _ -> "String"

// Stage 0's `EmitRuntimeManagedResultDrop`: the structural release of the reference-counted
// original once its arena clone stands in for it, or a single typed drop when the value is one
// allocation.
let emitArenaResultRelease (valueTemp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match state with
        | CoreLoweringState { nextTemp = nextTemp, nextLocal = nextLocal, nextLambdaId = lambdaId, nextLabelId = labelId, programState = CoreProgramState { dropperLabels = cache } } ->
            match synthesizeOwnedAggregateReleaseIndexed(valueTemp)(resolveType(state)(semanticType))(OwnedReleasePlan(deepUnique = false, constructorName = None, matchFieldTransfer = None))(dropperTypesOf(state))(cache)(nextTemp)(nextLocal)(lambdaId)(labelId) with
                | (Some(synthesis), _queryContext) -> spliceInlineReleaseWith(emit)(synthesis)(state)
                | (None, queryContext) ->
                    // A type only the normalization helper expresses is released through its own
                    // dropper even when no constructor owns a heap field, as stage 0's result
                    // drop routes it; any other single allocation is one typed drop.
                    let queried = withStateDropperContext(queryContext)(state)
                    in
                        match generalCopyPlanOf(semanticType)(queried) with
                            | Some(NormalizerArgumentCopy(_named)) ->
                                spliceInlineReleaseWith(emit)(synthesizeChildDrop(valueTemp)(resolveType(queried)(semanticType))(dropperTypesOf(queried))(cache)(nextTemp)(nextLocal)(lambdaId)(labelId))(queried)
                            | _ ->
                                emit(RcDrop(valueTemp)(semanticType
                                |> resolveType(queried)
                                |> arenaResultDropTypeName)(-1)(true)(false)(None))(queried)

let recursive anyOwnedSlotOf (origin: Maybe(IrFunctionOrigin)) (slots: List((Maybe(IrFunctionOrigin), Int, SemanticType))) =
    match slots with
        | [] -> false
        | (owner, _slot, _semanticType) :: rest -> owner == origin || anyOwnedSlotOf(origin)(rest)

let recursive isOwnedSlotOf (origin: Maybe(IrFunctionOrigin)) (slot: Int) (slots: List((Maybe(IrFunctionOrigin), Int, SemanticType))) =
    match slots with
        | [] -> false
        | (owner, candidate, _semanticType) :: rest -> owner == origin && candidate == slot || isOwnedSlotOf(origin)(slot)(rest)

let functionOwnsGeneralRcSlots (state: CoreLoweringState) =
    state
    |> stateGeneralRcOwnedSlots
    |> anyOwnedSlotOf(stateActiveFunctionOrigin(state))

// Stage 0's `IsScalarFieldVariant`: a variant every field of which is inline (an optional integer).
// The contract does not govern it, since it owns no heap child, and a generic one has no fixed
// copy-out either, so the normalization helper is what detaches it from a value it may have been
// read out of.
let isScalarFieldVariant (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemNamed(_symbolId, _name, _arguments) as named ->
            isGeneralRcAdmissible(named)([])(state) && !anyChildSurvivesNoReset(heapChildrenOfNamed(named)(state))(state)
        | _ -> false

// Stage 0's `IsScalarFieldVariantList`: a list of such variants (optional integers), copied cell by
// cell with each element, holds nothing of a value it may have been read out of.
let isScalarFieldVariantList (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemList(element) -> isScalarFieldVariant(element)(state)
        | _ -> false

// How a result outside the contract's types is detached from the values its function owns: by
// its fixed copy-out in the arena, where the caller takes it like any fresh result, by an arena
// deep copy, or by a normalization into an owned reference-counted value.
type IndependentResultCopy =
    | ArenaResultCopy(CallCopyOut)
    | OwnedResultCopy(ArgumentCopyPlan)
    | ArenaDeepResultCopy(SemanticType)

// Stage 0's `HasFixedArenaCopyOut` and `CanNormalizeIntoOwnedRuntimeValue` behind
// `normalizesOwnedResult`, the fixed copy-out first.
let independentResultCopyOf (bodyType: SemanticType) (state: CoreLoweringState) =
    match callCopyOutOf(bodyType)(state) with
        | Some(copyOut) -> Some(ArenaResultCopy(copyOut))
        | None ->
            if isScalarFieldVariantList(bodyType)(state)
            then Some(ArenaDeepResultCopy(bodyType))
            else
                match ownedResultPlanOf(bodyType)(state) with
                    | Some(ScalarArgumentCopy) -> None
                    | Some(plan) -> Some(OwnedResultCopy(plan))
                    | None ->
                        if isScalarFieldVariant(bodyType)(state)
                        then
                            match generalCopyPlanOf(bodyType)(state) with
                                | Some(plan) -> Some(OwnedResultCopy(plan))
                                | None -> None
                        else None

let emitIndependentResultCopy (copy: IndependentResultCopy) (sourceTemp: Int) (state: CoreLoweringState) =
    match copy with
        | OwnedResultCopy(plan) ->
            match emitGuardedDeepCopy(sourceTemp)(plan)(state) with
                | (copied, resultTemp) -> (markRuntimeTemp(resultTemp)(RuntimeNewlyProduced)(copied), resultTemp)
        | ArenaDeepResultCopy(semanticType) -> emitLocatedDeepCopy(sourceTemp)(semanticType)(state)
        | ArenaResultCopy(copyOut) ->
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = copyTemp } ->
                    (emit(match copyOut with
                        | ShallowCallCopyOut(sizeBytes) -> CopyOutArena(copyTemp)(sourceTemp)(sizeBytes)(false)(IndependentClone)(None)
                        | ListCallCopyOut(headCopy) -> CopyOutList(copyTemp)(sourceTemp)(headCopy)(false)(IndependentClone))(allocated), copyTemp)

// Stage 0's `FinishGeneralRcFunctionResult`: a result of the contract's types is made the
// function's own (a value the body produced owned is taken as it is when its cells turn out
// reference-counted and copied when they are still in the arena; any other value is retained or
// copied), and the function is recorded as handing its result over owned. The owned slots are
// released behind a result they cannot reach: one made independent here, an inline value, or one
// already reference-counted. Any other result may still point into them, and they are left.
// A type variable of a function's result that its scheme quantifies and none of its parameter
// types name is a phantom: no value of its type can reach the function, so the `a` of a result's
// `Maybe(a)` only ever reads `None`. The result's layout is decided with the variable read as
// `Never`, at the function's return and at every call of it alike, so a result of the contract's
// types but for the phantom is owned like any other instead of staying in the arena beside the
// owned values it holds. A variable a pending trait goal names is not a phantom (a method of the
// trait may produce a value of its type), and the reading is taken only where it makes the result
// one of the contract's types (stage 0's `ResolvePhantomResultLayout`).
let recursive substitutePhantomVariables (phantoms: List(Int)) (semanticType: SemanticType) =
    match semanticType with
        | SemVariable(id) ->
            if containsInt(id)(phantoms)
            then SemNever
            else semanticType
        | SemList(element) ->
            element
            |> substitutePhantomVariables(phantoms)
            |> SemList
        | SemTuple(elements) ->
            elements
            |> substitutePhantomVariablesIn(phantoms)
            |> SemTuple
        | SemNamed(symbolId, name, arguments) ->
            arguments
            |> substitutePhantomVariablesIn(phantoms)
            |> SemNamed(symbolId)(name)
        | other -> other
and substitutePhantomVariablesIn (phantoms: List(Int)) (types: List(SemanticType)) =
    match types with
        | [] -> []
        | head :: rest -> substitutePhantomVariables(phantoms)(head) :: substitutePhantomVariablesIn(phantoms)(rest)

let recursive functionResultOf (semanticType: SemanticType) =
    match semanticType with
        | SemFunction(_argument, result, _row) -> functionResultOf(result)
        | other -> other

let returnedStageOf (label: Str) (pairs: MapTree(Str, Str)) = Ashes.Collection.Map.getStr(label)(pairs)

// Stage 0's `CurryChainReaches`: whether the function `label` is `current` or one of the curry
// stages it returns.
let recursive curryChainReaches (depth: Int) (current: Str) (label: Str) (pairs: MapTree(Str, Str)) =
    if current == label
    then true
    else
        if depth == 0
        then false
        else
            match returnedStageOf(current)(pairs) with
                | Some(next) -> curryChainReaches(depth - 1)(next)(label)(pairs)
                | None -> false

// The names the function `label` (or a curry stage returning it) is bound to as itself.
let recursive selfNamesReaching (label: Str) (bindings: List(CoreBinding)) (state: CoreLoweringState) =
    match bindings with
        | [] -> []
        | CoreBinding { name = name, location = CoreSelf(selfLabel, _size) } :: rest ->
            if state
            |> stateFunctionReturnedClosureLabels
            |> curryChainReaches(64)(selfLabel)(label)
            then name :: selfNamesReaching(label)(rest)(state)
            else selfNamesReaching(label)(rest)(state)
        | _ :: rest -> selfNamesReaching(label)(rest)(state)

// A recursive self reference names its own result variables. Only independent bindings
// can establish that a value containing those variables already exists.
let isPhantomQuerySelf label selfNames binding state =
    match binding with
        | CoreBinding { location = CoreSelf(selfLabel, _size) } ->
            state
            |> stateFunctionReturnedClosureLabels
            |> curryChainReaches(64)(selfLabel)(label)
        | CoreBinding { name = name, location = CoreEnvironment(_index) } -> containsLabel(name)(selfNames)
        | _ -> false

let recursive candidatesBesideScheme candidates scheme =
    match candidates with
        | [] -> []
        | id :: [] ->
            if schemeHoldsVariable(id)(scheme)
            then []
            else candidates
        | id :: rest ->
            let remaining = candidatesBesideScheme(rest)(scheme)
            in
                if schemeHoldsVariable(id)(scheme)
                then remaining
                else id :: remaining

let recursive candidatesBesideBindings label selfNames candidates bindings (state: CoreLoweringState) =
    match candidates with
        | [] -> []
        | _ ->
            match bindings with
                | [] -> candidates
                | (CoreBinding { closedScheme = closed, scheme = scheme } as binding) :: rest ->
                    if closed || isPhantomQuerySelf(label)(selfNames)(binding)(state)
                    then candidatesBesideBindings(label)(selfNames)(candidates)(rest)(state)
                    else
                        let remaining =
                            scheme
                            |> applySchemeSubstitutionMap(state.substitution)
                            |> candidatesBesideScheme(candidates)
                        in candidatesBesideBindings(label)(selfNames)(remaining)(rest)(state)

let contractLayoutOrSelf (substituted: SemanticType) (original: SemanticType) (state: CoreLoweringState) =
    if isGeneralRcValueType(substituted)(state)
    then substituted
    else original

// The variables of `variables` that `held` does not name.
let recursive variablesNotHeld (held: List(Int)) (variables: List(Int)) =
    match variables with
        | [] -> []
        | id :: rest ->
            if containsInt(id)(held)
            then variablesNotHeld(held)(rest)
            else id :: variablesNotHeld(held)(rest)

let recursive joinVariables (left: List(Int)) (right: List(Int)) =
    match left with
        | [] -> right
        | id :: rest ->
            if containsInt(id)(right)
            then joinVariables(rest)(right)
            else joinVariables(rest)(id :: right)

// The variables of a function's resolved result that no binding beside the function's own self
// reference holds and no pending trait goal names: its phantoms.
let phantomResultVariables (label: Str) (resolved: SemanticType) (state: CoreLoweringState) =
    match freeTypeVariables(resolved) with
        | [] -> []
        | candidates ->
            match state
            |> pendingOperatorScheme
            |> candidatesBesideScheme(candidates) with
                | [] -> []
                | pending ->
                    let outer = stateEnclosingBindings(state)
                    in
                        let selfNames =
                            state
                            |> selfNamesReaching(label)(outer)
                            |> append(selfNamesReaching(label)(state.bindings)(state))
                        in
                            let remaining = candidatesBesideBindings(label)(selfNames)(pending)(state.bindings)(state)
                            in candidatesBesideBindings(label)(selfNames)(remaining)(outer)(state)

// Only a function a `let` binds is generalized once lowered; any other (a lambda handed to a call, a
// constructor taken as a function) is typed by the context it meets afterwards.
let resolvePhantomResultLayout (label: Str) (semanticType: SemanticType) (state: CoreLoweringState) =
    (let resolved = resolveType(state)(semanticType)
    in
        match resolved with
            | SemFunction(_argument, _result, _row) -> semanticType
            | _ ->
                if !containsUnresolvedLayout(resolved)(state) || !containsLabel(label)(stateLetBoundFunctionLabels(state))
                then semanticType
                else
                    match phantomResultVariables(label)(resolved)(state) with
                        | [] -> semanticType
                        | phantoms ->
                            contractLayoutOrSelf(substitutePhantomVariables(phantoms)(resolved))(semanticType)(state))

// Stage 0's `ResolvePhantomNormalizedResultLayout`: a result outside the contract's types whose
// phantoms, read as `Never`, leave a type with an independent copy (a `Result(e, List(Int))` only
// `Ok` builds) is detached as that type, so the owned values its cells were read out of are
// released behind it. The reading is taken at the return and wherever the return decides whether
// the function's result is reference-counted, which its callers then take as such.
let resolvePhantomNormalizedResultLayout (label: Str) (semanticType: SemanticType) (state: CoreLoweringState) =
    (let resolved = resolveType(state)(semanticType)
    in
        match resolved with
            | SemFunction(_argument, _result, _row) -> semanticType
            | _ ->
                if !containsUnresolvedLayout(resolved)(state) || !containsLabel(label)(stateLetBoundFunctionLabels(state))
                then semanticType
                else
                    match phantomResultVariables(label)(resolved)(state) with
                        | [] -> semanticType
                        | phantoms ->
                            let substituted = substitutePhantomVariables(phantoms)(resolved)
                            in
                                if containsUnresolvedLayout(substituted)(state) || resultSurvivesReset(substituted)(state)
                                then semanticType
                                else
                                    match independentResultCopyOf(substituted)(state) with
                                        | Some(_copy) -> substituted
                                        | None -> semanticType)

// Stage 0's `IsRuntimeOwnedCopyTupleLayout`: a tuple every element of which a runtime-managed
// parent can own and the runtime-managed deep copy reproduces without reaching a recursive type.
let recursive allRuntimeOwnedCopyElements (elements: List(SemanticType)) (state: CoreLoweringState) =
    match elements with
        | [] -> true
        | element :: rest ->
            heapRuntimeOwnedTupleElementLayout(element)(coverageEnvironment(state))([]) && allRuntimeOwnedCopyElements(rest)(state)

let recursive anyOwnedSlotIs (origin: Maybe(IrFunctionOrigin)) (slot: Int) (slots: List((Maybe(IrFunctionOrigin), Int, SemanticType))) =
    match slots with
        | [] -> false
        | (owner, candidate, _semanticType) :: rest -> owner == origin && candidate == slot || anyOwnedSlotIs(origin)(slot)(rest)

let recursive independentOwnedSlotsOf (origin: Maybe(IrFunctionOrigin)) (slots: List((Maybe(IrFunctionOrigin), Int, SemanticType))) (predecessors: List((Maybe(IrFunctionOrigin), Int, SemanticType))) (state: CoreLoweringState) =
    match slots with
        | [] -> []
        | (owner, slot, semanticType) :: rest ->
            if owner == origin && !anyOwnedSlotIs(origin)(slot)(predecessors) && isGeneralRcValueType(semanticType)(state)
            then slot :: independentOwnedSlotsOf(origin)(rest)(predecessors)(state)
            else independentOwnedSlotsOf(origin)(rest)(predecessors)(state)

// Stage 0's `IndependentOwnedSlots`: the owned slots of the function being lowered that hold a
// value of the contract's types other than a predecessor, a value with references of its own, so
// what reaches a result through one of them ends there and the arguments its call borrowed are not
// reached through it.
let independentOwnedSlots (state: CoreLoweringState) =
    independentOwnedSlotsOf(stateActiveFunctionOrigin(state))(stateGeneralRcOwnedSlots(state))(stateGeneralRcPredecessorSlots(state))(state)

let recursive ownedSlotRegistered (origin: Maybe(IrFunctionOrigin)) (slot: Int) (slots: List((Maybe(IrFunctionOrigin), Int, SemanticType))) =
    match slots with
        | [] -> false
        | (owner, candidate, _semanticType) :: rest -> owner == origin && candidate == slot || ownedSlotRegistered(origin)(slot)(rest)

let recursive aliasOwnerSlotPairsOf (origin: Maybe(IrFunctionOrigin)) (registered: List((Maybe(IrFunctionOrigin), Int, SemanticType))) (entries: List((Maybe(IrFunctionOrigin), Int, Int))) =
    match entries with
        | [] -> []
        | (owner, slot, aliasTemp) :: rest ->
            if owner == origin && ownedSlotRegistered(origin)(slot)(registered)
            then (slot, aliasTemp) :: aliasOwnerSlotPairsOf(origin)(registered)(rest)
            else aliasOwnerSlotPairsOf(origin)(registered)(rest)

// The owned slots of the function being lowered that hold the reference an escaping arena alias
// keeps of an owner it borrows, each with the alias (stage 0's `GeneralRcOwnedSlot.AliasTemp`).
let aliasOwnerSlotPairs (state: CoreLoweringState) =
    state
    |> stateGeneralRcAliasOwnerSlots
    |> aliasOwnerSlotPairsOf(stateActiveFunctionOrigin(state))(stateGeneralRcOwnedSlots(state))

// The local slots a function's result can have been read from, through no independent owned slot,
// and the slots keeping alive the owners of the arena aliases it reaches.
let resultReachedSlots (resultTemp: Int) (state: CoreLoweringState) =
    localSlotsReachingTempsWithAliases([resultTemp])(independentOwnedSlots(state))([])(aliasOwnerSlotPairs(state))(state.reversedInstructions)

// Whether the result reaches any owned slot of the function being finished.
let resultReachesOwnedSlots (resultTemp: Int) (resultType: SemanticType) (state: CoreLoweringState) =
    (let slots = stateGeneralRcOwnedSlots(state)
    in
        let outside =
            state
            |> ownedSlotsOutsideResult(resultType)(resultReachedSlots(resultTemp)(state))(slots)
            |> ownedSlotsOf(stateActiveFunctionOrigin(state))
        in
            length(outside([])) < length(ownedSlotsOf(stateActiveFunctionOrigin(state))(slots)([])))

// Stage 0's `KeepsUnreachedResultInPlace`: a result the arena has no copy for, that no owned value
// reaches, stays where it is. Normalized onto the reference-counted heap it would be owned by
// callers that cannot know it, a self-call lowered before this return among them, and leak there.
// The owned slots are released behind it all the same, since none of them is in its reach.
let keepsUnreachedResultInPlace (bodyTemp: Int) (bodyType: SemanticType) (state: CoreLoweringState) = !isRuntimeTemp(bodyTemp)(state) && !resultReachesOwnedSlots(bodyTemp)(bodyType)(state)

// Stage 0's `MovesOwnedPartsIntoTupleResult`: a tuple result that holds a part of an owned value
// is placed on the reference-counted heap at the return, each element the owned values hold
// retained into it and the others copied, so the owned slots are released behind it and their
// parts live on in the result. The copy plan, when the tuple qualifies.
let ownedTupleResultPlan (bodyTemp: Int) (bodyType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(bodyType) with
        | SemTuple(elements) as tuple ->
            if !containsUnresolvedLayout(tuple)(state) && allRuntimeOwnedCopyElements(elements)(state) && resultReachesOwnedSlots(bodyTemp)(tuple)(state)
            then argumentCopyPlanOf(tuple)(state)
            else None
        | _ -> None

let recursive definitionOfTemp (temp: Int) (reversedInstructions: List(IrInstruction)) =
    match reversedInstructions with
        | [] -> None
        | IrInstruction { instruction = instruction } :: rest ->
            if instruction
            |> getDefinedTemps
            |> containsInt(temp)
            then Some(instruction)
            else definitionOfTemp(temp)(rest)

let isArenaTupleCell (instruction: Maybe(IrInstructionKind)) =
    match instruction with
        | Some(Alloc(_target, _size, false)) -> true
        | _ -> false

let isBackEdgePlaceholder (instruction: Maybe(IrInstructionKind)) =
    match instruction with
        | Some(LoadConstInt(_target, 0)) -> true
        | _ -> false

// The one arena cell stored into `slot`, the placeholder a back edge writes aside; -1 otherwise.
let recursive soleArenaCellStoredIn (slot: Int) (instructions: List(IrInstruction)) (all: List(IrInstruction)) (cell: Int) =
    match instructions with
        | [] -> cell
        | IrInstruction { instruction = StoreLocal(storeSlot, source) } :: rest ->
            if storeSlot != slot
            then soleArenaCellStoredIn(slot)(rest)(all)(cell)
            else
                match definitionOfTemp(source)(all) with
                    | definition ->
                        if isBackEdgePlaceholder(definition)
                        then soleArenaCellStoredIn(slot)(rest)(all)(cell)
                        else
                            if isArenaTupleCell(definition) && cell < 0
                            then soleArenaCellStoredIn(slot)(rest)(all)(source)
                            else -1
        | _ :: rest -> soleArenaCellStoredIn(slot)(rest)(all)(cell)

// Stage 0's `SoleArenaCellOf`: the arena cell a value is, directly or through the one store into a
// local that is not the placeholder a back edge writes; -1 otherwise.
let soleArenaCellOf (temp: Int) (state: CoreLoweringState) =
    match definitionOfTemp(temp)(state.reversedInstructions) with
        | Some(LoadLocal(_target, slot)) -> soleArenaCellStoredIn(slot)(state.reversedInstructions)(state.reversedInstructions)(-1)
        | definition ->
            if isArenaTupleCell(definition)
            then temp
            else -1

let recursive nthElementType (index: Int) (types: List(SemanticType)) =
    match types with
        | [] -> SemInt
        | head :: rest ->
            if index == 0
            then head
            else nthElementType(index - 1)(rest)

let recursive freshTupleChildOffsets (cell: Int) (elementTypes: List(SemanticType)) (instructions: List(IrInstruction)) (state: CoreLoweringState) =
    match instructions with
        | [] -> []
        | IrInstruction { instruction = StoreMemOffset(basePtr, offset, source) } :: rest ->
            if basePtr == cell && offset >= 0 && offset - offset / 8 * 8 == 0 && offset / 8 < length(elementTypes) && (match runtimeTempStateOf(source)(state) with
                | Some(RuntimeNewlyProduced) -> true
                | _ -> false)
            then (offset, nthElementType(offset / 8)(elementTypes)) :: freshTupleChildOffsets(cell)(elementTypes)(rest)(state)
            else freshTupleChildOffsets(cell)(elementTypes)(rest)(state)
        | _ :: rest -> freshTupleChildOffsets(cell)(elementTypes)(rest)(state)

let recursive releaseTupleChildrenAt (bodyTemp: Int) (children: List((Int, SemanticType))) (state: CoreLoweringState) =
    match children with
        | [] -> state
        | (offset, elementType) :: rest ->
            if resultSurvivesReset(elementType)(state)
            then releaseTupleChildrenAt(bodyTemp)(rest)(state)
            else
                match freshTemp(state) with
                    | FreshTemp { state = allocated, temp = childTemp } ->
                        allocated
                        |> emit(LoadMemOffset(childTemp)(bodyTemp)(offset))
                        |> emitOwnedValueChildDrop(childTemp)(elementType)
                        |> releaseTupleChildrenAt(bodyTemp)(rest)

// Stage 0's `ReleaseFreshChildrenOfCopiedArenaTuple`: a result built as an arena tuple whose
// layout was unresolved when it was built holds the references of the reference-counted values
// freshly produced into it, which the arena cell can never release. Once the tuple is copied onto
// the reference-counted heap with references of its own, those are released: the fresh values
// move into the copy.
let releaseFreshChildrenOfCopiedArenaTuple (bodyTemp: Int) (bodyType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(bodyType) with
        | SemTuple(elementTypes) ->
            let cell = soleArenaCellOf(bodyTemp)(state)
            in
                if cell < 0
                then state
                else
                    releaseTupleChildrenAt(bodyTemp)(freshTupleChildOffsets(cell)(elementTypes)(reverse(state.reversedInstructions))(state))(state)
        | _ -> state

let isTupleResultPartType (semanticType: SemanticType) (state: CoreLoweringState) =
    isGeneralRcValueType(semanticType)(state) || generalRcEnabled(state) && (match resolveType(state)(semanticType) with
        | SemString -> true
        | SemBytes -> true
        | _ -> false)

// Stage 0's `TupleResultParts`: the contract elements of a tuple result type with an element of
// unresolved layout, by index.
let recursive contractElementIndices (elements: List(SemanticType)) (index: Int) (state: CoreLoweringState) =
    match elements with
        | [] -> []
        | element :: rest ->
            if isTupleResultPartType(element)(state)
            then index :: contractElementIndices(rest)(index + 1)(state)
            else contractElementIndices(rest)(index + 1)(state)

// The elements of a tuple type that hold no pointer (an integer, a flag).
let recursive scalarElementIndices (elements: List(SemanticType)) (index: Int) (state: CoreLoweringState) =
    match elements with
        | [] -> []
        | element :: rest ->
            if resultSurvivesReset(element)(state)
            then index :: scalarElementIndices(rest)(index + 1)(state)
            else scalarElementIndices(rest)(index + 1)(state)

let tupleScalarElements (bodyType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(bodyType) with
        | SemTuple(elements) -> scalarElementIndices(elements)(0)(state)
        | _ -> []

let tupleResultParts (bodyType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(bodyType) with
        | SemTuple(elements) ->
            if containsUnresolvedLayout(SemTuple(elements))(state)
            then contractElementIndices(elements)(0)(state)
            else []
        | _ -> []

// Stage 0's `EmitRetainIfReferenceCounted`.
let emitRetainIfReferenceCounted (valueTemp: Int) (state: CoreLoweringState) =
    match freshLabel("tuple_part_not_counted")(state) with
        | FreshLabel { state = labelled, label = skipLabel } ->
            match labelled
            |> emitSkipUnlessReferenceCounted(valueTemp)(skipLabel)
            |> freshTemp with
                | FreshTemp { state = allocated, temp = retainTemp } ->
                    allocated
                    |> emit(RcDup(retainTemp)(valueTemp)(true)(false))
                    |> emit(Label(skipLabel))

// Stage 0's `EmitJumpUnlessTuplePartsRequested`: jumps to `keepLabel` unless the caller set bit 2
// of the hidden ownership word, asking for the result's contract parts.
let emitJumpUnlessTuplePartsRequested (keepLabel: Str) (state: CoreLoweringState) =
    match freshTempRun(5)(state) with
        | FreshTemp { state = allocated, temp = wordTemp } ->
            allocated
            |> emit(LoadArgumentOwnership(wordTemp))
            |> emit(LoadConstInt(wordTemp + 1)(2))
            |> emit(ShrInt(wordTemp + 2)(wordTemp)(wordTemp + 1))
            |> emit(LoadConstInt(wordTemp + 3)(1))
            |> emit(AndInt(wordTemp + 4)(wordTemp + 2)(wordTemp + 3))
            |> emit(JumpIfFalse(wordTemp + 4)(keepLabel))

// The same, falling through only when the result is an arena cell as well.
let emitJumpUnlessTuplePartsRequestedOf (resultTemp: Int) (keepLabel: Str) (state: CoreLoweringState) =
    match state
    |> emitJumpUnlessTuplePartsRequested(keepLabel)
    |> freshLabel("tuple_parts_arena_result") with
        | FreshLabel { state = labelled, label = arenaLabel } ->
            match freshTemp(labelled) with
                | FreshTemp { state = allocated, temp = countedTemp } ->
                    allocated
                    |> emit(IsReferenceCounted(countedTemp)(resultTemp))
                    |> emit(JumpIfFalse(countedTemp)(arenaLabel))
                    |> emit(Jump(keepLabel))
                    |> emit(Label(arenaLabel))

let recursive storedSourcesOf (slot: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> []
        | IrInstruction { instruction = StoreLocal(storeSlot, source) } :: rest ->
            if storeSlot == slot
            then source :: storedSourcesOf(slot)(rest)
            else storedSourcesOf(slot)(rest)
        | _ :: rest -> storedSourcesOf(slot)(rest)

// Stage 0's `ArenaCellsOfResult`: the arena cells a result can be, the cell itself or every cell
// stored into the locals it is loaded from (the placeholder a back edge writes aside); None when
// anything else can reach it. `forward` is the function's instructions in order.
let recursive arenaCellsReaching (pending: List(Int)) (visited: List(Int)) (cells: List(Int)) (forward: List(IrInstruction)) (state: CoreLoweringState) =
    match pending with
        | [] -> Some(cells)
        | temp :: rest ->
            if containsInt(temp)(visited)
            then arenaCellsReaching(rest)(visited)(cells)(forward)(state)
            else
                match definitionOfTemp(temp)(state.reversedInstructions) with
                    | Some(Alloc(_target, _size, false)) -> arenaCellsReaching(rest)(temp :: visited)(temp :: cells)(forward)(state)
                    | Some(LoadConstInt(_target, 0)) -> arenaCellsReaching(rest)(temp :: visited)(cells)(forward)(state)
                    | Some(LoadLocal(_target, slot)) ->
                        arenaCellsReaching(append(storedSourcesOf(slot)(forward))(rest))(temp :: visited)(cells)(forward)(state)
                    | _ -> None

// Stage 0's `IsReadOutOfOwnedValue`: whether every value pending is an owned value, a borrowed
// one, or a part read out of either through locals, fields and borrows.
let recursive readOutOfOwnedValue (pending: List(Int)) (ownedSlots: List(Int)) (visited: List(Int)) (forward: List(IrInstruction)) (state: CoreLoweringState) =
    match pending with
        | [] -> true
        | temp :: rest ->
            if containsInt(temp)(visited)
            then readOutOfOwnedValue(rest)(ownedSlots)(visited)(forward)(state)
            else
                match definitionOfTemp(temp)(state.reversedInstructions) with
                    | Some(LoadEnv(_target, _index)) -> readOutOfOwnedValue(rest)(ownedSlots)(temp :: visited)(forward)(state)
                    | Some(LoadMemOffset(_target, basePtr, _offset)) -> readOutOfOwnedValue(basePtr :: rest)(ownedSlots)(temp :: visited)(forward)(state)
                    | Some(GetAdtField(_target, pointer, _field, _tagless)) -> readOutOfOwnedValue(pointer :: rest)(ownedSlots)(temp :: visited)(forward)(state)
                    | Some(Borrow(_target, source)) -> readOutOfOwnedValue(source :: rest)(ownedSlots)(temp :: visited)(forward)(state)
                    | Some(LoadLocal(_target, slot)) ->
                        if containsInt(slot)(ownedSlots)
                        then readOutOfOwnedValue(rest)(ownedSlots)(temp :: visited)(forward)(state)
                        else
                            readOutOfOwnedValue(append(storedSourcesOf(slot)(forward))(rest))(ownedSlots)(temp :: visited)(forward)(state)
                    | _ -> false

let recursive ownedSlotNumbers (slots: List((Int, SemanticType))) =
    match slots with
        | [] -> []
        | (slot, _semanticType) :: rest -> slot :: ownedSlotNumbers(rest)

let isFreshRuntimeTemp (temp: Int) (state: CoreLoweringState) =
    match runtimeTempStateOf(temp)(state) with
        | Some(RuntimeNewlyProduced) -> true
        | _ -> false

let isTuplePartOffset (offset: Int) (parts: List(Int)) = offset - offset / 8 * 8 == 0 && containsInt(offset / 8)(parts)

// The requested path of exactly one proved string demotion reads the original borrowed value.
// This is not a general ownership identity for CopyOutArena.
let recursive borrowedTuplePartSource (source: Int) (borrowed: List((Int, Int))) =
    match borrowed with
        | [] -> source
        | (target, original) :: rest ->
            if target == source
            then original
            else borrowedTuplePartSource(source)(rest)

let recursive tuplePartStoreCount (cell: Int) (offset: Int) (instructions: List(IrInstruction)) (count: Int) =
    match instructions with
        | [] -> count
        | IrInstruction { instruction = StoreMemOffset(pointer, at, _source) } :: rest ->
            tuplePartStoreCount(cell)(offset)(rest)(if pointer == cell && at == offset
            then count + 1
            else count)
        | _ :: rest -> tuplePartStoreCount(cell)(offset)(rest)(count)

// Stage 0's TryFindBorrowedStringDemotion: match the precise seven instructions emitted by
// emitArenaCopyOfReferenceCounted with no fresh-source release, most recent first.
let recursive borrowedStringDemotion (resultTemp: Int) (instructions: List(IrInstruction)) (ownedSlots: List(Int)) (forward: List(IrInstruction)) (state: CoreLoweringState) =
    match instructions with
        | IrInstruction { instruction = LoadLocal(target, slot) } :: IrInstruction { instruction = Label(doneLabel) } :: IrInstruction { instruction = StoreLocal(copiedSlot, copyTemp) } :: IrInstruction { instruction = CopyOutArena(copyTarget, source, -1, false, IndependentClone, None) } :: IrInstruction { instruction = JumpIfFalse(condition, skipLabel) } :: IrInstruction { instruction = IsReferenceCounted(counted, countedSource) } :: IrInstruction { instruction = StoreLocal(initialSlot, initialSource) } :: _rest when target == resultTemp ->
            if slot == copiedSlot && slot == initialSlot && copyTemp == copyTarget && source == countedSource && source == initialSource && condition == counted && doneLabel == skipLabel && !isFreshRuntimeTemp(source)(state) && readOutOfOwnedValue([source])(ownedSlots)([])(forward)(state) && countTempUses(resultTemp)(state.reversedInstructions)(0) == 1
            then Some((counted, source, doneLabel))
            else None
        | _instruction :: rest -> borrowedStringDemotion(resultTemp)(rest)(ownedSlots)(forward)(state)
        | [] -> None

let recursive stringPartStoreInterval (resultTemp: Int) (cell: Int) (offset: Int) (instructions: List(IrInstruction)) (inside: Bool) (collected: List(IrInstruction)) =
    match instructions with
        | [] -> None
        | (IrInstruction { instruction = LoadLocal(target, _slot) } as instruction) :: rest ->
            if target == resultTemp
            then stringPartStoreInterval(resultTemp)(cell)(offset)(rest)(true)([])
            else
                stringPartStoreInterval(resultTemp)(cell)(offset)(rest)(inside)(if inside
                then instruction :: collected
                else collected)
        | (IrInstruction { instruction = StoreMemOffset(pointer, at, source) } as instruction) :: rest ->
            if inside && pointer == cell && at == offset && source == resultTemp
            then
                collected
                |> reverse
                |> Some
            else
                stringPartStoreInterval(resultTemp)(cell)(offset)(rest)(inside)(if inside
                then instruction :: collected
                else collected)
        | instruction :: rest ->
            stringPartStoreInterval(resultTemp)(cell)(offset)(rest)(inside)(if inside
            then instruction :: collected
            else collected)

let recursive stringPartIntervalHasLabel (target: Str) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = Label(name) } :: rest -> name == target || stringPartIntervalHasLabel(target)(rest)
        | _instruction :: rest -> stringPartIntervalHasLabel(target)(rest)

// Stage 0's BorrowedStringSurvivesUntilStore: the original source must remain alive until
// its requested retain at the store. Calls, releases, resets and nonlocal branches fail closed.
let recursive stringPartIntervalKeepsOwner (instructions: List(IrInstruction)) (all: List(IrInstruction)) =
    match instructions with
        | [] -> true
        | IrInstruction { instruction = kind } :: rest ->
            let safe =
                match kind with
                    | LoadLocal(_target, _slot) -> true
                    | StoreLocal(_slot, _source) -> true
                    | LoadMemOffset(_target, _pointer, _offset) -> true
                    | StoreMemOffset(_pointer, _offset, _source) -> true
                    | LoadConstInt(_target, _value) -> true
                    | LoadArgumentOwnership(_target) -> true
                    | IsReferenceCounted(_target, _source) -> true
                    | CopyOutArena(_target, _source, _size, _runtime, _purpose, _deferred) -> true
                    | Alloc(_target, _size, _runtime) -> true
                    | Borrow(_target, _source) -> true
                    | RcDup(_target, _source, _runtime, _empty) -> true
                    | AndInt(_target, _left, _right) -> true
                    | ShrInt(_target, _left, _right) -> true
                    | Label(_name) -> true
                    | Jump(target) -> stringPartIntervalHasLabel(target)(all)
                    | JumpIfFalse(_condition, target) -> stringPartIntervalHasLabel(target)(all)
                    | _ -> false
            in safe && stringPartIntervalKeepsOwner(rest)(all)

let borrowedStringSurvivesUntilStore (resultTemp: Int) (cell: Int) (offset: Int) (state: CoreLoweringState) =
    match stringPartStoreInterval(resultTemp)(cell)(offset)(reverse(state.reversedInstructions))(false)([]) with
        | Some(interval) -> stringPartIntervalKeepsOwner(interval)(interval)
        | None -> false

let recursive guardStringDemotion (counted: Int) (guard: List(IrInstruction)) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> []
        | (IrInstruction { instruction = IsReferenceCounted(target, _source) } as instruction) :: rest ->
            if target == counted
            then instruction :: append(guard)(rest)
            else instruction :: guardStringDemotion(counted)(guard)(rest)
        | instruction :: rest -> instruction :: guardStringDemotion(counted)(guard)(rest)

let preserveRequestedStringDemotion (counted: Int) (doneLabel: Str) (state: CoreLoweringState) =
    match freshLabel("tuple_string_part_copy")((state with reversedInstructions = [])) with
        | FreshLabel { state = labelled, label = copyLabel } ->
            let guarded =
                labelled
                |> emitJumpUnlessTuplePartsRequested(copyLabel)
                |> emit(Jump(doneLabel))
                |> emit(Label(copyLabel))
            in guarded with reversedInstructions = guardStringDemotion(counted)(guarded.reversedInstructions)(state.reversedInstructions)

// Only the sole returned arena tuple can bypass a borrowed Bytes/Str copy, and only when its
// caller requests the parts. The existing retain at the store and mask settlement own and
// release each alias separately. Unrequested and fixed-copyout callers keep the original copy.
let recursive preserveRequestedBorrowedStringParts (cell: Int) (parts: List(Int)) (types: List(SemanticType)) (stores: List(IrInstruction)) (ownedSlots: List(Int)) (state: CoreLoweringState) (borrowed: List((Int, Int))) =
    match stores with
        | [] -> (state, borrowed)
        | IrInstruction { instruction = StoreMemOffset(pointer, offset, source) } :: rest ->
            let semanticType =
                types
                |> nthElementType(offset / 8)
                |> resolveType(state)
            in
                if pointer == cell && offset >= 0 && isTuplePartOffset(offset)(parts) && (semanticType == SemString || semanticType == SemBytes) && tuplePartStoreCount(cell)(offset)(state.reversedInstructions)(0) == 1 && borrowedStringSurvivesUntilStore(source)(cell)(offset)(state)
                then
                    match borrowedStringDemotion(source)(state.reversedInstructions)(ownedSlots)(reverse(state.reversedInstructions))(state) with
                        | Some((counted, original, doneLabel)) ->
                            preserveRequestedBorrowedStringParts(cell)(parts)(types)(rest)(ownedSlots)(preserveRequestedStringDemotion(counted)(doneLabel)(state))((source, original) :: borrowed)
                        | None -> preserveRequestedBorrowedStringParts(cell)(parts)(types)(rest)(ownedSlots)(state)(borrowed)
                else preserveRequestedBorrowedStringParts(cell)(parts)(types)(rest)(ownedSlots)(state)(borrowed)
        | _instruction :: rest -> preserveRequestedBorrowedStringParts(cell)(parts)(types)(rest)(ownedSlots)(state)(borrowed)

// Stage 0's `OwnedSlotsReleasedBehindTupleParts`, over the instructions in order with their
// positions: the values that pin owned slots (every other element of the result's cells, and a
// part that is fresh or not read out of an owned value), and the positions of the part stores of
// a value other than a fresh one, whose stored value is retained under the request.
let recursive tuplePartStores (instructions: List(IrInstruction)) (position: Int) (cells: List(Int)) (parts: List(Int)) (normalized: List(Int)) (scalars: List(Int)) (ownedSlots: List(Int)) (forward: List(IrInstruction)) (state: CoreLoweringState) (borrowed: List((Int, Int))) (pinning: List(Int)) (retained: List((Int, Int))) =
    match instructions with
        | [] -> (reverse(pinning), reverse(retained))
        | IrInstruction { instruction = StoreMemOffset(basePtr, offset, source) } :: rest ->
            if containsInt(basePtr)(cells) == false
            then tuplePartStores(rest)(position + 1)(cells)(parts)(normalized)(scalars)(ownedSlots)(forward)(state)(borrowed)(pinning)(retained)
            else
                if isTuplePartOffset(offset)(parts) == false
                then
                    if isTuplePartOffset(offset)(scalars)
                    then tuplePartStores(rest)(position + 1)(cells)(parts)(normalized)(scalars)(ownedSlots)(forward)(state)(borrowed)(pinning)(retained)
                    else tuplePartStores(rest)(position + 1)(cells)(parts)(normalized)(scalars)(ownedSlots)(forward)(state)(borrowed)(source :: pinning)(retained)
                else
                    if isTuplePartOffset(offset)(normalized)
                    then tuplePartStores(rest)(position + 1)(cells)(parts)(normalized)(scalars)(ownedSlots)(forward)(state)(borrowed)(pinning)(retained)
                    else
                        let original = borrowedTuplePartSource(source)(borrowed)
                        in
                            let fresh = isFreshRuntimeTemp(original)(state)
                            in
                                let nextRetained =
                                    if fresh
                                    then retained
                                    else (position, source) :: retained
                                in
                                    if fresh
                                    then tuplePartStores(rest)(position + 1)(cells)(parts)(normalized)(scalars)(ownedSlots)(forward)(state)(borrowed)(original :: pinning)(nextRetained)
                                    else
                                        if readOutOfOwnedValue([original])(ownedSlots)([])(forward)(state)
                                        then tuplePartStores(rest)(position + 1)(cells)(parts)(normalized)(scalars)(ownedSlots)(forward)(state)(borrowed)(pinning)(nextRetained)
                                        else tuplePartStores(rest)(position + 1)(cells)(parts)(normalized)(scalars)(ownedSlots)(forward)(state)(borrowed)(original :: pinning)(nextRetained)
        | _ :: rest -> tuplePartStores(rest)(position + 1)(cells)(parts)(normalized)(scalars)(ownedSlots)(forward)(state)(borrowed)(pinning)(retained)

// Stage 0's `PartsKeepingAliasOwnersAlive`: the parts every store of which is an arena alias of an
// owner only a slot of this function keeps alive (a record built over a let-bound list). No
// reference the part could hand over exists, so the part is placed on the reference-counted heap
// when the caller asks for it, where it holds references of its own, and the slot is released
// behind it.
let recursive partStoreSources (instructions: List(IrInstruction)) (cells: List(Int)) (offset: Int) (borrowed: List((Int, Int))) (sources: List(Int)) =
    match instructions with
        | [] -> sources
        | IrInstruction { instruction = StoreMemOffset(basePtr, storeOffset, source) } :: rest ->
            if storeOffset == offset && containsInt(basePtr)(cells)
            then partStoreSources(rest)(cells)(offset)(borrowed)(borrowedTuplePartSource(source)(borrowed) :: sources)
            else partStoreSources(rest)(cells)(offset)(borrowed)(sources)
        | _ :: rest -> partStoreSources(rest)(cells)(offset)(borrowed)(sources)

let recursive anyAliasOwnerSlotIn (slots: List(Int)) (aliases: List((Int, Int))) =
    match aliases with
        | [] -> false
        | (slot, _aliasTemp) :: rest -> containsInt(slot)(slots) || anyAliasOwnerSlotIn(slots)(rest)

let recursive keepAliasOwnersAlive (sources: List(Int)) (ownedSlots: List(Int)) (aliases: List((Int, Int))) (forward: List(IrInstruction)) (state: CoreLoweringState) =
    match sources with
        | [] -> true
        | source :: rest ->
            !isFreshRuntimeTemp(source)(state) && !readOutOfOwnedValue([source])(ownedSlots)([])(forward)(state) && anyAliasOwnerSlotIn(localSlotsReachingTempsWithAliases([source])([])([])(aliases)(state.reversedInstructions))(aliases) && keepAliasOwnersAlive(rest)(ownedSlots)(aliases)(forward)(state)

let isNormalizedPartType (semanticType: SemanticType) (state: CoreLoweringState) =
    match generalCopyPlanOf(semanticType)(state) with
        | Some(_plan) -> !resultSurvivesReset(semanticType)(state) && !containsUnresolvedLayout(semanticType)(state)
        | None -> false

let recursive partsKeepingAliasOwnersAlive (parts: List(Int)) (types: List(SemanticType)) (cells: List(Int)) (borrowed: List((Int, Int))) (ownedSlots: List(Int)) (aliases: List((Int, Int))) (forward: List(IrInstruction)) (state: CoreLoweringState) =
    match parts with
        | [] -> []
        | part :: rest ->
            match partStoreSources(forward)(cells)(part * 8)(borrowed)([]) with
                | [] -> partsKeepingAliasOwnersAlive(rest)(types)(cells)(borrowed)(ownedSlots)(aliases)(forward)(state)
                | sources ->
                    if isNormalizedPartType(types
                    |> nthElementType(part)
                    |> resolveType(state))(state) && keepAliasOwnersAlive(sources)(ownedSlots)(aliases)(forward)(state)
                    then part :: partsKeepingAliasOwnersAlive(rest)(types)(cells)(borrowed)(ownedSlots)(aliases)(forward)(state)
                    else partsKeepingAliasOwnersAlive(rest)(types)(cells)(borrowed)(ownedSlots)(aliases)(forward)(state)

let normalizedTupleParts (resultType: SemanticType) (parts: List(Int)) (cells: List(Int)) (borrowed: List((Int, Int))) (ownedSlots: List(Int)) (forward: List(IrInstruction)) (state: CoreLoweringState) =
    match (resolveType(state)(resultType), aliasOwnerSlotPairs(state)) with
        | (_resolved, []) -> []
        | (SemTuple(types), aliases) -> partsKeepingAliasOwnersAlive(parts)(types)(cells)(borrowed)(ownedSlots)(aliases)(forward)(state)
        | _ -> []

// Stage 0's `EmitReferenceCountedPartsOfArenaResult`: on the requested path of an arena result,
// each part of `parts` is replaced in the result by a reference to it on the reference-counted
// heap (a retain of one already there, a copy of an arena one), the reference the part hands over.
let recursive emitReferenceCountedParts (resultTemp: Int) (types: List(SemanticType)) (parts: List(Int)) (state: CoreLoweringState) =
    match parts with
        | [] -> state
        | part :: rest ->
            match generalCopyPlanOf(types
            |> nthElementType(part)
            |> resolveType(state))(state) with
                | None -> emitReferenceCountedParts(resultTemp)(types)(rest)(state)
                | Some(plan) ->
                    match freshLabel("tuple_part_absent")(state) with
                        | FreshLabel { state = labelled, label = skipLabel } ->
                            match freshTempRun(3)(labelled) with
                                | FreshTemp { state = allocated, temp = partTemp } ->
                                    match allocated
                                    |> emit(LoadMemOffset(partTemp)(resultTemp)(part * 8))
                                    |> emit(LoadConstInt(partTemp + 1)(0))
                                    |> emit(CmpIntNe(partTemp + 2)(partTemp)(partTemp + 1))
                                    |> emit(JumpIfFalse(partTemp + 2)(skipLabel))
                                    |> emitGuardedDeepCopy(partTemp)(plan) with
                                        | (copied, ownedTemp) ->
                                            copied
                                            |> emit(StoreMemOffset(resultTemp)(part * 8)(ownedTemp))
                                            |> emit(Label(skipLabel))
                                            |> emitReferenceCountedParts(resultTemp)(types)(rest)

let emitReferenceCountedPartsOfArenaResult (resultTemp: Int) (resultType: SemanticType) (parts: List(Int)) (state: CoreLoweringState) =
    match (parts, resolveType(state)(resultType)) with
        | ([], _resolved) -> state
        | (_parts, SemTuple(types)) -> emitReferenceCountedParts(resultTemp)(types)(parts)(state)
        | _ -> state

// The retain spliced behind one part store: the stored value is retained when the caller asked for
// the parts. Emitted on its own, most recent first.
let emitTuplePartRetain (source: Int) (state: CoreLoweringState) =
    match freshLabel("tuple_part_not_requested")(state) with
        | FreshLabel { state = labelled, label = skipLabel } ->
            labelled
            |> emitJumpUnlessTuplePartsRequested(skipLabel)
            |> emitRetainIfReferenceCounted(source)
            |> emit(Label(skipLabel))

let recursive emitTuplePartRetains (stores: List((Int, Int))) (state: CoreLoweringState) (spliced: List((Int, List(IrInstruction)))) =
    match stores with
        | [] -> (state, spliced)
        | (position, source) :: rest ->
            let emitted = emitTuplePartRetain(source)((state with reversedInstructions = []))
            in emitTuplePartRetains(rest)((emitted with reversedInstructions = state.reversedInstructions))((position, emitted.reversedInstructions) :: spliced)

let recursive splicedAt (position: Int) (spliced: List((Int, List(IrInstruction)))) =
    match spliced with
        | [] -> []
        | (at, instructions) :: rest ->
            if at == position
            then instructions
            else splicedAt(position)(rest)

// Rebuilds the most-recent-first instructions with each splice placed right behind the store at
// its position.
let recursive spliceBehindStores (reversedInstructions: List(IrInstruction)) (position: Int) (spliced: List((Int, List(IrInstruction)))) =
    match reversedInstructions with
        | [] -> []
        | instruction :: rest ->
            append(splicedAt(position)(spliced))(instruction :: spliceBehindStores(rest)(position - 1)(spliced))

// Stage 0's `RetainTuplePartStoresWhenRequested`.
let retainTuplePartStoresWhenRequested (stores: List((Int, Int))) (state: CoreLoweringState) =
    match emitTuplePartRetains(stores)(state)([]) with
        | (emitted, []) -> emitted
        | (emitted, spliced) -> emitted with reversedInstructions = spliceBehindStores(emitted.reversedInstructions)(length(emitted.reversedInstructions) - 1)(spliced)

let recursive emitTuplePartRetainsAtReturn (resultTemp: Int) (parts: List(Int)) (state: CoreLoweringState) =
    match parts with
        | [] -> state
        | part :: rest ->
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = childTemp } ->
                    allocated
                    |> emit(LoadMemOffset(childTemp)(resultTemp)(part * 8))
                    |> emitRetainIfReferenceCounted(childTemp)
                    |> emitTuplePartRetainsAtReturn(resultTemp)(rest)

// The same, giving back the temps the parts were read into.
let recursive emitTuplePartRetainsReading (resultTemp: Int) (parts: List(Int)) (partTemps: List(Int)) (state: CoreLoweringState) =
    match parts with
        | [] -> (state, partTemps)
        | part :: rest ->
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = childTemp } ->
                    allocated
                    |> emit(LoadMemOffset(childTemp)(resultTemp)(part * 8))
                    |> emitRetainIfReferenceCounted(childTemp)
                    |> emitTuplePartRetainsReading(resultTemp)(rest)(childTemp :: partTemps)

let recursive emitJumpIfAnyPartIs (valueTemp: Int) (partTemps: List(Int)) (dropLabel: Str) (state: CoreLoweringState) =
    match partTemps with
        | [] -> state
        | partTemp :: rest ->
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = sameTemp } ->
                    match freshLabel("tuple_part_owner_other")(allocated) with
                        | FreshLabel { state = labelled, label = otherLabel } ->
                            labelled
                            |> emit(CmpIntEq(sameTemp)(valueTemp)(partTemp))
                            |> emit(JumpIfFalse(sameTemp)(otherLabel))
                            |> emit(Jump(dropLabel))
                            |> emit(Label(otherLabel))
                            |> emitJumpIfAnyPartIs(valueTemp)(rest)(dropLabel)

// Stage 0's `EmitOwnedSlotDropsBehindRetainedParts`: a part retained at the return that is the
// very value an owned slot holds has the slot's reference released behind it, so the retain hands
// that reference over rather than adding one the function never releases. An owned value a part
// only reads out of keeps its reference.
let recursive emitOwnedSlotDropsBehindRetainedParts (origin: Maybe(IrFunctionOrigin)) (slots: List((Maybe(IrFunctionOrigin), Int, SemanticType))) (partTemps: List(Int)) (state: CoreLoweringState) =
    match slots with
        | [] -> state
        | (owner, slot, semanticType) :: rest ->
            if owner == origin
            then
                match freshLabel("tuple_part_owner_kept")(state) with
                    | FreshLabel { state = keptState, label = nextLabel } ->
                        match freshLabel("tuple_part_owner_moved")(keptState) with
                            | FreshLabel { state = movedState, label = dropLabel } ->
                                match freshTempRun(2)(movedState) with
                                    | FreshTemp { state = allocated, temp = valueTemp } ->
                                        allocated
                                        |> emit(LoadLocal(valueTemp)(slot))
                                        |> emitSkipUnlessReferenceCounted(valueTemp)(nextLabel)
                                        |> emitJumpIfAnyPartIs(valueTemp)(partTemps)(dropLabel)
                                        |> emit(Jump(nextLabel))
                                        |> emit(Label(dropLabel))
                                        |> emitOwnedValueChildDrop(valueTemp)(semanticType)
                                        |> emit(LoadConstInt(valueTemp + 1)(0))
                                        |> emit(StoreLocal(slot)(valueTemp + 1))
                                        |> emit(Label(nextLabel))
                                        |> emitOwnedSlotDropsBehindRetainedParts(origin)(rest)(partTemps)
            else emitOwnedSlotDropsBehindRetainedParts(origin)(rest)(partTemps)(state)

let recursive slotNumberAmong (slot: Int) (slots: List((Maybe(IrFunctionOrigin), Int, SemanticType))) =
    match slots with
        | [] -> false
        | (_owner, candidate, _semanticType) :: rest -> candidate == slot || slotNumberAmong(slot)(rest)

let recursive ownedSlotsBesideKept (kept: List((Maybe(IrFunctionOrigin), Int, SemanticType))) (slots: List((Maybe(IrFunctionOrigin), Int, SemanticType))) =
    match slots with
        | [] -> []
        | ((_owner, slot, _semanticType) as entry) :: rest ->
            if slotNumberAmong(slot)(kept)
            then ownedSlotsBesideKept(kept)(rest)
            else entry :: ownedSlotsBesideKept(kept)(rest)

// Stage 0's `TryReleaseOwnedSlotsHandingOverTupleParts`: the return of a function whose result is
// a generic tuple with contract elements. When the caller asks for the parts and the result is an
// arena cell, each part holds a reference of its own and the owned values only the parts read out
// of are released behind them; otherwise only the owned values out of the result's reach are. When
// every cell the result can be is known (outside a loop), a part takes its reference where it is
// stored: a fresh value's is the cell's already, any other value is retained under the request.
// The hand-over plan: the owned slots released when the parts are handed over, the state with the
// part retains spliced in, and whether every cell the result can be is known.
let tuplePartsHandOverPlan (resultTemp: Int) (resultType: SemanticType) (parts: List(Int)) (scalars: List(Int)) (registered: List((Maybe(IrFunctionOrigin), Int, SemanticType))) (kept: List((Maybe(IrFunctionOrigin), Int, SemanticType))) (state: CoreLoweringState) =
    (let forward = reverse(state.reversedInstructions)
    in
        let cells =
            match stateTcoLoopFrame(state) with
                | Some(_frame) -> None
                | None -> arenaCellsReaching([resultTemp])([])([])(forward)(state)
        in
            let ownedSlots =
                []
                |> ownedSlotsOf(stateActiveFunctionOrigin(state))(registered)
                |> ownedSlotNumbers
            in
                let prepared =
                    match (cells, resolveType(state)(resultType)) with
                        | (Some(cell :: []), SemTuple(types)) -> preserveRequestedBorrowedStringParts(cell)(parts)(types)(forward)(ownedSlots)(state)([])
                        | _ -> (state, [])
                in
                    match prepared with
                        | (guarded, borrowed) ->
                            match cells with
                                | Some(first :: others) ->
                                    let guardedForward = reverse(guarded.reversedInstructions)
                                    in
                                        let normalized = normalizedTupleParts(resultType)(parts)(first :: others)(borrowed)(ownedSlots)(guardedForward)(guarded)
                                        in
                                            match tuplePartStores(guardedForward)(0)(first :: others)(parts)(normalized)(scalars)(ownedSlots)(guardedForward)(guarded)(borrowed)([])([]) with
                                                | (pinning, retained) ->
                                                    (ownedSlotsOutside(localSlotsReachingTempsWithAliases(pinning)([])([])(aliasOwnerSlotPairs(guarded))(guarded.reversedInstructions))(registered), retainTuplePartStoresWhenRequested(retained)(guarded), true, normalized)
                                | _ -> (kept, guarded, false, []))

let recursive withoutAbsentValues (sources: List(Int)) (state: CoreLoweringState) =
    match sources with
        | [] -> []
        | source :: rest ->
            match definitionOfTemp(source)(state.reversedInstructions) with
                | Some(LoadConstInt(_target, value)) ->
                    if value == 0
                    then withoutAbsentValues(rest)(state)
                    else source :: withoutAbsentValues(rest)(state)
                | _ -> source :: withoutAbsentValues(rest)(state)

// The values stored into a slot that a call result can be: a match's fallback storing no value
// aside when there are others.
let callSourcesOf (sources: List(Int)) (state: CoreLoweringState) =
    match sources with
        | _first :: _second :: _rest -> withoutAbsentValues(sources)(state)
        | _ -> sources

// Stage 0's `SoleCallThroughLocals`: the call result a temp is, through locals stored once and
// borrows (a match's fallback storing no value aside); -1 when it may be another value.
let recursive soleCallSourceThroughLocals (temp: Int) (depth: Int) (forward: List(IrInstruction)) (state: CoreLoweringState) =
    if depth == 0
    then -1
    else
        match definitionOfTemp(temp)(state.reversedInstructions) with
            | Some(CallClosure(target, _closure, _argument, _word)) -> target
            | Some(Borrow(_target, source)) -> soleCallSourceThroughLocals(source)(depth - 1)(forward)(state)
            | Some(LoadLocal(_target, slot)) ->
                match callSourcesOf(storedSourcesOf(slot)(forward))(state) with
                    | source :: [] -> soleCallSourceThroughLocals(source)(depth - 1)(forward)(state)
                    | _ -> -1
            | _ -> -1

let recursive sameInts (left: List(Int)) (right: List(Int)) =
    match (left, right) with
        | ([], []) -> true
        | (l :: leftRest, r :: rightRest) -> l == r && sameInts(leftRest)(rightRest)
        | _ -> false

let recursive withdrawnRequestFor (callTemp: Int) (parts: List(Int)) (requests: List((Int, Int, List(Int)))) =
    match requests with
        | [] -> -1
        | (resultTemp, request, asked) :: rest ->
            if resultTemp == callTemp && sameInts(asked)(parts)
            then request
            else withdrawnRequestFor(callTemp)(parts)(rest)

// A tuple-parts request word: an `OrInt` of the request constant 4 and the address of the arena
// word the callee writes its mask into. The request constant, the word, and that address.
let tuplePartsRequestOfWord (requestWord: Int) (instructions: List(IrInstruction)) =
    match definitionOfTemp(requestWord)(instructions) with
        | Some(OrInt(_target, constant, cell)) ->
            match definitionOfTemp(constant)(instructions) with
                | Some(LoadConstInt(_constantTarget, 4)) -> Some((constant, requestWord, cell))
                | _ -> None
        | _ -> None

// The tuple-parts request of the ownership word `word` a call passes: the request word is the word
// itself or the left operand of the `OrInt` joining it to the rest; None when the call asked for no
// parts.
let tuplePartsRequestOf (word: Int) (instructions: List(IrInstruction)) =
    match tuplePartsRequestOfWord(word)(instructions) with
        | Some(request) -> Some(request)
        | None ->
            match definitionOfTemp(word)(instructions) with
                | Some(OrInt(_target, requestWord, _rest)) -> tuplePartsRequestOfWord(requestWord)(instructions)
                | _ -> None

let recursive callTuplePartsRequest (resultTemp: Int) (budget: Int) (instructions: List(IrInstruction)) =
    if budget == 0
    then None
    else
        match instructions with
            | [] -> None
            | IrInstruction { instruction = CallClosure(target, _closure, _argument, word) } :: rest ->
                if target == resultTemp
                then
                    if word < 0
                    then None
                    else tuplePartsRequestOf(word)(rest)
                else callTuplePartsRequest(resultTemp)(budget - 1)(rest)
            | _ :: rest -> callTuplePartsRequest(resultTemp)(budget - 1)(rest)

// The withdrawn request word replaced by this function's own caller's request and mask address,
// read out of its ownership word. The instructions are most recent first.
let recursive forwardRequestWord (requestWord: Int) (wordTemp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> []
        | (IrInstruction { instruction = OrInt(target, _left, _right) } as instruction) :: rest ->
            if target == requestWord
            then IrInstruction(instruction = AndInt(requestWord)(wordTemp)(wordTemp + 1), location = None) :: IrInstruction(instruction = LoadConstInt(wordTemp + 1)(-4), location = None) :: IrInstruction(instruction = LoadArgumentOwnership(wordTemp), location = None) :: rest
            else instruction :: forwardRequestWord(requestWord)(wordTemp)(rest)
        | instruction :: rest -> instruction :: forwardRequestWord(requestWord)(wordTemp)(rest)

// Stage 0's `TryForwardTupleResultPartsRequest`: a result that is another function's generic tuple
// result returned as it is takes no parts itself; the call's withdrawn request for the same parts
// becomes this function's own caller's request, so the callee hands the parts over to that caller
// directly. None when the result is no such call's.
let forwardTupleResultPartsRequest (resultTemp: Int) (parts: List(Int)) (state: CoreLoweringState) =
    (let callTemp =
        soleCallSourceThroughLocals(resultTemp)(16)(reverse(state.reversedInstructions))(state)
    in
        if callTemp < 0
        then None
        else
            let request =
                state
                |> stateWithdrawnTuplePartsRequests
                |> withdrawnRequestFor(callTemp)(parts)
            in
                if request < 0
                then None
                else
                    match freshTempRun(2)(state) with
                        | FreshTemp { state = allocated, temp = wordTemp } -> Some((allocated with reversedInstructions = forwardRequestWord(request)(wordTemp)(allocated.reversedInstructions))))

// The call producing `callTemp` with its ownership word replaced by `word` and the forwarded
// request instructions placed before it. The instructions are most recent first.
let recursive forwardRequestIntoCall (callTemp: Int) (word: Int) (forwarded: List(IrInstruction)) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> []
        | (IrInstruction { instruction = CallClosure(target, closure, argument, _word) } as instruction) :: rest ->
            if target == callTemp
            then (instruction with instruction = CallClosure(target)(closure)(argument)(word)) :: append(forwarded)(rest)
            else instruction :: forwardRequestIntoCall(callTemp)(word)(forwarded)(rest)
        | instruction :: rest -> instruction :: forwardRequestIntoCall(callTemp)(word)(forwarded)(rest)

let recursive callWordOf (callTemp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> None
        | IrInstruction { instruction = CallClosure(target, _closure, _argument, word) } :: rest ->
            if target == callTemp
            then Some(word)
            else callWordOf(callTemp)(rest)
        | _ :: rest -> callWordOf(callTemp)(rest)

// Stage 0's `ForwardCallerRequestIntoCall`: this function's own caller's request and mask address
// joined to whatever the call's word already passes, so the callee answers that caller.
let forwardCallerRequestIntoCall (callTemp: Int) (state: CoreLoweringState) =
    match callWordOf(callTemp)(state.reversedInstructions) with
        | None -> state
        | Some(callWord) ->
            match freshTempRun(if callWord < 0
            then 3
            else 4)(state) with
                | FreshTemp { state = allocated, temp = requestTemp } ->
                    let forwarded =
                        [
                            IrInstruction(instruction = AndInt(requestTemp)(requestTemp + 1)(requestTemp + 2), location = None),
                            IrInstruction(instruction = LoadConstInt(requestTemp + 2)(-4), location = None),
                            IrInstruction(instruction = LoadArgumentOwnership(requestTemp + 1), location = None)
                        ]
                    in
                        if callWord < 0
                        then allocated with reversedInstructions = forwardRequestIntoCall(callTemp)(requestTemp)(forwarded)(allocated.reversedInstructions)
                        else allocated with reversedInstructions = forwardRequestIntoCall(callTemp)(requestTemp + 3)(IrInstruction(instruction = OrInt(requestTemp + 3)(requestTemp)(callWord), location = None) :: forwarded)(allocated.reversedInstructions)

// Stage 0's `WholeResultHandedOverBit`: the mask bit a function writes when it handed its whole
// result over.
let wholeResultHandedOverBit = 4611686018427387904

// The part a function's entry in the tuple-result part labels carries when callers may ask it for
// its whole result (stage 0's `_wholeResultHandOverLabels`): it hands its owned result over whole,
// or forwards the request.
let wholeResultHandOverMarker = -1

// A function's entry in the tuple-result part labels with the whole-result marker added.
let withWholeResultHandOverLabel (label: Str) (state: CoreLoweringState) =
    (let existing =
        match state
        |> stateTupleResultPartLabels
        |> Ashes.Collection.Map.getStr(label) with
            | Some(parts) -> parts
            | None -> []
    in
        if containsInt(wholeResultHandOverMarker)(existing)
        then state
        else
            withStateTupleResultPartLabels(state
            |> stateTupleResultPartLabels
            |> Ashes.Collection.Map.setStr(label)(wholeResultHandOverMarker :: existing))(state))

// Stage 0's `IsGenericResultForwardedAsIs`: a result of unresolved type, or of a generic named
// type outside the contract's types (a `Result(e, T)` with `e` unresolved), whose caller may know a
// concrete type to ask for.
let isGenericResultForwardedAsIs (resultType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(resultType) with
        | SemVariable(_variable) -> true
        | SemNamed(symbolId, name, arguments) ->
            containsUnresolvedLayout(SemNamed(symbolId)(name)(arguments))(state) && !isGeneralRcValueType(SemNamed(symbolId)(name)(arguments))(state)
        | _ -> false

// The parts a call's withdrawn request asked for, when it has one on record.
let recursive withdrawnAskedFor (callTemp: Int) (requests: List((Int, Int, List(Int)))) =
    match requests with
        | [] -> None
        | (resultTemp, _request, asked) :: rest ->
            if resultTemp == callTemp
            then Some(asked)
            else withdrawnAskedFor(callTemp)(rest)

// Stage 0's `ForwardTupleResultPartsRequestThroughGenericResult`: a result of unresolved type that
// is a call's result returned as it is forwards its own caller's request and mask address to that
// call, beside whatever its word already passes; the mask the callee writes says whether it handed
// anything over.
let forwardTupleResultPartsRequestThroughGenericResult (label: Str) (resultTemp: Int) (resultType: SemanticType) (state: CoreLoweringState) =
    if generalRcEnabled(state) && isGenericResultForwardedAsIs(resultType)(state)
    then
        let callTemp =
            soleCallSourceThroughLocals(resultTemp)(16)(reverse(state.reversedInstructions))(state)
        in
            if callTemp < 0
            then state
            else
                match state
                |> stateWithdrawnTuplePartsRequests
                |> withdrawnAskedFor(callTemp) with
                    | Some(asked) ->
                        match forwardTupleResultPartsRequest(resultTemp)(asked)(state) with
                            | Some(forwarded) -> withWholeResultHandOverLabel(label)(forwarded)
                            | None -> state
                    | None ->
                        match callWordOf(callTemp)(state.reversedInstructions) with
                            | None -> state
                            | Some(_callWord) ->
                                match callTuplePartsRequest(callTemp)(64)(state.reversedInstructions) with
                                    | Some(_request) -> state
                                    | None ->
                                        state
                                        |> forwardCallerRequestIntoCall(callTemp)
                                        |> withWholeResultHandOverLabel(label)
    else state

let recursive powerOfTwo (exponent: Int) =
    if exponent <= 0
    then 1
    else 2 * powerOfTwo(exponent - 1)

// On the hand-over path: the mask of the parts handed over, written into the caller's word.
let recursive tuplePartsMask (parts: List(Int)) =
    match parts with
        | [] -> 0
        | part :: rest -> powerOfTwo(part) + tuplePartsMask(rest)

// The mask of what was handed over, written into the word the caller's request addresses.
let emitResultHandOverMask (mask: Int) (state: CoreLoweringState) =
    match freshTempRun(4)(state) with
        | FreshTemp { state = allocated, temp = wordTemp } ->
            allocated
            |> emit(LoadArgumentOwnership(wordTemp))
            |> emit(LoadConstInt(wordTemp + 1)(-8))
            |> emit(AndInt(wordTemp + 2)(wordTemp)(wordTemp + 1))
            |> emit(LoadConstInt(wordTemp + 3)(mask))
            |> emit(StoreMemOffset(wordTemp + 2)(0)(wordTemp + 3))

let emitTupleResultPartsMask (parts: List(Int)) (state: CoreLoweringState) =
    emitResultHandOverMask(tuplePartsMask(parts))(state)

// Stage 0's `IndependentResultBit`: the mask bit a function writes when its result holds nothing of
// the values its call borrowed.
let independentResultBit = 2305843009213693952

// Stage 0's `NoteOwnedContractResult`: a function of a contract result type that is asked writes the
// result's independence into the caller's mask.
let emitIndependentResultSignal (state: CoreLoweringState) =
    match freshLabel("independent_result_not_requested")(state) with
        | FreshLabel { state = labelled, label = skipLabel } ->
            labelled
            |> emitJumpUnlessTuplePartsRequested(skipLabel)
            |> emitResultHandOverMask(independentResultBit)
            |> emit(Label(skipLabel))

// Stage 0's `EmitClosureReturnsGeneralRcOwnedFlag`: bit 60 of the closure's header word says that
// its function returns a result of the contract's types owned.
let emitClosureReturnsGeneralRcOwnedFlag (closureTemp: Int) (state: CoreLoweringState) =
    match freshTempRun(5)(state) with
        | FreshTemp { state = allocated, temp = packedTemp } ->
            (allocated
            |> emit(LoadMemOffset(packedTemp)(closureTemp)(16))
            |> emit(LoadConstInt(packedTemp + 1)(60))
            |> emit(ShrInt(packedTemp + 2)(packedTemp)(packedTemp + 1))
            |> emit(LoadConstInt(packedTemp + 3)(1))
            |> emit(AndInt(packedTemp + 4)(packedTemp + 2)(packedTemp + 3)), packedTemp + 4)

// Stage 0's `ListHeadsHandedOverBit`: the mask bit a generic list builder writes when every
// reference-counted head of its result's arena cells holds a reference of its own.
let listHeadsHandedOverBit = 1152921504606846976

// The part a builder's entry in the tuple-result part labels carries when callers may ask it for
// its list's heads (stage 0's `_listHeadHandOverLabels`).
let listHeadHandOverMarker = -2

let withListHeadHandOverLabel (label: Str) (state: CoreLoweringState) =
    (let existing =
        match state
        |> stateTupleResultPartLabels
        |> Ashes.Collection.Map.getStr(label) with
            | Some(parts) -> parts
            | None -> []
    in
        if containsInt(listHeadHandOverMarker)(existing)
        then state
        else
            withStateTupleResultPartLabels(state
            |> stateTupleResultPartLabels
            |> Ashes.Collection.Map.setStr(label)(listHeadHandOverMarker :: existing))(state))

// The position of the last store of `headTemp` into the head of `cellTemp`, -1 when there is none.
let recursive listHeadStorePosition (cellTemp: Int) (headTemp: Int) (instructions: List(IrInstruction)) (position: Int) (found: Int) =
    match instructions with
        | [] -> found
        | IrInstruction { instruction = StoreMemOffset(basePtr, offset, source) } :: rest ->
            if basePtr == cellTemp && offset == 0 && source == headTemp
            then listHeadStorePosition(cellTemp)(headTemp)(rest)(position + 1)(position)
            else listHeadStorePosition(cellTemp)(headTemp)(rest)(position + 1)(found)
        | _ :: rest -> listHeadStorePosition(cellTemp)(headTemp)(rest)(position + 1)(found)

// Stage 0's `OwnListHeadWhenRequested`, emitted on its own: under the request, a
// reference-counted head the closure did not return owned is retained, so the cell holds a
// reference of its own either way.
let emitListHeadOwnership (headTemp: Int) (closureTemp: Int) (state: CoreLoweringState) =
    match freshLabel("list_head_not_requested")(state) with
        | FreshLabel { state = skipState, label = skipLabel } ->
            match freshLabel("list_head_borrowed")(skipState) with
                | FreshLabel { state = labelled, label = retainLabel } ->
                    match labelled
                    |> emitJumpUnlessTuplePartsRequested(skipLabel)
                    |> emitSkipUnlessReferenceCounted(headTemp)(skipLabel)
                    |> emitClosureReturnsGeneralRcOwnedFlag(closureTemp) with
                        | (flagged, ownedTemp) ->
                            match flagged
                            |> emit(JumpIfFalse(ownedTemp)(retainLabel))
                            |> emit(Jump(skipLabel))
                            |> emit(Label(retainLabel))
                            |> freshTemp with
                                | FreshTemp { state = allocated, temp = retainTemp } ->
                                    allocated
                                    |> emit(RcDup(retainTemp)(headTemp)(true)(false))
                                    |> emit(Label(skipLabel))

let recursive emitListHeadOwnerships (cells: List((Int, Int, Int, Int))) (forward: List(IrInstruction)) (state: CoreLoweringState) (spliced: List((Int, List(IrInstruction)))) =
    match cells with
        | [] -> (state, spliced)
        | (cellTemp, headTemp, closureTemp, _tailCall) :: rest ->
            let position = listHeadStorePosition(cellTemp)(headTemp)(forward)(0)(-1)
            in
                let emitted = emitListHeadOwnership(headTemp)(closureTemp)((state with reversedInstructions = []))
                in emitListHeadOwnerships(rest)(forward)((emitted with reversedInstructions = state.reversedInstructions))((position, emitted.reversedInstructions) :: spliced)

let recursive forwardListBuilderTails (cells: List((Int, Int, Int, Int))) (state: CoreLoweringState) =
    match cells with
        | [] -> state
        | (_cellTemp, _headTemp, _closureTemp, tailCall) :: rest ->
            if tailCall < 0
            then forwardListBuilderTails(rest)(state)
            else
                state
                |> forwardCallerRequestIntoCall(tailCall)
                |> forwardListBuilderTails(rest)

let recursive listBuilderTailsPresent (cells: List((Int, Int, Int, Int))) (state: CoreLoweringState) =
    match cells with
        | [] -> true
        | (_cellTemp, _headTemp, _closureTemp, tailCall) :: rest ->
            if tailCall < 0
            then listBuilderTailsPresent(rest)(state)
            else
                match callWordOf(tailCall)(state.reversedInstructions) with
                    | Some(_word) -> listBuilderTailsPresent(rest)(state)
                    | None -> false

let recursive listBuildersIn (cells: List(Int)) (noted: List((Int, Int, Int, Int))) =
    match noted with
        | [] -> []
        | ((cellTemp, _headTemp, _closureTemp, _tailCall) as cell) :: rest ->
            if containsInt(cellTemp)(cells)
            then cell :: listBuildersIn(cells)(rest)
            else listBuildersIn(cells)(rest)

let handOverListBuilderCells (label: Str) (builders: List((Int, Int, Int, Int))) (state: CoreLoweringState) =
    (let forwarded = forwardListBuilderTails(builders)(state)
    in
        match emitListHeadOwnerships(builders)(reverse(forwarded.reversedInstructions))(forwarded)([]) with
            | (emitted, spliced) ->
                match freshLabel("list_heads_not_requested")((emitted with reversedInstructions = spliceBehindStores(emitted.reversedInstructions)(length(emitted.reversedInstructions) - 1)(spliced))) with
                    | FreshLabel { state = labelled, label = skipLabel } ->
                        labelled
                        |> emitJumpUnlessTuplePartsRequested(skipLabel)
                        |> emitResultHandOverMask(listHeadsHandedOverBit)
                        |> emit(Label(skipLabel))
                        |> withListHeadHandOverLabel(label))

// Stage 0's `TryHandOverListBuilderHeads`: the return of a generic list builder every cell of
// whose result is one of its builder cells. When the caller asks, the recursive calls are asked
// on its behalf, each head holds a reference of its own, and the mask says so.
let handOverListBuilderHeads (label: Str) (resultTemp: Int) (resultType: SemanticType) (state: CoreLoweringState) =
    (let generic =
        match resolveType(state)(resultType) with
            | SemList(element) ->
                match resolveType(state)(element) with
                    | SemVariable(_id) -> true
                    | _ -> false
            | _ -> false
    in
        if generic
        then
            match arenaCellsReaching([resultTemp])([])([])(reverse(state.reversedInstructions))(state) with
                | Some(first :: others) ->
                    let builders =
                        state
                        |> stateListHeadCells
                        |> reverse
                        |> listBuildersIn(first :: others)
                    in
                        if length(builders) == length(first :: others) && listBuilderTailsPresent(builders)(state)
                        then handOverListBuilderCells(label)(builders)(state)
                        else state
                | _ -> state
        else state)

let recursive tuplePartElementType (index: Int) (elements: List(SemanticType)) =
    match elements with
        | [] -> None
        | element :: rest ->
            if index == 0
            then Some(element)
            else tuplePartElementType(index - 1)(rest)

// An arena result the caller asked for (bit 1 of the ownership word) holds nothing that needs an
// owner, so a part that is the very value an owned slot holds is copied into the arena in the
// result's field and the slot's reference is released. The caller reads the part as an arena value
// and never releases it; left as it was, the slot's reference was released by nobody.
let recursive emitArenaCopyOfOwnedPart (resultTemp: Int) (part: Int) (elementType: SemanticType) (origin: Maybe(IrFunctionOrigin)) (slots: List((Maybe(IrFunctionOrigin), Int, SemanticType))) (state: CoreLoweringState) =
    match slots with
        | [] -> state
        | (owner, slot, slotType) :: rest ->
            if owner == origin
            then
                match freshLabel("tuple_part_arena_owner_kept")(state) with
                    | FreshLabel { state = labelled, label = nextLabel } ->
                        match freshTempRun(4)(labelled) with
                            | FreshTemp { state = allocated, temp = valueTemp } ->
                                match allocated
                                |> emit(LoadLocal(valueTemp)(slot))
                                |> emitSkipUnlessReferenceCounted(valueTemp)(nextLabel)
                                |> emit(LoadMemOffset(valueTemp + 1)(resultTemp)(part * 8))
                                |> emit(CmpIntEq(valueTemp + 2)(valueTemp)(valueTemp + 1))
                                |> emit(JumpIfFalse(valueTemp + 2)(nextLabel))
                                |> emitArenaResultDeepCopy(valueTemp + 1)(elementType) with
                                    | (copied, copyTemp) ->
                                        copied
                                        |> emit(StoreMemOffset(resultTemp)(part * 8)(copyTemp))
                                        |> emitOwnedValueChildDrop(valueTemp)(slotType)
                                        |> emit(LoadConstInt(valueTemp + 3)(0))
                                        |> emit(StoreLocal(slot)(valueTemp + 3))
                                        |> emit(Label(nextLabel))
                                        |> emitArenaCopyOfOwnedPart(resultTemp)(part)(elementType)(origin)(rest)
            else emitArenaCopyOfOwnedPart(resultTemp)(part)(elementType)(origin)(rest)(state)

let recursive emitArenaCopiesOfOwnedParts (resultTemp: Int) (elements: List(SemanticType)) (parts: List(Int)) (origin: Maybe(IrFunctionOrigin)) (slots: List((Maybe(IrFunctionOrigin), Int, SemanticType))) (state: CoreLoweringState) =
    match parts with
        | [] -> state
        | part :: rest ->
            match tuplePartElementType(part)(elements) with
                | Some(elementType) ->
                    state
                    |> emitArenaCopyOfOwnedPart(resultTemp)(part)(elementType)(origin)(slots)
                    |> emitArenaCopiesOfOwnedParts(resultTemp)(elements)(rest)(origin)(slots)
                | None -> emitArenaCopiesOfOwnedParts(resultTemp)(elements)(rest)(origin)(slots)(state)

// The same under the caller's arena-result request only, for an arena tuple result.
let emitArenaCopiesOfOwnedPartsWhenRequested (resultTemp: Int) (resultType: SemanticType) (parts: List(Int)) (origin: Maybe(IrFunctionOrigin)) (slots: List((Maybe(IrFunctionOrigin), Int, SemanticType))) (state: CoreLoweringState) =
    match resolveType(state)(resultType) with
        | SemTuple(elements) ->
            match freshLabel("tuple_parts_arena_not_requested")(state) with
                | FreshLabel { state = labelled, label = skipLabel } ->
                    match freshLabel("tuple_parts_arena_copy")(labelled) with
                        | FreshLabel { state = copyLabelled, label = copyLabel } ->
                            match freshTempRun(5)(copyLabelled) with
                                | FreshTemp { state = allocated, temp = wordTemp } ->
                                    allocated
                                    |> emit(LoadArgumentOwnership(wordTemp))
                                    |> emit(LoadConstInt(wordTemp + 1)(1))
                                    |> emit(ShrInt(wordTemp + 2)(wordTemp)(wordTemp + 1))
                                    |> emit(AndInt(wordTemp + 3)(wordTemp + 2)(wordTemp + 1))
                                    |> emit(JumpIfFalse(wordTemp + 3)(skipLabel))
                                    |> emit(IsReferenceCounted(wordTemp + 4)(resultTemp))
                                    |> emit(JumpIfFalse(wordTemp + 4)(copyLabel))
                                    |> emit(Jump(skipLabel))
                                    |> emit(Label(copyLabel))
                                    |> emitArenaCopiesOfOwnedParts(resultTemp)(elements)(parts)(origin)(slots)
                                    |> emit(Label(skipLabel))
        | _ -> state

let handOverTupleParts (label: Str) (resultTemp: Int) (resultType: SemanticType) (parts: List(Int)) (scalars: List(Int)) origin registered kept (state: CoreLoweringState) =
    match tuplePartsHandOverPlan(resultTemp)(resultType)(parts)(scalars)(registered)(kept)(state) with
        | (handedOver, spliced, cellsKnown, normalized) ->
            match freshLabel("tuple_parts_not_requested")(spliced) with
                | FreshLabel { state = keepState, label = keepLabel } ->
                    match freshLabel("tuple_parts_handed_over")(keepState) with
                        | FreshLabel { state = doneState, label = doneLabel } ->
                            doneState
                            |> emitJumpUnlessTuplePartsRequestedOf(resultTemp)(keepLabel)
                            |> (given (requested: CoreLoweringState) ->
                                if cellsKnown
                                then requested
                                else
                                    match emitTuplePartRetainsReading(resultTemp)(parts)([])(requested) with
                                        | (retained, partTemps) ->
                                            emitOwnedSlotDropsBehindRetainedParts(origin)(ownedSlotsBesideKept(kept)(registered))(partTemps)(retained))
                            |> emitReferenceCountedPartsOfArenaResult(resultTemp)(resultType)(normalized)
                            |> emitTupleResultPartsMask(parts)
                            |> emitGeneralRcOwnedSlotDropsOf(origin)(handedOver)
                            |> emit(Jump(doneLabel))
                            |> emit(Label(keepLabel))
                            |> emitArenaCopiesOfOwnedPartsWhenRequested(resultTemp)(resultType)(parts)(origin)(ownedSlotsBesideKept(kept)(registered))
                            |> emitGeneralRcOwnedSlotDropsOf(origin)(kept)
                            |> emit(Label(doneLabel))
                            |> (given (finished: CoreLoweringState) ->
                                withStateTupleResultPartLabels(finished
                                |> stateTupleResultPartLabels
                                |> Ashes.Collection.Map.setStr(label)(parts))(finished))

let releaseOwnedSlotsHandingOverTupleParts (label: Str) (resultTemp: Int) (resultType: SemanticType) (parts: List(Int)) (scalars: List(Int)) (state: CoreLoweringState) =
    (let origin = stateActiveFunctionOrigin(state)
    in
        let registered =
            state
            |> stateGeneralRcOwnedSlots
            |> reverse
        in
            let kept =
                ownedSlotsOutsideResult(resultType)(resultReachedSlots(resultTemp)(state))(registered)(state)
            in
                match forwardTupleResultPartsRequest(resultTemp)(parts)(state) with
                    | Some(forwarded) ->
                        forwarded
                        |> emitGeneralRcOwnedSlotDropsOf(origin)(kept)
                        |> (given (finished: CoreLoweringState) ->
                            withStateTupleResultPartLabels(finished
                            |> stateTupleResultPartLabels
                            |> Ashes.Collection.Map.setStr(label)(parts))(finished))
                    | None -> handOverTupleParts(label)(resultTemp)(resultType)(parts)(scalars)(origin)(registered)(kept)(state))

// The return of a function whose result is outside the contract's types and holds no part of
// a generic tuple: an independent copy, a move onto the heap, or the owned slots out of reach.
// The owned slots the result may be as it is (stage 0's `WholeResultCandidates`): the active
// function's slots outside the releasable ones, none of them while a loop is being lowered.
let recursive wholeResultCandidates (origin: Maybe(IrFunctionOrigin)) (releasable: List((Maybe(IrFunctionOrigin), Int, SemanticType))) (slots: List((Maybe(IrFunctionOrigin), Int, SemanticType))) =
    match slots with
        | [] -> []
        | (owner, slot, _semanticType) :: rest ->
            if owner == origin && !ownedSlotNumberIn(slot)(releasable)
            then slot :: wholeResultCandidates(origin)(releasable)(rest)
            else wholeResultCandidates(origin)(releasable)(rest)
and ownedSlotNumberIn (slot: Int) (slots: List((Maybe(IrFunctionOrigin), Int, SemanticType))) =
    match slots with
        | [] -> false
        | (_owner, candidate, _semanticType) :: rest -> candidate == slot || ownedSlotNumberIn(slot)(rest)

// Stage 0's `EmitHandOverIfSame`: when the held value is the candidate, the copy `moveInto` makes,
// the mask, the releasable slots' release, and the jump to `doneLabel`.
let emitHandOverIfSame (heldTemp: Int) (candidateTemp: Int) (origin: Maybe(IrFunctionOrigin)) (releasable: List((Maybe(IrFunctionOrigin), Int, SemanticType))) (doneLabel: Str) (moveInto: CoreLoweringState -> CoreLoweringState) (state: CoreLoweringState) =
    match freshLabel("whole_result_other_value")(state) with
        | FreshLabel { state = labelled, label = nextLabel } ->
            match freshTemp(labelled) with
                | FreshTemp { state = allocated, temp = sameTemp } ->
                    allocated
                    |> emit(CmpIntEq(sameTemp)(heldTemp)(candidateTemp))
                    |> emit(JumpIfFalse(sameTemp)(nextLabel))
                    |> moveInto
                    |> emitResultHandOverMask(wholeResultHandedOverBit)
                    |> emitGeneralRcOwnedSlotDropsOf(origin)(releasable)
                    |> emit(Jump(doneLabel))
                    |> emit(Label(nextLabel))

// Each candidate slot's test against the held value.
let recursive emitWholeResultCandidateTests (heldTemp: Int) (candidates: List(Int)) (origin: Maybe(IrFunctionOrigin)) (releasable: List((Maybe(IrFunctionOrigin), Int, SemanticType))) (doneLabel: Str) (moveInto: CoreLoweringState -> CoreLoweringState) (state: CoreLoweringState) =
    match candidates with
        | [] -> state
        | slot :: rest ->
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = valueTemp } ->
                    allocated
                    |> emit(LoadLocal(valueTemp)(slot))
                    |> emitHandOverIfSame(heldTemp)(valueTemp)(origin)(releasable)(doneLabel)(moveInto)
                    |> emitWholeResultCandidateTests(heldTemp)(rest)(origin)(releasable)(doneLabel)(moveInto)

let recursive copyWrappedResultFields (resultTemp: Int) (copyTemp: Int) (field: Int) (arity: Int) (tagless: Bool) (state: CoreLoweringState) =
    if field >= arity
    then state
    else
        match freshTemp(state) with
            | FreshTemp { state = allocated, temp = valueTemp } ->
                allocated
                |> emit(GetAdtField(valueTemp)(resultTemp)(field)(tagless))
                |> emit(SetAdtField(copyTemp)(field)(valueTemp)(tagless))
                |> copyWrappedResultFields(resultTemp)(copyTemp)(field + 1)(arity)(tagless)

// Stage 0's `EmitWrappedResultCopy`: the arena cell copied onto the reference-counted heap field
// by field, the copy becoming the result.
let emitWrappedResultCopy (resultTemp: Int) (resultSlot: Int) (wrapper: (Int, Int, Bool, Int)) (state: CoreLoweringState) =
    match (wrapper, freshTemp(state)) with
        | ((tag, arity, tagless, _contractField), FreshTemp { state = allocated, temp = copyTemp }) ->
            allocated
            |> emit(AllocAdt(copyTemp)(tag)(arity)(true)(tagless))
            |> copyWrappedResultFields(resultTemp)(copyTemp)(0)(arity)(tagless)
            |> emit(StoreLocal(resultSlot)(copyTemp))

let emitWrappedResultTagTest (resultTemp: Int) (tag: Int) (tagless: Bool) (otherLabel: Str) (state: CoreLoweringState) =
    if tagless
    then state
    else
        match freshTempRun(3)(state) with
            | FreshTemp { state = allocated, temp = tagTemp } ->
                allocated
                |> emit(GetAdtTag(tagTemp)(resultTemp))
                |> emit(LoadConstInt(tagTemp + 1)(tag))
                |> emit(CmpIntEq(tagTemp + 2)(tagTemp)(tagTemp + 1))
                |> emit(JumpIfFalse(tagTemp + 2)(otherLabel))

// Stage 0's `EmitWrappedResultHandOver`: an arena result of the wrapper's constructor whose
// contract field holds an owned slot's reference-counted value is copied onto the
// reference-counted heap, the slot's reference moving into the copy, which becomes the result.
let emitWrappedResultHandOver (resultTemp: Int) (resultSlot: Int) (candidates: List(Int)) (origin: Maybe(IrFunctionOrigin)) (releasable: List((Maybe(IrFunctionOrigin), Int, SemanticType))) (keepLabel: Str) (doneLabel: Str) (wrapper: (Int, Int, Bool, Int)) (state: CoreLoweringState) =
    match (wrapper, freshLabel("whole_result_other_constructor")(state)) with
        | ((tag, _arity, tagless, contractField), FreshLabel { state = labelled, label = otherLabel }) ->
            match labelled
            |> emitSkipUnlessPresent(resultTemp)(keepLabel)
            |> emitWrappedResultTagTest(resultTemp)(tag)(tagless)(otherLabel)
            |> freshTemp with
                | FreshTemp { state = allocated, temp = fieldTemp } ->
                    allocated
                    |> emit(GetAdtField(fieldTemp)(resultTemp)(contractField)(tagless))
                    |> emitSkipUnlessReferenceCounted(fieldTemp)(otherLabel)
                    |> emitWholeResultCandidateTests(fieldTemp)(candidates)(origin)(releasable)(doneLabel)(emitWrappedResultCopy(resultTemp)(resultSlot)(wrapper))
                    |> emit(Label(otherLabel))

let recursive emitWrappedResultHandOvers (resultTemp: Int) (resultSlot: Int) (candidates: List(Int)) (origin: Maybe(IrFunctionOrigin)) (releasable: List((Maybe(IrFunctionOrigin), Int, SemanticType))) (keepLabel: Str) (doneLabel: Str) (wrappers: List((Int, Int, Bool, Int))) (state: CoreLoweringState) =
    match wrappers with
        | [] -> state
        | wrapper :: rest ->
            state
            |> emitWrappedResultHandOver(resultTemp)(resultSlot)(candidates)(origin)(releasable)(keepLabel)(doneLabel)(wrapper)
            |> emitWrappedResultHandOvers(resultTemp)(resultSlot)(candidates)(origin)(releasable)(keepLabel)(doneLabel)(rest)

let isScalarFieldType (semanticType: SemanticType) =
    match semanticType with
        | SemInt -> true
        | SemUInt(_bits) -> true
        | SemFloat -> true
        | SemBool -> true
        | SemRune -> true
        | _ -> false

// The one contract field of a constructor's fields when every other one is a scalar: its index,
// -1 when it has none, -2 when another field rules the constructor out.
let recursive wrappedContractField (fields: List(SemanticType)) (index: Int) (contractField: Int) (state: CoreLoweringState) =
    match fields with
        | [] -> contractField
        | fieldType :: rest ->
            if isGeneralRcValueType(fieldType)(state) && contractField < 0
            then wrappedContractField(rest)(index + 1)(index)(state)
            else
                if isScalarFieldType(fieldType)
                then wrappedContractField(rest)(index + 1)(contractField)(state)
                else -2

let layoutInstantiatedFieldTypes (layout: CoreConstructorLayout) (arguments: List(SemanticType)) (state: CoreLoweringState) =
    match layoutFieldTypes(layout)(state) with
        | (layoutFields, SemNamed(_symbolId, _name, variables)) ->
            layoutFields
            |> map(arguments
            |> typeVariablePairs(variables)
            |> replaceTypeVariables)
            |> map(resolveType(state))
        | (layoutFields, _resultType) ->
            layoutFields |> map(resolveType(state))

let recursive insertWrapperByTag (wrapper: (Int, Int, Bool, Int)) (wrappers: List((Int, Int, Bool, Int))) =
    match (wrapper, wrappers) with
        | (_wrapper, []) -> [wrapper]
        | ((tag, _arity, _tagless, _field), (otherTag, otherArity, otherTagless, otherField) :: rest) ->
            if tag <= otherTag
            then wrapper :: wrappers
            else (otherTag, otherArity, otherTagless, otherField) :: insertWrapperByTag(wrapper)(rest)

let recursive collectWrappedResultConstructors (typeName: Str) (arguments: List(SemanticType)) (layouts: List(CoreConstructorLayout)) (state: CoreLoweringState) (wrappers: List((Int, Int, Bool, Int))) =
    match layouts with
        | [] -> wrappers
        | layout :: rest ->
            if constructorResultNameIs(typeName)(layout)
            then
                let fields = layoutInstantiatedFieldTypes(layout)(arguments)(state)
                in
                    let contractField = wrappedContractField(fields)(0)(-1)(state)
                    in
                        if contractField >= 0
                        then
                            match layout with
                                | CoreConstructorLayout { tag = tag, tagless = tagless } ->
                                    wrappers
                                    |> insertWrapperByTag((tag, length(fields), tagless, contractField))
                                    |> collectWrappedResultConstructors(typeName)(arguments)(rest)(state)
                        else collectWrappedResultConstructors(typeName)(arguments)(rest)(state)(wrappers)
            else collectWrappedResultConstructors(typeName)(arguments)(rest)(state)(wrappers)

// Stage 0's `WrappedResultConstructors`: the constructors of a named result type whose cell can
// wrap an owned value, by tag: exactly one field of a contract type, every other field a scalar.
let wrappedResultConstructors (resultType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(resultType) with
        | SemNamed(_symbolId, name, arguments) ->
            collectWrappedResultConstructors(name)(arguments)(stateConstructorsOfType(name)(state))(state)([])
        | _ -> []

// Whether a call's ownership word forwards this function's own caller's request: the function's
// ownership word masked with -4 flows into it.
let recursive wordForwardsRequest (word: Int) (budget: Int) (state: CoreLoweringState) =
    if budget == 0 || word < 0
    then false
    else
        match definitionOfTemp(word)(state.reversedInstructions) with
            | Some(OrInt(_target, left, right)) -> wordForwardsRequest(left)(budget - 1)(state) || wordForwardsRequest(right)(budget - 1)(state)
            | Some(AndInt(_target, left, right)) ->
                match (definitionOfTemp(left)(state.reversedInstructions), definitionOfTemp(right)(state.reversedInstructions)) with
                    | (Some(LoadArgumentOwnership(_word)), Some(LoadConstInt(_constant, value))) -> value == -4
                    | _ -> false
            | _ -> false

// Stage 0's `_forwardedResultCallTemp`: the call a result of unresolved type returned as it is
// forwarded this function's caller's request to; -1 when there is none.
let forwardedResultCall (resultTemp: Int) (resultType: SemanticType) (state: CoreLoweringState) =
    if !isGenericResultForwardedAsIs(resultType)(state)
    then -1
    else
        let callTemp =
            soleCallSourceThroughLocals(resultTemp)(16)(reverse(state.reversedInstructions))(state)
        in
            if callTemp < 0
            then -1
            else
                match callWordOf(callTemp)(state.reversedInstructions) with
                    | Some(word) ->
                        if wordForwardsRequest(word)(8)(state)
                        then callTemp
                        else -1
                    | None -> -1

let recursive ownedSlotsNotIn (origin: Maybe(IrFunctionOrigin)) (excluded: List((Maybe(IrFunctionOrigin), Int, SemanticType))) (slots: List((Maybe(IrFunctionOrigin), Int, SemanticType))) =
    match slots with
        | [] -> []
        | ((owner, slot, _semanticType) as owned) :: rest ->
            if owner == origin && !anyOwnedSlotIs(origin)(slot)(excluded)
            then owned :: ownedSlotsNotIn(origin)(excluded)(rest)
            else ownedSlotsNotIn(origin)(excluded)(rest)

let emitIndependentForwardedResultDrops (behindCall: List((Maybe(IrFunctionOrigin), Int, SemanticType))) (origin: Maybe(IrFunctionOrigin)) (state: CoreLoweringState) =
    match freshLabel("forwarded_result_not_independent")(state) with
        | FreshLabel { state = labelled, label = skipLabel } ->
            match labelled
            |> emitJumpUnlessTuplePartsRequested(skipLabel)
            |> freshTempRun(8) with
                | FreshTemp { state = allocated, temp = wordTemp } ->
                    allocated
                    |> emit(LoadArgumentOwnership(wordTemp))
                    |> emit(LoadConstInt(wordTemp + 1)(-8))
                    |> emit(AndInt(wordTemp + 2)(wordTemp)(wordTemp + 1))
                    |> emit(LoadMemOffset(wordTemp + 3)(wordTemp + 2)(0))
                    |> emit(LoadConstInt(wordTemp + 4)(independentResultBit + wholeResultHandedOverBit))
                    |> emit(AndInt(wordTemp + 5)(wordTemp + 3)(wordTemp + 4))
                    |> emit(LoadConstInt(wordTemp + 6)(0))
                    |> emit(CmpIntNe(wordTemp + 7)(wordTemp + 5)(wordTemp + 6))
                    |> emit(JumpIfFalse(wordTemp + 7)(skipLabel))
                    |> emitGeneralRcOwnedSlotDropsOf(origin)(behindCall)
                    |> emit(Label(skipLabel))

// Stage 0's `ReleaseOwnedSlotsBehindIndependentForwardedResult`: after a forwarded call whose
// result is returned as it is, the owned slots out of the result's reach once that call's result is
// independent of its arguments, but not otherwise, are released when this function was asked and
// the callee's mask says the result is independent.
let releaseOwnedSlotsBehindIndependentForwardedResult (resultTemp: Int) (resultType: SemanticType) (registered: List((Maybe(IrFunctionOrigin), Int, SemanticType))) (releasable: List((Maybe(IrFunctionOrigin), Int, SemanticType))) (state: CoreLoweringState) =
    match stateTcoLoopFrame(state) with
        | Some(_frame) -> state
        | None ->
            let callTemp = forwardedResultCall(resultTemp)(resultType)(state)
            in
                if callTemp < 0
                then state
                else
                    let reached =
                        localSlotsReachingTempsWithAliases([resultTemp])(independentOwnedSlots(state))([callTemp])(aliasOwnerSlotPairs(state))(state.reversedInstructions)
                    in
                        match registered
                        |> ownedSlotsOutsideResult(resultType)(reached)
                        |> (given (outside) -> outside(state))
                        |> ownedSlotsNotIn(stateActiveFunctionOrigin(state))(releasable) with
                            | [] -> state
                            | behindCall ->
                                emitIndependentForwardedResultDrops(behindCall)(stateActiveFunctionOrigin(state))(state)

// Stage 0's `EmitRuntimeManagedShallowAggregateDrop`: a non-null aggregate cell is released
// without walking its children.
let emitRuntimeShallowAggregateDrop (valueTemp: Int) (typeName: Str) (state: CoreLoweringState) =
    match freshLabel("rcdrop_shallow_end")(state) with
        | FreshLabel { state = labelled, label = endLabel } ->
            match freshTempRun(2)(labelled) with
                | FreshTemp { state = reserved, temp = zeroTemp } ->
                    reserved
                    |> emit(LoadConstInt(zeroTemp)(0))
                    |> emit(CmpIntNe(zeroTemp + 1)(valueTemp)(zeroTemp))
                    |> emit(JumpIfFalse(zeroTemp + 1)(endLabel))
                    |> emit(RcDrop(valueTemp)(typeName)(-1)(true)(false)(None))
                    |> emit(Label(endLabel))

// Stage 0's `EmitRuntimeManagedChildPreservingDrop` for a kept owned value: a list gives up its
// spine, an aggregate its own cell, and a value with no parts is released with its children.
let emitKeptValueChildPreservingDrop (valueTemp: Int) (valueType: SemanticType) (state: CoreLoweringState) =
    match valueType with
        | SemList(_element) -> emitRuntimeListSpineDrop(valueTemp)(state)
        | SemTuple(_elements) -> emitRuntimeShallowAggregateDrop(valueTemp)("Tuple")(state)
        | SemNamed(_symbolId, name, _arguments) ->
            emitRuntimeShallowAggregateDrop(valueTemp)(runtimeManagedAdtTypeName(name))(state)
        | other -> emitOwnedValueChildDrop(valueTemp)(other)(state)

let recursive containsHandedOverPartsSlot (owner: Maybe(IrFunctionOrigin)) (slot: Int) (slots: List((Maybe(IrFunctionOrigin), Int))) =
    match slots with
        | [] -> false
        | (candidateOwner, candidate) :: rest -> candidate == slot && candidateOwner == owner || containsHandedOverPartsSlot(owner)(slot)(rest)

// Stage 0's `EmitKeptOwnedSlotChildPreservingDrops`: a handed-over argument kept in an owned slot
// for its parts, that the result reaches but whose type the result's has no place for, has its own
// cells released here, child-preserving, as the call would have released them had it not kept the
// value for the result; the parts keep the one reference the result relies on. Any other owned
// slot is left as it is: its value may be a cell a reuse token already took for the result.
let recursive emitKeptOwnedSlotChildPreservingDrops (resultType: SemanticType) (kept: List((Maybe(IrFunctionOrigin), Int, SemanticType))) (state: CoreLoweringState) =
    match kept with
        | [] -> state
        | (owner, slot, semanticType) :: rest ->
            match resolveType(state)(semanticType) with
                | SemFunction(_parameter, _result, _row) -> emitKeptOwnedSlotChildPreservingDrops(resultType)(rest)(state)
                | valueType ->
                    if !containsHandedOverPartsSlot(owner)(slot)(stateGeneralRcHandedOverPartsSlots(state)) || containsUnresolvedLayout(valueType)(state) || callResultMayContainArgumentType(resultType)(valueType)([])(state)
                    then emitKeptOwnedSlotChildPreservingDrops(resultType)(rest)(state)
                    else
                        match freshTemp(state) with
                            | FreshTemp { state = valueState, temp = valueTemp } ->
                                match valueState
                                |> emit(LoadLocal(valueTemp)(slot))
                                |> freshTemp with
                                    | FreshTemp { state = zeroState, temp = zeroTemp } ->
                                        match zeroState
                                        |> emit(LoadConstInt(zeroTemp)(0))
                                        |> freshTemp with
                                            | FreshTemp { state = presentState, temp = presentTemp } ->
                                                match presentState
                                                |> emit(CmpIntNe(presentTemp)(valueTemp)(zeroTemp))
                                                |> freshLabel("general_rc_kept_absent") with
                                                    | FreshLabel { state = labelled, label = skipLabel } ->
                                                        match labelled
                                                        |> emit(JumpIfFalse(presentTemp)(skipLabel))
                                                        |> freshTemp with
                                                            | FreshTemp { state = testedState, temp = referenceCountedTemp } ->
                                                                testedState
                                                                |> emit(IsReferenceCounted(referenceCountedTemp)(valueTemp))
                                                                |> emit(JumpIfFalse(referenceCountedTemp)(skipLabel))
                                                                |> emitKeptValueChildPreservingDrop(valueTemp)(valueType)
                                                                |> emit(StoreLocal(slot)(zeroTemp))
                                                                |> emit(Label(skipLabel))
                                                                |> emitKeptOwnedSlotChildPreservingDrops(resultType)(rest)

// Stage 0's `TryHandOverOwnedResultWhole` behind a result outside the contract's types: the owned
// slots out of the result's reach are released, and when the caller asked and the result is, at
// run time, the reference-counted value one of the other owned slots holds, or an arena cell of a
// wrapping constructor around one, that slot's reference is handed over whole with the mask
// instead of being left. The state and the temp to return.
// Whether an owned slot was stored inside a loop (it has a fresh flag).
let recursive hasFreshSlot (origin: Maybe(IrFunctionOrigin)) (slot: Int) (fresh: List((Maybe(IrFunctionOrigin), Int, Int))) =
    match fresh with
        | [] -> false
        | (owner, candidate, _flag) :: rest -> owner == origin && candidate == slot || hasFreshSlot(origin)(slot)(rest)

let noteTakenOverRecordOfFunction (state: CoreLoweringState) =
    match stateActiveFunctionOrigin(state) with
        | Some(IrFunctionOrigin { sourceOrigin = Some(SourceFunctionOrigin { functionSourceName = name }) }) -> withStateTakenOverRecordNames(name :: stateTakenOverRecordNames(state))(state)
        | _ -> state

// Stage 0's `EmitTakeOverSpineRelease` without a loop: a present reference-counted list gives up its
// cells and the slot is emptied.
let emitTakeOverSpineReleaseAtReturn (slot: Int) (state: CoreLoweringState) =
    match freshLabel("taken_over_skip")(state) with
        | FreshLabel { state = labelled, label = skipLabel } ->
            match freshTemp(labelled) with
                | FreshTemp { state = allocated, temp = valueTemp } ->
                    allocated
                    |> emit(LoadLocal(valueTemp)(slot))
                    |> emitSkipUnlessPresent(valueTemp)(skipLabel)
                    |> emitSkipUnlessReferenceCounted(valueTemp)(skipLabel)
                    |> emitRuntimeListSpineDrop(valueTemp)
                    |> (given (dropped: CoreLoweringState) ->
                        match freshTemp(dropped) with
                            | FreshTemp { state = zeroState, temp = zeroTemp } ->
                                zeroState
                                |> emit(LoadConstInt(zeroTemp)(0))
                                |> emit(StoreLocal(slot)(zeroTemp))
                                |> emit(Label(skipLabel)))
                    |> noteTakenOverRecordOfFunction
                    |> noteSpineFreedSlot(slot)

// Stage 0's `TryTakeOverAtReturn`: a recursive group member has no back edge, so an owned list kept
// behind a result of unresolved layout gives up its cells at the return.
let recursive takeOverAtReturn (resultType: SemanticType) (kept: List((Maybe(IrFunctionOrigin), Int, SemanticType))) (state: CoreLoweringState) =
    match kept with
        | [] -> state
        | (owner, slot, semanticType) :: rest ->
            if containsUnresolvedLayout(resultType)(state) && !hasFreshSlot(owner)(slot)(stateGeneralRcFreshSlots(state)) && isTakeOverCandidateList(semanticType)(state) && takenOverConsumerAccepts(owner)(slot)(stateTakenOverConsumers(state))
            then
                state
                |> emitTakeOverSpineReleaseAtReturn(slot)
                |> takeOverAtReturn(resultType)(rest)
            else takeOverAtReturn(resultType)(rest)(state)

let emitOwnedSlotDropsOrWholeHandOver (label: Str) (resultTemp: Int) (resultType: SemanticType) (state: CoreLoweringState) =
    (let origin = stateActiveFunctionOrigin(state)
    in
        let registered =
            state
            |> stateGeneralRcOwnedSlots
            |> reverse
        in
            let releasable =
                ownedSlotsOutsideResult(resultType)(resultReachedSlots(resultTemp)(state))(registered)(state)
            in
                let released =
                    state
                    |> releaseOwnedSlotsBehindIndependentForwardedResult(resultTemp)(resultType)(registered)(releasable)
                    |> takeOverAtReturn(resultType)(ownedSlotsNotIn(origin)(releasable)(registered))
                    |> emitKeptOwnedSlotChildPreservingDrops(resultType)(ownedSlotsNotIn(origin)(releasable)(registered))
                in
                    match (stateTcoLoopFrame(released), wholeResultCandidates(origin)(releasable)(registered)) with
                        | (None, first :: others) ->
                            let wrappers = wrappedResultConstructors(resultType)(released)
                            in
                                match freshLocal(released) with
                                    | FreshLocal { state = slotted, local = resultSlot } ->
                                        match slotted
                                        |> emit(StoreLocal(resultSlot)(resultTemp))
                                        |> freshLabel("whole_result_not_requested") with
                                            | FreshLabel { state = keepState, label = keepLabel } ->
                                                match freshLabel("whole_result_handed_over")(keepState) with
                                                    | FreshLabel { state = doneState, label = doneLabel } ->
                                                        match freshLabel("whole_result_arena")(doneState) with
                                                            | FreshLabel { state = arenaState, label = arenaLabel } ->
                                                                match arenaState
                                                                |> emitJumpUnlessTuplePartsRequested(keepLabel)
                                                                |> freshTemp with
                                                                    | FreshTemp { state = allocated, temp = countedTemp } ->
                                                                        match allocated
                                                                        |> emit(IsReferenceCounted(countedTemp)(resultTemp))
                                                                        |> emit(JumpIfFalse(countedTemp)(arenaLabel))
                                                                        |> emitWholeResultCandidateTests(resultTemp)(first :: others)(origin)(releasable)(doneLabel)(given (unchanged: CoreLoweringState) -> unchanged)
                                                                        |> emit(Jump(keepLabel))
                                                                        |> emit(Label(arenaLabel))
                                                                        |> emitWrappedResultHandOvers(resultTemp)(resultSlot)(first :: others)(origin)(releasable)(keepLabel)(doneLabel)(wrappers)
                                                                        |> emit(Label(keepLabel))
                                                                        |> emitGeneralRcOwnedSlotDropsOf(origin)(releasable)
                                                                        |> emit(Label(doneLabel))
                                                                        |> withWholeResultHandOverLabel(label)
                                                                        |> freshTemp with
                                                                            | FreshTemp { state = finished, temp = handedTemp } ->
                                                                                (emit(LoadLocal(handedTemp)(resultSlot))(finished), handedTemp)
                        | _ -> (emitGeneralRcOwnedSlotDropsOf(origin)(releasable)(released), resultTemp))

let finishGeneralRcFunctionResultOutsideContract (label: Str) (bodyTemp: Int) (bodyType: SemanticType) (state: CoreLoweringState) =
    match (functionOwnsGeneralRcSlots(state), independentResultCopyOf(bodyType)(state)) with
        | (true, Some(OwnedResultCopy(_plan))) when keepsUnreachedResultInPlace(bodyTemp)(bodyType)(state) -> emitOwnedSlotDropsOrWholeHandOver(label)(bodyTemp)(bodyType)(state)
        | (true, Some(ArenaDeepResultCopy(_type))) when keepsUnreachedResultInPlace(bodyTemp)(bodyType)(state) -> emitOwnedSlotDropsOrWholeHandOver(label)(bodyTemp)(bodyType)(state)
        | (true, Some(copy)) ->
            match emitIndependentResultCopy(copy)(bodyTemp)(state) with
                | (copied, resultTemp) -> (emitGeneralRcOwnedSlotDrops(copied), resultTemp)
        | (true, None) ->
            match ownedTupleResultPlan(bodyTemp)(bodyType)(state) with
                | Some(plan) ->
                    match emitGuardedDeepCopy(bodyTemp)(plan)(state) with
                        | (copied, resultTemp) ->
                            (copied
                            |> markRuntimeTemp(resultTemp)(RuntimeNewlyProduced)
                            |> emitGeneralRcOwnedSlotDrops, resultTemp)
                | None -> emitOwnedSlotDropsOrWholeHandOver(label)(bodyTemp)(bodyType)(state)
        | _ -> (state, bodyTemp)

let isArenaRecordCell (instruction: Maybe(IrInstructionKind)) =
    match instruction with
        | Some(AllocAdt(_target, _tag, _fieldCount, false, _tagless)) -> true
        | _ -> isArenaTupleCell(instruction)

// The one arena cell, a tuple or a record, stored into `slot` beside the back edge placeholder; -1
// otherwise.
let recursive soleArenaRecordCellStoredIn (slot: Int) (instructions: List(IrInstruction)) (all: List(IrInstruction)) (cell: Int) =
    match instructions with
        | [] -> cell
        | IrInstruction { instruction = StoreLocal(storeSlot, source) } :: rest ->
            if storeSlot != slot
            then soleArenaRecordCellStoredIn(slot)(rest)(all)(cell)
            else
                match definitionOfTemp(source)(all) with
                    | definition ->
                        if isBackEdgePlaceholder(definition)
                        then soleArenaRecordCellStoredIn(slot)(rest)(all)(cell)
                        else
                            if isArenaRecordCell(definition) && cell < 0
                            then soleArenaRecordCellStoredIn(slot)(rest)(all)(source)
                            else -1
        | _ :: rest -> soleArenaRecordCellStoredIn(slot)(rest)(all)(cell)

// Stage 0's `SoleArenaCellOf` with records allowed.
let soleArenaRecordCellOf (temp: Int) (state: CoreLoweringState) =
    match definitionOfTemp(temp)(state.reversedInstructions) with
        | Some(LoadLocal(_target, slot)) -> soleArenaRecordCellStoredIn(slot)(state.reversedInstructions)(state.reversedInstructions)(-1)
        | definition ->
            if isArenaRecordCell(definition)
            then temp
            else -1

let recursive isGeneralRcOwnedSlotIn (slot: Int) (ownedSlots: List((Maybe(IrFunctionOrigin), Int, SemanticType))) =
    match ownedSlots with
        | [] -> false
        | (_origin, candidate, _semanticType) :: rest -> candidate == slot || isGeneralRcOwnedSlotIn(slot)(rest)

let recursive retainStoredIntoOwnedSlot (temp: Int) (instructions: List(IrInstruction)) (ownedSlots: List((Maybe(IrFunctionOrigin), Int, SemanticType))) =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = StoreLocal(slot, source) } :: rest -> source == temp && isGeneralRcOwnedSlotIn(slot)(ownedSlots) || retainStoredIntoOwnedSlot(temp)(rest)(ownedSlots)
        | _ :: rest -> retainStoredIntoOwnedSlot(temp)(rest)(ownedSlots)

// A retain this function took that no owned slot releases.
let isUnparkedRetain (temp: Int) (state: CoreLoweringState) =
    match definitionOfTemp(temp)(state.reversedInstructions) with
        | Some(RcDup(_target, _source, _a, _b)) ->
            !retainStoredIntoOwnedSlot(temp)(state.reversedInstructions)(stateGeneralRcOwnedSlots(state))
        | _ -> false

let isArenaNothing (instruction: Maybe(IrInstructionKind)) =
    match instruction with
        | Some(AllocAdt(_target, _tag, 0, false, _tagless)) -> true
        | _ -> isBackEdgePlaceholder(instruction)

// Whether every store into `slot` is an unparked retain or an arena nullary value, with at least
// one of the former: 1 owned, 0 none seen, -1 another store.
let recursive slotStoresOwnReferences (slot: Int) (instructions: List(IrInstruction)) (seen: Int) (state: CoreLoweringState) =
    match instructions with
        | [] -> seen
        | IrInstruction { instruction = StoreLocal(storeSlot, source) } :: rest ->
            if storeSlot != slot || isArenaNothing(definitionOfTemp(source)(state.reversedInstructions))
            then slotStoresOwnReferences(slot)(rest)(seen)(state)
            else
                if isUnparkedRetain(source)(state)
                then slotStoresOwnReferences(slot)(rest)(1)(state)
                else -1
        | _ :: rest -> slotStoresOwnReferences(slot)(rest)(seen)(state)

// Stage 0's `HoldsOwnReference`: a join holding a reference this function took on every arm, a
// retain no owned slot releases or a fresh arena value that owns nothing. A join with a borrowed
// arm does not.
let holdsOwnReference (temp: Int) (state: CoreLoweringState) =
    match definitionOfTemp(temp)(state.reversedInstructions) with
        | Some(LoadLocal(_target, slot)) -> slotStoresOwnReferences(slot)(state.reversedInstructions)(0)(state) == 1
        | _ -> false

let recursive ownedRecordFieldIndices (cell: Int) (fieldTypes: List(SemanticType)) (instructions: List(IrInstruction)) (state: CoreLoweringState) =
    match instructions with
        | [] -> []
        | IrInstruction { instruction = SetAdtField(ptr, index, source, tagless) } :: rest ->
            if ptr == cell && index < length(fieldTypes) && !resultSurvivesReset(nthElementType(index)(fieldTypes))(state) && holdsOwnReference(source)(state)
            then (index, nthElementType(index)(fieldTypes), tagless) :: ownedRecordFieldIndices(cell)(fieldTypes)(rest)(state)
            else ownedRecordFieldIndices(cell)(fieldTypes)(rest)(state)
        | _ :: rest -> ownedRecordFieldIndices(cell)(fieldTypes)(rest)(state)

let recursive releaseRecordFieldsAt (bodyTemp: Int) (fields: List((Int, SemanticType, Bool))) (state: CoreLoweringState) =
    match fields with
        | [] -> state
        | (index, fieldType, tagless) :: rest ->
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = childTemp } ->
                    allocated
                    |> emit(GetAdtField(childTemp)(bodyTemp)(index)(tagless))
                    |> emitOwnedValueChildDrop(childTemp)(fieldType)
                    |> releaseRecordFieldsAt(bodyTemp)(rest)

let recursive soleConstructorFieldTypes (typeName: Str) (arguments: List(SemanticType)) (layouts: List(CoreConstructorLayout)) (found: Maybe(List(SemanticType))) (count: Int) (state: CoreLoweringState) =
    match layouts with
        | [] ->
            if count == 1
            then found
            else None
        | layout :: rest ->
            if constructorResultNameIs(typeName)(layout)
            then
                soleConstructorFieldTypes(typeName)(arguments)(rest)(state
                |> layoutInstantiatedFieldTypes(layout)(arguments)
                |> Some)(count + 1)(state)
            else soleConstructorFieldTypes(typeName)(arguments)(rest)(found)(count)(state)

// Stage 0's `ReleaseOwnedFieldsOfCopiedArenaRecord`: the same move for an arena record of one
// constructor. A field joining retains no owned slot releases with arena nullary values moves into
// the reference-counted copy; a field any arm of which is borrowed keeps the copy's retain.
let releaseOwnedFieldsOfCopiedArenaRecord (bodyTemp: Int) (bodyType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(bodyType) with
        | SemNamed(_symbolId, name, arguments) ->
            match soleConstructorFieldTypes(name)(arguments)(stateConstructorsOfType(name)(state))(None)(0)(state) with
                | Some(fieldTypes) ->
                    let cell = soleArenaRecordCellOf(bodyTemp)(state)
                    in
                        if cell < 0
                        then state
                        else
                            releaseRecordFieldsAt(bodyTemp)(ownedRecordFieldIndices(cell)(fieldTypes)(reverse(state.reversedInstructions))(state))(state)
                | None -> state
        | _ -> state

// Whether an owned slot of this function is a part duplicate's (stage 0's `PartDuplicate`).
let isPartDuplicateSlot (origin: Maybe(IrFunctionOrigin)) (slot: Int) (state: CoreLoweringState) =
    state
    |> stateGeneralRcPartDuplicateSlots
    |> containsHandedOverPartsSlot(origin)(slot)

let recursive anyOtherOwnedSlotOf (origin: Maybe(IrFunctionOrigin)) (slots: List((Maybe(IrFunctionOrigin), Int, SemanticType))) (state: CoreLoweringState) =
    match slots with
        | [] -> false
        | (owner, slot, _semanticType) :: rest -> owner == origin && !isPartDuplicateSlot(origin)(slot)(state) || anyOtherOwnedSlotOf(origin)(rest)(state)

let recursive withoutPartDuplicateSlots (origin: Maybe(IrFunctionOrigin)) (slots: List((Maybe(IrFunctionOrigin), Int, SemanticType))) (state: CoreLoweringState) =
    match slots with
        | [] -> []
        | (owner, slot, semanticType) :: rest ->
            if owner == origin && isPartDuplicateSlot(origin)(slot)(state)
            then withoutPartDuplicateSlots(origin)(rest)(state)
            else (owner, slot, semanticType) :: withoutPartDuplicateSlots(origin)(rest)(state)

let recursive leaveWithdrawnPartDuplicatesToTheirCells (origin: Maybe(IrFunctionOrigin)) (slots: List((Maybe(IrFunctionOrigin), Int, SemanticType))) (state: CoreLoweringState) =
    match slots with
        | [] -> state
        | (owner, slot, _semanticType) :: rest ->
            if owner == origin && isPartDuplicateSlot(origin)(slot)(state)
            then leaveWithdrawnPartDuplicatesToTheirCells(origin)(rest)((state with reversedInstructions = leaveWithdrawnPartDuplicateToItsCell(slot)(state.reversedInstructions)))
            else leaveWithdrawnPartDuplicatesToTheirCells(origin)(rest)(state)

// Stage 0's `DropPartDuplicateSlotsWhenTheResultCopiesAnyway`: a result its own rules copy or
// normalize anyway (a contract type, a reference-counted result, another owned value) settles a
// part's duplicate by those rules, so the part's slot is withdrawn; only a result nothing else copies
// keeps it, which makes the return copy the result and release the slot behind the copy. A withdrawn
// slot nothing reads has its store take the borrowed value, so the cell is the duplicate's only reader.
let dropPartDuplicateSlotsWhenTheResultCopiesAnyway (bodyTemp: Int) (bodyType: SemanticType) (state: CoreLoweringState) =
    (let origin = stateActiveFunctionOrigin(state)
    in
        if isGeneralRcValueType(bodyType)(state) || isRuntimeTemp(bodyTemp)(state) || anyOtherOwnedSlotOf(origin)(stateGeneralRcOwnedSlots(state))(state)
        then
            state
            |> leaveWithdrawnPartDuplicatesToTheirCells(origin)(stateGeneralRcOwnedSlots(state))
            |> (given (settled: CoreLoweringState) ->
                withStateGeneralRcOwnedSlots(withoutPartDuplicateSlots(origin)(stateGeneralRcOwnedSlots(settled))(settled))(settled))
        else state)

let finishGeneralRcFunctionResultWithoutPartSlots (producedOwned: Bool) (label: Str) (bodyTemp: Int) (bodyType: SemanticType) (state: CoreLoweringState) =
    if isGeneralRcValueType(bodyType)(state)
    then
        match generalCopyPlanOf(bodyType)(state) with
            | None -> (state, bodyTemp)
            | Some(plan) ->
                match if producedOwned
                then emitOwnedResultOrCopy(bodyTemp)(plan)(state)
                else
                    match emitGuardedDeepCopy(bodyTemp)(plan)(state) with
                        | (copied, copyTemp) ->
                            (copied
                            |> releaseFreshChildrenOfCopiedArenaTuple(bodyTemp)(bodyType)
                            |> releaseOwnedFieldsOfCopiedArenaRecord(bodyTemp)(bodyType), copyTemp) with
                    | (copied, resultTemp) ->
                        (copied
                        |> markRuntimeTemp(resultTemp)(RuntimeNewlyProduced)
                        |> (given (marked: CoreLoweringState) -> withStateGeneralRcOwnedResultLabels(label :: stateGeneralRcOwnedResultLabels(marked))(marked))
                        |> emitIndependentResultSignal
                        |> emitGeneralRcOwnedSlotDrops, resultTemp)
    else
        if resultSurvivesReset(bodyType)(state) || isRuntimeTemp(bodyTemp)(state)
        then (emitGeneralRcOwnedSlotDrops(state), bodyTemp)
        else
            match tupleResultParts(bodyType)(state) with
                | [] ->
                    match state
                    |> forwardTupleResultPartsRequestThroughGenericResult(label)(bodyTemp)(bodyType)
                    |> finishGeneralRcFunctionResultOutsideContract(label)(bodyTemp)(bodyType) with
                        | (finished, resultTemp) -> (handOverListBuilderHeads(label)(resultTemp)(bodyType)(finished), resultTemp)
                | parts ->
                    (releaseOwnedSlotsHandingOverTupleParts(label)(bodyTemp)(bodyType)(parts)(tupleScalarElements(bodyType)(state))(state), bodyTemp)

let finishGeneralRcFunctionResultWithLayout (producedOwned: Bool) (label: Str) (bodyTemp: Int) (bodyType: SemanticType) (state: CoreLoweringState) =
    if isGeneralRcFinishedResult(bodyTemp)(state)
    then (state, bodyTemp)
    else
        state
        |> dropPartDuplicateSlotsWhenTheResultCopiesAnyway(bodyTemp)(bodyType)
        |> finishGeneralRcFunctionResultWithoutPartSlots(producedOwned)(label)(bodyTemp)(bodyType)

let finishGeneralRcFunctionResultAs (producedOwned: Bool) (label: Str) (bodyTemp: Int) (bodyType: SemanticType) (state: CoreLoweringState) =
    finishGeneralRcFunctionResultWithLayout(producedOwned)(label)(bodyTemp)(resolvePhantomNormalizedResultLayout(label)(resolvePhantomResultLayout(label)(bodyType)(state))(state))(state)

let tempIsProducedOwned (temp: Int) (state: CoreLoweringState) =
    match runtimeTempStateOf(temp)(state) with
        | Some(RuntimeNewlyProduced) -> true
        | _ -> false

let finishGeneralRcFunctionResult (label: Str) (bodyTemp: Int) (bodyType: SemanticType) (state: CoreLoweringState) = finishGeneralRcFunctionResultAs(tempIsProducedOwned(bodyTemp)(state) || isClosedProducerChain(bodyTemp)(state))(label)(bodyTemp)(bodyType)(state)

// Whether a result outside the contract's types is normalized into an owned value behind the
// function's owned slots, asked of the state the return finishes it in: the part duplicates'
// slots are withdrawn first there, and a result that reached the owned values only through them
// stays in place, so asking before the withdrawal promised callers a reference-counted result
// the return left in the arena.
let normalizesOutsideContractResult (bodyTemp: Int) (bodyType: SemanticType) (state: CoreLoweringState) =
    !resultSurvivesReset(bodyType)(state) && functionOwnsGeneralRcSlots(state) && (match independentResultCopyOf(bodyType)(state) with
        | Some(OwnedResultCopy(_plan)) -> !keepsUnreachedResultInPlace(bodyTemp)(bodyType)(state)
        | Some(ArenaResultCopy(_copyOut)) -> false
        | Some(ArenaDeepResultCopy(_type)) -> false
        | None ->
            match ownedTupleResultPlan(bodyTemp)(bodyType)(state) with
                | Some(_plan) -> true
                | None -> false)

// Whether the function's result is reference-counted once it is finished, the fact its callers
// close their call windows on: it was produced so, it is of the contract's types, or it is
// normalized into an owned value behind the function's owned slots.
let finishedResultRuntimeManaged (bodyTemp: Int) (bodyType: SemanticType) (state: CoreLoweringState) =
    isRuntimeTemp(bodyTemp)(state) || (match (isGeneralRcValueType(bodyType)(state), generalCopyPlanOf(bodyType)(state)) with
        | (true, Some(_plan)) -> true
        | _ -> false) || (state |> dropPartDuplicateSlotsWhenTheResultCopiesAnyway(bodyTemp)(bodyType) |> normalizesOutsideContractResult(bodyTemp)(bodyType))

// Stage 0's `LowerLambdaCoreNormalizeRequestedArenaResult`: a callee whose result is
// reference-counted can be invoked from a body that cannot own such a result (a generic function
// applying a closure parameter has no static layout for the result and its own caller later
// deep-copies the whole result out), so that call site asks for an arena result through bit 1 of
// the hidden ownership word, and the callee honors it here by deep-copying its result into the
// arena and releasing the original. Only result types the arena deep copy reproduces completely
// qualify.
let normalizeRequestedArenaResult (bodyTemp: Int) (bodyType: SemanticType) (state: CoreLoweringState) =
    if isRuntimeTemp(bodyTemp)(state) && canNormalizeResultIntoArena(bodyType)(state)
    then
        match freshTemp(state) with
            | FreshTemp { state = wordState, temp = wordTemp } ->
                match freshTemp(wordState) with
                    | FreshTemp { state = shiftState, temp = shiftTemp } ->
                        match freshTempRun(2)(shiftState) with
                            | FreshTemp { state = requestedState, temp = shiftedTemp } ->
                                match freshLocal(requestedState) with
                                    | FreshLocal { state = slotState, local = resultSlot } ->
                                        match freshLabel("rc_result_owned")(slotState) with
                                            | FreshLabel { state = ownedState, label = ownedLabel } ->
                                                match freshLabel("rc_result_done")(ownedState) with
                                                    | FreshLabel { state = doneState, label = doneLabel } ->
                                                        match doneState
                                                        |> emit(LoadArgumentOwnership(wordTemp))
                                                        |> emit(LoadConstInt(shiftTemp)(1))
                                                        |> emit(ShrInt(shiftedTemp)(wordTemp)(shiftTemp))
                                                        |> emit(AndInt(shiftedTemp + 1)(shiftedTemp)(shiftTemp))
                                                        |> emit(JumpIfFalse(shiftedTemp + 1)(ownedLabel))
                                                        |> emitArenaResultDeepCopy(bodyTemp)(bodyType) with
                                                            | (copied, copiedTemp) ->
                                                                match copied
                                                                |> emitArenaResultRelease(bodyTemp)(bodyType)
                                                                |> emit(StoreLocal(resultSlot)(copiedTemp))
                                                                |> emit(Jump(doneLabel))
                                                                |> emit(Label(ownedLabel))
                                                                |> emit(StoreLocal(resultSlot)(bodyTemp))
                                                                |> emit(Label(doneLabel))
                                                                |> freshTemp with
                                                                    | FreshTemp { state = resultState, temp = resultTemp } ->
                                                                        (emit(LoadLocal(resultTemp)(resultSlot))(resultState), resultTemp)
    else (state, bodyTemp)

// What a function does to its result before returning it: a result of the contract's types is
// made the function's own first, and a caller's request for an arena result is honored after.
let normalizeFunctionResult (label: Str) (bodyTemp: Int) (bodyType: SemanticType) (state: CoreLoweringState) =
    match finishGeneralRcFunctionResult(label)(bodyTemp)(bodyType)(state) with
        | (finished, ownedTemp) -> normalizeRequestedArenaResult(ownedTemp)(bodyType)(finished)
