// Tail-call loop frames: loop parameter slots, exit own-field flags, specialization accumulators,
// and the lowering of function and lambda bodies. A part of `AshesCompiler.Semantics.CoreLowering`.

import AshesCompiler.Semantics.NodeIdentity.withoutNodeIdentity
import Ashes.Collection.List.append
import Ashes.Collection.List.length
import Ashes.Collection.List.map
import Ashes.Collection.List.reverse
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.Pattern
import AshesCompiler.Semantics.CoreBuiltinLowering
import AshesCompiler.Semantics.HeapLayoutClassification
import AshesCompiler.Semantics.IrControlFlowGraph.containsInt
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.OwnershipInference.moveSafetyProof
import AshesCompiler.Semantics.PathOccurrences
import AshesCompiler.Semantics.OwnershipSummary
import AshesCompiler.Semantics.StructuralDroppers
import AshesCompiler.Semantics.StructuralCopiers
import AshesCompiler.Semantics.ToSpaceCopiers
import AshesCompiler.Semantics.TcoAffineAppend.affineSelfAppendOrdinals
import AshesCompiler.Semantics.TcoRuntimeManagedParams
import AshesCompiler.Semantics.PatternBindingOwnership
import AshesCompiler.Semantics.TcoAnalysis.collectInnermostBody
import AshesCompiler.Semantics.TcoAnalysis.collectLambdaParamNames
import AshesCompiler.Semantics.TcoAnalysis.countLambdaArity
import AshesCompiler.Semantics.TcoAnalysis.hasTailSelfCalls
import AshesCompiler.Semantics.TcoAnalysis.hasTmcConsSelfCalls
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.ReuseFunctionSpecialization
import AshesCompiler.Semantics.ReuseResetSafety.namedAccumulatorFieldsPersistent
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.CoreLowering.LoweringState
import AshesCompiler.Semantics.CoreLowering.Emit
import AshesCompiler.Semantics.CoreLowering.Owners
import AshesCompiler.Semantics.CoreLowering.CopyPlans
import AshesCompiler.Semantics.CoreLowering.TcoReset
import AshesCompiler.Semantics.CoreLowering.ResultHandOver
import AshesCompiler.Semantics.CoreLowering.Bindings
import AshesCompiler.Semantics.CoreLowering.LoopPlacement
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.recordedParameterTypeAtEntry
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.recordedParameterTypeAtEntryBy
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.recordedSelfScopeKey
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.enterLambdaAtNode
export (
    value constructorNamesOf,
    value unwrapPatternSpan,
    value builtinBytesProvenanceOf,
    value enterRecursiveTcoLoop,
    value exitParameterSuccessorKind,
    value specializationFieldRelocatable,
    value accumulatorLayoutIsPersistable,
    value enterTcoLoopBody,
    value requestsArenaResult,
    value seedParameterFromConstructorFields,
    value withRecursiveBodyRequest,
    value lowerRecursiveBodyResolvingCalls,
    value expectedLambdaResultType,
    value lowerLambdaBody,
)

// Stage 0's `TcoContext`: a recursive binding whose innermost lambda body has a tail self-call
// applying every parameter is lowered as a TCO loop over that innermost body; the curried
// lambdas between the binding and the body are entered on the way.
let recursive constructorNamesOf (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> []
        | CoreConstructorLayout { name = name } :: rest -> name :: constructorNamesOf(rest)

// The constructors a bare pattern name tests rather than binds (`isNullaryConstructorPatternName`).
let recursive nullaryConstructorNamesOf (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> []
        | CoreConstructorLayout { scheme = TypeScheme { body = SemFunction(_, _, _) } } :: rest -> nullaryConstructorNamesOf(rest)
        | CoreConstructorLayout { name = name } :: rest -> name :: nullaryConstructorNamesOf(rest)

let recursive unwrapPatternSpan (pattern: Pattern) =
    match pattern with
        | PatternAt(_span, inner) -> unwrapPatternSpan(inner)
        | other -> other

let recursive firstConstructorPatternName (cases: List((Pattern, Expr, Maybe(Expr)))) =
    match cases with
        | [] -> None
        | (pattern, _body, _guard) :: rest ->
            match unwrapPatternSpan(pattern) with
                | PatternConstructor(name, _subPatterns) -> Some(name)
                | _ -> firstConstructorPatternName(rest)

let recursive matchedAccumulatorRecorded (name: Str) (found: List((Str, Str))) =
    match found with
        | [] -> false
        | (candidate, _constructorName) :: rest -> candidate == name || matchedAccumulatorRecorded(name)(rest)

// Stage 0's `CollectCtorMatchedScrutinees`: the loop parameters the loop body matches directly by
// a constructor pattern, each with the first such constructor, in the order the body first
// matches them; the walk follows `if` branches, `let` bodies, and match arms.
let recursive collectCtorMatchedScrutinees (parameters: List(Str)) (expression: Expr) (found: List((Str, Str))) =
    match expression with
        | ExprAt(_span, inner) -> collectCtorMatchedScrutinees(parameters)(inner)(found)
        | ExprNode(_node, inner) -> collectCtorMatchedScrutinees(parameters)(inner)(found)
        | ExprIf(_condition, thenBranch, elseBranch) ->
            found
            |> collectCtorMatchedScrutinees(parameters)(thenBranch)
            |> collectCtorMatchedScrutinees(parameters)(elseBranch)
        | ExprLet(_name, _value, body, _parameters, _annotation, _constraints) -> collectCtorMatchedScrutinees(parameters)(body)(found)
        | ExprLetRecursive(_name, _value, body, _parameters, _annotation, _constraints) -> collectCtorMatchedScrutinees(parameters)(body)(found)
        | ExprMatch(value, cases, _position) ->
            found
            |> recordMatchedAccumulator(parameters)(value)(cases)
            |> collectCtorMatchedScrutineesInCases(parameters)(cases)
        | _ -> found
and recordMatchedAccumulator (parameters: List(Str)) (value: Expr) (cases: List((Pattern, Expr, Maybe(Expr)))) (found: List((Str, Str))) =
    match unspanArgument(value) with
        | ExprVar(name) ->
            if containsName(name)(parameters) && !matchedAccumulatorRecorded(name)(found)
            then
                match firstConstructorPatternName(cases) with
                    | Some(constructorName) -> append(found)([(name, constructorName)])
                    | None -> found
            else found
        | _ -> found
and collectCtorMatchedScrutineesInCases (parameters: List(Str)) (cases: List((Pattern, Expr, Maybe(Expr)))) (found: List((Str, Str))) =
    match cases with
        | [] -> found
        | (_pattern, body, _guard) :: rest ->
            found
            |> collectCtorMatchedScrutinees(parameters)(body)
            |> collectCtorMatchedScrutineesInCases(parameters)(rest)

// The Bytes provenance a builtin's result is declared with (stage 0's `BytesProvenance` of a
// builtin member): a new buffer, a view borrowed from an argument, or a view of a mapping that
// lives as long as the program.
let builtinKindBytesProvenance (kind: CoreBuiltinKind) =
    match kind with
        | CoreBytesSubView -> BytesProvenanceBorrowedView
        | CoreBytesFromText -> BytesProvenanceBorrowedView
        | CoreFileMmap -> BytesProvenanceProgramLifetimeView
        | CoreBytesEmpty -> BytesProvenanceFreshOwnedBuffer
        | CoreBytesSingleton -> BytesProvenanceFreshOwnedBuffer
        | CoreBytesAppend -> BytesProvenanceFreshOwnedBuffer
        | CoreBytesAppendByte -> BytesProvenanceFreshOwnedBuffer
        | CoreBytesAllocate -> BytesProvenanceFreshOwnedBuffer
        | CoreBytesCopyRange -> BytesProvenanceFreshOwnedBuffer
        | CoreBytesSet -> BytesProvenanceFreshOwnedBuffer
        | CoreBytesSetU16Le -> BytesProvenanceFreshOwnedBuffer
        | CoreBytesSetU32Le -> BytesProvenanceFreshOwnedBuffer
        | CoreBytesSetU64Le -> BytesProvenanceFreshOwnedBuffer
        | CoreBytesFromList -> BytesProvenanceFreshOwnedBuffer
        | CoreBytesFromReversedList -> BytesProvenanceFreshOwnedBuffer
        | CoreBytesU16Le -> BytesProvenanceFreshOwnedBuffer
        | CoreBytesU32Le -> BytesProvenanceFreshOwnedBuffer
        | CoreBytesU64Le -> BytesProvenanceFreshOwnedBuffer
        | _ -> BytesProvenanceUnknown

// Stage 0's `TryGetBuiltinBytesProvenance`: the declared Bytes provenance of a saturated builtin
// call, unknown for any other expression.
let builtinBytesProvenanceOf (expression: Expr) (state: CoreLoweringState) =
    match qualifiedCallRoot(expression)(0) with
        | Some((moduleName, memberName, argumentCount)) ->
            match builtinLayout(moduleName)(memberName)(state) with
                | Some(CoreBuiltinLayout { kind = kind } as layout) ->
                    if argumentCount == builtinArity(layout)
                    then builtinKindBytesProvenance(kind)
                    else BytesProvenanceUnknown
                | None -> BytesProvenanceUnknown
        | None -> BytesProvenanceUnknown

// The candidates a loop parameter is routed to are the self-recursive ones with a single parameter
// and the nested-worker ones (`loopRoutableSpecializationCandidates`): stage 0 excludes a
// multi-parameter self-recursive candidate (`_freshCompositionOnlySpecializable`), since only the
// fresh-composition call site can supply its earlier arguments.
let recursiveTcoLoop (name: Str) (parameter: Str) (parameterType: SemanticType) (body: Expr) (emitsLoop: Bool) (layouts: List(CoreConstructorLayout)) (candidates: List((Str, List(Str), Expr))) =
    (let arity = 1 + countLambdaArity(0)(body)
    in
        let parameters = parameter :: collectLambdaParamNames([])(body)
        in
            let innermost = collectInnermostBody(body)
            in
                let shapes = tcoSelfCallShapes(name)(parameters)(innermost)
                in
                    if hasTailSelfCalls(innermost)(name)(arity) || hasTmcConsSelfCalls(innermost)(name)(arity)
                    then
                        Some(CoreTcoLoop(
                            selfName = name,
                            arity = arity,
                            pendingCurried = arity - 1,
                            parameterNames = parameters,
                            affineOrdinals = affineSelfAppendOrdinals(name)(parameters)(innermost),
                            runtimeManagedOrdinals = runtimeManagedStrOrdinals(name)(parameters)(innermost),
                            argumentShapes = shapes,
                            accumulatorRebuildOrdinals = tcoAccumulatorRebuildOrdinals(name)(parameters)(innermost),
                            patternFacts = patternBindingFacts(name)(parameters)(constructorNamesOf(layouts))(nullaryConstructorNamesOf(layouts))(innermost),
                            matchedAccumulators = collectCtorMatchedScrutinees(parameters)(innermost)([]),
                            specializationAccumulators = collectSpecializableCallArgs(parameters)(loopRoutableSpecializationCandidates(candidates))(innermost)([]),
                            tmcShapePresent = hasTmcConsSelfCalls(innermost)(name)(arity),
                            selfCallParameterFlow = tcoSelfCallParameterFlow(name)(parameters)([])(innermost),
                            selfCallSpineFlow = tcoSelfCallSpineFlow(name)(parameters)([])(innermost),
                            body = innermost,
                            callFlow = [],
                            callFlowPending = false,
                            emitsLoop = emitsLoop,
                            parameterTypes = [parameterType],
                            unreadSlots = []
                        ))
                    else None)

// A single recursive binding's loop is emitted as stage 0's loop; a mutual-recursion group
// member keeps the loop context for its tail self-call arguments but stays a call until the
// group dispatch is ported.
let enterRecursiveTcoLoop (name: Str) (parameter: Str) (parameterType: SemanticType) (body: Expr) (emitsLoop: Bool) (state: CoreLoweringState) =
    withStateTcoLoop(state
    |> stateSpecializationCandidates
    |> recursiveTcoLoop(name)(parameter)(parameterType)(body)(emitsLoop)(stateConstructorLayouts(state)))(state)

let recursive isChainParameterName (name: Str) (names: List(Str)) =
    match names with
        | [] -> false
        | candidate :: rest -> candidate == name || isChainParameterName(name)(rest)

// Stage 0's `LowerLambdaCoreBindTcoParamSlots`: every chain parameter the loop body captures
// moves from the closure environment into a local slot at function entry, and the body reads
// it from there.
let recursive bindChainParameterSlots (names: List(Str)) (parameter: Str) (bindings: List(CoreBinding)) (state: CoreLoweringState) =
    match bindings with
        | [] -> state
        | CoreBinding { name = name, scheme = scheme, location = CoreEnvironment(index) } :: rest ->
            if name != parameter && isChainParameterName(name)(names)
            then
                match freshLocal(state) with
                    | FreshLocal { state = slotState, local = slot } ->
                        match freshTemp(slotState) with
                            | FreshTemp { state = tempState, temp = temp } ->
                                tempState
                                |> emit(LoadEnv(temp)(index))
                                |> emit(StoreLocal(slot)(temp))
                                |> addBinding(name)(scheme)(CoreLocal(slot))
                                |> bindChainParameterSlots(names)(parameter)(rest)
            else bindChainParameterSlots(names)(parameter)(rest)(state)
        | _ :: rest -> bindChainParameterSlots(names)(parameter)(rest)(state)

// A synthetic slot joins the loop's unread slots with its parameter's type, which the placement
// reads in place of a binding's (stage 0's `UnreadParamSlots` beside `ParamTypes`).
let withUnreadLoopSlot (slot: Int) (ordinal: Int) (state: CoreLoweringState) =
    match stateTcoLoop(state) with
        | Some(loop) ->
            match typeAtIndex(ordinal)(loop.parameterTypes) with
                | Some(parameterType) -> withStateTcoLoop(Some((loop with unreadSlots = (slot, parameterType) :: loop.unreadSlots)))(state)
                | None -> state
        | None -> state

// Stage 0's `LowerLambdaCoreBuildTcoParamSlots`: the back edge's parameter slots in parameter
// order; a shadowed earlier occurrence of a duplicate name, or a parameter the body never reads,
// gets a zeroed synthetic slot nothing observes.
let recursive buildLoopParameterSlots (names: List(Str)) (reversedSlots: List(Int)) (state: CoreLoweringState) =
    match names with
        | [] -> (state, reverse(reversedSlots))
        | name :: rest ->
            match (isChainParameterName(name)(rest), lookupBinding(name)(state.bindings)) with
                | (false, Some(CoreBinding { location = CoreLocal(slot) })) -> buildLoopParameterSlots(rest)(slot :: reversedSlots)(state)
                | _ ->
                    match freshLocal(state) with
                        | FreshLocal { state = slotState, local = slot } ->
                            match freshTemp(slotState) with
                                | FreshTemp { state = tempState, temp = temp } ->
                                    tempState
                                    |> emit(LoadConstInt(temp)(0))
                                    |> emit(StoreLocal(slot)(temp))
                                    |> withUnreadLoopSlot(slot)(length(reversedSlots))
                                    |> buildLoopParameterSlots(rest)(slot :: reversedSlots)

// The reservation slot pair of every affine accumulator, zeroed so no string matches a
// reservation until the loop's first fallback reserves one; each pair comes back with the
// accumulator's parameter slot so the tail self-call can arm the in-place append.
let recursive emitAffineReservations (ordinals: List(Int)) (slots: List(Int)) (ordinal: Int) (zeroTemp: Int) (state: CoreLoweringState) =
    match slots with
        | [] -> (state, [])
        | slot :: rest ->
            if containsInt(ordinal)(ordinals)
            then
                match freshLocal(state) with
                    | FreshLocal { state = startState, local = reservationStart } ->
                        match freshLocal(startState) with
                            | FreshLocal { state = endState, local = reservationEnd } ->
                                match endState
                                |> emit(StoreLocal(reservationStart)(zeroTemp))
                                |> emit(StoreLocal(reservationEnd)(zeroTemp))
                                |> emitAffineReservations(ordinals)(rest)(ordinal + 1)(zeroTemp) with
                                    | (reserved, pairs) -> (reserved, (slot, reservationStart, reservationEnd) :: pairs)
            else emitAffineReservations(ordinals)(rest)(ordinal + 1)(zeroTemp)(state)

let finishTcoLoopEntry (label: Str) (slots: List(Int)) (exitOwnFieldFlags: List((Int, Int, Int))) (fixedCursorSlot: Int) (fixedEndSlot: Int) (compactionSizeSlot: Int) (entrySpliceCount: Int) (affineReservations: List((Int, Int, Int))) (listActiveSlots: List((Int, Int))) (tmcDestSlot: Int) (tmcResultSlot: Int) (bracket: ArenaBracket) =
    match bracket with
        | ArenaBracket { bracketState = iterationState, bracketCursorSlot = cursorSlot, bracketEndSlot = endSlot } ->
            match freshLocal(iterationState) with
                | FreshLocal { state = stackState, local = stackPointerSlot } ->
                    stackState
                    |> emit(SaveStackPointer(stackPointerSlot))
                    |> (given (entered: CoreLoweringState) ->
                        withStateTcoLoopFrame(Some(CoreTcoLoopFrame(
                            bodyLabel = label + "_body",
                            parameterSlots = slots,
                            arenaCursorSlot = cursorSlot,
                            arenaEndSlot = endSlot,
                            stackPointerSlot = stackPointerSlot,
                            fixedCursorSlot = fixedCursorSlot,
                            fixedEndSlot = fixedEndSlot,
                            compactionSizeSlot = compactionSizeSlot,
                            ownerDepth = entered.nextLocal,
                            entrySpliceCount = entrySpliceCount,
                            listActiveSlots = listActiveSlots,
                            affineReservationSlots = affineReservations,
                            runtimeManagedListSlots = [],
                            runtimeManagedAdtSlots = [],
                            runtimeManagedStrSlots = [],
                            tmcDestSlot = tmcDestSlot,
                            tmcResultSlot = tmcResultSlot,
                            exitOwnFieldFlags = exitOwnFieldFlags,
                            exitOwnFieldCells = []
                        )))(entered))

// The active-flag local of every parameter whose self-call shape may place a list on the
// reference-counted heap, or that the affine analysis keeps as a `Str` accumulator candidate,
// and whose type is already resolved, allocated in parameter order ahead of the body label the
// way stage 0 allocates `RuntimeManagedParamActiveSlots` at its loop entry; a parameter the
// resolved types later keep in the arena leaves its local unwritten.
let recursive allocateListActiveSlots (ordinal: Int) (loop: CoreTcoLoop) (shapes: List(TcoArgumentShape)) (slots: List(Int)) (state: CoreLoweringState) =
    match (shapes, slots) with
        | (shape :: restShapes, slot :: restSlots) ->
            if listActiveSlotCandidate(ordinal)(loop)(shape)(slot)(state) && loopSlotTypeResolved(slot)(state)
            then
                match freshLocal(state) with
                    | FreshLocal { state = allocated, local = activeSlot } ->
                        match allocateListActiveSlots(ordinal + 1)(loop)(restShapes)(restSlots)(allocated) with
                            | (rest, pairs) -> (rest, (slot, activeSlot) :: pairs)
            else allocateListActiveSlots(ordinal + 1)(loop)(restShapes)(restSlots)(state)
        | _ -> (state, [])

// The tail-modulo-constructor spine's two loop-carried slots, zeroed once before the body label:
// nothing has been built yet, so both the pending-destination cell and the spine head start nil.
// A loop without that cons shape reserves neither and keeps both at -1.
let allocateTmcChainSlots (tmcShapePresent: Bool) (zeroTemp: Int) (state: CoreLoweringState) =
    if tmcShapePresent
    then
        match freshLocal(state) with
            | FreshLocal { state = destState, local = destSlot } ->
                match freshLocal(destState) with
                    | FreshLocal { state = resultState, local = resultSlot } ->
                        (resultState
                        |> emit(StoreLocal(destSlot)(zeroTemp))
                        |> emit(StoreLocal(resultSlot)(zeroTemp)), destSlot, resultSlot)
    else (state, -1, -1)

let recursive exitSuccessorSpine (expression: Expr) (arguments: List(Expr)) =
    match expression with
        | ExprAt(_span, inner) -> exitSuccessorSpine(inner)(arguments)
        | ExprNode(_node, inner) -> exitSuccessorSpine(inner)(arguments)
        | ExprCall(function, argument, _sugar, _layout) -> exitSuccessorSpine(function)(argument :: arguments)
        | other -> (other, arguments)

let constructorNamedOfArity (name: Str) (arity: Int) (state: CoreLoweringState) =
    match constructorLayout(name)(state) with
        | Some(layout) -> constructorArity(layout) == arity
        | None -> false

// Stage 0's `ExitParameterSuccessorKind`: 0 the parameter handed on, 1 a nullary constructor, 2 a
// constructor application, -1 anything else.
let exitParameterSuccessorKind (argument: Expr) (name: Str) (state: CoreLoweringState) =
    match unspanArgument(argument) with
        | ExprVar(candidate) ->
            if candidate == name
            then 0
            else
                if constructorNamedOfArity(candidate)(0)(state)
                then 1
                else -1
        | ExprCall(_function, _argument, _sugar, _layout) ->
            match exitSuccessorSpine(argument)([]) with
                | (root, arguments) ->
                    match unspanArgument(root) with
                        | ExprVar(candidate) ->
                            if constructorNamedOfArity(candidate)(length(arguments))(state)
                            then 2
                            else -1
                        | _ -> -1
        | _ -> -1

// Stage 0's `IsNullaryConstructorPattern`.
let recursive exitNullaryConstructorPattern (pattern: Pattern) (state: CoreLoweringState) =
    match pattern with
        | PatternAt(_span, inner) -> exitNullaryConstructorPattern(inner)(state)
        | PatternConstructor(name, []) -> constructorNamedOfArity(name)(0)(state)
        | PatternVar(name) -> constructorNamedOfArity(name)(0)(state)
        | _ -> false

let recursive exitArgumentAt (index: Int) (arguments: List(Expr)) =
    match arguments with
        | [] -> None
        | argument :: rest ->
            if index == 0
            then Some(argument)
            else exitArgumentAt(index - 1)(rest)

// Stage 0's `ExitParameterSuccessorsQualify`: every tail self-call hands the parameter on, passes a
// nullary constructor, or builds a constructor in an arm where the parameter was matched nullary; the
// second component says whether some self-call builds one.
let recursive exitSuccessorsQualify (expression: Expr) (selfName: Str) (ordinal: Int) (name: Str) (arity: Int) (nullaryKnown: Bool) (state: CoreLoweringState) =
    match expression with
        | ExprAt(_span, inner) -> exitSuccessorsQualify(inner)(selfName)(ordinal)(name)(arity)(nullaryKnown)(state)
        | ExprNode(_node, inner) -> exitSuccessorsQualify(inner)(selfName)(ordinal)(name)(arity)(nullaryKnown)(state)
        | ExprIf(_condition, thenBranch, elseBranch) ->
            match exitSuccessorsQualify(thenBranch)(selfName)(ordinal)(name)(arity)(nullaryKnown)(state) with
                | (true, thenBuilds) ->
                    match exitSuccessorsQualify(elseBranch)(selfName)(ordinal)(name)(arity)(nullaryKnown)(state) with
                        | (qualifies, elseBuilds) -> (qualifies, thenBuilds || elseBuilds)
                | failed -> failed
        | ExprMatch(scrutinee, arms, _position) ->
            let onParameter =
                match unspanArgument(scrutinee) with
                    | ExprVar(candidate) -> candidate == name
                    | _ -> false
            in exitSuccessorArmsQualify(arms)(onParameter)(selfName)(ordinal)(name)(arity)(nullaryKnown)(state)
        | ExprLet(letName, _value, body, _parameters, _annotation, _requirements) ->
            if letName == name
            then (false, false)
            else exitSuccessorsQualify(body)(selfName)(ordinal)(name)(arity)(nullaryKnown)(state)
        | ExprCall(_function, _argument, _sugar, _layout) ->
            match exitSuccessorSpine(expression)([]) with
                | (root, arguments) ->
                    match unspanArgument(root) with
                        | ExprVar(callee) ->
                            if callee == selfName && length(arguments) == arity
                            then
                                match exitArgumentAt(ordinal)(arguments) with
                                    | Some(argument) ->
                                        match exitParameterSuccessorKind(argument)(name)(state) with
                                            | 2 -> (nullaryKnown, true)
                                            | kind -> (kind >= 0, false)
                                    | None -> (false, false)
                            else (true, false)
                        | _ -> (true, false)
        | _ -> (true, false)
and exitSuccessorArmsQualify (arms: List((Pattern, Expr, Maybe(Expr)))) (onParameter: Bool) (selfName: Str) (ordinal: Int) (name: Str) (arity: Int) (nullaryKnown: Bool) (state: CoreLoweringState) =
    match arms with
        | [] -> (true, false)
        | (pattern, body, _guard) :: rest ->
            if []
            |> shapePatternNames(pattern)
            |> containsLabel(name)
            then (false, false)
            else
                match exitSuccessorsQualify(body)(selfName)(ordinal)(name)(arity)(nullaryKnown || onParameter && exitNullaryConstructorPattern(pattern)(state))(state) with
                    | (true, armBuilds) ->
                        match exitSuccessorArmsQualify(rest)(onParameter)(selfName)(ordinal)(name)(arity)(nullaryKnown)(state) with
                            | (qualifies, restBuilds) -> (qualifies, armBuilds || restBuilds)
                    | failed -> failed

// The zero the flags start from, loaded once.
let exitFlagZero (zeroTemp: Int) (state: CoreLoweringState) =
    if zeroTemp < 0
    then
        match freshTemp(state) with
            | FreshTemp { state = zeroState, temp = zero } ->
                (emit(LoadConstInt(zero)(0))(zeroState), zero)
    else (state, zeroTemp)

// Stage 0's `PrepareExitParameterOwnFields`: a flag, zeroed ahead of the loop's entry, for every
// parameter whose self-calls have the checked shape and build a constructor successor
// (`GRC_NO_EXITPARAMOWNFIELDS` in stage 0); the entries are (slot, flag slot, ordinal).
let recursive allocateExitOwnFieldFlags (names: List(Str)) (slots: List(Int)) (ordinal: Int) (loop: CoreTcoLoop) (arity: Int) (zeroTemp: Int) (state: CoreLoweringState) =
    match (names, slots) with
        | (name :: restNames, slot :: restSlots) ->
            match exitSuccessorsQualify(loop.body)(loop.selfName)(ordinal)(name)(arity)(false)(state) with
                | (true, true) ->
                    match exitFlagZero(zeroTemp)(state) with
                        | (zeroed, zero) ->
                            match freshLocal(zeroed) with
                                | FreshLocal { state = flagState, local = flagSlot } ->
                                    match flagState
                                    |> emit(StoreLocal(flagSlot)(zero))
                                    |> allocateExitOwnFieldFlags(restNames)(restSlots)(ordinal + 1)(loop)(arity)(zero) with
                                        | (allocated, flags) -> (allocated, (slot, flagSlot, ordinal) :: flags)
                | _ -> allocateExitOwnFieldFlags(restNames)(restSlots)(ordinal + 1)(loop)(arity)(zeroTemp)(state)
        | _ -> (state, [])

let prepareExitOwnFieldFlags (slots: List(Int)) (loop: CoreTcoLoop) (state: CoreLoweringState) =
    if generalRcEnabled(state) && loop.selfName != ""
    then
        allocateExitOwnFieldFlags(loop.parameterNames)(slots)(0)(loop)(length(slots))(-1)(state)
    else (state, [])

// Stage 0's `LowerLambdaCoreEmitTcoLoopEntry`: the fixed loop-entry watermark saved once, the
// compaction-size slot and the affine reservation slots zeroed, the list parameters' active
// flags allocated, then the body label followed by the per-iteration watermark and stack pointer
// the back edge restores.
let emitTcoLoopEntryFlagged (label: Str) (slots: List(Int)) (loop: CoreTcoLoop) (entrySpliceCount: Int) (exitOwnFieldFlags: List((Int, Int, Int))) (state: CoreLoweringState) =
    match openArenaBracket(state) with
        | ArenaBracket { bracketState = fixedState, bracketCursorSlot = fixedCursorSlot, bracketEndSlot = fixedEndSlot } ->
            match freshLocal(fixedState) with
                | FreshLocal { state = compactionState, local = compactionSizeSlot } ->
                    match freshTemp(compactionState) with
                        | FreshTemp { state = zeroState, temp = zeroTemp } ->
                            match zeroState
                            |> emit(LoadConstInt(zeroTemp)(0))
                            |> emit(StoreLocal(compactionSizeSlot)(zeroTemp))
                            |> emitAffineReservations(loop.affineOrdinals)(slots)(0)(zeroTemp) with
                                | (reservedState, affineReservations) ->
                                    match allocateListActiveSlots(0)(loop)(loop.argumentShapes)(slots)(reservedState) with
                                        | (activeState, listActiveSlots) ->
                                            match allocateTmcChainSlots(loop.tmcShapePresent)(zeroTemp)(activeState) with
                                                | (tmcState, tmcDestSlot, tmcResultSlot) ->
                                                    tmcState
                                                    |> emit(Label(label + "_body"))
                                                    |> openArenaBracket
                                                    |> finishTcoLoopEntry(label)(slots)(exitOwnFieldFlags)(fixedCursorSlot)(fixedEndSlot)(compactionSizeSlot)(entrySpliceCount)(affineReservations)(listActiveSlots)(tmcDestSlot)(tmcResultSlot)

let emitTcoLoopEntry (label: Str) (slots: List(Int)) (loop: CoreTcoLoop) (entrySpliceCount: Int) (state: CoreLoweringState) =
    match prepareExitOwnFieldFlags(slots)(loop)(state) with
        | (flagged, exitOwnFieldFlags) -> emitTcoLoopEntryFlagged(label)(slots)(loop)(entrySpliceCount)(exitOwnFieldFlags)(flagged)

// A list-only self-call shape places a parameter provisionally unless its type is named: stage 0's
// provisional placement (`EvaluateTcoRcEligibility`) reads a self-call shape only for a list, and
// decides a named parameter by its type alone (`tcoAdtSlotAdmitted` here).
let listShapePlacesSlot (shape: TcoArgumentShape) (slot: Int) (state: CoreLoweringState) =
    if isTcoListShape(shape)
    then
        match slotResolvedType(slot)(state.bindings)(state) with
            | Some(SemNamed(_symbolId, _name, _arguments)) -> false
            | _ -> true
    else false

// A loop parameter the provisional loop entry already places on the reference-counted heap: a
// list-shaped or copy-ADT slot, or an affine `Str` accumulator.
let provisionallyRuntimeManagedLoopSlot (ordinal: Int) (slot: Int) (loop: CoreTcoLoop) (state: CoreLoweringState) =
    match loopShapeAtOrdinal(ordinal)(loop.argumentShapes) with
        | Some(shape) -> ordinaryRcPlacementAllowed(state) && (listShapePlacesSlot(shape)(slot)(state) || isAccumulatorRebuildListSlot(ordinal)(slot)(loop)(state) || shape == TcoFreshListShape && tcoListSlotElement(slot)(shape)(ordinal)(loop)(state) != None || tcoAdtSlotAdmitted(ordinal)(slot)(shape)(loop)(state) || containsInt(ordinal)(loop.runtimeManagedOrdinals))
        | None -> false

// A non-resource ADT whose whole cell has no shallow copy and that the contract does not govern:
// the only accumulators whose in-place rebuild pays, a shallow-copied cell being bounded by its
// copy-out already (stage 0's `LowerLambdaCoreScanDirectReuse` type test). Its copier always
// exists, since the caller found a constructor of it and the synthesized copier calls itself for a
// self field and copies a type parameter's word as it is; `arenaDeepCopySupported` answers whether
// an inline walk terminates instead, and refuses both.
let directReuseAccumulatorType (typeName: Str) (named: SemanticType) (state: CoreLoweringState) =
    match heapFactsOf(named)(state) with
        | HeapLayoutFacts { containsResource = containsResource, structuralCopy = structuralCopy } ->
            match structuralCopy with
                | ShallowCopy -> false
                | _ -> !isResourceTypeName(typeName) && !containsResource && !isGeneralRcValueType(named)(state)

let recursive moveCensusConstructorArities (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> []
        | (CoreConstructorLayout { name = name } as layout) :: rest -> (name, constructorArity(layout)) :: moveCensusConstructorArities(rest)

// Stage 0's `ReuseAccumulatorIsUnique`: the whole-program move analysis proves the loop
// function's accumulator parameter uniquely owned at every call, so the entry deep copy that
// would make it unique is redundant.
let reuseAccumulatorIsUnique (functionName: Str) (parameter: Str) (state: CoreLoweringState) =
    match moveSafetyProof(stateMoveFunctionTable(state))(stateMoveCallSites(state))(state
    |> stateConstructorLayouts
    |> moveCensusConstructorArities)(stateMoveNullaryConstructors(state))(stateReachSummaries(state))(functionName)(parameter) with
        | ParameterMoveSafetyProof { isMoveSafe = safe } -> safe

// The accumulator type's arena copier, synthesized ahead of the loop body as stage 0's
// `TrySynthesizeAdtCopier` in the scan does (the copier takes the next lambda id); the inline
// clone the synthesis emits alongside is discarded, only the copier, the cache, and the counters
// it advanced are kept.
let synthesizeAccumulatorCopier (named: SemanticType) (state: CoreLoweringState) =
    match state with
        | CoreLoweringState { nextLambdaId = lambdaId, nextLabelId = labelId, programState = CoreProgramState { dropperLabels = cache } } ->
            match synthesizeDeepCopy(0)(named)(dropperTypesOf(state))(cache)(0)(0)(lambdaId)(labelId)(IndependentClone) with
                | (InlineReleaseSynthesis { cache = copierCache, queryContext = queryContext, functions = synthesized, nextLambdaId = nextLambdaId, nextLabelId = nextLabelId }, _cloneTemp) ->
                    withStateDropperResult(copierCache)(queryContext)((state with functions = append(synthesized
                    |> map(locateSynthesizedFunction(state))
                    |> reverse)(state.functions), nextLambdaId = nextLambdaId, nextLabelId = nextLabelId))

// Stage 0's `LowerLambdaCoreScanDirectReuse`: every accumulator the loop body matches by a
// constructor pattern, not placed on the reference-counted heap at the provisional entry and of a
// direct-reuse type, becomes a linear reuse root: its copier is synthesized now, its name joins
// `linearReuseNames` so the matches on it hand their dead cells out as arena tokens, and its
// entry-copy candidate records whether the move analysis proves it unique.
let recursive scanDirectReuseAccumulators (loop: CoreTcoLoop) (slots: List(Int)) (accumulators: List((Str, Str))) (state: CoreLoweringState) =
    match accumulators with
        | [] -> state
        | (accumulator, constructorName) :: rest ->
            match (parameterOrdinalOf(accumulator)(loop.parameterNames)(0), constructorLayout(constructorName)(state)) with
                | (Some(ordinal), Some(layout)) ->
                    match (loopSlotAtOrdinal(ordinal)(slots), layoutFieldTypes(layout)(state)) with
                        | (Some(slot), (_fieldTypes, SemNamed(_symbolId, typeName, _arguments) as named)) ->
                            if !provisionallyRuntimeManagedLoopSlot(ordinal)(slot)(loop)(state) && directReuseAccumulatorType(typeName)(named)(state)
                            then
                                state
                                |> synthesizeAccumulatorCopier(named)
                                |> (given (synthesized: CoreLoweringState) ->
                                    synthesized
                                    |> withStateLinearReuseNames(accumulator :: stateLinearReuseNames(synthesized))
                                    |> withStateDirectReuseCandidates(append(stateDirectReuseCandidates(synthesized))([(slot, named, accumulator, reuseAccumulatorIsUnique(loop.selfName)(accumulator)(synthesized), false)])))
                                |> scanDirectReuseAccumulators(loop)(slots)(rest)
                            else scanDirectReuseAccumulators(loop)(slots)(rest)(state)
                        | _ -> scanDirectReuseAccumulators(loop)(slots)(rest)(state)
                | _ -> scanDirectReuseAccumulators(loop)(slots)(rest)(state)

// The type of the `ordinal`-th curried argument (zero-based) of a function type, stage 0's
// `NthCurriedArgType`.
let recursive nthCurriedArgumentType (functionType: SemanticType) (ordinal: Int) (state: CoreLoweringState) =
    match resolveType(state)(functionType) with
        | SemFunction(parameterType, resultType, _effects) ->
            if ordinal == 0
            then
                parameterType
                |> resolveType(state)
                |> Some
            else nthCurriedArgumentType(resultType)(ordinal - 1)(state)
        | _ -> None

// The accumulator type a specialization candidate is called at, read through the current
// substitution: the argument a saturated call passes last (the parameter of a single-parameter
// candidate, the worker's parameter of a nested-worker one), stage 0's
// `NthCurriedArgType(type, ArgCount - 1)`. Whether its layout admits the entry deep copy that makes
// it unique is `reusableSpecializationAccumulatorType`.
let specializationAccumulatorTypeOf (callee: Str) (state: CoreLoweringState) =
    match (lookupBinding(callee)(state.bindings), state
    |> stateSpecializationCandidateIndex
    |> specializationCandidateArity(callee)) with
        | (Some(CoreBinding { scheme = TypeScheme { body = body } }), Some(arity)) -> nthCurriedArgumentType(body)(arity - 1)(state)
        | _ -> None

// Whether the rebuild's constructor-site materialization can relocate a field of this type into
// to-space through a synthesized copier — the half of the persistence question that needs the type
// environment, so `ReuseResetSafety.ash` takes it as a parameter.
let specializationFieldRelocatable (fieldType: SemanticType) (state: CoreLoweringState) =
    state
    |> coverageEnvironment
    |> toSpaceCopySafeType(resolveType(state)(fieldType))

// Stage 0's `AccumulatorLayoutIsPersistable`: a list accumulator's cells are rebuilt in the
// ordinary heap and carry whatever their elements already were, so its layout constrains nothing;
// a named ADT accumulator's fresh cells would be allocated into the never-reset to-space, so it is
// admitted only when every field is the accumulator itself or a leaf the materialization relocates.
// A generic type's fields are read at the accumulator's instantiation (`MapTree(Str, List(Str))`'s
// value field is a list of strings), as stage 0 maps the type parameters to the type arguments.
let accumulatorLayoutIsPersistable (accumulatorType: SemanticType) (state: CoreLoweringState) =
    match accumulatorType with
        | SemNamed(_symbolId, typeName, arguments) ->
            match namedTypeDeclaredFields(typeName)(state) with
                | (false, _fieldTypes) -> false
                | (true, declaredFieldTypes) ->
                    let fieldTypes =
                        match arguments with
                            | [] -> declaredFieldTypes
                            | _ -> namedTypeFieldTypesAs(true)(typeName)(arguments)(state)
                    in
                        namedAccumulatorFieldsPersistent(typeName)(given (fieldType: SemanticType) -> specializationFieldRelocatable(fieldType)(state))(fieldTypes)
        | _ -> true

// Stage 0's `IsReusableSpecializationAccumulatorType`, whose real question is whether the loop
// entry can build the deep copy that makes the accumulator unique (`TrySynthesizeAdtCopier is not
// null`). A list qualifies when its element copies out; a named ADT when it is not a resource and
// every constructor field is the accumulator itself or a copy type — a self field the synthesized
// copier calls itself for, a copy field it copies inline, so the copier always exists. That is
// deliberately not `arenaDeepCopySupported`, whose cycle guard reports every recursive ADT as
// not deep-copyable: that flag answers whether an INLINE walk terminates, not whether a copier
// function can be synthesized, and `StructuralCopiers.ash`'s `emitCopierField` synthesizes a
// recursive one by construction.
let reusableSpecializationAccumulatorType (accumulatorType: SemanticType) (state: CoreLoweringState) =
    match accumulatorType with
        | SemList(element) ->
            match heapFactsOf(element)(state) with
                | HeapLayoutFacts { containsResource = containsResource, arenaDeepCopySupported = deepCopySupported } -> !containsResource && deepCopySupported
        | SemNamed(_symbolId, typeName, _arguments) -> !isResourceTypeName(typeName) && accumulatorLayoutIsPersistable(accumulatorType)(state)
        | _ -> false

// Stage 0's `HasMultipleSelfTypedFields`: some constructor of the named type holds two or more
// children of the type itself, so a rebuild can descend into one and share the others.
let recursive countSelfTypedFields (typeName: Str) (fields: List(SemanticType)) (state: CoreLoweringState) (count: Int) =
    match fields with
        | [] -> count
        | field :: rest ->
            match resolveType(state)(field) with
                | SemNamed(_id, fieldTypeName, _arguments) ->
                    if fieldTypeName == typeName
                    then countSelfTypedFields(typeName)(rest)(state)(count + 1)
                    else countSelfTypedFields(typeName)(rest)(state)(count)
                | _ -> countSelfTypedFields(typeName)(rest)(state)(count)

let recursive typeHasMultipleSelfFields (typeName: Str) (layouts: List(CoreConstructorLayout)) (state: CoreLoweringState) =
    match layouts with
        | [] -> false
        | layout :: rest ->
            match constructorResultName(layout) with
                | Some(resultName) ->
                    if resultName == typeName
                    then
                        match layoutFieldTypes(layout)(state) with
                            | (fields, _resultType) ->
                                if countSelfTypedFields(typeName)(fields)(state)(0) >= 2
                                then true
                                else typeHasMultipleSelfFields(typeName)(rest)(state)
                    else typeHasMultipleSelfFields(typeName)(rest)(state)
                | None -> typeHasMultipleSelfFields(typeName)(rest)(state)

// The recursive body a specializable function's clone walks, with the name it recurses under:
// the nested `go` of the Map.set shape behind its outer lambdas and leading lets, or the
// function's own innermost body.
let recursive specializationRecursiveBody (callee: Str) (value: Expr) =
    match value with
        | ExprAt(_span, inner) -> specializationRecursiveBody(callee)(inner)
        | ExprNode(_node, inner) -> specializationRecursiveBody(callee)(inner)
        | ExprLambda(_parameter, body, _annotation) -> specializationRecursiveBody(callee)(body)
        | ExprLet(_bound, _value, body, _params, _annotation, _traits) -> specializationRecursiveBody(callee)(body)
        | ExprLetRecursive(bound, recursiveValue, body, _params, _typeAnnotation, _traits) ->
            match (withoutNodeIdentity(recursiveValue), unspanArgument(body)) with
                | (ExprLambda(_parameter, recursiveBody, _annotation), ExprVar(reference)) ->
                    if reference == bound
                    then (bound, recursiveBody)
                    else (callee, value)
                | _ -> (callee, value)
        | _ -> (callee, value)

// Stage 0's `SpecializationRebuildsOnlyAPath`: the clone descends into at most one child of the
// accumulator on any path, over a type whose constructors hold two or more children of their own
// type, so its in-place pass rewrites one root-to-leaf path per call rather than the structure.
let specializationRebuildsOnlyAPath (callee: Str) (accumulatorType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(accumulatorType) with
        | SemNamed(_id, typeName, _arguments) ->
            if typeHasMultipleSelfFields(typeName)(stateConstructorsOfType(typeName)(state))(state)
            then
                match state
                |> stateSpecializationCandidateIndex
                |> specializationCandidateValueOf(callee) with
                    | Some(value) ->
                        match specializationRecursiveBody(callee)(value) with
                            | (selfName, body) -> maxPathOccurrences(selfName)(body) == 1
                    | None -> false
            else false
        | _ -> false

// Stage 0's `LowerLambdaCoreScanSpecializationReuse`: a loop parameter handed straight to a
// single-parameter specialization candidate is deep-copied once at loop entry so the
// specialization may rewrite it in place, and its name joins
// `linearSpecializationAccumulators` so the call site can prove it unique. The gate reads the
// CALLEE's accumulator parameter type, the copy the loop parameter's own.
// The type the scan's gates read: the callee's accumulator type, or, while that still holds a
// type variable (a generic library function such as `Map.upsertStr`), the loop parameter's own
// type, which the call unifies it with. Stage 0 reads the callee's type after the call's
// instantiation, which is the loop parameter's.
let scannedAccumulatorType (calleeAccumulatorType: SemanticType) (parameterType: SemanticType) (state: CoreLoweringState) =
    if containsUnresolvedLayout(calleeAccumulatorType)(state)
    then resolveType(state)(parameterType)
    else calleeAccumulatorType

let recursive scanSpecializationAccumulators (loop: CoreTcoLoop) (slots: List(Int)) (accumulators: List((Str, Str))) (state: CoreLoweringState) =
    match accumulators with
        | [] -> state
        | (accumulator, callee) :: rest ->
            match (parameterOrdinalOf(accumulator)(loop.parameterNames)(0), specializationAccumulatorTypeOf(callee)(state)) with
                | (Some(ordinal), Some(calleeAccumulatorType)) ->
                    match (loopSlotAtOrdinal(ordinal)(slots), lookupBinding(accumulator)(state.bindings)) with
                        | (Some(slot), Some(CoreBinding { scheme = TypeScheme { body = parameterType } })) ->
                            if containsName(accumulator)(stateLinearReuseNames(state)) == false && !provisionallyRuntimeManagedLoopSlot(ordinal)(slot)(loop)(state) && reusableSpecializationAccumulatorType(scannedAccumulatorType(calleeAccumulatorType)(parameterType)(state))(state)
                            then
                                let unique = reuseAccumulatorIsUnique(loop.selfName)(accumulator)(state)
                                in
                                    if !unique && specializationRebuildsOnlyAPath(callee)(scannedAccumulatorType(calleeAccumulatorType)(parameterType)(state))(state)
                                    then scanSpecializationAccumulators(loop)(slots)(rest)(state)
                                    else
                                        state
                                        |> (given (scanned: CoreLoweringState) ->
                                            scanned
                                            |> withStateLinearSpecializationAccumulators(accumulator :: stateLinearSpecializationAccumulators(scanned))
                                            |> withStateDirectReuseCandidates(append(stateDirectReuseCandidates(scanned))([(slot, resolveType(scanned)(parameterType), accumulator, unique, true)])))
                                        |> scanSpecializationAccumulators(loop)(slots)(rest)
                            else scanSpecializationAccumulators(loop)(slots)(rest)(state)
                        | _ -> scanSpecializationAccumulators(loop)(slots)(rest)(state)
                | _ -> scanSpecializationAccumulators(loop)(slots)(rest)(state)

// The loop body of a loop function (stage 0's `LowerLambdaCoreEnterTcoLoop`): once the innermost
// chain lambda is entered, its parameters get their back-edge slots, the direct-reuse
// accumulators are scanned, and the loop entry is emitted before the body. The instruction count
// is captured right before any loop-entry bracket is opened, so the runtime-managed parameter
// entry normalization the whole body's resolved types decide (`finalizeTcoRuntimeManagedParams`)
// can be spliced back in ahead of it.
// The loop's call policy is due once the specialization scan has run: the body's lowering
// computes it (stage 0's `RecomputeTcoParameterFlow` after `LowerLambdaCoreScanSpecializationReuse`).
let markLoopCallFlowPending (state: CoreLoweringState) =
    match stateTcoLoop(state) with
        | Some(loop) -> withStateTcoLoop(Some((loop with callFlowPending = true)))(state)
        | None -> state

let enterTcoLoopBody (label: Str) (parameter: Str) (state: CoreLoweringState) =
    match stateTcoLoop(state) with
        | Some(CoreTcoLoop { pendingCurried = 0, emitsLoop = true, parameterNames = names } as loop) ->
            match state
            |> bindChainParameterSlots(names)(parameter)(state.bindings)
            |> buildLoopParameterSlots(names)([]) with
                | (slotted, slots) ->
                    slotted
                    |> scanDirectReuseAccumulators(loop)(slots)(loop.matchedAccumulators)
                    |> scanSpecializationAccumulators(loop)(slots)(loop.specializationAccumulators)
                    |> emitTcoLoopEntry(label)(slots)(loop)(length(slotted.reversedInstructions))
                    |> markLoopCallFlowPending
        | _ -> state

// A lambda entered inside a loop function: the next curried parameter's lambda stays in the loop
// (one fewer to enter); any other lambda is a nested closure whose body leaves it.
let enterLambdaTcoLoop (parameterType: SemanticType) (state: CoreLoweringState) =
    match stateTcoLoop(state) with
        | Some(CoreTcoLoop { pendingCurried = pending } as context) ->
            if pending > 0
            then withStateTcoLoop(Some((context with pendingCurried = pending - 1, parameterTypes = append(context.parameterTypes)([parameterType]))))(state)
            else withStateTcoLoop(None)(state)
        | None -> state

// The loop body — the innermost lambda body of the loop function — starts in tail position.
let loopBodyTailPosition (state: CoreLoweringState) =
    match stateTcoLoop(state) with
        | Some(CoreTcoLoop { pendingCurried = pending }) -> pending == 0
        | None -> false

let withLoopBodyRequest (request: ConsumerRequest) (state: CoreLoweringState) = withConsumerRequest((request with tailPosition = loopBodyTailPosition(state), consTailPosition = loopBodyTailPosition(state)))(state)

// Stage 0's `RequestsArenaResult`: a call whose result has no static layout here (a generic body
// applying a closure parameter, or a result type inference has not resolved yet) cannot own a
// reference-counted result, so it asks the callee for an arena result.
let requestsArenaResult (resultType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(resultType) with
        | SemFunction(_argument, _result, _row) -> false
        | _ -> containsUnresolvedLayout(resultType)(state)

let recursive parameterArgumentIndex (parameter: Str) (arguments: List(Expr)) (index: Int) =
    match arguments with
        | [] -> None
        | argument :: rest ->
            match unspanArgument(argument) with
                | ExprVar(name) ->
                    if name == parameter
                    then Some(index)
                    else parameterArgumentIndex(parameter)(rest)(index + 1)
                | _ -> parameterArgumentIndex(parameter)(rest)(index + 1)

// The declared type of the constructor field the bare parameter is passed to in `expression`'s
// own constructor application, when that field holds a string or a named type.
let seededConstructorFieldType (parameter: Str) (expression: Expr) (state: CoreLoweringState) =
    match constructorApplicationOf(expression)([])(state) with
        | Some((layout, arguments)) ->
            match parameterArgumentIndex(parameter)(arguments)(0) with
                | Some(index) ->
                    match instantiateConstructor(layout)(state) with
                        | CoreConstructorShape { state = shaped, parameterTypes = fieldTypes } ->
                            match typeAtIndex(index)(fieldTypes) with
                                | Some(fieldType) ->
                                    match resolveType(shaped)(fieldType) with
                                        | SemString ->
                                            Some((fieldType, withStateRuntimeAdtRequested(stateRuntimeAdtRequested(state))(shaped)))
                                        | SemNamed(_symbolId, _name, _arguments) ->
                                            Some((fieldType, withStateRuntimeAdtRequested(stateRuntimeAdtRequested(state))(shaped)))
                                        | _ -> None
                                | None -> None
                | None -> None
        | None -> None

// Stage 0's `LowerLambdaCoreSeedParamTypeFromConstructorFields`: an un-annotated parameter whose
// type is still a variable takes the declared type of the first constructor field it is passed to
// directly anywhere in the body (the bare parameter as a record literal field or a positional
// constructor argument, outside any binder that shadows it), when that field holds a string or a
// named type: the unification the body performs later, made before the body so the entry
// normalization decided ahead of the body (`withNormalizedAlwaysReturnedParameter`) sees the
// resolved type and the aggregate storing the parameter is placed accordingly.
let recursive seedParameterFromConstructorFields (parameter: Str) (parameterType: SemanticType) (expression: Expr) (state: CoreLoweringState) =
    match resolveType(state)(parameterType) with
        | SemVariable(_id) ->
            match seededConstructorFieldType(parameter)(expression)(state) with
                | Some((fieldType, shaped)) ->
                    match bindType(parameterType)(fieldType)(shaped) with
                        | (bound, _error) -> bound
                | None -> seedParameterFromChildren(parameter)(parameterType)(expression)(state)
        | _ -> state
and seedParameterFromChildren (parameter: Str) (parameterType: SemanticType) (expression: Expr) (state: CoreLoweringState) =
    match expression with
        | ExprAt(_span, inner) -> seedParameterFromConstructorFields(parameter)(parameterType)(inner)(state)
        | ExprNode(_node, inner) -> seedParameterFromConstructorFields(parameter)(parameterType)(inner)(state)
        | ExprCall(function, argument, _isSugar, _layout) -> seedParameterFromEach(parameter)(parameterType)([function, argument])(state)
        | ExprLet(name, value, body, _params, _annotation, _requirements) -> seedParameterFromBinder(parameter)(parameterType)(name)(value)(body)(state)
        | ExprLetResult(name, value, body) -> seedParameterFromBinder(parameter)(parameterType)(name)(value)(body)(state)
        | ExprLetRecursive(name, value, body, _params, _annotation, _requirements) ->
            if name == parameter
            then state
            else seedParameterFromEach(parameter)(parameterType)([value, body])(state)
        | ExprLambda(name, body, _annotation) ->
            if name == parameter
            then state
            else seedParameterFromConstructorFields(parameter)(parameterType)(body)(state)
        | ExprIf(condition, thenBranch, elseBranch) -> seedParameterFromEach(parameter)(parameterType)([condition, thenBranch, elseBranch])(state)
        | ExprMatch(value, cases, _position) ->
            state
            |> seedParameterFromConstructorFields(parameter)(parameterType)(value)
            |> seedParameterFromCases(parameter)(parameterType)(cases)
        | ExprTuple(elements) -> seedParameterFromEach(parameter)(parameterType)(elements)(state)
        | ExprList(elements, _isMultiline) -> seedParameterFromEach(parameter)(parameterType)(elements)(state)
        | ExprRecord(_name, fields, _isMultiline) -> seedParameterFromFields(parameter)(parameterType)(fields)(state)
        | ExprRecordUpdate(target, fields) ->
            state
            |> seedParameterFromConstructorFields(parameter)(parameterType)(target)
            |> seedParameterFromFields(parameter)(parameterType)(fields)
        | ExprCons(head, tail) -> seedParameterFromEach(parameter)(parameterType)([head, tail])(state)
        | ExprAdd(left, right) -> seedParameterFromEach(parameter)(parameterType)([left, right])(state)
        | ExprSubtract(left, right) -> seedParameterFromEach(parameter)(parameterType)([left, right])(state)
        | ExprMultiply(left, right) -> seedParameterFromEach(parameter)(parameterType)([left, right])(state)
        | ExprDivide(left, right) -> seedParameterFromEach(parameter)(parameterType)([left, right])(state)
        | ExprModulo(left, right) -> seedParameterFromEach(parameter)(parameterType)([left, right])(state)
        | ExprBitwiseAnd(left, right) -> seedParameterFromEach(parameter)(parameterType)([left, right])(state)
        | ExprBitwiseOr(left, right) -> seedParameterFromEach(parameter)(parameterType)([left, right])(state)
        | ExprBitwiseXor(left, right) -> seedParameterFromEach(parameter)(parameterType)([left, right])(state)
        | ExprShiftLeft(left, right) -> seedParameterFromEach(parameter)(parameterType)([left, right])(state)
        | ExprShiftRight(left, right) -> seedParameterFromEach(parameter)(parameterType)([left, right])(state)
        | ExprLogicalAnd(left, right) -> seedParameterFromEach(parameter)(parameterType)([left, right])(state)
        | ExprLogicalOr(left, right) -> seedParameterFromEach(parameter)(parameterType)([left, right])(state)
        | ExprGreaterThan(left, right) -> seedParameterFromEach(parameter)(parameterType)([left, right])(state)
        | ExprLessThan(left, right) -> seedParameterFromEach(parameter)(parameterType)([left, right])(state)
        | ExprGreaterOrEqual(left, right) -> seedParameterFromEach(parameter)(parameterType)([left, right])(state)
        | ExprLessOrEqual(left, right) -> seedParameterFromEach(parameter)(parameterType)([left, right])(state)
        | ExprEqual(left, right) -> seedParameterFromEach(parameter)(parameterType)([left, right])(state)
        | ExprNotEqual(left, right) -> seedParameterFromEach(parameter)(parameterType)([left, right])(state)
        | ExprResultPipe(left, right) -> seedParameterFromEach(parameter)(parameterType)([left, right])(state)
        | ExprResultMapErrorPipe(left, right) -> seedParameterFromEach(parameter)(parameterType)([left, right])(state)
        | ExprBitwiseNot(operand) -> seedParameterFromConstructorFields(parameter)(parameterType)(operand)(state)
        | ExprLogicalNot(operand) -> seedParameterFromConstructorFields(parameter)(parameterType)(operand)(state)
        | ExprAwait(operand) -> seedParameterFromConstructorFields(parameter)(parameterType)(operand)(state)
        | ExprPerform(operand) -> seedParameterFromConstructorFields(parameter)(parameterType)(operand)(state)
        | _ -> state
and seedParameterFromEach (parameter: Str) (parameterType: SemanticType) (expressions: List(Expr)) (state: CoreLoweringState) =
    match expressions with
        | [] -> state
        | expression :: rest ->
            state
            |> seedParameterFromConstructorFields(parameter)(parameterType)(expression)
            |> seedParameterFromEach(parameter)(parameterType)(rest)
and seedParameterFromBinder (parameter: Str) (parameterType: SemanticType) (name: Str) (value: Expr) (body: Expr) (state: CoreLoweringState) =
    if name == parameter
    then seedParameterFromConstructorFields(parameter)(parameterType)(value)(state)
    else seedParameterFromEach(parameter)(parameterType)([value, body])(state)
and seedParameterFromCases (parameter: Str) (parameterType: SemanticType) (cases: List((Pattern, Expr, Maybe(Expr)))) (state: CoreLoweringState) =
    match cases with
        | [] -> state
        | (pattern, body, guard) :: rest ->
            if patternBindsName(parameter)(pattern)
            then seedParameterFromCases(parameter)(parameterType)(rest)(state)
            else
                state
                |> seedParameterFromGuard(parameter)(parameterType)(guard)
                |> seedParameterFromConstructorFields(parameter)(parameterType)(body)
                |> seedParameterFromCases(parameter)(parameterType)(rest)
and seedParameterFromGuard (parameter: Str) (parameterType: SemanticType) (guard: Maybe(Expr)) (state: CoreLoweringState) =
    match guard with
        | Some(expression) -> seedParameterFromConstructorFields(parameter)(parameterType)(expression)(state)
        | None -> state
and seedParameterFromFields (parameter: Str) (parameterType: SemanticType) (fields: List((Str, Expr))) (state: CoreLoweringState) =
    match fields with
        | [] -> state
        | (_field, expression) :: rest ->
            state
            |> seedParameterFromConstructorFields(parameter)(parameterType)(expression)
            |> seedParameterFromFields(parameter)(parameterType)(rest)

// A function body starts in tail position under the request its shape asks for.
let withFunctionBodyRequest (body: Expr) (prepared: CoreLoweringState) =
    (let request = functionBodyRequest(body)(prepared)
    in withLoopBodyRequest((request with tailCall = true))(prepared))

// A recursive member's body meets the member's result type while it is lowered, not only after:
// a nested lambda then pins the self type's next arrow and its parameter annotation before a
// self-call in its body is lowered, as stage 0's lambda-chain lowering does.
let withRecursiveBodyRequest (body: Expr) (resultType: SemanticType) (prepared: CoreLoweringState) =
    (let request = functionBodyRequest(body)(prepared)
    in withLoopBodyRequest((request with tailCall = true, expectedType = Some(resultType)))(prepared))

let recursive anyCallResultResolved (resultTypes: List(SemanticType)) (state: CoreLoweringState) =
    match resultTypes with
        | [] -> false
        | resultType :: rest -> requestsArenaResult(resultType)(state) == false || anyCallResultResolved(rest)(state)

// Lowers a function body from its prepared entry state. Stage 0 lowers against the whole
// program's finished inference, so a call whose result type a later expression of the same body
// resolves (a self call under an operator, a generic callee whose result the body's own use
// pins) already sees the resolved type. This lowering resolves as it goes: when a body's call
// asked for an arena result because its result type was still a variable, and the finished body
// resolved it, the body is lowered a second time from the same entry state carrying only the
// finished substitution and variable supply, so every counter, label, and lifted function is
// numbered as in a single pass over resolved types. A type the body never resolves (a generic
// function's own parameter) keeps the single pass.
// Whether a body applies an operator stage 0 maps to a trait requirement
// (`GetMappedOperatorTraitName`: the arithmetic, bitwise, equality, and ordering operators and
// the two negations).
let recursive exprAppliesMappedOperator (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> exprAppliesMappedOperator(inner)
        | ExprNode(_node, inner) -> exprAppliesMappedOperator(inner)
        | ExprAdd(_left, _right) -> true
        | ExprSubtract(_left, _right) -> true
        | ExprMultiply(_left, _right) -> true
        | ExprDivide(_left, _right) -> true
        | ExprModulo(_left, _right) -> true
        | ExprBitwiseAnd(_left, _right) -> true
        | ExprBitwiseOr(_left, _right) -> true
        | ExprBitwiseXor(_left, _right) -> true
        | ExprShiftLeft(_left, _right) -> true
        | ExprShiftRight(_left, _right) -> true
        | ExprBitwiseNot(_operand) -> true
        | ExprLogicalNot(_operand) -> true
        | ExprGreaterThan(_left, _right) -> true
        | ExprLessThan(_left, _right) -> true
        | ExprGreaterOrEqual(_left, _right) -> true
        | ExprLessOrEqual(_left, _right) -> true
        | ExprEqual(_left, _right) -> true
        | ExprNotEqual(_left, _right) -> true
        | ExprLogicalAnd(left, right) -> eitherAppliesMappedOperator(left)(right)
        | ExprLogicalOr(left, right) -> eitherAppliesMappedOperator(left)(right)
        | ExprResultPipe(left, right) -> eitherAppliesMappedOperator(left)(right)
        | ExprResultMapErrorPipe(left, right) -> eitherAppliesMappedOperator(left)(right)
        | ExprCons(left, right) -> eitherAppliesMappedOperator(left)(right)
        | ExprLet(_name, value, body, _params, _annotation, _requirements) -> eitherAppliesMappedOperator(value)(body)
        | ExprLetResult(_name, value, body) -> eitherAppliesMappedOperator(value)(body)
        | ExprLetRecursive(_name, value, body, _params, _annotation, _requirements) -> eitherAppliesMappedOperator(value)(body)
        | ExprIf(condition, thenBranch, elseBranch) -> exprAppliesMappedOperator(condition) || eitherAppliesMappedOperator(thenBranch)(elseBranch)
        | ExprLambda(_parameter, body, _annotation) -> exprAppliesMappedOperator(body)
        | ExprCall(function, argument, _isSugar, _layout) -> eitherAppliesMappedOperator(function)(argument)
        | ExprTuple(elements) -> anyAppliesMappedOperator(elements)
        | ExprList(elements, _isMultiline) -> anyAppliesMappedOperator(elements)
        | ExprMatch(value, cases, _position) -> exprAppliesMappedOperator(value) || matchCasesApplyMappedOperator(cases)
        | ExprAwait(operand) -> exprAppliesMappedOperator(operand)
        | ExprRecord(_ctorName, fields, _isMultiline) -> fieldsApplyMappedOperator(fields)
        | ExprRecordUpdate(target, fields) -> exprAppliesMappedOperator(target) || fieldsApplyMappedOperator(fields)
        | ExprPerform(operand) -> exprAppliesMappedOperator(operand)
        | ExprHandle(operand, arms) -> exprAppliesMappedOperator(operand) || handleArmsApplyMappedOperator(arms)
        | _ -> false
and eitherAppliesMappedOperator (left: Expr) (right: Expr) = exprAppliesMappedOperator(left) || exprAppliesMappedOperator(right)
and anyAppliesMappedOperator (expressions: List(Expr)) =
    match expressions with
        | [] -> false
        | head :: rest -> exprAppliesMappedOperator(head) || anyAppliesMappedOperator(rest)
and fieldsApplyMappedOperator (fields: List((Str, Expr))) =
    match fields with
        | [] -> false
        | (_fieldName, expression) :: rest -> exprAppliesMappedOperator(expression) || fieldsApplyMappedOperator(rest)
and matchCasesApplyMappedOperator (cases: List((Pattern, Expr, Maybe(Expr)))) =
    match cases with
        | [] -> false
        | (_pattern, body, guard) :: rest -> exprAppliesMappedOperator(body) || guardAppliesMappedOperator(guard) || matchCasesApplyMappedOperator(rest)
and guardAppliesMappedOperator (guard: Maybe(Expr)) =
    match guard with
        | Some(expression) -> exprAppliesMappedOperator(expression)
        | None -> false
and handleArmsApplyMappedOperator (arms: List((Maybe(Str), Str, List(Pattern), Expr))) =
    match arms with
        | [] -> false
        | (_binder, _operation, _patterns, body) :: rest -> exprAppliesMappedOperator(body) || handleArmsApplyMappedOperator(rest)

// A scope binding a body reads through a slot, its environment, or its own closure, with no
// quantified type of its own: a parameter, a capture, a `let` of the enclosing body, or the
// recursive binding whose body this is (stage 0's closed inferred type covers the binding's own
// arrow, its result included).
let isMonomorphicScopeBinding (binding: CoreBinding) =
    match binding with
        | CoreBinding { location = CoreLocal(_slot), scheme = TypeScheme { quantified = [] } } -> true
        | CoreBinding { location = CoreEnvironment(_index), scheme = TypeScheme { quantified = [] } } -> true
        | CoreBinding { location = CoreSelf(_label, _environmentSize), scheme = TypeScheme { quantified = [] } } -> true
        | _ -> false

// The type a scope binding is judged closed by: the recursive binding whose body this is by its
// arrow's innermost result (its parameters are the scope's own bindings, judged on their own),
// any other binding by its whole type.
let recursive judgedScopeType (binding: CoreBinding) (semanticType: SemanticType) (state: CoreLoweringState) =
    match binding.location with
        | CoreSelf(_label, _environmentSize) ->
            match resolveType(state)(semanticType) with
                | SemFunction(_argument, result, _row) -> judgedScopeType(binding)(result)(state)
                | other -> other
        | _ -> semanticType

// Whether every monomorphic scope binding's type is closed once the body is lowered, and at
// least one of them held a variable when the body was entered.
let recursive scopeTypesClosedByBody (bindings: List(CoreBinding)) (entered: CoreLoweringState) (finished: CoreLoweringState) (anyResolved: Bool) =
    match bindings with
        | [] -> anyResolved
        | (CoreBinding { scheme = TypeScheme { body = bindingType } } as binding) :: rest ->
            if isMonomorphicScopeBinding(binding)
            then
                if containsUnresolvedLayout(judgedScopeType(binding)(bindingType)(finished))(finished)
                then false
                else
                    scopeTypesClosedByBody(rest)(entered)(finished)(anyResolved || containsUnresolvedLayout(judgedScopeType(binding)(bindingType)(entered))(entered))
            else scopeTypesClosedByBody(rest)(entered)(finished)(anyResolved)

// Stage 0 lowers a binding that encountered a trait requirement against the type its discovery
// pass inferred for it (`ElaborateInferredTraitBindings` rewrites the binding with the closed
// inferred type as its annotation), so such a body sees its scope's resolved types from the
// start. A body applying a mapped operator whose first lowering closed a scope type it entered
// with as a variable is lowered again for the same reason.
let bodyEncounteredRequirementClosedScope (body: Expr) (entered: CoreLoweringState) (finished: CoreLoweringState) = exprAppliesMappedOperator(body) && scopeTypesClosedByBody(entered.bindings)(entered)(finished)(false)

// The closed inferred type stage 0 elaborates a recursive binding with covers the binding's own
// result, so the scope the body is lowered again against includes the result the body settled
// on: `close` binds it into a copy of the first lowering's state before the scope is judged
// closed, and the second lowering starts from that substitution. A body lowered again only
// because a call result resolved keeps the first substitution, where the binding's result is
// still the lowering-local variable its self calls defer to.
// The state a body is lowered again from: the scope it entered with, the substitution an earlier
// lowering settled, and the calls to a later group member that lowering recorded. Stage 0 sees such a
// call's result open at the call even when the body's own later use settles it, so the site keeps
// its record for the group's pinned lowering.
let restartedAgainst (entered: CoreLoweringState) (lowered: CoreLoweringState) =
    (entered with typeSupply = lowered.typeSupply)
    |> withSubstitution(lowered.substitution)
    |> withStatePendingSiblingCallResults(statePendingSiblingCallResults(lowered))

let recursive scopeParameterNamed (name: Str) (scope: List((Str, SemanticType))) =
    match scope with
        | [] -> None
        | (parameter, recorded) :: rest ->
            if parameter == name
            then Some(recorded)
            else scopeParameterNamed(name)(rest)

// The type the table records for a scope binding at the entered lambda: its own parameter by the
// binding its node notes, an earlier curried parameter (a capture) by the binding the enclosing
// lambda's node noted, and a recursive member's self binding by the whole arrow its own lambda's
// node records. None for an enclosing local and for a type the table leaves open.
let recordedScopeBindingTypeBy (settled: Bool) (binding: CoreBinding) (state: CoreLoweringState) =
    match binding.location with
        | CoreSelf(_label, _environmentSize) ->
            state
            |> stateRecordedParameterScope
            |> scopeParameterNamed(recordedSelfScopeKey(settled)(binding.name))
        | CoreLocal(_slot) -> recordedParameterTypeAtEntryBy(settled)(binding.name)(state)
        | CoreEnvironment(_index) ->
            if settled
            then None
            else
                state
                |> stateRecordedParameterScope
                |> scopeParameterNamed(binding.name)

let recordedScopeBindingType (binding: CoreBinding) (state: CoreLoweringState) = recordedScopeBindingTypeBy(false)(binding)(state)

// The scope bindings a requirement-closed lowering again is for: open at entry, closed by the body,
// each with whether the table records its type at entry.
let recursive scopeBindingsClosedByBody (bindings: List(CoreBinding)) (entered: CoreLoweringState) (finished: CoreLoweringState) =
    match bindings with
        | [] -> []
        | (CoreBinding { name = name, scheme = TypeScheme { body = bindingType } } as binding) :: rest ->
            if isMonomorphicScopeBinding(binding) && containsUnresolvedLayout(judgedScopeType(binding)(bindingType)(entered))(entered) && !containsUnresolvedLayout(judgedScopeType(binding)(bindingType)(finished))(finished)
            then
                let kind =
                    match binding.location with
                        | CoreLocal(_slot) -> "local"
                        | CoreEnvironment(_index) -> "capture"
                        | CoreSelf(_label, _size) -> "self result"
                in
                    let recorded =
                        match (recordedScopeBindingType(binding)(entered), recordedScopeBindingTypeBy(true)(binding)(entered)) with
                            | (Some(_recorded), _settled) -> "table"
                            | (None, Some(_settled)) -> "settled only"
                            | (None, None) -> "no table"
                    in name + " (" + kind + ", " + recorded + ")" :: scopeBindingsClosedByBody(rest)(entered)(finished)
            else scopeBindingsClosedByBody(rest)(entered)(finished)

let recursive joinedBindingNames (names: List(Str)) =
    match names with
        | [] -> "none"
        | name :: [] -> name
        | name :: rest -> name + ", " + joinedBindingNames(rest)

// The shadow answer of a lowering again: for a requirement-closed scope, the scope bindings the
// body closed and whether the table records them at entry.
let relowerAnswerText (rule: Int) (entered: CoreLoweringState) (first: CoreLoweringState) =
    if rule == 4
    then
        "once; closed by the body: " + joinedBindingNames(scopeBindingsClosedByBody(entered.bindings)(entered)(first))
    else "once"

// The second lowering of a body, from a state whose substitution already resolves its calls. The
// closures nested in it are lowered once (see `resolvingCallsAgain`); the flag is put back to what
// the enclosing lowering had before the result is handed on. `rule` is the step-8 rule the body is
// lowered again for (4, a requirement-closed scope; 10, a resolved call result), which the shadow
// comparison counts against a single lowering from final types.
let lowerFunctionBodyAgain (body: Expr) (prepare: CoreLoweringState -> CoreLoweringState) (lower: Expr -> CoreLoweringState -> LoweredCoreValue) (outer: Bool) (rule: Int) (entered: CoreLoweringState) (first: CoreLoweringState) =
    match first
    |> restartedAgainst(entered)
    |> withStateResolvingCallsAgain(true)
    |> observeLoweringEvent(2)
    |> observeShadow(rule)("relower")(first
    |> relowerAnswerText(rule)(entered)
    |> Some)
    |> prepare
    |> lower(body) with
        | LoweredCoreValue { state = againState } as result ->
            let resumed = withStateResolvingCallsAgain(outer)(againState)
            in
                // Replay can revisit a now-concrete operand without consulting the proof it
                // consumed on the first traversal. The next body starts after that traversal.
                if stateTraitGoalsPinned(entered)
                then
                    result with state = withStateProvenTraitGoals(stateProvenTraitGoals(first))(resumed)
                else result with state = resumed

// The second lowering a body asks for, or, in a measurement of what re-lowering costs (the
// driver's single-lowering run), its first lowering kept as it is.
let keepFirstWhenMeasuring (entered: CoreLoweringState) (first: LoweredCoreValue) (again: Unit -> LoweredCoreValue) =
    if stateSingleLowering(entered)
    then first
    else again(Unit)

// A body lowered again because a call to a later group member resolved its result keeps its self
// calls deferred as its first lowering did: stage 0 lowers the body once, with its own result still
// open at every self call, so those calls ask for an arena result. Only the requirement-closed
// lowering again mirrors stage 0's elaborated binding, which resolves them.
let recursive withMissingNames (names: List(Str)) (present: List(Str)) =
    match names with
        | [] -> present
        | name :: rest ->
            if containsLabel(name)(present)
            then withMissingNames(rest)(present)
            else withMissingNames(rest)(name :: present)

let keepFirstSelfResultDeferral (names: List(Str)) (prepare: CoreLoweringState -> CoreLoweringState) (state: CoreLoweringState) =
    state
    |> prepare
    |> (given (prepared: CoreLoweringState) ->
        withStateSelfResultDeferredNames(prepared
        |> stateSelfResultDeferredNames
        |> withMissingNames(names))(prepared))

// The first lowering of a body that may be lowered again. Discovery defers a trait goal whose
// operand is still open there, and the body's later code, or the deferred site's own result,
// often closes that operand, so the body is lowered again with it settled and the record is
// dropped. The replay then has no proof for the site. Its first lowering therefore defers the same
// way, with the pinned boundary lifted, and the lowering again stays pinned. A record that outlives
// the body is refused when the pinned pass ends.
let lowerBodyProvisionally (body: Expr) (prepare: CoreLoweringState -> CoreLoweringState) (lower: Expr -> CoreLoweringState -> LoweredCoreValue) (entered: CoreLoweringState) =
    if stateTraitGoalsPinned(entered)
    then
        match entered
        |> withStateTraitGoalsPinned(false)
        |> prepare
        |> lower(body) with
            | LoweredCoreValue { state = lowered } as first -> first with state = withStateTraitGoalsPinned(true)(lowered)
    else
        entered
        |> prepare
        |> lower(body)

let lowerFunctionBodyResolvingCalls (body: Expr) (prepare: CoreLoweringState -> CoreLoweringState) (close: LoweredCoreValue -> LoweredCoreValue) (lower: Expr -> CoreLoweringState -> LoweredCoreValue) (entered: CoreLoweringState) =
    match if stateResolvingCallsAgain(entered)
    then
        entered
        |> prepare
        |> lower(body)
    else lowerBodyProvisionally(body)(prepare)(lower)(entered) with
        | LoweredCoreValue { error = Some(_error) } as failed -> failed
        | LoweredCoreValue { state = firstState } as first ->
            if stateResolvingCallsAgain(entered)
            then first
            else
                match close(first) with
                    | LoweredCoreValue { state = closedState } ->
                        if bodyEncounteredRequirementClosedScope(body)(entered)(closedState)
                        then
                            keepFirstWhenMeasuring(entered)(first)(given (_) -> closedState |> lowerFunctionBodyAgain(body)(prepare)(lower)(false)(4)(entered))
                        else
                            if anyCallResultResolved(stateUnresolvedCallResults(firstState))(firstState)
                            then
                                keepFirstWhenMeasuring(entered)(first)(given (_) ->
                                    firstState |> lowerFunctionBodyAgain(body)(keepFirstSelfResultDeferral(stateSelfResultDeferredNames(firstState))(prepare))(lower)(false)(10)(entered))
                            else first

// A recursive member nested in a body that is being lowered again takes fresh type variables for
// its own arrow, which the enclosing body's resolved substitution does not cover: stage 0 seeds
// such a binding with the closed type its discovery pass inferred, wherever it is nested. So a
// member whose body applies a mapped operator and closes its scope is lowered again here too; the
// closures nested in it are still lowered once.
let lowerRecursiveBodyResolvingCalls (body: Expr) (prepare: CoreLoweringState -> CoreLoweringState) (close: LoweredCoreValue -> LoweredCoreValue) (lower: Expr -> CoreLoweringState -> LoweredCoreValue) (entered: CoreLoweringState) =
    if stateResolvingCallsAgain(entered)
    then
        match lowerBodyProvisionally(body)(prepare)(lower)(entered) with
            | LoweredCoreValue { error = Some(_error) } as failed -> failed
            | first ->
                match close(first) with
                    | LoweredCoreValue { state = closedState } ->
                        if bodyEncounteredRequirementClosedScope(body)(entered)(closedState)
                        then
                            keepFirstWhenMeasuring(entered)(first)(given (_) -> closedState |> lowerFunctionBodyAgain(body)(prepare)(lower)(true)(4)(entered))
                        else first
    else lowerFunctionBodyResolvingCalls(body)(prepare)(close)(lower)(entered)

// A lambda lowered against an expected function type hands the expected result type on to its
// body, as a recursive member does: a nested lambda of a curried chain then pins its own
// parameter before the body is lowered, so a trait-mapped operator inside it sees the type the
// context already knows rather than a variable no active evidence covers.
let withLambdaBodyRequest (body: Expr) (expectedResult: Maybe(SemanticType)) (prepared: CoreLoweringState) =
    match expectedResult with
        | Some(resultType) -> withRecursiveBodyRequest(body)(resultType)(prepared)
        | None -> withFunctionBodyRequest(body)(prepared)

let expectedLambdaResultType (state: CoreLoweringState) =
    match expectedTypeOf(state) with
        | Some(expected) ->
            match resolveType(state)(expected) with
                | SemFunction(_argument, result, _row) -> Some(result)
                | _ -> None
        | None -> None

let lowerLambdaBody parameter body stackAllocate lower lambdaId captures origin expectedResult fresh =
    match fresh with
        | FreshType { state = typedOuter, semanticType = parameterType } ->
            typedOuter
            |> takeRuntimeClosureRequest
            |> linkCurryStage("lambda_" + Ashes.Text.fromInt(lambdaId))(parameter)
            |> prepareLambdaBodyState(parameter)(parameterType)(captures)(lambdaId)(origin)
            |> enterLambdaAtNode(match typedOuter.currentNode with
                | Some(node) -> node
                | None -> -1)(parameter)
            |> noteLetBoundFunction("lambda_" + Ashes.Text.fromInt(lambdaId))(origin)
            |> lowerFunctionBodyResolvingCalls(body)(given (entered: CoreLoweringState) ->
                entered
                |> withNormalizedAlwaysReturnedParameter(parameter)(body)("lambda_" + Ashes.Text.fromInt(lambdaId))(parameterType)
                |> enterLambdaTcoLoop(parameterType)
                |> enterTcoLoopBody("lambda_" + Ashes.Text.fromInt(lambdaId))(parameter)
                |> withStateLoweringBodyLabel("lambda_" + Ashes.Text.fromInt(lambdaId))
                |> enterPendingCoroutineBody
                |> withPassthroughParameter(parameter)(body)(parameterType)
                |> predictRuntimeManagedResult("lambda_" + Ashes.Text.fromInt(lambdaId))(parameterType)
                |> beginCurryStage("lambda_" + Ashes.Text.fromInt(lambdaId))(body)
                |> withLambdaBodyRequest(body)(expectedResult))(given (first: LoweredCoreValue) -> first)(lower)
            |> normalizeAlwaysReturnedParameter(parameter)(body)("lambda_" + Ashes.Text.fromInt(lambdaId))(parameterType)
            |> finalizeTcoRuntimeManagedParams("lambda_" + Ashes.Text.fromInt(lambdaId))
            |> finishLambdaBody(parameter)(body)("lambda_" + Ashes.Text.fromInt(lambdaId))(origin)(captures)(stackAllocate)(typedOuter)(parameterType)
