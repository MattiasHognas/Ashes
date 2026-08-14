import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeSchemes
import AshesCompiler.Semantics.TypeResolution
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.Unification
import Ashes.Collection.List.reverse
import Ashes.Collection.List.sortBy
import Ashes.Text.compare as compareText
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
    | DuplicateTraitDeclaration(Str)
    | TraitRequiresTypeParameter(Str)
    | DuplicateTraitTypeParameter(Str, Str)
    | DuplicateTraitMethod(Str, Str)
    | TraitMethodMustMentionParameter(Str, Str)
    | UnknownSupertrait(Str, Str)
    | SupertraitArityMismatch(Str, Int, Int)
    | CyclicSupertraitRequirements(Str)
    | UnknownTraitImplementation(Str)
    | TraitImplementationArityMismatch(Str, Int, Int)
    | UnknownTraitImplementationRequirement(Str, Str)
    | TraitImplementationRequirementArityMismatch(Str, Int, Int)
    | TraitImplementationRequirementVariableEscapes(Str, Str)
    | UnknownTraitImplementationMethod(Str, Str)
    | DuplicateTraitImplementationMethod(Str, Str)
    | MissingTraitImplementationMethod(Str, Str)
    | OverlappingTraitImplementations(Str)
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

type TraitMethodRegistration =
    | environment: TypeEnvironment
    | methods: List(TraitMethodInferenceDefinition)
    | supply: TypeVariableSupply
    | error: Maybe(ProgramInferenceError)

type TraitConstraintRegistration =
    | constraints: List(TraitConstraint)
    | error: Maybe(ProgramInferenceError)

type TraitImplementationParameterRegistration =
    | context: TypeResolutionContext
    | nextTypeSymbolId: Int

type TraitImplementationMethodRegistration =
    | methods: List(TraitImplementationMethodInferenceDefinition)
    | substitution: List((Int, SemanticType))
    | supply: TypeVariableSupply
    | error: Maybe(ProgramInferenceError)

type TraitHeadOverlapResult =
    | substitutions: List((Int, SemanticType))
    | overlaps: Bool

let recursive wrapSugarParameters parameters body =
    match parameters with
        | [] -> body
        | head :: tail -> ExprLambda(head)(wrapSugarParameters(tail)(body))(None)

let recursive syntaxListLength parameters =
    match parameters with
        | [] -> 0
        | _head :: tail -> 1 + syntaxListLength(tail)

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

let recursive nameExists : Str -> List(Str) -> Bool =
    given (name) ->
        given (names) ->
            match names with
                | [] -> false
                | head :: tail ->
                    if name == head
                    then true
                    else nameExists(name)(tail)

let recursive findDuplicateTypeParameter parameters seen =
    match parameters with
        | [] -> None
        | TypeParameter { name = name } :: tail ->
            if nameExists(name)(seen)
            then Some(name)
            else findDuplicateTypeParameter(tail)(name :: seen)

let recursive findDuplicateTraitMethod methods seen =
    match methods with
        | [] -> None
        | TraitMethodDecl { name = name, signature = _signature, defaultImplementation = _defaultImplementation } :: tail ->
            if nameExists(name)(seen)
            then Some(name)
            else findDuplicateTraitMethod(tail)(name :: seen)

let recursive findTraitDeclaration name declarations =
    match declarations with
        | [] -> None
        | TraitDecl { name = candidateName, typeParameters = typeParameters, supertraits = supertraits, methods = methods } :: tail ->
            if name == candidateName
            then Some(TraitDecl(name = candidateName, typeParameters = typeParameters, supertraits = supertraits, methods = methods))
            else findTraitDeclaration(name)(tail)

let recursive collectTraitDeclarations items reversed =
    match items with
        | [] -> reverse(reversed)
        | TopLevelAt(_span, inner) :: tail -> collectTraitDeclarations(inner :: tail)(reversed)
        | TopLevelTrait(declaration) :: tail -> collectTraitDeclarations(tail)(declaration :: reversed)
        | _ :: tail -> collectTraitDeclarations(tail)(reversed)

let recursive validateSupertraitReferences owner supertraits declarations =
    match supertraits with
        | [] -> None
        | TraitConstraintSyntax { traitName = traitName, typeArguments = typeArguments } :: tail ->
            match findTraitDeclaration(traitName)(declarations) with
                | None -> Some(UnknownSupertrait(owner)(traitName))
                | Some(TraitDecl { name = _name, typeParameters = parameters, supertraits = _supertraits, methods = _methods }) ->
                    let expectedArity = syntaxListLength(parameters)
                    in
                        let actualArity = syntaxListLength(typeArguments)
                        in
                            if expectedArity == actualArity
                            then validateSupertraitReferences(owner)(tail)(declarations)
                            else Some(SupertraitArityMismatch(traitName)(expectedArity)(actualArity))

let recursive findSupertraitCycleInRequirements requirements declarations path =
    match requirements with
        | [] -> None
        | TraitConstraintSyntax { traitName = traitName, typeArguments = _typeArguments } :: tail ->
            match findSupertraitCycle(traitName)(declarations)(path) with
                | Some(cycle) -> Some(cycle)
                | None -> findSupertraitCycleInRequirements(tail)(declarations)(path)
and findSupertraitCycle name declarations path =
    if nameExists(name)(path)
    then Some(name)
    else
        match findTraitDeclaration(name)(declarations) with
            | None -> None
            | Some(TraitDecl { name = _declarationName, typeParameters = _parameters, supertraits = supertraits, methods = _methods }) -> findSupertraitCycleInRequirements(supertraits)(declarations)(name :: path)

let recursive validateTraitDeclarations declarations allDeclarations seen =
    match declarations with
        | [] -> None
        | TraitDecl { name = name, typeParameters = parameters, supertraits = supertraits, methods = methods } :: tail ->
            if nameExists(name)(seen)
            then Some(DuplicateTraitDeclaration(name))
            else
                match parameters with
                    | [] -> Some(TraitRequiresTypeParameter(name))
                    | _ ->
                        match findDuplicateTypeParameter(parameters)([]) with
                            | Some(parameterName) -> Some(DuplicateTraitTypeParameter(name)(parameterName))
                            | None ->
                                match findDuplicateTraitMethod(methods)([]) with
                                    | Some(methodName) -> Some(DuplicateTraitMethod(name)(methodName))
                                    | None ->
                                        match validateSupertraitReferences(name)(supertraits)(allDeclarations) with
                                            | Some(error) -> Some(error)
                                            | None ->
                                                match findSupertraitCycle(name)(allDeclarations)([]) with
                                                    | Some(cycle) -> Some(CyclicSupertraitRequirements(cycle))
                                                    | None -> validateTraitDeclarations(tail)(allDeclarations)(name :: seen)

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
            let typedEnvironment = addInferenceTypeDefinition(symbolId)(name)(syntaxListLength(parameters))(environment)
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
                                let actualArity = syntaxListLength(typeArguments)
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

let recursive anyTraitParameterOccurs parameters semanticType =
    match parameters with
        | [] -> false
        | SemVariable(variableId) :: tail ->
            if occursInType(variableId)(semanticType)
            then true
            else anyTraitParameterOccurs(tail)(semanticType)
        | _ :: tail -> anyTraitParameterOccurs(tail)(semanticType)

let recursive registerTraitMethods traitName declarations parameterTypes quantified context environment supply reversed =
    match declarations with
        | [] -> TraitMethodRegistration(environment = environment, methods = reverse(reversed), supply = supply, error = None)
        | TraitMethodDecl { name = methodName, signature = signature, defaultImplementation = defaultImplementation } :: tail ->
            match resolveTypeExpression(signature)(context) with
                | TypeResolutionResult { semanticType = methodType, error = None } ->
                    if anyTraitParameterOccurs(parameterTypes)(methodType)
                    then
                        let constraint = TraitConstraint(traitName = traitName, typeArguments = parameterTypes)
                        in
                            let scheme = TypeScheme(quantified = quantified, body = methodType, constraints = [constraint])
                            in
                                let definition = TraitMethodInferenceDefinition(name = methodName, scheme = scheme, defaultImplementation = defaultImplementation)
                                in registerTraitMethods(traitName)(tail)(parameterTypes)(quantified)(context)(addTypeBinding(traitName + "." + methodName)(scheme)(environment))(supply)(definition :: reversed)
                    else TraitMethodRegistration(environment = environment, methods = reverse(reversed), supply = supply, error = Some(TraitMethodMustMentionParameter(traitName)(methodName)))
                | TypeResolutionResult { semanticType = _methodType, error = Some(error) } -> TraitMethodRegistration(environment = environment, methods = reverse(reversed), supply = supply, error = Some(ProgramTypeResolutionError(error)))

let recursive resolveTraitConstraints declarations context reversed =
    match declarations with
        | [] -> TraitConstraintRegistration(constraints = reverse(reversed), error = None)
        | TraitConstraintSyntax { traitName = traitName, typeArguments = typeArguments } :: tail ->
            match resolveConstructorParameters(typeArguments)(context)([]) with
                | TypeListResolutionResult { semanticTypes = arguments, error = None } ->
                    let constraint = TraitConstraint(traitName = traitName, typeArguments = arguments)
                    in resolveTraitConstraints(tail)(context)(constraint :: reversed)
                | TypeListResolutionResult { semanticTypes = _arguments, error = Some(error) } -> TraitConstraintRegistration(constraints = reverse(reversed), error = Some(ProgramTypeResolutionError(error)))

let registerTraitDeclaration declaration state =
    match (declaration, state) with
        | (TraitDecl { name = name, typeParameters = parameters, supertraits = supertraits, methods = methods }, ProgramInferenceState { environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = None }) ->
            match registerTypeParameters(parameters)(inferenceTypeResolutionContext(environment))(supply)([])([]) with
                | TypeParameterRegistration { context = context, semanticTypes = parameterTypes, quantified = quantified, supply = parameterSupply } ->
                    match resolveTraitConstraints(supertraits)(context)([]) with
                        | TraitConstraintRegistration { constraints = resolvedSupertraits, error = None } ->
                            match registerTraitMethods(name)(methods)(parameterTypes)(quantified)(context)(environment)(parameterSupply)([]) with
                                | TraitMethodRegistration { environment = methodEnvironment, methods = registeredMethods, supply = methodSupply, error = None } -> ProgramInferenceState(environment = addTraitBinding(name)(syntaxListLength(parameters))(registeredMethods)(resolvedSupertraits)(methodEnvironment), substitution = substitution, supply = methodSupply, nextTypeSymbolId = nextTypeSymbolId, error = None)
                                | TraitMethodRegistration { environment = _methodEnvironment, methods = _registeredMethods, supply = methodSupply, error = Some(error) } -> ProgramInferenceState(environment = environment, substitution = substitution, supply = methodSupply, nextTypeSymbolId = nextTypeSymbolId, error = Some(error))
                        | TraitConstraintRegistration { constraints = _resolvedSupertraits, error = Some(error) } -> ProgramInferenceState(environment = environment, substitution = substitution, supply = parameterSupply, nextTypeSymbolId = nextTypeSymbolId, error = Some(error))
        | (_declaration, failedState) -> failedState

let isImplementationParameterName name =
    if name == "u8"
    then false
    else
        if name == "u16"
        then false
        else
            if name == "u32"
            then false
            else
                if name == "u64"
                then false
                else
                    let bytes = Ashes.Byte.fromText(name)
                    in
                        if Ashes.Byte.length(bytes) <= 0
                        then false
                        else
                            let first = Ashes.Number.UInt.toInt(Ashes.Byte.get(bytes)(0))
                            in
                                if first >= 97
                                then first <= 122
                                else false

let addImplementationParameterName name names =
    if nameExists(name)(names)
    then names
    else name :: names

let recursive collectImplementationTypeNames typeExpression names =
    match typeExpression with
        | TypeAt(_span, inner) -> collectImplementationTypeNames(inner)(names)
        | TypeNamed(name) ->
            if isImplementationParameterName(name)
            then addImplementationParameterName(name)(names)
            else names
        | TypeApplied(_name, arguments) -> collectImplementationTypeNamesFromList(arguments)(names)
        | TypeArrow(argument, result, _capabilities, _tail) ->
            let afterArgument = collectImplementationTypeNames(argument)(names)
            in collectImplementationTypeNames(result)(afterArgument)
        | TypeTuple(elements) -> collectImplementationTypeNamesFromList(elements)(names)
        | TypeUnit -> names
and collectImplementationTypeNamesFromList expressions names =
    match expressions with
        | [] -> names
        | head :: tail -> collectImplementationTypeNamesFromList(tail)(collectImplementationTypeNames(head)(names))

let sortImplementationNames : List(Str) -> List(Str) =
    given (names) ->
        sortBy(given (left) ->
            given (right) -> compareText(left)(right) <= 0)(names)

let collectImplementationHeadNames typeArguments = sortImplementationNames(collectImplementationTypeNamesFromList(typeArguments)([]))

let recursive registerImplementationParameters names context nextTypeSymbolId =
    match names with
        | [] -> TraitImplementationParameterRegistration(context = context, nextTypeSymbolId = nextTypeSymbolId)
        | name :: tail -> registerImplementationParameters(tail)(addTypeParameter(name)(SemParameter(nextTypeSymbolId)(name))(context))(nextTypeSymbolId + 1)

let recursive firstNameNotIn names allowed =
    match names with
        | [] -> None
        | head :: tail ->
            if nameExists(head)(allowed)
            then firstNameNotIn(tail)(allowed)
            else Some(head)

let recursive resolveImplementationRequirements owner requirements headNames context environment reversed =
    match requirements with
        | [] -> TraitConstraintRegistration(constraints = reverse(reversed), error = None)
        | TraitConstraintSyntax { traitName = traitName, typeArguments = typeArguments } :: tail ->
            match resolveTraitBinding(traitName)(environment) with
                | None -> TraitConstraintRegistration(constraints = reverse(reversed), error = Some(UnknownTraitImplementationRequirement(owner)(traitName)))
                | Some(TraitInferenceDefinition { name = _name, parameterCount = expectedArity, methods = _methods, supertraits = _supertraits }) ->
                    let actualArity = syntaxListLength(typeArguments)
                    in
                        if expectedArity == actualArity
                        then
                            let requirementNames = sortImplementationNames(collectImplementationTypeNamesFromList(typeArguments)([]))
                            in
                                match firstNameNotIn(requirementNames)(headNames) with
                                    | Some(name) -> TraitConstraintRegistration(constraints = reverse(reversed), error = Some(TraitImplementationRequirementVariableEscapes(owner)(name)))
                                    | None ->
                                        match resolveConstructorParameters(typeArguments)(context)([]) with
                                            | TypeListResolutionResult { semanticTypes = arguments, error = None } -> resolveImplementationRequirements(owner)(tail)(headNames)(context)(environment)(TraitConstraint(traitName = traitName, typeArguments = arguments) :: reversed)
                                            | TypeListResolutionResult { semanticTypes = _arguments, error = Some(error) } -> TraitConstraintRegistration(constraints = reverse(reversed), error = Some(ProgramTypeResolutionError(error)))
                        else TraitConstraintRegistration(constraints = reverse(reversed), error = Some(TraitImplementationRequirementArityMismatch(traitName)(expectedArity)(actualArity)))

let recursive implementationBindingExists methodName bindings =
    match bindings with
        | [] -> false
        | TraitImplementationMethodBinding { methodName = candidateName, implementation = _implementation } :: tail ->
            if methodName == candidateName
            then true
            else implementationBindingExists(methodName)(tail)

let recursive findUnknownImplementationBinding traitName bindings environment =
    match bindings with
        | [] -> None
        | TraitImplementationMethodBinding { methodName = methodName, implementation = _implementation } :: tail ->
            match resolveTraitMethod(traitName)(methodName)(environment) with
                | None -> Some(methodName)
                | Some(_) -> findUnknownImplementationBinding(traitName)(tail)(environment)

let recursive findDuplicateImplementationBinding bindings seen =
    match bindings with
        | [] -> None
        | TraitImplementationMethodBinding { methodName = methodName, implementation = _implementation } :: tail ->
            if nameExists(methodName)(seen)
            then Some(methodName)
            else findDuplicateImplementationBinding(tail)(methodName :: seen)

let recursive findMissingImplementationBindingFrom methods bindings candidate =
    match methods with
        | [] -> candidate
        | TraitMethodInferenceDefinition { name = methodName, scheme = _scheme, defaultImplementation = defaultImplementation } :: tail ->
            if implementationBindingExists(methodName)(bindings)
            then findMissingImplementationBindingFrom(tail)(bindings)(candidate)
            else
                match defaultImplementation with
                    | Some(_) -> findMissingImplementationBindingFrom(tail)(bindings)(candidate)
                    | None ->
                        let nextCandidate =
                            match candidate with
                                | None -> Some(methodName)
                                | Some(existing) ->
                                    if compareText(methodName)(existing) < 0
                                    then Some(methodName)
                                    else candidate
                        in findMissingImplementationBindingFrom(tail)(bindings)(nextCandidate)

let findMissingImplementationBinding methods bindings = findMissingImplementationBindingFrom(methods)(bindings)(None)

let recursive findTraitHeadSubstitution parameterId substitutions =
    match substitutions with
        | [] -> None
        | (candidateId, replacement) :: tail ->
            if parameterId == candidateId
            then Some(replacement)
            else findTraitHeadSubstitution(parameterId)(tail)

let recursive traitHeadTypesOverlap left right substitutions =
    match (left, right) with
        | ([], []) -> TraitHeadOverlapResult(substitutions = substitutions, overlaps = true)
        | (leftHead :: leftTail, rightHead :: rightTail) ->
            match traitHeadTypeOverlaps(leftHead)(rightHead)(substitutions) with
                | TraitHeadOverlapResult { substitutions = nextSubstitutions, overlaps = true } -> traitHeadTypesOverlap(leftTail)(rightTail)(nextSubstitutions)
                | failure -> failure
        | _ -> TraitHeadOverlapResult(substitutions = substitutions, overlaps = false)
and traitHeadTypeOverlaps left right substitutions =
    match left with
        | SemParameter(parameterId, _name) ->
            match findTraitHeadSubstitution(parameterId)(substitutions) with
                | Some(replacement) -> traitHeadTypeOverlaps(replacement)(right)(substitutions)
                | None -> TraitHeadOverlapResult(substitutions = (parameterId, right) :: substitutions, overlaps = true)
        | _ ->
            match right with
                | SemParameter(parameterId, _name) ->
                    match findTraitHeadSubstitution(parameterId)(substitutions) with
                        | Some(replacement) -> traitHeadTypeOverlaps(left)(replacement)(substitutions)
                        | None -> TraitHeadOverlapResult(substitutions = (parameterId, left) :: substitutions, overlaps = true)
                | _ ->
                    match (left, right) with
                        | (SemInt, SemInt) -> TraitHeadOverlapResult(substitutions = substitutions, overlaps = true)
                        | (SemUInt(leftBits), SemUInt(rightBits)) -> TraitHeadOverlapResult(substitutions = substitutions, overlaps = leftBits == rightBits)
                        | (SemFloat, SemFloat) -> TraitHeadOverlapResult(substitutions = substitutions, overlaps = true)
                        | (SemBigInt, SemBigInt) -> TraitHeadOverlapResult(substitutions = substitutions, overlaps = true)
                        | (SemString, SemString) -> TraitHeadOverlapResult(substitutions = substitutions, overlaps = true)
                        | (SemRune, SemRune) -> TraitHeadOverlapResult(substitutions = substitutions, overlaps = true)
                        | (SemBytes, SemBytes) -> TraitHeadOverlapResult(substitutions = substitutions, overlaps = true)
                        | (SemBool, SemBool) -> TraitHeadOverlapResult(substitutions = substitutions, overlaps = true)
                        | (SemNever, SemNever) -> TraitHeadOverlapResult(substitutions = substitutions, overlaps = true)
                        | (SemList(leftElement), SemList(rightElement)) -> traitHeadTypeOverlaps(leftElement)(rightElement)(substitutions)
                        | (SemTuple(leftElements), SemTuple(rightElements)) -> traitHeadTypesOverlap(leftElements)(rightElements)(substitutions)
                        | (SemNamed(_leftId, leftName, leftArguments), SemNamed(_rightId, rightName, rightArguments)) ->
                            if leftName == rightName
                            then traitHeadTypesOverlap(leftArguments)(rightArguments)(substitutions)
                            else TraitHeadOverlapResult(substitutions = substitutions, overlaps = false)
                        | (SemPointer(leftPointee), SemPointer(rightPointee)) -> traitHeadTypeOverlaps(leftPointee)(rightPointee)(substitutions)
                        | (SemOpaque(leftName), SemOpaque(rightName)) -> TraitHeadOverlapResult(substitutions = substitutions, overlaps = leftName == rightName)
                        | _ -> TraitHeadOverlapResult(substitutions = substitutions, overlaps = false)

let recursive anyTraitImplementationHeadOverlaps candidateArguments implementations =
    match implementations with
        | [] -> false
        | TraitImplementationInferenceDefinition { traitName = _traitName, typeArguments = existingArguments, requirements = _requirements, methods = _methods } :: tail ->
            match traitHeadTypesOverlap(existingArguments)(candidateArguments)([]) with
                | TraitHeadOverlapResult { substitutions = _substitutions, overlaps = true } -> true
                | _ -> anyTraitImplementationHeadOverlaps(candidateArguments)(tail)

let recursive implementationMethodSubstitution quantified typeArguments reversed =
    match (quantified, typeArguments) with
        | ((variableId, _name) :: quantifiedTail, argument :: argumentTail) -> implementationMethodSubstitution(quantifiedTail)(argumentTail)((variableId, argument) :: reversed)
        | _ -> reversed

let recursive registerImplementationMethods traitName bindings typeArguments environment substitution supply reversed =
    match bindings with
        | [] -> TraitImplementationMethodRegistration(methods = reverse(reversed), substitution = substitution, supply = supply, error = None)
        | TraitImplementationMethodBinding { methodName = methodName, implementation = implementation } :: tail ->
            match resolveTraitMethod(traitName)(methodName)(environment) with
                | None -> TraitImplementationMethodRegistration(methods = reverse(reversed), substitution = substitution, supply = supply, error = Some(UnknownTraitImplementationMethod(traitName)(methodName)))
                | Some(TraitMethodInferenceDefinition { name = _name, scheme = TypeScheme { quantified = quantified, body = methodBody, constraints = _constraints }, defaultImplementation = _defaultImplementation }) ->
                    let methodSubstitution = implementationMethodSubstitution(quantified)(typeArguments)([])
                    in
                        let expectedType = applySubstitution(methodSubstitution)(methodBody)
                        in
                            match inferExpressionFrom(implementation)(environment)(substitution)(supply) with
                                | TypeInferenceResult { semanticType = implementationType, substitution = implementationSubstitution, supply = implementationSupply, constraints = _implementationConstraints, error = None } ->
                                    match unify(applySubstitution(implementationSubstitution)(expectedType))(applySubstitution(implementationSubstitution)(implementationType)) with
                                        | UnificationResult { substitution = methodUnification, error = None } ->
                                            let combined = appendProgramSubstitution(methodUnification)(implementationSubstitution)
                                            in
                                                let registered = TraitImplementationMethodInferenceDefinition(name = methodName, implementation = implementation, semanticType = applySubstitution(combined)(expectedType))
                                                in registerImplementationMethods(traitName)(tail)(typeArguments)(environment)(combined)(implementationSupply)(registered :: reversed)
                                        | UnificationResult { substitution = _methodUnification, error = Some(error) } -> TraitImplementationMethodRegistration(methods = reverse(reversed), substitution = implementationSubstitution, supply = implementationSupply, error = Some(ProgramExpressionError(InferenceUnificationError(error))))
                                | TypeInferenceResult { semanticType = _implementationType, substitution = failedSubstitution, supply = failedSupply, constraints = _implementationConstraints, error = Some(error) } -> TraitImplementationMethodRegistration(methods = reverse(reversed), substitution = failedSubstitution, supply = failedSupply, error = Some(ProgramExpressionError(error)))

let registerTraitImplementation declaration state =
    match (declaration, state) with
        | (TraitImplementationDecl { traitName = traitName, typeArguments = typeArguments, requirements = requirements, bindings = bindings }, ProgramInferenceState { environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = None }) ->
            match resolveTraitBinding(traitName)(environment) with
                | None -> ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = Some(UnknownTraitImplementation(traitName)))
                | Some(TraitInferenceDefinition { name = _name, parameterCount = expectedArity, methods = traitMethods, supertraits = _supertraits }) ->
                    let actualArity = syntaxListLength(typeArguments)
                    in
                        if expectedArity == actualArity
                        then
                            let headNames = collectImplementationHeadNames(typeArguments)
                            in
                                match registerImplementationParameters(headNames)(inferenceTypeResolutionContext(environment))(nextTypeSymbolId) with
                                    | TraitImplementationParameterRegistration { context = context, nextTypeSymbolId = implementationNextTypeSymbolId } ->
                                        match resolveConstructorParameters(typeArguments)(context)([]) with
                                            | TypeListResolutionResult { semanticTypes = resolvedTypeArguments, error = None } ->
                                                match resolveImplementationRequirements(traitName)(requirements)(headNames)(context)(environment)([]) with
                                                    | TraitConstraintRegistration { constraints = resolvedRequirements, error = None } ->
                                                        match findUnknownImplementationBinding(traitName)(bindings)(environment) with
                                                            | Some(methodName) -> ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = implementationNextTypeSymbolId, error = Some(UnknownTraitImplementationMethod(traitName)(methodName)))
                                                            | None ->
                                                                match findDuplicateImplementationBinding(bindings)([]) with
                                                                    | Some(methodName) -> ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = implementationNextTypeSymbolId, error = Some(DuplicateTraitImplementationMethod(traitName)(methodName)))
                                                                    | None ->
                                                                        match findMissingImplementationBinding(traitMethods)(bindings) with
                                                                            | Some(methodName) -> ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = implementationNextTypeSymbolId, error = Some(MissingTraitImplementationMethod(traitName)(methodName)))
                                                                            | None ->
                                                                                if anyTraitImplementationHeadOverlaps(resolvedTypeArguments)(resolveTraitImplementations(traitName)(environment))
                                                                                then ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = implementationNextTypeSymbolId, error = Some(OverlappingTraitImplementations(traitName)))
                                                                                else
                                                                                    match registerImplementationMethods(traitName)(bindings)(resolvedTypeArguments)(environment)(substitution)(supply)([]) with
                                                                                        | TraitImplementationMethodRegistration { methods = registeredMethods, substitution = implementationSubstitution, supply = implementationSupply, error = None } -> ProgramInferenceState(environment = addTraitImplementation(traitName)(resolvedTypeArguments)(resolvedRequirements)(registeredMethods)(environment), substitution = implementationSubstitution, supply = implementationSupply, nextTypeSymbolId = implementationNextTypeSymbolId, error = None)
                                                                                        | TraitImplementationMethodRegistration { methods = _registeredMethods, substitution = failedSubstitution, supply = failedSupply, error = Some(error) } -> ProgramInferenceState(environment = environment, substitution = failedSubstitution, supply = failedSupply, nextTypeSymbolId = implementationNextTypeSymbolId, error = Some(error))
                                                    | TraitConstraintRegistration { constraints = _resolvedRequirements, error = Some(error) } -> ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = implementationNextTypeSymbolId, error = Some(error))
                                            | TypeListResolutionResult { semanticTypes = _resolvedTypeArguments, error = Some(error) } -> ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = implementationNextTypeSymbolId, error = Some(ProgramTypeResolutionError(error)))
                        else ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = Some(TraitImplementationArityMismatch(traitName)(expectedArity)(actualArity)))
        | (_declaration, failedState) -> failedState

let recursive validateTraitDefaultMethods traitName methods state =
    match (methods, state) with
        | ([], _) -> state
        | (TraitMethodDecl { name = methodName, signature = _signature, defaultImplementation = None } :: tail, _) -> validateTraitDefaultMethods(traitName)(tail)(state)
        | (TraitMethodDecl { name = methodName, signature = _signature, defaultImplementation = Some(defaultImplementation) } :: tail, ProgramInferenceState { environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = None }) ->
            match resolveTraitMethod(traitName)(methodName)(environment) with
                | None -> ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = Some(UnsupportedTopLevelDeclaration("trait default method")))
                | Some(TraitMethodInferenceDefinition { name = _name, scheme = scheme, defaultImplementation = _registeredDefault }) ->
                    match instantiate(scheme)(supply) with
                        | InstantiationResult { semanticType = expectedType, constraints = _expectedConstraints, supply = expectedSupply } ->
                            match inferExpressionFrom(defaultImplementation)(environment)(substitution)(expectedSupply) with
                                | TypeInferenceResult { semanticType = implementationType, substitution = implementationSubstitution, supply = implementationSupply, constraints = _implementationConstraints, error = None } ->
                                    match unify(applySubstitution(implementationSubstitution)(expectedType))(applySubstitution(implementationSubstitution)(implementationType)) with
                                        | UnificationResult { substitution = defaultSubstitution, error = None } ->
                                            let combined = appendProgramSubstitution(defaultSubstitution)(implementationSubstitution)
                                            in validateTraitDefaultMethods(traitName)(tail)(ProgramInferenceState(environment = environment, substitution = combined, supply = implementationSupply, nextTypeSymbolId = nextTypeSymbolId, error = None))
                                        | UnificationResult { substitution = _defaultSubstitution, error = Some(error) } -> ProgramInferenceState(environment = environment, substitution = implementationSubstitution, supply = implementationSupply, nextTypeSymbolId = nextTypeSymbolId, error = Some(ProgramExpressionError(InferenceUnificationError(error))))
                                | TypeInferenceResult { semanticType = _implementationType, substitution = failedSubstitution, supply = failedSupply, constraints = _implementationConstraints, error = Some(error) } -> ProgramInferenceState(environment = environment, substitution = failedSubstitution, supply = failedSupply, nextTypeSymbolId = nextTypeSymbolId, error = Some(ProgramExpressionError(error)))
        | (_methods, failedState) -> failedState

let recursive validateTraitDefaults declarations state =
    match declarations with
        | [] -> state
        | TraitDecl { name = name, typeParameters = _parameters, supertraits = _supertraits, methods = methods } :: tail -> validateTraitDefaults(tail)(validateTraitDefaultMethods(name)(methods)(state))

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
                    | TopLevelTrait(declaration) -> registerTraitDeclaration(declaration)(state)
                    | TopLevelImplementation(declaration) -> registerTraitImplementation(declaration)(state)
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
                let declarations = collectTraitDeclarations(items)([])
                in
                    let validatedState =
                        match validateTraitDeclarations(declarations)(declarations)([]) with
                            | None -> initialState
                            | Some(error) -> ProgramInferenceState(environment = emptyTypeEnvironment(Unit), substitution = [], supply = initialTypeVariableSupply(Unit), nextTypeSymbolId = 0, error = Some(error))
                    in
                        match validateTraitDefaults(declarations)(inferTopLevelItems(items)(validatedState)) with
                            | ProgramInferenceState { environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = _nextTypeSymbolId, error = None } ->
                                match body with
                                    | None -> ProgramInferenceResult(semanticType = SemTuple([]), substitution = substitution, environment = environment, error = None)
                                    | Some(expression) ->
                                        match inferExpressionFrom(expression)(environment)(substitution)(supply) with
                                            | TypeInferenceResult { semanticType = semanticType, substitution = bodySubstitution, supply = _bodySupply, constraints = _constraints, error = None } -> ProgramInferenceResult(semanticType = semanticType, substitution = bodySubstitution, environment = environment, error = None)
                                            | TypeInferenceResult { semanticType = semanticType, substitution = bodySubstitution, supply = _bodySupply, constraints = _constraints, error = Some(error) } -> ProgramInferenceResult(semanticType = semanticType, substitution = bodySubstitution, environment = environment, error = Some(ProgramExpressionError(error)))
                            | ProgramInferenceState { environment = environment, substitution = substitution, supply = _supply, nextTypeSymbolId = _nextTypeSymbolId, error = Some(error) } -> ProgramInferenceResult(semanticType = SemNever, substitution = substitution, environment = environment, error = Some(error))
