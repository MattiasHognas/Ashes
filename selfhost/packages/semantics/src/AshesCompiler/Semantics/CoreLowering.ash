// Lowers the strict functional core into semantic IR before ownership placement.
//
// Invariants:
// - Callees are evaluated before arguments, and curried arguments are applied one at a time.
// - Every function owns independent temp/local counters; closure env and argument locals are 0 and 1.
// - Captures follow first free-use order and occupy consecutive eight-byte environment words.
// - Lifted functions retain generation order, with nested functions preceding their enclosing function.

import Ashes.Collection.List.append
import Ashes.Collection.List.reverse
import AshesCompiler.Frontend.Syntax.Expr
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
)

type CoreLoweringError =
    | UnknownLoweringBinding(Str)
    | CoreCallRequiresFunction(SemanticType)
    | CoreCallTypeMismatch(UnificationError)
    | UnsupportedCoreLoweringExpression(Str)
    deriving {Eq, Show}

type CoreLoweringResult =
    | program: Maybe(IrProgram)
    | semanticType: SemanticType
    | error: Maybe(CoreLoweringError)

type CoreBindingLocation =
    | CoreLocal(Int)
    | CoreEnvironment(Int)

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
        nextStringId = 0,
        stringLiterals = [],
        typeSupply = initialTypeVariableSupply(Unit),
        substitution = []
    )

let withNextTemp nextTemp (state: CoreLoweringState) = state with nextTemp = nextTemp

let withNextLocal nextLocal (state: CoreLoweringState) = state with nextLocal = nextLocal

let withTypeSupply typeSupply (state: CoreLoweringState) = state with typeSupply = typeSupply

let withSubstitution substitution (state: CoreLoweringState) = state with substitution = substitution

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
            match freshTemp(instantiatedState) with
                | FreshTemp { state = tempState, temp = temp } ->
                    match binding with
                        | CoreBinding { location = CoreLocal(slot) } ->
                            tempState
                            |> emit(LoadLocal(temp)(slot))
                            |> success(temp)(semanticType)
                        | CoreBinding { location = CoreEnvironment(index) } ->
                            tempState
                            |> emit(LoadEnv(temp)(index))
                            |> success(temp)(semanticType)

let addBinding name scheme location state =
    match state with
        | CoreLoweringState { bindings = bindings } ->
            let binding = CoreBinding(name = name, scheme = scheme, location = location)
            in state with bindings = binding :: bindings

let restoreBindings bindings (state: CoreLoweringState) = state with bindings = bindings

let lowerLet name value body lower state =
    match state with
        | CoreLoweringState { bindings = outerBindings } ->
            match lower(value)(state) with
                | LoweredCoreValue { state = valueState, error = Some(error) } -> failure(valueState)(error)
                | LoweredCoreValue { state = valueState, temp = valueTemp, semanticType = valueType, error = None } ->
                    match freshLocal(valueState) with
                        | FreshLocal { state = localState, local = local } ->
                            let storedState =
                                emit(StoreLocal(local)(valueTemp))(localState)
                            in
                                let scheme =
                                    generalize(bindingSchemes(outerBindings))(resolveType(storedState)(valueType))([])
                                in
                                    let bodyState = addBinding(name)(scheme)(CoreLocal(local))(storedState)
                                    in
                                        match lower(body)(bodyState) with
                                            | LoweredCoreValue { state = resultState, temp = temp, semanticType = resultType, error = error } ->
                                                LoweredCoreValue(
                                                    state = restoreBindings(outerBindings)(resultState),
                                                    temp = temp,
                                                    semanticType = resultType,
                                                    error = error
                                                )

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

let recursive collectFree expression bound free =
    match expression with
        | ExprAt(_span, inner) -> collectFree(inner)(bound)(free)
        | ExprVar(name) -> addFreeName(name)(bound)(free)
        | ExprLet(name, value, body, _parameters, _annotation, _requirements) ->
            let valueFree = collectFree(value)(bound)(free)
            in collectFree(body)(name :: bound)(valueFree)
        | ExprLambda(parameter, body, _annotation) -> collectFree(body)(parameter :: bound)(free)
        | ExprCall(function, argument, _whitespace, _layout) ->
            let functionFree = collectFree(function)(bound)(free)
            in collectFree(argument)(bound)(functionFree)
        | _ -> free

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
        | CoreLoweringState { functions = functions, nextLambdaId = nextLambdaId, nextStringId = nextStringId, stringLiterals = stringLiterals, typeSupply = typeSupply, substitution = substitution } ->
            outer
            |> (given (current: CoreLoweringState) -> current with functions = functions)
            |> (given (current: CoreLoweringState) -> current with nextLambdaId = nextLambdaId)
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

let ensureFunctionType semanticType state =
    match resolveType(state)(semanticType) with
        | SemFunction(argumentType, resultType, _row) ->
            FunctionTypeResolution(
                state = state,
                argumentType = argumentType,
                resultType = resultType,
                error = None
            )
        | SemVariable(_id) ->
            match freshType(state) with
                | FreshType { state = argumentState, semanticType = argumentType } ->
                    match freshType(argumentState) with
                        | FreshType { state = resultState, semanticType = resultType } ->
                            let functionType = SemFunction(argumentType)(resultType)(None)
                            in
                                match bindType(semanticType)(functionType)(resultState) with
                                    | (unifiedState, None) ->
                                        FunctionTypeResolution(
                                            state = unifiedState,
                                            argumentType = argumentType,
                                            resultType = resultType,
                                            error = None
                                        )
                                    | (failedState, Some(error)) ->
                                        FunctionTypeResolution(
                                            state = failedState,
                                            argumentType = SemNever,
                                            resultType = SemNever,
                                            error = Some(error)
                                        )
        | other ->
            FunctionTypeResolution(
                state = state,
                argumentType = SemNever,
                resultType = SemNever,
                error = Some(CoreCallRequiresFunction(other))
            )

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

let expressionName expression =
    match expression with
        | ExprBigInt(_) -> "BigInt"
        | ExprQualifiedVar(_, _) -> "qualified variable"
        | ExprLetRecursive(_, _, _, _, _, _) -> "recursive let"
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
        | ExprLambda(parameter, body, _annotation) -> lowerLambda(parameter)(body)(false)(lowerCore)(state)
        | ExprCall(function, argument, _whitespace, _layout) -> lowerCall(function)(argument)(lowerCore)(state)
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
