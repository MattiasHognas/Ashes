// Binary and unary operators, trait method dispatch and evidence, deferred operator goals, big
// integer literals and variable references. A part of `AshesCompiler.Semantics.CoreLowering`.

import Ashes.Collection.List.append
import Ashes.Collection.List.reverse
import Ashes.Collection.List.sortBy
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Token.TextSpan
import AshesCompiler.Semantics.CoreExternalLowering
import AshesCompiler.Semantics.TraitResolution
import AshesCompiler.Semantics.TraitResolution.resolveTraitEvidence
import AshesCompiler.Semantics.TypeInference.resolveTraitBinding
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.TypeSchemes
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.NodeTypeTable.NodeOperator
import AshesCompiler.Semantics.NodeTypeTable.nodeOperatorAt
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.translateRecordedType
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.recordedSettledType
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.CoreLowering.LoweringState
import AshesCompiler.Semantics.CoreLowering.Emit
import AshesCompiler.Semantics.CoreLowering.Owners
import AshesCompiler.Semantics.CoreLowering.CopyPlans
import AshesCompiler.Semantics.CoreLowering.TcoReset
import AshesCompiler.Semantics.CoreLowering.CallArguments
import AshesCompiler.Semantics.CoreLowering.Constructors
import AshesCompiler.Semantics.CoreLowering.Records
export (
    value standardTraitName,
    value withoutSeededImplementation,
    value emitTraitMethodClosureFromLabel,
    value sortedTraitMethodNames,
    value activeTraitMethod,
    value hasActiveTraitEvidence,
    value constrainedPlanLacksEvidence,
    value buildTraitMethodClosure,
    value traitGoalKey,
    value pinProvenTraitGoal,
    value deferTraitGoal,
    value deferTraitGoalWithMethods,
    value dictionaryGoalCandidates,
    value observeDeferredOperator,
    value emitCoreTraitUnaryDispatch,
    value applyDeferredOperators,
    value applySealedDeferredOperators,
    value lowerCoreBinary,
    value lowerCoreTraitCompare,
    value lowerCoreLogicalNot,
    value lowerCoreBitwiseNot,
    value parseDecimalDigits,
    value lowerCoreBigInt,
    value lowerCoreVariable,
    value lowerQualified,
)

let bindCoreBinaryRightType binary resolvedRight =
    match binary with
        | LoweredCoreBinary { state = state, leftType = leftType, rightType = rightType, error = None } ->
            match resolvedRight with
                | SemVariable(_id) ->
                    state
                    |> bindType(rightType)(leftType
                    |> resolveType(state)
                    |> numericDefault)
                    |> finishCoreBinaryBinding(binary)
                | _ -> binary
        | failed -> failed

let finishCoreBinaryLeftBinding leftBound =
    match leftBound with
        | LoweredCoreBinary { state = state, rightType = rightType, error = None } ->
            rightType
            |> resolveType(state)
            |> bindCoreBinaryRightType(leftBound)
        | failed -> failed

let bindCoreBinaryRight binary =
    match binary with
        | LoweredCoreBinary { state = state, rightType = rightType, error = None } ->
            binary
            |> bindCoreBinaryLeft(resolveType(state)(rightType))
            |> finishCoreBinaryLeftBinding
        | failed -> failed

let withCoreBinaryLeftType leftType (typed: LoweredCoreBinary) = typed with leftType = leftType

let withCoreBinaryRightType rightType (typed: LoweredCoreBinary) = typed with rightType = rightType

let resolveCoreBinaryTypes state (typed: LoweredCoreBinary) =
    match typed with
        | LoweredCoreBinary { leftType = leftType, rightType = rightType } ->
            typed
            |> withCoreBinaryLeftType(resolveType(state)(leftType))
            |> withCoreBinaryRightType(resolveType(state)(rightType))

let resolvedCoreBinary binary =
    match bindCoreBinaryRight(binary) with
        | LoweredCoreBinary { state = state, error = None } as typed -> resolveCoreBinaryTypes(state)(typed)
        | failed -> failed

let operatorMismatch name binary =
    match binary with
        | LoweredCoreBinary { state = state, leftType = leftType, rightType = rightType } ->
            rightType
            |> CoreOperatorTypeMismatch(name)(leftType)
            |> failure(state)

let emitCoreBinaryTarget kind semanticType binary =
    match binary with
        | LoweredCoreBinary { state = state, leftTemp = left, rightTemp = right, error = None } ->
            match freshTemp(state) with
                | FreshTemp { state = targetState, temp = target } ->
                    targetState
                    |> emit(kind(target)(left)(right))
                    |> success(target)(semanticType)
        | LoweredCoreBinary { state = state, error = Some(error) } -> failure(state)(error)

let emitCoreFloatBinary kind binary = emitCoreBinaryTarget(kind)(SemFloat)(binary)

let emitCoreBoolBinary kind binary = emitCoreBinaryTarget(kind)(SemBool)(binary)

let reserveCoreTemps count state =
    match state with
        | CoreLoweringState { nextTemp = first } ->
            ReservedCoreTemps(
                state = withNextTemp(first + count)(state),
                first = first
            )

let maskCoreUInt bits lowered =
    match lowered with
        | LoweredCoreValue { state = state, error = Some(error) } -> failure(state)(error)
        | LoweredCoreValue { state = state, temp = raw, error = None } ->
            if bits == 64
            then success(raw)(SemUInt(bits))(state)
            else
                match reserveCoreTemps(2)(state) with
                    | ReservedCoreTemps { state = resultState, first = maskTemp } ->
                        resultState
                        |> emit(LoadConstInt(maskTemp)((1 << bits) - 1))
                        |> emit(AndInt(maskTemp + 1)(raw)(maskTemp))
                        |> success(maskTemp + 1)(SemUInt(bits))

let emitCoreUIntTarget kind bits binary =
    binary
    |> emitCoreBinaryTarget(kind)(SemUInt(bits))
    |> maskCoreUInt(bits)

let emitCoreUInt kind bits binary = emitCoreUIntTarget(kind)(bits)(binary)

let emitCoreDivision isUnsigned quotient left right state =
    if isUnsigned
    then
        emit(DivUInt(quotient)(left)(right))(state)
    else
        emit(DivInt(quotient)(left)(right))(state)

let emitCoreRemainder isUnsigned resultType binary =
    match binary with
        | LoweredCoreBinary { state = state, leftTemp = left, rightTemp = right, error = None } ->
            match reserveCoreTemps(3)(state) with
                | ReservedCoreTemps { state = remainderState, first = quotient } ->
                    remainderState
                    |> emitCoreDivision(isUnsigned)(quotient)(left)(right)
                    |> emit(MulInt(quotient + 1)(quotient)(right))
                    |> emit(SubInt(quotient + 2)(left)(quotient + 1))
                    |> success(quotient + 2)(resultType)
        | LoweredCoreBinary { state = state, error = Some(error) } -> failure(state)(error)

let emitCoreBigIntComparison kind binary =
    match binary with
        | LoweredCoreBinary { state = state, leftTemp = left, rightTemp = right, error = None } ->
            match reserveCoreTemps(3)(state) with
                | ReservedCoreTemps { state = targetState, first = comparison } ->
                    targetState
                    |> emit(BigIntCompare(comparison)(left)(right))
                    |> emit(LoadConstInt(comparison + 1)(0))
                    |> emit(kind(comparison + 2)(comparison)(comparison + 1))
                    |> success(comparison + 2)(SemBool)
        | LoweredCoreBinary { state = state, error = Some(error) } -> failure(state)(error)

let emitCoreBigIntBinary operation binary =
    emitCoreBinaryTarget(
        given (target) ->
            given (left) ->
                given (right) -> BigIntBinary(target)(left)(right)(operation)(false)
    )(SemBigInt)(binary)

let releaseConcatRightOperand (leftTemp: Int) (rightTemp: Int) (state: CoreLoweringState) =
    if rightTemp == leftTemp
    then state
    else releaseConsumedOperand(rightTemp)(state)

let markConcatResult (target: Int) (runtimeManaged: Bool) (state: CoreLoweringState) =
    if runtimeManaged
    then markRuntimeTemp(target)(RuntimeNewlyProduced)(state)
    else state

// Stage 0's `LowerStringAdd`: an armed affine accumulator append grows the accumulator's
// reservation in place (`ConcatStrTip`; the extend and fallback paths live in the backend, and
// the fallback releases the reference it replaces, so only a newly produced right operand is
// released here, its bytes copied on both paths; the runtime-managed flag is decided with the
// parameter's placement once the body is lowered); any other concatenation is placed on the
// reference-counted heap when its consumer asked for a runtime string, its result then newly
// produced, and a newly produced operand is released right after the use.
let emitCoreConcat binary =
    match binary with
        | LoweredCoreBinary { state = state, leftTemp = left, rightTemp = right, error = None } ->
            match freshTemp(state) with
                | FreshTemp { state = targetState, temp = target } ->
                    match stateAffineAppendReservation(state) with
                        | Some((reservationStart, reservationEnd)) ->
                            targetState
                            |> emit(ConcatStrTip(target)(left)(right)(reservationStart)(reservationEnd)(false))
                            |> releaseConcatRightOperand(left)(right)
                            |> success(target)(SemString)
                        | None ->
                            targetState
                            |> emit(state
                            |> runtimeStringRequested
                            |> ConcatStr(target)(left)(right))
                            |> markConcatResult(target)(runtimeStringRequested(state))
                            |> releaseConsumedOperand(left)
                            |> releaseConcatRightOperand(left)(right)
                            |> success(target)(SemString)
        | LoweredCoreBinary { state = state, error = Some(error) } -> failure(state)(error)

let emitResolvedCoreAdd binary =
    match binary with
        | LoweredCoreBinary { leftType = SemInt, rightType = SemInt } -> emitCoreBinaryTarget(AddInt)(SemInt)(binary)
        | LoweredCoreBinary { leftType = SemUInt(leftBits), rightType = SemUInt(rightBits) } ->
            if leftBits == rightBits
            then emitCoreUInt(AddInt)(leftBits)(binary)
            else operatorMismatch("+")(binary)
        | LoweredCoreBinary { leftType = SemFloat, rightType = SemFloat } -> emitCoreFloatBinary(AddFloat)(binary)
        | LoweredCoreBinary { leftType = SemBigInt, rightType = SemBigInt } -> emitCoreBigIntBinary("add")(binary)
        | LoweredCoreBinary { leftType = SemString, rightType = SemString } -> emitCoreConcat(binary)
        | _ -> operatorMismatch("+")(binary)

let emitResolvedCoreSubtract binary =
    match binary with
        | LoweredCoreBinary { leftType = SemInt, rightType = SemInt } -> emitCoreBinaryTarget(SubInt)(SemInt)(binary)
        | LoweredCoreBinary { leftType = SemUInt(leftBits), rightType = SemUInt(rightBits) } ->
            if leftBits == rightBits
            then emitCoreUInt(SubInt)(leftBits)(binary)
            else operatorMismatch("-")(binary)
        | LoweredCoreBinary { leftType = SemFloat, rightType = SemFloat } -> emitCoreFloatBinary(SubFloat)(binary)
        | LoweredCoreBinary { leftType = SemBigInt, rightType = SemBigInt } -> emitCoreBigIntBinary("sub")(binary)
        | _ -> operatorMismatch("-")(binary)

let emitResolvedCoreMultiply binary =
    match binary with
        | LoweredCoreBinary { leftType = SemInt, rightType = SemInt } -> emitCoreBinaryTarget(MulInt)(SemInt)(binary)
        | LoweredCoreBinary { leftType = SemUInt(leftBits), rightType = SemUInt(rightBits) } ->
            if leftBits == rightBits
            then emitCoreUInt(MulInt)(leftBits)(binary)
            else operatorMismatch("*")(binary)
        | LoweredCoreBinary { leftType = SemFloat, rightType = SemFloat } -> emitCoreFloatBinary(MulFloat)(binary)
        | LoweredCoreBinary { leftType = SemBigInt, rightType = SemBigInt } -> emitCoreBigIntBinary("mul")(binary)
        | _ -> operatorMismatch("*")(binary)

let emitResolvedCoreDivide binary =
    match binary with
        | LoweredCoreBinary { leftType = SemInt, rightType = SemInt } -> emitCoreBinaryTarget(DivInt)(SemInt)(binary)
        | LoweredCoreBinary { leftType = SemUInt(leftBits), rightType = SemUInt(rightBits) } ->
            if leftBits == rightBits
            then emitCoreUInt(DivUInt)(leftBits)(binary)
            else operatorMismatch("/")(binary)
        | LoweredCoreBinary { leftType = SemFloat, rightType = SemFloat } -> emitCoreFloatBinary(DivFloat)(binary)
        | LoweredCoreBinary { leftType = SemBigInt, rightType = SemBigInt } -> emitCoreBigIntBinary("div")(binary)
        | _ -> operatorMismatch("/")(binary)

let emitResolvedCoreModulo binary =
    match binary with
        | LoweredCoreBinary { leftType = SemInt, rightType = SemInt } -> emitCoreRemainder(false)(SemInt)(binary)
        | LoweredCoreBinary { leftType = SemUInt(leftBits), rightType = SemUInt(rightBits) } ->
            if leftBits == rightBits
            then
                binary
                |> emitCoreRemainder(true)(SemUInt(leftBits))
                |> maskCoreUInt(leftBits)
            else operatorMismatch("%")(binary)
        | LoweredCoreBinary { leftType = SemBigInt, rightType = SemBigInt } -> emitCoreBigIntBinary("mod")(binary)
        | _ -> operatorMismatch("%")(binary)

let emitResolvedCoreBitwise name intKind binary =
    match binary with
        | LoweredCoreBinary { leftType = SemInt, rightType = SemInt } -> emitCoreBinaryTarget(intKind)(SemInt)(binary)
        | LoweredCoreBinary { leftType = SemUInt(leftBits), rightType = SemUInt(rightBits) } ->
            if leftBits == rightBits
            then emitCoreUInt(intKind)(leftBits)(binary)
            else operatorMismatch(name)(binary)
        | _ -> operatorMismatch(name)(binary)

let emitResolvedCoreShift name intKind binary =
    match binary with
        | LoweredCoreBinary { leftType = SemInt, rightType = SemInt } -> emitCoreBinaryTarget(intKind)(SemInt)(binary)
        | LoweredCoreBinary { leftType = SemUInt(leftBits), rightType = SemUInt(rightBits) } ->
            if leftBits == rightBits
            then emitCoreUInt(intKind)(leftBits)(binary)
            else operatorMismatch(name)(binary)
        | _ -> operatorMismatch(name)(binary)

let recursive lastQualifiedPiece (pieces: List(Str)) =
    match pieces with
        | [] -> ""
        | piece :: [] -> piece
        | _piece :: rest -> lastQualifiedPiece(rest)

let traitLeafName (name: Str) =
    "."
    |> Ashes.Text.split(name)
    |> lastQualifiedPiece

// A stitched standard trait keeps its module-qualified compiler name (`Ashes_Trait_Eq`); the
// trait environment and the operator mapping know it by its bare name.
let standardTraitName (name: Str) =
    if Ashes.Text.startsWith(name)("Ashes_Trait_")
    then Ashes.Text.substring(name)(12)(Ashes.Text.length(name) - 12)
    else traitLeafName(name)

// Whether two implementation heads are the same shape, any type parameter standing for any
// other: the seeded standard implementation `Eq(List(a))` and the stitched `Ashes.Trait` one.
let recursive sameImplementationHead (left: SemanticType) (right: SemanticType) =
    match (left, right) with
        | (SemParameter(_leftId, _leftName), SemParameter(_rightId, _rightName)) -> true
        | (SemList(leftElement), SemList(rightElement)) -> sameImplementationHead(leftElement)(rightElement)
        | (SemTuple(leftElements), SemTuple(rightElements)) -> sameImplementationHeads(leftElements)(rightElements)
        | (SemNamed(_leftId, leftName, leftArguments), SemNamed(_rightId, rightName, rightArguments)) -> leftName == rightName && sameImplementationHeads(leftArguments)(rightArguments)
        | (SemFunction(leftArgument, leftResult, _leftRow), SemFunction(rightArgument, rightResult, _rightRow)) -> sameImplementationHead(leftArgument)(rightArgument) && sameImplementationHead(leftResult)(rightResult)
        | (SemPointer(leftInner), SemPointer(rightInner)) -> sameImplementationHead(leftInner)(rightInner)
        | _ -> left == right
and sameImplementationHeads (lefts: List(SemanticType)) (rights: List(SemanticType)) =
    match (lefts, rights) with
        | ([], []) -> true
        | (left :: leftRest, right :: rightRest) -> sameImplementationHead(left)(right) && sameImplementationHeads(leftRest)(rightRest)
        | _ -> false

// The environment without the seeded standard implementation a program implementation of the
// same trait and head shape replaces: the stitched `Ashes.Trait` bodies stand in for the
// placeholders `standardTraitEnvironment` registers.
let recursive withoutSeededImplementations (traitName: Str) (headTypes: List(SemanticType)) (implementations: List(TraitImplementationInferenceDefinition)) =
    match implementations with
        | [] -> []
        | (TraitImplementationInferenceDefinition { traitName = candidateTrait, typeArguments = candidateHeads } as implementation) :: rest ->
            if candidateTrait == traitName && sameImplementationHeads(candidateHeads)(headTypes)
            then withoutSeededImplementations(traitName)(headTypes)(rest)
            else implementation :: withoutSeededImplementations(traitName)(headTypes)(rest)

let withoutSeededImplementation (traitName: Str) (headTypes: List(SemanticType)) (environment: TypeEnvironment) = environment with traitImplementations = withoutSeededImplementations(traitName)(headTypes)(environment.traitImplementations)

let recursive findImplementationMethod (methodName: Str) (methods: List(TraitImplementationMethodInferenceDefinition)) =
    match methods with
        | [] -> None
        | TraitImplementationMethodInferenceDefinition { name = name, implementation = implementation } :: rest ->
            if name == methodName
            then Some(implementation)
            else findImplementationMethod(methodName)(rest)

let implementationMethodExpression (methodName: Str) (implementation: TraitImplementationInferenceDefinition) =
    match implementation with
        | TraitImplementationInferenceDefinition { methods = methods } -> findImplementationMethod(methodName)(methods)

let traitMethodLabelKey (traitName: Str) (methodName: Str) (operandType: SemanticType) = traitName + "/" + methodName + "/" + Ashes.Trait.Show.show(operandType)

// The label `lowerLambda` will give a capture-free implementation lambda lowered next, known
// before its body is lowered so a recursive type's derived method can call itself through the
// cache while its own body is still being lowered (a derived `Tree` equality compares the
// subtrees through the same `equal`).
let predictedTraitMethodLabel (implementation: Expr) (state: CoreLoweringState) =
    match stripExprAt(implementation) with
        | ExprLambda(parameter, body, _annotation) ->
            match capturedBindings(collectFree(body)([parameter])([]))(state.bindings)([]) with
                | [] -> Some("lambda_" + Ashes.Text.fromInt(state.nextLambdaId))
                | _captures -> None
        | _other -> None

let rememberTraitMethodLabel (key: Str) (label: Str) (semanticType: SemanticType) (state: CoreLoweringState) =
    withStateTraitMethodLabels(state
    |> stateTraitMethodLabels
    |> Ashes.Collection.Map.setStr(key)((label, semanticType)))(state)

let emitTraitMethodClosureFromLabel (label: Str) (semanticType: SemanticType) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = environmentState, temp = environmentTemp } ->
            match environmentState
            |> emit(LoadConstInt(environmentTemp)(0))
            |> emitClosure(label)(environmentTemp)(0)(false) with
                | (closureState, closureTemp) -> success(closureTemp)(semanticType)(closureState)

// The closure of an implementation method: lowered from its body the first time a site needs it
// and, when the closure carries no environment, remembered by label so every later site
// rebuilds the closure from the label with a null environment. The body is lowered against the
// method's expected type, with only its own requirement evidence active (the implementation is a
// closed term) and no consumer request. A capture-free lambda is remembered under its predicted
// label before its body is lowered; a body that then lowers to a different label is refused
// rather than left calling the wrong function.
let lowerTraitMethodClosure (key: Str) (operandType: SemanticType) (expectedType: SemanticType) (evidence: List((Str, SemanticType, Str, Str))) (parameters: List((Str, SemanticType))) (implementation: Expr) lower (state: CoreLoweringState) =
    match state
    |> stateTraitMethodLabels
    |> Ashes.Collection.Map.getStr(key) with
        | Some((label, semanticType)) -> emitTraitMethodClosureFromLabel(label)(semanticType)(state)
        | None ->
            let predicted = predictedTraitMethodLabel(implementation)(state)
            in
                let primed =
                    match predicted with
                        | Some(label) -> rememberTraitMethodLabel(key)(label)(expectedType)(state)
                        | None -> state
                in
                    match primed
                    |> withStateActiveTraitMethods(evidence)
                    |> withStateActiveTypeParameters(parameters)
                    |> withOnlyExpectedType(Some(expectedType))
                    |> lower(implementation) with
                        | LoweredCoreValue { state = loweredState, temp = temp, semanticType = semanticType, error = None } ->
                            let restored =
                                loweredState
                                |> withStateActiveTraitMethods(stateActiveTraitMethods(state))
                                |> withStateActiveTypeParameters(stateActiveTypeParameters(state))
                                |> withConsumerRequest(state.consumerRequest)
                            in
                                match (predicted, emptyEnvironmentClosureLabel(temp)(loweredState.reversedInstructions)) with
                                    | (Some(expected), Some(label)) ->
                                        if expected == label
                                        then
                                            restored
                                            |> rememberTraitMethodLabel(key)(label)(semanticType)
                                            |> success(temp)(semanticType)
                                        else
                                            operandType
                                            |> UnsupportedCoreTraitDispatch(key)
                                            |> failure(restored)
                                    | (Some(_expected), None) ->
                                        operandType
                                        |> UnsupportedCoreTraitDispatch(key)
                                        |> failure(restored)
                                    | (None, Some(label)) ->
                                        restored
                                        |> rememberTraitMethodLabel(key)(label)(semanticType)
                                        |> success(temp)(semanticType)
                                    | (None, None) -> success(temp)(semanticType)(restored)
                        | LoweredCoreValue { state = failedState, error = Some(error) } ->
                            failure(failedState
                            |> withStateActiveTraitMethods(stateActiveTraitMethods(state))
                            |> withStateActiveTypeParameters(stateActiveTypeParameters(state)))(error)

// Stage 0's `ReleaseFreshComparisonOperand`: an operand of a trait-dispatched comparison that no
// binding owns (a call result, say) is released after the call. The method borrows both operands
// and its result keeps nothing of them, so the caller's reference dies with the call.
let releaseFreshComparisonOperand (operandType: SemanticType) (temp: Int) (state: CoreLoweringState) =
    match runtimeTempStateOf(temp)(state) with
        | Some(RuntimeNewlyProduced) ->
            state
            |> emitRuntimeChildDrop(temp)(operandType)
            |> markRuntimeTemp(temp)(RuntimeTransferred)
        | _ -> state

// The two closure calls stage 0's `RecordMappedBinaryTrait` emits: the method applied to the left
// operand, the partial application to the right, then the release of a fresh operand. The result
// is a Bool that holds neither operand and neither call passes an ownership word, so both calls
// only borrow their argument (stage 0's `MarkComparisonOperandBorrowed`): lifetime placement gives
// an operand used again later no compensating duplicate, which nothing would release.
let emitTraitMethodCalls (closureTemp: Int) (leftTemp: Int) (rightTemp: Int) (operandType: SemanticType) (state: CoreLoweringState) =
    match reserveCoreTemps(2)(state) with
        | ReservedCoreTemps { state = callState, first = first } ->
            callState
            |> emit(CallClosure(first)(closureTemp)(leftTemp)(-1))
            |> recordBorrowedCall(true)(first)
            |> emit(CallClosure(first + 1)(first)(rightTemp)(-1))
            |> recordBorrowedCall(true)(first + 1)
            |> releaseFreshComparisonOperand(operandType)(leftTemp)
            |> releaseFreshComparisonOperand(operandType)(rightTemp)
            |> success(first + 1)(SemBool)

let emitBoolNegation (operand: Int) (state: CoreLoweringState) =
    match reserveCoreTemps(2)(state) with
        | ReservedCoreTemps { state = targetState, first = falseTemp } ->
            targetState
            |> emit(LoadConstBool(falseTemp)(false))
            |> emit(CmpIntEq(falseTemp + 1)(operand)(falseTemp))
            |> success(falseTemp + 1)(SemBool)

let traitDefinitionOf (traitName: Str) (state: CoreLoweringState) =
    state
    |> stateTraitEnvironment
    |> resolveTraitBinding(traitName)

let recursive findTraitMethodDefinition (methodName: Str) (methods: List(TraitMethodInferenceDefinition)) =
    match methods with
        | [] -> None
        | (TraitMethodInferenceDefinition { name = name } as method) :: rest ->
            if name == methodName
            then Some(method)
            else findTraitMethodDefinition(methodName)(rest)

let recursive traitMethodNamesOf (methods: List(TraitMethodInferenceDefinition)) =
    match methods with
        | [] -> []
        | TraitMethodInferenceDefinition { name = name } :: rest -> name :: traitMethodNamesOf(rest)

// A trait's methods in dictionary order: by name, as `planTraitEvidenceAbi` orders them.
let sortedTraitMethodNames (traitName: Str) (state: CoreLoweringState) =
    match traitDefinitionOf(traitName)(state) with
        | Some(TraitInferenceDefinition { methods = methods }) ->
            methods
            |> traitMethodNamesOf
            |> sortBy(given (left) ->
                given (right) -> Ashes.Text.compare(left)(right) <= 0)
        | None -> []

// `semanticType` with every occurrence of `parameter` (a trait's own parameter, or an
// implementation head's) replaced by `replacement`.
let recursive substituteTraitParameter (parameter: SemanticType) (replacement: SemanticType) (semanticType: SemanticType) =
    if semanticType == parameter
    then replacement
    else
        match semanticType with
            | SemList(element) ->
                element
                |> substituteTraitParameter(parameter)(replacement)
                |> SemList
            | SemTuple(elements) ->
                elements
                |> substituteTraitParameterIn(parameter)(replacement)
                |> SemTuple
            | SemFunction(argument, result, row) ->
                SemFunction(substituteTraitParameter(parameter)(replacement)(argument))(substituteTraitParameter(parameter)(replacement)(result))(row)
            | SemNamed(symbolId, name, arguments) ->
                arguments
                |> substituteTraitParameterIn(parameter)(replacement)
                |> SemNamed(symbolId)(name)
            | SemPointer(inner) ->
                inner
                |> substituteTraitParameter(parameter)(replacement)
                |> SemPointer
            | other -> other
and substituteTraitParameterIn (parameter: SemanticType) (replacement: SemanticType) (semanticTypes: List(SemanticType)) =
    match semanticTypes with
        | [] -> []
        | head :: rest -> substituteTraitParameter(parameter)(replacement)(head) :: substituteTraitParameterIn(parameter)(replacement)(rest)

let recursive substituteTraitParameters (pairs: List((SemanticType, SemanticType))) (semanticType: SemanticType) =
    match pairs with
        | [] -> semanticType
        | (parameter, replacement) :: rest ->
            semanticType
            |> substituteTraitParameter(parameter)(replacement)
            |> substituteTraitParameters(rest)

let recursive substituteTraitParametersIn (pairs: List((SemanticType, SemanticType))) (semanticTypes: List(SemanticType)) =
    match semanticTypes with
        | [] -> []
        | head :: rest -> substituteTraitParameters(pairs)(head) :: substituteTraitParametersIn(pairs)(rest)

// A method's type at a head: the trait method scheme's body with the trait's parameter replaced
// by the head type. Every standard trait takes one parameter.
let traitMethodTypeAt (traitName: Str) (methodName: Str) (headTypes: List(SemanticType)) (state: CoreLoweringState) =
    match (traitDefinitionOf(traitName)(state), headTypes) with
        | (Some(TraitInferenceDefinition { parameters = parameter :: [], methods = methods }), head :: []) ->
            match findTraitMethodDefinition(methodName)(methods) with
                | Some(TraitMethodInferenceDefinition { scheme = TypeScheme { body = body } }) ->
                    body
                    |> substituteTraitParameter(parameter)(head)
                    |> Some
                | None -> None
        | _ -> None

// The standard ordering defaults use the same predicate lowering in dictionaries
// as at direct operator sites. Keeping the mapped expression inside the typed method
// wrapper lets its head type select compare evidence; a bare Ord.compare reference
// in the default body has no ordinary value binding.
let orderingDefaultMethodBody (methodName: Str) (body: Expr) =
    (let left = ExprVar("__ordered_default_left")
    in
        let right = ExprVar("__ordered_default_right")
        in
            let comparison =
                match methodName with
                    | "less" ->
                        right
                        |> ExprLessThan(left)
                        |> Some
                    | "lessOrEqual" ->
                        right
                        |> ExprLessOrEqual(left)
                        |> Some
                    | "greater" ->
                        right
                        |> ExprGreaterThan(left)
                        |> Some
                    | "greaterOrEqual" ->
                        right
                        |> ExprGreaterOrEqual(left)
                        |> Some
                    | _ -> None
            in
                match comparison with
                    | Some(expression) ->
                        ExprLambda("__ordered_default_left")(ExprLambda("__ordered_default_right")(expression)(None))(None)
                    | None -> body)

// The method body an implementation supplies, or the trait's default for it.
let traitMethodBody (traitName: Str) (methodName: Str) (implementation: TraitImplementationInferenceDefinition) (state: CoreLoweringState) =
    match implementationMethodExpression(methodName)(implementation) with
        | Some(body) -> Some(body)
        | None ->
            match traitDefinitionOf(traitName)(state) with
                | Some(TraitInferenceDefinition { methods = methods }) ->
                    match findTraitMethodDefinition(methodName)(methods) with
                        | Some(TraitMethodInferenceDefinition { defaultImplementation = Some(body) }) ->
                            Some(if traitName == "Ord"
                            then orderingDefaultMethodBody(methodName)(body)
                            else body)
                        | Some(TraitMethodInferenceDefinition { defaultImplementation = None }) -> None
                        | None -> None
                | None -> None

let evidenceMethodBindingName (index: Int) (traitName: Str) (methodName: Str) = "__trait_method_" + Ashes.Text.fromInt(index) + "_" + traitName + "_" + methodName

let recursive collectHeadParameters (semanticType: SemanticType) (parameters: List(SemanticType)) =
    match semanticType with
        | SemParameter(_id, _name) ->
            if containsSemanticType(semanticType)(parameters)
            then parameters
            else semanticType :: parameters
        | SemList(element) -> collectHeadParameters(element)(parameters)
        | SemTuple(elements) -> collectHeadParametersIn(elements)(parameters)
        | SemFunction(argument, result, _row) ->
            parameters
            |> collectHeadParameters(argument)
            |> collectHeadParameters(result)
        | SemNamed(_symbolId, _name, arguments) -> collectHeadParametersIn(arguments)(parameters)
        | SemPointer(inner) -> collectHeadParameters(inner)(parameters)
        | _other -> parameters
and collectHeadParametersIn (semanticTypes: List(SemanticType)) (parameters: List(SemanticType)) =
    match semanticTypes with
        | [] -> parameters
        | head :: rest ->
            parameters
            |> collectHeadParameters(head)
            |> collectHeadParametersIn(rest)
and containsSemanticType (target: SemanticType) (semanticTypes: List(SemanticType)) =
    match semanticTypes with
        | [] -> false
        | head :: rest -> head == target || containsSemanticType(target)(rest)

let recursive constraintArgumentsOf (constraints: List(TraitConstraint)) (arguments: List(SemanticType)) =
    match constraints with
        | [] -> arguments
        | TraitConstraint { typeArguments = typeArguments } :: rest ->
            arguments
            |> append(typeArguments)
            |> constraintArgumentsOf(rest)

// Each head parameter of an implementation paired with a fresh type variable: the method body
// is lowered generically, its parameter types pinned to the head at those variables.
let recursive freshHeadParameterPairs (parameters: List(SemanticType)) (state: CoreLoweringState) =
    match parameters with
        | [] -> ([], state)
        | parameter :: rest ->
            match freshType(state) with
                | FreshType { state = freshState, semanticType = variable } ->
                    match freshHeadParameterPairs(rest)(freshState) with
                        | (pairs, nextState) -> ((parameter, variable) :: pairs, nextState)

// The evidence a generic method takes for one requirement: one hidden parameter per method of
// the required trait, in dictionary order, each recorded as active evidence for the requirement's
// type argument under its binding name, with the parameter's type.
type TraitEvidenceParameters =
    | entries: List((Str, SemanticType, Str, Str))
    | names: List(Str)
    | types: List(SemanticType)
    | error: Maybe(CoreLoweringError)

let recursive evidenceParametersForMethods (index: Int) (traitName: Str) (typeArgument: SemanticType) (methodNames: List(Str)) (state: CoreLoweringState) =
    match methodNames with
        | [] -> TraitEvidenceParameters(entries = [], names = [], types = [], error = None)
        | methodName :: rest ->
            match traitMethodTypeAt(traitName)(methodName)([typeArgument])(state) with
                | None ->
                    TraitEvidenceParameters(entries = [], names = [], types = [], error = typeArgument
                    |> UnsupportedCoreTraitDispatch(traitName)
                    |> Some)
                | Some(methodType) ->
                    match evidenceParametersForMethods(index)(traitName)(typeArgument)(rest)(state) with
                        | TraitEvidenceParameters { error = Some(_error) } as failed -> failed
                        | TraitEvidenceParameters { entries = entries, names = names, types = types, error = None } ->
                            let name = evidenceMethodBindingName(index)(traitName)(methodName)
                            in TraitEvidenceParameters(entries = (traitName, typeArgument, methodName, name) :: entries, names = name :: names, types = methodType :: types, error = None)

let recursive evidenceParametersFor (index: Int) (requirements: List(TraitConstraint)) (state: CoreLoweringState) =
    match requirements with
        | [] -> TraitEvidenceParameters(entries = [], names = [], types = [], error = None)
        | TraitConstraint { traitName = traitName, typeArguments = typeArgument :: [] } :: rest ->
            match evidenceParametersForMethods(index)(traitName)(typeArgument)(sortedTraitMethodNames(traitName)(state))(state) with
                | TraitEvidenceParameters { error = Some(_error) } as failed -> failed
                | TraitEvidenceParameters { entries = entries, names = names, types = types, error = None } ->
                    match evidenceParametersFor(index + 1)(rest)(state) with
                        | TraitEvidenceParameters { error = Some(_error) } as failed -> failed
                        | TraitEvidenceParameters { entries = restEntries, names = restNames, types = restTypes, error = None } -> TraitEvidenceParameters(entries = append(entries)(restEntries), names = append(names)(restNames), types = append(types)(restTypes), error = None)
        | TraitConstraint { traitName = traitName, typeArguments = typeArguments } :: _rest ->
            TraitEvidenceParameters(entries = [], names = [], types = [], error = SemTuple(typeArguments)
            |> UnsupportedCoreTraitDispatch(traitName)
            |> Some)

let recursive wrapEvidenceLambdas (names: List(Str)) (body: Expr) =
    match names with
        | [] -> body
        | name :: rest ->
            ExprLambda(name)(wrapEvidenceLambdas(rest)(body))(None)

let recursive wrapEvidenceTypes (types: List(SemanticType)) (resultType: SemanticType) =
    match types with
        | [] -> resultType
        | parameterType :: rest ->
            SemFunction(parameterType)(wrapEvidenceTypes(rest)(resultType))(None)

// A generic implementation method prepared for lowering: the body wrapped in one lambda per
// requirement method (the evidence it takes first, in requirement then dictionary order), the
// expected type of that wrapper at the head with fresh variables for the head's parameters,
// and the evidence entries the body dispatches through.
type PreparedTraitMethod =
    | wrapper: Expr
    | expectedType: SemanticType
    | evidence: List((Str, SemanticType, Str, Str))
    | requirements: List(TraitConstraint)
    | parameters: List((Str, SemanticType))
    | state: CoreLoweringState
    | error: Maybe(CoreLoweringError)

let recursive substituteConstraintArguments (pairs: List((SemanticType, SemanticType))) (constraints: List(TraitConstraint)) =
    match constraints with
        | [] -> []
        | TraitConstraint { traitName = traitName, typeArguments = typeArguments } :: rest -> TraitConstraint(traitName = traitName, typeArguments = substituteTraitParametersIn(pairs)(typeArguments)) :: substituteConstraintArguments(pairs)(rest)

let recursive namedHeadParameters (pairs: List((SemanticType, SemanticType))) =
    match pairs with
        | [] -> []
        | (SemParameter(_id, name), variable) :: rest -> (name, variable) :: namedHeadParameters(rest)
        | _pair :: rest -> namedHeadParameters(rest)

let prepareTraitMethod (traitName: Str) (methodName: Str) (implementation: TraitImplementationInferenceDefinition) (body: Expr) (state: CoreLoweringState) =
    match implementation with
        | TraitImplementationInferenceDefinition { typeArguments = headTypes, requirements = requirements } ->
            match freshHeadParameterPairs(collectHeadParametersIn(constraintArgumentsOf(requirements)(headTypes))([]))(state) with
                | (pairs, freshState) ->
                    let instantiatedRequirements = substituteConstraintArguments(pairs)(requirements)
                    in
                        match (traitMethodTypeAt(traitName)(methodName)(substituteTraitParametersIn(pairs)(headTypes))(freshState), evidenceParametersFor(0)(instantiatedRequirements)(freshState)) with
                            | (Some(methodType), TraitEvidenceParameters { entries = entries, names = names, types = types, error = None }) -> PreparedTraitMethod(wrapper = wrapEvidenceLambdas(names)(body), expectedType = wrapEvidenceTypes(types)(methodType), evidence = entries, requirements = instantiatedRequirements, parameters = namedHeadParameters(pairs), state = freshState, error = None)
                            | (_methodType, TraitEvidenceParameters { error = Some(error) }) -> PreparedTraitMethod(wrapper = body, expectedType = SemNever, evidence = [], requirements = [], parameters = [], state = freshState, error = Some(error))
                            | (None, _parameters) ->
                                PreparedTraitMethod(wrapper = body, expectedType = SemNever, evidence = [], requirements = [], parameters = [], state = freshState, error = SemTuple(headTypes)
                                |> UnsupportedCoreTraitDispatch(traitName)
                                |> Some)

// The active evidence for a trait at a type argument: the hidden parameter of the generic
// method body being lowered that carries the required trait's method for that type.
let recursive activeTraitMethod (traitName: Str) (typeArgument: SemanticType) (methodName: Str) (state: CoreLoweringState) (entries: List((Str, SemanticType, Str, Str))) =
    match entries with
        | [] -> None
        | (entryTrait, entryType, entryMethod, bindingName) :: rest ->
            if entryTrait == traitName && entryMethod == methodName && resolveType(state)(entryType) == resolveType(state)(typeArgument)
            then Some(bindingName)
            else activeTraitMethod(traitName)(typeArgument)(methodName)(state)(rest)

let hasActiveTraitEvidence (traitName: Str) (typeArgument: SemanticType) (state: CoreLoweringState) =
    match stateActiveTraitMethods(state) with
        | [] -> false
        | entries ->
            match sortedTraitMethodNames(traitName)(state) with
                | [] -> false
                | methodName :: _rest -> activeTraitMethod(traitName)(typeArgument)(methodName)(state)(entries) != None

let traitMethodLabelKeyOf (traitName: Str) (methodName: Str) (implementation: TraitImplementationInferenceDefinition) =
    match implementation with
        | TraitImplementationInferenceDefinition { typeArguments = headTypes } -> traitName + "/" + methodName + "/" + Ashes.Trait.Show.show(headTypes)

// The closure of a trait method for an evidence plan, stage 0's `BuildTraitDictionary` reduced
// to the one method a site selects: an instance's method is its implementation body lowered once
// per implementation head (or the trait's default), applied to the closures of every method of
// every requirement in turn; a parameter's method is the active evidence of the generic body
// being lowered.
//
// A plan's supertrait plans are carried but not turned into dictionary parameters. Only a method
// body that dispatches a supertrait method at the head's own type needs one, and at a concrete
// head that dispatch resolves its evidence straight from the environment instead. Where a generic
// head really would need it the body's dispatch reports `MissingCoreTraitEvidence` rather than
// lowering something wrong, so ignoring them here is a narrowing and not a hole. `Ord` is why it
// matters: it requires `Eq`, so before this every comparison operator failed here, with the
// `Ord.compare` its four default bodies actually call being a sibling method rather than a
// supertrait one.
let recursive buildTraitMethodClosure (plan: TraitEvidencePlan) (methodName: Str) lower (state: CoreLoweringState) =
    match plan with
        | TraitEvidenceParameter(TraitConstraint { traitName = traitName, typeArguments = typeArgument :: [] }) ->
            match state
            |> stateActiveTraitMethods
            |> activeTraitMethod(traitName)(typeArgument)(methodName)(state) with
                | Some(bindingName) ->
                    match state
                    |> withConsumerRequest(emptyConsumerRequest)
                    |> lower(ExprVar(bindingName)) with
                        | LoweredCoreValue { state = readState, temp = temp, semanticType = semanticType, error = None } ->
                            readState
                            |> withConsumerRequest(state.consumerRequest)
                            |> success(temp)(semanticType)
                        | failed -> failed
                | None ->
                    typeArgument
                    |> MissingCoreTraitEvidence(traitName)
                    |> failure(state)
        | TraitEvidenceInstance(TraitConstraint { traitName = traitName, typeArguments = goal :: [] }, implementation, requirementPlans, _supertraitPlans) ->
            match traitMethodBody(traitName)(methodName)(implementation)(state) with
                | None ->
                    goal
                    |> UnsupportedCoreTraitDispatch(traitName)
                    |> failure(state)
                | Some(body) ->
                    match prepareTraitMethod(traitName)(methodName)(implementation)(body)(state) with
                        | PreparedTraitMethod { state = failedState, error = Some(error) } -> failure(failedState)(error)
                        | PreparedTraitMethod { wrapper = wrapper, expectedType = expectedType, evidence = evidence, requirements = requirements, parameters = parameters, state = preparedState, error = None } ->
                            match lowerTraitMethodClosure(traitMethodLabelKeyOf(traitName)(methodName)(implementation))(goal)(expectedType)(evidence)(parameters)(wrapper)(lower)(preparedState) with
                                | LoweredCoreValue { state = closureState, temp = closureTemp, error = None } -> applyRequirementMethods(requirementPlans)(requirements)(closureTemp)(lower)(closureState)
                                | failed -> failed
        | TraitEvidenceInstance(TraitConstraint { traitName = traitName, typeArguments = typeArguments }, _implementation, _requirementPlans, _supertraitPlans) ->
            SemTuple(typeArguments)
            |> UnsupportedCoreTraitDispatch(traitName)
            |> failure(state)
        | TraitEvidenceParameter(TraitConstraint { traitName = traitName, typeArguments = typeArguments }) ->
            SemTuple(typeArguments)
            |> UnsupportedCoreTraitDispatch(traitName)
            |> failure(state)
and applyRequirementMethods (plans: List(TraitEvidencePlan)) (requirements: List(TraitConstraint)) (closureTemp: Int) lower (state: CoreLoweringState) =
    match (plans, requirements) with
        | ([], _requirements) -> success(closureTemp)(SemNever)(state)
        | (plan :: restPlans, TraitConstraint { traitName = traitName } :: restRequirements) ->
            match applyRequirementMethodList(plan)(sortedTraitMethodNames(traitName)(state))(closureTemp)(lower)(state) with
                | LoweredCoreValue { state = appliedState, temp = appliedTemp, error = None } -> applyRequirementMethods(restPlans)(restRequirements)(appliedTemp)(lower)(appliedState)
                | failed -> failed
        | (plan :: _restPlans, []) ->
            match plan with
                | TraitEvidenceParameter(TraitConstraint { traitName = traitName, typeArguments = typeArguments }) ->
                    SemTuple(typeArguments)
                    |> UnsupportedCoreTraitDispatch(traitName)
                    |> failure(state)
                | TraitEvidenceInstance(TraitConstraint { traitName = traitName, typeArguments = typeArguments }, _implementation, _requirementPlans, _supertraitPlans) ->
                    SemTuple(typeArguments)
                    |> UnsupportedCoreTraitDispatch(traitName)
                    |> failure(state)
and applyRequirementMethodList (plan: TraitEvidencePlan) (methodNames: List(Str)) (closureTemp: Int) lower (state: CoreLoweringState) =
    match methodNames with
        | [] -> success(closureTemp)(SemNever)(state)
        | methodName :: rest ->
            match buildTraitMethodClosure(plan)(methodName)(lower)(state) with
                | LoweredCoreValue { state = argumentState, temp = argumentTemp, error = None } ->
                    match freshTemp(argumentState) with
                        | FreshTemp { state = callState, temp = appliedTemp } ->
                            callState
                            |> emit(CallClosure(appliedTemp)(closureTemp)(argumentTemp)(-1))
                            |> applyRequirementMethodList(plan)(rest)(appliedTemp)(lower)
                | failed -> failed

// A binary trait method at an operand type, stage 0's `RecordMappedBinaryTrait` past its
// primitive specialization: the `Eq(T)` evidence is resolved against the program's
// implementations, the selected method's closure is built for the plan and called on the two
// operand temps. An unsupplied `notEqual` on a concrete instance is the default method's
// `!Eq.equal(left)(right)` without the lambda around it.
//
// `Ord`'s four comparison methods are the same case one step larger. Their defaults call
// `Ord.compare` and match the `Ordering` it returns, and a sibling-method call inside a default
// body has no lowering binding to resolve — `Ord.compare` names no value, and the head type it
// would dispatch at is not pinned on the default lambda's parameters. So they are emitted the way
// `notEqual` is: dispatch `compare`, read the result's tag, and test it against the constructors
// that default accepts. `Unordered` is why the accepting sets are listed rather than derived from
// tag order: `greaterOrEqual` is `Greater` or `Equal`, not "at least `Equal`".
let orderingPredicateConstructors (traitName: Str) (methodName: Str) =
    if traitName != "Ord"
    then []
    else
        match methodName with
            | "less" -> ["Less"]
            | "lessOrEqual" -> ["Less", "Equal"]
            | "greater" -> ["Greater"]
            | "greaterOrEqual" -> ["Greater", "Equal"]
            | _ -> []

// The tags the accepting constructors carry, or `None` if any of them is unknown to this program.
// A missing tag must not degrade to an arm that never matches: that would answer every comparison
// `false` instead of reporting that `Ordering` never reached lowering.
let recursive orderingPredicateTags (names: List(Str)) (state: CoreLoweringState) =
    match names with
        | [] -> Some([])
        | name :: rest ->
            match constructorLayout(name)(state) with
                | None -> None
                | Some(CoreConstructorLayout { tag = tag }) ->
                    match orderingPredicateTags(rest)(state) with
                        | None -> None
                        | Some(restTags) -> Some(tag :: restTags)

// Ors one tag equality test into the running result and carries the new accumulator temp. The
// predicate stays branchless deliberately: it is emitted inside whatever expression the comparison
// appears in, and a label there would split a block the surrounding lowering built as straight-line
// code — a partially applied call whose argument this is faults when it is cut in two.
let recursive emitOrderingTagTests (tagTemp: Int) (accumulatorTemp: Int) (tags: List(Int)) (state: CoreLoweringState) =
    match tags with
        | [] -> (state, accumulatorTemp)
        | tag :: rest ->
            match reserveCoreTemps(3)(state) with
                | ReservedCoreTemps { state = tempState, first = tagConstTemp } ->
                    tempState
                    |> emit(LoadConstInt(tagConstTemp)(tag))
                    |> emit(CmpIntEq(tagConstTemp + 1)(tagTemp)(tagConstTemp))
                    |> emit(OrInt(tagConstTemp + 2)(accumulatorTemp)(tagConstTemp + 1))
                    |> emitOrderingTagTests(tagTemp)(tagConstTemp + 2)(rest)

let emitOrderingPredicate (orderingTemp: Int) (names: List(Str)) (state: CoreLoweringState) =
    match orderingPredicateTags(names)(state) with
        | None -> failure(state)(UnknownLoweringBinding("Ordering"))
        | Some(tags) ->
            match reserveCoreTemps(2)(state) with
                | ReservedCoreTemps { state = tempState, first = falseTemp } ->
                    match tempState
                    |> emit(LoadConstBool(falseTemp)(false))
                    |> emit(GetAdtTag(falseTemp + 1)(orderingTemp))
                    |> emitOrderingTagTests(falseTemp + 1)(falseTemp)(tags) with
                        | (resultState, resultTemp) -> success(resultTemp)(SemBool)(resultState)

let recursive emitCoreTraitBinaryDispatch (traitName: Str) (methodName: Str) lower (binary: LoweredCoreBinary) =
    match binary with
        | LoweredCoreBinary { state = state, leftTemp = leftTemp, leftType = operandType, rightTemp = rightTemp, error = None } ->
            match state
            |> stateTraitEnvironment
            |> resolveTraitEvidence(TraitConstraint(traitName = traitName, typeArguments = [operandType])) with
                | TraitEvidenceResolution { plan = Some(TraitEvidenceInstance(_goal, implementation, _requirementPlans, _supertraitPlans) as plan) } ->
                    match (methodName, implementationMethodExpression(methodName)(implementation)) with
                        | ("notEqual", None) ->
                            match emitCoreTraitBinaryDispatch(traitName)("equal")(lower)(binary) with
                                | LoweredCoreValue { state = equalState, temp = equalTemp, error = None } -> emitBoolNegation(equalTemp)(equalState)
                                | failed -> failed
                        | (_unsupplied, None) ->
                            match orderingPredicateConstructors(traitName)(methodName) with
                                | [] -> emitCoreTraitPlanCalls(plan)(methodName)(lower)(binary)
                                | names ->
                                    match emitCoreTraitBinaryDispatch(traitName)("compare")(lower)(binary) with
                                        | LoweredCoreValue { state = compareState, temp = compareTemp, error = None } -> emitOrderingPredicate(compareTemp)(names)(compareState)
                                        | failed -> failed
                        | _ -> emitCoreTraitPlanCalls(plan)(methodName)(lower)(binary)
                | TraitEvidenceResolution { plan = Some(plan) } -> emitCoreTraitPlanCalls(plan)(methodName)(lower)(binary)
                | TraitEvidenceResolution { plan = None } ->
                    operandType
                    |> MissingCoreTraitEvidence(traitName)
                    |> failure(state)
        | LoweredCoreBinary { state = state, error = Some(error) } -> failure(state)(error)
and emitCoreTraitPlanCalls (plan: TraitEvidencePlan) (methodName: Str) lower (binary: LoweredCoreBinary) =
    match binary with
        | LoweredCoreBinary { state = state, leftTemp = leftTemp, leftType = operandType, rightTemp = rightTemp, error = None } ->
            match buildTraitMethodClosure(plan)(methodName)(lower)(state) with
                | LoweredCoreValue { state = closureState, temp = closureTemp, error = None } -> emitTraitMethodCalls(closureTemp)(leftTemp)(rightTemp)(operandType)(closureState)
                | failed -> failed
        | LoweredCoreBinary { state = state, error = Some(error) } -> failure(state)(error)

// Past the primitive comparisons of `emitResolvedCoreEquality`: the operands were unified before
// the comparison was chosen (`unifyEqualityOperands`), so what is left is one operand type,
// concrete or the type argument of an active requirement, which dispatches through `Eq`.
let emitCoreTraitEquality name lower binary =
    match binary with
        | LoweredCoreBinary { error = None } ->
            binary |> emitCoreTraitBinaryDispatch("Eq")(if name == "=="
            then "equal"
            else "notEqual")(lower)
        | LoweredCoreBinary { state = state, error = Some(error) } -> failure(state)(error)

// The `Ord` method each comparison operator maps to, the same pairing stage 0's mapped binary
// traits use.
let orderedTraitMethod name =
    match name with
        | "<" -> "less"
        | "<=" -> "lessOrEqual"
        | ">" -> "greater"
        | _ -> "greaterOrEqual"

// Past the primitive comparisons, exactly as `emitCoreTraitEquality` is past equality's: one
// operand type, concrete or the type argument of an active requirement, dispatching through `Ord`.
let emitCoreTraitOrdered name lower binary =
    match binary with
        | LoweredCoreBinary { error = None } ->
            binary |> emitCoreTraitBinaryDispatch("Ord")(orderedTraitMethod(name))(lower)
        | LoweredCoreBinary { state = state, error = Some(error) } -> failure(state)(error)

let emitResolvedCoreOrdered name intKind uintKind floatKind lower binary =
    match binary with
        | LoweredCoreBinary { leftType = SemInt, rightType = SemInt } -> emitCoreBinaryTarget(intKind)(SemBool)(binary)
        | LoweredCoreBinary { leftType = SemRune, rightType = SemRune } -> emitCoreBoolBinary(intKind)(binary)
        | LoweredCoreBinary { leftType = SemUInt(leftBits), rightType = SemUInt(rightBits) } ->
            if leftBits == rightBits
            then emitCoreBoolBinary(uintKind)(binary)
            else operatorMismatch(name)(binary)
        | LoweredCoreBinary { leftType = SemFloat, rightType = SemFloat } -> emitCoreBoolBinary(floatKind)(binary)
        | LoweredCoreBinary { leftType = SemBigInt, rightType = SemBigInt } -> emitCoreBigIntComparison(intKind)(binary)
        | _ -> emitCoreTraitOrdered(name)(lower)(binary)

// Stage 0's `LowerStrEquality`: the primitive comparison borrows both strings, as the trait method
// does, so a string no binding owns (a call's result) is released after it.
let emitCoreStringEquality kind (binary: LoweredCoreBinary) =
    match emitCoreBoolBinary(kind)(binary) with
        | LoweredCoreValue { state = state, temp = temp, error = None } ->
            state
            |> releaseFreshComparisonOperand(SemString)(binary.leftTemp)
            |> releaseFreshComparisonOperand(SemString)(binary.rightTemp)
            |> success(temp)(SemBool)
        | failed -> failed

let emitResolvedCoreEquality name intKind floatKind stringKind lower binary =
    match binary with
        | LoweredCoreBinary { leftType = SemInt, rightType = SemInt } -> emitCoreBinaryTarget(intKind)(SemBool)(binary)
        | LoweredCoreBinary { leftType = SemRune, rightType = SemRune } -> emitCoreBoolBinary(intKind)(binary)
        | LoweredCoreBinary { leftType = SemBool, rightType = SemBool } -> emitCoreBoolBinary(intKind)(binary)
        | LoweredCoreBinary { leftType = SemUInt(leftBits), rightType = SemUInt(rightBits) } ->
            if leftBits == rightBits
            then emitCoreBoolBinary(intKind)(binary)
            else operatorMismatch(name)(binary)
        | LoweredCoreBinary { leftType = SemFloat, rightType = SemFloat } -> emitCoreBoolBinary(floatKind)(binary)
        | LoweredCoreBinary { leftType = SemBigInt, rightType = SemBigInt } -> emitCoreBigIntComparison(intKind)(binary)
        | LoweredCoreBinary { leftType = SemString, rightType = SemString } -> emitCoreStringEquality(stringKind)(binary)
        | _ -> emitCoreTraitEquality(name)(lower)(binary)

// The one-argument counterpart of `emitTraitMethodCalls`: a unary trait method takes its operand
// directly rather than through a curried pair.
// A trait method is an implementation's closure applied in place, outside the ordinary call path
// that settles a reference-counted result. Without a request in its ownership word the closure
// hands its result over owned, and the caller, reading the call as an arena value, released it
// nowhere: the call asks for the result in the arena (bit 1), so the method copies a
// reference-counted result out and releases the original itself (stage 0's
// `RequestsArenaTraitMethodResult`).
let requestsArenaTraitMethodResult (resultType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(resultType) with
        | SemFunction(_argument, _result, _row) -> false
        | resolved -> !resultSurvivesReset(resolved)(state) && !containsUnresolvedLayout(resolved)(state) && !isGeneralRcValueType(resolved)(state) && hasCallCopyOut(resolved)(state)

let emitTraitMethodCall (closureTemp: Int) (operandTemp: Int) (resultType: SemanticType) (state: CoreLoweringState) =
    match if requestsArenaTraitMethodResult(resultType)(state)
    then emitRequestWord(-1)(state)
    else (state, -1) with
        | (requested, wordTemp) ->
            match freshTemp(requested) with
                | FreshTemp { state = callState, temp = target } ->
                    callState
                    |> emit(CallClosure(target)(closureTemp)(operandTemp)(wordTemp))
                    |> success(target)(resultType)

// A trait goal's site, named the same way by every pass: the passes lower one source, so the
// innermost enclosing span and what is asked for there identify the same site in all of them.
// Runtime machinery carries no span and is never deferred.
let traitGoalKey (traitName: Str) (methodName: Str) (state: CoreLoweringState) =
    match state.currentSpan with
        | Some(TextSpan { start = start, end = end }) -> Some(Ashes.Text.fromInt(start) + ":" + Ashes.Text.fromInt(end) + ":" + traitName + ":" + methodName)
        | None -> None

// The operand type a goal dispatches on, with an earlier pass's proof pinned when the type is
// still open. Unifying rather than substituting for the evidence alone keeps the rest of inference
// consistent with the choice.
let bindTraitGoalProof operandType proof (state: CoreLoweringState) =
    match proof with
        | None -> (state, operandType)
        | Some(proven) ->
            match bindType(operandType)(proven)(state) with
                | (pinned, None) -> (pinned, resolveType(pinned)(operandType))
                | (_failed, Some(_error)) -> (state, operandType)

// A discovery proof can name the lexical dictionary whose type matched after
// the rest of the function was inferred. Resolve that binding in the replay frame:
// type-variable ids from a different lowering pass are not interchangeable.
let recursive activeOperandForBinding binding entries =
    match entries with
        | [] -> None
        | (_trait, operand, _method, name) :: rest ->
            if name == binding
            then Some(operand)
            else activeOperandForBinding(binding)(rest)

let bindGoalAnswer operand answer state =
    match answer with
        | Some(ProvenOperandType(proven)) -> bindTraitGoalProof(operand)(Some(proven))(state)
        | Some(ProvenAmbientMethod(binding)) ->
            bindTraitGoalProof(operand)(state
            |> stateActiveTraitMethods
            |> activeOperandForBinding(binding))(state)
        | None -> (state, operand)

let pinProvenTraitGoal (traitName: Str) (methodName: Str) (operandType: SemanticType) (state: CoreLoweringState) =
    match (freeTypeVariables(operandType), traitGoalKey(traitName)(methodName)(state)) with
        | ([], _key) -> (state, operandType)
        | (_variables, None) -> (state, operandType)
        | (_variables, Some(key)) ->
            let proofs = stateProvenTraitGoals(state)
            in
                match Ashes.Collection.Map.getStr(key)(proofs) with
                    | Some(SharedTraitGoalProof(proven)) -> bindGoalAnswer(operandType)(Some(proven))(state)
                    | Some(OrderedTraitGoalProofs(proof :: remaining)) ->
                        state
                        |> withStateProvenTraitGoals(Ashes.Collection.Map.setStr(key)(OrderedTraitGoalProofs(remaining))(proofs))
                        |> bindGoalAnswer(operandType)(proof)
                    | _ -> (state, operandType)

// Whether a plan's evidence is a hidden parameter of a generic body this body does not carry.
// `resolveTraitEvidence` answers with that shape for an operand type inference has not settled
// too, which at the site is indistinguishable from a real requirement's type argument.
let traitPlanLacksActiveEvidence (plan: TraitEvidencePlan) (methodName: Str) (state: CoreLoweringState) =
    match plan with
        | TraitEvidenceParameter(TraitConstraint { traitName = traitName, typeArguments = typeArgument :: [] }) ->
            activeTraitMethod(traitName)(typeArgument)(methodName)(state)(stateActiveTraitMethods(state)) == None
        | _plan -> false

// The site to record when no evidence answers a goal. A pass lowering without proofs defers an
// operand inference may still resolve; with proofs pinned, an operand still open is ambiguous to
// every pass, which is the only case that is genuinely ambiguous.
let unprovenTraitGoalKey (traitName: Str) (methodName: Str) (operandType: SemanticType) (state: CoreLoweringState) =
    match (stateTraitGoalsPinned(state), freeTypeVariables(operandType)) with
        | (true, _variables) -> None
        | (_pinned, []) -> None
        | (_pinned, _variables) -> traitGoalKey(traitName)(methodName)(state)

// The value lowered at a deferred site: the goal joins the pass's record, and the site gets a
// constant of the method's own result type. A deferring pass exists to resolve types, and its
// instructions are discarded, so the constant is never run.
let deferTraitGoalWithMethods candidates (key: Str) (operandType: SemanticType) (resultType: SemanticType) (state: CoreLoweringState) =
    (let recorded =
        state
        |> withStatePendingTraitGoals((key, operandType, candidates) :: statePendingTraitGoals(state))
        |> observePendingGoal(false)(key)(operandType)
    in
        match resultType with
            | SemString -> lowerString("")(recorded)
            | _ ->
                lowerConstant(given (target) -> LoadConstInt(target)(0))(resultType)(recorded))

let deferTraitGoal key operand result state = deferTraitGoalWithMethods([])(key)(operand)(result)(state)

let recursive matchingMethodCandidates traitName methodName methods =
    match methods with
        | [] -> []
        | (candidateTrait, operand, candidateMethod, binding) :: rest ->
            let tail = matchingMethodCandidates(traitName)(methodName)(rest)
            in
                if candidateTrait == traitName && candidateMethod == methodName
                then (binding, operand) :: tail
                else tail

// The dictionaries of the generic body being lowered that can answer a whole-dictionary goal on
// `traitName`: one entry per active dictionary of that trait, named by its first method in
// dictionary order. Once the goal's type resolves to one of their operands, the goal no longer holds
// that type out of the enclosing generalization, and a later pass answers it from that dictionary.
let dictionaryGoalCandidates (traitName: Str) (state: CoreLoweringState) =
    match sortedTraitMethodNames(traitName)(state) with
        | [] -> []
        | methodName :: _rest ->
            state
            |> stateActiveTraitMethods
            |> matchingMethodCandidates(traitName)(methodName)

let deferMethodGoal traitName methodName key operand result state =
    deferTraitGoalWithMethods(state
    |> stateActiveTraitMethods
    |> matchingMethodCandidates(traitName)(methodName))(key)(operand)(result)(state)

// The caller's dictionary carries the actual method arrow. Its codomain can be
// the only occurrence of the trait parameter, as in first : Unit -> a.
let emitSelectedUnaryMethod plan closureTemp closureType operandTemp resultType state =
    match plan with
        | TraitEvidenceParameter(TraitConstraint { traitName = traitName }) ->
            match resolveType(state)(closureType) with
                | SemFunction(_argument, suppliedResult, _row) ->
                    match bindType(resultType)(suppliedResult)(state) with
                        | (bound, None) ->
                            emitTraitMethodCall(closureTemp)(operandTemp)(resolveType(bound)(resultType))(bound)
                        | (failed, Some(error)) -> failure(failed)(error)
                | _ ->
                    closureType
                    |> UnsupportedCoreTraitDispatch(traitName)
                    |> failure(state)
        | _ -> emitTraitMethodCall(closureTemp)(operandTemp)(resultType)(state)

// `Ashes.Trait.Show.show(x)` and its unary siblings, the counterpart of the mapped binary operators:
// resolve the trait's evidence for the operand's own type, build the method's closure for that plan,
// and call it. A concrete operand resolves an instance directly; inside a generic body the operand's
// type is the type argument of an active requirement and resolves to that parameter's evidence.
let emitCoreTraitUnaryDispatch (traitName: Str) (methodName: Str) (resultType: SemanticType) lower (operandTemp: Int) (operandType: SemanticType) (state: CoreLoweringState) =
    match if hasActiveTraitEvidence(traitName)(operandType)(state)
    then (state, operandType)
    else pinProvenTraitGoal(traitName)(methodName)(operandType)(state) with
        | (pinnedState, pinnedType) ->
            match pinnedState
            |> stateTraitEnvironment
            |> resolveTraitEvidence(TraitConstraint(traitName = traitName, typeArguments = [pinnedType])) with
                | TraitEvidenceResolution { plan = Some(plan) } ->
                    match (traitPlanLacksActiveEvidence(plan)(methodName)(pinnedState), unprovenTraitGoalKey(traitName)(methodName)(pinnedType)(pinnedState)) with
                        | (true, Some(key)) -> deferMethodGoal(traitName)(methodName)(key)(pinnedType)(resultType)(pinnedState)
                        | _resolvable ->
                            match buildTraitMethodClosure(plan)(methodName)(lower)(pinnedState) with
                                | LoweredCoreValue { state = closureState, temp = closureTemp, semanticType = closureType, error = None } -> emitSelectedUnaryMethod(plan)(closureTemp)(closureType)(operandTemp)(resultType)(closureState)
                                | failed -> failed
                | TraitEvidenceResolution { plan = None } ->
                    match unprovenTraitGoalKey(traitName)(methodName)(pinnedType)(pinnedState) with
                        | Some(key) -> deferMethodGoal(traitName)(methodName)(key)(pinnedType)(resultType)(pinnedState)
                        | None ->
                            pinnedType
                            |> MissingCoreTraitEvidence(traitName)
                            |> failure(pinnedState)

let emitResolvedCoreBinary operator lower binary =
    match operator with
        | CoreAddOperator -> emitResolvedCoreAdd(binary)
        | CoreSubtractOperator -> emitResolvedCoreSubtract(binary)
        | CoreMultiplyOperator -> emitResolvedCoreMultiply(binary)
        | CoreDivideOperator -> emitResolvedCoreDivide(binary)
        | CoreModuloOperator -> emitResolvedCoreModulo(binary)
        | CoreBitwiseAndOperator -> emitResolvedCoreBitwise("&")(AndInt)(binary)
        | CoreBitwiseOrOperator -> emitResolvedCoreBitwise("|")(OrInt)(binary)
        | CoreBitwiseXorOperator -> emitResolvedCoreBitwise("^")(XorInt)(binary)
        | CoreShiftLeftOperator -> emitResolvedCoreShift("<<")(ShlInt)(binary)
        | CoreShiftRightOperator -> emitResolvedCoreShift(">>")(ShrInt)(binary)
        | CoreGreaterOperator -> emitResolvedCoreOrdered(">")(CmpIntGt)(CmpUIntGt)(CmpFloatGt)(lower)(binary)
        | CoreGreaterOrEqualOperator -> emitResolvedCoreOrdered(">=")(CmpIntGe)(CmpUIntGe)(CmpFloatGe)(lower)(binary)
        | CoreLessOperator -> emitResolvedCoreOrdered("<")(CmpIntLt)(CmpUIntLt)(CmpFloatLt)(lower)(binary)
        | CoreLessOrEqualOperator -> emitResolvedCoreOrdered("<=")(CmpIntLe)(CmpUIntLe)(CmpFloatLe)(lower)(binary)
        | CoreEqualOperator -> emitResolvedCoreEquality("==")(CmpIntEq)(CmpFloatEq)(CmpStrEq)(lower)(binary)
        | CoreNotEqualOperator -> emitResolvedCoreEquality("!=")(CmpIntNe)(CmpFloatNe)(CmpStrNe)(lower)(binary)

let setBinaryLeft temp semanticType (binary: LoweredCoreBinary) state = binary with state = state, leftTemp = temp, leftType = semanticType

let coerceCoreFloatZero binary state =
    match freshTemp(state) with
        | FreshTemp { state = zeroState, temp = zero } ->
            zeroState
            |> emit(LoadConstFloat(zero)(0.0))
            |> setBinaryLeft(zero)(SemFloat)(binary)

let coerceCoreUIntZero bits binary state =
    match freshTemp(state) with
        | FreshTemp { state = zeroState, temp = zero } ->
            zeroState
            |> emit(LoadConstInt(zero)(0))
            |> setBinaryLeft(zero)(SemUInt(bits))(binary)

let coerceCoreBigIntZero binary state =
    match reserveCoreTemps(2)(state) with
        | ReservedCoreTemps { state = zeroState, first = zero } ->
            zeroState
            |> emit(LoadConstInt(zero)(0))
            |> emit(BigIntFromInt(zero + 1)(zero)(false))
            |> setBinaryLeft(zero + 1)(SemBigInt)(binary)

let coerceCoreNegationZero binary =
    match binary with
        | LoweredCoreBinary { state = state, leftType = SemInt, rightType = rightType, error = None } ->
            match resolveType(state)(rightType) with
                | SemFloat -> coerceCoreFloatZero(binary)(state)
                | SemBigInt -> coerceCoreBigIntZero(binary)(state)
                | SemUInt(bits) -> coerceCoreUIntZero(bits)(binary)(state)
                | _ -> binary
        | _ -> binary

let recursive isCoreZeroExpression expression =
    match expression with
        | ExprAt(_span, inner) -> isCoreZeroExpression(inner)
        | ExprNode(_node, inner) -> isCoreZeroExpression(inner)
        | ExprInt(0) -> true
        | _ -> false

// `+` over two still-unresolved type variables (`first + second` inside a nested recursive group
// whose element type only settles later, like `Text.join`'s reduce learning `Str` from `""`) must
// not be defaulted eagerly: the operands are unified with each other, an `AddInt` is emitted
// speculatively and recorded under its target temp (temps survive `pruneDeadCaptures`, positions
// do not), the shared variable flows on as the result type, and `buildProgram` swaps the
// instruction for the resolved type's real form once the substitution is final. The variable also
// joins the body's unresolved call results, as the deferred `==` does, so a body that resolves it
// is lowered again against the resolved type: a string `+` lowered against the open types is an
// arena concatenation that does not release the counted copy of an operand read from a call result.
// Every other operand shape still resolves immediately through `resolvedCoreBinary`.
let emitDeferredCoreArithmetic (speculative: Int -> Int -> Int -> IrInstructionKind) binary =
    match binary with
        | LoweredCoreBinary { state = state, leftTemp = left, leftType = leftType, rightTemp = right, rightType = rightType, error = None } ->
            match bindType(leftType)(rightType)(state) with
                | (failedState, Some(error)) -> failure(failedState)(error)
                | (typedState, None) ->
                    match freshTemp(typedState) with
                        | FreshTemp { state = targetState, temp = target } ->
                            match targetState with
                                | CoreLoweringState { functionState = CoreFunctionState { pendingOperatorDefaults = pending }, ownerState = CoreOwnerState { unresolvedCallResults = unresolved } } ->
                                    let recorded =
                                        targetState
                                        |> withStatePendingOperatorDefaults((target, Ashes.Internal.deepCopy(leftType)) :: pending)
                                        |> withStateUnresolvedCallResults(Ashes.Internal.deepCopy(leftType) :: unresolved)
                                    in
                                        recorded
                                        |> emit(speculative(target)(left)(right))
                                        |> success(target)(leftType)
        | LoweredCoreBinary { state = state, error = Some(error) } -> failure(state)(error)

let emitDeferredCoreAdd binary = emitDeferredCoreArithmetic(AddInt)(binary)

// `==` and `!=` over two still-unresolved type variables (`lit == ch` before the constructor
// field that pins `lit` to `Str`) take the same path as `+`: the operands are unified with each
// other, an integer comparison is emitted speculatively and recorded under its target temp for
// the finalization rewrite, and the shared variable joins the body's unresolved call results, so
// a body that resolves it later is lowered a second time against the resolved type, as stage 0
// re-lowers a binding whose trait-mapped comparison needed a late type hint. A variable no body
// resolves seals to the integer comparison. The site is recorded as an equality goal too: a
// variable only a later use outside the body resolves (the arguments a lambda is applied to) may
// settle on a type the integer comparison does not compare, which the program's passes answer
// by lowering again with that type pinned at the site.
let recordEqualityGoal (methodName: Str) (operandType: SemanticType) (state: CoreLoweringState) =
    match traitGoalKey("Eq")(methodName + ":structural")(state) with
        | Some(key) ->
            state
            |> withStatePendingEqualityGoals((key, operandType) :: statePendingEqualityGoals(state))
            |> observePendingGoal(true)(key)(operandType)
        | None -> state

let emitDeferredCoreEquality (methodName: Str) kind binary =
    match binary with
        | LoweredCoreBinary { state = state, leftTemp = left, leftType = leftType, rightTemp = right, rightType = rightType, error = None } ->
            match bindType(leftType)(rightType)(state) with
                | (failedState, Some(error)) -> failure(failedState)(error)
                | (typedState, None) ->
                    match freshTemp(typedState) with
                        | FreshTemp { state = targetState, temp = target } ->
                            match targetState with
                                | CoreLoweringState { functionState = CoreFunctionState { pendingOperatorDefaults = pending }, ownerState = CoreOwnerState { unresolvedCallResults = unresolved } } ->
                                    let recorded =
                                        targetState
                                        |> withStatePendingOperatorDefaults((target, Ashes.Internal.deepCopy(leftType)) :: pending)
                                        |> withStateUnresolvedCallResults(Ashes.Internal.deepCopy(leftType) :: unresolved)
                                        |> recordEqualityGoal(methodName)(Ashes.Internal.deepCopy(leftType))
                                    in
                                        recorded
                                        |> emit(kind(target)(left)(right))
                                        |> success(target)(SemBool)
        | LoweredCoreBinary { state = state, error = Some(error) } -> failure(state)(error)

let equalityGoalMethod operator =
    match operator with
        | CoreEqualOperator -> Some("equal")
        | CoreNotEqualOperator -> Some("notEqual")
        | _ -> None

let deferredCoreOperatorEmitter operator =
    match operator with
        | CoreAddOperator -> Some(emitDeferredCoreAdd)
        | CoreSubtractOperator ->
            SubInt
            |> emitDeferredCoreArithmetic
            |> Some
        | CoreMultiplyOperator ->
            MulInt
            |> emitDeferredCoreArithmetic
            |> Some
        | CoreEqualOperator ->
            CmpIntEq
            |> emitDeferredCoreEquality("equal")
            |> Some
        | CoreNotEqualOperator ->
            CmpIntNe
            |> emitDeferredCoreEquality("notEqual")
            |> Some
        | _ -> None

// Stage 0 unifies the two operands of a trait-mapped comparison before anything else: a side
// whose type is still a variable takes the other side's type rather than the numeric default
// the arithmetic operators fall back to. Operands that cannot be unified are the operator's
// own mismatch.
let bindEqualityOperands (name: Str) (leftType: SemanticType) (rightType: SemanticType) (binary: LoweredCoreBinary) (state: CoreLoweringState) =
    match bindType(leftType)(rightType)(state) with
        | (unifiedState, None) -> binary with state = unifiedState
        | (failedState, Some(_error)) ->
            rightType
            |> resolveType(failedState)
            |> CoreOperatorTypeMismatch(name)(resolveType(failedState)(leftType))
            |> failedCoreBinary(failedState)

// Whether the generic body being lowered carries evidence for the operator's trait at a still
// unresolved operand type: two variable operands then dispatch through that evidence rather
// than take the speculative integer comparison.
let activeEvidenceCoversOperator operator (operandType: SemanticType) (state: CoreLoweringState) =
    match operator with
        | CoreEqualOperator -> hasActiveTraitEvidence("Eq")(operandType)(state)
        | CoreNotEqualOperator -> hasActiveTraitEvidence("Eq")(operandType)(state)
        | _ -> false

let resolveCoreBinaryOwnTypes (binary: LoweredCoreBinary) =
    match binary with
        | LoweredCoreBinary { state = state, error = None } -> resolveCoreBinaryTypes(state)(binary)
        | failed -> failed

let unifyEqualityOperands operator (binary: LoweredCoreBinary) =
    match (operator, binary) with
        | (CoreEqualOperator, LoweredCoreBinary { state = state, leftType = leftType, rightType = rightType, error = None }) -> bindEqualityOperands("==")(leftType)(rightType)(binary)(state)
        | (CoreNotEqualOperator, LoweredCoreBinary { state = state, leftType = leftType, rightType = rightType, error = None }) -> bindEqualityOperands("!=")(leftType)(rightType)(binary)(state)
        | _ -> binary

let coreOrderedOperatorName operator =
    match operator with
        | CoreGreaterOperator -> Some(">")
        | CoreGreaterOrEqualOperator -> Some(">=")
        | CoreLessOperator -> Some("<")
        | CoreLessOrEqualOperator -> Some("<=")
        | _ -> None

// The operand type the standalone inference recorded for the operator node being lowered, as
// lowering writes it, when it holds no type variable. A generic body's operator keeps its
// quantified variable there, and a node lowering synthesized has no identity; both answer None.
let recordedOperandType (state: CoreLoweringState) =
    match (stateNodeTypes(state), state.currentNode) with
        | (Some(table), Some(node)) ->
            match nodeOperatorAt(node)(table) with
                | Some(NodeOperator { operandType = operandType }) ->
                    match translateRecordedType(state)(operandType) with
                        | Ok(translated) ->
                            if freeTypeVariables(translated) == []
                            then Some(translated)
                            else None
                        | Error(_message) -> None
                | None -> None
        | _ -> None

// The operand type recorded for the operator node being lowered, closed by the variables the
// program's uses settle, unless the body being lowered carries the operator trait's evidence for
// `operandType`, which then dispatches through it.
let recordedSettledOperandType (operandType: SemanticType) (state: CoreLoweringState) =
    match (stateNodeTypes(state), state.currentNode) with
        | (Some(table), Some(node)) ->
            match nodeOperatorAt(node)(table) with
                | Some(NodeOperator { traitName = traitName, operandType = recorded }) ->
                    if hasActiveTraitEvidence(traitName)(resolveType(state)(operandType))(state)
                    then None
                    else recordedSettledType(recorded)(state)
                | None -> None
        | _ -> None

let recursive bindRecordedOperands (recorded: SemanticType) (remaining: List(SemanticType)) (bound: CoreLoweringState) =
    match remaining with
        | [] -> Some(bound)
        | operandType :: rest ->
            match bindType(operandType)(recorded)(bound) with
                | (next, None) -> bindRecordedOperands(recorded)(rest)(next)
                | (_failed, Some(_error)) -> None

// The state with an operator's still-open operand types bound to the recorded operand type, the
// way a later pass pins a proven goal; None when nothing is recorded or the types do not unify. A
// recorded type that holds a variable of a binding the program uses at one type is closed by that
// use.
let pinRecordedOperands (operandTypes: List(SemanticType)) (state: CoreLoweringState) =
    match recordedOperandType(state) with
        | Some(recorded) -> bindRecordedOperands(recorded)(operandTypes)(state)
        | None ->
            match operandTypes with
                | [] -> None
                | first :: _rest ->
                    match recordedSettledOperandType(first)(state) with
                        | Some(settled) -> bindRecordedOperands(settled)(operandTypes)(state)
                        | None -> None

// An ordered comparison's operand type still open after an earlier pass's proof is pinned to the
// recorded operand type when there is one.
let recordedOrderedOperand (operandType: SemanticType) (state: CoreLoweringState) =
    match operandType with
        | SemVariable(_id) ->
            match pinRecordedOperands([operandType])(state) with
                | Some(recordedState) -> (observeLoweringEvent(0)(recordedState), resolveType(recordedState)(operandType))
                | None -> (state, operandType)
        | _ -> (state, operandType)

// Ordered comparisons infer one Ord operand type before numeric fallback can turn an
// unresolved parameter into Int. Discovery records the goal until callers settle it;
// replay pins that type and emits the real primitive comparison or trait dispatch.
// An active generic implementation already carries evidence and needs no replay.
let emitPreparedCoreOrdered name operator lower binary =
    match binary with
        | LoweredCoreBinary { state = state, leftType = leftType, rightType = rightType, error = None } ->
            match bindEqualityOperands(name)(leftType)(rightType)(binary)(state) with
                | LoweredCoreBinary { state = unifiedState, error = None } as unified ->
                    match pinProvenTraitGoal("Ord")(orderedTraitMethod(name))(resolveType(unifiedState)(leftType))(unifiedState) with
                        | (provenState, provenType) ->
                            match recordedOrderedOperand(provenType)(provenState) with
                                | (pinnedState, operandType) ->
                                    let prepared = unified with state = pinnedState, leftType = operandType, rightType = operandType
                                    in
                                        if hasActiveTraitEvidence("Ord")(operandType)(pinnedState)
                                        then emitCoreTraitOrdered(name)(lower)(prepared)
                                        else
                                            match (operandType, unprovenTraitGoalKey("Ord")(orderedTraitMethod(name))(operandType)(pinnedState)) with
                                                | (SemVariable(_id), Some(key)) ->
                                                    pinnedState
                                                    |> observeLoweringEvent(1)
                                                    |> deferTraitGoal(key)(operandType)(SemBool)
                                                | _ -> emitResolvedCoreBinary(operator)(lower)(prepared)
                | LoweredCoreBinary { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreBinary { state = failedState, error = Some(error) } -> failure(failedState)(error)

// The state of an `==` or `!=` over two variable operands whose site an earlier pass proved a
// type for: the left operand is bound to that type, and the variable joins the body's unresolved
// call results as the speculative comparison's does, so the body is lowered again against the
// resolved type. None when no pass proved the site.
let pinProvenEqualityGoal operator (operandType: SemanticType) (state: CoreLoweringState) =
    match equalityGoalMethod(operator) with
        | Some(methodName) ->
            match pinProvenTraitGoal("Eq")(methodName + ":structural")(operandType)(state) with
                | (_unpinnedState, SemVariable(_id)) -> None
                | (pinnedState, _pinnedType) ->
                    pinnedState
                    |> withStateUnresolvedCallResults(Ashes.Internal.deepCopy(operandType) :: stateUnresolvedCallResults(pinnedState))
                    |> Some
        | None -> None

let withCoreBinaryState (state: CoreLoweringState) (binary: LoweredCoreBinary) = binary with state = state

// The operators lowering emits a speculative integer form for when both operands are still open.
let operatorChoosesFormByOperandType operator =
    match operator with
        | CoreAddOperator -> true
        | CoreSubtractOperator -> true
        | CoreMultiplyOperator -> true
        | CoreEqualOperator -> true
        | CoreNotEqualOperator -> true
        | _ -> false

// Such an operator whose operands are not both bare variables but still hold one (`xs == []`
// before anything settles the element type) takes its form from the closed operand type the
// standalone inference recorded for the operator node, read through the instantiation of the body
// being lowered: the final type rather than the operands lowering has met so far. None when no
// closed type is recorded there or the operands do not unify with it; lowering then unifies the
// operands with each other and decides as before.
let recordedPartlyOpenOperands operator (leftType: SemanticType) (rightType: SemanticType) (state: CoreLoweringState) =
    if operatorChoosesFormByOperandType(operator) && (freeTypeVariables(leftType) != [] || freeTypeVariables(rightType) != [])
    then
        match recordedOperandType(state) with
            | Some(recorded) -> bindRecordedOperands(recorded)([leftType, rightType])(state)
            | None -> None
    else None

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

// The state with a partly open equality operand bound to the type recorded for the operator node,
// closed by the variables the program's uses settle, and that closed type; None when no table is
// present, nothing closed is recorded there, or the operand does not unify with it. It answers the
// goal the site would otherwise defer to the replay pass, which would pin the same type there.
let recordedSettledEqualityOperand (operandType: SemanticType) (state: CoreLoweringState) =
    match recordedSettledOperandType(operandType)(state) with
        | Some(settled) ->
            match bindRecordedOperands(settled)([operandType])(state) with
                | Some(bound) -> Some((observeLoweringEvent(0)(bound), resolveType(bound)(operandType)))
                | None -> None
        | None -> None

// Under typed lowering, the comparison a partly open equality whose plan lacks evidence is lowered
// to instead of deferring its goal; None without a node table, and when neither answer applies.
// A body lowered as a constrained binding that carries an Eq dictionary for the whole operand type
// (`Eq(List(a))` for `selected != []`) compares through that dictionary, as when the operand
// matches an active requirement directly. Otherwise the operand takes the recorded type closed by
// the program's settled uses.
let answerPartlyOpenEqualityFromTable operator (methodName: Str) lower (binary: LoweredCoreBinary) (operandType: SemanticType) (state: CoreLoweringState) =
    match stateNodeTypes(state) with
        | None -> None
        | Some(_table) ->
            let resolved = resolveType(state)(operandType)
            in
                if hasActiveTraitEvidence("Eq")(resolved)(state)
                then
                    (binary with state = observeLoweringEvent(0)(state), leftType = resolved, rightType = resolved)
                    |> emitCoreTraitPlanCalls(TraitEvidenceParameter(TraitConstraint(traitName = "Eq", typeArguments = [resolved])))(methodName)(lower)
                    |> Some
                else
                    match recordedSettledEqualityOperand(operandType)(state) with
                        | Some((recordedState, recordedType)) ->
                            (binary with state = recordedState, leftType = recordedType, rightType = recordedType)
                            |> emitResolvedCoreBinary(operator)(lower)
                            |> Some
                        | None -> None

// A partly open equality in a pass lowering with proofs pinned, where the site's goal is no
// longer deferred: an operand no proof closed (the first pass answered the site from the table,
// so it recorded no goal to prove) takes the same table answer again; a closed one, or one the
// table does not answer, compares at its type as before.
let emitPinnedPartlyOpenEquality operator (methodName: Str) lower (binary: LoweredCoreBinary) (pinnedType: SemanticType) (pinnedState: CoreLoweringState) =
    match (pinnedType
    |> resolveType(pinnedState)
    |> freeTypeVariables, stateNodeTypes(pinnedState)) with
        | (_ :: _, Some(_table)) ->
            match answerPartlyOpenEqualityFromTable(operator)(methodName)(lower)(binary)(pinnedType)(pinnedState) with
                | Some(answered) -> answered
                | None -> emitResolvedCoreBinary(operator)(lower)((binary with state = pinnedState, leftType = pinnedType, rightType = pinnedType))
        | _ -> emitResolvedCoreBinary(operator)(lower)((binary with state = pinnedState, leftType = pinnedType, rightType = pinnedType))

// A constructed operand can still contain an unknown type, as in xs == []. The list
// implementation is known, but its element dictionary may depend on a later application.
// Defer that comparison as a whole unless all its evidence is already in scope.
let emitPartlyOpenEquality operator methodName lower (binary: LoweredCoreBinary) =
    match binary with
        | LoweredCoreBinary { state = state, leftType = operandType, error = None } ->
            match freeTypeVariables(operandType) with
                | [] -> emitResolvedCoreBinary(operator)(lower)(binary)
                | _variables ->
                    match state
                    |> stateTraitEnvironment
                    |> resolveTraitEvidence(TraitConstraint(traitName = "Eq", typeArguments = [operandType])) with
                        | TraitEvidenceResolution { plan = Some(plan) } ->
                            if constrainedPlanLacksEvidence(plan)(state)
                            then
                                match pinProvenTraitGoal("Eq")(methodName)(operandType)(state) with
                                    | (pinnedState, pinnedType) ->
                                        match unprovenTraitGoalKey("Eq")(methodName)(pinnedType)(pinnedState) with
                                            | Some(key) ->
                                                match answerPartlyOpenEqualityFromTable(operator)(methodName)(lower)(binary)(pinnedType)(pinnedState) with
                                                    | Some(answered) -> answered
                                                    | None -> deferTraitGoal(key)(pinnedType)(SemBool)(pinnedState)
                                            | None -> emitPinnedPartlyOpenEquality(operator)(methodName)(lower)(binary)(pinnedType)(pinnedState)
                            else emitResolvedCoreBinary(operator)(lower)(binary)
                        | _ -> emitResolvedCoreBinary(operator)(lower)(binary)
        | LoweredCoreBinary { state = state, error = Some(error) } -> failure(state)(error)

let emitUnifiedCoreBinary operator lower binary =
    match equalityGoalMethod(operator) with
        | Some(methodName) -> emitPartlyOpenEquality(operator)(methodName)(lower)(binary)
        | None -> emitResolvedCoreBinary(operator)(lower)(binary)

// The trait and method of an operator lowering emits speculatively over two open operands.
let deferredOperatorTraitMethod operator =
    match operator with
        | CoreAddOperator -> Some(("Add", "add"))
        | CoreSubtractOperator -> Some(("Subtract", "subtract"))
        | CoreMultiplyOperator -> Some(("Multiply", "multiply"))
        | CoreEqualOperator -> Some(("Eq", "equal"))
        | CoreNotEqualOperator -> Some(("Eq", "notEqual"))
        | _ -> None

// Records a deferred operator site when the lowering observes its goal sites, under the site's
// goal key, or under its trait and method alone when the site has no span (a node lowering
// synthesized). Without an observation the state is returned as it is.
let observeDeferredOperator operator (operandType: SemanticType) (state: CoreLoweringState) =
    match stateTypeObservation(state) with
        | None -> state
        | Some(_observation) ->
            match deferredOperatorTraitMethod(operator) with
                | None -> state
                | Some((traitName, methodName)) ->
                    let key =
                        match traitGoalKey(traitName)(methodName)(state) with
                            | Some(siteKey) -> siteKey
                            | None -> "unspanned:" + traitName + ":" + methodName
                    in observeDeferredOperatorGoal(key)(operandType)(state)

let emitPreparedCoreBinary operator lower binary =
    match (coreOrderedOperatorName(operator), binary) with
        | (Some(name), _binary) -> emitPreparedCoreOrdered(name)(operator)(lower)(binary)
        | (None, LoweredCoreBinary { state = state, leftType = leftType, rightType = rightType, error = None }) ->
            match (deferredCoreOperatorEmitter(operator), resolveType(state)(leftType), resolveType(state)(rightType)) with
                | (Some(emitDeferred), SemVariable(leftId), SemVariable(rightId)) ->
                    if activeEvidenceCoversOperator(operator)(SemVariable(leftId))(state)
                    then
                        binary
                        |> unifyEqualityOperands(operator)
                        |> resolveCoreBinaryOwnTypes
                        |> emitResolvedCoreBinary(operator)(lower)
                    else
                        match pinProvenEqualityGoal(operator)(SemVariable(leftId))(state) with
                            | Some(pinnedState) ->
                                binary
                                |> withCoreBinaryState(pinnedState)
                                |> unifyEqualityOperands(operator)
                                |> resolvedCoreBinary
                                |> emitResolvedCoreBinary(operator)(lower)
                            | None ->
                                match pinRecordedOperands([SemVariable(leftId), SemVariable(rightId)])(state) with
                                    | Some(recordedState) ->
                                        binary
                                        |> withCoreBinaryState(recordedState
                                        |> withStateUnresolvedCallResults(Ashes.Internal.deepCopy(SemVariable(leftId)) :: stateUnresolvedCallResults(recordedState))
                                        |> observeLoweringEvent(0))
                                        |> unifyEqualityOperands(operator)
                                        |> resolvedCoreBinary
                                        |> emitResolvedCoreBinary(operator)(lower)
                                    | None ->
                                        binary
                                        |> withCoreBinaryState(state
                                        |> observeLoweringEvent(1)
                                        |> observeDeferredOperator(operator)(SemVariable(leftId)))
                                        |> emitDeferred
                | (_emitter, resolvedLeft, resolvedRight) ->
                    match recordedPartlyOpenOperands(operator)(resolvedLeft)(resolvedRight)(state) with
                        | Some(recordedState) ->
                            binary
                            |> withCoreBinaryState(observeLoweringEvent(0)(recordedState))
                            |> unifyEqualityOperands(operator)
                            |> resolvedCoreBinary
                            |> emitUnifiedCoreBinary(operator)(lower)
                        | None ->
                            binary
                            |> unifyEqualityOperands(operator)
                            |> resolvedCoreBinary
                            |> emitUnifiedCoreBinary(operator)(lower)
        | (None, LoweredCoreBinary { state = failedState, error = Some(error) }) -> failure(failedState)(error)

// The instructions a speculative operator becomes once its operand type is final, with the
// function's temp count after them: a BigInt comparison needs two temps of its own.
let resolveDeferredOperator state semanticType (kind: IrInstructionKind) (tempCount: Int) =
    match (resolveType(state)(semanticType), kind) with
        | (SemString, AddInt(target, left, right)) -> ([ConcatStr(target)(left)(right)(false)], tempCount)
        | (SemFloat, AddInt(target, left, right)) -> ([AddFloat(target)(left)(right)], tempCount)
        | (SemBigInt, AddInt(target, left, right)) -> ([BigIntBinary(target)(left)(right)("add")(false)], tempCount)
        | (SemFloat, SubInt(target, left, right)) -> ([SubFloat(target)(left)(right)], tempCount)
        | (SemBigInt, SubInt(target, left, right)) -> ([BigIntBinary(target)(left)(right)("sub")(false)], tempCount)
        | (SemFloat, MulInt(target, left, right)) -> ([MulFloat(target)(left)(right)], tempCount)
        | (SemBigInt, MulInt(target, left, right)) -> ([BigIntBinary(target)(left)(right)("mul")(false)], tempCount)
        | (SemString, CmpIntEq(target, left, right)) -> ([CmpStrEq(target)(left)(right)], tempCount)
        | (SemString, CmpIntNe(target, left, right)) -> ([CmpStrNe(target)(left)(right)], tempCount)
        | (SemFloat, CmpIntEq(target, left, right)) -> ([CmpFloatEq(target)(left)(right)], tempCount)
        | (SemFloat, CmpIntNe(target, left, right)) -> ([CmpFloatNe(target)(left)(right)], tempCount)
        | (SemBigInt, CmpIntEq(target, left, right)) -> ([BigIntCompare(tempCount)(left)(right), LoadConstInt(tempCount + 1)(0), CmpIntEq(target)(tempCount)(tempCount + 1)], tempCount + 2)
        | (SemBigInt, CmpIntNe(target, left, right)) -> ([BigIntCompare(tempCount)(left)(right), LoadConstInt(tempCount + 1)(0), CmpIntNe(target)(tempCount)(tempCount + 1)], tempCount + 2)
        | (_, other) -> ([other], tempCount)

let deferredOperatorTarget (kind: IrInstructionKind) =
    match kind with
        | AddInt(target, _left, _right) -> Some(target)
        | SubInt(target, _left, _right) -> Some(target)
        | MulInt(target, _left, _right) -> Some(target)
        | CmpIntEq(target, _left, _right) -> Some(target)
        | CmpIntNe(target, _left, _right) -> Some(target)
        | _ -> None

let locatedInstructions location (kinds: List(IrInstructionKind)) =
    Ashes.Collection.List.map(given (kind: IrInstructionKind) -> IrInstruction(instruction = kind, location = location))(kinds)

let recursive rewriteDeferredOperatorInto state pendingTarget semanticType instructions tempCount reversed =
    match instructions with
        | [] -> (reverse(reversed), tempCount)
        | (IrInstruction { instruction = kind, location = location } as instruction) :: rest ->
            match deferredOperatorTarget(kind) with
                | Some(target) ->
                    if target == pendingTarget
                    then
                        match resolveDeferredOperator(state)(semanticType)(kind)(tempCount) with
                            | (resolved, resolvedCount) ->
                                (rest
                                |> append(locatedInstructions(location)(resolved))
                                |> append(reverse(reversed)), resolvedCount)
                    else rewriteDeferredOperatorInto(state)(pendingTarget)(semanticType)(rest)(tempCount)(instruction :: reversed)
                | None -> rewriteDeferredOperatorInto(state)(pendingTarget)(semanticType)(rest)(tempCount)(instruction :: reversed)

let recursive applyDeferredOperators state pending (rewritten: (List(IrInstruction), Int)) =
    match (pending, rewritten) with
        | ([], _) -> rewritten
        | ((pendingTarget, semanticType) :: rest, (instructions, tempCount)) ->
            []
            |> rewriteDeferredOperatorInto(state)(pendingTarget)(semanticType)(instructions)(tempCount)
            |> applyDeferredOperators(state)(rest)

let recursive sealedDeferredFor (label: Str) (sealed: List((Str, Int, SemanticType))) =
    match sealed with
        | [] -> []
        | (candidate, position, semanticType) :: rest ->
            if candidate == label
            then (position, semanticType) :: sealedDeferredFor(label)(rest)
            else sealedDeferredFor(label)(rest)

let recursive applySealedDeferredOperators state sealed functions =
    match functions with
        | [] -> []
        | (IrFunction { label = label, instructions = instructions, tempCount = tempCount } as function) :: rest ->
            match applyDeferredOperators(state)(sealedDeferredFor(label)(sealed))((instructions, tempCount)) with
                | (resolvedInstructions, resolvedCount) -> (function with instructions = resolvedInstructions, tempCount = resolvedCount) :: applySealedDeferredOperators(state)(sealed)(rest)

let prepareCoreBinary operator left binary =
    match operator with
        | CoreSubtractOperator ->
            if isCoreZeroExpression(left)
            then coerceCoreNegationZero(binary)
            else binary
        | _ -> binary

// The operands of a binary operator are lowered without the context's request, as stage 0's
// `LowerAddOperands` lowers them; the request is restored on the operator's own state, so the
// concatenation the operator selects still reads it.
let restoreBinaryRequest (request: ConsumerRequest) (binary: LoweredCoreBinary) =
    match binary with
        | LoweredCoreBinary { state = state, error = None } -> binary with state = withConsumerRequest(request)(state)
        | _ -> binary

// The reservation an armed `+` grows, decided from the raw left operand before the operands are
// lowered and handed to the concatenation emitter once they are.
let binaryAffineReservation operator left (state: CoreLoweringState) =
    match operator with
        | CoreAddOperator -> affineReservationFor(left)(state)
        | _ -> None

let withBinaryAffineReservation (reservation: Maybe((Int, Int))) (binary: LoweredCoreBinary) =
    match binary with
        | LoweredCoreBinary { state = state, error = None } ->
            let armed = withStateAffineAppendReservation(reservation)(state)
            in binary with state = armed
        | _ -> binary

let clearAffineReservation (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state } ->
            let cleared = withStateAffineAppendReservation(None)(state)
            in lowered with state = cleared

// The trait the standalone inference gives a binary operator site.
let coreOperatorTraitName operator left =
    match operator with
        | CoreAddOperator -> "Add"
        | CoreSubtractOperator ->
            if isCoreZeroExpression(left)
            then "Negate"
            else "Subtract"
        | CoreMultiplyOperator -> "Multiply"
        | CoreDivideOperator -> "Divide"
        | CoreModuloOperator -> "Remainder"
        | CoreBitwiseAndOperator -> "BitAnd"
        | CoreBitwiseOrOperator -> "BitOr"
        | CoreBitwiseXorOperator -> "BitXor"
        | CoreShiftLeftOperator -> "ShiftLeft"
        | CoreShiftRightOperator -> "ShiftRight"
        | CoreGreaterOperator -> "Ord"
        | CoreGreaterOrEqualOperator -> "Ord"
        | CoreLessOperator -> "Ord"
        | CoreLessOrEqualOperator -> "Ord"
        | CoreEqualOperator -> "Eq"
        | CoreNotEqualOperator -> "Eq"

// Records the site's operand type when the lowering observes its types: the right operand of a
// negation written `0 - x`, the left one otherwise.
let observeCoreBinary operator left (binary: LoweredCoreBinary) =
    match binary with
        | LoweredCoreBinary { state = state, leftType = leftType, rightType = rightType, error = None } ->
            match stateTypeObservation(state) with
                | None -> binary
                | Some(_observation) ->
                    let traitName = coreOperatorTraitName(operator)(left)
                    in
                        let operandType =
                            if traitName == "Negate"
                            then rightType
                            else leftType
                        in binary with state = observeOperatorType(traitName)(operandType)(state)
        | _ -> binary

// The trait and method an arithmetic or bitwise operator stands for over an operand that is no
// primitive (stage 0's mapped binary traits): `Box(7) + Box(2)` is `Add.add`.
let arithmeticTraitMethod operator =
    match operator with
        | CoreAddOperator -> Some(("Add", "add"))
        | CoreSubtractOperator -> Some(("Subtract", "subtract"))
        | CoreMultiplyOperator -> Some(("Multiply", "multiply"))
        | CoreDivideOperator -> Some(("Divide", "divide"))
        | CoreModuloOperator -> Some(("Remainder", "remainder"))
        | CoreBitwiseAndOperator -> Some(("BitAnd", "bitAnd"))
        | CoreBitwiseOrOperator -> Some(("BitOr", "bitOr"))
        | CoreBitwiseXorOperator -> Some(("BitXor", "bitXor"))
        | CoreShiftLeftOperator -> Some(("ShiftLeft", "shiftLeft"))
        | CoreShiftRightOperator -> Some(("ShiftRight", "shiftRight"))
        | _ -> None

// A user type's operator: the implementation's method applied to both operands, its result of the
// operand type.
let emitCoreTraitArithmetic (traitName: Str) (methodName: Str) lower (binary: LoweredCoreBinary) =
    match binary with
        | LoweredCoreBinary { state = state, leftType = operandType, error = None } ->
            match state
            |> stateTraitEnvironment
            |> resolveTraitEvidence(TraitConstraint(traitName = traitName, typeArguments = [operandType])) with
                | TraitEvidenceResolution { plan = Some(plan) } ->
                    match emitCoreTraitPlanCalls(plan)(methodName)(lower)(binary) with
                        | LoweredCoreValue { error = None } as lowered -> lowered with semanticType = operandType
                        | failed -> failed
                | TraitEvidenceResolution { plan = None } ->
                    operandType
                    |> MissingCoreTraitEvidence(traitName)
                    |> failure(state)
        | LoweredCoreBinary { state = state, error = Some(error) } -> failure(state)(error)

let isSubtractOperator operator =
    match operator with
        | CoreSubtractOperator -> true
        | _ -> false

let isUserTypeOperand (semanticType: SemanticType) =
    match semanticType with
        | SemNamed(_symbolId, _name, _arguments) -> true
        | _ -> false

// An operator over a user type's operands dispatches through the operator's trait, a negation
// `0 - x` of one through `Negate`; every other operator takes its primitive route.
let emitPreparedOrTraitBinary operator left lower (binary: LoweredCoreBinary) =
    match binary with
        | LoweredCoreBinary { state = state, leftType = leftType, rightTemp = rightTemp, rightType = rightType, error = None } ->
            let resolvedLeft = resolveType(state)(leftType)
            in
                let resolvedRight = resolveType(state)(rightType)
                in
                    if isSubtractOperator(operator) && isCoreZeroExpression(left) && (isUserTypeOperand(resolvedRight) || hasActiveTraitEvidence("Negate")(resolvedRight)(state))
                    then emitCoreTraitUnaryDispatch("Negate")("negate")(resolvedRight)(lower)(rightTemp)(resolvedRight)(state)
                    else
                        match arithmeticTraitMethod(operator) with
                            | Some((traitName, methodName)) ->
                                if resolvedLeft == resolvedRight && (isUserTypeOperand(resolvedLeft) || hasActiveTraitEvidence(traitName)(resolvedLeft)(state))
                                then emitCoreTraitArithmetic(traitName)(methodName)(lower)((binary with leftType = resolvedLeft, rightType = resolvedRight))
                                else emitPreparedCoreBinary(operator)(lower)(binary)
                            | _ -> emitPreparedCoreBinary(operator)(lower)(binary)
        | _ -> emitPreparedCoreBinary(operator)(lower)(binary)

let lowerCoreBinary operator left right lower state =
    match consumerRequestOf(state) with
        | request ->
            state
            |> withConsumerRequest(emptyConsumerRequest)
            |> lowerCoreBinaryOperands(left)(right)(lower)
            |> restoreBinaryRequest(request)
            |> withBinaryAffineReservation(binaryAffineReservation(operator)(left)(state))
            |> prepareCoreBinary(operator)(left)
            |> observeCoreBinary(operator)(left)
            |> emitPreparedOrTraitBinary(operator)(left)(lower)
            |> clearAffineReservation

// The type of an `Ordering` value as this program's constructor layouts declare it.
let orderingResultType (state: CoreLoweringState) =
    match constructorLayout("Less")(state) with
        | Some(CoreConstructorLayout { scheme = TypeScheme { body = body } }) -> body
        | None -> SemNamed(0)("Ordering")([])

// The `compare` of the operands' shared type, typed as the `Ordering` it returns.
let emitCoreTraitCompare lower (binary: LoweredCoreBinary) =
    match binary with
        | LoweredCoreBinary { state = state, leftType = leftType, rightType = rightType, error = None } ->
            match bindType(leftType)(rightType)(state) with
                | (failedState, Some(error)) -> failure(failedState)(error)
                | (boundState, None) ->
                    match emitCoreTraitBinaryDispatch("Ord")("compare")(lower)((binary with state = boundState, leftType = resolveType(boundState)(leftType), rightType = resolveType(boundState)(rightType))) with
                        | LoweredCoreValue { state = comparedState, temp = compareTemp, error = None } ->
                            success(compareTemp)(orderingResultType(comparedState))(comparedState)
                        | failed -> failed
        | LoweredCoreBinary { state = failedState, error = Some(error) } -> failure(failedState)(error)

// A saturated `Ord.compare(left)(right)`, the call a derived comparison makes per field: both
// operands are lowered and unified, and the call dispatches through `Ord` at their type.
let lowerCoreTraitCompare left right lower state =
    match consumerRequestOf(state) with
        | request ->
            state
            |> withConsumerRequest(emptyConsumerRequest)
            |> lowerCoreBinaryOperands(left)(right)(lower)
            |> restoreBinaryRequest(request)
            |> emitCoreTraitCompare(lower)

let finishCoreLogicalNot lowered =
    match lowered with
        | LoweredCoreValue { state = state, temp = operand, semanticType = semanticType, error = None } ->
            match resolveType(state)(semanticType) with
                | SemBool ->
                    match reserveCoreTemps(2)(state) with
                        | ReservedCoreTemps { state = targetState, first = falseTemp } ->
                            targetState
                            |> emit(LoadConstBool(falseTemp)(false))
                            |> emit(CmpIntEq(falseTemp + 1)(operand)(falseTemp))
                            |> success(falseTemp + 1)(SemBool)
                | other ->
                    other
                    |> CoreOperatorTypeMismatch("not")(other)
                    |> failure(state)
        | LoweredCoreValue { state = state, error = Some(error) } -> failure(state)(error)

let finishCoreBitwiseNot lowered =
    match lowered with
        | LoweredCoreValue { state = state, temp = operand, semanticType = semanticType, error = None } ->
            match resolveType(state)(semanticType) with
                | SemInt ->
                    match reserveCoreTemps(2)(state) with
                        | ReservedCoreTemps { state = targetState, first = maskTemp } ->
                            targetState
                            |> emit(LoadConstInt(maskTemp)(-1))
                            |> emit(XorInt(maskTemp + 1)(operand)(maskTemp))
                            |> success(maskTemp + 1)(SemInt)
                | SemUInt(bits) ->
                    match reserveCoreTemps(2)(state) with
                        | ReservedCoreTemps { state = targetState, first = maskTemp } ->
                            targetState
                            |> emit(LoadConstInt(maskTemp)(if bits == 64
                            then -1
                            else (1 << bits) - 1))
                            |> emit(XorInt(maskTemp + 1)(operand)(maskTemp))
                            |> success(maskTemp + 1)(SemUInt(bits))
                | other ->
                    other
                    |> CoreOperatorTypeMismatch("~")(other)
                    |> failure(state)
        | LoweredCoreValue { state = state, error = Some(error) } -> failure(state)(error)

// A later mutual-recursion member's result can still be open at !. Keep the Not goal
// and its a -> a result until its consumer or a later body settles it, rather than
// forcing a generic Not requirement to Bool. Pending goals cannot be generalized or
// emitted: the final pass pins the proved type, or rejects a still-unresolved operand. Outside a
// body carrying `Not` evidence, an operand still open takes the closed operand type the standalone
// inference recorded for the `!` node, as the final pass pins a proved one.
let recordedNotOperand (operandType: SemanticType) (state: CoreLoweringState) =
    if hasActiveTraitEvidence("Not")(operandType)(state)
    then (state, operandType)
    else recordedOrderedOperand(operandType)(state)

let finishOrDeferCoreLogicalNot lower lowered =
    match lowered with
        | LoweredCoreValue { state = operandState, temp = operandTemp, semanticType = operandType, error = None } as lowered ->
            match pinProvenTraitGoal("Not")("not")(resolveType(operandState)(operandType))(operandState) with
                | (provenState, provenType) ->
                    match recordedNotOperand(provenType)(provenState) with
                        | (pinnedState, pinnedType) ->
                            if hasActiveTraitEvidence("Not")(pinnedType)(pinnedState)
                            then emitCoreTraitUnaryDispatch("Not")("not")(pinnedType)(lower)(operandTemp)(pinnedType)(pinnedState)
                            else
                                match (pinnedType, unprovenTraitGoalKey("Not")("not")(pinnedType)(pinnedState)) with
                                    | (SemVariable(_id), Some(key)) -> deferTraitGoal(key)(pinnedType)(pinnedType)(pinnedState)
                                    | (SemNamed(_symbolId, _name, _arguments), _key) -> emitCoreTraitUnaryDispatch("Not")("not")(pinnedType)(lower)(operandTemp)(pinnedType)(pinnedState)
                                    | _ -> finishCoreLogicalNot((lowered with state = pinnedState, semanticType = pinnedType))
        | failed -> failed

let lowerCoreLogicalNot operand lower state =
    state
    |> lower(operand)
    |> finishOrDeferCoreLogicalNot(lower)

// `~x` over a user type dispatches through `BitwiseNot`; a primitive operand takes the inline mask.
let finishCoreBitwiseNotOrTrait lower lowered =
    match lowered with
        | LoweredCoreValue { state = state, temp = operand, semanticType = semanticType, error = None } ->
            match resolveType(state)(semanticType) with
                | SemNamed(_symbolId, _name, _arguments) as named -> emitCoreTraitUnaryDispatch("BitwiseNot")("bitwiseNot")(named)(lower)(operand)(named)(state)
                | resolved ->
                    if hasActiveTraitEvidence("BitwiseNot")(resolved)(state)
                    then emitCoreTraitUnaryDispatch("BitwiseNot")("bitwiseNot")(resolved)(lower)(operand)(resolved)(state)
                    else finishCoreBitwiseNot(lowered)
        | failed -> failed

let lowerCoreBitwiseNot operand lower state =
    state
    |> lower(operand)
    |> finishCoreBitwiseNotOrTrait(lower)

let recursive parseDecimalDigits remaining value =
    match Ashes.Text.unconsText(remaining) with
        | None -> value
        | Some((digit, rest)) -> parseDecimalDigits(rest)(value * 10 + Ashes.Text.firstByteOf(digit) - 48)

let bigIntFitsLength digits digitCount =
    if digitCount < 19
    then true
    else
        if digitCount == 19
        then Ashes.Text.compare(digits)("9223372036854775807") <= 0
        else false

let bigIntFitsInt digits =
    digits
    |> Ashes.Text.length
    |> bigIntFitsLength(digits)

let emitCoreBigIntConstant value state =
    match reserveCoreTemps(2)(state) with
        | ReservedCoreTemps { state = targetState, first = constantTemp } ->
            targetState
            |> emit(LoadConstInt(constantTemp)(value))
            |> emit(BigIntFromInt(constantTemp + 1)(constantTemp)(false))
            |> success(constantTemp + 1)(SemBigInt)

let decimalChunk digits =
    (parseDecimalDigits(Ashes.Text.take(digits)(18))(0), Ashes.Text.drop(digits)(18))

let recursive emitCoreBigIntChunks remaining accumulator state =
    match remaining with
        | "" -> success(accumulator)(SemBigInt)(state)
        | _ ->
            match (decimalChunk(remaining), reserveCoreTemps(6)(state)) with
                | ((chunk, rest), ReservedCoreTemps { state = targetState, first = baseInt }) ->
                    targetState
                    |> emit(LoadConstInt(baseInt)(1000000000000000000))
                    |> emit(BigIntFromInt(baseInt + 1)(baseInt)(false))
                    |> emit(BigIntBinary(baseInt + 2)(accumulator)(baseInt + 1)("mul")(false))
                    |> emit(LoadConstInt(baseInt + 3)(chunk))
                    |> emit(BigIntFromInt(baseInt + 4)(baseInt + 3)(false))
                    |> emit(BigIntBinary(baseInt + 5)(baseInt + 2)(baseInt + 4)("add")(false))
                    |> emitCoreBigIntChunks(rest)(baseInt + 5)

let finishCoreBigIntHead rest lowered =
    match lowered with
        | LoweredCoreValue { error = None } as value ->
            match value with
                | LoweredCoreValue { state = state, temp = accumulator } ->
                    emitCoreBigIntChunks(
                        rest,
                        accumulator,
                        state
                    )
        | LoweredCoreValue { state = state, error = Some(error) } -> failure(state)(error)

let bigIntFirstLength digitCount =
    match digitCount % 18 with
        | 0 -> 18
        | remainder -> remainder

let lowerCoreLargeBigIntFromFirst digits state firstLength =
    state
    |> emitCoreBigIntConstant(parseDecimalDigits(Ashes.Text.take(digits)(firstLength))(0))
    |> finishCoreBigIntHead(Ashes.Text.drop(digits)(firstLength))

let lowerCoreLargeBigInt digits state =
    digits
    |> Ashes.Text.length
    |> bigIntFirstLength
    |> lowerCoreLargeBigIntFromFirst(digits)(state)

let lowerCoreBigInt digits state =
    if bigIntFitsInt(digits)
    then
        emitCoreBigIntConstant(parseDecimalDigits(digits)(0))(state)
    else lowerCoreLargeBigInt(digits)(state)

// A bare nullary constructor is placed under the consumer's request like any application, stage
// 0's `LowerNullaryConstructor`: a runtime-managed parent's `Nil` field lands on the
// reference-counted heap with it.
let finishCoreConstructorReference layout lower state =
    if constructorArity(layout) == 0
    then
        lowerConstructor(layout)([])(consumerRequestOf(state))(lower)(state)
    else
        lower(constructorLambda(layout))(state)

let finishCoreBuiltinReference layout lower state =
    if builtinArity(layout) == 0
    then lowerBuiltin(layout)([])(lower)(state)
    else
        lower(builtinLambda(layout))(state)

// A name not found anywhere ordinary (no local/top-level binding, no constructor, no external
// declaration) is either a genuine undefined identifier, or — under Model A's sequential top-level
// scoping — a binding that DOES exist, just later in the file, not yet visible from here.
// `topLevelNames` (every top-level value-binding name in the whole program, collected once up
// front by each whole-program entry point) is exactly what distinguishes the two: mirrors stage-0's
// LowerVarUnbound/_topLevelBindingNames specialization (Lowering.cs:2844). Expression-only entry
// points (lowerCoreExpression*) never populate topLevelNames, so this never fires for them — there
// is no "later in the file" to be forward-referencing without a whole program.
// Stage 0's `LowerVarUnbound` for a registered top-level function: reached only when the scope
// holds no binding for the name, from a body spliced into a scope that never captured it. The
// function is already lowered (a genuine backward reference), has no environment, so its closure
// is rebuilt from the label with a null environment and its scheme instantiated afresh.
let lowerTopLevelFunctionReference (label: Str) (scheme: TypeScheme) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = environmentState, temp = environmentTemp } ->
            match freshTemp(environmentState) with
                | FreshTemp { state = closureState, temp = closureTemp } ->
                    match instantiate(scheme)(closureState.typeSupply) with
                        | InstantiationResult { semanticType = semanticType, supply = nextSupply } ->
                            closureState
                            |> withTypeSupply(nextSupply)
                            |> emit(LoadConstInt(environmentTemp)(0))
                            |> emit(MakeClosure(closureTemp)(label)(environmentTemp)(0)(false)(bodyReturnsRuntimeManaged(label)(closureState))(false)(false))
                            |> success(closureTemp)(semanticType)

let lowerUnboundVariable name lower state =
    match state with
        | CoreLoweringState { programState = CoreProgramState { externalLayouts = externalLayouts, topLevelNames = topLevelNames } } ->
            match constructorLayout(name)(state) with
                | Some(layout) -> finishCoreConstructorReference(layout)(lower)(state)
                | None ->
                    match tryFindExternalLayout(name)(externalLayouts) with
                        | Some(extLayout) -> finishCoreExternalReference(extLayout)(lower)(state)
                        | None ->
                            if registeredTopLevelName(name)(topLevelNames)
                            then failure(state)(ForwardTopLevelReference(name))
                            else failure(state)(UnknownLoweringBinding(name))

let lowerCoreVariable name lower (state: CoreLoweringState) =
    match lookupBinding(name)(state.bindings) with
        | Some(binding) -> lowerBoundVariable(binding)(state)
        | None ->
            match state
            |> stateTopLevelFunctionRefIndex
            |> lookupTopLevelFunctionRef(name) with
                | Some((label, scheme)) -> lowerTopLevelFunctionReference(label)(scheme)(state)
                | None -> lowerUnboundVariable(name)(lower)(state)

let lowerCoreQualifiedVariable moduleName memberName lower state =
    match builtinLayout(moduleName)(memberName)(state) with
        | Some(layout) -> finishCoreBuiltinReference(layout)(lower)(state)
        | None ->
            match state with
                | CoreLoweringState { programState = CoreProgramState { externalLayouts = externalLayouts } } ->
                    match tryFindExternalLayout(memberName)(externalLayouts) with
                        | Some(extLayout) -> finishCoreExternalReference(extLayout)(lower)(state)
                        | None -> lowerRecordFieldAccess(moduleName)(memberName)(state)

let lowerQualified moduleName memberName lower state = lowerCoreQualifiedVariable(moduleName)(memberName)(lower)(state)
