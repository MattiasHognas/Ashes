// Function types, call contexts, call argument ownership and hand-over, the application of a call,
// and the dropper calls. A part of `AshesCompiler.Semantics.CoreLowering`.

import Ashes.Collection.List.append
import Ashes.Collection.List.length
import Ashes.Collection.List.reverse
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Token.TextSpan
import AshesCompiler.Semantics.CallOwnership
import AshesCompiler.Semantics.CallResultProvenance
import AshesCompiler.Semantics.CoreBuiltinLowering
import AshesCompiler.Semantics.HeapLayoutClassification
import AshesCompiler.Semantics.IrControlFlowGraph.containsInt
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.OwnershipInference.classifyParameterOwnership
import AshesCompiler.Semantics.ResultReachSummaries.ResultReachState
import AshesCompiler.Semantics.ResultReachSummaries.ReachSummary
import AshesCompiler.Semantics.ResultReachSummaries.ReachFunction
import AshesCompiler.Semantics.ResultReachSummaries.singleFunctionReach
import AshesCompiler.Semantics.OwnershipSummary
import AshesCompiler.Semantics.StructuralDroppers
import AshesCompiler.Semantics.ToSpaceCopiers
import AshesCompiler.Semantics.TcoRuntimeManagedParams
import AshesCompiler.Semantics.PatternBindingOwnership
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeSchemes.freeTypeVariables
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.CoreLowering.LoweringState
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.shadowSiteType
import AshesCompiler.Semantics.CoreLowering.Emit
import AshesCompiler.Semantics.CoreLowering.Owners
import AshesCompiler.Semantics.CoreLowering.CopyPlans
import AshesCompiler.Semantics.CoreLowering.TcoReset
import AshesCompiler.Semantics.CoreLowering.ResultHandOver
import AshesCompiler.Semantics.CoreLowering.Bindings
import AshesCompiler.Semantics.CoreLowering.LoopPlacement
import AshesCompiler.Semantics.CoreLowering.LoopFrames
export (
    value recordBorrowedCall,
    type CoreCallContext(..),
    type CoreConsumedArgument(..),
    type CoreCallStage(..),
    value ensureFunctionType,
    value withActiveTraitMethodNames,
    value lowerLambda,
    value ensureResultRcEligibility,
    value preparedReachOfSummary,
    value calleeReachSummary,
    value calleeFactsOf,
    value concreteCopyCallName,
    value isOwnLabelCallee,
    value isSelfCallee,
    value calleeSchemeOf,
    value calleeResultListElementQuantified,
    value callStageOf,
    value isVariableArgument,
    value parameterSlotOfName,
    value ordinalOfSlot,
    value shapeAtOrdinal,
    value parameterSlotAtOrdinal,
    value loopSlotIsRuntimeManaged,
    value readsDemotedLoopList,
    value loopParameterReadSlot,
    value patternBindingRootSlot,
    value loopParameterIsRuntimeManaged,
    value loopParameterPlacedUnblocked,
    value argumentRootSlotOf,
    value isBorrowedRetainableParameterType,
    value rootPlacedAtBackEdge,
    value emitAcceptsRuntimeManagedFlag,
    value emitReturnsRuntimeManagedFlag,
    value isListType,
    value reloadRuntimeSlot,
    value emitConditionalArgumentRetain,
    value hasCallCopyOut,
    value contextResultRuntimeManaged,
    value calleeVerifiedResultRuntimeManaged,
    value calleeReturnsGeneralRcOwned,
    value siblingResultOpen,
    value siblingCallSiteKey,
    value siblingResultPinned,
    value emitRequestWord,
    value isListHeadHandOverType,
    value calleeTupleResultParts,
    value argumentSite,
    value locateArgumentMismatch,
    value lowerCoreCallFunction,
    value emitRuntimeChildDrop,
)

let resolvedFunctionType state argumentType resultType =
    FunctionTypeResolution(
        state = state,
        argumentType = argumentType,
        resultType = resultType,
        error = None
    )

let failedFunctionType state error =
    FunctionTypeResolution(
        state = state,
        argumentType = SemNever,
        resultType = SemNever,
        error = Some(error)
    )

let finishFreshFunctionType semanticType fresh =
    match fresh with
        | FreshFunctionType { state = state, semanticType = functionType, parameterType = argumentType, resultType = resultType } ->
            match bindType(semanticType)(functionType)(state) with
                | (unifiedState, None) -> resolvedFunctionType(unifiedState)(argumentType)(resultType)
                | (failedState, Some(error)) -> failedFunctionType(failedState)(error)

let ensureFunctionType semanticType state =
    match resolveType(state)(semanticType) with
        | SemFunction(argumentType, resultType, _row) -> resolvedFunctionType(state)(argumentType)(resultType)
        | SemVariable(_id) ->
            state
            |> freshFunctionType
            |> finishFreshFunctionType(semanticType)
        | other -> failedFunctionType(state)(CoreCallRequiresFunction(other))

// An expected function type pins the parameter type before the body is lowered; its result type
// meets the body's type through the context's own unification afterwards.
let applyExpectedLambdaType parameterType state =
    match expectedTypeOf(state) with
        | None -> (clearConsumerRequest(state), None)
        | Some(expected) ->
            match state
            |> clearConsumerRequest
            |> ensureFunctionType(expected) with
                | FunctionTypeResolution { state = failedState, error = Some(error) } -> (failedState, Some(error))
                | FunctionTypeResolution { state = functionState, argumentType = argumentType, error = None } -> bindType(argumentType)(parameterType)(functionState)

// The hidden evidence parameters of the generic implementation method being lowered, by binding
// name: a nested lambda captures them alongside its body's free names, since a trait-mapped
// operator inside it reads them without naming them; a capture no instruction reads is pruned
// with the other dead captures.
let recursive activeTraitMethodNames (entries: List((Str, SemanticType, Str, Str))) (names: List(Str)) =
    match entries with
        | [] -> names
        | (_traitName, _typeArgument, _methodName, bindingName) :: rest ->
            if containsName(bindingName)(names)
            then activeTraitMethodNames(rest)(names)
            else activeTraitMethodNames(rest)(bindingName :: names)

let withActiveTraitMethodNames (state: CoreLoweringState) (names: List(Str)) =
    activeTraitMethodNames(stateActiveTraitMethods(state))(names)

// The lambda's origin is decided against the armed let name before the outer frame forgets it:
// the outer continuation must not hand the same name to a later lambda.
let lowerLambda parameter body annotation stackAllocate lower state =
    match state with
        | CoreLoweringState { bindings = outerBindings, nextLambdaId = lambdaId } ->
            match (capturedBindings([]
            |> collectFree(body)([parameter])
            |> withActiveTraitMethodNames(state))(outerBindings)([]), lambdaOriginFor("lambda_" + Ashes.Text.fromInt(lambdaId))(parameter)(state)) with
                | (captures, origin) ->
                    match state
                    |> recordLetLambdaLabel("lambda_" + Ashes.Text.fromInt(lambdaId))
                    |> (given (armed: CoreLoweringState) ->
                        armed
                        |> withStatePendingSourceFunction(None)
                        |> withStatePendingStackClosure(false))
                    |> freshType with
                        | FreshType { state = freshState, semanticType = parameterType } ->
                            match applyExpectedLambdaType(parameterType)(freshState) with
                                | (expectedFailed, Some(error)) -> failure(expectedFailed)(error)
                                | (expectedState, None) ->
                                    match expectedState
                                    |> enterInlineTypeVariables(parameter)
                                    |> lowerLambdaParameterType(annotation)(parameterType) with
                                        | (checkedState, Some(error)) -> failure(checkedState)(error)
                                        | (checkedState, None) ->
                                            lowerLambdaBody(parameter)(body)(stackAllocate)(lower)(lambdaId)(captures)(origin)(expectedLambdaResultType(freshState))(FreshType(state = seedParameterFromConstructorFields(parameter)(parameterType)(body)(checkedState), semanticType = parameterType))

let recursive constructorAritiesOf (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> []
        | (CoreConstructorLayout { name = name } as layout) :: rest -> (name, constructorArity(layout)) :: constructorAritiesOf(rest)

let recursive nullaryConstructorCount (typeName: Str) (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> 0
        | layout :: rest ->
            match constructorResultName(layout) with
                | Some(resultName) ->
                    if resultName == typeName && constructorArity(layout) == 0
                    then 1 + nullaryConstructorCount(typeName)(rest)
                    else nullaryConstructorCount(typeName)(rest)
                | None -> nullaryConstructorCount(typeName)(rest)

// The nullary constructors that are the only nullary constructor of their type.
let recursive soleNullaryConstructorNames (layouts: List(CoreConstructorLayout)) (index: CoreConstructorLookupIndex) =
    match layouts with
        | [] -> []
        | (CoreConstructorLayout { name = name } as layout) :: rest ->
            match constructorResultName(layout) with
                | Some(typeName) ->
                    if constructorArity(layout) == 0 && nullaryConstructorCount(typeName)(lookupResultConstructors(typeName)(index)) == 1
                    then name :: soleNullaryConstructorNames(rest)(index)
                    else soleNullaryConstructorNames(rest)(index)
                | None -> soleNullaryConstructorNames(rest)(index)

let provenanceConstructorsOf (state: CoreLoweringState) =
    (let group = state.programState
    in
        ProvenanceConstructors(
            arities = state
            |> stateConstructorLayouts
            |> constructorAritiesOf,
            soleNullary = state
            |> stateConstructorLayouts
            |> (given (layouts) -> soleNullaryConstructorNames(layouts)(group.constructorIndex))
        ))

// The functions the whole-program provenance fixpoint classifies: every function the reach
// registry registered, under its key, with the registered parameters and innermost body (a
// Map.set-shaped function's outer parameters plus its accumulator over the inner body, as stage
// 0's `_maFuncs` holds them) and the functions in scope of that body.
let recursive provenanceFunctionsOf (summaries: List(ReachSummary)) (functions: List(ProvenanceFunction)) =
    match summaries with
        | [] -> reverse(functions)
        | ReachSummary { function = ReachFunction { key = key, name = name, parameters = parameters, body = body, scope = scope } } :: rest -> provenanceFunctionsOf(rest)(ProvenanceFunction(key = key, name = name, parameters = parameters, body = body, scope = scope) :: functions)

// Stage 0's `IsRuntimeRcFreshBuiltinProducer`: a fully applied call to a builtin declared to produce a
// fresh reference-counted value of any kind, a string, a byte buffer or a big integer.
let isFreshBuiltinProducer (expression: Expr) (state: CoreLoweringState) =
    match qualifiedCallRoot(expression)(0) with
        | Some((moduleName, memberName, argumentCount)) ->
            match builtinLayout(moduleName)(memberName)(state) with
                | Some(CoreBuiltinLayout { kind = kind } as layout) -> isFreshRcBuiltinKind(kind) && argumentCount == builtinArity(layout)
                | None -> false
        | None -> false

let resultProvenanceOf (state: CoreLoweringState) =
    (given (expression: Expr) -> builtinBytesProvenanceOf(expression)(state))
    |> resultProvenanceNodes(provenanceFunctionsOf(stateReachSummaries(state))([]))(provenanceConstructorsOf(state))(given (expression: Expr) -> isFreshBuiltinProducer(expression)(state))
    |> resolvedResultProvenance

// Stage 0's `ComputeFunctionResultProvenanceFixpoint`: the RC-eligibility verdict of every
// registered function of the program, by key, solved once at the first call site that asks (by
// then every type declaration is registered, so a constructor application classifies as the
// direct construction it is) and read for the rest of the lowering.
let ensureResultRcEligibility (state: CoreLoweringState) =
    match stateResultRcEligibility(state) with
        | Some(_eligibility) -> state
        | None ->
            match resultProvenanceOf(state) with
                | (eligibility, _bytes) -> withStateResultRcEligibility(Some(eligibility))(state)

let resultRcEligibilityOf (state: CoreLoweringState) =
    match stateResultRcEligibility(state) with
        | Some(eligibility) -> eligibility
        | None -> Ashes.Collection.Map.empty

// Resolve the same completed function identity as calleeSummaryOf. Only an unregistered
// fallback prepares its local reach at a call site.
let preparedReachOfSummary (summary: ReachSummary) (state: CoreLoweringState) =
    match summary with
        | ReachSummary { function = ReachFunction { key = key, parameters = parameters }, reach = reach } ->
            let group = state.programState
            in
                match Ashes.Collection.Map.getStr(key)(group.callReachByKey) with
                    | Some(prepared) -> prepared
                    | None -> prepareCallReach(parameters)(reach)

let calleePreparedReach (callee: Str) (parameters: List(Str)) (reach: ResultReachState) (state: CoreLoweringState) =
    match calleeSummaryOf(callee)(state) with
        | Some(summary) -> preparedReachOfSummary(summary)(state)
        | None -> prepareCallReach(parameters)(reach)

// Stage 0's ownership summary of a let-bound callee: the whole-program result-reach summary of
// the function recorded under the name, whose parameters and body are the registered ones (a
// Map.set-shaped function's outer parameters plus its accumulator over the inner body); a callee
// outside the program's registry keeps its recorded chain with its reach computed on its own.
let calleeReachSummary (callee: Str) (recordedParameters: List(Str)) (recordedBody: Expr) (state: CoreLoweringState) =
    match calleeSummaryOf(callee)(state) with
        | Some(ReachSummary { function = ReachFunction { parameters = parameters, body = body }, reach = reach }) -> (parameters, body, reach)
        | None -> (recordedParameters, recordedBody, singleFunctionReach(recordedParameters)(recordedBody))

// A callee's RC-eligibility verdict: the fixpoint's answer for the registered function the name
// resolves to, never eligible for a callee outside the registry (stage 0's memo default).
let calleeRcEligible (callee: Str) (state: CoreLoweringState) =
    match calleeSummaryOf(callee)(state) with
        | Some(ReachSummary { function = ReachFunction { key = key } }) ->
            state
            |> resultRcEligibilityOf
            |> lookupRcEligible(key)
        | None -> false

// The name a call to a constrained function's concrete copy binds the copy's closure under: the
// call site's slot and closure temp, then the source function's name after a `%` no source name
// holds, so the call's facts are the source function's (stage 0's
// `BindConstrainedSpecializationRoot`, whose slot carries the copy's label and whose label names
// the source function).
let concreteCopyCallName (slot: Int) (closureTemp: Int) (sourceName: Str) = "__core_concrete_call_" + Ashes.Text.fromInt(slot) + "_" + Ashes.Text.fromInt(closureTemp) + "%" + sourceName

// Stage 0's known-callee resolution for a call spine: a let-bound function called by name
// carries its label, the ownership of its parameters (the single-function verdict overlaid with
// the whole-program fixpoint, as `markCallArgumentsMoved` consults it), and the parameter reach
// of its result; any other callee has no facts.
let calleeFactsOf (spine: CoreCallSpine) (state: CoreLoweringState) =
    match unspanArgument(spine.root) with
        | ExprVar(callee) ->
            match lookupLetLambdaNamed(callee)(state) with
                | Some((recordedParameters, recordedBody)) ->
                    match calleeReachSummary(callee)(recordedParameters)(recordedBody)(state) with
                        | (parameters, body, reach) ->
                            Some(CoreCalleeFacts(
                                label = state
                                |> stateLetLambdaLabels
                                |> lookupLetLambdaLabel(callee),
                                parameters = parameters,
                                ownership = provenParameterOwnership(calleeFactsName(callee))(parameters)(classifyParameterOwnership(parameters)(body)([]))(state),
                                reach = reach,
                                preparedReach = calleePreparedReach(callee)(parameters)(reach)(state),
                                rcEligible = calleeRcEligible(callee)(state),
                                argumentCount = length(spine.arguments)
                            ))
                | None -> None
        | _ -> None

// A callee that is the enclosing recursive binding itself or a group sibling, bound directly in
// the member's outermost stage or captured into an inner curried stage or a nested closure: the
// backend fuses its tail calls into native loops on the adjacency of the call and its return, so
// a call to it in tail position keeps the plain scope rule with no copy-out block after it; a
// self call elsewhere (`bang(head) :: stamp(tail)`) closes its window like any other call.
// A callee bound to its own label in this stage, which the backend fuses in tail position; a
// group sibling reached through the environment is applied like any other closure.
let isOwnLabelCallee (spine: CoreCallSpine) (state: CoreLoweringState) =
    match unspanArgument(spine.root) with
        | ExprVar(callee) ->
            match lookupBinding(callee)(state.bindings) with
                | Some(CoreBinding { location = CoreSelf(_label, _environmentSize) }) -> true
                | _ -> false
        | _ -> false

let isSelfCallee (spine: CoreCallSpine) (state: CoreLoweringState) =
    match unspanArgument(spine.root) with
        | ExprVar(callee) ->
            match lookupBinding(callee)(state.bindings) with
                | Some(CoreBinding { location = CoreSelf(_label, _environmentSize) }) -> true
                | Some(CoreBinding { location = CoreEnvironment(_index) }) ->
                    state
                    |> stateRecursiveGroupNames
                    |> containsLabel(callee)
                | _ -> false
        | _ -> false

// Stage 0's `IsCalleeResultListElementQuantifiedInScheme`: after `argumentCount` applications,
// the callee's declared scheme yields a list whose element is one of the scheme's own quantified
// variables, so the callee builds its result list for every instantiation alike and cannot
// place the elements this call's instantiation needs on the reference-counted heap.
let recursive resultListElementQuantified (quantified: List((Int, Str))) (argumentCount: Int) (body: SemanticType) =
    if argumentCount == 0
    then
        match body with
            | SemList(SemVariable(id)) -> quantifiedContains(id)(quantified)
            | _ -> false
    else
        match body with
            | SemFunction(_parameter, result, _row) -> resultListElementQuantified(quantified)(argumentCount - 1)(result)
            | _ -> false
and quantifiedContains (id: Int) (quantified: List((Int, Str))) =
    match quantified with
        | [] -> false
        | (candidate, _name) :: rest -> candidate == id || quantifiedContains(id)(rest)

// Stage 0's `IsCalleeParameterQuantifiedInScheme`: the callee's declared scheme leaves the
// parameter at `index` one of its own quantified variables, read off the scheme as it was
// generalized rather than through the substitution.
let recursive parameterQuantifiedAt (quantified: List((Int, Str))) (index: Int) (body: SemanticType) =
    match body with
        | SemFunction(parameter, result, _row) ->
            if index == 0
            then
                match parameter with
                    | SemVariable(id) -> quantifiedContains(id)(quantified)
                    | _ -> false
            else parameterQuantifiedAt(quantified)(index - 1)(result)
        | _ -> false

let calleeSchemeOf (spine: CoreCallSpine) (state: CoreLoweringState) =
    match unspanArgument(spine.root) with
        | ExprVar(callee) ->
            match lookupBinding(callee)(state.bindings) with
                | Some(CoreBinding { scheme = scheme }) -> Some(scheme)
                | None -> None
        | _ -> None

let calleeResultListElementQuantified (spine: CoreCallSpine) (state: CoreLoweringState) =
    match unspanArgument(spine.root) with
        | ExprVar(callee) ->
            match lookupBinding(callee)(state.bindings) with
                | Some(CoreBinding { scheme = TypeScheme { quantified = quantified, body = body } }) ->
                    resultListElementQuantified(quantified)(coreListLength(spine.arguments))(body)
                | None -> false
        | _ -> false

// What a spine's stages know about the callee: its facts, whether it is the enclosing recursive
// binding, whether the call to it sits in tail position (the fused self call), and whether its
// declared result is a list over one of its own quantified type variables.
type CoreCallContext =
    | facts: Maybe(CoreCalleeFacts)
    | selfCallee: Bool
    | tailCall: Bool
    | resultElementQuantified: Bool
    | calleeName: Maybe(Str)
    | argumentCount: Int
    // The type the consumer asked the whole spine for, read before the general call cleared the
    // consumer request.
    | expectedResult: Maybe(SemanticType)
    // The declared scheme of a callee named by a plain variable.
    | calleeScheme: Maybe(TypeScheme)
    // The label a lambda applied where it is written (a pipe stage) is lowered to: the spine's
    // callee is the lambda itself, whose compiled result the call reads.
    | appliedLambdaLabel: Maybe(Str)
    // The span the call is lowered under, the site stage 0's `CallResultTypeKey` names it by.
    | siteSpan: Maybe(TextSpan)
    // Stage 0's `_discardedResultCalls`: an applied lambda discards this call's result.
    | resultDiscarded: Bool
    // The spine's arguments, first applied first.
    | arguments: List(Expr)
    // The call is itself an ordinary call's argument (the consumer request's `callArgument`).
    | resultIsCallArgument: Bool

// A self callee whose result type was unresolved when the body being lowered began: its call's
// result is deferred (`deferSelfCallResultType`), so the call asks for an arena result without
// asking for the body to be lowered again once that type resolves.
let selfResultDeferred (context: CoreCallContext) (state: CoreLoweringState) =
    match context.calleeName with
        | Some(name) ->
            context.selfCallee && containsLabel(name)(stateSelfResultDeferredNames(state))
        | None -> false

// A fresh reference-counted argument the callee did not take, released after the call; the
// release preserves the argument's escaped children when the callee's result may keep them.
type CoreConsumedArgument =
    | temp: Int
    | semanticType: SemanticType
    | preserveEscapedChildren: Bool
    // The callee's own adoption bit when this reference was handed over for its result to keep, or -1 for
    // an ordinary fresh argument the caller releases outright. An adopting callee owns the reference; a
    // non-adopting one gives it up only where the result kept nothing of it.
    | adoptionFlagTemp: Int

// One spine stage's result with the fresh arguments applied so far and the returns-bit flag
// temp the last application read (`-1` for none).
// `resultNormalized` says the closed window copied the result out (a copy-out or deep copy,
// unconditionally or on the arena branch of the returns flag), so nothing reachable from it
// still borrows the call's consumed arguments; `resultDeepCopied` narrows that to the generic
// list deep copy, which copies every element and its owned parts.
type CoreCallStage =
    | lowered: LoweredCoreValue
    | consumedArguments: List(CoreConsumedArgument)
    | resultFlagTemp: Int
    | resultNormalized: Bool
    | resultDeepCopied: Bool
    // The result was normalized into an owned value on both of its branches, so it holds a
    // handed-over argument only as itself.
    | resultNormalizedOwned: Bool
    // The result is one of the contract's types, kept in an owned slot (stage 0's `generalRcResult`).
    | resultGeneralRc: Bool

let callStageOf (lowered: LoweredCoreValue) =
    CoreCallStage(
        lowered = lowered,
        resultNormalized = false,
        resultDeepCopied = false,
        resultNormalizedOwned = false,
        consumedArguments = [],
        resultFlagTemp = -1,
        resultGeneralRc = false
    )

// The argument position of the application `arity` stages from the end of the spine.
let argumentIndexOf (facts: Maybe(CoreCalleeFacts)) (arity: Int) =
    match facts with
        | Some(CoreCalleeFacts { argumentCount = count }) -> count - arity
        | None -> -1

let isVariableArgument (argument: Expr) =
    match unspanArgument(argument) with
        | ExprVar(_name) -> true
        | _ -> false

// Whether the argument names a binding that still owns a reference-counted value, or aliases a
// live owner of one.
let namesRuntimeOwner (argument: Expr) (state: CoreLoweringState) =
    match unspanArgument(argument) with
        | ExprVar(name) ->
            match liveRuntimeOwnerSlot(name)(state) with
                | Some(_slot) -> true
                | None -> false
        | _ -> false

// Stage 0's `TryGetRuntimeManagedCallArgument` outside a TCO frame: the argument temp holds a
// reference-counted value, or the argument names a binding that owns one.
let isRuntimeManagedCallArgument (argument: Expr) (argumentTemp: Int) (state: CoreLoweringState) = isRuntimeTemp(argumentTemp)(state) || namesRuntimeOwner(argument)(state)

// A freshly produced reference-counted argument rather than a binding read: the caller
// releases it after the call unless the callee takes it.
let isFreshRuntimeArgument (argument: Expr) (argumentTemp: Int) (state: CoreLoweringState) = isVariableArgument(argument) == false && isRuntimeTemp(argumentTemp)(state)

// Whether the callee normalizes the parameter at `index` on entry, stage 0's
// `IsKnownRuntimeNormalizedFunctionArgument`: such a callee adopts a fresh argument outright
// through the ownership flag. A later curried position is the stage that many returned closures
// past the callee's own label.
let calleeNormalizesArgument (facts: Maybe(CoreCalleeFacts)) (index: Int) (state: CoreLoweringState) =
    match facts with
        | Some(CoreCalleeFacts { label = Some(label) }) ->
            match state
            |> stateFunctionReturnedClosureLabels
            |> innermostStageLabel(index)(label) with
                | Some(stage) ->
                    state
                    |> stateRuntimeNormalizedArgumentLabels
                    |> containsLabel(stage)
                | None -> false
        | _ -> false

// Stage 0's `CalleeResultMayReachParameter`: the argument holds a reference-counted value the
// callee's result may keep alive past the call.
let argumentMayReachResult (facts: Maybe(CoreCalleeFacts)) (index: Int) (argumentTemp: Int) (state: CoreLoweringState) = isRuntimeTemp(argumentTemp)(state) && calleeResultReachesArgument(facts)(index)

// Stage 0's `CalleeResultMayKeepParameterWhole`: the argument holds a reference-counted value
// the callee's result may keep whole.
let argumentReachesResultWhole (facts: Maybe(CoreCalleeFacts)) (index: Int) (argumentTemp: Int) (state: CoreLoweringState) = isRuntimeTemp(argumentTemp)(state) && calleeResultReachesArgumentWhole(facts)(index)

// Stage 0's `TransfersFreshRuntimeArgument`: a fresh argument moves into a callee that
// normalizes it on entry or whose result keeps it whole, so the caller neither retains nor
// releases it; a callee keeping only destructured parts leaves the caller's spine release.
let transfersFreshArgument facts index argument argumentTemp state = calleeParameterBorrows(facts)(index) == false && isFreshRuntimeArgument(argument)(argumentTemp)(state) && (calleeNormalizesArgument(facts)(index)(state) || argumentReachesResultWhole(facts)(index)(argumentTemp)(state))

let parameterSlotOfName (name: Str) (frame: CoreTcoLoopFrame) (state: CoreLoweringState) =
    match lookupBinding(name)(state.bindings) with
        | Some(CoreBinding { location = CoreLocal(slot) }) ->
            if containsInt(slot)(frame.parameterSlots)
            then Some(slot)
            else None
        | _ -> None

let recursive ordinalOfSlot (slot: Int) (slots: List(Int)) (ordinal: Int) =
    match slots with
        | [] -> None
        | candidate :: rest ->
            if candidate == slot
            then Some(ordinal)
            else ordinalOfSlot(slot)(rest)(ordinal + 1)

let recursive shapeAtOrdinal (ordinal: Int) (shapes: List(TcoArgumentShape)) =
    match shapes with
        | [] -> TcoOtherShape
        | shape :: rest ->
            if ordinal == 0
            then shape
            else shapeAtOrdinal(ordinal - 1)(rest)

let recursive parameterSlotAtOrdinal (ordinal: Int) (slots: List(Int)) =
    match slots with
        | [] -> None
        | slot :: rest ->
            if ordinal == 0
            then Some(slot)
            else parameterSlotAtOrdinal(ordinal - 1)(rest)

// A loop parameter slot is a runtime-managed value when its resolved type and self-call shape
// admit it to reference-counted placement (an ADT cell, or a `Str` the affine analysis
// manages): a cell built around it may then live on the reference-counted heap. The finalize
// pass places the parameter or demotes the whole frame; a demoted frame never reclaims the arena
// at its back edge, so a cell placed early holds no dangling reference.
// Whether a loop parameter slot is admitted to the reference-counted heap at this point of the
// body (stage 0's `IsRuntimeManagedTcoParamSlot`): a copy-ADT slot by its resolved type and
// shape, a list slot by its shape and element type, a `Str` slot by the affine analysis.
// Stage 0's `IsRuntimeManagedTcoParamSlot` reads a placement its evaluation already demoted when
// a sibling parameter blocks the slot (`FindBlockingSiblingForCandidate`): an ADT slot its type
// admits is on the reference-counted heap only when no sibling keeps it off, as the frame's
// finalize decides. A retain taken for a slot admitted by its type alone would never be balanced.
let recursive candidateAdtManagedAt (slot: Int) (candidates: List(TcoManagedCandidate)) =
    match candidates with
        | [] -> false
        | candidate :: rest ->
            if candidate.slot == slot
            then candidateAdtManaged(candidate)
            else candidateAdtManagedAt(slot)(rest)

let adtSlotBlockedBySibling (slot: Int) (frame: CoreTcoLoopFrame) (loop: CoreTcoLoop) (state: CoreLoweringState) =
    state
    |> tcoManagedCandidates(0)(frame.parameterSlots)(loop.argumentShapes)(loop)
    |> (given (candidates: List(TcoManagedCandidate)) ->
        anyBlockingSibling(candidates)(state) && !candidateAdtManagedAt(slot)(demoteBlockedCandidates(candidates)(candidates)(loop)(state)))

// Whether the list candidate of `slot` survives the sibling demotion the loop's finalize pass makes
// (`unblockedCandidates`): a list a heap-typed sibling keeps in the arena is never normalized at the
// loop's entry, so reading it as reference-counted would retain or release the caller's borrowed
// value.
let recursive listCandidateSurvives (slot: Int) (candidates: List(TcoManagedCandidate)) =
    match candidates with
        | [] -> false
        | TcoManagedCandidate { slot = candidateSlot, listElement = Some(_element) } :: rest -> candidateSlot == slot || listCandidateSurvives(slot)(rest)
        | _candidate :: rest -> listCandidateSurvives(slot)(rest)

let listSlotUnblocked (slot: Int) (frame: CoreTcoLoopFrame) (loop: CoreTcoLoop) (state: CoreLoweringState) =
    state
    |> tcoManagedCandidates(0)(frame.parameterSlots)(loop.argumentShapes)(loop)
    |> (given (candidates: List(TcoManagedCandidate)) ->
        state
        |> unblockedCandidates(candidates)(loop)
        |> listCandidateSurvives(slot))

let loopSlotIsRuntimeManaged (slot: Int) (frame: CoreTcoLoopFrame) (loop: CoreTcoLoop) (state: CoreLoweringState) =
    match ordinalOfSlot(slot)(frame.parameterSlots)(0) with
        | Some(ordinal) ->
            let shape = shapeAtOrdinal(ordinal)(loop.argumentShapes)
            in ordinaryRcPlacementAllowed(state) && (tcoAdtSlotAdmitted(ordinal)(slot)(shape)(loop)(state) && !adtSlotBlockedBySibling(slot)(frame)(loop)(state) || isTcoListShape(shape) && tcoListSlotElement(slot)(shape)(ordinal)(loop)(state) != None || isAccumulatorRebuildListSlot(ordinal)(slot)(loop)(state) || containsInt(ordinal)(loop.runtimeManagedOrdinals) && isTcoRuntimeManagedStrSlot(slot)(state))
        | None -> false

// The loop parameter an aggregate child reads: the parameter itself, or a record field read out
// of it (`s.label`, a qualified name whose module part is the parameter's binding).
let loopParameterReadSlot (argument: Expr) (state: CoreLoweringState) =
    match stateTcoLoopFrame(state) with
        | None -> None
        | Some(frame) ->
            match unspanArgument(argument) with
                | ExprVar(name) -> parameterSlotOfName(name)(frame)(state)
                | ExprQualifiedVar(owner, _field) -> parameterSlotOfName(owner)(frame)(state)
                | _ -> None

// The loop parameter slot a pattern binding was extracted from, by its recorded fact.
let patternBindingRootSlot (name: Str) (frame: CoreTcoLoopFrame) (state: CoreLoweringState) =
    match patternBindingFact(name)(state) with
        | Some(PatternBindingFact { rootParameterOrdinal = ordinal }) when ordinal >= 0 -> parameterSlotAtOrdinal(ordinal)(frame.parameterSlots)
        | _ -> None

// A read of a loop parameter the frame places on the reference-counted heap by now, or of a
// pattern binding extracted from such a parameter (stage 0 tracks the binding as a
// runtime-managed owner under its admitted root), holds a reference-counted value.
let loopParameterIsRuntimeManaged (name: Str) (state: CoreLoweringState) =
    match (stateTcoLoopFrame(state), stateTcoLoop(state)) with
        | (Some(frame), Some(loop)) ->
            match parameterSlotOfName(name)(frame)(state) with
                | Some(slot) -> loopSlotIsRuntimeManaged(slot)(frame)(loop)(state)
                | None ->
                    match patternBindingRootSlot(name)(frame)(state) with
                        | Some(rootSlot) -> loopSlotIsRuntimeManaged(rootSlot)(frame)(loop)(state)
                        | None -> false
        | _ -> false

// Stage 0's `IsRuntimeManagedTcoParamSlot` as a join arm reads it (`BranchJoinsRuntimeManagedResult`):
// a list parameter, or a pattern binding taken out of one, is on the reference-counted heap only
// when no sibling keeps the list in the arena, which stage 0's placement has already decided by
// then (`FindBlockingSiblingForCandidate`) and stage 1's finalize decides later.
let loopParameterPlacedUnblocked (name: Str) (state: CoreLoweringState) =
    match (stateTcoLoopFrame(state), stateTcoLoop(state)) with
        | (Some(frame), Some(loop)) ->
            match match parameterSlotOfName(name)(frame)(state) with
                | Some(slot) -> Some(slot)
                | None -> patternBindingRootSlot(name)(frame)(state) with
                | Some(slot) ->
                    if loopSlotIsRuntimeManaged(slot)(frame)(loop)(state)
                    then
                        match slotResolvedType(slot)(state.bindings)(state) with
                            | Some(resolved) ->
                                match resolveType(state)(resolved) with
                                    | SemList(_element) -> listSlotUnblocked(slot)(frame)(loop)(state)
                                    | _ -> true
                            | None -> true
                    else false
                | None -> false
        | _ -> false

// Stage 0's `TryGetRuntimeManagedCallArgument` inside a TCO frame: an argument that reads a loop
// parameter, or a pattern binding extracted from one, holds a reference-counted value once the
// parameter is placed on the reference-counted heap. The placement is the finalize pass's to
// decide (the per-slot admission read during the body can still be demoted with the frame), so
// every such argument is pending under the parameter's slot: the ownership flag emitted for it
// is zeroed at finalize when the frame does not place the parameter, and stays the callee's
// accepts bit when it does.
let argumentRootSlotOf (argument: Expr) (state: CoreLoweringState) =
    match (stateTcoLoopFrame(state), stateTcoLoop(state), unspanArgument(argument)) with
        | (Some(frame), Some(loop), ExprVar(name)) ->
            match parameterSlotOfName(name)(frame)(state) with
                | Some(slot) -> Some(slot)
                | None -> patternBindingRootSlot(name)(frame)(state)
        | _ -> None

// The root parameter slot of an argument that is a pattern binding extracted from a loop
// parameter, stage 0's `TryGetRuntimeManagedPatternBindingArgument`: a read of the parameter
// itself has no such root.
let patternBindingArgumentRootSlot (argument: Expr) (state: CoreLoweringState) =
    match (stateTcoLoopFrame(state), stateTcoLoop(state), unspanArgument(argument)) with
        | (Some(frame), Some(loop), ExprVar(name)) ->
            match parameterSlotOfName(name)(frame)(state) with
                | Some(_slot) -> None
                | None -> patternBindingRootSlot(name)(frame)(state)
        | _ -> None

// One application's hand-off decisions, stage 0's `LowerAppliedClosureCall` facts.
type CoreArgumentHandOff =
    | borrowsOnly: Bool
    | fresh: Bool
    | runtimeArgument: Bool
    | mayReach: Bool
    | transfers: Bool
    // The callee normalizes the argument on entry, so a transferred argument is adopted outright
    // rather than handed over under the callee's adoption bit.
    | normalizes: Bool
    // The argument reads a loop parameter or a pattern binding of one, whose reference the
    // loop's exit and back edge release, and the callee's result may keep parts of it.
    | borrowedReach: Bool
    | pendingRootSlot: Maybe(Int)
    // The callee's result provably never holds this argument itself, so a fresh one is released in
    // the ordinary way after the call rather than handed over under the adoption bit.
    | cannotBeKeptWhole: Bool
    // A fresh argument the callee may return as its result, handed to a callee not known to adopt
    // it, with the result owned on every path: the caller keeps the reference and releases it after
    // the call unless the result is that argument, instead of giving it up for good.
    | keptWholeUnderOwnedResult: Bool
    // Stage 0 passes the argument borrowed, so lifetime placement gives the call no compensating
    // duplicate of an owner that stays live past it. A named binding of the contract's types is
    // borrowed even where the hand-off itself cannot change (`borrowsOnly` skips the type test there).
    | borrowsForPlacement: Bool

// Stage 0's `IsBorrowedRetainableParameterType`: a string, or a type the parameter passthrough
// normalizes, whose retained reference the caller can release outright.
let isBorrowedRetainableParameterType (argumentType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(argumentType) with
        | SemString -> true
        | _ -> isPassthroughNormalizableParameterType(argumentType)(state)

// Stage 0's `KeptWholeUnderOwnedResult`.
let keptWholeUnderOwnedResult facts index argument argumentTemp (resultNormalizedOwned: Bool) state = resultNormalizedOwned && calleeParameterBorrows(facts)(index) == false && isFreshRuntimeArgument(argument)(argumentTemp)(state) && calleeNormalizesArgument(facts)(index)(state) == false && argumentReachesResultWhole(facts)(index)(argumentTemp)(state)

// Stage 0's `IsRuntimeManagedSlot` after `LowerCallTcoPromoteResolvedRuntimeParams`: the
// placement the finalize pass makes for a root, evaluated at the back edge from the parameters'
// resolved types.
let rootPlacedAtBackEdge (rootSlot: Int) (frame: CoreTcoLoopFrame) (loop: CoreTcoLoop) (state: CoreLoweringState) =
    match lookupListActiveSlot(rootSlot)(frame.listActiveSlots) with
        | None -> false
        | Some(_activeSlot) ->
            state
            |> tcoManagedCandidates(0)(frame.parameterSlots)(loop.argumentShapes)(loop)
            |> (given (candidates: List(TcoManagedCandidate)) ->
                state
                |> unblockedCandidates(candidates)(loop)
                |> (given (unblocked: List(TcoManagedCandidate)) ->
                    managedSlotResult(rootSlot)(tcoRuntimeManagedListSlotsOf(unblocked)(frame.listActiveSlots))(tcoRuntimeManagedAdtSlotsOf(unblocked)(frame.listActiveSlots))([]) || !anyBlockingSibling(candidates)(state) && candidateStrManaged(rootSlot)(candidates)))

// A direct read of a loop parameter stage 0 places on the reference-counted heap at the loop's
// entry, from its static facts and resolved type (an accumulator rebuild, or a list of scalars
// handed on and rebuilt fresh), admitted by now, or an ADT parameter its type admits and no sibling
// blocks, the placement stage 0 settles at the loop entry and re-evaluates at each back edge: stage
// 0 reads it as a runtime-managed owner, so the argument is reference-counted rather than pending
// on the finalize pass.
let readsEntryPlacedLoopParameter (argument: Expr) (state: CoreLoweringState) =
    match (unspanArgument(argument), stateTcoLoopFrame(state), stateTcoLoop(state)) with
        | (ExprVar(name), Some(frame), Some(loop)) ->
            match parameterSlotOfName(name)(frame)(state) with
                | Some(slot) ->
                    match ordinalOfSlot(slot)(frame.parameterSlots)(0) with
                        | Some(ordinal) ->
                            let shape = shapeAtOrdinal(ordinal)(loop.argumentShapes)
                            in (isAccumulatorRebuildListSlot(ordinal)(slot)(loop)(state) || (shape == TcoRefilledFreshListShape || shape == TcoHandedOnFreshListShape) && tcoAdtSlotAdmitted(ordinal)(slot)(shape)(loop)(state)) && loopSlotIsRuntimeManaged(slot)(frame)(loop)(state) || rootPlacedAtBackEdge(slot)(frame)(loop)(state)
                        | None -> false
                | None -> false
        | _ -> false

// A pattern binding extracted from a loop parameter stage 0 places on the reference-counted heap
// at the loop's entry: stage 0's `TryGetRuntimeManagedPatternBindingArgument` leaves no pending
// slot for it, its root's placement being settled already.
let readsEntryPlacedPatternRoot (argument: Expr) (state: CoreLoweringState) =
    match (stateTcoLoopFrame(state), stateTcoLoop(state)) with
        | (Some(frame), Some(loop)) ->
            match patternBindingArgumentRootSlot(argument)(state) with
                | Some(rootSlot) -> rootPlacedAtBackEdge(rootSlot)(frame)(loop)(state)
                | None -> false
        | _ -> false

// A read of a list loop parameter the finalize pass keeps in the arena for a heap-typed sibling's
// sake: its placement is pending, as stage 0 leaves it, so its retain follows the flag the finalize
// pass settles rather than being taken outright on a value that may be an arena list.
let readsDemotedLoopList (argument: Expr) (state: CoreLoweringState) =
    match (unspanArgument(argument), stateTcoLoopFrame(state), stateTcoLoop(state)) with
        | (ExprVar(name), Some(frame), Some(loop)) ->
            match parameterSlotOfName(name)(frame)(state) with
                | Some(slot) ->
                    match slotResolvedType(slot)(state.bindings)(state) with
                        | Some(resolved) ->
                            match resolveType(state)(resolved) with
                                | SemList(_element) -> !listSlotUnblocked(slot)(frame)(loop)(state)
                                | _ -> false
                        | None -> false
                | None -> false
        | _ -> false

// Stage 0 registers a lone recursive binding's label with its ownership summary only once its body
// is lowered, so within that body the binding's call to itself reads no summary and its callee
// result never forces a retain (`CalleeResultMayReachOrKeepPatternBinding` and
// `CalleeResultMayReachParameter` both answer false); a member of a recursive group is registered
// before its body is lowered and keeps its summary. The closure such a self call goes through is
// built without the `AcceptsRuntimeManagedArgument` bit while the body adopts an argument handed
// over with the ownership flag set, so a forced retain there is released twice: once by the
// callee's exit and once by the caller's owned slot.
let selfCallReadsNoSummary (selfCallee: Bool) (state: CoreLoweringState) =
    selfCallee && length(stateRecursiveGroupNames(state)) == 1

// Stage 0's `CalleeResultMayReachOrKeepPatternBinding`: a pattern binding of a loop parameter
// the callee's result may keep is retained outright, its flag registered as pending. A value of
// the contract's types is always passed borrowed, whatever the callee's own summary says; the type
// test only runs for an argument whose hand-off it can change (a reference-counted one, one read
// from a loop parameter, or a fresh one), since it walks the type and every call argument passes
// here. A function's call to itself has no summary yet (stage 0's `GetOwnershipSummaryForCallRoot`
// answers nothing for a bound name), so no reach of it retains a borrowed loop argument.
let argumentHandOffOf facts (selfCallee: Bool) index argument argumentType argumentTemp (resultNormalizedOwned: Bool) state =
    match (isRuntimeManagedCallArgument(argument)(argumentTemp)(state) || readsEntryPlacedLoopParameter(argument)(state) || readsEntryPlacedPatternRoot(argument)(state), argumentRootSlotOf(argument)(state), isFreshRuntimeArgument(argument)(argumentTemp)(state)) with
        | (runtimeArgument, rootSlot, fresh) ->
            let calleeBorrows = calleeParameterBorrows(facts)(index)
            in
                match if !calleeBorrows && (runtimeArgument || rootSlot != None || fresh || isVariableArgument(argument))
                then isGeneralRcValueTypeIndexed(argumentType)(state)
                else (false, state) with
                    | (contractValue, queried) ->
                        let contractBorrow = !calleeBorrows && (runtimeArgument || rootSlot != None || fresh) && contractValue
                        in
                            let keptWhole = !contractBorrow && keptWholeUnderOwnedResult(facts)(index)(argument)(argumentTemp)(resultNormalizedOwned)(queried)
                            in
                                (CoreArgumentHandOff(
                                    borrowsOnly = contractBorrow || calleeBorrows,
                                    borrowsForPlacement = contractBorrow || calleeBorrows || isVariableArgument(argument) && contractValue,
                                    fresh = fresh,
                                    runtimeArgument = runtimeArgument || rootSlot != None,
                                    mayReach = !selfCallReadsNoSummary(selfCallee)(queried) && (argumentMayReachResult(facts)(index)(argumentTemp)(queried) || (patternBindingArgumentRootSlot(argument)(queried) != None || rootSlot != None && isBorrowedRetainableParameterType(argumentType)(queried)) && calleeResultReachesArgument(facts)(index)),
                                    transfers = !contractBorrow && !keptWhole && transfersFreshArgument(facts)(index)(argument)(argumentTemp)(queried),
                                    keptWholeUnderOwnedResult = keptWhole,
                                    normalizes = calleeNormalizesArgument(facts)(index)(queried),
                                    borrowedReach = rootSlot != None && !selfCallee && calleeResultReachesArgument(facts)(index),
                                    cannotBeKeptWhole = calleeResultCannotKeepArgumentWhole(facts)(index),
                                    pendingRootSlot = if runtimeArgument && !readsDemotedLoopList(argument)(queried)
                                    then None
                                    else rootSlot
                                ), queried)

// The callee's `AcceptsRuntimeManagedArgument` bit, bit 62 of the closure's packed environment
// size word, in a fresh flag temp.
let emitAcceptsRuntimeManagedFlag (closureTemp: Int) (state: CoreLoweringState) =
    match freshTempRun(5)(state) with
        | FreshTemp { state = reserved, temp = sizeTemp } ->
            reserved
            |> emit(LoadMemOffset(sizeTemp)(closureTemp)(16))
            |> emit(LoadConstInt(sizeTemp + 1)(62))
            |> emit(ShrInt(sizeTemp + 2)(sizeTemp)(sizeTemp + 1))
            |> emit(LoadConstInt(sizeTemp + 3)(1))
            |> emit(AndInt(sizeTemp + 4)(sizeTemp + 2)(sizeTemp + 3))
            |> (given (flagged) -> (flagged, sizeTemp + 4))

// The callee's `ReturnsRuntimeManaged` bit, bit 63 of the same word.
let emitReturnsRuntimeManagedFlag (closureTemp: Int) (state: CoreLoweringState) =
    match freshTempRun(3)(state) with
        | FreshTemp { state = reserved, temp = sizeTemp } ->
            reserved
            |> emit(LoadMemOffset(sizeTemp)(closureTemp)(16))
            |> emit(LoadConstInt(sizeTemp + 1)(63))
            |> emit(ShrInt(sizeTemp + 2)(sizeTemp)(sizeTemp + 1))
            |> (given (flagged) -> (flagged, sizeTemp + 2))

let isListType (semanticType: SemanticType) =
    match semanticType with
        | SemList(_element) -> true
        | _ -> false

// Stage 0's `EmitRuntimeManagedArgumentRetain`: the argument's reference is duplicated into a
// fresh reference-counted temp, null-tolerantly for a list.
let emitArgumentRetain (argumentTemp: Int) (argumentType: SemanticType) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = retainState, temp = retainedTemp } ->
            retainState
            |> emit(argumentType
            |> resolveType(state)
            |> isListType
            |> RcDup(retainedTemp)(argumentTemp)(true))
            |> markRuntimeTemp(retainedTemp)(RuntimeNewlyProduced)
            |> (given (retained) -> (retained, retainedTemp))

// Reloads a slot's reference-counted value into a fresh newly produced temp.
let reloadRuntimeSlot (slot: Int) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = reloadState, temp = reloadTemp } ->
            reloadState
            |> emit(LoadLocal(reloadTemp)(slot))
            |> markRuntimeTemp(reloadTemp)(RuntimeNewlyProduced)
            |> (given (reloaded) -> (reloaded, reloadTemp))

// Stage 0's `EmitConditionallyRetainedRuntimeArgument`: the argument is routed through a slot
// the retain path overwrites with the duplicate when the callee adopts runtime-managed
// arguments, and the slot's value is what the call receives.
let emitConditionalArgumentRetain (argumentTemp: Int) (argumentType: SemanticType) (flagTemp: Int) (state: CoreLoweringState) =
    match freshLocal(state) with
        | FreshLocal { state = allocated, local = slot } ->
            match allocated
            |> emit(StoreLocal(slot)(argumentTemp))
            |> freshLabel("rc_call_argument_not_retained") with
                | FreshLabel { state = labelled, label = doneLabel } ->
                    match labelled
                    |> emit(JumpIfFalse(flagTemp)(doneLabel))
                    |> emitArgumentRetain(argumentTemp)(argumentType) with
                        | (retained, retainedTemp) ->
                            retained
                            |> emit(StoreLocal(slot)(retainedTemp))
                            |> emit(Label(doneLabel))
                            |> reloadRuntimeSlot(slot)

let retainCallArgument (handOff: CoreArgumentHandOff) argumentType argumentTemp flagTemp state =
    match handOff with
        | CoreArgumentHandOff { mayReach = true, fresh = false, pendingRootSlot = None } -> emitArgumentRetain(argumentTemp)(argumentType)(state)
        | _ -> emitConditionalArgumentRetain(argumentTemp)(argumentType)(flagTemp)(state)

// Stage 0's `EmitForcedRetainFlag`: the retain guard of a pending argument the callee's result
// may keep defaults to one; finalize zeroes it only for an unadmitted root.
let emitForcedRetainFlag (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = allocated, temp = flagTemp } ->
            (emit(LoadConstInt(flagTemp)(1))(allocated), flagTemp)

// The flag of a pending argument, registered under the loop parameter slot whose placement the
// finalize pass decides (stage 0's `_pendingRuntimeArgumentFlags`).
let pendingArgumentFlag (handOff: CoreArgumentHandOff) (rootSlot: Int) (acceptsFlagTemp: Int) (state: CoreLoweringState) =
    match if handOff.mayReach
    then emitForcedRetainFlag(state)
    else (state, acceptsFlagTemp) with
        | (flagged, flagTemp) -> (withStatePendingRuntimeArgumentFlags((flagTemp, rootSlot) :: statePendingRuntimeArgumentFlags(flagged))(flagged), flagTemp)

// Stage 0's `EmitPendingRetainAdoptionFlag`: a reference retained under a pending parameter's
// forced flag counts as adopted when the callee's accepts bit reads true, and as nothing to
// release when the finalize pass zeroed the forced flag for a parameter kept in the arena.
let emitPendingRetainAdoptionFlag (acceptsFlagTemp: Int) (forcedFlagTemp: Int) (state: CoreLoweringState) =
    match freshTempRun(3)(state) with
        | FreshTemp { state = reserved, temp = zeroTemp } ->
            reserved
            |> emit(LoadConstInt(zeroTemp)(0))
            |> emit(CmpIntEq(zeroTemp + 1)(forcedFlagTemp)(zeroTemp))
            |> emit(OrInt(zeroTemp + 2)(acceptsFlagTemp)(zeroTemp + 1))
            |> (given (flagged) -> (flagged, zeroTemp + 2))

// The adoption flag of a pending argument the callee's result may keep, emitted right after its
// forced flag; an argument the result cannot keep is not handed over.
let pendingRetainAdoption (handOff: CoreArgumentHandOff) (acceptsFlagTemp: Int) (flagTemp: Int) (argumentType: SemanticType) (state: CoreLoweringState) =
    if handOff.mayReach && isBorrowedRetainableParameterType(argumentType)(state)
    then
        match emitPendingRetainAdoptionFlag(acceptsFlagTemp)(flagTemp)(state) with
            | (adopted, adoptionTemp) -> (adopted, Some(adoptionTemp))
    else (state, None)

let handedOverWith (passedTemp: Int) (adoption: Maybe(Int)) =
    match adoption with
        | Some(adoptionTemp) -> Some((passedTemp, adoptionTemp))
        | None -> None

// Stage 0's `PrepareRuntimeManagedCallArgument`: a borrowed parameter or an argument without a
// reference-counted value passes as is without a flag. Otherwise the callee's accepts bit is
// read, and the argument passes unchanged when it moves into the callee, retained
// unconditionally when the callee's result may keep a named binding, and retained under the bit
// otherwise. Yields the state, the temp the call receives, and the flag temp (`-1` for none).
// Stage 0's `RetainBorrowedLoopArgumentForCalleeResult`: a loop parameter, or a pattern binding
// of one, handed to a callee that only borrows it while its result keeps parts of it is retained
// for the result and handed over like a consumed argument the callee never adopts, so the caller
// releases it where the result was copied out; an admission still pending guards the retain and
// the release with the parameter's flag. Yields the state, the temp the call receives, and the
// handed-over reference with its adoption flag.
let retainBorrowedLoopArgument (rootSlot: Maybe(Int)) argumentType argumentTemp state =
    match rootSlot with
        | Some(slot) ->
            match emitForcedRetainFlag(state) with
                | (flagged, flagTemp) ->
                    match flagged
                    |> withStatePendingRuntimeArgumentFlags((flagTemp, slot) :: statePendingRuntimeArgumentFlags(flagged))
                    |> emitConditionalArgumentRetain(argumentTemp)(argumentType)(flagTemp) with
                        | (retained, passedTemp) ->
                            match freshTemp(retained) with
                                | FreshTemp { state = zeroState, temp = zeroTemp } ->
                                    match freshTemp(zeroState) with
                                        | FreshTemp { state = adoptionState, temp = adoptionTemp } ->
                                            (adoptionState
                                            |> emit(LoadConstInt(zeroTemp)(0))
                                            |> emit(CmpIntEq(adoptionTemp)(flagTemp)(zeroTemp)), passedTemp, Some((passedTemp, adoptionTemp)))
        | None ->
            match emitArgumentRetain(argumentTemp)(argumentType)(state) with
                | (retained, passedTemp) ->
                    match freshTemp(retained) with
                        | FreshTemp { state = adoptionState, temp = adoptionTemp } ->
                            (emit(LoadConstInt(adoptionTemp)(0))(adoptionState), passedTemp, Some((passedTemp, adoptionTemp)))

// A copy-type argument has no reference count to take, though it can still name a runtime owner:
// a scalar field bound out of a reference-counted list element is a pattern binding rooted at that
// list, and a callee that returns its parameter reads as one whose result keeps it. It passes
// under the flag it would have had, with nothing retained and nothing handed over.
let prepareCopyTypeArgument (handOff: CoreArgumentHandOff) functionTemp argumentTemp state =
    match emitAcceptsRuntimeManagedFlag(functionTemp)(state) with
        | (flagged, acceptsFlagTemp) ->
            match handOff.pendingRootSlot with
                | Some(rootSlot) ->
                    match pendingArgumentFlag(handOff)(rootSlot)(acceptsFlagTemp)(flagged) with
                        | (registered, flagTemp) -> (registered, argumentTemp, flagTemp, None)
                | None -> (flagged, argumentTemp, acceptsFlagTemp, None)

let prepareCallArgument (handOff: CoreArgumentHandOff) (handsFreshOver: Bool) argumentType functionTemp argumentTemp state =
    match handOff with
        | CoreArgumentHandOff { borrowsOnly = true, fresh = false, borrowedReach = true, pendingRootSlot = pendingRootSlot } ->
            if isBorrowedRetainableParameterType(argumentType)(state)
            then
                match retainBorrowedLoopArgument(pendingRootSlot)(argumentType)(argumentTemp)(state) with
                    | (retained, passedTemp, handedOver) -> (retained, passedTemp, -1, handedOver)
            else (state, argumentTemp, -1, None)
        | CoreArgumentHandOff { borrowsOnly = true } -> (state, argumentTemp, -1, None)
        | CoreArgumentHandOff { runtimeArgument = false } -> (state, argumentTemp, -1, None)
        | _copyType when resultSurvivesReset(argumentType)(state) -> prepareCopyTypeArgument(handOff)(functionTemp)(argumentTemp)(state)
        | CoreArgumentHandOff { pendingRootSlot = Some(rootSlot) } ->
            match emitAcceptsRuntimeManagedFlag(functionTemp)(state) with
                | (flagged, acceptsFlagTemp) ->
                    match pendingArgumentFlag(handOff)(rootSlot)(acceptsFlagTemp)(flagged) with
                        | (registered, flagTemp) ->
                            match pendingRetainAdoption(handOff)(acceptsFlagTemp)(flagTemp)(argumentType)(registered) with
                                | (adopted, adoption) ->
                                    match emitConditionalArgumentRetain(argumentTemp)(argumentType)(flagTemp)(adopted) with
                                        | (retained, passedTemp) -> (retained, passedTemp, flagTemp, handedOverWith(passedTemp)(adoption))
        | CoreArgumentHandOff { transfers = true } ->
            match emitAcceptsRuntimeManagedFlag(functionTemp)(state) with
                | (flagged, flagTemp) -> (flagged, argumentTemp, flagTemp, None)
        | CoreArgumentHandOff { mayReach = true, fresh = false } ->
            match emitAcceptsRuntimeManagedFlag(functionTemp)(state) with
                | (flagged, flagTemp) ->
                    match emitArgumentRetain(argumentTemp)(argumentType)(flagged) with
                        | (retained, passedTemp) -> (retained, passedTemp, flagTemp, Some((passedTemp, flagTemp)))
        | _ ->
            match emitAcceptsRuntimeManagedFlag(functionTemp)(state) with
                | (flagged, flagTemp) ->
                    // The caller's own reference is the one an adopting callee takes, so an argument
                    // handed over under the adoption bit is not retained a second time.
                    if handsFreshOver
                    then (flagged, argumentTemp, flagTemp, None)
                    else
                        match retainCallArgument(handOff)(argumentType)(argumentTemp)(flagTemp)(flagged) with
                            | (retained, passedTemp) -> (retained, passedTemp, flagTemp, None)

// Stage 0 hands a fresh argument over under the callee's adoption bit when the callee's result reach is
// unknown, so the result may be keeping it: releasing it here would free what such a callee stored.
// Stage 0 additionally exempts an argument whose reach account rules out keeping the parameter whole,
// which needs the whole-reach and exposed-parameter facts these reach summaries do not carry yet; until
// they do, a callee with a complete reach account keeps the ordinary release below.
let handedOverAdoptionFlag (context: CoreCallContext) (handOff: CoreArgumentHandOff) (flagTemp: Int) =
    if handOff.cannotBeKeptWhole
    then -1
    else
        match context.facts with
            | Some(CoreCalleeFacts { reach = ResultReachState { isPoisoned = true } }) -> flagTemp
            | Some(_facts) -> -1
            | None -> flagTemp

// Stage 0's `HandsFreshArgumentOverUnderAdoption`: a fresh argument that is not transferred, handed
// to a callee of unknown result reach, travels under the callee's adoption bit.
let handsFreshArgumentOverUnderAdoption (context: CoreCallContext) (handOff: CoreArgumentHandOff) = handOff.keptWholeUnderOwnedResult || handOff.fresh && handOff.transfers == false && handedOverAdoptionFlag(context)(handOff)(0) >= 0

// The fresh arguments the callee does not take, in argument order. A fresh argument transferred
// to a callee whose result keeps it whole, but which does not normalize it on entry, travels
// under the callee's adoption bit: the caller releases it after the call where the result was
// copied out and so kept nothing of it (stage 0's second `RegisterConsumedRuntimeArgument` arm).
let consumedArgumentsWith (context: CoreCallContext) (flagTemp: Int) (handOff: CoreArgumentHandOff) (argumentTemp: Int) (argumentType: SemanticType) (handedOver: Maybe((Int, Int))) (consumed: List(CoreConsumedArgument)) =
    match (handOff, handedOver) with
        | (_handOff, Some((retainedTemp, adoptionFlagTemp))) ->
            append(consumed)([CoreConsumedArgument(
                temp = retainedTemp,
                semanticType = argumentType,
                preserveEscapedChildren = true,
                adoptionFlagTemp = adoptionFlagTemp
            )])
        | (_handOff, None) ->
            match handOff with
                | CoreArgumentHandOff { borrowsOnly = false, fresh = true, transfers = false, mayReach = mayReach, keptWholeUnderOwnedResult = keptWhole } ->
                    append(consumed)([CoreConsumedArgument(
                        temp = argumentTemp,
                        semanticType = argumentType,
                        preserveEscapedChildren = keptWhole || mayReach,
                        adoptionFlagTemp = if keptWhole && flagTemp >= 0
                        then flagTemp
                        else handedOverAdoptionFlag(context)(handOff)(flagTemp)
                    )])
                | CoreArgumentHandOff { borrowsOnly = false, fresh = true, transfers = true, normalizes = false } ->
                    if flagTemp >= 0
                    then
                        append(consumed)([CoreConsumedArgument(
                            temp = argumentTemp,
                            semanticType = argumentType,
                            preserveEscapedChildren = true,
                            adoptionFlagTemp = flagTemp
                        )])
                    else consumed
                | _ -> consumed

// Settled monomorphic facts remain authoritative; a miss needs only the graph verdict.
let ownedChildrenDroppable (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemNamed(_symbolId, name, []) as resolved ->
            match state
            |> stateHeapLayoutFactsByType
            |> Ashes.Collection.Map.getStr(name) with
                | Some(facts) -> facts.ownedChildrenDroppable
                | None ->
                    state
                    |> coverageEnvironment
                    |> classifyHeapOwnedChildrenDroppable(resolved)
        | resolved ->
            state
            |> coverageEnvironment
            |> classifyHeapOwnedChildrenDroppable(resolved)

// Stage 0's `IsConcretelyRuntimeManageableResultType`: a result type runtime RC holds — a
// scalar, a string, `Bytes`, a `BigInt`, a list the runtime may own, a tuple the runtime-managed
// copy reproduces, or a named type whose owned children the type-directed dropper can release.
let isRuntimeManageableResultType (semanticType: SemanticType) (state: CoreLoweringState) =
    resultSurvivesReset(semanticType)(state) || (match resolveType(state)(semanticType) with
        | SemString -> true
        | SemBytes -> true
        | SemBigInt -> true
        | SemList(element) -> tcoListElementSupported(element)(state)
        | SemTuple(elements) -> allRuntimeOwnedCopyElements(elements)(state)
        | SemNamed(_symbolId, _name, _arguments) as named -> ownedChildrenDroppable(named)(state)
        | _ -> false)

let hasCallCopyOut (semanticType: SemanticType) (state: CoreLoweringState) =
    match callCopyOutOf(semanticType)(state) with
        | Some(_copyOut) -> true
        | None -> false

// Stage 0's `TryGetCompiledFunctionResultRuntimeManaged` read at a call site: the recorded
// placement of the innermost stage the spine's applications reach, following a curried callee's
// returned-closure chain; a body not lowered yet is unknown.
let calleeCompiledResultRuntimeManaged (facts: Maybe(CoreCalleeFacts)) (state: CoreLoweringState) =
    match state
    |> stateBodyRuntimeManagedByLabel
    |> compiledResultRuntimeManaged(facts)(stateFunctionReturnedClosureLabels(state)) with
        | Some(runtimeManaged) -> runtimeManaged
        | None -> false

// Stage 0's `TryResolveKnownFunctionResultOwnership`: a saturated call to a callee whose result
// the provenance classification proved RC-eligible, of a type runtime RC holds, whose lowered
// body produced a reference-counted result, read only where this function may place ordinary values on
// the reference-counted heap.
let knownResultRuntimeManaged (facts: Maybe(CoreCalleeFacts)) (resultType: SemanticType) (state: CoreLoweringState) = ordinaryRcPlacementAllowed(state) && calleeSaturatedAndEligible(facts) && isRuntimeManageableResultType(resultType)(state) && calleeCompiledResultRuntimeManaged(facts)(state)

// Stage 0's `IsDirectRuntimeManagedFunctionCall` with `ResolveUncopyableResultRuntimeManaged`:
// the known result ownership, or a lowered body's reference-counted result of a type runtime RC
// holds or of a heap type without any copy-out (such a result can only have left the callee's
// window by being RC).
let callResultRuntimeManaged (facts: Maybe(CoreCalleeFacts)) (resultType: SemanticType) (state: CoreLoweringState) = knownResultRuntimeManaged(facts)(resultType)(state) || calleeCompiledResultRuntimeManaged(facts)(state) && (isRuntimeManageableResultType(resultType)(state) || resultSurvivesReset(resultType)(state) == false && hasCallCopyOut(resultType)(state) == false)

// Stage 0's `AppliedLambdaResultRuntimeManaged`: a lambda applied where it is written is a known
// callee, lowered just before the call, so the call result takes the ownership its body decided:
// a body returning a reference-counted value hands it over owned, as a named function's does.
let appliedLambdaResultRuntimeManaged (context: CoreCallContext) (resultType: SemanticType) (state: CoreLoweringState) =
    match context.appliedLambdaLabel with
        | Some(label) ->
            ordinaryRcPlacementAllowed(state) && resultSurvivesReset(resultType)(state) == false && isRuntimeManageableResultType(resultType)(state) && (match state
            |> stateFunctionReturnedClosureLabels
            |> innermostStageLabel(context.argumentCount - 1)(label) with
                | Some(stage) ->
                    match state
                    |> stateBodyRuntimeManagedByLabel
                    |> Ashes.Collection.Map.getStr(stage) with
                        | Some(runtimeManaged) -> runtimeManaged
                        | None -> false
                | None -> false)
        | None -> false

// The call result's known ownership: the callee's facts, or an applied lambda's compiled body.
let contextResultRuntimeManaged (context: CoreCallContext) (resultType: SemanticType) (state: CoreLoweringState) = callResultRuntimeManaged(context.facts)(resultType)(state) || appliedLambdaResultRuntimeManaged(context)(resultType)(state)

// Stage 0's `CalleeCompiledResultVerifiedRuntimeManaged`: a callee whose compiled body is verified
// to return a reference-counted result, a named function or a lambda applied where it is written
// (compiled under its own label just before the call), copied or retained every part it kept.
let calleeVerifiedResultRuntimeManaged (context: CoreCallContext) (state: CoreLoweringState) =
    calleeCompiledResultRuntimeManaged(context.facts)(state) || (match context.appliedLambdaLabel with
        | Some(label) ->
            match state
            |> stateFunctionReturnedClosureLabels
            |> innermostStageLabel(context.argumentCount - 1)(label) with
                | Some(stage) ->
                    match state
                    |> stateBodyRuntimeManagedByLabel
                    |> Ashes.Collection.Map.getStr(stage) with
                        | Some(runtimeManaged) -> runtimeManaged
                        | None -> false
                | None -> false
        | None -> false)

// Stage 0's `IsNormalizableUncoveredRecord`: a single-constructor named type with no call copy-out
// that the entry normalization's copy re-establishes.
let classifyNormalizableUncoveredRecord (name: Str) (named: SemanticType) (state: CoreLoweringState) =
    (match state |> stateConstructorsOfType(name) with
        | _layout :: [] -> true
        | _ -> false) && hasCallCopyOut(named)(state) == false && (match (entryNormalizationPlanOf(named)(state), heapFactsOf(named)(state)) with
        | (Some(_plan), HeapLayoutFacts { arenaDeepCopySupported = true }) -> true
        | _ -> false)

let isNormalizableUncoveredRecord (resultType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(resultType) with
        | SemNamed(_symbolId, name, _arguments) as named ->
            let key = formatSemanticType(named)
            in
                match state
                |> stateNormalizableRecordTypes
                |> Ashes.Collection.Map.getStr(key) with
                    | Some(answer) -> (state, answer)
                    | None ->
                        let answer = classifyNormalizableUncoveredRecord(name)(named)(state)
                        in
                            (withStateNormalizableRecordTypes(state
                            |> stateNormalizableRecordTypes
                            |> Ashes.Collection.Map.setStr(key)(answer))(state), answer)
        | _ -> (state, false)

// Stage 0's `ResultOwnershipReadAtRunTime`: the result types whose ownership a call reads from the
// callee's returns bit when the callee cannot be resolved statically: the ones with a call
// copy-out, and a record with none that the entry normalization's copy re-establishes, unless the
// callee's compiled body already answers. A loop's non-tail call to itself keeps the loop's own
// protocol for its result.
let insideTcoLoop (state: CoreLoweringState) =
    match stateTcoLoopFrame(state) with
        | Some(_frame) -> true
        | None -> false

// Stage 0's `CalleeReturnsGeneralRcOwned`: a call of such a result type receives its result owned
// from a function compiled under the contract, reached along the returned-closure chain one hop
// per application past the first, or from the function being lowered itself, which normalizes its
// result at its return like any other.
let calleeReturnsGeneralRcOwned (context: CoreCallContext) (state: CoreLoweringState) =
    context.selfCallee || (match context.facts with
        | Some(CoreCalleeFacts { label = Some(label), argumentCount = count }) ->
            match state
            |> stateFunctionReturnedClosureLabels
            |> innermostStageLabel(count - 1)(label) with
                | Some(stageLabel) ->
                    state
                    |> stateGeneralRcOwnedResultLabels
                    |> containsLabel(stageLabel)
                | None -> false
        | _ -> false)

// Stage 0's `CapturedSelfListResultOwnedAtRunTime`: a call through a captured self (the recursive
// binding reached through a lambda's environment) reaches a body still being lowered, so for a
// list only the recursive deep copy expresses the call reads the callee's returns bit: a
// reference-counted result is taken over as it is, an arena one is copied, and the caller owns
// the result on both paths.
let capturedSelfListResultOwnedAtRunTime (context: CoreCallContext) (resultType: SemanticType) (state: CoreLoweringState) =
    (match context.calleeName with
        | Some(name) ->
            match lookupBinding(name)(state.bindings) with
                | Some(CoreBinding { location = CoreEnvironment(_index) }) ->
                    state
                    |> stateRecursiveGroupNames
                    |> containsLabel(name)
                | _ -> false
        | None -> false) && containsUnresolvedLayout(resultType)(state) == false && (match resolveType(state)(resultType) with
        | SemList(element) ->
            resultSurvivesReset(element)(state) == false && (match argumentCopyPlanOf(element)(state) with
                | Some(_plan) -> true
                | None -> false)
        | _ -> false)

// Stage 0's `LoopSelfCallAdtResultOwnedAtRunTime`: a loop's own non-tail self call reaches a body
// still being lowered, whose exit may yet place its result on the reference-counted heap. For an
// ADT the runtime can copy into an owned value but no fixed copy-out covers, the call reads the
// returns bit and owns the result on both paths.
let loopSelfCallAdtResultOwnedAtRunTime (context: CoreCallContext) (resultType: SemanticType) (state: CoreLoweringState) =
    context.selfCallee && insideTcoLoop(state) && calleeCompiledResultRuntimeManaged(context.facts)(state) == false && containsUnresolvedLayout(resultType)(state) == false && resultSurvivesReset(resultType)(state) == false && isGeneralRcValueType(resultType)(state) == false && (match resolveType(state)(resultType) with
        | SemNamed(_symbolId, _name, _arguments) ->
            match ownedResultPlanOf(resultType)(state) with
                | Some(ScalarArgumentCopy) -> false
                | Some(_plan) -> true
                | None -> false
        | _ -> false)

let resultOwnershipReadAtRunTime (context: CoreCallContext) (resultType: SemanticType) (state: CoreLoweringState) =
    if hasCallCopyOut(resultType)(state)
    then (state, true, false)
    else
        if generalRcEnabled(state) && capturedSelfListResultOwnedAtRunTime(context)(resultType)(state)
        then (state, true, false)
        else
            if generalRcEnabled(state) && loopSelfCallAdtResultOwnedAtRunTime(context)(resultType)(state)
            then (state, true, true)
            else
                if context.selfCallee && insideTcoLoop(state) || calleeCompiledResultRuntimeManaged(context.facts)(state) || calleeReturnsGeneralRcOwned(context)(state) == false
                then (state, false, false)
                else
                    match isNormalizableUncoveredRecord(resultType)(state) with
                        | (asked, normalizable) -> (asked, normalizable, normalizable)

// The last application of a spine reads the callee's returns bit when the result's ownership
// is not statically known and its type is read at run time, stage 0's `needsResultOwnership`.
// Whether the last application reads the callee's returns bit, and whether its result will then
// be normalized into an owned value on both of its branches: decided once per call, before the
// argument's hand-off, since that hand-off depends on it. A coroutine body or a function that may run
// inside one reads no bit (stage 0's `AllowsAsyncIndependentRcPlacement` term): its call results are
// copied to the arena at their call boundary.
// Stage 0's `GetOwnershipSummaryForCallRoot` answers nothing for a lone recursive binding's call to
// itself inside its own body, so the call's result ownership is read at run time from the self
// closure's returns bit: the result is copied out of the call window when it is still in the arena
// and taken as owned when it is reference-counted, and as the argument of a further call it is
// handed over to that call (with the callee closure's adoption bit) and released when not adopted.
// Only a self call that is itself an ordinary call's argument reads the bit here: a self call in
// tail position hands its result on as the function's own, and one stored in a constructor or
// returned keeps the ownership the callee's predicted result gives it.
let selfCallArgumentReadsResultBit (context: CoreCallContext) (state: CoreLoweringState) = context.resultIsCallArgument && selfCallReadsNoSummary(context.selfCallee)(state)

let readsResultOwnershipAtRunTime (context: CoreCallContext) (arity: Int) (resultType: SemanticType) (state: CoreLoweringState) =
    match context with
        | CoreCallContext { tailCall = true } -> (state, false, false)
        | CoreCallContext { facts = facts } ->
            if asyncIndependentRcPlacementAllowed(state) && arity == 1 && selfCallArgumentReadsResultBit(context)(state)
            then (state, true, false)
            else
                if asyncIndependentRcPlacementAllowed(state) && arity == 1 && knownResultRuntimeManaged(facts)(resultType)(state) == false
                then resultOwnershipReadAtRunTime(context)(resultType)(state)
                else (state, false, false)

let emitResultOwnershipFlag (reads: Bool) (functionTemp: Int) (state: CoreLoweringState) =
    if reads
    then emitReturnsRuntimeManagedFlag(functionTemp)(state)
    else (state, -1)

// A deferred self call whose result the consumer already names: the cons that holds it asks for
// `List(head)` before the tail is lowered, so the layout is known here even though the recursive
// binding's own arrow is still open, and the call is lowered the way the compiler lowers it with a
// complete inference behind it - adopting the result by the callee's returns bit rather than
// asking for an arena one.
// A body with an open sibling is lowered again once that sibling's result resolves, so its own
// binding's result looks named there although stage 0 sees it open at the call: such a self call
// keeps asking for the arena result.
let deferredSelfResultNamedByConsumer (context: CoreCallContext) (state: CoreLoweringState) =
    selfResultDeferred(context)(state) && !(match context.calleeName with
        | Some(name) ->
            state
            |> stateSiblingResultOpenNames
            |> containsLabel(name)
        | None -> false) && (match context.expectedResult with
        | Some(expected) -> containsUnresolvedLayout(expected)(state) == false
        | None -> false)

// A call to another member of the recursive group whose arrow was still open when this body began
// asks for an arena result as stage 0's does, also when the body is lowered again against the
// result the first lowering resolved.
let siblingResultOpen (context: CoreCallContext) (resultType: SemanticType) (state: CoreLoweringState) =
    match (context.calleeName, resolveType(state)(resultType)) with
        | (_name, SemFunction(_argument, _result, _row)) -> false
        | (Some(name), _resolved) ->
            context.selfCallee && containsLabel(name)(stateSiblingResultOpenNames(state))
        | (None, _resolved) -> false

let siblingCallSiteKey (context: CoreCallContext) =
    match (context.siteSpan, context.calleeName) with
        | (Some(TextSpan { start = start, end = end }), Some(name)) -> Some(Ashes.Text.fromInt(start) + ":" + Ashes.Text.fromInt(end) + ":" + name + ":" + Ashes.Text.fromInt(context.argumentCount))
        | _ -> None

// A call to a later member of the recursive group whose result the group's first lowering settled.
// Stage 0 lowers such a call with its result type still open and pins the type only once the call is
// lowered, so the call is lowered that way here too, also in a body lowered again against a scope
// the pin closed: it asks for no tuple parts, reads the closure's owned-result flag rather than
// taking the result as owned, and does not lower the body again once the pin resolves it.
let siblingResultPinned (context: CoreCallContext) (resultType: SemanticType) (state: CoreLoweringState) =
    siblingResultOpen(context)(resultType)(state) && (match siblingCallSiteKey(context) with
        | Some(key) ->
            match state
            |> stateProvenSiblingCallResults
            |> Ashes.Collection.Map.getStr(key) with
                | Some(_proven) -> true
                | None -> false
        | None -> false)

// Stage 0's `RequestsArenaResultFromUnknownCallee`: a call through a closure no let-bound function
// or applied lambda names, whose result is a variant outside the contract with no fixed copy-out,
// keeps that result as an arena value and never releases it. A callee that normalized its result
// onto the reference-counted heap would hand over a reference nothing releases, so the call asks
// for an arena result, which such a callee deep-copies into the arena before releasing its own.
// A callee whose label resolves but whose body has not been lowered yet (a function's non-tail
// call to itself, or to one later in its group) has no compiled result fact to read either, so its
// result is asked for in the arena the same way (stage 0's `CalleeResultUnsettled`).
let calleeResultUnsettled (facts: Maybe(CoreCalleeFacts)) (state: CoreLoweringState) =
    match state
    |> stateBodyRuntimeManagedByLabel
    |> compiledResultRuntimeManaged(facts)(stateFunctionReturnedClosureLabels(state)) with
        | Some(_runtimeManaged) -> false
        | None -> true

// Stage 0's `RequestsArenaResultFromUnknownCallee`: a call through a closure no name resolves,
// whose named or list result has no fixed copy-out and lies outside the contract, keeps that
// result as an arena value, so it asks the callee for an arena result; a known callee whose body is
// still being lowered asks only for a named result, so a recursive list producer is not
// deep-copied once per level.
let requestsArenaResultFromUnknownCallee (context: CoreCallContext) (readsResult: Bool) (resultType: SemanticType) (state: CoreLoweringState) =
    match (readsResult, context.facts, context.appliedLambdaLabel, resolveType(state)(resultType)) with
        | (false, None, None, SemNamed(_symbolId, _name, _arguments) as named) -> !resultSurvivesReset(named)(state) && !containsUnresolvedLayout(named)(state) && !hasCallCopyOut(named)(state) && !isGeneralRcValueType(named)(state) && canNormalizeResultIntoArena(named)(state)
        | (false, None, None, SemList(_element) as list) -> !resultSurvivesReset(list)(state) && !containsUnresolvedLayout(list)(state) && !hasCallCopyOut(list)(state) && !isGeneralRcValueType(list)(state) && canNormalizeResultIntoArena(list)(state)
        | (false, Some(_facts) as facts, None, SemNamed(_symbolId, _name, _arguments) as named) -> calleeResultUnsettled(facts)(state) && !resultSurvivesReset(named)(state) && !containsUnresolvedLayout(named)(state) && !hasCallCopyOut(named)(state) && !isGeneralRcValueType(named)(state) && canNormalizeResultIntoArena(named)(state)
        | _ -> false

// The request word itself: bit 1 beside the argument ownership flag, when the call passes one.
let emitRequestWord (argumentFlagTemp: Int) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = requestState, temp = requestTemp } ->
            let loaded =
                emit(LoadConstInt(requestTemp)(2))(requestState)
            in
                if argumentFlagTemp < 0
                then (loaded, requestTemp)
                else
                    match freshTemp(loaded) with
                        | FreshTemp { state = wordState, temp = wordTemp } ->
                            (emit(OrInt(wordTemp)(argumentFlagTemp)(requestTemp))(wordState), wordTemp)

// Step-8 shadow comparison, rules 6, 7 and 8: whether the call asks for an arena result, against
// the same request judged on the call's recorded result type. The decision is filed under the rule
// that made it open: a deferred self result (6), a sibling's open or pinned result or a deferred
// self result its consumer names (7), or the result layout itself (8).
let shadowArenaRequest (context: CoreCallContext) (resultType: SemanticType) (state: CoreLoweringState) =
    if shadowing(state)
    then
        let requestText =
            given (asks: Bool) ->
                if asks
                then "arena"
                else "no-arena"
        in
            let current = (requestsArenaResult(resultType)(state) || siblingResultOpen(context)(resultType)(state)) && deferredSelfResultNamedByConsumer(context)(state) == false
            in
                let rule =
                    if selfResultDeferred(context)(state)
                    then 6
                    else
                        if siblingResultOpen(context)(resultType)(state)
                        then 7
                        else 8
                in
                    match (shadowSiteType(state), resolveType(state)(resultType)) with
                        | (Some(recorded), resolved) ->
                            if freeTypeVariables(resolved) == [] && recorded != resolved
                            then
                                observeShadowUnrecorded(rule)(requestText(current))("the node in effect records " + Ashes.Trait.Show.show(recorded) + ", not the call's result " + Ashes.Trait.Show.show(resolved))(state)
                            else
                                observeShadow(rule)(requestText(current))(state
                                |> requestsArenaResult(recorded)
                                |> requestText
                                |> Some)(state)
                        | (None, _resolved) ->
                            observeShadow(rule)(requestText(current))(None)(state)
    else state

// Step-8 shadow comparison, rule 9: a known callee asked for an arena result because its body was
// not lowered yet. Whether its result fact is known once every body is lowered is settled after
// the program's lowering.
let shadowUnsettledCallee (context: CoreCallContext) (state: CoreLoweringState) =
    match context.facts with
        | Some(_facts) ->
            if shadowing(state)
            then observeShadowPending(9)("arena")(context.facts)(state)
            else state
        | None -> state

// Stage 0's `EmitArenaResultRequestWord`: the hidden ownership word carrying the arena-result
// request (bit 1) beside the argument ownership flag (bit 0), when the call passes one. Only the
// last application of a spine asks: an earlier one yields a closure, which stage 0 sees as a
// function type, while a self call of a recursive binding whose arrow is still open sees a variable.
// A call that reads the callee's returns bit never asks: the callee answers a request with an
// arena copy whatever its bit says, so the bit could no longer select between adopting the result
// and copying it out, and stage 0 reads the bit only of a call whose result it sees resolved,
// which it never asks for in the arena.
let arenaResultRequestWord (context: CoreCallContext) (readsResult: Bool) (argumentFlagTemp: Int) (resultType: SemanticType) (state: CoreLoweringState) =
    if (requestsArenaResult(resultType)(state) || siblingResultOpen(context)(resultType)(state)) && deferredSelfResultNamedByConsumer(context)(state) == false
    then
        let recorded =
            if selfResultDeferred(context)(state) || siblingResultPinned(context)(resultType)(state)
            then state
            else withStateUnresolvedCallResults(resultType :: stateUnresolvedCallResults(state))(state)
        in
            if readsResult
            then (recorded, argumentFlagTemp)
            else emitRequestWord(argumentFlagTemp)(recorded)
    else
        if requestsArenaResultFromUnknownCallee(context)(readsResult)(resultType)(state)
        then
            state
            |> shadowUnsettledCallee(context)
            |> emitRequestWord(argumentFlagTemp)
        else (state, argumentFlagTemp)

// The request with the step-8 shadow comparison recorded first; the comparison changes nothing the
// call emits.
let emitArenaResultRequestWord (context: CoreCallContext) (readsResult: Bool) (argumentFlagTemp: Int) (resultType: SemanticType) (state: CoreLoweringState) =
    state
    |> shadowArenaRequest(context)(resultType)
    |> arenaResultRequestWord(context)(readsResult)(argumentFlagTemp)(resultType)

// Stage 0's `_borrowedArgumentCalls`: a call that only borrows its argument, recorded by the
// function it is emitted into (the program entry outside every lifted function) and its result.
let recordBorrowedCall (borrows: Bool) (target: Int) (state: CoreLoweringState) =
    if borrows
    then
        match stateActiveFunctionOrigin(state) with
            | Some(IrFunctionOrigin { generatedLabel = label }) -> state with borrowedCalls = (label, target) :: state.borrowedCalls
            | None -> state with borrowedCalls = ("_start_main", target) :: state.borrowedCalls
    else state

// The contract elements of a call's tuple result type, by index: the parts a call asks for.
let contractElementParts (resultType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(resultType) with
        | SemTuple(elements) -> contractElementIndices(elements)(0)(state)
        | _ -> []

// Stage 0's `IsListHeadHandOverType`: a list of the contract's types whose heads a caller can
// release behind its copy, a concrete element type the copy retains when it is reference-counted.
let isListHeadHandOverType (resultType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(resultType) with
        | SemList(element) ->
            match listHeadCopyKindOf(element)(state) with
                | Some(_headCopy) -> false
                | None -> canTestRepresentation(state) && !resultSurvivesReset(element)(state) && !containsUnresolvedLayout(SemList(element))(state) && isGeneralRcValueType(SemList(element))(state)
        | _ -> false

let unknownCalleeTupleResultParts (candidates: List(Int)) (listHeads: Bool) =
    match candidates with
        | [] ->
            if listHeads
            then Some([])
            else None
        | _ -> Some(candidates)

let recursive partsWithoutMarker (parts: List(Int)) =
    match parts with
        | [] -> []
        | part :: rest ->
            if part == wholeResultHandOverMarker || part == listHeadHandOverMarker
            then partsWithoutMarker(rest)
            else part :: partsWithoutMarker(rest)

// The parts a call asks a known callee for when its own result type names none: the callee's
// parts, or none at all from a callee that may hand its whole result over when the call's result
// is of the contract's types, or its list's heads over when the call's result is such a list.
let askedCalleeParts (calleeParts: List(Int)) (contractResult: Bool) (listHeads: Bool) =
    match partsWithoutMarker(calleeParts) with
        | [] ->
            if contractResult && containsInt(wholeResultHandOverMarker)(calleeParts) || listHeads && containsInt(listHeadHandOverMarker)(calleeParts)
            then Some([])
            else None
        | parts -> Some(parts)

// Stage 0's `CalleeTupleResultParts`: the parts a call asks its callee for, the contract elements
// of its tuple result type (the callee's own parts when that type is not yet a tuple). A call asks a
// known function that may hand them over, reached along the returned-closure chain one hop per
// application past the first, and any callee it cannot name, since the mask the callee writes says
// what it handed over.
let calleeTupleResultParts (context: CoreCallContext) (resultType: SemanticType) (state: CoreLoweringState) =
    (let candidates = contractElementParts(resultType)(state)
    in
        let listHeads = isListHeadHandOverType(resultType)(state)
        in
            match context.facts with
                | Some(CoreCalleeFacts { label = Some(label), argumentCount = count }) ->
                    match state
                    |> stateFunctionReturnedClosureLabels
                    |> innermostStageLabel(count - 1)(label) with
                        | Some(stageLabel) ->
                            match state
                            |> stateTupleResultPartLabels
                            |> Ashes.Collection.Map.getStr(stageLabel) with
                                | Some(calleeParts) ->
                                    match candidates with
                                        | [] ->
                                            askedCalleeParts(calleeParts)(isGeneralRcValueType(resultType)(state))(listHeads)
                                        | _ -> Some(candidates)
                                | None -> None
                        | None -> unknownCalleeTupleResultParts(candidates)(listHeads)
                | _ -> unknownCalleeTupleResultParts(candidates)(listHeads))

// Stage 0's `EmitTupleResultPartsRequestWord`: bit 2 of the hidden ownership word asks the callee
// for its tuple result's contract parts, and the bits above it address a cleared arena word the
// callee writes the mask of the parts it handed over into; the request word is on the left of the
// word.
let emitTupleResultPartsRequestWord (context: CoreCallContext) (resultType: SemanticType) (wordTemp: Int) (state: CoreLoweringState) =
    match if siblingResultPinned(context)(resultType)(state)
    then None
    else calleeTupleResultParts(context)(resultType)(state) with
        | None -> (state, wordTemp)
        | Some(_parts) ->
            match freshTempRun(4)(state) with
                | FreshTemp { state = requestState, temp = cellTemp } ->
                    let loaded =
                        requestState
                        |> emit(Alloc(cellTemp)(8)(false))
                        |> emit(LoadConstInt(cellTemp + 1)(0))
                        |> emit(StoreMemOffset(cellTemp)(0)(cellTemp + 1))
                        |> emit(LoadConstInt(cellTemp + 2)(4))
                        |> emit(OrInt(cellTemp + 3)(cellTemp + 2)(cellTemp))
                    in
                        if wordTemp < 0
                        then (loaded, cellTemp + 3)
                        else
                            match freshTemp(loaded) with
                                | FreshTemp { state = joinedState, temp = joinedTemp } ->
                                    (emit(OrInt(joinedTemp)(cellTemp + 3)(wordTemp))(joinedState), joinedTemp)

// Stage 0's `AccessorArgumentRcStatus`: what a trivial accessor call knows about its argument's
// placement. A pending loop parameter carries its slot, settled at the loop's finalize pass.
type AccessorArgumentRc =
    | AccessorArgumentNotRc
    | AccessorArgumentDefinitelyRc
    | AccessorArgumentPendingSlot(Int)

let calleeIsTrivialAccessor (facts: Maybe(CoreCalleeFacts)) (state: CoreLoweringState) =
    match facts with
        | Some(CoreCalleeFacts { label = Some(label) }) ->
            match state
            |> stateTrivialParameterFieldAccessorLabels
            |> Ashes.Collection.Map.getStr(label) with
                | Some(known) -> known
                | None -> false
        | _ -> false

// Stage 0's `IsTrivialAccessorCall`: a call that reads the callee's returns bit, applying the first
// argument of a callee whose whole body is a borrowed field read of that parameter. Only the first
// application qualifies, where the callee's label names the accessor itself rather than an earlier
// stage of a curried chain.
let isTrivialAccessorCall (context: CoreCallContext) (arity: Int) (readsResult: Bool) (state: CoreLoweringState) = readsResult && argumentIndexOf(context.facts)(arity) == 0 && calleeIsTrivialAccessor(context.facts)(state)

// A loop parameter read is reference-counted when the frame has placed it there by now, and
// pending on the finalize pass otherwise.
let loopParameterAccessorArgument (argument: Expr) (slot: Int) (state: CoreLoweringState) =
    match stateTcoLoopFrame(state) with
        | Some(frame) ->
            if containsInt(slot)(frame.parameterSlots)
            then
                if readsEntryPlacedLoopParameter(argument)(state)
                then AccessorArgumentDefinitelyRc
                else AccessorArgumentPendingSlot(slot)
            else AccessorArgumentNotRc
        | None -> AccessorArgumentNotRc

// Stage 0's `ClassifyAccessorArgumentRc`: a reference-counted argument temp, or a binding that still
// owns a reference-counted value, is reference-counted; a read of a loop parameter is classified by
// the frame's placement of it; anything else is not known to be reference-counted.
let classifyAccessorArgumentRc (argument: Expr) (argumentTemp: Int) (state: CoreLoweringState) =
    if isRuntimeTemp(argumentTemp)(state)
    then AccessorArgumentDefinitelyRc
    else
        match unspanArgument(argument) with
            | ExprVar(name) ->
                match lookupBinding(name)(state.bindings) with
                    | Some(CoreBinding { location = CoreLocal(slot) }) ->
                        match runtimeOwnerStateOf(slot)(state) with
                            | Some(true) -> AccessorArgumentDefinitelyRc
                            | _ -> loopParameterAccessorArgument(argument)(slot)(state)
                    | _ -> AccessorArgumentNotRc
            | _ -> AccessorArgumentNotRc

let accessorArgumentRcOf (context: CoreCallContext) (arity: Int) (readsResult: Bool) (argument: Expr) (argumentTemp: Int) (state: CoreLoweringState) =
    if isTrivialAccessorCall(context)(arity)(readsResult)(state)
    then classifyAccessorArgumentRc(argument)(argumentTemp)(state)
    else AccessorArgumentNotRc

// Stage 0's `ResolveCallResultOwnershipFlag`: a trivial accessor call over a reference-counted
// argument reads its result as already owned, through a forced flag of one, whatever the callee's
// returns bit says; the flag of a pending loop parameter is zeroed at finalize when the frame keeps
// the parameter in the arena, which brings the copy back.
let resolveCallResultOwnershipFlag (accessorRc: AccessorArgumentRc) (readsResult: Bool) (functionTemp: Int) (state: CoreLoweringState) =
    match accessorRc with
        | AccessorArgumentNotRc -> emitResultOwnershipFlag(readsResult)(functionTemp)(state)
        | AccessorArgumentDefinitelyRc -> emitForcedRetainFlag(state)
        | AccessorArgumentPendingSlot(slot) ->
            match emitForcedRetainFlag(state) with
                | (flagged, flagTemp) -> (withStatePendingRuntimeArgumentFlags((flagTemp, slot) :: statePendingRuntimeArgumentFlags(flagged))(flagged), flagTemp)

// Stage 0's `RetainAccessorCallResult`: the field of a reference-counted parent is a value with its
// own header, so the call's result is that field retained rather than copied. A pending loop
// parameter's retain is emitted as an identity marker the finalize pass turns into a retain when
// the frame places the parameter on the reference-counted heap.
let retainAccessorCallResult (accessorRc: AccessorArgumentRc) (target: Int) (state: CoreLoweringState) =
    match accessorRc with
        | AccessorArgumentNotRc -> (state, target)
        | AccessorArgumentDefinitelyRc ->
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = retained } ->
                    (allocated
                    |> emit(RcDup(retained)(target)(true)(false))
                    |> markRuntimeTemp(retained)(RuntimeNewlyProduced), retained)
        | AccessorArgumentPendingSlot(slot) ->
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = retained } ->
                    (allocated
                    |> emit(RcDup(retained)(target)(false)(false))
                    |> withStatePendingAccessorResultRetains((retained, slot) :: statePendingAccessorResultRetains(allocated)), retained)

let emitAppliedCall (context: CoreCallContext) arity argumentType consumed functionTemp argumentTemp resultType (handOff: CoreArgumentHandOff) (accessorRc: AccessorArgumentRc) (readsResult: Bool) (resultNormalizedOwned: Bool) unifiedState =
    match prepareCallArgument(handOff)(handsFreshArgumentOverUnderAdoption(context)(handOff))(argumentType)(functionTemp)(argumentTemp)(unifiedState) with
        | (preparedState, passedTemp, argumentFlagTemp, handedOver) ->
            match resolveCallResultOwnershipFlag(accessorRc)(readsResult)(functionTemp)(preparedState) with
                | (flaggedState, resultFlagTemp) ->
                    match freshTemp(flaggedState) with
                        | FreshTemp { state = targetState, temp = target } ->
                            match if arity == 1
                            then
                                match emitArenaResultRequestWord(context)(readsResult)(argumentFlagTemp)(resultType)(targetState) with
                                    | (arenaState, arenaWord) -> emitTupleResultPartsRequestWord(context)(resultType)(arenaWord)(arenaState)
                            else (targetState, argumentFlagTemp) with
                                | (wordState, wordTemp) ->
                                    match wordState
                                    |> emit(CallClosure(target)(functionTemp)(passedTemp)(wordTemp))
                                    |> recordBorrowedCall(handOff.borrowsForPlacement)(target)
                                    |> retainAccessorCallResult(accessorRc)(target) with
                                        | (calledState, resultTemp) ->
                                            CoreCallStage(
                                                lowered = success(resultTemp)(resolveType(unifiedState)(resultType))(calledState),
                                                consumedArguments = consumedArgumentsWith(context)(argumentFlagTemp)(handOff)(argumentTemp)(argumentType)(handedOver)(consumed),
                                                resultFlagTemp = resultFlagTemp,
                                                resultNormalized = false,
                                                resultDeepCopied = false,
                                                resultNormalizedOwned = resultNormalizedOwned,
                                                resultGeneralRc = false
                                            )

// A callee whose own scheme leaves a parameter quantified is compiled once for every
// instantiation, so its body has no layout for that parameter and cannot normalize an
// arena-placed argument on entry. The caller knows the argument's type, and copies it into
// to-space, which no arena reset rewinds, before the call.
let copyGenericArgumentToSpace (context: CoreCallContext) (index: Int) (argumentType: SemanticType) (argumentTemp: Int) (state: CoreLoweringState) =
    match context.calleeScheme with
        | Some(TypeScheme { quantified = quantified, body = body }) ->
            if parameterQuantifiedAt(quantified)(index)(body) && specializationFieldRelocatable(argumentType)(state)
            then
                match state with
                    | CoreLoweringState { nextTemp = nextTemp, nextLocal = nextLocal, nextLambdaId = lambdaId, nextLabelId = labelId, programState = CoreProgramState { dropperLabels = cache } } ->
                        match synthesizeToSpaceCopy(argumentTemp)(resolveType(state)(argumentType))(dropperTypesOf(state))(cache)(nextTemp)(nextLocal)(lambdaId)(labelId) with
                            | (synthesis, resultTemp) ->
                                let copiedState = spliceInlineReleaseWith(emit)(synthesis)(state)
                                in (recordBorrowedCall(resultTemp != argumentTemp)(resultTemp)(copiedState), resultTemp)
            else (state, argumentTemp)
        | None -> (state, argumentTemp)

// The result of the last application of a curried call, `remaining` applications after this one;
// None when the type runs out of arrows first.
let recursive finalApplicationResult (resultType: SemanticType) (remaining: Int) (state: CoreLoweringState) =
    if remaining == 0
    then
        resultType
        |> resolveType(state)
        |> Some
    else
        match resolveType(state)(resultType) with
            | SemFunction(_argument, result, _row) -> finalApplicationResult(result)(remaining - 1)(state)
            | _ -> None

// Stage 0's `OwnsGenericStringArgument` and `OwnsGenericAdtArgument`: a string, or a value of the
// contract's types, passed to a generic parameter of a call whose
// final result holds no closure goes as an owned reference-counted reference instead of a copy in
// the persistent blob region, which nothing reclaims. Only the final result can keep it: no curry
// stage's closure escapes the call, and the final result is a value the caller settles the
// reference against.
// Stage 0's `OwnsGenericListArgument` covers a list of strings, inline values or the contract's
// values the same way, settled by a final result of the contract's types too.
let ownsGenericStringArgument (argumentType: SemanticType) (resultType: SemanticType) (arity: Int) (state: CoreLoweringState) =
    match resolveType(state)(argumentType) with
        | SemString ->
            match finalApplicationResult(resultType)(arity - 1)(state) with
                | Some(SemFunction(_argument, _result, _row)) -> false
                | Some(finalType) -> generalRcEnabled(state) && canTestRepresentation(state) && containsUnresolvedLayout(finalType)(state) == false && specializationFieldRelocatable(finalType)(state)
                | None -> false
        | SemNamed(_symbolId, _name, _arguments) as named ->
            match finalApplicationResult(resultType)(arity - 1)(state) with
                | Some(SemFunction(_argument, _result, _row)) -> false
                | Some(finalType) -> generalRcEnabled(state) && canTestRepresentation(state) && specializationFieldRelocatable(named)(state) && isGeneralRcValueType(named)(state) && containsUnresolvedLayout(finalType)(state) == false && specializationFieldRelocatable(finalType)(state)
                | None -> false
        | SemList(element) ->
            match finalApplicationResult(resultType)(arity - 1)(state) with
                | Some(SemFunction(_argument, _result, _row)) -> false
                | Some(finalType) -> generalRcEnabled(state) && canTestRepresentation(state) && (resolveType(state)(element) == SemString || resultSurvivesReset(element)(state) || isGeneralRcValueType(element)(state)) && containsUnresolvedLayout(finalType)(state) == false && (specializationFieldRelocatable(finalType)(state) || isGeneralRcValueType(finalType)(state))
                | None -> false
        | _ -> false

let rcStringCopyOut (sourceTemp: Int) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = allocated, temp = copyTemp } -> (rcNormalizationCopyOut(copyTemp)(sourceTemp)(-1)(allocated), copyTemp)

// The owned reference a handed-over generic argument goes as: a string's reference or copy, or a
// value of the contract's types retained or copied onto the reference-counted heap (stage 0's
// `EmitRuntimeManagedTcoDeepCopy`).
let handedOverArgumentCopy (argumentType: SemanticType) (argumentTemp: Int) (state: CoreLoweringState) =
    match (resolveType(state)(argumentType), generalCopyPlanOf(argumentType)(state)) with
        | (SemString, _plan) ->
            emitReferenceOrCopy(argumentTemp)(rcStringCopyOut(argumentTemp))(state)
        | (_named, Some(plan)) -> emitGuardedDeepCopy(argumentTemp)(plan)(state)
        | (_named, None) ->
            emitReferenceOrCopy(argumentTemp)(rcStringCopyOut(argumentTemp))(state)

// Stage 0's `EmitHandedOverGenericStringArgument`: the reference is handed over and never adopted,
// so the caller releases it once the result was copied out, and parks it in an owned slot while
// an arena result may still hold it.
let handOverGenericStringArgument (context: CoreCallContext) (arity: Int) (argumentType: SemanticType) (resultType: SemanticType) (argumentTemp: Int) (consumed: List(CoreConsumedArgument)) (state: CoreLoweringState) =
    match context.calleeScheme with
        | Some(TypeScheme { quantified = quantified, body = body }) ->
            if parameterQuantifiedAt(quantified)(context.argumentCount - arity)(body) && ownsGenericStringArgument(argumentType)(resultType)(arity)(state)
            then
                match handedOverArgumentCopy(argumentType)(argumentTemp)(state) with
                    | (owned, ownedTemp) ->
                        match freshTemp(owned) with
                            | FreshTemp { state = flagged, temp = notAdoptedTemp } ->
                                Some((flagged |> emit(LoadConstInt(notAdoptedTemp)(0)), ownedTemp, append(consumed)([CoreConsumedArgument(
                                    temp = ownedTemp,
                                    semanticType = argumentType,
                                    preserveEscapedChildren = true,
                                    adoptionFlagTemp = notAdoptedTemp
                                )])))
            else None
        | None -> None

// Stage 0's `ApplyReuseSpecializationClosure`: a call routed to a reuse specialization (its callee
// bound to the specialization's closure while the call is lowered) passes every argument but the
// accumulator borrowed, since the specialization copies them into the persistent region rather
// than keeping them. A fresh argument keeps its own hand-off, which releases it after the call.
let borrowedByReuseSpecialization (context: CoreCallContext) (arity: Int) (state: CoreLoweringState) (handOff: CoreArgumentHandOff) =
    match context.calleeName with
        | Some(callee) ->
            if arity > 1 && context.selfCallee == false && handOff.fresh == false && containsName(callee)(stateSpecializingInProgress(state))
            then handOff with borrowsOnly = true, borrowedReach = false, mayReach = false, transfers = false, keptWholeUnderOwnedResult = false, borrowsForPlacement = true, pendingRootSlot = None
            else handOff
        | None -> handOff

// One application, stage 0's `LowerAppliedClosureCall`: the argument's hand-off is decided from
// the callee facts and the argument temp, the retain and the flags are emitted, and the call
// follows.
// A copied argument lives in persistent non-RC storage. Source-binding ownership
// cannot describe the replacement temp, even if that binding is an owned loop field.
let toSpaceArgumentHandOff = CoreArgumentHandOff(borrowsOnly = true, borrowsForPlacement = true, fresh = false, runtimeArgument = false, mayReach = false, transfers = false, keptWholeUnderOwnedResult = false, normalizes = false, borrowedReach = false, cannotBeKeptWhole = false, pendingRootSlot = None)

let isFunctionType (semanticType: SemanticType) =
    match semanticType with
        | SemFunction(_parameter, _result, _row) -> true
        | _ -> false

// Stage 0's forced retain of a named argument the callee's result may keep
// (`CalleeResultMayReachParameter`), for an application that returns a curry stage. Stage 0 reads
// a runtime owner's slot as a reference-counted temp, so a let or pattern binding handed to such a
// call is retained outright and handed over, and `parkArgumentsHandedToCurryStage` keeps the
// reference in an owned slot when the stage does not adopt it: the stage's closure environment
// holds the argument after the binding's owner releases it. This holds whether the closure is
// the spine's result or is applied to the spine's later arguments at once: a later stage may
// return the captured argument itself (`combine(left)(right)` answering `left`), and that result
// outlives the owner's release too.
let curryStageOwnerReach (context: CoreCallContext) (arity: Int) (argument: Expr) (resultType: SemanticType) (state: CoreLoweringState) (handOff: CoreArgumentHandOff) =
    if handOff.mayReach || handOff.fresh || handOff.borrowsOnly || handOff.transfers || handOff.keptWholeUnderOwnedResult || handOff.runtimeArgument == false
    then handOff
    else
        match handOff.pendingRootSlot with
            | Some(_slot) -> handOff
            | None ->
                if !selfCallReadsNoSummary(context.selfCallee)(state) && isFunctionType(resolveType(state)(resultType)) && namesRuntimeOwner(argument)(state) && calleeResultReachesArgument(context.facts)(argumentIndexOf(context.facts)(arity))
                then handOff with mayReach = true
                else handOff

let applyCoreCall (context: CoreCallContext) arity argument (copiedToSpace: Bool) argumentType consumed functionTemp resultType passedTemp (copiedState: CoreLoweringState) =
    match readsResultOwnershipAtRunTime(context)(arity)(resultType)(copiedState) with
        | (decidedState, readsResult, resultNormalizedOwned) ->
            match if copiedToSpace
            then (toSpaceArgumentHandOff, decidedState)
            else
                argumentHandOffOf(context.facts)(context.selfCallee)(argumentIndexOf(context.facts)(arity))(argument)(argumentType)(passedTemp)(resultNormalizedOwned)(decidedState) with
                | (handOff, queried) ->
                    let placed =
                        if copiedToSpace
                        then handOff
                        else
                            handOff
                            |> curryStageOwnerReach(context)(arity)(argument)(resultType)(queried)
                            |> borrowedByReuseSpecialization(context)(arity)(queried)
                    in
                        emitAppliedCall(context)(arity)(argumentType)(consumed)(functionTemp)(passedTemp)(resultType)(placed)(accessorArgumentRcOf(context)(arity)(readsResult)(argument)(passedTemp)(queried))(readsResult)(resultNormalizedOwned)(queried)

let freshToSpaceSourceOwner (argument: Expr) (argumentTemp: Int) (state: CoreLoweringState) =
    match runtimeTempStateOf(argumentTemp)(state) with
        | Some(RuntimeNewlyProduced) -> !isVariableArgument(argument)
        | _ -> false

// The argument reaches the application as the value itself, a handed-over reference for a generic
// string parameter, or a to-space copy for any other generic parameter.
let finishCoreCall (context: CoreCallContext) arity argument argumentType consumed functionTemp argumentTemp resultType binding =
    match binding with
        | (unifiedState, Some(error)) ->
            error
            |> failure(unifiedState)
            |> callStageOf
        | (unifiedState, None) ->
            match handOverGenericStringArgument(context)(arity)(argumentType)(resultType)(argumentTemp)(consumed)(unifiedState) with
                | Some((ownedState, ownedTemp, handedConsumed)) -> applyCoreCall(context)(arity)(argument)(false)(argumentType)(handedConsumed)(functionTemp)(resultType)(ownedTemp)(ownedState)
                | None ->
                    match copyGenericArgumentToSpace(context)(context.argumentCount - arity)(argumentType)(argumentTemp)(unifiedState) with
                        | (copiedState, passedTemp) ->
                            let copied = passedTemp != argumentTemp
                            in
                                let sourceConsumed =
                                    if copied && freshToSpaceSourceOwner(argument)(argumentTemp)(unifiedState)
                                    then append(consumed)([CoreConsumedArgument(temp = argumentTemp, semanticType = resolveType(unifiedState)(argumentType), preserveEscapedChildren = false, adoptionFlagTemp = -1)])
                                    else consumed
                                in applyCoreCall(context)(arity)(argument)(copied)(argumentType)(sourceConsumed)(functionTemp)(resultType)(passedTemp)(copiedState)

// The site an argument's mismatch against its parameter type is reported at: the call, which the
// state's span is back to once the argument is lowered.
let argumentSite (callee: Maybe(Str)) (ordinal: Int) (state: CoreLoweringState) =
    CoreMismatchSite(
        span = state.currentSpan,
        location = currentLocation(state),
        argument = Some(CoreCallArgument(ordinal = ordinal, callee = callee))
    )

// Stage 0 reports an argument that fails to meet its parameter type once, at the call, under the
// argument's context; the mismatch a parameter binding found takes the same site.
let locateArgumentMismatch (site: CoreMismatchSite) binding =
    match binding with
        | (state, Some(CoreCallTypeMismatch(error, CoreMismatchSite { argument = None }))) ->
            (state, site
            |> CoreCallTypeMismatch(error)
            |> Some)
        | other -> other

let lowerCoreCallArgument (context: CoreCallContext) arity argument consumed functionTemp expectedArgumentType resultType loweredArgument =
    match loweredArgument with
        | LoweredCoreValue { state = argumentState, error = Some(error) } ->
            error
            |> failure(argumentState)
            |> callStageOf
        | LoweredCoreValue { state = argumentState, temp = argumentTemp, semanticType = argumentType, error = None } ->
            argumentState
            |> bindType(expectedArgumentType)(argumentType)
            |> locateArgumentMismatch(argumentSite(context.calleeName)(context.argumentCount - arity + 1)(argumentState))
            |> finishCoreCall(context)(arity)(argument)(argumentType)(consumed)(functionTemp)(argumentTemp)(resultType)

// Stage 0 lowers a recursive binding's body against a lowering-local arrow whose result type is
// unified only once the body is done, so a self call's result is unresolved at the call however
// much of the body has been lowered before it: the call asks for an arena result and defers its
// copy-out. This lowering resolves as it goes, so the last application of such a self call takes
// a fresh result type here, remembered beside the binding's own for `unifySelfCallResults` at
// the body's end; a partial application's arrow result stays as it is.
let deferSelfCallResultType (context: CoreCallContext) (arity: Int) (resultType: SemanticType) (state: CoreLoweringState) =
    if arity == 1 && selfResultDeferred(context)(state)
    then
        match resolveType(state)(resultType) with
            | SemFunction(_argument, _result, _row) -> (state, resultType)
            | _ ->
                match freshType(state) with
                    | FreshType { state = fresh, semanticType = deferred } -> (withStateSelfCallResultUnifications((deferred, resultType) :: stateSelfCallResultUnifications(fresh))(fresh), deferred)
    else (state, resultType)

// Stage 0's `BeginCallArgumentRetains` and `EndCallArgumentRetains`: the argument about to be
// lowered collects its record-update retains when the callee's result may reach it.
let collectCallArgumentRetains (collecting: Bool) (state: CoreLoweringState) =
    (let framed =
        withCallArgumentRetainFrame(stateCallArgumentRecordRetains(state))(collecting)(state)
    in
        let group = framed.tcoState
        in framed with tcoState = (group with callArgumentLabelStart = state.nextLabelId))

let stopCollectingCallArgumentRetains (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state } ->
            lowered with state = withCallArgumentRetainFrame(stateCallArgumentRecordRetains(state))(false)(state)

// An argument is expected to have the callee's parameter type. A tail self-call's argument
// becomes the next iteration's parameter, so it is lowered under the children transfer and its
// own read of a live owner is retained (stage 0's `LowerCallTcoEvalArg`). An argument the
// callee's result may reach travels on inside that result and escapes this function's binding
// scopes the same way, so it too is lowered under the children transfer (stage 0's
// `CalleeResultMayReachArgument`): an owned binding stored inside the aggregate is retained,
// since the binding's own scope-exit release still fires.
//
// The may-alias question, not the whole-parameter one. Narrowing it to
// `calleeResultReachesArgumentWhole` is a miscompilation: a callee whose result reaches the
// argument only through destructured components still holds those components afterwards, and the
// self-hosted formatter's own `formatTypeExpression` over a capability row formats wrongly when
// the retain is skipped. Over-retaining where the caller's binding outlives the call is a leak,
// which is the safe side of the trade.
// Stage 0's `MayHoldListCell`: a scalar or a string holds no reference to a list, so a cell passed
// to a call answering one is only borrowed and stays the plain cell the loop builds.
let mayHoldListCell (resultType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(resultType) with
        | SemInt -> false
        | SemUInt(_bits) -> false
        | SemFloat -> false
        | SemBigInt -> false
        | SemString -> false
        | SemRune -> false
        | SemBytes -> false
        | SemBool -> false
        | SemNever -> false
        | _ -> true

// Stage 0's `_callArgumentCell`: a cons written as a call's argument leaves the loop through the
// call, as the cell a loop returns does, so it is asked for as one; a reference-counted cell is
// released after the call unless the callee's result keeps it. Only a cons onto a variable: the
// flag it rides on also marks a cons over a self-call as the tail-modulo-cons shape.
let withEscapingArgumentCell (argument: Expr) (escapes: Bool) (state: CoreLoweringState) =
    match (escapes, unspanArgument(argument)) with
        | (true, ExprCons(_head, tail)) ->
            match unspanArgument(tail) with
                | ExprVar(_name) -> withConsumerRequest((consumerRequestOf(state) with consTailPosition = true, escapingArgumentCell = true))(state)
                | _ -> state
        | _ -> state

// Stage 0's `ResultHoldsOnlyElementsOf` for a named callee's parameter by index: an enumerated reach
// that holds the parameter, if at all, only as whole unshared list elements (its entry `p/e`).
let recursive parameterAt (index: Int) (parameters: List(Str)) =
    match parameters with
        | [] -> None
        | parameter :: rest ->
            if index == 0
            then Some(parameter)
            else parameterAt(index - 1)(rest)

let recursive entryOfRoot (root: Str) (entries: List(ParameterReachEntry)) =
    match entries with
        | [] -> None
        | ParameterReachEntry { parameterName = name } :: rest ->
            if name == root || Ashes.Text.startsWith(name)(root + "/")
            then Some(name)
            else entryOfRoot(root)(rest)

let recursive hasUnenumeratedInputs (causes: List(ResultReachCause)) =
    match causes with
        | [] -> false
        | UnenumeratedInputs :: _rest -> true
        | _cause :: rest -> hasUnenumeratedInputs(rest)

let calleeHoldsOnlyElementsOf (callee: Str) (index: Int) (state: CoreLoweringState) =
    match calleeSummaryOf(callee)(state) with
        | Some(ReachSummary { function = ReachFunction { parameters = parameters }, reach = ResultReachState { counts = counts, causes = causes } }) ->
            match parameterAt(index)(parameters) with
                | Some(parameter) ->
                    !hasUnenumeratedInputs(causes) && (match entryOfRoot(parameter)(counts) with
                        | None -> true
                        | Some(name) -> name == parameter + "/e")
                | None -> false
        | None -> false

let isEmptyListLiteral (argument: Expr) =
    match unspanArgument(argument) with
        | ExprList([], _isMultiline) -> true
        | _ -> false

// Stage 0's `_argumentTypes`: each argument of a named call, by the call's span, for the record proof.
let noteTakenOverArgumentType (context: CoreCallContext) (index: Int) (argument: Expr) (lowered: LoweredCoreValue) =
    match (lowered, context.calleeName, context.siteSpan) with
        | (LoweredCoreValue { state = state, semanticType = semanticType, error = None }, Some(_callee), Some(span)) -> lowered with state = withStateTakenOverCallArguments((span, index, isEmptyListLiteral(argument), semanticType) :: stateTakenOverCallArguments(state))(state)
        | _ -> lowered

// Stage 0's `NoteTakenOverArgument`: an argument read out of an owned call-result slot, handed to a
// named callee, is recorded with its index, which the back edge or the return that would keep the slot
// reads to ask whether the callee holds the list only as whole unshared elements.
let noteTakenOverArgument (context: CoreCallContext) (index: Int) (lowered: LoweredCoreValue) =
    match (lowered, context.calleeName) with
        | (LoweredCoreValue { state = state, temp = temp, error = None }, Some(callee)) ->
            match definitionOfTemp(temp)(state.reversedInstructions) with
                | Some(LoadLocal(_target, slot)) ->
                    if state
                    |> stateGeneralRcOwnedSlots
                    |> isOwnedSlotOf(stateActiveFunctionOrigin(state))(slot)
                    then lowered with state = withStateTakenOverConsumers((stateActiveFunctionOrigin(state), slot, calleeHoldsOnlyElementsOf(callee)(index)(state)) :: stateTakenOverConsumers(state))(state)
                    else lowered
                | _ -> lowered
        | _ -> lowered

// A call's argument escapes into the callee's result, not into a loop's arena successor: it is
// lowered without the kept-successor mark, which is restored after it.
let restoreKeptSuccessorLetLoop (saved: Maybe(Str)) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = loweredState } -> lowered with state = withStateArenaKeptSuccessorLetLoop(saved)(loweredState)

let lowerCoreCallTyped (context: CoreCallContext) arity argument (transfers: Bool) consumed lower functionTemp resolved =
    match resolved with
        | FunctionTypeResolution { state = typedState, error = Some(error) } ->
            error
            |> failure(typedState)
            |> callStageOf
        | FunctionTypeResolution { state = typedState, argumentType = expectedType, resultType = resultType, error = None } ->
            match deferSelfCallResultType(context)(arity)(resultType)(typedState) with
                | (deferredState, callResultType) ->
                    deferredState
                    |> withArgumentRequest(Some(expectedType))(deferredState
                    |> argumentSite(context.calleeName)(context.argumentCount - arity + 1)
                    |> Some)(transfers || calleeResultReachesArgument(context.facts)(argumentIndexOf(context.facts)(arity)))(transfers == false)
                    |> collectCallArgumentRetains(arity
                    |> argumentIndexOf(context.facts)
                    |> calleeResultReachesArgument(context.facts))
                    |> withEscapingArgumentCell(argument)(transfers == false && mayHoldListCell(resultType)(deferredState))
                    |> withStateArenaKeptSuccessorLetLoop(None)
                    |> lower(argument)
                    |> restoreKeptSuccessorLetLoop(stateArenaKeptSuccessorLetLoop(deferredState))
                    |> noteTakenOverArgument(context)(context.argumentCount - arity)
                    |> noteTakenOverArgumentType(context)(context.argumentCount - arity)(argument)
                    |> stopCollectingCallArgumentRetains
                    |> retainTransferredChild(argument)(transfers)
                    |> retainAliasArgumentOwners(argument)(transfers == false && calleeResultReachesArgument(context.facts)(argumentIndexOf(context.facts)(arity)))
                    |> lowerCoreCallArgument(context)(arity)(argument)(consumed)(functionTemp)(expectedType)(callResultType)

let lowerCoreCallFunction (context: CoreCallContext) arity argument (transfers: Bool) lower (stage: CoreCallStage) =
    match stage with
        | CoreCallStage { lowered = LoweredCoreValue { error = Some(_error) } } -> stage
        | CoreCallStage { lowered = LoweredCoreValue { state = functionState, temp = functionTemp, semanticType = functionType, error = None }, consumedArguments = consumed } ->
            functionState
            |> ensureFunctionType(functionType)
            |> lowerCoreCallTyped(context)(arity)(argument)(transfers)(consumed)(lower)(functionTemp)

// Names the constructor-switching dropper of a named type (stage 0's
// `SynthesizeRuntimeManagedAdtDropper`), synthesizing it once through the state's label cache.
let synthesizeAdtDropperLabel (named: SemanticType) (state: CoreLoweringState) =
    match state with
        | CoreLoweringState { functions = functions, nextLambdaId = lambdaId, nextLabelId = labelId, programState = CoreProgramState { dropperLabels = cache } } ->
            match synthesizeRuntimeManagedAdtDropper(resolveType(state)(named))(dropperTypesOf(state))(cache)(lambdaId)(labelId) with
                | DropperSynthesis { label = label, cache = nextCache, queryContext = queryContext, functions = synthesized, nextLambdaId = nextLambdaId, nextLabelId = nextLabelId } ->
                    (label, withStateDropperResult(nextCache)(queryContext)((state with functions = append(reverse(synthesized))(functions), nextLambdaId = nextLambdaId, nextLabelId = nextLabelId)))

// Stage 0's `EmitRecursiveRuntimeManagedAdtDrop`: the value is handed to its type's dropper.
let emitAdtDropperCall (valueTemp: Int) (typeName: Str) (named: SemanticType) (state: CoreLoweringState) =
    match synthesizeAdtDropperLabel(named)(state) with
        | (Some(label), synthesized) ->
            match freshTempRun(2)(synthesized) with
                | FreshTemp { state = reserved, temp = environmentTemp } ->
                    reserved
                    |> emit(LoadConstInt(environmentTemp)(0))
                    |> emit(CallKnown(environmentTemp + 1)(label)(environmentTemp)(valueTemp)(-1)(false))
        | (None, synthesized) ->
            emit(RcDrop(valueTemp)(typeName)(-1)(true)(false)(None))(synthesized)

// Stage 0's `TryEmitContractRecordDropCall`: a record of the ownership contract is released by one
// call to a dropper synthesized once per type through the state's label cache.
let emitRecordDropperCall (valueTemp: Int) (named: SemanticType) (state: CoreLoweringState) =
    match state with
        | CoreLoweringState { functions = functions, nextLambdaId = lambdaId, nextLabelId = labelId, programState = CoreProgramState { dropperLabels = cache } } ->
            match synthesizeRecordDropper(resolveType(state)(named))(dropperTypesOf(state))(cache)(lambdaId)(labelId) with
                | DropperSynthesis { label = Some(label), cache = nextCache, queryContext = queryContext, functions = synthesized, nextLambdaId = nextLambdaId, nextLabelId = nextLabelId } ->
                    match freshTempRun(2)(withStateDropperResult(nextCache)(queryContext)((state with functions = append(reverse(synthesized))(functions), nextLambdaId = nextLambdaId, nextLabelId = nextLabelId))) with
                        | FreshTemp { state = reserved, temp = environmentTemp } ->
                            reserved
                            |> emit(LoadConstInt(environmentTemp)(0))
                            |> emit(CallKnown(environmentTemp + 1)(label)(environmentTemp)(valueTemp)(-1)(false))
                | DropperSynthesis { queryContext = queryContext } -> withStateDropperContext(queryContext)(state)

// An argument-free record the program's start recorded as joining the ownership contract, where
// the contract governs the lowering.
let usesRecordDropper (named: SemanticType) (state: CoreLoweringState) =
    (let types = dropperTypesOf(state)
    in
        match resolveType(state)(named) with
            | SemNamed(_symbolId, name, []) -> Ashes.Collection.Map.getStr(name)(types.contractRecords) == Some(true)
            | _ -> false)

// A recursive-copy or owned-child ADT is released by its own constructor-switching dropper.
let usesAdtDropper (named: SemanticType) (state: CoreLoweringState) =
    match heapFactsOf(named)(state) with
        | HeapLayoutFacts { runtimeOwnedChildAdtSupported = true } -> true
        | _ ->
            state
            |> coverageEnvironment
            |> heapRuntimeRecursiveCopyAdtLayout(named)

// Stage 0's `NeedsRuntimeManagedAdtNormalizer`: an admissible named type that neither the fixed
// copy-out nor the inline runtime-managed layouts express is released by the dropper synthesized
// for its type, never by walking its fields inline, which a type reaching itself would never end.
let usesGeneralAdtDropper (named: SemanticType) (state: CoreLoweringState) =
    match heapFactsOf(named)(state) with
        | HeapLayoutFacts { structuralCopy = ShallowCopy } -> false
        | facts -> runtimeManagedAdtLayout(facts) == false && isGeneralRcAdmissible(named)([])(state)

// Stage 0's `EmitGeneralRuntimeManagedAdtDrop`: the value is handed to the dropper synthesized
// once per type through the state's label cache.
let emitGeneralAdtDropperCall (valueTemp: Int) (typeName: Str) (named: SemanticType) (state: CoreLoweringState) =
    match state with
        | CoreLoweringState { functions = functions, nextLambdaId = lambdaId, nextLabelId = labelId, programState = CoreProgramState { dropperLabels = cache } } ->
            match synthesizeGeneralAdtDropper(resolveType(state)(named))(dropperTypesOf(state))(cache)(lambdaId)(labelId) with
                | DropperSynthesis { label = Some(label), cache = nextCache, queryContext = queryContext, functions = synthesized, nextLambdaId = nextLambdaId, nextLabelId = nextLabelId } ->
                    match freshTempRun(2)(withStateDropperResult(nextCache)(queryContext)((state with functions = append(reverse(synthesized))(functions), nextLambdaId = nextLambdaId, nextLabelId = nextLabelId))) with
                        | FreshTemp { state = reserved, temp = environmentTemp } ->
                            reserved
                            |> emit(LoadConstInt(environmentTemp)(0))
                            |> emit(CallKnown(environmentTemp + 1)(label)(environmentTemp)(valueTemp)(-1)(false))
                | DropperSynthesis { queryContext = queryContext } ->
                    state
                    |> withStateDropperContext(queryContext)
                    |> emit(RcDrop(valueTemp)(typeName)(-1)(true)(false)(None))

// The layout of a named type's first constructor.
let recursive firstConstructorLayoutOf (typeName: Str) (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> None
        | (CoreConstructorLayout { tag = 0 } as layout) :: rest ->
            match constructorResultName(layout) with
                | Some(resultName) ->
                    if resultName == typeName
                    then Some(layout)
                    else firstConstructorLayoutOf(typeName)(rest)
                | None -> firstConstructorLayoutOf(typeName)(rest)
        | _ :: rest -> firstConstructorLayoutOf(typeName)(rest)

let ownedLayoutChildren (constructorName: Maybe(Str)) (semanticType: SemanticType) (state: CoreLoweringState) =
    state
    |> heapFactsOf(semanticType)
    |> ownedChildrenOf(constructorName)

// Stage 0's `EmitRuntimeManagedChildDrop` family: a string-like leaf is released as one
// allocation, a list walked cell by cell releasing each unique cell's head, a tuple or a
// single-constructor ADT its owned children under a uniqueness test, and a recursive or
// owned-child ADT handed to its dropper; a value the walk does not recognize is left in place.
let recursive emitRuntimeChildDrop (valueTemp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemTuple(_elements) as tuple -> emitRuntimeTupleDrop(valueTemp)(tuple)(state)
        | SemList(element) -> emitRuntimeListDrop(valueTemp)(element)(state)
        | SemNamed(_symbolId, name, _arguments) as named ->
            emitRuntimeAdtDrop(valueTemp)(runtimeManagedAdtTypeName(name))(named)(state)
        | SemString ->
            emit(RcDrop(valueTemp)("String")(-1)(true)(false)(None))(state)
        | SemBytes ->
            emit(RcDrop(valueTemp)("Bytes")(-1)(true)(false)(None))(state)
        | SemBigInt ->
            emit(RcDrop(valueTemp)("BigInt")(-1)(true)(false)(None))(state)
        | _ -> state
and emitRuntimeListDrop (listTemp: Int) (elementType: SemanticType) (state: CoreLoweringState) =
    match freshLocal(state) with
        | FreshLocal { state = allocated, local = currentSlot } ->
            match allocated
            |> emit(StoreLocal(currentSlot)(listTemp))
            |> freshListDropLabels("rcdrop_list") with
                | (loopLabel, sharedLabel, endLabel, labelled) ->
                    match labelled
                    |> emit(Label(loopLabel))
                    |> emitListDropTest(currentSlot)(endLabel) with
                        | (tested, currentTemp) ->
                            tested
                            |> emitUniqueCellTest(currentTemp)(sharedLabel)
                            |> emitListDropHead(currentTemp)(elementType)
                            |> emitListDropAdvance(currentTemp)(currentSlot)(loopLabel)
                            |> emitListDropSharedExit(currentTemp)(sharedLabel)(endLabel)
and emitListDropHead (cellTemp: Int) (elementType: SemanticType) (state: CoreLoweringState) =
    if resultSurvivesReset(elementType)(state)
    then state
    else
        match freshTemp(state) with
            | FreshTemp { state = reserved, temp = headTemp } ->
                reserved
                |> emit(LoadMemOffset(headTemp)(cellTemp)(0))
                |> emitRuntimeChildDrop(headTemp)(elementType)
and emitRuntimeTupleDrop (valueTemp: Int) (tuple: SemanticType) (state: CoreLoweringState) =
    match freshLabel("rc_drop_tuple_shared")(state) with
        | FreshLabel { state = labelled, label = sharedLabel } ->
            match ownedLayoutChildren(None)(tuple)(labelled) with
                | [] ->
                    emit(RcDrop(valueTemp)("Tuple")(-1)(true)(false)(None))(labelled)
                | children ->
                    labelled
                    |> emitUniqueCellTest(valueTemp)(sharedLabel)
                    |> emitTupleChildDrops(valueTemp)(children)
                    |> emit(Label(sharedLabel))
                    |> emit(RcDrop(valueTemp)("Tuple")(-1)(true)(false)(None))
and emitTupleChildDrops (valueTemp: Int) (children: List(HeapLayoutChild)) (state: CoreLoweringState) =
    match children with
        | [] -> state
        | HeapLayoutChild { fieldIndex = index, childType = childType } :: rest ->
            match freshTemp(state) with
                | FreshTemp { state = reserved, temp = childTemp } ->
                    reserved
                    |> emit(LoadMemOffset(childTemp)(valueTemp)(index * 8))
                    |> emitRuntimeChildDrop(childTemp)(childType)
                    |> emitTupleChildDrops(valueTemp)(rest)
and emitRuntimeAdtDrop (valueTemp: Int) (typeName: Str) (named: SemanticType) (state: CoreLoweringState) =
    if isScalarResultType(named)
    then
        emit(RcDrop(valueTemp)(typeName)(-1)(true)(false)(None))(state)
    else
        if usesAdtDropper(named)(state)
        then emitAdtDropperCall(valueTemp)(typeName)(named)(state)
        else
            if usesGeneralAdtDropper(named)(state)
            then emitGeneralAdtDropperCall(valueTemp)(typeName)(named)(state)
            else
                if usesRecordDropper(named)(state)
                then emitRecordDropperCall(valueTemp)(named)(state)
                else emitFirstConstructorDrop(valueTemp)(typeName)(named)(state)
and emitFirstConstructorDrop (valueTemp: Int) (typeName: Str) (named: SemanticType) (state: CoreLoweringState) =
    match state
    |> stateConstructorsOfType(typeName)
    |> firstConstructorLayoutOf(typeName) with
        | Some(CoreConstructorLayout { name = constructorName, tagless = tagless }) ->
            match ownedLayoutChildren(Some(constructorName))(named)(state) with
                | [] ->
                    emit(RcDrop(valueTemp)(typeName)(-1)(true)(false)(None))(state)
                | children ->
                    match freshTemp(state) with
                        | FreshTemp { state = reserved, temp = uniqueTemp } ->
                            match freshLabel("rc_drop_shared")(reserved) with
                                | FreshLabel { state = labelled, label = sharedLabel } ->
                                    labelled
                                    |> emit(RcIsUnique(uniqueTemp)(valueTemp))
                                    |> emit(JumpIfFalse(uniqueTemp)(sharedLabel))
                                    |> emitAdtFieldDrops(valueTemp)(tagless)(children)
                                    |> emit(Label(sharedLabel))
                                    |> emit(RcDrop(valueTemp)(typeName)(-1)(true)(false)(None))
        | None ->
            emit(RcDrop(valueTemp)(typeName)(-1)(true)(false)(None))(state)
and emitAdtFieldDrops (valueTemp: Int) (tagless: Bool) (children: List(HeapLayoutChild)) (state: CoreLoweringState) =
    match children with
        | [] -> state
        | HeapLayoutChild { fieldIndex = index, childType = childType } :: rest ->
            match freshTemp(state) with
                | FreshTemp { state = reserved, temp = childTemp } ->
                    reserved
                    |> emit(GetAdtField(childTemp)(valueTemp)(index)(tagless))
                    |> emitRuntimeChildDrop(childTemp)(childType)
                    |> emitAdtFieldDrops(valueTemp)(tagless)(rest)
