// Consumer requests, resource bindings, owner tracking and release, let-value storage, scope
// copy-out, fresh-value shape queries, pattern binding names and capture pruning. A part of
// `AshesCompiler.Semantics.CoreLowering`.

import Ashes.Collection.List.append
import Ashes.Collection.List.length
import Ashes.Collection.List.map
import Ashes.Collection.List.reverse
import Ashes.Collection.Map.MapTree
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.Pattern
import AshesCompiler.Semantics.AggregateOwnership
import AshesCompiler.Semantics.RecursiveProducerResult
import AshesCompiler.Semantics.CallOwnership
import AshesCompiler.Semantics.CoreConstrainedBindings
import AshesCompiler.Semantics.CoreBuiltinLowering
import AshesCompiler.Semantics.CoreExternalLowering
import AshesCompiler.Semantics.ConstructionRetains
import AshesCompiler.Semantics.ExternalAbi
import AshesCompiler.Semantics.ExternalTyping
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.HeapLayoutClassification
import AshesCompiler.Semantics.IrControlFlowGraph.containsInt
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrInstructionTemps.mapInstructionLocals
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.MatchArmOwnership
import AshesCompiler.Semantics.OwnershipInference.classifyParameterOwnership
import AshesCompiler.Semantics.ProgramParameterOwnershipIndex
import AshesCompiler.Semantics.OwnershipSummary
import AshesCompiler.Semantics.StructuralDroppers
import AshesCompiler.Semantics.SourceContext
import AshesCompiler.Semantics.TaglessAdtLayout
import AshesCompiler.Semantics.TcoRuntimeManagedParams
import AshesCompiler.Semantics.PatternBindingOwnership
import AshesCompiler.Semantics.TypeSchemes
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.Unification
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.CoreLowering.LoweringState
import AshesCompiler.Semantics.CoreLowering.Emit
export (
    type BuiltinResourceRole(..),
    type ScopeCopyOut(..),
    value dispatchRequest,
    value unifyUnforwardedExpectedType,
    value constructorLayout,
    value constructorNameKnown,
    value builtinLayout,
    value instantiateConstructor,
    value instantiateBuiltin,
    value coreListLength,
    value constructorResultName,
    value constructorArity,
    value builtinArity,
    value recordLayout,
    value failedCoreValues,
    value finishCoreValues,
    value lowerCoreValuesInto,
    value lowerCoreValues,
    value bindCoreValueTypes,
    value lowerConstant,
    value lowerGuardConstant,
    value internString,
    value lowerString,
    value lowerGuardString,
    value runtimeManagedAdtTypeName,
    value stateOwnedTypeNameOf,
    value patternOwnerTypeName,
    value isResourceTypeName,
    value isDeclaredResourceName,
    value isResourceTypeNameIn,
    value resourceTypeNameOf,
    value unspanArgument,
    value isConsValue,
    value resourceBindingOf,
    value resourceStateOf,
    value markResourceReleased,
    value markResourceArgumentsMoved,
    value markResourceArgumentClosed,
    value checkResourceArgumentUse,
    value externalInputParameters,
    value checkExternalResourceArguments,
    value applyLoweredExternalResourceTransfers,
    value builtinResourceRole,
    value lookupLetLambdaNamed,
    value registeredTopLevelName,
    value provenParameterOwnership,
    value markCallArgumentsMoved,
    value lookupLetLambdaLabel,
    value lookupClosureDropperLabel,
    value markLoweredCallArgumentsMoved,
    value emitResourceCleanup,
    value markCapturedResourcesMoved,
    value releaseRuntimeOwner,
    value transferRuntimeOwner,
    value retireRuntimeOwner,
    value runtimeOwnerStateOf,
    value liveRuntimeOwnerSlot,
    value mayBeEmptyList,
    value emitTransferRetain,
    value patternBindingFact,
    value patternOwnerBinding,
    value patternOwnerFlag,
    value duplicatePatternOwnerTemp,
    value duplicatePatternOwnerChild,
    value lowerBoundVariable,
    value lowerUnconstrainedBoundVariable,
    value addBinding,
    value addOwnedBinding,
    value addPatternBinding,
    value restoreBindings,
    value restoreLoweredBindings,
    value pendingOperatorTypes,
    value sealedOperatorTypes,
    value pendingOperatorScheme,
    value resolvedAmbientAnswer,
    value generalizeResolvedType,
    value tailForwardedVariable,
    value isTailForwardedBindingResult,
    value emptyEnvironmentClosureLabel,
    value inProgramEntryFrame,
    value storeLetValue,
    value stripExprAt,
    value patternBindsName,
    value nameUsedOnlyAsDirectCallee,
    value loweredValueOwnedTypeName,
    value coverageEnvironment,
    value withRefreshedHeapLayoutFacts,
    value withDroppedHeapLayoutFacts,
    value heapFactsOf,
    value heapResourceOf,
    value lookupOwnerReleasePlan,
    value locateSynthesizedFunction,
    value spliceInlineRelease,
    value emitInlineOwnerRelease,
    value emitOwnedLetRelease,
    value emitRestoreAndReclaim,
    value operatorDefaultedVariables,
    value resultSurvivesReset,
    value backEdgeArgumentSurvivesReset,
    value shareOwnerReleasePlan,
    value ownerValueTypeOf,
    value borrowedOwnersOfSlot,
    value retainTransferredChild,
    value retainAliasArgumentOwners,
    value closeScopeForResult,
    value shallowAdtCopySizeBytes,
    value scopeCopyOutOfIndexed,
    value markCopyRuntime,
    value isDuplicateMarker,
    value closeOwnedScopeForResult,
    value closeOwnedLetBracket,
    value recursiveProducerResult,
    value storeLetValueInSlot,
    value finishLetValueInSlot,
    value qualifiedCallRoot,
    value isRuntimeRcStringProducer,
    value isCaptureSafeStringProducer,
    value isDirectBindingResult,
    value isImmediateRuntimeStringUse,
    value layoutFieldTypes,
    value constructorApplicationOf,
    value isConstructorExpression,
    value isRecordLiteral,
    value isTupleLiteral,
    value recordAdtSupported,
    value copyAdtSupported,
    value ownedChildAdtSupported,
    value tcoOwnedChildAdtSupported,
    value namedTypeNameOf,
    value isFreshStringChild,
    value isNormalizedAlwaysReturnedStringParameterRead,
    value isPatternOwnerRead,
    value canRuntimeManageRecursiveCopyAdt,
    value isFreshConstructorTree,
    value canRuntimeManageGenericCopyAdtApplication,
    value canRuntimeManageFreshHeapChildAdtApplication,
    value isMaterializableStringChildRead,
    value allFreshOwnedChildren,
    value isFreshRuntimeManageableRecordTree,
    value canRuntimeManageOwnedChildAdtApplication,
    value isFreshRuntimeManageableAdtExpression,
    value callSpineRootAndArity,
    value isSelfFunnelArm,
    value armNeverReachesJoin,
    value patternFactsNamed,
    value retainedPatternOwnerTerminal,
    value armOwnerReleasePlan,
    value reconcilableStringArms,
    value isReconcilableFreshStringJoin,
    value escapingResultRequest,
    value containsName,
    value collectFree,
    value capturedBindings,
    value captureCount,
    value allocateEnvironment,
    value storeCaptureAsRead,
    value pruneDeadCaptures,
    value capturedScope,
    value finishLiftedFunction,
)

let recursive expectedTypeForwards expression =
    match expression with
        | ExprAt(_span, inner) -> expectedTypeForwards(inner)
        | ExprNode(_node, inner) -> expectedTypeForwards(inner)
        | ExprLet(_, _, _, _, _, _) -> true
        | ExprLetRecursive(_, _, _, _, _, _) -> true
        | ExprLambda(_, _, _) -> true
        | ExprIf(_, _, _) -> true
        | ExprMatch(_, _, _) -> true
        | ExprHandle(_, _) -> true
        | ExprCall(_, _, _, _) -> true
        | ExprList(_, _) -> true
        | ExprCons(_, _) -> true
        | ExprTuple(_) -> true
        | _ -> false

// The runtime-string request reaches every kind the expected type reaches except a tuple literal,
// which hands its elements requests of their own, and `+`, whose string concatenation is itself
// the producer that honors it.
let recursive runtimeRequestForwards expression =
    match expression with
        | ExprAt(_span, inner) -> runtimeRequestForwards(inner)
        | ExprNode(_node, inner) -> runtimeRequestForwards(inner)
        | ExprAdd(_, _) -> true
        | ExprTuple(_) -> false
        | other -> expectedTypeForwards(other)

// A binding transfer only reaches the straight `let` chain down to the binding's own read.
let recursive transferForwards expression =
    match expression with
        | ExprAt(_span, inner) -> transferForwards(inner)
        | ExprNode(_node, inner) -> transferForwards(inner)
        | ExprLet(_, _, _, _, _, _) -> true
        | ExprLetRecursive(_, _, _, _, _, _) -> true
        | ExprVar(_) -> true
        | _ -> false

// Tail position survives only the forms whose value is the enclosing body's own result — a
// `let` body, an `if` branch (the short-circuit operators lower as one), a `match` arm, and the
// call itself; stage 0's `TcoTailPositionScope` suppresses it under everything else, every
// operator operand included.
let recursive tailPositionForwards expression =
    match expression with
        | ExprAt(_span, inner) -> tailPositionForwards(inner)
        | ExprNode(_node, inner) -> tailPositionForwards(inner)
        | ExprLet(_, _, _, _, _, _) -> true
        | ExprLetRecursive(_, _, _, _, _, _) -> true
        | ExprIf(_, _, _) -> true
        | ExprLogicalAnd(_, _) -> true
        | ExprLogicalOr(_, _) -> true
        | ExprMatch(_, _, _) -> true
        | ExprCall(_, _, _, _) -> true
        | _ -> false

// A cons does not forward tail position to its operands — a call under one is not a tail call — but
// the cons ITSELF still stands where the function's result does, and that fact is what decides
// whether its children are transferred out of the loop and whether it is the tail-modulo-constructor
// shape. Stage 0 reads its ambient flag before the cons clears it; the same value is kept here by
// forwarding tail position (and only tail position, never `tailCall`) into the cons. A record or a
// record update standing there is the loop's result the same way (stage 0's ambient flag at its
// construction), which the duplicates its cells keep for the result's copy are owned against.
let recursive tailPositionOrConsForwards expression =
    match expression with
        | ExprAt(_span, inner) -> tailPositionOrConsForwards(inner)
        | ExprNode(_node, inner) -> tailPositionOrConsForwards(inner)
        | ExprCons(_, _) -> true
        | ExprRecord(_, _, _) -> true
        | ExprRecordUpdate(_, _) -> true
        | _ -> tailPositionForwards(expression)

// The transfer of an aggregate's children reaches the forms that forward their result and the
// aggregates that store the children: a constructor application, a record, a record update, a
// cons cell, a list literal, and a tuple.
let recursive transferRequestForwards expression =
    match expression with
        | ExprAt(_span, inner) -> transferRequestForwards(inner)
        | ExprNode(_node, inner) -> transferRequestForwards(inner)
        | ExprLet(_, _, _, _, _, _) -> true
        | ExprLetRecursive(_, _, _, _, _, _) -> true
        | ExprIf(_, _, _) -> true
        | ExprMatch(_, _, _) -> true
        | ExprCall(_, _, _, _) -> true
        | ExprRecord(_, _, _) -> true
        | ExprRecordUpdate(_, _) -> true
        | ExprCons(_, _) -> true
        | ExprList(_, _) -> true
        | ExprTuple(_) -> true
        | _ -> false

// An aggregate representation request reaches the forms that forward their result and the
// aggregates that honor it: a `let` body, an `if` branch, a `match` arm, a handled body, a call
// (a constructor application among them), a list literal, a cons cell, a tuple, and a record.
// A bare variable keeps the aggregate flags: a bound read ignores them, and a nullary constructor
// reference honors them the way stage 0's `LowerNullaryConstructor` takes the request.
let recursive aggregateRequestForwards expression =
    match expression with
        | ExprAt(_span, inner) -> aggregateRequestForwards(inner)
        | ExprNode(_node, inner) -> aggregateRequestForwards(inner)
        | ExprTuple(_) -> true
        | ExprRecord(_, _, _) -> true
        | ExprVar(_name) -> true
        | ExprLambda(_, _, _) -> false
        | other -> expectedTypeForwards(other)

// A borrowing successor cell's flag reaches the cons itself, through its spans, and nothing else.
// A successor cell's borrow of its parts reaches the cons the argument writes out, and the cons
// cells at the arms of a choice argument (stage 0's `CollectChoiceArmCells`) through `if`, `match`
// and `let` bodies.
let consPartsRequestForwards (expression: Expr) =
    match expression with
        | ExprAt(_span, _inner) -> true
        | ExprNode(_node, _inner) -> true
        | ExprCons(_head, _tail) -> true
        | ExprList(_first :: _rest, _isMultiline) -> true
        | ExprIf(_condition, _thenBranch, _elseBranch) -> true
        | ExprMatch(_value, _cases, _position) -> true
        | ExprLet(_name, _value, _body, [], _annotation, _requirements) -> true
        | _ -> false

// A record successor's flag reaches the record update itself, through its spans.
let recordSuccessorRequestForwards (expression: Expr) =
    match expression with
        | ExprAt(_span, _inner) -> true
        | ExprNode(_node, _inner) -> true
        | ExprRecordUpdate(_target, _fields) -> true
        | ExprRecord(_name, _fields, _isMultiline) -> true
        | _ -> false

// The escaping argument cell's flag reaches the cons the argument writes out, through its spans.
let argumentCellRequestForwards (expression: Expr) =
    match expression with
        | ExprAt(_span, _inner) -> true
        | ExprNode(_node, _inner) -> true
        | ExprCons(_head, _tail) -> true
        | _ -> false

let dispatchRequest (expression: Expr) (request: ConsumerRequest) =
    ConsumerRequest(
        expectedType = if expectedTypeForwards(expression)
        then request.expectedType
        else None,
        argumentSite = if expectedTypeForwards(expression)
        then request.argumentSite
        else None,
        runtimeString = runtimeRequestForwards(expression) && request.runtimeString,
        runtimeAdt = aggregateRequestForwards(expression) && request.runtimeAdt,
        runtimeRecord = aggregateRequestForwards(expression) && request.runtimeRecord,
        runtimeList = aggregateRequestForwards(expression) && request.runtimeList,
        runtimeTuple = aggregateRequestForwards(expression) && request.runtimeTuple,
        transferSlot = if transferForwards(expression)
        then request.transferSlot
        else None,
        tailPosition = tailPositionForwards(expression) && request.tailPosition,
        transfersRuntimeManagedChildren = transferRequestForwards(expression) && request.transfersRuntimeManagedChildren,
        tailCall = tailPositionForwards(expression) && request.tailCall,
        consTailPosition = tailPositionOrConsForwards(expression) && request.consTailPosition,
        runtimeTcoListTailSlot = if aggregateRequestForwards(expression)
        then request.runtimeTcoListTailSlot
        else None,
        borrowsCellParts = consPartsRequestForwards(expression) && request.borrowsCellParts,
        lateCopiedCellParts = consPartsRequestForwards(expression) && request.lateCopiedCellParts,
        copiedInnerCell = consPartsRequestForwards(expression) && request.copiedInnerCell,
        recordFieldCells = recordSuccessorRequestForwards(expression) && request.recordFieldCells,
        recordFieldCell = argumentCellRequestForwards(expression) && request.recordFieldCell,
        escapingArgumentCell = argumentCellRequestForwards(expression) && request.escapingArgumentCell,
        chainTailCell = argumentCellRequestForwards(expression) && request.chainTailCell,
        callArgument = transferRequestForwards(expression) && request.callArgument
    )

let unifyUnforwardedExpectedType (expression: Expr) (request: ConsumerRequest) (lowered: LoweredCoreValue) =
    match request.expectedType with
        | Some(expectedType) ->
            if expectedTypeForwards(expression)
            then lowered
            else
                lowered
                |> unifyExpectedResult(expectedType)
                |> locateLoweredMismatch(request.argumentSite)
        | None -> lowered

let recursive findConstructorLayout (name: Str) (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> None
        | (CoreConstructorLayout { name = candidate } as layout) :: rest ->
            if name == candidate
            then Some(layout)
            else findConstructorLayout(name)(rest)

let constructorLayout name state =
    match state with
        | CoreLoweringState { programState = CoreProgramState { constructorIndex = index } } -> lookupFirstConstructor(name)(index)

let constructorNameKnown (name: Str) (state: CoreLoweringState) =
    match constructorLayout(name)(state) with
        | Some(_layout) -> true
        | None -> false

let recursive findBuiltinLayout moduleName memberName layouts =
    match layouts with
        | [] -> None
        | (CoreBuiltinLayout { moduleName = candidateModule, memberName = candidateMember } as layout) :: rest ->
            if moduleName == candidateModule
            then
                if memberName == candidateMember
                then Some(layout)
                else findBuiltinLayout(moduleName)(memberName)(rest)
            else findBuiltinLayout(moduleName)(memberName)(rest)

let builtinLayout moduleName memberName state =
    match state with
        | CoreLoweringState { programState = CoreProgramState { builtinLayouts = layouts } } -> findBuiltinLayout(moduleName)(memberName)(layouts)

let recursive splitConstructorType semanticType reversed =
    match semanticType with
        | SemFunction(parameterType, resultType, _row) -> splitConstructorType(resultType)(parameterType :: reversed)
        | resultType -> (reverse(reversed), resultType)

// A constructor allocates in the arena unless its consumer requested a runtime-managed (RC) cell
// through `runtimeAdtRequested`. The request is consumed here, before the constructor's arguments
// are lowered, so a nested constructor argument allocates in the arena as usual.
let instantiateConstructor layout state =
    match (layout, state) with
        | (CoreConstructorLayout { scheme = scheme }, CoreLoweringState { typeSupply = supply, functionState = CoreFunctionState { runtimeAdtRequested = requested } }) ->
            match instantiate(scheme)(supply) with
                | InstantiationResult { semanticType = semanticType, supply = nextSupply } ->
                    match splitConstructorType(semanticType)([]) with
                        | (parameterTypes, resultType) ->
                            CoreConstructorShape(
                                state = state
                                |> withStateRuntimeAdtRequested(false)
                                |> withTypeSupply(nextSupply),
                                layout = layout,
                                parameterTypes = parameterTypes,
                                resultType = resultType,
                                constructorRuntimeManaged = requested
                            )

let instantiateBuiltin layout state =
    match (layout, state) with
        | (CoreBuiltinLayout { scheme = scheme }, CoreLoweringState { typeSupply = supply }) ->
            match instantiate(scheme)(supply) with
                | InstantiationResult { semanticType = semanticType, supply = nextSupply } ->
                    match splitConstructorType(semanticType)([]) with
                        | (parameterTypes, resultType) ->
                            CoreBuiltinShape(
                                state = withTypeSupply(nextSupply)(state),
                                layout = layout,
                                parameterTypes = parameterTypes,
                                resultType = resultType
                            )

let recursive coreListLength values =
    match values with
        | [] -> 0
        | _ :: rest -> 1 + coreListLength(rest)

let constructorResultName layout =
    match layout with
        | CoreConstructorLayout { scheme = TypeScheme { body = body } } ->
            match splitConstructorType(body)([]) with
                | (_parameters, SemNamed(_symbolId, name, _arguments)) -> Some(name)
                | _ -> None

let constructorArity layout =
    match layout with
        | CoreConstructorLayout { scheme = TypeScheme { body = body } } ->
            match splitConstructorType(body)([]) with
                | (parameterTypes, _resultType) -> coreListLength(parameterTypes)

let builtinArity layout =
    match layout with
        | CoreBuiltinLayout { scheme = TypeScheme { body = body } } ->
            match splitConstructorType(body)([]) with
                | (parameterTypes, _resultType) -> coreListLength(parameterTypes)

let recursive findRecordLayout (typeName: Str) (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> None
        | (CoreConstructorLayout { fieldNames = _field :: _rest } as layout) :: tail ->
            match constructorResultName(layout) with
                | Some(candidate) ->
                    if candidate == typeName
                    then Some(layout)
                    else findRecordLayout(typeName)(tail)
                | None -> findRecordLayout(typeName)(tail)
        | _layout :: tail -> findRecordLayout(typeName)(tail)

let recordLayout typeName state =
    state
    |> stateConstructorsOfType(typeName)
    |> findRecordLayout(typeName)

let failedCoreValues state error =
    LoweredCoreValues(
        state = state,
        temps = [],
        semanticTypes = [],
        error = Some(error)
    )

let finishCoreValues state reversedTemps reversedTypes =
    LoweredCoreValues(
        state = state,
        temps = reverse(reversedTemps),
        semanticTypes = reverse(reversedTypes),
        error = None
    )

let recursive lowerCoreValuesInto expressions lower state reversedTemps reversedTypes =
    match expressions with
        | [] -> finishCoreValues(state)(reversedTemps)(reversedTypes)
        | expression :: rest ->
            match lower(expression)(state) with
                | LoweredCoreValue { state = failedState, error = Some(error) } -> failedCoreValues(failedState)(error)
                | LoweredCoreValue { state = nextState, temp = temp, semanticType = semanticType, error = None } ->
                    lowerCoreValuesInto(
                        rest,
                        lower,
                        nextState,
                        temp :: reversedTemps,
                        semanticType :: reversedTypes
                    )

let lowerCoreValues expressions lower state = lowerCoreValuesInto(expressions)(lower)(state)([])([])

let recursive bindCoreValueTypes expected actual state =
    match (expected, actual) with
        | ([], []) -> (state, None)
        | (expectedHead :: expectedTail, actualHead :: actualTail) ->
            match bindType(expectedHead)(actualHead)(state) with
                | (failedState, Some(error)) -> (failedState, Some(error))
                | (typedState, None) -> bindCoreValueTypes(expectedTail)(actualTail)(typedState)
        | _ ->
            (state, state
            |> mismatchSiteOf
            |> CoreCallTypeMismatch(actual
            |> coreListLength
            |> TypeArityMismatch(coreListLength(expected)))
            |> Some)

let lowerConstant kind semanticType state =
    match freshTemp(state) with
        | FreshTemp { state = nextState, temp = temp } ->
            nextState
            |> emit(kind(temp))
            |> success(temp)(semanticType)

// Like `lowerConstant`, for a constant that only ever feeds a pattern guard's comparison
// instruction: it is never bound, returned, or otherwise a source-facing value, so unlike
// `lowerConstant` it does not record a value placement for the report.
let lowerGuardConstant kind semanticType state =
    match freshTemp(state) with
        | FreshTemp { state = nextState, temp = temp } ->
            LoweredCoreValue(
                state = emit(kind(temp))(nextState),
                temp = temp,
                semanticType = semanticType,
                error = None
            )

let recursive findStringLiteral value literals =
    match literals with
        | [] -> None
        | IrStringLiteral { label = label, value = candidate } :: rest ->
            if value == candidate
            then Some(label)
            else findStringLiteral(value)(rest)

let internString value state =
    match state with
        | CoreLoweringState { stringLiterals = literals, functionState = CoreFunctionState { nextStringId = nextId } } ->
            match findStringLiteral(value)(literals) with
                | Some(label) -> StringInterning(state = state, label = label)
                | None ->
                    let label = "str_" + Ashes.Text.fromInt(nextId)
                    in
                        StringInterning(
                            state = state
                            |> (given (current: CoreLoweringState) -> withStateNextStringId(nextId + 1)(current))
                            |> (given (current: CoreLoweringState) ->
                                current with stringLiterals = append(literals)([
                                    IrStringLiteral(label = label, value = value)
                                ])),
                            label = label
                        )

let lowerString value state =
    match internString(value)(state) with
        | StringInterning { state = internedState, label = label } ->
            lowerConstant(given (target) -> LoadConstStr(target)(label))(SemString)(internedState)

// Like `lowerString`, for a string constant that only feeds a pattern guard's comparison.
let lowerGuardString value state =
    match internString(value)(state) with
        | StringInterning { state = internedState, label = label } ->
            lowerGuardConstant(given (target) -> LoadConstStr(target)(label))(SemString)(internedState)

// The payload type a zero-cost single-constructor type is represented as, when `name` names one.
let recursive zeroCostPayloadType (name: Str) (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> None
        | CoreConstructorLayout { isZeroCost = true, scheme = TypeScheme { body = SemFunction(payload, SemNamed(_symbolId, candidate, _arguments), _row) } } :: rest ->
            if candidate == name
            then Some(payload)
            else zeroCostPayloadType(name)(rest)
        | _ :: rest -> zeroCostPayloadType(name)(rest)

// Stage 0's `RuntimeManagedAdtTypeName`: the type name a release or cleanup instruction carries
// for a user aggregate. The backend releases a `Function` through the closure protocol (its
// dropper at the closure's fourth word), so a user type of that name is tagged apart from it.
let runtimeManagedAdtTypeName (name: Str) =
    if name == "Function"
    then "Function_"
    else name

// The type name an owned binding carries, stage 0's `GetOwnedTypeName`: a heap-represented value
// (string, bytes, big integer, list, tuple, closure, or a declared type, seen through a zero-cost
// wrapper to its payload) is owned; copy types and unresolved type variables are not. A zero-cost
// wrapper whose payload is a type variable erases to that variable and so counts as not owned.
let recursive ownedTypeNameOf (semanticType: SemanticType) (index: CoreConstructorLookupIndex) =
    match semanticType with
        | SemString -> Some("String")
        | SemBytes -> Some("Bytes")
        | SemBigInt -> Some("BigInt")
        | SemList(_element) -> Some("List")
        | SemTuple(_elements) -> Some("Tuple")
        | SemFunction(_parameter, _result, _row) -> Some("Function")
        | SemNamed(_symbolId, name, _arguments) ->
            match index
            |> lookupResultConstructors(name)
            |> zeroCostPayloadType(name) with
                | Some(payload) -> ownedTypeNameOf(payload)(index)
                | None ->
                    name
                    |> runtimeManagedAdtTypeName
                    |> Some
        | _ -> None

let stateOwnedTypeNameOf (semanticType: SemanticType) (state: CoreLoweringState) =
    (let group = state.programState
    in ownedTypeNameOf(semanticType)(group.constructorIndex))

// The type name a pattern owner releases under: its owned type name, or stage 0's
// `PatternBinding` for a binding whose type is a copy type or still unresolved.
let patternOwnerTypeName (resolved: SemanticType) (state: CoreLoweringState) =
    match state |> stateOwnedTypeNameOf(resolved) with
        | Some(typeName) -> typeName
        | None -> "PatternBinding"

// The compiler-provided resource types (§16 of the language reference) the lowering tracks.
let isResourceTypeName (name: Str) = name == "FileHandle" || name == "Process" || name == "Socket" || name == "TlsSocket"

// The destructor of a declared external resource (`external type T resource destructor f`): the
// external function naming `T` as the resource it destroys.
let recursive findResourceDestructor (typeName: Str) (functions: List(ExternalFunctionAbi)) =
    match functions with
        | [] -> None
        | (ExternalFunctionAbi { destructorForResource = Some(resource) } as abi) :: rest ->
            if resource == typeName
            then Some(abi)
            else findResourceDestructor(typeName)(rest)
        | _ :: rest -> findResourceDestructor(typeName)(rest)

let declaredResourceDestructor (typeName: Str) (state: CoreLoweringState) = findResourceDestructor(typeName)(state.externalFunctions)

let isDeclaredResourceName (typeName: Str) (state: CoreLoweringState) =
    match declaredResourceDestructor(typeName)(state) with
        | Some(_abi) -> true
        | None -> false

// A resource type name: a compiler-provided handle or a declared external resource.
let isResourceTypeNameIn (typeName: Str) (state: CoreLoweringState) = isResourceTypeName(typeName) || isDeclaredResourceName(typeName)(state)

// The resource type a resolved type is, through zero-cost wrappers: a compiler-provided handle
// by its owned type name, a declared external resource by its opaque name.
let resourceTypeNameOf (semanticType: SemanticType) (state: CoreLoweringState) =
    match semanticType with
        | SemOpaque(name) ->
            if isDeclaredResourceName(name)(state)
            then Some(name)
            else None
        | other ->
            match state |> stateOwnedTypeNameOf(other) with
                | Some(typeName) ->
                    if isResourceTypeName(typeName)
                    then Some(typeName)
                    else None
                | None -> None

let recursive unspanArgument (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> unspanArgument(inner)
        | ExprNode(_node, inner) -> unspanArgument(inner)
        | other -> other

let isConsValue (expression: Expr) =
    match unspanArgument(expression) with
        | ExprCons(_head, _tail) -> true
        | _ -> false

// The resource a variable names: the slot and resource type of an owned local binding whose
// resolved type is a resource.
let resourceBindingOf (name: Str) (state: CoreLoweringState) =
    match lookupBinding(name)(state.bindings) with
        | Some(CoreBinding { location = CoreLocal(slot), ownedRead = true, scheme = TypeScheme { body = bindingType } }) ->
            match resourceTypeNameOf(resolveType(state)(bindingType))(state) with
                | Some(typeName) -> Some((slot, typeName))
                | None -> None
        | _ -> None

let recursive lookupResourceState (slot: Int) (states: List((Int, ResourceReleaseKind))) =
    match states with
        | [] -> None
        | (candidate, kind) :: rest ->
            if candidate == slot
            then Some(kind)
            else lookupResourceState(slot)(rest)

let resourceStateOf (slot: Int) (state: CoreLoweringState) =
    state
    |> stateResourceStates
    |> lookupResourceState(slot)

let markResourceReleased (slot: Int) (kind: ResourceReleaseKind) (state: CoreLoweringState) = withStateResourceStates((slot, kind) :: stateResourceStates(state))(state)

// The state of the resource a variable argument names, when it names one: `Some(None)` for a live
// resource, `Some(Some(kind))` for a released one, `None` for anything else.
let resourceArgumentState (argument: Expr) (state: CoreLoweringState) =
    match unspanArgument(argument) with
        | ExprVar(name) ->
            match resourceBindingOf(name)(state) with
                | Some((slot, _typeName)) -> Some((name, slot, resourceStateOf(slot)(state)))
                | None -> None
        | _ -> None

// Stage 0's `MarkResourceArgMoved`: a variable argument naming a live resource is moved.
let markResourceArgumentMoved (argument: Expr) (state: CoreLoweringState) =
    match resourceArgumentState(argument)(state) with
        | Some((_name, slot, None)) -> markResourceReleased(slot)(ResourceMoved)(state)
        | _ -> state

let recursive markResourceArgumentsMoved (arguments: List(Expr)) (state: CoreLoweringState) =
    match arguments with
        | [] -> state
        | argument :: rest ->
            state
            |> markResourceArgumentMoved(argument)
            |> markResourceArgumentsMoved(rest)

// Stage 0's `TryMarkDropped`: an explicit close releases a live resource binding.
let markResourceArgumentClosed (argument: Expr) (state: CoreLoweringState) =
    match resourceArgumentState(argument)(state) with
        | Some((_name, slot, None)) -> markResourceReleased(slot)(ResourceClosed)(state)
        | _ -> state

// Stage 0's `CheckUseAfterDrop`: using a released resource is use-after-move or use-after-close.
let checkResourceArgumentUse (argument: Expr) (state: CoreLoweringState) =
    match resourceArgumentState(argument)(state) with
        | Some((name, _slot, Some(ResourceMoved))) -> Some(ResourceUseAfterMove("Resource '" + name + "' has been moved and can no longer be used here. Passing a resource to a function or storing it in a data structure transfers ownership."))
        | Some((name, _slot, Some(ResourceClosed))) -> Some(ResourceUseAfterClose("Resource '" + name + "' has already been closed. Using a resource after it has been closed is not allowed."))
        | _ -> None

// Stage 0's `CheckExplicitExternalResourceClose`: closing a released resource through its
// destructor is closing a moved one or closing twice.
let checkResourceArgumentClose (argument: Expr) (state: CoreLoweringState) =
    match resourceArgumentState(argument)(state) with
        | Some((name, _slot, Some(ResourceMoved))) -> Some(ResourceUseAfterMove("Resource '" + name + "' has been moved and can no longer be closed here. Ownership was transferred when it was passed to a function or stored in a data structure."))
        | Some((name, _slot, Some(ResourceClosed))) -> Some(ResourceDoubleClose("Resource '" + name + "' has already been closed. Closing a resource twice is not allowed."))
        | _ -> None

// An external call's resource contract per input parameter (stage 0's
// `CheckExternalResourceArgument`/`ApplyExternalResourceTransfer`): a `consume` parameter of the
// resource's own destructor closes the argument, any other `consume` moves it, a `borrow` only
// reads it, and each is checked against the binding's state first.
let recursive externalInputParameters (parameters: List(ExternalParameterAbi)) =
    match parameters with
        | [] -> []
        | parameter :: rest ->
            if isOutParameterAbi(parameter)
            then externalInputParameters(rest)
            else parameter :: externalInputParameters(rest)

let externalParameterOwnership (parameter: ExternalParameterAbi) =
    match parameter with
        | ExternalParameterAbi { source = ExternalParameterTyping { ownership = ownership } } -> ownership

let recursive checkExternalResourceArguments (abi: ExternalFunctionAbi) (parameters: List(ExternalParameterAbi)) (arguments: List(Expr)) (state: CoreLoweringState) =
    match (parameters, arguments) with
        | (parameter :: parameterRest, argument :: argumentRest) ->
            match (externalParameterOwnership(parameter), abi.destructorForResource) with
                | (ExternalOwnershipConsume, Some(_resource)) ->
                    match checkResourceArgumentClose(argument)(state) with
                        | Some(error) -> Some(error)
                        | None -> checkExternalResourceArguments(abi)(parameterRest)(argumentRest)(state)
                | (ExternalOwnershipConsume, None) ->
                    match checkResourceArgumentUse(argument)(state) with
                        | Some(error) -> Some(error)
                        | None -> checkExternalResourceArguments(abi)(parameterRest)(argumentRest)(state)
                | (ExternalOwnershipBorrow, _destructor) ->
                    match checkResourceArgumentUse(argument)(state) with
                        | Some(error) -> Some(error)
                        | None -> checkExternalResourceArguments(abi)(parameterRest)(argumentRest)(state)
                | _ -> checkExternalResourceArguments(abi)(parameterRest)(argumentRest)(state)
        | _ -> None

let recursive applyExternalResourceTransfers (abi: ExternalFunctionAbi) (parameters: List(ExternalParameterAbi)) (arguments: List(Expr)) (state: CoreLoweringState) =
    match (parameters, arguments) with
        | (parameter :: parameterRest, argument :: argumentRest) ->
            match (externalParameterOwnership(parameter), abi.destructorForResource) with
                | (ExternalOwnershipConsume, Some(_resource)) ->
                    state
                    |> markResourceArgumentClosed(argument)
                    |> applyExternalResourceTransfers(abi)(parameterRest)(argumentRest)
                | (ExternalOwnershipConsume, None) ->
                    state
                    |> markResourceArgumentMoved(argument)
                    |> applyExternalResourceTransfers(abi)(parameterRest)(argumentRest)
                | _ -> applyExternalResourceTransfers(abi)(parameterRest)(argumentRest)(state)
        | _ -> state

let applyLoweredExternalResourceTransfers (abi: ExternalFunctionAbi) (arguments: List(Expr)) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { error = Some(_error) } -> lowered
        | LoweredCoreValue { state = state } ->
            lowered with state = applyExternalResourceTransfers(abi)(externalInputParameters(abi.parameters))(arguments)(state)

// Whether a builtin reads its first argument as a resource, closes it, or neither.
type BuiltinResourceRole =
    | BuiltinUsesResource
    | BuiltinClosesResource
    | BuiltinNoResource

let builtinResourceRole (kind: CoreBuiltinKind) =
    match kind with
        | CoreFileReadChunk -> BuiltinUsesResource
        | CoreFileReadLine -> BuiltinUsesResource
        | CoreFileClose -> BuiltinClosesResource
        | CoreProcessWriteStdin -> BuiltinUsesResource
        | CoreProcessReadStdoutLine -> BuiltinUsesResource
        | CoreProcessReadStderrLine -> BuiltinUsesResource
        | CoreProcessWaitForExit -> BuiltinUsesResource
        | CoreProcessKill -> BuiltinUsesResource
        | CoreTcpSend -> BuiltinUsesResource
        | CoreTcpReceive -> BuiltinUsesResource
        | CoreTcpAccept -> BuiltinUsesResource
        | CoreTcpClose -> BuiltinClosesResource
        | CoreTlsSend -> BuiltinUsesResource
        | CoreTlsReceive -> BuiltinUsesResource
        | CoreTlsClose -> BuiltinClosesResource
        | CoreTlsServerHandshake -> BuiltinUsesResource
        | _ -> BuiltinNoResource

// Which of a let-bound lambda's parameters a call through it only borrows: stage 0's parameter
// ownership summary, computed from the lambda's body. An unknown callee consumes everything.
// The parameters and innermost body recorded under a let-bound lambda's name, the latest
// recording of the name winning as the list it indexes is searched newest first.
let lookupLetLambdaNamed (name: Str) (state: CoreLoweringState) =
    state
    |> stateLetLambdasByName
    |> Ashes.Collection.Map.getStr(name)

let recursive markConsumedArguments (arguments: List(Expr)) (index: Int) (ownership: List((Str, ParameterOwnership))) (state: CoreLoweringState) =
    match arguments with
        | [] -> state
        | argument :: rest ->
            match parameterAtIndex(index)(ownership) with
                | Borrowed -> markConsumedArguments(rest)(index + 1)(ownership)(state)
                | Consumed ->
                    state
                    |> markResourceArgumentMoved(argument)
                    |> markConsumedArguments(rest)(index + 1)(ownership)

// Positionally overlays the whole-program verdict on the single-function one: a parameter the
// fixpoint proved borrowed stays a borrow where the single-function summary saw a consuming
// hand-off; every other parameter keeps its single-function classification.
let recursive overlayProvenBorrows (proven: List((Str, ParameterOwnership))) (ownership: List((Str, ParameterOwnership))) =
    match (proven, ownership) with
        | ((_provenParameter, Borrowed) :: provenRest, (parameter, _kind) :: rest) -> (parameter, Borrowed) :: overlayProvenBorrows(provenRest)(rest)
        | (_provenEntry :: provenRest, entry :: rest) -> entry :: overlayProvenBorrows(provenRest)(rest)
        | (_, remaining) -> remaining

let recursive sameParameterNames (proven: List((Str, ParameterOwnership))) (parameters: List(Str)) =
    match (proven, parameters) with
        | ([], []) -> true
        | ((provenParameter, _kind) :: provenRest, parameter :: rest) -> provenParameter == parameter && sameParameterNames(provenRest)(rest)
        | _ -> false

let registeredTopLevelName (name: Str) (names: MapTree(Str, Bool)) =
    match Ashes.Collection.Map.getStr(name)(names) with
        | Some(_declared) -> true
        | None -> false

// Stage 0's open-world hand-off approval: the whole-program inspect-only fixpoint
// (`inferProgramParameterOwnership`) is consulted for a callee that is a registered top-level
// function whose parameter chain is the one the fixpoint classified, so a hand-off to a proven
// inspecting helper stays a borrow; a callee the fixpoint did not resolve (a local lambda, or a
// name shadowing a registered function with a different parameter chain) keeps the
// single-function verdict.
let provenParameterOwnership (callee: Str) (parameters: List(Str)) (ownership: List((Str, ParameterOwnership))) (state: CoreLoweringState) =
    match state
    |> stateProgramParameterOwnership
    |> lookupIndexedProgramParameterOwnership(callee) with
        | Some(proven) ->
            if registeredTopLevelName(callee)(stateTopLevelNames(state)) && sameParameterNames(proven)(parameters)
            then overlayProvenBorrows(proven)(ownership)
            else ownership
        | None -> ownership

// Stage 0's per-argument `borrowsOnly` decision for a general call: an argument naming a live
// resource moves unless the callee is a let-bound lambda proven to only read that parameter,
// by its own summary or by the whole-program fixpoint.
let markCallArgumentsMoved (spine: CoreCallSpine) (state: CoreLoweringState) =
    match unspanArgument(spine.root) with
        | ExprVar(callee) ->
            match lookupLetLambdaNamed(callee)(state) with
                | Some((parameters, body)) ->
                    state
                    |> provenParameterOwnership(callee)(parameters)(classifyParameterOwnership(parameters)(body)([]))
                    |> (given (ownership) -> markConsumedArguments(spine.arguments)(0)(ownership)(state))
                | None -> markResourceArgumentsMoved(spine.arguments)(state)
        | _ -> markResourceArgumentsMoved(spine.arguments)(state)

// The generated label recorded under a let-bound function's name, once its body is lowered.
let lookupLetLambdaLabel (name: Str) (labels: MapTree(Str, Str)) = Ashes.Collection.Map.getStr(name)(labels)

let recursive lookupClosureDropperLabel (name: Str) (labels: List((Str, Str))) =
    match labels with
        | [] -> None
        | (candidate, label) :: rest ->
            if candidate == name
            then Some(label)
            else lookupClosureDropperLabel(name)(rest)

let markLoweredCallArgumentsMoved (spine: CoreCallSpine) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { error = Some(_error) } -> lowered
        | LoweredCoreValue { state = state } -> lowered with state = markCallArgumentsMoved(spine)(state)

// Stage 0's `EmitResourceCleanup`: a live resource binding is closed at its scope exit by a
// `CleanupResource` naming its type and, for a declared external resource, its destructor; a
// closed or moved one is left alone.
let emitResourceCleanup (typeName: Str) (slot: Int) (state: CoreLoweringState) =
    match resourceStateOf(slot)(state) with
        | Some(_kind) -> state
        | None ->
            match freshTemp(state) with
                | FreshTemp { state = loadState, temp = loadTemp } ->
                    loadState
                    |> emit(LoadLocal(loadTemp)(slot))
                    |> emit(state
                    |> declaredResourceDestructor(typeName)
                    |> CleanupResource(loadTemp)(typeName))

// Stage 0's closure-capture transfer for resources: a live resource a closure captures is
// reachable through the closure after this scope, so it moves into the closure instead of being
// closed at the scope exit.
let recursive markCapturedResourcesMoved (captures: List(CoreBinding)) (state: CoreLoweringState) =
    match captures with
        | [] -> state
        | CoreBinding { name = name } :: rest ->
            match resourceBindingOf(name)(state) with
                | Some((slot, _typeName)) ->
                    match resourceStateOf(slot)(state) with
                        | None ->
                            state
                            |> markResourceReleased(slot)(ResourceMoved)
                            |> markCapturedResourcesMoved(rest)
                        | Some(_kind) -> markCapturedResourcesMoved(rest)(state)
                | None -> markCapturedResourcesMoved(rest)(state)

// Stage 0's compiler-inferred borrowing (`LowerVar`): reading an owned binding yields a `Borrow`
// alias of the loaded value, so the owning scope keeps the drop obligation. Ownership is decided
// from the binding's resolved type at the read, since a pattern binding's type is a fresh variable
// when it is bound.
// The read of a reference-counted `let` binding in the position its scope hands it out through
// takes the slot's reference: the temp is newly produced for its consumer and the slot no longer
// owns anything at the scope exit.
let transfersSlot (slot: Int) (state: CoreLoweringState) =
    match consumerRequestOf(state) with
        | ConsumerRequest { transferSlot = Some(transfer) } -> transfer == slot
        | _ -> false

let recursive releaseRuntimeOwner (slot: Int) (owners: List((Int, Bool))) =
    match owners with
        | [] -> []
        | (candidate, owned) :: rest ->
            if candidate == slot
            then (candidate, false) :: rest
            else (candidate, owned) :: releaseRuntimeOwner(slot)(rest)

let transferRuntimeOwner (slot: Int) (temp: Int) (state: CoreLoweringState) =
    state
    |> markRuntimeTemp(temp)(RuntimeNewlyProduced)
    |> (given (transferred: CoreLoweringState) ->
        withStateRuntimeOwners(transferred
        |> stateRuntimeOwners
        |> releaseRuntimeOwner(slot))(transferred))

// An owner released at the end of its scope is retired: a loop's back edge releases every owner
// still held, and one whose scope closed before the self call (the adopted scrutinee of an arm,
// or a `let` in an operand of the self call) was released already. Stage 0's back edge sees only
// the ownership scopes still open.
let retireRuntimeOwner (slot: Int) (state: CoreLoweringState) =
    withStateRuntimeOwners(state
    |> stateRuntimeOwners
    |> releaseRuntimeOwner(slot))(state)

let recursive lookupRuntimeOwner (slot: Int) (owners: List((Int, Bool))) =
    match owners with
        | [] -> None
        | (candidate, owned) :: rest ->
            if candidate == slot
            then Some(owned)
            else lookupRuntimeOwner(slot)(rest)

let runtimeOwnerStateOf (slot: Int) (state: CoreLoweringState) =
    state
    |> stateRuntimeOwners
    |> lookupRuntimeOwner(slot)

let recursive lookupOwnerAlias (slot: Int) (aliases: List((Int, Int))) =
    match aliases with
        | [] -> None
        | (candidate, ownerSlot) :: rest ->
            if candidate == slot
            then Some(ownerSlot)
            else lookupOwnerAlias(slot)(rest)

// The slot of the owner whose reference-counted value a local binding reads: the binding's own
// slot when it still owns one, or the still-live owner a pattern binding aliases.
let liveRuntimeOwnerSlot (name: Str) (state: CoreLoweringState) =
    match lookupBinding(name)(state.bindings) with
        | Some(CoreBinding { location = CoreLocal(slot) }) ->
            match runtimeOwnerStateOf(slot)(state) with
                | Some(true) -> Some(slot)
                | Some(false) -> None
                | None ->
                    match state
                    |> stateRuntimeOwnerAliases
                    |> lookupOwnerAlias(slot) with
                        | Some(ownerSlot) ->
                            match runtimeOwnerStateOf(ownerSlot)(state) with
                                | Some(true) -> Some(ownerSlot)
                                | _ -> None
                        | None -> None
        | _ -> None

// Stage 0's `DuplicateRuntimeManagedOwnedValueForTransfer`: the read of a live owner that an
// aggregate carries out of the owner's scope is retained before the owner's own scope-exit
// release. A list-typed owner may hold the empty list, whose null representation has no header,
// so its retain is guarded (`MayBeEmpty`). The duplicate is a newly produced reference.
let mayBeEmptyList (semanticType: SemanticType) =
    match semanticType with
        | SemList(_element) -> true
        | _ -> false

let emitTransferRetain (temp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = allocated, temp = duplicate } ->
            allocated
            |> emit(semanticType
            |> resolveType(allocated)
            |> mayBeEmptyList
            |> RcDup(duplicate)(temp)(true))
            |> markRuntimeTemp(duplicate)(RuntimeNewlyProduced)
            |> success(duplicate)(semanticType)

// The fact belongs to this lexical binding, including when a later binder shadows its name.
let patternBindingFact (name: Str) (state: CoreLoweringState) =
    match lookupBinding(name)(state.bindings) with
        | Some(CoreBinding { patternFact = fact }) -> fact
        | None -> None

let patternOwnerBinding (name: Str) (state: CoreLoweringState) =
    match patternBindingFact(name)(state) with
        | Some(fact) when patternFactRequiresProtection(fact) -> Some(fact)
        | _ -> None

let patternOwnerFlag (fact: Maybe(PatternBindingFact)) =
    match fact with
        | Some(fact) -> patternFactRequiresProtection(fact)
        | None -> false

// Stage 0's `DuplicatePerceusPatternOwnerForAggregate`: a tail self-call argument or aggregate
// child that reads a pattern owner takes a duplicate of that read — an identity marker until the
// loop's finalize pass turns it into a real retain for a pattern owner it places on the
// reference-counted heap.
// A pattern-owner duplicate forwards the runtime fact of the value it duplicates, as stage 0's
// `PropagateRcDupOwnership` forwards an identity marker's source ownership: the duplicate of a
// head the cell's normalization copied into a fresh reference-counted value is that value, and
// the cell built on it is reference-counted.
let forwardRuntimeTemp (sourceTemp: Int) (targetTemp: Int) (state: CoreLoweringState) =
    match runtimeTempStateOf(sourceTemp)(state) with
        | Some(runtimeState) -> markRuntimeTemp(targetTemp)(runtimeState)(state)
        | None -> state

let duplicatePatternOwnerTemp (child: Expr) (temp: Int) (state: CoreLoweringState) =
    match unspanArgument(child) with
        | ExprVar(name) ->
            match patternOwnerBinding(name)(state) with
                | Some(_fact) ->
                    if state
                    |> statePatternOwnerCopyTemps
                    |> containsInt(temp)
                    then (state, temp)
                    else
                        match freshTemp(state) with
                            | FreshTemp { state = allocated, temp = duplicate } ->
                                (allocated
                                |> emit(RcDup(duplicate)(temp)(false)(false))
                                |> forwardRuntimeTemp(temp)(duplicate), duplicate)
                | None -> (state, temp)
        | _ -> (state, temp)

let duplicatePatternOwnerChild (child: Expr) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            match duplicatePatternOwnerTemp(child)(temp)(state) with
                | (duplicated, duplicate) -> success(duplicate)(semanticType)(duplicated)
        | _ -> lowered

let emitBorrowedRead temp semanticType state =
    match freshTemp(state) with
        | FreshTemp { state = borrowState, temp = borrowTemp } ->
            borrowState
            |> emit(Borrow(borrowTemp)(temp))
            |> success(borrowTemp)(semanticType)

// A pattern owner's read borrows whatever its type (stage 0's `LowerVar` borrows every binding
// it finds an ownership entry for); any other owned binding borrows once its type is heap-represented.
let finishOwnedRead ownedRead (patternOwner: Bool) temp semanticType state =
    if patternOwner
    then emitBorrowedRead(temp)(semanticType)(state)
    else
        if ownedRead
        then
            match stateOwnedTypeNameOf(resolveType(state)(semanticType))(state) with
                | Some(_typeName) -> emitBorrowedRead(temp)(semanticType)(state)
                | None -> success(temp)(semanticType)(state)
        else success(temp)(semanticType)(state)

// A pattern binding of the arm that published a reuse token reads the cell the token released:
// stage 0 aliases such a binding to the dead scrutinee owner, whose read no longer borrows.
let recursive isReuseTransferredName (name: Str) (names: List(Str)) =
    match names with
        | [] -> false
        | candidate :: rest -> candidate == name || isReuseTransferredName(name)(rest)

let recursive lowerVariable name state =
    match state with
        | CoreLoweringState { bindings = bindings } ->
            match lookupBinding(name)(bindings) with
                | None -> failure(state)(UnknownLoweringBinding(name))
                | Some(binding) -> lowerBoundVariable(binding)(state)
and lowerBoundVariable binding state =
    match bindingRequirements(binding) with
        | [] -> lowerUnconstrainedBoundVariable(binding)(state)
        | requirements ->
            match state
            |> stateTraitEnvironment
            |> coreConstrainedLayout(requirements) with
                | CoreConstrainedLayout { parameterTypes = parameters, error = None } ->
                    let scheme = binding.scheme
                    in lowerUnconstrainedBoundVariable((binding with scheme = (scheme with body = coreConstrainedPhysicalType(parameters)(scheme.body), constraints = []), closedScheme = false))(state)
                | CoreConstrainedLayout { error = Some(error) } ->
                    SemNever
                    |> UnsupportedCoreTraitDispatch(error)
                    |> failure(state)
and lowerUnconstrainedBoundVariable binding state =
    match instantiateBinding(binding)(state) with
        | InstantiatedBinding { state = instantiatedState, semanticType = semanticType } ->
            match binding with
                | CoreBinding { location = CoreSelf(label, environmentSize) } ->
                    match freshTemp(instantiatedState) with
                        | FreshTemp { state = closureAllocated, temp = closureTemp } ->
                            match freshTemp(closureAllocated) with
                                | FreshTemp { state = closureState, temp = environmentTemp } ->
                                    closureState
                                    |> emit(LoadLocal(environmentTemp)(0))
                                    |> emit(MakeClosure(
                                        closureTemp,
                                        label,
                                        environmentTemp,
                                        environmentSize,
                                        false,
                                        false,
                                        false,
                                        false
                                    ))
                                    |> success(closureTemp)(semanticType)
                | CoreBinding { name = name, location = CoreLocal(slot), ownedRead = ownedRead, patternFact = patternOwner } ->
                    match freshTemp(instantiatedState) with
                        | FreshTemp { state = tempState, temp = temp } ->
                            if transfersSlot(slot)(tempState)
                            then
                                tempState
                                |> emit(LoadLocal(temp)(slot))
                                |> transferRuntimeOwner(slot)(temp)
                                |> success(temp)(semanticType)
                            else
                                tempState
                                |> emit(LoadLocal(temp)(slot))
                                |> finishOwnedRead(ownedRead && isReuseTransferredName(name)(stateReuseTransferredNames(tempState)) == false)(patternOwnerFlag(patternOwner))(temp)(semanticType)
                | CoreBinding { location = CoreEnvironment(index), ownedRead = ownedRead } ->
                    match freshTemp(instantiatedState) with
                        | FreshTemp { state = tempState, temp = temp } ->
                            tempState
                            |> emit(LoadEnv(temp)(index))
                            |> finishOwnedRead(ownedRead)(false)(temp)(semanticType)

let addBinding name scheme location state =
    match state with
        | CoreLoweringState { bindings = bindings } ->
            let binding = CoreBinding(name = name, scheme = scheme, location = location, ownedRead = false, patternFact = None, closedScheme = schemeIsClosed(scheme))
            in state with bindings = binding :: bindings

let addOwnedBinding name scheme location state =
    match state with
        | CoreLoweringState { bindings = bindings } ->
            let binding = CoreBinding(name = name, scheme = scheme, location = location, ownedRead = true, patternFact = None, closedScheme = schemeIsClosed(scheme))
            in state with bindings = binding :: bindings

// Remember the exact binder's classification even when it needs no protective reference.
let addPatternBinding name scheme location (fact: PatternBindingFact) state =
    match state with
        | CoreLoweringState { bindings = bindings } ->
            let binding = CoreBinding(name = name, scheme = scheme, location = location, ownedRead = true, patternFact = Some(fact), closedScheme = schemeIsClosed(scheme))
            in state with bindings = binding :: bindings

let restoreBindings bindings (state: CoreLoweringState) = state with bindings = bindings

let restoreLoweredBindings outerBindings lowered =
    match lowered with
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = error } ->
            LoweredCoreValue(
                state = restoreBindings(outerBindings)(state),
                temp = temp,
                semanticType = semanticType,
                error = error
            )

// The type variables still owed a deferred `+` resolution must never be generalized: a scheme
// quantifying one hands every call site a fresh instantiation, leaving the recorded variable
// unresolvable and the speculative `AddInt` grounded to the wrong form. The pending types —
// resolved first, since the recorded variable may already stand for another — are bundled into
// one unquantified scheme treated as part of the environment, keeping them monomorphic so the
// first concrete use (a call argument, a `""` seed) binds them for the whole group.
let recursive resolvedPendingOperatorTypes state types acc =
    match types with
        | [] -> acc
        | semanticType :: rest -> resolvedPendingOperatorTypes(state)(rest)(resolveType(state)(semanticType) :: acc)

let recursive pendingOperatorTypes pending acc =
    match pending with
        | [] -> acc
        | (_target, semanticType) :: rest -> pendingOperatorTypes(rest)(semanticType :: acc)

let recursive sealedOperatorTypes sealed acc =
    match sealed with
        | [] -> acc
        | (_label, _target, semanticType) :: rest -> sealedOperatorTypes(rest)(semanticType :: acc)

let recursive resolvedAmbientAnswer operand candidates state =
    match candidates with
        | [] -> None
        | (binding, candidate) :: rest ->
            if resolveType(state)(candidate) == operand
            then Some(ProvenAmbientMethod(binding))
            else resolvedAmbientAnswer(operand)(rest)(state)

// A local eager result stays monomorphic while it is unresolved. Once the body
// has unified it with a dictionary in scope at the original use, replay can
// build it per invocation; this obligation no longer restricts the enclosing scheme.
let recursive pendingTraitGoalTypes state goals acc =
    match goals with
        | [] -> acc
        | (_key, semanticType, []) :: rest -> pendingTraitGoalTypes(state)(rest)(semanticType :: acc)
        | (_key, semanticType, candidates) :: rest ->
            match resolvedAmbientAnswer(resolveType(state)(semanticType))(candidates)(state) with
                | Some(_proof) -> pendingTraitGoalTypes(state)(rest)(acc)
                | None -> pendingTraitGoalTypes(state)(rest)(semanticType :: acc)

// The types a generalization must hold back, in the environment position that keeps them out of
// the quantifier: a deferred operator's operand, and the operand of a trait goal this pass could
// not answer. Quantifying either would leave the binding polymorphic in exactly the variable a
// later use is what settles, and the site that waits on it would never learn the type.
let pendingOperatorScheme state =
    match state with
        | CoreLoweringState { functionState = CoreFunctionState { pendingOperatorDefaults = pending, sealedOperatorDefaults = sealed, pendingTraitGoals = goals } } ->
            TypeScheme(
                quantified = [],
                body = []
                |> resolvedPendingOperatorTypes(state)([]
                |> pendingTraitGoalTypes(state)(goals)
                |> sealedOperatorTypes(sealed)
                |> pendingOperatorTypes(pending))
                |> SemTuple,
                constraints = []
            )

// Generalizes an already resolved binding type against the outer bindings. A type with no free
// variables generalizes to itself whatever the environment holds, so the outer bindings are only
// resolved when there is a candidate variable to hold back.
let generalizeResolvedType (outerBindings: List(CoreBinding)) (state: CoreLoweringState) (resolvedType: SemanticType) =
    match freeTypeVariables(resolvedType) with
        | [] -> generalize([])(resolvedType)([])
        | _ -> generalize(pendingOperatorScheme(state) :: resolvedBindingSchemes(outerBindings)(state))(resolvedType)([])

// A `let` bound to a newly produced reference-counted value owns it: the value temp is handed on
// to the slot, and a body that returns the binding itself (through nested lets) hands the slot's
// reference on again instead of borrowing it, stage 0's tail-forwarded binding result.
// The release plan armed for the binding being adopted: what its value expression proved about
// the graph's uniqueness and its constructor (`armOwnerReleasePlan`), or nothing for a value the
// `let` paths did not classify.
let takePendingOwnerPlan (state: CoreLoweringState) =
    match statePendingOwnerPlan(state) with
        | Some(plan) -> (plan, withStatePendingOwnerPlan(None)(state))
        | None -> (OwnedReleasePlan(deepUnique = false, constructorName = None, matchFieldTransfer = None), state)

let adoptRuntimeLetValue (valueTemp: Int) (slot: Int) (valueType: SemanticType) (state: CoreLoweringState) =
    match runtimeTempStateOf(valueTemp)(state) with
        | Some(RuntimeNewlyProduced) ->
            match takePendingOwnerPlan(state) with
                | (plan, planned) ->
                    planned
                    |> markRuntimeTemp(valueTemp)(RuntimeTransferred)
                    |> (given (adopted: CoreLoweringState) ->
                        adopted
                        |> withStateRuntimeOwners((slot, true) :: stateRuntimeOwners(adopted))
                        |> withStateOwnerReleasePlans((slot, valueType, plan) :: stateOwnerReleasePlans(adopted)))
        | _ -> withStatePendingOwnerPlan(None)(state)

// The name a body returns through its `let` chain, when the body is a plain read.
let recursive tailForwardedVariable (body: Expr) =
    match body with
        | ExprAt(_span, inner) -> tailForwardedVariable(inner)
        | ExprNode(_node, inner) -> tailForwardedVariable(inner)
        | ExprVar(name) -> Some(name)
        | ExprLet(nested, _value, nestedBody, _parameters, _annotation, _requirements) ->
            match tailForwardedVariable(nestedBody) with
                | Some(name) ->
                    if name == nested
                    then None
                    else Some(name)
                | None -> None
        | _ -> None

let recursive forwardedBindingContains (name: Str) (forwarded: List(Str)) =
    match forwarded with
        | [] -> false
        | candidate :: rest -> candidate == name || forwardedBindingContains(name)(rest)

// Whether a binding's value is just one of the forwarded names, which makes it an alias of the same
// reference rather than a new owner.
let recursive forwardedBindingAliasesValue (value: Expr) (forwarded: List(Str)) =
    match value with
        | ExprAt(_span, inner) -> forwardedBindingAliasesValue(inner)(forwarded)
        | ExprNode(_node, inner) -> forwardedBindingAliasesValue(inner)(forwarded)
        | ExprVar(source) -> forwardedBindingContains(source)(forwarded)
        | _ -> false

// Returning an alias still returns the aliased value, so `let a = … in let b = a in b` forwards `a`
// out of its scope. Missing that released the value at the chain's exit while the result still
// pointed at it.
let recursive isTailForwardedBindingResultNames (body: Expr) (forwarded: List(Str)) =
    match body with
        | ExprAt(_span, inner) -> isTailForwardedBindingResultNames(inner)(forwarded)
        | ExprNode(_node, inner) -> isTailForwardedBindingResultNames(inner)(forwarded)
        | ExprVar(candidate) -> forwardedBindingContains(candidate)(forwarded)
        | ExprLet(nested, value, nestedBody, _parameters, _annotation, _requirements) ->
            !forwardedBindingContains(nested)(forwarded) && isTailForwardedBindingResultNames(nestedBody)(
                if forwardedBindingAliasesValue(value)(forwarded)
                then nested :: forwarded
                else forwarded
            )
        | ExprLetRecursive(nested, value, nestedBody, _parameters, _annotation, _requirements) ->
            !forwardedBindingContains(nested)(forwarded) && isTailForwardedBindingResultNames(nestedBody)(
                if forwardedBindingAliasesValue(value)(forwarded)
                then nested :: forwarded
                else forwarded
            )
        | _ -> false

let isTailForwardedBindingResult (body: Expr) (name: Str) = isTailForwardedBindingResultNames(body)([name])

// The request the `let`'s body lowers under: `bodyRequest`, the caller's request for the body's
// result, carrying the binding's slot as the transfer slot when the body hands the newly produced
// value straight back. `requestBody` is the body the decision reads; the top-level `let` path lowers
// a placeholder continuation but decides on the program's remaining body.
let letBodyRequest (tailForwarded: Bool) (bodyRequest: ConsumerRequest) (valueTemp: Int) (slot: Int) (state: CoreLoweringState) =
    match (runtimeTempStateOf(valueTemp)(state), tailForwarded) with
        | (Some(RuntimeNewlyProduced), true) -> bodyRequest with transferSlot = Some(slot)
        | _ -> bodyRequest

// The label of the closure defining `temp` when that closure carries no environment.
let recursive emptyEnvironmentClosureLabel (temp: Int) (reversedInstructions: List(IrInstruction)) =
    match reversedInstructions with
        | [] -> None
        | IrInstruction { instruction = MakeClosure(target, label, _environment, size, _runtimeManaged, _returns, _accepts, _returnsOwned) } :: rest ->
            if target == temp
            then
                if size == 0
                then Some(label)
                else None
            else emptyEnvironmentClosureLabel(temp)(rest)
        | IrInstruction { instruction = MakeClosureStack(target, label, _environment, size, _returns, _accepts, _returnsOwned) } :: rest ->
            if target == temp
            then
                if size == 0
                then Some(label)
                else None
            else emptyEnvironmentClosureLabel(temp)(rest)
        | _ :: rest -> emptyEnvironmentClosureLabel(temp)(rest)

// The program entry lowers with no active function origin (a lambda's body enters its own).
let inProgramEntryFrame (state: CoreLoweringState) =
    match stateActiveFunctionOrigin(state) with
        | None -> true
        | Some(IrFunctionOrigin { originKind = ProgramEntryOrigin }) -> true
        | Some(_origin) -> false

// Stage 0's `LowerLetRegisterKnownFunctionIdentity`: a top-level `let` whose value is a closure
// with an empty environment is callable by its label from any scope.
let registerTopLevelFunctionRef (name: Str) (valueTemp: Int) (scheme: TypeScheme) (state: CoreLoweringState) =
    if inProgramEntryFrame(state)
    then
        match emptyEnvironmentClosureLabel(valueTemp)(state.reversedInstructions) with
            | Some(label) -> withRegisteredTopLevelFunctionRef(name)(label)(scheme)(state)
            | None -> state
    else state

// The stored half of a `let`: the value in its slot, the name bound over the body, and the body's
// consumer request armed. The body itself is lowered by the caller over the returned state, which
// takes the binding back out of scope afterwards.
let storeLetValue name (tailForwarded: Bool) bodyRequest outerBindings valueTemp valueType fresh =
    match fresh with
        | FreshLocal { state = state, local = local } ->
            let storedState =
                emit(StoreLocal(local)(valueTemp))(state)
            in
                let scheme =
                    valueType
                    |> resolveType(storedState)
                    |> generalizeResolvedType(outerBindings)(storedState)
                in
                    storedState
                    |> registerTopLevelFunctionRef(name)(valueTemp)(scheme)
                    |> withConsumerRequest(letBodyRequest(tailForwarded)(bodyRequest)(valueTemp)(local)(storedState))
                    |> adoptRuntimeLetValue(valueTemp)(local)(valueType)
                    |> addOwnedBinding(name)(scheme)(CoreLocal(local))

let recursive stripExprAt (expr: Expr) =
    match expr with
        | ExprAt(_span, inner) -> stripExprAt(inner)
        | ExprNode(_node, inner) -> stripExprAt(inner)
        | other -> other

let recursive patternBindsName (name: Str) (pattern: Pattern) =
    match pattern with
        | PatternAt(_span, inner) -> patternBindsName(name)(inner)
        | PatternVar(candidate) -> candidate == name
        | PatternCons(head, tail) -> patternBindsName(name)(head) || patternBindsName(name)(tail)
        | PatternTuple(elements) -> patternsBindName(name)(elements)
        | PatternConstructor(_constructor, arguments) -> patternsBindName(name)(arguments)
        | PatternRecord(_typeName, fields) -> fieldPatternsBindName(name)(fields)
        | PatternAs(inner, alias) -> alias == name || patternBindsName(name)(inner)
        | PatternOr(alternatives) -> patternsBindName(name)(alternatives)
        | _ -> false
and patternsBindName (name: Str) (patterns: List(Pattern)) =
    match patterns with
        | [] -> false
        | pattern :: rest -> patternBindsName(name)(pattern) || patternsBindName(name)(rest)
and fieldPatternsBindName (name: Str) (fields: List((Str, Pattern))) =
    match fields with
        | [] -> false
        | (_field, pattern) :: rest -> patternBindsName(name)(pattern) || fieldPatternsBindName(name)(rest)

// Stage 0's direct-callee analysis for one binding: whether `name` is used in `body` only as the
// callee of an application, the condition under which its lambda value is a stack closure. A
// binding that shadows the name ends the walk of its scope, and an expression form the walk does
// not know counts as a non-callee use, which only ever keeps a closure on the heap.
let recursive nameHasNonCalleeUse (name: Str) (expr: Expr) (asCallee: Bool) =
    match expr with
        | ExprAt(_span, inner) -> nameHasNonCalleeUse(name)(inner)(asCallee)
        | ExprNode(_node, inner) -> nameHasNonCalleeUse(name)(inner)(asCallee)
        | ExprInt(_value) -> false
        | ExprBigInt(_value) -> false
        | ExprUInt(_value, _bitWidth, _suffix) -> false
        | ExprFloat(_value, _suffix) -> false
        | ExprString(_value) -> false
        | ExprRune(_value) -> false
        | ExprBool(_value) -> false
        | ExprVar(candidate) -> candidate == name && asCallee == false
        | ExprQualifiedVar(_moduleName, _memberName) -> false
        | ExprCall(function, argument, _isSugar, _layout) -> nameHasNonCalleeUse(name)(function)(true) || nameHasNonCalleeUse(name)(argument)(false)
        | ExprLambda(parameter, body, _annotation) -> parameter != name && nameHasNonCalleeUse(name)(body)(false)
        | ExprLet(bound, value, body, _parameters, _annotation, _requirements) -> nameHasNonCalleeUse(name)(value)(false) || bound != name && nameHasNonCalleeUse(name)(body)(false)
        | ExprLetResult(bound, value, body) -> nameHasNonCalleeUse(name)(value)(false) || bound != name && nameHasNonCalleeUse(name)(body)(false)
        | ExprLetRecursive(bound, value, body, _parameters, _annotation, _requirements) -> bound != name && (nameHasNonCalleeUse(name)(value)(false) || nameHasNonCalleeUse(name)(body)(false))
        | ExprIf(condition, thenBranch, elseBranch) -> nameHasNonCalleeUse(name)(condition)(false) || nameHasNonCalleeUse(name)(thenBranch)(false) || nameHasNonCalleeUse(name)(elseBranch)(false)
        | ExprAdd(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprSubtract(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprMultiply(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprDivide(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprModulo(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprBitwiseAnd(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprBitwiseOr(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprBitwiseXor(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprShiftLeft(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprShiftRight(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprLogicalAnd(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprLogicalOr(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprGreaterThan(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprLessThan(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprGreaterOrEqual(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprLessOrEqual(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprEqual(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprNotEqual(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprResultPipe(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprResultMapErrorPipe(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprCons(head, tail) -> eitherHasNonCalleeUse(name)(head)(tail)
        | ExprBitwiseNot(operand) -> nameHasNonCalleeUse(name)(operand)(false)
        | ExprLogicalNot(operand) -> nameHasNonCalleeUse(name)(operand)(false)
        | ExprTuple(elements) -> anyHasNonCalleeUse(name)(elements)
        | ExprList(elements, _isMultiline) -> anyHasNonCalleeUse(name)(elements)
        | ExprRecord(_typeName, fields, _isMultiline) -> anyFieldHasNonCalleeUse(name)(fields)
        | ExprRecordUpdate(record, fields) -> nameHasNonCalleeUse(name)(record)(false) || anyFieldHasNonCalleeUse(name)(fields)
        | ExprMatch(scrutinee, arms, _position) -> nameHasNonCalleeUse(name)(scrutinee)(false) || anyArmHasNonCalleeUse(name)(arms)
        | _ -> true
and eitherHasNonCalleeUse (name: Str) (left: Expr) (right: Expr) = nameHasNonCalleeUse(name)(left)(false) || nameHasNonCalleeUse(name)(right)(false)
and anyHasNonCalleeUse (name: Str) (expressions: List(Expr)) =
    match expressions with
        | [] -> false
        | expression :: rest -> nameHasNonCalleeUse(name)(expression)(false) || anyHasNonCalleeUse(name)(rest)
and anyFieldHasNonCalleeUse (name: Str) (fields: List((Str, Expr))) =
    match fields with
        | [] -> false
        | (_field, expression) :: rest -> nameHasNonCalleeUse(name)(expression)(false) || anyFieldHasNonCalleeUse(name)(rest)
and anyArmHasNonCalleeUse (name: Str) (arms: List((Pattern, Expr, Maybe(Expr)))) =
    match arms with
        | [] -> false
        | (pattern, body, guard) :: rest ->
            if patternBindsName(name)(pattern)
            then anyArmHasNonCalleeUse(name)(rest)
            else guardHasNonCalleeUse(name)(guard) || nameHasNonCalleeUse(name)(body)(false) || anyArmHasNonCalleeUse(name)(rest)
and guardHasNonCalleeUse (name: Str) (guard: Maybe(Expr)) =
    match guard with
        | None -> false
        | Some(condition) -> nameHasNonCalleeUse(name)(condition)(false)

let nameUsedOnlyAsDirectCallee (name: Str) (body: Expr) = nameHasNonCalleeUse(name)(body)(false) == false

// A nested `let`'s arena bracket, stage 0's exact per-binding discipline: `SaveArenaState`
// before the value, and after the whole body the spill of an owned binding's result, its
// release anchor, and the reset the scope rule allows (an inner chained `let` opens and closes
// its own bracket inside this one, so the pairs close LIFO before the enclosing binding's
// store). Every `let` is bracketed; whether its window is reset is decided from the body's type
// when the scope closes, so no syntactic proof is needed up front.
// The owned type name of a lowered `let` value (`ownedTypeNameOf`), `None` when the value is a
// copy type; an owned binding's scope carries a drop obligation the closing bracket discharges.
// The owned type name a `let`'s value releases under, a declared external resource counting as
// owned by its opaque name.
let loweredValueOwnedTypeName lowered =
    match lowered with
        | LoweredCoreValue { state = state, semanticType = semanticType, error = None } ->
            match resolveType(state)(semanticType) with
                | SemOpaque(name) ->
                    if isDeclaredResourceName(name)(state)
                    then Some(name)
                    else None
                | resolved -> state |> stateOwnedTypeNameOf(resolved)
        | _ -> None

let coverageEnvironment (state: CoreLoweringState) = stateCoverageTypes(state)

// Recomputes the declared types' layout facts after a type declaration adds constructor layouts;
// the contract's per-type answers, read from them, are dropped with them.
let withRefreshedHeapLayoutFacts (state: CoreLoweringState) =
    state
    |> withStateHeapLayoutFactsByType(state
    |> stateCoverageTypes
    |> heapLayoutFactsTableFor(stateConstructorLayouts(state)))
    |> withStateGeneralRcTypeFacts(Ashes.Collection.Map.empty)
    |> withStateContractRecordFacts(Ashes.Collection.Map.empty)

// A type declaration adds constructor layouts, so the declared types' layout facts and the
// contract's per-type answers read from them no longer hold; they are classified again once the
// program's declarations are all registered.
let withDroppedHeapLayoutFacts (state: CoreLoweringState) =
    state
    |> withStateHeapLayoutFactsByType(Ashes.Collection.Map.empty)
    |> withStateGeneralRcTypeFacts(Ashes.Collection.Map.empty)
    |> withStateContractRecordFacts(Ashes.Collection.Map.empty)

let heapFactsOf (semanticType: SemanticType) (state: CoreLoweringState) =
    (let resolved = resolveType(state)(semanticType)
    in
        match resolved with
            | SemNamed(_symbolId, name, []) ->
                match state
                |> stateHeapLayoutFactsByType
                |> Ashes.Collection.Map.getStr(name) with
                    | Some(facts) -> facts
                    | None ->
                        state
                        |> coverageEnvironment
                        |> classifyHeapLayout(resolved)
            | _ ->
                state
                |> coverageEnvironment
                |> classifyHeapLayout(resolved))

// Keep settled monomorphic answers authoritative; uncached types need only the shallow-copy
// decision here, rather than every recursive heap-layout fact.
let cachedCoreShallowCopy semanticType (state: CoreLoweringState) =
    (let facts = coreHeapQueryFacts(semanticType)(state)
    in
        match facts.shallowCopy with
            | Some(answer) -> answer
            | None ->
                state
                |> stateCoverageTypes
                |> classifyHeapShallowCopy(semanticType))

let heapShallowCopyOfIndexed (semanticType: SemanticType) (state: CoreLoweringState) =
    (let resolved = resolveType(state)(semanticType)
    in
        match resolved with
            | SemNamed(_symbolId, name, []) ->
                match state
                |> stateHeapLayoutFactsByType
                |> Ashes.Collection.Map.getStr(name) with
                    | Some(facts) -> (facts.structuralCopy == ShallowCopy, state)
                    | None -> queryCoreShallowCopy(resolved)(state)
            | _ -> queryCoreShallowCopy(resolved)(state))

// Pure readers reuse completed views; stateful consumers retain cold query updates.
let heapShallowCopyOf (semanticType: SemanticType) (state: CoreLoweringState) =
    (let resolved = resolveType(state)(semanticType)
    in
        match resolved with
            | SemNamed(_symbolId, name, []) ->
                match state
                |> stateHeapLayoutFactsByType
                |> Ashes.Collection.Map.getStr(name) with
                    | Some(facts) -> facts.structuralCopy == ShallowCopy
                    | None -> cachedCoreShallowCopy(resolved)(state)
            | _ -> cachedCoreShallowCopy(resolved)(state))

// Preserve the same settled monomorphic cache answer as heapFactsOf. An uncached resource
// query needs only the resource walk, not unrelated copy/drop layout classification.
let heapResourceOf (semanticType: SemanticType) (state: CoreLoweringState) =
    (let resolved = resolveType(state)(semanticType)
    in
        match resolved with
            | SemNamed(_symbolId, name, []) ->
                match state
                |> stateHeapLayoutFactsByType
                |> Ashes.Collection.Map.getStr(name) with
                    | Some(facts) -> facts.containsResource
                    | None ->
                        state
                        |> coverageEnvironment
                        |> classifyHeapResource(resolved)
            | _ ->
                state
                |> coverageEnvironment
                |> classifyHeapResource(resolved))

let recursive lookupOwnerReleasePlan (slot: Int) (plans: List((Int, SemanticType, OwnedReleasePlan))) =
    match plans with
        | [] -> None
        | (candidate, semanticType, plan) :: rest ->
            if candidate == slot
            then Some((semanticType, plan))
            else lookupOwnerReleasePlan(slot)(rest)

let recursive emitSplicedInstructions (instructions: List(IrInstructionKind)) (state: CoreLoweringState) =
    match instructions with
        | [] -> state
        | instruction :: rest ->
            state
            |> emit(instruction)
            |> emitSplicedInstructions(rest)

// A synthesized dropper's or copier's instructions carry the location of the release or copy
// that synthesized it, the way stage 0 emits them under the site's span; runtime machinery
// stays unlocated.
let locateSynthesizedInstruction (state: CoreLoweringState) (instruction: IrInstruction) =
    match (state, instruction) with
        | (CoreLoweringState { currentSpan = span, programState = CoreProgramState { sourceContext = context }, functionState = CoreFunctionState { currentItem = item } }, IrInstruction { instruction = kind }) -> tagItemInstruction(kind)(span)(item)(context)

let locateSynthesizedFunction (state: CoreLoweringState) (function: IrFunction) =
    function with instructions = map(locateSynthesizedInstruction(state))(function.instructions)

// Splices a synthesized inline release into the current function: the instructions are emitted
// under the current span, and the counters, dropper cache, and dropper functions the synthesis
// advanced are carried into the state.
let spliceInlineRelease (synthesis: InlineReleaseSynthesis) (state: CoreLoweringState) =
    match synthesis with
        | InlineReleaseSynthesis { instructions = instructions, nextTemp = nextTemp, nextLocal = nextLocal, cache = cache, functions = synthesized, nextLambdaId = nextLambdaId, nextLabelId = nextLabelId } ->
            (state with nextTemp = nextTemp, nextLocal = nextLocal, functions = append(synthesized
            |> map(locateSynthesizedFunction(state))
            |> reverse)(state.functions), nextLambdaId = nextLambdaId, nextLabelId = nextLabelId)
            |> withStateDropperResult(cache)(synthesis.queryContext)
            |> emitSplicedInstructions(instructions)

// The scope-exit release of a runtime-managed owner whose release reaches past its own cell —
// a tuple, a list, or an ADT with owned children — walked inline at the lexical scope exit as
// stage 0's `EmitOwnedValueDrop` does (the walk is not one placeable instruction); `None` for an
// owner whose release is a single allocation.
let emitInlineOwnerRelease (loadTemp: Int) (ownerSlot: Int) (state: CoreLoweringState) =
    match state
    |> stateOwnerReleasePlans
    |> lookupOwnerReleasePlan(ownerSlot) with
        | None ->
            let types = stateDropperTypes(state)
            in (None, dropperQueryContextOf(types))
        | Some((semanticType, plan)) ->
            match state with
                | CoreLoweringState { nextTemp = nextTemp, nextLocal = nextLocal, nextLambdaId = lambdaId, nextLabelId = labelId, programState = CoreProgramState { dropperLabels = cache } } ->
                    match synthesizeOwnedAggregateReleaseIndexed(loadTemp)(resolveType(state)(semanticType))(plan)(dropperTypesOf(state))(cache)(nextTemp)(nextLocal)(lambdaId)(labelId) with
                        | (None, queryContext) -> (None, queryContext)
                        | (Some(synthesis), queryContext) ->
                            (state
                            |> spliceInlineRelease(synthesis)
                            |> Some, queryContext)

let emitOwnerDrop typeName ownerSlot loadTemp (runtimeOwner: Bool) (loaded: CoreLoweringState) =
    if typeName == "Function"
    then
        emit(CleanupResource(loadTemp)(typeName)(None))(loaded)
    else
        match (runtimeOwner, emitInlineOwnerRelease(loadTemp)(ownerSlot)(loaded)) with
            | (true, (Some(released), _queryContext)) -> released
            | (_runtimeOwner, (_release, queryContext)) ->
                loaded
                |> withStateDropperContext(queryContext)
                |> emit(RcDrop(loadTemp)(typeName)(ownerSlot)(runtimeOwner)(false)(None))

// The lexical release of an owned `let` binding at its scope exit, stage 0's
// `EmitOwnedValueDrop`: the owner is loaded back and released with an `RcDrop` naming its slot,
// which `PerceusLifetimePlacement` later moves to the binding's last use on each path, or with
// the inline walk of a runtime-managed aggregate. A closure is released with `CleanupResource`
// instead, which stays at the scope exit.
let emitOwnedLetRelease typeName ownerSlot state =
    if isResourceTypeNameIn(typeName)(state)
    then emitResourceCleanup(typeName)(ownerSlot)(state)
    else
        match runtimeOwnerStateOf(ownerSlot)(state) with
            | Some(false) -> state
            | runtimeOwner ->
                match freshTemp(state) with
                    | FreshTemp { state = loadState, temp = loadTemp } ->
                        loadState
                        |> emit(LoadLocal(loadTemp)(ownerSlot))
                        |> emitOwnerDrop(typeName)(ownerSlot)(loadTemp)(runtimeOwner == Some(true))

let emitRestoreAndReclaim cursorSlot endSlot preRestoreSlot state =
    state
    |> emit(RestoreArenaState(cursorSlot)(endSlot)(preRestoreSlot)(false))
    |> emit(ReclaimArenaChunks(endSlot)(preRestoreSlot)(false))

// A type variable that a deferred `+` default owns resolves to `Int` at finalization, the type
// the deferred add itself falls back to.
let recursive operatorDefaultedVariables (types: List(SemanticType)) (state: CoreLoweringState) =
    match types with
        | [] -> []
        | semanticType :: rest ->
            match resolveType(state)(semanticType) with
                | SemVariable(id) -> id :: operatorDefaultedVariables(rest)(state)
                | _ -> operatorDefaultedVariables(rest)(state)

// Whether a scope's or a call's result survives the arena reset that closes it: stage 0's
// `CanArenaReset`, a scalar seen through a zero-cost wrapper. A type variable that a deferred `+`
// default owns is the `Int` that default resolves to, which stage 0 already knows at that point.
let resultSurvivesReset (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemVariable(id) ->
            []
            |> sealedOperatorTypes(stateSealedOperatorDefaults(state))
            |> pendingOperatorTypes(statePendingOperatorDefaults(state))
            |> (given (types) ->
                state
                |> operatorDefaultedVariables(types)
                |> containsInt(id))
        | SemNamed(_symbolId, name, _arguments) ->
            match state
            |> stateConstructorsOfType(name)
            |> zeroCostPayloadType(name) with
                | Some(payload) ->
                    payload
                    |> resolveType(state)
                    |> canArenaResetLayout
                | None -> false
        // Stage 0's `IsOpaqueHandleWord`: a value of an external type with no destructor is a
        // foreign handle the program only passes along, one word copied like an integer that
        // needs no release; a declared resource owns what its handle names.
        | SemOpaque(name) -> isDeclaredResourceName(name)(state) == false
        | resolved -> canArenaResetLayout(resolved)

// Whether a loop argument survives the per-iteration arena reset at a back edge. A type variable
// only the current body's still-pending deferred operator owns may yet settle on `Str` at a later
// call (`grow(s + s)(n - 1)` applied to a string), which turns the speculative `AddInt` into an
// arena concatenation; resetting under it would free the successor the next iteration reads, so
// only a sealed default counts as the `Int` it resolves to here.
let backEdgeArgumentSurvivesReset (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemVariable(id) ->
            []
            |> sealedOperatorTypes(stateSealedOperatorDefaults(state))
            |> (given (types) ->
                state
                |> operatorDefaultedVariables(types)
                |> containsInt(id))
        | _ -> resultSurvivesReset(semanticType)(state)

// A live owner retained into a runtime cell shares its graph with the cell from then on, so its
// scope-exit release may no longer assume the graph is unique.
let recursive shareOwnerReleasePlan (slot: Int) (plans: List((Int, SemanticType, OwnedReleasePlan))) =
    match plans with
        | [] -> []
        | (candidate, semanticType, plan) :: rest ->
            if candidate == slot
            then (candidate, semanticType, (plan with deepUnique = false)) :: rest
            else (candidate, semanticType, plan) :: shareOwnerReleasePlan(slot)(rest)

// The type an owner slot's value was adopted with, from its release plan.
let recursive ownerValueTypeOf (slot: Int) (plans: List((Int, SemanticType, OwnedReleasePlan))) =
    match plans with
        | [] -> None
        | (candidate, semanticType, _plan) :: rest ->
            if candidate == slot
            then Some(semanticType)
            else ownerValueTypeOf(slot)(rest)

// The runtime-managed owners an arena binding's aggregate borrows a child of, recorded when the
// binding was stored (stage 0's `OwnershipInfo.BorrowedRuntimeOwners`).
let recursive borrowedOwnersOfSlot (slot: Int) (entries: List((Int, List((Int, SemanticType))))) =
    match entries with
        | [] -> []
        | (candidate, owners) :: rest ->
            if candidate == slot
            then owners
            else borrowedOwnersOfSlot(slot)(rest)

// Stage 0's `RetainBorrowedRuntimeOwnersOfAlias`: a transferred read of an arena binding that
// borrows runtime-managed owners retains each owner still live, as the literal's own transfer
// would have; the owner's graph is shared with the aggregate from then on.
let recursive retainBorrowedOwners (owners: List((Int, SemanticType))) (state: CoreLoweringState) =
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
                                    |> (given (retained: CoreLoweringState) ->
                                        withStateOwnerReleasePlans(retained
                                        |> stateOwnerReleasePlans
                                        |> shareOwnerReleasePlan(slot))(retained))
                                    |> retainBorrowedOwners(rest)
                | _ -> retainBorrowedOwners(rest)(state)

let retainAliasBorrowedOwners (name: Str) (state: CoreLoweringState) =
    match lookupBinding(name)(state.bindings) with
        | Some(CoreBinding { location = CoreLocal(slot) }) ->
            match runtimeOwnerStateOf(slot)(state) with
                | Some(_owned) -> state
                | None ->
                    retainBorrowedOwners(state
                    |> stateBorrowedOwners
                    |> borrowedOwnersOfSlot(slot))(state)
        | _ -> state

// A child an aggregate stores, or a tail self-call argument, retained when the context asked for
// the transfer and the child is the read of a binding that still owns its reference, whole or by
// a heap-typed field (a borrow of a child the owner releases with itself); a read of an arena
// binding that borrows owners retains those owners instead; every other child is stored as
// lowered. A pattern owner is not retained here (stage 0's `DuplicateRuntimeManagedOwnedValueForTransfer`
// skips it): the pattern-owner duplicate taken of its read is the reference the child carries.
let retainTransferredChild (child: Expr) (transfers: Bool) (lowered: LoweredCoreValue) =
    match (transfers, unspanArgument(child), lowered) with
        | (true, ExprVar(name), LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None }) ->
            match (liveRuntimeOwnerSlot(name)(state), patternOwnerBinding(name)(state)) with
                | (Some(_slot), None) -> emitTransferRetain(temp)(semanticType)(state)
                | _ -> lowered with state = retainAliasBorrowedOwners(name)(state)
        | (true, ExprQualifiedVar(owner, _field), LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None }) ->
            if resultSurvivesReset(semanticType)(state)
            then lowered
            else
                match (liveRuntimeOwnerSlot(owner)(state), patternOwnerBinding(owner)(state)) with
                    | (Some(_slot), None) -> emitTransferRetain(temp)(semanticType)(state)
                    | _ -> lowered
        | _ -> lowered

// Stage 0's `LowerCallArgumentValue`: a plain read passed to a callee whose result may reach it
// retains the owners its arena binding borrows.
let retainAliasArgumentOwners (argument: Expr) (reaches: Bool) (lowered: LoweredCoreValue) =
    match (reaches, unspanArgument(argument), lowered) with
        | (true, ExprVar(name), LoweredCoreValue { state = state, error = None }) -> lowered with state = retainAliasBorrowedOwners(name)(state)
        | _ -> lowered

// The closing reset of a scope: the pre-restore end slot is allocated either way, as stage 0
// does, and the arena is restored and reclaimed only when the scope's result survives it. A heap
// result leaves the window open, since the copy-out that would preserve it is not ported yet.
let closeScopeForResult (resultTemp: Int) (resultType: SemanticType) cursorSlot endSlot state =
    match freshLocal(state) with
        | FreshLocal { state = allocated, local = preRestoreSlot } ->
            if resultSurvivesReset(resultType)(allocated) || isRuntimeTemp(resultTemp)(allocated) || containsInt(resultTemp)(stateBackEdgeDummyTemps(allocated)) || isReuseResultTemp(resultTemp)(allocated)
            then emitGuardedRestoreAndReclaim(cursorSlot)(endSlot)(preRestoreSlot)(allocated)
            else allocated

// What a scope's heap result needs to cross the closing reset, stage 0's `GetCopyOutKind`: a
// string or `Bytes` copies by its dynamic size, a list over scalar elements walks its spine, and a
// named type copies its fixed cell when every constructor has the same scalar-only arity. A
// closure, a tuple, and every other layout have no copy-out and leave the window open.
type ScopeCopyOut =
    | ShallowScopeCopyOut(Int)
    | ListScopeCopyOut

let recursive firstLayoutOfType (name: Str) (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> None
        | layout :: rest ->
            if layoutResultName(layout) == Some(name)
            then Some(layout)
            else firstLayoutOfType(name)(rest)

// The cell size of a same-arity ADT: one word per field, behind a tag word unless the type is
// tagless.
let shallowAdtCopySizeBytes (name: Str) (state: CoreLoweringState) =
    match state
    |> stateConstructorsOfType(name)
    |> firstLayoutOfType(name) with
        | Some(CoreConstructorLayout { tagless = tagless } as layout) ->
            layout
            |> constructorArity
            |> adtAllocationSizeBytes(tagless)
        | None -> adtAllocationSizeBytes(false)(0)

// The copy is reference-counted, or an arena copy where the function may run under a live handler
// post (stage 0's `normalizeToRuntimeOwnership`).
let scopeCopyOutOfIndexed (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemString -> (Some(ShallowScopeCopyOut(-1)), state)
        | SemBytes -> (Some(ShallowScopeCopyOut(-1)), state)
        | SemList(element) ->
            if element
            |> resolveType(state)
            |> canArenaResetLayout
            then (Some(ListScopeCopyOut), state)
            else (None, state)
        | SemNamed(_symbolId, name, _arguments) as named ->
            match heapShallowCopyOfIndexed(named)(state) with
                | (true, queried) ->
                    (queried
                    |> shallowAdtCopySizeBytes(name)
                    |> ShallowScopeCopyOut
                    |> Some, queried)
                | (false, queried) -> (None, queried)
        | _ -> (None, state)

let scopeCopyOutOf semanticType state =
    match scopeCopyOutOfIndexed(semanticType)(state) with
        | (answer, _queried) -> answer

let scopeCopyOutInstruction copyOut (runtime: Bool) copyTemp resultTemp =
    (let purpose =
        if runtime
        then RcNormalization
        else ArenaScopeBoundary
    in
        match copyOut with
            | ShallowScopeCopyOut(staticSizeBytes) -> CopyOutArena(copyTemp)(resultTemp)(staticSizeBytes)(runtime)(purpose)(None)
            | ListScopeCopyOut -> CopyOutList(copyTemp)(resultTemp)(InlineListHead)(runtime)(purpose))

let markCopyRuntime (runtime: Bool) (temp: Int) (state: CoreLoweringState) =
    if runtime
    then markRuntimeTemp(temp)(RuntimeNewlyProduced)(state)
    else state

// Whether this function built `cellTemp` as an arena cell (a cons or tuple allocation).
let recursive allocatesCell (cellTemp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = Alloc(target, _sizeBytes, _runtimeManaged) } :: rest -> target == cellTemp || allocatesCell(cellTemp)(rest)
        | _instruction :: rest -> allocatesCell(cellTemp)(rest)

// Whether `temp` is a duplicate marker not yet made real: the identity a pattern owner's read
// takes for an aggregate until the loop's finalize pass promotes it.
let recursive isDuplicateMarker (temp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = RcDup(target, _source, runtimeManaged, _mayBeEmpty) } :: rest ->
            if target == temp
            then runtimeManaged == false
            else isDuplicateMarker(temp)(rest)
        | _instruction :: rest -> isDuplicateMarker(temp)(rest)

// Stage 0's `ReleaseCellTailRetainAfterCopy`: a scope's result built as an arena cons cell whose
// tail is a pattern owner's duplicate taken for the cell (a list matched out of a loop parameter
// inside the loop's successor). The reference-counted copy stops at that tail and takes a reference
// of its own, and the arena cell never releases anything, so the duplicate is released right after
// the copy by a marker promoted together with it.
let releaseCellTailDuplicateAfterCopy copyOut (resultType: SemanticType) (cellTemp: Int) (state: CoreLoweringState) =
    match (copyOut, constructionWordSource(cellTemp)(8)(state.reversedInstructions)) with
        | (ListScopeCopyOut, Some(tail)) ->
            if allocatesCell(cellTemp)(state.reversedInstructions) && isDuplicateMarker(tail)(state.reversedInstructions) && !containsInt(tail)(stateReleasedPatternOwnerDuplicates(state))
            then
                state
                |> emit(RcDrop(tail)(patternOwnerTypeName(resolveType(state)(resultType))(state))(-1)(false)(false)(None))
                |> withStateReleasedPatternOwnerDuplicates(tail :: stateReleasedPatternOwnerDuplicates(state))
            else state
        | _ -> state

// Whether `temp` is a reference-counted retain already, rather than an identity marker the loop's
// finalize may still place.
let recursive retainedAsRuntimeValue (temp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = RcDup(target, _source, runtimeManaged, _mayBeEmpty) } :: rest ->
            if target == temp
            then runtimeManaged
            else retainedAsRuntimeValue(temp)(rest)
        | _ :: rest -> retainedAsRuntimeValue(temp)(rest)

// Stage 0's `ReleaseCopiedRetainedPatternOwnerResult`: an arm returning a pattern binding retains it
// for the join, so a scope copy-out that replaces the value with its copy releases that retain when
// the original is reference-counted. With a capability in the program the copy-out may be skipped
// while a post is pending, so the retain is kept there.
let releaseCopiedRetainedPatternOwnerResult (resultType: SemanticType) (resultTemp: Int) (state: CoreLoweringState) =
    if stateCapabilityGlobalCount(state) == 0 && containsInt(resultTemp)(statePatternOwnerResultTemps(state)) && retainedAsRuntimeValue(resultTemp)(state.reversedInstructions)
    then
        match freshTemp(state) with
            | FreshTemp { state = allocated, temp = referenceCountedTemp } ->
                match freshLabel("rc_copied_owner_result_kept")(allocated) with
                    | FreshLabel { state = labelled, label = skipLabel } ->
                        labelled
                        |> emit(IsReferenceCounted(referenceCountedTemp)(resultTemp))
                        |> emit(JumpIfFalse(referenceCountedTemp)(skipLabel))
                        |> emit(RcDrop(resultTemp)(patternOwnerTypeName(resolveType(labelled)(resultType))(labelled))(-1)(true)(false)(None))
                        |> emit(Label(skipLabel))
    else state

// Restores the arena, copies the result past the reset into a fresh runtime-managed temp, and
// reclaims the chunks, stage 0's `TryEmitScopeCopyOut` with the RC-normalizing copy.
let emitUnguardedScopeCopyOut copyOut (resultType: SemanticType) resultTemp cursorSlot endSlot preRestoreSlot state =
    match state
    |> emit(RestoreArenaState(cursorSlot)(endSlot)(preRestoreSlot)(false))
    |> freshTemp with
        | FreshTemp { state = restored, temp = copyTemp } ->
            restored
            |> emit(scopeCopyOutInstruction(copyOut)(ordinaryRcPlacementAllowed(restored))(copyTemp)(resultTemp))
            |> markCopyRuntime(ordinaryRcPlacementAllowed(restored))(copyTemp)
            |> releaseCellTailDuplicateAfterCopy(copyOut)(resultType)(resultTemp)
            |> emit(ReclaimArenaChunks(endSlot)(preRestoreSlot)(false))
            |> releaseCopiedRetainedPatternOwnerResult(resultType)(resultTemp)
            |> (given (closed) -> (closed, copyTemp))

// With a capability in the program the copy-out runs only while no one-shot post is pending: the
// result travels through a slot that the skipped path leaves holding the original pointer. The
// reloaded temp carries the copy's ownership fact; on the skipped path it reads the arena value,
// whose release is a no-op against its immortal header.
let emitScopeCopyOut copyOut (resultType: SemanticType) resultTemp cursorSlot endSlot preRestoreSlot state =
    if stateCapabilityGlobalCount(state) == 0
    then
        match emitUnguardedScopeCopyOut(copyOut)(resultType)(resultTemp)(cursorSlot)(endSlot)(preRestoreSlot)(state) with
            | (closed, copyTemp) -> (closed, Some(copyTemp))
    else
        match freshLocal(state) with
            | FreshLocal { state = allocated, local = guardSlot } ->
                match allocated
                |> emit(StoreLocal(guardSlot)(resultTemp))
                |> beginLivePostsGuard with
                    | (guarded, skipLabel) ->
                        match emitUnguardedScopeCopyOut(copyOut)(resultType)(resultTemp)(cursorSlot)(endSlot)(preRestoreSlot)(guarded) with
                            | (copied, copyTemp) ->
                                match copied
                                |> emit(StoreLocal(guardSlot)(copyTemp))
                                |> endLivePostsGuard(skipLabel)
                                |> freshTemp with
                                    | FreshTemp { state = joined, temp = reloadTemp } ->
                                        joined
                                        |> emit(LoadLocal(reloadTemp)(guardSlot))
                                        |> markCopyRuntime(ordinaryRcPlacementAllowed(joined))(reloadTemp)
                                        |> (given (closed) -> (closed, Some(reloadTemp)))

// The closing reset of a scope that owned and released a binding, stage 0's `PopOwnershipScope`:
// a surviving result resets the arena, a heap result with a copy-out kind is copied past the
// reset (the copy temp replaces the result), and any other heap result leaves the window open.
let closeOwnedScopeForResult (resultTemp: Int) (resultType: SemanticType) cursorSlot endSlot state =
    match freshLocal(state) with
        | FreshLocal { state = allocated, local = preRestoreSlot } ->
            if resultSurvivesReset(resultType)(allocated) || isRuntimeTemp(resultTemp)(allocated) || containsInt(resultTemp)(stateBackEdgeDummyTemps(allocated)) || isReuseResultTemp(resultTemp)(allocated)
            then (emitGuardedRestoreAndReclaim(cursorSlot)(endSlot)(preRestoreSlot)(allocated), None)
            else
                match scopeCopyOutOf(resultType)(allocated) with
                    | None -> (allocated, None)
                    | Some(copyOut) -> emitScopeCopyOut(copyOut)(resultType)(resultTemp)(cursorSlot)(endSlot)(preRestoreSlot)(allocated)

// The reloaded copy of a spilled reference-counted result holds the same reference.
let inheritRuntimeTemp (sourceTemp: Int) (targetTemp: Int) (state: CoreLoweringState) =
    match runtimeTempStateOf(sourceTemp)(state) with
        | Some(runtimeState) -> markRuntimeTemp(targetTemp)(runtimeState)(state)
        | None -> state

// Reloads a spilled body result after its scope's release and reset. A join that held a reference
// of its own on every arm still does once reloaded, so a loop exit returning it releases every
// parameter rather than transferring the one it equals.
let reloadLetResult (resultTemp: Int) (resultSlot: Int) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = reloadState, temp = reloadTemp } ->
            reloadState
            |> emit(LoadLocal(reloadTemp)(resultSlot))
            |> inheritRuntimeTemp(resultTemp)(reloadTemp)
            |> (given (inherited) ->
                if isRetainedArmJoin(resultTemp)(inherited)
                then recordRetainedArmJoin(reloadTemp)(inherited)
                else inherited)
            |> (given (reloaded) -> (reloaded, reloadTemp))

// Closes a `let`'s arena bracket and returns the closed state with the result temp. A scope that
// owns its binding spills the body result to a slot, releases the binding, restores, and reloads
// the result afterwards (stage 0's result preservation: the release could otherwise overwrite the
// result temp); a scope owning nothing restores and returns the body temp directly.
// Closes an owned `let`'s arena bracket. A result that already survived the scope reset, or
// simply isn't owned here, keeps its own temp — already recorded by whatever produced it. A
// runtime-managed result that needed a copy-out is a genuinely new physical value, so this
// records the copy's own temp itself before handing it back; a reload merely relocates the same
// reference into a fresh temp to survive the reset and is deliberately left unrecorded.
let closeOwnedLetBracket ownedTypeName ownerSlot cursorSlot endSlot resultTemp resultType state =
    match (ownedTypeName, runtimeOwnerStateOf(ownerSlot)(state)) with
        | (Some(_transferred), Some(false)) -> (closeScopeForResult(resultTemp)(resultType)(cursorSlot)(endSlot)(state), resultTemp)
        | (Some(typeName), _owned) ->
            match freshLocal(state) with
                | FreshLocal { state = resultAllocated, local = resultSlot } ->
                    match resultAllocated
                    |> emit(StoreLocal(resultSlot)(resultTemp))
                    |> emitOwnedLetRelease(typeName)(ownerSlot)
                    |> retireRuntimeOwner(ownerSlot)
                    |> closeOwnedScopeForResult(resultTemp)(resultType)(cursorSlot)(endSlot) with
                        | (closed, Some(copyTemp)) -> (recordValuePlacement(copyTemp)(resultType)(closed), copyTemp)
                        | (closed, None) -> reloadLetResult(resultTemp)(resultSlot)(closed)
        | (None, _owned) -> (closeScopeForResult(resultTemp)(resultType)(cursorSlot)(endSlot)(state), resultTemp)

// Resolve producer provenance against the defining lexical scope, before the new binding shadows
// its name. Local slots are frame-specific; a recorded fact never confers unique ownership.
let recursiveProducerBinding name (state: CoreLoweringState) =
    match lookupBinding(name)(state.bindings) with
        | Some(CoreBinding { location = CoreSelf(_label, _size) }) -> RecursiveProducerFunction
        | Some(CoreBinding { location = CoreLocal(slot) }) ->
            if state
            |> stateRecursiveProducerResultSlots
            |> containsInt(slot)
            then RecursiveProducerValue
            else OtherProducerBinding
        | _ -> OtherProducerBinding

let recursiveProducerResult expression state =
    isRecursiveProducerResult(expression)(given (name) -> recursiveProducerBinding(name)(state))

let recordRecursiveProducerSlot expression (fresh: FreshLocal) =
    match fresh with
        | FreshLocal { state = state, local = local } ->
            if recursiveProducerResult(expression)(state)
            then
                let recorded = withStateRecursiveProducerResultSlots(local :: stateRecursiveProducerResultSlots(state))(state)
                in fresh with state = recorded
            else fresh

// The stored half of `finishLetValueInSlot`: the binding's slot allocated and its value stored in
// it, with the body left for the caller to lower over the returned state.
// The owners a `let` collected from its value ahead of the store, taken back out by name: the
// innermost entry of that name, since a nested `let` of the same name stores first.
let recursive takePendingBorrowedOwners (name: Str) (entries: List((Str, List((Int, SemanticType))))) (passed: List((Str, List((Int, SemanticType))))) =
    match entries with
        | [] -> ([], reverse(passed))
        | (candidate, owners) :: rest ->
            if candidate == name
            then (owners, appendPassed(passed)(rest))
            else takePendingBorrowedOwners(name)(rest)((candidate, owners) :: passed)
and appendPassed (passed: List((Str, List((Int, SemanticType))))) (rest: List((Str, List((Int, SemanticType))))) =
    match passed with
        | [] -> rest
        | entry :: more -> appendPassed(more)(entry :: rest)

// Stage 0's `TrackLetOwnership` for an arena binding: the owners its aggregate value borrows are
// recorded by its slot, so a transfer of the binding retains them.
let recordBorrowedOwners (name: Str) (valueTemp: Int) (slot: Int) (state: CoreLoweringState) =
    match takePendingBorrowedOwners(name)(statePendingBorrowedOwners(state))([]) with
        | (owners, remaining) ->
            match (isRuntimeTemp(valueTemp)(state), owners) with
                | (false, _owner :: _rest) ->
                    state
                    |> withStatePendingBorrowedOwners(remaining)
                    |> withStateBorrowedOwners((slot, owners) :: stateBorrowedOwners(state))
                | _ -> withStatePendingBorrowedOwners(remaining)(state)

let storeLetValueInSlot name value (tailForwarded: Bool) bodyRequest outerBindings lowered =
    match lowered with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> (failure(failedState)(error), -1)
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            match state
            |> freshLocal
            |> recordRecursiveProducerSlot(value) with
                | FreshLocal { local = local } as fresh ->
                    (fresh
                    |> storeLetValue(name)(tailForwarded)(bodyRequest)(outerBindings)(temp)(semanticType)
                    |> recordBorrowedOwners(name)(temp)(local)
                    |> success(temp)(semanticType), local)

let finishLetValueInSlot name value body requestBody bodyRequest lower outerBindings lowered =
    match lowered
    |> noteLoweredValueBinders([name])
    |> storeLetValueInSlot(name)(value)(isTailForwardedBindingResult(requestBody)(name))(bodyRequest)(outerBindings) with
        | (LoweredCoreValue { state = failedState, error = Some(error) }, slot) -> (failure(failedState)(error), slot)
        | (LoweredCoreValue { state = stored, error = None }, local) ->
            (stored
            |> lower(body)
            |> restoreLoweredBindings(outerBindings), local)

// Stage 0's `IsRuntimeRcStringProducer`: `+` or a fully applied call to a builtin declared to
// produce a fresh string — the expressions a runtime-string request can place on the
// reference-counted heap.
// The root and argument count of a call spine, spans looked through.
let recursive qualifiedCallRoot (expression: Expr) (argumentCount: Int) =
    match expression with
        | ExprAt(_span, inner) -> qualifiedCallRoot(inner)(argumentCount)
        | ExprNode(_node, inner) -> qualifiedCallRoot(inner)(argumentCount)
        | ExprCall(function, _argument, _isSugar, _layout) -> qualifiedCallRoot(function)(argumentCount + 1)
        | ExprQualifiedVar(moduleName, memberName) -> Some((moduleName, memberName, argumentCount))
        | _ -> None

let freshStringBuiltinCallKind (expression: Expr) (state: CoreLoweringState) =
    match qualifiedCallRoot(expression)(0) with
        | Some((moduleName, memberName, argumentCount)) ->
            match builtinLayout(moduleName)(memberName)(state) with
                | Some(CoreBuiltinLayout { kind = kind } as layout) ->
                    if isFreshStringBuiltinKind(kind) && argumentCount == builtinArity(layout)
                    then Some(kind)
                    else None
                | None -> None
        | None -> None

let isRuntimeRcStringProducer (expression: Expr) (state: CoreLoweringState) =
    match unspanArgument(expression) with
        | ExprAdd(_left, _right) -> true
        | _ ->
            match freshStringBuiltinCallKind(expression)(state) with
                | Some(_kind) -> true
                | None -> false

// Stage 0's `IsRuntimeRcClosureCaptureSafeStringProducer`: the producers whose fresh string a
// closure may capture, or a `let` hand on as its result, without an arena copy behind it.
let isCaptureSafeStringProducer (expression: Expr) (state: CoreLoweringState) =
    match unspanArgument(expression) with
        | ExprAdd(_left, _right) -> true
        | _ ->
            match freshStringBuiltinCallKind(expression)(state) with
                | Some(CoreTextFromInt) -> true
                | Some(CoreTextFromFloat) -> true
                | Some(CoreTextToHex) -> true
                | Some(CoreBigIntToString) -> true
                | Some(CoreTextFormatFloat) -> true
                | _ -> false

let isDirectBindingResult (body: Expr) (name: Str) =
    match unspanArgument(body) with
        | ExprVar(candidate) -> candidate == name
        | _ -> false

// Stage 0's `IsImmediateRuntimeStringUse`: the body hands the binding straight to a consumer
// that reads a runtime string (`Ashes.Text.length`/`byteLength`, `Ashes.IO.print`).
let isImmediateRuntimeStringUse (body: Expr) (name: Str) =
    match unspanArgument(body) with
        | ExprCall(function, argument, _isSugar, _layout) ->
            match (unspanArgument(function), isDirectBindingResult(argument)(name)) with
                | (ExprQualifiedVar(moduleName, memberName), true) -> moduleName == "Ashes.Text" && (memberName == "length" || memberName == "byteLength") || moduleName == "Ashes.IO" && memberName == "print"
                | _ -> false
        | _ -> false

// The structural facts stage 0 reads before it places an aggregate on the reference-counted
// heap, asked of the constructor layouts and the heap layout classification.
let recursive constructorLayoutsOfType (name: Str) (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> []
        | layout :: rest ->
            if layoutResultName(layout) == Some(name)
            then layout :: constructorLayoutsOfType(name)(rest)
            else constructorLayoutsOfType(name)(rest)

// A constructor's field types and result type, instantiated afresh from its scheme so a field
// typed by a type parameter shows as an unbound variable (the scheme's own quantified ids may
// coincide with live ids in the substitution and must never be resolved through it); the
// instantiation is a query, so the advanced supply is discarded.
let layoutFieldTypes (layout: CoreConstructorLayout) (state: CoreLoweringState) =
    match (layout, state) with
        | (CoreConstructorLayout { scheme = scheme }, CoreLoweringState { typeSupply = supply }) ->
            match instantiate(scheme)(supply) with
                | InstantiationResult { semanticType = semanticType } -> splitConstructorType(semanticType)([])

let layoutIsGeneric (layout: CoreConstructorLayout) =
    match layout with
        | CoreConstructorLayout { scheme = TypeScheme { quantified = [] } } -> false
        | _ -> true

let recursive lookupRecordFieldExpression (field: Str) (fields: List((Str, Expr))) =
    match fields with
        | [] -> None
        | (candidate, expression) :: rest ->
            if candidate == field
            then Some(expression)
            else lookupRecordFieldExpression(field)(rest)

let recursive orderedRecordFieldExpressions (fieldNames: List(Str)) (fields: List((Str, Expr))) =
    match fieldNames with
        | [] -> Some([])
        | field :: rest ->
            match (lookupRecordFieldExpression(field)(fields), orderedRecordFieldExpressions(rest)(fields)) with
                | (Some(expression), Some(ordered)) -> Some(expression :: ordered)
                | _ -> None

// The constructor application at the top cell of `expression` — a saturated call chain rooted at
// a constructor name, a nullary constructor, or a record literal — as its layout and arguments in
// field order, stage 0's `TryDescribeConstructorExpression`.
let recursive constructorApplicationOf (expression: Expr) (arguments: List(Expr)) (state: CoreLoweringState) =
    match expression with
        | ExprAt(_span, inner) -> constructorApplicationOf(inner)(arguments)(state)
        | ExprNode(_node, inner) -> constructorApplicationOf(inner)(arguments)(state)
        | ExprCall(function, argument, _isSugar, _layout) -> constructorApplicationOf(function)(argument :: arguments)(state)
        | ExprVar(name) ->
            match constructorLayout(name)(state) with
                | Some(layout) ->
                    if constructorArity(layout) == length(arguments)
                    then Some((layout, arguments))
                    else None
                | None -> None
        | ExprRecord(name, fields, _isMultiline) ->
            match (constructorLayout(name)(state), arguments) with
                | (Some(CoreConstructorLayout { fieldNames = fieldNames } as layout), []) ->
                    match orderedRecordFieldExpressions(fieldNames)(fields) with
                        | Some(ordered) -> Some((layout, ordered))
                        | None -> None
                | _ -> None
        | _ -> None

let isConstructorExpression (expression: Expr) (state: CoreLoweringState) =
    match constructorApplicationOf(expression)([])(state) with
        | Some(_application) -> true
        | None -> false

let isRecordLiteral (expression: Expr) =
    match unspanArgument(expression) with
        | ExprRecord(_name, _fields, _isMultiline) -> true
        | _ -> false

let isTupleLiteral (expression: Expr) =
    match unspanArgument(expression) with
        | ExprTuple(_elements) -> true
        | _ -> false

let isInlineCopyLiteral (expression: Expr) =
    match unspanArgument(expression) with
        | ExprInt(_value) -> true
        | ExprUInt(_value, _bits, _text) -> true
        | ExprFloat(_value, _text) -> true
        | ExprBool(_value) -> true
        | _ -> false

let recursive isFreshCopyList (expression: Expr) =
    match unspanArgument(expression) with
        | ExprList(elements, _isMultiline) -> allInlineCopyLiterals(elements)
        | ExprCons(head, tail) -> isInlineCopyLiteral(head) && isFreshCopyList(tail)
        | _ -> false
and allInlineCopyLiterals (elements: List(Expr)) =
    match elements with
        | [] -> true
        | element :: rest -> isInlineCopyLiteral(element) && allInlineCopyLiterals(rest)

let recordAdtSupported (facts: HeapLayoutFacts) =
    match facts with
        | HeapLayoutFacts { runtimeRecordAdtSupported = supported } -> supported

let copyAdtSupported (facts: HeapLayoutFacts) =
    match facts with
        | HeapLayoutFacts { runtimeCopyAdtSupported = supported } -> supported

let ownedChildAdtSupported (facts: HeapLayoutFacts) =
    match facts with
        | HeapLayoutFacts { runtimeOwnedChildAdtSupported = supported } -> supported

let tcoOwnedChildAdtSupported (facts: HeapLayoutFacts) =
    match facts with
        | HeapLayoutFacts { runtimeTcoOwnedChildAdtSupported = supported } -> supported

let positionalAdtSupported (facts: HeapLayoutFacts) =
    match facts with
        | HeapLayoutFacts { runtimePositionalAdtSupported = supported } -> supported

// Stage 0's `CanRuntimeManageTcoListElement`: whether a runtime-managed list may own an element
// of this type.
let tcoListElementFactsSupported (element: SemanticType) (state: CoreLoweringState) =
    match heapFactsOf(element)(state) with
        | HeapLayoutFacts { runtimeTcoListElementSupported = supported } -> supported

let factsContainResource (facts: HeapLayoutFacts) =
    match facts with
        | HeapLayoutFacts { containsResource = contains } -> contains

let namedTypeNameOf (semanticType: SemanticType) =
    match semanticType with
        | SemNamed(_symbolId, name, _arguments) -> Some(name)
        | _ -> None

let isFreshStringChild (expression: Expr) (state: CoreLoweringState) = isRuntimeRcStringProducer(expression)(state) && isCaptureSafeStringProducer(expression)(state)

// Stage 0's `IsNormalizedAlwaysReturnedStringParameterRead`: a read of the string parameter the
// entry normalization owns moves the owned copy (or the adopted argument) into the aggregate
// storing it, so that aggregate can live on the RC heap and release the string with itself.
let isNormalizedAlwaysReturnedStringParameterRead (expression: Expr) (state: CoreLoweringState) =
    match (stateNormalizedAlwaysReturnedParameter(state), unspanArgument(expression)) with
        | (Some((name, slot, parameterType)), ExprVar(candidate)) ->
            candidate == name && resolveType(state)(parameterType) == SemString && (match lookupBinding(name)(state.bindings) with
                | Some(CoreBinding { location = CoreLocal(readSlot) }) -> readSlot == slot
                | _ -> false)
        | _ -> false

let isVariableOrCall (expression: Expr) =
    match unspanArgument(expression) with
        | ExprVar(_name) -> true
        | ExprCall(_function, _argument, _isSugar, _layout) -> true
        | _ -> false

// Stage 0's `IsRuntimeManageableFreshGenericPayload`: what a type-parameter field may hold as a
// fresh owned value.
let recursive isFreshGenericPayload (expression: Expr) (state: CoreLoweringState) = isFreshStringChild(expression)(state) || isFreshCopyList(expression) || isFreshGenericTuple(expression)(state)
and isFreshGenericTuple (expression: Expr) (state: CoreLoweringState) =
    match unspanArgument(expression) with
        | ExprTuple(elements) -> allFreshGenericElements(elements)(state)
        | _ -> false
and allFreshGenericElements (elements: List(Expr)) (state: CoreLoweringState) =
    match elements with
        | [] -> true
        | element :: rest -> (isInlineCopyLiteral(element) || isFreshGenericPayload(element)(state)) && allFreshGenericElements(rest)(state)

// Stage 0's `CanRuntimeManageFreshTupleExpression`: every element is a scalar or a fresh owned
// producer of its element type.
let recursive canRuntimeManageFreshTuple (elements: List(Expr)) (elementTypes: List(SemanticType)) (state: CoreLoweringState) =
    match (elements, elementTypes) with
        | ([], []) -> true
        | (element :: restElements, elementType :: restTypes) ->
            freshTupleElementSupported(element)(resolveType(state)(elementType))(state) && canRuntimeManageFreshTuple(restElements)(restTypes)(state)
        | _ -> false
and freshTupleElementSupported (element: Expr) (elementType: SemanticType) (state: CoreLoweringState) =
    if resultSurvivesReset(elementType)(state)
    then true
    else
        match (elementType, unspanArgument(element)) with
            | (SemString, _expression) -> isFreshStringChild(element)(state)
            | (SemList(inner), _expression) -> tcoListElementFactsSupported(inner)(state) && isFreshListConstruction(element)
            | (SemTuple(innerTypes), ExprTuple(innerElements)) -> canRuntimeManageFreshTuple(innerElements)(innerTypes)(state)
            | _ -> false

// Stage 0's `IsPerceusPatternOwnerRead`: a read of a pattern owner binding. As a string child of
// an aggregate it counts as owned, since the aggregate's own pattern-owner duplicate is a real
// retain for a string whatever the pattern root's placement.
let isPatternOwnerRead (child: Expr) (state: CoreLoweringState) =
    match unspanArgument(child) with
        | ExprVar(name) ->
            state
            |> patternOwnerBinding(name)
            |> patternOwnerFlag
        | _ -> false

let isBuiltinTypeName (name: Str) = name == "Unit" || name == "List" || name == "Maybe" || name == "Result" || name == "Task" || isResourceTypeName(name)

let recursive allFieldsAreScalarOrSelf (fieldTypes: List(SemanticType)) (typeName: Str) (state: CoreLoweringState) (sawSelf: Bool) =
    match fieldTypes with
        | [] -> Some(sawSelf)
        | fieldType :: rest ->
            if resultSurvivesReset(fieldType)(state)
            then allFieldsAreScalarOrSelf(rest)(typeName)(state)(sawSelf)
            else
                if namedTypeNameOf(resolveType(state)(fieldType)) == Some(typeName)
                then allFieldsAreScalarOrSelf(rest)(typeName)(state)(true)
                else None

let recursive allConstructorsScalarOrSelf (layouts: List(CoreConstructorLayout)) (typeName: Str) (state: CoreLoweringState) (sawSelf: Bool) =
    match layouts with
        | [] -> sawSelf
        | layout :: rest ->
            match layoutFieldTypes(layout)(state) with
                | (fieldTypes, _resultType) ->
                    match allFieldsAreScalarOrSelf(fieldTypes)(typeName)(state)(sawSelf) with
                        | None -> false
                        | Some(saw) -> allConstructorsScalarOrSelf(rest)(typeName)(state)(saw)

// Stage 0's `CanRuntimeManageRecursiveCopyAdt`: a user-declared, non-generic, non-resource type
// whose every non-scalar field is the type itself.
let canRuntimeManageRecursiveCopyAdt (semanticType: SemanticType) (state: CoreLoweringState) =
    match semanticType
    |> resolveType(state)
    |> namedTypeNameOf with
        | None -> false
        | Some(name) ->
            match state |> stateConstructorsOfType(name) with
                | [] -> false
                | first :: _rest as layouts -> isBuiltinTypeName(name) == false && layoutIsGeneric(first) == false && isResourceTypeNameIn(name)(state) == false && heapResourceOf(semanticType)(state) == false && allConstructorsScalarOrSelf(layouts)(name)(state)(false)

// Stage 0's `IsFreshConstructorTree`: a constructor application of `typeName` whose every
// non-scalar field is itself such a tree.
let recursive isFreshConstructorTree (expression: Expr) (typeName: Str) (state: CoreLoweringState) =
    match constructorApplicationOf(expression)([])(state) with
        | Some((layout, arguments)) ->
            match layoutFieldTypes(layout)(state) with
                | (fieldTypes, resultType) -> namedTypeNameOf(resultType) == Some(typeName) && allFreshConstructorFields(arguments)(fieldTypes)(typeName)(state)
        | None -> false
and allFreshConstructorFields (arguments: List(Expr)) (fieldTypes: List(SemanticType)) (typeName: Str) (state: CoreLoweringState) =
    match (arguments, fieldTypes) with
        | ([], []) -> true
        | (argument :: restArguments, fieldType :: restTypes) -> (resultSurvivesReset(fieldType)(state) || isFreshConstructorTree(argument)(typeName)(state)) && allFreshConstructorFields(restArguments)(restTypes)(typeName)(state)
        | _ -> false

let recursive allGenericCopyFields (arguments: List(Expr)) (fieldTypes: List(SemanticType)) (state: CoreLoweringState) =
    match (arguments, fieldTypes) with
        | ([], []) -> true
        | (argument :: restArguments, fieldType :: restTypes) -> (resultSurvivesReset(fieldType)(state) || isFieldTypeVariable(fieldType)(state) && isInlineCopyLiteral(argument)) && allGenericCopyFields(restArguments)(restTypes)(state)
        | _ -> false
and isFieldTypeVariable (fieldType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(fieldType) with
        | SemVariable(_id) -> true
        | _ -> false

// Stage 0's `CanRuntimeManageGenericCopyAdtConstructorApplication`: a single-constructor generic
// type whose type-parameter fields all receive inline literals.
let canRuntimeManageGenericCopyAdtApplication (layout: CoreConstructorLayout) (arguments: List(Expr)) (state: CoreLoweringState) =
    match layoutFieldTypes(layout)(state) with
        | (fieldTypes, resultType) ->
            match namedTypeNameOf(resultType) with
                | None -> false
                | Some(name) ->
                    layoutIsGeneric(layout) && length(state |> stateConstructorsOfType(name)) == 1 && isResourceTypeNameIn(name)(state) == false && allGenericCopyFields(arguments)(fieldTypes)(state)

let freshHeapChildFieldSupported (argument: Expr) (fieldType: SemanticType) (state: CoreLoweringState) =
    match (resolveType(state)(fieldType), unspanArgument(argument)) with
        | (SemString, _expression) -> isFreshStringChild(argument)(state) || isNormalizedAlwaysReturnedStringParameterRead(argument)(state) || isPatternOwnerRead(argument)(state)
        | (SemList(element), _expression) -> tcoListElementFactsSupported(element)(state) && isFreshListConstruction(argument)
        | (SemTuple(elementTypes), ExprTuple(elements)) -> canRuntimeManageFreshTuple(elements)(elementTypes)(state)
        | (SemVariable(_id), _expression) -> isFreshGenericPayload(argument)(state)
        | _ -> false

let recursive allFreshHeapChildFields (arguments: List(Expr)) (fieldTypes: List(SemanticType)) (state: CoreLoweringState) (sawOwned: Bool) =
    match (arguments, fieldTypes) with
        | ([], []) -> sawOwned
        | (argument :: restArguments, fieldType :: restTypes) ->
            if resultSurvivesReset(fieldType)(state)
            then allFreshHeapChildFields(restArguments)(restTypes)(state)(sawOwned)
            else freshHeapChildFieldSupported(argument)(fieldType)(state) && allFreshHeapChildFields(restArguments)(restTypes)(state)(true)
        | _ -> false

// Stage 0's `CanRuntimeManageFreshHeapChildAdtConstructorApplication`: a single-constructor,
// non-resource type with at least one owned field, every owned field receiving a fresh producer.
let canRuntimeManageFreshHeapChildAdtApplication (layout: CoreConstructorLayout) (arguments: List(Expr)) (state: CoreLoweringState) =
    match layoutFieldTypes(layout)(state) with
        | (fieldTypes, resultType) ->
            match namedTypeNameOf(resultType) with
                | None -> false
                | Some(name) ->
                    length(state |> stateConstructorsOfType(name)) == 1 && isResourceTypeNameIn(name)(state) == false && heapResourceOf(resultType)(state) == false && allFreshHeapChildFields(arguments)(fieldTypes)(state)(false)

// A fresh constructor tree of a recursive-copy type.
let isFreshRecursiveCopyTree (expression: Expr) (resultType: SemanticType) (state: CoreLoweringState) =
    match namedTypeNameOf(resultType) with
        | Some(name) -> canRuntimeManageRecursiveCopyAdt(resultType)(state) && isFreshConstructorTree(expression)(name)(state)
        | None -> false

// Stage 0's `IsMaterializableStringChildRead`: a borrowed string a runtime-managed parent copies
// into an owned child of its own: a field read out of a local record binding, or a read of a
// name the arm around the literal binds later or of a local binding that owns no
// reference-counted string of its own; a literal keeps the parent in the arena.
let isMaterializableStringChildRead (expression: Expr) (state: CoreLoweringState) =
    match unspanArgument(expression) with
        | ExprQualifiedVar(owner, _field) ->
            match lookupBinding(owner)(state.bindings) with
                | Some(CoreBinding { location = CoreLocal(_slot) }) -> true
                | _ -> false
        | ExprVar(name) ->
            match lookupBinding(name)(state.bindings) with
                | None -> true
                | Some(CoreBinding { location = CoreLocal(_slot), patternFact = fact }) -> patternOwnerFlag(fact) == false && isNormalizedAlwaysReturnedStringParameterRead(expression)(state) == false && liveRuntimeOwnerSlot(name)(state) == None
                | _ -> false
        | _ -> false

// Stage 0's `IsLocalRecordFieldRead`: a field read out of a local binding, the record-field
// spelling of the value a `match` arm binds by name. A binding the lookup does not find counts
// too, since an escaping result is classified before the `let` that binds the receiver has
// lowered; the parent's own lowering normalizes or retains whichever form the child turns out to
// have.
let isLocalRecordFieldRead (expression: Expr) (state: CoreLoweringState) =
    match unspanArgument(expression) with
        | ExprQualifiedVar(owner, _field) ->
            match lookupBinding(owner)(state.bindings) with
                | None -> true
                | Some(CoreBinding { location = CoreLocal(_slot) }) -> true
                | Some(CoreBinding { location = CoreEnvironment(_index) }) -> true
                | _ -> false
        | _ -> false

// Stage 0's `CanRuntimeManageFreshOwnedChildExpression` and the record and accumulator trees it
// recurses into: a field holds a fresh owned value when it is a scalar, a fresh string producer
// or pattern-owner read, a list the runtime may own built fresh or read from a binding or call,
// a fresh tuple or a pattern-owner tuple read, a fresh record tree, a fresh application of an
// accumulator-shaped or any other runtime-manageable type, or a pattern-owner aggregate read.
let recursive canRuntimeManageFreshOwnedChild (expression: Expr) (fieldType: SemanticType) (state: CoreLoweringState) =
    if resultSurvivesReset(fieldType)(state)
    then true
    else
        match (resolveType(state)(fieldType), unspanArgument(expression)) with
            | (SemString, _expression) -> isFreshStringChild(expression)(state) || isNormalizedAlwaysReturnedStringParameterRead(expression)(state) || isPatternOwnerRead(expression)(state) || isMaterializableStringChildRead(expression)(state)
            | (SemList(element), _expression) -> tcoListElementFactsSupported(element)(state) && (isFreshListConstruction(expression) || isVariableOrCall(expression) || isLocalRecordFieldRead(expression)(state))
            | (SemTuple(elementTypes), ExprTuple(elements)) -> canRuntimeManageFreshTuple(elements)(elementTypes)(state)
            | (SemTuple(_elementTypes), _expression) -> isPatternOwnerRead(expression)(state)
            | (SemNamed(_symbolId, name, _arguments), _expression) -> isRecordLiteral(expression) && isFreshRuntimeManageableRecordTree(expression)(state) || isFreshTcoOwnedChildApplication(expression)(name)(state) || isFreshRuntimeManageableAdtExpression(expression)(state) || isPatternOwnerRead(expression)(state)
            | _ -> false
and allFreshOwnedChildren (arguments: List(Expr)) (fieldTypes: List(SemanticType)) (state: CoreLoweringState) =
    match (arguments, fieldTypes) with
        | ([], []) -> true
        | (argument :: restArguments, fieldType :: restTypes) -> canRuntimeManageFreshOwnedChild(argument)(fieldType)(state) && allFreshOwnedChildren(restArguments)(restTypes)(state)
        | _ -> false
and isFreshRuntimeManageableRecordTree (expression: Expr) (state: CoreLoweringState) =
    match constructorApplicationOf(expression)([])(state) with
        | Some((layout, arguments)) ->
            match layoutFieldTypes(layout)(state) with
                | (fieldTypes, resultType) ->
                    isRecordLiteral(expression) && recordAdtSupported(heapFactsOf(resultType)(state)) && allFreshOwnedChildren(arguments)(fieldTypes)(state)
        | None -> false
and isFreshTcoOwnedChildApplication (expression: Expr) (typeName: Str) (state: CoreLoweringState) =
    match constructorApplicationOf(expression)([])(state) with
        | Some((layout, arguments)) ->
            match layoutFieldTypes(layout)(state) with
                | (fieldTypes, resultType) ->
                    namedTypeNameOf(resultType) == Some(typeName) && tcoOwnedChildAdtSupported(heapFactsOf(resultType)(state)) && allFreshOwnedChildren(arguments)(fieldTypes)(state)
        | None -> false
// Stage 0's `CanRuntimeManageOwnedChildAdtConstructorApplication`, without the tracked child
// bindings of an immediate match.
and canRuntimeManageOwnedChildAdtApplication (layout: CoreConstructorLayout) (arguments: List(Expr)) (state: CoreLoweringState) =
    match layoutFieldTypes(layout)(state) with
        | (fieldTypes, resultType) ->
            ownedChildAdtSupported(heapFactsOf(resultType)(state)) && allFreshOwnedChildren(arguments)(fieldTypes)(state)
// Stage 0's `IsFreshRuntimeManageableAdtExpression`: a constructor application the runtime can
// own by one of its ADT shapes.
and isFreshRuntimeManageableAdtExpression (expression: Expr) (state: CoreLoweringState) =
    match constructorApplicationOf(expression)([])(state) with
        | None -> false
        | Some((layout, arguments)) ->
            match layoutFieldTypes(layout)(state) with
                | (_fieldTypes, resultType) ->
                    copyAdtSupported(heapFactsOf(resultType)(state)) || canRuntimeManageGenericCopyAdtApplication(layout)(arguments)(state) || canRuntimeManageFreshHeapChildAdtApplication(layout)(arguments)(state) || canRuntimeManageOwnedChildAdtApplication(layout)(arguments)(state) || isFreshRecursiveCopyTree(expression)(resultType)(state)

// The parent type of the constructor an arm applies at its top cell, stage 0's
// `AdtConstructorGroupKey`; a passthrough arm has none.
let constructorGroupKey (expression: Expr) (state: CoreLoweringState) =
    match constructorApplicationOf(expression)([])(state) with
        | Some((layout, _arguments)) -> layoutResultName(layout)
        | None -> None

let recursive callSpineRootAndArity (expression: Expr) (arity: Int) =
    match expression with
        | ExprAt(_span, inner) -> callSpineRootAndArity(inner)(arity)
        | ExprNode(_node, inner) -> callSpineRootAndArity(inner)(arity)
        | ExprCall(function, _argument, _isSugar, _layout) -> callSpineRootAndArity(function)(arity + 1)
        | other -> (other, arity)

// A full self-call back into the loop being lowered, stage 0's `IsSelfRecursiveTailFunnelArm`:
// the arm constructs nothing here, and every path through it bottoms out at one of the same
// function's own terminals.
let isSelfFunnelArm (expression: Expr) (state: CoreLoweringState) =
    match stateTcoLoop(state) with
        | None -> false
        | Some(CoreTcoLoop { selfName = selfName, arity = arity }) ->
            match callSpineRootAndArity(expression)(0) with
                | (ExprVar(name), applied) -> name == selfName && applied == arity
                | _ -> false

// Only an emitted back edge proves an arm cannot reach its join. A syntactic self call can
// remain a CallClosure in a curried helper and return a value owned by a local result slot.
// Treating that call as unreachable would let a fresh sibling give the join false ownership.
// Like stage 0's IsOwnershipNeutralTemp, this fact follows the lowered value, not the AST.
let armNeverReachesJoin (temp: Int) (state: CoreLoweringState) =
    state
    |> stateBackEdgeDummyTemps
    |> containsInt(temp)

let recursive patternFactsNamed (name: Str) (facts: List(PatternBindingFact)) =
    match facts with
        | [] -> []
        | (PatternBindingFact { name = candidate } as fact) :: rest ->
            if candidate == name
            then fact :: patternFactsNamed(name)(rest)
            else patternFactsNamed(name)(rest)

let recursive nthArgumentShape (ordinal: Int) (shapes: List(TcoArgumentShape)) =
    match shapes with
        | [] -> TcoOtherShape
        | shape :: rest ->
            if ordinal == 0
            then shape
            else nthArgumentShape(ordinal - 1)(rest)

let recursive allFactsRetained (loop: CoreTcoLoop) (facts: List(PatternBindingFact)) =
    match facts with
        | [] -> true
        | fact :: rest -> patternFactRequiresProtection(fact) && nthArgumentShape(fact.rootParameterOrdinal)(loop.argumentShapes) == TcoConsumedTailShape && allFactsRetained(loop)(rest)

// A terminal arm that reads a pattern owner extracted from a consumed-tail loop parameter: the
// branch retains the owner's reference for the result (stage 0's
// `TransferDirectRuntimeManagedBranchResult`), so the arm carries a runtime-managed value that
// constructs nothing here and never conflicts with a fresh sibling. The placement is decided
// once the body is lowered; a consumed-tail root is taken as placed.
let retainedPatternOwnerTerminal (expression: Expr) (state: CoreLoweringState) =
    match (unspanArgument(expression), stateTcoLoop(state)) with
        | (ExprVar(name), Some(loop)) ->
            match patternFactsNamed(name)(loop.patternFacts) with
                | [] -> false
                | facts -> allFactsRetained(loop)(facts)
        | _ -> false

// An arm that constructs nothing at the escaping position and never conflicts with a fresh
// sibling: a tail self-call, or a retained pattern owner.
let escapeFunnelArm (expression: Expr) (state: CoreLoweringState) = isSelfFunnelArm(expression)(state) || retainedPatternOwnerTerminal(expression)(state)

// Stage 0's `ProducesFreshRuntimeManageableAdt`: some terminal arm applies a runtime-manageable
// constructor and every sibling arm of its type, or without a constructor, is fresh too.
let producesFreshRuntimeManageableAdt (body: Expr) (state: CoreLoweringState) =
    anyArmConsistentlyFresh(freshEscapeTerminals(body))(given (terminal: Expr) -> isFreshRuntimeManageableAdtExpression(terminal)(state))(given (terminal: Expr) -> constructorGroupKey(terminal)(state))(given (terminal: Expr) -> escapeFunnelArm(terminal)(state))

let recordLiteralGroupKey (expression: Expr) =
    match unspanArgument(expression) with
        | ExprRecord(name, _fields, _isMultiline) -> Some(name)
        | _ -> None

// Stage 0's `ProducesFreshRuntimeManageableRecord`: some terminal arm builds a runtime-manageable
// record literal and every sibling arm of its type, or without a record, is fresh too.
let producesFreshRuntimeManageableRecord (body: Expr) (state: CoreLoweringState) =
    anyArmConsistentlyFresh(freshEscapeTerminals(body))(given (terminal: Expr) -> isFreshRuntimeManageableRecordTree(terminal)(state))(recordLiteralGroupKey)(given (terminal: Expr) -> escapeFunnelArm(terminal)(state))

// Stage 0's `ProducesFreshRuntimeManageableList`: every terminal arm builds its list fresh.
let producesFreshRuntimeManageableList (body: Expr) =
    body
    |> freshEscapeTerminals
    |> allTerminals(isFreshListConstruction)

let recursive producesFreshTuple (body: Expr) (state: CoreLoweringState) =
    freshTupleTerminals(freshEscapeTerminals(body))(state)(false)
and freshTupleTerminals (terminals: List(Expr)) (state: CoreLoweringState) (sawFreshTuple: Bool) =
    match terminals with
        | [] -> sawFreshTuple
        | terminal :: rest ->
            if escapeFunnelArm(terminal)(state)
            then freshTupleTerminals(rest)(state)(sawFreshTuple)
            else
                if isTupleLiteral(terminal) || constructorCarriesFreshTuple(terminal)(state)
                then freshTupleTerminals(rest)(state)(true)
                else isConstructorExpression(terminal)(state) && freshTupleTerminals(rest)(state)(sawFreshTuple)
and constructorCarriesFreshTuple (terminal: Expr) (state: CoreLoweringState) =
    match constructorApplicationOf(terminal)([])(state) with
        | Some((_layout, arguments)) -> anyProducesFreshTuple(arguments)(state)
        | None -> false
and anyProducesFreshTuple (arguments: List(Expr)) (state: CoreLoweringState) =
    match arguments with
        | [] -> false
        | argument :: rest -> producesFreshTuple(argument)(state) || anyProducesFreshTuple(rest)(state)

// What the scope-exit release of the binding may assume about its value, stage 0's
// `RuntimeDeepUnique` and `RuntimeConstructor` owner facts: a fresh list literal or cons chain,
// and a fresh constructor tree of a recursive-copy type, are the only references to their whole
// graph; a direct constructor application names the constructor whose fields the release walks.
//
// A body that may share a child rather than copy it has no such fact to offer: the graph a fresh
// literal built can be reached from whatever a representation test let a consumer keep, so the
// release tests for sharing rather than assuming there is none.
let armOwnerReleasePlan (value: Expr) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { error = Some(_error) } -> lowered
        | LoweredCoreValue { state = state, semanticType = semanticType, error = None } ->
            let deepUnique =
                canTestRepresentation(state) == false && (match resolveType(state)(semanticType) with
                    | SemList(_element) -> isFreshListConstruction(value)
                    | SemNamed(_symbolId, name, _arguments) as named -> canRuntimeManageRecursiveCopyAdt(named)(state) && isFreshConstructorTree(value)(name)(state)
                    | _ -> false)
            in
                let constructorName =
                    match constructorApplicationOf(value)([])(state) with
                        | Some((CoreConstructorLayout { name = name }, _arguments)) -> Some(name)
                        | None -> None
                in lowered with state = withStatePendingOwnerPlan(Some(OwnedReleasePlan(deepUnique = deepUnique, constructorName = constructorName, matchFieldTransfer = None)))(state)

// Stage 0's `LowerEscapingResult`: a body that is itself a fresh string producer, or whose
// terminal arms build a fresh runtime-manageable constructor, list, tuple, or record tree, is
// asked to place its result on the reference-counted heap on top of the request it already
// carries (a tuple request rides along with every aggregate request, so a tuple nested in the
// escaping aggregate is materialized too); a body producing none of those carries its children
// out of the scopes that own them (`WithEscapingConsumerOwnership`), so the aggregates it builds
// retain them.
let requestsRuntimeRepresentation (request: ConsumerRequest) =
    match request with
        | ConsumerRequest { runtimeString = runtimeString, runtimeAdt = runtimeAdt, runtimeList = runtimeList, runtimeRecord = runtimeRecord, runtimeTuple = runtimeTuple } -> runtimeString || runtimeAdt || runtimeList || runtimeRecord || runtimeTuple

let withEscapingTransfer (request: ConsumerRequest) =
    if requestsRuntimeRepresentation(request)
    then request
    else request with transfersRuntimeManagedChildren = true

// Stage 0's `IsReconcilableFreshStringJoin`: a control-flow join an escaping runtime-managed
// string request reconciles, every branch a literal (copied to the reference-counted heap
// inside its branch), a fresh producer, or a value that never reaches the join, and at least one
// branch fresh. Deciding the request on the whole join keeps a nested fresh branch from taking
// the representation alone and leaving the literal beside it as an arena value in a mixed join.
let reconcilableStringArms (cases: List((Pattern, Expr, Maybe(Expr)))) (state: CoreLoweringState) =
    shouldNormalizeStaticStringArms(given (body: Expr) -> isRuntimeRcStringProducer(body)(state) || retainedPatternOwnerTerminal(body)(state))(given (body: Expr) -> isSelfFunnelArm(body)(state))(given (_body: Expr) -> false)(cases)

let isReconcilableFreshStringJoin (body: Expr) (state: CoreLoweringState) =
    match unspanArgument(body) with
        | ExprIf(_condition, thenBranch, elseBranch) -> reconcilableStringArms([(PatternWildcard, thenBranch, None), (PatternWildcard, elseBranch, None)])(state)
        | ExprMatch(_scrutinee, cases, _position) -> reconcilableStringArms(cases)(state)
        | _ -> false

let escapingResultRequest (body: Expr) (request: ConsumerRequest) (state: CoreLoweringState) =
    (let produced =
        emptyConsumerRequest
        |> (given (fresh: ConsumerRequest) -> fresh with runtimeString = isRuntimeRcStringProducer(body)(state) || isReconcilableFreshStringJoin(body)(state))
        |> (given (fresh: ConsumerRequest) -> fresh with runtimeAdt = producesFreshRuntimeManageableAdt(body)(state))
        |> (given (fresh: ConsumerRequest) -> fresh with runtimeList = producesFreshRuntimeManageableList(body))
        |> (given (fresh: ConsumerRequest) -> fresh with runtimeRecord = producesFreshRuntimeManageableRecord(body)(state))
        |> (given (fresh: ConsumerRequest) -> fresh with runtimeTuple = producesFreshTuple(body)(state) || fresh.runtimeAdt || fresh.runtimeList || fresh.runtimeRecord)
    in
        if requestsRuntimeRepresentation(produced)
        then request with runtimeString = request.runtimeString || produced.runtimeString, runtimeAdt = request.runtimeAdt || produced.runtimeAdt, runtimeList = request.runtimeList || produced.runtimeList, runtimeRecord = request.runtimeRecord || produced.runtimeRecord, runtimeTuple = request.runtimeTuple || produced.runtimeTuple
        else withEscapingTransfer(request))

let recursive containsName name names =
    match names with
        | [] -> false
        | candidate :: rest ->
            if name == candidate
            then true
            else containsName(name)(rest)

let addFreeName name bound free =
    match (containsName(name)(bound), containsName(name)(free)) with
        | (false, false) -> append(free)([name])
        | _ -> free

let recursive patternBindingNames pattern names =
    match pattern with
        | PatternAt(_span, inner) -> patternBindingNames(inner)(names)
        | PatternVar(name) ->
            if containsName(name)(names)
            then names
            else name :: names
        | PatternCons(head, tail) ->
            names
            |> patternBindingNames(head)
            |> patternBindingNames(tail)
        | PatternTuple(patterns) -> patternListBindingNames(patterns)(names)
        | PatternConstructor(_name, patterns) -> patternListBindingNames(patterns)(names)
        | PatternRecord(_name, fields) -> patternFieldBindingNames(fields)(names)
        | PatternAs(inner, name) -> patternBindingNames(inner)(name :: names)
        | PatternOr([]) -> names
        | PatternOr(first :: _rest) -> patternBindingNames(first)(names)
        | _ -> names
and patternListBindingNames patterns names =
    match patterns with
        | [] -> names
        | pattern :: rest ->
            names
            |> patternBindingNames(pattern)
            |> patternListBindingNames(rest)
and patternFieldBindingNames fields names =
    match fields with
        | [] -> names
        | (_field, pattern) :: rest ->
            names
            |> patternBindingNames(pattern)
            |> patternFieldBindingNames(rest)

let recursive collectFree expression bound free =
    match expression with
        | ExprAt(_span, inner) -> collectFree(inner)(bound)(free)
        | ExprNode(_node, inner) -> collectFree(inner)(bound)(free)
        | ExprVar(name) -> addFreeName(name)(bound)(free)
        | ExprAdd(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprSubtract(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprMultiply(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprDivide(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprModulo(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprBitwiseAnd(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprBitwiseOr(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprBitwiseXor(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprShiftLeft(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprShiftRight(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprGreaterThan(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprGreaterOrEqual(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprLessThan(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprLessOrEqual(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprEqual(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprNotEqual(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprCons(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprBitwiseNot(operand) -> collectFree(operand)(bound)(free)
        | ExprLogicalNot(operand) -> collectFree(operand)(bound)(free)
        | ExprLogicalAnd(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprLogicalOr(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprLet(name, value, body, _parameters, _annotation, _requirements) ->
            let valueFree = collectFree(value)(bound)(free)
            in collectFree(body)(name :: bound)(valueFree)
        | ExprLetRecursive(name, value, body, _parameters, _annotation, _requirements) ->
            let recursiveBound = name :: bound
            in
                let valueFree = collectFree(value)(recursiveBound)(free)
                in collectFree(body)(recursiveBound)(valueFree)
        | ExprIf(condition, thenBranch, elseBranch) ->
            let conditionFree = collectFree(condition)(bound)(free)
            in
                let thenFree = collectFree(thenBranch)(bound)(conditionFree)
                in collectFree(elseBranch)(bound)(thenFree)
        | ExprLambda(parameter, body, _annotation) -> collectFree(body)(parameter :: bound)(free)
        | ExprCall(function, argument, _whitespace, _layout) ->
            let functionFree = collectFree(function)(bound)(free)
            in collectFree(argument)(bound)(functionFree)
        | ExprTuple(elements) -> collectFreeExpressions(elements)(bound)(free)
        | ExprList(elements, _isMultiline) -> collectFreeExpressions(elements)(bound)(free)
        | ExprRecord(_name, fields, _isMultiline) -> collectFreeFields(fields)(bound)(free)
        | ExprRecordUpdate(target, fields) ->
            free
            |> collectFree(target)(bound)
            |> collectFreeFields(fields)(bound)
        | ExprMatch(value, cases, _position) ->
            free
            |> collectFree(value)(bound)
            |> collectMatchCasesFree(cases)(bound)
        | ExprQualifiedVar(owner, _member) -> addFreeName(owner)(bound)(free)
        | ExprResultPipe(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprResultMapErrorPipe(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprLetResult(name, value, body) ->
            let valueFree = collectFree(value)(bound)(free)
            in collectFree(body)(name :: bound)(valueFree)
        | ExprAwait(operand) -> collectFree(operand)(bound)(free)
        | ExprPerform(operand) -> collectFree(operand)(bound)(free)
        | ExprHandle(body, arms) ->
            free
            |> collectFree(body)(bound)
            |> collectHandleArmsFree(arms)(bound)
        | _ -> free
and collectFreeBinary left right bound free =
    free
    |> collectFree(left)(bound)
    |> collectFree(right)(bound)
and collectFreeExpressions expressions bound free =
    match expressions with
        | [] -> free
        | expression :: rest ->
            free
            |> collectFree(expression)(bound)
            |> collectFreeExpressions(rest)(bound)
and collectFreeFields fields bound free =
    match fields with
        | [] -> free
        | (_name, expression) :: rest ->
            free
            |> collectFree(expression)(bound)
            |> collectFreeFields(rest)(bound)
and collectMatchCaseFree bound free case =
    match case with
        | (pattern, body, guard) ->
            let caseBound =
                append(patternBindingNames(pattern)([]))(bound)
            in
                let guardFree =
                    match guard with
                        | None -> free
                        | Some(expression) -> collectFree(expression)(caseBound)(free)
                in collectFree(body)(caseBound)(guardFree)
and collectMatchCasesFree cases bound free =
    match cases with
        | [] -> free
        | case :: rest ->
            case
            |> collectMatchCaseFree(bound)(free)
            |> collectMatchCasesFree(rest)(bound)
// Stage 0's `FreeVarsVisitHandle`: an arm's parameters and its `resume` are bound in its body.
and collectHandleArmsFree arms bound free =
    match arms with
        | [] -> free
        | (_capability, _operation, parameters, body) :: rest ->
            let armBound =
                append(patternListBindingNames(parameters)([]))("resume" :: bound)
            in
                free
                |> collectFree(body)(armBound)
                |> collectHandleArmsFree(rest)(bound)

let recursive capturedBindings names bindings reversed =
    match names with
        | [] -> reverse(reversed)
        | name :: rest ->
            match lookupBinding(name)(bindings) with
                | None -> capturedBindings(rest)(bindings)(reversed)
                | Some(binding) -> capturedBindings(rest)(bindings)(binding :: reversed)

// A pruned capture's fill is excised after it was lowered, so its temps stay allocated (stage
// 0's `LowerLambdaCoreRemoveDeadFills` removes the instructions, never the temps).
let reserveCaptureTemps binding state =
    match lowerBoundVariable(binding)(state) with
        | LoweredCoreValue { state = captureState, error = Some(error) } -> failure(captureState)(error)
        | LoweredCoreValue { state = captureState, error = None } -> success(-1)(SemInt)((captureState with reversedInstructions = state.reversedInstructions, runtimeTemps = state.runtimeTemps))

let recursive fillEnvironment copyCapture environmentTemp captures survivors index state =
    match captures with
        | [] -> success(environmentTemp)(SemInt)(state)
        | (CoreBinding { name = name } as binding) :: rest ->
            match lookupBinding(name)(survivors) with
                | None ->
                    match reserveCaptureTemps(binding)(state) with
                        | LoweredCoreValue { state = reservedState, error = Some(error) } -> failure(reservedState)(error)
                        | LoweredCoreValue { state = reservedState, error = None } -> fillEnvironment(copyCapture)(environmentTemp)(rest)(survivors)(index)(reservedState)
                | Some(_survivor) ->
                    match lowerBoundVariable(binding)(state) with
                        | LoweredCoreValue { state = captureState, error = Some(error) } -> failure(captureState)(error)
                        | LoweredCoreValue { state = captureState, temp = captureTemp, semanticType = captureType, error = None } ->
                            match copyCapture(binding)(captureTemp)(captureType)(captureState) with
                                | (copiedState, storedTemp) ->
                                    copiedState
                                    |> emit(StoreMemOffset(environmentTemp)(index * 8)(storedTemp))
                                    |> fillEnvironment(copyCapture)(environmentTemp)(rest)(survivors)(index + 1)

// The capture an environment stores as it was read.
let storeCaptureAsRead (_binding: CoreBinding) (captureTemp: Int) (_captureType: SemanticType) (state: CoreLoweringState) = (state, captureTemp)

let recursive captureCount captures =
    match captures with
        | [] -> 0
        | _ :: rest -> 1 + captureCount(rest)

// The environment holds the surviving captures; every capture of the free-variable analysis
// still lowers its fill, the pruned ones only to reserve their temps. `copyCapture` answers the
// temp a surviving capture is stored from: the capture itself, or a copy the environment keeps.
let allocateEnvironment copyCapture captures survivors stackAllocate state =
    match freshTemp(state) with
        | FreshTemp { state = tempState, temp = environmentTemp } ->
            match captures with
                | [] ->
                    tempState
                    |> emit(LoadConstInt(environmentTemp)(0))
                    |> success(environmentTemp)(SemInt)
                | _ ->
                    let byteCount = 8 * captureCount(survivors)
                    in
                        let allocatedState =
                            if stackAllocate
                            then
                                emit(AllocStack(environmentTemp)(byteCount))(tempState)
                            else
                                emit(Alloc(environmentTemp)(byteCount)(false))(tempState)
                        in fillEnvironment(copyCapture)(environmentTemp)(captures)(survivors)(0)(allocatedState)

// Dead-capture pruning. The free-variable analysis decides a lambda's captures before its body is
// lowered, so a capture the lowered body never reads through LoadEnv still gets an environment
// word and a fill. The body is lowered first here, into its own instruction list, so the captures
// its instructions actually read are known before the environment is built at the creation site:
// the survivors are renumbered to a compact 0..k-1 range in the body's LoadEnv indices, and the
// environment is then allocated and filled for the survivors only, so every consumer that derives
// its offsets from the capture list's enumeration order stays consistent without patching. The
// self-referential and mutual-recursion paths never come through here: a self reference rebuilds a
// closure over this same environment with a size fixed before pruning could run, and a group's
// environment is shared and filled once at the group site.
let recursive containsIndex (index: Int) (indices: List(Int)) =
    match indices with
        | [] -> false
        | candidate :: rest ->
            if candidate == index
            then true
            else containsIndex(index)(rest)

let recursive collectLoadEnvIndices instructions acc =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = LoadEnv(_, index) } :: rest ->
            if containsIndex(index)(acc)
            then collectLoadEnvIndices(rest)(acc)
            else collectLoadEnvIndices(rest)(index :: acc)
        | _ :: rest -> collectLoadEnvIndices(rest)(acc)

let recursive countIndices (indices: List(Int)) =
    match indices with
        | [] -> 0
        | _ :: rest -> 1 + countIndices(rest)

// (survivors in capture order, old index -> new index for each survivor)
let recursive pruneCaptureList captures usedIndices (index: Int) (nextIndex: Int) survivors remap =
    match captures with
        | [] -> (reverse(survivors), reverse(remap))
        | capture :: rest ->
            if containsIndex(index)(usedIndices)
            then pruneCaptureList(rest)(usedIndices)(index + 1)(nextIndex + 1)(capture :: survivors)((index, nextIndex) :: remap)
            else pruneCaptureList(rest)(usedIndices)(index + 1)(nextIndex)(survivors)(remap)

let recursive remappedIndex (index: Int) (remap: List((Int, Int))) =
    match remap with
        | [] -> index
        | (oldIndex, newIndex) :: rest ->
            if oldIndex == index
            then newIndex
            else remappedIndex(index)(rest)

let recursive renumberLoadEnv instructions remap acc =
    match instructions with
        | [] -> reverse(acc)
        | IrInstruction { instruction = LoadEnv(target, index), location = location } :: rest ->
            renumberLoadEnv(rest)(remap)(IrInstruction(instruction = remap
            |> remappedIndex(index)
            |> LoadEnv(target), location = location) :: acc)
        | head :: rest -> renumberLoadEnv(rest)(remap)(head :: acc)

// (surviving captures, the body's instructions with LoadEnv indices renumbered), in the input order
// of both lists; unchanged when every capture is read.
let pruneDeadCaptures captures instructions =
    (let usedIndices = collectLoadEnvIndices(instructions)([])
    in
        if countIndices(usedIndices) >= captureCount(captures)
        then (captures, instructions)
        else
            match pruneCaptureList(captures)(usedIndices)(0)(0)([])([]) with
                | (survivors, remap) -> (survivors, renumberLoadEnv(instructions)(remap)([])))

let recursive capturedScope captures index =
    match captures with
        | [] -> []
        | CoreBinding { name = name, scheme = scheme, ownedRead = ownedRead, closedScheme = closedScheme } :: rest ->
            let binding = CoreBinding(name = name, scheme = scheme, location = CoreEnvironment(index), ownedRead = ownedRead, patternFact = None, closedScheme = closedScheme)
            in binding :: capturedScope(rest)(index + 1)

let recursive sealOperatorDefaults label pending sealed =
    match pending with
        | [] -> sealed
        | (position, semanticType) :: rest -> sealOperatorDefaults(label)(rest)((Ashes.Internal.deepCopy(label), position, semanticType) :: sealed)

// How many of the retired locals sit below `local`: the distance a surviving local moves down.
let recursive retiredBelow (retired: List(Int)) (local: Int) =
    match retired with
        | [] -> 0
        | slot :: rest ->
            if slot < local
            then 1 + retiredBelow(rest)(local)
            else retiredBelow(rest)(local)

// The locals a loop retired once its placement settled (an active flag allocated at the loop
// entry for a list the resolved types kept in the arena) are squeezed out of the function's
// slots: every later local moves down, exactly as if the flag had never been allocated, so the
// numbering matches stage 0's, which decides the flag from the types it already knows at entry.
let renumberRetiredLocals (retired: List(Int)) (instructions: List(IrInstruction)) =
    match retired with
        | [] -> instructions
        | _ ->
            Ashes.Collection.List.map((given (instruction: IrInstruction) ->
                instruction with instruction = mapInstructionLocals(given (local: Int) -> local - retiredBelow(retired)(local))(instruction.instruction)))(instructions)

let finishLiftedFunction label origin bodyState =
    match bodyState with
        | CoreLoweringState { reversedInstructions = instructions, functions = functions, nextLocal = localCount, nextTemp = tempCount, functionState = CoreFunctionState { pendingOperatorDefaults = pending, sealedOperatorDefaults = sealed }, ownerState = CoreOwnerState { retiredLocals = retired } } ->
            let function =
                IrFunction(
                    label = label,
                    instructions = instructions
                    |> reverse
                    |> renumberRetiredLocals(retired),
                    localCount = localCount - length(retired),
                    tempCount = tempCount,
                    hasEnvAndArgParams = true,
                    coroutine = None,
                    localNames = [],
                    localTypes = [],
                    origin = Some(origin),
                    lifetimesPlaced = false
                )
            in
                bodyState
                |> (given (current: CoreLoweringState) -> current with functions = function :: functions)
                |> (given (current: CoreLoweringState) -> withStatePendingOperatorDefaults([])(current))
                |> (given (current: CoreLoweringState) ->
                    withStateSealedOperatorDefaults(sealOperatorDefaults(label)(pending)(sealed))(current))
