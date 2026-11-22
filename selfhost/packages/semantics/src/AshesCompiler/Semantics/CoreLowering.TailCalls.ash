// Tail self-calls: successor arguments, predecessor drops, hand-ons across the back edge and the
// back edge itself. A part of `AshesCompiler.Semantics.CoreLowering`.

import Ashes.Collection.List.append
import Ashes.Collection.List.length
import Ashes.Collection.List.reverse
import Ashes.Collection.Map.MapTree
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Semantics.CallOwnership
import AshesCompiler.Semantics.HeapLayoutClassification
import AshesCompiler.Semantics.IrControlFlowGraph.containsInt
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.StructuralDroppers.OwnedReleasePlan
import AshesCompiler.Semantics.StateMachineTransform.getUsedTemps
import AshesCompiler.Semantics.ValueFlowSlots.localSlotsReachingEachTemp
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.TcoRuntimeManagedParams
import AshesCompiler.Semantics.PatternBindingOwnership
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.ExprMentions.exprMentionsName
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.CoreLowering.LoweringState
import AshesCompiler.Semantics.CoreLowering.Emit
import AshesCompiler.Semantics.CoreLowering.Owners
import AshesCompiler.Semantics.CoreLowering.CopyPlans
import AshesCompiler.Semantics.CoreLowering.TcoReset
import AshesCompiler.Semantics.CoreLowering.ResultHandOver
import AshesCompiler.Semantics.CoreLowering.LoopPlacement
import AshesCompiler.Semantics.CoreLowering.LoopFrames
import AshesCompiler.Semantics.CoreLowering.CallArguments
import AshesCompiler.Semantics.CoreLowering.CallResults
import AshesCompiler.Semantics.CoreLowering.Matches
import AshesCompiler.Semantics.CoreLowering.Aggregates
export (
    value lowerTailSelfCall,
)

type CoreTailSelfCallArguments =
    | state: CoreLoweringState
    | temps: List(Int)
    | argumentTypes: List(SemanticType)
    | error: Maybe(CoreLoweringError)

let failedTailSelfCallArguments state error =
    CoreTailSelfCallArguments(
        state = state,
        temps = [],
        argumentTypes = [],
        error = Some(error)
    )

// Built outside the argument loop: a record the loop's exit builds around its own state parameter
// keeps a reference to it that nothing releases.
let loweredTailSelfCallArguments (state: CoreLoweringState) (reversedTemps: List(Int)) (reversedTypes: List(SemanticType)) =
    CoreTailSelfCallArguments(
        state = state,
        temps = reverse(reversedTemps),
        argumentTypes = reverse(reversedTypes),
        error = None
    )

// The request a tail self-call argument is lowered under: the loop function's parameter type,
// the children transfer, and — for a parameter every self-call grows by one cons cell (stage 0's
// `AffineConsList` request, `OwnedRuntime(List)`) — a runtime list, so the cell is allocated on
// the reference-counted heap owning its head when the head is runtime-manageable. A loop that may
// run while a handler's one-shot post is pending keeps those cells in the arena with its parameter
// (stage 0's `BuildsRuntimeCellsOutsideHandlerContext`).
// A `Str` parameter placed by type takes a reference-counted successor only when the successor's
// concatenation reads the parameter itself (`text + suffix`): stage 0 promotes a concatenation
// its runtime-managed parameter reaches, so the back edge stores it as the parameter's own value;
// a fresh string that reads no parameter (`fromInt(n) + "-x"`) is built in the arena and copied
// out by the back edge.
let isConsExpression (expression: Expr) =
    match unspanArgument(expression) with
        | ExprCons(_head, _tail) -> true
        | _ -> false

// An argument whose cells borrow their parts when the back edge copies the successor: a cons, or a
// choice whose arms build cons cells.
let buildsCopiedCells (expression: Expr) =
    match unspanArgument(expression) with
        | ExprCons(_head, _tail) -> true
        | ExprList(_first :: _rest, _isMultiline) -> true
        | ExprIf(_condition, _thenBranch, _elseBranch) -> true
        | ExprMatch(_value, _cases, _position) -> true
        | ExprLet(_name, _value, _body, [], _annotation, _requirements) -> true
        | _ -> false

let isRecordSuccessor (expression: Expr) =
    match unspanArgument(expression) with
        | ExprRecordUpdate(_target, _fields) -> true
        | ExprRecord(_name, _fields, _isMultiline) -> true
        | _ -> false

// Stage 0's `IsReleasableAffineConsElement`: a grown cons builds reference-counted cells only for an
// element a caller can release as part of a reference-counted list. A list of mixed-size scalar
// constructors (`TyInt | TyVar(Int)`) is not one: its callers reclaim the result with the arena.
let isReleasableAffineConsElement (parameterType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(parameterType) with
        | SemList(element) -> placesTcoListElement(element)(state) || isGeneralRcNamedType(element)(state)
        | _ -> true

let tailSelfCallArgumentRequest (parameterType: SemanticType) (site: CoreMismatchSite) (shape: TcoArgumentShape) (runtimeString: Bool) (tailSlot: Maybe(Int)) (copiesSuccessor: Bool) (argument: Expr) (state: CoreLoweringState) =
    (let grownCons = shape == TcoGrownConsShape && isReleasableAffineConsElement(parameterType)(state) && ordinaryRcPlacementAllowed(state)
    in
        withConsumerRequest((emptyConsumerRequest with expectedType = Some(parameterType), argumentSite = Some(site), transfersRuntimeManagedChildren = !copiesSuccessor, runtimeList = grownCons, runtimeString = runtimeString, runtimeTcoListTailSlot = (if grownCons
        then tailSlot
        else None), borrowsCellParts = copiesSuccessor && buildsCopiedCells(argument), lateCopiedCellParts = !copiesSuccessor && buildsCopiedCells(argument), recordFieldCells = !copiesSuccessor && isRecordSuccessor(argument)))(state))

// A `+` chain with the parameter's own read among its operands, through any nesting of `+`.
// A variable that is the parameter itself or a `let` bound to a plain read of it (the slot was
// stored once, from a load of the parameter's slot), the alias stage 0's promotion reads through.
let variableReadsParameterSlot (name: Str) (parameterSlot: Int) (state: CoreLoweringState) =
    match lookupBinding(name)(state.bindings) with
        | Some(CoreBinding { location = CoreLocal(boundSlot) }) ->
            boundSlot == parameterSlot || (match storedTempsOfSlot(boundSlot)(state.reversedInstructions)([]) with
                | storedTemp :: [] -> loadedSlotOfTemp(storedTemp)(state.reversedInstructions) == Some(parameterSlot)
                | _ -> false)
        | _ -> false

let recursive concatChainReadsParameter (expression: Expr) (parameterSlot: Int) (state: CoreLoweringState) =
    match expression with
        | ExprAt(_span, inner) -> concatChainReadsParameter(inner)(parameterSlot)(state)
        | ExprNode(_node, inner) -> concatChainReadsParameter(inner)(parameterSlot)(state)
        | ExprAdd(left, right) -> concatChainReadsParameter(left)(parameterSlot)(state) || concatChainReadsParameter(right)(parameterSlot)(state)
        | ExprVar(name) -> variableReadsParameterSlot(name)(parameterSlot)(state)
        | _ -> false

let recursive parameterNameAtOrdinal (ordinal: Int) (names: List(Str)) =
    match names with
        | [] -> None
        | name :: rest ->
            if ordinal == 0
            then Some(name)
            else parameterNameAtOrdinal(ordinal - 1)(rest)

let tailSelfCallStringSuccessor (argument: Expr) (slot: Maybe(Int)) (ordinal: Int) (shape: TcoArgumentShape) (loop: CoreTcoLoop) (state: CoreLoweringState) =
    match (slot, parameterNameAtOrdinal(ordinal)(loop.parameterNames)) with
        | (Some(parameterSlot), Some(_parameter)) -> shape != TcoPassThroughShape && !containsInt(ordinal)(loop.runtimeManagedOrdinals) && isTcoRuntimeManagedStrSlot(parameterSlot)(state) && isFreshStringChild(argument)(state) && concatChainReadsParameter(argument)(parameterSlot)(state)
        | _ -> false

let recursive restArgumentShapes (shapes: List(TcoArgumentShape)) =
    match shapes with
        | [] -> []
        | _shape :: rest -> rest

let headArgumentShape (shapes: List(TcoArgumentShape)) =
    match shapes with
        | [] -> TcoOtherShape
        | shape :: _rest -> shape

// Stage 0's `LowerCallTcoEvalArgs`: every argument of the tail self-call is lowered into a temp
// first, expected to have the loop function's parameter type, under the children transfer and
// never in tail position, before any parameter slot changes.
let headSlotOf (slots: List(Int)) =
    match slots with
        | slot :: _rest -> Some(slot)
        | [] -> None

let restSlotsOf (slots: List(Int)) =
    match slots with
        | _slot :: rest -> rest
        | [] -> []

let recursive exitOwnFieldFlagAt (ordinal: Int) (flags: List((Int, Int, Int))) =
    match flags with
        | [] -> None
        | (slot, flagSlot, candidate) :: rest ->
            if candidate == ordinal
            then Some((slot, flagSlot))
            else exitOwnFieldFlagAt(ordinal)(rest)

let recursive exitParameterNameAt (ordinal: Int) (names: List(Str)) =
    match names with
        | [] -> ""
        | name :: rest ->
            if ordinal == 0
            then name
            else exitParameterNameAt(ordinal - 1)(rest)

let storeExitOwnFieldFlag (flagSlot: Int) (value: Int) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = tempState, temp = valueTemp } ->
            tempState
            |> emit(LoadConstInt(valueTemp)(value))
            |> emit(StoreLocal(flagSlot)(valueTemp))

// Stage 0's `NoteExitParameterSuccessor`: after a tail self-call argument is lowered into a tracked
// parameter's position, the flag follows the successor, and a constructor successor's cell is kept
// for the check at the exit.
let noteExitOwnFieldSuccessor (argument: Expr) (ordinal: Int) (loop: CoreTcoLoop) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, temp = cellTemp, error = None } ->
            match stateTcoLoopFrame(state) with
                | Some(frame) ->
                    match exitOwnFieldFlagAt(ordinal)(frame.exitOwnFieldFlags) with
                        | Some((slot, flagSlot)) ->
                            match exitParameterSuccessorKind(argument)(exitParameterNameAt(ordinal)(loop.parameterNames))(state) with
                                | 0 -> lowered
                                | 2 ->
                                    let stored = storeExitOwnFieldFlag(flagSlot)(1)(state)
                                    in lowered with state = withStateTcoLoopFrame(Some((frame with exitOwnFieldCells = (slot, cellTemp) :: frame.exitOwnFieldCells)))(stored)
                                | _ -> lowered with state = storeExitOwnFieldFlag(flagSlot)(0)(state)
                        | None -> lowered
                | None -> lowered
        | _ -> lowered

let recursive lowerTailSelfCallArguments (arguments: List(Expr)) (slots: List(Int)) (shapes: List(TcoArgumentShape)) (ordinal: Int) (loop: CoreTcoLoop) (reservations: List((Int, Int, Int))) functionType lower (state: CoreLoweringState) (reversedTemps: List(Int)) (reversedTypes: List(SemanticType)) =
    match arguments with
        | [] -> loweredTailSelfCallArguments(state)(reversedTemps)(reversedTypes)
        | argument :: rest ->
            match ensureFunctionType(functionType)(state) with
                | FunctionTypeResolution { state = failedState, error = Some(error) } -> failedTailSelfCallArguments(failedState)(error)
                | FunctionTypeResolution { state = functionState, argumentType = parameterType, resultType = resultType, error = None } ->
                    match functionState
                    |> withStateBackEdgeArgumentSlot(headSlotOf(slots))
                    |> withStateArenaSuccessorJoin(tailArgumentSuccessorJoin(headSlotOf(slots))(argument)(functionState))
                    |> withStateAffineAppendContext(affineAppendContextFor(argument)(ordinal)(headSlotOf(slots))(loop)(reservations))
                    |> tailSelfCallArgumentRequest(parameterType)(argumentSite(Some(loop.selfName))(ordinal + 1)(functionState))(headArgumentShape(shapes))(tailSelfCallStringSuccessor(argument)(headSlotOf(slots))(ordinal)(headArgumentShape(shapes))(loop)(functionState))(headSlotOf(slots))(backEdgeCopiesListSuccessor(headSlotOf(slots))(headArgumentShape(shapes))(loop)(functionState) || backEdgeCopiesLiteralSuccessor(headSlotOf(slots))(headArgumentShape(shapes))(parameterType)(argument)(loop)(functionState))(argument)
                    |> lower(argument)
                    |> noteExitOwnFieldSuccessor(argument)(ordinal)(loop)
                    |> duplicatePatternOwnerChild(argument)
                    |> retainTransferredChild(argument)(true) with
                        | LoweredCoreValue { state = failedState, error = Some(error) } ->
                            failedTailSelfCallArguments(failedState
                            |> withStateBackEdgeArgumentSlot(None)
                            |> withStateArenaSuccessorJoin(stateArenaSuccessorJoin(functionState))
                            |> withStateAffineAppendContext(None))(error)
                        | LoweredCoreValue { state = argumentState, temp = argumentTemp, semanticType = argumentType, error = None } ->
                            match argumentState
                            |> withStateBackEdgeArgumentSlot(None)
                            |> withStateArenaSuccessorJoin(stateArenaSuccessorJoin(functionState))
                            |> withStateAffineAppendContext(None)
                            |> bindType(parameterType)(argumentType)
                            |> locateArgumentMismatch(argumentSite(Some(loop.selfName))(ordinal + 1)(argumentState)) with
                                | (failedState, Some(error)) -> failedTailSelfCallArguments(failedState)(error)
                                | (typedState, None) ->
                                    lowerTailSelfCallArguments(rest)(restSlotsOf(slots))(restArgumentShapes(shapes))(ordinal + 1)(loop)(reservations)(resultType)(lower)(typedState)(argumentTemp :: reversedTemps)(argumentType :: reversedTypes)

let recursive loadOldParameters (slots: List(Int)) (state: CoreLoweringState) (reversedTemps: List(Int)) =
    match slots with
        | [] -> (state, reverse(reversedTemps))
        | slot :: rest ->
            match freshTemp(state) with
                | FreshTemp { state = tempState, temp = temp } ->
                    loadOldParameters(rest)(emit(LoadLocal(temp)(slot))(tempState))(temp :: reversedTemps)

let recursive storeNewParameters (slots: List(Int)) (temps: List(Int)) (state: CoreLoweringState) =
    match (slots, temps) with
        | (slot :: restSlots, temp :: restTemps) ->
            state
            |> emit(StoreLocal(slot)(temp))
            |> storeNewParameters(restSlots)(restTemps)
        | _ -> state

let recursive ownerTypeNameOfSlot (slot: Int) (bindings: List(CoreBinding)) (state: CoreLoweringState) =
    match bindings with
        | [] -> None
        | CoreBinding { location = CoreLocal(candidate), scheme = TypeScheme { body = bindingType } } :: rest ->
            if candidate == slot
            then
                state |> stateOwnedTypeNameOf(resolveType(state)(bindingType))
            else ownerTypeNameOfSlot(slot)(rest)(state)
        | _ :: rest -> ownerTypeNameOfSlot(slot)(rest)(state)

// The type name an iteration's owner releases under: its binding's, or, for an owner no binding
// names (the slot a match keeps its adopted scrutinee in), the type its release plan was made for.
let iterationOwnerTypeName (slot: Int) (state: CoreLoweringState) =
    match ownerTypeNameOfSlot(slot)(state.bindings)(state) with
        | Some(typeName) -> Some(typeName)
        | None ->
            match state
            |> stateOwnerReleasePlans
            |> lookupOwnerReleasePlan(slot) with
                | Some((semanticType, _plan)) ->
                    state |> stateOwnedTypeNameOf(resolveType(state)(semanticType))
                | None -> None

// The runtime owners registered since the loop entry that still hold their reference at the back
// edge, with the type name each releases under (stage 0's `CollectTcoBackEdgeOwnedDrops`): their
// lexical release sits after the jump, unreachable on this path.
let recursive iterationOwnedDrops (depth: Int) (state: CoreLoweringState) (owners: List((Int, Bool))) =
    match owners with
        | [] -> []
        | (slot, true) :: rest ->
            match (slot >= depth, iterationOwnerTypeName(slot)(state)) with
                | (true, Some(typeName)) -> (slot, typeName) :: iterationOwnedDrops(depth)(state)(rest)
                | _ -> iterationOwnedDrops(depth)(state)(rest)
        | _ :: rest -> iterationOwnedDrops(depth)(state)(rest)

let recursive ownedDropSlots (state: CoreLoweringState) (drops: List((Int, Str))) =
    match drops with
        | [] -> []
        | (slot, _typeName) :: rest ->
            match state
            |> stateOwnerReleasePlans
            |> lookupOwnerReleasePlan(slot) with
                | Some((_type, OwnedReleasePlan { matchFieldTransfer = Some((flagSlot, _fields)) })) -> slot :: flagSlot :: ownedDropSlots(state)(rest)
                | _ -> slot :: ownedDropSlots(state)(rest)

// The reset placeholder (stage 0's `TcoResetPending`), resolved once the whole body is lowered
// so the argument types are as concrete as the function's own body makes them.
let recursive listActiveSlotsOf (pairs: List((Int, Int))) =
    match pairs with
        | [] -> []
        | (_slot, activeSlot) :: rest -> activeSlot :: listActiveSlotsOf(rest)

let recursive runtimeTempFlags (temps: List(Int)) (state: CoreLoweringState) =
    match temps with
        | [] -> []
        | temp :: rest -> isRuntimeTemp(temp)(state) :: runtimeTempFlags(rest)(state)

// Stage 0's `OwnedSlotReadInto`: whether a successor argument is a plain read of an owned slot.
let recursive readsOwnedSlotDirectly (temp: Int) (ownedSlots: List((Int, SemanticType))) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = LoadLocal(target, slot) } :: rest ->
            if target == temp
            then isOwnedSlotIn(slot)(ownedSlots)
            else readsOwnedSlotDirectly(temp)(ownedSlots)(rest)
        | _ :: rest -> readsOwnedSlotDirectly(temp)(ownedSlots)(rest)

let recursive slotStoreCount (slot: Int) (count: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> count
        | IrInstruction { instruction = StoreLocal(stored, _source) } :: rest ->
            if stored == slot
            then slotStoreCount(slot)(count + 1)(rest)
            else slotStoreCount(slot)(count)(rest)
        | _ :: rest -> slotStoreCount(slot)(count)(rest)

let recursive slotStoredTemp (slot: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> None
        | IrInstruction { instruction = StoreLocal(stored, source) } :: rest ->
            if stored == slot
            then Some((source, rest))
            else slotStoredTemp(slot)(rest)
        | _ :: rest -> slotStoredTemp(slot)(rest)

// The temp a field read defining `temp` reads its field out of, or -1 when no field read defines it
// (each temp is defined once).
let recursive fieldReadBase (temp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> -1
        | IrInstruction { instruction = LoadMemOffset(target, base, _offset) } :: rest ->
            if target == temp
            then base
            else fieldReadBase(temp)(rest)
        | IrInstruction { instruction = GetAdtField(target, base, _index, _tagless) } :: rest ->
            if target == temp
            then base
            else fieldReadBase(temp)(rest)
        | _ :: rest -> fieldReadBase(temp)(rest)

// Stage 0's `OwnedSlotPartBoundInto`: a binding stored once with a field read out of an owned
// slot's value (a pattern binding of a call's owned result), a part of a value holding its own
// references, which stays alive with it. A binding stored once with the owned slot's value itself
// (`let next = f(x)`) is that value.
let ownedSlotPartBound (slot: Int) (ownedSlots: List((Int, SemanticType))) (instructions: List(IrInstruction)) =
    if slotStoreCount(slot)(0)(instructions) != 1
    then false
    else
        match slotStoredTemp(slot)(instructions) with
            | None -> false
            | Some((partTemp, earlier)) ->
                let baseTemp = fieldReadBase(partTemp)(earlier)
                in baseTemp >= 0 && readsOwnedSlotDirectly(baseTemp)(ownedSlots)(earlier) || readsOwnedSlotDirectly(partTemp)(ownedSlots)(earlier)

// Stage 0's `OwnedSlotReadInto` with parts: whether a successor argument is a plain read of an
// owned slot, or, through its borrow, of a part of an owned slot's value.
let recursive readsOwnedSlotFrom (temp: Int) (ownedSlots: List((Int, SemanticType))) (all: List(IrInstruction)) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = LoadLocal(target, slot) } :: rest ->
            if target == temp
            then isOwnedSlotIn(slot)(ownedSlots) || ownedSlotPartBound(slot)(ownedSlots)(all)
            else readsOwnedSlotFrom(temp)(ownedSlots)(all)(rest)
        | IrInstruction { instruction = Borrow(target, source) } :: rest ->
            if target == temp
            then readsOwnedSlotFrom(source)(ownedSlots)(all)(rest)
            else readsOwnedSlotFrom(temp)(ownedSlots)(all)(rest)
        | _ :: rest -> readsOwnedSlotFrom(temp)(ownedSlots)(all)(rest)

let readsOwnedSlot (temp: Int) (ownedSlots: List((Int, SemanticType))) (instructions: List(IrInstruction)) = readsOwnedSlotFrom(temp)(ownedSlots)(instructions)(instructions)

// Per successor, whether it can be no blocker whatever the parts: inline, or a plain read of an
// owned slot (a result holding its own references).
let recursive successorBlockerSkips (argumentTypes: List(SemanticType)) (temps: List(Int)) (ownedSlots: List((Int, SemanticType))) (state: CoreLoweringState) =
    match (argumentTypes, temps) with
        | (argumentType :: restTypes, temp :: restTemps) -> (resultSurvivesReset(argumentType)(state) || readsOwnedSlot(temp)(ownedSlots)(state.reversedInstructions)) :: successorBlockerSkips(restTypes)(restTemps)(ownedSlots)(state)
        | _ -> []

// The ordinals of the successors that may still name an earlier iteration's value with these heap
// parts: not skipped, and of a type that can hold one of the parts.
let recursive predecessorBlockers (ordinal: Int) (parts: List(SemanticType)) (argumentTypes: List(SemanticType)) (skips: List(Bool)) (state: CoreLoweringState) =
    match (argumentTypes, skips) with
        | (argumentType :: restTypes, skip :: restSkips) ->
            let rest = predecessorBlockers(ordinal + 1)(parts)(restTypes)(restSkips)(state)
            in
                if skip || !resultMayHoldAnyPart(argumentType)(parts)(state)
                then rest
                else ordinal :: rest
        | _ -> []

// Per parameter position, whether the parameter is placed on the reference-counted heap.
let recursive managedParameterFlags (slots: List(Int)) (frame: CoreTcoLoopFrame) (loop: CoreTcoLoop) (state: CoreLoweringState) =
    match slots with
        | [] -> []
        | slot :: rest -> loopSlotIsRuntimeManaged(slot)(frame)(loop)(state) :: managedParameterFlags(rest)(frame)(loop)(state)

let recursive unmanagedFlags (count: Int) =
    if count == 0
    then []
    else false :: unmanagedFlags(count - 1)

// The successors naming nothing that can hold a part of the predecessor slot's value, on a path
// that did not normalize the successors and on one that did.
// The positions of the frame's parameters of a value of the contract's own types.
let recursive generalRcParameterPositions (slots: List(Int)) (position: Int) (state: CoreLoweringState) =
    match slots with
        | [] -> []
        | slot :: rest ->
            match slotResolvedType(slot)(state.bindings)(state) with
                | Some(resolved) ->
                    if isGeneralRcValueType(resolved)(state)
                    then position :: generalRcParameterPositions(rest)(position + 1)(state)
                    else generalRcParameterPositions(rest)(position + 1)(state)
                | None -> generalRcParameterPositions(rest)(position + 1)(state)

// Whether the frame's parameter at this position is a list of inline values.
let isInlineElementListParameter (position: Int) (frame: CoreTcoLoopFrame) (state: CoreLoweringState) =
    match slotAtOrdinal(position)(frame.parameterSlots) with
        | Some(slot) ->
            match slotResolvedType(slot)(state.bindings)(state) with
                | Some(SemList(element)) ->
                    element
                    |> resolveType(state)
                    |> canArenaResetLayout
                | _ -> false
        | None -> false

let recursive inlineHeadsOmittedSources (sources: List(Int)) (spineSources: List(Int)) (frame: CoreTcoLoopFrame) (state: CoreLoweringState) =
    match sources with
        | [] -> []
        | source :: rest ->
            if isInlineElementListParameter(source)(frame)(state) && containsInt(source)(spineSources) == false
            then inlineHeadsOmittedSources(rest)(spineSources)(frame)(state)
            else source :: inlineHeadsOmittedSources(rest)(spineSources)(frame)(state)

let recursive inlineHeadsOmittedPositions (flow: List(List(Int))) (spine: List(List(Int))) (position: Int) (frame: CoreTcoLoopFrame) (state: CoreLoweringState) =
    match flow with
        | [] -> []
        | sources :: rest ->
            inlineHeadsOmittedSources(sources)(flowSourcesAt(position)(spine))(frame)(state) :: inlineHeadsOmittedPositions(rest)(spine)(position + 1)(frame)(state)

// Stage 0's `FlowOmittingInlineHeads`: the body's parameter flow with the edges out of a list of
// inline values kept only where they run through its spine, since a head matched out of such a
// list is a copy and carries no part of it.
let flowOmittingInlineHeads (loop: CoreTcoLoop) (frame: CoreTcoLoopFrame) (state: CoreLoweringState) =
    match (loop.selfCallParameterFlow, loop.selfCallSpineFlow) with
        | (Some(flow), Some(spine)) ->
            state
            |> inlineHeadsOmittedPositions(flow)(spine)(0)(frame)
            |> Some
        | (flow, _spine) -> flow

// The successors naming nothing that can hold a part of the predecessor's value on both paths,
// given the frame's own-reference positions and its per-parameter cuts: through the names bound
// from the value, or, for a value no name was bound to, through the parameters it was handed to as
// a successor (`handedTo`).
let unnamedSuccessorsWith (loop: CoreTcoLoop) (binders: List(Str)) (handedTo: Maybe(List(Int))) (flow: Maybe(List(List(Int)))) (ownReferences: List(Int)) (cuts: List(Bool)) (arguments: List(Expr)) =
    match (binders, handedTo) with
        | ([], Some(positions)) -> flowSuccessorsNamingNoHandedParameter(loop.parameterNames)(loop.callFlow)(flow)(positions)(ownReferences)(cuts)(arguments)
        | _ -> flowSuccessorsNamingNoPartOf(loop.selfName)(loop.parameterNames)(loop.callFlow)(loop.body)(flow)(binders)(ownReferences)(cuts)(arguments)

// What the unnamed successors of a back edge are judged with, the same for every value the back
// edge judges: the loop's own-reference positions and its per-parameter cuts on a path that did
// not normalize the successors and on one that did. It holds nothing of the loop or of the state.
type UnnamedSuccessorSetup =
    | NoUnnamedSuccessorLoop
    | UnnamedSuccessorLoop(List(Int), List(Bool), List(Bool))

let unnamedSuccessorSetupOf (frame: CoreTcoLoopFrame) (state: CoreLoweringState) =
    match stateTcoLoop(state) with
        | None -> NoUnnamedSuccessorLoop
        | Some(loop) ->
            let ownReferences =
                state
                |> generalRcParameterPositions(frame.parameterSlots)(0)
                |> tcoOwnReferencePositions(loop.selfName)(loop.parameterNames)(state
                |> stateConstructorLayouts
                |> constructorNamesOf)(loop.body)
            in
                state
                |> managedParameterFlags(frame.parameterSlots)(frame)(loop)
                |> UnnamedSuccessorLoop(ownReferences)(frame.parameterSlots
                |> length
                |> unmanagedFlags)

let unnamedSuccessorsIn (setup: UnnamedSuccessorSetup) (binders: List(Str)) (handedTo: Maybe(List(Int))) (arguments: List(Expr)) (frame: CoreTcoLoopFrame) (state: CoreLoweringState) =
    match setup with
        | NoUnnamedSuccessorLoop -> ([], [])
        | UnnamedSuccessorLoop(ownReferences, plainCuts, managedCuts) ->
            match stateTcoLoop(state) with
                | None -> ([], [])
                | Some(loop) ->
                    let flow = flowOmittingInlineHeads(loop)(frame)(state)
                    in (unnamedSuccessorsWith(loop)(binders)(handedTo)(flow)(ownReferences)(plainCuts)(arguments), unnamedSuccessorsWith(loop)(binders)(handedTo)(flow)(ownReferences)(managedCuts)(arguments))

let unnamedSuccessorSetupFor (known: Maybe(UnnamedSuccessorSetup)) (frame: CoreTcoLoopFrame) (state: CoreLoweringState) =
    match known with
        | Some(setup) -> setup
        | None -> unnamedSuccessorSetupOf(frame)(state)

let recursive withoutStringParts (parts: List(SemanticType)) =
    match parts with
        | [] -> []
        | SemString :: rest -> withoutStringParts(rest)
        | part :: rest -> part :: withoutStringParts(rest)

// Stage 0's `PredecessorPartTypes`: the parts of an earlier iteration's value a successor could hold
// without a reference of its own. A string inside the value is retained wherever it is kept, so a
// successor holding one of the value's strings keeps it alive by itself; a value that is itself a
// string stays a part.
let predecessorPartTypes (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemString -> heapPartTypesInto([semanticType])([])([])(state)
        | _ ->
            state
            |> heapPartTypesInto([semanticType])([])([])
            |> withoutStringParts

let recursive anyIntIn (items: List(Int)) (candidates: List(Int)) =
    match items with
        | [] -> false
        | item :: rest -> containsInt(item)(candidates) || anyIntIn(rest)(candidates)

// What one instruction does with the reads of an owned slot: nothing (Some of no targets), hands
// one to a call as its argument (Some of the call's result), or anything else (None). The slot's
// own release and the save into its predecessor are no uses.
let handedOnUse (reads: List(Int)) (savedInto: List(Int)) (instruction: IrInstructionKind) =
    if !anyIntIn(getUsedTemps(instruction))(reads)
    then Some([])
    else
        match instruction with
            | StoreLocal(slot, _source) ->
                if containsInt(slot)(savedInto)
                then Some([])
                else None
            | CmpIntNe(_target, _left, _right) -> Some([])
            | IsReferenceCounted(_target, _source) -> Some([])
            | CallKnown(target, label, environment, argument, flag, _requestsArena) ->
                if Ashes.Text.startsWith(label)("__rcdrop")
                then Some([])
                else
                    if containsInt(argument)(reads) && !containsInt(environment)(reads) && !containsInt(flag)(reads)
                    then Some([target])
                    else None
            | CallClosure(target, closure, argument, flag) ->
                if containsInt(argument)(reads) && !containsInt(closure)(reads) && !containsInt(flag)(reads)
                then Some([target])
                else None
            | _ -> None

let recursive handedOnCallTargets (reads: List(Int)) (savedInto: List(Int)) (instructions: List(IrInstruction)) (targets: List(Int)) =
    match instructions with
        | [] -> Some(targets)
        | IrInstruction { instruction = instruction } :: rest ->
            match handedOnUse(reads)(savedInto)(instruction) with
                | None -> None
                | Some(added) ->
                    targets
                    |> append(added)
                    |> handedOnCallTargets(reads)(savedInto)(rest)

// The next calls of a curried chain a call's result is the closure of: None when the result is
// used any other way than as such a closure or by a read of its header for that call's flags.
let recursive curriedChainCalls (temp: Int) (instructions: List(IrInstruction)) (calls: List(Int)) =
    match instructions with
        | [] -> Some(calls)
        | IrInstruction { instruction = CallClosure(target, closure, argument, flag) } :: rest when closure == temp && argument != temp && flag != temp -> curriedChainCalls(temp)(rest)(target :: calls)
        | IrInstruction { instruction = LoadMemOffset(_target, base, _offset) } :: rest when base == temp -> curriedChainCalls(temp)(rest)(calls)
        | IrInstruction { instruction = instruction } :: rest ->
            if instruction
            |> getUsedTemps
            |> containsInt(temp)
            then None
            else curriedChainCalls(temp)(rest)(calls)

// Stage 0's `CurriedChainResult`: the result a curried call ends in, a partial application holding
// the argument only until the chain's last call returns.
let recursive curriedChainResult (temp: Int) (instructions: List(IrInstruction)) (hops: Int) =
    if hops >= 64
    then temp
    else
        match curriedChainCalls(temp)(instructions)([]) with
            | Some(next :: []) -> curriedChainResult(next)(instructions)(hops + 1)
            | _ -> temp

let recursive storedIntoOwnedSlot (target: Int) (ownedResultSlots: List(Int)) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = StoreLocal(slot, source) } :: rest -> source == target && containsInt(slot)(ownedResultSlots) || storedIntoOwnedSlot(target)(ownedResultSlots)(rest)
        | _ :: rest -> storedIntoOwnedSlot(target)(ownedResultSlots)(rest)

let recursive allStoredIntoOwnedSlots (targets: List(Int)) (ownedResultSlots: List(Int)) (instructions: List(IrInstruction)) =
    match targets with
        | [] -> true
        | target :: rest ->
            storedIntoOwnedSlot(curriedChainResult(target)(instructions)(0))(ownedResultSlots)(instructions) && allStoredIntoOwnedSlots(rest)(ownedResultSlots)(instructions)

let recursive slotsOtherThan (slots: List((Int, SemanticType))) (excluded: List(Int)) =
    match slots with
        | [] -> []
        | (slot, _semanticType) :: rest ->
            if containsInt(slot)(excluded)
            then slotsOtherThan(rest)(excluded)
            else slot :: slotsOtherThan(rest)(excluded)

let recursive slotNumbersOf (slots: List((Int, SemanticType))) =
    match slots with
        | [] -> []
        | (slot, _semanticType) :: rest -> slot :: slotNumbersOf(rest)

// Per predecessor slot, the owned slot it succeeds (or -1) and the names bound from that slot.
let recursive predecessorOwnersOf (predecessors: List((Int, SemanticType))) (state: CoreLoweringState) =
    match predecessors with
        | [] -> []
        | (slot, _semanticType) :: rest ->
            let owner =
                state
                |> stateGeneralRcPredecessorOwners
                |> predecessorOwnerOf(stateActiveFunctionOrigin(state))(slot)
            in
                (slot, owner, slotBindersOf(stateActiveFunctionOrigin(state))(owner)(stateGeneralRcSlotBinders(state))([])) :: predecessorOwnersOf(rest)(state)

let recursive predecessorOwnerIn (predecessorSlot: Int) (owners: List((Int, Int, List(Str)))) =
    match owners with
        | [] -> (-1, [])
        | (slot, owner, binders) :: rest ->
            if slot == predecessorSlot
            then (owner, binders)
            else predecessorOwnerIn(predecessorSlot)(rest)

let recursive predecessorsOwnedBy (owner: Int) (owners: List((Int, Int, List(Str)))) =
    match owners with
        | [] -> []
        | (slot, candidate, _binders) :: rest ->
            if candidate == owner
            then slot :: predecessorsOwnedBy(owner)(rest)
            else predecessorsOwnedBy(owner)(rest)

// The owned slots whose value no name was bound to: the ones whose reads are followed.
let recursive handedOnOwnersOf (owners: List((Int, Int, List(Str)))) =
    match owners with
        | [] -> []
        | (_slot, owner, []) :: rest ->
            if owner < 0
            then handedOnOwnersOf(rest)
            else owner :: handedOnOwnersOf(rest)
        | _ :: rest -> handedOnOwnersOf(rest)

// What one back edge needs of the instructions emitted so far to follow the reads of its unnamed
// owned slots, gathered in a few passes for all of them: every read of one of the slots, the
// instructions using one of those reads, and the instructions using the result of a call one of
// the reads was handed to or of a curried chain continuing such a result. Each list keeps the
// instructions' order, and an instruction left out uses none of the temps, so following one slot's
// reads through these lists answers what following them through every instruction does.
type BackEdgeHandOnScan =
    | scanSlotReads: List((Int, Int))
    | scanReadUses: List(IrInstruction)
    | scanTargetUses: List(IrInstruction)

let recursive tempSetWith (temps: List(Int)) (set: MapTree(Int, Bool)) =
    match temps with
        | [] -> set
        | temp :: rest ->
            set
            |> Ashes.Collection.Map.setInt(temp)(true)
            |> tempSetWith(rest)

let tempInSet (temp: Int) (set: MapTree(Int, Bool)) =
    match Ashes.Collection.Map.getInt(temp)(set) with
        | Some(_listed) -> true
        | None -> false

let recursive anyTempInSet (temps: List(Int)) (set: MapTree(Int, Bool)) =
    match temps with
        | [] -> false
        | temp :: rest -> tempInSet(temp)(set) || anyTempInSet(rest)(set)

let recursive readsOfSlots (slots: List(Int)) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> []
        | IrInstruction { instruction = LoadLocal(target, slot) } :: rest ->
            if containsInt(slot)(slots)
            then (slot, target) :: readsOfSlots(slots)(rest)
            else readsOfSlots(slots)(rest)
        | _ :: rest -> readsOfSlots(slots)(rest)

let recursive readTempsOfSlot (slot: Int) (reads: List((Int, Int))) =
    match reads with
        | [] -> []
        | (candidate, target) :: rest ->
            if candidate == slot
            then target :: readTempsOfSlot(slot)(rest)
            else readTempsOfSlot(slot)(rest)

let recursive readTempsOf (reads: List((Int, Int))) =
    match reads with
        | [] -> []
        | (_slot, target) :: rest -> target :: readTempsOf(rest)

let recursive instructionsUsing (set: MapTree(Int, Bool)) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> []
        | instruction :: rest ->
            match instruction with
                | IrInstruction { instruction = kind } ->
                    if anyTempInSet(getUsedTemps(kind))(set)
                    then instruction :: instructionsUsing(set)(rest)
                    else instructionsUsing(set)(rest)

// The results of the calls one of the temps is the argument of.
let recursive resultsOfCallsHanded (set: MapTree(Int, Bool)) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> []
        | IrInstruction { instruction = CallKnown(target, _label, _environment, argument, _flag, _requestsArena) } :: rest ->
            if tempInSet(argument)(set)
            then target :: resultsOfCallsHanded(set)(rest)
            else resultsOfCallsHanded(set)(rest)
        | IrInstruction { instruction = CallClosure(target, _closure, argument, _flag) } :: rest ->
            if tempInSet(argument)(set)
            then target :: resultsOfCallsHanded(set)(rest)
            else resultsOfCallsHanded(set)(rest)
        | _ :: rest -> resultsOfCallsHanded(set)(rest)

// (result, closure) of every closure call, oldest first.
let recursive closureCallsOf (instructions: List(IrInstruction)) (calls: List((Int, Int))) =
    match instructions with
        | [] -> calls
        | IrInstruction { instruction = CallClosure(target, closure, _argument, _flag) } :: rest -> closureCallsOf(rest)((target, closure) :: calls)
        | _ :: rest -> closureCallsOf(rest)(calls)

let recursive chainResultsInto (calls: List((Int, Int))) (set: MapTree(Int, Bool)) (grew: Bool) =
    match calls with
        | [] -> (set, grew)
        | (target, closure) :: rest ->
            if tempInSet(closure)(set) && !tempInSet(target)(set)
            then
                chainResultsInto(rest)(Ashes.Collection.Map.setInt(target)(true)(set))(true)
            else chainResultsInto(rest)(set)(grew)

// The temps and the result of every closure call whose closure is one of them, until none joins.
let recursive closedOverChains (calls: List((Int, Int))) (set: MapTree(Int, Bool)) =
    match chainResultsInto(calls)(set)(false) with
        | (next, true) -> closedOverChains(calls)(next)
        | (next, false) -> next

let backEdgeHandOnScanOf (owners: List(Int)) (instructions: List(IrInstruction)) =
    match owners with
        | [] -> BackEdgeHandOnScan(scanSlotReads = [], scanReadUses = [], scanTargetUses = [])
        | _ ->
            let reads = readsOfSlots(owners)(instructions)
            in
                let readSet =
                    tempSetWith(readTempsOf(reads))(Ashes.Collection.Map.empty)
                in
                    let readUses = instructionsUsing(readSet)(instructions)
                    in
                        match resultsOfCallsHanded(readSet)(readUses) with
                            | [] -> BackEdgeHandOnScan(scanSlotReads = reads, scanReadUses = readUses, scanTargetUses = [])
                            | results ->
                                BackEdgeHandOnScan(scanSlotReads = reads, scanReadUses = readUses, scanTargetUses = instructionsUsing(Ashes.Collection.Map.empty
                                |> tempSetWith(results)
                                |> closedOverChains(closureCallsOf(instructions)([])))(instructions))

// Stage 0's `OnlyHandedToOwnedResults`: the value an earlier iteration left in a predecessor's slot
// was never bound to a name and every read of it is the argument of a call whose result is kept in
// an owned slot (a step of a pipeline), so no successor can hold a part of it but through such a
// result, which holds references of its own.
let predecessorOnlyHandedOn (owner: Int) (binders: List(Str)) (owners: List((Int, Int, List(Str)))) (ownedResultSlots: List(Int)) (scan: BackEdgeHandOnScan) =
    match (owner < 0, binders) with
        | (false, []) ->
            match handedOnCallTargets(readTempsOfSlot(owner)(scan.scanSlotReads))(predecessorsOwnedBy(owner)(owners))(scan.scanReadUses)([]) with
                | None -> false
                | Some([]) -> false
                | Some(targets) -> allStoredIntoOwnedSlots(targets)(ownedResultSlots)(scan.scanTargetUses)
        | _ -> false

let recursive slotOrdinal (slot: Int) (slots: List(Int)) (position: Int) =
    match slots with
        | [] -> -1
        | candidate :: rest ->
            if candidate == slot
            then position
            else slotOrdinal(slot)(rest)(position + 1)

// The parameter positions a store hands one of the reads to as a successor.
let recursive successorPositionsReading (reads: List(Int)) (parameterSlots: List(Int)) (instructions: List(IrInstruction)) (positions: List(Int)) =
    match instructions with
        | [] -> positions
        | IrInstruction { instruction = StoreLocal(slot, source) } :: rest ->
            let position = slotOrdinal(slot)(parameterSlots)(0)
            in
                if position >= 0 && containsInt(source)(reads) && !containsInt(position)(positions)
                then successorPositionsReading(reads)(parameterSlots)(rest)(position :: positions)
                else successorPositionsReading(reads)(parameterSlots)(rest)(positions)
        | _ :: rest -> successorPositionsReading(reads)(parameterSlots)(rest)(positions)

// The positions of the back edge being lowered whose successor is one of the reads.
let recursive temporaryPositionsReading (reads: List(Int)) (temps: List(Int)) (position: Int) (positions: List(Int)) =
    match temps with
        | [] -> positions
        | temp :: rest ->
            if containsInt(temp)(reads) && !containsInt(position)(positions)
            then temporaryPositionsReading(reads)(rest)(position + 1)(position :: positions)
            else temporaryPositionsReading(reads)(rest)(position + 1)(positions)

// Stage 0's `UnnamedPredecessorParameterPositions`: the loop parameter positions the value an
// earlier iteration left in a predecessor's slot, never bound to a name, was handed to as a
// successor, when every other read of it is the argument of a call whose result is kept in an owned
// slot. Those parameters are the only names that can hold it, or a part of it, without a reference
// of their own.
let unnamedPredecessorParameterPositions (owner: Int) (binders: List(Str)) (owners: List((Int, Int, List(Str)))) (ownedResultSlots: List(Int)) (temps: List(Int)) (parameterSlots: List(Int)) (scan: BackEdgeHandOnScan) =
    match (owner < 0, binders) with
        | (false, []) ->
            let reads = readTempsOfSlot(owner)(scan.scanSlotReads)
            in
                match handedOnCallTargets(reads)(append(predecessorsOwnedBy(owner)(owners))(parameterSlots))(scan.scanReadUses)([]) with
                    | None -> None
                    | Some(targets) ->
                        if allStoredIntoOwnedSlots(targets)(ownedResultSlots)(scan.scanTargetUses)
                        then
                            []
                            |> successorPositionsReading(reads)(parameterSlots)(scan.scanReadUses)
                            |> temporaryPositionsReading(reads)(temps)(0)
                            |> Some
                        else None
        | _ -> None

// Stage 0's `IsOwnedResultCall`: a full application of a function compiled to return its result
// owned on the reference-counted heap.
let isOwnedResultCall (call: Expr) (state: CoreLoweringState) =
    match callSpineRootAndArity(call)(0) with
        | (ExprVar(name), applied) ->
            match constructorLayout(name)(state) with
                | Some(_layout) -> false
                | None ->
                    match state
                    |> stateLetLambdaLabels
                    |> lookupLetLambdaLabel(name) with
                        | Some(label) ->
                            match state
                            |> stateFunctionReturnedClosureLabels
                            |> innermostStageLabel(applied - 1)(label) with
                                | Some(stage) -> bodyReturnsRuntimeManaged(stage)(state)
                                | None -> false
                        | None -> false
        | _ -> false

let recursive mentionsAnyName (names: List(Str)) (expression: Expr) =
    match names with
        | [] -> false
        | name :: rest -> exprMentionsName(name)(expression) || mentionsAnyName(rest)(expression)

// Stage 0's `NamesPartBare`: whether the successor holds one of the parts as itself, or inside a
// cons, tuple, list or constructor it builds, rather than through an owned call result.
let recursive namesPartBare (parts: List(Str)) (state: CoreLoweringState) (expression: Expr) =
    match unspanArgument(expression) with
        | ExprVar(name) -> containsLabel(name)(parts)
        | ExprCons(head, tail) -> namesPartBare(parts)(state)(head) || namesPartBare(parts)(state)(tail)
        | ExprTuple(elements) -> namesAnyPartBare(parts)(state)(elements)
        | ExprList(elements, _multiline) -> namesAnyPartBare(parts)(state)(elements)
        | ExprCall(callee, argument, _sugar, _layout) as call ->
            if isOwnedResultCall(call)(state)
            then false
            else
                match callSpineRootAndArity(call)(0) with
                    | (ExprVar(name), _applied) ->
                        match constructorLayout(name)(state) with
                            | Some(_layout) -> namesPartBare(parts)(state)(callee) || namesPartBare(parts)(state)(argument)
                            | None -> mentionsAnyName(parts)(call)
                    | _ -> mentionsAnyName(parts)(call)
        | other -> mentionsAnyName(parts)(other)
and namesAnyPartBare (parts: List(Str)) (state: CoreLoweringState) (expressions: List(Expr)) =
    match expressions with
        | [] -> false
        | expression :: rest -> namesPartBare(parts)(state)(expression) || namesAnyPartBare(parts)(state)(rest)

let recursive withoutNames (names: List(Str)) (removed: List(Str)) =
    match names with
        | [] -> []
        | name :: rest ->
            if containsLabel(name)(removed)
            then withoutNames(rest)(removed)
            else name :: withoutNames(rest)(removed)

let recursive ownedSlotBinderNames (slots: List((Int, SemanticType))) (state: CoreLoweringState) =
    match slots with
        | [] -> []
        | (slot, _semanticType) :: rest ->
            state
            |> ownedSlotBinderNames(rest)
            |> append(slotBindersOf(stateActiveFunctionOrigin(state))(slot)(stateGeneralRcSlotBinders(state))([]))

let recursive successorsNamePartBare (parts: List(Str)) (arguments: List(Expr)) (argumentTypes: List(SemanticType)) (state: CoreLoweringState) =
    match (arguments, argumentTypes) with
        | (argument :: rest, argumentType :: restTypes) -> !resultSurvivesReset(argumentType)(state) && namesPartBare(parts)(state)(argument) || successorsNamePartBare(parts)(rest)(restTypes)(state)
        | _ -> false

// Stage 0's `SuccessorsHoldNoParameterPartBare`: an earlier iteration's value reaches this
// iteration's successors only through the loop's parameters and through this iteration's owned
// call results, which hold references of their own. When no successor names a parameter part bare
// (a name bound out of an owned call result is that result's part), whatever of the earlier value
// is still reachable is held by references of its own, and the slot keeping it releases it.
let successorsHoldNoParameterPartBare (arguments: List(Expr)) (argumentTypes: List(SemanticType)) (ownedSlots: List((Int, SemanticType))) (state: CoreLoweringState) =
    match stateTcoLoop(state) with
        | None -> false
        | Some(loop) ->
            match flowDerivedFrom(loop.body)(loop.parameterNames)(loop.parameterNames)(loop.callFlow) with
                | None -> false
                | Some((_nodes, named)) ->
                    !successorsNamePartBare(state
                    |> ownedSlotBinderNames(ownedSlots)
                    |> withoutNames(named))(arguments)(argumentTypes)(state)

// What every predecessor of one back edge is judged with. The answers depend on the back edge and
// on the instructions emitted up to it, so they are gathered once for all its predecessors and
// never kept past it.
type PredecessorDropContext =
    | dropOwners: List((Int, Int, List(Str)))
    | dropScan: BackEdgeHandOnScan
    | dropOwnedResultSlots: List(Int)
    | dropNoPartBare: Bool
    | dropSkips: List(Bool)

let recursive predecessorDropsWith (context: PredecessorDropContext) (setup: UnnamedSuccessorSetup) (predecessors: List((Int, SemanticType))) (argumentTypes: List(SemanticType)) (temps: List(Int)) (arguments: List(Expr)) (frame: CoreTcoLoopFrame) (state: CoreLoweringState) =
    match predecessors with
        | [] -> []
        | (slot, semanticType) :: rest ->
            match predecessorOwnerIn(slot)(context.dropOwners) with
                | (owner, binders) ->
                    match unnamedSuccessorsIn(setup)(binders)(unnamedPredecessorParameterPositions(owner)(binders)(context.dropOwners)(context.dropOwnedResultSlots)(temps)(frame.parameterSlots)(context.dropScan))(arguments)(frame)(state) with
                        | (unnamedPlain, unnamedNormalized) ->
                            (slot, semanticType, if context.dropNoPartBare || predecessorOnlyHandedOn(owner)(binders)(context.dropOwners)(context.dropOwnedResultSlots)(context.dropScan)
                            then []
                            else
                                predecessorBlockers(0)(predecessorPartTypes(semanticType)(state))(argumentTypes)(context.dropSkips)(state), unnamedPlain, unnamedNormalized) :: predecessorDropsWith(context)(setup)(rest)(argumentTypes)(temps)(arguments)(frame)(state)

let predecessorDropsOf (predecessors: List((Int, SemanticType))) (setup: UnnamedSuccessorSetup) (argumentTypes: List(SemanticType)) (temps: List(Int)) (ownedSlots: List((Int, SemanticType))) (arguments: List(Expr)) (frame: CoreTcoLoopFrame) (state: CoreLoweringState) =
    match predecessors with
        | [] -> []
        | _ ->
            let owners = predecessorOwnersOf(predecessors)(state)
            in
                let noPartBare = successorsHoldNoParameterPartBare(arguments)(argumentTypes)(ownedSlots)(state)
                in
                    predecessorDropsWith(PredecessorDropContext(
                        dropOwners = owners,
                        dropScan = backEdgeHandOnScanOf(handedOnOwnersOf(owners))(state.reversedInstructions),
                        dropOwnedResultSlots = predecessors
                        |> slotNumbersOf
                        |> slotsOtherThan(ownedSlots),
                        dropNoPartBare = noPartBare,
                        dropSkips = if noPartBare
                        then []
                        else successorBlockerSkips(argumentTypes)(temps)(ownedSlots)(state)
                    ))(setup)(predecessors)(argumentTypes)(temps)(arguments)(frame)(state)

// The ordinals of the successors whose type may hold one of these heap parts: neither inline nor of
// a type that cannot hold any of them.
let recursive typeBlockers (ordinal: Int) (parts: List(SemanticType)) (argumentTypes: List(SemanticType)) (state: CoreLoweringState) =
    match argumentTypes with
        | [] -> []
        | argumentType :: rest ->
            if resultSurvivesReset(argumentType)(state) || !resultMayHoldAnyPart(argumentType)(parts)(state)
            then typeBlockers(ordinal + 1)(parts)(rest)(state)
            else ordinal :: typeBlockers(ordinal + 1)(parts)(rest)(state)

let recursive freshSlotOf (origin: Maybe(IrFunctionOrigin)) (slot: Int) (freshSlots: List((Maybe(IrFunctionOrigin), Int, Int))) =
    match freshSlots with
        | [] -> -1
        | (owner, candidate, freshSlot) :: rest ->
            if candidate == slot && owner == origin
            then freshSlot
            else freshSlotOf(origin)(slot)(rest)

let recursive generalRcSlotFactsOf (slots: List((Int, SemanticType))) (predecessors: List((Int, SemanticType, List(Int), List(Int), List(Int)))) (argumentTypes: List(SemanticType)) (state: CoreLoweringState) =
    match slots with
        | [] -> []
        | (slot, semanticType) :: rest ->
            if isPredecessorSlot(slot)(predecessors)
            then generalRcSlotFactsOf(rest)(predecessors)(argumentTypes)(state)
            else
                (slot, semanticType, state
                |> stateGeneralRcFreshSlots
                |> freshSlotOf(stateActiveFunctionOrigin(state))(slot), typeBlockers(0)(heapPartTypesInto([semanticType])([])([])(state))(argumentTypes)(state)) :: generalRcSlotFactsOf(rest)(predecessors)(argumentTypes)(state)

let recursive generalRcFreshSlotBlockersOf (facts: List((Int, SemanticType, Int, List(Int)))) (argumentTypes: List(SemanticType)) (state: CoreLoweringState) =
    match facts with
        | [] -> []
        | (slot, semanticType, freshSlot, _blockers) :: rest ->
            if freshSlot < 0
            then generalRcFreshSlotBlockersOf(rest)(argumentTypes)(state)
            else
                (slot, typeBlockers(0)(predecessorPartTypes(semanticType)(state))(argumentTypes)(state)) :: generalRcFreshSlotBlockersOf(rest)(argumentTypes)(state)

// Stage 0's `NoSuccessorNamesPartOf` per slot this iteration stored: the successors naming nothing
// bound from its value, on a path that did not normalize the successors and on one that did; none
// for a value no name was bound to.
let recursive generalRcSlotUnnamedOf (facts: List((Int, SemanticType, Int, List(Int)))) (known: Maybe(UnnamedSuccessorSetup)) (arguments: List(Expr)) (frame: CoreTcoLoopFrame) (state: CoreLoweringState) =
    match facts with
        | [] -> []
        | (slot, _semanticType, freshSlot, blockers) :: rest ->
            match (freshSlot < 0, blockers) with
                | (false, _first :: _others) ->
                    match slotBindersOf(stateActiveFunctionOrigin(state))(slot)(stateGeneralRcSlotBinders(state))([]) with
                        | [] -> (slot, [], []) :: generalRcSlotUnnamedOf(rest)(known)(arguments)(frame)(state)
                        | binders ->
                            let setup = unnamedSuccessorSetupFor(known)(frame)(state)
                            in
                                match unnamedSuccessorsIn(setup)(binders)(None)(arguments)(frame)(state) with
                                    | (plain, normalized) -> (slot, plain, normalized) :: generalRcSlotUnnamedOf(rest)(Some(setup))(arguments)(frame)(state)
                | _ -> generalRcSlotUnnamedOf(rest)(known)(arguments)(frame)(state)

let recursive argumentOwnedSlotsOf (temps: List(Int)) (ownedSlots: List((Int, SemanticType))) (state: CoreLoweringState) =
    match temps with
        | [] -> []
        | temp :: rest -> ownedSlotReadOf(temp)(ownedSlots)(state.reversedInstructions) :: argumentOwnedSlotsOf(rest)(ownedSlots)(state)

let recursive argumentParameterPartsOf (arguments: List(Expr)) (state: CoreLoweringState) =
    match arguments with
        | [] -> []
        | argument :: rest -> (argumentRootSlotOf(argument)(state) != None) :: argumentParameterPartsOf(rest)(state)

let recursive innermostConsTail (expression: Expr) =
    match unspanArgument(expression) with
        | ExprCons(_head, tail) -> innermostConsTail(tail)
        | other -> other

// Stage 0's `IsInlineConsOverLoopParameterPart`: inline heads consed onto a loop parameter or a
// part of one (`position :: removed` over a `List(Int)`). The new cells hold only scalars and the
// tail is the parameter's value from before this iteration, so the successor holds no part of a
// value this iteration produced, whatever its type allows.
let isInlineConsOverLoopParameterPart (argument: Expr) (state: CoreLoweringState) =
    match unspanArgument(argument) with
        | ExprCons(_head, tail) ->
            match innermostConsTail(tail) with
                | ExprVar(name) ->
                    match lookupBinding(name)(state.bindings) with
                        | Some(CoreBinding { scheme = TypeScheme { body = body } }) ->
                            match resolveType(state)(body) with
                                | SemList(element) ->
                                    (element |> resolveType(state) |> canArenaResetLayout) && argumentRootSlotOf(ExprVar(name))(state) != None
                                | _ -> false
                        | None -> false
                | _ -> false
        | _ -> false

// Per successor, stage 0's `IsLoopParameterPart` widened by `IsInlineConsOverLoopParameterPart`:
// what a value this iteration produced is judged against.
let recursive argumentStrictParameterPartsOf (arguments: List(Expr)) (state: CoreLoweringState) =
    match arguments with
        | [] -> []
        | argument :: rest -> (argumentRootSlotOf(argument)(state) != None || isInlineConsOverLoopParameterPart(argument)(state)) :: argumentStrictParameterPartsOf(rest)(state)

let recursive transferRetainedFlags (temps: List(Int)) (state: CoreLoweringState) =
    match temps with
        | [] -> []
        | temp :: rest -> isTransferRetainIn(temp)(state.reversedInstructions) :: transferRetainedFlags(rest)(state)

let recursive transferredRootTemps (drops: List((Int, SemanticType))) =
    match drops with
        | [] -> []
        | (rootTemp, _rootType) :: rest -> rootTemp :: transferredRootTemps(rest)

let isCallDefinition (instruction: Maybe(IrInstructionKind)) =
    match instruction with
        | Some(CallClosure(_target, _closure, _argument, _flag)) -> true
        | Some(CallKnown(_target, _label, _environment, _argument, _flag, _requestsArena)) -> true
        | _ -> false

let recursive callDefinitionFlags (temps: List(Int)) (state: CoreLoweringState) =
    match temps with
        | [] -> []
        | temp :: rest ->
            isCallDefinition(definitionOfTemp(temp)(state.reversedInstructions)) :: callDefinitionFlags(rest)(state)

let recursive flaggedTemps (temps: List(Int)) (flags: List(Bool)) =
    match (temps, flags) with
        | (temp :: restTemps, flag :: restFlags) ->
            if flag
            then temp :: flaggedTemps(restTemps)(restFlags)
            else flaggedTemps(restTemps)(restFlags)
        | _ -> []

let recursive reachesByOrdinal (flags: List(Bool)) (reaches: List(List(Int))) =
    match flags with
        | [] -> []
        | flag :: restFlags ->
            if flag
            then
                match reaches with
                    | reach :: restReaches -> Some(reach) :: reachesByOrdinal(restFlags)(restReaches)
                    | [] -> None :: reachesByOrdinal(restFlags)([])
            else None :: reachesByOrdinal(restFlags)(reaches)

// Per successor of a back edge, the slots its value can have been read from when it is a call's
// result, and none when it is not. The same for every slot the back edge releases.
let callResultReaches (temps: List(Int)) (state: CoreLoweringState) =
    (let flags = callDefinitionFlags(temps)(state)
    in
        match flaggedTemps(temps)(flags) with
            | [] -> reachesByOrdinal(flags)([])
            | callTemps ->
                state.reversedInstructions
                |> localSlotsReachingEachTemp(callTemps)
                |> reachesByOrdinal(flags))

// The ordinals of the successors that are a call's result whose value can have been read from
// this slot.
let recursive callResultsReaching (slot: Int) (ordinal: Int) (reaches: List(Maybe(List(Int)))) =
    match reaches with
        | [] -> []
        | Some(slots) :: rest ->
            if containsInt(slot)(slots)
            then ordinal :: callResultsReaching(slot)(ordinal + 1)(rest)
            else callResultsReaching(slot)(ordinal + 1)(rest)
        | None :: rest -> callResultsReaching(slot)(ordinal + 1)(rest)

let recursive negatedFlags (flags: List(Bool)) =
    match flags with
        | [] -> []
        | flag :: rest -> !flag :: negatedFlags(rest)

// Per successor of a back edge that is no call's result, the slots its value can have been read
// from, and none for a call's result.
let plainSuccessorReaches (temps: List(Int)) (state: CoreLoweringState) =
    (let flags =
        state
        |> callDefinitionFlags(temps)
        |> negatedFlags
    in
        match flaggedTemps(temps)(flags) with
            | [] -> reachesByOrdinal(flags)([])
            | plainTemps ->
                state.reversedInstructions
                |> localSlotsReachingEachTemp(plainTemps)
                |> reachesByOrdinal(flags))

let recursive ownedDropPlainSuccessorsWith (drops: List((Int, Str))) (reaches: List(Maybe(List(Int)))) =
    match drops with
        | [] -> []
        | (slot, _typeName) :: rest -> (slot, callResultsReaching(slot)(0)(reaches)) :: ownedDropPlainSuccessorsWith(rest)(reaches)

// Per owned drop, the ordinals of the successors that are no call's result and whose value can
// have been read from its slot.
let ownedDropPlainSuccessorsOf (drops: List((Int, Str))) (temps: List(Int)) (state: CoreLoweringState) =
    match drops with
        | [] -> []
        | _ ->
            state
            |> plainSuccessorReaches(temps)
            |> ownedDropPlainSuccessorsWith(drops)

let recursive ownedDropFactsOf (drops: List((Int, Str))) (argumentTypes: List(SemanticType)) (temps: List(Int)) (known: Maybe(List(Maybe(List(Int))))) (state: CoreLoweringState) =
    match drops with
        | [] -> []
        | (slot, _typeName) :: rest ->
            match slotResolvedType(slot)(state.bindings)(state) with
                | None -> (slot, [], []) :: ownedDropFactsOf(rest)(argumentTypes)(temps)(known)(state)
                | Some(ownedType) ->
                    let reaches =
                        match known with
                            | Some(found) -> found
                            | None -> callResultReaches(temps)(state)
                    in
                        (slot, typeBlockers(0)(heapPartTypesInto([ownedType])([])([])(state))(argumentTypes)(state), callResultsReaching(slot)(0)(reaches)) :: ownedDropFactsOf(rest)(argumentTypes)(temps)(Some(reaches))(state)

// Stage 0's `SingleFreshCons`: a successor written `head :: tail` whose tail is one of the loop's
// parameters. The single-cell list copy keeps only that top cell across the reset, which is sound
// only while the tail already sits below the per-iteration watermark.
let backEdgeConsOverParameter (parameterSlots: List(Int)) (state: CoreLoweringState) (argument: Expr) =
    match unspanArgument(argument) with
        | ExprCons(_head, tail) ->
            match unspanArgument(tail) with
                | ExprVar(name) ->
                    match lookupBinding(name)(state.bindings) with
                        | Some(CoreBinding { location = CoreLocal(slot) }) -> containsInt(slot)(parameterSlots)
                        | _ -> false
                | _ -> false
        | _ -> false

let recursive backEdgeConsesOverParameters (arguments: List(Expr)) (parameterSlots: List(Int)) (state: CoreLoweringState) =
    match arguments with
        | [] -> []
        | argument :: rest -> backEdgeConsOverParameter(parameterSlots)(state)(argument) :: backEdgeConsesOverParameters(rest)(parameterSlots)(state)

let recursive backEdgeArgumentsPassThrough (arguments: List(Expr)) (parameterSlots: List(Int)) (state: CoreLoweringState) =
    match (arguments, parameterSlots) with
        | (argument :: rest, slot :: restSlots) -> isTcoBackEdgeArgPassThrough(argument)(slot)(state) :: backEdgeArgumentsPassThrough(rest)(restSlots)(state)
        | _ -> []

let scheduleTcoReset (frame: CoreTcoLoopFrame) (arguments: List(Expr)) (temps: List(Int)) (oldTemps: List(Int)) (argumentTypes: List(SemanticType)) (rootDrops: List((Int, SemanticType))) (state: CoreLoweringState) =
    (let loopSlots =
        slotsSharingIteration(ownedSlotsOfActiveFunction(state))(state)
    in
        let predecessors =
            slotsSharingIteration(ownedSlotsOf(stateActiveFunctionOrigin(state))(stateGeneralRcPredecessorSlots(state))([]))(state)
        in
            let knownSetup =
                match predecessors with
                    | [] -> None
                    | _ ->
                        state
                        |> unnamedSuccessorSetupOf(frame)
                        |> Some
            in
                let predecessorDrops =
                    match knownSetup with
                        | None -> []
                        | Some(setup) -> predecessorDropsOf(predecessors)(setup)(argumentTypes)(temps)(loopSlots)(arguments)(frame)(state)
                in
                    let slotFacts = generalRcSlotFactsOf(loopSlots)(predecessorDrops)(argumentTypes)(state)
                    in
                        state
                        |> stateRuntimeOwners
                        |> iterationOwnedDrops(frame.ownerDepth)(state)
                        |> (given (drops: List((Int, Str))) ->
                            CoreTcoReset(
                                resetId = state
                                |> statePendingTcoResets
                                |> length,
                                argumentTypes = argumentTypes,
                                ownedDrops = drops,
                                generalRcPredecessorDrops = predecessorDrops,
                                generalRcSlotFacts = slotFacts,
                                generalRcSlotUnnamed = generalRcSlotUnnamedOf(slotFacts)(knownSetup)(arguments)(frame)(state),
                                generalRcFreshSlotBlockers = generalRcFreshSlotBlockersOf(slotFacts)(argumentTypes)(state),
                                argumentOwnedSlots = argumentOwnedSlotsOf(temps)(loopSlots)(state),
                                argumentParameterParts = argumentParameterPartsOf(arguments)(state),
                                argumentStrictParameterParts = argumentStrictParameterPartsOf(arguments)(state),
                                arenaCursorSlot = frame.arenaCursorSlot,
                                arenaEndSlot = frame.arenaEndSlot,
                                fixedCursorSlot = frame.fixedCursorSlot,
                                fixedEndSlot = frame.fixedEndSlot,
                                argumentTemps = temps,
                                oldTemps = oldTemps,
                                argumentRuntime = runtimeTempFlags(temps)(state),
                                argumentExpressions = arguments,
                                argumentSingleFreshCons = backEdgeConsesOverParameters(arguments)(frame.parameterSlots)(state),
                                argumentPassThrough = backEdgeArgumentsPassThrough(arguments)(frame.parameterSlots)(state),
                                argumentTransferRetained = transferRetainedFlags(temps)(state),
                                transferredRootDrops = rootDrops,
                                lateCopiedCellHeads = stateLateCopiedCellHeads(state),
                                ownedDropFacts = ownedDropFactsOf(drops)(argumentTypes)(temps)(None)(state),
                                ownedDropPlainSuccessors = ownedDropPlainSuccessorsOf(drops)(temps)(state)
                            ))
                        |> (given (reset: CoreTcoReset) ->
                            state
                            |> emit([frame.fixedCursorSlot, frame.fixedEndSlot, frame.arenaCursorSlot, frame.arenaEndSlot, frame.compactionSizeSlot]
                            |> append(ownedDropSlots(state)(reset.ownedDrops))
                            |> append(frame.parameterSlots)
                            |> append(listActiveSlotsOf(frame.listActiveSlots))
                            |> TcoResetPending(reset.resetId)(temps
                            |> append(oldTemps)
                            |> append(transferredRootTemps(rootDrops))))
                            |> withStateLateCopiedCellHeads([])
                            |> (given (scheduled: CoreLoweringState) -> withStatePendingTcoResets(reset :: statePendingTcoResets(scheduled))(scheduled))))

// The back edge cannot reach its expression join; its synthetic zero is the value the join
// stores. Stage 0's `LowerCallTcoBackEdgeDummy` marks it a reference-counted value (so a join
// of reachable reference-counted arms and this arm is itself reference-counted, and a
// tail-modulo-constructor producer's closed spine keeps that representation through to its
// `ReturnsRuntimeManaged` bit and result-ownership epilogue) and it is recorded as a dummy so
// the back-edge arm closes its bracket without a copy.
let emitBackEdgeDummy (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = tempState, temp = dummy } ->
            match freshType(tempState) with
                | FreshType { state = typedState, semanticType = resultType } ->
                    typedState
                    |> withStateBackEdgeDummyTemps(dummy :: stateBackEdgeDummyTemps(typedState))
                    |> markRuntimeTemp(dummy)(RuntimeNewlyProduced)
                    |> emit(LoadConstInt(dummy)(0))
                    |> success(dummy)(resultType)

// The root parameter slot of a fact that proved its binding to be the parameter's unchanged
// successor.
let transferredRootOfFact (fact: PatternBindingFact) (frame: CoreTcoLoopFrame) =
    match fact with
        | PatternBindingFact { ownership = PatternTransferredToSameParameter, rootParameterOrdinal = ordinal } ->
            if ordinal >= 0
            then parameterSlotAtOrdinal(ordinal)(frame.parameterSlots)
            else None
        | _ -> None

// The binder of this particular argument must be the transferred pattern child.
let transferredPatternBindingRoot (argument: Expr) (frame: CoreTcoLoopFrame) (state: CoreLoweringState) =
    match unspanArgument(argument) with
        | ExprVar(name) ->
            match patternBindingFact(name)(state) with
                | Some(fact) -> transferredRootOfFact(fact)(frame)
                | None -> None
        | _ -> None

let recursive unresolvedParameterTypes (slots: List(Int)) (state: CoreLoweringState) =
    match slots with
        | [] -> []
        | slot :: rest ->
            match slotResolvedType(slot)(state.bindings)(state) with
                | Some(SemVariable(_id) as unresolved) -> unresolved :: unresolvedParameterTypes(rest)(state)
                | _ -> unresolvedParameterTypes(rest)(state)

// The value a transfer takes for a root whose placement the loop's finalize pass settles only
// after the body: `ifSet` when the root's active flag is set (the root was placed on the
// reference-counted heap at the entry, and at every back edge since), `otherwise` when it is not.
// A root the finalize pass leaves in the arena was never normalized, so it is the caller's
// borrowed value: the transfer takes no reference of it and the back edge releases nothing.
let selectUnderActiveFlag (activeSlot: Int) (otherwise: Int) emitIfSet (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = flagState, temp = flagTemp } ->
            match freshLocal(flagState) with
                | FreshLocal { state = slotState, local = resultSlot } ->
                    match freshLabel("rc_transfer_inactive")(slotState) with
                        | FreshLabel { state = labelState, label = skipLabel } ->
                            match labelState
                            |> emit(LoadLocal(flagTemp)(activeSlot))
                            |> emit(StoreLocal(resultSlot)(otherwise))
                            |> emit(JumpIfFalse(flagTemp)(skipLabel))
                            |> emitIfSet with
                                | (setState, setTemp) ->
                                    match freshTemp(setState) with
                                        | FreshTemp { state = resultState, temp = resultTemp } ->
                                            (resultState
                                            |> emit(StoreLocal(resultSlot)(setTemp))
                                            |> emit(Label(skipLabel))
                                            |> emit(LoadLocal(resultTemp)(resultSlot)), resultTemp)

let retainUnderActiveFlag (rootSlot: Int) (temp: Int) (frame: CoreTcoLoopFrame) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = allocated, temp = duplicate } ->
            match lookupListActiveSlot(rootSlot)(frame.listActiveSlots) with
                | Some(activeSlot) ->
                    allocated |> selectUnderActiveFlag(activeSlot)(temp)(given (current: CoreLoweringState) ->
                        (emit(RcDup(duplicate)(temp)(true)(true))(current), duplicate))
                | None ->
                    (emit(RcDup(duplicate)(temp)(true)(true))(allocated), duplicate)

// A list root is released as the empty list when its flag is clear.
let listRootUnderActiveFlag (rootSlot: Int) (rootTemp: Int) (rootType: SemanticType) (frame: CoreTcoLoopFrame) (state: CoreLoweringState) =
    match (resolveType(state)(rootType), lookupListActiveSlot(rootSlot)(frame.listActiveSlots)) with
        | (SemList(_element), Some(activeSlot)) ->
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = emptyTemp } ->
                    allocated
                    |> emit(LoadConstInt(emptyTemp)(0))
                    |> selectUnderActiveFlag(activeSlot)(emptyTemp)(given (current: CoreLoweringState) -> (current, rootTemp))
        | _ -> (state, rootTemp)

// Stage 0's `LowerCallTcoTransferPatternBindings`, first half: a tail self-call argument that
// reads the unchanged successor of a root placed on the reference-counted heap takes one
// reference of its own before the old root graph is released. A parameter whose type is still a
// variable defers the decision to the body's next lowering.
let recursive transferPatternBindingArguments (arguments: List(Expr)) (temps: List(Int)) (frame: CoreTcoLoopFrame) (loop: CoreTcoLoop) (state: CoreLoweringState) (reversedTemps: List(Int)) (reversedRoots: List(Int)) =
    match (arguments, temps) with
        | (argument :: restArguments, temp :: restTemps) ->
            match transferredPatternBindingRoot(argument)(frame)(state) with
                | None -> transferPatternBindingArguments(restArguments)(restTemps)(frame)(loop)(state)(temp :: reversedTemps)(reversedRoots)
                | Some(rootSlot) ->
                    match unresolvedParameterTypes(frame.parameterSlots)(state) with
                        | _unresolved :: _rest as unresolved ->
                            transferPatternBindingArguments(restArguments)(restTemps)(frame)(loop)(withStateUnresolvedCallResults(state
                            |> stateUnresolvedCallResults
                            |> append(unresolved))(state))(temp :: reversedTemps)(reversedRoots)
                        | [] ->
                            if rootPlacedAtBackEdge(rootSlot)(frame)(loop)(state)
                            then
                                match retainUnderActiveFlag(rootSlot)(temp)(frame)(state) with
                                    | (allocated, duplicate) ->
                                        allocated
                                        |> markRuntimeTemp(duplicate)(RuntimeNewlyProduced)
                                        |> (given (retained: CoreLoweringState) ->
                                            transferPatternBindingArguments(restArguments)(restTemps)(frame)(loop)(retained)(duplicate :: reversedTemps)(if containsInt(rootSlot)(reversedRoots)
                                            then reversedRoots
                                            else rootSlot :: reversedRoots))
                            else transferPatternBindingArguments(restArguments)(restTemps)(frame)(loop)(state)(temp :: reversedTemps)(reversedRoots)
        | _ -> (state, reverse(reversedTemps), reverse(reversedRoots))

let recursive rootMovesToNextIteration (rootSlot: Int) (arguments: List(Expr)) (frame: CoreTcoLoopFrame) (state: CoreLoweringState) =
    match arguments with
        | [] -> false
        | argument :: rest ->
            (match unspanArgument(argument) with
                | ExprVar(name) -> parameterSlotOfName(name)(frame)(state) == Some(rootSlot)
                | _ -> false) || rootMovesToNextIteration(rootSlot)(rest)(frame)(state)

let clearListActiveFlag (rootSlot: Int) (frame: CoreTcoLoopFrame) (state: CoreLoweringState) =
    match lookupListActiveSlot(rootSlot)(frame.listActiveSlots) with
        | None -> state
        | Some(activeSlot) ->
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = inactive } ->
                    allocated
                    |> emit(LoadConstInt(inactive)(0))
                    |> emit(StoreLocal(activeSlot)(inactive))
                    |> noteTransientFlagClear(inactive)

// Stage 0's `LowerCallTcoTransferPatternBindings`, second half: a root whose successor took its
// reference, and which does not itself move to the next iteration, is released unconditionally
// and its active flag cleared, so the reset's guarded release stands down. A loop with an arena
// window releases it at the back edge instead, after the other successors (stage 0's
// `ReleaseTransferredPatternBindingRoot`): released here, a head it frees may still be named by
// another successor, a generic call's result not yet normalized. Returns the deferred releases.
let recursive releaseTransferredRoots (roots: List(Int)) (arguments: List(Expr)) (frame: CoreTcoLoopFrame) (state: CoreLoweringState) (deferred: List((Int, SemanticType))) =
    match roots with
        | [] -> (state, reverse(deferred))
        | rootSlot :: rest ->
            if rootMovesToNextIteration(rootSlot)(arguments)(frame)(state)
            then releaseTransferredRoots(rest)(arguments)(frame)(state)(deferred)
            else
                match slotResolvedType(rootSlot)(state.bindings)(state) with
                    | None -> releaseTransferredRoots(rest)(arguments)(frame)(state)(deferred)
                    | Some(rootType) ->
                        match freshTemp(state) with
                            | FreshTemp { state = allocated, temp = rootTemp } ->
                                if frame.arenaCursorSlot >= 0
                                then
                                    match allocated
                                    |> emit(LoadLocal(rootTemp)(rootSlot))
                                    |> listRootUnderActiveFlag(rootSlot)(rootTemp)(rootType)(frame) with
                                        | (selected, releasedTemp) ->
                                            match clearListActiveFlag(rootSlot)(frame)(selected) with
                                                | loaded -> releaseTransferredRoots(rest)(arguments)(frame)(loaded)((releasedTemp, rootType) :: deferred)
                                else
                                    match allocated
                                    |> emit(LoadLocal(rootTemp)(rootSlot))
                                    |> emitOwnedValueRelease(emit)(rootTemp)(rootType)
                                    |> clearListActiveFlag(rootSlot)(frame) with
                                        | released -> releaseTransferredRoots(rest)(arguments)(frame)(released)(deferred)

// Stage 0's `LowerCallTcoSelfCall` after the arguments: the transferred pattern bindings take
// their references and release their roots, the parameters' old values are loaded, the new
// values stored into the parameter slots, the reset scheduled, the stack pointer restored to
// the loop body's entry, and the body label re-entered.
let emitTailSelfCallBackEdge (enteredFrame: CoreTcoLoopFrame) (loop: CoreTcoLoop) (arguments: List(Expr)) (temps: List(Int)) (argumentTypes: List(SemanticType)) (state: CoreLoweringState) =
    match allocateListActiveSlotsAt(argumentTypesResolved(argumentTypes)(state))(enteredFrame)(loop)(state) with
        | (resolvedState, frame) ->
            match transferPatternBindingArguments(arguments)(temps)(frame)(loop)(resolvedState)([])([]) with
                | (transferred, transferredTemps, roots) ->
                    match releaseTransferredRoots(roots)(arguments)(frame)(transferred)([]) with
                        | (released, rootDrops) ->
                            match loadOldParameters(frame.parameterSlots)(released)([]) with
                                | (loadedState, oldTemps) ->
                                    loadedState
                                    |> storeNewParameters(frame.parameterSlots)(transferredTemps)
                                    |> scheduleTcoReset(frame)(arguments)(transferredTemps)(oldTemps)(argumentTypes)(rootDrops)
                                    |> emit(RestoreStackPointer(frame.stackPointerSlot))
                                    |> emit(Jump(frame.bodyLabel))
                                    |> emitBackEdgeDummy

// A tail self-call inside an emitted loop body is the loop's back edge rather than a call: the
// loop function's own type gives each argument its expected parameter type.
// A binder matched out of a reference-counted tuple scrutinee at the position of the loop
// parameter at `slot` (`match (items, kept) with | (_, same)`).
let recursive isTupleBinderOfParameter (binderSlot: Int) (slot: Int) (binders: List((Int, Int))) =
    match binders with
        | [] -> false
        | (candidate, parameterSlot) :: rest -> candidate == binderSlot && parameterSlot == slot || isTupleBinderOfParameter(binderSlot)(slot)(rest)

// Stage 0's `HandTupleBindersBackAsParameters`: such a binder handed back to its own parameter's
// position is passed as the parameter itself. Passed as the binder, the successor is the tuple's
// reference-counted copy of the parameter, retained for a slot that, left in the arena, never
// releases it.
let handTupleBinderBack (argument: Expr) (slot: Int) (name: Str) (state: CoreLoweringState) =
    match unspanArgument(argument) with
        | ExprVar(binder) ->
            match (lookupBinding(binder)(state.bindings), lookupBinding(name)(state.bindings)) with
                | (Some(CoreBinding { location = CoreLocal(binderSlot) }), Some(CoreBinding { location = CoreLocal(parameterSlot) })) ->
                    if parameterSlot == slot && isTupleBinderOfParameter(binderSlot)(slot)(stateTupleBinderParameters(state))
                    then ExprVar(name)
                    else argument
                | _ -> argument
        | _ -> argument

let recursive handTupleBindersBack (arguments: List(Expr)) (slots: List(Int)) (names: List(Str)) (state: CoreLoweringState) =
    match (arguments, slots, names) with
        | (argument :: restArguments, slot :: restSlots, name :: restNames) -> handTupleBinderBack(argument)(slot)(name)(state) :: handTupleBindersBack(restArguments)(restSlots)(restNames)(state)
        | _ -> arguments

// The self call's parameters take the types the loop's own lambdas declared, before any argument
// is lowered: the recursive binding's type is settled only once the body is, so an argument call
// whose result type comes from this parameter alone (a generic helper's, say) would otherwise
// close its window on an unresolved type. A mismatch is left to the argument that reports it.
let recursive bindSelfCallParameterTypes functionType (parameterTypes: List(SemanticType)) (state: CoreLoweringState) =
    match parameterTypes with
        | [] -> state
        | parameterType :: rest ->
            match ensureFunctionType(functionType)(state) with
                | FunctionTypeResolution { error = Some(_error) } -> state
                | FunctionTypeResolution { state = functionState, argumentType = argumentType, resultType = resultType, error = None } ->
                    match bindType(argumentType)(parameterType)(functionState) with
                        | (_failed, Some(_error)) -> state
                        | (bound, None) -> bindSelfCallParameterTypes(resultType)(rest)(bound)

// The successor cells this self-call's arguments noted, carried into the frame the back edge writes.
let frameWithExitCells (frame: CoreTcoLoopFrame) (state: CoreLoweringState) =
    match stateTcoLoopFrame(state) with
        | Some(current) -> frame with exitOwnFieldCells = current.exitOwnFieldCells
        | None -> frame

let lowerTailSelfCall (spine: CoreCallSpine) (frame: CoreTcoLoopFrame) (loop: CoreTcoLoop) lower (state: CoreLoweringState) =
    match lookupBinding(loop.selfName)(state.bindings) with
        | None -> failure(state)(UnknownLoweringBinding(loop.selfName))
        | Some(binding) ->
            match instantiateBinding(binding)(state) with
                | InstantiatedBinding { state = instantiatedFunctionState, semanticType = functionType } ->
                    let instantiatedState = bindSelfCallParameterTypes(functionType)(loop.parameterTypes)(instantiatedFunctionState)
                    in
                        let arguments = handTupleBindersBack(spine.arguments)(frame.parameterSlots)(loop.parameterNames)(instantiatedState)
                        in
                            match lowerTailSelfCallArguments(arguments)(frame.parameterSlots)(loop.argumentShapes)(0)(loop)(frame.affineReservationSlots)(functionType)(lower)(instantiatedState)([])([]) with
                                | CoreTailSelfCallArguments { state = failedState, error = Some(error) } -> failure(failedState)(error)
                                | CoreTailSelfCallArguments { state = argumentState, temps = temps, argumentTypes = argumentTypes, error = None } ->
                                    argumentState
                                    |> markCallArgumentsMoved(spine)
                                    |> emitTailSelfCallBackEdge(frameWithExitCells(frame)(argumentState))(loop)(arguments)(temps)(argumentTypes)
