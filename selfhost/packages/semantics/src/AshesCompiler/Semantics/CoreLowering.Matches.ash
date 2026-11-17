// Conditionals, pattern lowering, match arms and their owners, reuse of the scrutinee cell, tag
// groups and case trimming. A part of `AshesCompiler.Semantics.CoreLowering`.

import Ashes.Collection.List.append
import Ashes.Collection.List.length
import Ashes.Collection.List.reverse
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.Pattern
import AshesCompiler.Frontend.Token.TextSpan
import AshesCompiler.Semantics.AggregateOwnership
import AshesCompiler.Semantics.ConstructionRetains
import AshesCompiler.Semantics.HeapLayoutClassification
import AshesCompiler.Semantics.IrControlFlowGraph.containsInt
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.StateMachineTransform.getDefinedTemps
import AshesCompiler.Semantics.MatchArmOwnership
import AshesCompiler.Semantics.StructuralDroppers
import AshesCompiler.Semantics.TcoRuntimeManagedParams
import AshesCompiler.Semantics.TaglessAdtLayout.isResourceBearingType
import AshesCompiler.Semantics.PatternBindingOwnership
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.ExprMentions.exprMentionsName
import AshesCompiler.Semantics.ReuseSpecialization
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.CoreLowering.LoweringState
import AshesCompiler.Semantics.CoreLowering.Emit
import AshesCompiler.Semantics.CoreLowering.Owners
import AshesCompiler.Semantics.CoreLowering.CopyPlans
import AshesCompiler.Semantics.CoreLowering.TcoReset
import AshesCompiler.Semantics.CoreLowering.Bindings
import AshesCompiler.Semantics.CoreLowering.LoopPlacement
import AshesCompiler.Semantics.CoreLowering.LoopFrames
import AshesCompiler.Semantics.CoreLowering.CallArguments
import AshesCompiler.Semantics.CoreLowering.CallResults
import AshesCompiler.Semantics.CoreLowering.ResultHandOver
export (
    value lowerIfThenBranch,
    value finishIfElseBranch,
    value lowerIf,
    value isNullaryConstructorPatternName,
    value preparePattern,
    value findPatternField,
    value lowerPattern,
    value failedMatchArm,
    value reusePointerFieldIndices,
    value tailArgumentSuccessorJoin,
    value letValueRequest,
    value escapingChainBody,
    value escapeBodyRequest,
    value escapingLetBodyRequest,
    value letOwnedTypeName,
    value lowerArenaBracketedNestedLetWith,
    value collectPendingBorrowedOwners,
    value lowerLet,
    value lowerAnnotatedLet,
    value listCellReuseConstructorName,
    value slotAtOrdinal,
    value finishPatternArm,
    value matchFailLabel,
    value labelNextMatchArm,
    value bracketMatchArm,
    value prepareMatchPlan,
    value finishMatchPlan,
    value lowerMatch,
    value lambdaParts,
)

// Stage 0's `TryLowerStaticStringNormalizedBranch` for a literal string: the constant loaded and
// copied to the reference-counted heap as an RC-normalized `CopyOutArena`, so it joins a fresh
// runtime-managed sibling uniformly; any other body lowers as usual.
let lowerStaticStringNormalizedBody body (normalizeStaticStrings: Bool) lower (state: CoreLoweringState) =
    match (normalizeStaticStrings, staticStringArmBody(body)) with
        | (true, Some(value)) ->
            match lowerString(value)(state) with
                | LoweredCoreValue { state = literalState, temp = literalTemp, semanticType = literalType, error = None } ->
                    match freshTemp(literalState) with
                        | FreshTemp { state = copyState, temp = copyTemp } ->
                            copyState
                            |> emit(CopyOutArena(copyTemp)(literalTemp)(-1)(true)(RcNormalization)(None))
                            |> markRuntimeTemp(copyTemp)(RuntimeNewlyProduced)
                            |> success(copyTemp)(literalType)
                | failed -> failed
        | _ -> lower(body)(state)

// Stage 0's `ShouldNormalizeStaticStringIfBranches`: an `if` lowered under a request for a
// runtime-managed string normalizes a literal branch beside a fresh branch exactly as a match
// normalizes its arms, since a join of a static literal and a reference-counted value is an
// arena join whose later copy-out orphans the fresh branch's reference-counted value.
let ifBranchesNormalizeStaticStrings thenBranch elseBranch (request: ConsumerRequest) (state: CoreLoweringState) = request.runtimeString && reconcilableStringArms([(PatternWildcard, thenBranch, None), (PatternWildcard, elseBranch, None)])(state)

// The field read a branch hands out as its value, through the lets around it (`state.refs`),
// unless a let in between rebinds the receiver.
let recursive tailFieldRead (body: Expr) =
    match body with
        | ExprAt(_span, inner) -> tailFieldRead(inner)
        | ExprNode(_node, inner) -> tailFieldRead(inner)
        | ExprQualifiedVar(_owner, _field) -> Some(body)
        | ExprLet(nested, _value, nestedBody, _parameters, _annotation, _requirements) ->
            match tailFieldRead(nestedBody) with
                | Some(read) ->
                    match read with
                        | ExprQualifiedVar(owner, _field) ->
                            if owner == nested
                            then None
                            else Some(read)
                        | _ -> None
                | None -> None
        | _ -> None

let inTcoLoop (state: CoreLoweringState) =
    match stateTcoLoop(state) with
        | Some(_loop) -> true
        | None -> false

// Stage 0's `TransferDirectRuntimeManagedBranchResult` for a loop exit: an arm in the loop's tail
// returning a heap field of a loop parameter (`| [] -> state.refs`) hands out a child the exit's
// release of that parameter would free, so the read takes a reference of its own: an identity
// marker the loop's finalize makes a real retain for a parameter it places on the heap.
let transferLoopExitFieldResult (request: ConsumerRequest) (body: Expr) (temp: Int) (bodyType: SemanticType) (state: CoreLoweringState) =
    if request.tailPosition == false || inTcoLoop(state) == false || resultSurvivesReset(resolveType(state)(bodyType))(state)
    then (state, temp)
    else
        match tailFieldRead(body) with
            | None -> (state, temp)
            | Some(read) ->
                match loopParameterReadSlot(read)(state) with
                    | None -> (state, temp)
                    | Some(slot) ->
                        match freshTemp(state) with
                            | FreshTemp { state = allocated, temp = duplicate } ->
                                (allocated
                                |> emit(RcDup(duplicate)(temp)(false)(false))
                                |> (given (marked: CoreLoweringState) -> withStateTcoParameterRetainSites((duplicate, slot, bodyType) :: stateTcoParameterRetainSites(marked))(marked)), duplicate)

// An `if` branch's result as the join stores it (`transferIfBranchResult`), or, at the loop's
// exit, a heap field of a loop parameter with a reference of its own.
let transferIfBranchOrExitField (request: ConsumerRequest) (branch: Expr) (temp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match transferIfBranchResult(request.tailCall)(branch)(temp)(semanticType)(state) with
        | (transferred, branchTemp) ->
            if branchTemp != temp
            then (transferred, branchTemp)
            else transferLoopExitFieldResult(request)(branch)(temp)(semanticType)(transferred)

let lowerIfThenBranch thenBranch (request: ConsumerRequest) (normalizeStaticStrings: Bool) lower plan =
    match plan with
        | CoreIfPlan { state = failedState, error = Some(error) } ->
            CoreIfThen(
                state = failedState,
                resultSlot = -1,
                endLabel = "",
                thenType = SemNever,
                thenArm = unknownArmResult,
                error = Some(error)
            )
        | CoreIfPlan { state = thenState, resultSlot = resultSlot, elseLabel = elseLabel, endLabel = endLabel, error = None } ->
            match thenState
            |> withConsumerRequest(request)
            |> lowerInGeneralRcBranch(endLabel)("then")(lowerStaticStringNormalizedBody(thenBranch)(normalizeStaticStrings)(lower)) with
                | LoweredCoreValue { state = failedState, error = Some(error) } ->
                    CoreIfThen(
                        state = failedState,
                        resultSlot = resultSlot,
                        endLabel = endLabel,
                        thenType = SemNever,
                        thenArm = unknownArmResult,
                        error = Some(error)
                    )
                | LoweredCoreValue { state = resultState, temp = loweredTemp, semanticType = semanticType, error = None } ->
                    match normalizeParameterPassthroughBranch(thenBranch)(loweredTemp)(resultState) with
                        | (normalizedState, temp) ->
                            match transferIfBranchOrExitField(request)(thenBranch)(temp)(semanticType)(normalizedState) with
                                | (transferred, branchTemp) ->
                                    CoreIfThen(
                                        state = transferred
                                        |> emit(StoreLocal(resultSlot)(branchTemp))
                                        |> emit(Jump(endLabel))
                                        |> emit(Label(elseLabel)),
                                        resultSlot = resultSlot,
                                        endLabel = endLabel,
                                        thenType = semanticType,
                                        thenArm = matchArmResultOf(thenBranch)(branchTemp)(semanticType)(transferred),
                                        error = None
                                    )

let finishIfElseBranch (successorJoin: Bool) elseBranch (request: ConsumerRequest) (normalizeStaticStrings: Bool) lower loweredThen =
    match loweredThen with
        | CoreIfThen { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | CoreIfThen { state = elseState, resultSlot = resultSlot, endLabel = endLabel, thenType = thenType, thenArm = thenArm, error = None } ->
            match lowerInGeneralRcBranch(endLabel)("else")(lowerStaticStringNormalizedBody(elseBranch)(normalizeStaticStrings)(lower))(withConsumerRequest((request with expectedType = Some(thenType)))(elseState)) with
                | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                | LoweredCoreValue { state = resultState, temp = loweredTemp, semanticType = elseType, error = None } ->
                    match bindType(thenType)(elseType)(resultState) with
                        | (failedState, Some(error)) -> failure(failedState)(error)
                        | (typedState, None) ->
                            match normalizeParameterPassthroughBranch(elseBranch)(loweredTemp)(typedState) with
                                | (normalizedState, temp) ->
                                    match transferIfBranchOrExitField(request)(elseBranch)(temp)(elseType)(normalizedState) with
                                        | (transferred, branchTemp) ->
                                            match freshTemp(transferred) with
                                                | FreshTemp { state = targetState, temp = target } ->
                                                    targetState
                                                    |> emit(StoreLocal(resultSlot)(branchTemp))
                                                    |> emit(Label(endLabel))
                                                    |> emit(LoadLocal(target)(resultSlot))
                                                    |> normalizeMixedJoinArmsAs(successorJoin)(resultSlot)(thenType)([thenArm, matchArmResultOf(elseBranch)(branchTemp)(elseType)(transferred)])
                                                    |> (given (joined: (CoreLoweringState, List(MatchArmResult))) ->
                                                        match joined with
                                                            | (normalized, joinedArms) ->
                                                                normalized
                                                                |> markControlFlowJoin(target)(joinedArms)
                                                                |> success(target)(resolveType(transferred)(thenType)))

// The then branch inherits the context's expected type; the else branch is expected to have the
// then branch's type.
// Stage 0's `WithReconcilableFreshStringJoinRequest`: a reconcilable join asks its branches for
// a runtime-managed string whatever its consumer asked for, so the fresh branch produces its
// value on the reference-counted heap and the literal branches copy theirs, and the join is
// uniformly runtime-managed wherever it flows.
let withReconcilableFreshStringJoinRequest (join: Expr) (request: ConsumerRequest) (state: CoreLoweringState) =
    if ordinaryRcPlacementAllowed(state) && request.runtimeString == false && isReconcilableFreshStringJoin(join)(state)
    then request with runtimeString = true
    else request

let lowerIf condition thenBranch elseBranch lower state =
    (let request =
        state
        |> consumerRequestOf
        |> branchRequest
        |> (given (branch: ConsumerRequest) ->
            withReconcilableFreshStringJoinRequest(ExprIf(condition)(thenBranch)(elseBranch))(branch)(state))
    in
        let normalizeStaticStrings = ifBranchesNormalizeStaticStrings(thenBranch)(elseBranch)(request)(state)
        in
            state
            |> clearConsumerRequest
            |> lower(condition)
            |> prepareIfPlan
            |> lowerIfThenBranch(thenBranch)(request)(normalizeStaticStrings)(lower)
            |> finishIfElseBranch(isArenaSuccessorJoin(state))(elseBranch)(request)(normalizeStaticStrings)(lower))

let patternName pattern =
    match pattern with
        | PatternEmptyList -> "empty list"
        | PatternCons(_, _) -> "list cons"
        | PatternTuple(_) -> "tuple"
        | PatternConstructor(_, _) -> "constructor"
        | PatternRecord(_, _) -> "record"
        | PatternAs(_, _) -> "as"
        | PatternOr(_) -> "or"
        | _ -> "pattern"

let finishPatternComparison valueTemp valueType failLabel comparison loweredConstant =
    match loweredConstant with
        | LoweredCoreValue { state = failedState, error = Some(error) } ->
            LoweredCorePattern(
                state = failedState,
                error = Some(error)
            )
        | LoweredCoreValue { state = constantState, temp = constantTemp, semanticType = constantType, error = None } ->
            match bindType(valueType)(constantType)(constantState) with
                | (failedState, Some(error)) -> LoweredCorePattern(state = failedState, error = Some(error))
                | (typedState, None) ->
                    match freshTemp(typedState) with
                        | FreshTemp { state = compareState, temp = compareTemp } ->
                            LoweredCorePattern(
                                state = compareState
                                |> emit(comparison(compareTemp)(valueTemp)(constantTemp))
                                |> emit(JumpIfFalse(compareTemp)(failLabel)),
                                error = None
                            )

let recursive coreRecordPatterns fields =
    match fields with
        | [] -> []
        | (_fieldName, pattern) :: rest -> pattern :: coreRecordPatterns(rest)

// A bare name the parser wrote as a variable pattern but that names a nullary constructor is
// that constructor's pattern, never a binder.
let isNullaryConstructorPatternName (name: Str) state =
    match constructorLayout(name)(state) with
        | Some(CoreConstructorLayout { scheme = TypeScheme { body = SemFunction(_, _, _) } }) -> false
        | Some(_) -> true
        | None -> false

// The pattern owner fact a binder under the span key `key` carries inside the active loop body:
// the loop function's classified fact for that binder, when it needs a protective reference. A
// binder the syntax tree carries no span for (key -1) matches no fact, nor does any binder of a
// function that may run under a live handler post (stage 0 tracks no pattern owner there).
let patternOwnerFactFor (key: Int) (state: CoreLoweringState) =
    match (stateTcoLoopFrame(state), stateTcoLoop(state)) with
        | (Some(_frame), Some(loop)) ->
            if key < 0 || ordinaryRcPlacementAllowed(state) == false
            then None
            else
                match lookupPatternBindingFact(key)(loop.patternFacts) with
                    | Some(fact) ->
                        if patternFactRequiresProtection(fact)
                        then Some(fact)
                        else None
                    | None -> None
        | _ -> None

let bindPreparedPatternName (name: Str) (key: Int) (state: CoreLoweringState) =
    match freshType(state) with
        | FreshType { state = typedState, semanticType = semanticType } ->
            match freshLocal(typedState) with
                | FreshLocal { state = localState, local = local } ->
                    match patternOwnerFactFor(key)(localState) with
                        | Some(fact) ->
                            addPatternOwnerBinding(name)(emptyScheme(semanticType))(CoreLocal(local))(fact)(localState)
                        | None ->
                            addOwnedBinding(name)(emptyScheme(semanticType))(CoreLocal(local))(localState)

let recursive unkeyedPatterns (patterns: List(Pattern)) =
    match patterns with
        | [] -> []
        | pattern :: rest -> (-1, pattern) :: unkeyedPatterns(rest)

// Every binder is prepared under the start offset of its nearest enclosing `PatternAt`, the key
// the loop function's pattern-binding facts are recorded by.
let recursive prepareCorePatternBindings (pending: List((Int, Pattern))) seen state =
    match pending with
        | [] -> state
        | (key, pattern) :: rest ->
            match pattern with
                | PatternAt(TextSpan { start = start }, inner) -> prepareCorePatternBindings((start, inner) :: rest)(seen)(state)
                | PatternVar(name) ->
                    if containsName(name)(seen) || isNullaryConstructorPatternName(name)(state)
                    then prepareCorePatternBindings(rest)(seen)(state)
                    else
                        state
                        |> bindPreparedPatternName(name)(key)
                        |> prepareCorePatternBindings(rest)(name :: seen)
                | PatternCons(head, tail) -> prepareCorePatternBindings((-1, head) :: (-1, tail) :: rest)(seen)(state)
                | PatternTuple(elements) ->
                    prepareCorePatternBindings(append(unkeyedPatterns(elements))(rest))(seen)(state)
                | PatternConstructor(_name, elements) ->
                    prepareCorePatternBindings(append(unkeyedPatterns(elements))(rest))(seen)(state)
                | PatternRecord(_name, fields) ->
                    prepareCorePatternBindings(append(fields
                    |> coreRecordPatterns
                    |> unkeyedPatterns)(rest))(seen)(state)
                | PatternAs(inner, name) ->
                    if containsName(name)(seen)
                    then prepareCorePatternBindings((-1, inner) :: rest)(seen)(state)
                    else
                        state
                        |> bindPreparedPatternName(name)(key)
                        |> prepareCorePatternBindings((-1, inner) :: rest)(name :: seen)
                | PatternOr(first :: _alternatives) -> prepareCorePatternBindings((key, first) :: rest)(seen)(state)
                | _ -> prepareCorePatternBindings(rest)(seen)(state)

let preparePattern pattern state = prepareCorePatternBindings([(-1, pattern)])([])(state)

let lowerPatternVariable name valueTemp valueType state =
    match state with
        | CoreLoweringState { bindings = bindings } ->
            match lookupBinding(name)(bindings) with
                | Some(CoreBinding { name = _bindingName, scheme = TypeScheme { quantified = _quantified, body = bindingType, constraints = _constraints }, location = CoreLocal(local) }) ->
                    match bindType(bindingType)(valueType)(state) with
                        | (failedState, Some(error)) -> LoweredCorePattern(state = failedState, error = Some(error))
                        | (typedState, None) ->
                            LoweredCorePattern(
                                state = emit(StoreLocal(local)(valueTemp))(typedState),
                                error = None
                            )
                | None ->
                    LoweredCorePattern(
                        state = state,
                        error = Some(UnsupportedCoreLoweringPattern("missing prepared variable " + name))
                    )
                | Some(_binding) ->
                    LoweredCorePattern(
                        state = state,
                        error = Some(UnsupportedCoreLoweringPattern("invalid prepared variable " + name))
                    )

let recursive freshCoreTypes count reversed state =
    if count <= 0
    then FreshCoreTypes(state = state, semanticTypes = reverse(reversed))
    else
        match freshType(state) with
            | FreshType { state = nextState, semanticType = semanticType } ->
                freshCoreTypes(
                    count - 1,
                    semanticType :: reversed,
                    nextState
                )

let finishPatternNonZero valueTemp failLabel zero =
    match zero with
        | LoweredCoreValue { state = state, temp = zeroTemp } ->
            match freshTemp(state) with
                | FreshTemp { state = compareState, temp = compareTemp } ->
                    LoweredCorePattern(
                        state = compareState
                        |> emit(CmpIntNe(compareTemp)(valueTemp)(zeroTemp))
                        |> emit(JumpIfFalse(compareTemp)(failLabel)),
                        error = None
                    )

let requirePatternNonZero valueTemp failLabel state =
    state
    |> lowerGuardConstant(given (target) -> LoadConstInt(target)(0))(SemInt)
    |> finishPatternNonZero(valueTemp)(failLabel)

let finishEmptyListPattern valueTemp valueType failLabel fresh =
    match fresh with
        | FreshType { state = typedState, semanticType = elementType } ->
            match bindType(valueType)(SemList(elementType))(typedState) with
                | (failedState, Some(error)) -> LoweredCorePattern(state = failedState, error = Some(error))
                | (state, None) ->
                    state
                    |> lowerGuardConstant(given (target) -> LoadConstInt(target)(0))(SemInt)
                    |> finishPatternComparison(valueTemp)(SemInt)(failLabel)(CmpIntEq)

let lowerEmptyListPattern valueTemp valueType failLabel state =
    state
    |> freshType
    |> finishEmptyListPattern(valueTemp)(valueType)(failLabel)

let lowerLoadedPattern pattern valueType failLabel lowerPattern loaded =
    match loaded with
        | FreshTemp { state = state, temp = temp } -> lowerPattern(pattern)(temp)(valueType)(failLabel)(state)

let loadTuplePatternField valueTemp index pattern valueType failLabel lowerPattern state =
    state
    |> freshTemp
    |> (given (fresh) ->
        match fresh with
            | FreshTemp { state = loadState, temp = temp } ->
                FreshTemp(
                    state = emit(LoadMemOffset(temp)(valueTemp)(index * 8))(loadState),
                    temp = temp
                ))
    |> lowerLoadedPattern(pattern)(valueType)(failLabel)(lowerPattern)

let recursive lowerTuplePatternFields patterns types valueTemp index failLabel lowerPattern result =
    match (patterns, types, result) with
        | (_patterns, _types, LoweredCorePattern { error = Some(_error) }) -> result
        | ([], [], _) -> result
        | (pattern :: patternRest, semanticType :: typeRest, LoweredCorePattern { state = state }) ->
            state
            |> loadTuplePatternField(valueTemp)(index)(pattern)(semanticType)(failLabel)(lowerPattern)
            |> lowerTuplePatternFields(patternRest)(typeRest)(valueTemp)(index + 1)(failLabel)(lowerPattern)
        | _ -> result

let finishTuplePattern pattern valueTemp valueType failLabel lowerPattern fresh =
    match (pattern, fresh) with
        | (PatternTuple(patterns), FreshCoreTypes { state = state, semanticTypes = types }) ->
            match bindType(valueType)(SemTuple(types))(state) with
                | (failedState, Some(error)) -> LoweredCorePattern(state = failedState, error = Some(error))
                | (typedState, None) ->
                    let lowerFields = lowerTuplePatternFields(patterns)(types)(valueTemp)(0)(failLabel)
                    in lowerFields(lowerPattern)(LoweredCorePattern(state = typedState, error = None))
        | (_pattern, FreshCoreTypes { state = state }) ->
            LoweredCorePattern(
                state = state,
                error = Some(UnsupportedCoreLoweringPattern("tuple"))
            )

let lowerTuplePattern pattern valueTemp valueType failLabel lowerPattern state =
    match pattern with
        | PatternTuple(patterns) ->
            state
            |> freshCoreTypes(coreListLength(patterns))([])
            |> finishTuplePattern(pattern)(valueTemp)(valueType)(failLabel)(lowerPattern)
        | _ -> LoweredCorePattern(state = state, error = Some(UnsupportedCoreLoweringPattern("tuple")))

let finishConsPatternTail tailPattern tailTemp listType failLabel lowerPattern headResult =
    match headResult with
        | LoweredCorePattern { error = Some(_error) } -> headResult
        | LoweredCorePattern { state = state, error = None } ->
            lowerPattern(
                tailPattern,
                tailTemp,
                listType,
                failLabel,
                state
            )

let lowerConsPatternFields headPattern tailPattern valueTemp elementType failLabel lowerPattern checked =
    match checked with
        | LoweredCorePattern { error = Some(_error) } -> checked
        | LoweredCorePattern { state = state, error = None } ->
            match freshTemp(state) with
                | FreshTemp { state = headState, temp = headTemp } ->
                    match freshTemp(headState) with
                        | FreshTemp { state = tailState, temp = tailTemp } ->
                            let loadedState =
                                tailState
                                |> emit(LoadMemOffset(headTemp)(valueTemp)(0))
                                |> emit(LoadMemOffset(tailTemp)(valueTemp)(8))
                            in
                                loadedState
                                |> lowerPattern(headPattern)(headTemp)(elementType)(failLabel)
                                |> finishConsPatternTail(
                                    tailPattern,
                                    tailTemp,
                                    SemList(elementType),
                                    failLabel,
                                    lowerPattern
                                )

let finishConsPattern headPattern tailPattern valueTemp valueType failLabel lowerPattern fresh =
    match fresh with
        | FreshType { state = state, semanticType = elementType } ->
            match bindType(valueType)(SemList(elementType))(state) with
                | (failedState, Some(error)) -> LoweredCorePattern(state = failedState, error = Some(error))
                | (typedState, None) ->
                    typedState
                    |> requirePatternNonZero(valueTemp)(failLabel)
                    |> lowerConsPatternFields(headPattern)(tailPattern)(valueTemp)(elementType)(failLabel)(lowerPattern)

let lowerConsPattern headPattern tailPattern valueTemp valueType failLabel lowerPattern state =
    state
    |> freshType
    |> finishConsPattern(headPattern)(tailPattern)(valueTemp)(valueType)(failLabel)(lowerPattern)

let lowerAdtPatternField valueTemp index tagless pattern semanticType failLabel lowerPattern result =
    match result with
        | LoweredCorePattern { error = Some(_error) } -> result
        | LoweredCorePattern { state = state, error = None } ->
            match freshTemp(state) with
                | FreshTemp { state = loadState, temp = fieldTemp } ->
                    loadState
                    |> emit(GetAdtField(fieldTemp)(valueTemp)(index)(tagless))
                    |> lowerPattern(pattern)(fieldTemp)(semanticType)(failLabel)

let recursive lowerAdtPatternFields patterns types valueTemp index tagless failLabel lowerPattern result =
    match (patterns, types) with
        | ([], []) -> result
        | (pattern :: patternRest, semanticType :: typeRest) ->
            result
            |> lowerAdtPatternField(valueTemp)(index)(tagless)(pattern)(semanticType)(failLabel)(lowerPattern)
            |> lowerAdtPatternFields(patternRest)(typeRest)(valueTemp)(index + 1)(tagless)(failLabel)(lowerPattern)
        | _ -> result

// An ordinary (tagged) constructor value is a heap pointer that may be null; stage 0 guards every
// tag test with `ptr != 0` first (`EmitRequireNonZero`: zero constant, `CmpIntNe`, jump to the
// arm's fail target on false), and this codegen's null-checked tag read depends on it. The zero
// temp is allocated before the comparison temp, matching stage 0's temp order exactly.
let requireNonZeroPattern valueTemp failLabel state =
    match freshTemp(state) with
        | FreshTemp { state = zeroState, temp = zeroTemp } ->
            match freshTemp(zeroState) with
                | FreshTemp { state = compareState, temp = compareTemp } ->
                    compareState
                    |> emit(LoadConstInt(zeroTemp)(0))
                    |> emit(CmpIntNe(compareTemp)(valueTemp)(zeroTemp))
                    |> emit(JumpIfFalse(compareTemp)(failLabel))

// The tag test allocates its temps in stage 0's `EmitRequireTagMatch` order — tag, compare,
// THEN the expected constant — which is the reverse of every other pattern comparison (a literal
// pattern lowers its constant first, then compares). `finishPatternComparison` would allocate the
// compare temp last, so the tag test emits its own three instructions.
let finishTaggedConstructorTag valueTemp tag failLabel state =
    match freshTemp(state) with
        | FreshTemp { state = tagState, temp = tagTemp } ->
            match freshTemp(tagState) with
                | FreshTemp { state = compareState, temp = compareTemp } ->
                    match freshTemp(compareState) with
                        | FreshTemp { state = expectedState, temp = expectedTemp } ->
                            LoweredCorePattern(
                                state = expectedState
                                |> emit(GetAdtTag(tagTemp)(valueTemp))
                                |> emit(LoadConstInt(expectedTemp)(tag))
                                |> emit(CmpIntEq(compareTemp)(tagTemp)(expectedTemp))
                                |> emit(JumpIfFalse(compareTemp)(failLabel)),
                                error = None
                            )

// A tagless cell is always its type's one constructor: there is no tag to test, and no temp is
// allocated, exactly as stage 0's `EmitRequireTagMatch` returns before allocating any.
let finishConstructorTag valueTemp tag tagless failLabel state =
    if tagless
    then LoweredCorePattern(state = state, error = None)
    else finishTaggedConstructorTag(valueTemp)(tag)(failLabel)(state)

let lowerZeroCostPattern patterns parameterTypes valueTemp failLabel lowerPattern state =
    match (patterns, parameterTypes) with
        | (pattern :: [], semanticType :: []) -> lowerPattern(pattern)(valueTemp)(semanticType)(failLabel)(state)
        | _ ->
            LoweredCorePattern(
                state = state,
                error = Some(UnsupportedCoreLoweringPattern("zero-cost constructor arity"))
            )

let finishConstructorPattern patterns valueTemp valueType failLabel lowerPattern shape =
    match shape with
        | CoreConstructorShape { state = state, layout = CoreConstructorLayout { isZeroCost = true }, parameterTypes = parameterTypes, resultType = resultType } ->
            match bindType(valueType)(resultType)(state) with
                | (failedState, Some(error)) -> LoweredCorePattern(state = failedState, error = Some(error))
                | (typedState, None) ->
                    lowerZeroCostPattern(
                        patterns,
                        parameterTypes,
                        valueTemp,
                        failLabel,
                        lowerPattern,
                        typedState
                    )
        | CoreConstructorShape { state = state, layout = CoreConstructorLayout { name = constructorName, tag = tag, tagless = tagless }, parameterTypes = parameterTypes, resultType = resultType } ->
            if coreListLength(patterns) != coreListLength(parameterTypes)
            then
                LoweredCorePattern(state = state, error = patterns
                |> coreListLength
                |> CoreConstructorPatternArityMismatch(constructorName)(coreListLength(parameterTypes))
                |> Some)
            else
                match bindType(valueType)(resultType)(state) with
                    | (failedState, Some(error)) -> LoweredCorePattern(state = failedState, error = Some(error))
                    | (typedState, None) ->
                        typedState
                        |> requireNonZeroPattern(valueTemp)(failLabel)
                        |> finishConstructorTag(valueTemp)(tag)(tagless)(failLabel)
                        |> lowerAdtPatternFields(patterns)(parameterTypes)(valueTemp)(0)(tagless)(failLabel)(lowerPattern)

let lowerConstructorPattern name patterns valueTemp valueType failLabel lowerPattern state =
    match constructorLayout(name)(state) with
        | None ->
            LoweredCorePattern(
                state = state,
                error = Some(UnsupportedCoreLoweringPattern("unknown constructor " + name))
            )
        | Some(layout) ->
            state
            |> instantiateConstructor(layout)
            |> finishConstructorPattern(patterns)(valueTemp)(valueType)(failLabel)(lowerPattern)

let recursive findPatternField (name: Str) (fieldNames: List(Str)) (fieldTypes: List(SemanticType)) index =
    match (fieldNames, fieldTypes) with
        | (fieldName :: fieldRest, fieldType :: typeRest) ->
            if name == fieldName
            then Some(CorePatternField(index = index, semanticType = fieldType))
            else findPatternField(name)(fieldRest)(typeRest)(index + 1)
        | _ -> None

let recursive lowerRecordPatternFields typeName fields fieldNames fieldTypes valueTemp tagless failLabel lowerPattern result =
    match (fields, result) with
        | ([], _) -> result
        | (_fields, LoweredCorePattern { error = Some(_error) }) -> result
        | ((fieldName, pattern) :: rest, LoweredCorePattern { state = state, error = None }) ->
            match findPatternField(fieldName)(fieldNames)(fieldTypes)(0) with
                | None ->
                    LoweredCorePattern(state = state, error = fieldName
                    |> UnknownCoreRecordPatternField(typeName)
                    |> Some)
                | Some(CorePatternField { index = index, semanticType = semanticType }) ->
                    result
                    |> lowerAdtPatternField(valueTemp)(index)(tagless)(pattern)(semanticType)(failLabel)(lowerPattern)
                    |> lowerRecordPatternFields(typeName)(rest)(fieldNames)(fieldTypes)(valueTemp)(tagless)(failLabel)(lowerPattern)

let finishRecordPattern fields valueTemp valueType failLabel lowerPattern shape =
    match shape with
        | CoreConstructorShape { state = state, layout = CoreConstructorLayout { name = typeName, tag = tag, fieldNames = fieldNames, tagless = tagless }, parameterTypes = fieldTypes, resultType = resultType } ->
            match bindType(valueType)(resultType)(state) with
                | (failedState, Some(error)) -> LoweredCorePattern(state = failedState, error = Some(error))
                | (typedState, None) ->
                    typedState
                    |> requireNonZeroPattern(valueTemp)(failLabel)
                    |> finishConstructorTag(valueTemp)(tag)(tagless)(failLabel)
                    |> lowerRecordPatternFields(typeName)(fields)(fieldNames)(fieldTypes)(valueTemp)(tagless)(failLabel)(lowerPattern)

let lowerRecordPattern name fields valueTemp valueType failLabel lowerPattern state =
    match constructorLayout(name)(state) with
        | None ->
            LoweredCorePattern(
                state = state,
                error = Some(UnsupportedCoreLoweringPattern("unknown record " + name))
            )
        | Some(layout) ->
            state
            |> instantiateConstructor(layout)
            |> finishRecordPattern(fields)(valueTemp)(valueType)(failLabel)(lowerPattern)

// The names a pattern binds, the first alternative of an or-pattern standing for all of them.
let recursive aliasCheckBoundNames (pattern: Pattern) (names: List(Str)) =
    match pattern with
        | PatternAt(_span, inner) -> aliasCheckBoundNames(inner)(names)
        | PatternVar(name) -> name :: names
        | PatternCons(head, tail) ->
            names
            |> aliasCheckBoundNames(head)
            |> aliasCheckBoundNames(tail)
        | PatternTuple(patterns) -> aliasCheckBoundNamesIn(patterns)(names)
        | PatternConstructor(_name, patterns) -> aliasCheckBoundNamesIn(patterns)(names)
        | PatternRecord(_name, fields) -> aliasCheckFieldNames(fields)(names)
        | PatternAs(inner, name) -> aliasCheckBoundNames(inner)(name :: names)
        | PatternOr(first :: _rest) -> aliasCheckBoundNames(first)(names)
        | _ -> names
and aliasCheckBoundNamesIn (patterns: List(Pattern)) (names: List(Str)) =
    match patterns with
        | [] -> names
        | pattern :: rest ->
            names
            |> aliasCheckBoundNames(pattern)
            |> aliasCheckBoundNamesIn(rest)
and aliasCheckFieldNames (fields: List((Str, Pattern))) (names: List(Str)) =
    match fields with
        | [] -> names
        | (_field, pattern) :: rest ->
            names
            |> aliasCheckBoundNames(pattern)
            |> aliasCheckFieldNames(rest)

let recursive constructorLayoutSchemes (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> []
        | CoreConstructorLayout { scheme = scheme } :: rest -> scheme :: constructorLayoutSchemes(rest)

// Whether a value of the type transitively owns a resource (stage 0's `IsResourceBearing`).
let isResourceBearingIn (semanticType: SemanticType) (state: CoreLoweringState) =
    semanticType
    |> resolveType(state)
    |> isResourceBearingType(given (name) -> isResourceTypeNameIn(name)(state))(state
    |> stateConstructorLayouts
    |> constructorLayoutSchemes)([])

let recursive anyResourceBearingBinding (names: List(Str)) (state: CoreLoweringState) =
    match names with
        | [] -> false
        | name :: rest ->
            match lookupBinding(name)(state.bindings) with
                | Some(CoreBinding { scheme = TypeScheme { body = bindingType } }) -> isResourceBearingIn(bindingType)(state) || anyResourceBearingBinding(rest)(state)
                | None -> anyResourceBearingBinding(rest)(state)

// Stage 0's pattern-alias rule: an alias of a resource-bearing value cannot stand beside a nested
// binding that also bears a resource, since both would own it.
let checkResourceAlias name inner valueType (lowered: LoweredCorePattern) =
    match lowered with
        | LoweredCorePattern { error = Some(_error) } -> lowered
        | LoweredCorePattern { state = state, error = None } ->
            if isResourceBearingIn(valueType)(state) && anyResourceBearingBinding(aliasCheckBoundNames(inner)([]))(state)
            then lowered with error = Some(ResourcePatternAliasConflict("Resource-bearing pattern alias '" + name + "' cannot coexist with a nested resource-bearing binding."))
            else lowered

let finishAsPattern name valueTemp valueType inner =
    match inner with
        | LoweredCorePattern { error = Some(_error) } -> inner
        | LoweredCorePattern { state = state, error = None } -> lowerPatternVariable(name)(valueTemp)(valueType)(state)

let finishOrAlternative successLabel result =
    match result with
        | LoweredCorePattern { error = Some(_error) } -> result
        | LoweredCorePattern { state = state, error = None } ->
            LoweredCorePattern(
                state = emit(Jump(successLabel))(state),
                error = None
            )

let labelOrAlternative nextLabel lowered =
    match lowered with
        | LoweredCorePattern { state = state, error = error } ->
            LoweredCorePattern(
                state = emit(Label(nextLabel))(state),
                error = error
            )

let recursive lowerOrAlternatives alternatives valueTemp valueType failLabel successLabel lowerPattern result =
    match (alternatives, result) with
        | (_alternatives, LoweredCorePattern { error = Some(_error) }) -> result
        | ([], _) -> result
        | (alternative :: [], LoweredCorePattern { state = state, error = None }) ->
            match lowerPattern(alternative)(valueTemp)(valueType)(failLabel)(state) with
                | LoweredCorePattern { state = finalState, error = error } ->
                    LoweredCorePattern(
                        state = emit(Label(successLabel))(finalState),
                        error = error
                    )
        | (alternative :: rest, LoweredCorePattern { state = state, error = None }) ->
            match freshLabel("pattern_or_next")(state) with
                | FreshLabel { state = labelState, label = nextLabel } ->
                    labelState
                    |> lowerPattern(alternative)(valueTemp)(valueType)(nextLabel)
                    |> finishOrAlternative(successLabel)
                    |> labelOrAlternative(nextLabel)
                    |> lowerOrAlternatives(rest)(valueTemp)(valueType)(failLabel)(successLabel)(lowerPattern)

let lowerOrPattern alternatives valueTemp valueType failLabel lowerPattern state =
    match freshLabel("pattern_or_match")(state) with
        | FreshLabel { state = labelState, label = successLabel } ->
            let lowerAlternatives = lowerOrAlternatives(alternatives)(valueTemp)(valueType)(failLabel)(successLabel)
            in lowerAlternatives(lowerPattern)(LoweredCorePattern(state = labelState, error = None))

let recursive lowerPattern pattern valueTemp valueType failLabel state =
    match pattern with
        | PatternAt(_span, inner) -> lowerPattern(inner)(valueTemp)(valueType)(failLabel)(state)
        | PatternWildcard -> LoweredCorePattern(state = state, error = None)
        | PatternVar(name) ->
            if isNullaryConstructorPatternName(name)(state)
            then lowerConstructorPattern(name)([])(valueTemp)(valueType)(failLabel)(lowerPattern)(state)
            else lowerPatternVariable(name)(valueTemp)(valueType)(state)
        | PatternEmptyList -> lowerEmptyListPattern(valueTemp)(valueType)(failLabel)(state)
        | PatternCons(head, tail) -> lowerConsPattern(head)(tail)(valueTemp)(valueType)(failLabel)(lowerPattern)(state)
        | PatternTuple(_elements) -> lowerTuplePattern(pattern)(valueTemp)(valueType)(failLabel)(lowerPattern)(state)
        | PatternConstructor(name, patterns) ->
            lowerConstructorPattern(
                name,
                patterns,
                valueTemp,
                valueType,
                failLabel,
                lowerPattern,
                state
            )
        | PatternRecord(name, fields) ->
            lowerRecordPattern(
                name,
                fields,
                valueTemp,
                valueType,
                failLabel,
                lowerPattern,
                state
            )
        | PatternAs(inner, name) ->
            state
            |> lowerPattern(inner)(valueTemp)(valueType)(failLabel)
            |> checkResourceAlias(name)(inner)(valueType)
            |> finishAsPattern(name)(valueTemp)(valueType)
        | PatternOr(alternatives) -> lowerOrPattern(alternatives)(valueTemp)(valueType)(failLabel)(lowerPattern)(state)
        | PatternInt(value) ->
            state
            |> lowerGuardConstant(given (target) -> LoadConstInt(target)(value))(SemInt)
            |> finishPatternComparison(valueTemp)(valueType)(failLabel)(CmpIntEq)
        | PatternRune(value) ->
            state
            |> lowerGuardConstant(given (target) -> LoadConstInt(target)(value))(SemRune)
            |> finishPatternComparison(valueTemp)(valueType)(failLabel)(CmpIntEq)
        | PatternBool(value) ->
            state
            |> lowerGuardConstant(given (target) -> LoadConstBool(target)(value))(SemBool)
            |> finishPatternComparison(valueTemp)(valueType)(failLabel)(CmpIntEq)
        | PatternString(value) ->
            state
            |> lowerGuardString(value)
            |> finishPatternComparison(valueTemp)(valueType)(failLabel)(CmpStrEq)
        | unsupported ->
            LoweredCorePattern(
                state = state,
                error = unsupported
                |> patternName
                |> UnsupportedCoreLoweringPattern
                |> Some
            )

let lowerMatchGuard guard failLabel lower patternResult =
    match (guard, patternResult) with
        | (_guard, LoweredCorePattern { state = failedState, error = Some(error) }) ->
            LoweredCoreValue(
                state = failedState,
                temp = -1,
                semanticType = SemNever,
                error = Some(error)
            )
        | (None, LoweredCorePattern { state = state, error = None }) -> success(-1)(SemNever)(state)
        | (Some(expression), LoweredCorePattern { state = state, error = None }) ->
            match lower(expression)(state) with
                | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                | LoweredCoreValue { state = guardState, temp = guardTemp, semanticType = guardType, error = None } ->
                    match bindType(SemBool)(guardType)(guardState) with
                        | (failedState, Some(error)) -> failure(failedState)(error)
                        | (typedState, None) ->
                            typedState
                            |> emit(JumpIfFalse(guardTemp)(failLabel))
                            |> success(-1)(SemNever)

// The bindings a match arm added over the bindings outside it, most recent first.
let recursive armBindings (count: Int) (bindings: List(CoreBinding)) =
    match bindings with
        | [] -> []
        | binding :: rest ->
            if count <= 0
            then []
            else binding :: armBindings(count - 1)(rest)

// A match arm's closing reset under the live-posts guard: the pre-restore slot is allocated
// first, as stage 0 does, then the guarded restore and reclaim.
let closeGuardedArmBracket cursorSlot endSlot (state: CoreLoweringState) =
    match freshLocal(state) with
        | FreshLocal { state = allocated, local = preRestoreSlot } ->
            match beginLivePostsGuard(allocated) with
                | (guarded, skipLabel) ->
                    guarded
                    |> emitRestoreAndReclaim(cursorSlot)(endSlot)(preRestoreSlot)
                    |> endLivePostsGuard(skipLabel)

// Whether an arm's closing bracket resets the arena, stage 0's `PopOwnershipScope` test: the
// result survives the reset, or already lives on the reference-counted heap.
let armResultSurvivesReset (resultTemp: Int) (resultType: SemanticType) (state: CoreLoweringState) =
    resultSurvivesReset(resultType)(state) || isRuntimeTemp(resultTemp)(state) || containsInt(resultTemp)(stateBackEdgeDummyTemps(state)) || isReuseResultTemp(resultTemp)(state)

// Stage 0's `EmitOwnedValueDrop` of a runtime-managed list owner whose heads are scalars: the
// owner is loaded into a cursor slot and its spine walked iteratively (`rcdrop_list_N`), a
// unique cell releasing its tail and freeing itself, a shared cell decremented once and keeping
// the rest of the spine. The walk stays at the arm exit: the placement pass moves only a single
// owner-slot release.
let emitScrutineeListRelease (ownerSlot: Int) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = loadState, temp = loadTemp } ->
            match freshLocal(loadState) with
                | FreshLocal { state = cursorState, local = cursorSlot } ->
                    match freshLabel("rcdrop_list")(cursorState) with
                        | FreshLabel { state = loopState, label = loopLabel } ->
                            match freshLabel("rcdrop_list_shared")(loopState) with
                                | FreshLabel { state = sharedState, label = sharedLabel } ->
                                    match freshLabel("rcdrop_list_end")(sharedState) with
                                        | FreshLabel { state = endState, label = endLabel } ->
                                            match freshTempRun(5)(endState) with
                                                | FreshTemp { state = walkState, temp = firstTemp } ->
                                                    walkState
                                                    |> emit(LoadLocal(loadTemp)(ownerSlot))
                                                    |> emit(StoreLocal(cursorSlot)(loadTemp))
                                                    |> emitListSpineWalkBody(cursorSlot)(loopLabel)(sharedLabel)(endLabel)(firstTemp)

// The owner a match makes for its scrutinee, stage 0's `TrackRuntimeManagedMatchScrutineeOwner`:
// a fresh runtime-managed value of an owned type, matched directly rather than read from a
// binding. A string or `Bytes` value releases as one owner drop, a list over scalars as a spine
// walk, and a tuple, a named type, or a list over heap elements through the inline structural
// walk of its release plan.
let scrutineeOwnerOf (scrutinee: Expr) (valueTemp: Int) (valueType: SemanticType) (state: CoreLoweringState) =
    if ordinaryRcPlacementAllowed(state) && scrutineeHasOwnerCandidate(scrutinee) && isRuntimeTemp(valueTemp)(state)
    then
        match resolveType(state)(valueType) with
            | SemString -> Some(MatchScrutineeOwner(scrutineeTypeName = "String", scrutineeIsList = false, scrutineeType = valueType))
            | SemBytes -> Some(MatchScrutineeOwner(scrutineeTypeName = "Bytes", scrutineeIsList = false, scrutineeType = valueType))
            | SemList(element) ->
                Some(MatchScrutineeOwner(scrutineeTypeName = "List", scrutineeIsList = element
                |> resolveType(state)
                |> canArenaResetLayout, scrutineeType = valueType))
            | SemTuple(_elements) -> Some(MatchScrutineeOwner(scrutineeTypeName = "Tuple", scrutineeIsList = false, scrutineeType = valueType))
            | SemNamed(_symbolId, _name, _arguments) as named ->
                match state |> stateOwnedTypeNameOf(named) with
                    | Some(typeName) -> Some(MatchScrutineeOwner(scrutineeTypeName = typeName, scrutineeIsList = false, scrutineeType = valueType))
                    | None -> None
            | _ -> None
    else None

// An owned value a match arm's pattern bound, stage 0's `TrackOwnedBindingsInPattern`: a
// resource by its name, slot, and resource type, any other heap-typed binding by its slot and
// owned type name. A binding whose type is still unresolved once the pattern is lowered owns
// nothing. The arm that matched a fresh runtime-managed scrutinee owns it too, by the owner slot
// it stored it in, its type name, whether the release walks a list spine, the temp that defined
// it, and its type (the structural dropper of a release placed before the arm result).
// A pattern owner (stage 0's Perceus pattern owner) is released under the owned type name its
// binding type resolves to by the arm's exit, or `PatternBinding` while it is unresolved.
type ArmOwner =
    | ArmResourceOwner(Str, Int, Str)
    | ArmHeapOwner(Int, Str)
    | ArmScrutineeOwner(Int, Str, Bool, Int, SemanticType)
    | ArmPatternOwner(Int, SemanticType)

let armOwnerOf (state: CoreLoweringState) (binding: CoreBinding) =
    match binding with
        | CoreBinding { location = CoreLocal(slot), patternOwner = Some(_fact), scheme = TypeScheme { body = bindingType } } ->
            bindingType
            |> ArmPatternOwner(slot)
            |> Some
        | CoreBinding { name = name, location = CoreLocal(slot), ownedRead = true, scheme = TypeScheme { body = bindingType } } ->
            bindingType
            |> resolveType(state)
            |> (given (resolved: SemanticType) ->
                match resourceTypeNameOf(resolved)(state) with
                    | Some(typeName) ->
                        typeName
                        |> ArmResourceOwner(name)(slot)
                        |> Some
                    | None ->
                        match state |> stateOwnedTypeNameOf(resolved) with
                            | Some(typeName) ->
                                typeName
                                |> ArmHeapOwner(slot)
                                |> Some
                            | None -> None)
        | _ -> None

let recursive armOwnersOf (state: CoreLoweringState) (bindings: List(CoreBinding)) =
    match bindings with
        | [] -> []
        | binding :: rest ->
            match armOwnerOf(state)(binding) with
                | Some(owner) -> owner :: armOwnersOf(state)(rest)
                | None -> armOwnersOf(state)(rest)

// The owned values an arm's pattern bound, in declaration order, read once the pattern is
// lowered and before its guard, when stage 0 tracks them.
let armOwners (outerBindings: List(CoreBinding)) (patternResult: LoweredCorePattern) =
    match patternResult with
        | LoweredCorePattern { state = state } ->
            state.bindings
            |> armBindings(length(state.bindings) - length(outerBindings))
            |> reverse
            |> armOwnersOf(state)

// An arm result that is the pattern-bound resource itself carries it out of the arm.
let armResultIsBinding (name: Str) (body: Expr) =
    match unspanArgument(body) with
        | ExprVar(candidate) -> candidate == name
        | _ -> false

// Whether an owner is still alive at the arm's exit: a heap owner always is, a resource unless
// the arm released or moved it, or its result carries it out.
let armOwnerAlive (body: Expr) (state: CoreLoweringState) owner =
    match owner with
        | ArmHeapOwner(_slot, _typeName) -> true
        | ArmScrutineeOwner(_slot, _typeName, _isList, _valueTemp, _scrutineeType) -> true
        | ArmPatternOwner(_slot, _bindingType) -> true
        | ArmResourceOwner(name, slot, _typeName) ->
            match resourceStateOf(slot)(state) with
                | Some(_kind) -> false
                | None -> !armResultIsBinding(name)(body)

let recursive anyArmOwnerAlive (body: Expr) (state: CoreLoweringState) (owners: List(ArmOwner)) =
    match owners with
        | [] -> false
        | owner :: rest ->
            if armOwnerAlive(body)(state)(owner)
            then true
            else anyArmOwnerAlive(body)(state)(rest)

// Stage 0's scope-exit drops for a match arm, emitted after the arm result is stored and before
// the arm bracket closes: a live resource the pattern bound is closed (or marked moved when the
// arm result carries it out), any other owned binding is released at the scope exit like an
// owned `let` (the placement pass moves the drop to its last use).
let recursive emitArmOwnerReleases (body: Expr) (owners: List(ArmOwner)) (state: CoreLoweringState) =
    match owners with
        | [] -> state
        | ArmHeapOwner(slot, typeName) :: rest ->
            state
            |> emitOwnedLetRelease(typeName)(slot)
            |> emitArmOwnerReleases(body)(rest)
        | ArmPatternOwner(slot, bindingType) :: rest ->
            state
            |> emitOwnedLetRelease(patternOwnerTypeName(resolveType(state)(bindingType))(state))(slot)
            |> emitArmOwnerReleases(body)(rest)
        | ArmScrutineeOwner(slot, _typeName, true, _valueTemp, _scrutineeType) :: rest ->
            match runtimeOwnerStateOf(slot)(state) with
                | Some(false) -> emitArmOwnerReleases(body)(rest)(state)
                | _ ->
                    state
                    |> emitScrutineeListRelease(slot)
                    |> retireRuntimeOwner(slot)
                    |> emitArmOwnerReleases(body)(rest)
        | ArmScrutineeOwner(slot, typeName, false, _valueTemp, _scrutineeType) :: rest ->
            state
            |> emitOwnedLetRelease(typeName)(slot)
            |> retireRuntimeOwner(slot)
            |> emitArmOwnerReleases(body)(rest)
        | ArmResourceOwner(name, slot, typeName) :: rest ->
            if armResultIsBinding(name)(body)
            then
                state
                |> markResourceReleased(slot)(ResourceMoved)
                |> emitArmOwnerReleases(body)(rest)
            else
                state
                |> emitResourceCleanup(typeName)(slot)
                |> emitArmOwnerReleases(body)(rest)

// The closing reset of an arm's bracket, stage 0's `PopOwnershipScope` at the arm exit: a
// surviving or runtime-managed result resets the arena; a heap result of an arm whose pattern
// owned a live value is copied past the reset when it has a copy-out kind, the copy replacing the
// result in the match's result slot; any other heap result leaves the window open.
// A pattern owner's retained reference as the arm's result crosses the reset like a
// runtime-managed value: the reset runs, and nothing is copied.
let closeRetainedResultBracket cursorSlot endSlot (state: CoreLoweringState) =
    match freshLocal(state) with
        | FreshLocal { state = allocated, local = preRestoreSlot } -> emitGuardedRestoreAndReclaim(cursorSlot)(endSlot)(preRestoreSlot)(allocated)

// An arm result that is the direct read of a loop parameter the frame places on the
// reference-counted heap by now: stage 0 tracks such a parameter as a runtime-managed owner, so
// its read is a reference-counted result and the arm's bracket resets.
let armResultReadsRuntimeManagedSlot (resultTemp: Int) (state: CoreLoweringState) =
    match (stateTcoLoopFrame(state), stateTcoLoop(state), loadedSlotOfTemp(resultTemp)(state.reversedInstructions)) with
        | (Some(frame), Some(loop), Some(slot)) -> containsInt(slot)(frame.parameterSlots) && loopSlotIsRuntimeManaged(slot)(frame)(loop)(state)
        | _ -> false

let closeArmBracket (bracket: ArenaBracket) (hadAliveOwner: Bool) resultSlot resultTemp resultType (state: CoreLoweringState) =
    match (state
    |> statePatternOwnerResultTemps
    |> containsInt(resultTemp), stateCapabilityGlobalCount(state) > 0 && armResultSurvivesReset(resultTemp)(resultType)(state), hadAliveOwner) with
        | (_retained, true, _owned) -> (closeGuardedArmBracket(bracket.bracketCursorSlot)(bracket.bracketEndSlot)(state), resultTemp)
        | (true, false, _owned) -> (closeRetainedResultBracket(bracket.bracketCursorSlot)(bracket.bracketEndSlot)(state), resultTemp)
        | (false, false, true) ->
            match closeOwnedScopeForResult(resultTemp)(resultType)(bracket.bracketCursorSlot)(bracket.bracketEndSlot)(state) with
                | (closed, Some(copyTemp)) ->
                    (emit(StoreLocal(resultSlot)(copyTemp))(closed), copyTemp)
                | (closed, None) -> (closed, resultTemp)
        | (false, false, false) ->
            if armResultReadsRuntimeManagedSlot(resultTemp)(state)
            then
                match freshLocal(state) with
                    | FreshLocal { state = allocated, local = preRestoreSlot } -> (emitGuardedRestoreAndReclaim(bracket.bracketCursorSlot)(bracket.bracketEndSlot)(preRestoreSlot)(allocated), resultTemp)
            else (closeScopeForResult(resultTemp)(resultType)(bracket.bracketCursorSlot)(bracket.bracketEndSlot)(state), resultTemp)

// Closes an arm's scope and returns the closed state with the temp the match slot finally holds:
// the copy when the close copied the result past the reset, the arm's own result otherwise.
let closeArmScope body owners bracket resultSlot resultTemp resultType (state: CoreLoweringState) =
    state
    |> emitArmOwnerReleases(body)(owners)
    |> closeArmBracket(bracket)(anyArmOwnerAlive(body)(state)(owners))(resultSlot)(resultTemp)(resultType)

// An arm that failed to lower contributes nothing to the match's result.
let failedMatchArm (failedState: CoreLoweringState) (error: CoreLoweringError) =
    LoweredMatchArm(
        lowered = failure(failedState)(error),
        armResult = MatchArmResult(armRuntimeManaged = false, armNewlyProduced = false, armRetainedOwner = false, armOwned = false, armStoreTemp = -1, armBorrowsLoopParameter = false, armFreshCell = false, armConsesOntoSelfResult = false, armJoinsSelfConsingArms = false)
    )

// A constructor application whose every argument is a literal or another such application, with
// the copy plan of its type: a value that owns nothing, built in the arena and copied to the
// reference-counted heap beside a retained sibling arm the way a literal string arm is (stage
// 0's `IsStaticConstructorArm`). Only a type the deep copy handles qualifies.
let recursive staticConstructorArm (expression: Expr) (state: CoreLoweringState) =
    match constructorApplicationOf(expression)([])(state) with
        | Some((layout, arguments)) ->
            if staticConstructorArguments(arguments)(state)
            then
                match layoutFieldTypes(layout)(state) with
                    | (_fieldTypes, resultType) -> argumentCopyPlanOf(resultType)(state)
            else None
        | None -> None
and staticConstructorArguments (arguments: List(Expr)) (state: CoreLoweringState) =
    match arguments with
        | [] -> true
        | argument :: rest -> staticConstructorArgument(argument)(state) && staticConstructorArguments(rest)(state)
and staticConstructorArgument (argument: Expr) (state: CoreLoweringState) =
    match unspanArgument(argument) with
        | ExprString(_value) -> true
        | ExprInt(_value) -> true
        | ExprBigInt(_value) -> true
        | ExprUInt(_value, _bits, _text) -> true
        | ExprFloat(_value, _text) -> true
        | ExprRune(_value) -> true
        | ExprBool(_value) -> true
        | other ->
            match staticConstructorArm(other)(state) with
                | Some(_plan) -> true
                | None -> false

let isStaticConstructorArm (expression: Expr) (state: CoreLoweringState) =
    match staticConstructorArm(expression)(state) with
        | Some(_plan) -> true
        | None -> false

// Stage 0's `LowerMatchArmExpression`: a literal string arm of a match whose arms are normalized
// is copied to the reference-counted heap as an RC-normalized `CopyOutArena` of the constant,
// the literal itself loaded at the match's own location; a static constructor arm is built in
// the arena as usual and deep-copied to the reference-counted heap, its literal string children
// included; any other arm body lowers as usual.
let lowerMatchArmBody body (normalizeStaticStrings: Bool) lower (state: CoreLoweringState) =
    match (normalizeStaticStrings, staticStringArmBody(body)) with
        | (true, Some(_value)) -> lowerStaticStringNormalizedBody(body)(true)(lower)(state)
        | _ ->
            match (normalizeStaticStrings, staticConstructorArm(body)(state)) with
                | (true, Some(plan)) ->
                    match lower(body)(state) with
                        | LoweredCoreValue { state = armState, temp = armTemp, semanticType = armType, error = None } ->
                            if canTestRepresentation(state)
                            then
                                // The copy marks its own temp. What the test answers with is the
                                // arm's own value when the runtime says it is already
                                // reference-counted, which this arm did not newly produce, so the
                                // scope it leaves closes the way an unmarked heap result closes it.
                                match emitReferenceOrCopy(armTemp)(emitArgumentDeepCopy(armTemp)(plan))(armState) with
                                    | (copied, copyTemp) -> success(copyTemp)(armType)(copied)
                            else
                                match emitArgumentDeepCopy(armTemp)(plan)(armState) with
                                    | (copied, copyTemp) ->
                                        copied
                                        |> markRuntimeTemp(copyTemp)(RuntimeNewlyProduced)
                                        |> success(copyTemp)(armType)
                        | failed -> failed
                | _ -> lower(body)(state)

let recursive armBindingSlotOf (name: Str) (bindings: List(CoreBinding)) =
    match bindings with
        | [] -> None
        | CoreBinding { name = candidate, location = CoreLocal(slot) } :: rest ->
            if candidate == name
            then Some(slot)
            else armBindingSlotOf(name)(rest)
        | _ :: rest -> armBindingSlotOf(name)(rest)

// Stage 0's `TransferVariableRuntimeManagedMatchResult`: an arm that returns a binding its own
// pattern bound as a runtime-managed owner hands the owner's one reference to the match result
// instead of borrowing it, so the arm exit releases nothing and the result needs no copy past
// the reset.
let transferArmBindingResult (body: Expr) (temp: Int) (outerBindings: List(CoreBinding)) (state: CoreLoweringState) =
    match tailForwardedVariable(body) with
        | None -> state
        | Some(name) ->
            match state.bindings
            |> armBindings(length(state.bindings) - length(outerBindings))
            |> armBindingSlotOf(name) with
                | None -> state
                | Some(slot) ->
                    match runtimeOwnerStateOf(slot)(state) with
                        | Some(true) -> transferRuntimeOwner(slot)(temp)(state)
                        | _ -> state

let recursive armScrutineeOwnerOf (owners: List(ArmOwner)) =
    match owners with
        | [] -> None
        | ArmScrutineeOwner(slot, typeName, isList, valueTemp, scrutineeType) :: _rest -> Some((slot, typeName, isList, valueTemp, scrutineeType))
        | _ :: rest -> armScrutineeOwnerOf(rest)

// Stage 0's `EmitRuntimeManagedParentFieldTransfer`: the child keeps a reference of its own, the
// owner is read once more (placement releases an owner right after its last use, and a unique
// owner's structural release would otherwise free the child before it was retained), and the
// owner is released whole through its structural dropper, its slot named so placement moves the
// release on every path through the match. The release names the owner's defining temp; the
// temp stage 0 spends on the owner's own release load stays reserved and unused.
let emitScrutineeChildTransfer (childTemp: Int) (childType: SemanticType) (ownerSlot: Int) (typeName: Str) (valueTemp: Int) (scrutineeType: SemanticType) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = duplicateState, temp = duplicate } ->
            match freshTempRun(2)(duplicateState) with
                | FreshTemp { state = loadState, temp = loadTemp } ->
                    match synthesizeStructuralDropperLabel(scrutineeType)(loadState) with
                        | (dropperLabel, dropperState) ->
                            dropperState
                            |> emit(childType
                            |> resolveType(state)
                            |> mayBeEmptyList
                            |> RcDup(duplicate)(childTemp)(true))
                            |> markRuntimeTemp(duplicate)(RuntimeNewlyProduced)
                            |> emit(LoadLocal(loadTemp)(ownerSlot))
                            |> emit(RcDrop(valueTemp)(typeName)(ownerSlot)(true)(scrutineeType
                            |> resolveType(state)
                            |> mayBeEmptyList)(dropperLabel))
                            |> (given (released: CoreLoweringState) ->
                                (withStateRuntimeOwners(released
                                |> stateRuntimeOwners
                                |> releaseRuntimeOwner(ownerSlot))(released), duplicate))

// Stage 0's `TransferVariableRuntimeManagedMatchResult` for a child of the scrutinee owner: an
// arm whose result is a heap value its own pattern bound out of the fresh scrutinee (the head of
// a matched list) hands a retained reference to the match result and releases the owner right
// there, so the arm exit releases nothing more; a scalar child owns nothing, and a binding that
// is an owner itself takes the whole-binding transfer instead.
let transferScrutineeChildResult (body: Expr) (temp: Int) (bodyType: SemanticType) (outerBindings: List(CoreBinding)) (owners: List(ArmOwner)) (state: CoreLoweringState) =
    match (tailForwardedVariable(body), armScrutineeOwnerOf(owners)) with
        | (Some(name), Some((ownerSlot, typeName, _isList, valueTemp, scrutineeType))) ->
            match state.bindings
            |> armBindings(length(state.bindings) - length(outerBindings))
            |> armBindingSlotOf(name) with
                | Some(slot) ->
                    if runtimeOwnerStateOf(slot)(state) == Some(true) || resultSurvivesReset(resolveType(state)(bodyType))(state)
                    then (state, temp)
                    else emitScrutineeChildTransfer(temp)(bodyType)(ownerSlot)(typeName)(valueTemp)(scrutineeType)(state)
                | None -> (state, temp)
        | _ -> (state, temp)

// Stage 0's `TransferDirectRuntimeManagedBranchResult` for a match arm: an arm returning a pattern
// owner of a loop parameter takes a duplicate of its read, as an `if` branch does, since the
// loop's exit releases the parameter that owns it (`| ([], acc) -> acc` over a tuple parameter);
// an arm at the loop's exit returning a heap field of a loop parameter retains it; any other arm
// result transfers a scrutinee child or the arm's own binding.
let transferArmResult (request: ConsumerRequest) (body: Expr) (temp: Int) (bodyType: SemanticType) (outerBindings: List(CoreBinding)) (owners: List(ArmOwner)) (state: CoreLoweringState) =
    match transferBranchOwnerResult(body)(temp)(state) with
        | (duplicated, duplicate) ->
            if duplicate != temp
            then (duplicated, duplicate)
            else
                match transferLoopExitFieldResult(request)(body)(temp)(bodyType)(state) with
                    | (retained, retainedTemp) ->
                        if retainedTemp != temp
                        then (retained, retainedTemp)
                        else
                            match transferScrutineeChildResult(body)(temp)(bodyType)(outerBindings)(owners)(state) with
                                | (transferred, resultTemp) -> (transferArmBindingResult(body)(resultTemp)(outerBindings)(transferred), resultTemp)

let finishMatchArm body (normalizeStaticStrings: Bool) resultSlot endLabel (armLabel: Str) resultType (request: ConsumerRequest) outerBindings bracket owners lower guarded =
    match guarded with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failedMatchArm(failedState)(error)
        | LoweredCoreValue { state = bodyState, error = None } ->
            match bodyState
            |> withConsumerRequest(request)
            |> lowerInGeneralRcBranch(endLabel)(armLabel)(lowerMatchArmBody(body)(normalizeStaticStrings)(lower)) with
                | LoweredCoreValue { state = failedState, error = Some(error) } -> failedMatchArm(failedState)(error)
                | LoweredCoreValue { state = resultState, temp = temp, semanticType = bodyType, error = None } ->
                    match bindType(resultType)(bodyType)(resultState) with
                        | (failedState, Some(error)) -> failedMatchArm(failedState)(error)
                        | (typedState, None) ->
                            match normalizeParameterPassthroughBranch(body)(temp)(typedState) with
                                | (normalizedState, normalizedTemp) ->
                                    match transferArmResult(request)(body)(normalizedTemp)(bodyType)(outerBindings)(owners)(normalizedState) with
                                        | (transferredState, resultTemp) ->
                                            match transferredState
                                            |> emit(StoreLocal(resultSlot)(resultTemp))
                                            |> closeArmScope(body)(owners)(bracket)(resultSlot)(resultTemp)(bodyType) with
                                                | (closed, finalTemp) ->
                                                    LoweredMatchArm(
                                                        lowered = closed
                                                        |> emit(Jump(endLabel))
                                                        |> restoreBindings(outerBindings)
                                                        |> success(resultTemp)(resultType),
                                                        armResult = matchArmResultOf(body)(finalTemp)(resultType)(closed)
                                                    )

let recursive allBindingsSurviveReset (state: CoreLoweringState) (bindings: List(CoreBinding)) =
    match bindings with
        | [] -> true
        | CoreBinding { scheme = TypeScheme { body = bindingType } } :: rest -> resultSurvivesReset(bindingType)(state) && allBindingsSurviveReset(state)(rest)

// A plain variable pattern binds the whole scrutinee, unless the name is a nullary constructor's
// (`None`), which the parser writes the same way and which binds nothing.
let recursive patternBindsWholeScrutineeIn (pattern: Pattern) (state: CoreLoweringState) =
    match pattern with
        | PatternAt(_span, inner) -> patternBindsWholeScrutineeIn(inner)(state)
        | PatternVar(name) ->
            match constructorLayout(name)(state) with
                | Some(_layout) -> false
                | None -> true
        | _ -> false

// Whether the arm takes the scrutinee owner: every arm does, except one whose plain variable
// pattern binds the whole scrutinee (stage 0 makes that binding's slot the owner, and the arm
// may hand it on). A heap value the pattern bound aliases the owner: its own read keeps a
// reference when the arm result carries it out, and the owner's release covers the rest.
let armAdoptsScrutinee (pattern: Pattern) (outerBindings: List(CoreBinding)) (state: CoreLoweringState) = patternBindsWholeScrutineeIn(pattern)(state) == false

// The slot of the plain variable pattern that binds the whole scrutinee, stage 0's
// `TryTrackWholeRuntimeManagedMatchBinding`: that binding's own slot becomes the owner instead
// of a second slot for the same single reference.
let wholeScrutineeBindingSlot (pattern: Pattern) (outerBindings: List(CoreBinding)) (state: CoreLoweringState) =
    if patternBindsWholeScrutineeIn(pattern)(state)
    then
        match armBindings(length(state.bindings) - length(outerBindings))(state.bindings) with
            | CoreBinding { location = CoreLocal(slot) } :: [] -> Some(slot)
            | _ -> None
    else None

// Hands the fresh scrutinee's reference to the owner slot: the value temp is transferred, and
// the slot's release walks the value's owned children inline, since the plan carries the type.
let adoptScrutineeIntoSlot (ownerSlot: Int) (valueTemp: Int) (scrutineeType: SemanticType) (state: CoreLoweringState) =
    state
    |> markRuntimeTemp(valueTemp)(RuntimeTransferred)
    |> (given (adopted: CoreLoweringState) ->
        adopted
        |> withStateRuntimeOwners((ownerSlot, true) :: stateRuntimeOwners(adopted))
        |> withStateOwnerReleasePlans((ownerSlot, scrutineeType, OwnedReleasePlan(deepUnique = false, constructorName = None)) :: stateOwnerReleasePlans(adopted)))

// Stage 0's `_ownershipAliases` for the arm: every heap-typed binding the pattern bound out of
// the adopted scrutinee reads a value the owner's release covers.
let recursive aliasArmBindingsToOwner (ownerSlot: Int) (bindings: List(CoreBinding)) (state: CoreLoweringState) =
    match bindings with
        | [] -> state
        | CoreBinding { location = CoreLocal(slot), scheme = TypeScheme { body = bindingType } } :: rest ->
            if resultSurvivesReset(resolveType(state)(bindingType))(state)
            then aliasArmBindingsToOwner(ownerSlot)(rest)(state)
            else
                state
                |> withStateRuntimeOwnerAliases((slot, ownerSlot) :: stateRuntimeOwnerAliases(state))
                |> aliasArmBindingsToOwner(ownerSlot)(rest)
        | _ :: rest -> aliasArmBindingsToOwner(ownerSlot)(rest)(state)

// Stage 0's `TrackRuntimeManagedMatchScrutineeOwner` after the guard: a plain variable pattern
// owns the fresh scrutinee through its own slot, released at the arm exit like any owned
// binding; any other arm that adopts the scrutinee stores it into an owner slot of its own
// (`$match_rc_N`), released the same way, and its heap-typed bindings alias that owner.
let adoptScrutineeOwner (valueTemp: Int) (owner: Maybe(MatchScrutineeOwner)) (pattern: Pattern) (outerBindings: List(CoreBinding)) (guarded: LoweredCoreValue) =
    match (owner, guarded) with
        | (Some(MatchScrutineeOwner { scrutineeTypeName = typeName, scrutineeIsList = isList, scrutineeType = scrutineeType }), LoweredCoreValue { state = state, error = None }) ->
            match wholeScrutineeBindingSlot(pattern)(outerBindings)(state) with
                | Some(bindingSlot) -> ((guarded with state = adoptScrutineeIntoSlot(bindingSlot)(valueTemp)(scrutineeType)(state)), [])
                | None ->
                    if armAdoptsScrutinee(pattern)(outerBindings)(state)
                    then
                        match freshLocal(state) with
                            | FreshLocal { state = allocated, local = ownerSlot } ->
                                allocated
                                |> emit(StoreLocal(ownerSlot)(valueTemp))
                                |> adoptScrutineeIntoSlot(ownerSlot)(valueTemp)(scrutineeType)
                                |> (given (adopted: CoreLoweringState) ->
                                    aliasArmBindingsToOwner(ownerSlot)(armBindings(length(adopted.bindings) - length(outerBindings))(adopted.bindings))(adopted))
                                |> (given (adopted) -> ((guarded with state = adopted), [ArmScrutineeOwner(ownerSlot)(typeName)(isList)(valueTemp)(scrutineeType)]))
                    else (guarded, [])
        | _ -> (guarded, [])

// OPT-42 consumer hooks: hands a match's dead scrutinee cell to a same-name rebuild inside the
// same arm as an arena/RC in-place-reuse token (`AshesCompiler.Semantics.ReuseSpecialization`
// decides the structural safety; this file owns the state-threaded token bookkeeping and the
// DropReuse/AllocReusing emission the runtime's own uniqueness check gates).
let recursive reuseUnwrapPattern (pattern: Pattern) =
    match pattern with
        | PatternAt(_span, inner) -> reuseUnwrapPattern(inner)
        | other -> other

// The constructor a match case's pattern names, including a nullary constructor written as a
// bare `PatternVar` — `None` for a wildcard, a literal, or any pattern that does not name one.
let reuseCaseConstructorLayout (pattern: Pattern) (state: CoreLoweringState) =
    match reuseUnwrapPattern(pattern) with
        | PatternConstructor(name, _subPatterns) -> constructorLayout(name)(state)
        | PatternVar(name) ->
            if isNullaryConstructorPatternName(name)(state)
            then constructorLayout(name)(state)
            else None
        | _ -> None

let recursive reuseCaseLayouts (cases: List((Pattern, Expr, Maybe(Expr)))) (state: CoreLoweringState) =
    match cases with
        | [] -> Some([])
        | (_pattern, _body, Some(_guard)) :: _rest -> None
        | (pattern, _body, None) :: rest ->
            match (reuseCaseConstructorLayout(pattern)(state), reuseCaseLayouts(rest)(state)) with
                | (Some(layout), Some(restLayouts)) -> Some(layout :: restLayouts)
                | _ -> None

let recursive reuseNamesAllPresent (required: List(Str)) (candidates: List(Str)) =
    match required with
        | [] -> true
        | name :: rest -> containsName(name)(candidates) && reuseNamesAllPresent(rest)(candidates)

let recursive reusePointerFieldIndicesFrom (index: Int) (fieldTypes: List(SemanticType)) (state: CoreLoweringState) =
    match fieldTypes with
        | [] -> []
        | fieldType :: rest ->
            if resultSurvivesReset(fieldType)(state)
            then reusePointerFieldIndicesFrom(index + 1)(rest)(state)
            else index :: reusePointerFieldIndicesFrom(index + 1)(rest)(state)

let reusePointerFieldIndices (fieldTypes: List(SemanticType)) (state: CoreLoweringState) = reusePointerFieldIndicesFrom(0)(fieldTypes)(state)

// A case is reuse-safe only when its own body rebuilds the SAME constructor it matched (through
// nested `let`s) — never a different constructor of the type or a passthrough — with every
// pointer-typed field passed straight through, unchanged, from the matching pattern binding.
// Requiring every case (not just the ones with pointer fields) to rebuild guarantees a token this
// module produces is always consumed by the very arm that produced it, so an unconsumed
// runtime-managed token — which would need its owned children walked and released before its
// bookkeeping could be dropped — never arises.
// A positional constructor records no field names; its rebuild is matched by arity alone, so
// the expected names are stand-ins of the field count.
let recursive reusePositionalNames (count: Int) =
    if count <= 0
    then []
    else Ashes.Text.fromInt(count) :: reusePositionalNames(count - 1)

let reuseCaseSafe (pattern: Pattern) (body: Expr) (layout: CoreConstructorLayout) (state: CoreLoweringState) =
    match (layout, layoutFieldTypes(layout)(state)) with
        | (CoreConstructorLayout { name = ctorName, fieldNames = fieldNames }, (fieldTypes, _resultType)) ->
            let expectedNames =
                match fieldNames with
                    | [] ->
                        fieldTypes
                        |> length
                        |> reusePositionalNames
                    | _ -> fieldNames
            in
                match reuseArmBodyRebuildsSameConstructor(ctorName)(expectedNames)(body) with
                    | None -> false
                    | Some(args) ->
                        reuseTransferredFieldsSafe(reusePointerFieldIndices(fieldTypes)(state))(reusePatternFieldBindings(pattern))(args)

let recursive reuseCaseSafetyAll (cases: List((Pattern, Expr, Maybe(Expr)))) (layouts: List(CoreConstructorLayout)) (state: CoreLoweringState) =
    match (cases, layouts) with
        | ([], []) -> true
        | ((pattern, body, _guard) :: restCases, layout :: restLayouts) -> reuseCaseSafe(pattern)(body)(layout)(state) && reuseCaseSafetyAll(restCases)(restLayouts)(state)
        | _ -> false

// Whether the whole match is a reuse-eligible rebuild of one type: guard-free, every case a
// distinct constructor of the SAME type with none missing (stage 0's exhaustiveness
// precondition), and every case's own rebuild transfer-safe.
let reuseMatchExhaustiveAndSafe (cases: List((Pattern, Expr, Maybe(Expr)))) (state: CoreLoweringState) =
    match reuseCaseLayouts(cases)(state) with
        | None -> false
        | Some([]) -> false
        | Some(firstLayout :: _rest as layouts) ->
            match layoutResultName(firstLayout) with
                | None -> false
                | Some(typeName) ->
                    let required =
                        state
                        |> stateConstructorsOfType(typeName)
                        |> constructorLayoutNames
                    in
                        let caseNames = constructorLayoutNames(layouts)
                        in length(caseNames) == length(required) && reuseNamesAllPresent(required)(caseNames) && reuseCaseSafetyAll(cases)(layouts)(state)

// Whether the body is a match whose arms are all reuse-safe rebuilds of one type, stage 0's
// `RuntimeReusePointerFieldsAreSafe` over the arms of an immediate match. With reuse disabled
// (`--debug-disable-reuse`) no arm publishes a token, so the admission it justifies is withheld
// and the binding keeps the arena placement it had before reuse.
let immediateMatchArmsReuseSafe (body: Expr) (state: CoreLoweringState) =
    match unspanArgument(body) with
        | ExprMatch(_value, cases, _position) -> stateReuseEnabled(state) && reuseMatchExhaustiveAndSafe(cases)(state)
        | _ -> false

// Stage 0's `IsImmediateSafeAdtMatchUse`: the binding is matched immediately through at least
// two arms; a self-recursive type is admitted only when every arm is a transfer-safe rebuild the
// runtime reuse path consumes, and stays on the arena path otherwise.
let isImmediateSafeAdtMatchUse (name: Str) (value: Expr) (body: Expr) (state: CoreLoweringState) =
    match constructorApplicationOf(value)([])(state) with
        | None -> false
        | Some((layout, _arguments)) ->
            match layoutFieldTypes(layout)(state) with
                | (_fieldTypes, resultType) -> isImmediateAdtMatchUse(name)(body) && (canRuntimeManageRecursiveCopyAdt(resultType)(state) == false || immediateMatchArmsReuseSafe(body)(state))

// The request a `let` value starts from, stage 0's `PushSequentialLet`: the enclosing request
// without its expected type, with the transfer flag cleared and out of tail position (the value is
// not the result, so a self-call in it is an ordinary call), which each rule below then adds a
// representation to. Starting from an empty request dropped the representation the enclosing
// position had already asked for — a tuple an escaping result wants reference-counted lost its
// request, and with it the retain its elements needed.
let inheritedLetValueRequest (state: CoreLoweringState) = consumerRequestOf(state) with expectedType = None, argumentSite = None, transfersRuntimeManagedChildren = false, tailPosition = false, tailCall = false, consTailPosition = false

// The representation a `let` asks of its aggregate value, stage 0's `TryLowerRuntimeRcRecordLet`,
// `TryLowerRuntimeRcTupleLet`, `TryLowerRuntimeRcListLet`, and `TryLowerRuntimeRcAdtLet` in that
// order: a record literal read only as a field receiver, matched by one constructor arm, or
// returned directly as a fresh tree; a tuple literal returned directly; a fresh list matched
// immediately (directly, or through a cons onto it) or returned directly; and a constructor
// application matched immediately or returned directly as a fresh runtime-manageable value.
let aggregateLetValueRequest (name: Str) (value: Expr) (body: Expr) (state: CoreLoweringState) =
    (let inherited = inheritedLetValueRequest(state)
    in
        let directEscape = bodyReturnsBinding(name)(body)
        in
            if isRecordLiteral(value) && (isImmediateCopyUseOfRecord(name)(body) || isImmediateRecordMatchUse(name)(body) || directEscape && isFreshRuntimeManageableRecordTree(value)(state))
            then inherited with runtimeRecord = true
            else
                if isTupleLiteral(value) && directEscape
                then inherited with runtimeTuple = true
                else
                    if isFreshListConstruction(value) && (isImmediateListMatchUse(name)(body) || isTailConsumedByImmediateListMatch(name)(body) || directEscape)
                    then inherited with runtimeList = true
                    else
                        if isConstructorExpression(value)(state) && (isImmediateSafeAdtMatchUse(name)(value)(body)(state) || directEscape && isFreshRuntimeManageableAdtExpression(value)(state))
                        then inherited with runtimeAdt = true
                        else inherited)

// Stage 0's `TryLowerRuntimeRcStringLet`: a `let` whose value is a fresh string producer and
// whose body either returns the binding or hands it straight to a runtime-string consumer places
// the value on the reference-counted heap; an aggregate value is asked for its representation by
// `aggregateLetValueRequest`; the value is otherwise lowered without a request.
let representationLetValueRequest (name: Str) (value: Expr) (body: Expr) (state: CoreLoweringState) =
    (let directEscape = isDirectBindingResult(body)(name)
    in
        if isRuntimeRcStringProducer(value)(state) && (directEscape || isImmediateRuntimeStringUse(body)(name)) && (isCaptureSafeStringProducer(value)(state) || directEscape == false)
        then inheritedLetValueRequest(state) with runtimeString = true
        else aggregateLetValueRequest(name)(value)(body)(state))

// The span of the `if` or `match` a value ends in, past the `let`s around it (stage 0's
// `SuccessorJoinOf`).
let recursive successorJoinSpanOf (value: Expr) (span: Maybe(TextSpan)) =
    match value with
        | ExprAt(at, inner) -> successorJoinSpanOf(inner)(Some(at))
        | ExprNode(_node, inner) -> successorJoinSpanOf(inner)(span)
        | ExprLet(_name, _bound, body, _parameters, _annotation, _requirements) -> successorJoinSpanOf(body)(None)
        | ExprIf(_condition, _thenBranch, _elseBranch) -> span
        | ExprMatch(_scrutinee, _cases, _position) -> span
        | _ -> None

// The position at which the self-call of a saturated call chain passes the name as an argument.
let recursive selfCallChainPassing (expression: Expr) (name: Str) (selfName: Str) (reversedArguments: List(Expr)) =
    match expression with
        | ExprAt(_span, inner) -> selfCallChainPassing(inner)(name)(selfName)(reversedArguments)
        | ExprNode(_node, inner) -> selfCallChainPassing(inner)(name)(selfName)(reversedArguments)
        | ExprCall(function, argument, _isSugar, _layout) -> selfCallChainPassing(function)(name)(selfName)(argument :: reversedArguments)
        | ExprVar(callee) ->
            if callee == selfName
            then argumentOrdinalNaming(reversedArguments)(name)(0)
            else None
        | _ -> None
and argumentOrdinalNaming (arguments: List(Expr)) (name: Str) (ordinal: Int) =
    match arguments with
        | [] -> None
        | argument :: rest ->
            match unspanArgument(argument) with
                | ExprVar(passed) ->
                    if passed == name
                    then Some(ordinal)
                    else argumentOrdinalNaming(rest)(name)(ordinal + 1)
                | _ -> argumentOrdinalNaming(rest)(name)(ordinal + 1)

// Stage 0's `TailSelfCallOrdinalPassing`: the position at which a self-call in tail position of
// the expression passes the name, through `let` bodies, both branches of an `if` and every arm of a
// `match`.
let recursive tailSelfCallOrdinalPassing (expression: Expr) (name: Str) (selfName: Str) =
    match expression with
        | ExprAt(_span, inner) -> tailSelfCallOrdinalPassing(inner)(name)(selfName)
        | ExprNode(_node, inner) -> tailSelfCallOrdinalPassing(inner)(name)(selfName)
        | ExprLet(bound, _value, body, _parameters, _annotation, _requirements) ->
            if bound == name
            then None
            else tailSelfCallOrdinalPassing(body)(name)(selfName)
        | ExprIf(_condition, thenBranch, elseBranch) ->
            match tailSelfCallOrdinalPassing(thenBranch)(name)(selfName) with
                | Some(found) -> Some(found)
                | None -> tailSelfCallOrdinalPassing(elseBranch)(name)(selfName)
        | ExprMatch(_scrutinee, cases, _position) -> tailSelfCallOrdinalPassingCases(cases)(name)(selfName)
        | ExprCall(_function, _argument, _isSugar, _layout) -> selfCallChainPassing(expression)(name)(selfName)([])
        | _ -> None
and tailSelfCallOrdinalPassingCases (cases: List((Pattern, Expr, Maybe(Expr)))) (name: Str) (selfName: Str) =
    match cases with
        | [] -> None
        | (_pattern, body, _guard) :: rest ->
            match tailSelfCallOrdinalPassing(body)(name)(selfName) with
                | Some(found) -> Some(found)
                | None -> tailSelfCallOrdinalPassingCases(rest)(name)(selfName)

// Whether the loop parameter at the slot is left in the arena.
let isArenaLoopSlot (slot: Int) (state: CoreLoweringState) =
    match (stateTcoLoopFrame(state), stateTcoLoop(state)) with
        | (Some(frame), Some(loop)) -> loopSlotIsRuntimeManaged(slot)(frame)(loop)(state) == false
        | _ -> false

// Stage 0's `EnterArenaSuccessorLetJoin`: marks the join a `let` value ends in when the self-call in
// tail position of its body passes the binding to a parameter left in the arena.
let enterArenaSuccessorLetJoin (name: Str) (value: Expr) (body: Expr) (state: CoreLoweringState) =
    match (stateTcoLoopFrame(state), stateTcoLoop(state)) with
        | (Some(frame), Some(loop)) ->
            match tailSelfCallOrdinalPassing(body)(name)(loop.selfName) with
                | None -> state
                | Some(ordinal) ->
                    match parameterSlotAtOrdinal(ordinal)(frame.parameterSlots) with
                        | None -> state
                        | Some(slot) ->
                            if isArenaLoopSlot(slot)(state)
                            then
                                match successorJoinSpanOf(value)(None) with
                                    | Some(span) -> withStateArenaSuccessorJoin(Some(span))(state)
                                    | None -> state
                            else state
        | _ -> state

// The join a tail self-call's argument written in place ends in, when its parameter is left in the
// arena.
let tailArgumentSuccessorJoin (slot: Maybe(Int)) (argument: Expr) (state: CoreLoweringState) =
    match slot with
        | Some(parameterSlot) ->
            if isArenaLoopSlot(parameterSlot)(state)
            then successorJoinSpanOf(argument)(None)
            else None
        | None -> None

let leaveArenaSuccessorJoin (saved: Maybe(TextSpan)) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = loweredState } -> lowered with state = withStateArenaSuccessorJoin(saved)(loweredState)

// Stage 0's `LoopSuccessorLetRequest`: a binding the loop's self-call passes on becomes the next
// iteration's parameter exactly like an argument written in place. It escapes every binding scope
// of this iteration, so an aggregate it builds retains the owned children it stores, whose owners
// are still released at the back edge. A parameter on the reference-counted heap needs none of it:
// its back edge copies the successor with references of its own before those owners are released.
// Stage 0's `ValueKeepsName`: whether the value of the expression may hold the named binding's
// value itself, as the result of a join arm or stored in a cell, tuple, list or constructor built
// there.
let recursive valueKeepsName (expression: Expr) (name: Str) (state: CoreLoweringState) =
    match expression with
        | ExprAt(_span, inner) -> valueKeepsName(inner)(name)(state)
        | ExprNode(_node, inner) -> valueKeepsName(inner)(name)(state)
        | ExprVar(variable) -> variable == name
        | ExprLet(bound, _value, body, _parameters, _annotation, _requirements) ->
            if bound == name
            then false
            else valueKeepsName(body)(name)(state)
        | ExprIf(_condition, thenBranch, elseBranch) ->
            if valueKeepsName(thenBranch)(name)(state)
            then true
            else valueKeepsName(elseBranch)(name)(state)
        | ExprMatch(_value, cases, _position) -> caseBodiesKeepName(cases)(name)(state)
        | ExprCons(head, tail) ->
            if valueKeepsName(head)(name)(state)
            then true
            else valueKeepsName(tail)(name)(state)
        | ExprTuple(elements) -> anyValueKeepsName(elements)(name)(state)
        | ExprList(elements, _isMultiline) -> anyValueKeepsName(elements)(name)(state)
        | ExprRecordUpdate(target, updates) ->
            if valueKeepsName(target)(name)(state)
            then true
            else fieldValuesKeepName(updates)(name)(state)
        | other ->
            match constructorApplicationOf(other)([])(state) with
                | Some((_layout, arguments)) -> anyValueKeepsName(arguments)(name)(state)
                | None -> false
and anyValueKeepsName (expressions: List(Expr)) (name: Str) (state: CoreLoweringState) =
    match expressions with
        | [] -> false
        | expression :: rest ->
            if valueKeepsName(expression)(name)(state)
            then true
            else anyValueKeepsName(rest)(name)(state)
and fieldValuesKeepName (fields: List((Str, Expr))) (name: Str) (state: CoreLoweringState) =
    match fields with
        | [] -> false
        | (_field, value) :: rest ->
            if valueKeepsName(value)(name)(state)
            then true
            else fieldValuesKeepName(rest)(name)(state)
and caseBodiesKeepName (cases: List((Pattern, Expr, Maybe(Expr)))) (name: Str) (state: CoreLoweringState) =
    match cases with
        | [] -> false
        | (_pattern, body, _guard) :: rest ->
            if valueKeepsName(body)(name)(state)
            then true
            else caseBodiesKeepName(rest)(name)(state)

// How many bindings a value may pass through on its way into a loop's successor, stage 0's
// `KeptSuccessorLetHops`.
let keptSuccessorLetHops = 6

// Stage 0's `KeepingLetOrdinalPassing`: the position at which a self-call passes on a later binding
// whose value keeps the name (as a join's arm, a cell's head or tail, a constructor's or tuple's
// field), directly or through further such bindings. Only the bindings on the body's path to its
// result are followed.
let recursive keepingLetOrdinalPassing (expression: Expr) (name: Str) (self: Str) (hops: Int) (state: CoreLoweringState) =
    if hops <= 0
    then None
    else
        match expression with
            | ExprAt(_span, inner) -> keepingLetOrdinalPassing(inner)(name)(self)(hops)(state)
            | ExprNode(_node, inner) -> keepingLetOrdinalPassing(inner)(name)(self)(hops)(state)
            | ExprLet(bound, value, body, _parameters, _annotation, _requirements) ->
                if bound == name
                then None
                else
                    match keptLetOrdinal(bound)(value)(body)(name)(self)(hops)(state) with
                        | Some(found) -> Some(found)
                        | None -> keepingLetOrdinalPassing(body)(name)(self)(hops)(state)
            | ExprIf(_condition, thenBranch, elseBranch) ->
                match keepingLetOrdinalPassing(thenBranch)(name)(self)(hops)(state) with
                    | Some(found) -> Some(found)
                    | None -> keepingLetOrdinalPassing(elseBranch)(name)(self)(hops)(state)
            | ExprMatch(_value, cases, _position) -> keepingLetOrdinalPassingCases(cases)(name)(self)(hops)(state)
            | _ -> None
and keptLetOrdinal (bound: Str) (value: Expr) (body: Expr) (name: Str) (self: Str) (hops: Int) (state: CoreLoweringState) =
    if valueKeepsName(value)(name)(state)
    then
        match selfCallOrdinalPassing(body)(bound)(self) with
            | Some(found) -> Some(found)
            | None -> keepingLetOrdinalPassing(body)(bound)(self)(hops - 1)(state)
    else None
and keepingLetOrdinalPassingCases (cases: List((Pattern, Expr, Maybe(Expr)))) (name: Str) (self: Str) (hops: Int) (state: CoreLoweringState) =
    match cases with
        | [] -> None
        | (_pattern, body, _guard) :: rest ->
            match keepingLetOrdinalPassing(body)(name)(self)(hops)(state) with
                | Some(found) -> Some(found)
                | None -> keepingLetOrdinalPassingCases(rest)(name)(self)(hops)(state)

// The position at which the loop's self-call passes the binding on, itself or kept by a later
// binding it passes.
let successorLetOrdinal (body: Expr) (name: Str) (self: Str) (state: CoreLoweringState) =
    match selfCallOrdinalPassing(body)(name)(self) with
        | Some(ordinal) -> Some(ordinal)
        | None -> keepingLetOrdinalPassing(body)(name)(self)(keptSuccessorLetHops)(state)

let isArenaLoopSuccessorLet (name: Str) (body: Expr) (state: CoreLoweringState) =
    match (stateTcoLoopFrame(state), stateTcoLoop(state)) with
        | (Some(frame), Some(loop)) ->
            match successorLetOrdinal(body)(name)(loop.selfName)(state) with
                | None -> false
                | Some(ordinal) ->
                    match parameterSlotAtOrdinal(ordinal)(frame.parameterSlots) with
                        | None -> false
                        | Some(slot) -> loopSlotIsRuntimeManaged(slot)(frame)(loop)(state) == false
        | _ -> false

let loopSuccessorLetRequest (name: Str) (body: Expr) (request: ConsumerRequest) (state: CoreLoweringState) =
    if isArenaLoopSuccessorLet(name)(body)(state)
    then request with transfersRuntimeManagedChildren = true
    else request

// A `let` value the loop's self call passes on as an arena successor is lowered inside the loop's
// mark; the mark is restored once the value is lowered. The kept-successor mark (stage 0's
// `_arenaKeptSuccessorLetLoop`) is set for a binding a later binding keeps, and cleared for one the
// self-call passes itself.
let enterArenaSuccessorLet (name: Str) (body: Expr) (state: CoreLoweringState) =
    match (isArenaLoopSuccessorLet(name)(body)(state), stateTcoLoop(state)) with
        | (true, Some(loop)) ->
            match selfCallOrdinalPassing(body)(name)(loop.selfName) with
                | Some(_ordinal) ->
                    state
                    |> withStateArenaSuccessorLetLoop(Some(loop.selfName))
                    |> withStateArenaKeptSuccessorLetLoop(None)
                | None ->
                    state
                    |> withStateArenaSuccessorLetLoop(Some(loop.selfName))
                    |> withStateArenaKeptSuccessorLetLoop(Some(loop.selfName))
        | _ -> state

let leaveArenaSuccessorLet (saved: Maybe(Str)) (savedKept: Maybe(Str)) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = loweredState } ->
            lowered with state = withStateArenaKeptSuccessorLetLoop(savedKept)(withStateArenaSuccessorLetLoop(saved)(loweredState))

let letValueRequest (name: Str) (value: Expr) (body: Expr) (state: CoreLoweringState) =
    loopSuccessorLetRequest(name)(body)(representationLetValueRequest(name)(value)(body)(state))(state)

// Stage 0's `LowerSequentialBindingChain`: the innermost body of a `let` chain escapes the chain
// under `escapingResultRequest` when the chain's last binding is an ordinary `let`; a chain ending
// in a `let recursive` lowers its body under the outer request unchanged.
let recursive escapingChainBody (body: Expr) (lastOrdinary: Bool) =
    match body with
        | ExprAt(_span, inner) -> escapingChainBody(inner)(lastOrdinary)
        | ExprNode(_node, inner) -> escapingChainBody(inner)(lastOrdinary)
        | ExprLet(_name, _value, nested, _parameters, _annotation, _requirements) -> escapingChainBody(nested)(true)
        | ExprLetRecursive(_name, _value, nested, _parameters, _annotation, _requirements) -> escapingChainBody(nested)(false)
        | _ ->
            if lastOrdinary
            then Some(body)
            else None

let escapeBodyRequest (escapeBody: Maybe(Expr)) (request: ConsumerRequest) (state: CoreLoweringState) =
    match escapeBody with
        | Some(chainBody) -> escapingResultRequest(chainBody)(request)(state)
        | None -> request

let escapingLetBodyRequest (body: Expr) (request: ConsumerRequest) (state: CoreLoweringState) =
    escapeBodyRequest(escapingChainBody(body)(true))(request)(state)

// Stage 0's alias rule in `TrackLetOwnership`: `let y = x` over a binding that already owns its
// value makes `y` an alias the original owner alone releases, so the binding owns no type name
// of its own at the scope exit.
let letAliasesOwnedBinding (value: Expr) (state: CoreLoweringState) =
    match unspanArgument(value) with
        | ExprVar(name) ->
            match lookupBinding(name)(state.bindings) with
                | Some(CoreBinding { ownedRead = true }) -> true
                | Some(CoreBinding { patternOwner = Some(_fact) }) -> true
                | _ -> false
        | _ -> false

let letOwnedTypeName (value: Expr) (loweredValue: LoweredCoreValue) (state: CoreLoweringState) =
    if letAliasesOwnedBinding(value)(state)
    then None
    else loweredValueOwnedTypeName(loweredValue)

// Whether the result of `expression` is, or holds, a closure capturing `name`: a lambda standing
// where the result does, through a `let` chain, a branch or an arm, or an element of the tuple,
// list, record or constructor cell built there. `aliases` are the chain's bindings already bound
// to such a value. The capture test does not follow shadowing, so a lambda rebinding the name
// counts as capturing it.
let recursive resultHoldsClosureCapturing (name: Str) (aliases: List(Str)) (expression: Expr) (state: CoreLoweringState) =
    match expression with
        | ExprAt(_span, inner) -> resultHoldsClosureCapturing(name)(aliases)(inner)(state)
        | ExprNode(_node, inner) -> resultHoldsClosureCapturing(name)(aliases)(inner)(state)
        | ExprLambda(parameter, lambdaBody, _annotation) -> parameter != name && exprMentionsName(name)(lambdaBody)
        | ExprVar(alias) -> containsName(alias)(aliases)
        | ExprLet(bound, value, nested, _parameters, _annotation, _requirements) ->
            if bound == name
            then false
            else
                if resultHoldsClosureCapturing(name)(aliases)(value)(state)
                then resultHoldsClosureCapturing(name)(bound :: aliases)(nested)(state)
                else resultHoldsClosureCapturing(name)(aliases)(nested)(state)
        | ExprLetResult(bound, _value, nested) -> bound != name && resultHoldsClosureCapturing(name)(aliases)(nested)(state)
        | ExprLetRecursive(bound, _value, nested, _parameters, _annotation, _requirements) -> bound != name && resultHoldsClosureCapturing(name)(aliases)(nested)(state)
        | ExprIf(_condition, thenBranch, elseBranch) -> resultHoldsClosureCapturing(name)(aliases)(thenBranch)(state) || resultHoldsClosureCapturing(name)(aliases)(elseBranch)(state)
        | ExprMatch(_scrutinee, cases, _position) -> anyArmHoldsClosureCapturing(name)(aliases)(cases)(state)
        | ExprTuple(elements) -> anyHoldsClosureCapturing(name)(aliases)(elements)(state)
        | ExprList(elements, _isMultiline) -> anyHoldsClosureCapturing(name)(aliases)(elements)(state)
        | ExprCons(head, tail) -> resultHoldsClosureCapturing(name)(aliases)(head)(state) || resultHoldsClosureCapturing(name)(aliases)(tail)(state)
        | other ->
            match constructorApplicationOf(other)([])(state) with
                | Some((_layout, arguments)) -> anyHoldsClosureCapturing(name)(aliases)(arguments)(state)
                | None -> false
and anyHoldsClosureCapturing (name: Str) (aliases: List(Str)) (expressions: List(Expr)) (state: CoreLoweringState) =
    match expressions with
        | [] -> false
        | expression :: rest -> resultHoldsClosureCapturing(name)(aliases)(expression)(state) || anyHoldsClosureCapturing(name)(aliases)(rest)(state)
and anyArmHoldsClosureCapturing (name: Str) (aliases: List(Str)) (cases: List((Pattern, Expr, Maybe(Expr)))) (state: CoreLoweringState) =
    match cases with
        | [] -> false
        | (_pattern, armBody, _guard) :: rest -> resultHoldsClosureCapturing(name)(aliases)(armBody)(state) || anyArmHoldsClosureCapturing(name)(aliases)(rest)(state)

// The body of a `let` whose result holds a closure capturing the binding is lowered with the
// binding named among the escaping captures; the names in scope before it come back afterwards.
let lowerBodyNamingEscapingCapture (name: Str) (body: Expr) lowerBody (expression: Expr) (state: CoreLoweringState) =
    if resultHoldsClosureCapturing(name)([])(body)(state)
    then
        match state
        |> withStateEscapingCaptureNames(name :: stateEscapingCaptureNames(state))
        |> lowerBody(expression) with
            | LoweredCoreValue { state = loweredState } as lowered ->
                lowered with state = withStateEscapingCaptureNames(stateEscapingCaptureNames(state))(loweredState)
    else lowerBody(expression)(state)

// Stage 0's `ResultMayViewCallArgument`: whether a scope's result can name the bytes of `name`. A
// call reading the binding scrutinized where the result is decided may hand back a view of it
// (`unconsText(text)`'s tail), and the result reaches that view through a `let` body, a branch, or
// an arm. The name test does not follow shadowing, so a rebinding still counts.
let recursive resultMayViewCallArgument (name: Str) (depth: Int) (body: Expr) =
    if depth > 16
    then false
    else
        match body with
            | ExprAt(_span, inner) -> resultMayViewCallArgument(name)(depth)(inner)
            | ExprLet(_bound, _value, nested, _parameters, _annotation, _requirements) -> resultMayViewCallArgument(name)(depth + 1)(nested)
            | ExprIf(_condition, thenBranch, elseBranch) -> resultMayViewCallArgument(name)(depth + 1)(thenBranch) || resultMayViewCallArgument(name)(depth + 1)(elseBranch)
            | ExprMatch(scrutinee, cases, _position) -> scrutineeCallReads(name)(scrutinee) || anyArmMayViewCallArgument(name)(depth + 1)(cases)
            | _ -> false
and scrutineeCallReads (name: Str) (scrutinee: Expr) =
    match unspanArgument(scrutinee) with
        | ExprCall(_callee, _argument, _isPipe, _layout) -> exprMentionsName(name)(scrutinee)
        | _ -> false
and anyArmMayViewCallArgument (name: Str) (depth: Int) (cases: List((Pattern, Expr, Maybe(Expr)))) =
    match cases with
        | [] -> false
        | (_pattern, armBody, _guard) :: rest -> resultMayViewCallArgument(name)(depth)(armBody) || anyArmMayViewCallArgument(name)(depth)(rest)

// Stage 0's `TypeCarriesTextBytes`: only a string or a bytes value is ever a view, so only a type
// that reaches one can carry a released backing out. A named type is answered once per walk.
let recursive typeCarriesTextBytes (visited: List(Str)) (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemString -> true
        | SemBytes -> true
        | SemTuple(elements) -> anyTypeCarriesTextBytes(visited)(elements)(state)
        | SemList(element) -> typeCarriesTextBytes(visited)(element)(state)
        | SemNamed(_symbolId, name, arguments) ->
            if containsName(name)(visited)
            then false
            else
                anyTypeCarriesTextBytes(name :: visited)(arguments)(state) || anyConstructorCarriesTextBytes(name :: visited)(stateConstructorsOfType(name)(state))(state)
        | _ -> false
and anyTypeCarriesTextBytes (visited: List(Str)) (types: List(SemanticType)) (state: CoreLoweringState) =
    match types with
        | [] -> false
        | head :: rest -> typeCarriesTextBytes(visited)(head)(state) || anyTypeCarriesTextBytes(visited)(rest)(state)
and anyConstructorCarriesTextBytes (visited: List(Str)) (layouts: List(CoreConstructorLayout)) (state: CoreLoweringState) =
    match layouts with
        | [] -> false
        | layout :: rest ->
            match layoutFieldTypes(layout)(state) with
                | (fieldTypes, _resultType) -> anyTypeCarriesTextBytes(visited)(fieldTypes)(state) || anyConstructorCarriesTextBytes(visited)(rest)(state)

// Stage 0's `MarkOwnerViewedByResult` and `MaterializeScopeResultViews`: a `let` owning a
// reference-counted string or bytes value whose result may name its bytes materializes the result
// before the binding is released. A result the arena deep copy reproduces is copied into the
// arena; one of the contract's types is copied onto the reference-counted heap when it is still in
// the arena and taken as it is when it is already reference-counted, holding no view; for anything
// else the binding is kept alive rather than released, a leak being the safe side of a
// view left pointing into freed memory. Returns the state, the result temp, and whether the
// binding's release is withheld.
let materializeLetResultViews (name: Str) (body: Expr) (valueType: SemanticType) (ownerSlot: Int) (resultTemp: Int) (resultType: SemanticType) (state: CoreLoweringState) =
    if runtimeOwnerStateOf(ownerSlot)(state) != Some(true) || !typeCarriesTextBytes([])(valueType)(state) || !resultMayViewCallArgument(name)(0)(body) || !typeCarriesTextBytes([])(resultType)(state) || isRuntimeTemp(resultTemp)(state)
    then (state, resultTemp, false)
    else
        if canNormalizeResultIntoArena(resultType)(state)
        then
            match emitArenaResultDeepCopy(resultTemp)(resultType)(state) with
                | (copied, copyTemp) -> (copied, copyTemp, false)
        else
            if isGeneralRcValueType(resultType)(state)
            then
                match generalCopyPlanOf(resultType)(state) with
                    | Some(plan) ->
                        match emitOwnedResultOrCopy(resultTemp)(plan)(state) with
                            | (copied, normalizedTemp) -> (copied, normalizedTemp, false)
                    | None -> (state, resultTemp, true)
            else (state, resultTemp, true)

// The body lowers through `lowerBody`, which an operation arm points at the rest of its own body;
// every analysis of the body still reads `body`.
let lowerArenaBracketedNestedLetWith name value body lowerBody lower outerBindings state =
    match freshLocal(state) with
        | FreshLocal { state = cursorAllocated, local = cursorSlot } ->
            match freshLocal(cursorAllocated) with
                | FreshLocal { state = endAllocated, local = endSlot } ->
                    match endAllocated
                    |> withConsumerRequest(letValueRequest(name)(value)(body)(state))
                    |> emit(SaveArenaState(cursorSlot)(endSlot)(false))
                    |> armSourceFunction(name)(value)(nameUsedOnlyAsDirectCallee(name)(body))
                    |> enterArenaSuccessorLet(name)(body)
                    |> enterArenaSuccessorLetJoin(name)(value)(body)
                    |> lower(value)
                    |> leaveArenaSuccessorLet(stateArenaSuccessorLetLoop(state))(stateArenaKeptSuccessorLetLoop(state))
                    |> leaveArenaSuccessorJoin(stateArenaSuccessorJoin(state)) with
                        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                        | LoweredCoreValue { error = None } as loweredValue ->
                            match loweredValue
                            |> withLoweredConsumerRequest(consumerRequestOf(state))
                            |> armOwnerReleasePlan(value)
                            |> finishLetValueInSlot(name)(value)(body)(body)(escapingLetBodyRequest(body)(consumerRequestOf(state))(state))(lowerBodyNamingEscapingCapture(name)(body)(lowerBody))(outerBindings) with
                                | (LoweredCoreValue { state = failedState, error = Some(error) }, _slot) -> failure(failedState)(error)
                                | (LoweredCoreValue { state = bodyState, temp = bodyTemp, semanticType = resultType, error = None }, ownerSlot) ->
                                    match materializeLetResultViews(name)(body)(loweredValue.semanticType)(ownerSlot)(bodyTemp)(resultType)(bodyState) with
                                        | (viewState, resultTemp, keepsOwner) ->
                                            match (if keepsOwner
                                            then retireRuntimeOwner(ownerSlot)(viewState)
                                            else viewState)
                                            |> settleLetScopedTakenOver(ownerSlot)(loweredValue.temp)
                                            |> closeOwnedLetBracket(if keepsOwner
                                            then None
                                            else letOwnedTypeName(value)(loweredValue)(state))(ownerSlot)(cursorSlot)(endSlot)(resultTemp)(resultType) with
                                                | (closed, finalTemp) -> finishClosedLetResult(finalTemp)(resultType)(closed)

let lowerArenaBracketedNestedLet name value body lower outerBindings state = lowerArenaBracketedNestedLetWith(name)(value)(body)(lower)(lower)(outerBindings)(state)

let recursive constructorSpineRoot (expression: Expr) (argumentCount: Int) =
    match expression with
        | ExprAt(_span, inner) -> constructorSpineRoot(inner)(argumentCount)
        | ExprNode(_node, inner) -> constructorSpineRoot(inner)(argumentCount)
        | ExprCall(function, _argument, _isSugar, _layout) -> constructorSpineRoot(function)(argumentCount + 1)
        | ExprVar(name) -> Some((name, argumentCount))
        | _ -> None

// Stage 0's `IsConstructorExpression`: the call spine's root names a constructor, so the whole
// expression builds one cell here rather than calling something that returns one. Saturation is
// required on top, because only a saturated application reaches `lowerConstructor` — a partial one
// builds a closure instead, and would leave the stack request for whatever constructor comes next.
let constructorExpression (expression: Expr) (state: CoreLoweringState) =
    match constructorSpineRoot(expression)(0) with
        | None -> false
        | Some((name, argumentCount)) ->
            match constructorLayout(name)(state) with
                | None -> false
                | Some(layout) -> constructorArity(layout) == argumentCount

let recursive constructorPattern (pattern: Pattern) =
    match pattern with
        | PatternAt(_span, inner) -> constructorPattern(inner)
        | PatternConstructor(_name, _subPatterns) -> true
        | _ -> false

// Stage 0's `ShouldStackAllocateImmediateMatchScrutinee`: one arm, and that arm destructures a
// constructor. The cell is read apart on the way in and nothing else can reach it.
let stackAllocatableScrutineeCases (cases: List((Pattern, Expr, Maybe(Expr)))) =
    match cases with
        | (pattern, _body, _guard) :: [] -> constructorPattern(pattern)
        | _ -> false

// Stage 0's `IsImmediateSingleArmAdtDestructuringMatch`: the binding's whole body is a single-arm
// destructuring match on the binding itself, and neither the guard nor the arm body names it again,
// so the cell is dead the moment the arm binds its fields. `exprMentionsName` is shadow-blind, so a
// pattern that rebinds the name declines the placement where stage 0 allows it — a missed
// optimization, never an unsound one.
let immediateSingleArmDestructuringMatch (name: Str) (body: Expr) =
    match unspanArgument(body) with
        | ExprMatch(scrutinee, cases, _position) ->
            match (unspanArgument(scrutinee), cases) with
                | (ExprVar(scrutineeName), (pattern, armBody, guard) :: []) ->
                    scrutineeName == name && constructorPattern(pattern) && exprMentionsName(name)(armBody) == false && (match guard with
                        | None -> true
                        | Some(guardExpr) -> exprMentionsName(name)(guardExpr) == false)
                | _ -> false
        | _ -> false

// The one-shot request `lowerConstructor` takes and clears, so the cell the `let` or `match` proved
// dead at the end of the frame is built there rather than in the arena.
let requestStackAllocatedConstructor (state: CoreLoweringState) = withStateStackAllocateConstructor(true)(state)

// Stage 0's `CollectBorrowedRuntimeOwners`: the runtime-managed owners an aggregate literal
// borrows a child of, in the literal's own order: a read of an owner, whole or by field, or a
// read of a binding that borrows one, through tuple, record, list, and constructor children.
let recursive borrowedOwnersOfExpression (expression: Expr) (state: CoreLoweringState) (acc: List((Int, SemanticType))) =
    match unspanArgument(expression) with
        | ExprTuple(elements) -> borrowedOwnersOfExpressions(elements)(state)(acc)
        | ExprRecord(_name, fields, _isMultiline) -> borrowedOwnersOfFields(fields)(state)(acc)
        | ExprList(elements, _isMultiline) -> borrowedOwnersOfExpressions(elements)(state)(acc)
        | ExprCons(head, tail) ->
            acc
            |> borrowedOwnersOfExpression(head)(state)
            |> borrowedOwnersOfExpression(tail)(state)
        | ExprVar(name) -> borrowedOwnersOfRead(name)(state)(acc)
        | ExprQualifiedVar(owner, _field) -> borrowedOwnersOfRead(owner)(state)(acc)
        | other ->
            match constructorApplicationOf(other)([])(state) with
                | Some((_layout, arguments)) -> borrowedOwnersOfExpressions(arguments)(state)(acc)
                | None -> acc
and borrowedOwnersOfExpressions (expressions: List(Expr)) (state: CoreLoweringState) (acc: List((Int, SemanticType))) =
    match expressions with
        | [] -> acc
        | expression :: rest ->
            acc
            |> borrowedOwnersOfExpression(expression)(state)
            |> borrowedOwnersOfExpressions(rest)(state)
and borrowedOwnersOfFields (fields: List((Str, Expr))) (state: CoreLoweringState) (acc: List((Int, SemanticType))) =
    match fields with
        | [] -> acc
        | (_field, expression) :: rest ->
            acc
            |> borrowedOwnersOfExpression(expression)(state)
            |> borrowedOwnersOfFields(rest)(state)
and borrowedOwnersOfRead (name: Str) (state: CoreLoweringState) (acc: List((Int, SemanticType))) =
    if state
    |> patternOwnerBinding(name)
    |> patternOwnerFlag
    then acc
    else
        match liveRuntimeOwnerSlot(name)(state) with
            | Some(slot) ->
                match state
                |> stateOwnerReleasePlans
                |> ownerValueTypeOf(slot) with
                    | Some(ownerType) -> (slot, ownerType) :: acc
                    | None -> acc
            | None ->
                match lookupBinding(name)(state.bindings) with
                    | Some(CoreBinding { location = CoreLocal(slot) }) ->
                        prependReversed(state
                        |> stateBorrowedOwners
                        |> borrowedOwnersOfSlot(slot))(acc)
                    | _ -> acc
and prependReversed (owners: List((Int, SemanticType))) (acc: List((Int, SemanticType))) =
    match owners with
        | [] -> acc
        | owner :: rest -> prependReversed(rest)(owner :: acc)

// The owners a `let` value borrows, collected before the value is lowered (the names it reads
// resolve the same either way) and handed to the store by the binding's name; a bare read is a
// `let` alias of its owner, not an aggregate borrowing one.
let collectPendingBorrowedOwners (name: Str) (value: Expr) (state: CoreLoweringState) =
    match unspanArgument(value) with
        | ExprVar(_alias) -> state
        | _ ->
            match []
            |> borrowedOwnersOfExpression(value)(state)
            |> reverse with
                | [] -> state
                | owners -> withStatePendingBorrowedOwners((name, owners) :: statePendingBorrowedOwners(state))(state)

let lowerLet name value body lower state =
    match collectPendingBorrowedOwners(name)(value)(state) with
        | CoreLoweringState { bindings = outerBindings } as state ->
            // Stage 0 also withholds the placement inside a coroutine body, whose frame is not the
            // native one; this lowering has no coroutine bodies to withhold it from yet.
            if constructorExpression(value)(state) && immediateSingleArmDestructuringMatch(name)(body)
            then
                state
                |> requestStackAllocatedConstructor
                |> lowerArenaBracketedNestedLet(name)(value)(body)(lower)(outerBindings)
            else lowerArenaBracketedNestedLet(name)(value)(body)(lower)(outerBindings)(state)

// `lowerLet` for a value its annotation gives a closed type: the value, once lowered, is bound to
// that type before the body is lowered against it.
let lowerAnnotatedLet name value body (annotationType: Maybe(SemanticType)) lower state =
    (let lowerValue =
        given (valueExpression) ->
            given (valueState) ->
                valueState
                |> lower(valueExpression)
                |> bindLoweredAnnotation(annotationType)
    in
        match collectPendingBorrowedOwners(name)(value)(state) with
            | CoreLoweringState { bindings = outerBindings } as collected ->
                if constructorExpression(value)(collected) && immediateSingleArmDestructuringMatch(name)(body)
                then
                    collected
                    |> requestStackAllocatedConstructor
                    |> lowerArenaBracketedNestedLetWith(name)(value)(body)(lower)(lowerValue)(outerBindings)
                else lowerArenaBracketedNestedLetWith(name)(value)(body)(lower)(lowerValue)(outerBindings)(collected))

// Whether every arm leaves the matched cell dead: no guard, and no body mentioning the scrutinee.
let recursive reuseCasesLeaveScrutineeDead (name: Str) (cases: List((Pattern, Expr, Maybe(Expr)))) =
    match cases with
        | [] -> true
        | (_pattern, _body, Some(_guard)) :: _rest -> false
        | (_pattern, body, None) :: rest -> exprMentionsName(name)(body) == false && reuseCasesLeaveScrutineeDead(name)(rest)

// The scrutinee's own bare-variable name, when the match qualifies for OPT-42 in-place reuse,
// stage 0's `TryGetRuntimeManagedReuseScrutinee`: a `let` binding still owning its
// reference-counted value (`liveRuntimeOwnerSlot`), every arm guard-free and leaving the cell
// dead, and the whole match an exhaustive, transfer-safe rebuild of one type
// (`reuseMatchExhaustiveAndSafe`). `None` otherwise — every other match keeps allocating fresh
// cells exactly as before.
let reuseEligibleScrutineeName (scrutinee: Expr) (cases: List((Pattern, Expr, Maybe(Expr)))) (state: CoreLoweringState) =
    match unspanArgument(scrutinee) with
        | ExprVar(name) ->
            match liveRuntimeOwnerSlot(name)(state) with
                | Some(slot) ->
                    if reuseCasesLeaveScrutineeDead(name)(cases) && reuseMatchExhaustiveAndSafe(cases)(state)
                    then Some((name, slot))
                    else None
                | None -> None
        | _ -> None

let recursive anyFieldKeepsHeap (fieldTypes: List(SemanticType)) (state: CoreLoweringState) =
    match fieldTypes with
        | [] -> false
        | fieldType :: rest -> !resultSurvivesReset(fieldType)(state) || anyFieldKeepsHeap(rest)(state)

// Stage 0's `IsArenaReuseUnsafeForRuntimeManagedChildren`: the arena in-place reuse of a loop
// accumulator's cell is declined when the first arm's constructor carries a heap field, whose
// reference-counted child the back edge's deferred release would re-read out of the overwritten
// cell.
let arenaReuseUnsafeForHeapChildren (cases: List((Pattern, Expr, Maybe(Expr)))) (state: CoreLoweringState) =
    match cases with
        | [] -> false
        | (pattern, _body, _guard) :: _rest ->
            match reuseCaseConstructorLayout(pattern)(state) with
                | None -> false
                | Some(layout) ->
                    match layoutFieldTypes(layout)(state) with
                        | (fieldTypes, _resultType) -> anyFieldKeepsHeap(fieldTypes)(state)

// Stage 0's `GetMatchReuseScrutinee`, first half: a linear reuse root matched directly hands its
// dead cell to every arm as an arena token, with no rebuild requirement attached (an unconsumed
// arena token is merely discarded).
let linearReuseScrutineeName (scrutinee: Expr) (cases: List((Pattern, Expr, Maybe(Expr)))) (state: CoreLoweringState) =
    match unspanArgument(scrutinee) with
        | ExprVar(name) ->
            if containsName(name)(stateLinearReuseNames(state)) && !arenaReuseUnsafeForHeapChildren(cases)(state)
            then Some(name)
            else None
        | _ -> None

// A reuse-eligible scrutinee's owner hands its reference to the arms' tokens: each arm's
// `DropReuse` releases or reuses the cell, so the binding's own scope-exit release is retired
// and no arm adopts the scrutinee as an owner of its own. A linear loop accumulator's cells are
// arena tokens and have no owner to retire.
let withReuseScrutinee (scrutinee: Expr) (cases: List((Pattern, Expr, Maybe(Expr)))) (plan: CoreMatchPlan) =
    match plan with
        | CoreMatchPlan { error = Some(_error) } -> plan
        | CoreMatchPlan { state = CoreLoweringState { reuseState = CoreReuseState { reuseEnabled = false } } } -> plan with reuseScrutineeName = None
        | CoreMatchPlan { state = state } ->
            match linearReuseScrutineeName(scrutinee)(cases)(state) with
                | Some(name) -> plan with reuseScrutineeName = Some((name, false))
                | None ->
                    match reuseEligibleScrutineeName(scrutinee)(cases)(state) with
                        | Some((name, slot)) ->
                            plan with reuseScrutineeName = Some((name, true)), scrutineeOwner = None, state = withStateRuntimeOwners(state
                            |> stateRuntimeOwners
                            |> releaseRuntimeOwner(slot))(state)
                        | None -> plan with reuseScrutineeName = None

// Publishes the arm's own DropReuse token when the match found a reuse-eligible scrutinee, this
// arm's pattern names one of its constructors, and the arm's body does not mention the scrutinee
// again (its matched cell is dead). Returns the (possibly updated) pattern result alongside the
// token count to restore to once the arm's body is lowered, `None` when nothing was published.
let recursive reuseFieldBindingNames (bindings: List((Int, Str))) =
    match bindings with
        | [] -> []
        | (_index, name) :: rest -> name :: reuseFieldBindingNames(rest)

// The pseudo-constructor a list cell's reuse token is minted and matched under: a cons cell has no
// constructor layout of its own, so its two-word shape is named here and nowhere else.
let listCellReuseConstructorName = "::"

let recursive isConsPattern (pattern: Pattern) =
    match pattern with
        | PatternAt(_span, inner) -> isConsPattern(inner)
        | PatternCons(_head, _tail) -> true
        | _ -> false

// A list cell matched by a cons pattern on a linear reuse root hands its dead cell to the arm as
// an arena token, stage 0's `ListCell` reuse: the rebuilt cell overwrites it in place. Only an
// arena token is minted this way — a reference-counted cell's transferred children would each
// need the runtime nullness guard the constructor path emits.
let listCellReuseToken (runtimeManagedToken: Bool) (pattern: Pattern) (valueTemp: Int) (state: CoreLoweringState) =
    if runtimeManagedToken || isConsPattern(pattern) == false
    then None
    else
        match freshTemp(state) with
            | FreshTemp { state = allocatedState, temp = tokenTemp } ->
                Some((CoreReuseToken(
                    temp = tokenTemp,
                    fieldCount = 2,
                    tagless = false,
                    runtimeManaged = false,
                    constructorName = listCellReuseConstructorName,
                    fieldBindings = []
                ), emit(DropReuse(tokenTemp)(valueTemp)(2)(false))(allocatedState)))

let publishReuseToken (token: CoreReuseToken) (transferredNames: List(Str)) (tokensBefore: Int) (state: CoreLoweringState) =
    (LoweredCorePattern(
        state = withStateReuseTransferredNames(transferredNames)((state with reuseTokens = token :: state.reuseTokens)),
        error = None
    ), Some(tokensBefore))

let reuseTokenIfEligible (reuseScrutineeName: Maybe((Str, Bool))) (pattern: Pattern) (body: Expr) (valueTemp: Int) (patternResult: LoweredCorePattern) =
    match patternResult with
        | LoweredCorePattern { error = Some(_error) } -> (patternResult, None)
        | LoweredCorePattern { state = state } ->
            match reuseScrutineeName with
                | None -> (patternResult, None)
                | Some((scrutineeName, runtimeManagedToken)) ->
                    if exprMentionsName(scrutineeName)(body)
                    then (patternResult, None)
                    else
                        match listCellReuseToken(runtimeManagedToken)(pattern)(valueTemp)(state) with
                            | Some((listToken, publishedState)) ->
                                publishReuseToken(listToken)([])(length(state.reuseTokens))(publishedState)
                            | None ->
                                match reuseCaseConstructorLayout(pattern)(state) with
                                    | None -> (patternResult, None)
                                    | Some(CoreConstructorLayout { name = ctorName, tagless = tagless }) ->
                                        let arity =
                                            match reusePatternConstructorArity(pattern) with
                                                | Some((_name, patternArity)) -> patternArity
                                                | None -> 0
                                        in
                                            let tokensBefore = length(state.reuseTokens)
                                            in
                                                match freshTemp(state) with
                                                    | FreshTemp { state = allocatedState, temp = tokenTemp } ->
                                                        let token =
                                                            CoreReuseToken(
                                                                temp = tokenTemp,
                                                                fieldCount = arity,
                                                                tagless = tagless,
                                                                runtimeManaged = runtimeManagedToken,
                                                                constructorName = ctorName,
                                                                fieldBindings = reusePatternFieldBindings(pattern)
                                                            )
                                                        in
                                                            allocatedState
                                                            |> emit(DropReuse(tokenTemp)(valueTemp)(arity)(runtimeManagedToken))
                                                            |> publishReuseToken(token)(pattern
                                                            |> reusePatternFieldBindings
                                                            |> reuseFieldBindingNames)(tokensBefore)

let recursive reuseDropExtra (excess: Int) (tokens: List(CoreReuseToken)) =
    if excess <= 0
    then tokens
    else
        match tokens with
            | [] -> []
            | _head :: rest -> reuseDropExtra(excess - 1)(rest)

let reuseTruncateTo (before: Int) (tokens: List(CoreReuseToken)) = reuseDropExtra(length(tokens) - before)(tokens)

// Restores the reuse-token list to its pre-arm length: a no-op when the arm's own rebuild
// consumed the token it published (the ordinary case, guaranteed by `reuseCaseSafe`'s
// every-case-rebuilds requirement), a safety net dropping a stray, unconsumed token's bookkeeping
// otherwise, so it can never be mistaken for a live token by an unrelated later allocation.
let reuseTruncateArmTokens (tokensBefore: Maybe(Int)) (armResult: LoweredMatchArm) =
    match (tokensBefore, armResult) with
        | (None, _armResult) -> armResult
        | (Some(before), LoweredMatchArm { lowered = LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None }, armResult = matchArmResult }) ->
            LoweredMatchArm(
                lowered = LoweredCoreValue(
                    state = withStateReuseTransferredNames([])((state with reuseTokens = reuseTruncateTo(before)(state.reuseTokens))),
                    temp = temp,
                    semanticType = semanticType,
                    error = None
                ),
                armResult = matchArmResult
            )
        | (Some(_before), _armResult) -> armResult

// The guard and body of an arm whose pattern is lowered. The guard lowers through `guardLower`
// and the body through `bodyLower`; the capability-operation arms hand in different ones. The
// arm's owners are the pattern's, then the scrutinee owner when the arm adopts one.
let recursive slotAtOrdinal (ordinal: Int) (slots: List(Int)) =
    match slots with
        | [] -> None
        | slot :: rest ->
            if ordinal == 0
            then Some(slot)
            else slotAtOrdinal(ordinal - 1)(rest)

let recursive patternOwnerSitesOf (frame: CoreTcoLoopFrame) (insertCount: Int) (heldByTuple: Bool) (sites: List(PatternOwnerSite)) (bindings: List(CoreBinding)) =
    match bindings with
        | [] -> sites
        | CoreBinding { location = CoreLocal(slot), patternOwner = Some(fact), scheme = TypeScheme { body = bindingType } } :: rest ->
            match slotAtOrdinal(fact.rootParameterOrdinal)(frame.parameterSlots) with
                | Some(rootSlot) -> patternOwnerSitesOf(frame)(insertCount)(heldByTuple)(PatternOwnerSite(siteSlot = slot, siteRootSlot = rootSlot, siteInsertCount = insertCount, siteType = bindingType, siteFact = fact, siteHeldByTuple = heldByTuple) :: sites)(rest)
                | None -> patternOwnerSitesOf(frame)(insertCount)(heldByTuple)(sites)(rest)
        | _ :: rest -> patternOwnerSitesOf(frame)(insertCount)(heldByTuple)(sites)(rest)

// Stage 0's `TrackPerceusPatternBindingOwners`: once an arm's pattern is lowered, every pattern
// owner it bound is joined to its slot and to the loop parameter slot it was extracted from, at
// the instruction count its protective duplicate is later spliced in at; the sites stay most
// recent first.
// A fresh tuple scrutinee owned on the reference-counted heap (stage 0's
// `ScrutineeIsReferenceCountedTuple`).
let scrutineeOwnerIsTuple (scrutineeOwner: Maybe(MatchScrutineeOwner)) =
    match scrutineeOwner with
        | Some(MatchScrutineeOwner { scrutineeTypeName = typeName }) -> typeName == "Tuple"
        | None -> false

let registerPatternOwnerSites (outerBindings: List(CoreBinding)) (heldByTuple: Bool) (patternResult: LoweredCorePattern) =
    match patternResult with
        | LoweredCorePattern { state = state, error = None } ->
            match stateTcoLoopFrame(state) with
                | Some(frame) ->
                    state.bindings
                    |> armBindings(length(state.bindings) - length(outerBindings))
                    |> reverse
                    |> patternOwnerSitesOf(frame)(length(state.reversedInstructions))(heldByTuple)(statePatternOwnerSites(state))
                    |> (given (sites: List(PatternOwnerSite)) -> LoweredCorePattern(state = withStatePatternOwnerSites(sites)(state), error = None))
                | None -> patternResult
        | _ -> patternResult

// Stage 0's `NoteTupleBinderParameterSlots`: the binders of a reference-counted tuple scrutinee's
// arm that stand at a loop parameter's own position.
let recursive noteTupleBinderParameters (parts: List(Pattern)) (slots: List(Maybe(Int))) (state: CoreLoweringState) =
    match (parts, slots) with
        | (part :: restParts, Some(parameterSlot) :: restSlots) ->
            match unwrapPatternSpan(part) with
                | PatternVar(binder) ->
                    match lookupBinding(binder)(state.bindings) with
                        | Some(CoreBinding { location = CoreLocal(binderSlot) }) ->
                            state
                            |> withStateTupleBinderParameters((binderSlot, parameterSlot) :: stateTupleBinderParameters(state))
                            |> noteTupleBinderParameters(restParts)(restSlots)
                        | _ -> noteTupleBinderParameters(restParts)(restSlots)(state)
                | _ -> noteTupleBinderParameters(restParts)(restSlots)(state)
        | (_part :: restParts, None :: restSlots) -> noteTupleBinderParameters(restParts)(restSlots)(state)
        | _ -> state

let noteArmTupleBinders (heldByTuple: Bool) (pattern: Pattern) (slots: List(Maybe(Int))) (patternResult: LoweredCorePattern) =
    match (heldByTuple, unwrapPatternSpan(pattern), patternResult) with
        | (true, PatternTuple(parts), LoweredCorePattern { state = state, error = None }) -> patternResult with state = noteTupleBinderParameters(parts)(slots)(state)
        | _ -> patternResult

let finishPatternArm (reuseScrutineeName: Maybe((Str, Bool))) (scrutineeOwner: Maybe(MatchScrutineeOwner)) (scrutineeParameterSlots: List(Maybe(Int))) (normalizeStaticStrings: Bool) pattern valueTemp body guard failLabel resultSlot endLabel resultType (request: ConsumerRequest) outerBindings bracket guardLower bodyLower patternResult =
    match patternResult
    |> noteArmTupleBinders(scrutineeOwnerIsTuple(scrutineeOwner))(pattern)(scrutineeParameterSlots)
    |> registerPatternOwnerSites(outerBindings)(scrutineeOwnerIsTuple(scrutineeOwner))
    |> reuseTokenIfEligible(reuseScrutineeName)(pattern)(body)(valueTemp) with
        | (reusedPatternResult, tokensBefore) ->
            match reusedPatternResult
            |> lowerMatchGuard(guard)(failLabel)(guardLower)
            |> adoptScrutineeOwner(valueTemp)(scrutineeOwner)(pattern)(outerBindings) with
                | (guarded, scrutineeOwners) ->
                    guarded
                    |> finishMatchArm(body)(normalizeStaticStrings)(resultSlot)(endLabel)(failLabel)(resultType)(request)(outerBindings)(bracket)(append(armOwners(outerBindings)(reusedPatternResult))(scrutineeOwners))(bodyLower)
                    |> reuseTruncateArmTokens(tokensBefore)

// One arm is bracketed on its own: `SaveArenaState` before the pattern test, and a matching
// restore/reclaim on BOTH exits — the success path (before the jump to the match end) and the
// `cleanupLabel` block a failed pattern or guard lands on, which then jumps to the real fail
// target. Stage 0 emits the cleanup block for every arm, including one whose pattern cannot fail.
let lowerMatchArm pattern body guard lower cleanupLabel (bracket: ArenaBracket) plan =
    match plan with
        | CoreMatchPlan { state = state, valueTemp = valueTemp, valueType = valueType, resultSlot = resultSlot, endLabel = endLabel, resultType = resultType, armRequest = request, scrutineeOwner = scrutineeOwner, normalizeStaticStrings = normalizeStaticStrings, reuseScrutineeName = reuseScrutineeName, scrutineeParameterSlots = scrutineeParameterSlots } ->
            match state with
                | CoreLoweringState { bindings = outerBindings } ->
                    state
                    |> preparePattern(pattern)
                    |> lowerPattern(pattern)(valueTemp)(valueType)(cleanupLabel)
                    |> finishPatternArm(reuseScrutineeName)(scrutineeOwner)(scrutineeParameterSlots)(normalizeStaticStrings)(pattern)(valueTemp)(body)(guard)(cleanupLabel)(resultSlot)(endLabel)(resultType)(request)(outerBindings)(bracket)(lower)(lower)

// The plan after one arm: the arm's state and error, and its result recorded ahead of the
// earlier arms'.
let recastMatchPlan plan (loweredArm: LoweredMatchArm) =
    match (plan, loweredArm) with
        | (CoreMatchPlan { valueTemp = valueTemp, valueType = valueType, resultSlot = resultSlot, endLabel = endLabel, noMatchLabel = noMatchLabel, resultType = resultType, armRequest = request, armResults = armResults, scrutineeOwner = scrutineeOwner, normalizeStaticStrings = normalizeStaticStrings, reuseScrutineeName = reuseScrutineeName, scrutineeParameterSlots = scrutineeParameterSlots }, LoweredMatchArm { lowered = LoweredCoreValue { state = state, error = error }, armResult = armResult }) ->
            CoreMatchPlan(
                state = state,
                valueTemp = valueTemp,
                valueType = valueType,
                resultSlot = resultSlot,
                endLabel = endLabel,
                noMatchLabel = noMatchLabel,
                resultType = resultType,
                armRequest = request,
                armResults = armResult :: armResults,
                scrutineeOwner = scrutineeOwner,
                normalizeStaticStrings = normalizeStaticStrings,
                reuseScrutineeName = reuseScrutineeName,
                scrutineeParameterSlots = scrutineeParameterSlots,
                error = error
            )

let matchFailLabel rest noMatchLabel state =
    match rest with
        | [] -> FreshLabel(state = state, label = noMatchLabel)
        | _ -> freshLabel("match_next")(state)

let labelNextMatchArm rest failLabel (plan: CoreMatchPlan) =
    match rest with
        | [] -> plan
        | _ -> plan with state = emit(Label(failLabel))(plan.state)

// The arm's cleanup block, emitted right after its jump to the match end: restore this arm's own
// bracket under the live-posts guard (a guard expression can perform a one-shot capability
// operation whose pending post must survive the failed arm's cleanup), then jump on to the real
// fail target (`match_next_N`, or the no-match label for the last arm). Label allocation order
// matches stage 0's — the next-arm label first, then this arm's cleanup label.
let emitMatchArmCleanup cleanupLabel failLabel (bracket: ArenaBracket) (plan: CoreMatchPlan) =
    match plan with
        | CoreMatchPlan { error = Some(_error) } -> plan
        | CoreMatchPlan { state = state } ->
            plan with state = emit(Jump(failLabel))(state
            |> emit(Label(cleanupLabel))
            |> closeGuardedArmBracket(bracket.bracketCursorSlot)(bracket.bracketEndSlot))

// Brackets one arm of a linear chain: the cleanup label is allocated after the arm's fail label,
// the bracket opens before the pattern test, and the cleanup block follows the arm's jump to the
// match end. `lowerArm` lowers the arm against its cleanup label and bracket.
let lowerBracketedMatchArm lowerArm cleanupLabel failLabel (bracket: ArenaBracket) (bracketedPlan: CoreMatchPlan) =
    bracketedPlan
    |> lowerArm(cleanupLabel)(bracket)
    |> recastMatchPlan(bracketedPlan)
    |> emitMatchArmCleanup(cleanupLabel)(failLabel)(bracket)

let bracketMatchArm lowerArm failLabel (plan: CoreMatchPlan) =
    match freshLabel("match_arm_cleanup")(plan.state) with
        | FreshLabel { state = cleanupState, label = cleanupLabel } ->
            match openArenaBracket(cleanupState) with
                | ArenaBracket { bracketState = bracketState } as bracket -> lowerBracketedMatchArm(lowerArm)(cleanupLabel)(failLabel)(bracket)((plan with state = bracketState))

let labelMatchPlan (label: Str) (plan: CoreMatchPlan) = plan with state = emit(Label(label))(plan.state)

let recursive lowerMatchArms cases lower plan =
    match (cases, plan) with
        | (_cases, CoreMatchPlan { error = Some(_error) }) -> plan
        | ([], _) -> plan
        | ((pattern, body, guard) :: rest, CoreMatchPlan { state = state, noMatchLabel = noMatchLabel }) ->
            match matchFailLabel(rest)(noMatchLabel)(state) with
                | FreshLabel { state = failState, label = failLabel } ->
                    (plan with state = failState)
                    |> bracketMatchArm(lowerMatchArm(pattern)(body)(guard)(lower))(failLabel)
                    |> labelNextMatchArm(rest)(failLabel)
                    |> lowerMatchArms(rest)(lower)

let failedMatchPlan state error =
    CoreMatchPlan(
        state = state,
        valueTemp = -1,
        valueType = SemNever,
        resultSlot = -1,
        endLabel = "",
        noMatchLabel = "",
        resultType = SemNever,
        armRequest = emptyConsumerRequest,
        armResults = [],
        scrutineeOwner = None,
        normalizeStaticStrings = false,
        reuseScrutineeName = None,
        scrutineeParameterSlots = [],
        error = Some(error)
    )

let finishPreparedMatch valueTemp valueType resultType resultSlot endLabel fresh =
    match fresh with
        | FreshLabel { state = state, label = noMatchLabel } ->
            CoreMatchPlan(
                state = state,
                valueTemp = valueTemp,
                valueType = valueType,
                resultSlot = resultSlot,
                endLabel = endLabel,
                noMatchLabel = noMatchLabel,
                resultType = resultType,
                armRequest = emptyConsumerRequest,
                armResults = [],
                scrutineeOwner = None,
                normalizeStaticStrings = false,
                reuseScrutineeName = None,
                scrutineeParameterSlots = [],
                error = None
            )

let withPlanArmRequest (request: ConsumerRequest) (plan: CoreMatchPlan) = plan with armRequest = request

let prepareMatchEndLabel valueTemp valueType resultType resultSlot fresh =
    match fresh with
        | FreshLabel { state = state, label = endLabel } ->
            state
            |> freshLabel("match_none")
            |> finishPreparedMatch(valueTemp)(valueType)(resultType)(resultSlot)(endLabel)

let prepareMatchResultSlot valueTemp valueType resultType fresh =
    match fresh with
        | FreshLocal { state = state, local = resultSlot } ->
            state
            |> freshLabel("match_end")
            |> prepareMatchEndLabel(valueTemp)(valueType)(resultType)(resultSlot)

let prepareMatchResultType valueTemp valueType fresh =
    match fresh with
        | FreshType { state = state, semanticType = resultType } ->
            state
            |> freshLocal
            |> prepareMatchResultSlot(valueTemp)(valueType)(resultType)

let prepareMatchPlan loweredValue =
    match loweredValue with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failedMatchPlan(failedState)(error)
        | LoweredCoreValue { state = valueState, temp = valueTemp, semanticType = valueType, error = None } ->
            valueState
            |> freshType
            |> prepareMatchResultType(valueTemp)(valueType)

// Whether `temp` is a cell built here on the reference-counted heap.
let recursive definedByRcAlloc (temp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = Alloc(target, _size, runtimeManaged) } :: rest ->
            if target == temp
            then runtimeManaged
            else definedByRcAlloc(temp)(rest)
        | _ :: rest -> definedByRcAlloc(temp)(rest)

// Stage 0's `FreshCellRecursiveTailOwnedSlot`: the owned slot an arm's reference-counted cell
// takes its tail from as it is, when the arm conses that one cell onto the function's own
// recursive result; -1 otherwise.
let freshCellRecursiveTailOwnedSlot (arm: MatchArmResult) (state: CoreLoweringState) =
    if arm.armOwned && arm.armNewlyProduced && !arm.armBorrowsLoopParameter && arm.armConsesOntoSelfResult && definedByRcAlloc(arm.armStoreTemp)(state.reversedInstructions)
    then
        let slot =
            loadedSlotOf(consCellTailTemp(arm.armStoreTemp)(state.reversedInstructions))(state.reversedInstructions)
        in
            if slot >= 0 && isOwnedSlotIn(slot)(ownedSlotsOfActiveFunction(state))
            then slot
            else -1
    else -1

// The cells of a join whose every reaching arm is the empty list or one reference-counted cell
// onto the function's own recursive result, each with the owned slot it takes its tail from;
// `None` when some arm is neither.
let recursive nilBesideFreshCells (arms: List(MatchArmResult)) (state: CoreLoweringState) =
    match arms with
        | [] -> Some([])
        | arm :: rest ->
            match nilBesideFreshCells(rest)(state) with
                | None -> None
                | Some(cells) ->
                    if arm.armStoreTemp < 0 || isZeroConstantTemp(arm.armStoreTemp)(state.reversedInstructions)
                    then Some(cells)
                    else
                        let slot = freshCellRecursiveTailOwnedSlot(arm)(state)
                        in
                            if slot < 0
                            then None
                            else Some((arm.armStoreTemp, slot) :: cells)

// The arm's store of `source` into the join slot followed by `after`, both most recent first.
let recursive insertAfterJoinArmStore (slot: Int) (source: Int) (after: List(IrInstruction)) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> []
        | (IrInstruction { instruction = StoreLocal(storeSlot, storeSource) } as instruction) :: rest ->
            if storeSlot == slot && storeSource == source
            then append(after)(instruction :: rest)
            else instruction :: insertAfterJoinArmStore(slot)(source)(after)(rest)
        | instruction :: rest -> instruction :: insertAfterJoinArmStore(slot)(source)(after)(rest)

let recursive clearTakenOverTailSlots (resultSlot: Int) (cells: List((Int, Int))) (state: CoreLoweringState) =
    match cells with
        | [] -> state
        | (storeTemp, ownedSlot) :: rest ->
            match freshTemp(state) with
                | FreshTemp { state = zeroState, temp = zeroTemp } ->
                    let index = joinArmStoreIndex(resultSlot)(storeTemp)(zeroState.reversedInstructions)
                    in
                        let cleared =
                            (zeroState with reversedInstructions = [])
                            |> emit(LoadConstInt(zeroTemp)(0))
                            |> emit(StoreLocal(ownedSlot)(zeroTemp))
                            |> (given (buffered: CoreLoweringState) -> buffered.reversedInstructions)
                        in
                            (zeroState with reversedInstructions = insertAfterJoinArmStore(resultSlot)(storeTemp)(cleared)(zeroState.reversedInstructions))
                            |> withStatePatternOwnerSites(zeroState
                            |> statePatternOwnerSites
                            |> shiftPatternOwnerSites(index)(2))
                            |> clearTakenOverTailSlots(resultSlot)(rest)

// Stage 0's `TryTakeOverNilBesideFreshCells`: in a fresh join of the empty list beside cells built
// on the reference-counted heap onto the function's own recursive result, each cell takes that
// result over from the owned slot keeping it, which then releases nothing at the return.
let takeOverNilBesideFreshCells (resultSlot: Int) (arms: List(MatchArmResult)) (state: CoreLoweringState) =
    if generalRcEnabled(state) && (joinIsNewlyProduced(arms) || reachingArmsAllNewlyProduced(arms))
    then
        match nilBesideFreshCells(arms)(state) with
            | Some(cells) -> clearTakenOverTailSlots(resultSlot)(cells)(state)
            | None -> state
    else state

let finishMatchPlanAs (successorJoin: Bool) plan =
    match plan with
        | CoreMatchPlan { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | CoreMatchPlan { state = state, resultSlot = resultSlot, endLabel = endLabel, noMatchLabel = noMatchLabel, resultType = resultType, armResults = armResults, error = None } ->
            match freshTemp(state) with
                | FreshTemp { state = defaultState, temp = defaultTemp } ->
                    match freshTemp(defaultState) with
                        | FreshTemp { state = resultState, temp = resultTemp } ->
                            match resultState
                            |> emit(Label(noMatchLabel))
                            |> emit(LoadConstInt(defaultTemp)(0))
                            |> emit(StoreLocal(resultSlot)(defaultTemp))
                            |> emit(Label(endLabel))
                            |> emit(LoadLocal(resultTemp)(resultSlot))
                            |> normalizeMixedJoinArmsAs(successorJoin)(resultSlot)(resultType)(reverse(armResults)) with
                                | (normalized, joinedArms) ->
                                    normalized
                                    |> takeOverNilBesideFreshCells(resultSlot)(joinedArms)
                                    |> markControlFlowJoin(resultTemp)(joinedArms)
                                    |> success(resultTemp)(resolveType(resultState)(resultType))

let finishMatchPlan plan = finishMatchPlanAs(false)(plan)

// Tag-group match dispatch. Arms whose patterns are constructors of one ADT are grouped by their
// outer constructor tag; one GetAdtTag/SwitchTag then dispatches to the group, a trivial single
// case binds its payload directly (the switch already proved the tag), and any other group tests
// its cases linearly in their original order, scoped to that group. A group's last case failing
// its own sub-pattern test does not mean nothing matches: the trailing wildcard/variable arm, when
// one exists, still covers it, so every group falls through to that default arm's label and only
// without a default to the match's no-match path. Guards, zero-cost constructors, a second ADT,
// or a catch-all anywhere but last decline to the linear lowering; so does an all-trivial match of
// at most four arms, where linear tag tests cost no more than a switch.
let recursive unwrapPatternAt pattern =
    match pattern with
        | PatternAt(_span, inner) -> unwrapPatternAt(inner)
        | other -> other

let isCatchAllPattern state pattern =
    match unwrapPatternAt(pattern) with
        | PatternWildcard -> true
        | PatternVar(name) -> !isNullaryConstructorPatternName(name)(state)
        | _ -> false

let recursive allCatchAllPatterns state patterns =
    match patterns with
        | [] -> true
        | pattern :: rest ->
            if isCatchAllPattern(state)(pattern)
            then allCatchAllPatterns(state)(rest)
            else false

let classifyConstructorCase (name: Str) patterns state =
    match constructorLayout(name)(state) with
        | None -> CaseReject
        | Some(CoreConstructorLayout { isZeroCost = true }) -> CaseReject
        | Some(CoreConstructorLayout { tag = tag, scheme = TypeScheme { body = body } }) ->
            match schemeResultName(body) with
                | Some(adtName) ->
                    patterns
                    |> allCatchAllPatterns(state)
                    |> CaseConstructor(name)(tag)(adtName)
                | None -> CaseReject

let classifyMatchCase (matchCase: (Pattern, Expr, Maybe(Expr))) state =
    match matchCase with
        | (_pattern, _body, Some(_guard)) -> CaseReject
        | (pattern, _body, None) ->
            match unwrapPatternAt(pattern) with
                | PatternWildcard -> CaseDefault
                | PatternVar(name) ->
                    if isNullaryConstructorPatternName(name)(state)
                    then classifyConstructorCase(name)([])(state)
                    else CaseDefault
                | PatternConstructor(name, patterns) -> classifyConstructorCase(name)(patterns)(state)
                | _ -> CaseReject

let recursive addCaseToTagGroups (groups: List(CoreTagGroup)) (tag: Int) (name: Str) (index: Int) (trivial: Bool) =
    match groups with
        | [] -> [CoreTagGroup(tag = tag, constructorName = name, caseIndices = [index], trivialSingleCase = trivial)]
        | group :: rest ->
            if group.tag == tag
            then (group with caseIndices = append(group.caseIndices)([index]), trivialSingleCase = false) :: rest
            else group :: addCaseToTagGroups(rest)(tag)(name)(index)(trivial)

let recursive anyGroupNeedsLinearFallback (groups: List(CoreTagGroup)) =
    match groups with
        | [] -> false
        | group :: rest ->
            if group.trivialSingleCase
            then anyGroupNeedsLinearFallback(rest)
            else true

let recursive countGroups (groups: List(CoreTagGroup)) (acc: Int) =
    match groups with
        | [] -> acc
        | _ :: rest -> countGroups(rest)(acc + 1)

// Some((groups, default case index)) when the match dispatches through a tag switch.
let recursive planTagGroups cases (index: Int) state (adtName: Maybe(Str)) (groups: List(CoreTagGroup)) =
    match cases with
        | [] ->
            if anyGroupNeedsLinearFallback(groups)
            then Some((groups, None))
            else
                if countGroups(groups)(0) > 4
                then Some((groups, None))
                else None
        | matchCase :: rest ->
            match classifyMatchCase(matchCase)(state) with
                | CaseReject -> None
                | CaseDefault ->
                    match rest with
                        | [] ->
                            match groups with
                                | [] -> None
                                | _ ->
                                    if anyGroupNeedsLinearFallback(groups)
                                    then Some((groups, Some(index)))
                                    else
                                        if countGroups(groups)(0) > 4
                                        then Some((groups, Some(index)))
                                        else None
                        | _ -> None
                | CaseConstructor(name, tag, caseAdt, trivial) ->
                    match adtName with
                        | Some(seen) ->
                            if seen == caseAdt
                            then
                                trivial
                                |> addCaseToTagGroups(groups)(tag)(name)(index)
                                |> planTagGroups(rest)(index + 1)(state)(adtName)
                            else None
                        | None ->
                            trivial
                            |> addCaseToTagGroups(groups)(tag)(name)(index)
                            |> planTagGroups(rest)(index + 1)(state)(Some(caseAdt))

let recursive nthMatchCase cases (index: Int) =
    match cases with
        | [] -> None
        | matchCase :: rest ->
            if index == 0
            then Some(matchCase)
            else nthMatchCase(rest)(index - 1)

// The tag is already proven by the switch: bind the payload fields without re-testing it.
let finishKnownTagConstructorPattern patterns valueTemp valueType failLabel lowerPattern shape =
    match shape with
        | CoreConstructorShape { layout = CoreConstructorLayout { isZeroCost = true } } -> finishConstructorPattern(patterns)(valueTemp)(valueType)(failLabel)(lowerPattern)(shape)
        | CoreConstructorShape { state = state, layout = CoreConstructorLayout { name = constructorName, tagless = tagless }, parameterTypes = parameterTypes, resultType = resultType } ->
            if coreListLength(patterns) != coreListLength(parameterTypes)
            then
                LoweredCorePattern(state = state, error = patterns
                |> coreListLength
                |> CoreConstructorPatternArityMismatch(constructorName)(coreListLength(parameterTypes))
                |> Some)
            else
                match bindType(valueType)(resultType)(state) with
                    | (failedState, Some(error)) -> LoweredCorePattern(state = failedState, error = Some(error))
                    | (typedState, None) -> lowerAdtPatternFields(patterns)(parameterTypes)(valueTemp)(0)(tagless)(failLabel)(lowerPattern)(LoweredCorePattern(state = typedState, error = None))

let recursive lowerKnownTagPattern pattern valueTemp valueType failLabel state =
    match unwrapPatternAt(pattern) with
        | PatternVar(name) ->
            if isNullaryConstructorPatternName(name)(state)
            then
                lowerKnownTagPattern(PatternConstructor(name)([]))(valueTemp)(valueType)(failLabel)(state)
            else lowerPattern(pattern)(valueTemp)(valueType)(failLabel)(state)
        | PatternConstructor(name, patterns) ->
            match constructorLayout(name)(state) with
                | None -> LoweredCorePattern(state = state, error = Some(UnsupportedCoreLoweringPattern("unknown constructor " + name)))
                | Some(layout) ->
                    state
                    |> instantiateConstructor(layout)
                    |> finishKnownTagConstructorPattern(patterns)(valueTemp)(valueType)(failLabel)(lowerPattern)
        | _ -> lowerPattern(pattern)(valueTemp)(valueType)(failLabel)(state)

// A trivial single-case group arm is bracketed on its success path only: the switch already
// proved the tag and the catch-all sub-patterns cannot fail, so stage 0 emits no cleanup block.
let lowerKnownTagMatchArm pattern body guard failLabel lower (plan: CoreMatchPlan) =
    match (plan.state, openArenaBracket(plan.state)) with
        | (CoreLoweringState { bindings = outerBindings }, ArenaBracket { bracketState = bracketState } as bracket) ->
            bracketState
            |> preparePattern(pattern)
            |> lowerKnownTagPattern(pattern)(plan.valueTemp)(plan.valueType)(failLabel)
            |> finishPatternArm(plan.reuseScrutineeName)(plan.scrutineeOwner)(plan.scrutineeParameterSlots)(plan.normalizeStaticStrings)(pattern)(plan.valueTemp)(body)(guard)(failLabel)(plan.resultSlot)(plan.endLabel)(plan.resultType)(plan.armRequest)(outerBindings)(bracket)(lower)(lower)

let groupCaseFailLabel rest (groupFailLabel: Str) state =
    match rest with
        | [] -> FreshLabel(state = state, label = groupFailLabel)
        | _ -> freshLabel("match_group_next")(state)

// The group's cases in their original order, each bracketed like a linear arm; the last one
// fails to the group's fail target.
let recursive lowerTagGroupCasesLinearly cases (indices: List(Int)) (groupFailLabel: Str) lower (plan: CoreMatchPlan) =
    match (indices, plan) with
        | (_indices, CoreMatchPlan { error = Some(_error) }) -> plan
        | ([], _) -> plan
        | (index :: rest, _) ->
            match nthMatchCase(cases)(index) with
                | None -> plan
                | Some((pattern, body, guard)) ->
                    match groupCaseFailLabel(rest)(groupFailLabel)(plan.state) with
                        | FreshLabel { state = failState, label = failLabel } ->
                            (plan with state = failState)
                            |> bracketMatchArm(lowerMatchArm(pattern)(body)(guard)(lower))(failLabel)
                            |> labelNextMatchArm(rest)(failLabel)
                            |> lowerTagGroupCasesLinearly(cases)(rest)(groupFailLabel)(lower)

let lowerTagGroup cases (group: CoreTagGroup) (groupLabel: Str) (groupFailLabel: Str) lower (plan: CoreMatchPlan) =
    match plan with
        | CoreMatchPlan { error = Some(_error) } -> plan
        | _ ->
            let labeled = plan with state = emit(Label(groupLabel))(plan.state)
            in
                if group.trivialSingleCase
                then
                    match group.caseIndices with
                        | index :: _ ->
                            match nthMatchCase(cases)(index) with
                                | Some((pattern, body, guard)) ->
                                    labeled
                                    |> lowerKnownTagMatchArm(pattern)(body)(guard)(groupFailLabel)(lower)
                                    |> recastMatchPlan(labeled)
                                | None -> labeled
                        | [] -> labeled
                else lowerTagGroupCasesLinearly(cases)(group.caseIndices)(groupFailLabel)(lower)(labeled)

let recursive lowerTagGroups cases (groups: List(CoreTagGroup)) (labels: List(Str)) (groupFailLabel: Str) lower (plan: CoreMatchPlan) =
    match (groups, labels) with
        | (group :: groupRest, label :: labelRest) ->
            plan
            |> lowerTagGroup(cases)(group)(label)(groupFailLabel)(lower)
            |> lowerTagGroups(cases)(groupRest)(labelRest)(groupFailLabel)(lower)
        | _ -> plan

let recursive freshGroupLabels (groups: List(CoreTagGroup)) state (reversedLabels: List(Str)) (reversedCases: List(IrSwitchCase)) =
    match groups with
        | [] -> (state, reverse(reversedLabels), reverse(reversedCases))
        | group :: rest ->
            match freshLabel("match_group")(state) with
                | FreshLabel { state = labelState, label = label } -> freshGroupLabels(rest)(labelState)(label :: reversedLabels)(IrSwitchCase(tag = group.tag, label = label) :: reversedCases)

let lowerDefaultTagGroupArm cases (defaultIndex: Maybe(Int)) (defaultLabel: Str) lower (plan: CoreMatchPlan) =
    match (defaultIndex, plan) with
        | (_index, CoreMatchPlan { error = Some(_error) }) -> plan
        | (None, _) -> plan
        | (Some(index), _) ->
            match nthMatchCase(cases)(index) with
                | None -> plan
                | Some((pattern, body, guard)) ->
                    plan
                    |> labelMatchPlan(defaultLabel)
                    |> bracketMatchArm(lowerMatchArm(pattern)(body)(guard)(lower))(plan.noMatchLabel)

// A single-constructor (tagless) scrutinee has exactly one group and nothing to switch on:
// control falls straight into that group's label, and no tag temp is allocated. Its per-case
// sub-pattern tests still run inside the group, and can still fall through to the default arm.
let isSoleTaglessTagGroup (groups: List(CoreTagGroup)) state =
    match groups with
        | CoreTagGroup { constructorName = name } :: [] ->
            match constructorLayout(name)(state) with
                | Some(CoreConstructorLayout { tagless = tagless }) -> tagless
                | None -> false
        | _ -> false

let emitTagGroupSwitch valueTemp switchCases defaultLabel (soleTagless: Bool) state =
    if soleTagless
    then state
    else
        match freshTemp(state) with
            | FreshTemp { state = tagState, temp = tagTemp } ->
                tagState
                |> emit(GetAdtTag(tagTemp)(valueTemp))
                |> emit(SwitchTag(tagTemp)(switchCases)(defaultLabel))

let lowerMatchArmsViaTagGroups cases (groups: List(CoreTagGroup)) (defaultIndex: Maybe(Int)) lower (plan: CoreMatchPlan) =
    match freshGroupLabels(groups)(plan.state)([])([]) with
        | (labelState, groupLabels, switchCases) ->
            let defaultLabelled =
                match defaultIndex with
                    | Some(_) -> freshLabel("match_group_default")(labelState)
                    | None -> FreshLabel(state = labelState, label = plan.noMatchLabel)
            in
                match defaultLabelled with
                    | FreshLabel { state = defaultState, label = defaultLabel } ->
                        let switched =
                            emitTagGroupSwitch(plan.valueTemp)(switchCases)(defaultLabel)(isSoleTaglessTagGroup(groups)(defaultState))(defaultState)
                        in
                            (plan with state = switched)
                            |> lowerTagGroups(cases)(groups)(groupLabels)(defaultLabel)(lower)
                            |> lowerDefaultTagGroupArm(cases)(defaultIndex)(defaultLabel)(lower)

// Dead-arm trimming. A trailing run of arms after a prefix that already matches every value is
// unreachable and is dropped before any arm is lowered. The verdict is only trusted for pattern
// shapes whose coverage reduces to a constructor set, a bool pair, or the two list shapes: a
// catch-all, a bool literal, the empty list, or a cons/tuple/constructor whose every child is a
// catch-all. Anything else (a literal, a record sub-pattern, a nested constructor) stops the
// prefix from growing, since a per-field coverage engine is a deliberate under-approximation of
// what is missing - right for a diagnostic, wrong as a proof that an arm can never run.
let recursive trimCatchAllChildren state patterns =
    match patterns with
        | [] -> true
        | pattern :: rest ->
            if isCatchAllPattern(state)(pattern)
            then trimCatchAllChildren(state)(rest)
            else false

let recursive isExactCoveragePattern state pattern =
    match unwrapPatternAt(pattern) with
        | PatternWildcard -> true
        | PatternVar(_name) -> true
        | PatternBool(_value) -> true
        | PatternEmptyList -> true
        | PatternCons(head, tail) -> trimCatchAllChildren(state)([head, tail])
        | PatternTuple(elements) -> trimCatchAllChildren(state)(elements)
        | PatternConstructor(_name, patterns) -> trimCatchAllChildren(state)(patterns)
        | PatternAs(inner, _name) -> isExactCoveragePattern(state)(inner)
        | PatternOr(alternatives) -> trimExactAlternatives(state)(alternatives)
        | _ -> false
and trimExactAlternatives state alternatives =
    match alternatives with
        | [] -> true
        | alternative :: rest ->
            if isExactCoveragePattern(state)(alternative)
            then trimExactAlternatives(state)(rest)
            else false

// The constructor a pattern names, when it is a constructor pattern (a bare nullary name included).
let trimConstructorName state pattern =
    match unwrapPatternAt(pattern) with
        | PatternConstructor(name, _patterns) ->
            match constructorLayout(name)(state) with
                | Some(_layout) -> Some(name)
                | None -> None
        | PatternVar(name) ->
            if isNullaryConstructorPatternName(name)(state)
            then Some(name)
            else None
        | _ -> None

let trimAdtOfConstructor state (name: Str) =
    match constructorLayout(name)(state) with
        | Some(CoreConstructorLayout { scheme = TypeScheme { body = body } }) -> schemeResultName(body)
        | None -> None

let recursive trimAdtConstructorNames (layouts: List(CoreConstructorLayout)) (adtName: Str) acc =
    match layouts with
        | [] -> reverse(acc)
        | CoreConstructorLayout { name = name, scheme = TypeScheme { body = body } } :: rest ->
            match schemeResultName(body) with
                | Some(candidate) ->
                    if candidate == adtName
                    then trimAdtConstructorNames(rest)(adtName)(name :: acc)
                    else trimAdtConstructorNames(rest)(adtName)(acc)
                | None -> trimAdtConstructorNames(rest)(adtName)(acc)

let recursive trimSeenConstructors state patterns acc =
    match patterns with
        | [] -> acc
        | pattern :: rest ->
            match trimConstructorName(state)(pattern) with
                | Some(name) -> trimSeenConstructors(state)(rest)(name :: acc)
                | None -> trimSeenConstructors(state)(rest)(acc)

let recursive trimAllSeen (names: List(Str)) (seen: List(Str)) =
    match names with
        | [] -> true
        | name :: rest ->
            if containsName(name)(seen)
            then trimAllSeen(rest)(seen)
            else false

let recursive trimAnyCatchAll state patterns =
    match patterns with
        | [] -> false
        | pattern :: rest ->
            if isCatchAllPattern(state)(pattern)
            then true
            else trimAnyCatchAll(state)(rest)

let recursive trimHasBool patterns (wanted: Bool) =
    match patterns with
        | [] -> false
        | pattern :: rest ->
            match unwrapPatternAt(pattern) with
                | PatternBool(value) ->
                    if value == wanted
                    then true
                    else trimHasBool(rest)(wanted)
                | _ -> trimHasBool(rest)(wanted)

let recursive trimHasEmptyList patterns =
    match patterns with
        | [] -> false
        | pattern :: rest ->
            match unwrapPatternAt(pattern) with
                | PatternEmptyList -> true
                | _ -> trimHasEmptyList(rest)

let recursive trimHasCons patterns =
    match patterns with
        | [] -> false
        | pattern :: rest ->
            match unwrapPatternAt(pattern) with
                | PatternCons(_head, _tail) -> true
                | _ -> trimHasCons(rest)

let recursive trimFirstConstructor state patterns =
    match patterns with
        | [] -> None
        | pattern :: rest ->
            match trimConstructorName(state)(pattern) with
                | Some(name) -> Some(name)
                | None -> trimFirstConstructor(state)(rest)

// Whether a prefix of exact-coverage patterns matches every value of its domain.
let trimPrefixCoversAll state patterns =
    if trimAnyCatchAll(state)(patterns)
    then true
    else
        match trimFirstConstructor(state)(patterns) with
            | Some(name) ->
                match trimAdtOfConstructor(state)(name) with
                    | Some(adtName) ->
                        match trimAdtConstructorNames(stateConstructorsOfType(adtName)(state))(adtName)([]) with
                            | [] -> false
                            | names ->
                                []
                                |> trimSeenConstructors(state)(patterns)
                                |> trimAllSeen(names)
                    | None -> false
            | None ->
                if trimHasBool(patterns)(true)
                then trimHasBool(patterns)(false)
                else
                    if trimHasEmptyList(patterns)
                    then trimHasCons(patterns)
                    else false

let recursive trimTake cases (count: Int) acc =
    match cases with
        | [] -> reverse(acc)
        | matchCase :: rest ->
            if count == 0
            then reverse(acc)
            else trimTake(rest)(count - 1)(matchCase :: acc)

// Grows the prefix one guard-free exact-coverage arm at a time; the first prefix that covers
// every value ends the match there.
let recursive trimProvablyUnreachableTrailingCasesFrom state cases remaining (prefixPatterns: List(Pattern)) (prefixLength: Int) =
    match remaining with
        | [] -> cases
        | (pattern, _body, guard) :: rest ->
            match rest with
                | [] -> cases
                | _ ->
                    match guard with
                        | Some(_guard) -> cases
                        | None ->
                            if isExactCoveragePattern(state)(pattern)
                            then
                                let patterns = append(prefixPatterns)([pattern])
                                in
                                    if trimPrefixCoversAll(state)(patterns)
                                    then trimTake(cases)(prefixLength + 1)([])
                                    else trimProvablyUnreachableTrailingCasesFrom(state)(cases)(rest)(patterns)(prefixLength + 1)
                            else cases

let trimProvablyUnreachableTrailingCases state cases = trimProvablyUnreachableTrailingCasesFrom(state)(cases)(cases)([])(0)

let lowerMatchArmsDispatch allCases lower (plan: CoreMatchPlan) =
    match plan with
        | CoreMatchPlan { error = Some(_error) } -> plan
        | _ ->
            let cases = trimProvablyUnreachableTrailingCases(plan.state)(allCases)
            in
                match planTagGroups(cases)(0)(plan.state)(None)([]) with
                    | Some((groups, defaultIndex)) -> lowerMatchArmsViaTagGroups(cases)(groups)(defaultIndex)(lower)(plan)
                    | None -> lowerMatchArms(cases)(lower)(plan)

let coverageErrorMessage inferenceError =
    match inferenceError with
        | NonExhaustiveMatch(message) -> message
        | UnreachableMatchArm(message) -> message
        | other -> Ashes.Trait.Show.show(other)

let checkCoreMatchCoverage cases (plan: CoreMatchPlan) =
    match plan with
        | CoreMatchPlan { error = Some(_error) } -> plan
        | CoreMatchPlan { state = state, valueType = valueType } ->
            match state
            |> coverageEnvironment
            |> matchCoverageError(cases)(resolveType(state)(valueType)) with
                | None -> plan
                | Some(inferenceError) ->
                    plan with error = Some(inferenceError
                    |> coverageErrorMessage
                    |> CoreMatchCoverageError)

let elementParameterSlot (element: Expr) (state: CoreLoweringState) =
    match (unspanArgument(element), stateTcoLoopFrame(state)) with
        | (ExprVar(name), Some(frame)) ->
            match lookupBinding(name)(state.bindings) with
                | Some(CoreBinding { location = CoreLocal(slot) }) ->
                    if ordinalOfSlot(slot)(frame.parameterSlots)(0) != None
                    then Some(slot)
                    else None
                | _ -> None
        | _ -> None

let recursive elementParameterSlots (elements: List(Expr)) (state: CoreLoweringState) =
    match elements with
        | [] -> []
        | element :: rest -> elementParameterSlot(element)(state) :: elementParameterSlots(rest)(state)

let scrutineeParameterSlotsOf (scrutinee: Expr) (state: CoreLoweringState) =
    match unspanArgument(scrutinee) with
        | ExprTuple(elements) -> elementParameterSlots(elements)(state)
        | _ -> []

// The scrutinee owner every arm may adopt, decided once the scrutinee is lowered.
let withScrutineeOwner (scrutinee: Expr) (plan: CoreMatchPlan) =
    match plan with
        | CoreMatchPlan { error = Some(_error) } -> plan
        | CoreMatchPlan { state = state, valueTemp = valueTemp, valueType = valueType } -> plan with scrutineeOwner = scrutineeOwnerOf(scrutinee)(valueTemp)(valueType)(state), scrutineeParameterSlots = scrutineeParameterSlotsOf(scrutinee)(state)

// Stage 0's `ShouldNormalizeStaticStringMatchJoin`: a function that may run under a live handler
// post normalizes its literal arms only for a consumer that asked for a reference-counted string.
let withStaticStringNormalization cases (plan: CoreMatchPlan) =
    match plan with
        | CoreMatchPlan { error = Some(_error) } -> plan
        | CoreMatchPlan { state = state, armRequest = ConsumerRequest { runtimeString = requested } } ->
            plan with normalizeStaticStrings = (ordinaryRcPlacementAllowed(state) || requested) && shouldNormalizeStaticStringArms(given (body: Expr) -> isRuntimeRcStringProducer(body)(state) || retainedPatternOwnerTerminal(body)(state))(given (body: Expr) -> isSelfFunnelArm(body)(state))(given (body: Expr) -> isStaticConstructorArm(body)(state))(cases)

// Whether no part of a pattern names a binder: a shape of wildcards and literals only.
let recursive bindsNoName (pattern: Pattern) =
    match pattern with
        | PatternAt(_span, inner) -> bindsNoName(inner)
        | PatternWildcard -> true
        | PatternEmptyList -> true
        | PatternInt(_value) -> true
        | PatternString(_value) -> true
        | PatternRune(_value) -> true
        | PatternBool(_value) -> true
        | PatternCons(head, tail) -> bindsNoName(head) && bindsNoName(tail)
        | PatternTuple(elements) -> allBindNoName(elements)
        | PatternConstructor(_name, patterns) -> allBindNoName(patterns)
        | PatternRecord(_name, fields) -> allFieldsBindNoName(fields)
        | PatternOr(alternatives) -> allBindNoName(alternatives)
        | _ -> false
and allBindNoName (patterns: List(Pattern)) =
    match patterns with
        | [] -> true
        | pattern :: rest -> bindsNoName(pattern) && allBindNoName(rest)
and allFieldsBindNoName (fields: List((Str, Pattern))) =
    match fields with
        | [] -> true
        | (_field, pattern) :: rest -> bindsNoName(pattern) && allFieldsBindNoName(rest)

// Stage 0's shape rule: a record, constructor, tuple, or cons shape matched against a value whose
// type is still a variable (a loop generic in it) binds a heap value only when it names a binder.
let isShapeAgainstVariable (pattern: Pattern) (semanticType: SemanticType) (state: CoreLoweringState) =
    match (pattern, resolveType(state)(semanticType)) with
        | (PatternAt(_span, inner), resolved) ->
            (match inner with
                | PatternRecord(_name, _fields) -> true
                | PatternConstructor(_name, _patterns) -> true
                | PatternTuple(_elements) -> true
                | PatternCons(_head, _tail) -> true
                | _ -> false) && (match resolved with
                | SemVariable(_id) -> true
                | SemParameter(_id, _name) -> true
                | _ -> false)
        | (PatternRecord(_name, _fields), SemVariable(_id)) -> true
        | (PatternConstructor(_name, _patterns), SemVariable(_id)) -> true
        | (PatternTuple(_elements), SemVariable(_id)) -> true
        | (PatternCons(_head, _tail), SemVariable(_id)) -> true
        | (PatternRecord(_name, _fields), SemParameter(_id, _pname)) -> true
        | (PatternConstructor(_name, _patterns), SemParameter(_id, _pname)) -> true
        | (PatternTuple(_elements), SemParameter(_id, _pname)) -> true
        | (PatternCons(_head, _tail), SemParameter(_id, _pname)) -> true
        | _ -> false

// Stage 0's `PatternBindsHeapValue`: whether `pattern`, matched against a value of
// `semanticType`, may bind a value that is not a copy type; any shape it cannot see through counts
// as one. A constructor's field types are instantiated on a copy of the state the answer leaves.
let recursive patternBindsHeapValue (pattern: Pattern) (semanticType: SemanticType) (state: CoreLoweringState) =
    if isShapeAgainstVariable(pattern)(semanticType)(state)
    then !bindsNoName(pattern)
    else
        match pattern with
            | PatternAt(_span, inner) -> patternBindsHeapValue(inner)(semanticType)(state)
            | PatternWildcard -> false
            | PatternEmptyList -> false
            | PatternInt(_value) -> false
            | PatternString(_value) -> false
            | PatternRune(_value) -> false
            | PatternBool(_value) -> false
            | PatternVar(_name) -> resultSurvivesReset(semanticType)(state) == false
            | PatternAs(inner, _name) -> resultSurvivesReset(semanticType)(state) == false || patternBindsHeapValue(inner)(semanticType)(state)
            | PatternOr(alternatives) -> patternsBindHeapValue(alternatives)(semanticType)(state)
            | PatternTuple(elements) ->
                match resolveType(state)(semanticType) with
                    | SemTuple(elementTypes) ->
                        if length(elements) == length(elementTypes)
                        then elementPatternsBindHeapValue(elements)(elementTypes)(state)
                        else true
                    | _ -> true
            | PatternCons(head, tail) ->
                match resolveType(state)(semanticType) with
                    | SemList(element) -> patternBindsHeapValue(head)(element)(state) || patternBindsHeapValue(tail)(semanticType)(state)
                    | _ -> true
            | PatternConstructor(name, patterns) ->
                match constructorLayout(name)(state) with
                    | None -> true
                    | Some(layout) ->
                        match instantiateConstructor(layout)(state) with
                            | CoreConstructorShape { state = instantiated, parameterTypes = parameterTypes, resultType = resultType } ->
                                match bindType(semanticType)(resultType)(instantiated) with
                                    | (typed, None) ->
                                        if length(patterns) == length(parameterTypes)
                                        then elementPatternsBindHeapValue(patterns)(parameterTypes)(typed)
                                        else true
                                    | (_failed, Some(_error)) -> true
            | PatternRecord(name, fields) ->
                match constructorLayout(name)(state) with
                    | None -> true
                    | Some(layout) ->
                        match instantiateConstructor(layout)(state) with
                            | CoreConstructorShape { state = instantiated, layout = CoreConstructorLayout { fieldNames = fieldNames }, parameterTypes = parameterTypes, resultType = resultType } ->
                                match bindType(semanticType)(resultType)(instantiated) with
                                    | (typed, None) -> fieldPatternsBindHeapValue(fields)(fieldNames)(parameterTypes)(typed)
                                    | (_failed, Some(_error)) -> true
and patternsBindHeapValue (patterns: List(Pattern)) (semanticType: SemanticType) (state: CoreLoweringState) =
    match patterns with
        | [] -> false
        | pattern :: rest -> patternBindsHeapValue(pattern)(semanticType)(state) || patternsBindHeapValue(rest)(semanticType)(state)
and elementPatternsBindHeapValue (patterns: List(Pattern)) (semanticTypes: List(SemanticType)) (state: CoreLoweringState) =
    match (patterns, semanticTypes) with
        | (pattern :: restPatterns, semanticType :: restTypes) -> patternBindsHeapValue(pattern)(semanticType)(state) || elementPatternsBindHeapValue(restPatterns)(restTypes)(state)
        | _ -> false
and fieldPatternsBindHeapValue (fields: List((Str, Pattern))) (fieldNames: List(Str)) (fieldTypes: List(SemanticType)) (state: CoreLoweringState) =
    match fields with
        | [] -> false
        | (fieldName, pattern) :: rest ->
            match findPatternField(fieldName)(fieldNames)(fieldTypes)(0) with
                | None -> true
                | Some(CorePatternField { semanticType = fieldType }) -> patternBindsHeapValue(pattern)(fieldType)(state) || fieldPatternsBindHeapValue(rest)(fieldNames)(fieldTypes)(state)

let recursive matchCasesBindHeapValue cases (semanticType: SemanticType) (state: CoreLoweringState) =
    match cases with
        | [] -> false
        | (pattern, _body, _guard) :: rest -> patternBindsHeapValue(pattern)(semanticType)(state) || matchCasesBindHeapValue(rest)(semanticType)(state)

// The reversed instructions with the pattern-owner marker defining `temp` made a plain borrow, so
// the loop's finalize never promotes it to a retain.
let recursive borrowInsteadOfMarker (temp: Int) (reversed: List(IrInstruction)) =
    match reversed with
        | [] -> []
        | (IrInstruction { instruction = RcDup(target, source, false, _mayBeEmpty) } as instruction) :: rest ->
            if target == temp
            then (instruction with instruction = Borrow(target)(source)) :: rest
            else instruction :: borrowInsteadOfMarker(temp)(rest)
        | instruction :: rest -> instruction :: borrowInsteadOfMarker(temp)(rest)

let recursive isMarkerDefinition (temp: Int) (reversed: List(IrInstruction)) =
    match reversed with
        | [] -> false
        | IrInstruction { instruction = RcDup(target, _source, false, _mayBeEmpty) } :: rest -> target == temp || isMarkerDefinition(temp)(rest)
        | IrInstruction { instruction = kind } :: rest ->
            if kind
            |> getDefinedTemps
            |> containsInt(temp)
            then false
            else isMarkerDefinition(temp)(rest)

// Stage 0's withdrawal of a pattern-owner duplicate a borrowed scrutinee stores: the fresh cell is
// matched only by its shape and dies in the arena, so the duplicate of an element it holds (the
// cell its only reader) is a plain borrow rather than a marker the root's placement would promote.
// A scrutinee built on the reference-counted heap owns its elements and keeps its markers.
let recursive demoteScrutineeMarkers (values: List(Int)) (state: CoreLoweringState) =
    match values with
        | [] -> state
        | value :: rest ->
            if isMarkerDefinition(value)(state.reversedInstructions) && countTempUses(value)(state.reversedInstructions)(0) == 1
            then demoteScrutineeMarkers(rest)((state with reversedInstructions = borrowInsteadOfMarker(value)(state.reversedInstructions)))
            else demoteScrutineeMarkers(rest)(state)

// Stage 0's `RecordBorrowedScrutinee`: a fresh aggregate a loop body builds only to match it, with
// no arm binding a heap-typed part of it, keeps nothing past the match, so the retains of
// loop-parameter reads its cell was built with are withdrawn once the loop's placements are final.
let recordBorrowedScrutinee (value: Expr) cases (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            match stateTcoLoopFrame(state) with
                | Some(_frame) ->
                    if isFreshAggregateLiteral(value)(state) && matchCasesBindHeapValue(cases)(semanticType)(state) == false
                    then
                        lowered with state = state |> demoteScrutineeMarkers(if isArenaCell(temp)(state.reversedInstructions)
                        then storedValuesOf(temp)(state.reversedInstructions)([])
                        else []) |> withStateBorrowedScrutineeCells(temp :: stateBorrowedScrutineeCells(state))
                    else lowered
                | None -> lowered
        | _ -> lowered

// A tuple literal matched on directly is the match's scrutinee: no consumer keeps it, which
// `lowerTuple` reads when it chooses the tuple's placement.
let markScrutineeTuple (value: Expr) (state: CoreLoweringState) =
    match stripExprAt(value) with
        | ExprTuple(_elements) -> withStateScrutineeTuple(true)(state)
        | _ -> state

let lowerMatch value cases lower state =
    state
    |> clearConsumerRequest
    |> (given (cleared: CoreLoweringState) ->
        if stackAllocatableScrutineeCases(cases) && constructorExpression(value)(cleared)
        then requestStackAllocatedConstructor(cleared)
        else cleared)
    |> markScrutineeTuple(value)
    |> lower(value)
    |> recordBorrowedScrutinee(value)(cases)
    |> noteLoweredValueBinders(matchCaseBinders(cases)([]))
    |> prepareMatchPlan
    |> withPlanArmRequest(withReconcilableFreshStringJoinRequest(ExprMatch(value)(cases)(None))(state
    |> consumerRequestOf
    |> branchRequest)(state))
    |> withScrutineeOwner(value)
    |> withReuseScrutinee(value)(cases)
    |> withStaticStringNormalization(cases)
    |> checkCoreMatchCoverage(cases)
    |> lowerMatchArmsDispatch(cases)(lower)
    |> finishMatchPlanAs(isArenaSuccessorJoin(state))

let recursive lambdaParts expression =
    match expression with
        | ExprAt(_span, inner) -> lambdaParts(inner)
        | ExprNode(_node, inner) -> lambdaParts(inner)
        | ExprLambda(parameter, body, annotation) -> Some((parameter, body, annotation))
        | _ -> None
