// Helper inlining, reuse-specialized and concrete copies of a callee, constrained bindings, cons
// under tail-modulo-cons, and `lowerCore`, the dispatch over every expression form. A part of
// `AshesCompiler.Semantics.CoreLowering`.

import Ashes.Collection.List.append
import Ashes.Collection.List.length
import Ashes.Collection.List.map
import Ashes.Collection.List.reverse
import Ashes.Collection.Map.MapTree
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.TraitConstraintSyntax
import AshesCompiler.Frontend.Syntax.callArgumentsInline
import AshesCompiler.Frontend.Syntax.callArgumentsPipe
import AshesCompiler.Semantics.CallOwnership
import AshesCompiler.Semantics.RecursiveTraitEvidence
import AshesCompiler.Semantics.CoreConstrainedSpecialization
import AshesCompiler.Semantics.CoreConstrainedBindings
import AshesCompiler.Semantics.OperatorConstraintOrigins.mayOriginateOperatorConstraint
import AshesCompiler.Semantics.TraitEvidenceAbi
import AshesCompiler.Semantics.CoreCapabilityLowering
import AshesCompiler.Semantics.ExprMentions.exprReadsName
import AshesCompiler.Semantics.ExprMentions.exprMentionsName
import AshesCompiler.Semantics.ConstructionRetains
import AshesCompiler.Semantics.TraitResolution
import AshesCompiler.Semantics.TraitResolution.resolveTraitEvidence
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.ResultReachSummaries.ResultReachState
import AshesCompiler.Semantics.ResultReachSummaries.ReachSummary
import AshesCompiler.Semantics.ResultReachSummaries.ReachFunction
import AshesCompiler.Semantics.OwnershipSummary
import AshesCompiler.Semantics.TcoRuntimeManagedParams
import AshesCompiler.Semantics.TraitEvidenceRewriting
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.TypeSchemes
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.ReuseFunctionSpecialization
import AshesCompiler.Semantics.ReuseResetSafety.specializationRebuildsAccumulator
import AshesCompiler.Semantics.ReuseResetSafety.accumulatorIsFullyPersistent
import AshesCompiler.Semantics.ReuseResetSafety.reuseResetSafety
import AshesCompiler.Semantics.ReuseResetSafety.ReuseResetSafety
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.CoreLowering.LoweringState
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.recordedClosedNodeType
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.recordedClosedTypeAt
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.recordedDictionariesAt
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.recordedCopyInstantiation
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.recordedOperatorGenericScheme
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.recordedSettledTypeAt
import AshesCompiler.Semantics.NodeIdentity.nodeIdentityOf
import AshesCompiler.Semantics.CoreLowering.Emit
import AshesCompiler.Semantics.CoreLowering.Owners
import AshesCompiler.Semantics.CoreLowering.CopyPlans
import AshesCompiler.Semantics.CoreLowering.TcoReset
import AshesCompiler.Semantics.CoreLowering.Bindings
import AshesCompiler.Semantics.CoreLowering.LoopPlacement
import AshesCompiler.Semantics.CoreLowering.LoopFrames
import AshesCompiler.Semantics.CoreLowering.CallArguments
import AshesCompiler.Semantics.CoreLowering.CallResults
import AshesCompiler.Semantics.CoreLowering.Matches
import AshesCompiler.Semantics.CoreLowering.RecursiveGroups
import AshesCompiler.Semantics.CoreLowering.Aggregates
import AshesCompiler.Semantics.CoreLowering.Constructors
import AshesCompiler.Semantics.CoreLowering.Records
import AshesCompiler.Semantics.CoreLowering.Operators
import AshesCompiler.Semantics.CoreLowering.Handlers
import AshesCompiler.Semantics.CoreLowering.TailCalls
import AshesCompiler.Semantics.CoreLowering.ResultHandOver.arenaDeepCopySupported
import AshesCompiler.Semantics.ExprMentions.exprContainsAwait
import AshesCompiler.Semantics.PerceusLifetimePlacement.placeFunctionLifetimes
import AshesCompiler.Semantics.StateMachineTransform.transformStateMachine
import AshesCompiler.Semantics.StateMachineTransform.spillCrossBlockTemps
import AshesCompiler.Semantics.StateMachineTransform.StateMachineResult
export (
    value recordCoverageOpenSchemes,
    value replaceConstrainedScheme,
    value registerConcreteSource,
    value recordCoverageOperatorConstrainedNames,
    value constrainedBindingPlan,
    value constrainedBindingPlanWith,
    value writtenRequirements,
    value lowerConstrainedBindingValue,
    value openPossiblyConstrainedRecursive,
    value constrainedAliasBinding,
    value lowerConstrainedAlias,
    value storedConstrainedScheme,
    value lowerCore,
)

// Whether a reuse specialization's body is being lowered (stage 0's `_inSpecialization`): its
// fresh inputs are recorded from the generating call until the body is lowered.
let loweringReuseSpecialization (state: CoreLoweringState) =
    match stateSpecializationFreshInputs(state) with
        | Some(_names) -> true
        | None -> false

// Stage 0's `LowerCallTryReuseInlineForm` trigger (`InlinesHelperCallHere`): a saturated helper
// call is spliced into its site while a reuse token is live (so the helper's constructor can
// consume it), anywhere in a reuse specialization's body (whose helpers it never captured), or
// under a loop's back edge when the helper's result is fresh (so the argument passes without the
// accepts-bit retain of a general call).
let inlineHelperTriggered (state: CoreLoweringState) =
    match (state.reuseTokens, stateBackEdgeArgumentSlot(state)) with
        | (_token :: _rest, _slot) -> true
        | ([], Some(_slot)) -> true
        | ([], None) -> loweringReuseSpecialization(state)

// Only the back-edge trigger asks for a fresh result.
let inlineHelperNeedsFreshResult (state: CoreLoweringState) =
    match state.reuseTokens with
        | [] -> loweringReuseSpecialization(state) == false
        | _token :: _rest -> false

// The callee name resolves to a function at this site: a local shadowing the helper's name with a
// non-function value keeps the call out of the inline path (stage 0's `_shadowedInlinables`). A
// name bound nowhere here is the top-level helper itself, which a spliced body (a reuse
// specialization's, an inlined helper's) reaches without having captured it.
let calleeBindsFunction (callee: Str) (state: CoreLoweringState) =
    match lookupBinding(callee)(state.bindings) with
        | Some(CoreBinding { scheme = TypeScheme { body = body } }) ->
            match resolveType(state)(body) with
                | SemFunction(_parameter, _result, _row) -> true
                | _ -> false
        | None -> true

let recursive parameterReachedBy (parameter: Str) (counts: List(ParameterReachEntry)) =
    match counts with
        | [] -> false
        | ParameterReachEntry { parameterName = name } :: rest -> name == parameter || Ashes.Text.startsWith(name)(parameter + "/") || parameterReachedBy(parameter)(rest)

let helperResultFresh (reach: ResultReachState) =
    match reach with
        | ResultReachState { counts = [], isPoisoned = false } -> true
        | _ -> false

// Stage 0's `InlinesHelperCallHere` for a helper an inlined body calls: that call is spliced in
// turn only when the splice's own conditions hold here. One declined is lowered as an ordinary
// reference, which the caller's scope may not reach.
let nestedHelperInlinedHere (name: Str) (parameters: List(Str)) (body: Expr) (state: CoreLoweringState) =
    if inlineHelperTriggered(state) && calleeBindsFunction(name)(state)
    then
        if inlineHelperNeedsFreshResult(state)
        then
            match calleeReachSummary(name)(parameters)(body)(state) with
                | (_summaryParameters, _summaryBody, reach) -> helperResultFresh(reach)
        else true
    else false

// Stage 0's `InlinedBodyReferencesResolveHere`: an inlined body lowers in the caller's scope, so
// every name it reads past its parameters must resolve there, lexically, as a constructor, or
// by being an inlinable helper whose own references resolve in turn. A helper already accepted
// on this walk is a shared sibling, not a cycle. A module qualifier (`Ashes.Text` of
// `Ashes.Text.byteLength`) names no binding; stage 0's free-variable walk never collects one.
let recursive inlinedReferencesResolveHere (names: List(Str)) (visited: List(Str)) (state: CoreLoweringState) =
    match names with
        | [] -> true
        | name :: rest ->
            if inlinedReferenceResolvesHere(name)(visited)(state)
            then inlinedReferencesResolveHere(rest)(visited)(state)
            else false
and inlinedReferenceResolvesHere (name: Str) (visited: List(Str)) (state: CoreLoweringState) =
    match lookupBinding(name)(state.bindings) with
        | Some(_binding) -> true
        | None ->
            if constructorNameKnown(name)(state) || containsName(name)(visited) || Ashes.Text.contains(name)(".") || lookupTopLevelFunctionRef(name)(stateTopLevelFunctionRefIndex(state)) != None
            then true
            else
                if state
                |> stateInlinableHelpers
                |> containsName(name)
                then
                    match lookupLetLambdaNamed(name)(state) with
                        | Some((parameters, body)) ->
                            nestedHelperInlinedHere(name)(parameters)(body)(state) && inlinedReferencesResolveHere(collectFree(body)(parameters)([]))(name :: visited)(state)
                        | None -> false
                else false

// The helper a saturated call names, when the call is to be spliced: registered, not being
// spliced already, bound as a function here, its arity matched, its references resolving here,
// and its result fresh when the trigger is the back edge rather than a live token.
let inlinableHelperOf (spine: CoreCallSpine) (state: CoreLoweringState) =
    match unspanArgument(spine.root) with
        | ExprVar(callee) ->
            if inlineHelperTriggered(state) && containsName(callee)(stateInlinableHelpers(state)) && containsName(callee)(stateInliningInProgress(state)) == false && calleeBindsFunction(callee)(state)
            then
                match lookupLetLambdaNamed(callee)(state) with
                    | Some((parameters, body)) ->
                        match calleeReachSummary(callee)(parameters)(body)(state) with
                            | (_summaryParameters, _summaryBody, reach) ->
                                if length(parameters) == coreListLength(spine.arguments) && (inlineHelperNeedsFreshResult(state) == false || helperResultFresh(reach)) && inlinedReferencesResolveHere(collectFree(body)(parameters)([]))([callee])(state)
                                then Some((callee, parameters, body, reach))
                                else None
                    | None -> None
            else None
        | _ -> None

// Each argument lowered in the caller's scope under a plain request and stored into a fresh
// local of its own, so the helper's parameter names cannot capture it: the argument, its temp,
// its type, and its slot.
let recursive lowerInlinedHelperArguments (arguments: List(Expr)) lower (state: CoreLoweringState) (reversed: List((Expr, Int, SemanticType, Int))) =
    match arguments with
        | [] -> (state, reverse(reversed), None)
        | argument :: rest ->
            match state
            |> withConsumerRequest(emptyConsumerRequest)
            |> lower(argument) with
                | LoweredCoreValue { state = failedState, error = Some(error) } -> (failedState, [], Some(error))
                | LoweredCoreValue { state = argumentState, temp = temp, semanticType = argumentType, error = None } ->
                    match freshLocal(argumentState) with
                        | FreshLocal { state = allocated, local = slot } ->
                            lowerInlinedHelperArguments(rest)(lower)(emit(StoreLocal(slot)(temp))(allocated))((argument, temp, argumentType, slot) :: reversed)

let recursive bindInlinedParameters (parameters: List(Str)) (arguments: List((Expr, Int, SemanticType, Int))) (state: CoreLoweringState) =
    match (parameters, arguments) with
        | (parameter :: restParameters, (_argument, _temp, argumentType, slot) :: restArguments) ->
            state
            |> addBinding(parameter)(TypeScheme(quantified = [], body = argumentType, constraints = []))(CoreLocal(slot))
            |> bindInlinedParameters(restParameters)(restArguments)
        | _ -> state

let isNewlyProducedTemp (temp: Int) (state: CoreLoweringState) =
    match runtimeTempStateOf(temp)(state) with
        | Some(RuntimeNewlyProduced) -> true
        | _ -> false

// Stage 0's `ReleaseInlinedFreshArguments`: a fresh reference-counted argument the spliced body
// read through its parameter slot is released after the body, as a call releases the fresh
// argument it consumed, unless the helper's result may keep the parameter (the body's result
// then carries the reference on).
let recursive releaseInlinedFreshArguments (parameters: List(Str)) (arguments: List((Expr, Int, SemanticType, Int))) (reach: ResultReachState) (resultTemp: Int) (state: CoreLoweringState) =
    match (parameters, arguments) with
        | (parameter :: restParameters, (argument, temp, argumentType, _slot) :: restArguments) ->
            if isVariableArgument(argument) || temp == resultTemp || isNewlyProducedTemp(temp)(state) == false || reach.isPoisoned || parameterReachedBy(parameter)(reach.counts)
            then releaseInlinedFreshArguments(restParameters)(restArguments)(reach)(resultTemp)(state)
            else
                state
                |> emitOwnedValueRelease(emit)(temp)(argumentType)
                |> releaseInlinedFreshArguments(restParameters)(restArguments)(reach)(resultTemp)
        | _ -> state

let recursive withoutName (name: Str) (names: List(Str)) =
    match names with
        | [] -> []
        | candidate :: rest ->
            if candidate == name
            then rest
            else candidate :: withoutName(name)(rest)

let withoutInliningInProgress (callee: Str) (state: CoreLoweringState) =
    withStateInliningInProgress(state
    |> stateInliningInProgress
    |> withoutName(callee))(state)

// Stage 0's `InlineCall` fresh-parameter half: an inlined helper's parameter inherits the enclosing
// specialization's fresh-input status when its argument names a fresh input, or is any expression
// other than a bare name — a value the arm computed lives in the per-iteration arena scratch the
// reset reclaims, exactly like a parameter handed in from the caller. A parameter bound to a name
// the arm matched out of the accumulator is already persistent and is left alone.
let recursive extendedSpecializationFreshInputs (parameters: List(Str)) (arguments: List(Expr)) (names: List(Str)) =
    match (parameters, arguments) with
        | (parameter :: restParameters, argument :: restArguments) ->
            if containsName(parameter)(names)
            then extendedSpecializationFreshInputs(restParameters)(restArguments)(names)
            else
                match unspanArgument(argument) with
                    | ExprVar(argumentName) ->
                        if containsName(argumentName)(names)
                        then extendedSpecializationFreshInputs(restParameters)(restArguments)(parameter :: names)
                        else extendedSpecializationFreshInputs(restParameters)(restArguments)(names)
                    | _ -> extendedSpecializationFreshInputs(restParameters)(restArguments)(parameter :: names)
        | _ -> names

let withInlinedSpecializationFreshInputs (parameters: List(Str)) (arguments: List(Expr)) (state: CoreLoweringState) =
    match stateSpecializationFreshInputs(state) with
        | None -> state
        | Some(names) ->
            withStateSpecializationFreshInputs(names
            |> extendedSpecializationFreshInputs(parameters)(arguments)
            |> Some)(state)

// Stage 0's `InlineCall`: the arguments are evaluated in the caller's scope, the parameters bound
// to their slots, and the helper's body lowered in place under the call's own request; the
// caller's bindings are restored afterwards.
let lowerInlinedHelperCall (callee: Str) (parameters: List(Str)) (body: Expr) (reach: ResultReachState) (arguments: List(Expr)) lower (state: CoreLoweringState) =
    match lowerInlinedHelperArguments(arguments)(lower)(state)([]) with
        | (failedState, _lowered, Some(error)) -> failure(failedState)(error)
        | (argumentState, lowered, None) ->
            match argumentState
            |> withStateInliningInProgress(callee :: stateInliningInProgress(argumentState))
            |> withInlinedSpecializationFreshInputs(parameters)(arguments)
            |> bindInlinedParameters(parameters)(lowered)
            |> withConsumerRequest(consumerRequestOf(state))
            |> lower(body) with
                | LoweredCoreValue { state = failedState, error = Some(error) } ->
                    failure(failedState
                    |> restoreBindings(state.bindings)
                    |> withoutInliningInProgress(callee)
                    |> (given (restored: CoreLoweringState) ->
                        withStateSpecializationFreshInputs(stateSpecializationFreshInputs(state))(restored)))(error)
                | LoweredCoreValue { state = bodyState, temp = resultTemp, semanticType = resultType, error = None } ->
                    bodyState
                    |> restoreBindings(state.bindings)
                    |> withoutInliningInProgress(callee)
                    |> (given (restored: CoreLoweringState) ->
                        withStateSpecializationFreshInputs(stateSpecializationFreshInputs(state))(restored))
                    |> releaseInlinedFreshArguments(parameters)(lowered)(reach)(resultTemp)
                    |> success(resultTemp)(resultType)

let tryInlineHelperCall (spine: CoreCallSpine) lower (state: CoreLoweringState) =
    match inlinableHelperOf(spine)(state) with
        | Some((callee, parameters, body, reach)) ->
            state
            |> lowerInlinedHelperCall(callee)(parameters)(body)(reach)(spine.arguments)(lower)
            |> Some
        | None -> None

let recursive lastArgument (arguments: List(Expr)) =
    match arguments with
        | [] -> None
        | argument :: [] -> Some(argument)
        | _argument :: rest -> lastArgument(rest)

// Stage 0's `IsFreshOwnershipResultCall`: the accumulator argument is itself a saturated call
// whose callee's whole-program result reach keeps none of its parameters, so the value handed to
// the specialization is freshly built here and aliased by nothing the specialization could
// overwrite out from under.
let reuseSpecializationArgumentIsFresh (argument: Expr) (state: CoreLoweringState) =
    match collectCallSpine(argument) with
        | CoreCallSpine { arguments = [] } -> false
        | CoreCallSpine { root = root, arguments = callArguments } ->
            match unspanArgument(root) with
                | ExprVar(callee) ->
                    match lookupLetLambdaNamed(callee)(state) with
                        | Some((recordedParameters, recordedBody)) ->
                            match calleeReachSummary(callee)(recordedParameters)(recordedBody)(state) with
                                | (parameters, _body, reach) -> length(parameters) == coreListLength(callArguments) && helperResultFresh(reach)
                        | None -> false
                | _ -> false

// The accumulator parameter type and the whole function type a specialization candidate is called
// at, read through the current substitution. A callee whose binding is not a resolved single-
// parameter function has neither.
// The type of the accumulator (the last parameter) after `argumentCount` arguments are applied,
// paired with the callee's whole resolved function type. `None` when the callee is not bound here
// or takes fewer parameters than the call passes arguments.
let recursive appliedAccumulatorType (functionType: SemanticType) (argumentCount: Int) (accumulator: Maybe(SemanticType)) =
    if argumentCount == 0
    then accumulator
    else
        match functionType with
            | SemFunction(argument, result, _effects) -> appliedAccumulatorType(result)(argumentCount - 1)(Some(argument))
            | _ -> None

let reuseSpecializationCalleeType (callee: Str) (argumentCount: Int) (state: CoreLoweringState) =
    match lookupBinding(callee)(state.bindings) with
        | Some(CoreBinding { scheme = TypeScheme { body = body } }) ->
            match resolveType(state)(body) with
                | SemFunction(_parameterType, _resultType, _effects) as functionType ->
                    match appliedAccumulatorType(functionType)(argumentCount)(None) with
                        | Some(accumulatorType) -> Some((accumulatorType, functionType))
                        | None -> None
                | _ -> None
        | None -> None

// Stage 0's `QualifyVariableReuseSpecialization`: the accumulator is a bare name the enclosing
// loop's entry deep copy already made uniquely owned, so the specialization may rewrite it in
// place without proving the value itself fresh.
let reuseSpecializationArgumentIsLinearAccumulator (argument: Expr) (state: CoreLoweringState) =
    match unspanArgument(argument) with
        | ExprVar(name) ->
            state
            |> stateLinearSpecializationAccumulators
            |> containsName(name)
        | _ -> false

// The two ways stage 0 proves the accumulator uniquely owned at the call. The fresh-result path
// additionally requires a list accumulator (`QualifyFreshResultReuseSpecialization`'s
// `NthCurriedArgType ... is not TypeRef.TList` rejection): a named ADT's fresh cells are allocated
// into the never-reset to-space, which only the loop path's entry copy makes safe.
let specializationAccumulatorIsUnique (accumulator: Expr) (accumulatorType: SemanticType) (state: CoreLoweringState) =
    if reuseSpecializationArgumentIsLinearAccumulator(accumulator)(state)
    then true
    else
        match accumulatorType with
            | SemList(_element) -> reuseSpecializationArgumentIsFresh(accumulator)(state)
            | _ -> false

// The specialization a call qualifies for, stage 0's `QualifyReuseSpecializationCall`: the
// accumulator is either a provably fresh call result or a loop accumulator the entry copy made
// unique. The callee is one of the program's self-recursive top-level functions,
// takes exactly its own parameter count of arguments, rebuilds its accumulator, carries a layout
// the specialization can keep persistent (a list always does; a named ADT does when the rebuild's
// to-space materialization reaches every one of its leaf fields), is handed a provably fresh
// accumulator, and mentions
// only names a body lowered here can bind. A candidate already being specialized is skipped, so
// neither the specialization's own self-calls nor the call it is generated for re-enter this path.
// Stage 0's `SpecializationReferencesResolvable`: a specialization's body is lowered with its
// helpers inlined wherever they are called (its arms hold the linear parameter's reuse tokens), so
// a free name of the candidate resolves when it is bound here, a constructor, a top-level function
// called by label, or an inlinable helper, whatever its result; a module qualifier
// (`Ashes.Byte` of `Ashes.Byte.compare`) is not a binding at all.
let specializationReferenceResolvesHere (name: Str) (state: CoreLoweringState) =
    match lookupBinding(name)(state.bindings) with
        | Some(_binding) -> true
        | None ->
            Ashes.Text.contains(name)(".") || constructorNameKnown(name)(state) || lookupTopLevelFunctionRef(name)(stateTopLevelFunctionRefIndex(state)) != None || containsName(name)(stateInlinableHelpers(state))

let recursive specializationReferencesResolveHere (names: List(Str)) (state: CoreLoweringState) =
    match names with
        | [] -> true
        | name :: rest -> specializationReferenceResolvesHere(name)(state) && specializationReferencesResolveHere(rest)(state)

// The accumulator type a call's gates read: the accumulator argument's own when it is a bound
// name, which the call unifies with the callee's (possibly generic) accumulator parameter, stage
// 0's argument source type; the callee's otherwise.
let callAccumulatorType (accumulator: Expr) (calleeAccumulatorType: SemanticType) (state: CoreLoweringState) =
    match unspanArgument(accumulator) with
        | ExprVar(name) ->
            match lookupBinding(name)(state.bindings) with
                | Some(CoreBinding { scheme = TypeScheme { body = body } }) -> resolveType(state)(body)
                | None -> calleeAccumulatorType
        | _ -> calleeAccumulatorType

let reuseSpecializedCallOf (spine: CoreCallSpine) (state: CoreLoweringState) =
    match unspanArgument(spine.root) with
        | ExprVar(callee) ->
            if stateReuseEnabled(state) && containsName(callee)(stateSpecializingInProgress(state)) == false
            then
                match state
                |> stateSpecializationCandidateIndex
                |> lookupSpecializationCandidate(callee) with
                    | Some((parameters, value)) ->
                        match (specializationLinearParameter(parameters)(value), lastArgument(spine.arguments)) with
                            | (Some(parameter), Some(accumulator)) ->
                                match reuseSpecializationCalleeType(callee)(coreListLength(spine.arguments))(state) with
                                    | Some((calleeAccumulatorType, functionType)) ->
                                        let accumulatorType = callAccumulatorType(accumulator)(calleeAccumulatorType)(state)
                                        in
                                            if specializationArity(parameters)(value) == coreListLength(spine.arguments) && specializationRebuildsAccumulator(functionType)(coreListLength(spine.arguments)) && accumulatorLayoutIsPersistable(accumulatorType)(state) && specializationAccumulatorIsUnique(accumulator)(accumulatorType)(state) && specializationReferencesResolveHere(collectFree(value)([callee])([]))(state)
                                            then Some((callee, parameter, parameters, value, accumulatorType, functionType, spine.arguments))
                                            else None
                                    | None -> None
                            | _ -> None
                    | None -> None
            else None
        | _ -> None

let relabelReuseSpecialization (label: Str) prepared =
    match prepared with
        | PreparedCoreRecursiveGroup { state = state, members = member :: [], error = None } ->
            PreparedCoreRecursiveGroup(
                state = state,
                members = [(member with label = label)],
                error = None
            )
        | _ -> prepared

let restoreSpecializationScope (outer: CoreLoweringState) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state } ->
            lowered with state = withStateSpecializationFreshInputs(stateSpecializationFreshInputs(outer))(state
            |> withStateSpecializingInProgress(stateSpecializingInProgress(outer))
            |> withStateSpecializingLinearParam(stateSpecializingLinearParam(outer))
            |> withStateSpecializingReuseLabel(stateSpecializingReuseLabel(outer)))

// Stage 0's `GetOrCreateReuseSpecialization` plus `LowerReuseSpecializedCall`, as one recursive
// group: the candidate's own lambda is lowered under a label of its own with its accumulator armed
// as a linear reuse root, its self-calls bind to that label through the group's self binding, and
// the group's continuation is the original call, now reaching the specialization. Each qualifying
// call site generates its own specialization; stage 0 shares one per concrete instantiation.
let recursive specializedCallExpression (callee: Expr) (arguments: List(Expr)) =
    match arguments with
        | [] -> callee
        | argument :: rest ->
            specializedCallExpression(ExprCall(callee)(argument)(false)(callArgumentsInline))(rest)

// Stage 0's `GetOrCreateReuseSpecialization` cache key: a specialization is monomorphized to the
// call's own argument types, so a function used at two concrete types gets two of them, and two
// call sites at the same type share one. A generic callee's own type is the same at every call,
// so the accumulator's concrete type keys it too.
let reuseSpecializationCacheKey (callee: Str) (functionType: SemanticType) (accumulatorType: SemanticType) = callee + "|" + formatSemanticType(functionType) + "|" + formatSemanticType(accumulatorType)

let recursive lookupReuseSpecialization (key: Str) (specializations: List((Str, Str))) =
    match specializations with
        | [] -> None
        | (candidate, label) :: rest ->
            if candidate == key
            then Some(label)
            else lookupReuseSpecialization(key)(rest)

// A call routed to an already-generated specialization: its label takes a closure over the empty
// environment (the generated body captures nothing — `inlinedReferencesResolveHere` is what proves
// that at qualification), the closure goes in a local, and the ordinary call path applies the
// arguments to it exactly as it would to any other bound function. The callee is marked in progress
// for that continuation exactly as generation marks it, since the rebuilt call otherwise qualifies
// again, hits this same cache entry, and recurses without end.
let lowerCachedReuseSpecializedCall (callee: Str) (label: Str) (functionType: SemanticType) (arguments: List(Expr)) lower (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = envState, temp = environmentTemp } ->
            match envState
            |> emit(LoadConstInt(environmentTemp)(0))
            |> emitClosure(label)(environmentTemp)(0)(false) with
                | (closureState, closureTemp) ->
                    match freshLocal(closureState) with
                        | FreshLocal { state = slotState, local = slot } ->
                            match slotState
                            |> emit(StoreLocal(slot)(closureTemp))
                            |> addBinding(callee)(TypeScheme(quantified = [], body = functionType, constraints = []))(CoreLocal(slot))
                            |> (given (bound: CoreLoweringState) -> withStateSpecializingInProgress(callee :: stateSpecializingInProgress(bound))(bound))
                            |> lower(specializedCallExpression(ExprVar(callee))(arguments)) with
                                | LoweredCoreValue { state = callState, temp = temp, semanticType = semanticType, error = error } ->
                                    LoweredCoreValue(
                                        state = callState
                                        |> withStateSpecializingInProgress(stateSpecializingInProgress(state))
                                        |> restoreBindings(state.bindings),
                                        temp = temp,
                                        semanticType = semanticType,
                                        error = error
                                    )

let recursive functionWithLabel (label: Str) (functions: List(IrFunction)) =
    match functions with
        | [] -> None
        | (IrFunction { label = candidate } as function) :: rest ->
            if candidate == label
            then Some(function)
            else functionWithLabel(label)(rest)

// Stage 0's `RecordReuseSpecializationDecisions` reset-safety half plus `RecordFullyReusingCall`:
// the body that bound the linear parameter is scanned, and when it allocates nothing that could
// escape the loop watermark AND the accumulator's own layout is fully persistent, the callee is
// recorded so a back edge threading its result knows the value is that accumulator rewritten in
// place. `AccumulatorIsFullyPersistent` is stricter than the routing gate: a list qualifies only
// over a copy-type element, since a heap element's cells are not the whole graph.
let specializationAccumulatorFullyPersistent (accumulatorType: SemanticType) (state: CoreLoweringState) =
    match accumulatorType with
        | SemNamed(_symbolId, typeName, _arguments) -> accumulatorLayoutIsPersistable(accumulatorType)(state)
        | other -> accumulatorIsFullyPersistent(other)

let recordFullyReusingSpecialization (callee: Str) (accumulatorType: SemanticType) (outer: CoreLoweringState) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { error = Some(_error) } -> lowered
        | LoweredCoreValue { state = state } ->
            match stateSpecializingReuseLabel(state) with
                | None -> lowered
                | Some(reuseLabel) ->
                    match functionWithLabel(reuseLabel)(state.functions) with
                        | None -> lowered
                        | Some(IrFunction { instructions = instructions }) ->
                            match reuseResetSafety(instructions) with
                                | ReuseResetSafety { accepted = false } -> lowered
                                | ReuseResetSafety { accepted = true } ->
                                    if specializationAccumulatorFullyPersistent(accumulatorType)(state)
                                    then lowered with state = withStateFullyReusingCallees(callee :: stateFullyReusingCallees(state))(state)
                                    else lowered

// Stage 0's `BuildReuseSpecializationFunctionType`: the specialization's body is lowered at the
// call's function type, so a parameter whose type its body pins only in a later arm (a key a
// comparison reads) is already known where an earlier arm stores it, and a constructor field built
// from it is relocated into to-space by its concrete type. The candidate's declared type is
// instantiated afresh and its accumulator parameter bound to the accumulator the call passes: a
// type parameter the body never constrains (a map's value type) takes the call's type argument
// from there, as it does in stage 0, where the arguments are lowered first. Left open, such a field
// is stored by reference, and a fresh value the body builds for it (an update's new value) stays in
// the arena scratch the enclosing call windows reclaim.
let pinSpecializationType (callee: Str) (accumulatorType: SemanticType) (argumentCount: Int) (prepared: PreparedCoreRecursiveGroup) =
    match prepared with
        | PreparedCoreRecursiveGroup { state = state, members = (PreparedCoreRecursiveBinding { semanticType = semanticType } as member) :: [], error = None } ->
            match lookupBinding(callee)(state.bindings) with
                | None -> prepared
                | Some(binding) ->
                    match instantiateBinding(binding)(state) with
                        | InstantiatedBinding { state = instantiatedState, semanticType = declared } ->
                            match bindType(semanticType)(declared)(instantiatedState) with
                                | (boundState, None) ->
                                    let accumulatorBound =
                                        match appliedAccumulatorType(resolveType(boundState)(declared))(argumentCount)(None) with
                                            | Some(parameterType) ->
                                                match bindType(parameterType)(accumulatorType)(boundState) with
                                                    | (pinnedState, None) -> pinnedState
                                                    | (_failedState, Some(_error)) -> boundState
                                            | None -> boundState
                                    in PreparedCoreRecursiveGroup(state = accumulatorBound, members = [member], error = None)
                                | (_failedState, Some(_error)) -> prepared
        | _ -> prepared

// Stage 0's `SpecializationHandsResultOverCounted` gate on the callee's type: only a function whose
// parameters and result are concrete, none of them a function, has its specialization checked; a
// generic callee keeps routing to its specialization.
let recursive specializationTypeIsConcrete (semanticType: SemanticType) =
    match semanticType with
        | SemVariable(_id) -> false
        | SemFunction(_argument, _result, _row) -> false
        | SemList(element) -> specializationTypeIsConcrete(element)
        | SemTuple(elements) -> specializationTypesAreConcrete(elements)
        | SemNamed(_symbolId, _typeName, arguments) -> specializationTypesAreConcrete(arguments)
        | _ -> true
and specializationTypesAreConcrete (types: List(SemanticType)) =
    match types with
        | [] -> true
        | head :: rest ->
            if specializationTypeIsConcrete(head)
            then specializationTypesAreConcrete(rest)
            else false

let recursive specializationSignatureIsConcrete (argumentCount: Int) (functionType: SemanticType) =
    if argumentCount <= 0
    then specializationTypeIsConcrete(functionType)
    else
        match functionType with
            | SemFunction(argument, result, _row) ->
                if specializationTypeIsConcrete(argument)
                then specializationSignatureIsConcrete(argumentCount - 1)(result)
                else false
            | _ -> false

// Stage 0's `SpecializationHandsResultOverCounted`: a specialization whose generated body hands its
// result over reference-counted keeps nothing it rebuilt in place, and the fresh cells its arms built
// went into the never-reclaimed to-space.
let specializationHandsResultOverCounted (label: Str) (argumentCount: Int) (state: CoreLoweringState) =
    match state
    |> stateFunctionReturnedClosureLabels
    |> innermostStageLabel(argumentCount - 1)(label) with
        | Some(stage) -> bodyReturnsRuntimeManaged(stage)(state)
        | None -> false

let restoreSpecializingInProgress (outer: CoreLoweringState) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state } ->
            lowered with state = withStateSpecializingInProgress(stateSpecializingInProgress(outer))(state)

// The call to a callee whose specialization hands its result over counted takes the ordinary call
// path: the callee is marked in progress so the call does not route to the specialization again.
let lowerCountedSpecializationCandidateCall (callee: Str) (arguments: List(Expr)) lower (state: CoreLoweringState) =
    state
    |> withStateSpecializingInProgress(callee :: stateSpecializingInProgress(state))
    |> lower(specializedCallExpression(ExprVar(callee))(arguments))
    |> restoreSpecializingInProgress(state)

// The continuation of a freshly generated specialization's group. When the generated body hands its
// result over counted, the call is lowered outside the group's binding of the callee, with the
// specialization scope and the routed accumulators taken back; the callee is still in progress
// there, so the call takes the ordinary path.
let continueReuseSpecializedCall (label: Str) (argumentCount: Int) (concrete: Bool) outerBindings (unrouted: CoreLoweringState) lower (call: Expr) (state: CoreLoweringState) =
    if concrete && specializationHandsResultOverCounted(label)(argumentCount)(state)
    then
        state
        |> restoreBindings(outerBindings)
        |> withStateSpecializationFreshInputs(stateSpecializationFreshInputs(unrouted))
        |> withStateSpecializingLinearParam(stateSpecializingLinearParam(unrouted))
        |> withStateSpecializingReuseLabel(stateSpecializingReuseLabel(unrouted))
        |> withStateRoutedSpecializationAccumulators(stateRoutedSpecializationAccumulators(unrouted))
        |> lower(call)
    else lower(call)(state)

let generateReuseSpecializedCall (callee: Str) (parameter: Str) (parameters: List(Str)) (value: Expr) (accumulatorType: SemanticType) (cacheKey: Str) (arguments: List(Expr)) (concrete: Bool) (unrouted: CoreLoweringState) lower (state: CoreLoweringState) =
    (let label =
        state
        |> stateReuseSpecializations
        |> length
        |> reuseSpecializationLabel(callee)
    in
        match state with
            | CoreLoweringState { bindings = outerBindings, currentSpan = declarationSpan } ->
                []
                |> prepareRecursiveGroup([(callee, value)])(state
                |> withStateRecursiveDeclarationSpan(declarationSpan)
                |> withStateSpecializingLinearParam(Some(parameter))
                |> withStateSpecializingReuseLabel(None)
                |> withStateSpecializingInProgress(callee :: stateSpecializingInProgress(state))
                |> withStateSpecializationFreshInputs(Some(parameters))
                |> withStateReuseSpecializations((cacheKey, label) :: stateReuseSpecializations(state)))
                |> relabelReuseSpecialization(label)
                |> pinSpecializationType(callee)(accumulatorType)(coreListLength(arguments))
                |> lowerPreparedRecursiveGroupWith([(callee, value)])(specializedCallExpression(ExprVar(callee))(arguments))(lower)(continueReuseSpecializedCall(label)(coreListLength(arguments))(concrete)(outerBindings)(unrouted)(lower))(outerBindings)
                |> recordFullyReusingSpecialization(callee)(accumulatorType)(state)
                |> restoreSpecializationScope(state))

// The specialization is generated once per concrete instantiation, stage 0's cache: a later call
// at the same type takes a closure over the label already emitted instead of lowering the
// candidate's body again.
// Stage 0's `RecordRoutedSpecializationCall`: the accumulator a routed call names is marked so
// its entry copy is kept; a scanned accumulator no call was routed for takes no copy.
let recursive recordRoutedAccumulator (arguments: List(Expr)) (state: CoreLoweringState) =
    match arguments with
        | [] -> state
        | accumulator :: [] ->
            match unspanArgument(accumulator) with
                | ExprVar(name) -> withStateRoutedSpecializationAccumulators(name :: stateRoutedSpecializationAccumulators(state))(state)
                | _ -> state
        | _ :: rest -> recordRoutedAccumulator(rest)(state)

let lowerReuseSpecializedCall (callee: Str) (parameter: Str) (parameters: List(Str)) (value: Expr) (accumulatorType: SemanticType) (functionType: SemanticType) (arguments: List(Expr)) lower (state: CoreLoweringState) =
    (let cacheKey = reuseSpecializationCacheKey(callee)(functionType)(accumulatorType)
    in
        let concrete =
            functionType
            |> resolveType(state)
            |> specializationSignatureIsConcrete(coreListLength(arguments))
        in
            let routed = recordRoutedAccumulator(arguments)(state)
            in
                match routed
                |> stateReuseSpecializations
                |> lookupReuseSpecialization(cacheKey) with
                    | Some(label) ->
                        if concrete && specializationHandsResultOverCounted(label)(coreListLength(arguments))(state)
                        then lowerCountedSpecializationCandidateCall(callee)(arguments)(lower)(state)
                        else lowerCachedReuseSpecializedCall(callee)(label)(functionType)(arguments)(lower)(routed)
                    | None -> generateReuseSpecializedCall(callee)(parameter)(parameters)(value)(accumulatorType)(cacheKey)(arguments)(concrete)(state)(lower)(routed))

// Logical binding schemes are inferred independently of their dictionary-prefixed
// runtime closures. Resolve the visible environment before the inference query;
// returned substitutions are merged into Core rather than discarded. A closed scheme
// is its own resolution, so only the open ones are read through the substitution.
let recursive constrainedLocalBindings (bindings: List(CoreBinding)) (tail: List((Str, TypeScheme))) (state: CoreLoweringState) =
    match bindings with
        | [] -> tail
        | CoreBinding { name = name, scheme = scheme, closedScheme = closed } :: rest ->
            if closed
            then (name, scheme) :: constrainedLocalBindings(rest)(tail)(state)
            else (name, applySchemeSubstitutionMap(state.substitution)(scheme)) :: constrainedLocalBindings(rest)(tail)(state)

let recursive openLocalSchemes (bindings: List(CoreBinding)) (tail: List(TypeScheme)) (state: CoreLoweringState) =
    match bindings with
        | [] -> tail
        | CoreBinding { scheme = scheme, closedScheme = closed } :: rest ->
            if closed
            then openLocalSchemes(rest)(tail)(state)
            else applySchemeSubstitutionMap(state.substitution)(scheme) :: openLocalSchemes(rest)(tail)(state)

// Closed schemes can be reused unchanged. Only schemes with free IDs need the
// current substitution; the flag is computed when the global reference is stored.
let recursive appendConstrainedGlobalBindings bindings tail (state: CoreLoweringState) =
    match bindings with
        | [] -> tail
        | (name, scheme, hasFreeVariables) :: rest ->
            let current =
                if hasFreeVariables
                then applySchemeSubstitutionMap(state.substitution)(scheme)
                else scheme
            in (name, current) :: appendConstrainedGlobalBindings(rest)(tail)(state)

let recursive openSchemesOf (bindings: List((Str, TypeScheme))) =
    match bindings with
        | [] -> []
        | (_name, scheme) :: rest ->
            match freeSchemeVariables(scheme) with
                | [] -> openSchemesOf(rest)
                | _variables -> scheme :: openSchemesOf(rest)

// Registration is complete before any item is lowered, so the coverage bindings that can hold a
// free variable are listed once. The version ties the list to the environment it describes.
let recordCoverageOpenSchemes (state: CoreLoweringState) =
    (let group = state.programState
    in
        let coverage = group.coverageTypes
        in state with programState = (group with coverageOpenSchemes = Some((group.coverageEnvironmentVersion, openSchemesOf(coverage.bindings)))))

let recursive openGlobalSchemes (open: List((Str, TypeScheme))) (tail: List(TypeScheme)) (state: CoreLoweringState) =
    match open with
        | [] -> tail
        | (_name, scheme) :: rest -> applySchemeSubstitutionMap(state.substitution)(scheme) :: openGlobalSchemes(rest)(tail)(state)

let recursive openGlobalBindings (open: List((Str, TypeScheme))) (tail: List((Str, TypeScheme))) (state: CoreLoweringState) =
    match open with
        | [] -> tail
        | (name, scheme) :: rest -> (name, applySchemeSubstitutionMap(state.substitution)(scheme)) :: openGlobalBindings(rest)(tail)(state)

// Every scheme that can hold a free variable: the open local bindings, the global references
// stored with free IDs, and the open coverage bindings. All other bindings are closed.
let constrainedGeneralizationSchemes (state: CoreLoweringState) =
    (let program = state.programState
    in
        match program.coverageOpenSchemes with
            | Some((version, open)) ->
                if version == program.coverageEnvironmentVersion
                then
                    state
                    |> openLocalSchemes(state.bindings)(openGlobalSchemes(program.topLevelInferenceOpen)(open)(state))
                    |> Some
                else None
            | None -> None)

// The global references and the coverage bindings a lookup falls through to after the local
// ones. The stored list is shared as it is when it describes the current coverage environment:
// each open reference is resolved in front of it, where a lookup by name finds it before its
// stored copy, which is the same answer as resolving it in place while no later reference took
// its name. Otherwise every reference is resolved in place.
let constrainedGlobalBindings (generalizes: Bool) (state: CoreLoweringState) =
    (let program = state.programState
    in
        let coverage = program.coverageTypes
        in
            match program.topLevelInferenceEnvironment with
                | Some((version, stored)) ->
                    if generalizes && version == program.coverageEnvironmentVersion && program.topLevelInferenceOpenShadowed == false
                    then openGlobalBindings(program.topLevelInferenceOpen)(stored)(state)
                    else appendConstrainedGlobalBindings(program.topLevelInferenceBindings)(coverage.bindings)(state)
                | None -> appendConstrainedGlobalBindings(program.topLevelInferenceBindings)(coverage.bindings)(state))

let constrainedInferenceEnvironment (state: CoreLoweringState) =
    (let coverage = coverageEnvironment(state)
    in
        let traits = stateTraitEnvironment(state)
        in
            let schemes = constrainedGeneralizationSchemes(state)
            in
                let generalizes =
                    match schemes with
                        | Some(_open) -> true
                        | None -> false
                in
                    coverage with generalizationSchemes = schemes, bindings = constrainedLocalBindings(state.bindings)(constrainedGlobalBindings(generalizes)(state))(state), traits = traits.traits, traitImplementations = traits.traitImplementations, capabilities = traits.capabilities, providers = traits.providers)

let recursive mergeConstrainedSubstitution substitutions state =
    match substitutions with
        | [] -> (state, None)
        | (variable, semanticType) :: rest ->
            match bindType(SemVariable(variable))(semanticType)(state) with
                | (next, None) -> mergeConstrainedSubstitution(rest)(next)
                | failed -> failed

let recursive replaceConstrainedScheme name scheme bindings =
    match bindings with
        | [] -> []
        | (CoreBinding { name = candidate } as binding) :: rest ->
            if candidate == name
            then (binding with scheme = scheme, closedScheme = schemeIsClosed(scheme)) :: rest
            else binding :: replaceConstrainedScheme(name)(scheme)(rest)

let constrainedInferenceFailure error state =
    match error with
        | InferenceUnificationError(cause) ->
            state
            |> mismatchSiteOf
            |> CoreCallTypeMismatch(cause)
            |> failure(state)
        | _ ->
            SemNever
            |> UnsupportedCoreTraitDispatch("constrained call: " + Ashes.Trait.Show.show(error))
            |> failure(state)

type ConstrainedTemporaryClose =
    | typeName: Maybe(Str)
    | slot: Int
    | cursor: Int
    | end: Int

let storeConstrainedTemporary prefix semanticType bracket lowered closes =
    match (bracket, lowered) with
        | (ArenaBracket { bracketState = saved, bracketCursorSlot = cursor, bracketEndSlot = end }, LoweredCoreValue { state = state, temp = temp, error = None }) ->
            match freshLocal(state) with
                | FreshLocal { local = slot } as fresh ->
                    let name = prefix + Ashes.Text.fromInt(slot)
                    in
                        let typedValue = lowered with semanticType = semanticType
                        in
                            let stored = storeLetValue(name)(false)(emptyConsumerRequest)(saved.bindings)(temp)(semanticType)(fresh)
                            in (stored, ExprVar(name), ConstrainedTemporaryClose(typeName = loweredValueOwnedTypeName(typedValue), slot = slot, cursor = cursor, end = end) :: closes, None)
        | (_, LoweredCoreValue { state = failed, error = Some(error) }) -> (failed, ExprBool(false), closes, Some(error))

let recursive closeConstrainedTemporaries (closes: List(ConstrainedTemporaryClose)) lowered =
    match (closes, lowered) with
        | ([], _) -> lowered
        | (_, LoweredCoreValue { error = Some(_error) }) -> lowered
        | (close :: rest, LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None }) ->
            match closeOwnedLetBracket(close.typeName)(close.slot)(close.cursor)(close.end)(temp)(semanticType)(state) with
                | (closed, resultTemp) ->
                    closed
                    |> finishClosedLetResult(resultTemp)(semanticType)
                    |> closeConstrainedTemporaries(rest)

// The type a missing-evidence report names: a single-parameter trait's argument itself.
let traitEvidenceSubject (typeArguments: List(SemanticType)) =
    match typeArguments with
        | only :: [] -> only
        | _ -> SemTuple(typeArguments)

let recursive firstInactiveTraitMethod (traitName: Str) (operand: SemanticType) (methods: List(Str)) (state: CoreLoweringState) =
    match methods with
        | [] -> None
        | name :: rest ->
            match state
            |> stateActiveTraitMethods
            |> activeTraitMethod(traitName)(operand)(name)(state) with
                | Some(_binding) -> firstInactiveTraitMethod(traitName)(operand)(rest)(state)
                | None -> Some(name)

// The requirement a dictionary cannot be built for, decided before any supertrait dictionary is
// lowered so the report names the requirement the source asked for and not a trait it implies.
let unansweredDictionaryRequirement (constraint: TraitConstraint) (methods: List(Str)) (state: CoreLoweringState) =
    match state
    |> stateTraitEnvironment
    |> resolveTraitEvidence(constraint) with
        | TraitEvidenceResolution { plan = Some(TraitEvidenceParameter(TraitConstraint { traitName = traitName, typeArguments = operand :: [] })), error = None } ->
            match firstInactiveTraitMethod(traitName)(operand)(methods)(state) with
                | Some(_name) ->
                    operand
                    |> MissingCoreTraitEvidence(traitName)
                    |> Some
                | None -> None
        | TraitEvidenceResolution { plan = Some(_plan), error = None } -> None
        | _ ->
            constraint.typeArguments
            |> traitEvidenceSubject
            |> MissingCoreTraitEvidence(constraint.traitName)
            |> Some

// New dictionary closures/tuples are ordinary lexical owners. The close records
// mirror nested lets and run after normal call-result normalization. Forwarded
// parameter evidence is already owned by its surrounding lexical binding.
let recursive lowerConstrainedDictionary shape lower state closes =
    match shape with
        | TraitDictionaryAbiShape { constraint = constraint, methods = methods, supertraits = supertraits } ->
            match unansweredDictionaryRequirement(constraint)(methods)(state) with
                | Some(error) -> (state, ExprBool(false), closes, Some(error))
                | None ->
                    match lowerConstrainedDictionaries(supertraits)(lower)(state)([])(closes) with
                        | (superState, superExpressions, superCloses, None) ->
                            match superState
                            |> stateTraitEnvironment
                            |> resolveTraitEvidence(constraint) with
                                | TraitEvidenceResolution { plan = Some(plan), error = None } ->
                                    match lowerConstrainedDictionaryMethods(constraint)(plan)(methods)(lower)(superState)([])(superCloses) with
                                        | (methodState, methodExpressions, methodCloses, None) ->
                                            let fields = append(methodExpressions)(superExpressions)
                                            in
                                                match fields with
                                                    | only :: [] -> (methodState, only, methodCloses, None)
                                                    | _ ->
                                                        match openArenaBracket(methodState) with
                                                            | ArenaBracket { bracketState = opened } as bracket ->
                                                                match lower(ExprTuple(fields))(opened) with
                                                                    | LoweredCoreValue { semanticType = semanticType } as tuple -> storeConstrainedTemporary("__trait_argument_")(semanticType)(bracket)(tuple)(methodCloses)
                                        | (failed, _values, nextCloses, Some(error)) -> (failed, ExprBool(false), nextCloses, Some(error))
                                | _ ->
                                    (superState, ExprBool(false), superCloses, constraint.typeArguments
                                    |> traitEvidenceSubject
                                    |> MissingCoreTraitEvidence(constraint.traitName)
                                    |> Some)
                        | (failed, _values, nextCloses, Some(error)) -> (failed, ExprBool(false), nextCloses, Some(error))
and lowerConstrainedDictionaries shapes lower state reversed closes =
    match shapes with
        | [] -> (state, reverse(reversed), closes, None)
        | shape :: rest ->
            match lowerConstrainedDictionary(shape)(lower)(state)(closes) with
                | (next, expression, nextCloses, None) -> lowerConstrainedDictionaries(rest)(lower)(next)(expression :: reversed)(nextCloses)
                | (failed, _value, nextCloses, Some(error)) -> (failed, [], nextCloses, Some(error))
and lowerConstrainedDictionaryMethods (constraint: TraitConstraint) plan names lower state reversed closes =
    match names with
        | [] -> (state, reverse(reversed), closes, None)
        | name :: rest ->
            match plan with
                | TraitEvidenceParameter(TraitConstraint { traitName = traitName, typeArguments = operand :: [] }) ->
                    match state
                    |> stateActiveTraitMethods
                    |> activeTraitMethod(traitName)(operand)(name)(state) with
                        | Some(binding) -> lowerConstrainedDictionaryMethods(constraint)(plan)(rest)(lower)(state)(ExprVar(binding) :: reversed)(closes)
                        | None ->
                            (state, [], closes, operand
                            |> MissingCoreTraitEvidence(traitName)
                            |> Some)
                | _ ->
                    match state
                    |> stateTraitEnvironment
                    |> dictionaryMethodType(constraint.traitName)(name)(constraint.typeArguments) with
                        | None ->
                            (state, [], closes, SemTuple(constraint.typeArguments)
                            |> UnsupportedCoreTraitDispatch(constraint.traitName + "." + name)
                            |> Some)
                        | Some(methodType) ->
                            match openArenaBracket(state) with
                                | ArenaBracket { bracketState = opened } as bracket ->
                                    match storeConstrainedTemporary("__trait_method_")(methodType)(bracket)(buildTraitMethodClosure(plan)(name)(lower)(opened))(closes) with
                                        | (next, expression, nextCloses, None) -> lowerConstrainedDictionaryMethods(constraint)(plan)(rest)(lower)(next)(expression :: reversed)(nextCloses)
                                        | (failed, _value, nextCloses, Some(error)) -> (failed, [], nextCloses, Some(error))

let recursive appendConstrainedArguments expression arguments =
    match arguments with
        | [] -> expression
        | argument :: rest ->
            appendConstrainedArguments(ExprCall(expression)(argument)(false)(callArgumentsInline))(rest)

let restoreConstrainedCall (original: CoreLoweringState) lowered =
    match lowered with
        | LoweredCoreValue { state = state } -> lowered with state = (state with bindings = original.bindings)

// Whether a plan's evidence, or any evidence it is built from, is a hidden parameter the body
// being lowered does not carry: the dictionary cannot be built here whatever the plan says.
let recursive constrainedPlanLacksEvidence (plan: TraitEvidencePlan) (state: CoreLoweringState) =
    match plan with
        | TraitEvidenceParameter(TraitConstraint { traitName = traitName, typeArguments = argument :: [] }) -> !hasActiveTraitEvidence(traitName)(argument)(state)
        | TraitEvidenceParameter(_constraint) -> false
        | TraitEvidenceInstance(_constraint, _implementation, requirements, supertraits) -> constrainedPlansLackEvidence(requirements)(state) || constrainedPlansLackEvidence(supertraits)(state)
and constrainedPlansLackEvidence (plans: List(TraitEvidencePlan)) (state: CoreLoweringState) =
    match plans with
        | [] -> false
        | plan :: rest -> constrainedPlanLacksEvidence(plan)(state) || constrainedPlansLackEvidence(rest)(state)

// A constrained call's dictionary sites are named by their slot: one call asks for one dictionary
// per requirement, all under the call's own span.
let constrainedGoalMethod (index: Int) = "dictionary" + Ashes.Text.fromInt(index)

// The call's requirements with an earlier pass's proof pinned at each one whose type argument is
// still open.
let recursive pinConstrainedGoals (index: Int) (goals: List(TraitConstraint)) (state: CoreLoweringState) =
    match goals with
        | [] -> state
        | TraitConstraint { traitName = traitName, typeArguments = argument :: [] } :: rest ->
            match pinProvenTraitGoal(traitName)(constrainedGoalMethod(index))(resolveType(state)(argument))(state) with
                | (pinnedState, _pinnedType) -> pinConstrainedGoals(index + 1)(rest)(pinnedState)
        | _goal :: rest -> pinConstrainedGoals(index + 1)(rest)(state)

// The site a dictionary still waiting on a type is recorded under. Unlike an operator's goal, it is
// recorded in a pass lowering with proofs pinned too: the body that settles the type is lowered
// again from its entry state, where the call builds the real dictionary and the record made by
// the abandoned lowering is gone. A record that outlives its pass is a goal no pass answered.
let openDictionaryGoalKey (traitName: Str) (methodName: Str) (operandType: SemanticType) (state: CoreLoweringState) =
    match freeTypeVariables(operandType) with
        | [] -> None
        | _variables -> traitGoalKey(traitName)(methodName)(state)

// The requirements a call cannot build a dictionary for yet: the type argument is still open (the
// caller's own parameter, settled by a later use in its body) and no active evidence answers it.
// Each is a trait goal a later pass answers once this one has resolved the type.
let recursive openConstrainedGoals (index: Int) (goals: List(TraitConstraint)) (state: CoreLoweringState) =
    match goals with
        | [] -> []
        | (TraitConstraint { traitName = traitName, typeArguments = argument :: [] } as goal) :: rest ->
            let resolved = resolveType(state)(argument)
            in
                match (openDictionaryGoalKey(traitName)(constrainedGoalMethod(index))(resolved)(state), state |> stateTraitEnvironment |> resolveTraitEvidence((goal with typeArguments = [resolved]))) with
                    | (Some(key), TraitEvidenceResolution { plan = Some(plan), error = None }) ->
                        if constrainedPlanLacksEvidence(plan)(state)
                        then (key, resolved) :: openConstrainedGoals(index + 1)(rest)(state)
                        else openConstrainedGoals(index + 1)(rest)(state)
                    | _ -> openConstrainedGoals(index + 1)(rest)(state)
        | _goal :: rest -> openConstrainedGoals(index + 1)(rest)(state)

// The value a call lowers to while any of its dictionaries waits on a type: every open requirement
// joins the pass's record and the call stands as a constant of its own result type.
let recursive deferConstrainedGoals (goals: List((Str, SemanticType))) (resultType: SemanticType) (state: CoreLoweringState) =
    match goals with
        | [] ->
            lowerConstant(given (target) -> LoadConstInt(target)(0))(resultType)(state)
        | (key, goalType) :: [] -> deferTraitGoal(key)(goalType)(resultType)(state)
        | (key, goalType) :: rest ->
            state
            |> withStatePendingTraitGoals((key, goalType) :: statePendingTraitGoals(state))
            |> deferConstrainedGoals(rest)(resultType)

// A requirement whose type argument is still open takes the type the standalone inference recorded
// for the same trait's dictionary at the reference, the first unused one of that trait in the
// scheme's order, the way a later pass pins a proven goal.
let recursive pinRecordedDictionaries (constraints: List(TraitConstraint)) (recorded: List((Str, SemanticType))) (state: CoreLoweringState) =
    match constraints with
        | [] -> state
        | TraitConstraint { traitName = traitName, typeArguments = argument :: [] } :: rest ->
            match argument
            |> resolveType(state)
            |> freeTypeVariables with
                | [] -> pinRecordedDictionaries(rest)(recorded)(state)
                | _open ->
                    match takeRecordedDictionary(traitName)(recorded)([]) with
                        | (Some(recordedType), remaining) ->
                            match bindType(argument)(recordedType)(state) with
                                | (bound, None) ->
                                    bound
                                    |> observeLoweringEvent(0)
                                    |> pinRecordedDictionaries(rest)(remaining)
                                | (_failed, Some(_error)) -> pinRecordedDictionaries(rest)(remaining)(state)
                        | (None, _remaining) -> pinRecordedDictionaries(rest)(recorded)(state)
        | _constraint :: rest -> pinRecordedDictionaries(rest)(recorded)(state)
and takeRecordedDictionary (traitName: Str) (recorded: List((Str, SemanticType))) (skipped: List((Str, SemanticType))) =
    match recorded with
        | [] -> (None, reverse(skipped))
        | (name, recordedType) :: rest ->
            if name == traitName
            then
                (Some(recordedType), appendRecorded(reverse(skipped))(rest))
            else takeRecordedDictionary(traitName)(rest)((name, recordedType) :: skipped)
and appendRecorded (left: List((Str, SemanticType))) (right: List((Str, SemanticType))) =
    match left with
        | [] -> right
        | head :: rest -> head :: appendRecorded(rest)(right)

let lowerConstrainedCallWithName expression name privateName (binding: CoreBinding) lower (state: CoreLoweringState) =
    match instantiatePreservingConstraintOrder(binding.scheme)(state.typeSupply) with
        | InstantiationResult { semanticType = sourceType, constraints = constraints, supply = supply } ->
            let primed =
                state
                |> withTypeSupply(supply)
                |> restoreBindings((binding with name = privateName, scheme = emptyScheme(sourceType), closedScheme = false) :: state.bindings)
            in
                let inferenceSpine = collectCallSpine(expression)
                in
                    match inferExpressionFrom(appendConstrainedArguments(ExprVar(privateName))(inferenceSpine.arguments))(constrainedInferenceEnvironment(primed))([])(supply) with
                        | TypeInferenceResult { error = Some(error) } -> constrainedInferenceFailure(error)(state)
                        | TypeInferenceResult { semanticType = resultType, substitution = substitutions, supply = nextSupply, error = None } ->
                            match primed
                            |> withTypeSupply(nextSupply)
                            |> mergeConstrainedSubstitution(substitutions) with
                                | (failed, Some(error)) -> failure(failed)(error)
                                | (inferred, None) ->
                                    match expectedTypeOf(state) with
                                        | expected ->
                                            match match expected with
                                                | None -> (inferred, None)
                                                | Some(expectedResult) -> bindType(resultType)(expectedResult)(inferred) with
                                                | (failed, Some(error)) -> failure(failed)(error)
                                                | (bound, None) ->
                                                    match bound
                                                    |> pinConstrainedGoals(0)(constraints)
                                                    |> pinRecordedDictionaries(constraints)(recordedDictionariesAt(nodeIdentityOf(inferenceSpine.root))(bound)) with
                                                        | settled ->
                                                            let goals =
                                                                map((given (constraint: TraitConstraint) ->
                                                                    constraint with typeArguments = map(resolveType(settled))(constraint.typeArguments)))(constraints)
                                                            in
                                                                match openConstrainedGoals(0)(goals)(settled) with
                                                                    | _first :: _rest as open ->
                                                                        settled
                                                                        |> deferConstrainedGoals(open)(resolveType(settled)(resultType))
                                                                        |> restoreConstrainedCall(state)
                                                                    | [] ->
                                                                        match settled
                                                                        |> stateTraitEnvironment
                                                                        |> coreConstrainedLayout(goals) with
                                                                            | CoreConstrainedLayout { error = Some(error) } ->
                                                                                sourceType
                                                                                |> UnsupportedCoreTraitDispatch(error)
                                                                                |> failure(settled)
                                                                            | CoreConstrainedLayout { parameterTypes = dictionaryTypes, error = None } ->
                                                                                let physical =
                                                                                    sourceType
                                                                                    |> resolveType(settled)
                                                                                    |> coreConstrainedPhysicalType(dictionaryTypes)
                                                                                in
                                                                                    let physicalState =
                                                                                        settled
                                                                                        |> restoreBindings((binding with name = privateName, scheme = emptyScheme(physical), closedScheme = false) :: state.bindings)
                                                                                        |> clearConsumerRequest
                                                                                    in
                                                                                        match lowerConstrainedDictionaries(settled
                                                                                        |> stateTraitEnvironment
                                                                                        |> coreConstrainedShapes(goals))(lower)((physicalState with reuseTokens = []))([])([]) with
                                                                                            | (failed, _values, _closes, Some(error)) ->
                                                                                                error
                                                                                                |> failure(failed)
                                                                                                |> restoreConstrainedCall(state)
                                                                                            | (dictionaryState, dictionaries, closes, None) ->
                                                                                                let spine = collectCallSpine(expression)
                                                                                                in
                                                                                                    let expanded =
                                                                                                        appendConstrainedArguments(appendConstrainedArguments(ExprVar(privateName))(dictionaries))(spine.arguments)
                                                                                                    in
                                                                                                        (dictionaryState with reuseTokens = state.reuseTokens, consumerRequest = state.consumerRequest)
                                                                                                        |> lower(expanded)
                                                                                                        |> closeConstrainedTemporaries(closes)
                                                                                                        |> restoreConstrainedCall(state)

let lowerConstrainedCall expression name binding lower state =
    match freshLocal(state) with
        | FreshLocal { state = reserved, local = identity } ->
            let privateName = "__core_constrained_call_" + Ashes.Text.fromInt(identity)
            in lowerConstrainedCallWithName(expression)(name)(privateName)(binding)(lower)(reserved)

let tryConstrainedGlobalCall name expression lower state =
    match state
    |> stateTopLevelFunctionRefIndex
    |> lookupTopLevelFunctionRef(name) with
        | Some((_label, TypeScheme { constraints = [] })) -> None
        | None -> None
        | Some((label, scheme)) ->
            match state
            |> stateTraitEnvironment
            |> coreConstrainedLayout(scheme.constraints) with
                | CoreConstrainedLayout { error = Some(error) } ->
                    scheme.body
                    |> UnsupportedCoreTraitDispatch(error)
                    |> failure(state)
                    |> Some
                | CoreConstrainedLayout { parameterTypes = parameters, error = None } ->
                    match emitTraitMethodClosureFromLabel(label)(coreConstrainedPhysicalType(parameters)(scheme.body))(state) with
                        | LoweredCoreValue { state = closureState, temp = temp, error = None } ->
                            match freshLocal(closureState) with
                                | FreshLocal { state = allocated, local = slot } ->
                                    let binding = CoreBinding(name = name, scheme = scheme, location = CoreLocal(slot), ownedRead = false, patternOwner = None, closedScheme = false)
                                    in
                                        allocated
                                        |> emit(StoreLocal(slot)(temp))
                                        |> restoreBindings(binding :: state.bindings)
                                        |> lowerConstrainedCall(expression)(name)(binding)(lower)
                                        |> restoreLoweredBindings(state.bindings)
                                        |> Some
                        | failed -> Some(failed)

let lowerPossiblyConstrainedVariable name lower (state: CoreLoweringState) =
    match lookupBinding(name)(state.bindings) with
        | Some(binding) ->
            match bindingRequirements(binding) with
                | [] -> lowerCoreVariable(name)(lower)(state)
                | _ -> lowerConstrainedCall(ExprVar(name))(name)(binding)(lower)(state)
        | None ->
            match tryConstrainedGlobalCall(name)(ExprVar(name))(lower)(state) with
                | Some(lowered) -> lowered
                | None -> lowerCoreVariable(name)(lower)(state)

// Concrete copies share the source schemes used by dictionary call inference.
let concreteCopyInferenceBindings (registry: CoreConcreteRegistry) =
    Ashes.Collection.Map.foldLeft(given (bindings) ->
        given (_name) ->
            given (candidate: CoreConcreteCandidate) -> (candidate.name, candidate.scheme) :: bindings)([])(registry.candidates)

// A candidate's scheme answers a name no visible binding has. When the environment lists its open
// schemes, the registry's bindings are read as the environment's fallback and its open schemes
// follow the listed ones; otherwise the candidates are appended to the bindings themselves, which
// is what a generalization then scans.
let concreteCopyInferenceEnvironment state =
    (let environment = constrainedInferenceEnvironment(state)
    in
        let registry = stateConcreteRegistry(state)
        in
            match environment.generalizationSchemes with
                | Some(schemes) ->
                    match coreConcreteOpenSchemes(registry) with
                        | [] -> environment with fallbackBindings = registry.inferenceBindings
                        | open ->
                            environment with fallbackBindings = registry.inferenceBindings, generalizationSchemes = Some(append(schemes)(open))
                | None ->
                    environment with bindings = append(environment.bindings)(concreteCopyInferenceBindings(registry)))

// Stage 0's `NamesConstrainedCandidate`: the root names the registered top-level function, not a
// local binding of the same name. A function body reads the top-level function through a capture,
// which carries the candidate's own generalized scheme, quantified variable identities included.
let concreteCandidateBindingMatches (candidate: CoreConcreteCandidate) (state: CoreLoweringState) =
    match lookupBinding(candidate.name)(state.bindings) with
        | Some(CoreBinding { location = CoreLocal(slot) }) -> inProgramEntryFrame(state) && slot == candidate.bindingSlot
        | Some(CoreBinding { location = CoreEnvironment(_index), scheme = scheme }) -> scheme.quantified != [] && scheme == candidate.scheme
        | Some(_binding) -> false
        | None -> stateConcreteDepth(state) > 0

let concreteCopyDeclined state =
    if stateConcreteDepth(state) > 0
    then withStateConcreteDeclines(stateConcreteDeclines(state) + 1)(state)
    else state

// The original scheme is instantiated privately; speculative inference cannot
// change caller state until a fully concrete source copy has been accepted.
let inferConcreteCopyType (candidate: CoreConcreteCandidate) arguments (state: CoreLoweringState) =
    match preparePureConcreteScheme(candidate.scheme)(candidate.arity) with
        | None -> None
        | Some(pureScheme) ->
            match instantiate(pureScheme)(state.typeSupply) with
                | InstantiationResult { semanticType = functionType, supply = supply } ->
                    let name = "__core_concrete_inference_" + Ashes.Text.fromInt(supply.nextId)
                    in
                        let environment = concreteCopyInferenceEnvironment(state)
                        in
                            let bound =
                                addTypeBinding(name)(emptyScheme(functionType))(environment)
                            in
                                match inferExpressionFrom(appendConstrainedArguments(ExprVar(name))(arguments))(bound)([])(supply) with
                                    | TypeInferenceResult { substitution = substitution, error = None } ->
                                        let resolved = applySubstitution(substitution)(functionType)
                                        in
                                            match concreteConstrainedArguments(candidate.arity)(resolved) with
                                                | None -> None
                                                | Some(parameters) ->
                                                    match pinProvenConcreteConstrainedFunction(candidate.scheme)(parameters)(resolved) with
                                                        | Some(pinned) ->
                                                            match concreteConstrainedArguments(candidate.arity)(pinned) with
                                                                | Some(_parameters) -> Some((parameters, pinned))
                                                                | None -> None
                                                        | None -> None
                                    | _ -> None

let recursive concreteRootReadsEnvironment instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = LoadEnv(_target, _index) } :: _rest -> true
        | _other :: rest -> concreteRootReadsEnvironment(rest)

// This is the source-copy equivalent of S0's surviving self-reference rejection.
// A nested closure that still materializes the original recursive function would
// take the non-TCO path; do not replace its generic implementation with that copy.
let recursive concreteCopyReferencesSelf label instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = MakeClosure(_target, targetLabel, _environment, _size, _stack, _managed, _accepts, _returnsOwned) } :: rest -> targetLabel == label || concreteCopyReferencesSelf(label)(rest)
        | IrInstruction { instruction = MakeClosureStack(_target, targetLabel, _environment, _size, _managed, _accepts, _returnsOwned) } :: rest -> targetLabel == label || concreteCopyReferencesSelf(label)(rest)
        | IrInstruction { instruction = CallKnown(_target, targetLabel, _environment, _argument, _flag, _requestsArena) } :: rest -> targetLabel == label || concreteCopyReferencesSelf(label)(rest)
        | IrInstruction { instruction = LoadFuncAddr(_target, targetLabel) } :: rest -> targetLabel == label || concreteCopyReferencesSelf(label)(rest)
        | _other :: rest -> concreteCopyReferencesSelf(label)(rest)

let concreteFunctionsBoundary functions =
    match functions with
        | [] -> None
        | IrFunction { label = label } :: _rest -> Some(label)

let recursive concreteNewFunctionsReferenceSelf label boundary functions =
    match functions with
        | [] -> false
        | IrFunction { label = candidate, instructions = instructions } :: rest ->
            if boundary == Some(candidate)
            then false
            else concreteCopyReferencesSelf(label)(instructions) || concreteNewFunctionsReferenceSelf(label)(boundary)(rest)

let concreteCopyAdmissible label original generated =
    stateConcreteDeclines(generated) == stateConcreteDeclines(original) && (match functionWithLabel(label)(generated.functions) with
        | None -> false
        | Some(IrFunction { instructions = instructions }) -> !concreteRootReadsEnvironment(instructions)) && !concreteNewFunctionsReferenceSelf(label)(concreteFunctionsBoundary(original.functions))(generated.functions)

let lowerConcreteCopyCall label functionType arguments lower state =
    match freshTemp(state) with
        | FreshTemp { state = environmentState, temp = environmentTemp } ->
            match environmentState
            |> emit(LoadConstInt(environmentTemp)(0))
            |> emitClosure(label)(environmentTemp)(0)(false) with
                | (closureState, closureTemp) ->
                    match freshLocal(closureState) with
                        | FreshLocal { state = slotState, local = slot } ->
                            let name = "__core_concrete_call_" + Ashes.Text.fromInt(slot) + "_" + Ashes.Text.fromInt(closureTemp)
                            in
                                slotState
                                |> emit(StoreLocal(slot)(closureTemp))
                                |> addBinding(name)(emptyScheme(functionType))(CoreLocal(slot))
                                |> lower(appendConstrainedArguments(ExprVar(name))(arguments))
                                |> restoreLoweredBindings(state.bindings)

let recordConcreteCopy (candidate: CoreConcreteCandidate) parameters label state =
    withStateConcreteRegistry(state
    |> stateConcreteRegistry
    |> recordCoreConcreteCopy(candidate.identity)(parameters)(label))(state)

let rejectedConcreteCopy candidate parameters state =
    (state
    |> concreteCopyDeclined
    |> recordConcreteCopy(candidate)(parameters)(None), None)

// The identity of the innermost numbered node around a call's root, the callee reference the
// inference recorded the call's instantiation at; `enclosing` when none is met below it, -1 for an
// unnumbered tree.
let recursive callRootNode (expression: Expr) (enclosing: Int) =
    match expression with
        | ExprAt(_span, inner) -> callRootNode(inner)(enclosing)
        | ExprNode(node, inner) -> callRootNode(inner)(node)
        | ExprCall(function, _argument, _whitespace, _layout) -> callRootNode(function)(-1)
        | _ -> enclosing

let generateConcreteCopy (candidate: CoreConcreteCandidate) (reference: Int) parameters functionType arguments lower (state: CoreLoweringState) =
    (let isolated =
        (state with bindings = [], reuseTokens = [])
        |> withStateActiveTraitMethods([])
        |> withStateSpecializingLinearParam(None)
        |> withStateSpecializationFreshInputs(None)
        |> withStateSpecializingReuseLabel(None)
        |> withStateConcreteDepth(stateConcreteDepth(state) + 1)
        |> withStateNodeInstantiation(recordedCopyInstantiation(reference)(nodeIdentityOf(candidate.source))(functionType)(state))
    in
        match prepareRecursiveGroup([(candidate.name, candidate.source)])(isolated)([]) with
            | PreparedCoreRecursiveGroup { state = preparedState, members = member :: [], error = None } as prepared ->
                match bindType(member.semanticType)(functionType)(preparedState) with
                    | (_failed, Some(_error)) -> rejectedConcreteCopy(candidate)(parameters)(state)
                    | (pinned, None) ->
                        match openPreparedRecursiveGroup([(candidate.name, candidate.source)])(lower)([])((prepared with state = pinned)) with
                            | LoweredCoreValue { state = generated, error = None } ->
                                if concreteCopyAdmissible(member.label)(state)(generated)
                                then
                                    let restored =
                                        generated
                                        |> restoreOuterFrame(state)
                                        |> recordConcreteCopy(candidate)(parameters)(Some(member.label))
                                    in
                                        (restored, restored
                                        |> lowerConcreteCopyCall(member.label)(functionType)(arguments)(lower)
                                        |> Some)
                                else rejectedConcreteCopy(candidate)(parameters)(state)
                            | _ -> rejectedConcreteCopy(candidate)(parameters)(state)
            | _ -> rejectedConcreteCopy(candidate)(parameters)(state))

let tryConcreteCandidateCall (candidate: CoreConcreteCandidate) (reference: Int) arguments lower state =
    if coreListLength(arguments) != candidate.arity || stateConcreteDepth(state) >= 8
    then (concreteCopyDeclined(state), None)
    else
        match inferConcreteCopyType(candidate)(arguments)(state) with
            | None -> (concreteCopyDeclined(state), None)
            | Some((parameters, functionType)) ->
                match state
                |> stateConcreteRegistry
                |> lookupCoreConcreteCopy(candidate.identity)(parameters) with
                    | Some(Some(label)) ->
                        (state, state
                        |> lowerConcreteCopyCall(label)(functionType)(arguments)(lower)
                        |> Some)
                    | Some(None) -> (concreteCopyDeclined(state), None)
                    | None -> generateConcreteCopy(candidate)(reference)(parameters)(functionType)(arguments)(lower)(state)

let tryConcreteSourceCall expression lower state =
    match collectCallSpine(expression) with
        | CoreCallSpine { root = root, arguments = arguments } ->
            match stripExprAt(root) with
                | ExprVar(name) ->
                    match state
                    |> stateConcreteRegistry
                    |> lookupCoreConcreteCandidate(name) with
                        | Some(candidate) ->
                            if concreteCandidateBindingMatches(candidate)(state)
                            then
                                tryConcreteCandidateCall(candidate)(callRootNode(expression)(-1))(arguments)(lower)(state)
                            else (state, None)
                        | None -> (state, None)
                | _ -> (state, None)

let tryConstrainedCall expression lower (state: CoreLoweringState) =
    match collectCallSpine(expression) with
        | CoreCallSpine { root = root } ->
            match stripExprAt(root) with
                | ExprVar(name) ->
                    match lookupBinding(name)(state.bindings) with
                        | Some(binding) ->
                            match bindingRequirements(binding) with
                                | [] -> None
                                | _constraints ->
                                    state
                                    |> lowerConstrainedCall(expression)(name)(binding)(lower)
                                    |> Some
                        | None -> tryConstrainedGlobalCall(name)(expression)(lower)(state)
                | _ -> None

let recursive withoutQuantified (variables: List(Int)) (quantified: List((Int, Str))) =
    match variables with
        | [] -> []
        | variable :: rest ->
            if quantifiedNamesVariable(variable)(quantified)
            then withoutQuantified(rest)(quantified)
            else variable :: withoutQuantified(rest)(quantified)
and quantifiedNamesVariable (variable: Int) (quantified: List((Int, Str))) =
    match quantified with
        | [] -> false
        | (id, _name) :: rest -> id == variable || quantifiedNamesVariable(variable)(rest)

// Whether a call's callee is a binding whose type is settled where the call is lowered: a
// top-level or let-bound function whose scheme holds no variable it does not quantify. A self or
// sibling call of a recursive group still being lowered, a parameter, and any other callee whose
// type the body is still working out keep their result open, as stage 0 sees it.
let calleeTypeSettled (expression: Expr) (state: CoreLoweringState) =
    match collectCallSpine(expression) with
        | CoreCallSpine { root = root } ->
            match unspanArgument(root) with
                | ExprVar(name) ->
                    match lookupBinding(name)(state.bindings) with
                        | Some(CoreBinding { scheme = TypeScheme { quantified = quantified, body = body } }) ->
                            withoutQuantified(body
                            |> resolveType(state)
                            |> freeTypeVariables)(quantified) == []
                        | None ->
                            lookupTopLevelFunctionRef(name)(stateTopLevelFunctionRefIndex(state)) != None
                | ExprQualifiedVar(_qualifier, _name) -> true
                | _ -> false

// The closed type the standalone inference recorded for a call node whose consumer expects no
// type of its result and whose callee's type is settled, so the callee's result is resolved before
// the arguments and before any arena request is decided.
let recordedCallExpectation (expression: Expr) (state: CoreLoweringState) =
    match expectedTypeOf(state) with
        | Some(_expected) -> None
        | None ->
            if calleeTypeSettled(expression)(state)
            then recordedClosedNodeType(state)
            else None

let lowerUnconstrainedGeneralCall expression function argument lower state =
    match (isTailSelfCall(collectCallSpine(expression))(state), stateTcoLoopFrame(state), stateTcoLoop(state)) with
        | (true, Some(frame), Some(loop)) ->
            state
            |> clearConsumerRequest
            |> lowerTailSelfCall(collectCallSpine(expression))(frame)(loop)(lower)
            |> unifyOptionalExpectedResult(expectedTypeOf(state))
            |> locateLoweredMismatch(argumentSiteOf(state))
        | _ ->
            match reuseSpecializedCallOf(collectCallSpine(expression))(state) with
                | Some((callee, parameter, parameters, value, accumulatorType, functionType, arguments)) -> lowerReuseSpecializedCall(callee)(parameter)(parameters)(value)(accumulatorType)(functionType)(arguments)(lower)(state)
                | None ->
                    match tryInlineHelperCall(collectCallSpine(expression))(lower)(state) with
                        | Some(inlined) -> inlined
                        | None ->
                            let lowerExpecting =
                                given (expected) ->
                                    given (from: CoreLoweringState) ->
                                        from
                                        |> clearConsumerRequest
                                        |> lowerCall(collectCallSpine(expression))(function)(argument)(expected)(argumentSiteOf(state))(isTailSelfCall(collectCallSpine(expression))(state))(tailCallPosition(state))(lower)
                                        |> markLoweredCallArgumentsMoved(collectCallSpine(expression))
                            in
                                match recordedCallExpectation(expression)(state) with
                                    | None ->
                                        lowerExpecting(expectedTypeOf(state))(state)
                                    | Some(recorded) ->
                                        // Lowering's own inference stays the authority: a call that
                                        // fails against the recorded type is lowered as before.
                                        match state
                                        |> observeLoweringEvent(3)
                                        |> lowerExpecting(Some(recorded)) with
                                            | LoweredCoreValue { error = None } as lowered -> lowered
                                            | _failed ->
                                                state
                                                |> observeLoweringEvent(4)
                                                |> lowerExpecting(None)

let lowerGeneralCall expression function argument lower state =
    match tryConcreteSourceCall(expression)(lower)(state) with
        | (_prepared, Some(lowered)) -> lowered
        | (prepared, None) ->
            match tryConstrainedCall(expression)(lower)(prepared) with
                | Some(lowered) -> lowered
                | None -> lowerUnconstrainedGeneralCall(expression)(function)(argument)(lower)(prepared)

// A call whose root qualifies an operation with a registered capability or a static provider is
// the implicit form of `perform`, and lowers as the operation call.
let isCapabilityOperationCall expression (state: CoreLoweringState) =
    match collectCallSpine(expression) with
        | CoreCallSpine { root = ExprQualifiedVar(capName, _opName), arguments = _argument :: _rest } ->
            match state
            |> stateCapabilityLayouts
            |> findCapabilityLayout(capName) with
                | Some(_layout) -> true
                | None ->
                    match state
                    |> stateStaticProviders
                    |> findStaticProvider(capName)([]) with
                        | Some(_provider) -> true
                        | None -> false
        | _other -> false

// A qualifier naming a trait of the shipped `Ashes.Trait` module, as stitching spells it.
let isShippedTraitQualifier (qualifier: Str) = Ashes.Text.startsWith(qualifier)("Ashes_Trait_")

// The binary methods of the shipped operator traits, each the operator stage 0 maps it to: a
// saturated `Ord.less(left)(right)` is `left < right`, `Add.add(left)(right)` is `left + right`.
let shippedTraitBinaryOperator (traitName: Str) (methodName: Str) =
    match (traitName, methodName) with
        | ("Ord", "less") -> Some(CoreLessOperator)
        | ("Ord", "lessOrEqual") -> Some(CoreLessOrEqualOperator)
        | ("Ord", "greater") -> Some(CoreGreaterOperator)
        | ("Ord", "greaterOrEqual") -> Some(CoreGreaterOrEqualOperator)
        | ("Add", "add") -> Some(CoreAddOperator)
        | ("Subtract", "subtract") -> Some(CoreSubtractOperator)
        | ("Multiply", "multiply") -> Some(CoreMultiplyOperator)
        | ("Divide", "divide") -> Some(CoreDivideOperator)
        | ("Remainder", "remainder") -> Some(CoreModuloOperator)
        | ("BitAnd", "bitAnd") -> Some(CoreBitwiseAndOperator)
        | ("BitOr", "bitOr") -> Some(CoreBitwiseOrOperator)
        | ("BitXor", "bitXor") -> Some(CoreBitwiseXorOperator)
        | ("ShiftLeft", "shiftLeft") -> Some(CoreShiftLeftOperator)
        | ("ShiftRight", "shiftRight") -> Some(CoreShiftRightOperator)
        | _ -> None

// A saturated `Eq.equal(left)(right)` or `Eq.notEqual(left)(right)`, the calls a derived
// equality makes per field, is the operator it stands for: the operands take the same
// primitive-or-dispatched comparison as `left == right`.
let traitOperatorCall (function: Expr) (argument: Expr) =
    match stripExprAt(function) with
        | ExprCall(inner, left, _sugar, _layout) ->
            match stripExprAt(inner) with
                | ExprQualifiedVar(qualifier, methodName) ->
                    match (standardTraitName(qualifier), methodName) with
                        | ("Eq", "equal") -> Some((CoreEqualOperator, left, argument))
                        | ("Eq", "notEqual") -> Some((CoreNotEqualOperator, left, argument))
                        | _ ->
                            if isShippedTraitQualifier(qualifier)
                            then
                                match shippedTraitBinaryOperator(standardTraitName(qualifier))(methodName) with
                                    | Some(operator) -> Some((operator, left, argument))
                                    | None -> None
                            else None
                | _ -> None
        | _ -> None

// The operands of a saturated `Ord.compare(left)(right)`, the call a derived comparison makes per
// field and the one `Ord`'s four comparison defaults are built on. A capability or provider named
// `Ord` keeps its operation call.
let traitCompareCall (expression: Expr) (function: Expr) (argument: Expr) (state: CoreLoweringState) =
    match stripExprAt(function) with
        | ExprCall(inner, left, _sugar, _layout) ->
            match stripExprAt(inner) with
                | ExprQualifiedVar(qualifier, "compare") ->
                    if standardTraitName(qualifier) == "Ord" && isCapabilityOperationCall(expression)(state) == false
                    then Some((left, argument))
                    else None
                | _ -> None
        | _ -> None

// The unary methods of the shipped operator traits, by the operator each stands for: `negate` is
// `0 - x`, `not` is `!x`, `bitwiseNot` is `~x`.
let shippedTraitUnaryOperator (function: Expr) =
    match stripExprAt(function) with
        | ExprQualifiedVar(qualifier, methodName) ->
            if isShippedTraitQualifier(qualifier)
            then
                match (standardTraitName(qualifier), methodName) with
                    | ("Negate", "negate") -> Some("negate")
                    | ("Not", "not") -> Some("not")
                    | ("BitwiseNot", "bitwiseNot") -> Some("bitwiseNot")
                    | ("Default", "default") -> Some("default")
                    | _ -> None
            else None
        | _ -> None

// The number of arguments a shipped trait method takes, for the methods a call lowers directly:
// a bare reference to one is the lambda applying it (stage 0's `BuildTraitMethodEtaLambda`).
let shippedTraitMethodArity (qualifier: Str) (methodName: Str) =
    if isShippedTraitQualifier(qualifier)
    then
        match (standardTraitName(qualifier), methodName) with
            | ("Eq", "equal") -> Some(2)
            | ("Eq", "notEqual") -> Some(2)
            | ("Show", "show") -> Some(1)
            | ("Hash", "hash") -> Some(1)
            | (traitName, _method) ->
                match shippedTraitBinaryOperator(traitName)(methodName) with
                    | Some(_operator) -> Some(2)
                    | None ->
                        match methodName
                        |> ExprQualifiedVar(qualifier)
                        |> shippedTraitUnaryOperator with
                            | Some(_method) -> Some(1)
                            | None -> None
    else None

let recursive traitMethodEtaBody (body: Expr) (index: Int) (arity: Int) =
    if index >= arity
    then body
    else
        traitMethodEtaBody(ExprCall(body)(ExprVar("__trait_method_arg_" + Ashes.Text.fromInt(index)))(false)(callArgumentsInline))(index + 1)(arity)

let recursive traitMethodEtaLambda (body: Expr) (index: Int) =
    if index < 0
    then body
    else
        traitMethodEtaLambda(ExprLambda("__trait_method_arg_" + Ashes.Text.fromInt(index))(body)(None))(index - 1)

// Stage 0's `LowerBareTraitMethodReference`: `List.map(Show.show)` passes the lambda
// `given (a0) -> Show.show(a0)`, whose saturated call takes the direct route.
let shippedTraitMethodReference (qualifier: Str) (methodName: Str) =
    match shippedTraitMethodArity(qualifier)(methodName) with
        | Some(arity) ->
            arity - 1
            |> traitMethodEtaLambda(traitMethodEtaBody(ExprQualifiedVar(qualifier)(methodName))(0)(arity))
            |> Some
        | None -> None

// `Default.default(Unit)` resolves the instance by its result type rather than by an operand: the
// type the context expects, or a goal a later pass pins.
let lowerTraitDefaultCall argument lower state =
    match state
    |> clearConsumerRequest
    |> lower(argument) with
        | LoweredCoreValue { state = loweredState, temp = unitTemp, error = None } ->
            match freshType(loweredState) with
                | FreshType { state = typeState, semanticType = valueType } ->
                    match expectedTypeOf(state) with
                        | Some(expected) ->
                            match bindType(valueType)(expected)(typeState) with
                                | (failedState, Some(error)) -> failure(failedState)(error)
                                | (boundState, None) ->
                                    boundState
                                    |> withConsumerRequest(state.consumerRequest)
                                    |> emitCoreTraitUnaryDispatch("Default")("default")(valueType)(lower)(unitTemp)(resolveType(boundState)(valueType))
                        | None ->
                            typeState
                            |> withConsumerRequest(state.consumerRequest)
                            |> emitCoreTraitUnaryDispatch("Default")("default")(valueType)(lower)(unitTemp)(valueType)
        | failed -> failed

// The unary counterpart of `traitOperatorCall`: `Ashes.Trait.Show.show(x)` and its siblings name a
// trait method directly rather than standing behind an operator, so there is no mapped operator to
// route them through. The result type is the method's own, fixed by the trait rather than by the
// operand.
let traitUnaryMethodCall (function: Expr) =
    match stripExprAt(function) with
        | ExprQualifiedVar(qualifier, methodName) ->
            match (standardTraitName(qualifier), methodName) with
                | ("Show", "show") -> Some(("Show", "show", SemString))
                | ("Hash", "hash") -> Some(("Hash", "hash", SemInt))
                | _ -> None
        | _ -> None

// An operand type still open takes the closed type the standalone inference recorded for the
// operand node, the way a later pass pins a proven goal. A recorded type that holds a variable of a
// binding the program uses at one type is closed by that use, unless the body being lowered carries
// the trait's evidence for the operand, which then dispatches through it.
let pinRecordedOperandType (traitName: Str) (operand: Expr) (operandType: SemanticType) (state: CoreLoweringState) =
    match operandType
    |> resolveType(state)
    |> freeTypeVariables with
        | [] -> (state, operandType)
        | _open ->
            let recorded =
                match recordedClosedTypeAt(nodeIdentityOf(operand))(state) with
                    | Some(closed) -> Some(closed)
                    | None ->
                        if hasActiveTraitEvidence(traitName)(resolveType(state)(operandType))(state)
                        then None
                        else
                            recordedSettledTypeAt(nodeIdentityOf(operand))(state)
            in
                match recorded with
                    | Some(closedType) ->
                        match bindType(operandType)(closedType)(state) with
                            | (bound, None) -> (observeLoweringEvent(0)(bound), resolveType(bound)(operandType))
                            | (_failed, Some(_error)) -> (state, operandType)
                    | None -> (state, operandType)

let lowerTraitUnaryMethodCall (traitName: Str) (methodName: Str) (resultType: SemanticType) argument lower state =
    match state
    |> clearConsumerRequest
    |> lower(argument) with
        | LoweredCoreValue { state = loweredState, temp = operandTemp, semanticType = loweredType, error = None } ->
            match pinRecordedOperandType(traitName)(argument)(loweredType)(loweredState) with
                | (operandState, operandType) ->
                    operandState
                    |> withConsumerRequest(state.consumerRequest)
                    |> emitCoreTraitUnaryDispatch(traitName)(methodName)(resultType)(lower)(operandTemp)(resolveType(operandState)(operandType))
        | failed -> failed

// A trait the program declares itself: lowering's trait environment knows it and the shipped
// environment does not.
let isUserDeclaredTrait (traitName: Str) (state: CoreLoweringState) =
    match resolveTraitBinding(traitName)(standardLoweringTraitEnvironment) with
        | Some(_shipped) -> false
        | None ->
            match state
            |> stateTraitEnvironment
            |> resolveTraitBinding(traitName) with
                | Some(_declared) -> true
                | None -> false

// The trait and the method scheme `qualifier.methodName` names, when the qualifier is a trait the
// program declares and the name one of its methods.
let userTraitMethodScheme (qualifier: Str) (methodName: Str) (state: CoreLoweringState) =
    (let traitName = standardTraitName(qualifier)
    in
        if isShippedTraitQualifier(qualifier) || isUserDeclaredTrait(traitName)(state) == false
        then None
        else
            match state
            |> stateTraitEnvironment
            |> resolveTraitMethod(traitName)(methodName) with
                | Some(TraitMethodInferenceDefinition { scheme = scheme }) -> Some((traitName, scheme))
                | None -> None)

let recursive userTraitMethodArity (semanticType: SemanticType) =
    match semanticType with
        | SemFunction(_argument, result, _row) -> 1 + userTraitMethodArity(result)
        | _ -> 0

// Stage 0's `LowerBareTraitMethodReference` for a user-declared trait: the method as a value is
// the lambda applying it, whose saturated call takes the dispatch route.
let userTraitMethodReference (qualifier: Str) (methodName: Str) (state: CoreLoweringState) =
    match userTraitMethodScheme(qualifier)(methodName)(state) with
        | Some((_traitName, TypeScheme { body = body })) ->
            let arity = userTraitMethodArity(body)
            in
                if arity < 1
                then None
                else
                    arity - 1
                    |> traitMethodEtaLambda(traitMethodEtaBody(ExprQualifiedVar(qualifier)(methodName))(0)(arity))
                    |> Some
        | None -> None

// The arguments after the one a trait method dispatches on apply to the dispatched result as an
// ordinary call.
let applyRemainingTraitArguments (resultTemp: Int) (resultType: SemanticType) (arguments: List(Expr)) lower (state: CoreLoweringState) =
    match arguments with
        | [] -> success(resultTemp)(resultType)(state)
        | _ ->
            match freshLocal(state) with
                | FreshLocal { state = slotState, local = slot } ->
                    let name = "__trait_method_result_" + Ashes.Text.fromInt(slot)
                    in
                        slotState
                        |> emit(StoreLocal(slot)(resultTemp))
                        |> addBinding(name)(emptyScheme(resultType))(CoreLocal(slot))
                        |> lower(appendConstrainedArguments(ExprVar(name))(arguments))
                        |> restoreLoweredBindings(state.bindings)

let consumerRequestForDispatch (rest: List(Expr)) (request: ConsumerRequest) (state: CoreLoweringState) =
    match rest with
        | [] -> withConsumerRequest(request)(state)
        | _ -> clearConsumerRequest(state)

// Stage 0's `LowerTraitMethodCall` for a user-declared trait with one parameter: the method's
// scheme is instantiated, its first parameter typed by the first argument, and the method
// dispatched through the evidence for the trait's type argument (an active requirement inside a
// constrained body, the registered implementation otherwise); later arguments apply to the result.
let lowerUserTraitMethodCall (traitName: Str) (methodName: Str) (scheme: TypeScheme) (arguments: List(Expr)) lower (state: CoreLoweringState) =
    match (arguments, instantiate(scheme)(state.typeSupply)) with
        | (first :: rest, InstantiationResult { semanticType = SemFunction(parameterType, resultType, _row), constraints = TraitConstraint { typeArguments = typeArgument :: [] } :: [], supply = nextSupply }) ->
            match state
            |> withTypeSupply(nextSupply)
            |> clearConsumerRequest
            |> lower(first) with
                | LoweredCoreValue { state = loweredState, temp = operandTemp, semanticType = operandType, error = None } ->
                    match bindType(parameterType)(operandType)(loweredState) with
                        | (failedState, Some(error)) -> failure(failedState)(error)
                        | (boundState, None) ->
                            match boundState
                            |> consumerRequestForDispatch(rest)(state.consumerRequest)
                            |> emitCoreTraitUnaryDispatch(traitName)(methodName)(resolveType(boundState)(resultType))(lower)(operandTemp)(resolveType(boundState)(typeArgument)) with
                                | LoweredCoreValue { state = dispatched, temp = resultTemp, semanticType = dispatchedType, error = None } ->
                                    dispatched
                                    |> withConsumerRequest(state.consumerRequest)
                                    |> applyRemainingTraitArguments(resultTemp)(dispatchedType)(rest)(lower)
                                | failed -> failed
                | failed -> failed
        | _ ->
            SemNever
            |> UnsupportedCoreTraitDispatch(traitName)
            |> failure(state)

// A call spine rooted at a method of a user-declared trait.
let userTraitMethodCall (expression: Expr) (state: CoreLoweringState) =
    match collectCallSpine(expression) with
        | CoreCallSpine { root = root, arguments = arguments } ->
            match stripExprAt(root) with
                | ExprQualifiedVar(qualifier, methodName) ->
                    match userTraitMethodScheme(qualifier)(methodName)(state) with
                        | Some((traitName, scheme)) -> Some((traitName, methodName, scheme, arguments))
                        | None -> None
                | _ -> None

// The name a call spine is rooted at and the number of arguments applied to it.
let recursive callSpineRootOf (expression: Expr) (count: Int) =
    match expression with
        | ExprAt(_span, inner) -> callSpineRootOf(inner)(count)
        | ExprNode(_node, inner) -> callSpineRootOf(inner)(count)
        | ExprCall(function, _argument, _sugar, _layout) -> callSpineRootOf(function)(count + 1)
        | ExprVar(name) -> Some((name, count))
        | _ -> None

let recursive arrowCountOf (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemFunction(_argument, result, _row) -> 1 + arrowCountOf(result)(state)
        | _ -> 0

// The number of arguments a named callee takes: a constructor's fields, or the arrows of a
// binding's type.
let calleeArityOf (name: Str) (state: CoreLoweringState) =
    match constructorLayout(name)(state) with
        | Some(layout) ->
            layout
            |> constructorArity
            |> Some
        | None ->
            match lookupBinding(name)(state.bindings) with
                | Some(CoreBinding { scheme = TypeScheme { body = body } }) ->
                    state
                    |> arrowCountOf(body)
                    |> Some
                | None -> None

// A call that applies a named callee to more arguments than it takes fails where the surplus
// argument meets a value that is no function. The failure is reported against the whole call, as
// stage 0 reports it: the callee, the arguments it takes, and the arguments the call passes; an
// enclosing application of the same spine raises the passed count to its own.
let describeCallArity (expression: Expr) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = failedState, error = Some(CoreCallRequiresFunction(actual)) } ->
            match callSpineRootOf(expression)(0) with
                | Some((name, provided)) ->
                    match calleeArityOf(name)(failedState) with
                        | Some(expected) ->
                            if expected < provided
                            then
                                actual
                                |> resolveType(failedState)
                                |> CoreCallArityMismatch(name)(expected)(provided)
                                |> failure(failedState)
                            else lowered
                        | None -> lowered
                | None -> lowered
        | LoweredCoreValue { state = failedState, error = Some(CoreCallArityMismatch(callee, expected, provided, actual)) } ->
            match callSpineRootOf(expression)(0) with
                | Some((name, applied)) ->
                    if name == callee && applied > provided
                    then
                        actual
                        |> CoreCallArityMismatch(callee)(expected)(applied)
                        |> failure(failedState)
                    else lowered
                | None -> lowered
        | _ -> lowered

// `async` names stage 0's intrinsic unless the program binds the name itself.
let asyncNameUnbound (state: CoreLoweringState) =
    match lookupBinding("async")(state.bindings) with
        | Some(_binding) -> false
        | None ->
            match state
            |> stateTopLevelFunctionRefIndex
            |> lookupTopLevelFunctionRef("async") with
                | Some(_function) -> false
                | None -> true

// The body of `async(body)` or `Ashes.Task.task(body)` when the expression is one.
let asyncTaskBody expression (state: CoreLoweringState) =
    match collectCallSpine(expression) with
        | CoreCallSpine { root = ExprVar(name), arguments = body :: [] } ->
            if name == "async" && asyncNameUnbound(state)
            then Some(body)
            else None
        | CoreCallSpine { root = ExprQualifiedVar(moduleName, memberName), arguments = body :: [] } ->
            if moduleName == "Ashes.Task" && memberName == "task"
            then Some(body)
            else None
        | _ -> None

let taskCall (memberName: Str) (argument: Expr) =
    ExprCall(ExprQualifiedVar("Ashes.Task")(memberName))(argument)(false)(callArgumentsInline)

// A task's type, `Task(Str, a)` for the `a` an `Ok(body)` result carries: binds the result's
// error type to `Str` and answers the task type.
let asyncTaskTypeOf (resultType: SemanticType) (state: CoreLoweringState) =
    match freshType(state) with
        | FreshType { state = typeState, semanticType = valueType } ->
            match bindType(resultType)(SemNamed(0)("Result")([SemString, valueType]))(typeState) with
                | (failedState, Some(error)) -> (failedState, SemNever, Some(error))
                | (boundState, None) -> (boundState, SemNamed(0)("Task")([SemString, valueType]), None)

// Stage 0's `LowerAsyncTask` for a body with no `await`: a task already completed with
// `Ok(body)`, of type `Task(Str, a)`.
let lowerCompletedAsyncTask body lower (state: CoreLoweringState) =
    match state
    |> clearConsumerRequest
    |> lower(callArgumentsInline
    |> ExprCall(ExprVar("Ok"))(body)(false)
    |> taskCall("fromResult")) with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValue { state = loweredState, temp = temp, semanticType = taskType, error = None } ->
            match freshType(loweredState) with
                | FreshType { state = typeState, semanticType = valueType } ->
                    match bindType(taskType)(SemNamed(0)("Task")([SemString, valueType]))(typeState) with
                        | (failedState, Some(error)) -> failure(failedState)(error)
                        | (boundState, None) -> success(temp)(taskType)(boundState)

// The environment size of the closure made for the lifted function `label`, in words.
let recursive closureCaptureCount (label: Str) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> 0
        | IrInstruction { instruction = MakeClosure(_target, funcLabel, _env, envSizeBytes, _managed, _returns, _accepts, _owned) } :: rest ->
            if funcLabel == label
            then envSizeBytes / 8
            else closureCaptureCount(label)(rest)
        | _instruction :: rest -> closureCaptureCount(label)(rest)

let largerOf (left: Int) (right: Int) =
    if left > right
    then left
    else right

// The lifted function `label` turned into a coroutine, as stage 0's coroutine build does: its
// lifetimes are placed on the linear body, where an await is still an ordinary control-flow edge,
// then the state-machine transform splits it at its awaits over the task frame, and every temp a
// block of it reads without defining goes through a local slot. Answers the functions and the frame
// size.
let recursive transformCoroutineFunction (label: Str) (captureCount: Int) (functions: List(IrFunction)) =
    match functions with
        | [] -> ([], 0)
        | function_ :: rest ->
            if function_.label == label
            then
                let placed = placeFunctionLifetimes(function_)
                in
                    match transformStateMachine(placed.instructions)(captureCount) with
                        | StateMachineResult { instructions = instructions, stateCount = stateCount, stateStructSize = stateStructSize, maxTemp = maxTemp } ->
                            let coroutine =
                                CoroutineInfo(
                                    stateCount = stateCount,
                                    stateStructSize = stateStructSize,
                                    captureCount = captureCount
                                )
                            in
                                match spillCrossBlockTemps(instructions)(placed.localCount) with
                                    | (spilledInstructions, addedSlots) -> ((placed with instructions = spilledInstructions, localCount = placed.localCount + addedSlots, tempCount = largerOf(placed.tempCount)(maxTemp + 1), coroutine = Some(coroutine)) :: rest, stateStructSize)
            else
                match transformCoroutineFunction(label)(captureCount)(rest) with
                    | (transformed, stateStructSize) -> (function_ :: transformed, stateStructSize)

// Stage 0's `RetainCoroutineCapture`: the task frame keeps its own reference to each captured value,
// because the frame outlives the expression that created the task and that scope releases its
// bindings. The closure is copied to its reference-counted form, whose environment normalizer
// retains (or copies) every capture, and the task takes its captures from that copy.
let frameOwnedClosure (closureTemp: Int) (captureCount: Int) (state: CoreLoweringState) =
    if captureCount == 0
    then (state, closureTemp)
    else
        match freshTemp(state) with
            | FreshTemp { state = allocated, temp = copyTemp } ->
                (emit(CopyOutClosure(copyTemp)(closureTemp)(true)(RcNormalization))(allocated), copyTemp)

// Whether the lifted function `label` forks a child task.
let recursive functionForks (label: Str) (functions: List(IrFunction)) =
    match functions with
        | [] -> false
        | IrFunction { label = functionLabel, instructions = functionInstructions } :: rest ->
            if functionLabel == label
            then instructionsFork(functionInstructions)
            else functionForks(label)(rest)
and instructionsFork (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = ForkScopedTask(_target, _owner, _task) } :: _rest -> true
        | _instruction :: rest -> instructionsFork(rest)

// A coroutine that forks owns an implicit task scope, as stage 0 builds it: the task is wrapped in
// a scope composite that cancels and drains the unjoined children once the task has its result.
let scopeForkingTask (forks: Bool) (taskTemp: Int) (taskType: SemanticType) (state: CoreLoweringState) =
    if forks
    then
        match freshTemp(state) with
            | FreshTemp { state = scopeState, temp = scopeTemp } ->
                match freshTemp(scopeState) with
                    | FreshTemp { state = scopedState, temp = scopedTemp } ->
                        scopedState
                        |> emit(CreateTaskScope(scopeTemp)(false))
                        |> emit(CreateScopedTask(scopedTemp)(taskTemp)(scopeTemp))
                        |> success(scopedTemp)(taskType)
    else success(taskTemp)(taskType)(state)

// Stage 0's `LowerAsyncTaskCoroutine`: an `async` body that awaits is a coroutine. The body is
// lowered as a lambda returning `Ok(body)` whose awaits suspend, the lambda's function becomes a
// coroutine over a task frame, and `CreateTask` builds the task from the lambda's closure, its
// captures copied into the frame.
let lowerCoroutineTask body lower (state: CoreLoweringState) =
    (let label = "lambda_" + Ashes.Text.fromInt(state.nextLambdaId)
    in
        match state
        |> clearConsumerRequest
        |> withStatePendingCoroutineBody(true)
        |> lowerLambda("__async_unit")(callArgumentsInline |> ExprCall(ExprVar("Ok"))(body)(false))(None)(false)(lower) with
            | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
            | LoweredCoreValue { state = loweredState, temp = closureTemp, semanticType = lambdaType, error = None } ->
                match resolveType(loweredState)(lambdaType) with
                    | SemFunction(_parameter, resultType, _row) ->
                        match loweredState
                        |> withStatePendingCoroutineBody(false)
                        |> asyncTaskTypeOf(resultType) with
                            | (failedState, _taskType, Some(error)) -> failure(failedState)(error)
                            | (typedState, taskType, None) ->
                                let captureCount = closureCaptureCount(label)(typedState.reversedInstructions)
                                in
                                    match transformCoroutineFunction(label)(captureCount)(typedState.functions) with
                                        | (functions, stateStructSize) ->
                                            match frameOwnedClosure(closureTemp)(captureCount)((typedState with functions = functions)) with
                                                | (ownedState, frameClosure) ->
                                                    match freshTemp(ownedState) with
                                                        | FreshTemp { state = tempState, temp = taskTemp } ->
                                                            tempState
                                                            |> emit(CreateTask(taskTemp)(frameClosure)(stateStructSize)(captureCount)(None)(false))
                                                            |> scopeForkingTask(functionForks(label)(functions))(taskTemp)(taskType)
                    | _other -> failure(loweredState)(UnsupportedCoreLoweringExpression("async body")))

let recursive typeHoldsJoinHandle (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemNamed(_symbolId, name, arguments) -> name == "JoinHandle" || anyTypeHoldsJoinHandle(arguments)(state)
        | SemTuple(elements) -> anyTypeHoldsJoinHandle(elements)(state)
        | SemList(element) -> typeHoldsJoinHandle(element)(state)
        | SemFunction(argument, result, _row) -> typeHoldsJoinHandle(argument)(state) || typeHoldsJoinHandle(result)(state)
        | _ -> false
and anyTypeHoldsJoinHandle (types: List(SemanticType)) (state: CoreLoweringState) =
    match types with
        | [] -> false
        | head :: rest -> typeHoldsJoinHandle(head)(state) || anyTypeHoldsJoinHandle(rest)(state)

let joinHandleEscapeMessage = "A JoinHandle cannot escape in the result of an async task."

// Stage 0's scope-escape check on an `async` task: its value may not hold a join handle, which
// belongs to the task scope that ends with the task.
let rejectEscapingJoinHandle (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, semanticType = taskType, error = None } ->
            match resolveType(state)(taskType) with
                | SemNamed(_symbolId, _name, _errorType :: valueType :: []) ->
                    if typeHoldsJoinHandle(valueType)(state)
                    then failure(state)(UnsupportedCoreBuiltinLowering(joinHandleEscapeMessage))
                    else lowered
                | _ -> lowered
        | failed -> failed

let lowerAsyncTask body lower (state: CoreLoweringState) =
    if exprContainsAwait(body)
    then
        state
        |> lowerCoroutineTask(body)(lower)
        |> rejectEscapingJoinHandle
    else
        state
        |> lowerCompletedAsyncTask(body)(lower)
        |> rejectEscapingJoinHandle

// The newest `RunTask` into `target` made an `AwaitTask`: inside an `async` body an `await`
// suspends the task instead of running the awaited one inline.
let recursive suspendRunTask (target: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> []
        | (IrInstruction { instruction = RunTask(runTarget, task) } as wrapped) :: rest ->
            if runTarget == target
            then (wrapped with instruction = AwaitTask(runTarget)(task)) :: rest
            else wrapped :: suspendRunTask(target)(rest)
        | wrapped :: rest -> wrapped :: suspendRunTask(target)(rest)

// Stage 0's `LowerAwait`: an `await` runs its task to completion outside an `async` body, as
// `Ashes.Task.run` does, and is a suspension point inside one.
let lowerAwait operand lower (state: CoreLoweringState) =
    match lower(taskCall("run")(operand))(state) with
        | LoweredCoreValue { state = loweredState, temp = temp, error = None } as lowered ->
            if stateCoroutineBody(loweredState)
            then lowered with state = (loweredState with reversedInstructions = suspendRunTask(temp)(loweredState.reversedInstructions))
            else lowered
        | failed -> failed

let lowerNonAsyncCallExpression expression function argument lower state =
    match state
    |> clearConsumerRequest
    |> tryLowerConstructorCall(expression)(consumerRequestOf(state))(lower) with
        | Some(lowered) ->
            lowered
            |> unifyOptionalExpectedResult(expectedTypeOf(state))
            |> locateLoweredMismatch(argumentSiteOf(state))
        | None ->
            match tryLowerBuiltinCall(expression)(lower)(withConsumerRequest((emptyConsumerRequest with runtimeString = runtimeStringRequested(state)))(state)) with
                | Some(lowered) -> lowered
                | None ->
                    match state
                    |> clearConsumerRequest
                    |> tryLowerExternalCall(expression)(lower) with
                        | Some(lowered) -> lowered
                        | None ->
                            if isCapabilityOperationCall(expression)(state)
                            then
                                state
                                |> clearConsumerRequest
                                |> lowerPerform(expression)(lower)
                                |> unifyOptionalExpectedResult(expectedTypeOf(state))
                                |> locateLoweredMismatch(argumentSiteOf(state))
                            else lowerGeneralCall(expression)(function)(argument)(lower)(state)

let unitArgument (function: Expr) = ExprCall(function)(ExprVar("Unit"))(false)(callArgumentsInline)

let plainLet (name: Str) (value: Expr) (body: Expr) = ExprLet(name)(value)(body)([])(None)([])

let recursive callWith (function: Expr) (arguments: List(Expr)) =
    match arguments with
        | [] -> function
        | argument :: rest ->
            callWith(ExprCall(function)(argument)(false)(callArgumentsInline))(rest)

let internalCall (memberName: Str) (arguments: List(Expr)) =
    callWith(ExprQualifiedVar("Ashes.Internal")(memberName))(arguments)

let parallelLeftName = "__parallel_left"

let parallelRightName = "__parallel_right"

let parallelDescName = "__parallel_desc"

// Stage 0's `CanRunRightOnWorker`: a result a worker can hand back to the parent, one with no
// pointer into the worker's arena once it is deep-copied: a scalar, a string or bytes, a list of
// scalars, strings or scalar lists, a tuple of such values, or a non-resource type with an arena
// deep copy.
let recursive canRunRightOnWorker (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemInt -> true
        | SemUInt(_bits) -> true
        | SemFloat -> true
        | SemRune -> true
        | SemBool -> true
        | SemString -> true
        | SemBytes -> true
        | SemList(element) ->
            match resolveType(state)(element) with
                | SemString -> true
                | SemList(inner) -> resultSurvivesReset(inner)(state)
                | resolvedElement -> resultSurvivesReset(resolvedElement)(state)
        | SemTuple(elements) -> allRunRightOnWorker(elements)(state)
        | SemNamed(_symbolId, name, _arguments) as named ->
            !isResourceTypeName(name) && arenaDeepCopySupported(heapFactsOf(named)(state))
        | _ -> false
and allRunRightOnWorker (elements: List(SemanticType)) (state: CoreLoweringState) =
    match elements with
        | [] -> true
        | element :: rest -> canRunRightOnWorker(element)(state) && allRunRightOnWorker(rest)(state)

// The result type of the thunk a local binding holds, once it is known.
let thunkResultType (name: Str) (state: CoreLoweringState) =
    match lookupBinding(name)(state.bindings) with
        | Some(CoreBinding { scheme = TypeScheme { body = bindingType } }) ->
            match resolveType(state)(bindingType) with
                | SemFunction(_parameter, result, _row) -> Some(result)
                | _ -> None
        | None -> None

// The type of a value a local binding holds, once it is known.
let thunkResultTypeOfValue (name: Str) (state: CoreLoweringState) =
    match lookupBinding(name)(state.bindings) with
        | Some(CoreBinding { scheme = TypeScheme { body = bindingType } }) ->
            bindingType
            |> resolveType(state)
            |> Some
        | None -> None

// Stage 0's `LowerParallelBoth` once both thunks are bound: at a result type a worker can hand
// back, the right thunk is forked onto a worker while the left one runs here, then joined, its
// result deep-copied into this arena and the worker released, the pair built from the two
// results; at any other type (an abstract one included) both run here in order.
let parallelBothBound (state: CoreLoweringState) =
    (let left = ExprVar(parallelLeftName)
    in
        let right = ExprVar(parallelRightName)
        in
            match thunkResultType(parallelRightName)(state) with
                | Some(resultType) ->
                    if canRunRightOnWorker(resultType)(state)
                    then
                        ExprTuple([ExprVar("__parallel_a"), ExprVar("__parallel_b")])
                        |> plainLet("_")(internalCall("parallelCleanup")([ExprVar(parallelDescName), right]))
                        |> plainLet("__parallel_b")(internalCall("deepCopy")([internalCall("parallelJoin")([ExprVar(parallelDescName), right])]))
                        |> plainLet("__parallel_a")(unitArgument(left))
                        |> plainLet(parallelDescName)(internalCall("parallelFork")([right]))
                    else ExprTuple([unitArgument(left), unitArgument(right)])
                | None -> ExprTuple([unitArgument(left), unitArgument(right)]))

// Stage 0's `LowerParallelReduceQueued` once the four arguments are bound: at a result type a worker
// can hand back the work-conserving queue maps and merges the list, the merge root deep-copied into
// this arena (the identity for an empty list) before the queue is released; at any other type the
// polymorphic `reduce` runs here.
let parallelReduceBound (state: CoreLoweringState) =
    (let combine = ExprVar("__parallel_combine")
    in
        let identity = ExprVar("__parallel_identity")
        in
            let mapper = ExprVar("__parallel_mapper")
            in
                let list = ExprVar("__parallel_list")
                in
                    let sequential = callWith(ExprVar("Ashes_Task_Parallel_reduce"))([combine, identity, mapper, list])
                    in
                        match thunkResultTypeOfValue("__parallel_identity")(state) with
                            | Some(resultType) ->
                                if canRunRightOnWorker(resultType)(state)
                                then
                                    let queue = ExprVar("__parallel_queue")
                                    in
                                        ExprVar("__parallel_reduced")
                                        |> plainLet("_")(internalCall("parallelQueueCleanup")([queue]))
                                        |> plainLet("__parallel_reduced")(ExprIf(ExprGreaterThan(internalCall("parallelQueueCount")([queue]))(ExprInt(0)))(internalCall("deepCopy")([internalCall("parallelQueueAwait")([queue, identity])]))(identity))
                                        |> plainLet("__parallel_queue")(internalCall("parallelQueueStart")([mapper, combine, list]))
                                else sequential
                            | None -> sequential)

// A saturated `Ashes.Task.Parallel.reduce` call that is not already the bound form above.
let parallelReduceCall expression =
    match collectCallSpine(expression) with
        | CoreCallSpine { root = root, arguments = combine :: identity :: mapper :: list :: [] } ->
            match (unspanArgument(root), unspanArgument(combine)) with
                | (ExprVar("Ashes_Task_Parallel_reduce"), ExprVar("__parallel_combine")) -> None
                | (ExprVar("Ashes_Task_Parallel_reduce"), _) ->
                    [ExprVar("__parallel_identity"), ExprVar("__parallel_list")]
                    |> callWith(ExprQualifiedVar("Ashes.Task.Parallel")("__reduceBound"))
                    |> plainLet("__parallel_list")(list)
                    |> plainLet("__parallel_mapper")(mapper)
                    |> plainLet("__parallel_identity")(identity)
                    |> plainLet("__parallel_combine")(combine)
                    |> Some
                | _ -> None
        | _ -> None

// `Ashes.Task.Parallel.both(left)(right)` binds its two thunks in order and continues with the
// bound form above; `withWorkers(count)(action)` checks the count, installs it as the worker
// override for the action and restores the enclosing override after it, stage 0's
// `LowerParallelWithWorkers`.
let parallelCallRewrite expression (state: CoreLoweringState) =
    match collectCallSpine(expression) with
        | CoreCallSpine { root = ExprQualifiedVar(moduleName, memberName), arguments = first :: second :: [] } ->
            if moduleName == "Ashes.Task.Parallel"
            then
                if memberName == "both"
                then
                    [ExprVar(parallelLeftName), ExprVar(parallelRightName)]
                    |> callWith(ExprQualifiedVar("Ashes.Task.Parallel")("__bothBound"))
                    |> plainLet(parallelRightName)(second)
                    |> plainLet(parallelLeftName)(first)
                    |> Some
                else
                    if memberName == "__bothBound" || memberName == "__reduceBound"
                    then
                        if memberName == "__bothBound"
                        then
                            state
                            |> parallelBothBound
                            |> Some
                        else
                            state
                            |> parallelReduceBound
                            |> Some
                    else
                        if memberName == "withWorkers"
                        then
                            let count = ExprVar("__parallel_count")
                            in
                                let scoped =
                                    ExprVar("__parallel_result")
                                    |> plainLet("_")(internalCall("setParallelOverride")([ExprVar("__parallel_saved")]))
                                    |> plainLet("__parallel_result")(unitArgument(second))
                                    |> plainLet("_")(internalCall("setParallelOverride")([count]))
                                    |> plainLet("__parallel_saved")(internalCall("parallelOverride")([ExprVar("Unit")]))
                                in
                                    callArgumentsInline
                                    |> ExprCall(ExprQualifiedVar("Ashes.IO")("panic"))(ExprString("Ashes.Task.Parallel.withWorkers: worker count must be positive."))(false)
                                    |> ExprIf(ExprGreaterThan(count)(ExprInt(0)))(scoped)
                                    |> plainLet("__parallel_count")(first)
                                    |> Some
                        else None
            else None
        | _ -> parallelReduceCall(expression)

// `Ashes.Task.fork` outside an `async` body has no owning task scope.
let forksOutsideAsync expression (state: CoreLoweringState) =
    match collectCallSpine(expression) with
        | CoreCallSpine { root = ExprQualifiedVar(moduleName, memberName) } -> moduleName == "Ashes.Task" && memberName == "fork" && stateCoroutineBody(state) == false
        | _ -> false

// The local a `Ashes.Task.join(handle)` call names as its handle: a join consumes the handle, so a
// second join of the same binding is stage 0's use-after-move (`ASH008`).
let joinedHandleLocal expression (state: CoreLoweringState) =
    match collectCallSpine(expression) with
        | CoreCallSpine { root = ExprQualifiedVar(moduleName, memberName), arguments = handleArgument :: [] } ->
            if moduleName == "Ashes.Task" && memberName == "join"
            then
                match unspanArgument(handleArgument) with
                    | ExprVar(name) ->
                        match lookupBinding(name)(state.bindings) with
                            | Some(CoreBinding { location = CoreLocal(slot) }) -> Some((name, slot))
                            | _ -> None
                    | _ -> None
            else None
        | _ -> None

let lowerJoinOrCall expression function argument lower (state: CoreLoweringState) =
    match joinedHandleLocal(expression)(state) with
        | Some((name, slot)) ->
            match resourceStateOf(slot)(state) with
                | Some(ResourceMoved) -> failure(state)(ResourceUseAfterMove("Resource '" + name + "' has been moved and can no longer be used here. Passing a resource to a function or storing it in a data structure transfers ownership."))
                | _ ->
                    match lowerNonAsyncCallExpression(expression)(function)(argument)(lower)(state) with
                        | LoweredCoreValue { state = loweredState, error = None } as lowered -> lowered with state = markResourceReleased(slot)(ResourceMoved)(loweredState)
                        | failed -> failed
        | None -> lowerNonAsyncCallExpression(expression)(function)(argument)(lower)(state)

// A call of one of the `Ashes.Task.Parallel` functions whose callbacks run on worker threads.
let isParallelWorkerCall expression =
    match collectCallSpine(expression) with
        | CoreCallSpine { root = ExprQualifiedVar(moduleName, memberName) } -> moduleName == "Ashes.Task.Parallel" && (memberName == "map" || memberName == "reduce" || memberName == "mapGrained" || memberName == "reduceGrained")
        | CoreCallSpine { root = ExprVar(name) } -> name == "Ashes_Task_Parallel_map" || name == "Ashes_Task_Parallel_reduce" || name == "Ashes_Task_Parallel_mapGrained" || name == "Ashes_Task_Parallel_reduceGrained"
        | _ -> false

// Lowers such a call as code that may run on a worker, so the callbacks lifted from its arguments
// do (stage 0 marks the callback functions themselves); the enclosing answer is restored after it.
let lowerAsParallelWorker lowerCall (state: CoreLoweringState) =
    (let enclosing = stateMayRunAsParallelWorker(state)
    in
        match state
        |> withStateMayRunAsParallelWorker(true)
        |> lowerCall with
            | LoweredCoreValue { state = loweredState } as lowered -> lowered with state = withStateMayRunAsParallelWorker(enclosing)(loweredState))

// Lowers an expression lowering built in place of the node being lowered that is not that node's
// own value, such as an eta expansion or the runtime machinery around a call: it has no identity
// of its own, so nothing recorded for the enclosing node is read for it, while the nodes it
// carries from the source keep theirs. The enclosing node is current again afterwards.
let lowerSynthesized lower (expression: Expr) (state: CoreLoweringState) =
    match state with
        | CoreLoweringState { currentNode = previous } ->
            match lower(expression)((state with currentNode = None)) with
                | LoweredCoreValue { state = innerState, temp = temp, semanticType = semanticType, error = error } ->
                    LoweredCoreValue(
                        state = (innerState with currentNode = previous),
                        temp = temp,
                        semanticType = semanticType,
                        error = error
                    )

let lowerCallExpression expression function argument lower state =
    match asyncTaskBody(expression)(state) with
        | Some(body) -> lowerAsyncTask(body)(lower)(state)
        | None ->
            match parallelCallRewrite(expression)(state) with
                | None ->
                    if forksOutsideAsync(expression)(state)
                    then failure(state)(UnsupportedCoreBuiltinLowering("Ashes.Task.fork() must be used inside an async task."))
                    else
                        if isParallelWorkerCall(expression)
                        then
                            lowerAsParallelWorker(lowerJoinOrCall(expression)(function)(argument)(lower))(state)
                        else lowerJoinOrCall(expression)(function)(argument)(lower)(state)
                | Some(rewritten) ->
                    if isParallelWorkerCall(expression)
                    then
                        lowerAsParallelWorker(lowerSynthesized(lower)(rewritten))(state)
                    else lowerSynthesized(lower)(rewritten)(state)

// The spine of the loop being lowered now holds a cell, so its return has one to close.
let activateTmcChain (state: CoreLoweringState) =
    match stateTcoLoopFrame(state) with
        | Some(CoreTcoLoopFrame { bodyLabel = bodyLabel }) -> withStateTmcActivatedLabels(bodyLabel :: stateTmcActivatedLabels(state))(state)
        | None -> state

// Stage 0's `EmitTmcLinkCell`: the first cell becomes the spine head, every later one is stored into
// its predecessor's still-nil tail field, and the new cell becomes the pending destination.
let emitTmcLinkCell (frame: CoreTcoLoopFrame) (cellTemp: Int) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = destState, temp = destTemp } ->
            match freshTemp(destState) with
                | FreshTemp { state = zeroState, temp = zeroTemp } ->
                    match freshTemp(zeroState) with
                        | FreshTemp { state = condState, temp = condTemp } ->
                            match freshLabel("tmc_first")(condState) with
                                | FreshLabel { state = firstState, label = firstLabel } ->
                                    match freshLabel("tmc_link_done")(firstState) with
                                        | FreshLabel { state = doneState, label = doneLabel } ->
                                            doneState
                                            |> emit(LoadLocal(destTemp)(frame.tmcDestSlot))
                                            |> emit(LoadConstInt(zeroTemp)(0))
                                            |> emit(CmpIntNe(condTemp)(destTemp)(zeroTemp))
                                            |> emit(JumpIfFalse(condTemp)(firstLabel))
                                            |> emit(StoreMemOffset(destTemp)(8)(cellTemp))
                                            |> emit(Jump(doneLabel))
                                            |> emit(Label(firstLabel))
                                            |> emit(StoreLocal(frame.tmcResultSlot)(cellTemp))
                                            |> emit(Label(doneLabel))
                                            |> emit(StoreLocal(frame.tmcDestSlot)(cellTemp))
                                            |> activateTmcChain

// Tail modulo constructor, stage 0's `LowerConsTmc`: one more cell on an iteratively built spine plus
// the loop's own back edge, instead of a call whose pending constructor keeps a native frame alive per
// element. The cell goes through the ordinary cell path with a NIL tail, so placement, reuse tokens,
// and head ownership are decided exactly as for a cons that is not transformed; nil is the one value
// that owns nothing, so filling it on the next iteration is a plain ownership transfer, and the spine
// is a complete, walkable list at every instant.
// The spine must be reference-counted, stage 0's `CanBuildRuntimeManagedCell`. An arena spine sits
// above the per-iteration watermark, so the back edge reclaims it unless the loop stops reclaiming,
// and a loop that stops reclaiming strands every iteration's garbage for the whole traversal; and
// even with the watermark moved past each cell, an arena cell stores its head raw while the back
// edge is free to release the parameter graph that head was borrowed from. A head the cell cannot
// own as a reference hands the cons back to the ordinary path, which recurses: correct, just not
// stack-safe.
let finishConsTmc (request: ConsumerRequest) (transfers: Bool) (frame: CoreTcoLoopFrame) (loop: CoreTcoLoop) (headExpression: Expr) (tailExpression: Expr) lower head =
    match head with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValue { state = headState, temp = headTemp, semanticType = headType, error = None } when cellIsRuntimeManaged(request)(headExpression)(headTemp)(headType)(headState) == false -> finishConsTail(request)(transfers)(lower)(headExpression)(tailExpression)(head)
        | LoweredCoreValue { state = headState, temp = headTemp, semanticType = headType, error = None } ->
            match duplicatePatternOwnerTemp(headExpression)(headTemp)(headState) with
                | (duplicatedState, duplicatedHead) ->
                    match freshTemp(duplicatedState) with
                        | FreshTemp { state = nilState, temp = nilTemp } ->
                            match nilState
                            |> emit(LoadConstInt(nilTemp)(0))
                            |> allocateListCell(duplicatedHead)(nilTemp)(headType)(cellIsRuntimeManaged(request)(headExpression)(duplicatedHead)(headType)(nilState)) with
                                | LoweredCoreValue { state = failedCell, error = Some(error) } -> failure(failedCell)(error)
                                | LoweredCoreValue { state = cellState, temp = cellTemp, error = None } ->
                                    match cellState
                                    |> emitTmcLinkCell(frame)(cellTemp)
                                    |> lowerTailSelfCall(collectCallSpine(tailExpression))(frame)(loop)(lower) with
                                        | LoweredCoreValue { state = failedEdge, error = Some(error) } -> failure(failedEdge)(error)
                                        | LoweredCoreValue { state = edgeState, temp = edgeTemp, semanticType = edgeType, error = None } ->
                                            match bindType(SemList(headType))(edgeType)(edgeState) with
                                                | (typedEdge, _error) ->
                                                    success(edgeTemp)(headType
                                                    |> resolveType(typedEdge)
                                                    |> SemList)(typedEdge)

// Whether this cons is the tail-modulo-constructor shape: the enclosing loop reserved the spine slots
// for it, and the cons tail is that loop's own saturated self-call in tail position.
let tmcSelfCallTail (spine: CoreCallSpine) (state: CoreLoweringState) =
    match (stateTcoLoop(state), consumerRequestOf(state), unspanArgument(spine.root)) with
        | (Some(CoreTcoLoop { selfName = selfName, arity = arity, pendingCurried = pending }), ConsumerRequest { consTailPosition = true }, ExprVar(name)) -> pending == 0 && name == selfName && coreListLength(spine.arguments) == arity
        | _ -> false

let tmcCandidateCons (tailExpression: Expr) (state: CoreLoweringState) =
    match stateTcoLoopFrame(state) with
        | Some(CoreTcoLoopFrame { tmcDestSlot = destSlot }) ->
            destSlot >= 0 && tmcSelfCallTail(collectCallSpine(tailExpression))(state)
        | None -> false

// Stage 0's `OwnContractHeadForTmcCell`: a head of the ownership contract's own types that a call
// produced (its owned result, kept in an owned slot the function releases itself) takes a reference
// of its own for the spine's cell, so the recursion builds its reference-counted spine instead of
// recursing per element.
let ownContractHeadForTmcCell (request: ConsumerRequest) (headExpression: Expr) (lowered: LoweredCoreValue) =
    match (lowered, unspanArgument(headExpression)) with
        | (LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None }, ExprCall(_function, _argument, _whitespace, _layout)) ->
            match resolveType(state)(semanticType) with
                | SemNamed(_symbolId, _name, _arguments) as named ->
                    if requestsRuntimeList(request) && !isRuntimeTemp(temp)(state) && isGeneralRcValueType(named)(state)
                    then
                        match generalCopyPlanOf(named)(state) with
                            | Some(plan) ->
                                let heldRetains =
                                    if isArenaCell(temp)(state.reversedInstructions)
                                    then retainsHeldPastTheCopy(temp)([])(state.reversedInstructions)
                                    else []
                                in
                                    match emitGuardedDeepCopy(temp)(plan)(state) with
                                        | (copied, ownedTemp) ->
                                            copied
                                            |> emitHeldRetainReleases(heldRetains)(semanticType)
                                            |> markRuntimeTemp(ownedTemp)(RuntimeNewlyProduced)
                                            |> success(ownedTemp)(semanticType)
                            | None -> lowered
                    else lowered
                | _ -> lowered
        | _ -> lowered

// The transformed cons's own head prelude, deliberately identical to `lowerCons`'s up to the point
// the tail would be lowered — a candidate tail is a saturated self-call, so the reuse-token rule
// reaches the same request either way, and only the finisher differs. Keeping the prelude shared is
// what lets `finishConsTmc` decline after the head is lowered without lowering it a second time. The
// head is lowered for a reference-counted list whether or not the cell reuses a token, as stage 0's
// `LowerCons` asks for one for every candidate cell.
let lowerConsTmc head tail lower state =
    match consumerRequestOf(state) with
        | outerRequest ->
            let request =
                if listCellReuseTokenAvailable(state.reuseTokens) == false
                then outerRequest with runtimeList = true
                else outerRequest
            in
                match (stateTcoLoopFrame(state), stateTcoLoop(state)) with
                    | (Some(frame), Some(loop)) ->
                        state
                        |> markResourceArgumentsMoved([head, tail])
                        |> withConsumerRequest(listElementRequest((outerRequest with runtimeList = true))(expectedListElementType(state))(listTransfers(request)(state))(head)(state))
                        |> lower(head)
                        |> normalizeRuntimeManagedConsHead(request)(head)(tail)
                        |> normalizePatternOwnerHead(request)(head)
                        |> retainListElement(request)(listTransfers(request)(state))(head)
                        |> ownContractHeadForTmcCell(request)(head)
                        |> ownBorrowedHeadForProducerCell(true)(request)(head)
                        |> finishConsTmc(request)(listTransfers(request)(state))(frame)(loop)(head)(tail)(lower)
                    | _ -> lowerCons(head)(tail)(lower)(state)

// The original source scheme remains public; only the stored closure receives
// the canonical dictionary prefix. A failed speculative inference leaves the old
// lowerer responsible for its existing diagnostic, without adopting fresh IDs.
let recursive concreteSourceMentionsCandidate names registry =
    match names with
        | [] -> false
        | name :: rest ->
            match lookupCoreConcreteCandidate(name)(registry) with
                | Some(_candidate) -> true
                | None -> concreteSourceMentionsCandidate(rest)(registry)

// Stage 0's `BodyPerformsProvidedParameterizedCapability`: the value names an operation of a
// parameterized capability that has a provider, so a copy at a concrete call site lets that
// operation resolve to the provider (capability monomorphization).
let recursive performsProvidedParameterizedCapability (value: Expr) (providers: List(CoreStaticProviderLayout)) =
    match providers with
        | [] -> false
        | CoreStaticProviderLayout { capabilityName = name, typeArguments = arguments } :: rest ->
            if arguments != [] && exprReadsName(name)(value)
            then true
            else performsProvidedParameterizedCapability(value)(rest)

// Stage 0's `_parallelSpecializable`: the grained data-parallel combinators, copied at each
// concrete call site so the `both` splits in the copy see a concrete result type and fork.
let isParallelSpecializable (name: Str) = name == "Ashes_Task_Parallel_mapGrained" || name == "Ashes_Task_Parallel_reduceGrained"

let registerConcreteCandidateScheme name value (scheme: TypeScheme) slot isRecursive state =
    match lambdaParameterChain(value)([]) with
        | (parameters, _body) ->
            withStateConcreteRegistry(state
            |> stateConcreteRegistry
            |> registerCoreConcreteCandidate(name)(value)(scheme)(length(parameters))(slot)(isRecursive))(state)

// A binding lowering keeps monomorphic, whose recorded scheme the standalone inference generalized
// over an operator trait lowering holds monomorphic, registers with the recorded scheme: each call
// at a concrete type then gets its own copy, as lowering's own constrained schemes do.
let registerConcreteSource name value isRecursive state =
    if stateConcreteDepth(state) != 0 || !inProgramEntryFrame(state)
    then state
    else
        match (lambdaParts(value), lookupBinding(name)(state.bindings)) with
            | (Some(_parts), Some(CoreBinding { location = CoreLocal(slot) } as binding)) ->
                let scheme = binding.scheme
                in
                    if scheme.constraints != []
                    then registerConcreteCandidateScheme(name)(value)(scheme)(slot)(isRecursive)(state)
                    else
                        match recordedOperatorGenericScheme(nodeIdentityOf(value))(state) with
                            | Some(recorded) -> registerConcreteCandidateScheme(name)(value)(recorded)(slot)(isRecursive)(state)
                            | None ->
                                if concreteSourceMentionsCandidate(collectFree(value)([name])([]))(stateConcreteRegistry(state)) || performsProvidedParameterizedCapability(value)(stateStaticProviders(state)) || isParallelSpecializable(name)
                                then registerConcreteCandidateScheme(name)(value)(scheme)(slot)(isRecursive)(state)
                                else state
            | _ -> state

// These mapped operators already have typed active-evidence dispatch in Core.
// Arithmetic/bitwise fallback operators keep their existing inference path until
// their dictionary dispatch is implemented, rather than emitting a generic body
// containing a provisional primitive operation.
let recursive constrainedOperatorsSupported constraints =
    match constraints with
        | [] -> true
        | TraitConstraint { traitName = name } :: rest -> (name == "Eq" || name == "Ord" || name == "Not") && constrainedOperatorsSupported(rest)

// A written `requires` may also name `Show` or `Hash`: their methods dispatch through the active
// requirement's evidence (`emitCoreTraitUnaryDispatch`) as the mapped operators' do. An inferred
// requirement still needs one of the operators above, so a binding that only shows its argument and
// says nothing keeps its monomorphic lowering.
let recursive writtenConstraintsSupported constraints =
    match constraints with
        | [] -> true
        | TraitConstraint { traitName = name } :: rest -> (name == "Eq" || name == "Ord" || name == "Not" || name == "Show" || name == "Hash") && writtenConstraintsSupported(rest)

let constrainedPlanSupported (written: Bool) constraints =
    if written
    then writtenConstraintsSupported(constraints)
    else constrainedOperatorsSupported(constraints)

// A plan's requirements are each supported as `constrainedPlanSupported` says, or name a trait the
// program declares, whose methods dispatch through the active requirement's evidence
// (`lowerUserTraitMethodCall`).
let recursive constrainedPlanSupportedIn (written: Bool) (constraints: List(TraitConstraint)) (state: CoreLoweringState) =
    match constraints with
        | [] -> true
        | (TraitConstraint { traitName = name } as constraint) :: rest -> (isUserDeclaredTrait(name)(state) || constrainedPlanSupported(written)([constraint])) && constrainedPlanSupportedIn(written)(rest)(state)

let programTraitDefinitions (state: CoreLoweringState) =
    match stateTraitEnvironment(state) with
        | TypeEnvironment { traits = traits } -> traits

// Whether the value names a trait the program declares, which an unannotated binding's plan may
// then rest on.
let recursive mentionsUserDeclaredTrait (value: Expr) (traits: List(TraitInferenceDefinition)) (state: CoreLoweringState) =
    match traits with
        | [] -> false
        | TraitInferenceDefinition { name = name } :: rest -> isUserDeclaredTrait(name)(state) && exprReadsName(name)(value) || mentionsUserDeclaredTrait(value)(rest)(state)

let recursive hasOperatorConstraint (constraints: List(TraitConstraint)) =
    match constraints with
        | [] -> false
        | TraitConstraint { traitName = name } :: rest -> name == "Eq" || name == "Ord" || name == "Not" || hasOperatorConstraint(rest)

let recursive operatorConstrainedNames (bindings: List((Str, TypeScheme))) (names: MapTree(Str, Bool)) =
    match bindings with
        | [] -> names
        | (name, TypeScheme { constraints = constraints }) :: rest ->
            if hasOperatorConstraint(constraints)
            then
                names
                |> Ashes.Collection.Map.setStr(name)(true)
                |> operatorConstrainedNames(rest)
            else operatorConstrainedNames(rest)(names)

let recursive namedOperatorConstrained (name: Str) (bindings: List((Str, TypeScheme))) =
    match bindings with
        | [] -> false
        | (candidate, TypeScheme { constraints = constraints }) :: rest -> candidate == name && hasOperatorConstraint(constraints) || namedOperatorConstrained(name)(rest)

// Registration is complete before any item is lowered, so the coverage bindings are read once.
let recordCoverageOperatorConstrainedNames (state: CoreLoweringState) =
    (let group = state.programState
    in
        let coverage = group.coverageTypes
        in
            let names : MapTree(Str, Bool) =
                Ashes.Collection.Map.empty
                |> operatorConstrainedNames(coverage.bindings)
                |> operatorConstrainedNames(coverage.fallbackBindings)
            in state with programState = (group with coverageOperatorConstrainedNames = Some((group.coverageEnvironmentVersion, names))))

let coverageNameCarriesOperatorConstraint (name: Str) (state: CoreLoweringState) =
    (let program = state.programState
    in
        let coverage = program.coverageTypes
        in
            let scanned =
                given (_unit) -> namedOperatorConstrained(name)(coverage.bindings) || namedOperatorConstrained(name)(coverage.fallbackBindings)
            in
                match program.coverageOperatorConstrainedNames with
                    | Some((version, names)) ->
                        if version == program.coverageEnvironmentVersion
                        then
                            match Ashes.Collection.Map.getStr(name)(names) with
                                | Some(_present) -> true
                                | None -> false
                        else scanned(Unit)
                    | None -> scanned(Unit))

// A name resolves as `constrainedInferenceEnvironment` resolves it: the nearest local binding,
// then the youngest top-level function reference, then the coverage bindings.
let nameCarriesOperatorConstraint (state: CoreLoweringState) (name: Str) =
    match lookupBinding(name)(state.bindings) with
        | Some(binding) ->
            binding
            |> bindingRequirements
            |> hasOperatorConstraint
        | None ->
            match state
            |> stateTopLevelFunctionRefIndex
            |> lookupTopLevelFunctionRef(name) with
                | Some((_label, TypeScheme { constraints = constraints })) -> hasOperatorConstraint(constraints)
                | None -> coverageNameCarriesOperatorConstraint(name)(state)

// A plan needs a generalized `Eq`, `Ord` or `Not` requirement on the binding's scheme. With no
// written `requires`, the scheme's requirements are the ones its value produced, so a value that
// can produce none of the three has no plan and the inference that would say so is not run.
let constrainedPlanCannotApply (value: Expr) (requirements: List(TraitConstraintSyntax)) (state: CoreLoweringState) =
    match requirements with
        | [] ->
            mayOriginateOperatorConstraint(nameCarriesOperatorConstraint(state))(value) == false && mentionsUserDeclaredTrait(value)(programTraitDefinitions(state))(state) == false
        | _ -> false

let writtenRequirements (requirements: List(TraitConstraintSyntax)) =
    match requirements with
        | [] -> false
        | _ -> true

// `written` says the binding declares a `requires`: its plan is then inferred whatever its value
// mentions, and may rest on `Show` or `Hash` as well as on the mapped operators.
let constrainedBindingPlanWith (written: Bool) name isRecursive value annotation requirements (state: CoreLoweringState) =
    match lambdaParts(value) with
        | None -> None
        | Some(_parts) ->
            if written == false && constrainedPlanCannotApply(value)(requirements)(state)
            then None
            else
                match inferCoreConstrainedBinding(name)(isRecursive)(value)(annotation)(requirements)(constrainedInferenceEnvironment(state))(state.typeSupply) with
                    | CoreConstrainedInference { scheme = TypeScheme { constraints = [] } } -> None
                    | CoreConstrainedInference { error = Some(_error) } -> None
                    | CoreConstrainedInference { scheme = TypeScheme { constraints = constraints } } as plan ->
                        if constrainedPlanSupportedIn(written)(constraints)(state)
                        then Some(plan)
                        else None

let constrainedBindingPlan name isRecursive value annotation requirements (state: CoreLoweringState) =
    constrainedBindingPlanWith(writtenRequirements(requirements))(name)(isRecursive)(value)(annotation)(requirements)(state)

let recursive constrainedPlanAvailable plan state =
    match plan with
        | TraitEvidenceParameter(TraitConstraint { traitName = traitName, typeArguments = argument :: [] }) ->
            allConstrainedMethodsAvailable(traitName)(argument)(sortedTraitMethodNames(traitName)(state))(state)
        | TraitEvidenceParameter(_constraint) -> false
        | TraitEvidenceInstance(_constraint, _implementation, requirements, supertraits) -> constrainedPlansAvailable(requirements)(state) && constrainedPlansAvailable(supertraits)(state)
and constrainedPlansAvailable plans state =
    match plans with
        | [] -> true
        | plan :: rest -> constrainedPlanAvailable(plan)(state) && constrainedPlansAvailable(rest)(state)
and allConstrainedMethodsAvailable traitName argument methods state =
    match methods with
        | [] -> true
        | methodName :: rest ->
            activeTraitMethod(traitName)(argument)(methodName)(state)(stateActiveTraitMethods(state)) != None && allConstrainedMethodsAvailable(traitName)(argument)(rest)(state)

let recursive checkConstrainedBoundary constraints state =
    match constraints with
        | [] -> None
        | constraint :: rest ->
            match state
            |> stateTraitEnvironment
            |> resolveTraitEvidence(constraint) with
                | TraitEvidenceResolution { plan = Some(plan), error = None } ->
                    if constrainedPlanAvailable(plan)(state)
                    then checkConstrainedBoundary(rest)(state)
                    else
                        constraint.typeArguments
                        |> traitEvidenceSubject
                        |> MissingCoreTraitEvidence(constraint.traitName)
                        |> Some
                | _ ->
                    constraint.typeArguments
                    |> traitEvidenceSubject
                    |> MissingCoreTraitEvidence(constraint.traitName)
                    |> Some

let adoptConstrainedPlan (plan: CoreConstrainedInference) state =
    match state
    |> withTypeSupply(plan.supply)
    |> mergeConstrainedSubstitution(plan.substitution) with
        | (adopted, None) -> (adopted, checkConstrainedBoundary(plan.boundaryRequirements)(adopted))
        | failed -> failed

let restoreConstrainedBindingState outer lowered =
    match lowered with
        | LoweredCoreValue { state = loweredState } ->
            lowered with state = withStateActiveTraitMethods(stateActiveTraitMethods(outer))(loweredState)

// A type with every arrow's capability row dropped. Lowering's own inference gives a lambda no row,
// and a function type without one unifies only with an empty closed row, so a constrained plan's
// type, inferred with rows (an external call's `{UnsafeFfi | a}`), is taken without them.
let recursive withoutCapabilityRows (semanticType: SemanticType) =
    match semanticType with
        | SemFunction(argument, result, _row) ->
            SemFunction(withoutCapabilityRows(argument))(withoutCapabilityRows(result))(None)
        | SemList(element) ->
            element
            |> withoutCapabilityRows
            |> SemList
        | SemPointer(element) ->
            element
            |> withoutCapabilityRows
            |> SemPointer
        | SemTuple(elements) ->
            elements
            |> withoutCapabilityRowsIn
            |> SemTuple
        | SemNamed(symbol, name, arguments) ->
            arguments
            |> withoutCapabilityRowsIn
            |> SemNamed(symbol)(name)
        | other -> other
and withoutCapabilityRowsIn (types: List(SemanticType)) =
    match types with
        | [] -> []
        | head :: rest -> withoutCapabilityRows(head) :: withoutCapabilityRowsIn(rest)

let finalizeConstrainedScheme (scheme: TypeScheme) outerBindings state =
    (let resolved =
        scheme.body
        |> resolveType(state)
        |> withoutCapabilityRows
    in
        let requirements =
            map((given (constraint: TraitConstraint) ->
                constraint with typeArguments = map(resolveType(state))(constraint.typeArguments)))(scheme.constraints)
        in
            let generalized = generalize(pendingOperatorScheme(state) :: resolvedBindingSchemes(outerBindings)(state))(resolved)(requirements)
            in generalized with constraints = requirements)

let constrainedPlanRequirements (plan: CoreConstrainedInference) =
    (let scheme = plan.scheme
    in scheme.constraints)

let constrainedPlanSourceType (plan: CoreConstrainedInference) =
    (let scheme = plan.scheme
    in withoutCapabilityRows(scheme.body))

let lowerConstrainedBindingValue name (plan: CoreConstrainedInference) value lower state =
    match adoptConstrainedPlan(plan)(state) with
        | (failed, Some(error)) -> failure(failed)(error)
        | (adopted, None) ->
            match adopted
            |> stateTraitEnvironment
            |> coreConstrainedLayout(constrainedPlanRequirements(plan)) with
                | CoreConstrainedLayout { error = Some(error) } ->
                    plan
                    |> constrainedPlanSourceType
                    |> UnsupportedCoreTraitDispatch(error)
                    |> failure(adopted)
                | CoreConstrainedLayout { parameterTypes = parameters, methods = methods, error = None } ->
                    let rewritten =
                        adopted
                        |> stateTraitEnvironment
                        |> rewriteCoreConstrainedValue(value)(constrainedPlanRequirements(plan))
                    in
                        adopted
                        |> withStateActiveTraitMethods(state
                        |> stateActiveTraitMethods
                        |> append(methods))
                        |> withOnlyExpectedType(plan
                        |> constrainedPlanSourceType
                        |> coreConstrainedPhysicalType(parameters)
                        |> Some)
                        |> armSourceFunction(name)(rewritten)(false)
                        |> lower(rewritten)
                        |> restoreConstrainedBindingState(state)

let openForwardedConstrainedRecursive name value (plan: CoreConstrainedInference) lower (state: CoreLoweringState) =
    match adoptConstrainedPlan(plan)(state) with
        | (failed, Some(error)) -> failure(failed)(error)
        | (adopted, None) ->
            match adopted
            |> stateTraitEnvironment
            |> coreConstrainedLayout(constrainedPlanRequirements(plan)) with
                | CoreConstrainedLayout { error = Some(error) } ->
                    plan
                    |> constrainedPlanSourceType
                    |> UnsupportedCoreTraitDispatch(error)
                    |> failure(adopted)
                | CoreConstrainedLayout { parameterTypes = parameters, methods = methods, error = None } ->
                    let rewritten =
                        adopted
                        |> stateTraitEnvironment
                        |> rewriteCoreConstrainedValue(value)(constrainedPlanRequirements(plan))
                    in
                        let physical =
                            plan
                            |> constrainedPlanSourceType
                            |> coreConstrainedPhysicalType(parameters)
                        in
                            match []
                            |> prepareRecursiveGroup([(name, rewritten)])(adopted)
                            |> relabelSingleRecursive(adopted.nextLambdaId) with
                                | PreparedCoreRecursiveGroup { state = preparedState, members = member :: [], error = None } as prepared ->
                                    match bindType(member.semanticType)(physical)(preparedState) with
                                        | (failed, Some(error)) -> failure(failed)(error)
                                        | (pinned, None) ->
                                            let memberLower =
                                                given (body) ->
                                                    given (entered: CoreLoweringState) ->
                                                        entered
                                                        |> withStateActiveTraitMethods(state
                                                        |> stateActiveTraitMethods
                                                        |> append(methods))
                                                        |> lower(body)
                                            in
                                                match openPreparedRecursiveGroup([(name, rewritten)])(memberLower)(state.bindings)((prepared with state = pinned)) |> restoreConstrainedBindingState(state) with
                                                    | LoweredCoreValue { state = opened, error = None } as result ->
                                                        result with state = restoreBindings(replaceConstrainedScheme(name)(finalizeConstrainedScheme(plan.scheme)(state.bindings)(opened))(opened.bindings))(opened)
                                                    | failed -> failed
                                | PreparedCoreRecursiveGroup { state = failed, error = Some(error) } -> failure(failed)(error)
                                | _ -> failure(state)(CoreRecursiveBindingRequiresFunction(name))

let recursive constrainedDictionaryNames constraints index =
    match constraints with
        | [] -> []
        | _constraint :: rest -> "__trait_evidence_" + Ashes.Text.fromInt(index) :: constrainedDictionaryNames(rest)(index + 1)

let openConstrainedRecursive name value (plan: CoreConstrainedInference) lower state =
    match rewriteRecursiveTraitEvidence(name)(constrainedDictionaryNames(constrainedPlanRequirements(plan))(0))(value) with
        | Some(forwarded) -> openForwardedConstrainedRecursive(name)(forwarded)(plan)(lower)(state)
        | None ->
            plan
            |> constrainedPlanSourceType
            |> UnsupportedCoreTraitDispatch("recursive evidence forwarding")
            |> failure(state)

let openUnregisteredConstrainedRecursive name value annotation requirements lower (state: CoreLoweringState) =
    match constrainedBindingPlan(name)(true)(value)(annotation)(requirements)(state) with
        | Some(plan) -> openConstrainedRecursive(name)(value)(plan)(lower)(state)
        | None ->
            []
            |> prepareRecursiveGroup([(name, bindingLambdaValue(annotation)(value)(state))])(state)
            |> relabelSingleRecursive(state.nextLambdaId)
            |> openPreparedRecursiveGroup([(name, bindingLambdaValue(annotation)(value)(state))])(lower)(state.bindings)

let openPossiblyConstrainedRecursive name value annotation requirements lower state =
    match openUnregisteredConstrainedRecursive(name)(value)(annotation)(requirements)(lower)(state) with
        | LoweredCoreValue { state = opened, error = None } as result -> result with state = registerConcreteSource(name)(value)(true)(opened)
        | failed -> failed

let constrainedAliasBinding value (state: CoreLoweringState) =
    match stripExprAt(value) with
        | ExprVar(name) ->
            match lookupBinding(name)(state.bindings) with
                | Some(binding) ->
                    match bindingRequirements(binding) with
                        | [] -> None
                        | _ -> Some(binding)
                | None -> None
        | _ -> None

let lowerConstrainedAlias (binding: CoreBinding) state =
    (let scheme = binding.scheme
    in
        match state
        |> stateTraitEnvironment
        |> coreConstrainedLayout(bindingRequirements(binding)) with
            | CoreConstrainedLayout { parameterTypes = parameters, error = None } -> lowerUnconstrainedBoundVariable((binding with scheme = (binding.scheme with body = coreConstrainedPhysicalType(parameters)(scheme.body), constraints = []), closedScheme = false))(state)
            | CoreConstrainedLayout { error = Some(error) } ->
                scheme.body
                |> UnsupportedCoreTraitDispatch(error)
                |> failure(state))

let storedConstrainedScheme provisional scheme outerBindings (state: CoreLoweringState) =
    if provisional
    then finalizeConstrainedScheme(scheme)(outerBindings)(state)
    else applySchemeSubstitutionMap(state.substitution)(scheme)

let lowerConstrainedNestedBody name provisional scheme outerBindings lower body (state: CoreLoweringState) =
    (let logical = storedConstrainedScheme(provisional)(scheme)(outerBindings)(state)
    in
        state
        |> restoreBindings(replaceConstrainedScheme(name)(logical)(state.bindings))
        |> lower(body))

let lowerPossiblyConstrainedLet name value body annotation requirements lower (state: CoreLoweringState) =
    match constrainedAliasBinding(value)(state) with
        | Some(binding) ->
            lowerArenaBracketedNestedLetWith(name)(value)(body)(lowerConstrainedNestedBody(name)(false)(binding.scheme)(state.bindings)(lower))(given (_value) ->
                given (entered) -> lowerConstrainedAlias(binding)(entered))(state.bindings)(state)
        | None ->
            match constrainedBindingPlan(name)(false)(value)(annotation)(requirements)(state) with
                | Some(plan) ->
                    lowerArenaBracketedNestedLetWith(name)(value)(body)(lowerConstrainedNestedBody(name)(true)(plan.scheme)(state.bindings)(lower))(given (source) ->
                        given (entered) -> lowerConstrainedBindingValue(name)(plan)(source)(lower)(entered))(state.bindings)(state)
                | None ->
                    match annotatedValueType(annotation)(value)(state) with
                        | Some(annotationType) -> lowerAnnotatedLet(name)(value)(body)(Some(annotationType))(lower)(state)
                        | None ->
                            lowerLet(name)(bindingLambdaValue(annotation)(value)(state))(body)(lower)(state)

let lowerPossiblyConstrainedLetRecursive name value body annotation requirements lower (state: CoreLoweringState) =
    match constrainedBindingPlan(name)(true)(value)(annotation)(requirements)(state) with
        | Some(plan) ->
            match openConstrainedRecursive(name)(value)(plan)(lower)(state) with
                | LoweredCoreValue { state = opened, error = None } ->
                    opened
                    |> lower(body)
                    |> restoreLoweredBindings(state.bindings)
                | failed -> failed
        | None ->
            lowerLetRecursive(name)(bindingLambdaValue(annotation)(value)(state))(body)(annotation)(lower)(state)

// Whether a lambda body names a resource binding of the enclosing scope other than its own
// parameter.
let recursive bodyNamesOuterResource (parameter: Str) (body: Expr) (bindings: List(CoreBinding)) (state: CoreLoweringState) =
    match bindings with
        | [] -> false
        | CoreBinding { name = name, location = CoreLocal(_slot), ownedRead = true } :: rest ->
            if name != parameter && exprMentionsName(name)(body)
            then
                match resourceBindingOf(name)(state) with
                    | Some(_resource) -> true
                    | None -> bodyNamesOuterResource(parameter)(body)(rest)(state)
            else bodyNamesOuterResource(parameter)(body)(rest)(state)
        | _binding :: rest -> bodyNamesOuterResource(parameter)(body)(rest)(state)

// A pipeline stage written as a lambda literal over a resource of the enclosing scope,
// `Unit |> (given (_) -> Ashes.IO.File.close(fh)) |> (given (_) -> Ashes.IO.File.readChunk(fh)(4))`,
// is lowered as the `let` it means, and so is every lambda stage after it in the same pipeline:
// `argument |> (given (p) -> body)` evaluates the argument and then the body, so the earlier
// stages' closes and moves are recorded before a later stage's uses of the same resource are
// checked (stage 0 checks a stage closure's captured resources at its application for the same
// reason), and each parameter takes its argument's type. A stage that awaits keeps its closure,
// and so does one whose argument is itself a lambda, which a `let` would generalize.
let recursive resourcePipelineStageLet (function: Expr) (argument: Expr) (layout: Int) (state: CoreLoweringState) =
    if layout != callArgumentsPipe
    then None
    else
        match (stripExprAt(function), stripExprAt(argument)) with
            | (ExprLambda(_parameter, _body, _annotation), ExprLambda(_innerParameter, _innerBody, _innerAnnotation)) -> None
            | (ExprLambda(parameter, body, annotation), strippedArgument) ->
                if exprContainsAwait(body) == false && (bodyNamesOuterResource(parameter)(body)(state.bindings)(state) || followsResourcePipelineStage(strippedArgument)(state))
                then
                    []
                    |> ExprLet(parameter)(argument)(body)([])(annotation)
                    |> Some
                else None
            | _ -> None
and followsResourcePipelineStage (argument: Expr) (state: CoreLoweringState) =
    match argument with
        | ExprCall(function, inner, _whitespace, layout) ->
            match resourcePipelineStageLet(function)(inner)(layout)(state) with
                | Some(_stageLet) -> true
                | None -> false
        | _ -> false

let lowerCoreDispatch expression lowerCore state =
    match expression with
        | ExprAt(span, inner) ->
            match state with
                | CoreLoweringState { currentSpan = previous } ->
                    match lowerCore(inner)((state with currentSpan = Some(span))) with
                        | LoweredCoreValue { state = innerState, temp = temp, semanticType = semanticType, error = error } ->
                            let restored = innerState with currentSpan = previous
                            in
                                LoweredCoreValue(
                                    state = restored,
                                    temp = temp,
                                    semanticType = semanticType,
                                    error = error
                                )
        | ExprNode(node, inner) ->
            match state with
                | CoreLoweringState { currentNode = previous } ->
                    match lowerCore(inner)((state with currentNode = Some(node))) with
                        | LoweredCoreValue { state = innerState, temp = temp, semanticType = semanticType, error = error } ->
                            let restored = innerState with currentNode = previous
                            in
                                LoweredCoreValue(
                                    state = restored,
                                    temp = temp,
                                    semanticType = semanticType,
                                    error = error
                                )
        | ExprInt(value) ->
            lowerConstant(given (target) -> LoadConstInt(target)(value))(SemInt)(state)
        | ExprBigInt(digits) -> lowerCoreBigInt(digits)(state)
        | ExprUInt(value, bits, _raw) ->
            lowerConstant(given (target) -> LoadConstInt(target)(value))(SemUInt(bits))(state)
        | ExprFloat(value, _raw) ->
            lowerConstant(given (target) -> LoadConstFloat(target)(value))(SemFloat)(state)
        | ExprString(value) -> lowerString(value)(state)
        | ExprRune(value) ->
            lowerConstant(given (target) -> LoadConstInt(target)(value))(SemRune)(state)
        | ExprBool(value) ->
            lowerConstant(given (target) -> LoadConstBool(target)(value))(SemBool)(state)
        | ExprAdd(left, right) -> lowerCoreBinary(CoreAddOperator)(left)(right)(lowerCore)(state)
        | ExprSubtract(left, right) -> lowerCoreBinary(CoreSubtractOperator)(left)(right)(lowerCore)(state)
        | ExprMultiply(left, right) -> lowerCoreBinary(CoreMultiplyOperator)(left)(right)(lowerCore)(state)
        | ExprDivide(left, right) -> lowerCoreBinary(CoreDivideOperator)(left)(right)(lowerCore)(state)
        | ExprModulo(left, right) -> lowerCoreBinary(CoreModuloOperator)(left)(right)(lowerCore)(state)
        | ExprBitwiseAnd(left, right) -> lowerCoreBinary(CoreBitwiseAndOperator)(left)(right)(lowerCore)(state)
        | ExprBitwiseOr(left, right) -> lowerCoreBinary(CoreBitwiseOrOperator)(left)(right)(lowerCore)(state)
        | ExprBitwiseXor(left, right) -> lowerCoreBinary(CoreBitwiseXorOperator)(left)(right)(lowerCore)(state)
        | ExprShiftLeft(left, right) -> lowerCoreBinary(CoreShiftLeftOperator)(left)(right)(lowerCore)(state)
        | ExprShiftRight(left, right) -> lowerCoreBinary(CoreShiftRightOperator)(left)(right)(lowerCore)(state)
        | ExprBitwiseNot(operand) -> lowerCoreBitwiseNot(operand)(lowerCore)(state)
        | ExprLogicalNot(operand) -> lowerCoreLogicalNot(operand)(lowerCore)(state)
        | ExprLogicalAnd(left, right) -> lowerIf(left)(right)(ExprBool(false))(lowerCore)(state)
        | ExprLogicalOr(left, right) -> lowerIf(left)(ExprBool(true))(right)(lowerCore)(state)
        | ExprGreaterThan(left, right) -> lowerCoreBinary(CoreGreaterOperator)(left)(right)(lowerCore)(state)
        | ExprGreaterOrEqual(left, right) -> lowerCoreBinary(CoreGreaterOrEqualOperator)(left)(right)(lowerCore)(state)
        | ExprLessThan(left, right) -> lowerCoreBinary(CoreLessOperator)(left)(right)(lowerCore)(state)
        | ExprLessOrEqual(left, right) -> lowerCoreBinary(CoreLessOrEqualOperator)(left)(right)(lowerCore)(state)
        | ExprEqual(left, right) -> lowerCoreBinary(CoreEqualOperator)(left)(right)(lowerCore)(state)
        | ExprNotEqual(left, right) -> lowerCoreBinary(CoreNotEqualOperator)(left)(right)(lowerCore)(state)
        | ExprVar(name) -> lowerPossiblyConstrainedVariable(name)(lowerCore)(state)
        | ExprQualifiedVar(receiverName, fieldName) ->
            match operationEtaExpansion(receiverName)(fieldName)(state) with
                | Some(lambda) -> lowerSynthesized(lowerCore)(lambda)(state)
                | None ->
                    match shippedTraitMethodReference(receiverName)(fieldName) with
                        | Some(etaLambda) -> lowerSynthesized(lowerCore)(etaLambda)(state)
                        | None ->
                            match userTraitMethodReference(receiverName)(fieldName)(state) with
                                | Some(etaLambda) -> lowerSynthesized(lowerCore)(etaLambda)(state)
                                | None -> lowerQualified(receiverName)(fieldName)(lowerCore)(state)
        | ExprLet(name, value, body, _parameters, annotation, requirements) -> lowerPossiblyConstrainedLet(name)(value)(body)(annotation)(requirements)(lowerCore)(state)
        | ExprLetRecursive(name, value, body, _parameters, annotation, requirements) -> lowerPossiblyConstrainedLetRecursive(name)(value)(body)(annotation)(requirements)(lowerCore)(state)
        | ExprIf(condition, thenBranch, elseBranch) -> lowerIf(condition)(thenBranch)(elseBranch)(lowerCore)(state)
        | ExprLambda(parameter, body, annotation) ->
            lowerLambda(parameter)(body)(annotation)(statePendingStackClosure(state))(lowerCore)(state)
        | ExprCall(function, argument, _whitespace, layout) ->
            match resourcePipelineStageLet(function)(argument)(layout)(state) with
                | Some(stageLet) -> lowerCore(stageLet)(state)
                | None ->
                    match traitOperatorCall(function)(argument) with
                        | Some((operator, left, right)) -> lowerCoreBinary(operator)(left)(right)(lowerCore)(state)
                        | None ->
                            match traitCompareCall(expression)(function)(argument)(state) with
                                | Some((left, right)) -> lowerCoreTraitCompare(left)(right)(lowerCore)(state)
                                | None ->
                                    match traitUnaryMethodCall(function) with
                                        | Some((traitName, methodName, resultType)) -> lowerTraitUnaryMethodCall(traitName)(methodName)(resultType)(argument)(lowerCore)(state)
                                        | None ->
                                            match shippedTraitUnaryOperator(function) with
                                                | Some("negate") -> lowerCoreBinary(CoreSubtractOperator)(ExprInt(0))(argument)(lowerCore)(state)
                                                | Some("not") -> lowerCoreLogicalNot(argument)(lowerCore)(state)
                                                | Some("bitwiseNot") -> lowerCoreBitwiseNot(argument)(lowerCore)(state)
                                                | Some(_default) -> lowerTraitDefaultCall(argument)(lowerCore)(state)
                                                | None ->
                                                    match userTraitMethodCall(expression)(state) with
                                                        | Some((traitName, methodName, scheme, arguments)) -> lowerUserTraitMethodCall(traitName)(methodName)(scheme)(arguments)(lowerCore)(state)
                                                        | None ->
                                                            state
                                                            |> lowerCallExpression(expression)(function)(argument)(lowerCore)
                                                            |> describeCallArity(expression)
        | ExprTuple(elements) -> lowerTuple(elements)(lowerCore)(state)
        | ExprList(elements, _isMultiline) -> lowerListLiteral(elements)(lowerCore)(state)
        | ExprCons(head, tail) ->
            if tmcCandidateCons(tail)(state)
            then lowerConsTmc(head)(tail)(lowerCore)(state)
            else lowerCons(head)(tail)(lowerCore)(state)
        | ExprRecord(name, fields, _isMultiline) -> lowerRecord(name)(fields)(lowerCore)(state)
        | ExprRecordUpdate(target, fields) -> lowerRecordUpdate(target)(fields)(lowerCore)(state)
        | ExprMatch(value, cases, _position) ->
            state
            |> lowerMatch(value)(cases)(lowerCore)
            |> settleMatchScopedTakenOver(value)
        | ExprPerform(operation) -> lowerPerform(operation)(lowerCore)(state)
        | ExprHandle(body, arms) -> lowerHandle(body)(arms)(lowerCore)(state)
        | ExprResultPipe(left, right) -> lowerResultPipe(left)(right)(lowerCore)(state)
        | ExprResultMapErrorPipe(left, right) -> lowerResultMapErrorPipe(left)(right)(lowerCore)(state)
        | ExprAwait(operand) -> lowerAwait(operand)(lowerCore)(state)
        | unsupported ->
            unsupported
            |> expressionName
            |> UnsupportedCoreLoweringExpression
            |> failure(state)

// The context's request reaches the dispatch trimmed to what this expression kind forwards; an
// expected type a kind does not forward is unified with its result afterwards. The result state
// never carries a request.
// The trait evidence a call passes ahead of its source arguments, stage 0's
// `SummaryParameterIndex`, which the policy always follows.
let recursive flowEvidenceArgumentCount (arguments: List(Expr)) =
    match arguments with
        | [] -> 0
        | argument :: rest ->
            match unspanArgument(argument) with
                | ExprVar(name) ->
                    if Ashes.Text.startsWith(name)("__trait_evidence_")
                    then 1 + flowEvidenceArgumentCount(rest)
                    else 0
                | _ -> 0

let recursive flowTrueFlags (count: Int) =
    if count == 0
    then []
    else true :: flowTrueFlags(count - 1)

// Stage 0's `SummaryFlowingArguments`: the arguments a callee's result may reach, by the
// program's result-reach summary of the callee, when it is unpoisoned and the call applies one
// argument per parameter past the trait evidence.
let flowSummaryFlags (root: Expr) (arguments: List(Expr)) (state: CoreLoweringState) =
    match unspanArgument(root) with
        | ExprVar(callee) ->
            match calleeSummaryOf(callee)(state) with
                | Some(ReachSummary { function = ReachFunction { parameters = parameters }, reach = ResultReachState { isPoisoned = false } } as summary) ->
                    let evidence = flowEvidenceArgumentCount(arguments)
                    in
                        if coreListLength(arguments) - evidence == length(parameters)
                        then
                            state
                            |> preparedReachOfSummary(summary)
                            |> preparedCallReachFlags
                            |> append(flowTrueFlags(evidence))
                            |> Some
                        else None
                | _ -> None
        | _ -> None

// Stage 0's `IsMaterializedSpecializationFieldType`: the field types a specialization's
// constructor site relocates into its region rather than storing by reference.
let materializedSpecializationFieldType (fieldType: SemanticType) (accumulatorType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(fieldType) with
        | SemString -> true
        | SemBytes -> true
        | SemTuple(elements) -> allCopyTypeElements(elements)
        | SemList(element) -> resolveType(state)(element) == SemString
        | SemNamed(_symbolId, _typeName, _arguments) as named ->
            !specializationFieldIsAccumulatorSelf(named)(resolveType(state)(accumulatorType)) && specializationFieldRelocatable(named)(state)
        | _ -> false

// Per argument of a call routed to a specialization rebuilding a named accumulator: an argument
// the specialization copies when it stores it holds nothing the result holds; the accumulator,
// the last, and any other argument flow (stage 0's `ReuseSpecializationFlowingArguments`).
let recursive flowSpecializationFlags (functionType: SemanticType) (remaining: Int) (accumulatorType: SemanticType) (instantiation: List((Int, SemanticType))) (state: CoreLoweringState) =
    match functionType with
        | SemFunction(argumentType, resultType, _effects) ->
            if remaining <= 1
            then [true]
            else
                (materializedSpecializationFieldType(applySubstitution(instantiation)(argumentType))(accumulatorType)(state) == false) :: flowSpecializationFlags(resultType)(remaining - 1)(accumulatorType)(instantiation)(state)
        | _ -> []

// Stage 0's `MatchTypeVariables`: binds each type variable of `pattern` to what stands at its
// position in `actual`.
let recursive matchTypeVariables (pattern: SemanticType) (actual: SemanticType) (instantiation: List((Int, SemanticType))) =
    match (pattern, actual) with
        | (SemVariable(variableId), bound) -> (variableId, bound) :: instantiation
        | (SemNamed(symbolId, name, arguments), SemNamed(actualSymbolId, actualName, actualArguments)) ->
            if symbolId == actualSymbolId && name == actualName && length(arguments) == length(actualArguments)
            then matchTypeVariablesAll(arguments)(actualArguments)(instantiation)
            else instantiation
        | (SemList(element), SemList(actualElement)) -> matchTypeVariables(element)(actualElement)(instantiation)
        | (SemTuple(elements), SemTuple(actualElements)) ->
            if length(elements) == length(actualElements)
            then matchTypeVariablesAll(elements)(actualElements)(instantiation)
            else instantiation
        | (SemFunction(argument, result, _effects), SemFunction(actualArgument, actualResult, _actualEffects)) ->
            instantiation
            |> matchTypeVariables(argument)(actualArgument)
            |> matchTypeVariables(result)(actualResult)
        | _ -> instantiation
and matchTypeVariablesAll (patterns: List(SemanticType)) (actuals: List(SemanticType)) (instantiation: List((Int, SemanticType))) =
    match (patterns, actuals) with
        | (pattern :: restPatterns, actual :: restActuals) ->
            instantiation
            |> matchTypeVariables(pattern)(actual)
            |> matchTypeVariablesAll(restPatterns)(restActuals)
        | _ -> instantiation

// The callee's scheme leaves the collection's element types as variables; the call's own
// instantiation is read off the accumulator argument's type, a loop parameter's binding.
let specializationCallInstantiation (accumulatorParameterType: SemanticType) (arguments: List(Expr)) (state: CoreLoweringState) =
    match lastArgument(arguments) with
        | Some(accumulator) ->
            match unspanArgument(accumulator) with
                | ExprVar(name) ->
                    match lookupBinding(name)(state.bindings) with
                        | Some(CoreBinding { scheme = TypeScheme { body = actual } }) ->
                            matchTypeVariables(resolveType(state)(accumulatorParameterType))(resolveType(state)(actual))([])
                        | _ -> []
                | _ -> []
        | None -> []

// Stage 0's `IsPersistentCollectionOperation`: the standard library's map and hash map
// operations, the functions the in-place collection specialization keeps a persistent region
// for; only they store their inputs at constructor sites alone, where the specialization copies
// them.
let persistentCollectionOperation (callee: Str) = Ashes.Text.startsWith(callee)("Ashes_Collection_Map_") || Ashes.Text.startsWith(callee)("Ashes_Collection_HashMap_")

// Stage 0's `FlowingCallArguments`: a call routed to a reuse specialization of a persistent
// collection's operation rebuilding a named accumulator flows the arguments the specialization
// stores by reference; any other call flows what its callee's summary admits.
let flowCallFlagsOf (call: Expr) (state: CoreLoweringState) =
    match collectCallSpine(call) with
        | CoreCallSpine { root = root, arguments = arguments } as spine ->
            match reuseSpecializedCallOf(spine)(state) with
                | Some((callee, _parameter, _parameters, _value, SemNamed(_symbolId, _typeName, _typeArguments) as accumulatorType, functionType, _callArguments)) ->
                    if persistentCollectionOperation(callee)
                    then
                        state
                        |> flowSpecializationFlags(resolveType(state)(functionType))(coreListLength(arguments))(accumulatorType)(specializationCallInstantiation(accumulatorType)(arguments)(state))
                        |> Some
                    else flowSummaryFlags(root)(arguments)(state)
                | _ -> flowSummaryFlags(root)(arguments)(state)

let recursive loopCallFlowOf (nodes: List(Expr)) (table: List((Str, Int, Str, List(Bool)))) (state: CoreLoweringState) =
    match nodes with
        | [] -> table
        | (ExprCall(_function, _argument, _sugar, _layout) as call) :: rest ->
            match (flowCallKeyOf(call), flowCallFlagsOf(call)(state)) with
                | (Some((callee, count, accumulator)), Some(flags)) -> loopCallFlowOf(rest)((callee, count, accumulator, flags) :: table)(state)
                | _ -> loopCallFlowOf(rest)(table)(state)
        | _node :: rest -> loopCallFlowOf(rest)(table)(state)

// Stage 0's `CreateFlowCallPolicy` and `RecomputeTcoParameterFlow` once the specialization scan
// has run: the loop's call policy over its body's calls, and its self-call parameter flow again
// under that policy, which the placements after the body and the back-edge rules read.
let resolveLoopCallFlow (state: CoreLoweringState) =
    match stateTcoLoop(state) with
        | Some(CoreTcoLoop { callFlowPending = true, body = body, selfName = selfName, parameterNames = parameters } as loop) ->
            let table =
                match flowNodes([body])([]) with
                    | Some(nodes) -> loopCallFlowOf(nodes)([])(state)
                    | None -> []
            in withStateTcoLoop(Some((loop with callFlow = table, callFlowPending = false, selfCallParameterFlow = tcoSelfCallParameterFlow(selfName)(parameters)(table)(body), selfCallSpineFlow = tcoSelfCallSpineFlow(selfName)(parameters)(table)(body))))(state)
        | _ -> state

// Whether an expression is a bare reference to a dictionary-passing binding. Its dictionaries are
// built where it is read, so the type its context expects has to reach the read itself: unifying
// afterwards, as every other variable read does, is too late to choose the evidence.
let constrainedReference (expression: Expr) (state: CoreLoweringState) =
    match stripExprAt(expression) with
        | ExprVar(name) ->
            match lookupBinding(name)(state.bindings) with
                | Some(binding) -> bindingRequirements(binding) != []
                | None ->
                    match state
                    |> stateTopLevelFunctionRefIndex
                    |> lookupTopLevelFunctionRef(name) with
                        | Some((_label, TypeScheme { constraints = [] })) -> false
                        | Some(_constrained) -> true
                        | None -> false
        | _ -> false

let requestAtConstrainedReference (expression: Expr) (request: ConsumerRequest) (state: CoreLoweringState) (dispatched: ConsumerRequest) =
    match request.expectedType with
        | None -> dispatched
        | Some(_expected) ->
            if constrainedReference(expression)(state)
            then dispatched with expectedType = request.expectedType
            else dispatched

// A `let?` is lowered as the match it means, under the whole request: its body stands where the
// `let?` does, as stage 0's `LowerLetResult` lowers it.
let recursive lowerCore expression state =
    match (expression, state
    |> resolveLoopCallFlow
    |> consumerRequestOf) with
        | (ExprLetResult(name, value, body), _request) ->
            lowerCore(letResultMatch(name)(value)(body))(state)
        | (_expression, request) ->
            match if isEmptyConsumerRequest(request)
            then request
            else
                request
                |> dispatchRequest(expression)
                |> requestAtConstrainedReference(expression)(request)(state) with
                | dispatched ->
                    state
                    |> resolveLoopCallFlow
                    |> withConsumerRequest(dispatched)
                    |> lowerCoreDispatch(expression)(lowerCore)
                    |> withLoweredConsumerRequest(emptyConsumerRequest)
                    |> unifyUnforwardedExpectedType(expression)(request)
