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
import AshesCompiler.Frontend.Syntax.LetBindingSyntax
import AshesCompiler.Frontend.Syntax.TopLevelItem
import AshesCompiler.Frontend.Syntax.ProgramSyntax
import AshesCompiler.Frontend.Syntax.callArgumentsInline
import AshesCompiler.Frontend.Token.TextSpan
import AshesCompiler.Semantics.CoreBuiltinLowering
import AshesCompiler.Semantics.CoreCapabilityLowering
import AshesCompiler.Semantics.CoreExternalLowering
import AshesCompiler.Semantics.ExternalAbi
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.SourceContext
import AshesCompiler.Semantics.TypeSchemes
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.Unification
export (
    type CoreLoweringError(..),
    type CoreLoweringResult(..),
    type CoreConstructorLayout(..),
    value lowerCoreExpression,
    value lowerCoreExpressionWithLayouts,
    value lowerCoreExpressionWithContext,
    value lowerCoreExpressionWithFullContext,
    value lowerCoreExpressionWithCompleteContext,
    value lowerCoreExpressionLocated,
    value lowerCoreRecursiveGroup,
    value pruneDeadCaptures,
    value lowerCoreProgram,
    value lowerCoreProgramWithSource,
)

type CoreLoweringError =
    | UnknownLoweringBinding(Str)
    | CoreCallRequiresFunction(SemanticType)
    | CoreCallTypeMismatch(UnificationError)
    | CoreOperatorTypeMismatch(Str, SemanticType, SemanticType)
    | CoreConstructorArityMismatch(Str, Int, Int)
    | CoreBuiltinArityMismatch(Str, Str, Int, Int)
    | UnsupportedCoreBuiltinLowering(Str)
    | CoreExternalDirectOnlyViolation(Str)
    | UnsupportedCoreExternalLowering(Str)
    | CoreUnhandledCapabilityOperation(Str, Str)
    | CoreAmbiguousCapabilitySatisfaction(Str)
    | UnknownCoreRecordField(Str, Str)
    | CoreRecordUpdateRequiresRecord(SemanticType)
    | UnsupportedCoreLoweringPattern(Str)
    | CoreRecursiveBindingRequiresFunction(Str)
    | UnsupportedCoreLoweringExpression(Str)
    | DuplicateTopLevelBinding(Str)
    deriving {Eq, Show}

type CoreLoweringResult =
    | program: Maybe(IrProgram)
    | semanticType: SemanticType
    | error: Maybe(CoreLoweringError)

type CoreConstructorLayout =
    | name: Str
    | tag: Int
    | scheme: TypeScheme
    | fieldNames: List(Str)
    | isZeroCost: Bool
    deriving {Eq, Show}

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
    | constructorLayouts: List(CoreConstructorLayout)
    | builtinLayouts: List(CoreBuiltinLayout)
    | externalLayouts: List(CoreExternalFunctionLayout)
    | externalFunctions: List(ExternalFunctionAbi)
    | externalOpaqueTypes: List(Str)
    | capabilityLayouts: List(CoreCapabilityLayout)
    | staticProviders: List(CoreStaticProviderLayout)
    | capabilityGlobalCount: Int
    | nextTemp: Int
    | nextLocal: Int
    | nextLambdaId: Int
    | nextLabelId: Int
    | nextStringId: Int
    | stringLiterals: List(IrStringLiteral)
    | typeSupply: TypeVariableSupply
    | substitution: List((Int, SemanticType))
    | sourceContext: Maybe(SourceContext)
    | currentSpan: Maybe(TextSpan)
    | currentItem: Int

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

// The cases of one match that share an outer constructor tag, in first-seen order. A group is a
// trivial single case only while it has exactly one case whose sub-patterns are all catch-alls,
// the one shape the switch can dispatch to without re-testing the tag it just proved.
type CoreTagGroup =
    | tag: Int
    | constructorName: Str
    | caseIndices: List(Int)
    | trivialSingleCase: Bool

type CoreCaseClass =
    | CaseConstructor(Str, Int, Str, Bool)
    | CaseDefault
    | CaseReject

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

type LoweredCoreValues =
    | state: CoreLoweringState
    | temps: List(Int)
    | semanticTypes: List(SemanticType)
    | error: Maybe(CoreLoweringError)

type LoweredCoreBinary =
    | state: CoreLoweringState
    | leftTemp: Int
    | leftType: SemanticType
    | rightTemp: Int
    | rightType: SemanticType
    | error: Maybe(CoreLoweringError)

type CoreBinaryOperator =
    | CoreAddOperator
    | CoreSubtractOperator
    | CoreMultiplyOperator
    | CoreDivideOperator
    | CoreModuloOperator
    | CoreBitwiseAndOperator
    | CoreBitwiseOrOperator
    | CoreBitwiseXorOperator
    | CoreShiftLeftOperator
    | CoreShiftRightOperator
    | CoreGreaterOperator
    | CoreGreaterOrEqualOperator
    | CoreLessOperator
    | CoreLessOrEqualOperator
    | CoreEqualOperator
    | CoreNotEqualOperator

type ReservedCoreTemps =
    | state: CoreLoweringState
    | first: Int

type CoreConstructorShape =
    | state: CoreLoweringState
    | layout: CoreConstructorLayout
    | parameterTypes: List(SemanticType)
    | resultType: SemanticType

type CoreBuiltinShape =
    | state: CoreLoweringState
    | layout: CoreBuiltinLayout
    | parameterTypes: List(SemanticType)
    | resultType: SemanticType

type CoreCallSpine =
    | root: Expr
    | arguments: List(Expr)

type CoreRecordArguments =
    | expressions: List(Expr)
    | error: Maybe(CoreLoweringError)

type FreshCoreTypes =
    | state: CoreLoweringState
    | semanticTypes: List(SemanticType)

type CorePatternField =
    | index: Int
    | semanticType: SemanticType

let emptyScheme semanticType =
    TypeScheme(
        quantified = [],
        body = semanticType,
        constraints = []
    )

let initialStateWithCompleteContext constructorLayouts builtinLayouts externalLayouts externalFunctions externalOpaqueTypes capabilityLayouts staticProviders capabilityGlobalCount unit =
    CoreLoweringState(
        reversedInstructions = [],
        functions = [],
        bindings = [],
        constructorLayouts = constructorLayouts,
        builtinLayouts = builtinLayouts,
        externalLayouts = externalLayouts,
        externalFunctions = externalFunctions,
        externalOpaqueTypes = externalOpaqueTypes,
        capabilityLayouts = capabilityLayouts,
        staticProviders = staticProviders,
        capabilityGlobalCount = capabilityGlobalCount,
        nextTemp = 0,
        nextLocal = 0,
        nextLambdaId = 0,
        nextLabelId = 0,
        nextStringId = 0,
        stringLiterals = [],
        typeSupply = initialTypeVariableSupply(Unit),
        substitution = [],
        sourceContext = None,
        currentSpan = None,
        currentItem = 0
    )

let initialStateWithFullContext constructorLayouts builtinLayouts externalLayouts externalFunctions externalOpaqueTypes unit = initialStateWithCompleteContext(constructorLayouts)(builtinLayouts)(externalLayouts)(externalFunctions)(externalOpaqueTypes)([])([])(0)(unit)

let initialStateWithContext constructorLayouts builtinLayouts unit = initialStateWithFullContext(constructorLayouts)(builtinLayouts)([])([])([])(unit)

let initialStateWithLayouts layouts unit = initialStateWithContext(layouts)([])(unit)

let initialState unit = initialStateWithContext([])([])(unit)

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

// Every emitted instruction carries the innermost enclosing source span resolved through the
// installed source context (runtime machinery stays unlocated); without a context, no location.
let emit kind state =
    match state with
        | CoreLoweringState { reversedInstructions = instructions, sourceContext = context, currentSpan = span, currentItem = item } ->
            let wrapped = tagItemInstruction(kind)(span)(item)(context)
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

let recursive findConstructorLayout (name: Str) (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> None
        | (CoreConstructorLayout { name = candidate } as layout) :: rest ->
            if name == candidate
            then Some(layout)
            else findConstructorLayout(name)(rest)

let constructorLayout name state =
    match state with
        | CoreLoweringState { constructorLayouts = layouts } -> findConstructorLayout(name)(layouts)

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
        | CoreLoweringState { builtinLayouts = layouts } -> findBuiltinLayout(moduleName)(memberName)(layouts)

let recursive splitConstructorType semanticType reversed =
    match semanticType with
        | SemFunction(parameterType, resultType, _row) -> splitConstructorType(resultType)(parameterType :: reversed)
        | resultType -> (reverse(reversed), resultType)

let instantiateConstructor layout state =
    match (layout, state) with
        | (CoreConstructorLayout { scheme = scheme }, CoreLoweringState { typeSupply = supply }) ->
            match instantiate(scheme)(supply) with
                | InstantiationResult { semanticType = semanticType, supply = nextSupply } ->
                    match splitConstructorType(semanticType)([]) with
                        | (parameterTypes, resultType) ->
                            CoreConstructorShape(
                                state = withTypeSupply(nextSupply)(state),
                                layout = layout,
                                parameterTypes = parameterTypes,
                                resultType = resultType
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
    match state with
        | CoreLoweringState { constructorLayouts = layouts } -> findRecordLayout(typeName)(layouts)

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
            (state, Some(actual
            |> coreListLength
            |> TypeArityMismatch(
                coreListLength(expected)
            )
            |> CoreCallTypeMismatch))

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

let emitPrunedClosure label captures stackAllocate parameterType bodyType finishedBody allocated =
    match allocated with
        | LoweredCoreValue { state = environmentState, error = Some(error) } -> failure(environmentState)(error)
        | LoweredCoreValue { state = environmentState, temp = environmentTemp, error = None } ->
            environmentState
            |> emitClosure(label)(environmentTemp)(captureCount(captures))(stackAllocate)
            |> finishClosureResult(parameterType)(bodyType)(finishedBody)

let finishLambdaBody label captures stackAllocate typedOuter parameterType lowered =
    match lowered with
        | LoweredCoreValue { state = failedBody, error = Some(error) } -> failure(failedBody)(error)
        | LoweredCoreValue { state = loweredBody, temp = bodyTemp, semanticType = bodyType, error = None } ->
            let returned = emit(Return(bodyTemp))(loweredBody)
            in
                match pruneDeadCaptures(captures)(returned.reversedInstructions) with
                    | (survivors, prunedInstructions) ->
                        let finishedBody =
                            finishLiftedFunction(label)(lambdaOrigin(label))((returned with reversedInstructions = prunedInstructions))
                        in
                            finishedBody
                            |> restoreOuterFrame(typedOuter)
                            |> allocateEnvironment(survivors)(stackAllocate)
                            |> emitPrunedClosure(label)(survivors)(stackAllocate)(parameterType)(bodyType)(finishedBody)

let lowerLambdaBody parameter body stackAllocate lower lambdaId captures fresh =
    match fresh with
        | FreshType { state = typedOuter, semanticType = parameterType } ->
            let label = "lambda_" + Ashes.Text.fromInt(lambdaId)
            in
                let bodyState = prepareLambdaBodyState(parameter)(parameterType)(captures)(lambdaId)(typedOuter)
                in
                    bodyState
                    |> lower(body)
                    |> finishLambdaBody(label)(captures)(stackAllocate)(typedOuter)(parameterType)

let lowerLambda parameter body stackAllocate lower state =
    match state with
        | CoreLoweringState { bindings = outerBindings, nextLambdaId = lambdaId } ->
            let freeNames = collectFree(body)([parameter])([])
            in
                let captures = capturedBindings(freeNames)(outerBindings)([])
                in
                    state
                    |> freshType
                    |> lowerLambdaBody(parameter)(body)(stackAllocate)(lower)(lambdaId)(captures)

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

let recursive prepareCorePatternBindings pending seen state =
    match pending with
        | [] -> state
        | pattern :: rest ->
            match pattern with
                | PatternAt(_span, inner) -> prepareCorePatternBindings(inner :: rest)(seen)(state)
                | PatternVar(name) ->
                    if containsName(name)(seen)
                    then prepareCorePatternBindings(rest)(seen)(state)
                    else
                        if isNullaryConstructorPatternName(name)(state)
                        then prepareCorePatternBindings(rest)(seen)(state)
                        else
                            match freshType(state) with
                                | FreshType { state = typedState, semanticType = semanticType } ->
                                    match freshLocal(typedState) with
                                        | FreshLocal { state = localState, local = local } ->
                                            localState
                                            |> addBinding(name)(emptyScheme(semanticType))(CoreLocal(local))
                                            |> prepareCorePatternBindings(rest)(name :: seen)
                | PatternCons(head, tail) -> prepareCorePatternBindings(head :: tail :: rest)(seen)(state)
                | PatternTuple(elements) ->
                    prepareCorePatternBindings(append(elements)(rest))(seen)(state)
                | PatternConstructor(_name, elements) ->
                    prepareCorePatternBindings(append(elements)(rest))(seen)(state)
                | PatternRecord(_name, fields) ->
                    prepareCorePatternBindings(append(coreRecordPatterns(fields))(rest))(seen)(state)
                | PatternAs(inner, name) ->
                    if containsName(name)(seen)
                    then prepareCorePatternBindings(inner :: rest)(seen)(state)
                    else
                        match freshType(state) with
                            | FreshType { state = typedState, semanticType = semanticType } ->
                                match freshLocal(typedState) with
                                    | FreshLocal { state = localState, local = local } ->
                                        localState
                                        |> addBinding(name)(emptyScheme(semanticType))(CoreLocal(local))
                                        |> prepareCorePatternBindings(inner :: rest)(name :: seen)
                | PatternOr(first :: _alternatives) -> prepareCorePatternBindings(first :: rest)(seen)(state)
                | _ -> prepareCorePatternBindings(rest)(seen)(state)

let preparePattern pattern state = prepareCorePatternBindings([pattern])([])(state)

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
    |> lowerConstant(given (target) -> LoadConstInt(target)(0))(SemInt)
    |> finishPatternNonZero(valueTemp)(failLabel)

let finishEmptyListPattern valueTemp valueType failLabel fresh =
    match fresh with
        | FreshType { state = typedState, semanticType = elementType } ->
            match bindType(valueType)(SemList(elementType))(typedState) with
                | (failedState, Some(error)) -> LoweredCorePattern(state = failedState, error = Some(error))
                | (state, None) ->
                    state
                    |> lowerConstant(given (target) -> LoadConstInt(target)(0))(SemInt)
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

let lowerAdtPatternField valueTemp index pattern semanticType failLabel lowerPattern result =
    match result with
        | LoweredCorePattern { error = Some(_error) } -> result
        | LoweredCorePattern { state = state, error = None } ->
            match freshTemp(state) with
                | FreshTemp { state = loadState, temp = fieldTemp } ->
                    loadState
                    |> emit(GetAdtField(fieldTemp)(valueTemp)(index))
                    |> lowerPattern(pattern)(fieldTemp)(semanticType)(failLabel)

let recursive lowerAdtPatternFields patterns types valueTemp index failLabel lowerPattern result =
    match (patterns, types) with
        | ([], []) -> result
        | (pattern :: patternRest, semanticType :: typeRest) ->
            result
            |> lowerAdtPatternField(valueTemp)(index)(pattern)(semanticType)(failLabel)(lowerPattern)
            |> lowerAdtPatternFields(patternRest)(typeRest)(valueTemp)(index + 1)(failLabel)(lowerPattern)
        | _ -> result

let finishConstructorTag valueTemp tag failLabel state =
    match freshTemp(state) with
        | FreshTemp { state = tagState, temp = tagTemp } ->
            tagState
            |> emit(GetAdtTag(tagTemp)(valueTemp))
            |> lowerConstant(given (target) -> LoadConstInt(target)(tag))(SemInt)
            |> finishPatternComparison(tagTemp)(SemInt)(failLabel)(CmpIntEq)

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
        | CoreConstructorShape { state = state, layout = CoreConstructorLayout { tag = tag }, parameterTypes = parameterTypes, resultType = resultType } ->
            if coreListLength(patterns) != coreListLength(parameterTypes)
            then LoweredCorePattern(state = state, error = Some(UnsupportedCoreLoweringPattern("constructor arity")))
            else
                match bindType(valueType)(resultType)(state) with
                    | (failedState, Some(error)) -> LoweredCorePattern(state = failedState, error = Some(error))
                    | (typedState, None) ->
                        typedState
                        |> finishConstructorTag(valueTemp)(tag)(failLabel)
                        |> lowerAdtPatternFields(patterns)(parameterTypes)(valueTemp)(0)(failLabel)(lowerPattern)

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

let recursive lowerRecordPatternFields fields fieldNames fieldTypes valueTemp failLabel lowerPattern result =
    match (fields, result) with
        | ([], _) -> result
        | (_fields, LoweredCorePattern { error = Some(_error) }) -> result
        | ((fieldName, pattern) :: rest, LoweredCorePattern { state = state, error = None }) ->
            match findPatternField(fieldName)(fieldNames)(fieldTypes)(0) with
                | None ->
                    LoweredCorePattern(state = state, error = fieldName
                    |> UnknownCoreRecordField("record pattern")
                    |> Some)
                | Some(CorePatternField { index = index, semanticType = semanticType }) ->
                    result
                    |> lowerAdtPatternField(valueTemp)(index)(pattern)(semanticType)(failLabel)(lowerPattern)
                    |> lowerRecordPatternFields(rest)(fieldNames)(fieldTypes)(valueTemp)(failLabel)(lowerPattern)

let finishRecordPattern fields valueTemp valueType failLabel lowerPattern shape =
    match shape with
        | CoreConstructorShape { state = state, layout = CoreConstructorLayout { tag = tag, fieldNames = fieldNames }, parameterTypes = fieldTypes, resultType = resultType } ->
            match bindType(valueType)(resultType)(state) with
                | (failedState, Some(error)) -> LoweredCorePattern(state = failedState, error = Some(error))
                | (typedState, None) ->
                    typedState
                    |> finishConstructorTag(valueTemp)(tag)(failLabel)
                    |> lowerRecordPatternFields(fields)(fieldNames)(fieldTypes)(valueTemp)(failLabel)(lowerPattern)

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
            |> finishAsPattern(name)(valueTemp)(valueType)
        | PatternOr(alternatives) -> lowerOrPattern(alternatives)(valueTemp)(valueType)(failLabel)(lowerPattern)(state)
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
                    |> preparePattern(pattern)
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

let recursive schemeResultName (semanticType: SemanticType) =
    match semanticType with
        | SemFunction(_, result, _) -> schemeResultName(result)
        | SemNamed(_, name, _) -> Some(name)
        | _ -> None

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
        | CoreConstructorShape { state = state, parameterTypes = parameterTypes, resultType = resultType } ->
            if coreListLength(patterns) != coreListLength(parameterTypes)
            then LoweredCorePattern(state = state, error = Some(UnsupportedCoreLoweringPattern("constructor arity")))
            else
                match bindType(valueType)(resultType)(state) with
                    | (failedState, Some(error)) -> LoweredCorePattern(state = failedState, error = Some(error))
                    | (typedState, None) -> lowerAdtPatternFields(patterns)(parameterTypes)(valueTemp)(0)(failLabel)(lowerPattern)(LoweredCorePattern(state = typedState, error = None))

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

let lowerKnownTagMatchArm pattern body guard failLabel lower (plan: CoreMatchPlan) =
    match plan.state with
        | CoreLoweringState { bindings = outerBindings } ->
            plan.state
            |> preparePattern(pattern)
            |> lowerKnownTagPattern(pattern)(plan.valueTemp)(plan.valueType)(failLabel)
            |> lowerMatchGuard(guard)(failLabel)(lower)
            |> finishMatchArm(body)(plan.resultSlot)(plan.endLabel)(plan.resultType)(outerBindings)(lower)

// The group's cases in their original order; the last one fails to the group's fail target.
let recursive lowerTagGroupCasesLinearly cases (indices: List(Int)) (groupFailLabel: Str) lower (plan: CoreMatchPlan) =
    match (indices, plan) with
        | (_indices, CoreMatchPlan { error = Some(_error) }) -> plan
        | ([], _) -> plan
        | (index :: rest, _) ->
            match nthMatchCase(cases)(index) with
                | None -> plan
                | Some((pattern, body, guard)) ->
                    match rest with
                        | [] ->
                            plan
                            |> lowerMatchArm(pattern)(body)(guard)(groupFailLabel)(lower)
                            |> recastMatchPlan(plan)
                        | _ ->
                            match freshLabel("match_group_next")(plan.state) with
                                | FreshLabel { state = labelState, label = caseFailLabel } ->
                                    let currentPlan = plan with state = labelState
                                    in
                                        currentPlan
                                        |> lowerMatchArm(pattern)(body)(guard)(caseFailLabel)(lower)
                                        |> recastMatchPlan(currentPlan)
                                        |> (given (next: CoreMatchPlan) -> next with state = emit(Label(caseFailLabel))(next.state))
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
                    let labeled = plan with state = emit(Label(defaultLabel))(plan.state)
                    in
                        labeled
                        |> lowerMatchArm(pattern)(body)(guard)(plan.noMatchLabel)(lower)
                        |> recastMatchPlan(labeled)

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
                        match freshTemp(defaultState) with
                            | FreshTemp { state = tagState, temp = tagTemp } ->
                                let switched =
                                    tagState
                                    |> emit(GetAdtTag(tagTemp)(plan.valueTemp))
                                    |> emit(SwitchTag(tagTemp)(switchCases)(defaultLabel))
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

let stateConstructorLayouts state =
    match state with
        | CoreLoweringState { constructorLayouts = layouts } -> layouts

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
                        match trimAdtConstructorNames(stateConstructorLayouts(state))(adtName)([]) with
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

let lowerMatch value cases lower state =
    state
    |> lower(value)
    |> prepareMatchPlan
    |> lowerMatchArmsDispatch(cases)(lower)
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

// Splits the single `lower` continuation lowerPreparedRecursiveGroup uses into two: memberLower
// lowers each recursive member's own body, continuationLower lowers what follows the group. A
// whole-program driver needs the two to differ (member bodies always go through the ordinary
// expression lowerer; the continuation is "the rest of the top-level items", which isn't an Expr
// at all), so the plain single-`lower` form below is now a same-lowerer convenience wrapper.
let lowerPreparedRecursiveGroupWith bindings body memberLower continuationLower outerBindings prepared =
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
                                    memberLower
                                )
                                |> finishRecursiveGroupContinuation(members)(outerBindings)(body)(continuationLower)

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

let lowerLetRecursive name value body lower state =
    match state with
        | CoreLoweringState { bindings = outerBindings, nextLambdaId = lambdaId } ->
            []
            |> prepareRecursiveGroup([(name, value)])(state)
            |> relabelSingleRecursive(lambdaId)
            |> lowerPreparedRecursiveGroup([(name, value)])(body)(lower)(outerBindings)

let recursive emitTupleFields baseTemp index temps state =
    match temps with
        | [] -> state
        | temp :: rest ->
            state
            |> emit(StoreMemOffset(baseTemp)(index * 8)(temp))
            |> emitTupleFields(baseTemp)(index + 1)(rest)

let finishTupleLowering lowered =
    match lowered with
        | LoweredCoreValues { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValues { state = state, temps = temps, semanticTypes = semanticTypes, error = None } ->
            match freshTemp(state) with
                | FreshTemp { state = allocatedState, temp = tupleTemp } ->
                    allocatedState
                    |> emit(Alloc(tupleTemp)(coreListLength(temps) * 8)(false))
                    |> emitTupleFields(tupleTemp)(0)(temps)
                    |> success(tupleTemp)(SemTuple(semanticTypes))

let lowerTuple elements lower state =
    state
    |> lowerCoreValues(elements)(lower)
    |> finishTupleLowering

let allocateListCell headTemp tailTemp elementType state =
    match freshTemp(state) with
        | FreshTemp { state = allocatedState, temp = cellTemp } ->
            allocatedState
            |> emit(Alloc(cellTemp)(16)(false))
            |> emit(StoreMemOffset(cellTemp)(0)(headTemp))
            |> emit(StoreMemOffset(cellTemp)(8)(tailTemp))
            |> success(cellTemp)(elementType
            |> resolveType(allocatedState)
            |> SemList)

let finishCons head tail =
    match (head, tail) with
        | (LoweredCoreValue { state = failedState, error = Some(error) }, _tail) -> failure(failedState)(error)
        | (_head, LoweredCoreValue { state = failedState, error = Some(error) }) -> failure(failedState)(error)
        | (LoweredCoreValue { temp = headTemp, semanticType = headType }, LoweredCoreValue { state = tailState, temp = tailTemp, semanticType = tailType }) ->
            match bindType(SemList(headType))(tailType)(tailState) with
                | (failedState, Some(error)) -> failure(failedState)(error)
                | (typedState, None) -> allocateListCell(headTemp)(tailTemp)(headType)(typedState)

let finishConsTail lower tailExpression head =
    match head with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValue { state = headState } ->
            headState
            |> lower(tailExpression)
            |> finishCons(head)

let lowerCons head tail lower state =
    state
    |> lower(head)
    |> finishConsTail(lower)(tail)

let emptyList state =
    match freshType(state) with
        | FreshType { state = typedState, semanticType = elementType } ->
            lowerConstant(given (target) -> LoadConstInt(target)(0))(SemList(elementType))(typedState)

let recursive lowerListElements elements elementType tailTemp lower state =
    match elements with
        | [] ->
            success(tailTemp)(elementType
            |> resolveType(state)
            |> SemList)(state)
        | expression :: rest ->
            match lower(expression)(state) with
                | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                | LoweredCoreValue { state = valueState, temp = headTemp, semanticType = headType, error = None } ->
                    match bindType(elementType)(headType)(valueState) with
                        | (failedState, Some(error)) -> failure(failedState)(error)
                        | (typedState, None) ->
                            match allocateListCell(headTemp)(tailTemp)(elementType)(typedState) with
                                | LoweredCoreValue { state = cellState, temp = cellTemp, error = None } ->
                                    lowerListElements(
                                        rest,
                                        elementType,
                                        cellTemp,
                                        lower,
                                        cellState
                                    )
                                | failed -> failed

let finishListLiteral elements lower empty =
    match empty with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValue { state = emptyState, temp = emptyTemp, semanticType = SemList(elementType), error = None } ->
            lowerListElements(reverse(elements))(elementType)(emptyTemp)(lower)(emptyState)
        | LoweredCoreValue { state = failedState } ->
            failure(
                failedState,
                UnsupportedCoreLoweringExpression("invalid empty list type")
            )

let lowerListLiteral elements lower state =
    state
    |> emptyList
    |> finishListLiteral(elements)(lower)

let recursive emitAdtFields baseTemp index temps state =
    match temps with
        | [] -> state
        | temp :: rest ->
            state
            |> emit(SetAdtField(baseTemp)(index)(temp))
            |> emitAdtFields(baseTemp)(index + 1)(rest)

let finishConstructorAllocation layout resultType lowered =
    match (layout, lowered) with
        | (_layout, LoweredCoreValues { state = failedState, error = Some(error) }) -> failure(failedState)(error)
        | (CoreConstructorLayout { isZeroCost = true }, LoweredCoreValues { state = state, temps = temp :: [], error = None }) ->
            success(temp)(resolveType(state)(resultType))(state)
        | (CoreConstructorLayout { name = name, isZeroCost = true }, LoweredCoreValues { state = state, temps = temps, error = None }) ->
            temps
            |> coreListLength
            |> CoreConstructorArityMismatch(name)(1)
            |> failure(state)
        | (CoreConstructorLayout { tag = tag }, LoweredCoreValues { state = state, temps = temps, error = None }) ->
            match freshTemp(state) with
                | FreshTemp { state = allocatedState, temp = resultTemp } ->
                    allocatedState
                    |> emit(AllocAdt(resultTemp)(tag)(coreListLength(temps))(false))
                    |> emitAdtFields(resultTemp)(0)(temps)
                    |> success(resultTemp)(resolveType(allocatedState)(resultType))

let finishConstructorArguments arguments lower shape =
    match shape with
        | CoreConstructorShape { state = state, layout = layout, parameterTypes = parameterTypes, resultType = resultType } ->
            let expectedArity = coreListLength(parameterTypes)
            in
                let actualArity = coreListLength(arguments)
                in
                    if expectedArity != actualArity
                    then
                        match layout with
                            | CoreConstructorLayout { name = name } ->
                                actualArity
                                |> CoreConstructorArityMismatch(name)(expectedArity)
                                |> failure(state)
                    else
                        match lowerCoreValues(arguments)(lower)(state) with
                            | LoweredCoreValues { state = failedState, error = Some(error) } -> failure(failedState)(error)
                            | LoweredCoreValues { state = valuesState, semanticTypes = actualTypes, error = None } as lowered ->
                                match bindCoreValueTypes(parameterTypes)(actualTypes)(valuesState) with
                                    | (failedState, Some(error)) -> failure(failedState)(error)
                                    | (typedState, None) ->
                                        let typedValues = lowered with state = typedState
                                        in finishConstructorAllocation(layout)(resultType)(typedValues)

let lowerConstructor layout arguments lower state =
    state
    |> instantiateConstructor(layout)
    |> finishConstructorArguments(arguments)(lower)

let recursive resolveCoreTypes state semanticTypes =
    match semanticTypes with
        | [] -> []
        | semanticType :: rest -> resolveType(state)(semanticType) :: resolveCoreTypes(state)(rest)

let recursive emitCoreInstructions instructions state =
    match instructions with
        | [] -> state
        | instruction :: rest ->
            state
            |> emit(instruction)
            |> emitCoreInstructions(rest)

let finishBuiltinUnit resultType lower state =
    match constructorLayout("Unit")(state) with
        | None -> failure(state)(UnknownLoweringBinding("Unit"))
        | Some(layout) ->
            match lowerConstructor(layout)([])(lower)(state) with
                | LoweredCoreValue { state = unitState, temp = temp, error = None } ->
                    success(temp)(resolveType(unitState)(resultType))(unitState)
                | failed -> failed

let finishBuiltinResult resultType lower result emittedState =
    match result with
        | CoreBuiltinTemp(temp) ->
            success(temp)(resolveType(emittedState)(resultType))(emittedState)
        | CoreBuiltinNever(temp) -> success(temp)(SemNever)(emittedState)
        | CoreBuiltinUnit -> finishBuiltinUnit(resultType)(lower)(emittedState)

let finishBuiltinEmission resultType lower state emission =
    match emission with
        | CoreBuiltinEmission { error = Some(error) } -> failure(state)(UnsupportedCoreBuiltinLowering(error))
        | CoreBuiltinEmission { instructions = instructions, nextTemp = nextTemp, result = result, error = None } ->
            state
            |> withNextTemp(nextTemp)
            |> emitCoreInstructions(instructions)
            |> finishBuiltinResult(resultType)(lower)(result)

let emitBuiltin layout resultType lower lowered =
    match (layout, lowered) with
        | (_, LoweredCoreValues { state = failedState, error = Some(error) }) -> failure(failedState)(error)
        | (CoreBuiltinLayout { kind = kind }, LoweredCoreValues { state = state, temps = temps, semanticTypes = semanticTypes, error = None }) ->
            match state with
                | CoreLoweringState { nextTemp = nextTemp } ->
                    semanticTypes
                    |> resolveCoreTypes(state)
                    |> emitCoreBuiltin(kind)(nextTemp)(temps)
                    |> finishBuiltinEmission(resultType)(lower)(state)

let emitTypedBuiltin layout resultType lower (lowered: LoweredCoreValues) typedState =
    emitBuiltin(
        layout,
        resultType,
        lower,
        lowered with state = typedState
    )

let finishBuiltinArity arguments lower shape expectedArity actualArity =
    match shape with
        | CoreBuiltinShape { state = state, layout = layout, parameterTypes = parameterTypes, resultType = resultType } ->
            if actualArity != expectedArity
            then
                match layout with
                    | CoreBuiltinLayout { moduleName = moduleName, memberName = memberName } ->
                        actualArity
                        |> CoreBuiltinArityMismatch(
                            moduleName,
                            memberName,
                            expectedArity
                        )
                        |> failure(state)
            else
                match lowerCoreValues(arguments)(lower)(state) with
                    | LoweredCoreValues { state = failedState, error = Some(error) } -> failure(failedState)(error)
                    | LoweredCoreValues { state = valuesState, semanticTypes = actualTypes, error = None } as lowered ->
                        match bindCoreValueTypes(parameterTypes)(actualTypes)(valuesState) with
                            | (failedState, Some(error)) -> failure(failedState)(error)
                            | (typedState, None) -> emitTypedBuiltin(layout)(resultType)(lower)(lowered)(typedState)

let finishBuiltinArguments arguments lower shape =
    match shape with
        | CoreBuiltinShape { parameterTypes = parameterTypes } ->
            finishBuiltinArity(
                arguments,
                lower,
                shape,
                coreListLength(parameterTypes),
                coreListLength(arguments)
            )

let lowerBuiltin layout arguments lower state =
    state
    |> instantiateBuiltin(layout)
    |> finishBuiltinArguments(arguments)(lower)

let recursive collectCallSpine expression =
    match expression with
        | ExprAt(_span, inner) -> collectCallSpine(inner)
        | ExprCall(function, argument, _whitespace, _layout) ->
            match collectCallSpine(function) with
                | CoreCallSpine { root = root, arguments = arguments } ->
                    CoreCallSpine(
                        root = root,
                        arguments = append(arguments)([argument])
                    )
        | root -> CoreCallSpine(root = root, arguments = [])

let recursive constructorParameterNames name count index =
    if index >= count
    then []
    else
        "__core_ctor_" + name + "_" + Ashes.Text.fromInt(index) :: constructorParameterNames(
            name,
            count,
            index + 1
        )

let recursive applyConstructorParameters expression parameters =
    match parameters with
        | [] -> expression
        | parameter :: rest ->
            applyConstructorParameters(
                ExprCall(
                    expression,
                    ExprVar(parameter),
                    false,
                    callArgumentsInline
                ),
                rest
            )

let recursive wrapConstructorParameters parameters body =
    match parameters with
        | [] -> body
        | parameter :: rest ->
            ExprLambda(
                parameter,
                wrapConstructorParameters(rest)(body),
                None
            )

let constructorLambda layout =
    match layout with
        | CoreConstructorLayout { name = name } ->
            let parameters =
                constructorParameterNames(name)(constructorArity(layout))(0)
            in
                parameters
                |> applyConstructorParameters(ExprVar(name))
                |> wrapConstructorParameters(parameters)

let builtinLambda layout =
    match layout with
        | CoreBuiltinLayout { moduleName = moduleName, memberName = memberName } ->
            0
            |> constructorParameterNames("builtin_" + memberName)(builtinArity(layout))
            |> (given (parameters) ->
                parameters
                |> applyConstructorParameters(ExprQualifiedVar(moduleName)(memberName))
                |> wrapConstructorParameters(parameters))

let tryLowerConstructorCall expression lower state =
    match collectCallSpine(expression) with
        | CoreCallSpine { root = ExprVar(name), arguments = arguments } ->
            match constructorLayout(name)(state) with
                | None -> None
                | Some(layout) ->
                    if coreListLength(arguments) == constructorArity(layout)
                    then
                        state
                        |> lowerConstructor(layout)(arguments)(lower)
                        |> Some
                    else None
        | _ -> None

let tryLowerBuiltinCall expression lower state =
    match collectCallSpine(expression) with
        | CoreCallSpine { root = ExprQualifiedVar(moduleName, memberName), arguments = arguments } ->
            match builtinLayout(moduleName)(memberName)(state) with
                | None -> None
                | Some(layout) ->
                    if coreListLength(arguments) == builtinArity(layout)
                    then
                        state
                        |> lowerBuiltin(layout)(arguments)(lower)
                        |> Some
                    else None
        | _ -> None

let externalLayout name state =
    match state with
        | CoreLoweringState { externalLayouts = layouts } -> tryFindExternalLayout(name)(layouts)

let recursive buildExternalParameterNameList count index =
    if index >= count
    then []
    else "__ext_param_" + Ashes.Text.fromInt(index) :: buildExternalParameterNameList(count)(index + 1)

let recursive applyExternalParameters expression parameters =
    match parameters with
        | [] -> expression
        | parameter :: rest ->
            applyExternalParameters(
                ExprCall(
                    expression,
                    ExprVar(parameter),
                    false,
                    callArgumentsInline
                )
            )(rest)

let recursive wrapExternalParameters parameters body =
    match parameters with
        | [] -> body
        | parameter :: rest ->
            ExprLambda(
                parameter,
                wrapExternalParameters(rest)(body),
                None
            )

let externalWrapperLambda layout =
    match layout with
        | CoreExternalFunctionLayout { name = name, abi = abi } ->
            match abi with
                | ExternalFunctionAbi { parameters = parameters } ->
                    let inputCount = externalInputParameterCount(parameters)
                    in
                        if inputCount == 0
                        then
                            ExprLambda(
                                "__ext_unit",
                                ExprCall(ExprVar(name))(ExprVar("__ext_unit"))(false)(callArgumentsInline),
                                None
                            )
                        else
                            let paramNames = buildExternalParameterNameList(inputCount)(0)
                            in
                                paramNames
                                |> applyExternalParameters(ExprVar(name))
                                |> wrapExternalParameters(paramNames)

let finishCoreExternalReference layout lower state =
    match layout with
        | CoreExternalFunctionLayout { name = name, abi = abi } ->
            match abi with
                | ExternalFunctionAbi { directOnly = directOnly } ->
                    if directOnly
                    then failure(state)(CoreExternalDirectOnlyViolation(name))
                    else
                        lower(externalWrapperLambda(layout))(state)

let unwrapNullaryExternalArgs expectedCount arguments =
    if expectedCount == 0
    then
        match arguments with
            | ExprVar("Unit") :: [] -> []
            | other -> other
    else arguments

let recursive emitInstructions (instructions: List(IrInstructionKind)) state =
    match instructions with
        | [] -> state
        | inst :: rest ->
            state
            |> emit(inst)
            |> emitInstructions(rest)

let lowerExternalCallArguments abi arguments lower state =
    match lowerCoreValues(arguments)(lower)(state) with
        | LoweredCoreValues { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValues { state = valuesState, temps = argTemps, semanticTypes = argTypes, error = None } ->
            match valuesState with
                | CoreLoweringState { nextTemp = startTemp, nextLocal = startLocal } ->
                    match emitDirectExternalCall(abi)(startTemp)(startLocal)(argTemps)(argTypes) with
                        | CoreExternalLoweringEmission { error = Some(err) } -> failure(valuesState)(UnsupportedCoreExternalLowering(err))
                        | CoreExternalLoweringEmission { instructions = callInstrs, nextTemp = endTemp, nextLocal = endLocal, resultTemp = resTemp, resultType = resType, error = None } ->
                            let emittedState =
                                valuesState
                                |> emitInstructions(callInstrs)
                                |> withNextTemp(endTemp)
                                |> withNextLocal(endLocal)
                            in success(resTemp)(resType)(emittedState)

let tryLowerExternalCall expression lower state =
    match collectCallSpine(expression) with
        | CoreCallSpine { root = ExprVar(name), arguments = arguments } ->
            match externalLayout(name)(state) with
                | None -> None
                | Some(layout) ->
                    match layout with
                        | CoreExternalFunctionLayout { abi = abi } ->
                            match abi with
                                | ExternalFunctionAbi { parameters = parameters } ->
                                    let expectedCount = externalInputParameterCount(parameters)
                                    in
                                        let normalizedArgs = unwrapNullaryExternalArgs(expectedCount)(arguments)
                                        in
                                            if coreListLength(normalizedArgs) == expectedCount
                                            then
                                                state
                                                |> lowerExternalCallArguments(abi)(normalizedArgs)(lower)
                                                |> Some
                                            else None
        | CoreCallSpine { root = ExprQualifiedVar(_moduleName, name), arguments = arguments } ->
            match externalLayout(name)(state) with
                | None -> None
                | Some(layout) ->
                    match layout with
                        | CoreExternalFunctionLayout { abi = abi } ->
                            match abi with
                                | ExternalFunctionAbi { parameters = parameters } ->
                                    let expectedCount = externalInputParameterCount(parameters)
                                    in
                                        let normalizedArgs = unwrapNullaryExternalArgs(expectedCount)(arguments)
                                        in
                                            if coreListLength(normalizedArgs) == expectedCount
                                            then
                                                state
                                                |> lowerExternalCallArguments(abi)(normalizedArgs)(lower)
                                                |> Some
                                            else None
        | _ -> None

let recursive findNamedField (name: Str) (fields: List((Str, Expr))) =
    match fields with
        | [] -> None
        | (candidate, expression) :: rest ->
            if name == candidate
            then Some(expression)
            else findNamedField(name)(rest)

let recursive orderRecordArguments (typeName: Str) (fieldNames: List(Str)) (fields: List((Str, Expr))) reversed =
    match fieldNames with
        | [] -> CoreRecordArguments(expressions = reverse(reversed), error = None)
        | fieldName :: rest ->
            match findNamedField(fieldName)(fields) with
                | None ->
                    CoreRecordArguments(expressions = [], error = fieldName
                    |> UnknownCoreRecordField(typeName)
                    |> Some)
                | Some(expression) -> orderRecordArguments(typeName)(rest)(fields)(expression :: reversed)

let lowerRecord name fields lower state =
    match constructorLayout(name)(state) with
        | None -> failure(state)(UnknownLoweringBinding(name))
        | Some(CoreConstructorLayout { fieldNames = [] }) ->
            ""
            |> UnknownCoreRecordField(name)
            |> failure(state)
        | Some(CoreConstructorLayout { fieldNames = fieldNames } as layout) ->
            match orderRecordArguments(name)(fieldNames)(fields)([]) with
                | CoreRecordArguments { error = Some(error) } -> failure(state)(error)
                | CoreRecordArguments { expressions = expressions, error = None } ->
                    lowerConstructor(
                        layout,
                        expressions,
                        lower,
                        state
                    )

let emitRecordFieldLoad receiverTemp fieldType index fresh =
    match fresh with
        | FreshTemp { state = state, temp = fieldTemp } ->
            state
            |> emit(GetAdtField(fieldTemp)(receiverTemp)(index))
            |> success(fieldTemp)(resolveType(state)(fieldType))

let loadResolvedRecordField typeName fieldName receiverTemp fieldNames fieldTypes typed =
    match typed with
        | (failedState, Some(error)) -> failure(failedState)(error)
        | (typedState, None) ->
            match findPatternField(fieldName)(fieldNames)(fieldTypes)(0) with
                | None ->
                    fieldName
                    |> UnknownCoreRecordField(typeName)
                    |> failure(typedState)
                | Some(CorePatternField { index = index, semanticType = fieldType }) ->
                    typedState
                    |> freshTemp
                    |> emitRecordFieldLoad(receiverTemp)(fieldType)(index)

let finishRecordFieldShape typeName fieldName receiverTemp receiverType fieldNames shape =
    match shape with
        | CoreConstructorShape { state = state, parameterTypes = fieldTypes, resultType = resultType } ->
            state
            |> bindType(receiverType)(resultType)
            |> loadResolvedRecordField(typeName)(fieldName)(receiverTemp)(fieldNames)(fieldTypes)

let finishRecordFieldLayout typeName fieldName receiverTemp receiverType state layout =
    match layout with
        | None -> failure(state)(CoreRecordUpdateRequiresRecord(receiverType))
        | Some(CoreConstructorLayout { fieldNames = fieldNames } as constructor) ->
            state
            |> instantiateConstructor(constructor)
            |> finishRecordFieldShape(typeName)(fieldName)(receiverTemp)(receiverType)(fieldNames)

let finishRecordFieldAccess _receiverName fieldName lowered =
    match lowered with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValue { state = state, temp = receiverTemp, semanticType = receiverType, error = None } ->
            match resolveType(state)(receiverType) with
                | SemNamed(_symbolId, typeName, _arguments) ->
                    state
                    |> recordLayout(typeName)
                    |> finishRecordFieldLayout(typeName)(fieldName)(receiverTemp)(receiverType)(state)
                | other -> failure(state)(CoreRecordUpdateRequiresRecord(other))

let lowerRecordFieldAccess receiverName fieldName state =
    match state with
        | CoreLoweringState { bindings = bindings } ->
            match lookupBinding(receiverName)(bindings) with
                | None -> failure(state)(UnknownLoweringBinding(receiverName + "." + fieldName))
                | Some(binding) ->
                    state
                    |> lowerBoundVariable(binding)
                    |> finishRecordFieldAccess(receiverName)(fieldName)

let finishUpdatedRecordField expectedType reversedTemps reversedTypes lowered =
    match lowered with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failedCoreValues(failedState)(error)
        | LoweredCoreValue { state = valueState, temp = temp, semanticType = semanticType, error = None } ->
            match bindType(expectedType)(semanticType)(valueState) with
                | (failedState, Some(error)) -> failedCoreValues(failedState)(error)
                | (typedState, None) ->
                    LoweredCoreValues(
                        state = typedState,
                        temps = temp :: reversedTemps,
                        semanticTypes = expectedType :: reversedTypes,
                        error = None
                    )

let loadUnchangedRecordField targetTemp index fieldType state reversedTemps reversedTypes =
    match freshTemp(state) with
        | FreshTemp { state = loadState, temp = temp } ->
            LoweredCoreValues(
                state = emit(GetAdtField(temp)(targetTemp)(index))(loadState),
                temps = temp :: reversedTemps,
                semanticTypes = fieldType :: reversedTypes,
                error = None
            )

let recursive lowerRecordUpdateFields fieldNames fieldTypes updates targetTemp index lower reversedTemps reversedTypes state =
    match (fieldNames, fieldTypes) with
        | ([], []) -> finishCoreValues(state)(reversedTemps)(reversedTypes)
        | (fieldName :: fieldRest, fieldType :: typeRest) ->
            let loweredField =
                match findNamedField(fieldName)(updates) with
                    | None ->
                        loadUnchangedRecordField(
                            targetTemp,
                            index,
                            fieldType,
                            state,
                            reversedTemps,
                            reversedTypes
                        )
                    | Some(expression) ->
                        state
                        |> lower(expression)
                        |> finishUpdatedRecordField(fieldType)(reversedTemps)(reversedTypes)
            in
                match loweredField with
                    | LoweredCoreValues { state = failedState, error = Some(error) } -> failedCoreValues(failedState)(error)
                    | LoweredCoreValues { state = nextState, temps = nextTemps, semanticTypes = nextTypes, error = None } ->
                        lowerRecordUpdateFields(
                            fieldRest,
                            typeRest,
                            updates,
                            targetTemp,
                            index + 1,
                            lower,
                            nextTemps,
                            nextTypes,
                            nextState
                        )
        | _ -> failedCoreValues(state)(UnsupportedCoreLoweringExpression("record layout arity"))

let lowerTypedRecordUpdate layout resultType fieldNames fieldTypes fields targetTemp lower typed =
    match typed with
        | (failedState, Some(error)) -> failure(failedState)(error)
        | (typedState, None) ->
            typedState
            |> lowerRecordUpdateFields(fieldNames)(fieldTypes)(fields)(targetTemp)(0)(lower)([])([])
            |> finishConstructorAllocation(layout)(resultType)

let finishRecordUpdateShape layout fieldNames fields targetTemp targetType lower shape =
    match shape with
        | CoreConstructorShape { state = state, parameterTypes = fieldTypes, resultType = resultType } ->
            state
            |> bindType(targetType)(resultType)
            |> lowerTypedRecordUpdate(layout)(resultType)(fieldNames)(fieldTypes)(fields)(targetTemp)(lower)

let finishRecordUpdateLayout fields targetTemp targetType lower state layout =
    match layout with
        | None -> failure(state)(CoreRecordUpdateRequiresRecord(targetType))
        | Some(CoreConstructorLayout { fieldNames = fieldNames } as constructor) ->
            state
            |> instantiateConstructor(constructor)
            |> finishRecordUpdateShape(constructor)(fieldNames)(fields)(targetTemp)(targetType)(lower)

let finishRecordUpdate fields lower loweredTarget =
    match loweredTarget with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValue { state = state, temp = targetTemp, semanticType = targetType, error = None } ->
            match resolveType(state)(targetType) with
                | SemNamed(_symbolId, typeName, _arguments) ->
                    state
                    |> recordLayout(typeName)
                    |> finishRecordUpdateLayout(fields)(targetTemp)(targetType)(lower)(state)
                | other -> failure(state)(CoreRecordUpdateRequiresRecord(other))

let lowerRecordUpdate target fields lower state =
    state
    |> lower(target)
    |> finishRecordUpdate(fields)(lower)

let failedCoreBinary state error =
    LoweredCoreBinary(
        state = state,
        leftTemp = -1,
        leftType = SemNever,
        rightTemp = -1,
        rightType = SemNever,
        error = Some(error)
    )

let finishCoreBinary leftTemp leftType loweredRight =
    match loweredRight with
        | LoweredCoreValue { state = state, error = Some(error) } -> failedCoreBinary(state)(error)
        | LoweredCoreValue { state = state, temp = rightTemp, semanticType = rightType, error = None } ->
            LoweredCoreBinary(
                state = state,
                leftTemp = leftTemp,
                leftType = leftType,
                rightTemp = rightTemp,
                rightType = rightType,
                error = None
            )

let lowerCoreBinaryRight right lower loweredLeft =
    match loweredLeft with
        | LoweredCoreValue { state = state, error = Some(error) } -> failedCoreBinary(state)(error)
        | LoweredCoreValue { state = state, temp = leftTemp, semanticType = leftType, error = None } ->
            state
            |> lower(right)
            |> finishCoreBinary(leftTemp)(leftType)

let lowerCoreBinaryOperands left right lower state =
    state
    |> lower(left)
    |> lowerCoreBinaryRight(right)(lower)

let numericDefault semanticType =
    match semanticType with
        | SemFloat -> SemFloat
        | SemRune -> SemRune
        | SemBigInt -> SemBigInt
        | SemUInt(bits) -> SemUInt(bits)
        | _ -> SemInt

let finishCoreBinaryBinding (binary: LoweredCoreBinary) result =
    match result with
        | (failedState, Some(error)) -> failedCoreBinary(failedState)(error)
        | (typedState, None) -> binary with state = typedState

let bindCoreBinaryLeftType resolvedRight binary resolvedLeft =
    match binary with
        | LoweredCoreBinary { state = state, leftType = leftType } ->
            match resolvedLeft with
                | SemVariable(_id) ->
                    state
                    |> bindType(leftType)(numericDefault(resolvedRight))
                    |> finishCoreBinaryBinding(binary)
                | _ -> binary

let bindCoreBinaryLeft resolvedRight binary =
    match binary with
        | LoweredCoreBinary { state = state, leftType = leftType, error = None } ->
            leftType
            |> resolveType(state)
            |> bindCoreBinaryLeftType(resolvedRight)(binary)
        | failed -> failed

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

let emitCoreConcat binary =
    emitCoreBinaryTarget(
        given (target) ->
            given (left) ->
                given (right) -> ConcatStr(target)(left)(right)(false)
    )(SemString)(binary)

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

let emitResolvedCoreOrdered name intKind uintKind floatKind binary =
    match binary with
        | LoweredCoreBinary { leftType = SemInt, rightType = SemInt } -> emitCoreBinaryTarget(intKind)(SemBool)(binary)
        | LoweredCoreBinary { leftType = SemRune, rightType = SemRune } -> emitCoreBoolBinary(intKind)(binary)
        | LoweredCoreBinary { leftType = SemUInt(leftBits), rightType = SemUInt(rightBits) } ->
            if leftBits == rightBits
            then emitCoreBoolBinary(uintKind)(binary)
            else operatorMismatch(name)(binary)
        | LoweredCoreBinary { leftType = SemFloat, rightType = SemFloat } -> emitCoreBoolBinary(floatKind)(binary)
        | LoweredCoreBinary { leftType = SemBigInt, rightType = SemBigInt } -> emitCoreBigIntComparison(intKind)(binary)
        | _ -> operatorMismatch(name)(binary)

let emitResolvedCoreEquality name intKind floatKind stringKind binary =
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
        | LoweredCoreBinary { leftType = SemString, rightType = SemString } -> emitCoreBoolBinary(stringKind)(binary)
        | _ -> operatorMismatch(name)(binary)

let emitResolvedCoreBinary operator binary =
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
        | CoreGreaterOperator -> emitResolvedCoreOrdered(">")(CmpIntGt)(CmpUIntGt)(CmpFloatGt)(binary)
        | CoreGreaterOrEqualOperator -> emitResolvedCoreOrdered(">=")(CmpIntGe)(CmpUIntGe)(CmpFloatGe)(binary)
        | CoreLessOperator -> emitResolvedCoreOrdered("<")(CmpIntLt)(CmpUIntLt)(CmpFloatLt)(binary)
        | CoreLessOrEqualOperator -> emitResolvedCoreOrdered("<=")(CmpIntLe)(CmpUIntLe)(CmpFloatLe)(binary)
        | CoreEqualOperator -> emitResolvedCoreEquality("==")(CmpIntEq)(CmpFloatEq)(CmpStrEq)(binary)
        | CoreNotEqualOperator -> emitResolvedCoreEquality("!=")(CmpIntNe)(CmpFloatNe)(CmpStrNe)(binary)

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
        | ExprInt(0) -> true
        | _ -> false

let prepareCoreBinary operator left binary =
    match operator with
        | CoreSubtractOperator ->
            if isCoreZeroExpression(left)
            then coerceCoreNegationZero(binary)
            else binary
        | _ -> binary

let lowerCoreBinary operator left right lower state =
    state
    |> lowerCoreBinaryOperands(left)(right)(lower)
    |> prepareCoreBinary(operator)(left)
    |> resolvedCoreBinary
    |> emitResolvedCoreBinary(operator)

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

let lowerCoreLogicalNot operand lower state =
    state
    |> lower(operand)
    |> finishCoreLogicalNot

let lowerCoreBitwiseNot operand lower state =
    state
    |> lower(operand)
    |> finishCoreBitwiseNot

let recursive parseDecimalDigits remaining value =
    match Ashes.Text.unconsText(remaining) with
        | None -> value
        | Some((digit, rest)) -> parseDecimalDigits(rest)(value * 10 + Ashes.Text.firstByteOf(digit) - 48)

let bigIntFitsLength digits length =
    if length < 19
    then true
    else
        if length == 19
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

let bigIntFirstLength length =
    match length % 18 with
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

let finishCoreConstructorReference layout lower state =
    if constructorArity(layout) == 0
    then lowerConstructor(layout)([])(lower)(state)
    else
        lower(constructorLambda(layout))(state)

let finishCoreBuiltinReference layout lower state =
    if builtinArity(layout) == 0
    then lowerBuiltin(layout)([])(lower)(state)
    else
        lower(builtinLambda(layout))(state)

let lowerCoreVariable name lower state =
    match state with
        | CoreLoweringState { bindings = bindings, externalLayouts = externalLayouts } ->
            match lookupBinding(name)(bindings) with
                | Some(binding) -> lowerBoundVariable(binding)(state)
                | None ->
                    match constructorLayout(name)(state) with
                        | Some(layout) -> finishCoreConstructorReference(layout)(lower)(state)
                        | None ->
                            match tryFindExternalLayout(name)(externalLayouts) with
                                | Some(extLayout) -> finishCoreExternalReference(extLayout)(lower)(state)
                                | None -> failure(state)(UnknownLoweringBinding(name))

let lowerCoreQualifiedVariable moduleName memberName lower state =
    match builtinLayout(moduleName)(memberName)(state) with
        | Some(layout) -> finishCoreBuiltinReference(layout)(lower)(state)
        | None ->
            match state with
                | CoreLoweringState { externalLayouts = externalLayouts } ->
                    match tryFindExternalLayout(memberName)(externalLayouts) with
                        | Some(extLayout) -> finishCoreExternalReference(extLayout)(lower)(state)
                        | None -> lowerRecordFieldAccess(moduleName)(memberName)(state)

let lowerQualified moduleName memberName lower state = lowerCoreQualifiedVariable(moduleName)(memberName)(lower)(state)

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

let lowerPerform operation lower state =
    match collectCallSpine(operation) with
        | CoreCallSpine { root = ExprQualifiedVar(capName, opName), arguments = arguments } ->
            if capName == "Stop"
            then
                if opName == "stop"
                then
                    match arguments with
                        | arg :: [] ->
                            match lower(arg)(state) with
                                | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                                | LoweredCoreValue { state = argState, temp = argTemp, error = None } ->
                                    argState
                                    |> emit(RequestServerStop(argTemp))
                                    |> success(argTemp)(SemNamed(0)("Unit")([]))
                        | _ ->
                            arguments
                            |> coreListLength
                            |> CoreBuiltinArityMismatch("Stop")("stop")(1)
                            |> failure(state)
                else failure(state)(UnknownLoweringBinding("Stop." + opName))
            else
                match state with
                    | CoreLoweringState { capabilityLayouts = capLayouts, staticProviders = providers, capabilityGlobalCount = globalCount } ->
                        match findStaticProvider(capName)(providers) with
                            | Some(provider) ->
                                match findProviderOperation(opName)(provider.operations) with
                                    | Some(implExpr) ->
                                        match lower(implExpr)(state) with
                                            | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                                            | LoweredCoreValue { state = implState, temp = implTemp, error = None } ->
                                                match lowerCoreValues(arguments)(lower)(implState) with
                                                    | LoweredCoreValues { state = argFailedState, error = Some(error) } -> failure(argFailedState)(error)
                                                    | LoweredCoreValues { state = valuesState, temps = argTemps, error = None } ->
                                                        match valuesState with
                                                            | CoreLoweringState { nextTemp = startTemp, nextLocal = startLocal } ->
                                                                match []
                                                                |> SemNamed(0)("Unit")
                                                                |> emitStaticProviderCall(provider)(opName)(implTemp)(startTemp)(startLocal)(argTemps) with
                                                                    | CoreCapabilityPerformEmission { instructions = callInstrs, nextTemp = endTemp, nextLocal = endLocal, resultTemp = resTemp, semanticType = resType, error = None } ->
                                                                        let emittedState =
                                                                            valuesState
                                                                            |> emitInstructions(callInstrs)
                                                                            |> withNextTemp(endTemp)
                                                                            |> withNextLocal(endLocal)
                                                                        in success(resTemp)(resType)(emittedState)
                                                                    | _ ->
                                                                        opName
                                                                        |> CoreUnhandledCapabilityOperation(capName)
                                                                        |> failure(valuesState)
                                    | None ->
                                        opName
                                        |> CoreUnhandledCapabilityOperation(capName)
                                        |> failure(state)
                            | None ->
                                match findCapabilityLayout(capName)(capLayouts) with
                                    | Some(layout) ->
                                        match findCapabilityOperationIndex(opName)(layout.operations) with
                                            | Some(opIndex) ->
                                                match lowerCoreValues(arguments)(lower)(state) with
                                                    | LoweredCoreValues { state = argFailedState, error = Some(error) } -> failure(argFailedState)(error)
                                                    | LoweredCoreValues { state = valuesState, temps = argTemps, error = None } ->
                                                        match valuesState with
                                                            | CoreLoweringState { nextTemp = startTemp, nextLocal = startLocal } ->
                                                                let resType = SemNamed(0)("Unit")([])
                                                                in
                                                                    match emitDynamicPerform(capName)(opName)(layout.index)(opIndex)(globalCount)(startTemp)(startLocal)(argTemps)(resType) with
                                                                        | CoreCapabilityPerformEmission { instructions = performInstrs, nextTemp = endTemp, nextLocal = endLocal, resultTemp = resTemp, semanticType = resultSemType, error = None } ->
                                                                            let emittedState =
                                                                                valuesState
                                                                                |> emitInstructions(performInstrs)
                                                                                |> withNextTemp(endTemp)
                                                                                |> withNextLocal(endLocal)
                                                                            in success(resTemp)(resultSemType)(emittedState)
                                                                        | _ ->
                                                                            opName
                                                                            |> CoreUnhandledCapabilityOperation(capName)
                                                                            |> failure(valuesState)
                                            | None ->
                                                opName
                                                |> CoreUnhandledCapabilityOperation(capName)
                                                |> failure(state)
                                    | None ->
                                        opName
                                        |> CoreUnhandledCapabilityOperation(capName)
                                        |> failure(state)
        | _ -> lower(operation)(state)

let lowerHandle body arms lower state =
    match splitHandlerArms(arms) with
        | ParsedHandlerArms { opArms = opArms, returnArm = returnArm } ->
            match state with
                | CoreLoweringState { capabilityLayouts = capLayouts, capabilityGlobalCount = globalCount } ->
                    match freshTemp(state) with
                        | FreshTemp { state = stackState, temp = postsHeadPtrTemp } ->
                            let initPostsState =
                                emit(AllocStack(postsHeadPtrTemp)(8))(stackState)
                            in
                                match freshTemp(initPostsState) with
                                    | FreshTemp { state = zeroState, temp = zeroTemp } ->
                                        let prepState =
                                            zeroState
                                            |> emit(LoadConstInt(zeroTemp)(0))
                                            |> emit(StoreMemOffset(postsHeadPtrTemp)(0)(zeroTemp))
                                        in
                                            match opArms with
                                                | [] -> lower(body)(prepState)
                                                | (capName, _opName, _pats, _armBody) :: _ ->
                                                    match findCapabilityLayout(capName)(capLayouts) with
                                                        | None -> lower(body)(prepState)
                                                        | Some(CoreCapabilityLayout { index = capIdx, operations = ops }) ->
                                                            let opCount = coreListLength(ops)
                                                            in
                                                                match freshTemp(prepState) with
                                                                    | FreshTemp { state = frameAllocState, temp = frameTemp } ->
                                                                        let frameSize = (globalCount + 1 + opCount) * 8
                                                                        in
                                                                            let frameInitState =
                                                                                frameAllocState
                                                                                |> emit(AllocStack(frameTemp)(frameSize))
                                                                                |> emitSnapshotGlobals(0)(globalCount)(frameTemp)
                                                                                |> emit(StoreMemOffset(frameTemp)(globalCount * 8)(postsHeadPtrTemp))
                                                                                |> emit(StoreCapabilityHandler(capIdx)(frameTemp))
                                                                            in
                                                                                match lower(body)(frameInitState) with
                                                                                    | LoweredCoreValue { state = bodyState, temp = bodyTemp, semanticType = bodyType, error = None } ->
                                                                                        let uninstallState =
                                                                                            match freshTemp(bodyState) with
                                                                                                | FreshTemp { state = unState, temp = prevTemp } ->
                                                                                                    unState
                                                                                                    |> emit(LoadMemOffset(prevTemp)(frameTemp)(capIdx * 8))
                                                                                                    |> emit(StoreCapabilityHandler(capIdx)(prevTemp))
                                                                                        in
                                                                                            match returnArm with
                                                                                                | None -> success(bodyTemp)(bodyType)(uninstallState)
                                                                                                | Some((returnPat, returnExpr)) ->
                                                                                                    lower(ExprMatch(ExprVar("__body_res"))([(returnPat, returnExpr, None)])(None))(uninstallState)
                                                                                    | failed -> failed

let expressionName expression =
    match expression with
        | ExprBigInt(_) -> "BigInt"
        | ExprQualifiedVar(_, _) -> "qualified variable"
        | _ -> "non-core expression"

let recursive lowerCore expression state =
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
        | ExprGreaterThan(left, right) -> lowerCoreBinary(CoreGreaterOperator)(left)(right)(lowerCore)(state)
        | ExprGreaterOrEqual(left, right) -> lowerCoreBinary(CoreGreaterOrEqualOperator)(left)(right)(lowerCore)(state)
        | ExprLessThan(left, right) -> lowerCoreBinary(CoreLessOperator)(left)(right)(lowerCore)(state)
        | ExprLessOrEqual(left, right) -> lowerCoreBinary(CoreLessOrEqualOperator)(left)(right)(lowerCore)(state)
        | ExprEqual(left, right) -> lowerCoreBinary(CoreEqualOperator)(left)(right)(lowerCore)(state)
        | ExprNotEqual(left, right) -> lowerCoreBinary(CoreNotEqualOperator)(left)(right)(lowerCore)(state)
        | ExprVar(name) -> lowerCoreVariable(name)(lowerCore)(state)
        | ExprQualifiedVar(receiverName, fieldName) -> lowerQualified(receiverName)(fieldName)(lowerCore)(state)
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
        | ExprCall(function, argument, _whitespace, _layout) ->
            match tryLowerConstructorCall(expression)(lowerCore)(state) with
                | Some(lowered) -> lowered
                | None ->
                    match tryLowerBuiltinCall(expression)(lowerCore)(state) with
                        | Some(lowered) -> lowered
                        | None ->
                            match tryLowerExternalCall(expression)(lowerCore)(state) with
                                | Some(lowered) -> lowered
                                | None -> lowerCall(function)(argument)(lowerCore)(state)
        | ExprTuple(elements) -> lowerTuple(elements)(lowerCore)(state)
        | ExprList(elements, _isMultiline) -> lowerListLiteral(elements)(lowerCore)(state)
        | ExprCons(head, tail) -> lowerCons(head)(tail)(lowerCore)(state)
        | ExprRecord(name, fields, _isMultiline) -> lowerRecord(name)(fields)(lowerCore)(state)
        | ExprRecordUpdate(target, fields) -> lowerRecordUpdate(target)(fields)(lowerCore)(state)
        | ExprMatch(value, cases, _position) -> lowerMatch(value)(cases)(lowerCore)(state)
        | ExprPerform(operation) -> lowerPerform(operation)(lowerCore)(state)
        | ExprHandle(body, arms) -> lowerHandle(body)(arms)(lowerCore)(state)
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

type CoreProgramUses =
    | printInt: Bool
    | printStr: Bool
    | printBool: Bool
    | concatStr: Bool

let emptyCoreProgramUses =
    CoreProgramUses(
        printInt = false,
        printStr = false,
        printBool = false,
        concatStr = false
    )

let includeCoreInstruction operation (uses: CoreProgramUses) =
    match operation with
        | PrintInt(_) -> uses with printInt = true
        | PrintStr(_) -> uses with printStr = true
        | PrintBool(_) -> uses with printBool = true
        | ConcatStr(_, _, _, _) -> uses with concatStr = true
        | ConcatStrTip(_, _, _, _, _, _) -> uses with concatStr = true
        | _ -> uses

let recursive collectCoreInstructionUses instructions uses =
    match instructions with
        | [] -> uses
        | IrInstruction { instruction = operation } :: rest ->
            uses
            |> includeCoreInstruction(operation)
            |> collectCoreInstructionUses(rest)

let recursive collectCoreFunctionUses functions uses =
    match functions with
        | [] -> uses
        | IrFunction { instructions = instructions } :: rest ->
            uses
            |> collectCoreInstructionUses(instructions)
            |> collectCoreFunctionUses(rest)

type TopLevelDuplicateCheck =
    | seen: List(Str)
    | duplicate: Maybe(Str)

let recursive checkTopLevelNames names seen =
    match names with
        | [] -> TopLevelDuplicateCheck(seen = seen, duplicate = None)
        | name :: rest ->
            if containsName(name)(seen)
            then TopLevelDuplicateCheck(seen = seen, duplicate = Some(name))
            else checkTopLevelNames(rest)(name :: seen)

let recursive letBindingSyntaxPairs bindings =
    match bindings with
        | [] -> []
        | LetBindingSyntax { name = name, value = value } :: rest -> (name, value) :: letBindingSyntaxPairs(rest)

let recursive letBindingSyntaxNames bindings =
    match bindings with
        | [] -> []
        | LetBindingSyntax { name = name } :: rest -> name :: letBindingSyntaxNames(rest)

// Handed to finishLetValue/lowerPreparedRecursiveGroupWith wherever the real "body" is supplied by
// the continuation lower instead (the rest of the top-level items, not a literal expression) — the
// continuation lower ignores it. A bare int literal needs no environment/binding resolution, so it
// stays inert even if a future change accidentally lowers it before the continuation intercepts it.
let topLevelContinuationBody = ExprInt(0)

// Lowers a whole program's top-level items one at a time, threading lowering state through them,
// rather than desugaring into one big nested-let expression up front: a top-level
// `let recursive ... and ...` group has no expression-level representation (the language only
// allows `and` groups as top-level declarations, never nested inside another expression), so its
// members must go through lowerPreparedRecursiveGroupWith's own member/continuation split, with
// "the rest of the program" supplied as the continuation lower rather than as a literal Expr — the
// continuation lower ignores the placeholder body it's handed and lowers the remaining items
// instead. Type, external, capability, provider, trait, and implementation declarations are
// registered ahead of lowering by inference and are not part of the value chain, so they are
// skipped here rather than lowered.
let recursive lowerCoreProgramItems items trailingBody seen state =
    match items with
        | [] -> lowerCore(trailingBody)(state)
        | TopLevelAt(_span, inner) :: rest -> lowerCoreProgramItems(inner :: rest)(trailingBody)(seen)(state)
        | TopLevelLet(LetBindingSyntax { name = name, value = value }, false) :: rest ->
            match checkTopLevelNames([name])(seen) with
                | TopLevelDuplicateCheck { duplicate = Some(duplicateName) } -> failure(state)(DuplicateTopLevelBinding(duplicateName))
                | TopLevelDuplicateCheck { seen = nextSeen, duplicate = None } ->
                    match state with
                        | CoreLoweringState { bindings = outerBindings } ->
                            state
                            |> lowerCore(value)
                            |> finishLetValue(
                                name,
                                topLevelContinuationBody,
                                given (_ignoredBody) ->
                                    given (s) -> lowerCoreProgramItems(rest)(trailingBody)(nextSeen)(s),
                                outerBindings
                            )
        | TopLevelLet(LetBindingSyntax { name = name, value = value }, true) :: rest ->
            match checkTopLevelNames([name])(seen) with
                | TopLevelDuplicateCheck { duplicate = Some(duplicateName) } -> failure(state)(DuplicateTopLevelBinding(duplicateName))
                | TopLevelDuplicateCheck { seen = nextSeen, duplicate = None } ->
                    match state with
                        | CoreLoweringState { bindings = outerBindings, nextLambdaId = lambdaId } ->
                            []
                            |> prepareRecursiveGroup([(name, value)])(state)
                            |> relabelSingleRecursive(lambdaId)
                            |> lowerPreparedRecursiveGroupWith(
                                [(name, value)],
                                topLevelContinuationBody,
                                lowerCore,
                                given (_ignoredBody) ->
                                    given (s) -> lowerCoreProgramItems(rest)(trailingBody)(nextSeen)(s),
                                outerBindings
                            )
        | TopLevelRecursiveGroup(bindings) :: rest ->
            match checkTopLevelNames(letBindingSyntaxNames(bindings))(seen) with
                | TopLevelDuplicateCheck { duplicate = Some(duplicateName) } -> failure(state)(DuplicateTopLevelBinding(duplicateName))
                | TopLevelDuplicateCheck { seen = nextSeen, duplicate = None } ->
                    match state with
                        | CoreLoweringState { bindings = outerBindings } ->
                            let pairs = letBindingSyntaxPairs(bindings)
                            in
                                []
                                |> prepareRecursiveGroup(pairs)(state)
                                |> lowerPreparedRecursiveGroupWith(
                                    pairs,
                                    topLevelContinuationBody,
                                    lowerCore,
                                    given (_ignoredBody) ->
                                        given (s) -> lowerCoreProgramItems(rest)(trailingBody)(nextSeen)(s),
                                    outerBindings
                                )
        | _ :: rest -> lowerCoreProgramItems(rest)(trailingBody)(seen)(state)

let buildProgram lowered =
    match lowered with
        | LoweredCoreValue { error = Some(error) } -> failedCoreLowering(error)
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            match state with
                | CoreLoweringState { reversedInstructions = instructions, functions = functions, externalFunctions = externalFunctions, externalOpaqueTypes = externalOpaqueTypes, nextLocal = localCount, nextTemp = tempCount, stringLiterals = stringLiterals } ->
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
                        match collectCoreFunctionUses(
                            functions
                        )(
                            collectCoreInstructionUses(instructions)(emptyCoreProgramUses)
                        ) with
                            | CoreProgramUses { printInt = usesPrintInt, printStr = usesPrintStr, printBool = usesPrintBool, concatStr = usesConcatStr } ->
                                CoreLoweringResult(
                                    program = Some(IrProgram(
                                        entryFunction = entry,
                                        functions = functions,
                                        stringLiterals = stringLiterals,
                                        externalFunctions = externalFunctions,
                                        externalOpaqueTypes = externalOpaqueTypes,
                                        usesPrintInt = usesPrintInt,
                                        usesPrintStr = usesPrintStr,
                                        usesPrintBool = usesPrintBool,
                                        usesConcatStr = usesConcatStr,
                                        usesClosures = hasFunctions(functions),
                                        usesAsync = false,
                                        capabilityHandlerGlobals = 0,
                                        traitEvidence = emptyTraitEvidenceAnnotations
                                    )),
                                    semanticType = resolveType(state)(semanticType),
                                    error = None
                                )

let lowerCoreProgram (program: ProgramSyntax) =
    match program with
        | ProgramSyntax { items = items, body = body } ->
            let trailingBody =
                match body with
                    | Some(expression) -> expression
                    | None -> ExprVar("Unit")
            in
                Unit
                |> initialState
                |> lowerCoreProgramItems(items)(trailingBody)([])
                |> buildProgram

// As lowerCoreProgram, but tags every emitted instruction with its source location — a plain,
// non-stitched single-file context, unlike lowerCoreExpressionLocated's stitched-project one.
let lowerCoreProgramWithSource (filePath: Str) (source: Str) (program: ProgramSyntax) =
    match program with
        | ProgramSyntax { items = items, body = body } ->
            let trailingBody =
                match body with
                    | Some(expression) -> expression
                    | None -> ExprVar("Unit")
            in
                Unit
                |> initialState
                |> (given (state: CoreLoweringState) ->
                    state with sourceContext = Some(createSourceContext(filePath)(source)))
                |> lowerCoreProgramItems(items)(trailingBody)([])
                |> buildProgram

let lowerCoreExpression expression =
    Unit
    |> initialState
    |> lowerCore(expression)
    |> buildProgram

let lowerCoreExpressionWithLayouts layouts expression =
    Unit
    |> initialStateWithLayouts(layouts)
    |> lowerCore(expression)
    |> buildProgram

let lowerCoreExpressionWithContext constructorLayouts builtinLayouts expression =
    Unit
    |> initialStateWithContext(constructorLayouts)(builtinLayouts)
    |> lowerCore(expression)
    |> buildProgram

let lowerCoreExpressionWithFullContext constructorLayouts builtinLayouts externalLayouts externalFunctions externalOpaqueTypes expression =
    Unit
    |> initialStateWithFullContext(constructorLayouts)(builtinLayouts)(externalLayouts)(externalFunctions)(externalOpaqueTypes)
    |> lowerCore(expression)
    |> buildProgram

let lowerCoreExpressionWithCompleteContext constructorLayouts builtinLayouts externalLayouts externalFunctions externalOpaqueTypes capabilityLayouts staticProviders capabilityGlobalCount expression =
    Unit
    |> initialStateWithCompleteContext(constructorLayouts)(builtinLayouts)(externalLayouts)(externalFunctions)(externalOpaqueTypes)(capabilityLayouts)(staticProviders)(capabilityGlobalCount)
    |> lowerCore(expression)
    |> buildProgram

// Lowers an expression that is (part of) the combined item at itemIndex of a stitched project,
// locating every emitted instruction through the context's item regions.
let lowerCoreExpressionLocated (context: SourceContext) (itemIndex: Int) expression =
    Unit
    |> initialState
    |> (given (state: CoreLoweringState) -> state with sourceContext = Some(context), currentItem = itemIndex)
    |> lowerCore(expression)
    |> buildProgram

let lowerCoreRecursiveGroup bindings body =
    Unit
    |> initialState
    |> lowerRecursiveGroup(bindings)(body)(lowerCore)
    |> buildProgram
