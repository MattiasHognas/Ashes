// Function body requests and entry normalization, the placement of tail-call loop parameters,
// pattern owner sites, exit drops and own-field releases, and the finishing of managed placement
// and direct reuse. A part of `AshesCompiler.Semantics.CoreLowering`.

import Ashes.Collection.List.append
import Ashes.Collection.List.length
import Ashes.Collection.List.map
import Ashes.Collection.List.reverse
import Ashes.Collection.List.sortBy
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Semantics.ConstructionRetains
import AshesCompiler.Semantics.HeapLayoutClassification
import AshesCompiler.Semantics.IrControlFlowGraph.containsInt
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.ResultReachSummaries.ReachSummary
import AshesCompiler.Semantics.ResultReach.resultReachesVariableOutsideCells
import AshesCompiler.Semantics.StructuralDroppers
import AshesCompiler.Semantics.TcoRuntimeManagedParams
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.CoreLowering.LoweringState
import AshesCompiler.Semantics.CoreLowering.Emit
import AshesCompiler.Semantics.CoreLowering.Owners
import AshesCompiler.Semantics.CoreLowering.CopyPlans
import AshesCompiler.Semantics.CoreLowering.TcoReset
import AshesCompiler.Semantics.CoreLowering.ResultHandOver
import AshesCompiler.Semantics.CoreLowering.Bindings
export (
    type TcoManagedCandidate(..),
    value functionBodyRequest,
    value constructorLayoutNames,
    value calleeSummaryOf,
    value withNormalizedAlwaysReturnedParameter,
    value normalizeAlwaysReturnedParameter,
    value tcoListSlotElement,
    value isAccumulatorRebuildListSlot,
    value tcoAdtSlotAdmitted,
    value loopShapeAtOrdinal,
    value loopSlotAtOrdinal,
    value parameterOrdinalOf,
    value tcoManagedCandidates,
    value candidateAdtManaged,
    value anyBlockingSibling,
    value flowSourcesAt,
    value demoteBlockedCandidates,
    value unblockedCandidates,
    value lookupListActiveSlot,
    value tcoRuntimeManagedListSlotsOf,
    value tcoRuntimeManagedAdtSlotsOf,
    value loadedSlotOfTemp,
    value managedSlotResult,
    value storedTempsOfSlot,
    value isZeroConstantTemp,
    value candidateStrManaged,
    value synthesizeStructuralDropperLabel,
    value demotesTupleElement,
    value ownFieldKindOf,
    value containsResolvedType,
    value anyFieldTypeHeldBy,
    value listActiveSlotCandidate,
    value loopSlotTypeResolved,
    value argumentTypesResolved,
    value allocateListActiveSlotsAt,
    value finalizeTcoRuntimeManagedParams,
)

// Stage 0's `LowerEscapingResult` at a function body, from an empty request; a function that may
// run under a live handler post lowers its body plainly, as stage 0 does.
let functionBodyRequest (body: Expr) (state: CoreLoweringState) =
    if escapingRcPlacementAllowed(state)
    then escapingResultRequest(body)(emptyConsumerRequest)(state)
    else emptyConsumerRequest

// Stage 0's `EmitRuntimeManagedTcoArgumentNormalization`: bit 0 of the hidden ownership word says
// whether the caller handed over a retained reference; a borrowed argument is copied into an
// owned value, and either lands in the result slot.
let emitArgumentOwnershipNormalization (sourceTemp: Int) (plan: ArgumentCopyPlan) (state: CoreLoweringState) =
    match freshLocal(state) with
        | FreshLocal { state = slotState, local = resultSlot } ->
            match freshTemp(slotState) with
                | FreshTemp { state = wordState, temp = wordTemp } ->
                    match freshTemp(wordState) with
                        | FreshTemp { state = maskState, temp = maskTemp } ->
                            match freshTemp(maskState) with
                                | FreshTemp { state = ownershipState, temp = ownershipTemp } ->
                                    match freshLabel("rc_arg_normalize_copy")(ownershipState) with
                                        | FreshLabel { state = copyLabelState, label = copyLabel } ->
                                            match freshLabel("rc_arg_normalize_done")(copyLabelState) with
                                                | FreshLabel { state = labelState, label = doneLabel } ->
                                                    match labelState
                                                    |> emit(LoadArgumentOwnership(wordTemp))
                                                    |> emit(LoadConstInt(maskTemp)(1))
                                                    |> emit(AndInt(ownershipTemp)(wordTemp)(maskTemp))
                                                    |> emit(JumpIfFalse(ownershipTemp)(copyLabel))
                                                    |> emit(StoreLocal(resultSlot)(sourceTemp))
                                                    |> emit(Jump(doneLabel))
                                                    |> emit(Label(copyLabel))
                                                    |> emitArgumentCopy(sourceTemp)(plan) with
                                                        | (copied, copiedTemp) ->
                                                            match copied
                                                            |> emit(StoreLocal(resultSlot)(copiedTemp))
                                                            |> emit(Label(doneLabel))
                                                            |> freshTemp with
                                                                | FreshTemp { state = resultState, temp = resultTemp } ->
                                                                    (emit(LoadLocal(resultTemp)(resultSlot))(resultState), resultTemp)

// The entry block of a function that normalizes its argument: the argument is reloaded from
// slot 1, normalized, and stored back, ahead of the body already lowered.
let emitEntryArgumentNormalization (plan: ArgumentCopyPlan) (label: Str) (state: CoreLoweringState) =
    match state with
        | CoreLoweringState { reversedInstructions = bodyInstructions } ->
            match freshTemp((state with reversedInstructions = [])) with
                | FreshTemp { state = allocated, temp = sourceTemp } ->
                    match allocated
                    |> emit(LoadLocal(sourceTemp)(1))
                    |> emitArgumentOwnershipNormalization(sourceTemp)(plan) with
                        | (normalized, normalizedTemp) ->
                            normalized
                            |> emit(StoreLocal(1)(normalizedTemp))
                            |> (given (entry: CoreLoweringState) -> entry with reversedInstructions = append(bodyInstructions)(entry.reversedInstructions))
                            |> recordRuntimeNormalizedArgument(label)

let recursive constructorLayoutNames (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> []
        | CoreConstructorLayout { name = name } :: rest -> name :: constructorLayoutNames(rest)

// Stage 0's `LowerLambdaCoreNormalizeAlwaysReturnedParameter`: a function whose parameter always
// reaches its result keeps the argument past the call, so it takes ownership of a runtime-managed
// argument: it advertises that it accepts one and copies a borrowed argument into an owned value
// at entry. Only a string or ADT parameter with a copy plan is normalized; a scalar parameter,
// or one whose type has no plan yet, is left as it is.
// Stage 0's `NormalizesAlwaysReturnedParameter`: decided before the body is lowered (so the
// body's aggregates can count a read of the parameter as a fresh owned child) and again after
// it, when the parameter's type may have resolved further; the later decision can only add the
// normalization, never withdraw it.
// Stage 0's callee resolution to a registered function: the whole-program summary of the
// function recorded under the name (by its lambda identity, else the first of that name, a
// top-level one preferred), `None` for a callee outside the program's registry.
let calleeSummaryOf (callee: Str) (state: CoreLoweringState) =
    match state with
        | CoreLoweringState { programState = CoreProgramState { letLambdaIdentities = identities, reachByIdentity = byIdentity, reachByName = byName } } ->
            let found =
                match Ashes.Collection.Map.getStr(callee)(identities) with
                    | Some(identity) -> Ashes.Collection.Map.getStr(callee + "@" + Ashes.Text.fromInt(identity))(byIdentity)
                    | None -> None
            in
                match found with
                    | Some(summary) -> Some(summary)
                    | None -> Ashes.Collection.Map.getStr(callee)(byName)

// A known let-bound function as the always-reaches walk consults it: its parameter chain and the
// parameters its result always reaches, from the reach analysis's must-reach table. Stage 0
// registers a recursive group's members before their bodies and a lone recursive binding only
// after its own, so the latter's self calls resolve to nothing.
let mustReachCallee (unregistered: List(Str)) (state: CoreLoweringState) (name: Str) =
    match lookupLetLambdaNamed(name)(state) with
        | Some((parameters, _body)) ->
            Some((parameters, match calleeSummaryOf(name)(state) with
                | Some(ReachSummary { mustReach = reached }) ->
                    if containsLabel(name)(unregistered)
                    then []
                    else reached
                | None -> []))
        | None -> None

let loneRecursiveBindingNames (state: CoreLoweringState) =
    match stateRecursiveGroupNames(state) with
        | name :: [] -> [name]
        | _ -> []

// A curried stage whose body is the next stage never normalizes: its parameter reaches the result
// only as a capture of that stage's closure, whose environment normalizer copies or retains it
// once the closure escapes. An owned entry copy would instead sit in an arena closure environment
// that is never released.
let normalizesAlwaysReturnedParameter parameter body label parameterType (state: CoreLoweringState) =
    match entryNormalizationPlanOf(parameterType)(state) with
        | None -> false
        | Some(_plan) ->
            !acceptsRuntimeManagedArgument(label)(state) && !letValueIsLambda(body) && resultReachesVariableOutsideCells(state
            |> stateConstructorLayouts
            |> constructorLayoutNames)(mustReachCallee(loneRecursiveBindingNames(state))(state))(body)(parameter)

// The entry-normalized parameter (if any) made visible to the body's placement decisions; the
// direct argument lives in local slot 1.
let withNormalizedAlwaysReturnedParameter parameter body label parameterType (state: CoreLoweringState) =
    if normalizesAlwaysReturnedParameter(parameter)(body)(label)(parameterType)(state)
    then
        state
        |> withStateNormalizedParameterCloned(false)
        |> withStateNormalizedAlwaysReturnedParameter(Some((parameter, 1, parameterType)))
    else
        state
        |> withStateNormalizedParameterCloned(false)
        |> withStateNormalizedAlwaysReturnedParameter(None)

let normalizeAlwaysReturnedParameter parameter body label parameterType lowered =
    match lowered with
        | LoweredCoreValue { error = Some(_error) } -> lowered
        | LoweredCoreValue { state = bodyState } ->
            match entryNormalizationPlanOf(parameterType)(bodyState) with
                | None -> lowered
                | Some(plan) ->
                    if normalizesAlwaysReturnedParameter(parameter)(body)(label)(parameterType)(bodyState)
                    then
                        lowered with state = emitEntryArgumentNormalization(entryCopyPlanOf(parameterType)(plan)(bodyState))(label)(bodyState)
                    else lowered

// A TCO loop parameter's runtime-managed placement (stage 0's `TcoContext` slot placement,
// narrowed to the one shape this port covers): a `Str`-typed parameter the affine self-append
// analysis (`TcoAffineAppend.ash`) already proved is the loop's own leftmost-leaf accumulator —
// never a parameter the body pattern-matches or destructures (a match/`unconsText` scrutinee is
// excluded from the affine ordinals for the same reason it disqualifies a list parameter from
// the pattern-owner path), so this never collides with a consumed-list- or consumed-text-tail
// parameter's own ownership machinery.
// Stage 0's `LowerLambdaCoreSpliceRuntimeManagedTcoParams`, generated against an empty
// instruction list (so `freshTemp`/`freshLocal` still advance the function's own counters) and
// reinserted at the recorded splice point via `spliceGeneratedInstructions` rather than a
// deferred placeholder instruction: the function's own direct argument (local slot 1) asks the
// caller's hidden ownership flag (`EmitRuntimeManagedTcoArgumentNormalization`); a chain
// parameter captured from an outer curried lambda's environment has no such flag and is always
// copied (`EmitRuntimeManagedTcoParamCopy`).
let emitTcoActiveFlagStore (activeSlot: Maybe(Int)) (state: CoreLoweringState) =
    match activeSlot with
        | None -> state
        | Some(slot) ->
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = activeTemp } ->
                    allocated
                    |> emit(LoadConstInt(activeTemp)(1))
                    |> emit(StoreLocal(slot)(activeTemp))

let emitTcoInactiveFlagStore (activeSlot: Maybe(Int)) (state: CoreLoweringState) =
    match activeSlot with
        | None -> state
        | Some(slot) ->
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = inactiveTemp } ->
                    allocated
                    |> emit(LoadConstInt(inactiveTemp)(0))
                    |> emit(StoreLocal(slot)(inactiveTemp))

let isUnreadLoopSlot (slot: Int) (state: CoreLoweringState) =
    match stateTcoLoop(state) with
        | Some(loop) -> unreadLoopSlotType(slot)(loop.unreadSlots) != None
        | None -> false

// Each entry names the parameter slot, the copy a borrowed argument needs, and the active flag a
// list slot sets once its own reference is established. An unread parameter's slot holds no
// argument (zero) until the first back edge stores its successor, so it starts inactive: nothing
// to normalize, nothing to release.
let recursive emitTcoParamEntryNormalizations (entries: List((Int, ArgumentCopyPlan, Maybe(Int)))) (state: CoreLoweringState) =
    match entries with
        | [] -> state
        | (slot, _plan, activeSlot) :: rest when isUnreadLoopSlot(slot)(state) ->
            state
            |> emitTcoInactiveFlagStore(activeSlot)
            |> emitTcoParamEntryNormalizations(rest)
        | (slot, plan, activeSlot) :: rest ->
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = sourceTemp } ->
                    match emit(LoadLocal(sourceTemp)(slot))(allocated) with
                        | loaded ->
                            match if slot == 1
                            then emitArgumentOwnershipNormalization(sourceTemp)(plan)(loaded)
                            else emitArgumentCopy(sourceTemp)(plan)(loaded) with
                                | (normalized, normalizedTemp) ->
                                    normalized
                                    |> emit(StoreLocal(slot)(normalizedTemp))
                                    |> emitTcoActiveFlagStore(activeSlot)
                                    |> emitTcoParamEntryNormalizations(rest)

// Rebuilds `reversedInstructions` with `toInsert` (itself in most-recent-first order) spliced
// back in `remaining` elements below the top — the functional equivalent of stage 0's
// `_inst.RemoveRange`/`InsertRange` pair, since the instruction list here is an immutable,
// prepend-only list rather than an indexable array.
let recursive spliceGeneratedInstructions (remaining: Int) (accumulated: List(IrInstruction)) (rest: List(IrInstruction)) (toInsert: List(IrInstruction)) =
    if remaining == 0
    then
        rest
        |> append(toInsert)
        |> append(reverse(accumulated))
    else
        match rest with
            | [] ->
                rest
                |> append(toInsert)
                |> append(reverse(accumulated))
            | head :: tail -> spliceGeneratedInstructions(remaining - 1)(head :: accumulated)(tail)(toInsert)

let spliceTcoEntryNormalization (entrySpliceCount: Int) (entries: List((Int, ArgumentCopyPlan, Maybe(Int)))) (state: CoreLoweringState) =
    (let generated = emitTcoParamEntryNormalizations(entries)((state with reversedInstructions = []))
    in
        let recentCount = length(state.reversedInstructions) - entrySpliceCount
        in generated with reversedInstructions = spliceGeneratedInstructions(recentCount)([])(state.reversedInstructions)(generated.reversedInstructions))

let recursive nthFlag (index: Int) (flags: List(Bool)) =
    match flags with
        | [] -> false
        | flag :: rest ->
            if index == 0
            then flag
            else nthFlag(index - 1)(rest)

// Whether every back edge of the loop stored a cons cell allocated on the reference-counted heap
// into the parameter at `ordinal`: a head the cell could not own keeps the whole accumulator in
// the arena.
let recursive tcoBackEdgesAllocateRuntimeCells (ordinal: Int) (resets: List(CoreTcoReset)) =
    match resets with
        | [] -> true
        | reset :: rest -> nthFlag(ordinal)(reset.argumentRuntime) && tcoBackEdgesAllocateRuntimeCells(ordinal)(rest)

// A value that is reference-counted whatever holds it (stage 0's `IsUnconditionallyRcManagedType`):
// an arena spine routinely holds such elements, so a pattern owner of one of these types is
// protected even when its root parameter stays in the arena.
let isUnconditionallyRcManagedType (semanticType: SemanticType) =
    match semanticType with
        | SemString -> true
        | SemBytes -> true
        | SemBigInt -> true
        | _ -> false

// A list-shaped loop parameter admitted to runtime-managed placement once the body's types are
// resolved (stage 0's `EvaluateTcoRcEligibility` for a list): the resolved type is a list whose
// element the runtime-managed accumulator layout supports and whose spine the entry copy can
// normalize; a consumed tail only over heap elements (a list of scalars is borrowed through the
// traversal instead; a matched head that outlives its arm is retained by its pattern owner,
// whose promoted release walks an aggregate head through its structural dropper); a grown
// accumulator only when every back edge allocated its cons cell on the reference-counted heap.
// Answers the element type of an admitted slot.
// Stage 0's `IsWalkedOrRefilledElement`: a walked-or-refilled list of strings, or of lists of
// strings (a worklist of paths), whose heads are protected wherever they escape, or of inline
// values, whose heads need no protection.
let isWalkedOrRefilledElement (element: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(element) with
        | SemString -> true
        | SemList(inner) -> resolveType(state)(inner) == SemString
        | resolved -> resultSurvivesReset(resolved)(state)

let recursive anyHeapTupleTailPartner (partners: List(Int)) (loop: CoreTcoLoop) (state: CoreLoweringState) =
    match partners with
        | [] -> false
        | partner :: rest ->
            match typeAtIndex(partner)(loop.parameterTypes) with
                | Some(partnerType) ->
                    if resultSurvivesReset(partnerType)(state)
                    then anyHeapTupleTailPartner(rest)(loop)(state)
                    else true
                | None -> anyHeapTupleTailPartner(rest)(loop)(state)

// Stage 0's `IsTupleScrutineeTailBesideHeapParameter`: a list of inline elements walked by its
// tail out of a tuple scrutinee that also holds a parameter owning heap parts takes the
// consumed-tail placement, since the tuple is built on the reference-counted heap and retains the
// tail it binds for the next iteration.
let tupleTailBesideHeapParameter (ordinal: Int) (loop: CoreTcoLoop) (state: CoreLoweringState) =
    match parameterNameAt(ordinal)(loop.parameterNames) with
        | Some(name) ->
            anyHeapTupleTailPartner(tcoTupleTailPartnerOrdinals(loop.parameterNames)(name)(loop.body))(loop)(state)
        | None -> false

// Stage 0's `IsGeneralTupleScrutineeTail`: a list of general reference-counted values, or of
// mixed-size scalar constructors, walked by its tail out of a tuple scrutinee built on the
// reference-counted heap is copied into that tuple and its tail retained for the next iteration, so
// it takes the consumed-tail placement whose back edge releases it.
let isGeneralTupleScrutineeTail (element: SemanticType) (shape: TcoArgumentShape) (ordinal: Int) (loop: CoreTcoLoop) (state: CoreLoweringState) =
    shape == TcoConsumedTailShape && (isGeneralRcNamedType(element)(state) || copyAdtSupported(heapFactsOf(element)(state))) && tupleTailBesideHeapParameter(ordinal)(loop)(state)

// A grown accumulator is placed on the reference-counted heap when every back edge stores a counted
// cell into it, or, as stage 0 places any list of Bytes-holding values under the general contract,
// when its elements hold Bytes: a cell over a head of unknown Bytes origin stays in the arena and the
// back edge copies the successor. A choice accumulator of values of the contract's types (a call
// spine's argument list, handed on by one self-call and consed onto by another) is placed too, as
// stage 0's `IsChoiceAccumulatorElement` does: left in the arena it would also keep its sibling
// parameters there, which the loop's callers take as placed.
let tcoListSlotElement (slot: Int) (shape: TcoArgumentShape) (ordinal: Int) (loop: CoreTcoLoop) (state: CoreLoweringState) =
    match slotResolvedType(slot)(state.bindings)(state) with
        | Some(SemList(element)) ->
            match tcoListCopyPlanOf(element)(state) with
                | None -> None
                | Some(_plan) ->
                    if isGeneralRcNamedType(element)(state) && shape == TcoGrownConsShape || shape == TcoChoiceAccumulatorShape && isGeneralRcValueType(element)(state) || isGeneralTupleScrutineeTail(element)(shape)(ordinal)(loop)(state) || isCallResultAccumulatorElement(element)(state) && (shape == TcoAccumulatorRebuildShape || containsInt(ordinal)(loop.accumulatorRebuildOrdinals)) || (shape == TcoWalkedOrRefilledShape || shape == TcoRefilledFreshListShape) && isWalkedOrRefilledElement(element)(state) || tcoListElementSupported(element)(state) && (shape == TcoGrownConsShape && (tcoBackEdgesAllocateRuntimeCells(ordinal)(statePendingTcoResets(state)) || containsBytesLayout(element)(state)) || shape == TcoConsumedTailShape && (!resultSurvivesReset(element)(state) || tupleTailBesideHeapParameter(ordinal)(loop)(state)) || shape == TcoChoiceAccumulatorShape && !resultSurvivesReset(element)(state))
                    then Some(element)
                    else None
        | _ -> None

// Stage 0's `IsGeneralRcAccumulatorList` for an `AccumulatorRebuild` position: a list of the
// contract's values the self-calls hand on, cons onto, or replace with a call's result or a part
// matched out of one, in a loop with no tail-modulo-cons spine.
let accumulatorRebuildListElement (ordinal: Int) (slot: Int) (loop: CoreTcoLoop) (state: CoreLoweringState) =
    if containsInt(ordinal)(loop.accumulatorRebuildOrdinals) && !loop.tmcShapePresent
    then tcoListSlotElement(slot)(TcoAccumulatorRebuildShape)(ordinal)(loop)(state)
    else None

let isAccumulatorRebuildListSlot (ordinal: Int) (slot: Int) (loop: CoreTcoLoop) (state: CoreLoweringState) = accumulatorRebuildListElement(ordinal)(slot)(loop)(state) != None

// The element a list slot is placed by through its own shape's route.
let shapeListElement (ordinal: Int) (slot: Int) (shape: TcoArgumentShape) (loop: CoreTcoLoop) (state: CoreLoweringState) =
    if isTcoListShape(shape)
    then tcoListSlotElement(slot)(shape)(ordinal)(loop)(state)
    else None

// A single-constructor ADT loop parameter admitted to runtime-managed placement, stage 0's
// `IsRcEligibleScalarTupleOrAdtType`: a copy ADT (every field a scalar, `CanCopyOutAdt`) or a
// tuple of scalars copies its cell whole at entry and at every back edge and releases as one
// `RcDrop` under the type's name (`Tuple` for a tuple); a record with owned children
// (`CanRuntimeManageTcoAdt`) and a tuple whose elements a runtime-managed parent owns
// (`CanRuntimeManageOwnedTupleType`) copy their children with the cell and release them through
// the cell's structural walk; a parameter of the contract's own types is normalized by its helper
// (`tcoGeneralRcParameterPlacement`). Answers the resolved type and that name.
let tcoNamedSlotCopy (named: SemanticType) (name: Str) (layouts: List(CoreConstructorLayout)) (state: CoreLoweringState) =
    match layouts with
        | _layout :: [] ->
            match heapFactsOf(named)(state) with
                | HeapLayoutFacts { structuralCopy = ShallowCopy } -> Some((named, name))
                | facts ->
                    if runtimeManagedAdtLayout(facts)
                    then
                        match inlineAdtCopyPlanOf(named)(state) with
                            | Some(ConstructorArgumentCopy(_constructorPlan)) -> Some((named, name))
                            | _ -> None
                    else None
        | _ -> None

let recursive loopParameterNameAt (ordinal: Int) (names: List(Str)) =
    match names with
        | [] -> None
        | name :: rest ->
            if ordinal == 0
            then Some(name)
            else loopParameterNameAt(ordinal - 1)(rest)

let recursive namesSpecializationAccumulator (name: Str) (accumulators: List((Str, Str))) =
    match accumulators with
        | [] -> false
        | (accumulator, _callee) :: rest -> accumulator == name || namesSpecializationAccumulator(name)(rest)

let isSpecializationAccumulatorAt (ordinal: Int) (loop: CoreTcoLoop) =
    match loopParameterNameAt(ordinal)(loop.parameterNames) with
        | Some(name) -> namesSpecializationAccumulator(name)(loop.specializationAccumulators)
        | None -> false

let isSingleRecordLayout (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | CoreConstructorLayout { fieldNames = _field :: _fields } :: [] -> true
        | _ -> false

let recursive genericVariantCallSpine (expression: Expr) (count: Int) =
    match stripExprAt(expression) with
        | ExprCall(function, _argument, _sugar, _layout) -> genericVariantCallSpine(function)(count + 1)
        | root -> (root, count)

// Stage 0's `GenericVariantReturnsOwnedRoot`: every path of the loop body ends in the parameter
// itself, unshadowed, or in a full self-call, so the loop's result is the parameter's own root.
let recursive genericVariantReturnsOwnedRoot (self: Str) (arity: Int) (parameter: Str) (shadowed: List(Str)) (body: Expr) =
    match stripExprAt(body) with
        | ExprVar(name) -> !containsName(parameter)(shadowed) && name == parameter
        | ExprIf(_condition, thenBranch, elseBranch) -> genericVariantReturnsOwnedRoot(self)(arity)(parameter)(shadowed)(thenBranch) && genericVariantReturnsOwnedRoot(self)(arity)(parameter)(shadowed)(elseBranch)
        | ExprMatch(_value, cases, _position) ->
            match cases with
                | [] -> false
                | _ -> genericVariantArmsReturnOwnedRoot(self)(arity)(parameter)(shadowed)(cases)
        | ExprLet(name, _value, inner, _parameters, _annotation, _requirements) -> genericVariantReturnsOwnedRoot(self)(arity)(parameter)(name :: shadowed)(inner)
        | ExprCall(_function, _argument, _sugar, _layout) ->
            match genericVariantCallSpine(body)(0) with
                | (ExprVar(callee), count) -> callee == self && !containsName(self)(shadowed) && count == arity
                | _ -> false
        | _ -> false
and genericVariantArmsReturnOwnedRoot (self: Str) (arity: Int) (parameter: Str) (shadowed: List(Str)) cases =
    match cases with
        | [] -> true
        | (pattern, armBody, _guard) :: rest ->
            genericVariantReturnsOwnedRoot(self)(arity)(parameter)(append(shapePatternNames(pattern)([]))(shadowed))(armBody) && genericVariantArmsReturnOwnedRoot(self)(arity)(parameter)(shadowed)(rest)

let recursive appliedLoopResult (functionType: SemanticType) (count: Int) (state: CoreLoweringState) =
    if count == 0
    then
        functionType
        |> resolveType(state)
        |> Some
    else
        match resolveType(state)(functionType) with
            | SemFunction(_argument, result, _row) -> appliedLoopResult(result)(count - 1)(state)
            | _ -> None

// Stage 0's `GenericVariantHasSafeExit` result-type proof: a scalar result keeps nothing of the
// parameter, and a result of the contract's types acquires its own graph before the loop's owners
// are released. A result type not yet resolved proves nothing.
let genericVariantResultKeepsNoRoot (loop: CoreTcoLoop) (state: CoreLoweringState) =
    match lookupBinding(loop.selfName)(state.bindings) with
        | Some(CoreBinding { scheme = TypeScheme { body = body } }) ->
            match appliedLoopResult(body)(loop.arity)(state) with
                | Some(SemVariable(_id)) -> false
                | Some(result) -> resultSurvivesReset(result)(state) || isGeneralRcValueType(result)(state)
                | None -> false
        | None -> false

let recursive namesTraitEvidence (names: List(Str)) =
    match names with
        | [] -> false
        | name :: rest -> Ashes.Text.startsWith(name)("__trait_") || namesTraitEvidence(rest)

// Stage 0's `IsGeneralRcGenericVariantLoopParameter`: a changing loop parameter of a generic
// variant the contract governs (a map's tree) needs an owned loop root like a monomorphic variant,
// so an arena update does not borrow an iteration's reference-counted children and keep them past
// their release. A loop carrying trait evidence, a tail-modulo-cons loop, and a parameter already
// taken as a reuse root, a specialization accumulator or a routed one stay as they are. The exit
// contract is the owned-root return or a result that keeps no root; stage 0's third proof (an
// exhaustive reach summary that never reaches the parameter) is not ported, so a loop proven only
// by it keeps its parameter in the arena and its specialization. Stage 0's
// `ReserveGenericVariantLoopRoot` is the same rule without the exit contract, read before the
// specialization scan admits the parameter; here the scan reads the provisional placement, which
// holds the exit contract, so a loop whose exit is not proven yet keeps its specialization.
let genericVariantLoopParameter (ordinal: Int) (named: SemanticType) (layouts: List(CoreConstructorLayout)) (loop: CoreTcoLoop) (state: CoreLoweringState) =
    match (named, loopParameterNameAt(ordinal)(loop.parameterNames)) with
        | (SemNamed(_symbolId, _typeName, _argument :: _arguments), Some(parameter)) ->
            generalRcEnabled(state) && stateSpecializingLinearParam(state) != Some(parameter) && !loop.tmcShapePresent && !namesTraitEvidence(loop.parameterNames) && length(layouts) >= 2 && !containsName(parameter)(stateLinearReuseNames(state)) && !containsName(parameter)(stateLinearSpecializationAccumulators(state)) && !containsName(parameter)(stateRoutedSpecializationAccumulators(state)) && !containsUnresolvedLayout(named)(state) && isGeneralRcAdmissible(named)([])(state) && isGeneralRcNamedType(named)(state) && (genericVariantReturnsOwnedRoot(loop.selfName)(loop.arity)(parameter)([])(loop.body) || genericVariantResultKeepsNoRoot(loop)(state))
        | _ -> false

// Stage 0's `IsGeneralRcTcoParameterType`: a record only the normalization helper expresses (a
// state threaded through a loop) is placed on the reference-counted heap like any other aggregate,
// normalized at entry and at the back edge, its predecessor released; so is a monomorphic variant
// of the contract's types (a syntax node), which left in the arena would block every sibling that
// may hold it. A variant stays in the arena in a tail-modulo-cons loop, whose self-call embeds it
// in the cells the loop produces, and either one does when the loop hands it to a specializable
// rewriter, whose persistent region keeps it. None for any other type, Some with whether it is
// placed for one of these.
let tcoGeneralRcParameterPlacement (ordinal: Int) (named: SemanticType) (layouts: List(CoreConstructorLayout)) (loop: CoreTcoLoop) (state: CoreLoweringState) =
    match (named, isSingleRecordLayout(layouts)) with
        | (SemNamed(_symbolId, _name, _arguments), true) ->
            if isGeneralRcNamedType(named)(state)
            then Some(!isSpecializationAccumulatorAt(ordinal)(loop))
            else None
        | (SemNamed(_symbolId, _name, []), false) ->
            if isGeneralRcNamedType(named)(state)
            then Some(!isSpecializationAccumulatorAt(ordinal)(loop) && !loop.tmcShapePresent)
            else None
        | (SemNamed(_symbolId, _name, _argument :: _arguments), false) ->
            if genericVariantLoopParameter(ordinal)(named)(layouts)(loop)(state)
            then Some(true)
            else None
        | _ -> None

// A byte buffer every self-call hands on unchanged is placed like one the loop rebuilds (stage 0's
// `IsRcEligibleScalarTupleOrAdtType` has no ownership-shape condition for `Bytes`): normalized at the
// loop entry and released at its exit.
let tcoPassThroughSlotCopy (slot: Int) (state: CoreLoweringState) =
    match slotResolvedType(slot)(state.bindings)(state) with
        | Some(SemBytes) -> Some((SemBytes, "Bytes"))
        | _ -> None

// A list-only self-call shape (a choice between handing the parameter on and a call consuming it,
// say) says nothing about a named type's cells: stage 0's `IsRcEligibleScalarTupleOrAdtType` places
// a named loop parameter whatever its self-calls pass, short of handing it on unchanged. Every
// other type keeps the list-shape gate.
let tcoUnnamedSlotCopy (slot: Int) (shape: TcoArgumentShape) (loop: CoreTcoLoop) (resolved: Maybe(SemanticType)) (state: CoreLoweringState) =
    if isTcoListShape(shape) && shape != TcoRefilledFreshListShape
    then None
    else
        match resolved with
            | Some(SemTuple(elements) as tuple) ->
                if length(elements) > 0 && allRuntimeOwnedCopyElements(elements)(state)
                then Some((tuple, "Tuple"))
                else None
            | Some(SemString) -> Some((SemString, "String"))
            | Some(SemBytes) -> Some((SemBytes, "Bytes"))
            | Some(SemList(element) as list) ->
                if (shape == TcoFreshListShape || (shape == TcoRefilledFreshListShape || shape == TcoHandedOnFreshListShape) && resultSurvivesReset(element)(state) && !loop.tmcShapePresent) && tcoListElementSupported(element)(state)
                then
                    match argumentCopyPlanOf(list)(state) with
                        | Some(_plan) -> Some((list, "List"))
                        | None -> None
                else None
            | _ -> None

let tcoAdtSlotCopy (ordinal: Int) (slot: Int) (shape: TcoArgumentShape) (loop: CoreTcoLoop) (state: CoreLoweringState) =
    if shape == TcoPassThroughShape
    then tcoPassThroughSlotCopy(slot)(state)
    else
        match slotResolvedType(slot)(state.bindings)(state) with
            | Some(SemNamed(_symbolId, name, _arguments) as named) ->
                if isResourceHandle(named)(state)
                then None
                else
                    let layouts = state |> stateConstructorsOfType(name)
                    in
                        match tcoGeneralRcParameterPlacement(ordinal)(named)(layouts)(loop)(state) with
                            | Some(true) -> Some((named, name))
                            | Some(false) -> None
                            | None -> tcoNamedSlotCopy(named)(name)(layouts)(state)
            | resolved -> tcoUnnamedSlotCopy(slot)(shape)(loop)(resolved)(state)

let tcoAdtSlotAdmitted (ordinal: Int) (slot: Int) (shape: TcoArgumentShape) (loop: CoreTcoLoop) (state: CoreLoweringState) =
    match tcoAdtSlotCopy(ordinal)(slot)(shape)(loop)(state) with
        | Some(_copy) -> true
        | None -> false

// One loop parameter's placement facts once the body is lowered: whether the affine-append
// analysis and its resolved type admit it as a runtime-managed `Str`, the element type when
// its self-call shape and resolved type admit it as a runtime-managed list, and the cell size
// and type name when its resolved type admits it as a runtime-managed copy ADT.
type TcoManagedCandidate =
    | ordinal: Int
    | slot: Int
    | shape: TcoArgumentShape
    | strManaged: Bool
    | listElement: Maybe(SemanticType)
    | adtCopy: Maybe((SemanticType, Str))

let recursive loopShapeAtOrdinal (ordinal: Int) (shapes: List(TcoArgumentShape)) =
    match shapes with
        | [] -> None
        | shape :: rest ->
            if ordinal == 0
            then Some(shape)
            else loopShapeAtOrdinal(ordinal - 1)(rest)

let recursive loopSlotAtOrdinal (ordinal: Int) (slots: List(Int)) =
    match slots with
        | [] -> None
        | slot :: rest ->
            if ordinal == 0
            then Some(slot)
            else loopSlotAtOrdinal(ordinal - 1)(rest)

let recursive parameterOrdinalOf (name: Str) (names: List(Str)) (ordinal: Int) =
    match names with
        | [] -> None
        | candidate :: rest ->
            if candidate == name
            then Some(ordinal)
            else parameterOrdinalOf(name)(rest)(ordinal + 1)

// Stage 0's `TcoPlacementReason.ReuseAccumulator`: a linear reuse root keeps the arena, its
// matched cells being rebuilt in place, so the resolved back edge never places it on the
// reference-counted heap.
// Stage 0's `TcoPlacementReason.DynamicCapabilityBoundary` keeps every parameter of a loop that
// may run under a live handler post in the arena the same way.
let loopParameterIsLinearReuseRoot (ordinal: Int) (loop: CoreTcoLoop) (state: CoreLoweringState) =
    match loopParameterNameAt(ordinal)(loop.parameterNames) with
        | Some(name) ->
            state
            |> stateLinearReuseNames
            |> containsName(name)
        | None -> false

let recursive tcoManagedCandidates (ordinal: Int) (slots: List(Int)) (shapes: List(TcoArgumentShape)) (loop: CoreTcoLoop) (state: CoreLoweringState) =
    match (slots, shapes) with
        | (slot :: restSlots, shape :: restShapes) ->
            (let linearReuse = ordinaryRcPlacementAllowed(state) == false || loopParameterIsLinearReuseRoot(ordinal)(loop)(state)
            in
                let strManaged = !linearReuse && containsInt(ordinal)(loop.runtimeManagedOrdinals) && isTcoRuntimeManagedStrSlot(slot)(state)
                in
                    TcoManagedCandidate(
                        ordinal = ordinal,
                        slot = slot,
                        shape = if !linearReuse && accumulatorRouteShape(shape) && shapeListElement(ordinal)(slot)(shape)(loop)(state) == None && isAccumulatorRebuildListSlot(ordinal)(slot)(loop)(state)
                        then TcoAccumulatorRebuildShape
                        else shape,
                        strManaged = strManaged,
                        listElement = if linearReuse
                        then None
                        else
                            match shapeListElement(ordinal)(slot)(shape)(loop)(state) with
                                | Some(element) -> Some(element)
                                | None ->
                                    if accumulatorRouteShape(shape)
                                    then accumulatorRebuildListElement(ordinal)(slot)(loop)(state)
                                    else None,
                        adtCopy = if strManaged || linearReuse
                        then None
                        else tcoAdtSlotCopy(ordinal)(slot)(shape)(loop)(state)
                    )) :: tcoManagedCandidates(ordinal + 1)(restSlots)(restShapes)(loop)(state)
        | _ -> []

let candidateListManaged (candidate: TcoManagedCandidate) =
    match candidate.listElement with
        | Some(_element) -> true
        | None -> false

let candidateAdtManaged (candidate: TcoManagedCandidate) =
    match candidate.adtCopy with
        | Some(_copy) -> true
        | None -> false

// Stage 0's `IsPermanentlyBlockingTcoParam`: a sibling that keeps the frame's per-iteration
// reclaim from ever running — heap-typed, neither a resource handle nor the loop's own unchanged
// value, not a consumed tail over scalars, and not itself placed on the reference-counted heap —
// demotes every runtime-managed list candidate of the frame.
let tcoParameterBlocksFrame (candidate: TcoManagedCandidate) (state: CoreLoweringState) =
    match slotResolvedType(candidate.slot)(state.bindings)(state) with
        | None -> false
        | Some(SemFunction(_argument, _result, _row)) -> false
        | Some(resolved) -> !candidate.strManaged && !candidateListManaged(candidate) && !candidateAdtManaged(candidate) && !resultSurvivesReset(resolved)(state) && !isResourceHandle(resolved)(state) && candidate.shape != TcoPassThroughShape && !consumedScalarListTail(resolved)(candidate.shape)(state)

let recursive anyBlockingSibling (candidates: List(TcoManagedCandidate)) (state: CoreLoweringState) =
    match candidates with
        | [] -> false
        | candidate :: rest -> tcoParameterBlocksFrame(candidate)(state) || anyBlockingSibling(rest)(state)

// The accumulator a blocking sibling does not demote, stage 0's `unblockedAccumulator`. Reference-
// counted cells reach a list accumulator whatever its placement: one of strings or of heap
// elements has its cells built on the reference-counted heap, and one of inline elements has the
// type of a list field of the contract values the iteration owns. Kept in the arena for a
// sibling's sake, the list would keep them without the back edge releasing any. So would a value
// of the contract's own types threaded through the loop (a state each iteration rebuilds), once
// the self-calls' parameter flow is known. So would a value whose fields are all scalars, which its
// producers hand back on the reference-counted heap (stage 0's `GRC_NO_SCALARFIELDACCUMULATOR`).
let unblockedAccumulatorType (candidate: TcoManagedCandidate) (loop: CoreTcoLoop) (state: CoreLoweringState) =
    if candidate.shape == TcoConsumedTailShape
    then None
    else
        match (slotResolvedType(candidate.slot)(state.bindings)(state), loop.selfCallParameterFlow) with
            | (Some(SemList(_element) as accumulator), _flow) -> Some(accumulator)
            | (Some(resolved), Some(_flow)) ->
                if isGeneralRcValueType(resolved)(state) || isScalarFieldVariant(resolved)(state)
                then Some(resolved)
                else None
            | _ -> None

let recursive flowSourcesAt (ordinal: Int) (flow: List(List(Int))) =
    match flow with
        | [] -> []
        | sources :: rest ->
            if ordinal == 0
            then sources
            else flowSourcesAt(ordinal - 1)(rest)

// Whether a self-call may pass the accumulator, or a part of it, in the sibling's position: from
// the self-calls' parameter flow when it was followed, from the sibling's type otherwise.
let flowMayPass (siblingOrdinal: Int) (accumulatorOrdinal: Int) (flow: Maybe(List(List(Int)))) =
    match flow with
        | None -> true
        | Some(positions) ->
            positions
            |> flowSourcesAt(siblingOrdinal)
            |> containsInt(accumulatorOrdinal)

// Stage 0's `AccumulatorFlowFor`: a list of strings reaches a sibling only through its spine, since a
// string head matched out of it is retained wherever it is kept, and so does a list of inline
// values, whose heads are copies.
let accumulatorFlowFor (accumulatorType: SemanticType) (loop: CoreTcoLoop) (state: CoreLoweringState) =
    match (resolveType(state)(accumulatorType), loop.selfCallSpineFlow) with
        | (SemList(SemString), Some(spine)) -> Some(spine)
        | (SemList(element), Some(spine)) ->
            if element
            |> resolveType(state)
            |> canArenaResetLayout
            then Some(spine)
            else loop.selfCallParameterFlow
        | _ -> loop.selfCallParameterFlow

// Stage 0's `SiblingMayReceiveAccumulator`: a sibling that could hold the accumulator, or a part of
// it, would keep that part without a reference of its own, so it still blocks.
// Stage 0's `SiblingMayReceiveAccumulatorPart`: a sibling whose value may be a part of an accumulator
// element (an inner list matched out of it and handed on) receives that part without a reference
// of its own when it is kept in the arena, and the back edge releases the accumulator it came from.
let siblingReceivesAccumulatorPart (siblingType: SemanticType) (accumulatorType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(accumulatorType) with
        | SemList(element) ->
            !(siblingType |> resolveType(state) |> canArenaResetLayout) && typeMayContainArgumentType(false)(element)(siblingType)([])(state)
        | _ -> false

let siblingMayReceiveAccumulator (sibling: TcoManagedCandidate) (candidate: TcoManagedCandidate) (accumulator: Maybe(SemanticType)) (loop: CoreTcoLoop) (state: CoreLoweringState) =
    match (accumulator, slotResolvedType(sibling.slot)(state.bindings)(state)) with
        | (Some(accumulatorType), Some(siblingType)) ->
            flowMayPass(sibling.ordinal)(candidate.ordinal)(accumulatorFlowFor(accumulatorType)(loop)(state)) && (typeMayContainArgumentType(false)(siblingType)(accumulatorType)([])(state) || siblingReceivesAccumulatorPart(siblingType)(accumulatorType)(state))
        | _ -> true

// Stage 0's `FindBlockingSiblingForCandidate`: whether some other parameter of the frame keeps this
// candidate off the reference-counted heap.
let recursive candidateBlockedBySibling (candidate: TcoManagedCandidate) (accumulator: Maybe(SemanticType)) (siblings: List(TcoManagedCandidate)) (loop: CoreTcoLoop) (state: CoreLoweringState) =
    match siblings with
        | [] -> false
        | sibling :: rest -> sibling.ordinal != candidate.ordinal && tcoParameterBlocksFrame(sibling)(state) && siblingMayReceiveAccumulator(sibling)(candidate)(accumulator)(loop)(state) || candidateBlockedBySibling(candidate)(accumulator)(rest)(loop)(state)

// A candidate the frame does not place on the reference-counted heap has nothing to demote.
let recursive demoteBlockedCandidates (candidates: List(TcoManagedCandidate)) (siblings: List(TcoManagedCandidate)) (loop: CoreTcoLoop) (state: CoreLoweringState) =
    match candidates with
        | [] -> []
        | candidate :: rest ->
            (if (candidateListManaged(candidate) || candidateAdtManaged(candidate)) && candidateBlockedBySibling(candidate)(unblockedAccumulatorType(candidate)(loop)(state))(siblings)(loop)(state)
            then candidate with listElement = None, adtCopy = None
            else candidate) :: demoteBlockedCandidates(rest)(siblings)(loop)(state)

// The frame's candidates with every one a sibling blocks taken off the reference-counted heap.
let unblockedCandidates (candidates: List(TcoManagedCandidate)) (loop: CoreTcoLoop) (state: CoreLoweringState) =
    if anyBlockingSibling(candidates)(state)
    then demoteBlockedCandidates(candidates)(candidates)(loop)(state)
    else candidates

let recursive lookupListActiveSlot (slot: Int) (pairs: List((Int, Int))) =
    match pairs with
        | [] -> None
        | (candidate, activeSlot) :: rest ->
            if candidate == slot
            then Some(activeSlot)
            else lookupListActiveSlot(slot)(rest)

let recursive tcoRuntimeManagedListSlotsOf (candidates: List(TcoManagedCandidate)) (activeSlots: List((Int, Int))) =
    match candidates with
        | [] -> []
        | TcoManagedCandidate { slot = slot, listElement = Some(element) } :: rest ->
            match lookupListActiveSlot(slot)(activeSlots) with
                | Some(activeSlot) -> (slot, activeSlot, element) :: tcoRuntimeManagedListSlotsOf(rest)(activeSlots)
                | None -> tcoRuntimeManagedListSlotsOf(rest)(activeSlots)
        | _candidate :: rest -> tcoRuntimeManagedListSlotsOf(rest)(activeSlots)

let recursive tcoRuntimeManagedAdtSlotsOf (candidates: List(TcoManagedCandidate)) (activeSlots: List((Int, Int))) =
    match candidates with
        | [] -> []
        | TcoManagedCandidate { slot = slot, adtCopy = Some((semanticType, typeName)) } :: rest ->
            match lookupListActiveSlot(slot)(activeSlots) with
                | Some(activeSlot) -> (slot, activeSlot, semanticType, typeName) :: tcoRuntimeManagedAdtSlotsOf(rest)(activeSlots)
                | None -> tcoRuntimeManagedAdtSlotsOf(rest)(activeSlots)
        | _candidate :: rest -> tcoRuntimeManagedAdtSlotsOf(rest)(activeSlots)

// The runtime-managed `Str` slots: every affine candidate with the active flag the loop entry
// allocated for it.
let recursive tcoRuntimeManagedStrSlotsOf (candidates: List(TcoManagedCandidate)) (activeSlots: List((Int, Int))) =
    match candidates with
        | [] -> []
        | TcoManagedCandidate { slot = slot, strManaged = true } :: rest ->
            match lookupListActiveSlot(slot)(activeSlots) with
                | Some(activeSlot) -> (slot, activeSlot) :: tcoRuntimeManagedStrSlotsOf(rest)(activeSlots)
                | None -> tcoRuntimeManagedStrSlotsOf(rest)(activeSlots)
        | _candidate :: rest -> tcoRuntimeManagedStrSlotsOf(rest)(activeSlots)

// The entry normalization of every runtime-managed parameter, in parameter order: a `Str` copies
// out as one allocation and sets its active flag, a list copies its spine with its heads and
// sets its active flag, and a copy ADT copies its cell whole and sets its active flag.
let recursive tcoEntryNormalizationEntries (candidates: List(TcoManagedCandidate)) (managedLists: List((Int, Int, SemanticType))) (managedAdts: List((Int, Int, SemanticType, Str))) (managedStrs: List((Int, Int))) (state: CoreLoweringState) =
    match candidates with
        | [] -> []
        | candidate :: rest ->
            match (lookupManagedStrSlot(candidate.slot)(managedStrs), lookupRuntimeManagedListSlot(candidate.slot)(managedLists), lookupRuntimeManagedAdtSlot(candidate.slot)(managedAdts)) with
                | (Some(activeSlot), _managed, _adt) -> (candidate.slot, LeafArgumentCopy(-1), Some(activeSlot)) :: tcoEntryNormalizationEntries(rest)(managedLists)(managedAdts)(managedStrs)(state)
                | (None, Some((activeSlot, element)), _adt) ->
                    match tcoListCopyPlanOf(element)(state) with
                        | Some(plan) -> (candidate.slot, plan, Some(activeSlot)) :: tcoEntryNormalizationEntries(rest)(managedLists)(managedAdts)(managedStrs)(state)
                        | None -> tcoEntryNormalizationEntries(rest)(managedLists)(managedAdts)(managedStrs)(state)
                | (None, None, Some((activeSlot, semanticType, _typeName))) -> (candidate.slot, tcoAdtCopyPlanOf(semanticType)(state), Some(activeSlot)) :: tcoEntryNormalizationEntries(rest)(managedLists)(managedAdts)(managedStrs)(state)
                | _ -> tcoEntryNormalizationEntries(rest)(managedLists)(managedAdts)(managedStrs)(state)

let recursive loadedSlotOfTemp (temp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> None
        | IrInstruction { instruction = LoadLocal(target, slot) } :: rest ->
            if target == temp
            then Some(slot)
            else loadedSlotOfTemp(temp)(rest)
        | _instruction :: rest -> loadedSlotOfTemp(temp)(rest)

// A body result that is the direct read of a runtime-managed list slot transfers that slot's
// reference to the caller (the exit check below skips its drop), so the function's result is a
// reference-counted value and the closure carrying it says so.
let managedSlotResult (slot: Int) (managedLists: List((Int, Int, SemanticType))) (managedAdts: List((Int, Int, SemanticType, Str))) (managedStrs: List((Int, Int))) =
    match (lookupRuntimeManagedListSlot(slot)(managedLists), lookupRuntimeManagedAdtSlot(slot)(managedAdts), lookupManagedStrSlot(slot)(managedStrs)) with
        | (Some(_managed), _adt, _str) -> true
        | (None, Some(_adt), _str) -> true
        | (None, None, Some(_str)) -> true
        | _ -> false

let recursive storedTempsOfSlot (slot: Int) (instructions: List(IrInstruction)) (found: List(Int)) =
    match instructions with
        | [] -> found
        | IrInstruction { instruction = StoreLocal(target, temp) } :: rest ->
            storedTempsOfSlot(slot)(rest)(if target == slot
            then temp :: found
            else found)
        | _instruction :: rest -> storedTempsOfSlot(slot)(rest)(found)

// The back edge's synthetic zero (`emitBackEdgeDummy`): a join store that never runs and owns
// nothing.
let recursive isZeroConstantTemp (temp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = LoadConstInt(target, value) } :: rest -> target == temp && value == 0 || isZeroConstantTemp(temp)(rest)
        | _instruction :: rest -> isZeroConstantTemp(temp)(rest)

// Whether every value a join slot is stored is a reference-counted one: a runtime-managed temp,
// the direct read of a runtime-managed slot, the back edge's synthetic zero, or the reload of a
// nested join whose own stores all are (an `if` whose branches both take the back edge inside a
// `match` arm). `visited` holds the join slots already on the walk, so a slot stored from a
// reload of itself proves nothing.
let recursive joinStoresAreRuntimeManaged (temps: List(Int)) (visited: List(Int)) (managedLists: List((Int, Int, SemanticType))) (managedAdts: List((Int, Int, SemanticType, Str))) (managedStrs: List((Int, Int))) (state: CoreLoweringState) =
    match temps with
        | [] -> true
        | temp :: rest ->
            (isRuntimeTemp(temp)(state) || isZeroConstantTemp(temp)(state.reversedInstructions) || (match loadedSlotOfTemp(temp)(state.reversedInstructions) with
                | Some(slot) -> slotJoinIsRuntimeManaged(slot)(visited)(managedLists)(managedAdts)(managedStrs)(state)
                | None -> false)) && joinStoresAreRuntimeManaged(rest)(visited)(managedLists)(managedAdts)(managedStrs)(state)
and slotJoinIsRuntimeManaged (slot: Int) (visited: List(Int)) (managedLists: List((Int, Int, SemanticType))) (managedAdts: List((Int, Int, SemanticType, Str))) (managedStrs: List((Int, Int))) (state: CoreLoweringState) =
    managedSlotResult(slot)(managedLists)(managedAdts)(managedStrs) || !containsInt(slot)(visited) && (match storedTempsOfSlot(slot)(state.reversedInstructions)([]) with
        | [] -> false
        | temps -> joinStoresAreRuntimeManaged(temps)(slot :: visited)(managedLists)(managedAdts)(managedStrs)(state))

// A body result that is the direct read of a runtime-managed slot, or the reload of a join every
// branch stored such a read (or a runtime-managed value) into, transfers a slot's reference to
// the caller (the exit check skips its drop), so the function's result is a reference-counted
// value and the closure carrying it says so.
let markRuntimeListResult (bodyTemp: Int) (managedLists: List((Int, Int, SemanticType))) (managedAdts: List((Int, Int, SemanticType, Str))) (managedStrs: List((Int, Int))) (state: CoreLoweringState) =
    match loadedSlotOfTemp(bodyTemp)(state.reversedInstructions) with
        | None -> state
        | Some(slot) ->
            if slotJoinIsRuntimeManaged(slot)([])(managedLists)(managedAdts)(managedStrs)(state)
            then markRuntimeTemp(bodyTemp)(RuntimeNewlyProduced)(state)
            else state

// The temps the affine append promotion follows from a runtime-managed `Str` slot's reads: the
// slot's loads, their borrows and duplicates, and the in-place appends rooted at them (stage 0's
// `LowerLambdaCoreRefreshRuntimeManagedTcoResult` walk).
let recursive promoteAffineAppendInstructions (instructions: List(IrInstruction)) (strSlots: List(Int)) (managedTemps: List(Int)) (promoted: List(Int)) (reversedRewritten: List(IrInstruction)) =
    match instructions with
        | [] -> (reversedRewritten, promoted)
        | (IrInstruction { instruction = LoadLocal(target, slot) } as instruction) :: rest ->
            promoteAffineAppendInstructions(rest)(strSlots)(if containsInt(slot)(strSlots)
            then target :: managedTemps
            else managedTemps)(promoted)(instruction :: reversedRewritten)
        | (IrInstruction { instruction = Borrow(target, source) } as instruction) :: rest ->
            promoteAffineAppendInstructions(rest)(strSlots)(if containsInt(source)(managedTemps)
            then target :: managedTemps
            else managedTemps)(promoted)(instruction :: reversedRewritten)
        | (IrInstruction { instruction = RcDup(target, source, _runtimeManaged, _mayBeEmpty) } as instruction) :: rest ->
            promoteAffineAppendInstructions(rest)(strSlots)(if containsInt(source)(managedTemps)
            then target :: managedTemps
            else managedTemps)(promoted)(instruction :: reversedRewritten)
        | (IrInstruction { instruction = ConcatStrTip(target, left, right, reservationStart, reservationEnd, runtimeManaged) } as instruction) :: rest ->
            if containsInt(left)(managedTemps)
            then
                promoteAffineAppendInstructions(rest)(strSlots)(target :: managedTemps)(if runtimeManaged
                then promoted
                else target :: promoted)((instruction with instruction = ConcatStrTip(target)(left)(right)(reservationStart)(reservationEnd)(true)) :: reversedRewritten)
            else promoteAffineAppendInstructions(rest)(strSlots)(managedTemps)(promoted)(instruction :: reversedRewritten)
        | instruction :: rest -> promoteAffineAppendInstructions(rest)(strSlots)(managedTemps)(promoted)(instruction :: reversedRewritten)

let recursive markPromotedAppends (temps: List(Int)) (state: CoreLoweringState) =
    match temps with
        | [] -> state
        | temp :: rest ->
            state
            |> markRuntimeTemp(temp)(RuntimeNewlyProduced)
            |> markPromotedAppends(rest)

let recursive managedStrSlotsOnly (entries: List((Int, Int))) =
    match entries with
        | [] -> []
        | (slot, _activeSlot) :: rest -> slot :: managedStrSlotsOnly(rest)

// Stage 0's `PromoteRuntimeManagedStringConcats`: an in-place append whose accumulator is a
// runtime-managed `Str` slot's value grows a reference-counted reservation, so the instruction
// is flagged runtime-managed and its result is a newly produced reference-counted value (the
// back edge stores it as the parameter's own successor and the exit may hand it to the caller).
let promoteAffineAppends (managedStrs: List((Int, Int))) (state: CoreLoweringState) =
    match managedStrs with
        | [] -> state
        | _entries ->
            match promoteAffineAppendInstructions(reverse(state.reversedInstructions))(managedStrSlotsOnly(managedStrs))([])([])([]) with
                | (reversedRewritten, promoted) -> markPromotedAppends(promoted)((state with reversedInstructions = reversedRewritten))

// Stage 0's `LowerLambdaCoreEmitRuntimeManagedTcoExitDrops` for one list slot when the body
// result may be one of the runtime-managed lists: the slot's value is compared with the result
// and, when they match and no earlier slot already transferred, the reference is transferred to
// the caller instead of released.
let emitTcoListExitTransferCheck (bodyTemp: Int) (transferSelectedSlot: Int) (zeroTemp: Int) (slot: Int) (activeSlot: Int) (elementType: SemanticType) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = sourceState, temp = sourceTemp } ->
            match freshTemp(sourceState) with
                | FreshTemp { state = activeState, temp = activeTemp } ->
                    match freshTemp(activeState) with
                        | FreshTemp { state = matchesState, temp = matchesTemp } ->
                            match freshTemp(matchesState) with
                                | FreshTemp { state = selectedState, temp = selectedTemp } ->
                                    match freshTemp(selectedState) with
                                        | FreshTemp { state = availableState, temp = availableTemp } ->
                                            match freshTemp(availableState) with
                                                | FreshTemp { state = activeMatchState, temp = activeMatchTemp } ->
                                                    match freshTemp(activeMatchState) with
                                                        | FreshTemp { state = canTransferState, temp = canTransferTemp } ->
                                                            match freshLabel("rc_tco_exit_transfer_not_selected")(canTransferState) with
                                                                | FreshLabel { state = dropLabelState, label = dropLabel } ->
                                                                    match freshLabel("rc_tco_exit_transfer_done")(dropLabelState) with
                                                                        | FreshLabel { state = doneLabelState, label = doneLabel } ->
                                                                            match freshTemp(doneLabelState) with
                                                                                | FreshTemp { state = oneState, temp = oneTemp } ->
                                                                                    oneState
                                                                                    |> emit(LoadLocal(sourceTemp)(slot))
                                                                                    |> emit(LoadLocal(activeTemp)(activeSlot))
                                                                                    |> emit(CmpIntEq(matchesTemp)(sourceTemp)(bodyTemp))
                                                                                    |> emit(LoadLocal(selectedTemp)(transferSelectedSlot))
                                                                                    |> emit(CmpIntEq(availableTemp)(selectedTemp)(zeroTemp))
                                                                                    |> emit(AndInt(activeMatchTemp)(activeTemp)(matchesTemp))
                                                                                    |> emit(AndInt(canTransferTemp)(activeMatchTemp)(availableTemp))
                                                                                    |> emit(JumpIfFalse(canTransferTemp)(dropLabel))
                                                                                    |> emit(LoadConstInt(oneTemp)(1))
                                                                                    |> emit(StoreLocal(transferSelectedSlot)(oneTemp))
                                                                                    |> emit(Jump(doneLabel))
                                                                                    |> emit(Label(dropLabel))
                                                                                    |> emitGuardedListRelease(emit)("rc_tco_exit_drop_inactive")(activeSlot)(sourceTemp)(elementType)
                                                                                    |> emit(Label(doneLabel))

let emitTcoListExitDrop (slot: Int) (activeSlot: Int) (elementType: SemanticType) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = sourceState, temp = sourceTemp } ->
            sourceState
            |> emit(LoadLocal(sourceTemp)(slot))
            |> emitGuardedListRelease(emit)("rc_tco_exit_drop_inactive")(activeSlot)(sourceTemp)(elementType)

let recursive emitTcoListExitDropsWith (bodyTemp: Int) (transfer: Maybe((Int, Int))) (entries: List((Int, Int, SemanticType))) (state: CoreLoweringState) =
    match entries with
        | [] -> state
        | (slot, activeSlot, elementType) :: rest ->
            match transfer with
                | Some((transferSelectedSlot, zeroTemp)) ->
                    state
                    |> emitTcoListExitTransferCheck(bodyTemp)(transferSelectedSlot)(zeroTemp)(slot)(activeSlot)(elementType)
                    |> emitTcoListExitDropsWith(bodyTemp)(transfer)(rest)
                | None ->
                    state
                    |> emitTcoListExitDrop(slot)(activeSlot)(elementType)
                    |> emitTcoListExitDropsWith(bodyTemp)(transfer)(rest)

// The exit transfer check of one runtime-managed copy-ADT slot, the list check's shape with the
// cell's own release: the slot's value is compared with the body result and, when they match
// and no earlier slot already transferred, the reference goes to the caller instead of being
// released.
let emitTcoAdtExitTransferCheck (bodyTemp: Int) (transferSelectedSlot: Int) (zeroTemp: Int) (slot: Int) (activeSlot: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = sourceState, temp = sourceTemp } ->
            match freshTemp(sourceState) with
                | FreshTemp { state = activeState, temp = activeTemp } ->
                    match freshTemp(activeState) with
                        | FreshTemp { state = matchesState, temp = matchesTemp } ->
                            match freshTemp(matchesState) with
                                | FreshTemp { state = selectedState, temp = selectedTemp } ->
                                    match freshTemp(selectedState) with
                                        | FreshTemp { state = availableState, temp = availableTemp } ->
                                            match freshTemp(availableState) with
                                                | FreshTemp { state = activeMatchState, temp = activeMatchTemp } ->
                                                    match freshTemp(activeMatchState) with
                                                        | FreshTemp { state = canTransferState, temp = canTransferTemp } ->
                                                            match freshLabel("rc_tco_exit_transfer_not_selected")(canTransferState) with
                                                                | FreshLabel { state = dropLabelState, label = dropLabel } ->
                                                                    match freshLabel("rc_tco_exit_transfer_done")(dropLabelState) with
                                                                        | FreshLabel { state = doneLabelState, label = doneLabel } ->
                                                                            match freshTemp(doneLabelState) with
                                                                                | FreshTemp { state = oneState, temp = oneTemp } ->
                                                                                    oneState
                                                                                    |> emit(LoadLocal(sourceTemp)(slot))
                                                                                    |> emit(LoadLocal(activeTemp)(activeSlot))
                                                                                    |> emit(CmpIntEq(matchesTemp)(sourceTemp)(bodyTemp))
                                                                                    |> emit(LoadLocal(selectedTemp)(transferSelectedSlot))
                                                                                    |> emit(CmpIntEq(availableTemp)(selectedTemp)(zeroTemp))
                                                                                    |> emit(AndInt(activeMatchTemp)(activeTemp)(matchesTemp))
                                                                                    |> emit(AndInt(canTransferTemp)(activeMatchTemp)(availableTemp))
                                                                                    |> emit(JumpIfFalse(canTransferTemp)(dropLabel))
                                                                                    |> emit(LoadConstInt(oneTemp)(1))
                                                                                    |> emit(StoreLocal(transferSelectedSlot)(oneTemp))
                                                                                    |> emit(Jump(doneLabel))
                                                                                    |> emit(Label(dropLabel))
                                                                                    |> emitGuardedAdtRelease(emit)("rc_tco_exit_drop_inactive")(activeSlot)(sourceTemp)(semanticType)
                                                                                    |> emit(Label(doneLabel))

let emitTcoAdtExitDrop (slot: Int) (activeSlot: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = sourceState, temp = sourceTemp } ->
            sourceState
            |> emit(LoadLocal(sourceTemp)(slot))
            |> emitGuardedAdtRelease(emit)("rc_tco_exit_drop_inactive")(activeSlot)(sourceTemp)(semanticType)

let recursive emitTcoAdtExitDropsWith (bodyTemp: Int) (transfer: Maybe((Int, Int))) (entries: List((Int, Int, SemanticType, Str))) (state: CoreLoweringState) =
    match entries with
        | [] -> state
        | (slot, activeSlot, semanticType, _typeName) :: rest ->
            match transfer with
                | Some((transferSelectedSlot, zeroTemp)) ->
                    state
                    |> emitTcoAdtExitTransferCheck(bodyTemp)(transferSelectedSlot)(zeroTemp)(slot)(activeSlot)(semanticType)
                    |> emitTcoAdtExitDropsWith(bodyTemp)(transfer)(rest)
                | None ->
                    state
                    |> emitTcoAdtExitDrop(slot)(activeSlot)(semanticType)
                    |> emitTcoAdtExitDropsWith(bodyTemp)(transfer)(rest)

let recursive anyEntryOnSlot (slot: Int) (entries: List((Int, ArgumentCopyPlan, Maybe(Int)))) =
    match entries with
        | [] -> false
        | (candidate, _plan, _activeSlot) :: rest -> candidate == slot || anyEntryOnSlot(slot)(rest)

// A loop function whose direct argument (local slot 1) is normalized at entry reads the caller's
// hidden ownership flag, so its closure advertises that it accepts a reference-counted argument
// (stage 0's `RecordRuntimeNormalizedTcoParamLabel`).
let recordTcoNormalizedArgumentLabel (label: Str) (entries: List((Int, ArgumentCopyPlan, Maybe(Int)))) (state: CoreLoweringState) =
    if anyEntryOnSlot(1)(entries)
    then recordRuntimeNormalizedArgument(label)(state)
    else state

let recursive candidateStrManaged (slot: Int) (candidates: List(TcoManagedCandidate)) =
    match candidates with
        | [] -> false
        | TcoManagedCandidate { slot = candidate, strManaged = strManaged } :: rest ->
            if candidate == slot
            then strManaged
            else candidateStrManaged(slot)(rest)

// A pattern owner's root parameter placed on the reference-counted heap: a runtime-managed list
// slot, an ADT-style slot (a record, a tuple, a string, or a freshly rebuilt list), or a
// runtime-managed `Str` candidate.
let patternSiteRootManaged (site: PatternOwnerSite) (managedLists: List((Int, Int, SemanticType))) (managedAdts: List((Int, Int, SemanticType, Str))) (candidates: List(TcoManagedCandidate)) = managedSlotResult(site.siteRootSlot)(managedLists)(managedAdts)([]) || candidateStrManaged(site.siteRootSlot)(candidates)

// Stage 0's `ResolvePatternBindingPlacementOutcome`: a copy-typed binding owns nothing; a
// binding whose root parameter lives on the reference-counted heap, or whose own type is
// reference-counted whatever holds it (outside a parallel worker), gets its protective owner, as
// does one bound out of a tuple matched on the reference-counted heap, which descends from the
// tuple's counted copy of an arena-placed root; any other binding of an arena-placed root keeps its
// identity markers.
let patternSitePlaced (site: PatternOwnerSite) (managedLists: List((Int, Int, SemanticType))) (managedAdts: List((Int, Int, SemanticType, Str))) (candidates: List(TcoManagedCandidate)) (state: CoreLoweringState) =
    site.siteType
    |> resolveType(state)
    |> (given (resolved: SemanticType) -> !resultSurvivesReset(resolved)(state) && (patternSiteRootManaged(site)(managedLists)(managedAdts)(candidates) || site.siteHeldByTuple || isUnconditionallyRcManagedType(resolved) && canTestRepresentation(state)))

let recursive placedPatternOwnerSites (sites: List(PatternOwnerSite)) (managedLists: List((Int, Int, SemanticType))) (managedAdts: List((Int, Int, SemanticType, Str))) (candidates: List(TcoManagedCandidate)) (state: CoreLoweringState) =
    match sites with
        | [] -> []
        | site :: rest ->
            if patternSitePlaced(site)(managedLists)(managedAdts)(candidates)(state)
            then site :: placedPatternOwnerSites(rest)(managedLists)(managedAdts)(candidates)(state)
            else placedPatternOwnerSites(rest)(managedLists)(managedAdts)(candidates)(state)

// The temps aliasing a pattern owner's slot value, in the function's instruction order: every
// load of the slot, and every borrow or duplicate of an alias (a temp is defined before it is
// read, so one forward pass sees every alias).
let recursive patternOwnerAliases (instructions: List(IrInstruction)) (slot: Int) (aliases: List(Int)) =
    match instructions with
        | [] -> aliases
        | IrInstruction { instruction = LoadLocal(target, loaded) } :: rest ->
            patternOwnerAliases(rest)(slot)(if loaded == slot
            then target :: aliases
            else aliases)
        | IrInstruction { instruction = Borrow(target, source) } :: rest ->
            patternOwnerAliases(rest)(slot)(if containsInt(source)(aliases)
            then target :: aliases
            else aliases)
        | IrInstruction { instruction = RcDup(target, source, _runtimeManaged, _mayBeEmpty) } :: rest ->
            patternOwnerAliases(rest)(slot)(if containsInt(source)(aliases)
            then target :: aliases
            else aliases)
        | _ :: rest -> patternOwnerAliases(rest)(slot)(aliases)

// Names the structural release helper for a value of `semanticType` (stage 0's
// `SynthesizeStructuralOwnerDropper`), synthesizing it and any ADT dropper it calls into the
// program once per type through the state's label cache; `None` when the value's release is a
// single allocation.
let synthesizeStructuralDropperLabel (semanticType: SemanticType) (state: CoreLoweringState) =
    match state with
        | CoreLoweringState { functions = functions, nextLambdaId = lambdaId, nextLabelId = labelId, programState = CoreProgramState { dropperLabels = cache } } ->
            match synthesizeStructuralOwnerDropper(resolveType(state)(semanticType))(dropperTypesOf(state))(cache)(lambdaId)(labelId) with
                | DropperSynthesis { label = label, cache = nextCache, queryContext = queryContext, functions = synthesized, nextLambdaId = nextLambdaId, nextLabelId = nextLabelId } ->
                    (label, withStateDropperResult(nextCache)(queryContext)((state with functions = append(synthesized
                    |> map(locateSynthesizedFunction(state))
                    |> reverse)(functions), nextLambdaId = nextLambdaId, nextLabelId = nextLabelId)))

// Stage 0's `PromotePatternBindingOwnerMarkers`: every identity duplicate of an alias becomes a
// real retain, and the owner's release marker a real runtime-managed release under the resolved
// type name, naming the structural dropper of a value whose release reaches past its own cell
// (the marker is one instruction, so an aggregate owner's walk lives behind that label). A release
// marker of a duplicate a scope's copy took over (`released`) is promoted with that duplicate.
let recursive promotePatternOwnerInstructions (instructions: List(IrInstruction)) (slot: Int) (aliases: List(Int)) (released: List(Int)) (typeName: Str) (mayBeEmpty: Bool) (dropper: Maybe(Str)) =
    match instructions with
        | [] -> []
        | (IrInstruction { instruction = RcDup(target, source, runtimeManaged, _mayBeEmpty) } as instruction) :: rest ->
            (if containsInt(source)(aliases) && !runtimeManaged
            then instruction with instruction = RcDup(target)(source)(true)(mayBeEmpty)
            else instruction) :: promotePatternOwnerInstructions(rest)(slot)(aliases)(released)(typeName)(mayBeEmpty)(dropper)
        | (IrInstruction { instruction = RcDrop(source, markerTypeName, ownerSlot, runtimeManaged, _mayBeEmpty, _dropper) } as instruction) :: rest ->
            (if ownerSlot == slot
            then instruction with instruction = RcDrop(source)(typeName)(ownerSlot)(true)(mayBeEmpty)(dropper)
            else
                if !runtimeManaged && containsInt(source)(released) && containsInt(source)(aliases)
                then instruction with instruction = RcDrop(source)(markerTypeName)(ownerSlot)(true)(mayBeEmpty)(dropper)
                else instruction) :: promotePatternOwnerInstructions(rest)(slot)(aliases)(released)(typeName)(mayBeEmpty)(dropper)
        | instruction :: rest -> instruction :: promotePatternOwnerInstructions(rest)(slot)(aliases)(released)(typeName)(mayBeEmpty)(dropper)

let recursive markRuntimeTemps (temps: List(Int)) (state: CoreLoweringState) =
    match temps with
        | [] -> state
        | temp :: rest ->
            state
            |> markRuntimeTemp(temp)(RuntimeTransferred)
            |> markRuntimeTemps(rest)

// The dropper is synthesized before the rewrite, which scans the instruction list the synthesis
// would otherwise extend.
let promotePatternOwnerMarkers (site: PatternOwnerSite) (state: CoreLoweringState) =
    match (resolveType(state)(site.siteType), synthesizeStructuralDropperLabel(site.siteType)(state)) with
        | (resolved, (dropper, synthesized)) ->
            match reverse(synthesized.reversedInstructions) with
                | instructions ->
                    []
                    |> patternOwnerAliases(instructions)(site.siteSlot)
                    |> (given (aliases: List(Int)) ->
                        synthesized
                        |> markRuntimeTemps(aliases)
                        |> (given (marked: CoreLoweringState) ->
                            marked with reversedInstructions = reverse(promotePatternOwnerInstructions(instructions)(site.siteSlot)(aliases)(stateReleasedPatternOwnerDuplicates(marked))(patternOwnerTypeName(resolved)(marked))(mayBeEmptyList(resolved))(dropper))))

let recursive promotePatternOwnerSites (sites: List(PatternOwnerSite)) (state: CoreLoweringState) =
    match sites with
        | [] -> state
        | site :: rest ->
            state
            |> promotePatternOwnerMarkers(site)
            |> promotePatternOwnerSites(rest)

// Stage 0's `EmitPerceusPatternBindingOwnerDup`, spliced in right after the arm's pattern: the
// slot is reloaded, retained, and rebound to the retained reference.
let splicePatternOwnerDup (site: PatternOwnerSite) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = valueState, temp = valueTemp } ->
            match freshTemp(valueState) with
                | FreshTemp { state = protectedState, temp = protectedTemp } ->
                    (protectedState with reversedInstructions = [])
                    |> emit(LoadLocal(valueTemp)(site.siteSlot))
                    |> unlocatedInstruction(site.siteType
                    |> resolveType(state)
                    |> mayBeEmptyList
                    |> RcDup(protectedTemp)(valueTemp)(true))
                    |> emit(StoreLocal(site.siteSlot)(protectedTemp))
                    |> (given (generated: CoreLoweringState) -> generated with reversedInstructions = spliceGeneratedInstructions(length(state.reversedInstructions) - site.siteInsertCount)([])(state.reversedInstructions)(generated.reversedInstructions))

// The sites are spliced most recent first, so an earlier site's count still names its own point.
let recursive splicePatternOwnerDups (sites: List(PatternOwnerSite)) (state: CoreLoweringState) =
    match sites with
        | [] -> state
        | site :: rest ->
            state
            |> splicePatternOwnerDup(site)
            |> splicePatternOwnerDups(rest)

let recursive isPromotedMarker (marker: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = RcDup(target, _source, runtimeManaged, _mayBeEmpty) } :: rest -> target == marker && runtimeManaged || isPromotedMarker(marker)(rest)
        | _instruction :: rest -> isPromotedMarker(marker)(rest)

let recursive storeMarkerIntoSlot (slot: Int) (marker: Int) (zeroTemp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> []
        | (IrInstruction { instruction = StoreLocal(storeSlot, source) } as instruction) :: rest ->
            if storeSlot == slot && source == zeroTemp
            then (instruction with instruction = StoreLocal(slot)(marker)) :: rest
            else instruction :: storeMarkerIntoSlot(slot)(marker)(zeroTemp)(rest)
        | instruction :: rest -> instruction :: storeMarkerIntoSlot(slot)(marker)(zeroTemp)(rest)

// Stage 0's `SettleHandedOverMarkerSlots` for arena cells: once the pattern owners are placed, a
// marker handed to an owned slot that became a real retain is stored into it; one that stayed an
// identity took no reference.
let recursive settleCellMarkerSlots (slots: List((Int, Int, Int))) (state: CoreLoweringState) =
    match slots with
        | [] -> withStateCellMarkerSlots([])(state)
        | (slot, marker, zeroTemp) :: rest ->
            if isPromotedMarker(marker)(state.reversedInstructions)
            then settleCellMarkerSlots(rest)((state with reversedInstructions = storeMarkerIntoSlot(slot)(marker)(zeroTemp)(state.reversedInstructions)))
            else settleCellMarkerSlots(rest)(state)

// The temps of the reference-counted allocations the function makes.
let recursive countedAllocations (instructions: List(IrInstruction)) (allocations: List(Int)) =
    match instructions with
        | [] -> allocations
        | IrInstruction { instruction = Alloc(target, _size, true) } :: rest -> countedAllocations(rest)(target :: allocations)
        | IrInstruction { instruction = AllocAdt(target, _tag, _fieldCount, true, _tagless) } :: rest -> countedAllocations(rest)(target :: allocations)
        | _instruction :: rest -> countedAllocations(rest)(allocations)

// The values stored into those allocations' fields.
let recursive storedIntoCounted (instructions: List(IrInstruction)) (allocations: List(Int)) (stored: List(Int)) =
    match instructions with
        | [] -> stored
        | IrInstruction { instruction = StoreMemOffset(base, _offset, source) } :: rest ->
            storedIntoCounted(rest)(allocations)(if containsInt(base)(allocations)
            then source :: stored
            else stored)
        | IrInstruction { instruction = SetAdtField(base, _field, source, _tagless) } :: rest ->
            storedIntoCounted(rest)(allocations)(if containsInt(base)(allocations)
            then source :: stored
            else stored)
        | _instruction :: rest -> storedIntoCounted(rest)(allocations)(stored)

// The identity duplicates of a pattern owner's aliases a reference-counted aggregate stores.
let recursive aggregateMarkersOf (instructions: List(IrInstruction)) (aliases: List(Int)) (stored: List(Int)) (markers: List((Int, Int))) =
    match instructions with
        | [] -> markers
        | IrInstruction { instruction = RcDup(target, source, false, _mayBeEmpty) } :: rest ->
            aggregateMarkersOf(rest)(aliases)(stored)(if containsInt(source)(aliases) && containsInt(target)(stored)
            then (target, source) :: markers
            else markers)
        | _instruction :: rest -> aggregateMarkersOf(rest)(aliases)(stored)(markers)

let recursive lookupMarkerRetain (target: Int) (retains: List((Int, Int, Int, Int, Str))) =
    match retains with
        | [] -> None
        | ((marker, _source, _countedTemp, _retainTemp, _skipLabel) as retain) :: rest ->
            if marker == target
            then Some(retain)
            else lookupMarkerRetain(target)(rest)

// Each marker is followed by its guarded retain: a reference-counted value is retained, an arena
// one (which a runtime retain would corrupt) is left as it is.
let recursive insertMarkerRetains (instructions: List(IrInstruction)) (retains: List((Int, Int, Int, Int, Str))) =
    match instructions with
        | [] -> []
        | (IrInstruction { instruction = RcDup(target, _source, false, _mayBeEmpty), location = location } as instruction) :: rest ->
            match lookupMarkerRetain(target)(retains) with
                | Some((_marker, source, countedTemp, retainTemp, skipLabel)) -> instruction :: IrInstruction(instruction = IsReferenceCounted(countedTemp)(source), location = location) :: IrInstruction(instruction = JumpIfFalse(countedTemp)(skipLabel), location = location) :: IrInstruction(instruction = RcDup(retainTemp)(source)(true)(false), location = location) :: IrInstruction(instruction = Label(skipLabel), location = location) :: insertMarkerRetains(rest)(retains)
                | None -> instruction :: insertMarkerRetains(rest)(retains)
        | instruction :: rest -> instruction :: insertMarkerRetains(rest)(retains)

let recursive plannedMarkerRetains (markers: List((Int, Int))) (state: CoreLoweringState) (retains: List((Int, Int, Int, Int, Str))) =
    match markers with
        | [] -> (state, retains)
        | (marker, source) :: rest ->
            match freshTempRun(2)(state) with
                | FreshTemp { state = allocated, temp = countedTemp } ->
                    match freshLabel("pattern_owner_aggregate_retain_skip")(allocated) with
                        | FreshLabel { state = labelled, label = skipLabel } -> plannedMarkerRetains(rest)(labelled)((marker, source, countedTemp, countedTemp + 1, skipLabel) :: retains)

// A pattern owner whose root parameter stays in the arena keeps its identity markers, but a
// reference-counted aggregate that stores one of them releases it with itself, and a branch that
// hands one on as its result (a join the result's owner releases) releases it with that owner: such a marker is
// followed by a real retain of a reference-counted value, so the aggregate owns its child whatever
// the root's placement.
let retainUnplacedAggregateMarkers (site: PatternOwnerSite) (state: CoreLoweringState) =
    (let instructions = reverse(state.reversedInstructions)
    in
        let aliases = patternOwnerAliases(instructions)(site.siteSlot)([])
        in
            let stored =
                storedIntoCounted(instructions)(countedAllocations(instructions)([]))([])
            in
                match aggregateMarkersOf(instructions)(aliases)(state
                |> statePatternOwnerResultTemps
                |> append(stored))([]) with
                    | [] -> state
                    | markers ->
                        match plannedMarkerRetains(markers)(state)([]) with
                            | (planned, retains) ->
                                planned with reversedInstructions = reverse(insertMarkerRetains(instructions)(retains)))

let recursive patternOwnerSiteIn (site: PatternOwnerSite) (sites: List(PatternOwnerSite)) =
    match sites with
        | [] -> false
        | candidate :: rest -> candidate.siteSlot == site.siteSlot && candidate.siteInsertCount == site.siteInsertCount || patternOwnerSiteIn(site)(rest)

let recursive retainUnplacedAggregateMarkersOf (sites: List(PatternOwnerSite)) (placed: List(PatternOwnerSite)) (state: CoreLoweringState) =
    match sites with
        | [] -> state
        | site :: rest ->
            if resultSurvivesReset(site.siteType)(state) || patternOwnerSiteIn(site)(placed)
            then retainUnplacedAggregateMarkersOf(rest)(placed)(state)
            else
                state
                |> retainUnplacedAggregateMarkers(site)
                |> retainUnplacedAggregateMarkersOf(rest)(placed)

// Stage 0's `FinalizePerceusPatternBindingOwners`, run once the loop's parameter placements are
// known and before the entry normalization is spliced in below every site.
let finalizePatternOwnerSites (managedLists: List((Int, Int, SemanticType))) (managedAdts: List((Int, Int, SemanticType, Str))) (candidates: List(TcoManagedCandidate)) (state: CoreLoweringState) =
    state
    |> placedPatternOwnerSites(statePatternOwnerSites(state))(managedLists)(managedAdts)(candidates)
    |> (given (placed: List(PatternOwnerSite)) ->
        state
        |> promotePatternOwnerSites(placed)
        |> (given (promoted: CoreLoweringState) ->
            settleCellMarkerSlots(stateCellMarkerSlots(promoted))(promoted))
        |> splicePatternOwnerDups(placed)
        |> retainUnplacedAggregateMarkersOf(statePatternOwnerSites(state))(placed)
        |> (given (finalized: CoreLoweringState) -> withStatePatternOwnerSites([])(finalized)))

// A loop parameter placed on the reference-counted heap: a runtime-managed list or ADT slot, or
// a runtime-managed `Str` candidate.
let tcoParameterPlaced (slot: Int) (managedLists: List((Int, Int, SemanticType))) (managedAdts: List((Int, Int, SemanticType, Str))) (candidates: List(TcoManagedCandidate)) = managedSlotResult(slot)(managedLists)(managedAdts)([]) || candidateStrManaged(slot)(candidates)

let recursive promotedRetainTemps (sites: List((Int, Int, SemanticType))) (managedLists: List((Int, Int, SemanticType))) (managedAdts: List((Int, Int, SemanticType, Str))) (candidates: List(TcoManagedCandidate)) (state: CoreLoweringState) =
    match sites with
        | [] -> []
        | (duplicate, slot, semanticType) :: rest ->
            if tcoParameterPlaced(slot)(managedLists)(managedAdts)(candidates)
            then
                (duplicate, semanticType
                |> resolveType(state)
                |> mayBeEmptyList) :: promotedRetainTemps(rest)(managedLists)(managedAdts)(candidates)(state)
            else promotedRetainTemps(rest)(managedLists)(managedAdts)(candidates)(state)

let recursive lookupPromotedRetain (temp: Int) (promoted: List((Int, Bool))) =
    match promoted with
        | [] -> None
        | (candidate, mayBeEmpty) :: rest ->
            if candidate == temp
            then Some(mayBeEmpty)
            else lookupPromotedRetain(temp)(rest)

let recursive promoteRetainInstructions (instructions: List(IrInstruction)) (promoted: List((Int, Bool))) =
    match instructions with
        | [] -> []
        | (IrInstruction { instruction = RcDup(target, source, false, _mayBeEmpty) } as instruction) :: rest ->
            (match lookupPromotedRetain(target)(promoted) with
                | Some(mayBeEmpty) -> instruction with instruction = RcDup(target)(source)(true)(mayBeEmpty)
                | None -> instruction) :: promoteRetainInstructions(rest)(promoted)
        | instruction :: rest -> instruction :: promoteRetainInstructions(rest)(promoted)

// Stage 0's `FinalizeTcoParameterAggregateRetains`: every loop-parameter retain marker of a
// parameter placed on the reference-counted heap becomes a real retain, guarded for a list that
// may be empty; the markers of an arena-placed parameter stay identities.
let promoteTcoParameterRetains (managedLists: List((Int, Int, SemanticType))) (managedAdts: List((Int, Int, SemanticType, Str))) (candidates: List(TcoManagedCandidate)) (state: CoreLoweringState) =
    match promotedRetainTemps(stateTcoParameterRetainSites(state))(managedLists)(managedAdts)(candidates)(state) with
        | [] -> withStateTcoParameterRetainSites([])(state)
        | promoted -> withStateTcoParameterRetainSites([])((state with reversedInstructions = promoteRetainInstructions(state.reversedInstructions)(promoted)))

// Whether an element of an arena tuple beside a generic sibling is copied into the arena: a
// resolved heap value the reference-counted heap may hold, other than one of the contract's types.
let demotesTupleElement (semanticType: SemanticType) (state: CoreLoweringState) = !resultSurvivesReset(semanticType)(state) && !containsUnresolvedLayout(semanticType)(state) && isGeneralRcAdmissible(semanticType)([])(state) && !isGeneralRcValueType(semanticType)(state)

// Whether a parameter an arena tuple stores is copied into the arena in place of its retain: a
// resolved heap value the reference-counted heap may hold, the contract's own types included.
let copiesArenaParameter (semanticType: SemanticType) (state: CoreLoweringState) =
    !resultSurvivesReset(semanticType)(state) && !containsUnresolvedLayout(semanticType)(state) && isGeneralRcAdmissible(semanticType)([])(state) && (match semanticType with
        | SemFunction(_parameter, _result, _row) -> false
        | _ -> true)

// The arena copy that replaces a promoted retain, emitted on its own and ending with the retain's
// own temp loaded from the copy's slot.
let arenaParameterCopyInstructions (duplicate: Int) (source: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match emitArenaCopyOfReferenceCounted(source)(semanticType)(false)((state with reversedInstructions = [])) with
        | (copied, copyTemp) ->
            match freshLocal(copied) with
                | FreshLocal { state = slotted, local = copySlot } ->
                    slotted
                    |> emit(StoreLocal(copySlot)(copyTemp))
                    |> emit(LoadLocal(duplicate)(copySlot))

let recursive spliceArenaParameterCopy (duplicate: Int) (copy: List(IrInstruction)) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> []
        | (IrInstruction { instruction = RcDup(target, _source, true, _mayBeEmpty) } as instruction) :: rest ->
            if target == duplicate
            then append(copy)(rest)
            else instruction :: spliceArenaParameterCopy(duplicate)(copy)(rest)
        | instruction :: rest -> instruction :: spliceArenaParameterCopy(duplicate)(copy)(rest)

let recursive copyArenaParameterRetains (pending: List((Int, Int, Int, SemanticType))) (managedLists: List((Int, Int, SemanticType))) (managedAdts: List((Int, Int, SemanticType, Str))) (candidates: List(TcoManagedCandidate)) (state: CoreLoweringState) =
    match pending with
        | [] -> state
        | (duplicate, source, slot, semanticType) :: rest ->
            copyArenaParameterRetains(rest)(managedLists)(managedAdts)(candidates)((if tcoParameterPlaced(slot)(managedLists)(managedAdts)(candidates) && copiesArenaParameter(resolveType(state)(semanticType))(state)
            then
                match arenaParameterCopyInstructions(duplicate)(source)(resolveType(state)(semanticType))(state) with
                    | copied -> copied with reversedInstructions = spliceArenaParameterCopy(duplicate)(copied.reversedInstructions)(state.reversedInstructions)
            else state))

// Stage 0's `FinalizeArenaParameterCopies`: the promoted retain of a parameter an arena tuple
// stores becomes an arena copy of the parameter once its type allows one, so the tuple holds no
// reference nothing releases. It runs once the loop's result is finished, whose copy out of the
// arena withdraws the retains its cells were built with: a withdrawn retain needs no copy.
let copyArenaTupleParameters (managedLists: List((Int, Int, SemanticType))) (managedAdts: List((Int, Int, SemanticType, Str))) (candidates: List(TcoManagedCandidate)) (state: CoreLoweringState) =
    state
    |> copyArenaParameterRetains(stateArenaParameterCopies(state))(managedLists)(managedAdts)(candidates)
    |> withStateArenaParameterCopies([])

let recursive unadmittedPendingFlags (flags: List((Int, Int))) (managedLists: List((Int, Int, SemanticType))) (managedAdts: List((Int, Int, SemanticType, Str))) (candidates: List(TcoManagedCandidate)) =
    match flags with
        | [] -> []
        | (flagTemp, rootSlot) :: rest ->
            if tcoParameterPlaced(rootSlot)(managedLists)(managedAdts)(candidates)
            then unadmittedPendingFlags(rest)(managedLists)(managedAdts)(candidates)
            else flagTemp :: unadmittedPendingFlags(rest)(managedLists)(managedAdts)(candidates)

let recursive zeroPendingFlagInstructions (instructions: List(IrInstruction)) (flags: List(Int)) =
    match instructions with
        | [] -> []
        | (IrInstruction { instruction = AndInt(target, _left, _right) } as instruction) :: rest ->
            (if containsInt(target)(flags)
            then instruction with instruction = LoadConstInt(target)(0)
            else instruction) :: zeroPendingFlagInstructions(rest)(flags)
        | (IrInstruction { instruction = LoadConstInt(target, _value) } as instruction) :: rest ->
            (if containsInt(target)(flags)
            then instruction with instruction = LoadConstInt(target)(0)
            else instruction) :: zeroPendingFlagInstructions(rest)(flags)
        | (IrInstruction { instruction = CmpIntNe(target, _left, _right) } as instruction) :: rest ->
            (if containsInt(target)(flags)
            then instruction with instruction = LoadConstInt(target)(0)
            else instruction) :: zeroPendingFlagInstructions(rest)(flags)
        | instruction :: rest -> instruction :: zeroPendingFlagInstructions(rest)(flags)

// Stage 0's `ResolvePendingRuntimeArgumentFlags`: the ownership flag of a call argument that
// reads a loop parameter the frame did not admit to the reference-counted heap (or a pattern
// binding extracted from one) is rewritten to zero where it is defined, so the call neither
// retains the argument nor hands over a reference; the flag of an admitted parameter stays.
let resolvePendingArgumentFlags (managedLists: List((Int, Int, SemanticType))) (managedAdts: List((Int, Int, SemanticType, Str))) (candidates: List(TcoManagedCandidate)) (state: CoreLoweringState) =
    match unadmittedPendingFlags(statePendingRuntimeArgumentFlags(state))(managedLists)(managedAdts)(candidates) with
        | [] -> withStatePendingRuntimeArgumentFlags([])(state)
        | flags -> withStatePendingRuntimeArgumentFlags([])((state with reversedInstructions = zeroPendingFlagInstructions(state.reversedInstructions)(flags)))

// The exit releases in parameter order (stage 0's `RuntimeManagedSlotsInOrder`, the order the
// slots were admitted, parameter order for slots admitted together): a runtime-managed `Str`
// slot releases as a copy-ADT slot under the type name `String`.
let recursive emitTcoExitDropsInOrder (bodyTemp: Int) (transfer: Maybe((Int, Int))) (slots: List(Int)) (managedLists: List((Int, Int, SemanticType))) (managedAdts: List((Int, Int, SemanticType, Str))) (managedStrs: List((Int, Int))) (state: CoreLoweringState) =
    match slots with
        | [] -> state
        | slot :: rest ->
            emitTcoExitDropsInOrder(bodyTemp)(transfer)(rest)(managedLists)(managedAdts)(managedStrs)(match (lookupManagedStrSlot(slot)(managedStrs), lookupRuntimeManagedListSlot(slot)(managedLists), lookupRuntimeManagedAdtSlot(slot)(managedAdts)) with
                | (Some(activeSlot), _list, _adt) -> emitTcoAdtExitDropsWith(bodyTemp)(transfer)([(slot, activeSlot, SemString, "String")])(state)
                | (None, Some((activeSlot, elementType)), _adt) -> emitTcoListExitDropsWith(bodyTemp)(transfer)([(slot, activeSlot, elementType)])(state)
                | (None, None, Some((activeSlot, semanticType, typeName))) -> emitTcoAdtExitDropsWith(bodyTemp)(transfer)([(slot, activeSlot, semanticType, typeName)])(state)
                | _ -> state)

// Stage 0's `LowerLambdaCoreEmitRuntimeManagedTcoExitDrops`: the exit releases of every
// runtime-managed slot, right before the function's own `Return`. A reference-counted or
// heap-typed body result may be one of the slots' own values, so every slot is transfer-checked
// against it under one selection flag; a copy-typed result never carries a slot's value out, so
// every slot releases under its active flag.
let emitTcoExitDrops (bodyTemp: Int) (resultType: SemanticType) (slots: List(Int)) (managedLists: List((Int, Int, SemanticType))) (managedAdts: List((Int, Int, SemanticType, Str))) (managedStrs: List((Int, Int))) (state: CoreLoweringState) =
    match (managedLists, managedAdts, managedStrs) with
        | ([], [], []) -> state
        | _entries ->
            ((given (prepared: (CoreLoweringState, Maybe((Int, Int)))) ->
                match prepared with
                    | (preparedState, transfer) -> emitTcoExitDropsInOrder(bodyTemp)(transfer)(slots)(managedLists)(managedAdts)(managedStrs)(preparedState)))(if !isGeneralRcFinishedResult(bodyTemp)(state) && !exitResultOwnsItsReference(bodyTemp)(state) && (isRuntimeTemp(bodyTemp)(state) || isCopyTypeSemantic(resolveType(state)(resultType)) == false)
            then
                match freshLocal(state) with
                    | FreshLocal { state = slotState, local = transferSelectedSlot } ->
                        match freshTemp(slotState) with
                            | FreshTemp { state = zeroState, temp = zeroTemp } ->
                                (zeroState
                                |> emit(LoadConstInt(zeroTemp)(0))
                                |> emit(StoreLocal(transferSelectedSlot)(zeroTemp)), Some((transferSelectedSlot, zeroTemp)))
            else (state, None))

let recursive loopParametersWithActiveSlots (slots: List(Int)) (pairs: List((Int, Int))) =
    match slots with
        | [] -> []
        | slot :: rest ->
            (slot, match lookupListActiveSlot(slot)(pairs) with
                | Some(activeSlot) -> activeSlot
                | None -> -1) :: loopParametersWithActiveSlots(rest)(pairs)

// Each loop parameter's slot beside its active flag, -1 for one without (stage 0's
// `LoopParametersOf`).
let frameLoopParameters (frame: CoreTcoLoopFrame) = loopParametersWithActiveSlots(frame.parameterSlots)(frame.listActiveSlots)

let recursive patternOwnerSiteSlots (sites: List(PatternOwnerSite)) =
    match sites with
        | [] -> []
        | site :: rest -> site.siteSlot :: patternOwnerSiteSlots(rest)

let withdrawCopiedLoopResultRetains (ownerSlots: List(Int)) (parameters: List((Int, Int))) (bodyTemp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match (isGeneralRcValueType(semanticType)(state), generalCopyPlanOf(semanticType)(state)) with
        | (true, Some(_plan)) ->
            state with reversedInstructions = withdrawLoopResultRetainsOfCopiedCells(ownerSlots)(stateReleasedPatternOwnerDuplicates(state))(stateTransientFlagClears(state))(bodyTemp)(parameters)(state.reversedInstructions)
        | _ -> state

let recursive heapFieldsOf (types: List(SemanticType)) (index: Int) (state: CoreLoweringState) =
    match types with
        | [] -> []
        | fieldType :: rest ->
            if resultSurvivesReset(fieldType)(state)
            then heapFieldsOf(rest)(index + 1)(state)
            else (index, fieldType) :: heapFieldsOf(rest)(index + 1)(state)

let recursive ownFieldConstructors (arguments: List(SemanticType)) (state: CoreLoweringState) (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> []
        | (CoreConstructorLayout { tag = tag, tagless = tagless } as layout) :: rest ->
            (tag, tagless, heapFieldsOf(layoutInstantiatedFieldTypes(layout)(arguments)(state))(0)(state)) :: ownFieldConstructors(arguments)(state)(rest)

let recursive anyHeapField (constructors: List((Int, Bool, List((Int, SemanticType))))) =
    match constructors with
        | [] -> false
        | (_tag, _tagless, []) :: rest -> anyHeapField(rest)
        | (_tag, _tagless, _fields) :: _rest -> true

// Stage 0's `OwnFieldResultType`: a resolved named result type with a constructor holding a heap field,
// its constructors in tag order with their heap fields.
let ownFieldKindOf (resultType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(resultType) with
        | SemNamed(symbolId, name, arguments) ->
            if containsUnresolvedLayout(SemNamed(symbolId)(name)(arguments))(state)
            then None
            else
                let constructors =
                    state
                    |> stateConstructorsOfType(name)
                    |> sortBy(given (left: CoreConstructorLayout) ->
                        given (right: CoreConstructorLayout) -> left.tag <= right.tag)
                    |> ownFieldConstructors(arguments)(state)
                in
                    if anyHeapField(constructors)
                    then
                        constructors
                        |> OwnFieldKind(match constructors with
                            | (_tag, true, _fields) :: [] -> true
                            | _ -> false)
                        |> Some
                    else None
        | _ -> None

let recursive seenRecordHolderType (target: SemanticType) (types: List(SemanticType)) =
    match types with
        | [] -> false
        | candidate :: rest -> candidate == target || seenRecordHolderType(target)(rest)

// Stage 0's `ContainsResolvedType`: whether a value of this type can hold a value of the record's
// type: the type itself, a type argument, a constructor field, or a function. A type variable cannot.
let recursive containsResolvedType (semanticType: SemanticType) (target: SemanticType) (seen: List(SemanticType)) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | resolved ->
            if resolved == target
            then true
            else
                match resolved with
                    | SemList(element) -> containsResolvedType(element)(target)(seen)(state)
                    | SemTuple(elements) -> anyContainsResolvedType(elements)(target)(seen)(state)
                    | SemFunction(_parameter, _result, _row) -> true
                    | SemNamed(_id, name, arguments) ->
                        if seenRecordHolderType(resolved)(seen)
                        then false
                        else
                            anyContainsResolvedType(arguments)(target)(resolved :: seen)(state) || anyLayoutFieldContains(state |> stateConstructorsOfType(name))(target)(resolved :: seen)(state)
                    | _ -> false
and anyContainsResolvedType (types: List(SemanticType)) (target: SemanticType) (seen: List(SemanticType)) (state: CoreLoweringState) =
    match types with
        | [] -> false
        | candidate :: rest -> containsResolvedType(candidate)(target)(seen)(state) || anyContainsResolvedType(rest)(target)(seen)(state)
and anyLayoutFieldContains (layouts: List(CoreConstructorLayout)) (target: SemanticType) (seen: List(SemanticType)) (state: CoreLoweringState) =
    match layouts with
        | [] -> false
        | layout :: rest ->
            match layoutFieldTypes(layout)(state) with
                | (fieldTypes, _resultType) -> anyContainsResolvedType(fieldTypes)(target)(seen)(state) || anyLayoutFieldContains(rest)(target)(seen)(state)

let recursive fieldTypeHeldBy (resultType: SemanticType) (fields: List((Int, SemanticType))) (state: CoreLoweringState) =
    match fields with
        | [] -> false
        | (_index, fieldType) :: rest ->
            containsResolvedType(resultType)(resolveType(state)(fieldType))([])(state) || fieldTypeHeldBy(resultType)(rest)(state)

let recursive anyFieldTypeHeldBy (resultType: SemanticType) (constructors: List((Int, Bool, List((Int, SemanticType))))) (state: CoreLoweringState) =
    match constructors with
        | [] -> false
        | (_tag, _tagless, fields) :: rest -> fieldTypeHeldBy(resultType)(fields)(state) || anyFieldTypeHeldBy(resultType)(rest)(state)

// A stored word that holds a reference of its own: a realized retain, or a constant.
let exitStoredFieldRetained (source: Int) (state: CoreLoweringState) =
    match definitionOfTemp(source)(state.reversedInstructions) with
        | Some(RcDup(_target, _source, true, _mayBeEmpty)) -> true
        | Some(LoadConstInt(_target, _value)) -> true
        | Some(LoadConstBool(_target, _value)) -> true
        | Some(LoadConstStr(_target, _label)) -> true
        | _ -> false

let recursive exitCellFieldsRetained (cell: Int) (index: Int) (state: CoreLoweringState) =
    match constructionFieldSource(cell)(index)(state.reversedInstructions) with
        | None -> true
        | Some(source) -> exitStoredFieldRetained(source)(state) && exitCellFieldsRetained(cell)(index + 1)(state)

// Stage 0's `ExitParameterConstructionRetains`: an arena cell whose every stored field is a realized
// retain or a constant.
let exitCellConstructionRetains (cell: Int) (state: CoreLoweringState) =
    match definitionOfTemp(cell)(state.reversedInstructions) with
        | Some(AllocAdt(_target, _tag, _fieldCount, false, _tagless)) -> exitCellFieldsRetained(cell)(0)(state)
        | Some(Alloc(_target, _size, false)) -> exitCellFieldsRetained(cell)(0)(state)
        | _ -> false

let recursive exitCellsOfSlot (slot: Int) (cells: List((Int, Int))) =
    match cells with
        | [] -> []
        | (candidate, cell) :: rest ->
            if candidate == slot
            then cell :: exitCellsOfSlot(slot)(rest)
            else exitCellsOfSlot(slot)(rest)

let recursive allExitCellsRetain (cells: List(Int)) (state: CoreLoweringState) =
    match cells with
        | [] -> true
        | cell :: rest -> exitCellConstructionRetains(cell)(state) && allExitCellsRetain(rest)(state)

let recursive exitParameterTypeAt (ordinal: Int) (types: List(SemanticType)) =
    match types with
        | [] -> None
        | parameterType :: rest ->
            if ordinal == 0
            then Some(parameterType)
            else exitParameterTypeAt(ordinal - 1)(rest)

// The flagged walk: a set flag releases the arena cell's own fields.
let emitFlaggedOwnFieldWalk (slot: Int) (flagSlot: Int) (tagless: Bool) (constructors: List((Int, Bool, List((Int, SemanticType))))) (state: CoreLoweringState) =
    match freshLabel("exit_param_own_fields_skip")(state) with
        | FreshLabel { state = labelled, label = skipLabel } ->
            match freshTemp(labelled) with
                | FreshTemp { state = flagState, temp = flagTemp } ->
                    match flagState
                    |> emit(LoadLocal(flagTemp)(flagSlot))
                    |> emit(JumpIfFalse(flagTemp)(skipLabel))
                    |> freshTemp with
                        | FreshTemp { state = valueState, temp = valueTemp } ->
                            valueState
                            |> emit(LoadLocal(valueTemp)(slot))
                            |> emitOwnFieldWalk(valueTemp)(tagless)(constructors)
                            |> emit(Label(skipLabel))

// Stage 0's `EmitExitParameterOwnFieldReleases`: at the return, a set flag releases an arena
// parameter's successor cell's own fields, when every constructor successor stored retains and the
// result holds references of its own or cannot hold the released fields.
let recursive emitExitOwnFieldReleasesOf (entries: List((Int, Int, Int))) (resultTemp: Int) (resultType: SemanticType) (frame: CoreTcoLoopFrame) (loop: CoreTcoLoop) (managedLists: List((Int, Int, SemanticType))) (managedAdts: List((Int, Int, SemanticType, Str))) (managedStrs: List((Int, Int))) (state: CoreLoweringState) =
    match entries with
        | [] -> state
        | (slot, flagSlot, ordinal) :: rest ->
            let cells = exitCellsOfSlot(slot)(frame.exitOwnFieldCells)
            in
                let managed =
                    match (lookupRuntimeManagedListSlot(slot)(managedLists), lookupRuntimeManagedAdtSlot(slot)(managedAdts), lookupManagedStrSlot(slot)(managedStrs)) with
                        | (None, None, None) -> false
                        | _ -> true
                in
                    let released =
                        if managed || cells == []
                        then state
                        else
                            match exitParameterTypeAt(ordinal)(loop.parameterTypes) with
                                | Some(parameterType) ->
                                    match ownFieldKindOf(parameterType)(state) with
                                        | Some(OwnFieldKind(tagless, constructors)) ->
                                            if (isRuntimeTemp(resultTemp)(state) || isGeneralRcFinishedResult(resultTemp)(state) || !anyFieldTypeHeldBy(resolveType(state)(resultType))(constructors)(state)) && allExitCellsRetain(cells)(state)
                                            then emitFlaggedOwnFieldWalk(slot)(flagSlot)(tagless)(constructors)(state)
                                            else state
                                        | _ -> state
                                | None -> state
                    in emitExitOwnFieldReleasesOf(rest)(resultTemp)(resultType)(frame)(loop)(managedLists)(managedAdts)(managedStrs)(released)

// A loop whose result is of the contract's types makes it the function's own before its exit
// releases the loop parameters, as stage 0 orders the two: the result then holds its own
// reference, so every parameter goes and none is transferred as the result. An inline result
// releases the function's owned slots there too, and is never transferred either. The return that
// follows finds the result finished and leaves it. The copy of an arena result borrows its
// children ahead of the exit releases, so the retains of loop-parameter reads its cells were built
// with are withdrawn (stage 0's `EmitGeneralRcOwnedFunctionResult`), after the part duplicates' slots
// are, so a withdrawn slot's store is no second reader of its duplicate. A generic tuple result
// whose contract parts the caller may ask for hands those parts over ahead of the exit too
// (stage 0's `FinishLoopFunctionResult` precedes `LowerLambdaCoreEmitTcoExitReleases`): each part
// is retained before the exit releases the loop parameter it may be, which released first left the
// part freed in the caller's hands.
let finishLoopResultAheadOfExit (producedOwned: Bool) (label: Str) (ownerSlots: List(Int)) (parameters: List((Int, Int))) (bodyTemp: Int) (loopType: SemanticType) (state: CoreLoweringState) =
    (let semanticType = resolvePhantomResultLayout(label)(loopType)(state)
    in
        if isGeneralRcValueType(semanticType)(state) || resultSurvivesReset(semanticType)(state) && !isRuntimeTemp(bodyTemp)(state) && isCopyTypeSemantic(resolveType(state)(semanticType)) || !isRuntimeTemp(bodyTemp)(state) && !resultSurvivesReset(semanticType)(state) && tupleResultParts(semanticType)(state) != []
        then
            match state
            |> dropPartDuplicateSlotsWhenTheResultCopiesAnyway(bodyTemp)(semanticType)
            |> withdrawCopiedLoopResultRetains(ownerSlots)(parameters)(bodyTemp)(semanticType)
            |> finishGeneralRcFunctionResultWithLayout(producedOwned)(label)(bodyTemp)(semanticType) with
                | (finished, resultTemp) -> (withGeneralRcFinishedResult(resultTemp)(finished), resultTemp)
        else (state, bodyTemp))

let finishTcoManagedPlacement (label: Str) (ownerSlots: List(Int)) (frame: CoreTcoLoopFrame) (loop: CoreTcoLoop) (bodyTemp: Int) (semanticType: SemanticType) (candidates: List(TcoManagedCandidate)) (managedLists: List((Int, Int, SemanticType))) (managedAdts: List((Int, Int, SemanticType, Str))) (managedStrs: List((Int, Int))) (state: CoreLoweringState) =
    match tcoEntryNormalizationEntries(candidates)(managedLists)(managedAdts)(managedStrs)(state) with
        | [] ->
            match closeTmcChain(label)(bodyTemp)(semanticType)(state) with
                | (closed, resultTemp) ->
                    closed
                    |> emitExitOwnFieldReleasesOf(frame.exitOwnFieldFlags)(resultTemp)(semanticType)(frame)(loop)(managedLists)(managedAdts)(managedStrs)
                    |> success(resultTemp)(semanticType)
        | entries ->
            match state |> (given (current: CoreLoweringState) -> withStateTcoLoopFrame(Some((frame with runtimeManagedListSlots = managedLists, runtimeManagedAdtSlots = managedAdts, runtimeManagedStrSlots = managedStrs)))(current)) |> promoteAffineAppends(managedStrs) |> markRuntimeListResult(bodyTemp)(managedLists)(managedAdts)(managedStrs) |> recordTcoNormalizedArgumentLabel(label)(entries) |> spliceTcoEntryNormalization(frame.entrySpliceCount)(entries) |> closeTmcChain(label)(bodyTemp)(semanticType) with
                | (closed, closedTemp) ->
                    closed
                    |> finishLoopResultAheadOfExit(tempIsProducedOwned(bodyTemp)(state) || isClosedProducerChain(closedTemp)(closed))(label)(ownerSlots)(frameLoopParameters(frame))(closedTemp)(semanticType)
                    |> (given (finished: (CoreLoweringState, Int)) ->
                        match finished with
                            | (finishedState, resultTemp) ->
                                finishedState
                                |> copyArenaTupleParameters(managedLists)(managedAdts)(candidates)
                                |> emitTcoExitDrops(tcoExitTransferTemp(resultTemp)(finishedState))(semanticType)(frame.parameterSlots)(managedLists)(managedAdts)(managedStrs)
                                |> emitExitOwnFieldReleasesOf(frame.exitOwnFieldFlags)(resultTemp)(semanticType)(frame)(loop)(managedLists)(managedAdts)(managedStrs)
                                |> success(resultTemp)(semanticType))

// A parameter whose self-call shape, copy-ADT layout, or affine analysis could place it on the
// reference-counted heap: the candidates whose active flag the loop allocates.
// Stage 0 counts a list the loop rebuilds fresh at its self call as ownership-shape eligible
// (`EvaluateTcoListRcEligibility`, `facts.FreshRebuiltList`), so such a parameter gets its active
// flag at the loop entry like a grown or consumed one, and its placement follows from its type
// rather than waiting for the body.
let listActiveSlotCandidate (ordinal: Int) (loop: CoreTcoLoop) (shape: TcoArgumentShape) (slot: Int) (state: CoreLoweringState) = ordinaryRcPlacementAllowed(state) && (isTcoListShape(shape) || isAccumulatorRebuildListSlot(ordinal)(slot)(loop)(state) || shape == TcoFreshListShape && tcoListSlotElement(slot)(shape)(ordinal)(loop)(state) != None || tcoAdtSlotAdmitted(ordinal)(slot)(shape)(loop)(state) || containsInt(ordinal)(loop.runtimeManagedOrdinals))

// Stage 0 places a loop parameter on the reference-counted heap only once its type has a
// resolved layout (`ResolvedLayoutEligible`), so a parameter whose type is still a variable at
// the loop entry gets its active flag only when a back edge or the body's end resolves it,
// after every local allocated in between.
let loopSlotTypeResolved (slot: Int) (state: CoreLoweringState) =
    match slotResolvedType(slot)(state.bindings)(state) with
        | Some(SemVariable(_id)) -> false
        | _ -> true

// The active flags a resolution point after the loop entry allocates (stage 0's
// `LowerCallTcoPromoteResolvedRuntimeParams` at a back edge, and the post-body refresh of
// `LowerLambdaCoreIdentifyRuntimeManagedTcoParams`): every candidate still without a flag whose
// type the point has resolved, in parameter order, appended to the frame's pairs.
let recursive allocatePendingListActiveSlots (ordinal: Int) (loop: CoreTcoLoop) (slots: List(Int)) (resolved: List(Bool)) (pairs: List((Int, Int))) (state: CoreLoweringState) =
    match (slots, resolved, loopShapeAtOrdinal(ordinal)(loop.argumentShapes)) with
        | (slot :: restSlots, slotResolved :: restResolved, Some(shape)) ->
            if slotResolved && lookupListActiveSlot(slot)(pairs) == None && listActiveSlotCandidate(ordinal)(loop)(shape)(slot)(state)
            then
                match freshLocal(state) with
                    | FreshLocal { state = allocated, local = activeSlot } ->
                        allocatePendingListActiveSlots(ordinal + 1)(loop)(restSlots)(restResolved)(append(pairs)([(slot, activeSlot)]))(allocated)
            else allocatePendingListActiveSlots(ordinal + 1)(loop)(restSlots)(restResolved)(pairs)(state)
        | _ -> (state, pairs)

let recursive argumentTypesResolved (argumentTypes: List(SemanticType)) (state: CoreLoweringState) =
    match argumentTypes with
        | [] -> []
        | argumentType :: rest ->
            (match resolveType(state)(argumentType) with
                | SemVariable(_id) -> false
                | _ -> true) :: argumentTypesResolved(rest)(state)

let recursive allResolved (count: Int) =
    if count <= 0
    then []
    else true :: allResolved(count - 1)

// A back edge whose argument types resolve a parameter the entry left in the arena allocates
// its active flag there, and the body's end allocates the flags of every remaining candidate,
// which the resolved types then admit or retire.
let allocateListActiveSlotsAt (resolved: List(Bool)) (frame: CoreTcoLoopFrame) (loop: CoreTcoLoop) (state: CoreLoweringState) =
    match allocatePendingListActiveSlots(0)(loop)(frame.parameterSlots)(resolved)(frame.listActiveSlots)(state) with
        | (allocated, pairs) ->
            let refreshed = frame with listActiveSlots = pairs
            in (withStateTcoLoopFrame(Some(refreshed))(allocated), refreshed)

// The active flags allocated for list-shaped, copy-ADT and affine `Str` parameters the resolved
// types did not admit: never written or read, they are retired from the function's slots.
let recursive unusedListActiveSlots (pairs: List((Int, Int))) (managedLists: List((Int, Int, SemanticType))) (managedAdts: List((Int, Int, SemanticType, Str))) (managedStrs: List((Int, Int))) =
    match pairs with
        | [] -> []
        | (slot, activeSlot) :: rest ->
            match (lookupRuntimeManagedListSlot(slot)(managedLists), lookupRuntimeManagedAdtSlot(slot)(managedAdts), lookupManagedStrSlot(slot)(managedStrs)) with
                | (None, None, None) -> activeSlot :: unusedListActiveSlots(rest)(managedLists)(managedAdts)(managedStrs)
                | _ -> unusedListActiveSlots(rest)(managedLists)(managedAdts)(managedStrs)

let retireUnusedListActiveSlots (pairs: List((Int, Int))) (managedLists: List((Int, Int, SemanticType))) (managedAdts: List((Int, Int, SemanticType, Str))) (managedStrs: List((Int, Int))) (state: CoreLoweringState) =
    withStateRetiredLocals(managedStrs
    |> unusedListActiveSlots(pairs)(managedLists)(managedAdts)
    |> append(stateRetiredLocals(state)))(state)

let recursive withdrawScrutineeCells (cells: List(Int)) (parameters: List((Int, Int))) (state: CoreLoweringState) =
    match cells with
        | [] -> state
        | cell :: rest ->
            withdrawScrutineeCells(rest)(parameters)((state with reversedInstructions = withdrawLoopParameterRetainsOfCopiedCells(stateTransientFlagClears(state))(cell)(parameters)(state.reversedInstructions)))

// Stage 0's `WithdrawBorrowedScrutineeRetains`, run once the retains carry their resolved form.
let withdrawBorrowedScrutineeRetains (parameters: List((Int, Int))) (state: CoreLoweringState) =
    state
    |> withdrawScrutineeCells(stateBorrowedScrutineeCells(state))(parameters)
    |> withStateBorrowedScrutineeCells([])

let finalizeTcoManagedPlacementResolved (label: Str) (frame: CoreTcoLoopFrame) (loop: CoreTcoLoop) (bodyTemp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    state
    |> tcoManagedCandidates(0)(frame.parameterSlots)(loop.argumentShapes)(loop)
    |> (given (candidates: List(TcoManagedCandidate)) ->
        frame.listActiveSlots
        |> tcoRuntimeManagedStrSlotsOf(candidates)
        |> ((given (managedLists: List((Int, Int, SemanticType))) ->
            given (managedAdts: List((Int, Int, SemanticType, Str))) ->
                given (managedStrs: List((Int, Int))) ->
                    state
                    |> retireUnusedListActiveSlots(frame.listActiveSlots)(managedLists)(managedAdts)(managedStrs)
                    |> finalizePatternOwnerSites(managedLists)(managedAdts)(candidates)
                    |> promoteTcoParameterRetains(managedLists)(managedAdts)(candidates)
                    |> resolvePendingArgumentFlags(managedLists)(managedAdts)(candidates)
                    |> withdrawBorrowedScrutineeRetains(frameLoopParameters(frame))
                    |> finishTcoManagedPlacement(label)(state
                    |> placedPatternOwnerSites(statePatternOwnerSites(state))(managedLists)(managedAdts)(candidates)
                    |> patternOwnerSiteSlots)(frame)(loop)(bodyTemp)(semanticType)(candidates)(managedLists)(managedAdts)(managedStrs)))(tcoRuntimeManagedListSlotsOf(unblockedCandidates(candidates)(loop)(state))(frame.listActiveSlots))(tcoRuntimeManagedAdtSlotsOf(unblockedCandidates(candidates)(loop)(state))(frame.listActiveSlots)))

// Stage 0's post-body refresh allocates the active flag of every candidate the loop entry and
// the back edges left without one, ahead of the entry normalization's own locals.
let finalizeTcoManagedPlacement (label: Str) (frame: CoreTcoLoopFrame) (loop: CoreTcoLoop) (bodyTemp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match allocateListActiveSlotsAt(frame.parameterSlots
    |> length
    |> allResolved)(frame)(loop)(state) with
        | (allocated, refreshed) -> finalizeTcoManagedPlacementResolved(label)(refreshed)(loop)(bodyTemp)(semanticType)(allocated)

// Runs once the loop body's whole lowering has resolved every parameter's type: splices the
// entry normalization in at the recorded loop-entry point and emits the exit drops right before
// the lambda's own `Return`, for every parameter the affine-append analysis and the resolved
// type together admit to runtime-managed `Str` placement and every list parameter its self-call
// shape and resolved type admit.
let recursive bodyHasStructuralReuse (count: Int) (instructions: List(IrInstruction)) =
    if count <= 0
    then false
    else
        match instructions with
            | [] -> false
            | IrInstruction { instruction = AllocReusing(_target, _tag, fieldCount, _token, _runtimeManaged, _flag, _tagless) } :: rest -> fieldCount > 0 || bodyHasStructuralReuse(count - 1)(rest)
            | _instruction :: rest -> bodyHasStructuralReuse(count - 1)(rest)

let recursive revertReuseAllocations (count: Int) (instructions: List(IrInstruction)) =
    if count <= 0
    then instructions
    else
        match instructions with
            | [] -> []
            | IrInstruction { instruction = AllocReusing(target, tag, fieldCount, _token, _runtimeManaged, _flag, tagless), location = location } :: rest -> IrInstruction(instruction = AllocAdt(target)(tag)(fieldCount)(false)(tagless), location = location) :: revertReuseAllocations(count - 1)(rest)
            | instruction :: rest -> instruction :: revertReuseAllocations(count - 1)(rest)

// The entry deep copy of every direct-reuse accumulator the move analysis did not prove unique:
// the accumulator is loaded, cloned, and stored back, so the loop body may overwrite its
// matched cells in place.
let recursive emitDirectReuseEntryCopies (candidates: List((Int, SemanticType, Str, Bool, Bool))) (state: CoreLoweringState) =
    match candidates with
        | [] -> state
        | (_slot, _semanticType, _name, true, _isSpecialization) :: rest -> emitDirectReuseEntryCopies(rest)(state)
        | (slot, semanticType, _name, false, _isSpecialization) :: rest ->
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = loaded } ->
                    match allocated
                    |> emit(LoadLocal(loaded)(slot))
                    |> emitLocatedDeepCopy(loaded)(semanticType) with
                        | (copied, copyTemp) ->
                            copied
                            |> emit(StoreLocal(slot)(copyTemp))
                            |> emitDirectReuseEntryCopies(rest)

// Stage 0's `PrepareDirectReuseBody` and `LowerLambdaCoreSpliceReuseCopies`, once the loop body
// is lowered: a direct-reuse entry deep copy is worth its cost only when the body rebuilt the
// accumulator's structure in place (an `AllocReusing` with fields); otherwise the copies are
// omitted and the body's nullary reuses, no longer backed by a copy, revert to fresh arena
// allocations. A copy that stays is elided when the move analysis proves the accumulator
// uniquely owned at every call, and spliced in at the loop-entry point otherwise.
let recursive specializationEntryCopies (candidates: List((Int, SemanticType, Str, Bool, Bool))) =
    match candidates with
        | [] -> []
        | (slot, semanticType, name, provesUnique, true) :: rest -> (slot, semanticType, name, provesUnique, true) :: specializationEntryCopies(rest)
        | _candidate :: rest -> specializationEntryCopies(rest)

// Stage 0's `NoSpecializedCall` outcome: a specialization copy exists only so a clone may
// rewrite the accumulator in place, so a scanned accumulator no call was routed for keeps none.
let recursive routedReuseCandidates (candidates: List((Int, SemanticType, Str, Bool, Bool))) (routed: List(Str)) =
    match candidates with
        | [] -> []
        | (slot, semanticType, name, provesUnique, true) :: rest ->
            if containsName(name)(routed)
            then (slot, semanticType, name, provesUnique, true) :: routedReuseCandidates(rest)(routed)
            else routedReuseCandidates(rest)(routed)
        | candidate :: rest -> candidate :: routedReuseCandidates(rest)(routed)

let finalizeDirectReuse (frame: CoreTcoLoopFrame) lowered =
    match lowered with
        | LoweredCoreValue { error = Some(_error) } -> lowered
        | LoweredCoreValue { state = state, temp = bodyTemp, semanticType = semanticType, error = None } ->
            match state
            |> stateRoutedSpecializationAccumulators
            |> routedReuseCandidates(stateDirectReuseCandidates(state)) with
                | [] -> lowered
                | candidates ->
                    let bodyCount = length(state.reversedInstructions) - frame.entrySpliceCount
                    in
                        if bodyHasStructuralReuse(bodyCount)(state.reversedInstructions)
                        then
                            match emitDirectReuseEntryCopies(candidates)((state with reversedInstructions = [])) with
                                | generated -> success(bodyTemp)(semanticType)((generated with reversedInstructions = spliceGeneratedInstructions(bodyCount)([])(state.reversedInstructions)(generated.reversedInstructions)))
                        else
                            // Only the direct candidates lose their copies: a specialization's own
                            // reuse fires inside the generated function, which this body's
                            // instructions never contain.
                            match emitDirectReuseEntryCopies(specializationEntryCopies(candidates))((state with reversedInstructions = [])) with
                                | generated ->
                                    success(bodyTemp)(semanticType)((generated with reversedInstructions = spliceGeneratedInstructions(bodyCount)([])(revertReuseAllocations(bodyCount)(state.reversedInstructions))(generated.reversedInstructions)))

let finalizeTcoRuntimeManagedParams (label: Str) lowered =
    match lowered with
        | LoweredCoreValue { error = Some(_error) } -> lowered
        | LoweredCoreValue { state = state, temp = bodyTemp, semanticType = semanticType, error = None } ->
            match (stateTcoLoopFrame(state), stateTcoLoop(state)) with
                | (Some(frame), Some(loop)) ->
                    state
                    |> finalizeTcoManagedPlacement(label)(frame)(loop)(bodyTemp)(semanticType)
                    |> finalizeDirectReuse(frame)
                | _ -> lowered
