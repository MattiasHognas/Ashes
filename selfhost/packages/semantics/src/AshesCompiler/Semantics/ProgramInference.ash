import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeSchemes
import AshesCompiler.Semantics.TypeResolution
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.Unification
import Ashes.Collection.List.reverse
export (
    type ProgramInferenceError(..),
    type ProgramInferenceResult(..),
    value inferProgram,
)

type ProgramInferenceError =
    | ProgramExpressionError(TypeInferenceError)
    | ProgramTypeResolutionError(TypeResolutionError)
    | DuplicateCapabilityDeclaration(Str)
    | ReservedCapabilityDeclaration(Str)
    | DuplicateCapabilityOperation(Str, Str)
    | ParameterizedCapabilityOperationRequiresSignature(Str, Str)
    | CapabilityOperationRequiresFunction(Str, Str)
    | UnknownProviderCapability(Str)
    | ProviderCapabilityArityMismatch(Str, Int, Int)
    | DuplicateCapabilityProvider(SemanticType)
    | UnknownProviderOperation(Str, Str)
    | DuplicateProviderOperation(Str, Str)
    | MissingProviderOperation(Str, Str)
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

type PendingRecursiveBinding =
    | name: Str
    | value: Expr
    | annotation: Maybe(TypeExpr)
    | placeholderType: SemanticType

type ResolvedRecursiveBinding =
    | name: Str
    | semanticType: SemanticType
    | constraints: List(TraitConstraint)

type RecursivePreparation =
    | environment: TypeEnvironment
    | pending: List(PendingRecursiveBinding)
    | supply: TypeVariableSupply

type RecursiveInference =
    | resolved: List(ResolvedRecursiveBinding)
    | substitution: List((Int, SemanticType))
    | supply: TypeVariableSupply
    | error: Maybe(TypeInferenceError)

type AliasParameterRegistration =
    | context: TypeResolutionContext
    | parameterIds: List(Int)

type CapabilityOperationRegistration =
    | environment: TypeEnvironment
    | operations: List(CapabilityOperationInferenceDefinition)
    | supply: TypeVariableSupply
    | error: Maybe(ProgramInferenceError)

type ProviderOperationRegistration =
    | operations: List(CapabilityProviderOperationInferenceDefinition)
    | substitution: List((Int, SemanticType))
    | supply: TypeVariableSupply
    | error: Maybe(ProgramInferenceError)

let recursive wrapSugarParameters parameters body =
    match parameters with
        | [] -> body
        | head :: tail -> ExprLambda(head)(wrapSugarParameters(tail)(body))(None)

let recursive parameterCount parameters =
    match parameters with
        | [] -> 0
        | _head :: tail -> 1 + parameterCount(tail)

let recursive registerAliasParameters parameters context nextId reversedIds =
    match parameters with
        | [] -> AliasParameterRegistration(context = context, parameterIds = reverse(reversedIds))
        | TypeParameter { name = name } :: tail -> registerAliasParameters(tail)(addTypeParameter(name)(SemParameter(nextId)(name))(context))(nextId + 1)(nextId :: reversedIds)

let recursive appendProgramSubstitution left right =
    match left with
        | [] -> right
        | head :: tail -> head :: appendProgramSubstitution(tail)(right)

let recursive prepareRecursiveBindings bindings environment supply reversedPending =
    match bindings with
        | [] -> RecursivePreparation(environment = environment, pending = reverse(reversedPending), supply = supply)
        | LetBindingSyntax { name = name, value = value, sugarParameters = parameters, typeAnnotation = annotation, requirements = _requirements } :: tail ->
            match freshTypeVariable(supply) with
                | (placeholderType, nextSupply) ->
                    let scheme = TypeScheme(quantified = [], body = placeholderType, constraints = [])
                    in
                        let pending = PendingRecursiveBinding(name = name, value = wrapSugarParameters(parameters)(value), annotation = annotation, placeholderType = placeholderType)
                        in prepareRecursiveBindings(tail)(addTypeBinding(name)(scheme)(environment))(nextSupply)(pending :: reversedPending)

let recursive inferRecursiveBindings pending environment substitution supply reversedResolved =
    match pending with
        | [] -> RecursiveInference(resolved = reverse(reversedResolved), substitution = substitution, supply = supply, error = None)
        | PendingRecursiveBinding { name = name, value = value, annotation = annotation, placeholderType = placeholderType } :: tail ->
            match inferExpressionFrom(value)(environment)(substitution)(supply) with
                | TypeInferenceResult { semanticType = valueType, substitution = valueSubstitution, supply = valueSupply, constraints = valueConstraints, error = None } ->
                    match unify(applySubstitution(valueSubstitution)(placeholderType))(applySubstitution(valueSubstitution)(valueType)) with
                        | UnificationResult { substitution = unificationSubstitution, error = None } ->
                            let unifiedSubstitution = appendProgramSubstitution(unificationSubstitution)(valueSubstitution)
                            in
                                let unifiedType = applySubstitution(unifiedSubstitution)(placeholderType)
                                in
                                    let annotationResult =
                                        match annotation with
                                            | None -> TypeInferenceResult(semanticType = unifiedType, substitution = unifiedSubstitution, supply = valueSupply, constraints = [], error = None)
                                            | Some(typeExpression) -> checkInferenceAnnotation(typeExpression)(unifiedType)(environment)(unifiedSubstitution)(valueSupply)
                                    in
                                        match annotationResult with
                                            | TypeInferenceResult { semanticType = annotatedType, substitution = annotatedSubstitution, supply = annotatedSupply, constraints = _annotationConstraints, error = None } ->
                                                let resolvedBinding = ResolvedRecursiveBinding(name = name, semanticType = annotatedType, constraints = valueConstraints)
                                                in inferRecursiveBindings(tail)(environment)(annotatedSubstitution)(annotatedSupply)(resolvedBinding :: reversedResolved)
                                            | TypeInferenceResult { semanticType = _failedType, substitution = failedSubstitution, supply = failedSupply, constraints = _failedConstraints, error = Some(error) } -> RecursiveInference(resolved = [], substitution = failedSubstitution, supply = failedSupply, error = Some(error))
                        | UnificationResult { substitution = _unificationSubstitution, error = Some(error) } -> RecursiveInference(resolved = [], substitution = valueSubstitution, supply = valueSupply, error = Some(InferenceUnificationError(error)))
                | TypeInferenceResult { semanticType = _failedType, substitution = failedSubstitution, supply = failedSupply, constraints = _failedConstraints, error = Some(error) } -> RecursiveInference(resolved = [], substitution = failedSubstitution, supply = failedSupply, error = Some(error))

let recursive generalizeRecursiveBindings bindings outerEnvironment substitution resultEnvironment =
    match bindings with
        | [] -> resultEnvironment
        | ResolvedRecursiveBinding { name = name, semanticType = semanticType, constraints = constraints } :: tail ->
            let resolvedType = applySubstitution(substitution)(semanticType)
            in
                let resolvedConstraints = applyInferenceConstraints(substitution)(constraints)
                in
                    let scheme = generalize(inferenceEnvironmentSchemes(outerEnvironment))(resolvedType)(resolvedConstraints)
                    in generalizeRecursiveBindings(tail)(outerEnvironment)(substitution)(addTypeBinding(name)(scheme)(resultEnvironment))

let inferRecursiveGroup bindings state =
    match state with
        | ProgramInferenceState { environment = outerEnvironment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = None } ->
            match prepareRecursiveBindings(bindings)(outerEnvironment)(supply)([]) with
                | RecursivePreparation { environment = recursiveEnvironment, pending = pending, supply = preparedSupply } ->
                    match inferRecursiveBindings(pending)(recursiveEnvironment)(substitution)(preparedSupply)([]) with
                        | RecursiveInference { resolved = resolved, substitution = resolvedSubstitution, supply = resolvedSupply, error = None } ->
                            let finalEnvironment = generalizeRecursiveBindings(resolved)(outerEnvironment)(resolvedSubstitution)(outerEnvironment)
                            in ProgramInferenceState(environment = finalEnvironment, substitution = resolvedSubstitution, supply = resolvedSupply, nextTypeSymbolId = nextTypeSymbolId, error = None)
                        | RecursiveInference { resolved = _resolved, substitution = failedSubstitution, supply = failedSupply, error = Some(error) } -> ProgramInferenceState(environment = outerEnvironment, substitution = failedSubstitution, supply = failedSupply, nextTypeSymbolId = nextTypeSymbolId, error = Some(ProgramExpressionError(error)))
        | failedState -> failedState

let recursive registerTypeParameters parameters context supply reversedTypes reversedQuantified =
    match parameters with
        | [] -> TypeParameterRegistration(context = context, semanticTypes = reverse(reversedTypes), quantified = reverse(reversedQuantified), supply = supply)
        | TypeParameter { name = name } :: tail ->
            match freshTypeVariable(supply) with
                | (SemVariable(variableId), nextSupply) -> registerTypeParameters(tail)(addTypeParameter(name)(SemVariable(variableId))(context))(nextSupply)(SemVariable(variableId) :: reversedTypes)((variableId, name) :: reversedQuantified)
                | (_unexpected, nextSupply) -> registerTypeParameters(tail)(context)(nextSupply)(reversedTypes)(reversedQuantified)

let recursive operationNameExists name operations =
    match operations with
        | [] -> false
        | CapabilityOperationInferenceDefinition { name = candidateName, scheme = _scheme, hasExplicitSignature = _hasExplicitSignature } :: tail ->
            if name == candidateName
            then true
            else operationNameExists(name)(tail)

let isBuiltinRuntimeCapability name =
    if name == "ConsoleIO"
    then true
    else
        if name == "FileRead"
        then true
        else
            if name == "FileWrite"
            then true
            else
                if name == "ProcessSpawn"
                then true
                else
                    if name == "ProcessExit"
                    then true
                    else
                        if name == "TimeRead"
                        then true
                        else
                            if name == "EnvironmentRead"
                            then true
                            else
                                if name == "Entropy"
                                then true
                                else
                                    if name == "UnsafeFfi"
                                    then true
                                    else
                                        if name == "NetListen"
                                        then true
                                        else
                                            if name == "NetConnect"
                                            then true
                                            else name == "Stop"

let recursive addCapabilityToInnermostArrow capabilityType semanticType =
    match semanticType with
        | SemFunction(argument, result, row) ->
            match result with
                | SemFunction(_, _, _) ->
                    match addCapabilityToInnermostArrow(capabilityType)(result) with
                        | None -> None
                        | Some(effectfulResult) -> Some(SemFunction(argument)(effectfulResult)(row))
                | _ -> Some(SemFunction(argument)(result)(Some(SemRow([capabilityType])(None))))
        | _ -> None

let recursive registerCapabilityOperations capabilityName declarations capabilityType capabilityScheme quantified context environment supply reversed =
    match declarations with
        | [] -> CapabilityOperationRegistration(environment = addCapabilityBinding(capabilityName)(capabilityScheme)(reverse(reversed))(environment), operations = reverse(reversed), supply = supply, error = None)
        | CapabilityOperation { name = operationName, signature = signature } :: tail ->
            if operationNameExists(operationName)(reversed)
            then CapabilityOperationRegistration(environment = environment, operations = reverse(reversed), supply = supply, error = Some(DuplicateCapabilityOperation(capabilityName)(operationName)))
            else
                match signature with
                    | None ->
                        match quantified with
                            | _ :: _ -> CapabilityOperationRegistration(environment = environment, operations = reverse(reversed), supply = supply, error = Some(ParameterizedCapabilityOperationRequiresSignature(capabilityName)(operationName)))
                            | [] ->
                                match freshTypeVariable(supply) with
                                    | (operationType, nextSupply) ->
                                        let scheme = TypeScheme(quantified = [], body = operationType, constraints = [])
                                        in
                                            let definition = CapabilityOperationInferenceDefinition(name = operationName, scheme = scheme, hasExplicitSignature = false)
                                            in registerCapabilityOperations(capabilityName)(tail)(capabilityType)(capabilityScheme)(quantified)(context)(addTypeBinding(capabilityName + "." + operationName)(scheme)(environment))(nextSupply)(definition :: reversed)
                    | Some(signatureType) ->
                        match resolveTypeExpression(signatureType)(context) with
                            | TypeResolutionResult { semanticType = resolvedSignature, error = None } ->
                                match addCapabilityToInnermostArrow(capabilityType)(resolvedSignature) with
                                    | None -> CapabilityOperationRegistration(environment = environment, operations = reverse(reversed), supply = supply, error = Some(CapabilityOperationRequiresFunction(capabilityName)(operationName)))
                                    | Some(operationType) ->
                                        let scheme = TypeScheme(quantified = quantified, body = operationType, constraints = [])
                                        in
                                            let definition = CapabilityOperationInferenceDefinition(name = operationName, scheme = scheme, hasExplicitSignature = true)
                                            in registerCapabilityOperations(capabilityName)(tail)(capabilityType)(capabilityScheme)(quantified)(context)(addTypeBinding(capabilityName + "." + operationName)(scheme)(environment))(supply)(definition :: reversed)
                            | TypeResolutionResult { semanticType = _resolvedSignature, error = Some(error) } -> CapabilityOperationRegistration(environment = environment, operations = reverse(reversed), supply = supply, error = Some(ProgramTypeResolutionError(error)))

let registerCapabilityDeclaration declaration state =
    match (declaration, state) with
        | (CapabilityDecl { name = name, typeParameters = parameters, operations = operations }, ProgramInferenceState { environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = None }) ->
            if isBuiltinRuntimeCapability(name)
            then ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = Some(ReservedCapabilityDeclaration(name)))
            else
                match resolveCapabilityBinding(name)(environment) with
                    | Some(_) -> ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = Some(DuplicateCapabilityDeclaration(name)))
                    | None ->
                        match registerTypeParameters(parameters)(inferenceTypeResolutionContext(environment))(supply)([])([]) with
                            | TypeParameterRegistration { context = context, semanticTypes = parameterTypes, quantified = quantified, supply = parameterSupply } ->
                                let capabilityType = SemCapability(name)(parameterTypes)
                                in
                                    let capabilityScheme = TypeScheme(quantified = quantified, body = capabilityType, constraints = [])
                                    in
                                        match registerCapabilityOperations(name)(operations)(capabilityType)(capabilityScheme)(quantified)(context)(environment)(parameterSupply)([]) with
                                            | CapabilityOperationRegistration { environment = capabilityEnvironment, operations = _registeredOperations, supply = operationSupply, error = None } -> ProgramInferenceState(environment = capabilityEnvironment, substitution = substitution, supply = operationSupply, nextTypeSymbolId = nextTypeSymbolId, error = None)
                                            | CapabilityOperationRegistration { environment = _capabilityEnvironment, operations = _registeredOperations, supply = operationSupply, error = Some(error) } -> ProgramInferenceState(environment = environment, substitution = substitution, supply = operationSupply, nextTypeSymbolId = nextTypeSymbolId, error = Some(error))
        | (_declaration, failedState) -> failedState

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
        | TypeConstructor { name = name, parameters = parameters, fieldNames = fieldNames } :: tail ->
            match resolveConstructorParameters(parameters)(context)([]) with
                | TypeListResolutionResult { semanticTypes = parameterTypes, error = None } ->
                    let constructorType = constructorFunctionType(parameterTypes)(resultType)
                    in
                        let scheme = TypeScheme(quantified = quantified, body = constructorType, constraints = [])
                        in registerConstructors(tail)(resultType)(quantified)(context)(addConstructorBinding(name)(scheme)(fieldNames)(environment))
                | TypeListResolutionResult { semanticTypes = _parameterTypes, error = Some(error) } -> ConstructorRegistration(environment = environment, error = Some(error))

let recursive semanticTypeListLength values =
    match values with
        | [] -> 0
        | _ :: tail -> 1 + semanticTypeListLength(tail)

let recursive providerBindingExists operationName bindings =
    match bindings with
        | [] -> false
        | ProvideBinding { operationName = candidateName, implementation = _implementation } :: tail ->
            if operationName == candidateName
            then true
            else providerBindingExists(operationName)(tail)

let recursive providerNameExists name values =
    match values with
        | [] -> false
        | head :: tail ->
            if name == head
            then true
            else providerNameExists(name)(tail)

let recursive findProviderBinding operationName bindings =
    match bindings with
        | [] -> None
        | ProvideBinding { operationName = candidateName, implementation = implementation } :: tail ->
            if operationName == candidateName
            then Some(implementation)
            else findProviderBinding(operationName)(tail)

let recursive findDuplicateProviderBinding bindings seen =
    match bindings with
        | [] -> None
        | ProvideBinding { operationName = operationName, implementation = _implementation } :: tail ->
            if providerNameExists(operationName)(seen)
            then Some(operationName)
            else findDuplicateProviderBinding(tail)(operationName :: seen)

let recursive findUnknownProviderBinding capabilityName bindings environment =
    match bindings with
        | [] -> None
        | ProvideBinding { operationName = operationName, implementation = _implementation } :: tail ->
            match resolveCapabilityOperation(capabilityName)(operationName)(environment) with
                | None -> Some(operationName)
                | Some(_) -> findUnknownProviderBinding(capabilityName)(tail)(environment)

let recursive findMissingProviderBinding operations bindings =
    match operations with
        | [] -> None
        | CapabilityOperationInferenceDefinition { name = operationName, scheme = _scheme, hasExplicitSignature = _hasExplicitSignature } :: tail ->
            if providerBindingExists(operationName)(bindings)
            then findMissingProviderBinding(tail)(bindings)
            else Some(operationName)

let recursive providerOperationCapability capabilityName semanticType =
    match semanticType with
        | SemFunction(_argument, result, row) ->
            match result with
                | SemFunction(_, _, _) -> providerOperationCapability(capabilityName)(result)
                | _ ->
                    match row with
                        | Some(SemRow(capabilities, _tail)) ->
                            let recursive findCapability values =
                                match values with
                                    | [] -> None
                                    | SemCapability(name, arguments) :: tail ->
                                        if name == capabilityName
                                        then Some(SemCapability(name)(arguments))
                                        else findCapability(tail)
                                    | _ :: tail -> findCapability(tail)
                            in findCapability(capabilities)
                        | _ -> None
        | _ -> None

let recursive detachProviderOperationRow semanticType =
    match semanticType with
        | SemFunction(argument, result, row) ->
            match result with
                | SemFunction(_, _, _) -> SemFunction(argument)(detachProviderOperationRow(result))(row)
                | _ -> SemFunction(argument)(result)(None)
        | _ -> semanticType

let prepareProviderExpectedType capabilityName capabilityType operation substitution supply =
    match operation with
        | CapabilityOperationInferenceDefinition { name = _operationName, scheme = scheme, hasExplicitSignature = hasExplicitSignature } ->
            match instantiate(scheme)(supply) with
                | InstantiationResult { semanticType = operationType, constraints = _constraints, supply = operationSupply } ->
                    if hasExplicitSignature
                    then
                        match providerOperationCapability(capabilityName)(operationType) with
                            | None -> TypeInferenceResult(semanticType = SemNever, substitution = substitution, supply = operationSupply, constraints = [], error = Some(UnsupportedInferenceExpression("provider operation has no capability row")))
                            | Some(operationCapability) ->
                                match unify(applySubstitution(substitution)(operationCapability))(applySubstitution(substitution)(capabilityType)) with
                                    | UnificationResult { substitution = operationSubstitution, error = None } ->
                                        let combined = appendProgramSubstitution(operationSubstitution)(substitution)
                                        in TypeInferenceResult(semanticType = applySubstitution(combined)(detachProviderOperationRow(operationType)), substitution = combined, supply = operationSupply, constraints = [], error = None)
                                    | UnificationResult { substitution = _operationSubstitution, error = Some(error) } -> TypeInferenceResult(semanticType = SemNever, substitution = substitution, supply = operationSupply, constraints = [], error = Some(InferenceUnificationError(error)))
                    else TypeInferenceResult(semanticType = operationType, substitution = substitution, supply = operationSupply, constraints = [], error = None)

let recursive registerProviderOperations capabilityName capabilityType operationDefinitions bindings environment substitution supply reversed =
    match operationDefinitions with
        | [] -> ProviderOperationRegistration(operations = reverse(reversed), substitution = substitution, supply = supply, error = None)
        | operation :: tail ->
            match operation with
                | CapabilityOperationInferenceDefinition { name = operationName, scheme = _scheme, hasExplicitSignature = _hasExplicitSignature } ->
                    match findProviderBinding(operationName)(bindings) with
                        | None -> ProviderOperationRegistration(operations = reverse(reversed), substitution = substitution, supply = supply, error = Some(MissingProviderOperation(capabilityName)(operationName)))
                        | Some(implementation) ->
                            match prepareProviderExpectedType(capabilityName)(capabilityType)(operation)(substitution)(supply) with
                                | TypeInferenceResult { semanticType = expectedType, substitution = expectedSubstitution, supply = expectedSupply, constraints = _expectedConstraints, error = None } ->
                                    match inferExpressionFrom(implementation)(environment)(expectedSubstitution)(expectedSupply) with
                                        | TypeInferenceResult { semanticType = implementationType, substitution = implementationSubstitution, supply = implementationSupply, constraints = _implementationConstraints, error = None } ->
                                            match unify(applySubstitution(implementationSubstitution)(expectedType))(applySubstitution(implementationSubstitution)(implementationType)) with
                                                | UnificationResult { substitution = providerSubstitution, error = None } ->
                                                    let combined = appendProgramSubstitution(providerSubstitution)(implementationSubstitution)
                                                    in
                                                        let registered = CapabilityProviderOperationInferenceDefinition(name = operationName, semanticType = applySubstitution(combined)(implementationType))
                                                        in registerProviderOperations(capabilityName)(capabilityType)(tail)(bindings)(environment)(combined)(implementationSupply)(registered :: reversed)
                                                | UnificationResult { substitution = _providerSubstitution, error = Some(error) } -> ProviderOperationRegistration(operations = reverse(reversed), substitution = implementationSubstitution, supply = implementationSupply, error = Some(ProgramExpressionError(InferenceUnificationError(error))))
                                        | TypeInferenceResult { semanticType = _implementationType, substitution = failedSubstitution, supply = failedSupply, constraints = _implementationConstraints, error = Some(error) } -> ProviderOperationRegistration(operations = reverse(reversed), substitution = failedSubstitution, supply = failedSupply, error = Some(ProgramExpressionError(error)))
                                | TypeInferenceResult { semanticType = _expectedType, substitution = failedSubstitution, supply = failedSupply, constraints = _expectedConstraints, error = Some(error) } -> ProviderOperationRegistration(operations = reverse(reversed), substitution = failedSubstitution, supply = failedSupply, error = Some(ProgramExpressionError(error)))

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

let registerTypeAlias declaration state =
    match (declaration, state) with
        | (TypeAliasDecl { name = name, typeParameters = parameters, target = target }, ProgramInferenceState { environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = None }) ->
            match registerAliasParameters(parameters)(inferenceTypeResolutionContext(environment))(0)([]) with
                | AliasParameterRegistration { context = aliasContext, parameterIds = parameterIds } ->
                    match resolveTypeExpression(target)(aliasContext) with
                        | TypeResolutionResult { semanticType = targetType, error = None } -> ProgramInferenceState(environment = addInferenceTypeAlias(name)(parameterIds)(targetType)(environment), substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = None)
                        | TypeResolutionResult { semanticType = _targetType, error = Some(error) } -> ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = Some(ProgramTypeResolutionError(error)))
        | (_declaration, failedState) -> failedState

let registerZeroCostType declaration state =
    match declaration with
        | ZeroCostTypeDecl { name = name, typeParameters = typeParameters, constructor = constructor, derivingTraits = derivingTraits } ->
            let nominal = TypeDecl(name = name, typeParameters = typeParameters, constructors = [constructor], isRecord = false, derivingTraits = derivingTraits)
            in registerTypeDeclaration(nominal)(state)

let registerCapabilityProvider declaration state =
    match (declaration, state) with
        | (ProvideDecl { capabilityName = capabilityName, typeArguments = typeArguments, bindings = bindings }, ProgramInferenceState { environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = None }) ->
            match resolveCapabilityBinding(capabilityName)(environment) with
                | None -> ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = Some(UnknownProviderCapability(capabilityName)))
                | Some(CapabilityInferenceDefinition { name = _name, scheme = capabilityScheme, operations = operations }) ->
                    match capabilityScheme with
                        | TypeScheme { quantified = _quantified, body = SemCapability(_schemeName, parameterTypes), constraints = _constraints } ->
                            let expectedArity = semanticTypeListLength(parameterTypes)
                            in
                                let actualArity = parameterCount(typeArguments)
                                in
                                    if expectedArity == actualArity
                                    then
                                        match resolveConstructorParameters(typeArguments)(inferenceTypeResolutionContext(environment))([]) with
                                            | TypeListResolutionResult { semanticTypes = resolvedArguments, error = None } ->
                                                let capabilityType = SemCapability(capabilityName)(resolvedArguments)
                                                in
                                                    match resolveCapabilityProvider(capabilityType)(environment) with
                                                        | Some(_) -> ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = Some(DuplicateCapabilityProvider(capabilityType)))
                                                        | None ->
                                                            match findDuplicateProviderBinding(bindings)([]) with
                                                                | Some(operationName) -> ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = Some(DuplicateProviderOperation(capabilityName)(operationName)))
                                                                | None ->
                                                                    match findUnknownProviderBinding(capabilityName)(bindings)(environment) with
                                                                        | Some(operationName) -> ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = Some(UnknownProviderOperation(capabilityName)(operationName)))
                                                                        | None ->
                                                                            match findMissingProviderBinding(operations)(bindings) with
                                                                                | Some(operationName) -> ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = Some(MissingProviderOperation(capabilityName)(operationName)))
                                                                                | None ->
                                                                                    match registerProviderOperations(capabilityName)(capabilityType)(operations)(bindings)(environment)(substitution)(supply)([]) with
                                                                                        | ProviderOperationRegistration { operations = registeredOperations, substitution = providerSubstitution, supply = providerSupply, error = None } -> ProgramInferenceState(environment = addCapabilityProvider(capabilityType)(registeredOperations)(environment), substitution = providerSubstitution, supply = providerSupply, nextTypeSymbolId = nextTypeSymbolId, error = None)
                                                                                        | ProviderOperationRegistration { operations = _registeredOperations, substitution = failedSubstitution, supply = failedSupply, error = Some(error) } -> ProgramInferenceState(environment = environment, substitution = failedSubstitution, supply = failedSupply, nextTypeSymbolId = nextTypeSymbolId, error = Some(error))
                                            | TypeListResolutionResult { semanticTypes = _resolvedArguments, error = Some(error) } -> ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = Some(ProgramTypeResolutionError(error)))
                                    else ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = Some(ProviderCapabilityArityMismatch(capabilityName)(expectedArity)(actualArity)))
                        | _ -> ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = Some(UnknownProviderCapability(capabilityName)))
        | (_declaration, failedState) -> failedState

let inferTopLevelLet binding isRecursive state =
    match (binding, state) with
        | (LetBindingSyntax { name = name, value = value, sugarParameters = parameters, typeAnnotation = annotation, requirements = _requirements }, ProgramInferenceState { environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = None }) ->
            if isRecursive
            then inferRecursiveGroup([binding])(state)
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
                    | TopLevelTypeAlias(declaration) -> registerTypeAlias(declaration)(state)
                    | TopLevelZeroCostType(declaration) -> registerZeroCostType(declaration)(state)
                    | TopLevelCapability(declaration) -> registerCapabilityDeclaration(declaration)(state)
                    | TopLevelProvide(declaration) -> registerCapabilityProvider(declaration)(state)
                    | TopLevelLet(binding, isRecursive) -> inferTopLevelLet(binding)(isRecursive)(state)
                    | TopLevelRecursiveGroup(bindings) -> inferRecursiveGroup(bindings)(state)
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
