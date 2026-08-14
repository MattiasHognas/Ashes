import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeSchemes
import AshesCompiler.Semantics.TypeResolution
import AshesCompiler.Semantics.TypeInference
import Ashes.Collection.List.reverse
export (
    type ProgramInferenceError(..),
    type ProgramInferenceResult(..),
    value inferProgram,
)

type ProgramInferenceError =
    | ProgramExpressionError(TypeInferenceError)
    | ProgramTypeResolutionError(TypeResolutionError)
    | UnsupportedTopLevelDeclaration(Str)
    deriving {Eq, Show}

type ProgramInferenceResult =
    | semanticType: SemanticType
    | substitution: List((Int, SemanticType))
    | environment: TypeEnvironment
    | error: Maybe(ProgramInferenceError)

type ProgramInferenceState =
    | environment: TypeEnvironment
    | substitution: List((Int, SemanticType))
    | supply: TypeVariableSupply
    | nextTypeSymbolId: Int
    | error: Maybe(ProgramInferenceError)

type TypeParameterRegistration =
    | context: TypeResolutionContext
    | semanticTypes: List(SemanticType)
    | quantified: List((Int, Str))
    | supply: TypeVariableSupply

type ConstructorRegistration =
    | environment: TypeEnvironment
    | error: Maybe(TypeResolutionError)

let recursive wrapSugarParameters parameters body =
    match parameters with
        | [] -> body
        | head :: tail -> ExprLambda(head)(wrapSugarParameters(tail)(body))(None)

let recursive parameterCount parameters =
    match parameters with
        | [] -> 0
        | _head :: tail -> 1 + parameterCount(tail)

let recursive registerTypeParameters parameters context supply reversedTypes reversedQuantified =
    match parameters with
        | [] -> TypeParameterRegistration(context = context, semanticTypes = reverse(reversedTypes), quantified = reverse(reversedQuantified), supply = supply)
        | TypeParameter { name = name } :: tail ->
            match freshTypeVariable(supply) with
                | (SemVariable(variableId), nextSupply) -> registerTypeParameters(tail)(addTypeParameter(name)(SemVariable(variableId))(context))(nextSupply)(SemVariable(variableId) :: reversedTypes)((variableId, name) :: reversedQuantified)
                | (_unexpected, nextSupply) -> registerTypeParameters(tail)(context)(nextSupply)(reversedTypes)(reversedQuantified)

let recursive resolveConstructorParameters parameters context reversed =
    match parameters with
        | [] -> TypeListResolutionResult(semanticTypes = reverse(reversed), error = None)
        | head :: tail ->
            match resolveTypeExpression(head)(context) with
                | TypeResolutionResult { semanticType = semanticType, error = None } -> resolveConstructorParameters(tail)(context)(semanticType :: reversed)
                | TypeResolutionResult { semanticType = _semanticType, error = Some(error) } -> TypeListResolutionResult(semanticTypes = [], error = Some(error))

let recursive constructorFunctionType parameters resultType =
    match parameters with
        | [] -> resultType
        | head :: tail -> SemFunction(head)(constructorFunctionType(tail)(resultType))(None)

let recursive registerConstructors constructors resultType quantified context environment =
    match constructors with
        | [] -> ConstructorRegistration(environment = environment, error = None)
        | TypeConstructor { name = name, parameters = parameters, fieldNames = _fieldNames } :: tail ->
            match resolveConstructorParameters(parameters)(context)([]) with
                | TypeListResolutionResult { semanticTypes = parameterTypes, error = None } ->
                    let constructorType = constructorFunctionType(parameterTypes)(resultType)
                    in
                        let scheme = TypeScheme(quantified = quantified, body = constructorType, constraints = [])
                        in registerConstructors(tail)(resultType)(quantified)(context)(addTypeBinding(name)(scheme)(environment))
                | TypeListResolutionResult { semanticTypes = _parameterTypes, error = Some(error) } -> ConstructorRegistration(environment = environment, error = Some(error))

let registerTypeDeclaration declaration state =
    match (declaration, state) with
        | (TypeDecl { name = name, typeParameters = parameters, constructors = constructors, isRecord = _isRecord, derivingTraits = _derivingTraits }, ProgramInferenceState { environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = symbolId, error = None }) ->
            let typedEnvironment = addInferenceTypeDefinition(symbolId)(name)(parameterCount(parameters))(environment)
            in
                match registerTypeParameters(parameters)(inferenceTypeResolutionContext(typedEnvironment))(supply)([])([]) with
                    | TypeParameterRegistration { context = context, semanticTypes = parameterTypes, quantified = quantified, supply = parameterSupply } ->
                        let resultType = SemNamed(symbolId)(name)(parameterTypes)
                        in
                            match registerConstructors(constructors)(resultType)(quantified)(context)(typedEnvironment) with
                                | ConstructorRegistration { environment = constructorEnvironment, error = None } -> ProgramInferenceState(environment = constructorEnvironment, substitution = substitution, supply = parameterSupply, nextTypeSymbolId = symbolId + 1, error = None)
                                | ConstructorRegistration { environment = _constructorEnvironment, error = Some(error) } -> ProgramInferenceState(environment = environment, substitution = substitution, supply = parameterSupply, nextTypeSymbolId = symbolId + 1, error = Some(ProgramTypeResolutionError(error)))
        | (_declaration, failedState) -> failedState

let inferTopLevelLet binding isRecursive state =
    match (binding, state) with
        | (LetBindingSyntax { name = name, value = value, sugarParameters = parameters, typeAnnotation = annotation, requirements = _requirements }, ProgramInferenceState { environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = None }) ->
            if isRecursive
            then ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = Some(UnsupportedTopLevelDeclaration("recursive binding")))
            else
                let bindingValue = wrapSugarParameters(parameters)(value)
                in
                    match inferTopLevelBinding(name)(bindingValue)(annotation)(environment)(substitution)(supply) with
                        | TopLevelBindingInferenceResult { environment = nextEnvironment, semanticType = _semanticType, substitution = nextSubstitution, supply = nextSupply, error = None } -> ProgramInferenceState(environment = nextEnvironment, substitution = nextSubstitution, supply = nextSupply, nextTypeSymbolId = nextTypeSymbolId, error = None)
                        | TopLevelBindingInferenceResult { environment = _nextEnvironment, semanticType = _semanticType, substitution = nextSubstitution, supply = nextSupply, error = Some(error) } -> ProgramInferenceState(environment = environment, substitution = nextSubstitution, supply = nextSupply, nextTypeSymbolId = nextTypeSymbolId, error = Some(ProgramExpressionError(error)))
        | (_binding, failedState) -> failedState

let recursive inferTopLevelItems items state =
    match items with
        | [] -> state
        | head :: tail ->
            let nextState =
                match head with
                    | TopLevelAt(_span, inner) -> inferTopLevelItems([inner])(state)
                    | TopLevelExport(_export) -> state
                    | TopLevelType(declaration) -> registerTypeDeclaration(declaration)(state)
                    | TopLevelLet(binding, isRecursive) -> inferTopLevelLet(binding)(isRecursive)(state)
                    | _ ->
                        match state with
                            | ProgramInferenceState { environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = _error } -> ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = Some(UnsupportedTopLevelDeclaration("declaration kind")))
            in inferTopLevelItems(tail)(nextState)

let inferProgram program =
    match program with
        | ProgramSyntax { items = items, body = body } ->
            let initialState = ProgramInferenceState(environment = emptyTypeEnvironment(Unit), substitution = [], supply = initialTypeVariableSupply(Unit), nextTypeSymbolId = 0, error = None)
            in
                match inferTopLevelItems(items)(initialState) with
                    | ProgramInferenceState { environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = _nextTypeSymbolId, error = None } ->
                        match body with
                            | None -> ProgramInferenceResult(semanticType = SemTuple([]), substitution = substitution, environment = environment, error = None)
                            | Some(expression) ->
                                match inferExpressionFrom(expression)(environment)(substitution)(supply) with
                                    | TypeInferenceResult { semanticType = semanticType, substitution = bodySubstitution, supply = _bodySupply, constraints = _constraints, error = None } -> ProgramInferenceResult(semanticType = semanticType, substitution = bodySubstitution, environment = environment, error = None)
                                    | TypeInferenceResult { semanticType = semanticType, substitution = bodySubstitution, supply = _bodySupply, constraints = _constraints, error = Some(error) } -> ProgramInferenceResult(semanticType = semanticType, substitution = bodySubstitution, environment = environment, error = Some(ProgramExpressionError(error)))
                    | ProgramInferenceState { environment = environment, substitution = substitution, supply = _supply, nextTypeSymbolId = _nextTypeSymbolId, error = Some(error) } -> ProgramInferenceResult(semanticType = SemNever, substitution = substitution, environment = environment, error = Some(error))
