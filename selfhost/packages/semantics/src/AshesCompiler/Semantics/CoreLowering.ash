// Lowers the strict functional core into semantic IR before ownership placement.
//
// Invariants:
// - Callees are evaluated before arguments, and curried arguments are applied one at a time.
// - Conditions and match scrutinees are evaluated once; guarded arms retain source order.
// - Every function owns independent temp/local counters; closure env and argument locals are 0 and 1.
// - Captures follow first free-use order and occupy consecutive eight-byte environment words.
// - Recursive self/sibling references rebuild closures from the current shared environment.
// - Lifted functions retain generation order, with nested functions preceding their enclosing function.

import Ashes.Collection.List.append
import Ashes.Collection.List.reverse
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.Pattern
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.TypeSchemes
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.Unification
export (
    type CoreLoweringError(..),
    type CoreLoweringResult(..),
    value lowerCoreExpression,
    value lowerCoreRecursiveGroup,
)

type CoreLoweringError =
    | UnknownLoweringBinding(Str)
    | CoreCallRequiresFunction(SemanticType)
    | CoreCallTypeMismatch(UnificationError)
    | UnsupportedCoreLoweringPattern(Str)
    | CoreRecursiveBindingRequiresFunction(Str)
    | UnsupportedCoreLoweringExpression(Str)
    deriving {Eq, Show}

type CoreLoweringResult =
    | program: Maybe(IrProgram)
    | semanticType: SemanticType
    | error: Maybe(CoreLoweringError)

type CoreBindingLocation =
    | CoreLocal(Int)
    | CoreEnvironment(Int)
    | CoreSelf(Str, Int)

type CoreBinding =
    | name: Str
    | scheme: TypeScheme
    | location: CoreBindingLocation

type CoreLoweringState =
    | reversedInstructions: List(IrInstruction)
    | functions: List(IrFunction)
    | bindings: List(CoreBinding)
    | nextTemp: Int
    | nextLocal: Int
    | nextLambdaId: Int
    | nextLabelId: Int
    | nextStringId: Int
    | stringLiterals: List(IrStringLiteral)
    | typeSupply: TypeVariableSupply
    | substitution: List((Int, SemanticType))

type LoweredCoreValue =
    | state: CoreLoweringState
    | temp: Int
    | semanticType: SemanticType
    | error: Maybe(CoreLoweringError)

type FreshTemp =
    | state: CoreLoweringState
    | temp: Int

type FreshLocal =
    | state: CoreLoweringState
    | local: Int

type FreshType =
    | state: CoreLoweringState
    | semanticType: SemanticType

type FreshLabel =
    | state: CoreLoweringState
    | label: Str

type FreshFunctionType =
    | state: CoreLoweringState
    | semanticType: SemanticType
    | parameterType: SemanticType
    | resultType: SemanticType

type LoweredCorePattern =
    | state: CoreLoweringState
    | error: Maybe(CoreLoweringError)

type CoreIfPlan =
    | state: CoreLoweringState
    | resultSlot: Int
    | elseLabel: Str
    | endLabel: Str
    | error: Maybe(CoreLoweringError)

type CoreIfThen =
    | state: CoreLoweringState
    | resultSlot: Int
    | endLabel: Str
    | thenType: SemanticType
    | error: Maybe(CoreLoweringError)

type CoreMatchPlan =
    | state: CoreLoweringState
    | valueTemp: Int
    | valueType: SemanticType
    | resultSlot: Int
    | endLabel: Str
    | noMatchLabel: Str
    | resultType: SemanticType
    | error: Maybe(CoreLoweringError)

type PreparedCoreRecursiveBinding =
    | name: Str
    | parameter: Str
    | body: Expr
    | label: Str
    | slot: Int
    | semanticType: SemanticType
    | parameterType: SemanticType
    | resultType: SemanticType

type PreparedCoreRecursiveGroup =
    | state: CoreLoweringState
    | members: List(PreparedCoreRecursiveBinding)
    | error: Maybe(CoreLoweringError)

type PreparedCoreRecursiveMemberResult =
    | state: CoreLoweringState
    | member: Maybe(PreparedCoreRecursiveBinding)
    | error: Maybe(CoreLoweringError)

type InstantiatedBinding =
    | state: CoreLoweringState
    | semanticType: SemanticType

type StringInterning =
    | state: CoreLoweringState
    | label: Str

type FunctionTypeResolution =
    | state: CoreLoweringState
    | argumentType: SemanticType
    | resultType: SemanticType
    | error: Maybe(CoreLoweringError)

let emptyScheme semanticType =
    TypeScheme(
        quantified = [],
        body = semanticType,
        constraints = []
    )

let initialState unit =
    CoreLoweringState(
        reversedInstructions = [],
        functions = [],
        bindings = [],
        nextTemp = 0,
        nextLocal = 0,
        nextLambdaId = 0,
        nextLabelId = 0,
        nextStringId = 0,
        stringLiterals = [],
        typeSupply = initialTypeVariableSupply(Unit),
        substitution = []
    )

let withNextTemp nextTemp (state: CoreLoweringState) = state with nextTemp = nextTemp

let withNextLocal nextLocal (state: CoreLoweringState) = state with nextLocal = nextLocal

let withTypeSupply typeSupply (state: CoreLoweringState) = state with typeSupply = typeSupply

let withSubstitution substitution (state: CoreLoweringState) = state with substitution = substitution

let withNextLambdaId nextLambdaId (state: CoreLoweringState) = state with nextLambdaId = nextLambdaId

let withNextLabelId nextLabelId (state: CoreLoweringState) = state with nextLabelId = nextLabelId

let freshTemp state =
    match state with
        | CoreLoweringState { nextTemp = nextTemp } ->
            FreshTemp(
                state = withNextTemp(nextTemp + 1)(state),
                temp = nextTemp
            )

let freshLocal state =
    match state with
        | CoreLoweringState { nextLocal = nextLocal } ->
            FreshLocal(
                state = withNextLocal(nextLocal + 1)(state),
                local = nextLocal
            )

let freshType state =
    match state with
        | CoreLoweringState { typeSupply = supply } ->
            match freshTypeVariable(supply) with
                | (semanticType, nextSupply) ->
                    FreshType(
                        state = withTypeSupply(nextSupply)(state),
                        semanticType = semanticType
                    )

let freshLabel prefix state =
    match state with
        | CoreLoweringState { nextLabelId = nextLabelId } ->
            FreshLabel(
                state = withNextLabelId(nextLabelId + 1)(state),
                label = prefix + "_" + Ashes.Text.fromInt(nextLabelId)
            )

let freshFunctionType state =
    match freshType(state) with
        | FreshType { state = parameterState, semanticType = parameterType } ->
            match freshType(parameterState) with
                | FreshType { state = resultState, semanticType = resultType } ->
                    FreshFunctionType(
                        state = resultState,
                        semanticType = SemFunction(parameterType)(resultType)(None),
                        parameterType = parameterType,
                        resultType = resultType
                    )

let emit kind state =
    match state with
        | CoreLoweringState { reversedInstructions = instructions } ->
            let wrapped = IrInstruction(instruction = kind, location = None)
            in state with reversedInstructions = wrapped :: instructions

let success temp semanticType state =
    LoweredCoreValue(
        state = state,
        temp = temp,
        semanticType = semanticType,
        error = None
    )

let failure state error =
    LoweredCoreValue(
        state = state,
        temp = -1,
        semanticType = SemNever,
        error = Some(error)
    )

let bindingScheme binding =
    match binding with
        | CoreBinding { scheme = scheme } -> scheme

let recursive bindingSchemes bindings =
    match bindings with
        | [] -> []
        | binding :: rest -> bindingScheme(binding) :: bindingSchemes(rest)

let recursive lookupBinding name bindings =
    match bindings with
        | [] -> None
        | (CoreBinding { name = candidate } as binding) :: rest ->
            if name == candidate
            then Some(binding)
            else lookupBinding(name)(rest)

let instantiateBinding binding state =
    match (binding, state) with
        | (CoreBinding { scheme = scheme }, CoreLoweringState { typeSupply = supply }) ->
            match instantiate(scheme)(supply) with
                | InstantiationResult { semanticType = semanticType, supply = nextSupply } ->
                    InstantiatedBinding(
                        state = withTypeSupply(nextSupply)(state),
                        semanticType = semanticType
                    )

let resolveType state semanticType =
    match state with
        | CoreLoweringState { substitution = substitution } -> applySubstitution(substitution)(semanticType)

let bindType left right state =
    match state with
        | CoreLoweringState { substitution = existing } ->
            match right
            |> resolveType(state)
            |> unify(resolveType(state)(left)) with
                | UnificationResult { substitution = added, error = None } ->
                    (withSubstitution(append(added)(existing))(state), None)
                | UnificationResult { error = Some(error) } -> (state, Some(CoreCallTypeMismatch(error)))

let lowerConstant kind semanticType state =
    match freshTemp(state) with
        | FreshTemp { state = nextState, temp = temp } ->
            nextState
            |> emit(kind(temp))
            |> success(temp)(semanticType)

let recursive findStringLiteral value literals =
    match literals with
        | [] -> None
        | IrStringLiteral { label = label, value = candidate } :: rest ->
            if value == candidate
            then Some(label)
            else findStringLiteral(value)(rest)

let internString value state =
    match state with
        | CoreLoweringState { stringLiterals = literals, nextStringId = nextId } ->
            match findStringLiteral(value)(literals) with
                | Some(label) -> StringInterning(state = state, label = label)
                | None ->
                    let label = "str_" + Ashes.Text.fromInt(nextId)
                    in
                        StringInterning(
                            state = state
                            |> (given (current: CoreLoweringState) -> current with nextStringId = nextId + 1)
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

let recursive lowerVariable name state =
    match state with
        | CoreLoweringState { bindings = bindings } ->
            match lookupBinding(name)(bindings) with
                | None -> failure(state)(UnknownLoweringBinding(name))
                | Some(binding) -> lowerBoundVariable(binding)(state)
and lowerBoundVariable binding state =
    match instantiateBinding(binding)(state) with
        | InstantiatedBinding { state = instantiatedState, semanticType = semanticType } ->
            match binding with
                | CoreBinding { location = CoreSelf(label, environmentSize) } ->
                    match freshTemp(instantiatedState) with
                        | FreshTemp { state = environmentState, temp = environmentTemp } ->
                            match freshTemp(environmentState) with
                                | FreshTemp { state = closureState, temp = closureTemp } ->
                                    closureState
                                    |> emit(LoadLocal(environmentTemp)(0))
                                    |> emit(MakeClosure(
                                        closureTemp,
                                        label,
                                        environmentTemp,
                                        environmentSize,
                                        false,
                                        false,
                                        false
                                    ))
                                    |> success(closureTemp)(semanticType)
                | CoreBinding { location = CoreLocal(slot) } ->
                    match freshTemp(instantiatedState) with
                        | FreshTemp { state = tempState, temp = temp } ->
                            tempState
                            |> emit(LoadLocal(temp)(slot))
                            |> success(temp)(semanticType)
                | CoreBinding { location = CoreEnvironment(index) } ->
                    match freshTemp(instantiatedState) with
                        | FreshTemp { state = tempState, temp = temp } ->
                            tempState
                            |> emit(LoadEnv(temp)(index))
                            |> success(temp)(semanticType)

let addBinding name scheme location state =
    match state with
        | CoreLoweringState { bindings = bindings } ->
            let binding = CoreBinding(name = name, scheme = scheme, location = location)
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

let lowerStoredLet name body lower outerBindings valueTemp valueType fresh =
    match fresh with
        | FreshLocal { state = state, local = local } ->
            let storedState =
                emit(StoreLocal(local)(valueTemp))(state)
            in
                let scheme =
                    generalize(bindingSchemes(outerBindings))(resolveType(storedState)(valueType))([])
                in
                    storedState
                    |> addBinding(name)(scheme)(CoreLocal(local))
                    |> lower(body)
                    |> restoreLoweredBindings(outerBindings)

let finishLetValue name body lower outerBindings lowered =
    match lowered with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            state
            |> freshLocal
            |> lowerStoredLet(name)(body)(lower)(outerBindings)(temp)(semanticType)

let lowerLet name value body lower state =
    match state with
        | CoreLoweringState { bindings = outerBindings } ->
            state
            |> lower(value)
            |> finishLetValue(name)(body)(lower)(outerBindings)

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
        | ExprVar(name) -> addFreeName(name)(bound)(free)
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
        | ExprMatch(value, cases, _position) ->
            free
            |> collectFree(value)(bound)
            |> collectMatchCasesFree(cases)(bound)
        | _ -> free
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

let recursive capturedBindings names bindings reversed =
    match names with
        | [] -> reverse(reversed)
        | name :: rest ->
            match lookupBinding(name)(bindings) with
                | None -> capturedBindings(rest)(bindings)(reversed)
                | Some(binding) -> capturedBindings(rest)(bindings)(binding :: reversed)

let recursive fillEnvironment environmentTemp captures index state =
    match captures with
        | [] -> success(environmentTemp)(SemInt)(state)
        | binding :: rest ->
            match lowerBoundVariable(binding)(state) with
                | LoweredCoreValue { state = captureState, error = Some(error) } -> failure(captureState)(error)
                | LoweredCoreValue { state = captureState, temp = captureTemp, error = None } ->
                    let storedState =
                        emit(StoreMemOffset(environmentTemp)(index * 8)(captureTemp))(captureState)
                    in fillEnvironment(environmentTemp)(rest)(index + 1)(storedState)

let recursive captureCount captures =
    match captures with
        | [] -> 0
        | _ :: rest -> 1 + captureCount(rest)

let allocateEnvironment captures stackAllocate state =
    match freshTemp(state) with
        | FreshTemp { state = tempState, temp = environmentTemp } ->
            match captures with
                | [] ->
                    tempState
                    |> emit(LoadConstInt(environmentTemp)(0))
                    |> success(environmentTemp)(SemInt)
                | _ ->
                    let byteCount = 8 * captureCount(captures)
                    in
                        let allocatedState =
                            if stackAllocate
                            then
                                emit(AllocStack(environmentTemp)(byteCount))(tempState)
                            else
                                emit(Alloc(environmentTemp)(byteCount)(false))(tempState)
                        in fillEnvironment(environmentTemp)(captures)(0)(allocatedState)

let recursive capturedScope captures index =
    match captures with
        | [] -> []
        | CoreBinding { name = name, scheme = scheme } :: rest ->
            let binding = CoreBinding(name = name, scheme = scheme, location = CoreEnvironment(index))
            in binding :: capturedScope(rest)(index + 1)

let lambdaOrigin label =
    IrFunctionOrigin(
        generatedLabel = label,
        originKind = ClosureHelperOrigin,
        sourceOrigin = None,
        parentGeneratedLabel = None,
        compilerOwner = None,
        stableDiscriminator = None,
        generationLocation = None
    )

let finishLiftedFunction label origin bodyState =
    match bodyState with
        | CoreLoweringState { reversedInstructions = instructions, functions = functions, nextLocal = localCount, nextTemp = tempCount } ->
            let function =
                IrFunction(
                    label = label,
                    instructions = reverse(instructions),
                    localCount = localCount,
                    tempCount = tempCount,
                    hasEnvAndArgParams = true,
                    coroutine = None,
                    localNames = [],
                    localTypes = [],
                    origin = Some(origin),
                    lifetimesPlaced = false
                )
            in bodyState with functions = append(functions)([function])

let restoreOuterFrame outer bodyState =
    match bodyState with
        | CoreLoweringState { functions = functions, nextLambdaId = nextLambdaId, nextLabelId = nextLabelId, nextStringId = nextStringId, stringLiterals = stringLiterals, typeSupply = typeSupply, substitution = substitution } ->
            outer
            |> (given (current: CoreLoweringState) -> current with functions = functions)
            |> (given (current: CoreLoweringState) -> current with nextLambdaId = nextLambdaId)
            |> (given (current: CoreLoweringState) -> current with nextLabelId = nextLabelId)
            |> (given (current: CoreLoweringState) -> current with nextStringId = nextStringId)
            |> (given (current: CoreLoweringState) -> current with stringLiterals = stringLiterals)
            |> (given (current: CoreLoweringState) -> current with typeSupply = typeSupply)
            |> (given (current: CoreLoweringState) -> current with substitution = substitution)

let emitClosure label environmentTemp captureTotal stackAllocate state =
    match freshTemp(state) with
        | FreshTemp { state = tempState, temp = closureTemp } ->
            let byteCount = captureTotal * 8
            in
                let closureState =
                    if stackAllocate
                    then
                        emit(MakeClosureStack(closureTemp)(label)(environmentTemp)(byteCount)(false)(false))(tempState)
                    else
                        false
                        |> MakeClosure(closureTemp)(label)(environmentTemp)(byteCount)(false)(false)
                        |> (given (instruction) -> emit(instruction)(tempState))
                in (closureState, closureTemp)

let prepareLambdaBodyState parameter parameterType captures lambdaId state =
    (let functionBindings =
        CoreBinding(
            name = parameter,
            scheme = emptyScheme(parameterType),
            location = CoreLocal(1)
        ) :: capturedScope(captures)(0)
    in
        state
        |> (given (current: CoreLoweringState) -> current with reversedInstructions = [])
        |> (given (current: CoreLoweringState) -> current with bindings = functionBindings)
        |> (given (current: CoreLoweringState) -> current with nextTemp = 0)
        |> (given (current: CoreLoweringState) -> current with nextLocal = 2)
        |> (given (current: CoreLoweringState) -> current with nextLambdaId = lambdaId + 1))

let finishClosureResult parameterType bodyType finishedBody closure =
    match closure with
        | (closureState, closureTemp) ->
            let resultType = resolveType(finishedBody)(bodyType)
            in
                success(closureTemp)(SemFunction(parameterType)(resultType)(None))(closureState)

let finishLambdaBody label environmentTemp captures stackAllocate typedOuter parameterType lowered =
    match lowered with
        | LoweredCoreValue { state = failedBody, error = Some(error) } -> failure(failedBody)(error)
        | LoweredCoreValue { state = loweredBody, temp = bodyTemp, semanticType = bodyType, error = None } ->
            let finishedBody =
                loweredBody
                |> emit(Return(bodyTemp))
                |> finishLiftedFunction(label)(lambdaOrigin(label))
            in
                let restored = restoreOuterFrame(typedOuter)(finishedBody)
                in
                    restored
                    |> emitClosure(label)(environmentTemp)(captureCount(captures))(stackAllocate)
                    |> finishClosureResult(parameterType)(bodyType)(finishedBody)

let lowerLambdaBody parameter body stackAllocate lower lambdaId captures environmentTemp fresh =
    match fresh with
        | FreshType { state = typedOuter, semanticType = parameterType } ->
            let label = "lambda_" + Ashes.Text.fromInt(lambdaId)
            in
                let bodyState = prepareLambdaBodyState(parameter)(parameterType)(captures)(lambdaId)(typedOuter)
                in
                    bodyState
                    |> lower(body)
                    |> finishLambdaBody(label)(environmentTemp)(captures)(stackAllocate)(typedOuter)(parameterType)

let lowerLambdaEnvironment parameter body stackAllocate lower lambdaId captures allocated =
    match allocated with
        | LoweredCoreValue { state = environmentState, error = Some(error) } -> failure(environmentState)(error)
        | LoweredCoreValue { state = environmentState, temp = environmentTemp, error = None } ->
            environmentState
            |> freshType
            |> lowerLambdaBody(parameter)(body)(stackAllocate)(lower)(lambdaId)(captures)(environmentTemp)

let lowerLambda parameter body stackAllocate lower state =
    match state with
        | CoreLoweringState { bindings = outerBindings, nextLambdaId = lambdaId } ->
            let freeNames = collectFree(body)([parameter])([])
            in
                let captures = capturedBindings(freeNames)(outerBindings)([])
                in
                    state
                    |> allocateEnvironment(captures)(stackAllocate)
                    |> lowerLambdaEnvironment(parameter)(body)(stackAllocate)(lower)(lambdaId)(captures)

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

let recursive lowerCallFunction expression lower state =
    match expression with
        | ExprAt(_span, inner) -> lowerCallFunction(inner)(lower)(state)
        | ExprLambda(parameter, body, _annotation) -> lowerLambda(parameter)(body)(true)(lower)(state)
        | _ -> lower(expression)(state)

let finishCoreCall functionTemp argumentTemp resultType binding =
    match binding with
        | (unifiedState, Some(error)) -> failure(unifiedState)(error)
        | (unifiedState, None) ->
            match freshTemp(unifiedState) with
                | FreshTemp { state = targetState, temp = target } ->
                    targetState
                    |> emit(CallClosure(target)(functionTemp)(argumentTemp)(-1))
                    |> success(target)(resolveType(unifiedState)(resultType))

let lowerCoreCallArgument functionTemp expectedArgumentType resultType loweredArgument =
    match loweredArgument with
        | LoweredCoreValue { state = argumentState, error = Some(error) } -> failure(argumentState)(error)
        | LoweredCoreValue { state = argumentState, temp = argumentTemp, semanticType = argumentType, error = None } ->
            argumentState
            |> bindType(expectedArgumentType)(argumentType)
            |> finishCoreCall(functionTemp)(argumentTemp)(resultType)

let lowerCoreCallTyped argument lower functionTemp resolved =
    match resolved with
        | FunctionTypeResolution { state = typedState, error = Some(error) } -> failure(typedState)(error)
        | FunctionTypeResolution { state = typedState, argumentType = expectedType, resultType = resultType, error = None } ->
            typedState
            |> lower(argument)
            |> lowerCoreCallArgument(functionTemp)(expectedType)(resultType)

let lowerCoreCallFunction argument lower loweredFunction =
    match loweredFunction with
        | LoweredCoreValue { state = functionState, error = Some(error) } -> failure(functionState)(error)
        | LoweredCoreValue { state = functionState, temp = functionTemp, semanticType = functionType, error = None } ->
            functionState
            |> ensureFunctionType(functionType)
            |> lowerCoreCallTyped(argument)(lower)(functionTemp)

let lowerCall function argument lower state =
    state
    |> lowerCallFunction(function)(lower)
    |> lowerCoreCallFunction(argument)(lower)

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

let lowerIfThenBranch thenBranch lower plan =
    match plan with
        | CoreIfPlan { state = failedState, error = Some(error) } ->
            CoreIfThen(
                state = failedState,
                resultSlot = -1,
                endLabel = "",
                thenType = SemNever,
                error = Some(error)
            )
        | CoreIfPlan { state = thenState, resultSlot = resultSlot, elseLabel = elseLabel, endLabel = endLabel, error = None } ->
            match lower(thenBranch)(thenState) with
                | LoweredCoreValue { state = failedState, error = Some(error) } ->
                    CoreIfThen(
                        state = failedState,
                        resultSlot = resultSlot,
                        endLabel = endLabel,
                        thenType = SemNever,
                        error = Some(error)
                    )
                | LoweredCoreValue { state = resultState, temp = temp, semanticType = semanticType, error = None } ->
                    CoreIfThen(
                        state = resultState
                        |> emit(StoreLocal(resultSlot)(temp))
                        |> emit(Jump(endLabel))
                        |> emit(Label(elseLabel)),
                        resultSlot = resultSlot,
                        endLabel = endLabel,
                        thenType = semanticType,
                        error = None
                    )

let finishIfElseBranch elseBranch lower loweredThen =
    match loweredThen with
        | CoreIfThen { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | CoreIfThen { state = elseState, resultSlot = resultSlot, endLabel = endLabel, thenType = thenType, error = None } ->
            match lower(elseBranch)(elseState) with
                | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                | LoweredCoreValue { state = resultState, temp = temp, semanticType = elseType, error = None } ->
                    match bindType(thenType)(elseType)(resultState) with
                        | (failedState, Some(error)) -> failure(failedState)(error)
                        | (typedState, None) ->
                            match freshTemp(typedState) with
                                | FreshTemp { state = targetState, temp = target } ->
                                    targetState
                                    |> emit(StoreLocal(resultSlot)(temp))
                                    |> emit(Label(endLabel))
                                    |> emit(LoadLocal(target)(resultSlot))
                                    |> success(target)(resolveType(typedState)(thenType))

let lowerIf condition thenBranch elseBranch lower state =
    state
    |> lower(condition)
    |> prepareIfPlan
    |> lowerIfThenBranch(thenBranch)(lower)
    |> finishIfElseBranch(elseBranch)(lower)

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

let lowerPatternVariable name valueTemp valueType state =
    match freshLocal(state) with
        | FreshLocal { state = localState, local = local } ->
            LoweredCorePattern(
                state = localState
                |> emit(StoreLocal(local)(valueTemp))
                |> addBinding(name)(emptyScheme(valueType))(CoreLocal(local)),
                error = None
            )

let recursive lowerPattern pattern valueTemp valueType failLabel state =
    match pattern with
        | PatternAt(_span, inner) -> lowerPattern(inner)(valueTemp)(valueType)(failLabel)(state)
        | PatternWildcard -> LoweredCorePattern(state = state, error = None)
        | PatternVar(name) -> lowerPatternVariable(name)(valueTemp)(valueType)(state)
        | PatternInt(value) ->
            state
            |> lowerConstant(given (target) -> LoadConstInt(target)(value))(SemInt)
            |> finishPatternComparison(valueTemp)(valueType)(failLabel)(CmpIntEq)
        | PatternRune(value) ->
            state
            |> lowerConstant(given (target) -> LoadConstInt(target)(value))(SemRune)
            |> finishPatternComparison(valueTemp)(valueType)(failLabel)(CmpIntEq)
        | PatternBool(value) ->
            state
            |> lowerConstant(given (target) -> LoadConstBool(target)(value))(SemBool)
            |> finishPatternComparison(valueTemp)(valueType)(failLabel)(CmpIntEq)
        | PatternString(value) ->
            state
            |> lowerString(value)
            |> finishPatternComparison(valueTemp)(valueType)(failLabel)(CmpStrEq)
        | unsupported ->
            LoweredCorePattern(
                state = state,
                error = Some(unsupported
                |> patternName
                |> UnsupportedCoreLoweringPattern)
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

let finishMatchArm body resultSlot endLabel resultType outerBindings lower guarded =
    match guarded with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValue { state = bodyState, error = None } ->
            match lower(body)(bodyState) with
                | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                | LoweredCoreValue { state = resultState, temp = temp, semanticType = bodyType, error = None } ->
                    match bindType(resultType)(bodyType)(resultState) with
                        | (failedState, Some(error)) -> failure(failedState)(error)
                        | (typedState, None) ->
                            typedState
                            |> emit(StoreLocal(resultSlot)(temp))
                            |> emit(Jump(endLabel))
                            |> restoreBindings(outerBindings)
                            |> success(temp)(resultType)

let lowerMatchArm pattern body guard failLabel lower plan =
    match plan with
        | CoreMatchPlan { state = state, valueTemp = valueTemp, valueType = valueType, resultSlot = resultSlot, endLabel = endLabel, resultType = resultType } ->
            match state with
                | CoreLoweringState { bindings = outerBindings } ->
                    state
                    |> lowerPattern(pattern)(valueTemp)(valueType)(failLabel)
                    |> lowerMatchGuard(guard)(failLabel)(lower)
                    |> finishMatchArm(body)(resultSlot)(endLabel)(resultType)(outerBindings)(lower)

let recastMatchPlan plan lowered =
    match (plan, lowered) with
        | (CoreMatchPlan { valueTemp = valueTemp, valueType = valueType, resultSlot = resultSlot, endLabel = endLabel, noMatchLabel = noMatchLabel, resultType = resultType }, LoweredCoreValue { state = state, error = error }) ->
            CoreMatchPlan(
                state = state,
                valueTemp = valueTemp,
                valueType = valueType,
                resultSlot = resultSlot,
                endLabel = endLabel,
                noMatchLabel = noMatchLabel,
                resultType = resultType,
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

let recursive lowerMatchArms cases lower plan =
    match (cases, plan) with
        | (_cases, CoreMatchPlan { error = Some(_error) }) -> plan
        | ([], _) -> plan
        | ((pattern, body, guard) :: rest, CoreMatchPlan { state = state, noMatchLabel = noMatchLabel }) ->
            match matchFailLabel(rest)(noMatchLabel)(state) with
                | FreshLabel { state = failState, label = failLabel } ->
                    let currentPlan = plan with state = failState
                    in
                        currentPlan
                        |> lowerMatchArm(pattern)(body)(guard)(failLabel)(lower)
                        |> recastMatchPlan(currentPlan)
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
                error = None
            )

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

let finishMatchPlan plan =
    match plan with
        | CoreMatchPlan { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | CoreMatchPlan { state = state, resultSlot = resultSlot, endLabel = endLabel, noMatchLabel = noMatchLabel, resultType = resultType, error = None } ->
            match freshTemp(state) with
                | FreshTemp { state = defaultState, temp = defaultTemp } ->
                    match freshTemp(defaultState) with
                        | FreshTemp { state = resultState, temp = resultTemp } ->
                            resultState
                            |> emit(Label(noMatchLabel))
                            |> emit(LoadConstInt(defaultTemp)(0))
                            |> emit(StoreLocal(resultSlot)(defaultTemp))
                            |> emit(Label(endLabel))
                            |> emit(LoadLocal(resultTemp)(resultSlot))
                            |> success(resultTemp)(resolveType(resultState)(resultType))

let lowerMatch value cases lower state =
    state
    |> lower(value)
    |> prepareMatchPlan
    |> lowerMatchArms(cases)(lower)
    |> finishMatchPlan

let recursive lambdaParts expression =
    match expression with
        | ExprAt(_span, inner) -> lambdaParts(inner)
        | ExprLambda(parameter, body, _annotation) -> Some((parameter, body))
        | _ -> None

let prepareRecursiveBodyState parameter parameterType captures selfBindings state =
    (let functionBindings =
        CoreBinding(
            name = parameter,
            scheme = emptyScheme(parameterType),
            location = CoreLocal(1)
        ) :: append(selfBindings)(capturedScope(captures)(0))
    in
        state
        |> (given (current: CoreLoweringState) -> current with reversedInstructions = [])
        |> (given (current: CoreLoweringState) -> current with bindings = functionBindings)
        |> (given (current: CoreLoweringState) -> current with nextTemp = 0)
        |> (given (current: CoreLoweringState) -> current with nextLocal = 2))

let finishRecursiveLambdaBody prepared captures environmentTemp typedOuter lowered =
    match (prepared, lowered) with
        | (_prepared, LoweredCoreValue { state = failedState, error = Some(error) }) -> failure(failedState)(error)
        | (PreparedCoreRecursiveBinding { label = label, semanticType = semanticType, resultType = resultType }, LoweredCoreValue { state = bodyState, temp = bodyTemp, semanticType = bodyType, error = None }) ->
            match bindType(resultType)(bodyType)(bodyState) with
                | (failedState, Some(error)) -> failure(failedState)(error)
                | (typedBody, None) ->
                    let finishedBody =
                        typedBody
                        |> emit(Return(bodyTemp))
                        |> finishLiftedFunction(label)(lambdaOrigin(label))
                    in
                        let restored = restoreOuterFrame(typedOuter)(finishedBody)
                        in
                            match emitClosure(label)(environmentTemp)(captureCount(captures))(false)(restored) with
                                | (closureState, closureTemp) ->
                                    success(closureTemp)(resolveType(finishedBody)(semanticType))(closureState)

let lowerPreparedRecursiveLambda prepared selfBindings captures environmentTemp lower state =
    match prepared with
        | PreparedCoreRecursiveBinding { parameter = parameter, body = body, parameterType = parameterType } ->
            state
            |> prepareRecursiveBodyState(parameter)(parameterType)(captures)(selfBindings)
            |> lower(body)
            |> finishRecursiveLambdaBody(prepared)(captures)(environmentTemp)(state)

let preparedSelfBinding environmentSize prepared =
    match prepared with
        | PreparedCoreRecursiveBinding { name = name, label = label, semanticType = semanticType } ->
            CoreBinding(
                name = name,
                scheme = emptyScheme(semanticType),
                location = CoreSelf(label)(environmentSize)
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

let allocatePreparedRecursiveMember name parameter body lambdaId fresh =
    match fresh with
        | FreshLocal { state = state, local = slot } ->
            state
            |> freshFunctionType
            |> finishPreparedRecursiveMember(name)(parameter)(body)(lambdaId)(slot)

let prepareRecursiveMember name value state =
    match (lambdaParts(value), state) with
        | (None, _) ->
            PreparedCoreRecursiveMemberResult(
                state = state,
                member = None,
                error = Some(CoreRecursiveBindingRequiresFunction(name))
            )
        | (Some((parameter, body)), CoreLoweringState { nextLambdaId = lambdaId }) ->
            state
            |> freshLocal
            |> allocatePreparedRecursiveMember(name)(parameter)(body)(lambdaId)

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

let recursive addRecursiveGroupContinuationBindings members outerBindings state =
    match members with
        | [] -> state
        | PreparedCoreRecursiveBinding { name = name, slot = slot, semanticType = semanticType } :: rest ->
            let scheme =
                generalize(bindingSchemes(outerBindings))(resolveType(state)(semanticType))([])
            in
                state
                |> addBinding(name)(scheme)(CoreLocal(slot))
                |> addRecursiveGroupContinuationBindings(rest)(outerBindings)

let finishRecursiveGroupContinuation members outerBindings body lower loweredMembers =
    match loweredMembers with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValue { state = groupState, error = None } ->
            let continuationState = addRecursiveGroupContinuationBindings(members)(outerBindings)(groupState)
            in
                match lower(body)(continuationState) with
                    | LoweredCoreValue { state = resultState, temp = temp, semanticType = semanticType, error = error } ->
                        LoweredCoreValue(
                            state = restoreBindings(outerBindings)(resultState),
                            temp = temp,
                            semanticType = semanticType,
                            error = error
                        )

let lowerPreparedRecursiveGroup bindings body lower outerBindings prepared =
    match prepared with
        | PreparedCoreRecursiveGroup { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | PreparedCoreRecursiveGroup { state = preparedState, members = members, error = None } ->
            let groupNames = recursiveBindingNames(bindings)([])
            in
                let captures =
                    []
                    |> collectRecursiveGroupFree(bindings)(groupNames)
                    |> (given (names) -> capturedBindings(names)(outerBindings)([]))
                in
                    match allocateEnvironment(captures)(false)(preparedState) with
                        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                        | LoweredCoreValue { state = environmentState, temp = environmentTemp, error = None } ->
                            let selfBindings =
                                preparedSelfBindings(captureCount(captures))(members)
                            in
                                environmentState
                                |> lowerPreparedRecursiveMembers(
                                    members,
                                    selfBindings,
                                    captures,
                                    environmentTemp,
                                    lower
                                )
                                |> finishRecursiveGroupContinuation(members)(outerBindings)(body)(lower)

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

let lowerLetRecursive name value body lower state =
    match state with
        | CoreLoweringState { bindings = outerBindings, nextLambdaId = lambdaId } ->
            []
            |> prepareRecursiveGroup([(name, value)])(state)
            |> relabelSingleRecursive(lambdaId)
            |> lowerPreparedRecursiveGroup([(name, value)])(body)(lower)(outerBindings)

let expressionName expression =
    match expression with
        | ExprBigInt(_) -> "BigInt"
        | ExprQualifiedVar(_, _) -> "qualified variable"
        | _ -> "non-core expression"

let recursive lowerCore expression state =
    match expression with
        | ExprAt(_span, inner) -> lowerCore(inner)(state)
        | ExprInt(value) ->
            lowerConstant(given (target) -> LoadConstInt(target)(value))(SemInt)(state)
        | ExprUInt(value, bits, _raw) ->
            lowerConstant(given (target) -> LoadConstInt(target)(value))(SemUInt(bits))(state)
        | ExprFloat(value, _raw) ->
            lowerConstant(given (target) -> LoadConstFloat(target)(value))(SemFloat)(state)
        | ExprString(value) -> lowerString(value)(state)
        | ExprRune(value) ->
            lowerConstant(given (target) -> LoadConstInt(target)(value))(SemRune)(state)
        | ExprBool(value) ->
            lowerConstant(given (target) -> LoadConstBool(target)(value))(SemBool)(state)
        | ExprVar(name) -> lowerVariable(name)(state)
        | ExprLet(name, value, body, _parameters, _annotation, _requirements) ->
            lowerLet(
                name,
                value,
                body,
                lowerCore,
                state
            )
        | ExprLetRecursive(name, value, body, _parameters, _annotation, _requirements) ->
            lowerLetRecursive(
                name,
                value,
                body,
                lowerCore,
                state
            )
        | ExprIf(condition, thenBranch, elseBranch) -> lowerIf(condition)(thenBranch)(elseBranch)(lowerCore)(state)
        | ExprLambda(parameter, body, _annotation) -> lowerLambda(parameter)(body)(false)(lowerCore)(state)
        | ExprCall(function, argument, _whitespace, _layout) -> lowerCall(function)(argument)(lowerCore)(state)
        | ExprMatch(value, cases, _position) -> lowerMatch(value)(cases)(lowerCore)(state)
        | unsupported ->
            failure(state)(unsupported
            |> expressionName
            |> UnsupportedCoreLoweringExpression)

let entryOrigin =
    IrFunctionOrigin(
        generatedLabel = "_start_main",
        originKind = ProgramEntryOrigin,
        sourceOrigin = None,
        parentGeneratedLabel = None,
        compilerOwner = None,
        stableDiscriminator = None,
        generationLocation = None
    )

let hasFunctions functions =
    match functions with
        | [] -> false
        | _ -> true

let entryInstructions temp instructions =
    reverse(IrInstruction(
        instruction = Return(temp),
        location = None
    ) :: instructions)

let failedCoreLowering error =
    CoreLoweringResult(
        program = None,
        semanticType = SemNever,
        error = Some(error)
    )

let buildProgram lowered =
    match lowered with
        | LoweredCoreValue { error = Some(error) } -> failedCoreLowering(error)
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            match state with
                | CoreLoweringState { reversedInstructions = instructions, functions = functions, nextLocal = localCount, nextTemp = tempCount, stringLiterals = stringLiterals } ->
                    let entry =
                        IrFunction(
                            label = "_start_main",
                            instructions = entryInstructions(temp)(instructions),
                            localCount = localCount,
                            tempCount = tempCount,
                            hasEnvAndArgParams = false,
                            coroutine = None,
                            localNames = [],
                            localTypes = [],
                            origin = Some(entryOrigin),
                            lifetimesPlaced = false
                        )
                    in
                        let program =
                            IrProgram(
                                entryFunction = entry,
                                functions = functions,
                                stringLiterals = stringLiterals,
                                externalFunctions = [],
                                externalOpaqueTypes = [],
                                usesPrintInt = false,
                                usesPrintStr = false,
                                usesPrintBool = false,
                                usesConcatStr = false,
                                usesClosures = hasFunctions(functions),
                                usesAsync = false,
                                capabilityHandlerGlobals = 0,
                                traitEvidence = emptyTraitEvidenceAnnotations
                            )
                        in
                            CoreLoweringResult(
                                program = Some(program),
                                semanticType = resolveType(state)(semanticType),
                                error = None
                            )

let lowerCoreExpression expression =
    Unit
    |> initialState
    |> lowerCore(expression)
    |> buildProgram

let lowerCoreRecursiveGroup bindings body =
    Unit
    |> initialState
    |> lowerRecursiveGroup(bindings)(body)(lowerCore)
    |> buildProgram
