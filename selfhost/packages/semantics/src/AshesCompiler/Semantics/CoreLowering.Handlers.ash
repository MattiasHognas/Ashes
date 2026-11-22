// Capability operations: perform, handle, operation arms and result pipes. A part of
// `AshesCompiler.Semantics.CoreLowering`.

import Ashes.Collection.List.append
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.Pattern
import AshesCompiler.Frontend.Syntax.callArgumentsInline
import AshesCompiler.Semantics.CallOwnership
import AshesCompiler.Semantics.CoreCapabilityLowering
import AshesCompiler.Semantics.CoreResultPipeLowering
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.TypeSchemes
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.CoreLowering.LoweringState
import AshesCompiler.Semantics.CoreLowering.Emit
import AshesCompiler.Semantics.CoreLowering.Owners
import AshesCompiler.Semantics.CoreLowering.CopyPlans
import AshesCompiler.Semantics.CoreLowering.TcoReset
import AshesCompiler.Semantics.CoreLowering.ResultHandOver
import AshesCompiler.Semantics.CoreLowering.CallArguments
import AshesCompiler.Semantics.CoreLowering.CallResults
import AshesCompiler.Semantics.CoreLowering.Matches
import AshesCompiler.Semantics.CoreLowering.RecursiveGroups
import AshesCompiler.Semantics.CoreLowering.Aggregates
import AshesCompiler.Semantics.CoreLowering.Constructors
import AshesCompiler.Semantics.CoreLowering.Records
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.recordedSettledTypeAt
import AshesCompiler.Semantics.NodeIdentity.nodeIdentityOf
export (
    value lowerPerform,
    value operationEtaExpansion,
    value lowerHandle,
    value lowerResultPipe,
    value lowerResultMapErrorPipe,
    value letResultMatch,
    value expressionName,
    value unifyOptionalExpectedResult,
)

let recursive emitSnapshotGlobals currentK globalCount frameTemp state =
    if currentK >= globalCount
    then state
    else
        match freshTemp(state) with
            | FreshTemp { state = nextState, temp = snapTemp } ->
                nextState
                |> emit(LoadCapabilityHandler(snapTemp)(currentK))
                |> emit(StoreMemOffset(frameTemp)(currentK * 8)(snapTemp))
                |> emitSnapshotGlobals(currentK + 1)(globalCount)(frameTemp)

// After a dynamic perform's arm closure returns, checks the well-known "pending post" capability
// global (index globalCount, the same slot array StoreCapabilityHandler/LoadCapabilityHandler
// already manage) a one-shot-resume arm stores its post continuation into
// (buildOperationArmClosure's one-shot-let path). When non-zero, conses it onto the handler
// frame's posts list (the same 16-byte head/tail cell shape allocateListCell already builds for
// ordinary list cons), counts it live, and resets the register to zero so a later, unrelated
// perform site never mistakes a stale value for its own post.
let collectCapabilityPost globalCount capabilityIndex state =
    match freshTemp(state) with
        | FreshTemp { state = regState, temp = postRegTemp } ->
            match regState
            |> emit(LoadCapabilityHandler(postRegTemp)(globalCount))
            |> freshTemp with
                | FreshTemp { state = zeroState, temp = zeroTemp } ->
                    match zeroState
                    |> emit(LoadConstInt(zeroTemp)(0))
                    |> freshTemp with
                        | FreshTemp { state = cmpState, temp = hasPostTemp } ->
                            match freshLabel("capability_post_skip")(cmpState) with
                                | FreshLabel { state = labeledState, label = skipLabel } ->
                                    let checkedState =
                                        labeledState
                                        |> emit(CmpIntNe(hasPostTemp)(postRegTemp)(zeroTemp))
                                        |> emit(JumpIfFalse(hasPostTemp)(skipLabel))
                                    in
                                        match freshTemp(checkedState) with
                                            | FreshTemp { state = frameState, temp = frameTemp } ->
                                                let withFrame =
                                                    emit(LoadCapabilityHandler(frameTemp)(capabilityIndex))(frameState)
                                                in
                                                    match freshTemp(withFrame) with
                                                        | FreshTemp { state = headAddrState, temp = postsHeadAddrTemp } ->
                                                            let withHeadAddr =
                                                                emit(LoadMemOffset(postsHeadAddrTemp)(frameTemp)(globalCount * 8))(headAddrState)
                                                            in
                                                                match freshTemp(withHeadAddr) with
                                                                    | FreshTemp { state = prevHeadState, temp = previousHeadTemp } ->
                                                                        let withPrevHead =
                                                                            emit(LoadMemOffset(previousHeadTemp)(postsHeadAddrTemp)(0))(prevHeadState)
                                                                        in
                                                                            match allocateListCell(postRegTemp)(previousHeadTemp)(SemNever)(false)(withPrevHead) with
                                                                                | LoweredCoreValue { state = failedCellState, error = Some(error) } -> failure(failedCellState)(error)
                                                                                | LoweredCoreValue { state = cellState, temp = cellTemp, error = None } ->
                                                                                    cellState
                                                                                    |> emit(StoreMemOffset(postsHeadAddrTemp)(0)(cellTemp))
                                                                                    |> emit(StoreCapabilityHandler(globalCount)(zeroTemp))
                                                                                    |> emitLivePostsAdjust(1)
                                                                                    |> emit(Label(skipLabel))
                                                                                    |> success(-1)(SemNever)

let recursive curriedCallType (argumentTypes: List(SemanticType)) (resultType: SemanticType) =
    match argumentTypes with
        | [] -> resultType
        | argumentType :: rest ->
            SemFunction(argumentType)(curriedCallType(rest)(resultType))(None)

let recursive findCapabilityOperationScheme (capName: Str) (opName: Str) (schemes: List((Str, Str, TypeScheme))) =
    match schemes with
        | [] -> None
        | (candidateCap, candidateOp, scheme) :: rest ->
            if candidateCap == capName && candidateOp == opName
            then Some(scheme)
            else findCapabilityOperationScheme(capName)(opName)(rest)

let recursive signatureArity (semanticType: SemanticType) =
    match semanticType with
        | SemFunction(_argument, result, _row) -> 1 + signatureArity(result)
        | _ -> 0

let recursive operationEtaCall (root: Expr) (parameters: List(Str)) =
    match parameters with
        | [] -> root
        | parameter :: rest ->
            operationEtaCall(ExprCall(root)(ExprVar(parameter))(false)(callArgumentsInline))(rest)

let recursive operationEtaLambda (parameters: List(Str)) (body: Expr) =
    match parameters with
        | [] -> body
        | parameter :: rest ->
            ExprLambda(parameter)(operationEtaLambda(rest)(body))(None)

let recursive operationEtaParameters (prefix: Str) (index: Int) (count: Int) =
    if index >= count
    then []
    else prefix + Ashes.Text.fromInt(index) :: operationEtaParameters(prefix)(index + 1)(count)

// Stage 0's `BuildOperationEtaLambda`: a first-class reference to an operation with a declared
// signature, `Clock.now`, is `given a -> Clock.now(a)`, curried to the signature's arity, so the
// operation is performed where the value is applied. None for anything else.
let operationEtaExpansion (capName: Str) (opName: Str) (state: CoreLoweringState) =
    match state
    |> stateCapabilityLayouts
    |> findCapabilityLayout(capName) with
        | None -> None
        | Some(_layout) ->
            match state
            |> stateCapabilityOperationSchemes
            |> findCapabilityOperationScheme(capName)(opName) with
                | Some(TypeScheme { body = SemTuple(_instance :: operationType :: []) }) ->
                    let arity = signatureArity(operationType)
                    in
                        let parameters =
                            operationEtaParameters("__op_arg_" + Ashes.Text.fromInt(state.nextTemp) + "_")(0)(if arity < 1
                            then 1
                            else arity)
                        in
                            parameters
                            |> operationEtaCall(ExprQualifiedVar(capName)(opName))
                            |> operationEtaLambda(parameters)
                            |> Some
                | _ -> None

let recursive resolvedTypes (state: CoreLoweringState) (types: List(SemanticType)) =
    match types with
        | [] -> []
        | head :: rest -> resolveType(state)(head) :: resolvedTypes(state)(rest)

// A perform site's types: the operation's declared signature instantiated and unified with the
// lowered arguments' types gives the result type, the instantiated signature, and the type
// arguments of the capability instance the site performs (its scheme pairs the instance with the
// signature). An operation without a recorded scheme gets a fresh result variable, no signature
// and no type arguments.
let performSiteTypes (capName: Str) (opName: Str) (argumentTypes: List(SemanticType)) (state: CoreLoweringState) =
    match freshType(state) with
        | FreshType { state = resultState, semanticType = resultType } ->
            match state
            |> stateCapabilityOperationSchemes
            |> findCapabilityOperationScheme(capName)(opName) with
                | None -> (resultState, resultType, [], None, None)
                | Some(scheme) ->
                    match instantiate(scheme)(resultState.typeSupply) with
                        | InstantiationResult { semanticType = SemTuple(SemCapability(_instanceName, instanceArguments) :: operationType :: []), supply = nextSupply } ->
                            match resultState
                            |> withTypeSupply(nextSupply)
                            |> bindType(operationType)(curriedCallType(argumentTypes)(resultType)) with
                                | (failedState, Some(error)) -> (failedState, resultType, [], None, Some(error))
                                | (boundState, None) ->
                                    (boundState, resolveType(boundState)(resultType), resolvedTypes(boundState)(instanceArguments), operationType
                                    |> resolveType(boundState)
                                    |> Some, None)
                        | _ -> (resultState, resultType, [], None, None)

// A perform site whose result has a call copy-out adopted the arm's result (stage 0's
// `PerformResultOwnership.Adopt`): it owns a reference-counted value on both branches, newly
// produced for its consumer.
let markPerformResultAdopted (resultTemp: Int) (resultType: SemanticType) (state: CoreLoweringState) =
    match callCopyOutOf(resultType)(state) with
        | Some(_copyOut) -> markRuntimeTemp(resultTemp)(RuntimeNewlyProduced)(state)
        | None -> state

let recursive capabilityHandled (name: Str) (handled: List(Str)) =
    match handled with
        | [] -> false
        | candidate :: rest -> candidate == name || capabilityHandled(name)(rest)

let recursive anyAbstractType (types: List(SemanticType)) =
    match types with
        | [] -> false
        | SemVariable(_id) :: _rest -> true
        | SemParameter(_id, _name) :: _rest -> true
        | _concrete :: rest -> anyAbstractType(rest)

// Stage 0's `ResolveProvider`: the provider registered for the concrete instance a perform site
// performs, none while a type argument is still a variable.
let performSiteProvider (capName: Str) (instanceArguments: List(SemanticType)) (state: CoreLoweringState) =
    if anyAbstractType(instanceArguments)
    then None
    else
        state
        |> stateStaticProviders
        |> findStaticProviderExact(capName)(instanceArguments)

let bindProviderImplementation (implementationType: SemanticType) (operationType: Maybe(SemanticType)) (state: CoreLoweringState) =
    match operationType with
        | None -> (state, None)
        | Some(expected) -> bindType(implementationType)(expected)(state)

// Stage 0's `EmitStaticProviderCall`: the provider's implementation of the operation lowered
// against the operation's instantiated signature and applied to the arguments as an ordinary
// curried call, with no handler evidence involved.
let lowerStaticProviderCall (provider: CoreStaticProviderLayout) (capName: Str) (opName: Str) (argTemps: List(Int)) (resultType: SemanticType) (operationType: Maybe(SemanticType)) lower (state: CoreLoweringState) =
    match findProviderOperation(opName)(provider.operations) with
        | None ->
            opName
            |> CoreUnhandledCapabilityOperation(capName)
            |> failure(state)
        | Some(implementation) ->
            match state
            |> withOnlyExpectedType(operationType)
            |> lower(implementation)
            |> withLoweredConsumerRequest(consumerRequestOf(state)) with
                | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                | LoweredCoreValue { state = implementationState, temp = implementationTemp, semanticType = implementationType, error = None } ->
                    match bindProviderImplementation(implementationType)(operationType)(implementationState) with
                        | (failedState, Some(error)) -> failure(failedState)(error)
                        | (boundState, None) ->
                            match emitStaticProviderCall(provider)(opName)(implementationTemp)(boundState.nextTemp)(boundState.nextLocal)(argTemps)(resultType) with
                                | CoreCapabilityPerformEmission { instructions = callInstructions, nextTemp = endTemp, nextLocal = endLocal, resultTemp = callResultTemp } ->
                                    boundState
                                    |> emitInstructions(callInstructions)
                                    |> withNextTemp(endTemp)
                                    |> withNextLocal(endLocal)
                                    |> success(callResultTemp)(resolveType(boundState)(resultType))

// The evidence-based perform of an operation a handler satisfies.
let lowerDynamicPerform (layout: CoreCapabilityLayout) (capName: Str) (opName: Str) (opIndex: Int) (argTemps: List(Int)) (resType: SemanticType) (typedState: CoreLoweringState) =
    match typedState with
        | CoreLoweringState { programState = CoreProgramState { capabilityGlobalCount = globalCount } } ->
            match internString("Unhandled capability operation '" + capName + "." + opName + "'.")(typedState) with
                | StringInterning { state = internedState, label = panicLabel } ->
                    match internedState with
                        | CoreLoweringState { nextTemp = startTemp, nextLocal = startLocal } ->
                            match internedState
                            |> callCopyOutOf(resType)
                            |> emitDynamicPerform(capName)(opName)(layout.index)(opIndex)(globalCount)(startTemp)(startLocal)(argTemps)(resType)(panicLabel) with
                                | CoreCapabilityPerformEmission { instructions = performInstrs, nextTemp = endTemp, nextLocal = endLocal, resultTemp = resTemp, semanticType = resultSemType, error = None } ->
                                    let emittedState =
                                        internedState
                                        |> emitInstructions(performInstrs)
                                        |> withNextTemp(endTemp)
                                        |> withNextLocal(endLocal)
                                        |> markPerformResultAdopted(resTemp)(resultSemType)
                                    in
                                        match collectCapabilityPost(globalCount)(layout.index)(emittedState) with
                                            | LoweredCoreValue { state = failedPostState, error = Some(error) } -> failure(failedPostState)(error)
                                            | LoweredCoreValue { state = postCollectedState, error = None } -> success(resTemp)(resultSemType)(postCollectedState)
                                | _ ->
                                    opName
                                    |> CoreUnhandledCapabilityOperation(capName)
                                    |> failure(internedState)

let recursive bindInstanceArguments (arguments: List(SemanticType)) (providerArguments: List(SemanticType)) (state: CoreLoweringState) =
    match (arguments, providerArguments) with
        | ([], []) -> Some(state)
        | (argument :: rest, providerArgument :: providerRest) ->
            match bindType(argument)(providerArgument)(state) with
                | (_failed, Some(_error)) -> None
                | (bound, None) -> bindInstanceArguments(rest)(providerRest)(bound)
        | _ -> None

let resolvedOperationType (state: CoreLoweringState) (operationType: Maybe(SemanticType)) =
    match operationType with
        | Some(semanticType) ->
            semanticType
            |> resolveType(state)
            |> Some
        | None -> None

// Inside a concrete copy of a generic binding (stage 0 inlines such a binding at its concrete call
// site) every quantified type is concrete, so an instance still open at a perform site is one this
// lowering has not unified yet, such as an operation in a closure handed to a higher-order helper
// before the helper's other arguments are lowered. When the capability is not handled here and
// all its providers share one instance, the instance is that provider's: its type arguments are
// bound to the provider's and the call resolves to it. Anything else stays a dynamic perform.
let concreteCopyProvider (capName: Str) (instanceArguments: List(SemanticType)) (handled: Bool) (state: CoreLoweringState) =
    if handled || stateConcreteDepth(state) == 0 || !anyAbstractType(instanceArguments)
    then None
    else
        match state
        |> stateStaticProviders
        |> findStaticProvider(capName)([]) with
            | None -> None
            | Some(provider) ->
                match bindInstanceArguments(instanceArguments)(provider.typeArguments)(state) with
                    | Some(bound) -> Some((bound, provider))
                    | None -> None

// Stage 0's `LowerCapabilityOperationCallDispatch`, once the arguments pin the instance: an
// operation of a capability an enclosing `handle` discharges resolves through the handler's
// evidence, an operation with a provider for its concrete instance calls the provider directly,
// and both at once is an ambiguity the type checker reports.
let lowerCapabilityOperationCall (layout: CoreCapabilityLayout) (capName: Str) (opName: Str) (opIndex: Int) arguments lower (state: CoreLoweringState) =
    match lowerCoreValues(arguments)(lower)(state) with
        | LoweredCoreValues { state = argFailedState, error = Some(error) } -> failure(argFailedState)(error)
        | LoweredCoreValues { state = valuesState, temps = argTemps, semanticTypes = argTypes, error = None } ->
            match performSiteTypes(capName)(opName)(argTypes)(valuesState) with
                | (failedTypeState, _resType, _instance, _operationType, Some(error)) -> failure(failedTypeState)(error)
                | (typedState, resType, instanceArguments, operationType, None) ->
                    let handled =
                        typedState
                        |> stateHandledCapabilities
                        |> capabilityHandled(capName)
                    in
                        match performSiteProvider(capName)(instanceArguments)(typedState) with
                            | Some(provider) ->
                                if handled
                                then failure(typedState)(CoreAmbiguousCapabilitySatisfaction(capName))
                                else lowerStaticProviderCall(provider)(capName)(opName)(argTemps)(resType)(operationType)(lower)(typedState)
                            | None ->
                                match concreteCopyProvider(capName)(instanceArguments)(handled)(typedState) with
                                    | Some((boundState, provider)) ->
                                        lowerStaticProviderCall(provider)(capName)(opName)(argTemps)(resolveType(boundState)(resType))(resolvedOperationType(boundState)(operationType))(lower)(boundState)
                                    | None -> lowerDynamicPerform(layout)(capName)(opName)(opIndex)(argTemps)(resType)(typedState)

let lowerBuiltinStop argument lower state =
    match constructorLayout("Unit")(state) with
        | None -> failure(state)(UnknownLoweringBinding("Unit"))
        | Some(layout) ->
            let scheme = layout.scheme
            in
                match state
                |> lower(argument)
                |> unifyExpectedResult(scheme.body) with
                    | LoweredCoreValue { state = argState, error = None } ->
                        match freshTemp(argState) with
                            | FreshTemp { state = next, temp = target } ->
                                next
                                |> emit(RequestServerStop(target))
                                |> lowerConstructor(layout)([])(emptyConsumerRequest)(lower)
                    | failed -> failed

let lowerPerform operation lower state =
    match collectCallSpine(operation) with
        | CoreCallSpine { root = ExprQualifiedVar(capName, opName), arguments = arguments } ->
            if capName == "Stop"
            then
                if opName == "stop"
                then
                    match arguments with
                        | arg :: [] -> lowerBuiltinStop(arg)(lower)(state)
                        | _ ->
                            arguments
                            |> coreListLength
                            |> CoreBuiltinArityMismatch("Stop")("stop")(1)
                            |> failure(state)
                else failure(state)(UnknownLoweringBinding("Stop." + opName))
            else
                match state
                |> stateCapabilityLayouts
                |> findCapabilityLayout(capName) with
                    | Some(layout) ->
                        match findCapabilityOperationIndex(opName)(layout.operations) with
                            | Some(opIndex) -> lowerCapabilityOperationCall(layout)(capName)(opName)(opIndex)(arguments)(lower)(state)
                            | None ->
                                opName
                                |> CoreUnhandledCapabilityOperation(capName)
                                |> failure(state)
                    | None ->
                        opName
                        |> CoreUnhandledCapabilityOperation(capName)
                        |> failure(state)
        | _ -> failure(state)(PerformTargetNotCapabilityOperation("'perform' must be applied to a capability operation call."))

// An operation arm's body must call `resume` exactly once; only the tail-position form
// `resume(e)` (the arm body itself, unwrapped of span nodes) is supported so far — it rewrites to
// plain `e`, no continuation-splitting needed. `let x = resume(v) in body`, a match/if scrutinee
// resume, and every other position stage 0 also accepts are not yet ported (they need the
// one-shot post-resume continuation machinery `LowerHandleFoldPosts` builds, which selfhost
// doesn't have yet); an arm using one of those is rejected with UnsupportedOperationArmResume
// rather than silently producing wrong IR.
let recursive unspanForResumeCheck expression =
    match expression with
        | ExprAt(_span, inner) -> unspanForResumeCheck(inner)
        | ExprNode(_node, inner) -> unspanForResumeCheck(inner)
        | other -> other

let tailResumeArgument body =
    match unspanForResumeCheck(body) with
        | ExprCall(function, argument, _whitespace, _layout) ->
            match unspanForResumeCheck(function) with
                | ExprVar("resume") -> Some(argument)
                | _ -> None
        | _ -> None

// Handed to lowerPreparedRecursiveGroupWith wherever the real "body" is supplied by
// a continuation lower instead of a literal expression (the rest of a top-level program's items in
// lowerCoreProgramItems, or the rest of an operation arm's body in resolveOperationArmBody below)
// — the continuation lower ignores it. A bare int literal needs no environment/binding resolution,
// so it stays inert even if a future change accidentally lowers it before the continuation
// intercepts it.
let topLevelContinuationBody = ExprInt(0)

// Pure, no lowering: does `expr` reference the special `resume` marker anywhere, unshadowed.
// Reuses collectFree since it already tracks shadowing via `bound` — `resume` is never a real
// outer binding, so its presence in the free set means the expression calls or mentions it.
let exprReferencesResume expr =
    []
    |> collectFree(expr)([])
    |> containsName("resume")

// Stage 0's `TryRewriteResume` wording for an arm path it cannot rewrite: a `resume` in a
// position other than the tail, a let value or a match scrutinee, or a path that never resumes.
let armResumeDiagnostic (capName: Str) (opName: Str) (expression: Expr) =
    (let reason =
        if exprReferencesResume(expression)
        then "'resume' is only supported in tail position, as the value of a let, or as the scrutinee of a match; bind its result with 'let' to move the work after it."
        else "every path of an operation arm must call resume(...) (aborting arms need unwinding and are not supported)."
    in CoreCapabilityDiagnostic("ASH020")("In handler arm '" + capabilityDisplayName(capName) + "." + opName + "': " + reason))

// Pure, no lowering: does any case body or guard in a one-shot-scrutinee match reference `resume`
// again — a one-shot `resume` may run at most once per path (matching stage-0's
// TryRewriteResumeOneShotMatch check), and once the scrutinee itself is the resume call, every
// case becomes part of the SINGLE post continuation (there is no further per-case independent
// resolution the way ordinary case-body recursion allows), so any case resuming a second time is
// rejected rather than silently producing wrong IR.
let recursive matchCasesReferenceResume cases =
    match cases with
        | [] -> false
        | (_pattern, caseBody, guard) :: rest ->
            let guardReferencesResume =
                match guard with
                    | Some(guardExpr) -> exprReferencesResume(guardExpr)
                    | None -> false
            in
                if exprReferencesResume(caseBody)
                then true
                else
                    if guardReferencesResume
                    then true
                    else matchCasesReferenceResume(rest)

// Pure, no lowering: a resume-free stand-in for `body`, used only so lowerLambda's own capture
// analysis (`collectFree`) sees an operation-arm closure's true free variables — the placeholder
// itself is never lowered. Mirrors resolveOperationArmBody's shape recognition one layer at a
// time: a non-resuming let/let-recursive prefix survives with its own body replaced by the
// placeholder for what follows; a one-shot `let x = resume(v) in body` becomes the same
// resume-free tuple-of-value-and-post-lambda shape the leaf case actually builds; anything else
// falls back to its own (already resume-free, or rejected downstream) tail shape.
let recursive armBodyCapturePlaceholder body =
    match unspanForResumeCheck(body) with
        | ExprLet(name, value, letBody, _parameters, _annotation, _requirements) ->
            match tailResumeArgument(value) with
                | Some(resumeArgument) ->
                    ExprTuple([resumeArgument, ExprLambda(name)(armBodyCapturePlaceholder(letBody))(None)])
                | None ->
                    ExprLet(name)(value)(armBodyCapturePlaceholder(letBody))([])(None)([])
        | ExprLetRecursive(name, value, letBody, _parameters, _annotation, _requirements) ->
            ExprLetRecursive(name)(value)(armBodyCapturePlaceholder(letBody))([])(None)([])
        | ExprIf(condition, thenBranch, elseBranch) ->
            elseBranch
            |> armBodyCapturePlaceholder
            |> ExprIf(condition)(armBodyCapturePlaceholder(thenBranch))
        | ExprMatch(value, cases, position) ->
            match tailResumeArgument(value) with
                | Some(resumeArgument) ->
                    ExprTuple(
                        [
                            resumeArgument,
                            ExprLambda("__resume_result_placeholder")(ExprMatch(ExprVar("__resume_result_placeholder"))(cases)(position))(None)
                        ]
                    )
                | None ->
                    ExprMatch(value)(armBodyCapturePlaceholderCases(cases))(position)
        | _ ->
            match tailResumeArgument(body) with
                | Some(resumedValue) -> resumedValue
                | None -> body
and armBodyCapturePlaceholderCases cases =
    match cases with
        | [] -> []
        | (pattern, body, guard) :: rest -> (pattern, armBodyCapturePlaceholder(body), guard) :: armBodyCapturePlaceholderCases(rest)

// The one-shot post-resume leaf: `v` is returned to the perform site immediately, and
// `given postName -> postBody` is lowered as a fresh closure and stashed in the pending-post
// register for collectCapabilityPost (at the perform call site) to pick up. The post runs after
// the arm that made it has returned, so every binding its body reads is named among the
// escaping captures while its closure is built. The post's parameter is what `resume` returns;
// when the standalone inference recorded a closed type for the `resume` call, the parameter takes
// it before the post's body is lowered.
let lowerOneShotPost resumeCall resumeArgument postName postBody lower postRegisterIndex state =
    match lower(resumeArgument)(state) with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValue { state = valueState, temp = valueTemp, semanticType = valueType, error = None } ->
            let capturingState =
                valueState |> withStateEscapingCaptureNames(valueState
                |> stateEscapingCaptureNames
                |> collectFree(postBody)([postName]))
            in
                let postState =
                    match recordedSettledTypeAt(nodeIdentityOf(resumeCall))(capturingState) with
                        | Some(resumedType) ->
                            match freshType(capturingState) with
                                | FreshType { state = typedState, semanticType = postResultType } ->
                                    withOnlyExpectedType(None
                                    |> SemFunction(resumedType)(postResultType)
                                    |> Some)(typedState)
                        | None -> capturingState
                in
                    match postState |> lowerLambda(postName)(postBody)(None)(false)(lower) with
                        | LoweredCoreValue { state = failedPostState, error = Some(error) } -> failure(failedPostState)(error)
                        | LoweredCoreValue { state = loweredPostState, temp = postTemp, error = None } ->
                            loweredPostState
                            |> withStateEscapingCaptureNames(stateEscapingCaptureNames(valueState))
                            |> emit(StoreCapabilityHandler(postRegisterIndex)(postTemp))
                            |> success(valueTemp)(valueType)

// Resolves an operation arm's body (after all of its own operation parameters have been peeled
// off by lowerOperationArmParameters) to its final lowered value: recurses through any
// non-resuming `let`/`let recursive` prefix — an ordinary binding evaluated before the arm
// resumes, e.g. `let y = f(x) in resume(y)` — through an `if` whose condition doesn't resume (each
// branch independently resolved, possibly to a different resume shape — tail in one arm, one-shot
// in the other), and through a `match` whose scrutinee and guards don't resume (same per-case
// independence as `if`'s branches), down to the eventual resume shape, bare tail `resume(e)` or
// one-shot `let x = resume(v) in body` or one-shot `match resume(v) with | ...`. Mirrors stage-0's
// TryRewriteResume family (TryRewriteResumeLet/LetRecursive/If/MatchCases/OneShotMatch), but
// interleaves shape recognition with real lowering rather than rewriting the Expr tree first and
// lowering the rewrite once: selfhost's Expr type is a closed ADT with no synthetic "post" node to
// rewrite into, so each non-resuming prefix's own value is lowered here, in place, through the
// ordinary let/let-recursive/if/match lowering path (lowerArenaBracketedNestedLetWith /
// lowerPreparedRecursiveGroupWith / lowerIfThenBranch+finishIfElseBranch / a
// resolveOperationArmMatchArm(s) mirror of lowerMatchArm(s)), with the recursive call into the
// rest of the arm body supplied as the continuation — the same sentinel-placeholder-plus-custom-
// continuation technique lowerCoreProgramItems already uses for top-level declarations. `resume`
// in the `if`'s own condition, or a match's scrutinee/any guard, is rejected outright, same as
// stage-0's TryRewriteResumeIf/TryRewriteResumeMatchCases — there is no one-shot if-condition-
// resume shape, but a match scrutinee that IS directly a resume call is the distinct one-shot
// scrutinee shape (stage-0's TryRewriteResumeOneShotMatch): the whole match, re-run against the
// resumed value via a fresh synthetic parameter, becomes the single post continuation (reusing
// lowerOneShotPost unchanged), and every case's body/guard must not itself resume again
// (multi-shot rejected, matchCasesReferenceResume).
let recursive resolveOperationArmBody body lower postRegisterIndex capName opName armState =
    armState
    |> clearConsumerRequest
    |> resolveOperationArmBodyIn(body)(lower)(postRegisterIndex)(capName)(opName)
and resolveOperationArmBodyIn body lower postRegisterIndex capName opName state =
    match unspanForResumeCheck(body) with
        | ExprLet(name, value, letBody, _parameters, _annotation, _requirements) ->
            match tailResumeArgument(value) with
                | Some(resumeArgument) -> lowerOneShotPost(value)(resumeArgument)(name)(letBody)(lower)(postRegisterIndex)(state)
                | None ->
                    if exprReferencesResume(value)
                    then
                        value
                        |> armResumeDiagnostic(capName)(opName)
                        |> (given (diagnostic) -> failureAt(value)(diagnostic)(state))
                    else
                        match state with
                            | CoreLoweringState { bindings = outerBindings } ->
                                state
                                |> collectPendingBorrowedOwners(name)(value)
                                |> lowerArenaBracketedNestedLetWith(
                                    name,
                                    value,
                                    letBody,
                                    given (_ignoredBody) ->
                                        given (s) -> resolveOperationArmBody(letBody)(lower)(postRegisterIndex)(capName)(opName)(s),
                                    lower,
                                    outerBindings
                                )
        | ExprLetRecursive(name, value, letBody, _parameters, _annotation, _requirements) ->
            if exprReferencesResume(value)
            then
                opName
                |> UnsupportedOperationArmResume(capName)
                |> failure(state)
            else
                match state with
                    | CoreLoweringState { bindings = outerBindings, nextLambdaId = lambdaId } ->
                        []
                        |> prepareRecursiveGroup([(name, value)])(state)
                        |> relabelSingleRecursive(lambdaId)
                        |> lowerPreparedRecursiveGroupWith(
                            [(name, value)],
                            topLevelContinuationBody,
                            lower,
                            given (_ignoredBody) ->
                                given (s) -> resolveOperationArmBody(letBody)(lower)(postRegisterIndex)(capName)(opName)(s),
                            outerBindings
                        )
        | ExprIf(condition, thenBranch, elseBranch) ->
            if exprReferencesResume(condition)
            then
                opName
                |> UnsupportedOperationArmResume(capName)
                |> failure(state)
            else
                let branchLower =
                    given (branchBody) ->
                        given (s) -> resolveOperationArmBody(branchBody)(lower)(postRegisterIndex)(capName)(opName)(s)
                in
                    state
                    |> lower(condition)
                    |> prepareIfPlan
                    |> lowerIfThenBranch(thenBranch)(emptyConsumerRequest)(false)(branchLower)
                    |> finishIfElseBranch(false)(elseBranch)(emptyConsumerRequest)(false)(branchLower)
        // A scrutinee that IS itself a resume call (`match resume(v) with | ...`) is the one-shot
        // scrutinee shape (stage-0's TryRewriteResumeOneShotMatch): `v` returns to the perform
        // site immediately, and the WHOLE match — re-run against the resumed value, via a fresh
        // synthetic parameter — becomes the single post continuation, mirroring stage-0's
        // BuildCapabilityPost(postParam, new Match(postScrutinee, oneShotMatch.Cases, ...), ...).
        // Since the whole match is now the post body, there is no further per-case independent
        // resolution the way ordinary case-body recursion (below) allows — every case's body and
        // guard must NOT reference resume again (multi-shot rejected, matchCasesReferenceResume),
        // and the reconstructed match lowers through the arm's own ordinary `lower` inside
        // lowerOneShotPost (unchanged from the one-shot-let case — it already accepts an arbitrary
        // postBody Expr, and an ordinary ExprMatch needs no resolveOperationArmBody routing once
        // none of its cases resume).
        | ExprMatch(value, cases, position) ->
            match tailResumeArgument(value) with
                | Some(resumeArgument) ->
                    if matchCasesReferenceResume(cases)
                    then
                        opName
                        |> UnsupportedOperationArmResume(capName)
                        |> failure(state)
                    else
                        match freshTemp(state) with
                            | FreshTemp { state = namedState, temp = paramId } ->
                                let postParamName = "__resume_result_" + Ashes.Text.fromInt(paramId)
                                in
                                    let postBody = ExprMatch(ExprVar(postParamName))(cases)(position)
                                    in lowerOneShotPost(value)(resumeArgument)(postParamName)(postBody)(lower)(postRegisterIndex)(namedState)
                | None ->
                    // Case-body recursion only (mirrors stage-0's TryRewriteResumeMatchCases): the
                    // scrutinee and every guard must not reference resume at all, and each case
                    // body is independently resolved by this same recursion, same as an if's two
                    // branches. Always dispatches through the plain linear arm-by-arm path
                    // (resolveOperationArmMatchArms), never lowerMatch's own tag-group dispatch
                    // optimization (lowerMatchArmsViaTagGroups) — correct but potentially slower
                    // IR for a resume-containing match on constructor patterns; acceptable since
                    // operation arms are not hot paths the way ordinary pattern matching is.
                    if exprReferencesResume(value)
                    then
                        opName
                        |> UnsupportedOperationArmResume(capName)
                        |> failure(state)
                    else
                        state
                        |> lower(value)
                        |> prepareMatchPlan
                        |> resolveOperationArmMatchArms(cases)(lower)(postRegisterIndex)(capName)(opName)
                        |> finishMatchPlan
        | _ ->
            match tailResumeArgument(body) with
                | Some(resumedValue) -> lower(resumedValue)(state)
                | None ->
                    body
                    |> armResumeDiagnostic(capName)(opName)
                    |> (given (diagnostic) -> failureAt(body)(diagnostic)(state))
// Mirrors lowerMatchArm, except the guard (which must not reference resume, checked here) still
// lowers via the arm's own ordinary `lower`, while the arm body lowers via a wrapper back into
// resolveOperationArmBody — the same split lowerOperationArmParameters/resolveOperationArmBody
// already use elsewhere, just applied per pipe stage instead of via a single injected `lower`
// (lowerMatchGuard and finishMatchArm are separate pipe stages here, so each can be handed a
// different `lower` directly, with no need to distinguish them inside one closure).
and resolveOperationArmMatchArm pattern body guard lower postRegisterIndex capName opName failLabel (bracket: ArenaBracket) plan =
    match plan with
        | CoreMatchPlan { state = state, valueTemp = valueTemp, valueType = valueType, resultSlot = resultSlot, endLabel = endLabel, resultType = resultType } ->
            match state with
                | CoreLoweringState { bindings = outerBindings } ->
                    let guardRejected =
                        match guard with
                            | Some(guardExpr) -> exprReferencesResume(guardExpr)
                            | None -> false
                    in
                        if guardRejected
                        then
                            opName
                            |> UnsupportedOperationArmResume(capName)
                            |> failedMatchArm(state)
                        else
                            let bodyLower =
                                given (armBody) ->
                                    given (s) -> resolveOperationArmBody(armBody)(lower)(postRegisterIndex)(capName)(opName)(s)
                            in
                                state
                                |> preparePattern(pattern)
                                |> lowerPattern(pattern)(valueTemp)(valueType)(failLabel)
                                |> finishPatternArm(None)(None)([])(false)(pattern)(valueTemp)(body)(guard)(failLabel)(resultSlot)(endLabel)(resultType)(emptyConsumerRequest)(outerBindings)(bracket)(lower)(bodyLower)
// Each operation arm is bracketed like a linear arm of an ordinary match.
and resolveOperationArmMatchArms cases lower postRegisterIndex capName opName plan =
    match (cases, plan) with
        | (_cases, CoreMatchPlan { error = Some(_error) }) -> plan
        | ([], _) -> plan
        | ((pattern, body, guard) :: rest, CoreMatchPlan { state = state, noMatchLabel = noMatchLabel }) ->
            match matchFailLabel(rest)(noMatchLabel)(state) with
                | FreshLabel { state = failState, label = failLabel } ->
                    (plan with state = failState)
                    |> bracketMatchArm(resolveOperationArmMatchArm(pattern)(body)(guard)(lower)(postRegisterIndex)(capName)(opName))(failLabel)
                    |> labelNextMatchArm(rest)(failLabel)
                    |> resolveOperationArmMatchArms(rest)(lower)(postRegisterIndex)(capName)(opName)

// Wraps an operation arm's (already resume-rewritten) body in one lambda per parameter, matching
// each non-variable pattern via a fresh synthetic parameter name — the ordinary lambda/match
// lowering already handles the result like any other closure. Position-based names are safe here
// because each arm's parameters are wrapped in one call, never interleaved with another arm's.
let recursive buildArmParameterExpr index patterns body =
    match patterns with
        | [] -> body
        | PatternVar(name) :: rest ->
            ExprLambda(name)(buildArmParameterExpr(index + 1)(rest)(body))(None)
        | pattern :: rest ->
            let paramName = "__arm_arg_" + Ashes.Text.fromInt(index)
            in
                let innerBody = buildArmParameterExpr(index + 1)(rest)(body)
                in
                    ExprLambda(paramName)(ExprMatch(ExprVar(paramName))([(pattern, innerBody, None)])(None))(None)

// Lowers an operation arm's non-tail-resume parameters directly through lowerLambda, one at a
// time, rather than building a plain Expr tree and handing it to `lower` in one call: lowerCore's
// own ExprLambda case always recurses into a nested lambda's body via itself (never an injected
// `lower`), so nothing below the outermost layer could be intercepted that way. lowerLambda has no
// such hard-coded recursion — it always lowers the body through whatever `lower` it's given — so
// calling it directly at every parameter layer, with the innermost layer resolving the arm's
// actual body (resolveOperationArmBody, which may itself recurse through non-resuming let
// prefixes before reaching a resume shape), reaches the same place. Every layer is handed the same
// placeholderBody purely so lowerLambda's own free-variable/capture analysis (`collectFree`) sees
// the closure's true free variables; the placeholder itself is never lowered.
// A span-wrapped parameter is looked through, and a wildcard binds a name no source can read.
let recursive lowerOperationArmParameters patterns armBody placeholderBody lower postRegisterIndex capName opName state =
    match patterns with
        | [] -> resolveOperationArmBody(armBody)(lower)(postRegisterIndex)(capName)(opName)(state)
        | PatternAt(_span, inner) :: rest -> lowerOperationArmParameters(inner :: rest)(armBody)(placeholderBody)(lower)(postRegisterIndex)(capName)(opName)(state)
        | PatternWildcard :: rest -> lowerOperationArmParameters(PatternVar("__arm_wildcard") :: rest)(armBody)(placeholderBody)(lower)(postRegisterIndex)(capName)(opName)(state)
        | PatternVar(name) :: rest ->
            lowerLambda(
                name,
                placeholderBody,
                None,
                false,
                given (_ignoredBody) ->
                    given (s) -> lowerOperationArmParameters(rest)(armBody)(placeholderBody)(lower)(postRegisterIndex)(capName)(opName)(s),
                state
            )
        | _pattern :: _rest ->
            opName
            |> UnsupportedOperationArmResume(capName)
            |> failure(state)

// Lowers an operation arm closure with its own escaping result admitted to the reference-counted
// heap, restoring the enclosing answer after it.
let withArmOwnResult build (state: CoreLoweringState) =
    match state
    |> withStateHandlerArmOwnResult(true)
    |> build with
        | LoweredCoreValue { state = built } as lowered ->
            lowered with state = withStateHandlerArmOwnResult(stateHandlerArmOwnResult(state))(built)

// A bare tail `resume(e)` (the arm's whole unwrapped body, no let/letrec prefix) reuses the plain
// closure/match lowering machinery via buildArmParameterExpr, which — unlike
// lowerOperationArmParameters — also supports non-variable operation parameters (synthetic name +
// match), since `e` needs no lowering-time hook of its own. Everything else (a one-shot
// `let x = resume(v) in body`, or any non-resuming let/letrec prefix before either shape) goes
// through the general resolveOperationArmBody recursion, which supports variable and wildcard
// operation parameters.
let buildOperationArmClosure capName opName patterns armBody lower postRegisterIndex state =
    match tailResumeArgument(armBody) with
        | Some(resumedValue) ->
            lower(buildArmParameterExpr(0)(patterns)(resumedValue))(state)
        | None ->
            let placeholderBody = armBodyCapturePlaceholder(armBody)
            in lowerOperationArmParameters(patterns)(armBody)(placeholderBody)(lower)(postRegisterIndex)(capName)(opName)(state)

let recursive layoutListed (name: Str) (layouts: List(CoreCapabilityLayout)) =
    match layouts with
        | [] -> false
        | CoreCapabilityLayout { name = candidate } :: rest -> candidate == name || layoutListed(name)(rest)

// Stage 0's `LowerHandleCollectArms` grouping: the distinct capabilities the operation arms
// handle, in arm order. An arm naming no declared operation is the type checker's to report and
// is skipped here.
let recursive handledCapabilityLayouts (opArms: List((Str, Str, List(Pattern), Expr))) (layouts: List(CoreCapabilityLayout)) (handled: List(CoreCapabilityLayout)) =
    match opArms with
        | [] -> Ashes.Collection.List.reverse(handled)
        | (capName, opName, _patterns, _body) :: rest ->
            match findCapabilityLayout(capName)(layouts) with
                | Some(layout) ->
                    match findCapabilityOperationIndex(opName)(layout.operations) with
                        | Some(_index) ->
                            if layoutListed(capName)(handled)
                            then handledCapabilityLayouts(rest)(layouts)(handled)
                            else handledCapabilityLayouts(rest)(layouts)(layout :: handled)
                        | None -> handledCapabilityLayouts(rest)(layouts)(handled)
                | None -> handledCapabilityLayouts(rest)(layouts)(handled)

// Stage 0's `HandlerOperationType`: an arm closure is lowered against its operation's declared
// signature, freshly instantiated, so the arm's parameters have the operation's argument types
// while the arm and its posts are lowered. An unsigned operation gives no expectation.
let armExpectedType (capName: Str) (opName: Str) (state: CoreLoweringState) =
    match state
    |> stateCapabilityOperationSchemes
    |> findCapabilityOperationScheme(capName)(opName) with
        | Some(scheme) ->
            match instantiate(scheme)(state.typeSupply) with
                | InstantiationResult { semanticType = SemTuple(_instance :: operationType :: []), supply = nextSupply } -> (withTypeSupply(nextSupply)(state), Some(operationType))
                | _ -> (state, None)
        | None -> (state, None)

// Stage 0's `LowerHandleLowerArmClosures`: every operation arm lowered to a closure, in arm order,
// in the enclosing context. Each entry names the arm's capability index, the operation's index in
// its declaration, and the closure's temp. An arm naming no declared operation is skipped.
let recursive lowerOperationArmClosures (layouts: List(CoreCapabilityLayout)) (opArms: List((Str, Str, List(Pattern), Expr))) (globalCount: Int) lower (closures: List((Int, Int, Int))) (state: CoreLoweringState) =
    match opArms with
        | [] -> (state, Ashes.Collection.List.reverse(closures), None)
        | (capName, opName, patterns, armBody) :: rest ->
            match findCapabilityLayout(capName)(layouts) with
                | None -> lowerOperationArmClosures(layouts)(rest)(globalCount)(lower)(closures)(state)
                | Some(CoreCapabilityLayout { index = capIdx, operations = ops }) ->
                    match findCapabilityOperationIndex(opName)(ops) with
                        | None -> lowerOperationArmClosures(layouts)(rest)(globalCount)(lower)(closures)(state)
                        | Some(opIndex) ->
                            match armExpectedType(capName)(opName)(state) with
                                | (typedState, expected) ->
                                    match typedState
                                    |> withOnlyExpectedType(expected)
                                    |> withArmOwnResult(buildOperationArmClosure(capName)(opName)(patterns)(armBody)(lower)(globalCount))
                                    |> withLoweredConsumerRequest(consumerRequestOf(state)) with
                                        | LoweredCoreValue { state = failedState, error = Some(error) } -> (failedState, [], Some(error))
                                        | LoweredCoreValue { state = closureState, temp = closureTemp, error = None } -> lowerOperationArmClosures(layouts)(rest)(globalCount)(lower)((capIdx, opIndex, closureTemp) :: closures)(closureState)

let recursive storeFrameArmClosures (capIdx: Int) (frameTemp: Int) (globalCount: Int) (closures: List((Int, Int, Int))) (state: CoreLoweringState) =
    match closures with
        | [] -> state
        | (armCapIdx, opIndex, closureTemp) :: rest ->
            (if armCapIdx == capIdx
            then
                emit(StoreMemOffset(frameTemp)((globalCount + 1 + opIndex) * 8)(closureTemp))(state)
            else state) |> storeFrameArmClosures(capIdx)(frameTemp)(globalCount)(rest)

// Stage 0's `LowerHandleInstallFrames`: one frame per handled capability, each holding a snapshot of
// every evidence global taken before any of this handle's frames is installed, the pointer to the
// handle's shared posts-list head, and the capability's arm closures at
// ((globalCount + 1 + opIndex) * 8), where emitDynamicPerform reads them. Returns each capability's
// index with its frame.
let recursive buildHandlerFrames (handled: List(CoreCapabilityLayout)) (postsHeadPtrTemp: Int) (globalCount: Int) (closures: List((Int, Int, Int))) (frames: List((Int, Int))) (state: CoreLoweringState) =
    match handled with
        | [] -> (state, Ashes.Collection.List.reverse(frames))
        | CoreCapabilityLayout { index = capIdx, operations = ops } :: rest ->
            match freshTemp(state) with
                | FreshTemp { state = frameAllocState, temp = frameTemp } ->
                    frameAllocState
                    |> emit(AllocStack(frameTemp)((globalCount + 1 + coreListLength(ops)) * 8))
                    |> emitSnapshotGlobals(0)(globalCount)(frameTemp)
                    |> emit(StoreMemOffset(frameTemp)(globalCount * 8)(postsHeadPtrTemp))
                    |> storeFrameArmClosures(capIdx)(frameTemp)(globalCount)(closures)
                    |> buildHandlerFrames(rest)(postsHeadPtrTemp)(globalCount)(closures)((capIdx, frameTemp) :: frames)

let recursive installHandlerFrames (frames: List((Int, Int))) (state: CoreLoweringState) =
    match frames with
        | [] -> state
        | (capIdx, frameTemp) :: rest ->
            state
            |> emit(StoreCapabilityHandler(capIdx)(frameTemp))
            |> installHandlerFrames(rest)

// Stage 0's `LowerHandleUninstallFrames`: each handled capability's global restored from its own
// frame's snapshot slot.
let recursive uninstallHandlerFrames (frames: List((Int, Int))) (state: CoreLoweringState) =
    match frames with
        | [] -> state
        | (capIdx, frameTemp) :: rest ->
            match freshTemp(state) with
                | FreshTemp { state = unState, temp = prevTemp } ->
                    unState
                    |> emit(LoadMemOffset(prevTemp)(frameTemp)(capIdx * 8))
                    |> emit(StoreCapabilityHandler(capIdx)(prevTemp))
                    |> uninstallHandlerFrames(rest)

let recursive layoutNames (layouts: List(CoreCapabilityLayout)) =
    match layouts with
        | [] -> []
        | CoreCapabilityLayout { name = name } :: rest -> name :: layoutNames(rest)

// The posts fold hands the handle's value to each post under the owned-argument contract when the
// value it starts from is reference-counted and its type has a complete copy-out (stage 0's
// `ownedResult` in `LowerHandleFoldPosts`); the copy-out is what an arena post result is
// normalized with.
let ownedPostsFoldCopyOut (initialResultTemp: Int) (resultType: SemanticType) (state: CoreLoweringState) =
    if isRuntimeTemp(initialResultTemp)(state)
    then callCopyOutOf(resultType)(state)
    else None

// A post call whose fold slot holds an arena value: bit 1 of the ownership word asks the post for
// its result in the arena, so a post that produces a reference-counted value copies it out and
// releases the original.
let emitArenaPostCall postClosureTemp currentResultTemp resultLocal state =
    match emitRequestWord(-1)(state) with
        | (wordState, wordTemp) ->
            match freshTemp(wordState) with
                | FreshTemp { state = callState, temp = nextResultTemp } ->
                    callState
                    |> emit(CallClosure(nextResultTemp)(postClosureTemp)(currentResultTemp)(wordTemp))
                    |> emit(StoreLocal(resultLocal)(nextResultTemp))

// Stage 0's `EmitOwnedContinuationCall` for a post: the fold's value is retained when the post's
// entry adopts reference-counted arguments and handed over under that flag; a reference-counted
// result is adopted and an arena one normalized into a reference-counted copy, after which the
// value the post was given is released, so the fold's slot holds one owned reference-counted
// value before and after every post.
let emitOwnedPostCall (copyOut: CallCopyOut) postClosureTemp currentResultTemp resultLocal resultType state =
    match emitAcceptsRuntimeManagedFlag(postClosureTemp)(state) with
        | (flagged, acceptsFlagTemp) ->
            match emitConditionalArgumentRetain(currentResultTemp)(resultType)(acceptsFlagTemp)(flagged) with
                | (retained, passedTemp) ->
                    match emitReturnsRuntimeManagedFlag(postClosureTemp)(retained) with
                        | (returnsFlagged, returnsFlagTemp) ->
                            match freshLabel("posts_fold_copy_arena_result")(returnsFlagged) with
                                | FreshLabel { state = copyLabelled, label = copyLabel } ->
                                    match freshLabel("posts_fold_owned_result")(copyLabelled) with
                                        | FreshLabel { state = ownedLabelled, label = ownedLabel } ->
                                            match freshTempRun(2)(ownedLabelled) with
                                                | FreshTemp { state = callState, temp = nextResultTemp } ->
                                                    callState
                                                    |> emit(CallClosure(nextResultTemp)(postClosureTemp)(passedTemp)(acceptsFlagTemp))
                                                    |> emit(StoreLocal(resultLocal)(nextResultTemp))
                                                    |> emit(JumpIfFalse(returnsFlagTemp)(copyLabel))
                                                    |> emit(Jump(ownedLabel))
                                                    |> emit(Label(copyLabel))
                                                    |> emit(callCopyOutInstruction(copyOut)(nextResultTemp + 1)(nextResultTemp))
                                                    |> emit(StoreLocal(resultLocal)(nextResultTemp + 1))
                                                    |> emit(Label(ownedLabel))
                                                    |> emitArenaResultRelease(currentResultTemp)(resultType)

let emitCapabilityPostCall postClosureTemp currentResultTemp resultLocal initialResultTemp resultType state =
    match ownedPostsFoldCopyOut(initialResultTemp)(resultType)(state) with
        | Some(copyOut) -> emitOwnedPostCall(copyOut)(postClosureTemp)(currentResultTemp)(resultLocal)(resultType)(state)
        | None -> emitArenaPostCall(postClosureTemp)(currentResultTemp)(resultLocal)(state)

// Folds every collected one-shot post continuation over the handle's current result, in
// most-recently-collected-first order (the posts list is a stack, newest at the head — matching
// the innermost `resume` completing first). Empty list: the loop's own JumpIfFalse skips straight
// to reading the initial result back out, so a handle with no one-shot resumes pays one dead
// compare-and-branch and nothing else. A real loop (labels/locals, not a pure Expr tree) because
// the list length is only known at runtime.
let recursive foldCapabilityPosts postsHeadPtrTemp initialResultTemp resultType state =
    match freshLocal(state) with
        | FreshLocal { state = resultLocalState, local = resultLocal } ->
            match resultLocalState
            |> emit(StoreLocal(resultLocal)(initialResultTemp))
            |> freshLocal with
                | FreshLocal { state = cellLocalState, local = cellLocal } ->
                    match freshTemp(cellLocalState) with
                        | FreshTemp { state = initHeadState, temp = initHeadTemp } ->
                            let initState =
                                initHeadState
                                |> emit(LoadMemOffset(initHeadTemp)(postsHeadPtrTemp)(0))
                                |> emit(StoreLocal(cellLocal)(initHeadTemp))
                            in
                                match freshLabel("capability_posts_loop")(initState) with
                                    | FreshLabel { state = loopLabelState, label = loopLabel } ->
                                        match freshLabel("capability_posts_done")(loopLabelState) with
                                            | FreshLabel { state = doneLabelState, label = doneLabel } ->
                                                doneLabelState
                                                |> emit(Label(loopLabel))
                                                |> foldCapabilityPostsLoop(loopLabel)(doneLabel)(cellLocal)(resultLocal)(initialResultTemp)(resultType)
and foldCapabilityPostsLoop loopLabel doneLabel cellLocal resultLocal initialResultTemp resultType state =
    match freshTemp(state) with
        | FreshTemp { state = cellState, temp = cellTemp } ->
            match cellState
            |> emit(LoadLocal(cellTemp)(cellLocal))
            |> freshTemp with
                | FreshTemp { state = zeroState, temp = zeroTemp } ->
                    match zeroState
                    |> emit(LoadConstInt(zeroTemp)(0))
                    |> freshTemp with
                        | FreshTemp { state = cmpState, temp = hasCellTemp } ->
                            let checkedState =
                                cmpState
                                |> emit(CmpIntNe(hasCellTemp)(cellTemp)(zeroTemp))
                                |> emit(JumpIfFalse(hasCellTemp)(doneLabel))
                            in
                                match freshTemp(checkedState) with
                                    | FreshTemp { state = closureState, temp = postClosureTemp } ->
                                        let loadedClosureState =
                                            emit(LoadMemOffset(postClosureTemp)(cellTemp)(0))(closureState)
                                        in
                                            match freshTemp(loadedClosureState) with
                                                | FreshTemp { state = currentResultState, temp = currentResultTemp } ->
                                                    let loadedResultState =
                                                        emit(LoadLocal(currentResultTemp)(resultLocal))(currentResultState)
                                                    in
                                                        match loadedResultState
                                                        |> emitCapabilityPostCall(postClosureTemp)(currentResultTemp)(resultLocal)(initialResultTemp)(resultType)
                                                        |> freshTemp with
                                                            | FreshTemp { state = nextCellState, temp = nextCellTemp } ->
                                                                nextCellState
                                                                |> emit(LoadMemOffset(nextCellTemp)(cellTemp)(8))
                                                                |> emit(StoreLocal(cellLocal)(nextCellTemp))
                                                                |> emitLivePostsAdjust(-1)
                                                                |> emit(Jump(loopLabel))
                                                                |> emit(Label(doneLabel))
                                                                |> finishCapabilityPostsFold(resultLocal)(initialResultTemp)(resultType)
// The handle's value read back after the fold is reference-counted exactly when the fold handed
// it to the posts owned; a fold from any other value asks every post for an arena result.
and finishCapabilityPostsFold resultLocal initialResultTemp resultType state =
    match freshTemp(state) with
        | FreshTemp { state = readState, temp = finalResultTemp } ->
            let read =
                emit(LoadLocal(finalResultTemp)(resultLocal))(readState)
            in
                match ownedPostsFoldCopyOut(initialResultTemp)(resultType)(read) with
                    | Some(_copyOut) ->
                        read
                        |> markRuntimeTemp(finalResultTemp)(RuntimeNewlyProduced)
                        |> success(finalResultTemp)(resultType)
                    | None -> success(finalResultTemp)(resultType)(read)

// Whether some operation arm does more than resume with a value. Such an arm leaves a post, and
// while posts are live the handle's result keeps its arena rather than being copied out of it.
let recursive anyArmLeavesPost (opArms: List((Str, Str, List(Pattern), Expr))) =
    match opArms with
        | [] -> false
        | (_capability, _operation, _patterns, armBody) :: rest ->
            match tailResumeArgument(armBody) with
                | None -> true
                | Some(_argument) -> anyArmLeavesPost(rest)

// The binding the handled body's value takes over the return arm, with the expression lowered
// under it. Under a handle whose arms leave posts, a return arm that names the value binds it
// directly, so an arm handing the value on as its result is a `let` returning its own binding and
// keeps the value's reference: matched against a binding of its own, the result would borrow a
// value the binding releases, with no copy behind it while posts are live. Every other return arm
// (another pattern, a nullary constructor's name, a handle without posts) is matched against a
// binding of its own.
let recursive returnArmBinding (leavesPost: Bool) (returnPat: Pattern) (returnExpr: Expr) (state: CoreLoweringState) =
    match returnPat with
        | PatternAt(_span, inner) -> returnArmBinding(leavesPost)(inner)(returnExpr)(state)
        | PatternVar(name) ->
            match (leavesPost, constructorLayout(name)(state)) with
                | (true, None) -> (name, returnExpr)
                | _ -> ("__body_res", ExprMatch(ExprVar("__body_res"))([(returnPat, returnExpr, None)])(None))
        | _ -> ("__body_res", ExprMatch(ExprVar("__body_res"))([(returnPat, returnExpr, None)])(None))

// The return arm binds the handled body's value as a `let` binding scoped over the arm, bracketed
// and closed as a nested `let` is: a value the binding adopted as its owner is released when the
// arm's result does not hand it on, as stage 0's owned continuation call releases its argument.
let lowerHandleReturnArm (leavesPost: Bool) returnPat returnExpr lower outerBindings bodyTemp bodyType state =
    match returnArmBinding(leavesPost)(returnPat)(returnExpr)(state) with
        | (bindingName, armBody) ->
            match freshLocal(state) with
                | FreshLocal { state = cursorAllocated, local = cursorSlot } ->
                    match freshLocal(cursorAllocated) with
                        | FreshLocal { state = endAllocated, local = endSlot } ->
                            let bodyValue =
                                endAllocated
                                |> emit(SaveArenaState(cursorSlot)(endSlot)(false))
                                |> success(bodyTemp)(bodyType)
                                |> armOwnerReleasePlan(ExprVar("__body_res"))
                            in
                                match finishLetValueInSlot(bindingName)(ExprVar("__body_res"))(armBody)(armBody)(escapingLetBodyRequest(armBody)(consumerRequestOf(state))(state))(lower)(outerBindings)(bodyValue) with
                                    | (LoweredCoreValue { state = failedState, error = Some(error) }, _slot) -> failure(failedState)(error)
                                    | (LoweredCoreValue { state = armState, temp = resultTemp, semanticType = resultType, error = None }, ownerSlot) ->
                                        match closeOwnedLetBracket(loweredValueOwnedTypeName(bodyValue))(ownerSlot)(cursorSlot)(endSlot)(resultTemp)(resultType)(armState) with
                                            | (closed, finalTemp) -> finishClosedLetResult(finalTemp)(resultType)(closed)

// Stage 0's `LowerHandle`: the arms are lowered to closures in the enclosing context,
// one frame per handled capability is built and installed, the body is lowered with those
// capabilities discharged (so a provider for one of them is an ambiguity), the frames are
// uninstalled, the return arm applied, and the collected one-shot posts folded over the result.
let lowerHandleWithExpected body arms (request: ConsumerRequest) lower state =
    match splitHandlerArms(arms) with
        | ParsedHandlerArms { opArms = opArms, returnArm = returnArm } ->
            match state with
                | CoreLoweringState { bindings = outerBindings, programState = CoreProgramState { capabilityLayouts = capLayouts, capabilityGlobalCount = globalCount } } ->
                    match handledCapabilityLayouts(opArms)(capLayouts)([]) with
                        | [] ->
                            state
                            |> withConsumerRequest(request)
                            |> lower(body)
                        | handled ->
                            match lowerOperationArmClosures(capLayouts)(opArms)(globalCount)(lower)([])(state) with
                                | (armsFailedState, _closures, Some(error)) -> failure(armsFailedState)(error)
                                | (armsState, closures, None) ->
                                    match freshTemp(armsState) with
                                        | FreshTemp { state = stackState, temp = postsHeadPtrTemp } ->
                                            match freshTemp(stackState) with
                                                | FreshTemp { state = zeroState, temp = zeroTemp } ->
                                                    match zeroState
                                                    |> emit(AllocStack(postsHeadPtrTemp)(8))
                                                    |> emit(LoadConstInt(zeroTemp)(0))
                                                    |> emit(StoreMemOffset(postsHeadPtrTemp)(0)(zeroTemp))
                                                    |> buildHandlerFrames(handled)(postsHeadPtrTemp)(globalCount)(closures)([]) with
                                                        | (framesState, frames) ->
                                                            let enclosingHandled = stateHandledCapabilities(framesState)
                                                            in
                                                                match framesState
                                                                |> installHandlerFrames(frames)
                                                                |> withStateHandledCapabilities(append(layoutNames(handled))(enclosingHandled))
                                                                |> withConsumerRequest(request)
                                                                |> lower(body) with
                                                                    | LoweredCoreValue { state = bodyState, temp = bodyTemp, semanticType = bodyType, error = None } ->
                                                                        let uninstallState =
                                                                            bodyState
                                                                            |> withStateHandledCapabilities(enclosingHandled)
                                                                            |> uninstallHandlerFrames(frames)
                                                                        in
                                                                            let currentResultOutcome =
                                                                                match returnArm with
                                                                                    | None -> success(bodyTemp)(bodyType)(uninstallState)
                                                                                    | Some((returnPat, returnExpr)) ->
                                                                                        lowerHandleReturnArm(anyArmLeavesPost(opArms))(returnPat)(returnExpr)(lower)(outerBindings)(bodyTemp)(bodyType)(uninstallState)
                                                                            in
                                                                                match currentResultOutcome with
                                                                                    | LoweredCoreValue { state = failedResultState, error = Some(error) } -> failure(failedResultState)(error)
                                                                                    | LoweredCoreValue { state = resultState, temp = resultTemp, semanticType = resultType, error = None } -> foldCapabilityPosts(postsHeadPtrTemp)(resultTemp)(resultType)(resultState)
                                                                    | failed -> failed

// The handled body inherits the context's expected type; the operation arms do not.
let lowerHandle body arms lower state =
    state
    |> clearConsumerRequest
    |> lowerHandleWithExpected(body)(arms)(state
    |> consumerRequestOf
    |> branchRequest)(lower)

// Stage 0's `LowerResultPipe`: the left operand is unified with `Result(e, s)` before the mapper
// is lowered against `s -> r`; a mapper returning `Result(e, s2)` makes the pipe a flat map whose
// value is stored as it is, any other mapper result is rewrapped in `Ok`. Neither operand inherits
// the context's request, an operator operand never being in tail position.
let resultPipeShape (returnType: SemanticType) (errorType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(returnType) with
        | SemNamed(_symbolId, "Result", nestedError :: nestedSuccess :: []) ->
            match bindType(errorType)(nestedError)(state) with
                | (failedState, Some(error)) -> (failedState, true, SemNever, Some(error))
                | (boundState, None) -> (boundState, true, SemNamed(0)("Result")([errorType, nestedSuccess]), None)
        | _other -> (state, false, SemNamed(0)("Result")([errorType, returnType]), None)

let emitResultPipe leftTemp funcTemp (okLayout: CoreConstructorLayout) (isFlatMap: Bool) (pipeType: SemanticType) (state: CoreLoweringState) =
    match freshLabel("result_error")(state) with
        | FreshLabel { state = errorLabelState, label = errorLabel } ->
            match freshLabel("result_end")(errorLabelState) with
                | FreshLabel { state = labelState, label = endLabel } ->
                    match emitResultPipeBranches(leftTemp)(funcTemp)(okLayout.tag)(okLayout.tagless)(isFlatMap)(errorLabel)(endLabel)(labelState.nextTemp)(labelState.nextLocal) with
                        | CoreResultPipeEmission { instructions = instructions, nextTemp = nextTemp, nextLocal = nextLocal, resultTemp = resultTemp } ->
                            labelState
                            |> emitInstructions(instructions)
                            |> withNextTemp(nextTemp)
                            |> withNextLocal(nextLocal)
                            |> success(resultTemp)(pipeType)

let lowerResultPipeMapper leftTemp funcTemp funcType successType errorType (okLayout: CoreConstructorLayout) state =
    match freshType(state) with
        | FreshType { state = returnState, semanticType = returnType } ->
            match bindType(funcType)(SemFunction(successType)(returnType)(None))(returnState) with
                | (failedState, Some(error)) -> failure(failedState)(error)
                | (typedState, None) ->
                    match resultPipeShape(returnType)(errorType)(typedState) with
                        | (failedState, _isFlatMap, _pipeType, Some(error)) -> failure(failedState)(error)
                        | (shapedState, isFlatMap, pipeType, None) -> emitResultPipe(leftTemp)(funcTemp)(okLayout)(isFlatMap)(pipeType)(shapedState)

let lowerResultPipe left right lower state =
    match constructorLayout("Ok")(state) with
        | None -> failure(state)(UnknownLoweringBinding("Ok"))
        | Some(okLayout) ->
            match state
            |> clearConsumerRequest
            |> lower(left) with
                | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                | LoweredCoreValue { state = leftState, temp = leftTemp, semanticType = leftType, error = None } ->
                    match freshType(leftState) with
                        | FreshType { state = errorTypeState, semanticType = errorType } ->
                            match freshType(errorTypeState) with
                                | FreshType { state = successTypeState, semanticType = successType } ->
                                    match bindType(leftType)(SemNamed(0)("Result")([errorType, successType]))(successTypeState) with
                                        | (failedState, Some(error)) -> failure(failedState)(error)
                                        | (boundState, None) ->
                                            match boundState
                                            |> clearConsumerRequest
                                            |> lower(right) with
                                                | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                                                | LoweredCoreValue { state = rightState, temp = funcTemp, semanticType = funcType, error = None } -> lowerResultPipeMapper(leftTemp)(funcTemp)(funcType)(successType)(errorType)(okLayout)(rightState)

// Stage 0's `LowerResultMapErrorPipe`: `left |!> f` compares the left operand's tag with
// `Error`'s, passes the error payload through the mapper and rewraps the mapped value in `Error`;
// an `Ok` operand flows through unchanged. The branches are the success pipe's with the roles of
// the two constructors exchanged.
let emitResultMapErrorPipe leftTemp funcTemp (errorLayout: CoreConstructorLayout) (pipeType: SemanticType) (state: CoreLoweringState) =
    match freshLabel("result_map_error")(state) with
        | FreshLabel { state = errorLabelState, label = errorLabel } ->
            match freshLabel("result_map_error_end")(errorLabelState) with
                | FreshLabel { state = labelState, label = endLabel } ->
                    match emitResultPipeBranches(leftTemp)(funcTemp)(errorLayout.tag)(errorLayout.tagless)(false)(errorLabel)(endLabel)(labelState.nextTemp)(labelState.nextLocal) with
                        | CoreResultPipeEmission { instructions = instructions, nextTemp = nextTemp, nextLocal = nextLocal, resultTemp = resultTemp } ->
                            labelState
                            |> emitInstructions(instructions)
                            |> withNextTemp(nextTemp)
                            |> withNextLocal(nextLocal)
                            |> success(resultTemp)(pipeType)

let lowerResultMapErrorMapper leftTemp funcTemp funcType successType errorType (errorLayout: CoreConstructorLayout) state =
    match freshType(state) with
        | FreshType { state = mappedState, semanticType = mappedErrorType } ->
            match bindType(funcType)(SemFunction(errorType)(mappedErrorType)(None))(mappedState) with
                | (failedState, Some(error)) -> failure(failedState)(error)
                | (typedState, None) ->
                    emitResultMapErrorPipe(leftTemp)(funcTemp)(errorLayout)(SemNamed(0)("Result")([mappedErrorType, successType]))(typedState)

let lowerResultMapErrorPipe left right lower state =
    match constructorLayout("Error")(state) with
        | None -> failure(state)(UnknownLoweringBinding("Error"))
        | Some(errorLayout) ->
            match state
            |> clearConsumerRequest
            |> lower(left) with
                | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                | LoweredCoreValue { state = leftState, temp = leftTemp, semanticType = leftType, error = None } ->
                    match freshType(leftState) with
                        | FreshType { state = errorTypeState, semanticType = errorType } ->
                            match freshType(errorTypeState) with
                                | FreshType { state = successTypeState, semanticType = successType } ->
                                    match bindType(leftType)(SemNamed(0)("Result")([errorType, successType]))(successTypeState) with
                                        | (failedState, Some(error)) -> failure(failedState)(error)
                                        | (boundState, None) ->
                                            match boundState
                                            |> clearConsumerRequest
                                            |> lower(right) with
                                                | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                                                | LoweredCoreValue { state = rightState, temp = funcTemp, semanticType = funcType, error = None } -> lowerResultMapErrorMapper(leftTemp)(funcTemp)(funcType)(successType)(errorType)(errorLayout)(rightState)

// Stage 0's `LowerLetResult` as the match it means: `let? name = value in body` binds the `Ok`
// payload for the body and hands an `Error` operand back as an `Error` of the body's type.
let letResultMatch (name: Str) (value: Expr) (body: Expr) =
    ExprMatch(value)([
        (PatternConstructor("Ok")([PatternVar(name)]), body, None),
        (PatternConstructor("Error")([PatternVar("__let_result_error")]), ExprCall(ExprVar("Error"))(ExprVar("__let_result_error"))(false)(callArgumentsInline), None)
    ])(None)

let expressionName expression =
    match expression with
        | ExprBigInt(_) -> "BigInt"
        | ExprQualifiedVar(_, _) -> "qualified variable"
        | _ -> "non-core expression"

// A constructor call's arguments are lowered without the expected type and its result unified
// with it; a builtin or external call ignores it; a general call constrains its result with it
// and lowers each argument against the callee's parameter type.
let unifyOptionalExpectedResult expected lowered =
    match expected with
        | None -> lowered
        | Some(expectedType) -> unifyExpectedResult(expectedType)(lowered)
