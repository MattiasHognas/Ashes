// Recursive bindings: their preparation, sibling result pinning and `let recursive` lowering. A
// part of `AshesCompiler.Semantics.CoreLowering`.

import AshesCompiler.Semantics.NodeIdentity.withoutNodeIdentity
import Ashes.Collection.List.append
import Ashes.Collection.List.reverse
import Ashes.Collection.Map.MapTree
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.TypeExpr
import AshesCompiler.Frontend.Token.TextSpan
import AshesCompiler.Semantics.IrInstructions
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
import AshesCompiler.Semantics.CoreLowering.LoopFrames
import AshesCompiler.Semantics.CoreLowering.CallArguments
import AshesCompiler.Semantics.CoreLowering.Matches
export (
    value prepareRecursiveGroup,
    value openPreparedRecursiveGroup,
    value lowerPreparedRecursiveGroupWith,
    value lowerRecursiveGroup,
    value relabelSingleRecursive,
    value lowerLetRecursive,
    value emitTupleFields,
    value retainLoopParameterChild,
    value childIsRuntimeManaged,
    value isRuntimeManageableHeapChild,
    value isRuntimeManageableTupleElement,
)

let recursive bindingNames (bindings: List(CoreBinding)) =
    match bindings with
        | [] -> []
        | CoreBinding { name = name } :: rest -> name :: bindingNames(rest)

let recursive innermostResultType (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemFunction(_argument, result, _row) -> innermostResultType(result)(state)
        | other -> other

// The member whose body is being lowered, when its arrow's innermost result is still unresolved
// as the body begins: a self call to it takes a fresh result type, the way stage 0's
// lowering-local self arrow stays unresolved until the member's body is done. A sibling of a
// recursive group is left alone: stage 0 reaches its siblings through the group's merged
// dispatch, whose shape is OPT-19's.
let recursive deferredResultNames (selfName: Str) (bindings: List(CoreBinding)) (state: CoreLoweringState) =
    match bindings with
        | [] -> []
        | CoreBinding { name = name, scheme = TypeScheme { body = bindingType } } :: rest ->
            if name == selfName && containsUnresolvedLayout(innermostResultType(bindingType)(state))(state)
            then name :: deferredResultNames(selfName)(rest)(state)
            else deferredResultNames(selfName)(rest)(state)

let recursive openSiblingResultNames (selfName: Str) (bindings: List(CoreBinding)) (state: CoreLoweringState) =
    match bindings with
        | [] -> []
        | CoreBinding { name = name, scheme = TypeScheme { body = bindingType } } :: rest ->
            if name != selfName && containsUnresolvedLayout(innermostResultType(bindingType)(state))(state)
            then name :: openSiblingResultNames(selfName)(rest)(state)
            else openSiblingResultNames(selfName)(rest)(state)

// The members whose result is open at the body's entry, the body's own binding among them when a
// sibling's is too: stage 0 lowers the body once, with every member's result still open at every
// call, so a self call asks for an arena result also when a sibling's resolved result has this body
// lowered again.
let openGroupResultNames (selfName: Str) (bindings: List(CoreBinding)) (state: CoreLoweringState) =
    match openSiblingResultNames(selfName)(bindings)(state) with
        | [] -> []
        | siblings ->
            if state
            |> deferredResultNames(selfName)(bindings)
            |> containsLabel(selfName)
            then selfName :: siblings
            else siblings

let recursive withoutFirstName (name: Str) (names: List(Str)) =
    match names with
        | [] -> []
        | candidate :: rest ->
            if candidate == name
            then rest
            else candidate :: withoutFirstName(name)(rest)

// Decided again each time the body is entered: a body lowered again against the scope its first
// lowering closed sees its own binding's result resolved, so its self calls take the binding's
// result the way stage 0's elaborated binding does, while a first lowering still defers them.
let refreshSelfResultDeferral (selfName: Str) (state: CoreLoweringState) =
    withStateSelfResultDeferredNames(state
    |> stateSelfResultDeferredNames
    |> withoutFirstName(selfName)
    |> append(deferredResultNames(selfName)(state.bindings)(state)))(state)

// The recursive group's member names are remembered for the body and everything lifted out of
// it, so a member captured into an inner curried stage is still known as a self callee.
let prepareRecursiveBodyState selfName parameter parameterType captures selfBindings origin state =
    (let functionBindings =
        CoreBinding(
            name = parameter,
            scheme = emptyScheme(parameterType),
            location = CoreLocal(1),
            ownedRead = false,
            patternOwner = None,
            closedScheme = false
        ) :: append(selfBindings)(capturedScope(captures)(0))
    in
        state
        |> enterFunctionOrigin(origin)
        |> (given (current: CoreLoweringState) -> current with reversedInstructions = [], tcoState = (current.tcoState with callArgumentFrameActive = false))
        |> (given (current: CoreLoweringState) -> current with bindings = functionBindings)
        |> (given (current: CoreLoweringState) ->
            withStateRecursiveGroupNames(bindingNames(selfBindings))(current))
        |> (given (current: CoreLoweringState) ->
            withStateSelfResultDeferredNames(current
            |> stateSelfResultDeferredNames
            |> append(deferredResultNames(selfName)(selfBindings)(current)))(current))
        |> (given (current: CoreLoweringState) ->
            withStateSiblingResultOpenNames(openGroupResultNames(selfName)(selfBindings)(current))(current))
        |> (given (current: CoreLoweringState) -> current with nextTemp = 0)
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
        |> (given (current: CoreLoweringState) -> current with nextLocal = 2))

// A recursive member's result meets the callers the same way a plain lambda's does: a
// reference-counted result is copied into the arena when the caller asked for one.
let finishRecursiveLambdaBody prepared origin captures environmentTemp typedOuter lowered =
    match (prepared, lowered
    |> closeTmcChainPrepared(prepared)
    |> unifySelfCallResults) with
        | (_prepared, LoweredCoreValue { state = failedState, error = Some(error) }) -> failure(failedState)(error)
        | (PreparedCoreRecursiveBinding { label = label, semanticType = semanticType, resultType = resultType }, LoweredCoreValue { state = bodyState, temp = loweredTemp, semanticType = bodyType, error = None }) ->
            match bindType(resultType)(bodyType)(bodyState) with
                | (failedState, Some(error)) -> failure(failedState)(error)
                | (boundBody, None) ->
                    match boundBody
                    |> adoptDeferredSelfCallResults(label)(loweredTemp)
                    |> adoptNormalizedParameterResult(label)(loweredTemp)
                    |> reserveTcoExitTransferSlot(label)(loweredTemp)(bodyType)
                    |> keepPredictedRuntimeManagedResult(label)(loweredTemp)(bodyType) with
                        | (typedBody, bodyTemp) ->
                            let bodyRuntimeManaged = finishedResultRuntimeManaged(bodyTemp)(bodyType)(typedBody)
                            in
                                let finishedBody =
                                    match normalizeFunctionResult(label)(bodyTemp)(bodyType)(typedBody) with
                                        | (normalized, returnedTemp) ->
                                            normalized
                                            |> emit(Return(returnedTemp))
                                            |> resolvePendingTcoResets
                                            |> backfillSelfClosureResultOwnership(label)(bodyRuntimeManaged)
                                            |> finishLiftedFunction(label)(origin)
                                in
                                    let restored =
                                        finishedBody
                                        |> restoreOuterFrame(typedOuter)
                                        |> recordBodyRuntimeManaged(label)(bodyRuntimeManaged)
                                        |> recordReturnedClosureLabel(label)(bodyTemp)(typedBody.reversedInstructions)
                                    in
                                        match emitClosure(label)(environmentTemp)(captureCount(captures))(false)(restored) with
                                            | (closureState, closureTemp) ->
                                                success(closureTemp)(resolveType(finishedBody)(semanticType))(closureState)

// A curried parameter lambda written as `let recursive f a b = ...` sugar carries its
// declaration's span in stage 0's syntax tree, so the chain's inner lambdas are lowered under it.
// The parser ends a sugar binding's span at its unspanned lambda value, so a declaration at the
// start of the file would collapse to the unlocated `TextSpan(0, 0)`; only the start locates
// instructions, so the span is widened past it.
let sugarChainBody (body: Expr) (declarationSpan: Maybe(TextSpan)) =
    match (withoutNodeIdentity(body), declarationSpan) with
        | (ExprLambda(_parameter, _inner, _annotation), Some(TextSpan(start, end))) ->
            if end > start
            then ExprAt(TextSpan(start = start, end = end))(body)
            else ExprAt(TextSpan(start = start, end = start + 1))(body)
        | _ -> body

// The member's own arrow closed the way stage 0's inferred annotation closes it: every deferred
// self-call result meets the binding's result, and the result meets the body's type. Applied to
// a copy of the first lowering to judge whether the body is lowered again against the closed
// scope; the lowering that stands is closed the same way when it finishes.
let closeRecursiveBodyResult (resultType: SemanticType) (lowered: LoweredCoreValue) =
    match unifySelfCallResults(lowered) with
        | LoweredCoreValue { error = Some(_error) } as failed -> failed
        | LoweredCoreValue { state = unified, temp = temp, semanticType = bodyType, error = None } ->
            match bindType(resultType)(bodyType)(unified) with
                | (failedState, Some(error)) -> failure(failedState)(error)
                | (bound, None) -> LoweredCoreValue(state = bound, temp = temp, semanticType = bodyType, error = None)

// A later member of a top-level recursive group is located at its own `and`, as stage 0's
// binding spans place it; the first member and every other group keep the group's span.
let recursive memberDeclarationSpan (name: Str) (starts: List((Str, Int, Int))) (groupSpan: Maybe(TextSpan)) =
    match (starts, groupSpan) with
        | ((member, start, groupStart) :: rest, Some(TextSpan(spanStart, spanEnd))) ->
            if member == name && groupStart == spanStart
            then Some(TextSpan(start = start, end = spanEnd))
            else memberDeclarationSpan(name)(rest)(groupSpan)
        | _ -> groupSpan

// A recursive member is a known callee like a let-bound lambda: its label is remembered under
// its name, so a call through the name can consult the member's recorded body placement.
let lowerPreparedRecursiveLambda prepared selfBindings captures environmentTemp lower (state: CoreLoweringState) =
    match prepared with
        | PreparedCoreRecursiveBinding { name = name, label = label, parameter = parameter, body = body, parameterType = parameterType, resultType = resultType } ->
            match withStateLetLambdaLabels(state
            |> stateLetLambdaLabels
            |> Ashes.Collection.Map.setStr(name)(label))(state) with
                | labeled ->
                    labeled
                    |> sourceFunctionOrigin(label)(sourceFunctionOriginFor(name)(labeled))
                    |> (given (origin) ->
                        labeled
                        |> seedParameterFromConstructorFields(parameter)(parameterType)(body)
                        |> prepareRecursiveBodyState(name)(parameter)(parameterType)(captures)(selfBindings)(origin)
                        |> noteLetBoundFunction(label)(origin)
                        |> lowerRecursiveBodyResolvingCalls(labeled
                        |> stateRecursiveDeclarationSpan
                        |> memberDeclarationSpan(name)(stateRecursiveMemberStarts(labeled))
                        |> sugarChainBody(body))(given (entered: CoreLoweringState) ->
                            entered
                            |> refreshSelfResultDeferral(name)
                            |> enterRecursiveTcoLoop(name)(parameter)(parameterType)(body)(captureCount(selfBindings) == 1)
                            |> enterTcoLoopBody(label)(parameter)
                            |> withStateLoweringBodyLabel(label)
                            |> withStateCoroutineBody(false)
                            |> withPassthroughParameter(parameter)(body)(parameterType)
                            |> predictRuntimeManagedResult(label)(parameterType)
                            |> beginCurryStage(label)(body)
                            |> withRecursiveBodyRequest(body)(resultType))(closeRecursiveBodyResult(resultType))(lower)
                        |> finalizeTcoRuntimeManagedParams(label)
                        |> finishRecursiveLambdaBody(prepared)(origin)(captures)(environmentTemp)(labeled))

let preparedSelfBinding environmentSize prepared =
    match prepared with
        | PreparedCoreRecursiveBinding { name = name, label = label, semanticType = semanticType } ->
            CoreBinding(
                name = name,
                scheme = emptyScheme(semanticType),
                location = CoreSelf(label)(environmentSize),
                ownedRead = false,
                patternOwner = None,
                closedScheme = false
            )

let recursive preparedSelfBindings environmentSize members =
    match members with
        | [] -> []
        | member :: rest -> preparedSelfBinding(environmentSize)(member) :: preparedSelfBindings(environmentSize)(rest)

let recursive recursiveBindingNames bindings reversed =
    match bindings with
        | [] -> reverse(reversed)
        | (name, _value) :: rest -> recursiveBindingNames(rest)(name :: reversed)

let recursive collectRecursiveGroupFree bindings groupNames free =
    match bindings with
        | [] -> free
        | (_name, value) :: rest ->
            free
            |> collectFree(value)(groupNames)
            |> collectRecursiveGroupFree(rest)(groupNames)

let finishPreparedRecursiveMember name parameter body lambdaId slot fresh =
    match fresh with
        | FreshFunctionType { state = state, semanticType = semanticType, parameterType = parameterType, resultType = resultType } ->
            PreparedCoreRecursiveMemberResult(
                state = withNextLambdaId(lambdaId + 1)(state),
                member = Some(PreparedCoreRecursiveBinding(
                    name = name,
                    parameter = parameter,
                    body = body,
                    label = "recgroup_" + Ashes.Text.fromInt(lambdaId) + "_" + name,
                    slot = slot,
                    semanticType = semanticType,
                    parameterType = parameterType,
                    resultType = resultType
                )),
                error = None
            )

// The member's first parameter takes its annotation before the body is lowered, as a plain
// lambda's does, so a self-call inside the body meets the annotated parameter type.
let annotatePreparedParameter annotation fresh =
    match fresh with
        | FreshFunctionType { state = state, semanticType = semanticType, parameterType = parameterType, resultType = resultType } ->
            match state
            |> withStateInlineTypeVariables([])
            |> lowerLambdaParameterType(annotation)(parameterType) with
                | (annotatedState, error) ->
                    (FreshFunctionType(
                        state = annotatedState,
                        semanticType = semanticType,
                        parameterType = parameterType,
                        resultType = resultType
                    ), error)

let allocatePreparedRecursiveMember name parameter body annotation lambdaId fresh =
    match fresh with
        | FreshLocal { state = state, local = slot } ->
            match state
            |> freshFunctionType
            |> annotatePreparedParameter(annotation) with
                | (FreshFunctionType { state = failedState }, Some(error)) ->
                    PreparedCoreRecursiveMemberResult(
                        state = failedState,
                        member = None,
                        error = Some(error)
                    )
                | (annotated, None) -> finishPreparedRecursiveMember(name)(parameter)(body)(lambdaId)(slot)(annotated)

let prepareRecursiveMember name value state =
    match (lambdaParts(value), state) with
        | (None, _) ->
            PreparedCoreRecursiveMemberResult(
                state = state,
                member = None,
                error = Some(CoreRecursiveBindingRequiresFunction(name))
            )
        | (Some((parameter, body, annotation)), CoreLoweringState { nextLambdaId = lambdaId }) ->
            state
            |> recordLetLambda(name)(value)
            |> freshLocal
            |> allocatePreparedRecursiveMember(name)(parameter)(body)(annotation)(lambdaId)

let recursive prepareRecursiveGroup bindings state reversed =
    match bindings with
        | [] -> PreparedCoreRecursiveGroup(state = state, members = reverse(reversed), error = None)
        | (name, value) :: rest ->
            match prepareRecursiveMember(name)(value)(state) with
                | PreparedCoreRecursiveMemberResult { state = failedState, error = Some(error) } ->
                    PreparedCoreRecursiveGroup(
                        state = failedState,
                        members = reverse(reversed),
                        error = Some(error)
                    )
                | PreparedCoreRecursiveMemberResult { state = nextState, member = Some(member), error = None } ->
                    prepareRecursiveGroup(
                        rest,
                        nextState,
                        member :: reversed
                    )

let recursive lowerPreparedRecursiveMembers members selfBindings captures environmentTemp lower state =
    match members with
        | [] -> success(-1)(SemNever)(state)
        | (PreparedCoreRecursiveBinding { slot = slot, semanticType = semanticType } as member) :: rest ->
            match lowerPreparedRecursiveLambda(member)(selfBindings)(captures)(environmentTemp)(lower)(state) with
                | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                | LoweredCoreValue { state = closureState, temp = closureTemp, semanticType = closureType, error = None } ->
                    match bindType(semanticType)(closureType)(closureState) with
                        | (failedState, Some(error)) -> failure(failedState)(error)
                        | (typedState, None) ->
                            typedState
                            |> emit(StoreLocal(slot)(closureTemp))
                            |> lowerPreparedRecursiveMembers(rest)(selfBindings)(captures)(environmentTemp)(lower)

let recursive pinnableCallResult (semanticType: SemanticType) =
    match semanticType with
        | SemVariable(_variable) -> false
        | SemFunction(_argument, _result, _row) -> false
        | SemRow(_elements, _tail) -> false
        | SemCapability(_name, _arguments) -> false
        | SemList(element) -> pinnableCallResult(element)
        | SemTuple(elements) -> allPinnableCallResults(elements)
        | SemNamed(_symbolId, _name, arguments) -> allPinnableCallResults(arguments)
        | SemPointer(pointee) -> pinnableCallResult(pointee)
        | _ -> true
and allPinnableCallResults (types: List(SemanticType)) =
    match types with
        | [] -> true
        | semanticType :: rest -> pinnableCallResult(semanticType) && allPinnableCallResults(rest)

// The sites the group's own lowering recorded (the first `count` of the pending list) whose type its
// substitution has since settled, stage 0's `ResolveProvenCallResultTypes`.
let recursive settledSiblingCallResults (pending: List((Str, SemanticType))) (count: Int) (state: CoreLoweringState) (proven: MapTree(Str, SemanticType)) (settled: Bool) =
    match (pending, count > 0) with
        | ((key, semanticType) :: rest, true) ->
            let resolved = resolveType(state)(semanticType)
            in
                if pinnableCallResult(resolved)
                then
                    settledSiblingCallResults(rest)(count - 1)(state)(Ashes.Collection.Map.setStr(key)(resolved)(proven))(true)
                else settledSiblingCallResults(rest)(count - 1)(state)(proven)(settled)
        | _ -> (proven, settled)

// A group whose members call a later member before its result is known is lowered twice, as stage 0
// lowers the whole program twice: the first lowering settles each such call's result type, and the
// one that stands starts over from the state before the group with those types pinned at their
// sites, so its numbering is the single lowering's.
let lowerRecursiveMembersPinningSiblingResults members selfBindings captures environmentTemp memberLower (environmentState: CoreLoweringState) =
    // The disposable member pass must still infer later siblings during a pinned
    // whole-program replay. Its final pass restores the caller's pinned boundary.
    match environmentState
    |> withStateTraitGoalsPinned(false)
    |> lowerPreparedRecursiveMembers(members)(selfBindings)(captures)(environmentTemp)(memberLower) with
        | LoweredCoreValue { error = Some(_error) } as failed -> failed
        | LoweredCoreValue { state = firstState } as first ->
            let recorded =
                coreListLength(statePendingSiblingCallResults(firstState)) - coreListLength(statePendingSiblingCallResults(environmentState))
            in
                match settledSiblingCallResults(statePendingSiblingCallResults(firstState))(recorded)(firstState)(stateProvenSiblingCallResults(environmentState))(false) with
                    | (proven, settled) ->
                        // Pending goals are append-only, so an empty discovery list proves
                        // no provisional trait value was emitted. A pinned entry normally
                        // starts empty; preserve its boundary without replaying this group.
                        // A nonempty list conservatively requires the final pinned pass.
                        let needsPinnedReplay =
                            match statePendingTraitGoals(firstState) with
                                | [] -> false
                                | _goals -> stateTraitGoalsPinned(environmentState)
                        in
                            if settled || needsPinnedReplay
                            then
                                environmentState
                                |> withStateProvenSiblingCallResults(proven)
                                |> lowerPreparedRecursiveMembers(members)(selfBindings)(captures)(environmentTemp)(memberLower)
                            else
                                first with state = withStateTraitGoalsPinned(stateTraitGoalsPinned(environmentState))(firstState)

let recursive addRecursiveGroupContinuationBindings members outerBindings state =
    match members with
        | [] -> state
        | PreparedCoreRecursiveBinding { name = name, slot = slot, semanticType = semanticType } :: rest ->
            let scheme =
                semanticType
                |> resolveType(state)
                |> generalizeResolvedType(outerBindings)(state)
            in
                state
                |> addBinding(name)(scheme)(CoreLocal(slot))
                |> addRecursiveGroupContinuationBindings(rest)(outerBindings)

// The group's names bound over what follows it and the continuation's consumer request armed,
// once every member is lowered and stored; the continuation itself is lowered by the caller.
let bindRecursiveGroupContinuation members outerBindings (request: ConsumerRequest) loweredMembers =
    match loweredMembers with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValue { state = groupState, error = None } ->
            groupState
            |> addRecursiveGroupContinuationBindings(members)(outerBindings)
            |> withConsumerRequest(request)
            |> success(-1)(SemNever)

// Splits the single `lower` continuation lowerPreparedRecursiveGroup uses into two: memberLower
// lowers each recursive member's own body, continuationLower lowers what follows the group. A
// whole-program driver needs the two to differ (member bodies always go through the ordinary
// expression lowerer; the continuation is "the rest of the top-level items", which isn't an Expr
// at all), so the plain single-`lower` form below is now a same-lowerer convenience wrapper.
// The opened half of lowerPreparedRecursiveGroupWith: the members lowered and stored and the
// group's names bound over what follows, which the caller lowers over the returned state before
// restoring `outerBindings`. The whole-program driver keeps the flat top-level chain iterative by
// opening each group this way and taking its names back out of scope once the rest is lowered.
let openPreparedRecursiveGroup bindings memberLower outerBindings prepared =
    match prepared with
        | PreparedCoreRecursiveGroup { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | PreparedCoreRecursiveGroup { state = preparedState, members = members, error = None } ->
            let groupNames = recursiveBindingNames(bindings)([])
            in
                let captures =
                    []
                    |> collectRecursiveGroupFree(bindings)(groupNames)
                    |> withActiveTraitMethodNames(preparedState)
                    |> (given (names) -> capturedBindings(names)(outerBindings)([]))
                in
                    match preparedState
                    |> clearConsumerRequest
                    |> allocateEnvironment(storeCaptureAsRead)(captures)(captures)(false) with
                        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                        | LoweredCoreValue { state = environmentState, temp = environmentTemp, error = None } ->
                            let selfBindings = preparedSelfBindings(captureCount(captures) * 8)(members)
                            in
                                environmentState
                                |> lowerRecursiveMembersPinningSiblingResults(
                                    members,
                                    selfBindings,
                                    captures,
                                    environmentTemp,
                                    memberLower
                                )
                                |> bindRecursiveGroupContinuation(members)(outerBindings)(consumerRequestOf(preparedState))

let lowerPreparedRecursiveGroupWith bindings body memberLower continuationLower outerBindings prepared =
    match openPreparedRecursiveGroup(bindings)(memberLower)(outerBindings)(prepared) with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValue { state = continuationState, error = None } ->
            continuationState
            |> continuationLower(body)
            |> restoreLoweredBindings(outerBindings)

let lowerPreparedRecursiveGroup bindings body lower outerBindings prepared = lowerPreparedRecursiveGroupWith(bindings)(body)(lower)(lower)(outerBindings)(prepared)

let lowerRecursiveGroup bindings body lower state =
    match state with
        | CoreLoweringState { bindings = outerBindings } ->
            []
            |> prepareRecursiveGroup(bindings)(state)
            |> lowerPreparedRecursiveGroup(bindings)(body)(lower)(outerBindings)

let relabelSingleRecursive lambdaId prepared =
    match prepared with
        | PreparedCoreRecursiveGroup { state = state, members = member :: [], error = None } ->
            PreparedCoreRecursiveGroup(
                state = state,
                members = [(member with label = "lambda_" + Ashes.Text.fromInt(lambdaId))],
                error = None
            )
        | _ -> prepared

// A lowercase type name other than the unsigned integer types: a type variable.
let isTypeVariableName (name: Str) =
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

let recursive typeExprNamesVariable (typeExpr: TypeExpr) =
    match typeExpr with
        | TypeAt(_span, inner) -> typeExprNamesVariable(inner)
        | TypeNamed(name) -> isTypeVariableName(name)
        | TypeApplied(_name, arguments) -> anyTypeExprNamesVariable(arguments)
        | TypeArrow(argument, result, _capabilities, _tail) -> typeExprNamesVariable(argument) || typeExprNamesVariable(result)
        | TypeTuple(elements) -> anyTypeExprNamesVariable(elements)
        | TypeUnit -> false
and anyTypeExprNamesVariable (typeExprs: List(TypeExpr)) =
    match typeExprs with
        | [] -> false
        | typeExpr :: rest -> typeExprNamesVariable(typeExpr) || anyTypeExprNamesVariable(rest)

// A recursive let's declared type is unified with the binding's fresh function type before the
// body is lowered, so a self call inside the body meets the declared result, as stage 0's
// elaborated binding does, and asks for no arena result. Inside a generic implementation method
// body the annotation names the head's type parameters (`let recursive equalLists : List(a) ->
// List(a) -> Bool requires {Eq(a)}`) and is read against the active type parameters, so a
// comparison inside the body sees the variable the active evidence carries. Elsewhere an
// annotation naming a type variable is left to the ordinary inference.
let pinRecursiveAnnotation (annotation: Maybe(TypeExpr)) (prepared: PreparedCoreRecursiveGroup) =
    match (annotation, prepared) with
        | (Some(typeExpr), PreparedCoreRecursiveGroup { state = state, members = (PreparedCoreRecursiveBinding { semanticType = semanticType } as member) :: [], error = None }) ->
            let expanded =
                expandTypeAliases(stateTypeAliases(state))(32)(typeExpr)
            in
                match (stateActiveTypeParameters(state), typeExprNamesVariable(expanded)) with
                    | ([], true) -> prepared
                    | (parameters, _namesVariable) ->
                        match typeExprToSemanticType(expanded)(parameters) with
                            | None -> prepared
                            | Some(annotationType) ->
                                match bindType(semanticType)(annotationType)(state) with
                                    | (boundState, None) -> PreparedCoreRecursiveGroup(state = boundState, members = [member], error = None)
                                    | (failedState, Some(error)) -> PreparedCoreRecursiveGroup(state = failedState, members = [member], error = Some(error))
        | _ -> prepared

let lowerLetRecursive name value body annotation lower state =
    match state with
        | CoreLoweringState { bindings = outerBindings, nextLambdaId = lambdaId, currentSpan = declarationSpan } ->
            []
            |> prepareRecursiveGroup([(name, value)])(withStateRecursiveDeclarationSpan(declarationSpan)(state))
            |> relabelSingleRecursive(lambdaId)
            |> pinRecursiveAnnotation(annotation)
            |> lowerPreparedRecursiveGroup([(name, value)])(body)(lower)(outerBindings)

let recursive emitTupleFields baseTemp index temps state =
    match temps with
        | [] -> state
        | temp :: rest ->
            state
            |> emit(StoreMemOffset(baseTemp)(index * 8)(temp))
            |> emitTupleFields(baseTemp)(index + 1)(rest)

// A parameter's own read inside the tail self-call argument that rebuilds it: the reference the
// back edge moves into the successor (stage 0's `_loweringTcoBackEdgeArgumentSlot` exclusion). A
// field read out of the parameter is a borrow of a child the old parameter's structural walk
// releases, so it is never excluded.
let isOwnSuccessorRead (argument: Expr) (slot: Int) (state: CoreLoweringState) =
    match (unspanArgument(argument), stateBackEdgeArgumentSlot(state)) with
        | (ExprVar(_name), Some(argumentSlot)) -> argumentSlot == slot
        | _ -> false

// Stage 0's `RetainRuntimeManagedTcoConstructorArguments` and
// `DuplicateRuntimeManagedTcoParameterForAggregate`: a heap-typed loop parameter read, or a
// heap-typed field read out of one, stored into an aggregate cell takes a duplicate — an
// identity marker until the loop's finalize pass turns it into a real retain for a parameter it
// places on the reference-counted heap. The back edge releases the old parameter's own reference
// (and its children through the structural walk) once its argument rebuilds the value, so a
// stored borrow would be freed under the cell.
// A field read whose type needs no release (`state.done: Bool`) is a copied word, never retained.
let loopParameterChildSlot (argument: Expr) (semanticType: SemanticType) (state: CoreLoweringState) =
    match unspanArgument(argument) with
        | ExprQualifiedVar(_owner, _field) ->
            if resultSurvivesReset(semanticType)(state)
            then None
            else loopParameterReadSlot(argument)(state)
        | _ -> loopParameterReadSlot(argument)(state)

let retainLoopParameterChild (argument: Expr) (originalTemp: Int) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            if temp != originalTemp
            then lowered
            else
                match loopParameterChildSlot(argument)(semanticType)(state) with
                    | Some(slot) ->
                        if isOwnSuccessorRead(argument)(slot)(state)
                        then lowered
                        else
                            match freshTemp(state) with
                                | FreshTemp { state = allocated, temp = duplicate } ->
                                    allocated
                                    |> emit(RcDup(duplicate)(temp)(false)(false))
                                    |> (given (marked: CoreLoweringState) -> withStateTcoParameterRetainSites((duplicate, slot, semanticType) :: stateTcoParameterRetainSites(marked))(marked))
                                    |> success(duplicate)(semanticType)
                    | None -> lowered
        | _ -> lowered

// Whether a lowered child is represented on the reference-counted heap: a newly produced or
// handed-on runtime temp, the borrowed read of a binding that still owns its reference, or the
// read of a loop parameter the frame places on the reference-counted heap.
let childIsRuntimeManaged (child: Expr) (temp: Int) (state: CoreLoweringState) =
    if isRuntimeTemp(temp)(state)
    then true
    else
        match unspanArgument(child) with
            | ExprVar(name) ->
                match liveRuntimeOwnerSlot(name)(state) with
                    | Some(_slot) -> true
                    | None -> loopParameterIsRuntimeManaged(name)(state)
            | _ -> false

let isRuntimeManageableHeapChild (semanticType: SemanticType) (listOverScalarsOnly: Bool) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemTuple(_elements) -> true
        | SemString -> true
        | SemBytes -> true
        | SemBigInt -> true
        | SemList(element) -> listOverScalarsOnly == false || resultSurvivesReset(element)(state)
        | SemNamed(_symbolId, _name, _arguments) -> true
        | _ -> false

// Stage 0's `IsRuntimeManageableTupleElement`: a scalar, or a runtime-managed tuple, string,
// bytes, big integer, list, or named type.
let isRuntimeManageableTupleElement (child: Expr) (temp: Int) (semanticType: SemanticType) (state: CoreLoweringState) = resultSurvivesReset(semanticType)(state) || childIsRuntimeManaged(child)(temp)(state) && isRuntimeManageableHeapChild(semanticType)(false)(state)
