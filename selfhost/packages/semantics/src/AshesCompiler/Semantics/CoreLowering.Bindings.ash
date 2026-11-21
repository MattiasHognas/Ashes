// Chain results and parameter passthrough normalization, type expressions and inline type
// variables, and the annotation of lambda bindings. A part of
// `AshesCompiler.Semantics.CoreLowering`.

import Ashes.Collection.List.length
import Ashes.Collection.List.map
import Ashes.Collection.List.reverse
import Ashes.Collection.Map.MapTree
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.Pattern
import AshesCompiler.Frontend.Syntax.TopLevelItem
import AshesCompiler.Frontend.Syntax.TypeAliasDecl
import AshesCompiler.Frontend.Syntax.ExternalDecl
import AshesCompiler.Frontend.Syntax.TypeParameter
import AshesCompiler.Frontend.Syntax.TypeExpr
import AshesCompiler.Semantics.CallOwnership
import AshesCompiler.Semantics.HeapLayoutClassification
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.ParameterPassthrough.returnsParameterOrFreshValue
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.ExprMentions.exprReadsName
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.CoreLowering.LoweringState
import AshesCompiler.Semantics.CoreLowering.Emit
import AshesCompiler.Semantics.CoreLowering.Owners
import AshesCompiler.Semantics.CoreLowering.CopyPlans
import AshesCompiler.Semantics.CoreLowering.TcoReset
import AshesCompiler.Semantics.CoreLowering.ResultHandOver
export (
    value adoptNormalizedParameterResult,
    value tcoListElementSupported,
    value isPassthroughNormalizableParameterType,
    value withPassthroughParameter,
    value isConsChainOntoSelfCall,
    value nestedJoinConsesOntoSelfResult,
    value consCellTailTemp,
    value loadedSlotOf,
    value loadedSlotThroughBorrows,
    value consChainTailOwnedSlot,
    value normalizeParameterPassthroughBranch,
    value predictRuntimeManagedResult,
    value keepPredictedRuntimeManagedResult,
    value beginCurryStage,
    value noteLetBoundFunction,
    value linkCurryStage,
    value closeTmcChain,
    value adoptDeferredSelfCallResults,
    value unifySelfCallResults,
    value reserveTcoExitTransferSlot,
    value finishLambdaBody,
    value expandTypeAliases,
    value typeAliasesOf,
    value typeExprToSemanticType,
    value typeExprListToSemanticTypes,
    value enterInlineTypeVariables,
    value lowerLambdaParameterType,
    value bindingLambdaValue,
    value annotatedValueType,
    value bindLoweredAnnotation,
    value recordLetLambdaLabel,
)

// Whether `bodyTemp` is defined by a plain read of local `slot`.
let recursive definesSlotRead (bodyTemp: Int) (slot: Int) (reversedInstructions: List(IrInstruction)) =
    match reversedInstructions with
        | [] -> false
        | IrInstruction { instruction = LoadLocal(target, source) } :: rest ->
            if target == bodyTemp
            then source == slot
            else definesSlotRead(bodyTemp)(slot)(rest)
        | _ :: rest -> definesSlotRead(bodyTemp)(slot)(rest)

// Stage 0's `ReturnsNormalizedAlwaysReturnedParameter`: a function whose entry normalizes its
// always-returned parameter returns that owned value whenever its body's result is a plain
// read of the parameter's slot (the adopted argument or the entry copy, reference-counted
// either way), so the body result counts as newly produced: the closure advertises a
// runtime-managed result and a caller asking for an arena result gets the boundary copy with
// the original released. A loop function's parameter slot is the loop's own business.
let adoptNormalizedParameterResult (label: Str) (bodyTemp: Int) (state: CoreLoweringState) =
    match stateTcoLoopFrame(state) with
        | Some(_frame) -> state
        | None ->
            if acceptsRuntimeManagedArgument(label)(state) && isRuntimeTemp(bodyTemp)(state) == false && definesSlotRead(bodyTemp)(1)(state.reversedInstructions)
            then markRuntimeTemp(bodyTemp)(RuntimeNewlyProduced)(state)
            else state

// The closed result joins two branches, the spine (reference-counted by the transform's own
// eligibility gate, its last tail the body value) and the body value alone, so it carries the body
// value's own representation and ownership, stage 0's `RecordControlFlowJoinTemp` at the close.
// Left without a fact, the slot read reports an arena result: the closure then lacks its
// `ReturnsRuntimeManaged` bit and the result-ownership epilogue, and a caller that copies the
// "arena" result out reclaims only the arena, stranding the reference-counted spine it copied.
let carryRuntimeTempState (fromTemp: Int) (toTemp: Int) (state: CoreLoweringState) =
    match runtimeTempStateOf(fromTemp)(state) with
        | Some(runtimeState) -> markRuntimeTemp(toTemp)(runtimeState)(state)
        | None -> state

// Stage 0's `ownsBorrowedListBase` in `LowerLambdaCoreCloseTmcChain`: a borrowed base-case list
// joined with the spine would report the whole result as arena, and a caller copying it out would
// strand the spine, so it holds its own reference on both branches.
let ownsBorrowedListBase (bodyTemp: Int) (bodyType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(bodyType) with
        | SemList(_element) ->
            if isRuntimeTemp(bodyTemp)(state)
            then false
            else
                match generalCopyPlanOf(bodyType)(state) with
                    | Some(_plan) -> true
                    | None -> false
        | _ -> false

// The result of a chain that built no cell: the body's value, which under the contract holds a
// reference of its own when the body did not produce it (a borrowed parameter), as the last cell's
// tail does.
let emitEmptyChainResult (bodyTemp: Int) (bodyType: SemanticType) (state: CoreLoweringState) =
    if isRuntimeTemp(bodyTemp)(state) || !isGeneralRcValueType(bodyType)(state) && !ownsBorrowedListBase(bodyTemp)(bodyType)(state)
    then (state, bodyTemp)
    else
        match generalCopyPlanOf(bodyType)(state) with
            | Some(plan) -> emitArgumentCopy(bodyTemp)(plan)(state)
            | None -> (state, bodyTemp)

// The last cell's tail under stage 0's borrowed-list-base rule: a borrowed base-case list holds its
// own reference in the spine as well, so the spine owns everything it reaches.
let emitChainTail (bodyTemp: Int) (bodyType: SemanticType) (state: CoreLoweringState) =
    if ownsBorrowedListBase(bodyTemp)(bodyType)(state)
    then
        match generalCopyPlanOf(bodyType)(state) with
            | Some(plan) -> emitArgumentCopy(bodyTemp)(plan)(state)
            | None -> (state, bodyTemp)
    else (state, bodyTemp)

// The closed result's representation: an owned reference-counted list on both branches under the
// borrowed-list-base rule, otherwise the body value's own.
let markClosedChainResult (bodyTemp: Int) (bodyType: SemanticType) (outTemp: Int) (state: CoreLoweringState) =
    if ownsBorrowedListBase(bodyTemp)(bodyType)(state)
    then markRuntimeTemp(outTemp)(RuntimeNewlyProduced)(state)
    else carryRuntimeTempState(bodyTemp)(outTemp)(state)

// The branch stage 0 emits in `LowerLambdaCoreCloseTmcChain`: with a pending cell, the body's value
// fills its still-nil tail and the spine head becomes the result; with none, the body's value is the
// result.
let emitTmcChainClose (destSlot: Int) (resultSlot: Int) (bodyTemp: Int) (bodyType: SemanticType) (joinSlot: Int) (emptyLabel: Str) (doneLabel: Str) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = destState, temp = destTemp } ->
            match freshTemp(destState) with
                | FreshTemp { state = zeroState, temp = zeroTemp } ->
                    match freshTemp(zeroState) with
                        | FreshTemp { state = condState, temp = condTemp } ->
                            match freshTemp(condState) with
                                | FreshTemp { state = spineState, temp = spineTemp } ->
                                    match freshTemp(spineState) with
                                        | FreshTemp { state = outState, temp = outTemp } ->
                                            match outState
                                            |> emit(LoadLocal(destTemp)(destSlot))
                                            |> emit(LoadConstInt(zeroTemp)(0))
                                            |> emit(CmpIntNe(condTemp)(destTemp)(zeroTemp))
                                            |> emit(JumpIfFalse(condTemp)(emptyLabel))
                                            |> emitChainTail(bodyTemp)(bodyType) with
                                                | (tailed, tailTemp) ->
                                                    match tailed
                                                    |> emit(StoreMemOffset(destTemp)(8)(tailTemp))
                                                    |> emit(LoadLocal(spineTemp)(resultSlot))
                                                    |> emit(StoreLocal(joinSlot)(spineTemp))
                                                    |> emit(Jump(doneLabel))
                                                    |> emit(Label(emptyLabel))
                                                    |> emitEmptyChainResult(bodyTemp)(bodyType) with
                                                        | (emptied, emptyTemp) ->
                                                            (emptied
                                                            |> emit(StoreLocal(joinSlot)(emptyTemp))
                                                            |> emit(Label(doneLabel))
                                                            |> emit(LoadLocal(outTemp)(joinSlot))
                                                            |> markClosedChainResult(bodyTemp)(bodyType)(outTemp)
                                                            |> withClosedProducerChain(outTemp)(if isRuntimeTemp(bodyTemp)(state)
                                                            then bodyTemp
                                                            else outTemp), outTemp)

// Closes a tail-modulo-constructor spine at the function's single return, before any ownership
// finalization: from here on the function's result IS the spine and the body value is only the last
// cell's tail, so the returned-root transfer, the exit drops, and the result-ownership bit the call
// site branches on all have to see the spine. Only the loop that owns this label closes, so a nested
// binding lowered under an enclosing loop's frame leaves that loop's chain alone.
let closeTmcChain (label: Str) (bodyTemp: Int) (bodyType: SemanticType) (state: CoreLoweringState) =
    match stateTcoLoopFrame(state) with
        | Some(CoreTcoLoopFrame { bodyLabel = bodyLabel, tmcDestSlot = destSlot, tmcResultSlot = resultSlot }) ->
            if destSlot < 0 || !containsName(bodyLabel)(stateTmcActivatedLabels(state)) || bodyLabel != label + "_body"
            then (state, bodyTemp)
            else
                match freshLocal(state) with
                    | FreshLocal { state = joinState, local = joinSlot } ->
                        match freshLabel("tmc_close_empty")(joinState) with
                            | FreshLabel { state = emptyState, label = emptyLabel } ->
                                match freshLabel("tmc_close_done")(emptyState) with
                                    | FreshLabel { state = doneState, label = doneLabel } -> emitTmcChainClose(destSlot)(resultSlot)(bodyTemp)(bodyType)(joinSlot)(emptyLabel)(doneLabel)(doneState)
        | None -> (state, bodyTemp)

let tcoListElementSupported (element: SemanticType) (state: CoreLoweringState) =
    match heapFactsOf(element)(state) with
        | HeapLayoutFacts { runtimeTcoListElementSupported = supported } -> supported

// Stage 0's `IsPassthroughNormalizableParameterType`: the parameter types the passthrough arm
// copies into an owned reference-counted graph. Strings stay out: their affine growth already
// decides their placement.
let isPassthroughNormalizableParameterType (parameterType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(parameterType) with
        | SemList(element) -> tcoListElementSupported(element)(state)
        | SemTuple(elements) -> allRuntimeOwnedCopyElements(elements)(state)
        | SemNamed(_symbolId, _name, _arguments) as named ->
            match argumentCopyPlanOf(named)(state) with
                | Some(_plan) -> true
                | None -> false
        | _ -> false

// Whether a call names the recursive binding being lowered or a member of its group.
let isSelfCallName (name: Str) (state: CoreLoweringState) =
    match lookupBinding(name)(state.bindings) with
        | Some(CoreBinding { location = CoreSelf(_label, _environmentSize) }) -> true
        | _ ->
            state
            |> stateRecursiveGroupNames
            |> containsLabel(name)

// Stage 0's `Binding.Self` test of `ProducesFreshValue`: the name is bound as a recursive binding
// in the lambda being lowered. In a curried stage's body the function and its group siblings are
// closure captures instead, and a call through one of those promises nothing about its result.
let isBoundSelfName (name: Str) (state: CoreLoweringState) =
    match lookupBinding(name)(state.bindings) with
        | Some(CoreBinding { location = CoreSelf(_label, _environmentSize) }) -> true
        | _ -> false

// Stage 0's `ProducesFreshValue`: a terminal arm that builds its value — a constructor
// application, a list, record or tuple literal, a cons, a call to a function compiled with a
// reference-counted result, or a call to a recursive binding bound as itself here, whose result
// is promised reference-counted.
let producesFreshValue (expression: Expr) (state: CoreLoweringState) =
    match unspanArgument(expression) with
        | ExprCons(_head, _tail) -> true
        | ExprRecord(_name, _fields, _multiline) -> true
        | ExprRecordUpdate(_target, _updates) -> true
        | ExprTuple(_elements) -> true
        | ExprList(_elements, _multiline) -> true
        | ExprCall(_callee, _argument, _sugar, _layout) as call ->
            match callSpineRootAndArity(call)(0) with
                | (ExprVar(name), applied) ->
                    match constructorLayout(name)(state) with
                        | Some(_layout) -> true
                        | None ->
                            isBoundSelfName(name)(state) || (match state
                            |> stateLetLambdaLabels
                            |> lookupLetLambdaLabel(name) with
                                | Some(label) ->
                                    match state
                                    |> stateFunctionReturnedClosureLabels
                                    |> innermostStageLabel(applied - 1)(label) with
                                        | Some(stage) -> bodyReturnsRuntimeManaged(stage)(state)
                                        | None -> false
                                | None -> false)
                | _ -> false
        | _ -> false

// A member of a recursive group whose siblings it reaches through its closure emits no loop: stage 0
// lowers the group bodies with no tail-call context at all, so such a body is a plain one too.
let isPlainFunctionBody (state: CoreLoweringState) =
    match (stateTcoLoop(state), stateTcoLoopFrame(state), stateNormalizedAlwaysReturnedParameter(state)) with
        | (None, None, None) -> true
        | (Some(CoreTcoLoop { emitsLoop = false }), None, None) -> true
        | _ -> false

// Stage 0's `ResolvePassthroughParameter`: the parameter of a plain function whose body returns
// it on some arms and a fresh value on the others; the direct argument lives in local slot 1.
let withPassthroughParameter (parameter: Str) (body: Expr) (parameterType: SemanticType) (state: CoreLoweringState) =
    if ordinaryRcPlacementAllowed(state) && isPlainFunctionBody(state) && returnsParameterOrFreshValue(given (arm: Expr) -> producesFreshValue(arm)(state))(parameter)(body)
    then withStatePassthroughParameter(Some((parameter, 1, parameterType)))(state)
    else withStatePassthroughParameter(None)(state)

// Stage 0's `EmitRuntimeManagedTcoParamCopy` reserves the copy's result temp before it walks a
// list whose heads have no spine copy; the walk allocates its own, so the reserved temp stays
// unused.
let emitPassthroughCopyByCopy (sourceTemp: Int) (plan: ArgumentCopyPlan) (state: CoreLoweringState) =
    match plan with
        | ListDeepArgumentCopy(elementPlan) ->
            match freshTemp(state) with
                | FreshTemp { state = reserved, temp = _reservedTemp } -> emitListDeepCopy(sourceTemp)(elementPlan)(reserved)
        | _ -> emitArgumentDeepCopy(sourceTemp)(plan)(state)

// The branch takes a reference to the parameter's value when the runtime answers that it is
// already reference-counted, and the copy otherwise.
let emitOwnedPassthroughCopy (sourceTemp: Int) (parameterType: SemanticType) (state: CoreLoweringState) =
    match argumentCopyPlanOf(parameterType)(state) with
        | None -> (state, sourceTemp)
        | Some(inlinePlan) ->
            let plan = entryCopyPlanOf(parameterType)(inlinePlan)(state)
            in
                match if canTestRepresentation(state) && resultSurvivesReset(parameterType)(state) == false
                then
                    emitReferenceOrCopy(sourceTemp)(emitPassthroughCopyByCopy(sourceTemp)(plan))(state)
                else emitPassthroughCopyByCopy(sourceTemp)(plan)(state) with
                    | (copied, copyTemp) -> (markRuntimeTemp(copyTemp)(RuntimeNewlyProduced)(copied), copyTemp)

let bindingIsLocalSlot (name: Str) (slot: Int) (state: CoreLoweringState) =
    match lookupBinding(name)(state.bindings) with
        | Some(CoreBinding { location = CoreLocal(boundSlot) }) -> boundSlot == slot
        | _ -> false

// The body a branch's `let` chain ends in.
let recursive letChainBody (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> letChainBody(inner)
        | ExprNode(_node, inner) -> letChainBody(inner)
        | ExprLet(_name, _value, body, _parameters, _annotation, _requirements) -> letChainBody(body)
        | other -> other

let recursive consChainTail (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> consChainTail(inner)
        | ExprNode(_node, inner) -> consChainTail(inner)
        | ExprCons(_head, tail) -> consChainTail(tail)
        | other -> other

// The label a recursive call through `name` enters: the binding's own, or the let-bound lambda's.
let selfCallLabel (name: Str) (state: CoreLoweringState) =
    match lookupBinding(name)(state.bindings) with
        | Some(CoreBinding { location = CoreSelf(label, _environmentSize) }) -> Some(label)
        | _ ->
            state
            |> stateLetLambdaLabels
            |> lookupLetLambdaLabel(name)

// Stage 0's `IsConsChainOntoSelfCall`: a branch consing cells onto the function's own full
// recursive call. That call's result was promised reference-counted, so the last cell holds an
// owned reference nothing else releases.
let isConsChainOntoSelfCall (branch: Expr) (state: CoreLoweringState) =
    match letChainBody(branch) with
        | ExprCons(_head, tail) ->
            match consChainTail(tail) with
                | ExprCall(_callee, _argument, _sugar, _layout) as call ->
                    match callSpineRootAndArity(call)(0) with
                        | (ExprVar(name), applied) ->
                            if isSelfCallName(name)(state)
                            then
                                match selfCallLabel(name)(state) with
                                    | Some(label) ->
                                        match state
                                        |> stateFunctionReturnedClosureLabels
                                        |> innermostStageLabel(applied - 1)(label) with
                                            | Some(stage) -> stage == stateLoweringBodyLabel(state)
                                            | None -> false
                                    | None -> false
                            else false
                        | _ -> false
                | _ -> false
        | _ -> false

// Whether a branch ends in a nested match or `if` one of whose arms, at any depth, conses onto the
// function's own full recursive call.
let recursive nestedJoinConsesOntoSelfResult (branch: Expr) (state: CoreLoweringState) =
    match letChainBody(branch) with
        | ExprMatch(_scrutinee, cases, _id) -> anyCaseConsesOntoSelfResult(cases)(state)
        | ExprIf(_condition, thenBranch, elseBranch) -> branchConsesOntoSelfResult(thenBranch)(state) || branchConsesOntoSelfResult(elseBranch)(state)
        | _ -> false
and anyCaseConsesOntoSelfResult (cases: List((Pattern, Expr, Maybe(Expr)))) (state: CoreLoweringState) =
    match cases with
        | [] -> false
        | (_pattern, body, _guard) :: rest -> branchConsesOntoSelfResult(body)(state) || anyCaseConsesOntoSelfResult(rest)(state)
and branchConsesOntoSelfResult (branch: Expr) (state: CoreLoweringState) = isConsChainOntoSelfCall(branch)(state) || nestedJoinConsesOntoSelfResult(branch)(state)

// The temp the most recent store wrote into a cons cell's tail, or -1.
let recursive consCellTailTemp (cellTemp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> -1
        | IrInstruction { instruction = StoreMemOffset(base, offset, source) } :: rest ->
            if base == cellTemp && offset == 8
            then source
            else consCellTailTemp(cellTemp)(rest)
        | _ :: rest -> consCellTailTemp(cellTemp)(rest)

// Stage 0's `IsConsChainCell`: a cons cell the chain builds here, an arena cell or a list cell
// built over a reuse token (the matched parameter's own cell), which is fresh whether the token's
// cell was on the arena or the heap.
let recursive definedByConsChainCell (temp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = Alloc(target, _size, runtimeManaged) } :: rest ->
            if target == temp
            then runtimeManaged == false
            else definedByConsChainCell(temp)(rest)
        | IrInstruction { instruction = AllocReusing(target, _tag, _fieldCount, _token, runtimeManaged, listCell, _tagless) } :: rest ->
            if target == temp
            then listCell && runtimeManaged == false
            else definedByConsChainCell(temp)(rest)
        | _ :: rest -> definedByConsChainCell(temp)(rest)

// The local slot a temp is a plain read of, or -1.
let recursive loadedSlotOf (temp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> -1
        | IrInstruction { instruction = LoadLocal(target, slot) } :: rest ->
            if target == temp
            then slot
            else loadedSlotOf(temp)(rest)
        | _ :: rest -> loadedSlotOf(temp)(rest)

// The slot `temp` was loaded from, through the borrows taken of the load.
let recursive loadedSlotThroughBorrows (temp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> -1
        | IrInstruction { instruction = LoadLocal(target, slot) } :: rest ->
            if target == temp
            then slot
            else loadedSlotThroughBorrows(temp)(rest)
        | IrInstruction { instruction = Borrow(target, source) } :: rest ->
            if target == temp
            then loadedSlotThroughBorrows(source)(rest)
            else loadedSlotThroughBorrows(temp)(rest)
        | _ :: rest -> loadedSlotThroughBorrows(temp)(rest)

// The sources of every store into a local slot.
let recursive slotStoreSources (slot: Int) (instructions: List(IrInstruction)) (sources: List(Int)) =
    match instructions with
        | [] -> sources
        | IrInstruction { instruction = StoreLocal(storeSlot, source) } :: rest ->
            if storeSlot == slot
            then slotStoreSources(slot)(rest)(source :: sources)
            else slotStoreSources(slot)(rest)(sources)
        | _ :: rest -> slotStoreSources(slot)(rest)(sources)

let recursive definedByCall (temp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = CallClosure(target, _closure, _argument, _flag) } :: rest ->
            if target == temp
            then true
            else definedByCall(temp)(rest)
        | IrInstruction { instruction = CallKnown(target, _label, _environment, _argument, _flag, _tail) } :: rest ->
            if target == temp
            then true
            else definedByCall(temp)(rest)
        | _ :: rest -> definedByCall(temp)(rest)

// The owned slot a call result was stored into as it is, or -1.
let recursive ownedSlotStoringTemp (temp: Int) (ownedSlots: List((Int, SemanticType))) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> -1
        | IrInstruction { instruction = StoreLocal(slot, source) } :: rest ->
            if source == temp && isOwnedSlotIn(slot)(ownedSlots)
            then slot
            else ownedSlotStoringTemp(temp)(ownedSlots)(rest)
        | _ :: rest -> ownedSlotStoringTemp(temp)(ownedSlots)(rest)

// Stage 0's `ArenaCellOf`: the arena cell a value is, directly or through the one store of the
// local it is read from; -1 otherwise.
let arenaCellOf (temp: Int) (state: CoreLoweringState) =
    if definedByConsChainCell(temp)(state.reversedInstructions)
    then temp
    else
        let slot = loadedSlotOf(temp)(state.reversedInstructions)
        in
            if slot < 0
            then -1
            else
                match slotStoreSources(slot)(state.reversedInstructions)([]) with
                    | source :: [] ->
                        if definedByConsChainCell(source)(state.reversedInstructions)
                        then source
                        else -1
                    | _ -> -1

// Stage 0's `ConsChainTailOwnedSlot`: where the reference a cons chain's last arena cell holds to
// its tail is kept. The owned slot keeping the recursive call's result, which the function's end
// releases when its result no longer reaches it; -1 when the tail is a call result or a read of a
// slot nothing releases; -2 when the chain or its tail is not recognized, and the reference is
// retained rather than taken over.
let recursive consChainTailOwnedSlotFrom (hops: Int) (cellTemp: Int) (state: CoreLoweringState) =
    if hops >= 64 || cellTemp < 0
    then -2
    else
        let tailTemp = consCellTailTemp(cellTemp)(state.reversedInstructions)
        in
            if tailTemp < 0
            then -2
            else
                let slot = loadedSlotOf(tailTemp)(state.reversedInstructions)
                in
                    if slot >= 0
                    then
                        if state
                        |> ownedSlotsOfActiveFunction
                        |> isOwnedSlotIn(slot)
                        then slot
                        else -1
                    else
                        if definedByCall(tailTemp)(state.reversedInstructions)
                        then
                            ownedSlotStoringTemp(tailTemp)(ownedSlotsOfActiveFunction(state))(state.reversedInstructions)
                        else
                            if definedByConsChainCell(tailTemp)(state.reversedInstructions)
                            then consChainTailOwnedSlotFrom(hops + 1)(tailTemp)(state)
                            else -2

let consChainTailOwnedSlot (valueTemp: Int) (state: CoreLoweringState) =
    consChainTailOwnedSlotFrom(0)(arenaCellOf(valueTemp)(state))(state)

// The branch copies such a chain's arena cells onto the heap and links the recursive result as
// their tail, taking over the reference the last cell held, so the join is owned on every branch
// and copied no further.
let emitConsChainOntoSelfResultCopy (temp: Int) (listType: SemanticType) (state: CoreLoweringState) =
    match argumentCopyPlanOf(listType)(state) with
        | Some(ListDeepArgumentCopy(elementPlan)) ->
            let ownedSlot = consChainTailOwnedSlot(temp)(state)
            in
                match emitListDeepCopyWith(true)(ownedSlot != -2)(ownedSlot)(temp)(elementPlan)(state) with
                    | (copied, copyTemp) -> Some((copied, copyTemp))
        | _ -> None

// Stage 0's `NormalizeParameterPassthroughBranch`: a branch result that is a plain read of the
// passthrough parameter is copied into an owned reference-counted graph, so the join it flows
// into is reference-counted on every branch. A branch consing onto the function's own recursive
// result moves that result into its copy instead.
let normalizeParameterPassthroughBranch (branch: Expr) (temp: Int) (state: CoreLoweringState) =
    match (statePassthroughParameter(state), tailForwardedVariable(branch)) with
        | (Some((name, slot, parameterType)), Some(variable)) ->
            if variable == name && isRuntimeTemp(temp)(state) == false && bindingIsLocalSlot(name)(slot)(state) && isPassthroughNormalizableParameterType(parameterType)(state)
            then emitOwnedPassthroughCopy(temp)(parameterType)(state)
            else (state, temp)
        | (Some((_name, _slot, parameterType)), None) ->
            match resolveType(state)(parameterType) with
                | SemList(_element) as listType ->
                    if isRuntimeTemp(temp)(state) == false && isPassthroughNormalizableParameterType(listType)(state) && isConsChainOntoSelfCall(branch)(state)
                    then
                        match emitConsChainOntoSelfResultCopy(temp)(listType)(state) with
                            | Some(moved) -> moved
                            | None -> (state, temp)
                    else (state, temp)
                | _ -> (state, temp)
        | _ -> (state, temp)

// Stage 0's `PredictRuntimeManagedResult`: the function's result is promised reference-counted
// before its body is lowered when the passthrough parameter's type is already copyable, so the
// body's own recursive call sites hand the result over as an owned value.
let predictRuntimeManagedResult (label: Str) (parameterType: SemanticType) (state: CoreLoweringState) =
    match statePassthroughParameter(state) with
        | Some(_parameter) ->
            if isPassthroughNormalizableParameterType(parameterType)(state)
            then
                state
                |> withStatePredictedRuntimeManagedResultLabels(label :: statePredictedRuntimeManagedResultLabels(state))
                |> recordBodyRuntimeManaged(label)(true)
            else state
        | None -> state

// Stage 0's `KeepPredictedRuntimeManagedResult`: a promised body result that did not end up
// reference-counted is copied into an owned graph before it is returned.
let keepPredictedRuntimeManagedResult (label: Str) (bodyTemp: Int) (bodyType: SemanticType) (state: CoreLoweringState) =
    if containsLabel(label)(statePredictedRuntimeManagedResultLabels(state)) && isRuntimeTemp(bodyTemp)(state) == false
    then emitOwnedPassthroughCopy(bodyTemp)(bodyType)(state)
    else (state, bodyTemp)

// Stage 0's `BeginCurryStage` and `LinkCurryStage`: a stage whose body is a lambda records the
// returned-closure link as soon as that lambda is entered, so a recursive call inside it
// resolves through the chain while the body is still being lowered.
let beginCurryStage (label: Str) (body: Expr) (state: CoreLoweringState) =
    match unspanArgument(body) with
        | ExprLambda(parameter, _inner, _annotation) -> withStateCurryStage(Some((label, parameter)))(state)
        | _ -> withStateCurryStage(None)(state)

// Stage 0's `NoteLetBoundCurryStage`: a curry stage a let-bound function returns is let-bound too.
let noteLetBoundCurryStage (stageLabel: Str) (label: Str) (state: CoreLoweringState) =
    if state
    |> stateLetBoundFunctionLabels
    |> containsLabel(stageLabel)
    then withStateLetBoundFunctionLabels(label :: stateLetBoundFunctionLabels(state))(state)
    else state

// Stage 0's `NoteLetBoundFunction`: a function whose origin is a source function is the value a
// `let` binds.
let noteLetBoundFunction (label: Str) (origin: IrFunctionOrigin) (state: CoreLoweringState) =
    match origin.originKind with
        | SourceFunctionOriginKind -> withStateLetBoundFunctionLabels(label :: stateLetBoundFunctionLabels(state))(state)
        | _ -> state

let linkCurryStage (label: Str) (parameter: Str) (state: CoreLoweringState) =
    match stateCurryStage(state) with
        | Some((stageLabel, stageParameter)) ->
            if stageParameter == parameter
            then
                state
                |> withStateCurryStage(None)
                |> withStateFunctionReturnedClosureLabels(state
                |> stateFunctionReturnedClosureLabels
                |> Ashes.Collection.Map.setStr(stageLabel)(label))
                |> noteLetBoundCurryStage(stageLabel)(label)
            else withStateCurryStage(None)(state)
        | None -> state

let recursive bindSelfCallResults (pairs: List((SemanticType, SemanticType))) (state: CoreLoweringState) =
    match pairs with
        | [] -> (state, None)
        | (deferred, original) :: rest ->
            match bindType(original)(deferred)(state) with
                | (failedState, Some(error)) -> (failedState, Some(error))
                | (bound, None) -> bindSelfCallResults(rest)(bound)

// Stage 0's end-of-body unification of a recursive binding's lowering-local result type, for the
// self calls that took a fresh result type (`deferSelfCallResultType`): each meets the binding's
// own once the body is done, so a deferred copy-out resolves against the type the body settled
// on. Applied right after the spine closes, before any ownership step reads the body's type.
// The result's layout is read as the function's own return reads it, its phantoms as `Never`, so a
// self call agrees with the return on whether the result is one of the contract's types.
let recursive markOwnedDeferredSelfCalls (label: Str) (pending: List(CorePendingCallCopyOut)) (state: CoreLoweringState) =
    match pending with
        | [] -> []
        | copyOut :: rest ->
            (let layout = resolvePhantomResultLayout(label)(copyOut.deferredType)(state)
            in
                if copyOut.deferredSelfCall && isGeneralRcValueType(layout)(state)
                then copyOut with deferredOwned = true, deferredType = layout
                else copyOut) :: markOwnedDeferredSelfCalls(label)(rest)(state)

let recursive registerOwnedDeferredSlots (pending: List(CorePendingCallCopyOut)) (state: CoreLoweringState) =
    match pending with
        | [] -> state
        | copyOut :: rest ->
            (if copyOut.deferredOwned
            then registerGeneralRcOwnedSlot(copyOut.deferredResultSlot)(copyOut.deferredType)(state)
            else state) |> registerOwnedDeferredSlots(rest)

// Stage 0 pins a self call's result type at the call from its discovery pass, and keeps a result
// of the contract's types in an owned slot the function releases (`LowerGeneralRcCallResult`).
// This lowering learns that type only once the recursive binding's result is unified at the
// body's end: each deferred self call whose result is then of the contract's types registers its
// result slot as an owned slot there, before the function's result releases its owned slots, and
// its deferred block becomes the owned-result block. A loop's owned slots are released at its
// back edges as well, which are already lowered, so a loop keeps the deferred copy-out; a loop with no
// back edge (a non-tail recursion lowered under a loop scaffold) adopts them like any other body.
// A loop whose exit already made the result its own and released the owned slots (a curried
// stage's loop body) releases the adopted results right away, the result holding its own
// references.
let recursive adoptedDeferredSlots (pending: List(CorePendingCallCopyOut)) (state: CoreLoweringState) =
    match pending with
        | [] -> []
        | copyOut :: rest ->
            if copyOut.deferredOwned
            then (stateActiveFunctionOrigin(state), copyOut.deferredResultSlot, copyOut.deferredType) :: adoptedDeferredSlots(rest)(state)
            else adoptedDeferredSlots(rest)(state)

let adoptMarkedDeferredSelfCalls (label: Str) (resultFinished: Bool) (state: CoreLoweringState) =
    (let marked =
        markOwnedDeferredSelfCalls(label)(statePendingCallCopyOuts(state))(state)
    in
        if resultFinished
        then
            state
            |> withStatePendingCallCopyOuts(marked)
            |> emitGeneralRcOwnedSlotDropsOf(stateActiveFunctionOrigin(state))(adoptedDeferredSlots(reverse(marked))(state))
        else
            state
            |> withStatePendingCallCopyOuts(marked)
            |> registerOwnedDeferredSlots(reverse(marked)))

let adoptDeferredSelfCallResults (label: Str) (bodyTemp: Int) (state: CoreLoweringState) =
    match (stateTcoLoopFrame(state), statePendingTcoResets(state)) with
        | (None, _) -> adoptMarkedDeferredSelfCalls(label)(false)(state)
        | (Some(_frame), []) ->
            adoptMarkedDeferredSelfCalls(label)(isGeneralRcFinishedResult(bodyTemp)(state))(state)
        | (Some(_frame), _resets) -> state

let unifySelfCallResults (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { error = Some(_error) } -> lowered
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            match bindSelfCallResults(state
            |> stateSelfCallResultUnifications
            |> reverse)(state) with
                | (failedState, Some(error)) -> failure(failedState)(error)
                | (bound, None) -> LoweredCoreValue(state = withStateSelfCallResultUnifications([])(bound), temp = temp, semanticType = semanticType, error = None)

// Stage 0's `LowerLambdaCoreEmitRuntimeManagedTcoExitDrops` reserves the exit-transfer selection
// slot whenever a loop's result is reference-counted, before it walks the runtime-managed
// parameter slots; a loop that placed no parameter has nothing to walk and the zero store stays.
// `emitTcoExitDrops` emits it with the walk, so this covers only the loop with no placed
// parameter, at the point stage 0 reaches it: after the spine closes, before the result-ownership
// epilogue. A result of the contract's types is made the function's own first, and holds its own
// reference, so no parameter is transferred as the result.
let reserveTcoExitTransferSlot (label: Str) (bodyTemp: Int) (bodyType: SemanticType) (state: CoreLoweringState) =
    match stateTcoLoopFrame(state) with
        | Some(CoreTcoLoopFrame { bodyLabel = bodyLabel, runtimeManagedListSlots = [], runtimeManagedAdtSlots = [], runtimeManagedStrSlots = [] }) ->
            if bodyLabel == label + "_body" && isRuntimeTemp(bodyTemp)(state) && !exitResultOwnsItsReference(bodyTemp)(state) && !isGeneralRcValueType(bodyType)(state)
            then
                match freshLocal(state) with
                    | FreshLocal { state = slotState, local = transferSelectedSlot } ->
                        match freshTemp(slotState) with
                            | FreshTemp { state = zeroState, temp = zeroTemp } ->
                                zeroState
                                |> emit(LoadConstInt(zeroTemp)(0))
                                |> emit(StoreLocal(transferSelectedSlot)(zeroTemp))
            else state
        | _ -> state

// The operands of an expression that only combines its operands' values: an operator, a cons, a
// tuple, a list or a record literal.
let combinedOperands (expression: Expr) =
    match expression with
        | ExprAdd(left, right) -> Some([left, right])
        | ExprSubtract(left, right) -> Some([left, right])
        | ExprMultiply(left, right) -> Some([left, right])
        | ExprDivide(left, right) -> Some([left, right])
        | ExprModulo(left, right) -> Some([left, right])
        | ExprBitwiseAnd(left, right) -> Some([left, right])
        | ExprBitwiseOr(left, right) -> Some([left, right])
        | ExprBitwiseXor(left, right) -> Some([left, right])
        | ExprShiftLeft(left, right) -> Some([left, right])
        | ExprShiftRight(left, right) -> Some([left, right])
        | ExprBitwiseNot(operand) -> Some([operand])
        | ExprLogicalNot(operand) -> Some([operand])
        | ExprLogicalAnd(left, right) -> Some([left, right])
        | ExprLogicalOr(left, right) -> Some([left, right])
        | ExprGreaterThan(left, right) -> Some([left, right])
        | ExprLessThan(left, right) -> Some([left, right])
        | ExprGreaterOrEqual(left, right) -> Some([left, right])
        | ExprLessOrEqual(left, right) -> Some([left, right])
        | ExprEqual(left, right) -> Some([left, right])
        | ExprNotEqual(left, right) -> Some([left, right])
        | ExprCons(head, tail) -> Some([head, tail])
        | ExprTuple(elements) -> Some(elements)
        | ExprList(elements, _isMultiline) -> Some(elements)
        | ExprRecord(_constructorName, fields, _isMultiline) ->
            fields
            |> map(given (field: (Str, Expr)) ->
                match field with
                    | (_fieldName, value) -> value)
            |> Some
        | _ -> None

let isVariableNamed (name: Str) (expression: Expr) =
    match unspanArgument(expression) with
        | ExprVar(candidate) -> candidate == name
        | _ -> false

// Stage 0's `ParameterReadOnlyThroughFieldsOrCalls`: every use of the parameter in the body reads
// it without storing it, as a field read, the target of a record update or a match, or an argument
// of a call (a callee borrows it or takes its own reference), or an element of a tuple or record
// literal, which retains or copies it. A closure mentioning it at all, and anything unrecognized,
// fails.
let recursive parameterReadOnlyThroughFieldsOrCalls (name: Str) (depth: Int) (returned: Bool) (expression: Expr) =
    if depth > 256
    then false
    else
        match expression with
            | ExprAt(_span, inner) -> parameterReadOnlyThroughFieldsOrCalls(name)(depth)(returned)(inner)
            | ExprNode(_node, inner) -> parameterReadOnlyThroughFieldsOrCalls(name)(depth)(returned)(inner)
            | ExprVar(candidate) -> returned || candidate != name
            | ExprQualifiedVar(_qualifier, _member) -> true
            | ExprInt(_value) -> true
            | ExprUInt(_value, _bits, _text) -> true
            | ExprBigInt(_text) -> true
            | ExprFloat(_value, _text) -> true
            | ExprString(_value) -> true
            | ExprRune(_value) -> true
            | ExprBool(_value) -> true
            | ExprCall(function, argument, _sugar, _layout) -> parameterReadOnlyThroughFieldsOrCalls(name)(depth + 1)(false)(function) && (isVariableNamed(name)(argument) || parameterReadOnlyThroughFieldsOrCalls(name)(depth + 1)(false)(argument))
            | ExprRecordUpdate(target, fields) -> (isVariableNamed(name)(target) || parameterReadOnlyThroughFieldsOrCalls(name)(depth + 1)(false)(target)) && fieldsReadParameterOnly(name)(depth + 1)(fields)
            | ExprLet(bound, value, body, _parameters, _annotation, _requirements) -> parameterReadOnlyThroughFieldsOrCalls(name)(depth + 1)(false)(value) && (bound == name || parameterReadOnlyThroughFieldsOrCalls(name)(depth + 1)(returned)(body))
            | ExprIf(condition, thenBranch, elseBranch) -> parameterReadOnlyThroughFieldsOrCalls(name)(depth + 1)(false)(condition) && parameterReadOnlyThroughFieldsOrCalls(name)(depth + 1)(returned)(thenBranch) && parameterReadOnlyThroughFieldsOrCalls(name)(depth + 1)(returned)(elseBranch)
            | ExprMatch(value, cases, _position) -> (isVariableNamed(name)(value) || parameterReadOnlyThroughFieldsOrCalls(name)(depth + 1)(false)(value)) && casesReadParameterOnly(name)(depth + 1)(returned)(cases)
            | ExprLambda(parameter, body, _annotation) -> parameter == name || !exprReadsName(name)(body)
            | ExprTuple(elements) -> elementsRetainParameter(name)(depth + 1)(elements)
            | ExprRecord(_constructorName, fields, _isMultiline) ->
                fields
                |> map(given (field: (Str, Expr)) ->
                    match field with
                        | (_fieldName, value) -> value)
                |> elementsRetainParameter(name)(depth + 1)
            | other ->
                match combinedOperands(other) with
                    | Some(operands) -> operandsReadParameterOnly(name)(depth + 1)(operands)
                    | None -> !exprReadsName(name)(other)
and elementsRetainParameter (name: Str) (depth: Int) (elements: List(Expr)) =
    match elements with
        | [] -> true
        | element :: rest -> (isVariableNamed(name)(element) || parameterReadOnlyThroughFieldsOrCalls(name)(depth)(false)(element)) && elementsRetainParameter(name)(depth)(rest)
and operandsReadParameterOnly (name: Str) (depth: Int) (operands: List(Expr)) =
    match operands with
        | [] -> true
        | operand :: rest -> parameterReadOnlyThroughFieldsOrCalls(name)(depth)(false)(operand) && operandsReadParameterOnly(name)(depth)(rest)
and fieldsReadParameterOnly (name: Str) (depth: Int) (fields: List((Str, Expr))) =
    match fields with
        | [] -> true
        | (_fieldName, value) :: rest -> parameterReadOnlyThroughFieldsOrCalls(name)(depth)(false)(value) && fieldsReadParameterOnly(name)(depth)(rest)
and casesReadParameterOnly (name: Str) (depth: Int) (returned: Bool) (cases: List((Pattern, Expr, Maybe(Expr)))) =
    match cases with
        | [] -> true
        | (pattern, body, guard) :: rest ->
            (patternBindsName(name)(pattern) || (match guard with
                | Some(condition) -> parameterReadOnlyThroughFieldsOrCalls(name)(depth)(false)(condition)
                | None -> true) && parameterReadOnlyThroughFieldsOrCalls(name)(depth)(returned)(body)) && casesReadParameterOnly(name)(depth)(returned)(rest)

// Stage 0's `CanOwnResultBehindNormalizedParameter`: the results a copy turns into an owned value
// that keeps nothing of the parameter alive but references of its own, a record or variant, and a
// tuple of those and of scalars, which a function returning its rebuilt value beside a second
// result hands back.
let recursive canOwnResultBehindNormalizedParameter (resultType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(resultType) with
        | SemNamed(_symbolId, _name, _arguments) as named ->
            match (entryNormalizationPlanOf(named)(state), heapFactsOf(named)(state)) with
                | (Some(_plan), HeapLayoutFacts { arenaDeepCopySupported = true }) -> true
                | _ -> false
        | SemTuple(elements) -> allOwnableBehindParameter(elements)(state)
        | _ -> false
and allOwnableBehindParameter (elements: List(SemanticType)) (state: CoreLoweringState) =
    match elements with
        | [] -> true
        | element :: rest ->
            (resultSurvivesReset(element)(state) || (match resolveType(state)(element) with
                | SemNamed(_symbolId, _name, _arguments) as part -> canOwnResultBehindNormalizedParameter(part)(state)
                | _ -> false)) && allOwnableBehindParameter(rest)(state)

let emitNormalizedParameterRelease (parameterType: SemanticType) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = allocated, temp = parameterTemp } ->
            allocated
            |> emit(LoadLocal(parameterTemp)(1))
            |> emitArenaResultRelease(parameterTemp)(parameterType)

// Stage 0's `ReleaseNormalizedParameterAfterContractResult`: a result of the contract's types the copy
// does not express (a generic wrapper such as `Ok(state with ...)`) is still normalized into an
// owned value at the function's return, which retains whatever it keeps of the parameter: the
// parameter becomes an owned slot released there, after that normalization.
let releaseNormalizedParameterAfterContractResult (label: Str) (parameterType: SemanticType) (bodyType: SemanticType) (state: CoreLoweringState) =
    if isGeneralRcValueType(resolvePhantomResultLayout(label)(bodyType)(state))(state)
    then registerGeneralRcOwnedSlot(1)(parameterType)(state)
    else state

// A string parameter is released behind the result like a record parameter: a result that copies
// it out holds references of its own.
let releasesNormalizedParameterOfType (parameterType: SemanticType) =
    match parameterType with
        | SemNamed(_symbolId, _name, _arguments) -> true
        | SemString -> true
        | _ -> false

let recursive tupleElementsStoreParameterOnlyBare (name: Str) (elements: List(Expr)) =
    match elements with
        | [] -> true
        | element :: rest -> (isVariableNamed(name)(element) || !exprReadsName(name)(element)) && tupleElementsStoreParameterOnlyBare(name)(rest)

// Stage 0's `ReachesResultOnlyAsClonedTupleElement`: an arena tuple the function returns stores an
// independent copy of a string parameter read as one of its elements, so the owned parameter the
// entry normalization made is referenced by nothing once the tuple is built. Only a body that is
// that tuple, with every other element and every let value before it not mentioning the
// parameter, qualifies: any other path could store the parameter itself.
let recursive bodyReachesParameterOnlyAsTupleElement (name: Str) (body: Expr) =
    match body with
        | ExprAt(_span, inner) -> bodyReachesParameterOnlyAsTupleElement(name)(inner)
        | ExprNode(_node, inner) -> bodyReachesParameterOnlyAsTupleElement(name)(inner)
        | ExprLet(bound, value, rest, _parameters, _annotation, _requirements) -> bound != name && !exprReadsName(name)(value) && bodyReachesParameterOnlyAsTupleElement(name)(rest)
        | ExprTuple(elements) -> tupleElementsStoreParameterOnlyBare(name)(elements)
        | _ -> false

let reachesResultOnlyAsClonedTupleElement (parameter: Str) (body: Expr) (parameterType: SemanticType) (state: CoreLoweringState) = parameterType == SemString && stateNormalizedParameterCloned(state) && bodyReachesParameterOnlyAsTupleElement(parameter)(body)

// The release behind a result that is not reference-counted: right after it when the result holds
// an independent copy of the parameter, after the result's copy into an owned value when the
// contract can express one, and otherwise at the return, after the result's normalization.
let releaseNormalizedParameterBehindArenaResult (label: Str) (parameter: Str) (body: Expr) (parameterType: SemanticType) (bodyType: SemanticType) (kept: (CoreLoweringState, Int)) =
    match kept with
        | (state, bodyTemp) ->
            if reachesResultOnlyAsClonedTupleElement(parameter)(body)(parameterType)(state)
            then (emitNormalizedParameterRelease(parameterType)(state), bodyTemp)
            else
                if canOwnResultBehindNormalizedParameter(bodyType)(state)
                then
                    match argumentCopyPlanOf(bodyType)(state) with
                        | None -> kept
                        | Some(plan) ->
                            match emitArgumentCopy(bodyTemp)(entryCopyPlanOf(bodyType)(plan)(state))(state) with
                                | (copied, ownedTemp) ->
                                    (copied
                                    |> emitNormalizedParameterRelease(parameterType)
                                    |> markRuntimeTemp(ownedTemp)(RuntimeNewlyProduced), ownedTemp)
                else (releaseNormalizedParameterAfterContractResult(label)(parameterType)(bodyType)(state), bodyTemp)

// Stage 0's `ReleaseNormalizedParameterBehindResult`: a function whose entry normalizes its
// always-returned parameter owns that parameter, and a result other than the parameter itself only
// borrows what it reaches of it: a record update shares the parameter's unchanged children, a cons
// shares its list. Nothing else releases the parameter, so every call would leak it. The result is
// therefore made reference-counted first (retained when it already is, copied otherwise, which
// retains every reference-counted child it reaches) and the parameter released after it. A result
// that is reference-counted already keeps nothing of the parameter alive unless the parameter
// itself went into it, which only a bare read of it outside a call argument can do; a bare read
// the function returns is the one exception, since a reference-counted join with such an arm
// retained the parameter there, and the release hands that reference to the result. A tuple or
// record literal storing the parameter retains or copies it, so it is released after them too.
let releaseNormalizedParameterBehindResult (label: Str) (parameter: Str) (body: Expr) (parameterType: SemanticType) (bodyType: SemanticType) (kept: (CoreLoweringState, Int)) =
    match kept with
        | (state, bodyTemp) ->
            match (stateTcoLoopFrame(state), resolveType(state)(parameterType)) with
                | (None, named) ->
                    if !releasesNormalizedParameterOfType(named) || !acceptsRuntimeManagedArgument(label)(state) || definesSlotRead(bodyTemp)(1)(state.reversedInstructions)
                    then kept
                    else
                        if isRuntimeTemp(bodyTemp)(state)
                        then
                            // A reference-counted aggregate stores the owned string parameter as it
                            // is, without a retain: the result releases it.
                            if named != SemString && parameterReadOnlyThroughFieldsOrCalls(parameter)(0)(true)(body)
                            then (emitNormalizedParameterRelease(named)(state), bodyTemp)
                            else kept
                        else releaseNormalizedParameterBehindArenaResult(label)(parameter)(body)(named)(bodyType)(kept)
                | _ -> kept

// A closure handed out of the scope of a reference-counted `let` binding it captures keeps an
// arena copy of the binding's value in its environment: the binding releases its own reference
// when its scope ends, which is before the closure runs. A stack closure never leaves that scope,
// and a capture of any other binding is stored as it was read.
let copyEscapingCapture (stackAllocate: Bool) (binding: CoreBinding) (captureTemp: Int) (captureType: SemanticType) (state: CoreLoweringState) =
    match binding with
        | CoreBinding { name = name } ->
            if stackAllocate || containsName(name)(stateEscapingCaptureNames(state)) == false
            then (state, captureTemp)
            else
                match liveRuntimeOwnerSlot(name)(state) with
                    | None -> (state, captureTemp)
                    | Some(_slot) ->
                        if resultSurvivesReset(captureType)(state) || containsUnresolvedLayout(captureType)(state) || isGeneralRcAdmissible(captureType)([])(state) == false
                        then (state, captureTemp)
                        else emitLocatedDeepCopy(captureTemp)(captureType)(state)

let finishLambdaBody (parameter: Str) (body: Expr) label origin captures stackAllocate typedOuter parameterType lowered =
    match lowered |> unifySelfCallResults with
        | LoweredCoreValue { state = failedBody, error = Some(error) } -> failure(failedBody)(error)
        | LoweredCoreValue { state = bodyState, temp = loweredTemp, semanticType = bodyType, error = None } ->
            match bodyState
            |> adoptDeferredSelfCallResults(label)(loweredTemp)
            |> adoptNormalizedParameterResult(label)(loweredTemp)
            |> reserveTcoExitTransferSlot(label)(loweredTemp)(bodyType)
            |> keepPredictedRuntimeManagedResult(label)(loweredTemp)(bodyType)
            |> releaseNormalizedParameterBehindResult(label)(parameter)(body)(parameterType)(bodyType) with
                | (loweredBody, bodyTemp) ->
                    let bodyRuntimeManaged =
                        finishedResultRuntimeManaged(bodyTemp)(resolvePhantomNormalizedResultLayout(label)(resolvePhantomResultLayout(label)(bodyType)(loweredBody))(loweredBody))(loweredBody)
                    in
                        let returned =
                            match normalizeFunctionResult(label)(bodyTemp)(bodyType)(loweredBody) with
                                | (normalized, returnedTemp) ->
                                    normalized
                                    |> emit(Return(returnedTemp))
                                    |> resolvePendingTcoResets
                        in
                            match pruneDeadCaptures(captures)(returned.reversedInstructions) with
                                | (survivors, prunedInstructions) ->
                                    let finishedBody =
                                        (returned with reversedInstructions = prunedInstructions)
                                        |> backfillSelfClosureResultOwnership(label)(bodyRuntimeManaged)
                                        |> finishLiftedFunction(label)(origin)
                                    in
                                        finishedBody
                                        |> restoreOuterFrame(typedOuter)
                                        |> recordBodyRuntimeManaged(label)(bodyRuntimeManaged)
                                        |> recordReturnedClosureLabel(label)(bodyTemp)(loweredBody.reversedInstructions)
                                        |> markCapturedResourcesMoved(survivors)
                                        |> allocateEnvironment(copyEscapingCapture(stackAllocate))(captures)(survivors)(stackAllocate)
                                        |> emitPrunedClosure(label)(origin)(survivors)(stackAllocate)(parameterType)(bodyType)(finishedBody)

// A type annotation (an ADT constructor field's, or — via `lowerLambdaParameterType` below — an
// explicit lambda parameter's) is resolved against exactly the scalar primitives listed here, plus
// (via `parameterTypes`) the enclosing type's own type parameters, plus the pure function arrow
// over such types — not through `TypeResolution.ash`'s real `resolveTypeExpression`, which needs a
// full `TypeEnvironment` this single-file pipeline does not build. An annotation outside this list
// (a resource, an arrow carrying a capability row) answers `None` — the caller's job to treat that
// as "can't check this one," not as an error, since it is a gap in this resolver, not proof the
// annotation is invalid.
let recursive lookupTypeParameter (name: Str) (parameterTypes: List((Str, SemanticType))) =
    match parameterTypes with
        | [] -> None
        | (candidateName, semanticType) :: rest ->
            if candidateName == name
            then Some(semanticType)
            else lookupTypeParameter(name)(rest)

// Stage 0's `ResolveTypeAlias`: a type expression with every alias replaced by its target, the
// alias's parameters substituted by the written arguments. An alias applied with the wrong number
// of arguments, or a name that is no alias, is left as written; the fuel bounds an alias cycle.
let recursive lookupBoundTypeExpr (name: Str) (bindings: List((Str, TypeExpr))) =
    match bindings with
        | [] -> None
        | (candidateName, bound) :: rest ->
            if candidateName == name
            then Some(bound)
            else lookupBoundTypeExpr(name)(rest)

let recursive substituteTypeParameters (bindings: List((Str, TypeExpr))) (typeExpr: TypeExpr) =
    match typeExpr with
        | TypeAt(span, inner) ->
            inner
            |> substituteTypeParameters(bindings)
            |> TypeAt(span)
        | TypeNamed(name) ->
            match lookupBoundTypeExpr(name)(bindings) with
                | Some(bound) -> bound
                | None -> typeExpr
        | TypeApplied(name, arguments) ->
            arguments
            |> substituteTypeParametersList(bindings)
            |> TypeApplied(name)
        | TypeArrow(argument, result, capabilities, tail) ->
            TypeArrow(substituteTypeParameters(bindings)(argument))(substituteTypeParameters(bindings)(result))(capabilities)(tail)
        | TypeTuple(elements) ->
            elements
            |> substituteTypeParametersList(bindings)
            |> TypeTuple
        | TypeUnit -> TypeUnit
and substituteTypeParametersList (bindings: List((Str, TypeExpr))) (typeExprs: List(TypeExpr)) =
    match typeExprs with
        | [] -> []
        | head :: rest -> substituteTypeParameters(bindings)(head) :: substituteTypeParametersList(bindings)(rest)

let recursive bindTypeParameters (names: List(Str)) (arguments: List(TypeExpr)) =
    match (names, arguments) with
        | (name :: restNames, argument :: restArguments) -> (name, argument) :: bindTypeParameters(restNames)(restArguments)
        | _ -> []

// An `external type` has no type expression of its own, so its name stands for this applied form:
// the space keeps it out of reach of any written type name. Alias expansion carries it to
// `typeExprToSemanticType`, which reads it back as the opaque type the external signatures use.
let externalTypeExpr (name: Str) = TypeApplied("external type")([TypeNamed(name)])

let recursive expandTypeAliases (aliases: MapTree(Str, (List(Str), TypeExpr))) (fuel: Int) (typeExpr: TypeExpr) =
    if fuel <= 0
    then typeExpr
    else
        match typeExpr with
            | TypeAt(span, inner) ->
                inner
                |> expandTypeAliases(aliases)(fuel)
                |> TypeAt(span)
            | TypeNamed(name) ->
                match Ashes.Collection.Map.getStr(name)(aliases) with
                    | Some(([], target)) -> expandTypeAliases(aliases)(fuel - 1)(target)
                    | _ -> typeExpr
            | TypeApplied("external type", _) -> typeExpr
            | TypeApplied(name, arguments) ->
                let expandedArguments = expandTypeAliasesList(aliases)(fuel)(arguments)
                in
                    match Ashes.Collection.Map.getStr(name)(aliases) with
                        | Some((parameters, target)) ->
                            if length(parameters) == length(expandedArguments)
                            then
                                target
                                |> substituteTypeParameters(bindTypeParameters(parameters)(expandedArguments))
                                |> expandTypeAliases(aliases)(fuel - 1)
                            else TypeApplied(name)(expandedArguments)
                        | None -> TypeApplied(name)(expandedArguments)
            | TypeArrow(argument, result, capabilities, tail) ->
                TypeArrow(expandTypeAliases(aliases)(fuel)(argument))(expandTypeAliases(aliases)(fuel)(result))(capabilities)(tail)
            | TypeTuple(elements) ->
                elements
                |> expandTypeAliasesList(aliases)(fuel)
                |> TypeTuple
            | TypeUnit -> TypeUnit
and expandTypeAliasesList (aliases: MapTree(Str, (List(Str), TypeExpr))) (fuel: Int) (typeExprs: List(TypeExpr)) =
    match typeExprs with
        | [] -> []
        | head :: rest -> expandTypeAliases(aliases)(fuel)(head) :: expandTypeAliasesList(aliases)(fuel)(rest)

let recursive typeParameterNames (parameters: List(TypeParameter)) =
    match parameters with
        | [] -> []
        | TypeParameter { name = name } :: rest -> name :: typeParameterNames(rest)

// Every type alias of the program, collected before any declaration is registered.
let recursive typeAliasesOf (items: List(TopLevelItem)) (aliases: MapTree(Str, (List(Str), TypeExpr))) =
    match items with
        | [] -> aliases
        | TopLevelAt(_span, inner) :: rest -> typeAliasesOf(inner :: rest)(aliases)
        | TopLevelExternal(ExternalOpaqueType(name, _destructor)) :: rest ->
            aliases
            |> Ashes.Collection.Map.setStr(name)(([], externalTypeExpr(name)))
            |> typeAliasesOf(rest)
        | TopLevelTypeAlias(TypeAliasDecl { name = name, typeParameters = typeParameters, target = target }) :: rest ->
            aliases
            |> Ashes.Collection.Map.setStr(name)((typeParameterNames(typeParameters), target))
            |> typeAliasesOf(rest)
        | _ :: rest -> typeAliasesOf(rest)(aliases)

let recursive typeExprToSemanticType (typeExpr: TypeExpr) (parameterTypes: List((Str, SemanticType))) =
    match typeExpr with
        | TypeAt(_span, inner) -> typeExprToSemanticType(inner)(parameterTypes)
        | TypeNamed("Int") -> Some(SemInt)
        | TypeNamed("Str") -> Some(SemString)
        | TypeNamed("Bool") -> Some(SemBool)
        | TypeNamed("Float") -> Some(SemFloat)
        | TypeNamed("BigInt") -> Some(SemBigInt)
        | TypeNamed("Rune") -> Some(SemRune)
        | TypeNamed("Bytes") -> Some(SemBytes)
        | TypeNamed("u8") -> Some(SemUInt(8))
        | TypeNamed("u16") -> Some(SemUInt(16))
        | TypeNamed("u32") -> Some(SemUInt(32))
        | TypeNamed("u64") -> Some(SemUInt(64))
        | TypeUnit ->
            []
            |> SemNamed(0)("Unit")
            |> Some
        | TypeNamed(name) ->
            match lookupTypeParameter(name)(parameterTypes) with
                | Some(parameterType) -> Some(parameterType)
                | None ->
                    []
                    |> SemNamed(0)(name)
                    |> Some
        | TypeApplied("external type", TypeNamed(name) :: []) -> Some(SemOpaque(name))
        | TypeApplied("List", element :: []) ->
            match typeExprToSemanticType(element)(parameterTypes) with
                | None -> None
                | Some(elementType) -> Some(SemList(elementType))
        | TypeApplied(name, arguments) ->
            match typeExprListToSemanticTypes(arguments)(parameterTypes) with
                | None -> None
                | Some(argumentTypes) ->
                    argumentTypes
                    |> SemNamed(0)(name)
                    |> Some
        | TypeTuple(elements) ->
            match typeExprListToSemanticTypes(elements)(parameterTypes) with
                | None -> None
                | Some(elementTypes) -> Some(SemTuple(elementTypes))
        | TypeArrow(argument, result, [], None) ->
            match typeExprToSemanticType(argument)(parameterTypes) with
                | None -> None
                | Some(argumentType) ->
                    match typeExprToSemanticType(result)(parameterTypes) with
                        | None -> None
                        | Some(resultType) ->
                            None
                            |> SemFunction(argumentType)(resultType)
                            |> Some
        | _other -> None
and typeExprListToSemanticTypes (typeExprs: List(TypeExpr)) (parameterTypes: List((Str, SemanticType))) =
    match typeExprs with
        | [] -> Some([])
        | typeExpr :: rest ->
            match typeExprToSemanticType(typeExpr)(parameterTypes) with
                | None -> None
                | Some(semanticType) ->
                    match typeExprListToSemanticTypes(rest)(parameterTypes) with
                        | None -> None
                        | Some(restTypes) -> Some(semanticType :: restTypes)

// Binds a lambda parameter's fresh type variable to its explicit annotation, when one was written
// and this resolver can understand it — `given (x: Bool) -> ...`, `let f (x: Bool) = ...` (the
// parser desugars both into the same `ExprLambda` shape). An unresolvable annotation (a function
// type, a resource, anything `typeExprToSemanticType` answers `None` for) is left unchecked rather
// than rejected, matching this resolver's existing constructor-field contract: an unproven "can't
// check this" is not the same as "this is wrong."
// A lowercase type name other than an unsigned integer width (stage 0's `CollectTypeParameterNames`).
let isAnnotationTypeVariableName (name: Str) =
    if name == "u8" || name == "u16" || name == "u32" || name == "u64"
    then false
    else
        let bytes = Ashes.Byte.fromText(name)
        in
            if Ashes.Byte.length(bytes) <= 0
            then false
            else
                let first =
                    0
                    |> Ashes.Byte.get(bytes)
                    |> Ashes.Number.UInt.toInt
                in first >= 97 && first <= 122

let recursive annotationTypeVariableNames (typeExpr: TypeExpr) (names: List(Str)) =
    match typeExpr with
        | TypeAt(_span, inner) -> annotationTypeVariableNames(inner)(names)
        | TypeNamed(name) ->
            if isAnnotationTypeVariableName(name) && !containsName(name)(names)
            then name :: names
            else names
        | TypeApplied(_name, arguments) -> annotationTypeVariableNamesIn(arguments)(names)
        | TypeArrow(argument, result, _capabilities, _tail) ->
            names
            |> annotationTypeVariableNames(argument)
            |> annotationTypeVariableNames(result)
        | TypeTuple(elements) -> annotationTypeVariableNamesIn(elements)(names)
        | TypeUnit -> names
and annotationTypeVariableNamesIn (types: List(TypeExpr)) (names: List(Str)) =
    match types with
        | [] -> names
        | head :: rest ->
            names
            |> annotationTypeVariableNames(head)
            |> annotationTypeVariableNamesIn(rest)

// Stage 0's `EnterInlineAnnotationTypeVariables`: a curry stage shares its enclosing stage's
// inline annotation type variables; any other lambda starts with none.
let enterInlineTypeVariables (parameter: Str) (state: CoreLoweringState) =
    match stateCurryStage(state) with
        | Some((_stageLabel, stageParameter)) ->
            if stageParameter == parameter
            then state
            else withStateInlineTypeVariables([])(state)
        | None -> withStateInlineTypeVariables([])(state)

// Each lowercase name of an inline annotation not yet a type variable of the function, nor a type
// parameter in scope, becomes a fresh one (stage 0's `ResolveInlineParameterAnnotation`).
let recursive bindInlineTypeVariables (names: List(Str)) (state: CoreLoweringState) =
    match names with
        | [] -> state
        | name :: rest ->
            match (state
            |> stateInlineTypeVariables
            |> lookupTypeParameter(name), state
            |> stateActiveTypeParameters
            |> lookupTypeParameter(name)) with
                | (None, None) ->
                    match freshType(state) with
                        | FreshType { state = fresh, semanticType = variable } ->
                            fresh
                            |> withStateInlineTypeVariables((name, variable) :: stateInlineTypeVariables(fresh))
                            |> bindInlineTypeVariables(rest)
                | _ -> bindInlineTypeVariables(rest)(state)

let recursive appendTypeParameters (left: List((Str, SemanticType))) (right: List((Str, SemanticType))) =
    match left with
        | [] -> right
        | head :: rest -> head :: appendTypeParameters(rest)(right)

let lowerLambdaParameterType annotation parameterType state =
    match annotation with
        | None -> (state, None)
        | Some(typeExpr) ->
            match state |> bindInlineTypeVariables([]
            |> annotationTypeVariableNames(typeExpr)
            |> reverse) with
                | bound ->
                    match bound
                    |> stateInlineTypeVariables
                    |> appendTypeParameters(stateActiveTypeParameters(bound))
                    |> typeExprToSemanticType(expandTypeAliases(stateTypeAliases(bound))(32)(typeExpr)) with
                        | None -> (bound, None)
                        | Some(annotationType) -> bindType(parameterType)(annotationType)(bound)

// Keep a binding's declared parameter types available while its lambda body is lowered.
// Inference already checks the complete signature; ownership placement must see the same
// callback result layout before deciding whether the call returns an owned value.
let recursive annotateBindingLambdaChain (annotation: TypeExpr) (value: Expr) =
    match (annotation, value) with
        | (TypeAt(_span, inner), _) -> annotateBindingLambdaChain(inner)(value)
        | (_, ExprAt(span, inner)) ->
            inner
            |> annotateBindingLambdaChain(annotation)
            |> ExprAt(span)
        | (_, ExprNode(node, inner)) ->
            inner
            |> annotateBindingLambdaChain(annotation)
            |> ExprNode(node)
        | (TypeArrow(argument, result, _capabilities, _tail), ExprLambda(parameter, body, existing)) ->
            ExprLambda(parameter)(annotateBindingLambdaChain(result)(body))(match existing with
                | Some(_explicit) -> existing
                | None -> Some(argument))
        | _ -> value

let bindingLambdaValue annotation value (state: CoreLoweringState) =
    match annotation with
        | None -> value
        | Some(signature) ->
            let expanded =
                expandTypeAliases(stateTypeAliases(state))(32)(signature)
            in
                // Generic signatures need a member-local type-variable scope before their
                // annotations can be shared across a mutually recursive lambda chain.
                match annotationTypeVariableNames(expanded)([]) with
                    | [] -> annotateBindingLambdaChain(expanded)(value)
                    | _ -> value

let recursive annotatedValueIsLambda (value: Expr) =
    match value with
        | ExprAt(_span, inner) -> annotatedValueIsLambda(inner)
        | ExprNode(_node, inner) -> annotatedValueIsLambda(inner)
        | ExprLambda(_parameter, _body, _annotation) -> true
        | _ -> false

// The closed type a `let`'s annotation gives a value that is not a function: stage 0 unifies the
// value's type with its annotation (`let okLeft : Result(Str, Int) = Ok(1)` fixes the error type a
// bare `Ok(1)` leaves open). A function's annotation reaches its parameters through
// `bindingLambdaValue` instead, and an annotation naming type variables is left to inference.
let annotatedValueType (annotation: Maybe(TypeExpr)) (value: Expr) (state: CoreLoweringState) =
    match annotation with
        | None -> None
        | Some(typeExpr) ->
            if annotatedValueIsLambda(value)
            then None
            else
                let expanded =
                    expandTypeAliases(stateTypeAliases(state))(32)(typeExpr)
                in
                    match annotationTypeVariableNames(expanded)([]) with
                        | [] -> typeExprToSemanticType(expanded)([])
                        | _ -> None

// A lowered `let` value bound to the type its annotation gives it.
let bindLoweredAnnotation (annotationType: Maybe(SemanticType)) (lowered: LoweredCoreValue) =
    match (annotationType, lowered) with
        | (Some(expected), LoweredCoreValue { state = state, semanticType = valueType, error = None }) ->
            match bindType(valueType)(expected)(state) with
                | (failedState, Some(error)) -> failure(failedState)(error)
                | (boundState, None) -> lowered with state = boundState
        | _ -> lowered

// A let-bound lambda's generated label is remembered under the let's name, so a call through the
// name can consult the function's recorded body placement.
let recordLetLambdaLabel (label: Str) (state: CoreLoweringState) =
    match statePendingSourceFunction(state) with
        | Some(SourceFunctionOrigin { functionSourceName = name }) ->
            withStateLetLambdaLabels(state
            |> stateLetLambdaLabels
            |> Ashes.Collection.Map.setStr(name)(label))(state)
        | None -> state
