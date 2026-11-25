// Consumed argument release, deferred copy-out, call windows, the ownership of a call result, if
// plans and the normalization of join arms. A part of `AshesCompiler.Semantics.CoreLowering`.

import Ashes.Collection.List.append
import Ashes.Collection.List.length
import Ashes.Collection.List.reverse
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.Pattern
import AshesCompiler.Frontend.Token.TextSpan
import AshesCompiler.Semantics.CallOwnership
import AshesCompiler.Semantics.ConstructionRetains
import AshesCompiler.Semantics.IrControlFlowGraph.containsInt
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.MatchArmOwnership
import AshesCompiler.Semantics.ResultReachSummaries.ResultReachState
import AshesCompiler.Semantics.ResultReachSummaries.ReachSummary
import AshesCompiler.Semantics.ResultReachSummaries.ReachFunction
import AshesCompiler.Semantics.ResultReachSummaries.recordReachOf
import AshesCompiler.Semantics.OwnershipSummary
import AshesCompiler.Semantics.TcoRuntimeManagedParams
import AshesCompiler.Semantics.TypeSchemes
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.recordedClosedTypeAt
import AshesCompiler.Semantics.NodeIdentity.nodeIdentityOf
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.CoreLowering.LoweringState
import AshesCompiler.Semantics.CoreLowering.Emit
import AshesCompiler.Semantics.CoreLowering.Owners
import AshesCompiler.Semantics.CoreLowering.CopyPlans
import AshesCompiler.Semantics.CoreLowering.TcoReset
import AshesCompiler.Semantics.CoreLowering.ResultHandOver
import AshesCompiler.Semantics.CoreLowering.Bindings
import AshesCompiler.Semantics.CoreLowering.LoopPlacement
import AshesCompiler.Semantics.CoreLowering.CallArguments
export (
    value heapPartTypesInto,
    value resultMayHoldAnyPart,
    value closureOfCallResult,
    value settleLetScopedTakenOver,
    value settleMatchScopedTakenOver,
    value isTailSelfCall,
    value tailCallPosition,
    value lowerCall,
    value prepareIfPlan,
    value tempIsNewlyProduced,
    value matchArmResultOf,
    value unknownArmResult,
    value transferBranchOwnerResult,
    value transferIfBranchResult,
    value transferBranchLetOwnerResult,
    value joinArmStoreIndex,
    value shiftPatternOwnerSites,
    value normalizeMixedJoinArmsAs,
    value markControlFlowJoin,
)

// Stage 0's `EmitRuntimeManagedChildPreservingDrop`: only the references the caller still owns
// are given up, a list's spine or an aggregate's own cell, because the callee's arena-placed
// result may still reference the argument's parts.
let emitChildPreservingDrop (valueTemp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemList(_element) -> emitRuntimeListSpineDrop(valueTemp)(state)
        | SemTuple(_elements) -> emitRuntimeShallowAggregateDrop(valueTemp)("Tuple")(state)
        | SemNamed(_symbolId, name, _arguments) ->
            emitRuntimeShallowAggregateDrop(valueTemp)(runtimeManagedAdtTypeName(name))(state)
        | other -> emitRuntimeChildDrop(valueTemp)(other)(state)

// Stage 0's `LowerCallDropConsumedRuntimeArguments` for one argument: a scalar needs nothing, a
// closure is closed and released, an argument whose parts the callee's arena-placed result may
// still reference gives up only the references the caller owns, and any other is released with
// its owned children.
// Stage 0's `EmitConsumedArgumentDropByResultBranch`: the consumed argument's release chosen by
// the call's result branch. The owned branch (the callee returned a reference-counted result
// that may hold the argument's parts) keeps the child-preserving release; the copied branch
// (the arena result was copied out with its heads) releases the argument with its parts.
let emitConsumedArgumentDropByResultBranch (temp: Int) (valueType: SemanticType) (resultFlagTemp: Int) (state: CoreLoweringState) =
    match freshLabel("rc_consumed_copied")(state) with
        | FreshLabel { state = copiedState, label = copiedLabel } ->
            match freshLabel("rc_consumed_done")(copiedState) with
                | FreshLabel { state = doneState, label = doneLabel } ->
                    doneState
                    |> emit(JumpIfFalse(resultFlagTemp)(copiedLabel))
                    |> emitChildPreservingDrop(temp)(valueType)
                    |> emit(Jump(doneLabel))
                    |> emit(Label(copiedLabel))
                    |> emitRuntimeChildDrop(temp)(valueType)
                    |> emit(Label(doneLabel))

// Stage 0's `EmitHandedOverArgumentRelease`: releases a reference handed to the callee for its result to
// keep, now that the caller has copied the result outright and the copy holds none of it. An adopting
// callee (the adoption bit reads true) consumed the reference itself, and on the owned branch of a
// conditional copy-out (the result bit reads true) the callee's reference-counted result may still hold
// it, so both leave the reference where it is.
// The guard of a handed-over release against a result that is the argument itself.
let emitNotResultGuard (temp: Int) (resultTemp: Int) (doneLabel: Str) (state: CoreLoweringState) =
    if resultTemp < 0
    then state
    else
        match freshLabel("rc_handed_over_not_result")(state) with
            | FreshLabel { state = labelled, label = releaseLabel } ->
                match freshTemp(labelled) with
                    | FreshTemp { state = allocated, temp = isResultTemp } ->
                        allocated
                        |> emit(CmpIntEq(isResultTemp)(resultTemp)(temp))
                        |> emit(JumpIfFalse(isResultTemp)(releaseLabel))
                        |> emit(Jump(doneLabel))
                        |> emit(Label(releaseLabel))

// `resultTemp`, when not -1, is a result owned on every path that can hold the argument only as
// itself: a callee that did not adopt the reference and did not return it left it with the caller.
let emitHandedOverArgumentRelease (temp: Int) (valueType: SemanticType) (adoptionFlagTemp: Int) (resultCopyFlagTemp: Int) (resultTemp: Int) (state: CoreLoweringState) =
    match freshLabel("rc_handed_over_not_adopted")(state) with
        | FreshLabel { state = notAdoptedState, label = notAdoptedLabel } ->
            match freshLabel("rc_handed_over_done")(notAdoptedState) with
                | FreshLabel { state = doneState, label = doneLabel } ->
                    match doneState
                    |> emit(JumpIfFalse(adoptionFlagTemp)(notAdoptedLabel))
                    |> emit(Jump(doneLabel))
                    |> emit(Label(notAdoptedLabel))
                    |> emitNotResultGuard(temp)(resultTemp)(doneLabel) with
                        | adoptionTested ->
                            match if resultCopyFlagTemp >= 0
                            then
                                match freshLabel("rc_handed_over_copied")(adoptionTested) with
                                    | FreshLabel { state = copiedState, label = copiedLabel } ->
                                        copiedState
                                        |> emit(JumpIfFalse(resultCopyFlagTemp)(copiedLabel))
                                        |> emit(Jump(doneLabel))
                                        |> emit(Label(copiedLabel))
                            else adoptionTested with
                                | resultTested ->
                                    resultTested
                                    |> emitRuntimeChildDrop(temp)(valueType)
                                    |> emit(Label(doneLabel))

// Stage 0's `HandedOverArgumentResultFlag`: the result-ownership bit that decides whether a handed-over
// reference stays with the callee's reference-counted result, or -1 when a result of this type cannot
// hold a value of the argument's type at all — a value is only ever reachable as its own type, so such a
// result kept nothing and the callee's adoption is the whole question. Without this, the bit alone keeps
// every reference a reference-counted result could conceivably hold, which never releases the argument
// at all for a producer whose result shares none of it.
// A reference-counted cell owns every part it points at, so only a result that may hold a value of
// any type (a closure, or a still-unresolved type) keeps the reference without one of its own.
let handedOverArgumentResultFlag (resultType: SemanticType) (argumentType: SemanticType) (resultCopyFlagTemp: Int) (state: CoreLoweringState) =
    if callResultMayContainArgumentType(resultType)(argumentType)([])(state) && callResultMayContainArgumentType(resultType)(SemNever)([])(state)
    then resultCopyFlagTemp
    else -1

// What the release of a call's consumed arguments knows of the call: whether the callee's
// lowered body produced a reference-counted result, the flag of the result's conditional list
// copy-out when that copy-out copies the heads on its arena branch (-1 otherwise), whether the
// closed window copied the result out at all and whether it deep-copied it, and whether the
// callee's result reach is unknown.
type CoreConsumedReleasePolicy =
    | verifiedRuntimeResult: Bool
    | elementCopyingFlagTemp: Int
    | resultNormalized: Bool
    | resultDeepCopied: Bool
    | calleeResultPoisoned: Bool
    // Stage 0's `ResolveHandedOverReleaseGuard`: whether copying the result out severs every reference it
    // held to the arguments, which is where a reference handed over for the result to keep is released,
    // and the result-ownership bit selecting the copy branch (-1 when the copy is unconditional or the
    // result cannot hold the argument's type at all). The result type narrows that bit per argument.
    | resultCopySevers: Bool
    | resultCopyFlagTemp: Int
    | resultType: SemanticType
    // A result normalized into an owned value on both of its branches, which holds a handed-over
    // argument only as itself; -1 otherwise.
    | normalizedOwnedResultTemp: Int
    // Stage 0's `resultDiscarded`: nothing reads the call's result.
    | resultDiscarded: Bool

// Stage 0's `ConsumedDeepCopiedListStaysWithCallee`: a consumed list that an earlier call's
// generic deep copy produced is left with a callee whose result was neither copied out here
// nor produced reference-counted and whose result reach is unknown, since that result may keep
// the list whole or any of its parts.
let consumedDeepCopiedListStaysWithCallee (temp: Int) (policy: CoreConsumedReleasePolicy) (state: CoreLoweringState) =
    policy.calleeResultPoisoned && policy.resultNormalized == false && policy.verifiedRuntimeResult == false && containsInt(temp)(stateGenericDeepCopiedListTemps(state))

// Stage 0's `LowerCallDropConsumedRuntimeArguments` for one argument: a scalar needs nothing, a
// closure is closed and released, an argument whose parts the callee's arena-placed result may
// still reference gives up only the references the caller owns (or, when the result's
// conditional list copy-out copies the heads on its arena branch, `elementCopyingFlagTemp` is
// that branch's flag and the release follows it), and any other is released with its owned
// children; a deep-copied result shares nothing with the arguments, so their parts go with them.
let emitConsumedValueDrop (policy: CoreConsumedReleasePolicy) (temp: Int) (valueType: SemanticType) (preserve: Bool) (state: CoreLoweringState) =
    if preserve && policy.verifiedRuntimeResult == false && policy.resultDeepCopied == false
    then
        if policy.elementCopyingFlagTemp >= 0
        then emitConsumedArgumentDropByResultBranch(temp)(valueType)(policy.elementCopyingFlagTemp)(state)
        else emitChildPreservingDrop(temp)(valueType)(state)
    else emitRuntimeChildDrop(temp)(valueType)(state)

// Stage 0's `HeapPartTypes`: the heap types reachable inside a value of a type, itself included.
// A named type's constructor fields are expanded once per type name; its type arguments are walked
// every time.
let recursive heapPartTypesInto (pending: List(SemanticType)) (expanded: List(Str)) (parts: List(SemanticType)) (state: CoreLoweringState) =
    match pending with
        | [] -> parts
        | next :: rest ->
            match resolveType(state)(next) with
                | resolved ->
                    if resultSurvivesReset(resolved)(state)
                    then heapPartTypesInto(rest)(expanded)(parts)(state)
                    else
                        match resolved with
                            | SemNamed(_id, name, arguments) ->
                                if containsName(name)(expanded)
                                then
                                    heapPartTypesInto(append(arguments)(rest))(expanded)(resolved :: parts)(state)
                                else
                                    match namedTypeDeclaredFields(name)(state) with
                                        | (_found, fieldTypes) ->
                                            heapPartTypesInto(rest
                                            |> append(arguments)
                                            |> append(fieldTypes))(name :: expanded)(resolved :: parts)(state)
                            | other ->
                                heapPartTypesInto(append(structuralComponentTypes(other))(rest))(expanded)(other :: parts)(state)

let resultMayHoldAnyPart (resultType: SemanticType) (parts: List(SemanticType)) (state: CoreLoweringState) = callResultMayContainAnyArgumentType(resultType)(parts)(state)

// Stage 0's `KeepHandedOverArgumentInOwnedSlot`: the owned slot a handed-over reference waits in
// until the function's result or the loop's successors hold references of their own; a callee that
// adopted the reference consumed it, so the slot is left empty.
let keepHandedOverArgumentInOwnedSlot (handedOverParts: Bool) (temp: Int) (valueType: SemanticType) (adoptionFlagTemp: Int) (state: CoreLoweringState) =
    match freshLocal(state) with
        | FreshLocal { state = slotted, local = ownedSlot } ->
            match slotted
            |> emit(StoreLocal(ownedSlot)(temp))
            |> freshLabel("rc_handed_over_kept") with
                | FreshLabel { state = labelled, label = keptLabel } ->
                    match labelled
                    |> emit(JumpIfFalse(adoptionFlagTemp)(keptLabel))
                    |> freshTemp with
                        | FreshTemp { state = allocated, temp = zeroTemp } ->
                            allocated
                            |> emit(LoadConstInt(zeroTemp)(0))
                            |> emit(StoreLocal(ownedSlot)(zeroTemp))
                            |> emit(Label(keptLabel))
                            |> registerGeneralRcOwnedSlot(ownedSlot)(valueType)
                            |> (given (registered: CoreLoweringState) ->
                                if handedOverParts
                                then withStateGeneralRcHandedOverPartsSlots((stateActiveFunctionOrigin(registered), ownedSlot) :: stateGeneralRcHandedOverPartsSlots(registered))(registered)
                                else registered)

// Stage 0's `LowerCallDropHandedOverArgumentResultCannotHold`: a reference handed over for the
// callee's result to keep is not in a result whose type has no place for a value of the
// argument's type, so a callee that did not adopt it left it with the caller, which releases it
// here. Where a part of the argument could be in an arena result the caller does not copy at the
// call, the argument goes to an owned slot and is released with its parts once the function's
// result or the loop's successors hold references of their own, since releasing only its spine
// here would leave one reference on every part the result did not keep.
let emitHandedOverArgumentResultCannotHold (policy: CoreConsumedReleasePolicy) (temp: Int) (valueType: SemanticType) (adoptionFlagTemp: Int) (state: CoreLoweringState) =
    match valueType with
        | SemFunction(_parameter, _result, _row) -> state
        | _ ->
            if resultSurvivesReset(valueType)(state) || containsUnresolvedLayout(valueType)(state) || containsUnresolvedLayout(policy.resultType)(state) || callResultMayContainArgumentType(policy.resultType)(valueType)([])(state)
            then state
            else
                let mayHoldParts =
                    resultMayHoldAnyPart(policy.resultType)(heapPartTypesInto([valueType])([])([])(state))(state)
                in
                    if mayHoldParts && generalRcEnabled(state) && policy.verifiedRuntimeResult == false && policy.resultDeepCopied == false
                    then keepHandedOverArgumentInOwnedSlot(true)(temp)(valueType)(adoptionFlagTemp)(state)
                    else
                        let unconditional = policy with elementCopyingFlagTemp = -1
                        in
                            match freshLabel("rc_handed_over_adopted")(state) with
                                | FreshLabel { state = adoptedLabelled, label = adoptedLabel } ->
                                    match freshLabel("rc_handed_over_release")(adoptedLabelled) with
                                        | FreshLabel { state = releaseLabelled, label = releaseLabel } ->
                                            releaseLabelled
                                            |> emit(JumpIfFalse(adoptionFlagTemp)(releaseLabel))
                                            |> emit(Jump(adoptedLabel))
                                            |> emit(Label(releaseLabel))
                                            |> emitConsumedValueDrop(unconditional)(temp)(valueType)(mayHoldParts)
                                            |> emit(Label(adoptedLabel))

// Stage 0's `HandOverArgumentKeptByArenaResultToOwnedSlot`: a reference handed over for the
// callee's result to keep, where the result is an arena value that may hold the argument and that
// the caller does not copy at the call, goes to an owned slot the function releases once its
// result or the loop's successors hold references of their own; a callee that adopted the
// reference consumed it, so the slot is left empty. A result of unresolved layout (a closure
// parameter applied in a generic body) may hold the argument as anything, so the slot is released
// only where the function's own result is out of that call's reach.
let handOverArgumentKeptByArenaResult (policy: CoreConsumedReleasePolicy) (temp: Int) (valueType: SemanticType) (adoptionFlagTemp: Int) (state: CoreLoweringState) =
    match valueType with
        | SemFunction(_parameter, _result, _row) -> state
        | _ ->
            if generalRcEnabled(state) == false || policy.verifiedRuntimeResult || containsUnresolvedLayout(valueType)(state) || callResultMayContainArgumentType(policy.resultType)(valueType)([])(state) == false
            then state
            else keepHandedOverArgumentInOwnedSlot(false)(temp)(valueType)(adoptionFlagTemp)(state)

let handedOverValueIsClosure (valueType: SemanticType) =
    match valueType with
        | SemFunction(_parameter, _result, _row) -> true
        | _ -> false

let emitConsumedArgumentDrop (policy: CoreConsumedReleasePolicy) (consumed: CoreConsumedArgument) (state: CoreLoweringState) =
    match consumed with
        | CoreConsumedArgument { temp = temp, semanticType = semanticType, adoptionFlagTemp = adoptionFlagTemp } when adoptionFlagTemp >= 0 ->
            match resolveType(state)(semanticType) with
                | valueType ->
                    if policy.resultDiscarded && containsUnresolvedLayout(policy.resultType)(state) && handedOverValueIsClosure(valueType) == false
                    then emitHandedOverArgumentRelease(temp)(valueType)(adoptionFlagTemp)(-1)(-1)(state)
                    else
                        if policy.resultCopySevers
                        then
                            emitHandedOverArgumentRelease(temp)(valueType)(adoptionFlagTemp)(
                                handedOverArgumentResultFlag(policy.resultType)(valueType)(policy.resultCopyFlagTemp)(state)
                            )(policy.normalizedOwnedResultTemp)(state)
                        else
                            state
                            |> emitHandedOverArgumentResultCannotHold(policy)(temp)(valueType)(adoptionFlagTemp)
                            |> handOverArgumentKeptByArenaResult(policy)(temp)(valueType)(adoptionFlagTemp)
        | CoreConsumedArgument { temp = temp, semanticType = semanticType, preserveEscapedChildren = preserve } ->
            match resolveType(state)(semanticType) with
                | SemFunction(_parameter, _result, _row) ->
                    state
                    |> emit(CleanupResource(temp)("Function")(None))
                    |> emit(RcDrop(temp)("Function")(-1)(true)(false)(None))
                | valueType ->
                    if resultSurvivesReset(valueType)(state) || consumedDeepCopiedListStaysWithCallee(temp)(policy)(state)
                    then state
                    else emitConsumedValueDrop(policy)(temp)(valueType)(preserve)(state)

// Each consumed temp is released once.
let recursive emitConsumedArgumentDrops (policy: CoreConsumedReleasePolicy) (dropped: List(Int)) (consumed: List(CoreConsumedArgument)) (state: CoreLoweringState) =
    match consumed with
        | [] -> state
        | (CoreConsumedArgument { temp = temp } as argument) :: rest ->
            if containsInt(temp)(dropped)
            then emitConsumedArgumentDrops(policy)(dropped)(rest)(state)
            else
                state
                |> emitConsumedArgumentDrop(policy)(argument)
                |> emitConsumedArgumentDrops(policy)(temp :: dropped)(rest)

// Stage 0's `GetOwnershipSummaryForCallRoot(...) is not { ResultPoisoned: false }`: a callee
// without facts has an unknown result reach.
let calleeResultPoisoned (context: CoreCallContext) =
    match context.facts with
        | Some(CoreCalleeFacts { reach = ResultReachState { isPoisoned = isPoisoned } }) -> isPoisoned
        | None -> true

// Stage 0's `IsSpecializationSelfReuseCall`: a reuse specialization's call to itself rebuilds the
// accumulator in place, so its result stays where it is rather than being copied past the reset.
let specializationSelfReuseCall (context: CoreCallContext) (state: CoreLoweringState) =
    context.selfCallee && (match stateSpecializingReuseLabel(state) with
        | Some(_label) -> true
        | None -> false)

// Stage 0's `LowerReuseSpecializedCall`: a call routed to a reuse specialization from outside it
// applies the specialization's closure with no window of its own, and a result of the contract's
// types records no ownership (`RecordReuseSpecializationResultOwnership` returns early for one):
// the result is the accumulator rewritten in place or cells built in the persistent to-space.
let routedSpecializationCall (context: CoreCallContext) (state: CoreLoweringState) =
    !context.selfCallee && (match context.calleeName with
        | Some(name) ->
            state
            |> stateRoutedSpecializationCallees
            |> containsName(name)
        | None -> false)

// Stage 0's `ResolveHandedOverReleaseGuard`. A result a reset reclaims holds no reference to any
// argument, so copying it out severs everything. A callee whose compiled body is verified to return a
// reference-counted result owns every part of it — its construction paths copied or retained whatever
// they kept — so such a result strands the handed-over reference exactly as a copied one does, and needs
// no runtime bit to say so. Otherwise the copy branch of a shallow or list copy-out is what severs the
// references, and the result-ownership bit selects that branch at runtime. A specialization's self reuse
// call keeps its result in place, so nothing is severed there.
let handedOverReleaseGuard (context: CoreCallContext) (resultType: SemanticType) (resultFlagTemp: Int) (state: CoreLoweringState) =
    (let stableReuse = specializationSelfReuseCall(context)(state)
    in
        if resultSurvivesReset(resolveType(state)(resultType))(state)
        then (true, -1)
        else
            if contextResultRuntimeManaged(context)(resultType)(state)
            then (!stableReuse && calleeVerifiedResultRuntimeManaged(context)(state), -1)
            else (!stableReuse && hasCallCopyOut(resultType)(state), resultFlagTemp))

// The generic list deep copy rebuilds every element and its owned parts on the arena branch of
// the result flag, so the copied result shares nothing with a handed-over argument either: the
// reference handed to the callee is released where the copy ran.
// A result of the contract's types is owned by the caller on every path, so the release is not
// guarded by the result-ownership bit.
let deepCopiedResultSevers (stage: CoreCallStage) (guard: (Bool, Int)) =
    match guard with
        | (false, _flagTemp) ->
            if stage.resultDeepCopied
            then
                (true, if stage.resultGeneralRc
                then -1
                else stage.resultFlagTemp)
            else guard
        | _ -> guard

// Stage 0's `NormalizedOwnedResultSevers`: a result normalized into an owned value on both of its
// branches holds a handed-over argument only as itself, so it severs every other reference, and
// the release is guarded by the result.
let normalizedOwnedResultSevers (stage: CoreCallStage) (guard: (Bool, Int)) =
    if stage.resultNormalizedOwned
    then (true, -1)
    else guard

let normalizedOwnedResultTempOf (stage: CoreCallStage) =
    match stage with
        | CoreCallStage { resultNormalizedOwned = true, lowered = LoweredCoreValue { temp = temp } } -> temp
        | _ -> -1

let parkedFunctionType (semanticType: SemanticType) =
    match semanticType with
        | SemFunction(_parameter, _result, _row) -> true
        | _ -> false

let recursive parkedTemp (temp: Int) (parked: List(Int)) =
    match parked with
        | [] -> false
        | candidate :: rest -> candidate == temp || parkedTemp(temp)(rest)

// Stage 0's `ParkArgumentsHandedToCurryStage`: a curry stage's result is a closure whose
// environment keeps the argument, and a stage that does not adopt a reference stores the pointer
// in an arena environment, so a reference handed over for the result to keep waits in an owned
// slot (left empty when the stage adopted it), released once the function's result or the loop's
// successors hold references of their own.
let recursive parkArgumentsHandedToCurryStage (consumed: List(CoreConsumedArgument)) (parked: List(Int)) (state: CoreLoweringState) =
    match consumed with
        | [] -> state
        | CoreConsumedArgument { temp = temp, semanticType = semanticType, adoptionFlagTemp = adoptionFlagTemp } :: rest ->
            let valueType = resolveType(state)(semanticType)
            in
                if adoptionFlagTemp < 0 || resultSurvivesReset(valueType)(state) || parkedFunctionType(valueType) || containsUnresolvedLayout(valueType)(state) || parkedTemp(temp)(parked)
                then parkArgumentsHandedToCurryStage(rest)(parked)(state)
                else
                    state
                    |> keepHandedOverArgumentInOwnedSlot(false)(temp)(valueType)(adoptionFlagTemp)
                    |> parkArgumentsHandedToCurryStage(rest)(temp :: parked)

// A partial application keeps its fresh arguments alive in the returned closure; a reference
// handed over for it to keep is parked.
let releaseConsumedArguments (context: CoreCallContext) (resultType: SemanticType) (elementCopyingFlagTemp: Int) (stage: CoreCallStage) (state: CoreLoweringState) =
    match resolveType(state)(resultType) with
        | SemFunction(_parameter, _result, _row) ->
            if generalRcEnabled(state)
            then parkArgumentsHandedToCurryStage(stage.consumedArguments)([])(state)
            else state
        | _ ->
            match state
            |> handedOverReleaseGuard(context)(resultType)(stage.resultFlagTemp)
            |> deepCopiedResultSevers(stage)
            |> normalizedOwnedResultSevers(stage) with
                | (resultCopySevers, resultCopyFlagTemp) ->
                    emitConsumedArgumentDrops(CoreConsumedReleasePolicy(
                        verifiedRuntimeResult = calleeVerifiedResultRuntimeManaged(context)(state),
                        elementCopyingFlagTemp = elementCopyingFlagTemp,
                        resultNormalized = stage.resultNormalized,
                        resultDeepCopied = stage.resultDeepCopied,
                        calleeResultPoisoned = calleeResultPoisoned(context),
                        resultCopySevers = resultCopySevers,
                        resultCopyFlagTemp = resultCopyFlagTemp,
                        resultType = resultType,
                        normalizedOwnedResultTemp = normalizedOwnedResultTempOf(stage),
                        resultDiscarded = context.resultDiscarded
                    ))([])(stage.consumedArguments)(state)

let markCallResultOwnership (context: CoreCallContext) (temp: Int) (resultType: SemanticType) (state: CoreLoweringState) =
    if contextResultRuntimeManaged(context)(resultType)(state)
    then markRuntimeTemp(temp)(RuntimeNewlyProduced)(state)
    else state

// After the last application, stage 0's `LowerCallGeneral` tail: a result the callee is known
// to place on the reference-counted heap is marked newly produced. A call that asked for an arena
// result receives the callee's arena copy instead, so its result is not, unless it is of the
// contract's types, whose request is withdrawn when the result is taken, or one with no call copy-out,
// whose window stage 0 closes without one.
let markCallSpineResult (context: CoreCallContext) (stage: CoreCallStage) =
    match stage with
        | CoreCallStage { lowered = LoweredCoreValue { error = Some(_error) } } -> stage
        | CoreCallStage { lowered = LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } as lowered } ->
            if callRequestsArenaResult(temp)(state.reversedInstructions) && !isGeneralRcValueType(semanticType)(state) && hasCallCopyOut(semanticType)(state)
            then stage
            else stage with lowered = (lowered with state = markCallResultOwnership(context)(temp)(semanticType)(state))

// Stage 0's `LowerCallFinish` tail: the fresh arguments the callee did not take are released
// only once the call's window is closed and its result copied out of it, since an arena-placed
// result can still reference the arguments' parts until the copy-out has copied them.
let releaseCallSpineArguments (context: CoreCallContext) (elementCopyingFlagTemp: Int) (stage: CoreCallStage) =
    match stage with
        | CoreCallStage { lowered = LoweredCoreValue { error = Some(_error) } as closed } -> closed
        | CoreCallStage { lowered = LoweredCoreValue { state = state, semanticType = semanticType, error = None } as closed } -> closed with state = releaseConsumedArguments(context)(semanticType)(elementCopyingFlagTemp)(stage)(state)

// Stage 0's `ResultCopyBranchFlagTemp`: the call's result-ownership flag when its result takes
// a conditional copy-out that copies the whole result on its arena branch, a list copy-out that
// copies the heads or a shallow copy (a string, or a cell of scalar fields), so the consumed
// arguments' release can follow the same branch; -1 otherwise.
let elementCopyingFlagOf (context: CoreCallContext) (stage: CoreCallStage) =
    match stage with
        | CoreCallStage { lowered = LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None }, resultFlagTemp = flagTemp } ->
            if flagTemp >= 0 && isRuntimeTemp(temp)(state) == false && resultSurvivesReset(semanticType)(state) == false && specializationSelfReuseCall(context)(state) == false
            then
                match callCopyOutOf(semanticType)(state) with
                    | Some(ListCallCopyOut(InlineListHead)) -> -1
                    | Some(ListCallCopyOut(_headCopy)) -> flagTemp
                    | Some(ShallowCallCopyOut(_sizeBytes)) -> flagTemp
                    | _ -> -1
            else -1
        | _ -> -1

// Stage 0's `EmitPostGuardedArenaRestore`: a call window's rewind is skipped while a one-shot post
// is pending, since a perform inside the window pushed its post cell and closure into the window's
// allocations, which must survive until the handle folds them. The paired reclaim is guarded the
// same way; the result normalization between the two runs either way, since it copies into
// reference-counted storage.
let emitPostGuardedArenaRestore cursorSlot endSlot preRestoreSlot (state: CoreLoweringState) =
    match beginLivePostsGuard(state) with
        | (guarded, skipLabel) ->
            guarded
            |> emit(RestoreArenaState(cursorSlot)(endSlot)(preRestoreSlot)(false))
            |> endLivePostsGuard(skipLabel)

let emitPostGuardedArenaReclaim endSlot preRestoreSlot (state: CoreLoweringState) =
    match beginLivePostsGuard(state) with
        | (guarded, skipLabel) ->
            guarded
            |> emit(ReclaimArenaChunks(endSlot)(preRestoreSlot)(false))
            |> endLivePostsGuard(skipLabel)

// Stage 0's `LowerCallConditionalCopyOutResult`: the result is spilled to a slot, the arena is
// restored, and the callee's returns bit selects between reclaiming the window as is (the
// result already lives on the reference-counted heap) and first copying the result past the
// reset; the slot's value is reloaded as the call's reference-counted result.
let emitConditionalCallCopyOut copyOut resultTemp flagTemp cursorSlot endSlot preRestoreSlot state =
    match freshLocal(state) with
        | FreshLocal { state = allocated, local = resultSlot } ->
            match allocated
            |> emit(StoreLocal(resultSlot)(resultTemp))
            |> emitPostGuardedArenaRestore(cursorSlot)(endSlot)(preRestoreSlot)
            |> freshLabel("call_copy_arena_result") with
                | FreshLabel { state = copyLabelled, label = copyLabel } ->
                    match freshLabel("call_reclaim_owned_result")(copyLabelled) with
                        | FreshLabel { state = labelled, label = reclaimLabel } ->
                            match labelled
                            |> emit(JumpIfFalse(flagTemp)(copyLabel))
                            |> emit(Jump(reclaimLabel))
                            |> emit(Label(copyLabel))
                            |> freshTemp with
                                | FreshTemp { state = copyState, temp = copiedTemp } ->
                                    copyState
                                    |> emit(callCopyOutInstruction(copyOut)(copiedTemp)(resultTemp))
                                    |> emit(StoreLocal(resultSlot)(copiedTemp))
                                    |> emit(Label(reclaimLabel))
                                    |> emitPostGuardedArenaReclaim(endSlot)(preRestoreSlot)
                                    |> reloadRuntimeSlot(resultSlot)

// Stage 0's `LowerCallConditionalNormalizeResult`: a record result with no fixed copy-out from a
// callee whose result ownership is settled only at run time. The result is spilled to a slot, the
// arena restored, and the callee's returns bit decides: a reference-counted result is taken over
// as it is, an arena one is copied into an owned reference-counted graph, so the result is owned
// on both paths and no consumer retains a reference the callee already gave up.
let emitConditionalNormalizeResult (plan: ArgumentCopyPlan) (resultTemp: Int) (flagTemp: Int) cursorSlot endSlot preRestoreSlot (state: CoreLoweringState) =
    match freshLocal(state) with
        | FreshLocal { state = allocated, local = resultSlot } ->
            match allocated
            |> emit(StoreLocal(resultSlot)(resultTemp))
            |> emitPostGuardedArenaRestore(cursorSlot)(endSlot)(preRestoreSlot)
            |> freshLabel("call_normalize_arena_result") with
                | FreshLabel { state = copyLabelled, label = copyLabel } ->
                    match freshLabel("call_owned_result")(copyLabelled) with
                        | FreshLabel { state = labelled, label = ownedLabel } ->
                            match labelled
                            |> emit(JumpIfFalse(flagTemp)(copyLabel))
                            |> emit(Jump(ownedLabel))
                            |> emit(Label(copyLabel))
                            |> emitArgumentCopyByCopy(resultTemp)(plan) with
                                | (copied, copiedTemp) ->
                                    copied
                                    |> emit(StoreLocal(resultSlot)(copiedTemp))
                                    |> emit(Label(ownedLabel))
                                    |> emitPostGuardedArenaReclaim(endSlot)(preRestoreSlot)
                                    |> reloadRuntimeSlot(resultSlot)

// Stage 0's `LowerCallDeepCopyOutListResult`: the generic list result is walked out of the call
// window by the per-element deep copy the loop entry normalization uses, unconditionally when
// the call read no returns flag (the result may be arena-built), else only on the flag's arena
// branch, where a list the runtime answers is already reference-counted is taken by reference and
// the copy shares the first reference-counted suffix, the slot's value reloaded as the result either way.
let emitCallDeepCopyOut (elementPlan: ArgumentCopyPlan) (resultTemp: Int) (flagTemp: Int) cursorSlot endSlot preRestoreSlot (state: CoreLoweringState) =
    if flagTemp < 0
    then
        match state
        |> emitPostGuardedArenaRestore(cursorSlot)(endSlot)(preRestoreSlot)
        |> emitListDeepCopy(resultTemp)(elementPlan) with
            | (copied, copiedTemp) -> (emitPostGuardedArenaReclaim(endSlot)(preRestoreSlot)(copied), copiedTemp)
    else
        match freshLocal(state) with
            | FreshLocal { state = allocated, local = resultSlot } ->
                match allocated
                |> emit(StoreLocal(resultSlot)(resultTemp))
                |> emitPostGuardedArenaRestore(cursorSlot)(endSlot)(preRestoreSlot)
                |> freshLabel("call_deep_copy_arena_result") with
                    | FreshLabel { state = copyLabelled, label = copyLabel } ->
                        match freshLabel("call_reclaim_owned_result")(copyLabelled) with
                            | FreshLabel { state = labelled, label = reclaimLabel } ->
                                match labelled
                                |> emit(JumpIfFalse(flagTemp)(copyLabel))
                                |> emit(Jump(reclaimLabel))
                                |> emit(Label(copyLabel))
                                |> emitGuardedListDeepCopy(resultTemp)(elementPlan) with
                                    | (copied, copiedTemp) ->
                                        match copied
                                        |> emit(StoreLocal(resultSlot)(copiedTemp))
                                        |> emit(Label(reclaimLabel))
                                        |> emitPostGuardedArenaReclaim(endSlot)(preRestoreSlot)
                                        |> freshTemp with
                                            | FreshTemp { state = reloadState, temp = reloadTemp } ->
                                                (emit(LoadLocal(reloadTemp)(resultSlot))(reloadState), reloadTemp)

// Stage 0's `LowerUncoveredCallResultCopyOut` deep-copy case: a list result with no call
// copy-out, from a callee whose declared result element is one of its own quantified variables,
// whose element does not survive the arena reset but has a deep-copy plan.
let genericListDeepCopyPlanOf (context: CoreCallContext) (readsFlag: Bool) (semanticType: SemanticType) (state: CoreLoweringState) =
    if context.resultElementQuantified || readsFlag
    then
        match resolveType(state)(semanticType) with
            | SemList(element) ->
                if resultSurvivesReset(element)(state)
                then None
                else argumentCopyPlanOf(element)(state)
            | _ -> None
    else None

// Stage 0's `LowerCallDeepCopyOutListResult` with a returns bit: the result is owned on both paths,
// the reference-counted one taken over as it is and the arena one rebuilt with references of its own.
let markFlaggedDeepCopyOwned (flagTemp: Int) (resultTemp: Int) (state: CoreLoweringState) =
    if flagTemp >= 0
    then markRuntimeTemp(resultTemp)(RuntimeNewlyProduced)(state)
    else state

let closedCallStage (stage: CoreCallStage) (normalized: Bool) (deepCopied: Bool) (lowered: LoweredCoreValue) = stage with lowered = lowered, resultNormalized = normalized, resultDeepCopied = deepCopied

// Stage 0's `DeferCallResultCopyOut`: a result whose type still holds an unresolved layout is
// routed through a local slot and read back out of it, and the copy-out block that belongs
// between the store and the reload is emitted by `resolvePendingTcoResets` once the body is
// lowered and the type known; a block that resolves to nothing removes the slot round trip but
// keeps the numbering. The reload temp is what the rest of the body reads.
let deferCallCopyOut (selfCall: Bool) cursorSlot endSlot preRestoreSlot (resultTemp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    if containsUnresolvedLayout(semanticType)(state)
    then
        match freshLocal(state) with
            | FreshLocal { state = allocated, local = resultSlot } ->
                match allocated
                |> emit(StoreLocal(resultSlot)(resultTemp))
                |> freshTemp with
                    | FreshTemp { state = stored, temp = reloadTemp } ->
                        let pending =
                            CorePendingCallCopyOut(
                                deferredCursorSlot = cursorSlot,
                                deferredEndSlot = endSlot,
                                deferredPreRestoreSlot = preRestoreSlot,
                                deferredResultSlot = resultSlot,
                                deferredResultTemp = resultTemp,
                                deferredReloadTemp = reloadTemp,
                                deferredType = semanticType,
                                deferredSelfCall = selfCall,
                                deferredOwned = false,
                                deferredLetReleaseSlots = []
                            )
                        in
                            (stored
                            |> withStatePendingCallCopyOuts(pending :: statePendingCallCopyOuts(stored))
                            |> emit(LoadLocal(reloadTemp)(resultSlot)), reloadTemp)
    else (state, resultTemp)

// Closes a call's arena window after its last application, stage 0's `LowerCallRestoreArena`:
// the pre-restore end slot is allocated first either way; a result that survives the reset or
// is reference-counted resets the window; a heap result with a call copy-out and a returns-bit
// flag crosses the reset through the conditional copy-out; a generic callee's list result with
// no copy-out crosses it through the deep copy and is recorded as deep-copied; any other heap
// result leaves the window open.
// Stage 0 pins a call's result type at the call site to the type its discovery pass settled, so a
// self call's window closes on the resolved type rather than deferring its copy-out. This lowering
// resolves as it goes: a deferred self call's result is bound to the binding's own result type
// here once that type holds no unresolved layout.
let recursive deferredSelfCallOriginal (variable: Int) (pairs: List((SemanticType, SemanticType))) =
    match pairs with
        | [] -> None
        | (SemVariable(candidate), original) :: rest ->
            if candidate == variable
            then Some(original)
            else deferredSelfCallOriginal(variable)(rest)
        | _ :: rest -> deferredSelfCallOriginal(variable)(rest)

let pinDeferredSelfCallResult (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemVariable(variable) ->
            match state
            |> stateSelfCallResultUnifications
            |> deferredSelfCallOriginal(variable) with
                | None -> state
                | Some(original) ->
                    if containsUnresolvedLayout(original)(state)
                    then state
                    else
                        match bindType(semanticType)(original)(state) with
                            | (bound, None) -> bound
                            | (_failed, Some(_error)) -> state
        | _ -> state

// The branch on a closure's owned-result flag ahead of the normalization: set, the result is taken
// over as it is; clear, or with no closure to ask, it is normalized.
let emitOwnedFlagTest (ownedFlagTemp: Int) (ownedLabel: Str) (state: CoreLoweringState) =
    if ownedFlagTemp < 0
    then state
    else
        match freshLabel("general_rc_result_normalize")(state) with
            | FreshLabel { state = labelled, label = normalizeLabel } ->
                labelled
                |> emit(JumpIfFalse(ownedFlagTemp)(normalizeLabel))
                |> emit(Jump(ownedLabel))
                |> emit(Label(normalizeLabel))

// The closure an indirect application called to produce `temp`, looked for among the most recent
// instructions as stage 0's `TryEmitClosureReturnsGeneralRcOwnedFlag` does.
let recursive closureOfCallResult (temp: Int) (budget: Int) (instructions: List(IrInstruction)) =
    if budget == 0
    then None
    else
        match instructions with
            | [] -> None
            | IrInstruction { instruction = CallClosure(target, closureTemp, _argumentTemp, _flagTemp) } :: rest ->
                if target == temp
                then Some(closureTemp)
                else closureOfCallResult(temp)(budget - 1)(rest)
            | _instruction :: rest -> closureOfCallResult(temp)(budget - 1)(rest)

let recursive zeroRequestConstant (request: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> []
        | (IrInstruction { instruction = LoadConstInt(target, _value) } as instruction) :: rest ->
            if target == request
            then (instruction with instruction = LoadConstInt(target)(0)) :: rest
            else instruction :: zeroRequestConstant(request)(rest)
        | instruction :: rest -> instruction :: zeroRequestConstant(request)(rest)

// A withdrawn request is remembered by its request word with the parts it asked for, so a function
// returning the call's result as it is can forward its own caller's request in its place.
let recordWithdrawnTuplePartsRequest (resultTemp: Int) (requestWord: Int) (parts: Maybe(List(Int))) (state: CoreLoweringState) =
    match parts with
        | Some(asked) -> withStateWithdrawnTuplePartsRequests((resultTemp, requestWord, asked) :: stateWithdrawnTuplePartsRequests(state))(state)
        | None -> state

// Stage 0's `TakeTupleResultPartsRequest`: the parts the call producing `resultTemp` asked for and
// the arena word its callee writes the mask of the handed-over parts into, or None; a withdrawn
// request's constant is cleared in the call's ownership word, so the callee keeps its own
// references.
let takeTupleResultPartsRequest (context: CoreCallContext) (resultTemp: Int) (resultType: SemanticType) (withdraw: Bool) (state: CoreLoweringState) =
    match callTuplePartsRequest(resultTemp)(64)(state.reversedInstructions) with
        | None -> (state, None)
        | Some((constant, requestWord, cell)) ->
            if withdraw
            then
                (recordWithdrawnTuplePartsRequest(resultTemp)(requestWord)(calleeTupleResultParts(context)(resultType)(state))((state with reversedInstructions = zeroRequestConstant(constant)(state.reversedInstructions))), None)
            else
                match calleeTupleResultParts(context)(resultType)(state) with
                    | Some(parts) -> (state, Some((parts, cell)))
                    | None -> (state, None)

let recursive loadHandedOverTupleParts (resultTemp: Int) (parts: List(Int)) (state: CoreLoweringState) (temps: List(Int)) =
    match parts with
        | [] -> (state, reverse(temps))
        | part :: rest ->
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = partTemp } ->
                    loadHandedOverTupleParts(resultTemp)(rest)(emit(LoadMemOffset(partTemp)(resultTemp)(part * 8))(allocated))(partTemp :: temps)

// The mask the callee wrote, then the parts of the call's tuple result.
let loadHandedOverMaskAndParts (resultTemp: Int) (parts: List(Int)) (cell: Int) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = allocated, temp = maskTemp } ->
            match loadHandedOverTupleParts(resultTemp)(parts)(emit(LoadMemOffset(maskTemp)(cell)(0))(allocated))([]) with
                | (loaded, partTemps) -> (loaded, maskTemp, partTemps)

let recursive releaseHandedOverTuplePartList (parts: List(Int)) (partTemps: List(Int)) (maskTemp: Int) (elementTypes: List(SemanticType)) (state: CoreLoweringState) =
    match (parts, partTemps) with
        | (part :: restParts, partTemp :: restTemps) ->
            match freshLabel("tuple_part_kept")(state) with
                | FreshLabel { state = labelled, label = skipLabel } ->
                    match freshTempRun(4)(labelled) with
                        | FreshTemp { state = allocated, temp = shiftTemp } ->
                            allocated
                            |> emit(LoadConstInt(shiftTemp)(part))
                            |> emit(ShrInt(shiftTemp + 1)(maskTemp)(shiftTemp))
                            |> emit(LoadConstInt(shiftTemp + 2)(1))
                            |> emit(AndInt(shiftTemp + 3)(shiftTemp + 1)(shiftTemp + 2))
                            |> emit(JumpIfFalse(shiftTemp + 3)(skipLabel))
                            |> emitSkipUnlessReferenceCounted(partTemp)(skipLabel)
                            |> emitOwnedValueChildDrop(partTemp)(nthElementType(part)(elementTypes))
                            |> emit(Label(skipLabel))
                            |> releaseHandedOverTuplePartList(restParts)(restTemps)(maskTemp)(elementTypes)
        | _ -> state

// Stage 0's `ReleaseHandedOverTupleParts`: after a call's tuple result is copied out of the arena,
// the references the callee handed over with the parts its mask names are released, the copy
// holding its own.
let releaseHandedOverTupleParts (semanticType: SemanticType) (parts: List(Int)) (maskTemp: Int) (partTemps: List(Int)) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemTuple(elementTypes) -> releaseHandedOverTuplePartList(parts)(partTemps)(maskTemp)(elementTypes)(state)
        | _ -> state

// Stage 0's `EmitJumpIfWholeResultHandedOver`: after a call that asked for its result's hand-over,
// jumps to the owned path when the callee's mask says it handed the whole result over, which the
// owned slot then holds as it is.
let emitJumpIfWholeResultHandedOver (maskTemp: Int) (ownedLabel: Str) (state: CoreLoweringState) =
    match freshTempRun(4)(state) with
        | FreshTemp { state = allocated, temp = bitTemp } ->
            match freshLabel("whole_result_not_handed_over")(allocated) with
                | FreshLabel { state = labelled, label = copyLabel } ->
                    labelled
                    |> emit(LoadConstInt(bitTemp)(wholeResultHandedOverBit))
                    |> emit(AndInt(bitTemp + 1)(maskTemp)(bitTemp))
                    |> emit(LoadConstInt(bitTemp + 2)(0))
                    |> emit(CmpIntNe(bitTemp + 3)(bitTemp + 1)(bitTemp + 2))
                    |> emit(JumpIfFalse(bitTemp + 3)(copyLabel))
                    |> emit(Jump(ownedLabel))
                    |> emit(Label(copyLabel))

// Stage 0's `EmitJumpIfIndependentCountedResult`: after a call that asked for a record or variant
// result of the contract's types and named no parts, jumps to the owned path when the mask says
// the result is independent and the result is reference-counted. Only a function of a contract
// result type writes that bit, and a forwarder passes the request only to a call whose result it
// returns as it is, so the value is that function's owned result.
let emitJumpIfIndependentCountedResult (maskTemp: Int) (resultTemp: Int) (semanticType: SemanticType) (parts: List(Int)) (ownedLabel: Str) (state: CoreLoweringState) =
    match (parts, resolveType(state)(semanticType), isGeneralRcValueType(semanticType)(state)) with
        | ([], SemNamed(_id, _name, _arguments), true) ->
            match freshTempRun(4)(state) with
                | FreshTemp { state = allocated, temp = bitTemp } ->
                    match freshLabel("independent_result_not_counted")(allocated) with
                        | FreshLabel { state = labelled, label = copyLabel } ->
                            labelled
                            |> emit(LoadConstInt(bitTemp)(independentResultBit))
                            |> emit(AndInt(bitTemp + 1)(maskTemp)(bitTemp))
                            |> emit(LoadConstInt(bitTemp + 2)(0))
                            |> emit(CmpIntNe(bitTemp + 3)(bitTemp + 1)(bitTemp + 2))
                            |> emit(JumpIfFalse(bitTemp + 3)(copyLabel))
                            |> emitSkipUnlessReferenceCounted(resultTemp)(copyLabel)
                            |> emit(Jump(ownedLabel))
                            |> emit(Label(copyLabel))
        | _ -> state

// Stage 0's `ReleaseHandedOverListHeads`: after a call's list result is copied out of the arena,
// when the callee's mask says it handed the heads over, the reference each arena cell's
// reference-counted head holds is released, up to the first reference-counted cell, from which on
// the copy shares the list.
let releaseHandedOverListHeads (semanticType: SemanticType) (maskTemp: Int) (resultTemp: Int) (state: CoreLoweringState) =
    match (isListHeadHandOverType(semanticType)(state), resolveType(state)(semanticType)) with
        | (true, SemList(element)) ->
            match freshLabel("list_heads_kept")(state) with
                | FreshLabel { state = doneState, label = doneLabel } ->
                    match freshTempRun(4)(doneState) with
                        | FreshTemp { state = allocated, temp = bitTemp } ->
                            match allocated
                            |> emit(LoadConstInt(bitTemp)(listHeadsHandedOverBit))
                            |> emit(AndInt(bitTemp + 1)(maskTemp)(bitTemp))
                            |> emit(LoadConstInt(bitTemp + 2)(0))
                            |> emit(CmpIntNe(bitTemp + 3)(bitTemp + 1)(bitTemp + 2))
                            |> emit(JumpIfFalse(bitTemp + 3)(doneLabel))
                            |> freshLocal with
                                | FreshLocal { state = slotted, local = cursorSlot } ->
                                    match slotted
                                    |> emit(StoreLocal(cursorSlot)(resultTemp))
                                    |> freshLabel("list_heads_release") with
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
                                                            |> freshLabel("list_heads_arena_cell") with
                                                                | FreshLabel { state = arenaState, label = arenaLabel } ->
                                                                    match arenaState
                                                                    |> emit(JumpIfFalse(countedTemp)(arenaLabel))
                                                                    |> emit(Jump(doneLabel))
                                                                    |> emit(Label(arenaLabel))
                                                                    |> freshTemp with
                                                                        | FreshTemp { state = headState, temp = headTemp } ->
                                                                            match headState
                                                                            |> emit(LoadMemOffset(headTemp)(cellTemp)(0))
                                                                            |> freshLabel("list_heads_next") with
                                                                                | FreshLabel { state = nextState, label = nextLabel } ->
                                                                                    match nextState
                                                                                    |> emitSkipUnlessReferenceCounted(headTemp)(nextLabel)
                                                                                    |> emitOwnedValueChildDrop(headTemp)(element)
                                                                                    |> emit(Label(nextLabel))
                                                                                    |> freshTemp with
                                                                                        | FreshTemp { state = tailState, temp = tailTemp } ->
                                                                                            tailState
                                                                                            |> emit(LoadMemOffset(tailTemp)(cellTemp)(8))
                                                                                            |> emit(StoreLocal(cursorSlot)(tailTemp))
                                                                                            |> emit(Jump(loopLabel))
                                                                                            |> emit(Label(doneLabel))
        | _ -> state

let recursive spanListed (span: TextSpan) (spans: List(TextSpan)) =
    match spans with
        | [] -> false
        | candidate :: rest -> candidate == span || spanListed(span)(rest)

// Stage 0's `TryNewTakenOverSettle`: what a call result's settlement releases, the list record's tuple
// field when the result is a list of tuples whose first field has a resolved heap type, else the own
// fields of a named result type.
let takenOverSettleKind (resultType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(resultType) with
        | SemList(element) ->
            match resolveType(state)(element) with
                | SemTuple(first :: _rest) ->
                    if containsUnresolvedLayout(first)(state) || resultSurvivesReset(first)(state)
                    then None
                    else Some(ListRecordKind(first))
                | _ -> None
        | _ -> ownFieldKindOf(resultType)(state)

// The callee a settlement names: a function the call resolved by name.
let takenOverSettleCallee (context: CoreCallContext) =
    match (context.facts, context.calleeName) with
        | (Some(_facts), Some(name)) -> Some(name)
        | _ -> None

let recursive callArgumentsAt (span: TextSpan) (facts: List((TextSpan, Int, Bool, SemanticType))) (found: List((Int, Bool, SemanticType))) =
    match facts with
        | [] -> found
        | (candidate, index, empty, argumentType) :: rest ->
            if candidate == span
            then callArgumentsAt(span)(rest)((index, empty, argumentType) :: found)
            else callArgumentsAt(span)(rest)(found)

let recursive argumentFactAt (index: Int) (arguments: List((Int, Bool, SemanticType))) =
    match arguments with
        | [] -> None
        | (candidate, empty, argumentType) :: rest ->
            if candidate == index
            then Some((empty, argumentType))
            else argumentFactAt(index)(rest)

let recursive elementAtSegment (segment: Str) (index: Int) (elements: List(SemanticType)) =
    match elements with
        | [] -> None
        | element :: rest ->
            if Ashes.Text.fromInt(index) == segment
            then Some(element)
            else elementAtSegment(segment)(index + 1)(rest)

// Stage 0's `TypeAtReachPath`: the type a reach path names inside a value of this type, as far as the
// path can be read: a list's head ("0", or a callee's element "e") and tail ("1"), a tuple's field
// by index. A segment it cannot read leaves the type reached so far.
let recursive typeAtReachPath (semanticType: SemanticType) (segments: List(Str)) (state: CoreLoweringState) =
    match (segments, resolveType(state)(semanticType)) with
        | ([], resolved) -> resolved
        | (segment :: rest, SemList(element)) ->
            if segment == "0" || segment == "e"
            then typeAtReachPath(element)(rest)(state)
            else
                if segment == "1"
                then typeAtReachPath(SemList(element))(rest)(state)
                else SemList(element)
        | (segment :: rest, SemTuple(elements)) ->
            match elementAtSegment(segment)(0)(elements) with
                | Some(element) -> typeAtReachPath(element)(rest)(state)
                | None -> SemTuple(elements)
        | (_segments, resolved) -> resolved

let withoutExposedTag (root: Str) =
    if Ashes.Text.startsWith(root)("~")
    then Ashes.Text.substring(root)(1)(Ashes.Text.length(root) - 1)
    else root

let recursive indexOfParameter (name: Str) (parameters: List(Str)) (index: Int) =
    match parameters with
        | [] -> -1
        | candidate :: rest ->
            if candidate == name
            then index
            else indexOfParameter(name)(rest)(index + 1)

// Stage 0's `RecordTokenProven`: a value of the callee's own is held once and never a construction;
// a parameter path whose type at this call can hold the record's type names an argument holding
// nothing (an empty list); a binding's identity token rides beside the values it names.
let recordTokenProven (entry: ParameterReachEntry) (parameters: List(Str)) (arguments: List((Int, Bool, SemanticType))) (target: SemanticType) (state: CoreLoweringState) =
    match entry with
        | ParameterReachEntry { parameterName = name, reachCount = count } ->
            match Ashes.Text.split(name)("/") with
                | [] -> false
                | first :: segments ->
                    let root = withoutExposedTag(first)
                    in
                        if Ashes.Text.startsWith(root)("#S")
                        then count <= 1
                        else
                            if Ashes.Text.startsWith(root)("#B")
                            then true
                            else
                                if Ashes.Text.startsWith(root)("#")
                                then false
                                else
                                    match argumentFactAt(indexOfParameter(root)(parameters)(0))(arguments) with
                                        | None -> false
                                        | Some((true, _argumentType)) -> true
                                        | Some((false, argumentType)) ->
                                            !containsResolvedType(typeAtReachPath(argumentType)(segments)(state))(target)([])(state)

let recursive allRecordTokensProven (entries: List(ParameterReachEntry)) (parameters: List(Str)) (arguments: List((Int, Bool, SemanticType))) (target: SemanticType) (state: CoreLoweringState) =
    match entries with
        | [] -> true
        | entry :: rest -> recordTokenProven(entry)(parameters)(arguments)(target)(state) && allRecordTokensProven(rest)(parameters)(arguments)(target)(state)

// Only an internal sharing the tokens account for leaves a record reach proven.
let recursive recordCausesAllowed (causes: List(ResultReachCause)) =
    match causes with
        | [] -> true
        | cause :: rest -> (cause == InternalSharing || cause == ReachCauseNone) && recordCausesAllowed(rest)

// The record-mode reach of a function by name, with the calls at the cut spans as its own values.
let recordReachNamed (name: Str) (cuts: List(TextSpan)) (state: CoreLoweringState) =
    match (stateReachRegistry(state), calleeSummaryOf(name)(state)) with
        | (Some(registry), Some(ReachSummary { function = ReachFunction { key = key, parameters = parameters } })) ->
            match recordReachOf(registry)(stateReachTable(state))(key)(cuts) with
                | Some(reach) -> Some((reach, parameters))
                | None -> None
        | _ -> None

// Stage 0's `TakenOverRecordProven`, obligations 2 and 3 at the settling call: the callee handed a
// record over, and in its record reach every parameter path that can hold the record's type at this
// call names an argument holding nothing; any other value its result holds there declines.
let takenOverRecordProven (callee: Str) (span: Maybe(TextSpan)) (element: SemanticType) (state: CoreLoweringState) =
    match (state
    |> stateTakenOverRecordNames
    |> containsLabel(callee), span) with
        | (true, Some(callSpan)) ->
            match recordReachNamed(callee)(stateSpineFreedSpans(state))(state) with
                | Some((ResultReachState { counts = counts, causes = causes }, parameters)) ->
                    recordCausesAllowed(causes) && allRecordTokensProven(counts)(parameters)(callArgumentsAt(callSpan)(stateTakenOverCallArguments(state))([]))(resolveType(state)(element))(state)
                | None -> false
        | _ -> false

let recursive ownFieldSpine (expression: Expr) (arguments: List(Expr)) =
    match expression with
        | ExprAt(_span, inner) -> ownFieldSpine(inner)(arguments)
        | ExprNode(_node, inner) -> ownFieldSpine(inner)(arguments)
        | ExprCall(function, argument, _sugar, _layout) -> ownFieldSpine(function)(argument :: arguments)
        | other -> (other, arguments)

let recursive ownFieldWithoutNames (names: List(Str)) (parameters: List(Str)) =
    match parameters with
        | [] -> []
        | parameter :: rest ->
            if containsLabel(parameter)(names)
            then ownFieldWithoutNames(names)(rest)
            else parameter :: ownFieldWithoutNames(names)(rest)

// Stage 0's `OwnFieldArgument`: a constructor argument the cell owns or that holds nothing
// reference-counted, a retained parameter or a scalar or static literal.
let ownFieldArgument (parameters: List(Str)) (argument: Expr) =
    match unspanArgument(argument) with
        | ExprVar(name) -> containsLabel(name)(parameters)
        | ExprInt(_value) -> true
        | ExprBool(_value) -> true
        | ExprString(_value) -> true
        | _ -> false

let recursive allOwnFieldArguments (parameters: List(Str)) (arguments: List(Expr)) =
    match arguments with
        | [] -> true
        | argument :: rest -> ownFieldArgument(parameters)(argument) && allOwnFieldArguments(parameters)(rest)

let nullaryConstructorName (name: Str) (state: CoreLoweringState) =
    match constructorLayout(name)(state) with
        | Some(layout) -> constructorArity(layout) == 0
        | None -> false

let constructorOfArity (name: Str) (arity: Int) (state: CoreLoweringState) =
    match constructorLayout(name)(state) with
        | Some(layout) -> constructorArity(layout) == arity
        | None -> false

// Stage 0's `OwnFieldLeaves`: every value the callee can return is a nullary constructor, its own tail
// self-call, or a constructor over retained parameters and literals, unshadowed where they are read.
let recursive ownFieldLeaves (expression: Expr) (parameters: List(Str)) (selfName: Str) (arity: Int) (state: CoreLoweringState) =
    match expression with
        | ExprAt(_span, inner) -> ownFieldLeaves(inner)(parameters)(selfName)(arity)(state)
        | ExprNode(_node, inner) -> ownFieldLeaves(inner)(parameters)(selfName)(arity)(state)
        | ExprIf(_condition, thenBranch, elseBranch) -> ownFieldLeaves(thenBranch)(parameters)(selfName)(arity)(state) && ownFieldLeaves(elseBranch)(parameters)(selfName)(arity)(state)
        | ExprMatch(_scrutinee, arms, _position) -> ownFieldArms(arms)(parameters)(selfName)(arity)(state)
        | ExprLet(name, _value, body, _parameters, _annotation, _requirements) ->
            ownFieldLeaves(body)(ownFieldWithoutNames([name])(parameters))(selfName)(arity)(state)
        | ExprVar(name) -> nullaryConstructorName(name)(state)
        | ExprCall(_function, _argument, _sugar, _layout) ->
            match ownFieldSpine(expression)([]) with
                | (root, arguments) ->
                    match unspanArgument(root) with
                        | ExprVar(name) ->
                            if name == selfName && length(arguments) == arity
                            then true
                            else
                                constructorOfArity(name)(length(arguments))(state) && allOwnFieldArguments(parameters)(arguments)
                        | _ -> false
        | _ -> false
and ownFieldArms (arms: List((Pattern, Expr, Maybe(Expr)))) (parameters: List(Str)) (selfName: Str) (arity: Int) (state: CoreLoweringState) =
    match arms with
        | [] -> true
        | (pattern, body, _guard) :: rest ->
            ownFieldLeaves(body)(ownFieldWithoutNames(shapePatternNames(pattern)([]))(parameters))(selfName)(arity)(state) && ownFieldArms(rest)(parameters)(selfName)(arity)(state)

let recursive namesNotedFor (function: Str) (notes: List((Str, Str))) =
    match notes with
        | [] -> []
        | (owner, name) :: rest ->
            if owner == function
            then name :: namesNotedFor(function)(rest)
            else namesNotedFor(function)(rest)

let recursive keptParameters (parameters: List(Str)) (retained: List(Str)) (pending: List(Str)) =
    match parameters with
        | [] -> []
        | parameter :: rest ->
            if containsLabel(parameter)(retained) && !containsLabel(parameter)(pending)
            then parameter :: keptParameters(rest)(retained)(pending)
            else keptParameters(rest)(retained)(pending)

// Stage 0's `OwnFieldRecordProven`.
let ownFieldRecordProven (callee: Str) (state: CoreLoweringState) =
    match calleeSummaryOf(callee)(state) with
        | Some(ReachSummary { function = ReachFunction { name = selfName, parameters = parameters, body = body } }) ->
            match state
            |> stateOwnFieldPendingParameters
            |> namesNotedFor(callee)
            |> keptParameters(parameters)(state
            |> stateOwnFieldRetainedParameters
            |> namesNotedFor(callee)) with
                | [] -> false
                | kept ->
                    ownFieldLeaves(body)(kept)(selfName)(length(parameters))(state)
        | None -> false

let settleKindProven (callee: Str) (span: Maybe(TextSpan)) (kind: CoreSettleKind) (state: CoreLoweringState) =
    match kind with
        | ListRecordKind(element) -> takenOverRecordProven(callee)(span)(element)(state)
        | OwnFieldKind(_tagless, _constructors) -> ownFieldRecordProven(callee)(state)

let recursive holdsOwnValueOf (entries: List(ParameterReachEntry)) =
    match entries with
        | [] -> false
        | ParameterReachEntry { parameterName = name } :: rest -> Ashes.Text.startsWith(name)("#S") || holdsOwnValueOf(rest)

// Stage 0's `LetScopedSettleOutOfReach`: the enclosing function's record reach, with the let's value
// call as a value of its own, names nothing of it.
let letScopedSettleOutOfReach (span: Maybe(TextSpan)) (state: CoreLoweringState) =
    match (stateActiveFunctionOrigin(state), span) with
        | (Some(IrFunctionOrigin { sourceOrigin = Some(SourceFunctionOrigin { functionSourceName = enclosing }) }), Some(callSpan)) ->
            match recordReachNamed(enclosing)([callSpan])(state) with
                | Some((ResultReachState { counts = counts, causes = causes }, _parameters)) -> recordCausesAllowed(causes) && !holdsOwnValueOf(counts)
                | None -> false
        | _ -> false

// A new settlement and its placeholder, kept out of the stream's meaning (it reads and defines
// nothing) until the settlement round.
let newTakenOverSettle (temp: Int) (callee: Str) (kind: CoreSettleKind) (state: CoreLoweringState) =
    (let settles = statePendingTakenOverSettles(state)
    in
        let id = length(settles)
        in (withStatePendingTakenOverSettles(CoreTakenOverSettle(settleId = id, settleTemp = temp, settleCallee = callee, settleKind = kind) :: settles)(state), IrInstruction(instruction = TcoResetPending(takenOverSettleBase + id)([])([]), location = None)))

// Stage 0's `TrySettleTakenOverBehindCopy`: behind a caller's copy of a call's arena result, the
// references the arena cells took over are released from the original.
let settleTakenOverBehindCopy (context: CoreCallContext) (temp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match (takenOverSettleCallee(context), takenOverSettleKind(semanticType)(state)) with
        | (Some(callee), Some(kind)) ->
            let noted =
                match context.siteSpan with
                    | Some(span) -> withStateSettledBehindCopySpans(span :: stateSettledBehindCopySpans(state))(state)
                    | None -> state
            in
                if settleKindProven(callee)(context.siteSpan)(kind)(noted)
                then
                    match newTakenOverSettle(temp)(callee)(kind)(noted) with
                        | (settled, placeholder) -> settled with reversedInstructions = placeholder :: settled.reversedInstructions
                else noted
        | _ -> state

// The placeholder goes behind the newest call instruction, ahead of that call's window restore.
let recursive insertBehindNewestCall (placeholder: IrInstruction) (reversedInstructions: List(IrInstruction)) =
    match reversedInstructions with
        | [] -> []
        | (IrInstruction { instruction = CallClosure(_target, _closure, _argument, _flag) } as call) :: rest -> placeholder :: call :: rest
        | (IrInstruction { instruction = CallKnown(_target, _label, _environment, _argument, _flag, _tail) } as call) :: rest -> placeholder :: call :: rest
        | instruction :: rest -> instruction :: insertBehindNewestCall(placeholder)(rest)

let recursive newestCallArgumentOf (reversedInstructions: List(IrInstruction)) =
    match reversedInstructions with
        | [] -> None
        | IrInstruction { instruction = CallClosure(_target, _closure, argument, _flag) } :: _rest -> Some(argument)
        | IrInstruction { instruction = CallKnown(_target, _label, _environment, argument, _flag, _tail) } :: _rest -> Some(argument)
        | _instruction :: rest -> newestCallArgumentOf(rest)

// Stage 0's `SettleHeldTakenOverResultsConsumedBy`: a held call result whose last use is the argument
// of a call with a scalar result is settled right after that call.
let recursive settleHeldTakenOverResults (argument: Int) (held: List((Int, Str, CoreSettleKind, Maybe(TextSpan)))) (kept: List((Int, Str, CoreSettleKind, Maybe(TextSpan)))) (state: CoreLoweringState) =
    match held with
        | [] ->
            withStateTakenOverHeldResults(reverse(kept))(state)
        | (temp, callee, kind, span) :: rest ->
            if temp == argument
            then
                if settleKindProven(callee)(span)(kind)(state)
                then
                    match newTakenOverSettle(temp)(callee)(kind)(state) with
                        | (noted, placeholder) -> settleHeldTakenOverResults(argument)(rest)(kept)((noted with reversedInstructions = insertBehindNewestCall(placeholder)(noted.reversedInstructions)))
                else settleHeldTakenOverResults(argument)(rest)(kept)(state)
            else settleHeldTakenOverResults(argument)(rest)((temp, callee, kind, span) :: kept)(state)

// Stage 0's `NoteTakenOverCallResult`: a call settles the held results it consumes, and its own
// result, when not settled behind a copy, is held.
let noteTakenOverCallResult (context: CoreCallContext) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            let settled =
                match (resultSurvivesReset(semanticType)(state), newestCallArgumentOf(state.reversedInstructions)) with
                    | (true, Some(argument)) ->
                        settleHeldTakenOverResults(argument)(stateTakenOverHeldResults(state))([])(state)
                    | _ -> state
            in
                let behindCopy =
                    match context.siteSpan with
                        | Some(span) ->
                            settled
                            |> stateSettledBehindCopySpans
                            |> spanListed(span)
                        | None -> false
                in
                    match (behindCopy, takenOverSettleCallee(context), takenOverSettleKind(semanticType)(settled)) with
                        | (false, Some(callee), Some(kind)) -> lowered with state = withStateTakenOverHeldResults((temp, callee, kind, context.siteSpan) :: stateTakenOverHeldResults(settled))(settled)
                        | _ -> lowered with state = settled
        | _ -> lowered

let recursive takeHeldTakenOverResult (temp: Int) (held: List((Int, Str, CoreSettleKind, Maybe(TextSpan)))) =
    match held with
        | [] -> None
        | (candidate, callee, kind, span) :: rest ->
            if candidate == temp
            then Some(((callee, kind, span), rest))
            else
                match takeHeldTakenOverResult(temp)(rest) with
                    | Some((found, remaining)) -> Some((found, (candidate, callee, kind, span) :: remaining))
                    | None -> None

// Stage 0's `SettleLetScopedTakenOver`: at the end of a let scope whose value is a held call result,
// the let is the holder, settled when nothing the enclosing function returns can reach the value. The
// walk reads the value through a load of the let's own slot (stage 0's `LetOwnerLoadForSettle`),
// emitted for every held value as stage 0 does before its proof: lifetime placement follows the
// owner's loads, so the owner's release lands after the walk.
let settleLetScopedTakenOver (ownerSlot: Int) (valueTemp: Int) (state: CoreLoweringState) =
    match state
    |> stateTakenOverHeldResults
    |> takeHeldTakenOverResult(valueTemp) with
        | None -> state
        | Some(((callee, kind, span), remaining)) ->
            match state
            |> withStateTakenOverHeldResults(remaining)
            |> freshTemp with
                | FreshTemp { state = loadState, temp = ownerLoad } ->
                    let taken =
                        emit(LoadLocal(ownerLoad)(ownerSlot))(loadState)
                    in
                        if settleKindProven(callee)(span)(kind)(taken) && letScopedSettleOutOfReach(span)(taken)
                        then
                            match newTakenOverSettle(ownerLoad)(callee)(kind)(taken) with
                                | (noted, placeholder) -> noted with reversedInstructions = placeholder :: noted.reversedInstructions
                        else taken

let recursive heldResultAtSpan (span: TextSpan) (held: List((Int, Str, CoreSettleKind, Maybe(TextSpan)))) =
    match held with
        | [] -> None
        | (temp, callee, kind, Some(candidate)) :: rest ->
            if candidate == span
            then Some((temp, callee, kind))
            else heldResultAtSpan(span)(rest)
        | _entry :: rest -> heldResultAtSpan(span)(rest)

// Stage 0's `SettleMatchScopedTakenOver`: the end of a match whose scrutinee is a held call result is
// a settle site when the match's result cannot hold a value of any of the result's heap field types.
let settleMatchScopedTakenOver (value: Expr) (lowered: LoweredCoreValue) =
    match (value, lowered) with
        | (ExprAt(span, _inner), LoweredCoreValue { state = state, semanticType = matchType, error = None }) ->
            match state
            |> stateTakenOverHeldResults
            |> heldResultAtSpan(span) with
                | Some((temp, callee, OwnFieldKind(tagless, constructors))) ->
                    if anyFieldTypeHeldBy(resolveType(state)(matchType))(constructors)(state)
                    then lowered
                    else
                        match state
                        |> stateTakenOverHeldResults
                        |> takeHeldTakenOverResult(temp) with
                            | Some(((_callee, _kind, heldSpan), remaining)) ->
                                let taken = withStateTakenOverHeldResults(remaining)(state)
                                in
                                    if settleKindProven(callee)(heldSpan)(OwnFieldKind(tagless)(constructors))(taken)
                                    then
                                        match newTakenOverSettle(temp)(callee)(OwnFieldKind(tagless)(constructors))(taken) with
                                            | (noted, placeholder) -> lowered with state = (noted with reversedInstructions = placeholder :: noted.reversedInstructions)
                                    else lowered with state = taken
                            | None -> lowered
                | _ -> lowered
        | _ -> lowered

// The normalizing copy of a call's contract result, with the mask and the parts the callee handed
// over read before it and the parts the mask names released after it.
let emitCallResultCopyTakingParts (context: CoreCallContext) (temp: Int) (semanticType: SemanticType) (plan: ArgumentCopyPlan) (ownedSlot: Int) (handedParts: Maybe((List(Int), Int))) (ownedLabel: Str) (state: CoreLoweringState) =
    match handedParts with
        | None ->
            match emitGuardedDeepCopy(temp)(plan)(state) with
                | (copied, copyTemp) ->
                    copied
                    |> emit(StoreLocal(ownedSlot)(copyTemp))
                    |> settleTakenOverBehindCopy(context)(temp)(semanticType)
        | Some((parts, cell)) ->
            match loadHandedOverMaskAndParts(temp)(parts)(cell)(state) with
                | (loaded, maskTemp, partTemps) ->
                    match loaded
                    |> emitJumpIfWholeResultHandedOver(maskTemp)(ownedLabel)
                    |> emitJumpIfIndependentCountedResult(maskTemp)(temp)(semanticType)(parts)(ownedLabel)
                    |> emitGuardedDeepCopy(temp)(plan) with
                        | (copied, copyTemp) ->
                            copied
                            |> emit(StoreLocal(ownedSlot)(copyTemp))
                            |> releaseHandedOverTupleParts(semanticType)(parts)(maskTemp)(partTemps)
                            |> releaseHandedOverListHeads(semanticType)(maskTemp)(temp)
                            |> settleTakenOverBehindCopy(context)(temp)(semanticType)

// Stage 0's withdrawal in `LowerCallRestoreArenaFor`: a call result that is not copied out under
// the contract takes no parts over.
let withdrawTuplePartsRequestOutsideContract (context: CoreCallContext) (temp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match isGeneralRcValueTypeIndexed(semanticType)(state) with
        | (true, queried) -> queried
        | (false, queried) ->
            match takeTupleResultPartsRequest(context)(temp)(semanticType)(true)(queried) with
                | (withdrawn, _parts) -> withdrawn

// Stage 0's `NoteTakenOverOwnedSlotSource`: the call an owned call-result slot holds the result of.
let noteTakenOverSlotSource (context: CoreCallContext) (ownedSlot: Int) (state: CoreLoweringState) =
    match context.siteSpan with
        | Some(span) -> withStateTakenOverSlotSources((stateActiveFunctionOrigin(state), ownedSlot, span) :: stateTakenOverSlotSources(state))(state)
        | None -> state

// Stage 0's `LowerGeneralRcCallResult`: a call result of the contract's types is owned by the
// caller once the call's window is reset. It is taken over as it is from a callee known to return
// it owned; from a closure it cannot name it is taken over when the closure's header says its
// function returns it owned; and it is normalized otherwise, since a generic callee's result may
// borrow its arguments' parts. The value is kept in an owned slot and handed on without a
// runtime-managed fact.
let lowerGeneralRcCallResultTakingParts (context: CoreCallContext) (returnsOwned: Bool) (handedParts: Maybe((List(Int), Int))) (cursorSlot: Int) (endSlot: Int) (preRestoreSlot: Int) (temp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match if returnsOwned
    then (state, -1)
    else
        match closureOfCallResult(temp)(64)(state.reversedInstructions) with
            | Some(closureTemp) -> emitClosureReturnsGeneralRcOwnedFlag(closureTemp)(state)
            | None -> (state, -1) with
        | (flagged, ownedFlagTemp) ->
            match freshLocal(flagged) with
                | FreshLocal { state = slottedOwned, local = ownedSlot } ->
                    match saveGeneralRcOwnedPredecessor(ownedSlot)(slottedOwned) with
                        | (slotted, predecessorSlot) ->
                            let restored =
                                slotted
                                |> emit(StoreLocal(ownedSlot)(temp))
                                |> emitPostGuardedArenaRestore(cursorSlot)(endSlot)(preRestoreSlot)
                            in
                                let owned =
                                    match (returnsOwned, generalCopyPlanOf(semanticType)(restored)) with
                                        | (false, Some(plan)) ->
                                            match freshLabel("general_rc_result_owned")(restored) with
                                                | FreshLabel { state = labelled, label = ownedLabel } ->
                                                    labelled
                                                    |> emitOwnedFlagTest(ownedFlagTemp)(ownedLabel)
                                                    |> emitCallResultCopyTakingParts(context)(temp)(semanticType)(plan)(ownedSlot)(handedParts)(ownedLabel)
                                                    |> emit(Label(ownedLabel))
                                        | _ -> restored
                                in
                                    match owned
                                    |> emitPostGuardedArenaReclaim(endSlot)(preRestoreSlot)
                                    |> registerGeneralRcOwnedSlot(ownedSlot)(semanticType)
                                    |> noteTakenOverSlotSource(context)(ownedSlot)
                                    |> registerGeneralRcOwnedPredecessor(predecessorSlot)(ownedSlot)(semanticType)
                                    |> freshTemp with
                                        | FreshTemp { state = loaded, temp = resultTemp } ->
                                            loaded
                                            |> emit(LoadLocal(resultTemp)(ownedSlot))
                                            |> success(resultTemp)(semanticType)

let lowerGeneralRcCallResultOwned (context: CoreCallContext) (cursorSlot: Int) (endSlot: Int) (preRestoreSlot: Int) (temp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    (let returnsOwned = calleeReturnsGeneralRcOwned(context)(state) && !siblingResultPinned(context)(semanticType)(state)
    in
        match takeTupleResultPartsRequest(context)(temp)(semanticType)(returnsOwned)(state) with
            | (requestState, handedParts) -> lowerGeneralRcCallResultTakingParts(context)(returnsOwned)(handedParts)(cursorSlot)(endSlot)(preRestoreSlot)(temp)(semanticType)(requestState))

// A result of the contract's types is owned by the caller once the call is lowered, so a request
// for an arena result the call made while its type was unresolved (a phantom in it) is withdrawn
// first, as stage 0's `LowerGeneralRcCallResult` does.
let lowerGeneralRcCallResult (context: CoreCallContext) (cursorSlot: Int) (endSlot: Int) (preRestoreSlot: Int) (temp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    state
    |> cancelArenaResultRequest(temp)
    |> lowerGeneralRcCallResultOwned(context)(cursorSlot)(endSlot)(preRestoreSlot)(temp)(semanticType)

let closeContractCallWindow (context: CoreCallContext) cursorSlot endSlot (stage: CoreCallStage) =
    match stage with
        | CoreCallStage { lowered = LoweredCoreValue { error = Some(_error) } } -> stage
        | CoreCallStage { lowered = LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None }, resultFlagTemp = flagTemp, resultNormalizedOwned = normalizedOwned } ->
            match state
            |> pinDeferredSelfCallResult(semanticType)
            |> withdrawTuplePartsRequestOutsideContract(context)(temp)(semanticType)
            |> freshLocal with
                | FreshLocal { state = allocated, local = preRestoreSlot } ->
                    if !isGeneralRcValueType(semanticType)(allocated) && (isRuntimeTemp(temp)(allocated) || resultSurvivesReset(semanticType)(allocated) || specializationSelfReuseCall(context)(allocated))
                    then
                        (if specializationSelfReuseCall(context)(allocated)
                        then markReuseResultTemp(temp)(allocated)
                        else allocated)
                        |> emitGuardedRestoreAndReclaim(cursorSlot)(endSlot)(preRestoreSlot)
                        |> success(temp)(semanticType)
                        |> closedCallStage(stage)(false)(false)
                    else
                        if isGeneralRcValueType(semanticType)(allocated)
                        then
                            allocated
                            |> lowerGeneralRcCallResult(context)(cursorSlot)(endSlot)(preRestoreSlot)(temp)(semanticType)
                            |> closedCallStage(stage)(true)(true)
                            |> (given (closed: CoreCallStage) -> closed with resultGeneralRc = true)
                        else
                            match (callCopyOutOf(semanticType)(allocated), flagTemp >= 0) with
                                | (Some(copyOut), true) ->
                                    match emitConditionalCallCopyOut(copyOut)(temp)(flagTemp)(cursorSlot)(endSlot)(preRestoreSlot)(allocated) with
                                        | (closed, resultTemp) ->
                                            closed
                                            |> success(resultTemp)(semanticType)
                                            |> closedCallStage(stage)(true)(false)
                                // Stage 0's `LowerCallCopyOutResult`: a call that asked for an arena result holds the
                                // callee's arena copy, copied out of the window before it is reset.
                                | (Some(copyOut), false) when callRequestsArenaResult(temp)(allocated.reversedInstructions) ->
                                    match emitPlainCallCopyOut(copyOut)(temp)(cursorSlot)(endSlot)(preRestoreSlot)(allocated) with
                                        | (closed, resultTemp) ->
                                            closed
                                            |> success(resultTemp)(semanticType)
                                            |> closedCallStage(stage)(true)(false)
                                | (None, _) ->
                                    match (flagTemp >= 0 && normalizedOwned, argumentCopyPlanOf(semanticType)(allocated), genericListDeepCopyPlanOf(context)(flagTemp >= 0)(semanticType)(allocated)) with
                                        | (true, Some(plan), _elementPlan) ->
                                            match emitConditionalNormalizeResult(plan)(temp)(flagTemp)(cursorSlot)(endSlot)(preRestoreSlot)(allocated) with
                                                | (closed, resultTemp) ->
                                                    closed
                                                    |> success(resultTemp)(semanticType)
                                                    |> closedCallStage(stage)(true)(false)
                                                    |> (given (owned: CoreCallStage) -> owned with resultNormalizedOwned = true)
                                        | (_normalized, _plan, Some(elementPlan)) ->
                                            match emitCallDeepCopyOut(elementPlan)(temp)(flagTemp)(cursorSlot)(endSlot)(preRestoreSlot)(allocated) with
                                                | (closed, resultTemp) ->
                                                    closed
                                                    |> withStateGenericDeepCopiedListTemps(resultTemp :: stateGenericDeepCopiedListTemps(closed))
                                                    |> markFlaggedDeepCopyOwned(flagTemp)(resultTemp)
                                                    |> success(resultTemp)(semanticType)
                                                    |> closedCallStage(stage)(true)(true)
                                        | _ ->
                                            match deferCallCopyOut(context.selfCallee && stateSpecializingReuseLabel(allocated) == None)(cursorSlot)(endSlot)(preRestoreSlot)(temp)(semanticType)(allocated) with
                                                | (deferred, resultTemp) ->
                                                    deferred
                                                    |> success(resultTemp)(semanticType)
                                                    |> closedCallStage(stage)(false)(false)
                                | _ ->
                                    allocated
                                    |> success(temp)(semanticType)
                                    |> closedCallStage(stage)(false)(false)

// A call routed to a reuse specialization whose result is of the contract's types leaves its window
// open, as stage 0 opens none, and hands its result on as a reuse result the enclosing resets
// keep; every other call closes its window by its result's contract.
let closeCallWindow (context: CoreCallContext) cursorSlot endSlot (stage: CoreCallStage) =
    match stage with
        | CoreCallStage { lowered = LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } } ->
            if routedSpecializationCall(context)(state) && isGeneralRcValueType(semanticType)(state)
            then
                state
                |> markReuseResultTemp(temp)
                |> success(temp)(semanticType)
                |> closedCallStage(stage)(false)(false)
            else closeContractCallWindow(context)(cursorSlot)(endSlot)(stage)
        | _ -> closeContractCallWindow(context)(cursorSlot)(endSlot)(stage)

// Unifies the result a spine of `arity` applications of the callee produces with the type the
// context expects of the call, before any argument is lowered: a callee type that is still a
// variable at some arrow is made a function type on the way.
let recursive preconstrainCallResultType functionType arity expected state =
    if arity == 0
    then bindType(expected)(functionType)(state)
    else
        match ensureFunctionType(functionType)(state) with
            | FunctionTypeResolution { state = failedState, error = Some(_error) } -> (failedState, None)
            | FunctionTypeResolution { state = functionState, resultType = resultType, error = None } -> preconstrainCallResultType(resultType)(arity - 1)(expected)(functionState)

let preconstrainCallResult expected (site: Maybe(CoreMismatchSite)) arity loweredCallee =
    match (expected, loweredCallee) with
        | (None, _) -> loweredCallee
        | (_, LoweredCoreValue { error = Some(_error) }) -> loweredCallee
        | (Some(expectedType), LoweredCoreValue { state = state, temp = temp, semanticType = functionType, error = None }) ->
            match preconstrainCallResultType(functionType)(arity)(expectedType)(state) with
                | (failedState, Some(error)) ->
                    error
                    |> failure(failedState)
                    |> locateLoweredMismatch(site)
                | (constrainedState, None) -> success(temp)(functionType)(constrainedState)

// The type an argument's form gives it before it is lowered: a literal's, a variable's whose
// binding is monomorphic, or a tuple's or a non-empty list's whose items all have one.
let recursive knownArgumentType (expression: Expr) (state: CoreLoweringState) =
    match unspanArgument(expression) with
        | ExprInt(_value) -> Some(SemInt)
        | ExprUInt(_value, bits, _raw) -> Some(SemUInt(bits))
        | ExprFloat(_value, _raw) -> Some(SemFloat)
        | ExprBigInt(_digits) -> Some(SemBigInt)
        | ExprString(_value) -> Some(SemString)
        | ExprRune(_value) -> Some(SemRune)
        | ExprBool(_value) -> Some(SemBool)
        | ExprVar(name) ->
            match lookupBinding(name)(state.bindings) with
                | Some(CoreBinding { scheme = TypeScheme { quantified = [], body = body, constraints = [] } }) -> Some(body)
                | _ -> None
        | ExprTuple(elements) ->
            match knownArgumentTypes(elements)(state) with
                | Some(types) -> Some(SemTuple(types))
                | None -> None
        | ExprList(item :: items, _layout) ->
            match (knownArgumentType(item)(state), knownArgumentTypes(items)(state)) with
                | (Some(itemType), Some(_types)) -> Some(SemList(itemType))
                | _ -> None
        | _ -> None
and knownArgumentTypes (expressions: List(Expr)) (state: CoreLoweringState) =
    match expressions with
        | [] -> Some([])
        | expression :: rest ->
            match (knownArgumentType(expression)(state), knownArgumentTypes(rest)(state)) with
                | (Some(first), Some(others)) -> Some(first :: others)
                | _ -> None

// Stage 0's `PreconstrainKnownCallArgumentTypes`: before any argument of the spine is lowered,
// each parameter meets the type its argument's form gives it, so a lambda argument lowered first
// sees the type a later argument gives its parameter (`apply(given (x) -> print(x))(3)`). A
// mismatch is left to the argument's own unification.
let recursive preconstrainKnownArgumentTypes functionType (arguments: List(Expr)) (state: CoreLoweringState) =
    match arguments with
        | [] -> state
        | argument :: rest ->
            match ensureFunctionType(functionType)(state) with
                | FunctionTypeResolution { error = Some(_error) } -> state
                | FunctionTypeResolution { state = functionState, argumentType = argumentType, resultType = resultType, error = None } ->
                    match match recordedClosedTypeAt(nodeIdentityOf(argument))(functionState) with
                        | Some(recorded) -> Some(recorded)
                        | None -> knownArgumentType(argument)(functionState) with
                        | None ->
                            functionState
                            |> preconstrainConstructorArgument(argument)(argumentType)
                            |> preconstrainKnownArgumentTypes(resultType)(rest)
                        | Some(known) ->
                            match bindType(argumentType)(known)(functionState) with
                                | (constrained, None) -> preconstrainKnownArgumentTypes(resultType)(rest)(constrained)
                                | (_failed, Some(_error)) -> preconstrainKnownArgumentTypes(resultType)(rest)(functionState)
and preconstrainConstructorArgument argument expected state =
    match ownFieldSpine(argument)([]) with
        | (ExprVar(name), fields) ->
            match constructorLayout(name)(state) with
                | Some(layout) ->
                    if length(fields) == constructorArity(layout)
                    then
                        match instantiate(layout.scheme)(state.typeSupply) with
                            | InstantiationResult { semanticType = arrow, supply = supply } ->
                                let constrained =
                                    state
                                    |> withTypeSupply(supply)
                                    |> preconstrainKnownArgumentTypes(arrow)(fields)
                                in
                                    match preconstrainCallResultType(arrow)(length(fields))(expected)(constrained) with
                                        | (typed, None) -> typed
                                        | _ -> state
                    else state
                | None -> state
        | _ -> state

let preconstrainKnownArguments (context: CoreCallContext) loweredCallee =
    match loweredCallee with
        | LoweredCoreValue { error = Some(_error) } -> loweredCallee
        | LoweredCoreValue { state = state, temp = temp, semanticType = functionType, error = None } ->
            state
            |> preconstrainKnownArgumentTypes(functionType)(context.arguments)
            |> success(temp)(functionType)

// The callee of one application inside a call spine `f(a)(b)`: a further application is another
// stage of the same spine, an applied lambda literal is lowered as a stack closure, and anything
// else is an ordinary expression. The stages share the window `lowerCall` opened around the
// whole spine; a call inside an argument opens its own. The root callee's result after the
// spine's `arity` applications is constrained to the expected type before the arguments.
let recursive lowerCallSpineCallee expression (context: CoreCallContext) expected (site: Maybe(CoreMismatchSite)) arity (transfers: Bool) lower state =
    match expression with
        // The applications of a spine are located at the outermost one; the callee they end in is
        // located where it is written, which a pipeline puts on a later line than the call.
        | ExprAt(_span, inner) ->
            match unspanArgument(inner) with
                | ExprCall(_function, _argument, _isSugar, _layout) -> lowerCallSpineCallee(inner)(context)(expected)(site)(arity)(transfers)(lower)(state)
                | ExprLambda(_parameter, _body, _annotation) -> lowerCallSpineCallee(inner)(context)(expected)(site)(arity)(transfers)(lower)(state)
                | _ ->
                    state
                    |> lower(expression)
                    |> preconstrainCallResult(expected)(site)(arity)
                    |> preconstrainKnownArguments(context)
                    |> callStageOf
        | ExprNode(_node, inner) ->
            match unspanArgument(inner) with
                | ExprCall(_function, _argument, _isSugar, _layout) -> lowerCallSpineCallee(inner)(context)(expected)(site)(arity)(transfers)(lower)(state)
                | ExprLambda(_parameter, _body, _annotation) -> lowerCallSpineCallee(inner)(context)(expected)(site)(arity)(transfers)(lower)(state)
                | _ ->
                    state
                    |> lower(expression)
                    |> preconstrainCallResult(expected)(site)(arity)
                    |> preconstrainKnownArguments(context)
                    |> callStageOf
        | ExprCall(function, argument, _isSugar, _layout) -> lowerCallSpineStage(function)(argument)(context)(expected)(site)(arity + 1)(transfers)(lower)(state)
        | ExprLambda(parameter, body, annotation) ->
            state
            |> lowerLambda(parameter)(body)(annotation)(true)(lower)
            |> preconstrainCallResult(expected)(site)(arity)
            |> preconstrainKnownArguments(context)
            |> callStageOf
        | _ ->
            state
            |> lower(expression)
            |> preconstrainCallResult(expected)(site)(arity)
            |> preconstrainKnownArguments(context)
            |> callStageOf
and lowerCallSpineStage function argument (context: CoreCallContext) expected (site: Maybe(CoreMismatchSite)) arity (transfers: Bool) lower state =
    state
    |> lowerCallSpineCallee(function)(context)(expected)(site)(arity)(transfers)(lower)
    |> lowerCoreCallFunction(context)(arity)(argument)(transfers)(lower)

// Stage 0's TCO tail self-call (`LowerCallTcoEvalArgs`): in tail position of the loop body, the
// loop function applied to exactly its parameters. Every argument of the spine is lowered under
// the children transfer.
let isTailSelfCall (spine: CoreCallSpine) (state: CoreLoweringState) =
    match (stateTcoLoop(state), consumerRequestOf(state), unspanArgument(spine.root)) with
        | (Some(CoreTcoLoop { selfName = selfName, arity = arity, pendingCurried = pending }), ConsumerRequest { tailPosition = true }, ExprVar(name)) -> pending == 0 && name == selfName && coreListLength(spine.arguments) == arity
        | _ -> false

let tailCallPosition (state: CoreLoweringState) =
    match consumerRequestOf(state) with
        | ConsumerRequest { tailCall = tailCall } -> tailCall

// `tailCall` is the call expression's own tail position, read before the general call clears
// the consumer request.
// The callee name a diagnostic shows: a plain or module-qualified variable, nothing else.
let calleeDisplayName (root: Expr) =
    match unspanArgument(root) with
        | ExprVar(name) -> Some(name)
        | ExprQualifiedVar(moduleName, name) -> Some(moduleName + "." + name)
        | _ -> None

// The label the spine's callee is lowered to when it is a lambda written where it is applied: the
// callee is lowered first, under the next lambda number.
let appliedLambdaLabelOf (spine: CoreCallSpine) (state: CoreLoweringState) =
    match unspanArgument(spine.root) with
        | ExprLambda(_parameter, _body, _annotation) -> Some("lambda_" + Ashes.Text.fromInt(state.nextLambdaId))
        | _ -> None

let requestsCallArgument (request: ConsumerRequest) = request.callArgument

let callContextOf (spine: CoreCallSpine) (tailCall: Bool) (expected: Maybe(SemanticType)) (resultDiscarded: Bool) (state: CoreLoweringState) =
    (let selfCallee = isSelfCallee(spine)(state)
    in
        CoreCallContext(
            facts = calleeFactsOf(spine)(state),
            selfCallee = selfCallee,
            tailCall = isOwnLabelCallee(spine)(state) && tailCall,
            resultElementQuantified = calleeResultListElementQuantified(spine)(state),
            calleeName = calleeDisplayName(spine.root),
            argumentCount = coreListLength(spine.arguments),
            expectedResult = expected,
            calleeScheme = calleeSchemeOf(spine)(state),
            appliedLambdaLabel = appliedLambdaLabelOf(spine)(state),
            siteSpan = state.currentSpan,
            resultDiscarded = resultDiscarded,
            arguments = spine.arguments,
            resultIsCallArgument = state
            |> consumerRequestOf
            |> requestsCallArgument
        ))

// The consumed arguments whose parts the callee's result may still name: released spine-only unless
// the result is made to own what it keeps.
let recursive hasPreservedHeapArgument (consumed: List(CoreConsumedArgument)) (state: CoreLoweringState) =
    match consumed with
        | [] -> false
        | CoreConsumedArgument { preserveEscapedChildren = true, adoptionFlagTemp = flagTemp, semanticType = semanticType } :: rest ->
            flagTemp < 0 && isCopyTypeSemantic(resolveType(state)(semanticType)) == false || hasPreservedHeapArgument(rest)(state)
        | _ :: rest -> hasPreservedHeapArgument(rest)(state)

let recursive releaseWithAllParts (consumed: List(CoreConsumedArgument)) =
    match consumed with
        | [] -> []
        | (CoreConsumedArgument { preserveEscapedChildren = true, adoptionFlagTemp = flagTemp } as argument) :: rest ->
            (if flagTemp < 0
            then argument with preserveEscapedChildren = false
            else argument) :: releaseWithAllParts(rest)
        | argument :: rest -> argument :: releaseWithAllParts(rest)

// Stage 0's `resultCopyCopiesElements`: a list copy-out whose heads are strings or lists copies
// them, so its result shares nothing with the arguments already.
let resultCopyCopiesElements (semanticType: SemanticType) (state: CoreLoweringState) =
    match callCopyOutOf(semanticType)(state) with
        | Some(ListCallCopyOut(InlineListHead)) -> false
        | Some(ListCallCopyOut(_headCopy)) -> true
        | _ -> false

let resultAlreadySettled (context: CoreCallContext) (stage: CoreCallStage) (temp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    isRuntimeTemp(temp)(state) || stage.resultNormalized || stage.resultDeepCopied || context.selfCallee && (match stateSpecializingReuseLabel(state) with
        | Some(_label) -> true
        | None -> false) || contextResultRuntimeManaged(context)(semanticType)(state) || calleeVerifiedResultRuntimeManaged(context)(state) || resultCopyCopiesElements(semanticType)(state)

// The elements of a tuple result made owned before its arguments are released: each is a scalar
// or a value that normalizes into an owned one, and at least one is not a scalar.
let recursive tupleElementsOwnable (elements: List(SemanticType)) (anyHeap: Bool) (state: CoreLoweringState) =
    match elements with
        | [] -> anyHeap
        | element :: rest ->
            if resultSurvivesReset(element)(state)
            then tupleElementsOwnable(rest)(anyHeap)(state)
            else
                match ownedResultPlanOf(element)(state) with
                    | None -> false
                    | Some(_plan) -> tupleElementsOwnable(rest)(true)(state)

// Stage 0's `CanOwnResultBeforeArgumentRelease`: a tuple result is copied into an owned tuple, the
// copy retaining each reference-counted element it keeps.
let ownedResultBeforeReleasePlanOf (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemTuple(elements) ->
            if tupleElementsOwnable(elements)(false)(state)
            then argumentCopyPlanOf(semanticType)(state)
            else None
        | _ -> ownedResultPlanOf(semanticType)(state)

// Stage 0's `OwnResultBeforeArgumentRelease`: a consumed argument whose parts the callee's arena
// result may still name would be released spine-only, stranding every part the result did not
// keep. The result is made an owned reference-counted value first, retained or copied with its
// reference-counted parts retained, and the argument then goes with all its parts.
let ownResultBeforeArgumentRelease (context: CoreCallContext) (stage: CoreCallStage) =
    match stage with
        | CoreCallStage { lowered = LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } as lowered, consumedArguments = consumed } ->
            if resultAlreadySettled(context)(stage)(temp)(semanticType)(state) || hasPreservedHeapArgument(consumed)(state) == false
            then stage
            else
                match ownedResultBeforeReleasePlanOf(semanticType)(state) with
                    | None -> stage
                    | Some(plan) ->
                        match emitArgumentCopy(temp)(entryCopyPlanOf(semanticType)(plan)(state))(state) with
                            | (copied, ownedTemp) -> stage with lowered = (lowered with state = markRuntimeTemp(ownedTemp)(RuntimeNewlyProduced)(copied), temp = ownedTemp), consumedArguments = releaseWithAllParts(consumed)
        | _ -> stage

// A general call keeps its chain's intermediates in an arena window of its own, saved before the
// callee and arguments are lowered and closed after the last application.
// Stage 0's `LeaveCallArgumentRetainFrame`: once the call's result holds its own references (a
// scalar, a reference-counted result, or one of the contract's types), each retain an argument
// collected is released where it is present. The presence test joins the loop's pending argument
// flags, zeroed when the parameter is not placed on the reference-counted heap and the retain
// stays an identity.
let recursive emitCallArgumentRetainReleases (retains: List((Int, Int, SemanticType))) (state: CoreLoweringState) =
    match retains with
        | [] -> state
        | (temp, slot, semanticType) :: rest ->
            match freshTempRun(2)(state) with
                | FreshTemp { state = reserved, temp = zeroTemp } ->
                    match freshLabel("call_argument_retain_absent")(reserved) with
                        | FreshLabel { state = labelled, label = skipLabel } ->
                            labelled
                            |> emit(LoadConstInt(zeroTemp)(0))
                            |> emit(CmpIntNe(zeroTemp + 1)(temp)(zeroTemp))
                            |> emit(JumpIfFalse(zeroTemp + 1)(skipLabel))
                            |> emitOwnedValueChildDrop(temp)(semanticType)
                            |> emit(Label(skipLabel))
                            |> (given (released: CoreLoweringState) ->
                                if slot < 0
                                then released
                                else withStatePendingRuntimeArgumentFlags((zeroTemp + 1, slot) :: statePendingRuntimeArgumentFlags(released))(released))
                            |> emitCallArgumentRetainReleases(rest)

let callResultOwnsItself (temp: Int) (semanticType: SemanticType) (state: CoreLoweringState) = resultSurvivesReset(semanticType)(state) || isRuntimeTemp(temp)(state) || isGeneralRcValueType(semanticType)(state)

// The frame's entries for the pattern-owner duplicates an arena tuple of an argument stored: slot
// -2 when the callee's result may reach the argument, -3 when it may not.
let isTupleOwnerDuplicateEntry (slot: Int) = slot == -2 || slot == -3

let recursive callArgumentRecordRetainsOnly (retains: List((Int, Int, SemanticType))) =
    match retains with
        | [] -> []
        | (temp, slot, semanticType) :: rest ->
            if isTupleOwnerDuplicateEntry(slot)
            then callArgumentRecordRetainsOnly(rest)
            else (temp, slot, semanticType) :: callArgumentRecordRetainsOnly(rest)

// Stage 0's `LeaveCallArgumentRetainFrame` for the duplicates of `RecordCallArgumentTupleOwnerDuplicates`:
// each is released after the call unless the callee's result may reach the argument and does not
// own itself. The release is a marker promoted together with the duplicate, so a root that stays
// in the arena keeps neither.
let recursive emitCallArgumentTupleOwnerDuplicateReleases (retains: List((Int, Int, SemanticType))) (resultOwnsItself: Bool) (state: CoreLoweringState) =
    match retains with
        | [] -> state
        | (temp, slot, semanticType) :: rest ->
            if (slot == -3 || slot == -2 && resultOwnsItself) && !containsInt(temp)(stateReleasedPatternOwnerDuplicates(state))
            then
                state
                |> emit(RcDrop(temp)(patternOwnerTypeName(resolveType(state)(semanticType))(state))(-1)(false)(false)(None))
                |> withStateReleasedPatternOwnerDuplicates(temp :: stateReleasedPatternOwnerDuplicates(state))
                |> emitCallArgumentTupleOwnerDuplicateReleases(rest)(resultOwnsItself)
            else emitCallArgumentTupleOwnerDuplicateReleases(rest)(resultOwnsItself)(state)

let leaveCallArgumentRetainFrame (outerRetains: List((Int, Int, SemanticType))) (outerCollecting: Bool) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            let released =
                match stateCallArgumentRecordRetains(state) with
                    | [] -> state
                    | retains ->
                        let resultOwnsItself = callResultOwnsItself(temp)(semanticType)(state)
                        in
                            state
                            |> emitCallArgumentTupleOwnerDuplicateReleases(reverse(retains))(resultOwnsItself)
                            |> (given (duplicatesReleased: CoreLoweringState) ->
                                match callArgumentRecordRetainsOnly(retains) with
                                    | [] -> duplicatesReleased
                                    | recordRetains ->
                                        if resultOwnsItself
                                        then
                                            emitCallArgumentRetainReleases(reverse(recordRetains))(duplicatesReleased)
                                        else duplicatesReleased)
            in lowered with state = withCallArgumentRetainFrame(outerRetains)(outerCollecting)(released)
        | LoweredCoreValue { state = state } -> lowered with state = withCallArgumentRetainFrame(outerRetains)(outerCollecting)(state)

// Stage 0's `ResolveCallPhantomResultLayout`: the layout of a call's result, with the phantoms of
// the callee's scheme still unresolved at this call read as `Never`. The instantiated type is
// walked beside the scheme's: a variable the call resolved keeps its resolution, a layout of its
// own. A call of the function being lowered reads them the way the function's return will.
let recursive substituteInstantiatedPhantoms (phantoms: List(Int)) (schemeType: SemanticType) (instantiated: SemanticType) =
    match (schemeType, instantiated) with
        | (SemVariable(id), SemVariable(_other)) ->
            if containsInt(id)(phantoms)
            then SemNever
            else instantiated
        | (SemList(schemeElement), SemList(element)) ->
            element
            |> substituteInstantiatedPhantoms(phantoms)(schemeElement)
            |> SemList
        | (SemTuple(schemeElements), SemTuple(elements)) ->
            if coreListLength(schemeElements) == coreListLength(elements)
            then
                elements
                |> substituteInstantiatedPhantomsIn(phantoms)(schemeElements)
                |> SemTuple
            else instantiated
        | (SemNamed(schemeId, schemeName, schemeArguments), SemNamed(symbolId, name, arguments)) ->
            if schemeId == symbolId && schemeName == name && coreListLength(schemeArguments) == coreListLength(arguments)
            then
                arguments
                |> substituteInstantiatedPhantomsIn(phantoms)(schemeArguments)
                |> SemNamed(symbolId)(name)
            else instantiated
        | _ -> instantiated
and substituteInstantiatedPhantomsIn (phantoms: List(Int)) (schemeTypes: List(SemanticType)) (types: List(SemanticType)) =
    match (schemeTypes, types) with
        | (schemeHead :: schemeRest, head :: rest) -> substituteInstantiatedPhantoms(phantoms)(schemeHead)(head) :: substituteInstantiatedPhantomsIn(phantoms)(schemeRest)(rest)
        | _ -> []

// The scheme's result after `count` applications, beside the variables its parameters name.
let recursive schemeResultAfter (count: Int) (body: SemanticType) (held: List(Int)) =
    if count == 0
    then Some((held, body))
    else
        match body with
            | SemFunction(argument, result, _row) ->
                held
                |> joinVariables(freeTypeVariables(argument))
                |> schemeResultAfter(count - 1)(result)
            | _ -> None

let recursive quantifiesVariable (id: Int) (quantified: List((Int, Str))) =
    match quantified with
        | [] -> false
        | (candidate, _name) :: rest -> candidate == id || quantifiesVariable(id)(rest)

let recursive quantifiedOnly (quantified: List((Int, Str))) (variables: List(Int)) =
    match variables with
        | [] -> []
        | id :: rest ->
            if quantifiesVariable(id)(quantified)
            then id :: quantifiedOnly(quantified)(rest)
            else quantifiedOnly(quantified)(rest)

let recursive typesVariables (types: List(SemanticType)) (held: List(Int)) =
    match types with
        | [] -> held
        | semanticType :: rest ->
            held
            |> joinVariables(freeTypeVariables(semanticType))
            |> typesVariables(rest)

let recursive constraintVariables (constraints: List(TraitConstraint)) (held: List(Int)) =
    match constraints with
        | [] -> held
        | TraitConstraint { typeArguments = arguments } :: rest ->
            held
            |> typesVariables(arguments)
            |> constraintVariables(rest)

let substituteSchemePhantoms (scheme: TypeScheme) (argumentCount: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match schemeResultAfter(argumentCount)(resolveType(state)(scheme.body))([]) with
        | None -> semanticType
        | Some((held, schemeResult)) ->
            match schemeResult
            |> freeTypeVariables
            |> quantifiedOnly(scheme.quantified)
            |> variablesNotHeld(constraintVariables(scheme.constraints)(held)) with
                | [] -> semanticType
                | phantoms ->
                    contractLayoutOrSelf(semanticType
                    |> resolveType(state)
                    |> substituteInstantiatedPhantoms(phantoms)(schemeResult))(semanticType)(state)

let resolveCallPhantomResultLayout (context: CoreCallContext) (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemFunction(_argument, _result, _row) -> semanticType
        | resolved ->
            if !containsUnresolvedLayout(resolved)(state)
            then semanticType
            else
                if context.selfCallee
                then
                    match context.calleeName with
                        | Some(name) ->
                            match lookupBinding(name)(state.bindings) with
                                | Some(CoreBinding { location = CoreSelf(selfLabel, _size) }) -> resolvePhantomResultLayout(selfLabel)(semanticType)(state)
                                | _ -> semanticType
                        | None -> semanticType
                else
                    match context.calleeScheme with
                        | Some(scheme) -> substituteSchemePhantoms(scheme)(context.argumentCount)(semanticType)(state)
                        | None -> semanticType

let withCallResultLayout (context: CoreCallContext) (stage: CoreCallStage) =
    match stage with
        | CoreCallStage { lowered = LoweredCoreValue { state = state, semanticType = semanticType, error = None } as lowered } -> stage with lowered = (lowered with semanticType = resolveCallPhantomResultLayout(context)(semanticType)(state))
        | _ -> stage

// The call's result keeps the type the spine gave it once its window is closed and its arguments
// released, as stage 0 returns the call's own type after deciding the result's layout.
let withCallResultType (applied: CoreCallStage) (lowered: LoweredCoreValue) =
    match (applied, lowered) with
        | (CoreCallStage { lowered = LoweredCoreValue { semanticType = semanticType, error = None } }, LoweredCoreValue { error = None }) -> lowered with semanticType = semanticType
        | _ -> lowered

// Stage 0's `ContainsUnsettledVariable`: a variable anywhere in the type, also one its layout does not
// depend on.
let holdsUnsettledVariable (semanticType: SemanticType) (state: CoreLoweringState) =
    match semanticType
    |> resolveType(state)
    |> freeTypeVariables with
        | [] -> false
        | _variables -> true

let recordOrPinSiblingCallResult (key: Str) (semanticType: SemanticType) (state: CoreLoweringState) =
    match state
    |> stateProvenSiblingCallResults
    |> Ashes.Collection.Map.getStr(key) with
        | None -> withStatePendingSiblingCallResults((key, semanticType) :: statePendingSiblingCallResults(state))(state)
        | Some(proven) ->
            match bindType(semanticType)(proven)(state) with
                | (pinned, None) -> pinned
                | (_failed, Some(_error)) -> state

// Stage 0's `RecordOrPinCallResultType` for a call to a later member of the recursive group, whose
// result type is still open when the call is lowered: the group's first lowering records the site,
// and the lowering that follows it pins the type the whole group settled there, so the call and the
// caller's own return decide the result's ownership against the type stage 0's discovery pass
// pins.
let pinSiblingCallResult (context: CoreCallContext) (stage: CoreCallStage) =
    match stage with
        | CoreCallStage { lowered = LoweredCoreValue { state = state, semanticType = semanticType, error = None } as lowered } ->
            match siblingCallSiteKey(context) with
                | Some(key) ->
                    if siblingResultOpen(context)(semanticType)(state) && holdsUnsettledVariable(semanticType)(state)
                    then stage with lowered = (lowered with state = recordOrPinSiblingCallResult(key)(semanticType)(state))
                    else stage
                | None -> stage
        | _ -> stage

let prepareCallAdmissibility (stage: CoreCallStage) =
    match stage with
        | CoreCallStage { lowered = LoweredCoreValue { state = state, semanticType = semanticType, error = None } as lowered } ->
            match callCopyOutOfIndexed(semanticType)(state) with
                | (Some(_copyOut), queried) -> stage with lowered = (lowered with state = queried)
                | (None, queried) -> stage with lowered = (lowered with state = prepareGeneralRcAdmissibility(semanticType)(queried))
        | _ -> stage

let lowerCallInFrame (spine: CoreCallSpine) function argument expected (site: Maybe(CoreMismatchSite)) (transfers: Bool) (tailCall: Bool) (resultDiscarded: Bool) lower state =
    match state
    |> ensureResultRcEligibility
    |> openArenaBracket with
        | ArenaBracket { bracketState = opened, bracketCursorSlot = cursorSlot, bracketEndSlot = endSlot } ->
            opened
            |> callContextOf(spine)(tailCall)(expected)(resultDiscarded)
            |> (given (context: CoreCallContext) ->
                match opened |> lowerCallSpineStage(function)(argument)(context)(expected)(site)(1)(transfers)(lower) with
                    | applied ->
                        match applied
                        |> pinSiblingCallResult(context)
                        |> withCallResultLayout(context)
                        |> prepareCallAdmissibility
                        |> markCallSpineResult(context) with
                            | stage ->
                                stage
                                |> closeCallWindow(context)(cursorSlot)(endSlot)
                                |> ownResultBeforeArgumentRelease(context)
                                |> releaseCallSpineArguments(context)(elementCopyingFlagOf(context)(stage))
                                |> withCallResultType(applied)
                                |> noteTakenOverCallResult(context))

let restoreCallArgumentLabelStart (start: Int) (active: Bool) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state } ->
            let group = state.tcoState
            in lowered with state = (state with tcoState = (group with callArgumentLabelStart = start, callArgumentFrameActive = active))

// Stage 0's `NoteDiscardedResultArgument`: a lambda ignoring its parameter applied to a call.
let noteDiscardedResultArgument (spine: CoreCallSpine) (state: CoreLoweringState) =
    match (unspanArgument(spine.root), spine.arguments) with
        | (ExprLambda("_", _body, _annotation), ExprAt(span, inner) :: []) ->
            match unspanArgument(inner) with
                | ExprCall(_function, _argument, _layout, _arguments) ->
                    let group = state.programState
                    in state with programState = (group with discardedResultSpans = span :: group.discardedResultSpans)
                | _ -> state
        | _ -> state

let recursive withoutSpan (span: TextSpan) (spans: List(TextSpan)) =
    match spans with
        | [] -> []
        | candidate :: rest ->
            if candidate == span
            then rest
            else candidate :: withoutSpan(span)(rest)

// Whether the call lowered under the current span is a noted discarded one, which it takes.
let takeDiscardedResultSite (state: CoreLoweringState) =
    (let group = state.programState
    in
        match state.currentSpan with
            | Some(span) ->
                if spanListed(span)(group.discardedResultSpans)
                then (true, (state with programState = (group with discardedResultSpans = withoutSpan(span)(group.discardedResultSpans))))
                else (false, state)
            | None -> (false, state))

// A call opens its own frame of argument retains; the enclosing call's is restored after it.
let lowerCall (spine: CoreCallSpine) function argument expected (site: Maybe(CoreMismatchSite)) (transfers: Bool) (tailCall: Bool) lower state =
    match state
    |> noteDiscardedResultArgument(spine)
    |> takeDiscardedResultSite with
        | (resultDiscarded, noted) ->
            let framed = noted with tcoState = (noted.tcoState with callArgumentFrameActive = true)
            in
                framed
                |> withCallArgumentRetainFrame([])(false)
                |> lowerCallInFrame(spine)(function)(argument)(expected)(site)(transfers)(tailCall)(resultDiscarded)(lower)
                |> leaveCallArgumentRetainFrame(stateCallArgumentRecordRetains(state))(stateCollectingCallArgumentRetains(state))
                |> restoreCallArgumentLabelStart(stateCallArgumentLabelStart(state))(stateCallArgumentFrameActive(state))

let failedIfPlan state error =
    CoreIfPlan(
        state = state,
        resultSlot = -1,
        elseLabel = "",
        endLabel = "",
        error = Some(error)
    )

let finishIfPlan conditionTemp elseLabel endLabel fresh =
    match fresh with
        | FreshLocal { state = state, local = resultSlot } ->
            CoreIfPlan(
                state = emit(JumpIfFalse(conditionTemp)(elseLabel))(state),
                resultSlot = resultSlot,
                elseLabel = elseLabel,
                endLabel = endLabel,
                error = None
            )

let prepareIfEndLabel conditionTemp elseLabel fresh =
    match fresh with
        | FreshLabel { state = state, label = endLabel } ->
            state
            |> freshLocal
            |> finishIfPlan(conditionTemp)(elseLabel)(endLabel)

let prepareIfElseLabel conditionTemp fresh =
    match fresh with
        | FreshLabel { state = state, label = elseLabel } ->
            state
            |> freshLabel("endif")
            |> prepareIfEndLabel(conditionTemp)(elseLabel)

let prepareTypedIfPlan conditionTemp typed =
    match typed with
        | (failedState, Some(error)) -> failedIfPlan(failedState)(error)
        | (typedState, None) ->
            typedState
            |> freshLabel("else")
            |> prepareIfElseLabel(conditionTemp)

let prepareIfPlan loweredCondition =
    match loweredCondition with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failedIfPlan(failedState)(error)
        | LoweredCoreValue { state = conditionState, temp = conditionTemp, semanticType = conditionType, error = None } ->
            conditionState
            |> bindType(SemBool)(conditionType)
            |> prepareTypedIfPlan(conditionTemp)

let resultTypeIsList (resultType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(resultType) with
        | SemList(_element) -> true
        | _ -> false

let tempIsNewlyProduced (temp: Int) (state: CoreLoweringState) =
    match runtimeTempStateOf(temp)(state) with
        | Some(RuntimeNewlyProduced) -> true
        | _ -> false

// A `Str` loop parameter, which stage 0 places on the reference-counted heap at the loop entry by
// its type alone, where this lowering admits one only through the affine append analysis. An arm
// that hands it on borrows from the parameter there, so it is not normalized here either.
let isStringLoopParameter (name: Str) (state: CoreLoweringState) =
    match stateTcoLoopFrame(state) with
        | None -> false
        | Some(frame) ->
            match parameterSlotOfName(name)(frame)(state) with
                | None -> false
                | Some(slot) ->
                    match slotResolvedType(slot)(state.bindings)(state) with
                        | Some(SemString) -> true
                        | _ -> false

// Stage 0's `BorrowsLoopParameter`: an arm handing on a loop parameter the frame places on the
// reference-counted heap, or a pattern binding taken out of one. Its value is reference-counted,
// but the reference is the parameter's.
let armBorrowsLoopParameterOf (body: Expr) (state: CoreLoweringState) =
    match tailForwardedVariable(body) with
        | Some(name) -> loopParameterPlacedUnblocked(name)(state) || isStringLoopParameter(name)(state)
        | None -> false

// Stage 0's `ConstructsFreshCell`: an arm that builds its value itself (a cons, a literal, a
// constructor application, though not a record literal, which stage 0 does not count as one). A
// reference-counted value it hands on is fresh and its own, so it is kept rather than retained.
let recursive constructsFreshCell (body: Expr) (state: CoreLoweringState) =
    match body with
        | ExprAt(_span, inner) -> constructsFreshCell(inner)(state)
        | ExprNode(_node, inner) -> constructsFreshCell(inner)(state)
        | ExprLet(_name, _value, nested, _parameters, _annotation, _requirements) -> constructsFreshCell(nested)(state)
        | ExprCons(_head, _tail) -> true
        | ExprTuple(_elements) -> true
        | ExprList(_first :: _rest, _isMultiline) -> true
        | ExprRecord(_name, _fields, _isMultiline) -> false
        | other -> isConstructorExpression(other)(state)

// Whether a temp is defined by a retain, the duplicate a branch takes of the pattern binding it
// returns.
let recursive definedByRetain (temp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = RcDup(target, _source, _runtimeManaged, _mayBeEmpty) } :: rest ->
            if target == temp
            then true
            else definedByRetain(temp)(rest)
        | IrInstruction { instruction = LoadLocal(target, _slot) } :: rest ->
            if target == temp
            then false
            else definedByRetain(temp)(rest)
        | _ :: rest -> definedByRetain(temp)(rest)

// What a branch stored into its join slot: a reference-counted value when its final temp is one,
// or when it is the empty list literal of a list-typed join; newly produced only when the temp
// itself was freshly produced. A branch returning a pattern binding retained it before the
// binding's scope releases it (stage 0's `IsRetainedPatternOwnerArm`) when the binding's root
// parameter is placed on the reference-counted heap, at the loop's entry or by its back edge: the
// arm hands the join a reference-counted value of its own, not one the parameter owns. Under an
// arena root the retain stays an identity marker and the arm a plain read. The empty list literal owns no
// cell, so it takes nothing from the join's freshness: a join of it and fresh lists is a fresh
// list its `let` owns, as stage 0's `TrackLetOwnership` owns any reference-counted join that
// borrows nothing.
// Whether the branch returns a pattern binding as it is: its retain is an identity marker until
// the root parameter is placed, so it owns nothing of its own under an arena root.
let forwardsPatternOwner (body: Expr) (state: CoreLoweringState) =
    match tailForwardedVariable(body) with
        | Some(name) ->
            match patternOwnerBinding(name)(state) with
                | Some(_fact) -> true
                | None -> false
        | None -> false

let matchArmResultWith body (finalTemp: Int) (resultType: SemanticType) (borrowsParameter: Bool) (retainedParameterPart: Bool) (state: CoreLoweringState) =
    (let emptyListArm = branchIsEmptyListLiteral(body) && resultTypeIsList(resultType)(state)
    in
        MatchArmResult(
            armRuntimeManaged = isRuntimeTemp(finalTemp)(state) || retainedParameterPart || emptyListArm,
            armNewlyProduced = tempIsNewlyProduced(finalTemp)(state) || retainedParameterPart || emptyListArm,
            armRetainedOwner = containsInt(finalTemp)(statePatternOwnerResultTemps(state)) && (retainedParameterPart || forwardsPatternOwner(body)(state) == false) || armNeverReachesJoin(finalTemp)(state),
            armOwned = isRuntimeTemp(finalTemp)(state) || retainedParameterPart,
            armBorrowsLoopParameter = borrowsParameter && !retainedParameterPart,
            armFreshCell = constructsFreshCell(body)(state),
            armConsesOntoSelfResult = isConsChainOntoSelfCall(body)(state),
            armJoinsSelfConsingArms = nestedJoinConsesOntoSelfResult(body)(state),
            armStoreTemp = if armNeverReachesJoin(finalTemp)(state)
            then -1
            else finalTemp
        ))

// Whether the pattern binding an arm returns has a root loop parameter placed on the
// reference-counted heap, at the loop's entry or by its back edge (stage 0's
// `IsRuntimeManagedPatternOwnerOfTcoParameter`).
let armPatternRootIsPlaced (body: Expr) (state: CoreLoweringState) =
    match (tailForwardedVariable(body), stateTcoLoopFrame(state), stateTcoLoop(state)) with
        | (Some(name), Some(frame), Some(loop)) ->
            match patternBindingRootSlot(name)(frame)(state) with
                | Some(rootSlot) -> loopParameterPlacedUnblocked(name)(state) || rootPlacedAtBackEdge(rootSlot)(frame)(loop)(state)
                | None -> false
        | _ -> false

let matchArmResultOf body (finalTemp: Int) (resultType: SemanticType) (state: CoreLoweringState) =
    (let borrowsParameter = armBorrowsLoopParameterOf(body)(state)
    in
        matchArmResultWith(body)(finalTemp)(resultType)(borrowsParameter)((borrowsParameter || armPatternRootIsPlaced(body)(state)) && containsInt(finalTemp)(statePatternOwnerResultTemps(state)) && definedByRetain(finalTemp)(state.reversedInstructions))(state))

let unknownArmResult = MatchArmResult(armRuntimeManaged = false, armNewlyProduced = false, armRetainedOwner = false, armOwned = false, armStoreTemp = -1, armBorrowsLoopParameter = false, armFreshCell = false, armConsesOntoSelfResult = false, armJoinsSelfConsingArms = false)

// Stage 0's `TransferDirectRuntimeManagedBranchResult`: an `if` branch that returns a pattern
// owner takes a duplicate of its read, an identity marker until the loop's finalize places the
// owner, since the owner's own release follows the whole `if`; the duplicate is the branch's
// retained result. A binding of a copy type owns nothing and keeps its plain read.
let transferBranchOwnerResult (branch: Expr) (temp: Int) (state: CoreLoweringState) =
    match tailForwardedVariable(branch) with
        | None -> (state, temp)
        | Some(name) ->
            match (patternOwnerBinding(name)(state), lookupBinding(name)(state.bindings)) with
                | (Some(_fact), Some(CoreBinding { scheme = TypeScheme { body = bindingType } })) ->
                    if resultSurvivesReset(resolveType(state)(bindingType))(state)
                    then (state, temp)
                    else
                        match duplicatePatternOwnerTemp(ExprVar(name))(temp)(state) with
                            | (duplicated, duplicate) -> (withStatePatternOwnerResultTemps(duplicate :: statePatternOwnerResultTemps(duplicated))(duplicated), duplicate)
                | _ -> (state, temp)

// Stage 0's `TransferVariableRuntimeManagedMatchResult` for an `if` branch: a branch that returns
// a `let` binding still owning its reference-counted value takes a reference of its own, since
// the binding's release follows the whole `if`. A pattern owner is handled above, and a binding
// of a copy type owns nothing.
let transferBranchLetOwnerResult (branch: Expr) (temp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match tailForwardedVariable(branch) with
        | None -> (state, temp)
        | Some(name) ->
            match (patternOwnerBinding(name)(state), liveRuntimeOwnerSlot(name)(state)) with
                | (None, Some(_slot)) ->
                    if resultSurvivesReset(resolveType(state)(semanticType))(state)
                    then (state, temp)
                    else
                        match emitTransferRetain(temp)(semanticType)(state) with
                            | LoweredCoreValue { state = retained, temp = duplicate } -> (retained, duplicate)
                | _ -> (state, temp)

// An `if` branch's result as the join stores it: a pattern owner's duplicate, a reference of its
// own to a `let` owner when the `if` is the function's result (`resultEscapes`), or the value as it
// was lowered. An `if` whose value stays inside the function (a record field, an operand) stores
// the plain read: the aggregate or operation that consumes it takes its own reference.
let transferIfBranchResult (resultEscapes: Bool) (branch: Expr) (temp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match transferBranchOwnerResult(branch)(temp)(state) with
        | (duplicated, duplicate) ->
            if duplicate != temp
            then (duplicated, duplicate)
            else
                if resultEscapes
                then transferBranchLetOwnerResult(branch)(temp)(semanticType)(duplicated)
                else (duplicated, temp)

// Stage 0's `MarkRuntimeManagedMatchResult` and `MarkUniformRuntimeManagedResult`: a join's
// reloaded value is a reference-counted value when every branch stored one, and newly produced
// only when every branch's was, so a consuming read (a concat operand, a byte length, a print)
// releases it as it would have released each branch's value without the join. One borrowed or
// unknown branch makes the join a transferred value: the merged value may then be a live
// binding's, and releasing it would free memory still in use.
// A join whose every arm left a reference-counted value carries that representation itself; the
// walk would instead read the shared result slot, which the unreachable no-match default wrote
// last.
let recordJoinRepresentation (resultTemp: Int) (arms: List(MatchArmResult)) (state: CoreLoweringState) =
    if joinIsRuntimeManaged(arms)
    then recordDecidedRepresentation(resultTemp)(false)(true)(state)
    else state

// The instructions with the arm's store of `source` into the join slot replaced by `replacement`.
// Both lists run most recent first, as the function's own instructions do.
let recursive replaceJoinArmStore (slot: Int) (source: Int) (replacement: List(IrInstruction)) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> []
        | (IrInstruction { instruction = StoreLocal(storeSlot, storeSource) } as instruction) :: rest ->
            if storeSlot == slot && storeSource == source
            then append(replacement)(rest)
            else instruction :: replaceJoinArmStore(slot)(source)(replacement)(rest)
        | instruction :: rest -> instruction :: replaceJoinArmStore(slot)(source)(replacement)(rest)

// How many instructions precede the arm's store of `source` into the join slot, `-1` when the
// store is not there.
let recursive joinArmStoreIndex (slot: Int) (source: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> -1
        | IrInstruction { instruction = StoreLocal(storeSlot, storeSource) } :: rest ->
            if storeSlot == slot && storeSource == source
            then length(rest)
            else joinArmStoreIndex(slot)(source)(rest)
        | _ :: rest -> joinArmStoreIndex(slot)(source)(rest)

// Stage 0's `ShiftRecordedInstructionIndices`: a pattern owner's retain is spliced in later at the
// instruction count recorded for it, so a site past the arm's store moves with the instructions
// the normalization put in the store's place.
let recursive shiftPatternOwnerSites (index: Int) (delta: Int) (sites: List(PatternOwnerSite)) =
    match sites with
        | [] -> []
        | site :: rest ->
            (if site.siteInsertCount > index
            then site with siteInsertCount = site.siteInsertCount + delta
            else site) :: shiftPatternOwnerSites(index)(delta)(rest)

// The slot `reserveArmCellHeadSlot` registered for the arm cell `cell`: the zero store right behind
// the cell's tail store, most recent instruction first.
let armCellHeadSlotAt (cell: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | IrInstruction { instruction = StoreLocal(slot, zeroTemp) } :: IrInstruction { instruction = LoadConstInt(loaded, 0) } :: IrInstruction { instruction = StoreMemOffset(basePtr, 8, _tail) } :: _rest ->
            if loaded == zeroTemp && basePtr == cell
            then Some(slot)
            else None
        | _ -> None

let recursive armCellHeadSlotOf (cell: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> None
        | _instruction :: rest ->
            match armCellHeadSlotAt(cell)(instructions) with
                | Some(slot) -> Some(slot)
                | None -> armCellHeadSlotOf(cell)(rest)

let recursive armCellStoredHead (cell: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> None
        | IrInstruction { instruction = StoreMemOffset(basePtr, 0, source) } :: rest ->
            if basePtr == cell
            then Some(source)
            else armCellStoredHead(cell)(rest)
        | _instruction :: rest -> armCellStoredHead(cell)(rest)

// The write of an arm cell's head duplicate, run only when the cell was copied: a cell that is
// reference-counted at run time is shared by the join's copy as it is and keeps its head's
// duplicate (stage 0's guard in `WriteArmCellHeadSlot`).
let emitWhenArmCellCopied (cell: Int) write (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = tested, temp = referenceCountedTemp } ->
            match freshLabel("arm_cell_head_copied")(tested) with
                | FreshLabel { state = writeLabelled, label = writeLabel } ->
                    match freshLabel("arm_cell_head_shared")(writeLabelled) with
                        | FreshLabel { state = labelled, label = doneLabel } ->
                            labelled
                            |> emit(IsReferenceCounted(referenceCountedTemp)(cell))
                            |> emit(JumpIfFalse(referenceCountedTemp)(writeLabel))
                            |> emit(Jump(doneLabel))
                            |> emit(Label(writeLabel))
                            |> write
                            |> emit(Label(doneLabel))

// Stage 0's `WriteArmCellHeadSlot`: right behind the join's copy of an arm's own arena cell, the
// cell's head duplicate goes into the slot registered at the cons. A duplicate still a pattern
// owner's marker is written as zero and handed to the cell marker slots, which store it once the
// loop's finalize promotes it to a real retain; an identity marker retained nothing and leaves the
// slot zero.
let writeArmCellHeadSlot (arm: MatchArmResult) (original: CoreLoweringState) (state: CoreLoweringState) =
    if arm.armConsesOntoSelfResult
    then
        match (armCellHeadSlotOf(arm.armStoreTemp)(original.reversedInstructions), armCellStoredHead(arm.armStoreTemp)(original.reversedInstructions)) with
            | (Some(slot), Some(head)) ->
                match definitionOfTemp(head)(original.reversedInstructions) with
                    | Some(RcDup(_target, _source, true, _mayBeEmpty)) ->
                        emitWhenArmCellCopied(arm.armStoreTemp)(head
                        |> StoreLocal(slot)
                        |> emit)(state)
                    | Some(RcDup(_target, _source, false, _mayBeEmpty)) ->
                        match freshTemp(state) with
                            | FreshTemp { state = withTemp, temp = zeroTemp } ->
                                withTemp
                                |> emitWhenArmCellCopied(arm.armStoreTemp)(given (guarded: CoreLoweringState) ->
                                    guarded
                                    |> emit(LoadConstInt(zeroTemp)(0))
                                    |> emit(StoreLocal(slot)(zeroTemp)))
                                |> (given (stored: CoreLoweringState) -> withStateCellMarkerSlots((slot, head, zeroTemp) :: stateCellMarkerSlots(stored))(stored))
                    | _ -> state
            | _ -> state
    else state

// An arm consing arena cells onto the function's own recursive result copies them and takes that
// owned result over as their tail (stage 0's `NormalizeJoinArmStoreMovingTail`).
let emitJoinArmNormalization (consedElementPlan: Maybe(ArgumentCopyPlan)) (ownedSlot: Int) (arm: MatchArmResult) (plan: ArgumentCopyPlan) (state: CoreLoweringState) =
    match (arm.armConsesOntoSelfResult, consedElementPlan) with
        | (true, Some(elementPlan)) -> emitListDeepCopyWith(true)(ownedSlot != -2)(ownedSlot)(arm.armStoreTemp)(elementPlan)(state)
        | _ ->
            if arm.armFreshCell
            then emitOwnedResultOrCopy(arm.armStoreTemp)(plan)(state)
            else emitGuardedDeepCopy(arm.armStoreTemp)(plan)(state)

// Stage 0's `NormalizeJoinArmStore`: the arm's store becomes a normalization of its value (a
// reference when it is reference-counted, a copy otherwise) followed by the store of the
// normalized value, emitted into a buffer and spliced where the store was.
let normalizeJoinArmStore (slot: Int) (consedElementPlan: Maybe(ArgumentCopyPlan)) (plan: ArgumentCopyPlan) (arm: MatchArmResult) (state: CoreLoweringState) =
    (let emptied = state with reversedInstructions = []
    in
        match emitJoinArmNormalization(consedElementPlan)(consChainTailOwnedSlot(arm.armStoreTemp)(state))(arm)(plan)(emptied) with
            | (copied, normalizedTemp) ->
                let buffered =
                    copied
                    |> writeArmCellHeadSlot(arm)(state)
                    |> emit(StoreLocal(slot)(normalizedTemp))
                in
                    let index = joinArmStoreIndex(slot)(arm.armStoreTemp)(state.reversedInstructions)
                    in
                        (buffered with reversedInstructions = replaceJoinArmStore(slot)(arm.armStoreTemp)(buffered.reversedInstructions)(state.reversedInstructions))
                        |> markRuntimeTemp(normalizedTemp)(RuntimeNewlyProduced)
                        |> (given (spliced: CoreLoweringState) ->
                            if index < 0
                            then spliced
                            else
                                withStatePatternOwnerSites(spliced
                                |> statePatternOwnerSites
                                |> shiftPatternOwnerSites(index)(length(buffered.reversedInstructions) - 1))(spliced)))

// Stage 0's `RecordJoinCopiedArenaCell`: a fresh arena cell a loop body hands to a join whose
// normalization copies it. The copy takes its own reference to every reference-counted child, and
// the arena cell never releases the retains its construction took of loop-parameter reads, so they
// are withdrawn like a borrowed scrutinee's once the loop's placements are final. An arm whose
// value is itself a join (an `if` whose one arm builds the cell) hands over the cells that join
// carries.
let carriesJoinedArenaCell (temp: Int) (frame: CoreTcoLoopFrame) (state: CoreLoweringState) =
    match definitionOfTemp(temp)(state.reversedInstructions) with
        | Some(LoadLocal(_target, _slot)) ->
            length(arenaCellsCarrying([temp])([])(frame.parameterSlots)(state.reversedInstructions)([])) > 0
        | _ -> false

let recordJoinCopiedArenaCell (consedElementPlan: Maybe(ArgumentCopyPlan)) (arm: MatchArmResult) (state: CoreLoweringState) =
    match (stateTcoLoopFrame(state), arm.armConsesOntoSelfResult, consedElementPlan) with
        | (None, _, _) -> state
        | (Some(_frame), true, Some(_elementPlan)) -> state
        | (Some(frame), _, _) ->
            if isArenaCell(arm.armStoreTemp)(state.reversedInstructions) || carriesJoinedArenaCell(arm.armStoreTemp)(frame)(state)
            then withStateBorrowedScrutineeCells(arm.armStoreTemp :: stateBorrowedScrutineeCells(state))(state)
            else state

let recursive normalizeUnownedJoinArms (slot: Int) (consedElementPlan: Maybe(ArgumentCopyPlan)) (plan: ArgumentCopyPlan) (arms: List(MatchArmResult)) (state: CoreLoweringState) =
    match arms with
        | [] -> (state, [])
        | arm :: rest ->
            if arm.armStoreTemp < 0 || (arm.armOwned || arm.armRetainedOwner) && !arm.armBorrowsLoopParameter
            then
                match normalizeUnownedJoinArms(slot)(consedElementPlan)(plan)(rest)(state) with
                    | (normalized, restArms) -> (normalized, arm :: restArms)
            else
                match state
                |> normalizeJoinArmStore(slot)(consedElementPlan)(plan)(arm)
                |> recordJoinCopiedArenaCell(consedElementPlan)(arm)
                |> normalizeUnownedJoinArms(slot)(consedElementPlan)(plan)(rest) with
                    | (normalized, restArms) -> (normalized, MatchArmResult(armRuntimeManaged = true, armNewlyProduced = true, armRetainedOwner = arm.armRetainedOwner, armOwned = true, armStoreTemp = arm.armStoreTemp, armBorrowsLoopParameter = false, armFreshCell = arm.armFreshCell, armConsesOntoSelfResult = false, armJoinsSelfConsingArms = arm.armJoinsSelfConsingArms) :: restArms)

// Stage 0's `OwnedArmBesideBorrowedParameter`: an owned arm beside one borrowing a loop parameter. The
// join is reference-counted on every path but owned on only some, so the borrowing arm takes a
// reference of its own; a loop parameter the join hands to the next iteration is released as the
// predecessor at the back edge.
let recursive anyOwnedJoinArm (arms: List(MatchArmResult)) =
    match arms with
        | [] -> false
        | arm :: rest -> arm.armStoreTemp >= 0 && (arm.armOwned || arm.armRetainedOwner) && !arm.armBorrowsLoopParameter || anyOwnedJoinArm(rest)

let recursive anyParameterBorrowingJoinArm (arms: List(MatchArmResult)) =
    match arms with
        | [] -> false
        | arm :: rest -> arm.armStoreTemp >= 0 && arm.armBorrowsLoopParameter || anyParameterBorrowingJoinArm(rest)

let ownedArmBesideBorrowedParameter (arms: List(MatchArmResult)) = anyOwnedJoinArm(arms) && anyParameterBorrowingJoinArm(arms)

// The arms that reach the join, and how many of them hand it a reference-counted value: one of
// their own, or a reference a pattern owner retained (stage 0's `IsReferenceCountedJoinArm`).
let recursive joinArmTally (arms: List(MatchArmResult)) (reaching: Int) (owned: Int) =
    match arms with
        | [] -> (reaching, owned)
        | arm :: rest ->
            if arm.armStoreTemp < 0
            then joinArmTally(rest)(reaching)(owned)
            else
                if arm.armOwned || arm.armRetainedOwner || arm.armBorrowsLoopParameter
                then joinArmTally(rest)(reaching + 1)(owned + 1)
                else joinArmTally(rest)(reaching + 1)(owned)

// Stage 0's `IsNormalizableJoinTuple`: a tuple element a join normalization can own, a scalar or
// a value that normalizes into an owned one.
let recursive joinTupleElementsNormalize (elements: List(SemanticType)) (state: CoreLoweringState) =
    match elements with
        | [] -> true
        | element :: rest ->
            (resultSurvivesReset(element)(state) || (match ownedResultPlanOf(element)(state) with
                | None -> false
                | Some(ScalarArgumentCopy) -> false
                | Some(_plan) -> true)) && joinTupleElementsNormalize(rest)(state)

let recursive joinTupleHoldsHeapElement (elements: List(SemanticType)) (state: CoreLoweringState) =
    match elements with
        | [] -> false
        | element :: rest -> !resultSurvivesReset(element)(state) || joinTupleHoldsHeapElement(rest)(state)

// Stage 0's `IsPassthroughScalarJoinTuple`: a tuple of scalars only is placed in the arena like any
// scalar value, so its joins are left alone, except in a function returning its passthrough
// parameter: that arm is copied into an owned reference-counted tuple, and the promised
// reference-counted result would otherwise retain it a second time at the return. The arena arms
// are copied too, so the join is owned on every arm and returned as it is.
let isPassthroughScalarJoinTuple (semanticType: SemanticType) (state: CoreLoweringState) =
    match statePassthroughParameter(state) with
        | Some((_name, _slot, parameterType)) ->
            match resolveType(state)(parameterType) with
                | SemTuple(_parameterElements) -> isPassthroughNormalizableParameterType(semanticType)(state)
                | _ -> false
        | None -> false

// The copy a mixed join's arms are normalized with: stage 0's `CanNormalizeIntoOwnedRuntimeValue`,
// and a tuple every element of which normalizes (an arena arm is copied into an owned tuple that
// retains each reference-counted element it keeps), and stage 0's `IsGeneralRcValueType`: a tuple of the
// contract's types is copied as the contract copies it.
let joinNormalizationPlanOf (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemTuple(elements) ->
            if joinTupleHoldsHeapElement(elements)(state) && joinTupleElementsNormalize(elements)(state)
            then argumentCopyPlanOf(semanticType)(state)
            else
                if isGeneralRcValueType(semanticType)(state)
                then generalCopyPlanOf(semanticType)(state)
                else
                    if isPassthroughScalarJoinTuple(semanticType)(state)
                    then argumentCopyPlanOf(semanticType)(state)
                    else None
        | _ -> ownedResultPlanOf(semanticType)(state)

// The element copy of a list result whose arms may cons onto the function's own recursive
// result, when its elements are ones the runtime manages (stage 0's `ConsesOntoOwnedSelfResult`).
let consedJoinElementPlan (resultType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(resultType) with
        | SemList(element) as listType ->
            if tcoListElementSupported(element)(state)
            then
                match argumentCopyPlanOf(listType)(state) with
                    | Some(ListDeepArgumentCopy(elementPlan)) -> Some(elementPlan)
                    | _ -> None
            else None
        | _ -> None

// Whether an arm the join does not own conses onto the function's own recursive result: its
// value then owns the reference its last cell holds although its first cells are in the arena.
let recursive anyArmConsesOntoSelfResult (arms: List(MatchArmResult)) =
    match arms with
        | [] -> false
        | arm :: rest -> arm.armStoreTemp >= 0 && arm.armConsesOntoSelfResult && !arm.armOwned && !arm.armRetainedOwner && !arm.armBorrowsLoopParameter || anyArmConsesOntoSelfResult(rest)

// Whether an arm reaching the join is a nested join that took part in the arm normalization
// because one of its own arms conses onto the function's own recursive result.
let recursive anyArmJoinsSelfConsingArms (arms: List(MatchArmResult)) =
    match arms with
        | [] -> false
        | arm :: rest -> arm.armStoreTemp >= 0 && arm.armJoinsSelfConsingArms || anyArmJoinsSelfConsingArms(rest)

// Stage 0's `DemoteArenaSuccessorJoinArms` for one arm: the arm's store becomes an arena copy of its
// reference-counted value, the value released after the copy when the arm owned it.
let demoteArenaSuccessorJoinArmStore (slot: Int) (resultType: SemanticType) (arm: MatchArmResult) (state: CoreLoweringState) =
    (let emptied = state with reversedInstructions = []
    in
        match emitArenaCopyOfReferenceCounted(arm.armStoreTemp)(resultType)((arm.armOwned || arm.armRetainedOwner) && !arm.armBorrowsLoopParameter)(emptied) with
            | (copied, copiedTemp) ->
                let buffered =
                    emit(StoreLocal(slot)(copiedTemp))(copied)
                in
                    let index = joinArmStoreIndex(slot)(arm.armStoreTemp)(state.reversedInstructions)
                    in
                        (buffered with reversedInstructions = replaceJoinArmStore(slot)(arm.armStoreTemp)(buffered.reversedInstructions)(state.reversedInstructions)) |> (given (spliced: CoreLoweringState) ->
                            if index < 0
                            then spliced
                            else
                                withStatePatternOwnerSites(spliced
                                |> statePatternOwnerSites
                                |> shiftPatternOwnerSites(index)(length(buffered.reversedInstructions) - 1))(spliced)))

// The arm as an arena value: what it hands the join is a value the arena keeps.
let arenaJoinArm (arm: MatchArmResult) = MatchArmResult(armRuntimeManaged = false, armNewlyProduced = false, armRetainedOwner = false, armOwned = false, armStoreTemp = arm.armStoreTemp, armBorrowsLoopParameter = false, armFreshCell = arm.armFreshCell, armConsesOntoSelfResult = false, armJoinsSelfConsingArms = false)

let recursive demoteArenaSuccessorJoinArms (slot: Int) (resultType: SemanticType) (arms: List(MatchArmResult)) (state: CoreLoweringState) =
    match arms with
        | [] -> (state, [])
        | arm :: rest ->
            (if arm.armStoreTemp >= 0 && (arm.armOwned || arm.armRetainedOwner || arm.armBorrowsLoopParameter)
            then demoteArenaSuccessorJoinArmStore(slot)(resultType)(arm)(state)
            else state)
            |> demoteArenaSuccessorJoinArms(slot)(resultType)(rest)
            |> (given (demoted: (CoreLoweringState, List(MatchArmResult))) ->
                match demoted with
                    | (demotedState, restArms) -> (demotedState, arenaJoinArm(arm) :: restArms))

// Stage 0's `DemoteArenaSuccessorJoinArms`: a join whose arms mix a reference-counted value with an
// arena value, when the join becomes a loop parameter left in the arena. Normalizing the arena arm
// onto the reference-counted heap would hand the parameter an owned reference that nothing
// releases, so the reference-counted arms are copied into the arena instead.
let demotesArenaSuccessorJoin (successorJoin: Bool) (resultType: SemanticType) (arms: List(MatchArmResult)) (state: CoreLoweringState) =
    match (successorJoin, stateTcoLoop(state)) with
        | (true, Some(_loop)) ->
            match joinArmTally(arms)(0)(0) with
                | (reaching, owned) -> reaching >= 2 && owned > 0 && owned < reaching && !resultSurvivesReset(resultType)(state) && !containsUnresolvedLayout(resultType)(state) && isGeneralRcAdmissible(resultType)([])(state)
        | _ -> false

// Stage 0's `NormalizeMixedJoinArms`. A join whose arms mix an owned reference-counted value with
// one it does not own is not owned as a whole, so the owned arm's reference is never released: the
// consumer retains or copies the join's value again. Such arms are normalized in place instead,
// right before their store, so every arm hands the join a reference of its own. A reuse
// specialization keeps its values in the persistent region and takes no part in this, except for
// an arm consing onto the specialization's own recursive result: that result is reference-counted,
// and the arm takes it over as stage 0's passthrough branch does. A join enclosing such an arm's
// join takes part too, as stage 0's passthrough branch copies the parameter its other arms return.
let normalizeMixedJoinArms (resultSlot: Int) (resultType: SemanticType) (arms: List(MatchArmResult)) (state: CoreLoweringState) =
    (let consedElementPlan = consedJoinElementPlan(resultType)(state)
    in
        let consing =
            match consedElementPlan with
                | Some(_elementPlan) -> anyArmConsesOntoSelfResult(arms)
                | None -> false
        in
            let outsideSpecialization =
                match stateSpecializationFreshInputs(state) with
                    | None -> true
                    | Some(_inputs) -> consing || anyArmJoinsSelfConsingArms(arms)
            in
                match joinArmTally(arms)(0)(0) with
                    | (reaching, owned) ->
                        if !outsideSpecialization || joinIsRuntimeManaged(arms) && !ownedArmBesideBorrowedParameter(arms) || reaching < 2 || owned == 0 && !consing || owned == reaching && !ownedArmBesideBorrowedParameter(arms) || resultSurvivesReset(resultType)(state) || containsUnresolvedLayout(resultType)(state)
                        then (state, arms)
                        else
                            match joinNormalizationPlanOf(resultType)(state) with
                                | None -> (state, arms)
                                | Some(ScalarArgumentCopy) -> (state, arms)
                                | Some(plan) ->
                                    normalizeUnownedJoinArms(resultSlot)(consedElementPlan)(entryCopyPlanOf(resultType)(plan)(state))(arms)(state))

// A join that becomes a loop parameter left in the arena copies its reference-counted arms into the
// arena; any other mixed join is normalized as `normalizeMixedJoinArms` does.
let normalizeMixedJoinArmsAs (successorJoin: Bool) (resultSlot: Int) (resultType: SemanticType) (arms: List(MatchArmResult)) (state: CoreLoweringState) =
    if demotesArenaSuccessorJoin(successorJoin)(resultType)(arms)(state)
    then demoteArenaSuccessorJoinArms(resultSlot)(resultType)(arms)(state)
    else normalizeMixedJoinArms(resultSlot)(resultType)(arms)(state)

// Stage 0's `IsOwnedScopeResultArm` over every arm that reaches the join: a fresh value, a retain the
// arm took of a reference-counted value, or a join that holds its own reference, none borrowed
// from a loop parameter.
let recursive armsOwnTheirReference (arms: List(MatchArmResult)) (state: CoreLoweringState) =
    match arms with
        | [] -> true
        | arm :: rest -> (arm.armStoreTemp < 0 || !arm.armBorrowsLoopParameter && (arm.armNewlyProduced || isOwnedReferenceJoin(arm.armStoreTemp)(state) || isRuntimeTemp(arm.armStoreTemp)(state) && definedByRetain(arm.armStoreTemp)(state.reversedInstructions))) && armsOwnTheirReference(rest)(state)

let recursive anyArmReachesJoin (arms: List(MatchArmResult)) =
    match arms with
        | [] -> false
        | arm :: rest -> arm.armStoreTemp >= 0 || anyArmReachesJoin(rest)

let reachingArmsOwnTheirReference (arms: List(MatchArmResult)) (state: CoreLoweringState) = anyArmReachesJoin(arms) && armsOwnTheirReference(arms)(state)

// Stage 0's `RecordRetainedArmJoin`: every reaching arm stores a retain it just took.
let recursive armsStoreRetains (arms: List(MatchArmResult)) (state: CoreLoweringState) =
    match arms with
        | [] -> true
        | arm :: rest -> (arm.armStoreTemp < 0 || isRuntimeTemp(arm.armStoreTemp)(state) && definedByRetain(arm.armStoreTemp)(state.reversedInstructions)) && armsStoreRetains(rest)(state)

// Stage 0's RecordNeutralJoin: a join is unreachable only if all its arms are proven so.
// Keep that proof through nested if/match joins without excluding any reaching call result.
let recordNeutralJoin (resultTemp: Int) (arms: List(MatchArmResult)) (state: CoreLoweringState) =
    match arms with
        | [] -> state
        | _ ->
            if anyArmReachesJoin(arms)
            then state
            else withStateBackEdgeDummyTemps(resultTemp :: stateBackEdgeDummyTemps(state))(state)

let markControlFlowJoin (resultTemp: Int) (arms: List(MatchArmResult)) (state: CoreLoweringState) =
    ((given (marked: CoreLoweringState) ->
        marked
        |> recordNeutralJoin(resultTemp)(arms)
        |> (given (owners: CoreLoweringState) ->
            if joinIsRetainedOwner(arms)
            then withStatePatternOwnerResultTemps(resultTemp :: statePatternOwnerResultTemps(owners))(owners)
            else owners)
        |> (given (owned: CoreLoweringState) ->
            if reachingArmsOwnTheirReference(arms)(owned)
            then recordOwnedReferenceJoin(resultTemp)(owned)
            else owned)
        |> (given (retained: CoreLoweringState) ->
            if anyArmReachesJoin(arms) && armsStoreRetains(arms)(retained)
            then recordRetainedArmJoin(resultTemp)(retained)
            else retained)))(match (joinIsRuntimeManaged(arms) || reachingArmsAllNewlyProduced(arms), joinIsNewlyProduced(arms) || reachingArmsAllNewlyProduced(arms)) with
        | (true, true) ->
            state
            |> markRuntimeTemp(resultTemp)(RuntimeNewlyProduced)
            |> recordJoinRepresentation(resultTemp)(arms)
        | (true, false) ->
            state
            |> markRuntimeTemp(resultTemp)(RuntimeTransferred)
            |> recordJoinRepresentation(resultTemp)(arms)
        | (false, _) -> state)
