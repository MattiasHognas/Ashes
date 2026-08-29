// Infers expression and pattern types with substitutions, constraints, and capability rows.
//
// Invariants:
// - Substitutions and fresh-variable supplies are threaded left to right through strict evaluation.
// - Let values are generalized only after their right-hand side succeeds.
// - Ambient capability rows remain part of the environment and are never independently generalized.

import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.Pattern
import AshesCompiler.Frontend.Syntax.TypeExpr
import AshesCompiler.Frontend.Syntax.TraitConstraintSyntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.Unification
import AshesCompiler.Semantics.TypeSchemes
import AshesCompiler.Semantics.TypeResolution
import Ashes.Collection.List.append
import Ashes.Collection.List.reverse
export (
    type TypeEnvironment(..),
    type ExternalFunctionInferenceDefinition(..),
    type ConstructorInferenceDefinition(..),
    type CapabilityInferenceDefinition(..),
    type CapabilityOperationInferenceDefinition(..),
    type CapabilityProviderInferenceDefinition(..),
    type CapabilityProviderOperationInferenceDefinition(..),
    type TraitInferenceDefinition(..),
    type TraitMethodInferenceDefinition(..),
    type TraitImplementationInferenceDefinition(..),
    type TraitImplementationMethodInferenceDefinition(..),
    type TypeInferenceError(..),
    type TypeInferenceResult(..),
    type TopLevelBindingInferenceResult(..),
    value emptyTypeEnvironment,
    value emptyTypeEnvironmentForPackage,
    value withInferencePackage,
    value inferencePackageId,
    value addTypeBinding,
    value addInferenceTypeDefinition,
    value addInferenceZeroCostTypeDefinition,
    value addInferenceTypeAlias,
    value addInferenceExternalType,
    value addExternalFunctionBinding,
    value resolveExternalFunctionBinding,
    value addConstructorBinding,
    value resolveConstructorBinding,
    value addCapabilityBinding,
    value resolveCapabilityBinding,
    value resolveCapabilityOperation,
    value addCapabilityProvider,
    value resolveCapabilityProvider,
    value addTraitBinding,
    value resolveTraitBinding,
    value resolveTraitMethod,
    value addTraitImplementation,
    value resolveTraitImplementations,
    value inferenceTypeResolutionContext,
    value inferenceEnvironmentSchemes,
    value applyInferenceConstraints,
    value simplifyTraitConstraints,
    value checkInferenceBindingSignature,
    value checkInferenceAnnotation,
    value inferExpressionFrom,
    value inferTopLevelBinding,
    value inferExpression,
)

type ConstructorInferenceDefinition =
    | name: Str
    | scheme: TypeScheme
    | fieldNames: List(Str)

type CapabilityOperationInferenceDefinition =
    | name: Str
    | scheme: TypeScheme
    | hasExplicitSignature: Bool

type CapabilityInferenceDefinition =
    | name: Str
    | scheme: TypeScheme
    | operations: List(CapabilityOperationInferenceDefinition)

type CapabilityProviderOperationInferenceDefinition =
    | name: Str
    | semanticType: SemanticType

type CapabilityProviderInferenceDefinition =
    | capabilityType: SemanticType
    | operations: List(CapabilityProviderOperationInferenceDefinition)

type TraitMethodInferenceDefinition =
    | name: Str
    | scheme: TypeScheme
    | defaultImplementation: Maybe(Expr)

type TraitInferenceDefinition =
    | name: Str
    | parameterCount: Int
    | parameters: List(SemanticType)
    | methods: List(TraitMethodInferenceDefinition)
    | supertraits: List(TraitConstraint)
    | provenance: DeclarationProvenance

type TraitImplementationMethodInferenceDefinition =
    | name: Str
    | implementation: Expr
    | semanticType: SemanticType

type TraitImplementationInferenceDefinition =
    | traitName: Str
    | typeArguments: List(SemanticType)
    | requirements: List(TraitConstraint)
    | methods: List(TraitImplementationMethodInferenceDefinition)

type ExternalFunctionInferenceDefinition =
    | name: Str
    | directScheme: TypeScheme
    | firstClassScheme: Maybe(TypeScheme)

type TypeEnvironment =
    | packageId: Str
    | bindings: List((Str, TypeScheme))
    | constructors: List(ConstructorInferenceDefinition)
    | capabilities: List(CapabilityInferenceDefinition)
    | traits: List(TraitInferenceDefinition)
    | traitImplementations: List(TraitImplementationInferenceDefinition)
    | providers: List(CapabilityProviderInferenceDefinition)
    | handledCapabilities: List(Str)
    | externalFunctions: List(ExternalFunctionInferenceDefinition)
    | typeResolutionContext: TypeResolutionContext

type TypeInferenceError =
    | UnknownValue(Str)
    | InferenceUnificationError(UnificationError)
    | InferenceTypeResolutionError(TypeResolutionError)
    | DuplicatePatternBinding(Str)
    | UnknownPatternConstructor(Str)
    | ConstructorPatternArityMismatch(Str)
    | UnknownRecordPatternField(Str, Str)
    | DuplicateRecordPatternField(Str)
    | UnknownRecordType(Str)
    | RecordUpdateRequiresRecord(SemanticType)
    | MissingRecordField(Str, Str)
    | UnknownRecordField(Str, Str)
    | DuplicateRecordField(Str)
    | ExpectedResultType(SemanticType)
    | ExpectedTaskType(SemanticType)
    | PerformRequiresCapabilityOperation
    | UnknownCapabilityOperation(Str, Str)
    | UnsignedCapabilityOperationRequiresSignature(Str, Str)
    | InvalidHandler(Str)
    | AmbiguousCapabilitySatisfaction(Str)
    | InconsistentOrPatternBindings
    | UnknownWrittenTraitRequirement(Str)
    | WrittenTraitRequirementArityMismatch(Str, Int, Int)
    | MissingWrittenTraitRequirement(Str)
    | UnjustifiedWrittenTraitRequirement(Str)
    | AmbiguousTraitRequirement(Str)
    | ExternalFunctionRequiresDirectCall(Str)
    | NonExhaustiveMatch(Str)
    | UnreachableMatchArm(Str)
    | ConstructorPatternsFromDifferentAdts(List(Str))
    | UnsupportedInferencePattern(Str)
    | UnsupportedInferenceExpression(Str)
    deriving {Eq, Show}

type TypeInferenceResult =
    | semanticType: SemanticType
    | substitution: List((Int, SemanticType))
    | supply: TypeVariableSupply
    | constraints: List(TraitConstraint)
    | error: Maybe(TypeInferenceError)
    deriving {Eq, Show}

type TopLevelBindingInferenceResult =
    | environment: TypeEnvironment
    | semanticType: SemanticType
    | substitution: List((Int, SemanticType))
    | supply: TypeVariableSupply
    | error: Maybe(TypeInferenceError)

type PatternInferenceResult =
    | semanticType: SemanticType
    | environment: TypeEnvironment
    | substitution: List((Int, SemanticType))
    | supply: TypeVariableSupply
    | names: List(Str)
    | error: Maybe(TypeInferenceError)

type ConstructorTypeShape =
    | parameters: List(SemanticType)
    | resultType: SemanticType

type ResultTypeShape =
    | symbolId: Int
    | errorType: SemanticType
    | successType: SemanticType

type HandlerOperationArmDefinition =
    | capabilityName: Str
    | operationName: Str
    | parameters: List(Pattern)
    | body: Expr

type HandlerArmCollection =
    | operationArms: List(HandlerOperationArmDefinition)
    | capabilityNames: List(Str)
    | returnArm: Maybe((Pattern, Expr))
    | error: Maybe(TypeInferenceError)

type HandledCapabilityDefinition =
    | name: Str
    | semanticType: SemanticType
    | operations: List(CapabilityOperationInferenceDefinition)

type HandledCapabilityPreparation =
    | capabilities: List(HandledCapabilityDefinition)
    | supply: TypeVariableSupply
    | error: Maybe(TypeInferenceError)

type HandlerOperationShape =
    | parameters: List(SemanticType)
    | resultType: SemanticType

type HandlerOperationTypePreparation =
    | semanticType: SemanticType
    | substitution: List((Int, SemanticType))
    | supply: TypeVariableSupply
    | error: Maybe(TypeInferenceError)

type HandlerParameterInference =
    | environment: TypeEnvironment
    | substitution: List((Int, SemanticType))
    | supply: TypeVariableSupply
    | error: Maybe(TypeInferenceError)

type HandlerArmInference =
    | substitution: List((Int, SemanticType))
    | supply: TypeVariableSupply
    | constraints: List(TraitConstraint)
    | error: Maybe(TypeInferenceError)

type ProviderRowResolution =
    | semanticType: SemanticType
    | error: Maybe(TypeInferenceError)

type ProviderCapabilityResolution =
    | capabilities: List(SemanticType)
    | error: Maybe(TypeInferenceError)

type BindingRequirementResolution =
    | constraints: List(TraitConstraint)
    | error: Maybe(TypeInferenceError)

let emptyTypeEnvironmentForPackage packageId =
    TypeEnvironment(packageId = packageId, bindings = [], constructors = [], capabilities = [], traits = [], traitImplementations = [], providers = [], handledCapabilities = [], externalFunctions = [], typeResolutionContext = emptyTypeResolutionContext(
        Unit
    ))

let emptyTypeEnvironment unit = emptyTypeEnvironmentForPackage("standalone")

let withInferencePackage packageId (environment: TypeEnvironment) = environment with packageId = packageId

let inferencePackageId environment =
    match environment with
        | TypeEnvironment { packageId = packageId } -> packageId

let addTypeBinding name scheme (environment: TypeEnvironment) =
    match environment with
        | TypeEnvironment { bindings = bindings } -> environment with bindings = (name, scheme) :: bindings

let addConstructorBinding name scheme fieldNames (environment: TypeEnvironment) =
    match addTypeBinding(name)(scheme)(environment) with
        | TypeEnvironment { constructors = constructors } as withBinding -> withBinding with constructors = ConstructorInferenceDefinition(name = name, scheme = scheme, fieldNames = fieldNames) :: constructors

let addCapabilityBinding name scheme operations (environment: TypeEnvironment) =
    match environment with
        | TypeEnvironment { capabilities = capabilities } -> environment with capabilities = CapabilityInferenceDefinition(name = name, scheme = scheme, operations = operations) :: capabilities

let addInferenceTypeDefinition symbolId name arity (environment: TypeEnvironment) =
    match environment with
        | TypeEnvironment { packageId = packageId, typeResolutionContext = typeResolutionContext } ->
            environment with typeResolutionContext = addTypeDefinitionWithProvenance(
                symbolId,
                name,
                arity,
                DeclarationProvenance(packageId = packageId),
                typeResolutionContext
            )

let addInferenceZeroCostTypeDefinition symbolId name parameterIds representation (environment: TypeEnvironment) =
    match environment with
        | TypeEnvironment { packageId = packageId, typeResolutionContext = typeResolutionContext } ->
            environment with typeResolutionContext = addZeroCostTypeDefinitionWithProvenance(
                symbolId,
                name,
                parameterIds,
                representation,
                DeclarationProvenance(packageId = packageId),
                typeResolutionContext
            )

let addInferenceTypeAlias name parameterIds target (environment: TypeEnvironment) =
    match environment with
        | TypeEnvironment { typeResolutionContext = typeResolutionContext } ->
            environment with typeResolutionContext = addTypeAliasDefinition(
                name,
                parameterIds,
                target,
                typeResolutionContext
            )

let addInferenceExternalType name destructor (environment: TypeEnvironment) =
    match environment with
        | TypeEnvironment { typeResolutionContext = typeResolutionContext } ->
            environment with typeResolutionContext = addExternalTypeDefinition(
                name,
                destructor,
                typeResolutionContext
            )

let addExternalFunctionBinding name directScheme firstClassScheme (environment: TypeEnvironment) =
    (let withFirstClass =
        match firstClassScheme with
            | None -> environment
            | Some(scheme) -> addTypeBinding(name)(scheme)(environment)
    in
        match withFirstClass with
            | TypeEnvironment { externalFunctions = externalFunctions } -> withFirstClass with externalFunctions = ExternalFunctionInferenceDefinition(name = name, directScheme = directScheme, firstClassScheme = firstClassScheme) :: externalFunctions)

let recursive findExternalFunctionBinding name definitions =
    match definitions with
        | [] -> None
        | ExternalFunctionInferenceDefinition { name = candidateName, directScheme = directScheme, firstClassScheme = firstClassScheme } :: tail ->
            if name == candidateName
            then
                Some(
                    ExternalFunctionInferenceDefinition(name = candidateName, directScheme = directScheme, firstClassScheme = firstClassScheme)
                )
            else findExternalFunctionBinding(name)(tail)

let resolveExternalFunctionBinding name environment =
    match environment with
        | TypeEnvironment { externalFunctions = externalFunctions } -> findExternalFunctionBinding(name)(externalFunctions)

let recursive findTypeBinding name bindings =
    match bindings with
        | [] -> None
        | (candidateName, scheme) :: tail ->
            if name == candidateName
            then Some(scheme)
            else findTypeBinding(name)(tail)

let resolveTypeBinding name environment =
    match environment with
        | TypeEnvironment { bindings = bindings, constructors = _constructors, capabilities = _capabilities, traits = _traits, providers = _providers, handledCapabilities = _handledCapabilities, typeResolutionContext = _typeResolutionContext } ->
            findTypeBinding(
                name,
                bindings
            )

let recursive findConstructorBinding : Str -> List(ConstructorInferenceDefinition) -> Maybe(ConstructorInferenceDefinition) =
    given (name) ->
        given (constructors) ->
            match constructors with
                | [] -> None
                | ConstructorInferenceDefinition { name = candidateName, scheme = scheme, fieldNames = fieldNames } :: tail ->
                    if name == candidateName
                    then
                        Some(
                            ConstructorInferenceDefinition(name = candidateName, scheme = scheme, fieldNames = fieldNames)
                        )
                    else findConstructorBinding(name)(tail)

let resolveConstructorBinding name environment =
    match environment with
        | TypeEnvironment { bindings = _bindings, constructors = constructors, capabilities = _capabilities, traits = _traits, providers = _providers, handledCapabilities = _handledCapabilities, typeResolutionContext = _typeResolutionContext } ->
            findConstructorBinding(
                name,
                constructors
            )

let recursive findCapabilityBinding : Str -> List(CapabilityInferenceDefinition) -> Maybe(CapabilityInferenceDefinition) =
    given (name) ->
        given (capabilities) ->
            match capabilities with
                | [] -> None
                | CapabilityInferenceDefinition { name = candidateName, scheme = scheme, operations = operations } :: tail ->
                    if name == candidateName
                    then
                        Some(
                            CapabilityInferenceDefinition(name = candidateName, scheme = scheme, operations = operations)
                        )
                    else findCapabilityBinding(name)(tail)

let resolveCapabilityBinding name environment =
    match environment with
        | TypeEnvironment { bindings = _bindings, constructors = _constructors, capabilities = capabilities, traits = _traits, providers = _providers, handledCapabilities = _handledCapabilities, typeResolutionContext = _typeResolutionContext } ->
            findCapabilityBinding(
                name,
                capabilities
            )

let recursive findCapabilityOperation name operations =
    match operations with
        | [] -> None
        | CapabilityOperationInferenceDefinition { name = candidateName, scheme = scheme, hasExplicitSignature = hasExplicitSignature } :: tail ->
            if name == candidateName
            then
                Some(
                    CapabilityOperationInferenceDefinition(name = candidateName, scheme = scheme, hasExplicitSignature = hasExplicitSignature)
                )
            else findCapabilityOperation(name)(tail)

let resolveCapabilityOperation capabilityName operationName environment =
    match resolveCapabilityBinding(capabilityName)(environment) with
        | None -> None
        | Some(CapabilityInferenceDefinition { name = _name, scheme = _scheme, operations = operations }) ->
            findCapabilityOperation(
                operationName,
                operations
            )

let addTraitBinding name parameterCount parameters methods supertraits (environment: TypeEnvironment) =
    match environment with
        | TypeEnvironment { packageId = packageId, traits = traits } ->
            environment with traits = TraitInferenceDefinition(name = name, parameterCount = parameterCount, parameters = parameters, methods = methods, supertraits = canonicalizeTraitConstraints(
                supertraits
            ), provenance = DeclarationProvenance(packageId = packageId)) :: traits

let recursive findTraitBinding name traits =
    match traits with
        | [] -> None
        | TraitInferenceDefinition { name = candidateName, parameterCount = parameterCount, parameters = parameters, methods = methods, supertraits = supertraits, provenance = provenance } :: tail ->
            if name == candidateName
            then
                Some(
                    TraitInferenceDefinition(name = candidateName, parameterCount = parameterCount, parameters = parameters, methods = methods, supertraits = supertraits, provenance = provenance)
                )
            else findTraitBinding(name)(tail)

let resolveTraitBinding name environment =
    match environment with
        | TypeEnvironment { bindings = _bindings, constructors = _constructors, capabilities = _capabilities, traits = traits, providers = _providers, handledCapabilities = _handledCapabilities, typeResolutionContext = _typeResolutionContext } ->
            findTraitBinding(
                name,
                traits
            )

let recursive findTraitMethod name methods =
    match methods with
        | [] -> None
        | TraitMethodInferenceDefinition { name = candidateName, scheme = scheme, defaultImplementation = defaultImplementation } :: tail ->
            if name == candidateName
            then
                Some(
                    TraitMethodInferenceDefinition(name = candidateName, scheme = scheme, defaultImplementation = defaultImplementation)
                )
            else findTraitMethod(name)(tail)

let resolveTraitMethod traitName methodName environment =
    match resolveTraitBinding(traitName)(environment) with
        | None -> None
        | Some(TraitInferenceDefinition { name = _name, parameterCount = _parameterCount, methods = methods, supertraits = _supertraits }) ->
            findTraitMethod(
                methodName,
                methods
            )

let addTraitImplementation traitName typeArguments requirements methods (environment: TypeEnvironment) =
    match environment with
        | TypeEnvironment { traitImplementations = traitImplementations } ->
            let definition = TraitImplementationInferenceDefinition(traitName = traitName, typeArguments = typeArguments, requirements = requirements, methods = methods)
            in environment with traitImplementations = definition :: traitImplementations

let recursive filterTraitImplementations traitName implementations =
    match implementations with
        | [] -> []
        | TraitImplementationInferenceDefinition { traitName = candidateName, typeArguments = typeArguments, requirements = requirements, methods = methods } :: tail ->
            let rest = filterTraitImplementations(traitName)(tail)
            in
                if traitName == candidateName
                then TraitImplementationInferenceDefinition(traitName = candidateName, typeArguments = typeArguments, requirements = requirements, methods = methods) :: rest
                else rest

let resolveTraitImplementations traitName environment =
    match environment with
        | TypeEnvironment { traitImplementations = implementations } ->
            filterTraitImplementations(
                traitName,
                implementations
            )

let addCapabilityProvider capabilityType operations (environment: TypeEnvironment) =
    match environment with
        | TypeEnvironment { providers = providers } -> environment with providers = CapabilityProviderInferenceDefinition(capabilityType = capabilityType, operations = operations) :: providers

let recursive findCapabilityProvider capabilityType providers =
    match providers with
        | [] -> None
        | CapabilityProviderInferenceDefinition { capabilityType = candidateType, operations = operations } :: tail ->
            if capabilityType == candidateType
            then Some(CapabilityProviderInferenceDefinition(capabilityType = candidateType, operations = operations))
            else findCapabilityProvider(capabilityType)(tail)

let resolveCapabilityProvider capabilityType environment =
    match environment with
        | TypeEnvironment { bindings = _bindings, constructors = _constructors, capabilities = _capabilities, traits = _traits, providers = providers, handledCapabilities = _handledCapabilities, typeResolutionContext = _typeResolutionContext } ->
            findCapabilityProvider(
                capabilityType,
                providers
            )

let recursive stringExists name values =
    match values with
        | [] -> false
        | head :: tail ->
            if name == head
            then true
            else stringExists(name)(tail)

let recursive appendHandledCapabilities names existing =
    match names with
        | [] -> existing
        | head :: tail -> head :: appendHandledCapabilities(tail)(existing)

let withHandledCapabilities names (environment: TypeEnvironment) =
    match environment with
        | TypeEnvironment { handledCapabilities = handledCapabilities } ->
            environment with handledCapabilities = appendHandledCapabilities(
                names,
                handledCapabilities
            )

let capabilityIsHandled name environment =
    match environment with
        | TypeEnvironment { bindings = _bindings, constructors = _constructors, capabilities = _capabilities, traits = _traits, providers = _providers, handledCapabilities = handledCapabilities, typeResolutionContext = _typeResolutionContext } ->
            stringExists(
                name,
                handledCapabilities
            )

let recursive handlerOperationExists capabilityName operationName arms =
    match arms with
        | [] -> false
        | HandlerOperationArmDefinition { capabilityName = candidateCapability, operationName = candidateOperation, parameters = _parameters, body = _body } :: tail ->
            if capabilityName == candidateCapability
            then
                if operationName == candidateOperation
                then true
                else handlerOperationExists(capabilityName)(operationName)(tail)
            else handlerOperationExists(capabilityName)(operationName)(tail)

let recursive collectHandlerArms arms environment reversedOperations reversedCapabilities returnArm =
    match arms with
        | [] ->
            match reversedOperations with
                | [] ->
                    HandlerArmCollection(operationArms = [], capabilityNames = [], returnArm = returnArm, error = Some(
                        InvalidHandler("a handler needs at least one operation arm")
                    ))
                | _ ->
                    HandlerArmCollection(operationArms = reverse(reversedOperations), capabilityNames = reverse(
                        reversedCapabilities
                    ), returnArm = returnArm, error = None)
        | (None, _operationName, parameters, body) :: tail ->
            match (returnArm, parameters) with
                | (Some(_), _) ->
                    HandlerArmCollection(operationArms = reverse(
                        reversedOperations
                    ), capabilityNames = reverse(
                        reversedCapabilities
                    ), returnArm = returnArm, error = Some(
                        InvalidHandler("duplicate return arm")
                    ))
                | (None, parameter :: []) ->
                    collectHandlerArms(
                        tail,
                        environment,
                        reversedOperations,
                        reversedCapabilities,
                        Some((parameter, body))
                    )
                | _ ->
                    HandlerArmCollection(operationArms = reverse(reversedOperations), capabilityNames = reverse(
                        reversedCapabilities
                    ), returnArm = returnArm, error = Some(
                        InvalidHandler("the return arm takes exactly one parameter")
                    ))
        | (Some(capabilityName), operationName, parameters, body) :: tail ->
            match resolveCapabilityOperation(capabilityName)(operationName)(environment) with
                | None ->
                    HandlerArmCollection(operationArms = reverse(reversedOperations), capabilityNames = reverse(
                        reversedCapabilities
                    ), returnArm = returnArm, error = Some(
                        InvalidHandler("unknown operation " + capabilityName + "." + operationName)
                    ))
                | Some(_) ->
                    if handlerOperationExists(capabilityName)(operationName)(reversedOperations)
                    then
                        HandlerArmCollection(operationArms = reverse(reversedOperations), capabilityNames = reverse(
                            reversedCapabilities
                        ), returnArm = returnArm, error = Some(
                            InvalidHandler("duplicate operation arm " + capabilityName + "." + operationName)
                        ))
                    else
                        let operationArm = HandlerOperationArmDefinition(capabilityName = capabilityName, operationName = operationName, parameters = parameters, body = body)
                        in
                            if stringExists(capabilityName)(reversedCapabilities)
                            then
                                collectHandlerArms(
                                    tail,
                                    environment,
                                    operationArm :: reversedOperations,
                                    reversedCapabilities,
                                    returnArm
                                )
                            else
                                collectHandlerArms(
                                    tail,
                                    environment,
                                    operationArm :: reversedOperations,
                                    capabilityName :: reversedCapabilities,
                                    returnArm
                                )

let recursive findMissingHandlerOperation capabilityNames environment arms =
    match capabilityNames with
        | [] -> None
        | capabilityName :: tail ->
            match resolveCapabilityBinding(capabilityName)(environment) with
                | None -> Some(capabilityName)
                | Some(CapabilityInferenceDefinition { name = _name, scheme = _scheme, operations = operations }) ->
                    let recursive missingOperation definitions =
                        match definitions with
                            | [] -> None
                            | CapabilityOperationInferenceDefinition { name = operationName, scheme = _operationScheme, hasExplicitSignature = _hasExplicitSignature } :: rest ->
                                if handlerOperationExists(capabilityName)(operationName)(arms)
                                then missingOperation(rest)
                                else Some(capabilityName + "." + operationName)
                    in
                        match missingOperation(operations) with
                            | Some(missing) -> Some(missing)
                            | None -> findMissingHandlerOperation(tail)(environment)(arms)

let recursive prepareHandledCapabilities names environment supply reversed =
    match names with
        | [] -> HandledCapabilityPreparation(capabilities = reverse(reversed), supply = supply, error = None)
        | name :: tail ->
            match resolveCapabilityBinding(name)(environment) with
                | None ->
                    HandledCapabilityPreparation(capabilities = reverse(reversed), supply = supply, error = Some(
                        InvalidHandler("unknown capability " + name)
                    ))
                | Some(CapabilityInferenceDefinition { name = _name, scheme = scheme, operations = operations }) ->
                    match instantiate(scheme)(supply) with
                        | InstantiationResult { semanticType = capabilityType, constraints = _constraints, supply = nextSupply } ->
                            let definition = HandledCapabilityDefinition(name = name, semanticType = capabilityType, operations = operations)
                            in prepareHandledCapabilities(tail)(environment)(nextSupply)(definition :: reversed)

let recursive findHandledCapability name capabilities =
    match capabilities with
        | [] -> None
        | HandledCapabilityDefinition { name = candidateName, semanticType = semanticType, operations = operations } :: tail ->
            if name == candidateName
            then
                Some(
                    HandledCapabilityDefinition(name = candidateName, semanticType = semanticType, operations = operations)
                )
            else findHandledCapability(name)(tail)

let recursive handledCapabilityTypes capabilities =
    match capabilities with
        | [] -> []
        | HandledCapabilityDefinition { name = _name, semanticType = semanticType, operations = _operations } :: tail ->
            semanticType :: handledCapabilityTypes(
                tail
            )

let recursive operationCapabilityFromType capabilityName semanticType =
    match semanticType with
        | SemFunction(_argument, result, row) ->
            match result with
                | SemFunction(_, _, _) -> operationCapabilityFromType(capabilityName)(result)
                | _ ->
                    match row with
                        | None -> None
                        | Some(SemRow(capabilities, _tail)) ->
                            let recursive findCapability values =
                                match values with
                                    | [] -> None
                                    | SemCapability(name, arguments) :: rest ->
                                        if name == capabilityName
                                        then Some(SemCapability(name)(arguments))
                                        else findCapability(rest)
                                    | _ :: rest -> findCapability(rest)
                            in findCapability(capabilities)
                        | Some(_) -> None
        | _ -> None

let recursive splitHandlerOperationType remaining semanticType reversed =
    match remaining with
        | [] ->
            match semanticType with
                | SemFunction(_, _, _) -> None
                | _ -> Some(HandlerOperationShape(parameters = reverse(reversed), resultType = semanticType))
        | _ :: tail ->
            match semanticType with
                | SemFunction(argument, result, _row) -> splitHandlerOperationType(tail)(result)(argument :: reversed)
                | _ -> None

let recursive buildUnsignedOperationType parameters capabilityType supply reversed =
    match parameters with
        | [] ->
            match freshTypeVariable(supply) with
                | (resultType, resultSupply) ->
                    let recursive wrap arguments body =
                        match arguments with
                            | [] -> body
                            | argument :: tail ->
                                match tail with
                                    | [] -> SemFunction(argument)(body)(Some(SemRow([capabilityType])(None)))
                                    | _ -> SemFunction(argument)(wrap(tail)(body))(None)
                    in
                        HandlerOperationTypePreparation(semanticType = wrap(
                            reverse(reversed),
                            resultType
                        ), substitution = [], supply = resultSupply, error = None)
        | _ :: tail ->
            match freshTypeVariable(supply) with
                | (parameterType, nextSupply) ->
                    buildUnsignedOperationType(
                        tail,
                        capabilityType,
                        nextSupply,
                        parameterType :: reversed
                    )

let inferenceTypeResolutionContext environment =
    match environment with
        | TypeEnvironment { bindings = _bindings, constructors = _constructors, capabilities = _capabilities, traits = _traits, providers = _providers, handledCapabilities = _handledCapabilities, typeResolutionContext = typeResolutionContext } -> typeResolutionContext

let inferenceSuccess semanticType substitution supply = TypeInferenceResult(semanticType = semanticType, substitution = substitution, supply = supply, constraints = [], error = None)

let inferenceFailure semanticType substitution supply error =
    TypeInferenceResult(semanticType = semanticType, substitution = substitution, supply = supply, constraints = [], error = Some(
        error
    ))

let recursive appendConstraints left right =
    match left with
        | [] -> right
        | head :: tail -> head :: appendConstraints(tail)(right)

let addConstraints additional result =
    match result with
        | TypeInferenceResult { semanticType = semanticType, substitution = substitution, supply = supply, constraints = constraints, error = error } ->
            TypeInferenceResult(semanticType = semanticType, substitution = substitution, supply = supply, constraints = appendConstraints(
                additional,
                constraints
            ), error = error)

let recursive applyInferenceConstraints substitution constraints =
    match constraints with
        | [] -> []
        | TraitConstraint { traitName = traitName, typeArguments = typeArguments } :: tail ->
            let recursive applyArguments arguments =
                match arguments with
                    | [] -> []
                    | head :: rest ->
                        let resolvedHead = applySubstitution(substitution)(head)
                        in
                            let resolvedRest = applyArguments(rest)
                            in resolvedHead :: resolvedRest
            in
                let resolvedArguments = applyArguments(typeArguments)
                in
                    let resolvedTail = applyInferenceConstraints(substitution)(tail)
                    in TraitConstraint(traitName = traitName, typeArguments = resolvedArguments) :: resolvedTail

let recursive traitParameterSubstitution parameters arguments reversed =
    match (parameters, arguments) with
        | ([], []) -> reversed
        | (SemVariable(variableId) :: parameterTail, argument :: argumentTail) ->
            traitParameterSubstitution(
                parameterTail,
                argumentTail,
                (variableId, argument) :: reversed
            )
        | (_parameter :: parameterTail, _argument :: argumentTail) ->
            traitParameterSubstitution(
                parameterTail,
                argumentTail,
                reversed
            )
        | _ -> reversed

let directSupertraitConstraints constraint environment =
    match constraint with
        | TraitConstraint { traitName = traitName, typeArguments = typeArguments } ->
            match resolveTraitBinding(traitName)(environment) with
                | None -> []
                | Some(TraitInferenceDefinition { name = _name, parameterCount = _parameterCount, parameters = parameters, methods = _methods, supertraits = supertraits }) ->
                    let substitution = traitParameterSubstitution(parameters)(typeArguments)([])
                    in canonicalizeTraitConstraints(applyInferenceConstraints(substitution)(supertraits))

let recursive traitConstraintImpliesFrom pending targetKey environment visited =
    match pending with
        | [] -> false
        | head :: tail ->
            let key = traitConstraintStableKey(head)
            in
                if stringExists(key)(visited)
                then traitConstraintImpliesFrom(tail)(targetKey)(environment)(visited)
                else
                    if key == targetKey
                    then true
                    else
                        traitConstraintImpliesFrom(
                            appendConstraints(tail)(directSupertraitConstraints(head)(environment)),
                            targetKey,
                            environment,
                            key :: visited
                        )

let traitConstraintImplies stronger target environment =
    traitConstraintImpliesFrom(
        directSupertraitConstraints(stronger)(environment),
        traitConstraintStableKey(target),
        environment,
        []
    )

let recursive anyTraitConstraintImplies candidate constraints environment =
    match constraints with
        | [] -> false
        | stronger :: tail ->
            if traitConstraintStableKey(stronger) == traitConstraintStableKey(candidate)
            then anyTraitConstraintImplies(candidate)(tail)(environment)
            else
                if traitConstraintImplies(stronger)(candidate)(environment)
                then true
                else anyTraitConstraintImplies(candidate)(tail)(environment)

let recursive removeImpliedTraitConstraints remaining allConstraints environment =
    match remaining with
        | [] -> []
        | head :: tail ->
            if anyTraitConstraintImplies(head)(allConstraints)(environment)
            then removeImpliedTraitConstraints(tail)(allConstraints)(environment)
            else head :: removeImpliedTraitConstraints(tail)(allConstraints)(environment)

let simplifyTraitConstraints environment constraints =
    (let canonical = canonicalizeTraitConstraints(constraints)
    in canonicalizeTraitConstraints(removeImpliedTraitConstraints(canonical)(canonical)(environment)))

let recursive bindingRequirementCount values =
    match values with
        | [] -> 0
        | _head :: tail -> 1 + bindingRequirementCount(tail)

let recursive prepareBindingRequirementArguments arguments context supply =
    match arguments with
        | [] -> TypeResolutionPreparationResult(context = context, supply = supply)
        | head :: tail ->
            match prepareTypeResolutionContext(head)(context)(supply) with
                | TypeResolutionPreparationResult { context = headContext, supply = headSupply } ->
                    prepareBindingRequirementArguments(
                        tail,
                        headContext,
                        headSupply
                    )

let recursive prepareBindingRequirements requirements context supply =
    match requirements with
        | [] -> TypeResolutionPreparationResult(context = context, supply = supply)
        | TraitConstraintSyntax { traitName = _traitName, typeArguments = typeArguments } :: tail ->
            match prepareBindingRequirementArguments(typeArguments)(context)(supply) with
                | TypeResolutionPreparationResult { context = argumentContext, supply = argumentSupply } ->
                    prepareBindingRequirements(
                        tail,
                        argumentContext,
                        argumentSupply
                    )

let prepareBindingSignatureContext annotation requirements environment supply =
    (let baseContext = inferenceTypeResolutionContext(environment)
    in
        let annotationPreparation =
            match annotation with
                | None -> TypeResolutionPreparationResult(context = baseContext, supply = supply)
                | Some(typeExpression) -> prepareTypeResolutionContext(typeExpression)(baseContext)(supply)
        in
            match annotationPreparation with
                | TypeResolutionPreparationResult { context = annotationContext, supply = annotationSupply } ->
                    prepareBindingRequirements(
                        requirements,
                        annotationContext,
                        annotationSupply
                    ))

let recursive resolveBindingRequirementTypes arguments context reversed =
    match arguments with
        | [] -> (reverse(reversed), None)
        | head :: tail ->
            match resolveTypeExpression(head)(context) with
                | TypeResolutionResult { semanticType = semanticType, error = None } ->
                    resolveBindingRequirementTypes(
                        tail,
                        context,
                        semanticType :: reversed
                    )
                | TypeResolutionResult { semanticType = _semanticType, error = Some(error) } ->
                    ([], Some(
                        InferenceTypeResolutionError(error)
                    ))

let recursive resolveBindingRequirements requirements context environment reversed =
    match requirements with
        | [] -> BindingRequirementResolution(constraints = reverse(reversed), error = None)
        | TraitConstraintSyntax { traitName = traitName, typeArguments = typeArguments } :: tail ->
            match resolveTraitBinding(traitName)(environment) with
                | None ->
                    BindingRequirementResolution(constraints = reverse(reversed), error = Some(
                        UnknownWrittenTraitRequirement(traitName)
                    ))
                | Some(TraitInferenceDefinition { name = _name, parameterCount = parameterCount, methods = _methods, supertraits = _supertraits }) ->
                    let actualCount = bindingRequirementCount(typeArguments)
                    in
                        if parameterCount == actualCount
                        then
                            match resolveBindingRequirementTypes(typeArguments)(context)([]) with
                                | (resolvedArguments, None) ->
                                    resolveBindingRequirements(
                                        tail,
                                        context,
                                        environment,
                                        TraitConstraint(traitName = traitName, typeArguments = resolvedArguments) :: reversed
                                    )
                                | (_resolvedArguments, Some(error)) ->
                                    BindingRequirementResolution(constraints = reverse(
                                        reversed
                                    ), error = Some(error))
                        else
                            BindingRequirementResolution(constraints = reverse(reversed), error = Some(
                                WrittenTraitRequirementArityMismatch(traitName)(parameterCount)(actualCount)
                            ))

let recursive integerExists value values =
    match values with
        | [] -> false
        | head :: tail ->
            if value == head
            then true
            else integerExists(value)(tail)

let recursive firstAmbiguousRequirementVariable variables bodyVariables environmentVariables =
    match variables with
        | [] -> false
        | head :: tail ->
            if integerExists(head)(bodyVariables)
            then firstAmbiguousRequirementVariable(tail)(bodyVariables)(environmentVariables)
            else
                if integerExists(head)(environmentVariables)
                then firstAmbiguousRequirementVariable(tail)(bodyVariables)(environmentVariables)
                else true

let recursive constraintWithKeyExists key constraints =
    match constraints with
        | [] -> false
        | head :: tail ->
            if traitConstraintStableKey(head) == key
            then true
            else constraintWithKeyExists(key)(tail)

let recursive firstConstraintMissingFrom candidates available =
    match candidates with
        | [] -> None
        | TraitConstraint { traitName = traitName, typeArguments = typeArguments } :: tail ->
            let candidate = TraitConstraint(traitName = traitName, typeArguments = typeArguments)
            in
                if constraintWithKeyExists(traitConstraintStableKey(candidate))(available)
                then firstConstraintMissingFrom(tail)(available)
                else Some(traitName)

let recursive appendSubstitution left right =
    match left with
        | [] -> right
        | head :: tail -> head :: appendSubstitution(tail)(right)

let patternSuccess semanticType environment substitution supply names = PatternInferenceResult(semanticType = semanticType, environment = environment, substitution = substitution, supply = supply, names = names, error = None)

let patternFailure semanticType environment substitution supply names error =
    PatternInferenceResult(semanticType = semanticType, environment = environment, substitution = substitution, supply = supply, names = names, error = Some(
        error
    ))

let mergePatternUnification currentSubstitution result supply fallbackType environment names =
    match result with
        | UnificationResult { substitution = unificationSubstitution, error = None } ->
            let combined = appendSubstitution(unificationSubstitution)(currentSubstitution)
            in patternSuccess(applySubstitution(combined)(fallbackType))(environment)(combined)(supply)(names)
        | UnificationResult { substitution = _unificationSubstitution, error = Some(error) } ->
            patternFailure(
                fallbackType,
                environment,
                currentSubstitution,
                supply,
                names,
                InferenceUnificationError(error)
            )

let recursive patternNameExists : Str -> List(Str) -> Bool =
    given (name) ->
        given (names) ->
            match names with
                | [] -> false
                | head :: tail ->
                    if name == head
                    then true
                    else patternNameExists(name)(tail)

// The parser writes a bare `None` or `NoVal` as a variable pattern; only the constructor table
// tells it apart from a binder, exactly as stage 0 resolves the name at this point.
let isNullaryConstructorName (name: Str) environment =
    match resolveConstructorBinding(name)(environment) with
        | Some(ConstructorInferenceDefinition { scheme = TypeScheme { body = SemFunction(_, _, _) } }) -> false
        | Some(_) -> true
        | None -> false

let recursive splitConstructorType semanticType reversedParameters =
    match semanticType with
        | SemFunction(parameter, result, None) -> splitConstructorType(result)(parameter :: reversedParameters)
        | _ -> ConstructorTypeShape(parameters = reverse(reversedParameters), resultType = semanticType)

// Match coverage. The patterns of every arm are examined once the arms are typed, against the
// scrutinee type as far as it is resolved: a match over an ADT must name every constructor or end
// in a catch-all, a list match needs both `[]` and a cons, a bool match both literals, and any
// deeper hole is found by a per-field-position search that reports the first missing pattern it
// can name. The search is a deliberate under-approximation of what is missing (each field
// position is checked on its own), which is exactly right for a diagnostic that must never report
// a false "Missing case". Unreachable arms (after a catch-all, a repeated literal, constructor, or
// composite pattern) and constructor patterns from two ADTs are reported before coverage.
let recursive coverageUnwrap pattern =
    match pattern with
        | PatternAt(_span, inner) -> coverageUnwrap(inner)
        | other -> other

let recursive coverageContainsText (name: Str) (names: List(Str)) =
    match names with
        | [] -> false
        | candidate :: rest ->
            if candidate == name
            then true
            else coverageContainsText(name)(rest)

let recursive coverageContainsInt (value: Int) (values: List(Int)) =
    match values with
        | [] -> false
        | candidate :: rest ->
            if candidate == value
            then true
            else coverageContainsInt(value)(rest)

let recursive coverageLength items (acc: Int) =
    match items with
        | [] -> acc
        | _ :: rest -> coverageLength(rest)(acc + 1)

let recursive coverageWildcards (count: Int) acc =
    if count <= 0
    then acc
    else coverageWildcards(count - 1)(PatternWildcard :: acc)

let recursive coverageReplaceAt patterns (index: Int) replacement acc =
    match patterns with
        | [] -> reverse(acc)
        | pattern :: rest ->
            if index == 0
            then coverageReplaceAt(rest)(index - 1)(replacement)(replacement :: acc)
            else coverageReplaceAt(rest)(index - 1)(replacement)(pattern :: acc)

let recursive coverageNth items (index: Int) =
    match items with
        | [] -> None
        | item :: rest ->
            if index == 0
            then Some(item)
            else coverageNth(rest)(index - 1)

let environmentConstructors environment =
    match environment with
        | TypeEnvironment { constructors = constructors } -> constructors

// (ADT name, arity) of a constructor.
let coverageConstructorShape (name: Str) environment =
    match resolveConstructorBinding(name)(environment) with
        | Some(ConstructorInferenceDefinition { scheme = TypeScheme { body = body } }) ->
            match splitConstructorType(body)([]) with
                | ConstructorTypeShape { parameters = parameters, resultType = SemNamed(_id, adtName, _arguments) } -> Some((adtName, coverageLength(parameters)(0)))
                | _ -> None
        | None -> None

// The constructor a pattern names, when it is a constructor pattern (a bare nullary name included).
let coverageConstructorName pattern environment =
    match coverageUnwrap(pattern) with
        | PatternConstructor(name, _patterns) ->
            match resolveConstructorBinding(name)(environment) with
                | Some(_) -> Some(name)
                | None -> None
        | PatternVar(name) ->
            if isNullaryConstructorName(name)(environment)
            then Some(name)
            else None
        | _ -> None

// Every constructor of an ADT, in declaration order, as (name, arity).
let recursive coverageAdtConstructors (definitions: List(ConstructorInferenceDefinition)) (adtName: Str) acc =
    match definitions with
        | [] -> reverse(acc)
        | ConstructorInferenceDefinition { name = name, scheme = TypeScheme { body = body } } :: rest ->
            match splitConstructorType(body)([]) with
                | ConstructorTypeShape { parameters = parameters, resultType = SemNamed(_id, candidate, _arguments) } ->
                    if candidate == adtName
                    then coverageAdtConstructors(rest)(adtName)((name, coverageLength(parameters)(0)) :: acc)
                    else coverageAdtConstructors(rest)(adtName)(acc)
                | _ -> coverageAdtConstructors(rest)(adtName)(acc)

let recursive coverageIsCatchAll environment pattern =
    match coverageUnwrap(pattern) with
        | PatternWildcard -> true
        | PatternVar(name) -> !isNullaryConstructorName(name)(environment)
        | PatternTuple(elements) -> coverageAllCatchAll(environment)(elements)
        | PatternAs(inner, _name) -> coverageIsCatchAll(environment)(inner)
        | PatternOr(alternatives) -> coverageAnyCatchAll(environment)(alternatives)
        | _ -> false
and coverageAllCatchAll environment patterns =
    match patterns with
        | [] -> true
        | pattern :: rest ->
            if coverageIsCatchAll(environment)(pattern)
            then coverageAllCatchAll(environment)(rest)
            else false
and coverageAnyCatchAll environment patterns =
    match patterns with
        | [] -> false
        | pattern :: rest ->
            if coverageIsCatchAll(environment)(pattern)
            then true
            else coverageAnyCatchAll(environment)(rest)

let coverageHexDigit (digit: Int) =
    match digit with
        | 10 -> "A"
        | 11 -> "B"
        | 12 -> "C"
        | 13 -> "D"
        | 14 -> "E"
        | 15 -> "F"
        | other -> Ashes.Text.fromInt(other)

let recursive coverageHexDigits (value: Int) (acc: Str) =
    if value == 0
    then acc
    else coverageHexDigits(value / 16)(coverageHexDigit(value % 16) + acc)

let coverageHex (value: Int) =
    if value == 0
    then "0"
    else coverageHexDigits(value)("")

let recursive coverageFormatPattern pattern =
    match pattern with
        | PatternAt(_span, inner) -> coverageFormatPattern(inner)
        | PatternEmptyList -> "[]"
        | PatternWildcard -> "_"
        | PatternVar(name) -> name
        | PatternCons(head, tail) -> coverageFormatPattern(head) + " :: " + coverageFormatPattern(tail)
        | PatternTuple(elements) -> "(" + coverageJoinPatterns(elements)("") + ")"
        | PatternConstructor(name, []) -> name
        | PatternConstructor(name, patterns) -> name + "(" + coverageJoinPatterns(patterns)("") + ")"
        | PatternInt(value) -> Ashes.Text.fromInt(value)
        | PatternString(value) -> "\"" + value + "\""
        | PatternRune(value) -> "U+" + coverageHex(value)
        | PatternBool(true) -> "true"
        | PatternBool(false) -> "false"
        | _ -> "_"
and coverageJoinPatterns patterns (acc: Str) =
    match patterns with
        | [] -> acc
        | pattern :: rest ->
            if acc == ""
            then coverageJoinPatterns(rest)(coverageFormatPattern(pattern))
            else coverageJoinPatterns(rest)(acc + ", " + coverageFormatPattern(pattern))

let recursive coverageAnyEmptyList patterns =
    match patterns with
        | [] -> false
        | pattern :: rest ->
            match coverageUnwrap(pattern) with
                | PatternEmptyList -> true
                | _ -> coverageAnyEmptyList(rest)

let recursive coverageConsPatterns patterns acc =
    match patterns with
        | [] -> reverse(acc)
        | pattern :: rest ->
            match coverageUnwrap(pattern) with
                | PatternCons(head, tail) -> coverageConsPatterns(rest)((head, tail) :: acc)
                | _ -> coverageConsPatterns(rest)(acc)

let recursive coverageConsHeads (conses: List((Pattern, Pattern))) acc =
    match conses with
        | [] -> reverse(acc)
        | (head, _tail) :: rest -> coverageConsHeads(rest)(head :: acc)

let recursive coverageConsTails (conses: List((Pattern, Pattern))) acc =
    match conses with
        | [] -> reverse(acc)
        | (_head, tail) :: rest -> coverageConsTails(rest)(tail :: acc)

let recursive coverageTuplePatterns patterns (arity: Int) acc =
    match patterns with
        | [] -> reverse(acc)
        | pattern :: rest ->
            match coverageUnwrap(pattern) with
                | PatternTuple(elements) ->
                    if coverageLength(elements)(0) == arity
                    then coverageTuplePatterns(rest)(arity)(elements :: acc)
                    else coverageTuplePatterns(rest)(arity)(acc)
                | _ -> coverageTuplePatterns(rest)(arity)(acc)

let recursive coverageFirstTupleArity patterns =
    match patterns with
        | [] -> None
        | pattern :: rest ->
            match coverageUnwrap(pattern) with
                | PatternTuple(elements) -> Some(coverageLength(elements)(0))
                | _ -> coverageFirstTupleArity(rest)

let recursive coverageColumn (rows: List(List(Pattern))) (index: Int) acc =
    match rows with
        | [] -> reverse(acc)
        | row :: rest ->
            match coverageNth(row)(index) with
                | Some(pattern) -> coverageColumn(rest)(index)(pattern :: acc)
                | None -> coverageColumn(rest)(index)(acc)

let coverageIsPatternForConstructor pattern (constructorName: Str) environment =
    match coverageConstructorName(pattern)(environment) with
        | Some(name) -> name == constructorName
        | None -> false

let recursive coveragePatternsForConstructor patterns (constructorName: Str) environment acc =
    match patterns with
        | [] -> reverse(acc)
        | pattern :: rest ->
            if coverageIsPatternForConstructor(pattern)(constructorName)(environment)
            then coveragePatternsForConstructor(rest)(constructorName)(environment)(pattern :: acc)
            else coveragePatternsForConstructor(rest)(constructorName)(environment)(acc)

let recursive coverageConstructorArgumentRows patterns acc =
    match patterns with
        | [] -> reverse(acc)
        | pattern :: rest ->
            match coverageUnwrap(pattern) with
                | PatternConstructor(_name, arguments) -> coverageConstructorArgumentRows(rest)(arguments :: acc)
                | _ -> coverageConstructorArgumentRows(rest)(acc)

let coverageMissingConstructorPattern (constructorName: Str) (arity: Int) (fieldIndex: Int) (missingField: Maybe(Pattern)) =
    if arity == 0
    then PatternVar(constructorName)
    else
        match missingField with
            | Some(field) -> PatternConstructor(constructorName)(coverageReplaceAt(coverageWildcards(arity)([]))(fieldIndex)(field)([]))
            | None -> PatternConstructor(constructorName)(coverageWildcards(arity)([]))

let recursive coverageAnyBoolPattern patterns =
    match patterns with
        | [] -> false
        | pattern :: rest ->
            match coverageUnwrap(pattern) with
                | PatternBool(_value) -> true
                | _ -> coverageAnyBoolPattern(rest)

let recursive coverageHasBoolLiteral patterns (wanted: Bool) =
    match patterns with
        | [] -> false
        | pattern :: rest ->
            match coverageUnwrap(pattern) with
                | PatternBool(value) ->
                    if value == wanted
                    then true
                    else coverageHasBoolLiteral(rest)(wanted)
                | _ -> coverageHasBoolLiteral(rest)(wanted)

let recursive coverageAnyLiteral patterns =
    match patterns with
        | [] -> false
        | pattern :: rest ->
            match coverageUnwrap(pattern) with
                | PatternInt(_value) -> true
                | PatternString(_value) -> true
                | PatternRune(_value) -> true
                | _ -> coverageAnyLiteral(rest)

// The ADT the patterns name, when every constructor pattern belongs to one ADT.
let recursive coverageSharedAdt patterns environment (seen: Maybe(Str)) =
    match patterns with
        | [] -> seen
        | pattern :: rest ->
            match coverageConstructorName(pattern)(environment) with
                | None -> coverageSharedAdt(rest)(environment)(seen)
                | Some(name) ->
                    match coverageConstructorShape(name)(environment) with
                        | None -> coverageSharedAdt(rest)(environment)(seen)
                        | Some((adtName, _arity)) ->
                            match seen with
                                | None -> coverageSharedAdt(rest)(environment)(Some(adtName))
                                | Some(previous) ->
                                    if previous == adtName
                                    then coverageSharedAdt(rest)(environment)(seen)
                                    else None

let recursive coverageMissing environment (valueType: Maybe(SemanticType)) patterns =
    if coverageAnyCatchAll(environment)(patterns)
    then None
    else
        match coverageMissingList(environment)(valueType)(patterns) with
            | Some(missing) -> Some(missing)
            | None ->
                match coverageMissingTuple(environment)(valueType)(patterns) with
                    | Some(missing) -> Some(missing)
                    | None ->
                        match coverageMissingAdt(environment)(valueType)(patterns) with
                            | Some(missing) -> Some(missing)
                            | None ->
                                match coverageMissingBool(environment)(valueType)(patterns) with
                                    | Some(missing) -> Some(missing)
                                    | None ->
                                        if coverageAnyLiteral(patterns)
                                        then Some(PatternWildcard)
                                        else None
and coverageMissingList environment valueType patterns =
    (let isListType =
        match valueType with
            | Some(SemList(_element)) -> true
            | _ -> false
    in
        let conses = coverageConsPatterns(patterns)([])
        in
            if !isListType
            then
                if !coverageAnyEmptyList(patterns)
                then
                    match conses with
                        | [] -> None
                        | _ -> Some(PatternEmptyList)
                else coverageMissingConsPart(environment)(valueType)(conses)
            else
                if !coverageAnyEmptyList(patterns)
                then Some(PatternEmptyList)
                else coverageMissingConsPart(environment)(valueType)(conses))
and coverageMissingConsPart environment valueType conses =
    match conses with
        | [] -> Some(PatternCons(PatternWildcard)(PatternWildcard))
        | _ ->
            let elementType =
                match valueType with
                    | Some(SemList(element)) -> Some(element)
                    | _ -> None
            in
                match coverageMissing(environment)(elementType)(coverageConsHeads(conses)([])) with
                    | Some(missingHead) -> Some(PatternCons(missingHead)(PatternWildcard))
                    | None ->
                        match coverageMissing(environment)(valueType)(coverageConsTails(conses)([])) with
                            | Some(missingTail) -> Some(PatternCons(PatternWildcard)(missingTail))
                            | None -> None
and coverageMissingTuple environment valueType patterns =
    (let arity =
        match valueType with
            | Some(SemTuple(elements)) -> Some(coverageLength(elements)(0))
            | _ -> coverageFirstTupleArity(patterns)
    in
        match arity with
            | None -> None
            | Some(count) ->
                match coverageTuplePatterns(patterns)(count)([]) with
                    | [] -> Some(PatternTuple(coverageWildcards(count)([])))
                    | rows -> coverageMissingTupleColumn(environment)(valueType)(rows)(count)(0))
and coverageMissingTupleColumn environment valueType rows (count: Int) (index: Int) =
    if index >= count
    then None
    else
        let elementType =
            match valueType with
                | Some(SemTuple(elements)) -> coverageNth(elements)(index)
                | _ -> None
        in
            match coverageMissing(environment)(elementType)(coverageColumn(rows)(index)([])) with
                | Some(missing) -> Some(PatternTuple(coverageReplaceAt(coverageWildcards(count)([]))(index)(missing)([])))
                | None -> coverageMissingTupleColumn(environment)(valueType)(rows)(count)(index + 1)
and coverageMissingAdt environment valueType patterns =
    (let adtName =
        match valueType with
            | Some(SemNamed(_id, name, _arguments)) -> Some(name)
            | _ -> coverageSharedAdt(patterns)(environment)(None)
    in
        match adtName with
            | None -> None
            | Some(name) -> coverageMissingConstructor(environment)(patterns)(coverageAdtConstructors(environmentConstructors(environment))(name)([])))
and coverageMissingConstructor environment patterns (constructors: List((Str, Int))) =
    match constructors with
        | [] -> None
        | (constructorName, arity) :: rest ->
            match coveragePatternsForConstructor(patterns)(constructorName)(environment)([]) with
                | [] -> Some(coverageMissingConstructorPattern(constructorName)(arity)(-1)(None))
                | constructorPatterns ->
                    if arity == 0
                    then coverageMissingConstructor(environment)(patterns)(rest)
                    else
                        match coverageMissingConstructorField(environment)(coverageConstructorArgumentRows(constructorPatterns)([]))(constructorName)(arity)(0) with
                            | Some(missing) -> Some(missing)
                            | None -> coverageMissingConstructor(environment)(patterns)(rest)
and coverageMissingConstructorField environment rows (constructorName: Str) (arity: Int) (index: Int) =
    if index >= arity
    then None
    else
        match coverageMissing(environment)(None)(coverageColumn(rows)(index)([])) with
            | Some(missing) -> Some(coverageMissingConstructorPattern(constructorName)(arity)(index)(Some(missing)))
            | None -> coverageMissingConstructorField(environment)(rows)(constructorName)(arity)(index + 1)
and coverageMissingBool environment valueType patterns =
    (let isBoolType =
        match valueType with
            | Some(SemBool) -> true
            | _ -> false
    in
        if !isBoolType
        then
            if coverageAnyBoolPattern(patterns)
            then coverageMissingBoolLiteral(patterns)
            else None
        else coverageMissingBoolLiteral(patterns))
and coverageMissingBoolLiteral patterns =
    if !coverageHasBoolLiteral(patterns)(true)
    then Some(PatternBool(true))
    else
        if !coverageHasBoolLiteral(patterns)(false)
        then Some(PatternBool(false))
        else None

// Every arm is examined as the set of plain patterns it stands for: or-alternatives are separate
// arms, an as-pattern is its inner pattern, nested alternatives inside a cons, tuple, or
// constructor multiply out, and a record pattern becomes the positional constructor pattern it
// names, with a wildcard for every field it does not mention.
let recursive coverageFieldIndex (fieldName: Str) (fieldNames: List(Str)) (index: Int) =
    match fieldNames with
        | [] -> None
        | candidate :: rest ->
            if candidate == fieldName
            then Some(index)
            else coverageFieldIndex(fieldName)(rest)(index + 1)

let recursive coveragePlaceRecordFields (fields: List((Str, Pattern))) (fieldNames: List(Str)) positional =
    match fields with
        | [] -> positional
        | (fieldName, fieldPattern) :: rest ->
            match coverageFieldIndex(fieldName)(fieldNames)(0) with
                | Some(index) -> coveragePlaceRecordFields(rest)(fieldNames)(coverageReplaceAt(positional)(index)(fieldPattern)([]))
                | None -> coveragePlaceRecordFields(rest)(fieldNames)(positional)

let recursive coverageExpandPattern pattern environment =
    match pattern with
        | PatternAt(_span, inner) -> coverageExpandPattern(inner)(environment)
        | PatternOr(alternatives) -> coverageExpandAlternatives(alternatives)(environment)([])
        | PatternAs(inner, _name) -> coverageExpandPattern(inner)(environment)
        | PatternCons(head, tail) -> coverageConsCombinations(coverageCombineChildren([head, tail])(environment))([])
        | PatternTuple(elements) -> coverageTupleCombinations(coverageCombineChildren(elements)(environment))([])
        | PatternConstructor(name, patterns) -> coverageConstructorCombinations(name)(coverageCombineChildren(patterns)(environment))([])
        | PatternRecord(name, fields) ->
            match resolveConstructorBinding(name)(environment) with
                | Some(ConstructorInferenceDefinition { fieldNames = fieldNames }) ->
                    match fieldNames with
                        | [] -> [pattern]
                        | _ ->
                            let positional = coveragePlaceRecordFields(fields)(fieldNames)(coverageWildcards(coverageLength(fieldNames)(0))([]))
                            in coverageConstructorCombinations(name)(coverageCombineChildren(positional)(environment))([])
                | None -> [pattern]
        | other -> [other]
and coverageExpandAlternatives alternatives environment acc =
    match alternatives with
        | [] -> acc
        | alternative :: rest -> coverageExpandAlternatives(rest)(environment)(append(acc)(coverageExpandPattern(alternative)(environment)))
and coverageCombineChildren children environment =
    match children with
        | [] -> [[]]
        | child :: rest -> coverageProduct(coverageExpandPattern(child)(environment))(coverageCombineChildren(rest)(environment))([])
and coverageProduct heads (tails: List(List(Pattern))) acc =
    match heads with
        | [] -> acc
        | head :: rest -> coverageProduct(rest)(tails)(append(acc)(coveragePrefixAll(head)(tails)([])))
and coveragePrefixAll head (tails: List(List(Pattern))) acc =
    match tails with
        | [] -> reverse(acc)
        | tail :: rest -> coveragePrefixAll(head)(rest)((head :: tail) :: acc)
and coverageConsCombinations (combinations: List(List(Pattern))) acc =
    match combinations with
        | [] -> reverse(acc)
        | (head :: tail :: []) :: rest -> coverageConsCombinations(rest)(PatternCons(head)(tail) :: acc)
        | _ :: rest -> coverageConsCombinations(rest)(acc)
and coverageTupleCombinations (combinations: List(List(Pattern))) acc =
    match combinations with
        | [] -> reverse(acc)
        | elements :: rest -> coverageTupleCombinations(rest)(PatternTuple(elements) :: acc)
and coverageConstructorCombinations (name: Str) (combinations: List(List(Pattern))) acc =
    match combinations with
        | [] -> reverse(acc)
        | arguments :: rest -> coverageConstructorCombinations(name)(rest)(PatternConstructor(name)(arguments) :: acc)

let recursive coverageExpandCases cases environment acc =
    match cases with
        | [] -> reverse(acc)
        | (pattern, body, guard) :: rest -> coverageExpandCases(rest)(environment)(coverageExpandedArms(coverageExpandPattern(pattern)(environment))(body)(guard)(acc))
and coverageExpandedArms patterns body guard acc =
    match patterns with
        | [] -> acc
        | pattern :: rest -> coverageExpandedArms(rest)(body)(guard)((pattern, body, guard) :: acc)

let recursive coverageDistinctAdts cases environment (names: List(Str)) =
    match cases with
        | [] -> reverse(names)
        | (pattern, _body, _guard) :: rest ->
            match coverageConstructorName(pattern)(environment) with
                | None -> coverageDistinctAdts(rest)(environment)(names)
                | Some(name) ->
                    match coverageConstructorShape(name)(environment) with
                        | Some((adtName, _arity)) ->
                            if coverageContainsText(adtName)(names)
                            then coverageDistinctAdts(rest)(environment)(names)
                            else coverageDistinctAdts(rest)(environment)(adtName :: names)
                        | None -> coverageDistinctAdts(rest)(environment)(names)

let coverageIsComposite pattern =
    match coverageUnwrap(pattern) with
        | PatternConstructor(_name, _patterns) -> true
        | PatternCons(_head, _tail) -> true
        | PatternTuple(_elements) -> true
        | _ -> false

// Walks the arms in order carrying what earlier arms already matched.
let recursive coverageUnreachable cases environment (hasCatchAll: Bool) (composites: List(Str)) (ints: List(Int)) (texts: List(Str)) (seenTrue: Bool) (seenFalse: Bool) (constructors: List(Str)) =
    match cases with
        | [] -> None
        | (pattern, _body, guard) :: rest ->
            if hasCatchAll
            then Some(UnreachableMatchArm("Unreachable match arm: a catch-all pattern was already matched earlier."))
            else
                match guard with
                    | Some(_guard) -> coverageUnreachable(rest)(environment)(hasCatchAll)(composites)(ints)(texts)(seenTrue)(seenFalse)(constructors)
                    | None ->
                        if coverageIsCatchAll(environment)(pattern)
                        then coverageUnreachable(rest)(environment)(true)(composites)(ints)(texts)(seenTrue)(seenFalse)(constructors)
                        else
                            if coverageIsComposite(pattern)
                            then
                                let key = coverageFormatPattern(pattern)
                                in
                                    if coverageContainsText(key)(composites)
                                    then Some(UnreachableMatchArm("Unreachable match arm: pattern " + key + " is already matched earlier."))
                                    else coverageUnreachableConstructor(pattern)(rest)(environment)(key :: composites)(ints)(texts)(seenTrue)(seenFalse)(constructors)
                            else coverageUnreachableLiteral(pattern)(rest)(environment)(composites)(ints)(texts)(seenTrue)(seenFalse)(constructors)
and coverageUnreachableLiteral pattern rest environment composites ints texts seenTrue seenFalse constructors =
    match coverageUnwrap(pattern) with
        | PatternInt(value) ->
            if coverageContainsInt(value)(ints)
            then Some(UnreachableMatchArm("Unreachable match arm: integer literal " + Ashes.Text.fromInt(value) + " is already matched earlier."))
            else coverageUnreachable(rest)(environment)(false)(composites)(value :: ints)(texts)(seenTrue)(seenFalse)(constructors)
        | PatternRune(value) ->
            if coverageContainsInt(value)(ints)
            then Some(UnreachableMatchArm("Unreachable match arm: rune literal U+" + coverageHex(value) + " is already matched earlier."))
            else coverageUnreachable(rest)(environment)(false)(composites)(value :: ints)(texts)(seenTrue)(seenFalse)(constructors)
        | PatternString(value) ->
            if coverageContainsText(value)(texts)
            then Some(UnreachableMatchArm("Unreachable match arm: string literal \"" + value + "\" is already matched earlier."))
            else coverageUnreachable(rest)(environment)(false)(composites)(ints)(value :: texts)(seenTrue)(seenFalse)(constructors)
        | PatternBool(true) ->
            if seenTrue
            then Some(UnreachableMatchArm("Unreachable match arm: 'true' is already matched earlier."))
            else coverageUnreachable(rest)(environment)(false)(composites)(ints)(texts)(true)(seenFalse)(constructors)
        | PatternBool(false) ->
            if seenFalse
            then Some(UnreachableMatchArm("Unreachable match arm: 'false' is already matched earlier."))
            else coverageUnreachable(rest)(environment)(false)(composites)(ints)(texts)(seenTrue)(true)(constructors)
        | _ -> coverageUnreachableConstructor(pattern)(rest)(environment)(composites)(ints)(texts)(seenTrue)(seenFalse)(constructors)
and coverageUnreachableConstructor pattern rest environment composites ints texts seenTrue seenFalse constructors =
    match coverageConstructorName(pattern)(environment) with
        | None -> coverageUnreachable(rest)(environment)(false)(composites)(ints)(texts)(seenTrue)(seenFalse)(constructors)
        | Some(name) ->
            match coverageConstructorShape(name)(environment) with
                | Some((_adtName, 0)) ->
                    if coverageContainsText(name)(constructors)
                    then Some(UnreachableMatchArm("Unreachable match arm: constructor " + name + " is already matched earlier."))
                    else coverageUnreachable(rest)(environment)(false)(composites)(ints)(texts)(seenTrue)(seenFalse)(name :: constructors)
                | _ -> coverageUnreachable(rest)(environment)(false)(composites)(ints)(texts)(seenTrue)(seenFalse)(name :: constructors)

let recursive coverageUnguardedPatterns cases acc =
    match cases with
        | [] -> reverse(acc)
        | (pattern, _body, None) :: rest -> coverageUnguardedPatterns(rest)(pattern :: acc)
        | (_pattern, _body, Some(_guard)) :: rest -> coverageUnguardedPatterns(rest)(acc)

let recursive coverageSeenConstructors patterns environment acc =
    match patterns with
        | [] -> acc
        | pattern :: rest ->
            match coverageConstructorName(pattern)(environment) with
                | Some(name) -> coverageSeenConstructors(rest)(environment)(name :: acc)
                | None -> coverageSeenConstructors(rest)(environment)(acc)

let recursive coverageMissingNames (constructors: List((Str, Int))) (seen: List(Str)) acc =
    match constructors with
        | [] -> reverse(acc)
        | (name, _arity) :: rest ->
            if coverageContainsText(name)(seen)
            then coverageMissingNames(rest)(seen)(acc)
            else coverageMissingNames(rest)(seen)(name :: acc)

let recursive coverageQuoteNames (names: List(Str)) (limit: Int) (acc: Str) =
    match names with
        | [] -> acc
        | name :: rest ->
            if limit == 0
            then acc
            else
                if acc == ""
                then coverageQuoteNames(rest)(limit - 1)("'" + name + "'")
                else coverageQuoteNames(rest)(limit - 1)(acc + ", '" + name + "'")

let recursive coverageJoinWithAnd (names: List(Str)) (acc: Str) =
    match names with
        | [] -> acc
        | name :: rest ->
            if acc == ""
            then coverageJoinWithAnd(rest)(name)
            else coverageJoinWithAnd(rest)(acc + " and " + name)

let coverageMissingConstructorsMessage (adtName: Str) (missing: List(Str)) =
    if adtName == "Result"
    then "Non-exhaustive match on Result: missing " + coverageJoinWithAnd(missing)("") + "."
    else
        let count = coverageLength(missing)(0)
        in
            if count <= 5
            then "Non-exhaustive match expression. Missing constructor(s): " + coverageQuoteNames(missing)(count)("") + "."
            else "Non-exhaustive match expression. Missing constructor(s): " + coverageQuoteNames(missing)(3)("") + ", ... and " + Ashes.Text.fromInt(count - 3) + " more."

let recursive coverageAnyConstructorPattern patterns =
    match patterns with
        | [] -> false
        | pattern :: rest ->
            match coverageUnwrap(pattern) with
                | PatternConstructor(_name, _patterns) -> true
                | _ -> coverageAnyConstructorPattern(rest)

let recursive coverageAnyTuplePattern patterns =
    match patterns with
        | [] -> false
        | pattern :: rest ->
            match coverageUnwrap(pattern) with
                | PatternTuple(_elements) -> true
                | _ -> coverageAnyTuplePattern(rest)

let recursive coverageAnyCons patterns =
    match patterns with
        | [] -> false
        | pattern :: rest ->
            match coverageUnwrap(pattern) with
                | PatternCons(_head, _tail) -> true
                | _ -> coverageAnyCons(rest)

let coverageDefinitelyExhaustive environment patterns =
    if coverageAnyCatchAll(environment)(patterns)
    then true
    else
        if coverageAnyEmptyList(patterns)
        then coverageAnyCons(patterns)
        else false

let coverageBoolExhaustive environment patterns =
    if coverageAnyCatchAll(environment)(patterns)
    then true
    else
        if coverageHasBoolLiteral(patterns)(true)
        then coverageHasBoolLiteral(patterns)(false)
        else false

let coverageMissingCaseError environment (valueType: SemanticType) patterns =
    match coverageMissing(environment)(Some(valueType))(patterns) with
        | Some(missing) -> Some(NonExhaustiveMatch("Non-exhaustive match expression. Missing case: " + coverageFormatPattern(missing) + "."))
        | None -> None

let coverageExhaustiveness environment (valueType: SemanticType) cases =
    (let unguarded = coverageUnguardedPatterns(cases)([])
    in
        if coverageAnyCatchAll(environment)(unguarded)
        then None
        else
            match valueType with
                | SemNamed(_id, adtName, _arguments) ->
                    match coverageAdtConstructors(environmentConstructors(environment))(adtName)([]) with
                        | [] -> coverageMissingCaseError(environment)(valueType)(unguarded)
                        | constructors ->
                            match coverageMissingNames(constructors)(coverageSeenConstructors(unguarded)(environment)([]))([]) with
                                | [] -> coverageMissingCaseError(environment)(valueType)(unguarded)
                                | missing -> Some(NonExhaustiveMatch(coverageMissingConstructorsMessage(adtName)(missing)))
                | SemList(_element) ->
                    if !coverageAnyEmptyList(unguarded)
                    then Some(NonExhaustiveMatch("Non-exhaustive match expression. Missing case: []."))
                    else
                        if !coverageAnyCons(unguarded)
                        then Some(NonExhaustiveMatch("Non-exhaustive match expression. Missing case: x :: xs."))
                        else coverageMissingCaseError(environment)(valueType)(unguarded)
                | _ ->
                    let hasTupleArm =
                        match valueType with
                            | SemTuple(_elements) -> coverageAnyTuplePattern(unguarded)
                            | _ -> false
                    in
                        if hasTupleArm
                        then coverageMissingCaseError(environment)(valueType)(unguarded)
                        else
                            if coverageAnyConstructorPattern(unguarded)
                            then coverageMissingCaseError(environment)(valueType)(unguarded)
                            else
                                if coverageDefinitelyExhaustive(environment)(unguarded)
                                then coverageMissingCaseError(environment)(valueType)(unguarded)
                                else
                                    if coverageBoolExhaustive(environment)(unguarded)
                                    then coverageMissingCaseError(environment)(valueType)(unguarded)
                                    else Some(NonExhaustiveMatch("Non-exhaustive match expression.")))

// The first diagnostic a match's arms deserve, or None when they are consistent and complete.
let matchCoverageError cases (valueType: SemanticType) environment =
    (let expanded = coverageExpandCases(cases)(environment)([])
    in
        match coverageDistinctAdts(expanded)(environment)([]) with
            | first :: second :: more -> Some(ConstructorPatternsFromDifferentAdts(first :: second :: more))
            | _ ->
                match coverageUnreachable(expanded)(environment)(false)([])([])([])(false)(false)([]) with
                    | Some(error) -> Some(error)
                    | None -> coverageExhaustiveness(environment)(valueType)(expanded))

let checkMatchCoverage cases scrutineeType environment (result: TypeInferenceResult) =
    match result with
        | TypeInferenceResult { error = Some(_error) } -> result
        | TypeInferenceResult { substitution = substitution } ->
            match matchCoverageError(cases)(applySubstitution(substitution)(scrutineeType))(environment) with
                | None -> result
                | Some(error) -> result with error = Some(error)

let recursive findRecordFieldType : Str -> List(Str) -> List(SemanticType) -> Maybe(SemanticType) =
    given (fieldName) ->
        given (fieldNames) ->
            given (fieldTypes) ->
                match (fieldNames, fieldTypes) with
                    | (candidateName :: nameTail, fieldType :: typeTail) ->
                        if fieldName == candidateName
                        then Some(fieldType)
                        else findRecordFieldType(fieldName)(nameTail)(typeTail)
                    | _ -> None

let recursive findMissingRecordField requiredFields providedFields =
    match requiredFields with
        | [] -> None
        | head :: tail ->
            if patternNameExists(head)(providedFields)
            then findMissingRecordField(tail)(providedFields)
            else Some(head)

let resultTypeShape semanticType =
    match semanticType with
        | SemNamed(symbolId, "Result", errorType :: successType :: []) ->
            Some(
                ResultTypeShape(symbolId = symbolId, errorType = errorType, successType = successType)
            )
        | _ -> None

// `await` runs a `Task(e, a)` to its `Result(e, a)`: the operand must already resolve to a task
// (a fresh error/success pair is unified into it first), and the result's symbol is the standard
// Result definition the same environment seeds.
let taskTypeShape semanticType =
    match semanticType with
        | SemNamed(symbolId, "Task", errorType :: successType :: []) ->
            Some(
                ResultTypeShape(symbolId = symbolId, errorType = errorType, successType = successType)
            )
        | _ -> None

let recursive standardTypeSymbolId (name: Str) (definitions: List(ConstructorInferenceDefinition)) (fallback: Int) =
    match definitions with
        | [] -> fallback
        | ConstructorInferenceDefinition { scheme = TypeScheme { body = body } } :: rest ->
            match splitConstructorType(body)([]) with
                | ConstructorTypeShape { resultType = SemNamed(symbolId, candidate, _arguments) } ->
                    if candidate == name
                    then symbolId
                    else standardTypeSymbolId(name)(rest)(fallback)
                | _ -> standardTypeSymbolId(name)(rest)(fallback)

let resolveResultSymbolId environment =
    match environment with
        | TypeEnvironment { constructors = constructors } -> standardTypeSymbolId("Result")(constructors)(2)

let resolveTaskSymbolId environment =
    match environment with
        | TypeEnvironment { constructors = constructors } -> standardTypeSymbolId("Task")(constructors)(3)

let recursive allPatternNamesPresent : List(Str) -> List(Str) -> Bool =
    given (names) ->
        given (candidates) ->
            match names with
                | [] -> true
                | head :: tail ->
                    if patternNameExists(head)(candidates)
                    then allPatternNamesPresent(tail)(candidates)
                    else false

let samePatternNameSets left right =
    if allPatternNamesPresent(left)(right)
    then allPatternNamesPresent(right)(left)
    else false

let mergeUnification currentSubstitution result supply fallbackType =
    match result with
        | UnificationResult { substitution = unificationSubstitution, error = None } ->
            let combined = appendSubstitution(unificationSubstitution)(currentSubstitution)
            in inferenceSuccess(applySubstitution(combined)(fallbackType))(combined)(supply)
        | UnificationResult { substitution = _unificationSubstitution, error = Some(error) } ->
            inferenceFailure(
                fallbackType,
                currentSubstitution,
                supply,
                InferenceUnificationError(error)
            )

let recursive anyAbstractSemanticType values =
    match values with
        | [] -> false
        | head :: tail ->
            if isAbstractSemanticType(head)
            then true
            else anyAbstractSemanticType(tail)
and isAbstractSemanticType semanticType =
    match semanticType with
        | SemVariable(_) -> true
        | SemParameter(_, _) -> true
        | SemList(element) -> isAbstractSemanticType(element)
        | SemTuple(elements) -> anyAbstractSemanticType(elements)
        | SemFunction(argument, result, capabilityRow) ->
            if isAbstractSemanticType(argument)
            then true
            else
                if isAbstractSemanticType(result)
                then true
                else
                    match capabilityRow with
                        | None -> false
                        | Some(row) -> isAbstractSemanticType(row)
        | SemCapability(_name, arguments) -> anyAbstractSemanticType(arguments)
        | SemRow(capabilities, tail) ->
            if anyAbstractSemanticType(capabilities)
            then true
            else
                match tail with
                    | None -> false
                    | Some(tailType) -> isAbstractSemanticType(tailType)
        | SemNamed(_symbolId, _name, arguments) -> anyAbstractSemanticType(arguments)
        | SemPointer(pointee) -> isAbstractSemanticType(pointee)
        | _ -> false

let recursive resolveProvidedCapabilities capabilities environment reversed =
    match capabilities with
        | [] -> ProviderCapabilityResolution(capabilities = reverse(reversed), error = None)
        | head :: tail ->
            match head with
                | SemCapability(name, _arguments) ->
                    if isAbstractSemanticType(head)
                    then resolveProvidedCapabilities(tail)(environment)(head :: reversed)
                    else
                        match resolveCapabilityProvider(head)(environment) with
                            | None -> resolveProvidedCapabilities(tail)(environment)(head :: reversed)
                            | Some(_) ->
                                if capabilityIsHandled(name)(environment)
                                then
                                    ProviderCapabilityResolution(capabilities = reverse(reversed), error = Some(
                                        AmbiguousCapabilitySatisfaction(name)
                                    ))
                                else resolveProvidedCapabilities(tail)(environment)(reversed)
                | _ -> resolveProvidedCapabilities(tail)(environment)(head :: reversed)
and resolveProvidedCapabilityRow semanticType environment =
    match semanticType with
        | SemRow(capabilities, tail) ->
            match resolveProvidedCapabilities(capabilities)(environment)([]) with
                | ProviderCapabilityResolution { capabilities = _resolvedCapabilities, error = Some(error) } ->
                    ProviderRowResolution(semanticType = semanticType, error = Some(
                        error
                    ))
                | ProviderCapabilityResolution { capabilities = resolvedCapabilities, error = None } ->
                    match tail with
                        | None -> ProviderRowResolution(semanticType = SemRow(resolvedCapabilities)(None), error = None)
                        | Some(tailType) ->
                            match resolveProvidedCapabilityRow(tailType)(environment) with
                                | ProviderRowResolution { semanticType = _resolvedTail, error = Some(error) } ->
                                    ProviderRowResolution(semanticType = semanticType, error = Some(
                                        error
                                    ))
                                | ProviderRowResolution { semanticType = resolvedTail, error = None } ->
                                    ProviderRowResolution(semanticType = SemRow(
                                        resolvedCapabilities,
                                        Some(resolvedTail)
                                    ), error = None)
        | _ -> ProviderRowResolution(semanticType = semanticType, error = None)

let subsumeCapabilityRow capabilityRow environment ambientRow substitution supply resultType =
    (let resolvedRow = applySubstitution(substitution)(capabilityRow)
    in
        let resolvedAmbient = applySubstitution(substitution)(ambientRow)
        in
            match resolveProvidedCapabilityRow(resolvedRow)(environment) with
                | ProviderRowResolution { semanticType = _providerResolvedRow, error = Some(error) } ->
                    inferenceFailure(
                        resultType,
                        substitution,
                        supply,
                        error
                    )
                | ProviderRowResolution { semanticType = providerResolvedRow, error = None } ->
                    match providerResolvedRow with
                        | SemRow([], None) ->
                            inferenceSuccess(
                                applySubstitution(substitution)(resultType),
                                substitution,
                                supply
                            )
                        | SemRow(capabilities, None) ->
                            match freshTypeVariable(supply) with
                                | (tailType, nextSupply) ->
                                    mergeUnification(
                                        substitution,
                                        unify(resolvedAmbient)(SemRow(capabilities)(Some(tailType))),
                                        nextSupply,
                                        resultType
                                    )
                        | _ ->
                            mergeUnification(
                                substitution,
                                unify(resolvedAmbient)(providerResolvedRow),
                                supply,
                                resultType
                            ))

let recursive capabilityOperationCallRoot expression sawArgument =
    match expression with
        | ExprAt(_span, inner) -> capabilityOperationCallRoot(inner)(sawArgument)
        | ExprCall(function, _argument, _whitespace, _layout) -> capabilityOperationCallRoot(function)(true)
        | ExprQualifiedVar(capabilityName, operationName) ->
            if sawArgument
            then Some((capabilityName, operationName))
            else None
        | _ -> None

let subsumeUnsignedCapabilityOperation function environment ambientRow result =
    match result with
        | TypeInferenceResult { semanticType = semanticType, substitution = substitution, supply = supply, constraints = constraints, error = None } ->
            match capabilityOperationCallRoot(function)(true) with
                | None -> result
                | Some((capabilityName, operationName)) ->
                    match resolveCapabilityOperation(capabilityName)(operationName)(environment) with
                        | Some(CapabilityOperationInferenceDefinition { name = _name, scheme = _scheme, hasExplicitSignature = false }) ->
                            addConstraints(
                                constraints,
                                subsumeCapabilityRow(
                                    SemRow([SemCapability(capabilityName)([])])(None),
                                    environment,
                                    ambientRow,
                                    substitution,
                                    supply,
                                    semanticType
                                )
                            )
                        | _ -> result
        | _ -> result

let checkInferenceAnnotation annotation expectedType environment substitution supply =
    match prepareTypeResolutionContext(annotation)(inferenceTypeResolutionContext(environment))(supply) with
        | TypeResolutionPreparationResult { context = preparedContext, supply = preparedSupply } ->
            match resolveTypeExpression(annotation)(preparedContext) with
                | TypeResolutionResult { semanticType = annotationType, error = None } ->
                    mergeUnification(
                        substitution,
                        unify(applySubstitution(substitution)(expectedType))(annotationType),
                        preparedSupply,
                        expectedType
                    )
                | TypeResolutionResult { semanticType = _annotationType, error = Some(error) } ->
                    inferenceFailure(
                        expectedType,
                        substitution,
                        preparedSupply,
                        InferenceTypeResolutionError(error)
                    )

let inferenceEnvironmentSchemes environment =
    match environment with
        | TypeEnvironment { bindings = bindings, constructors = _constructors, capabilities = _capabilities, traits = _traits, providers = _providers, handledCapabilities = _handledCapabilities, typeResolutionContext = _typeResolutionContext } ->
            let recursive schemes values =
                match values with
                    | [] -> []
                    | (_name, scheme) :: tail -> scheme :: schemes(tail)
            in schemes(bindings)

let recursive firstAmbiguousTraitRequirement constraints bindingType environment =
    match constraints with
        | [] -> None
        | TraitConstraint { traitName = traitName, typeArguments = typeArguments } :: tail ->
            let variables = freeTypeVariables(SemTuple(typeArguments))
            in
                let bodyVariables = freeTypeVariables(bindingType)
                in
                    let environmentVariables = freeEnvironmentVariables(inferenceEnvironmentSchemes(environment))
                    in
                        if firstAmbiguousRequirementVariable(variables)(bodyVariables)(environmentVariables)
                        then Some(traitName)
                        else firstAmbiguousTraitRequirement(tail)(bindingType)(environment)

let bindingSignatureSuccess semanticType substitution supply constraints = TypeInferenceResult(semanticType = semanticType, substitution = substitution, supply = supply, constraints = constraints, error = None)

let validateWrittenBindingRequirements inferred written semanticType substitution supply =
    match firstConstraintMissingFrom(inferred)(written) with
        | Some(traitName) ->
            inferenceFailure(
                semanticType,
                substitution,
                supply,
                MissingWrittenTraitRequirement(traitName)
            )
        | None ->
            match firstConstraintMissingFrom(written)(inferred) with
                | Some(traitName) ->
                    inferenceFailure(
                        semanticType,
                        substitution,
                        supply,
                        UnjustifiedWrittenTraitRequirement(traitName)
                    )
                | None -> bindingSignatureSuccess(semanticType)(substitution)(supply)(written)

let validateBindingRequirementSet requirements inferred written semanticType environment substitution supply =
    (let selected =
        match requirements with
            | [] -> inferred
            | _ -> written
    in
        match firstAmbiguousTraitRequirement(selected)(semanticType)(environment) with
            | Some(traitName) ->
                inferenceFailure(
                    semanticType,
                    substitution,
                    supply,
                    AmbiguousTraitRequirement(traitName)
                )
            | None ->
                match requirements with
                    | [] -> bindingSignatureSuccess(semanticType)(substitution)(supply)(selected)
                    | _ -> validateWrittenBindingRequirements(inferred)(written)(semanticType)(substitution)(supply))

let finishInferenceBindingSignature requirements context checkedType inferredConstraints environment substitution supply =
    match resolveBindingRequirements(requirements)(context)(environment)([]) with
        | BindingRequirementResolution { constraints = writtenConstraints, error = Some(error) } ->
            inferenceFailure(
                checkedType,
                substitution,
                supply,
                error
            )
        | BindingRequirementResolution { constraints = writtenConstraints, error = None } ->
            let semanticType = applySubstitution(substitution)(checkedType)
            in
                let inferred =
                    simplifyTraitConstraints(
                        environment,
                        applyInferenceConstraints(substitution)(inferredConstraints)
                    )
                in
                    let written =
                        simplifyTraitConstraints(
                            environment,
                            applyInferenceConstraints(substitution)(writtenConstraints)
                        )
                    in
                        validateBindingRequirementSet(
                            requirements,
                            inferred,
                            written,
                            semanticType,
                            environment,
                            substitution,
                            supply
                        )

let checkInferenceBindingSignature annotation requirements expectedType inferredConstraints environment substitution supply =
    match prepareBindingSignatureContext(annotation)(requirements)(environment)(supply) with
        | TypeResolutionPreparationResult { context = context, supply = preparedSupply } ->
            let annotationResult =
                match annotation with
                    | None -> inferenceSuccess(expectedType)(substitution)(preparedSupply)
                    | Some(typeExpression) ->
                        match resolveTypeExpression(typeExpression)(context) with
                            | TypeResolutionResult { semanticType = annotationType, error = None } ->
                                mergeUnification(
                                    substitution,
                                    unify(applySubstitution(substitution)(expectedType))(annotationType),
                                    preparedSupply,
                                    expectedType
                                )
                            | TypeResolutionResult { semanticType = _annotationType, error = Some(error) } ->
                                inferenceFailure(
                                    expectedType,
                                    substitution,
                                    preparedSupply,
                                    InferenceTypeResolutionError(error)
                                )
            in
                match annotationResult with
                    | TypeInferenceResult { semanticType = checkedType, substitution = checkedSubstitution, supply = checkedSupply, constraints = _annotationConstraints, error = None } ->
                        finishInferenceBindingSignature(
                            requirements,
                            context,
                            checkedType,
                            inferredConstraints,
                            environment,
                            checkedSubstitution,
                            checkedSupply
                        )
                    | failure -> failure

let recursive inferExpressions expressions environment substitution supply ambientRow reversedTypes =
    match expressions with
        | [] -> inferenceSuccess(SemTuple(reversedTypes))(substitution)(supply)
        | head :: tail ->
            match inferWith(head)(environment)(substitution)(supply)(ambientRow) with
                | TypeInferenceResult { semanticType = inferredType, substitution = nextSubstitution, supply = nextSupply, constraints = inferredConstraints, error = None } ->
                    addConstraints(
                        inferredConstraints,
                        inferExpressions(
                            tail,
                            environment,
                            nextSubstitution,
                            nextSupply,
                            ambientRow,
                            inferredType :: reversedTypes
                        )
                    )
                | failure -> failure
and inferListElements expressions elementType environment substitution supply ambientRow =
    match expressions with
        | [] -> inferenceSuccess(SemList(applySubstitution(substitution)(elementType)))(substitution)(supply)
        | head :: tail ->
            match inferWith(head)(environment)(substitution)(supply)(ambientRow) with
                | TypeInferenceResult { semanticType = inferredType, substitution = nextSubstitution, supply = nextSupply, constraints = inferredConstraints, error = None } ->
                    let unification =
                        unify(
                            applySubstitution(nextSubstitution)(elementType),
                            applySubstitution(nextSubstitution)(inferredType)
                        )
                    in
                        match mergeUnification(nextSubstitution)(unification)(nextSupply)(elementType) with
                            | TypeInferenceResult { semanticType = unifiedElement, substitution = unifiedSubstitution, supply = unifiedSupply, constraints = _unificationConstraints, error = None } ->
                                addConstraints(
                                    inferredConstraints,
                                    inferListElements(
                                        tail,
                                        unifiedElement,
                                        environment,
                                        unifiedSubstitution,
                                        unifiedSupply,
                                        ambientRow
                                    )
                                )
                            | failure -> failure
                | failure -> failure
and inferPatternList patterns environment substitution supply names reversedTypes =
    match patterns with
        | [] -> patternSuccess(SemTuple(reversedTypes))(environment)(substitution)(supply)(names)
        | head :: tail ->
            match inferPattern(head)(environment)(substitution)(supply)(names) with
                | PatternInferenceResult { semanticType = headType, environment = headEnvironment, substitution = headSubstitution, supply = headSupply, names = headNames, error = None } ->
                    inferPatternList(
                        tail,
                        headEnvironment,
                        headSubstitution,
                        headSupply,
                        headNames,
                        headType :: reversedTypes
                    )
                | failure -> failure
and inferConstructorPatternArguments constructorName patterns parameterTypes resultType environment substitution supply names =
    match (patterns, parameterTypes) with
        | ([], []) ->
            patternSuccess(
                applySubstitution(substitution)(resultType),
                environment,
                substitution,
                supply,
                names
            )
        | (pattern :: patternTail, parameterType :: parameterTail) ->
            match inferPattern(pattern)(environment)(substitution)(supply)(names) with
                | PatternInferenceResult { semanticType = patternType, environment = patternEnvironment, substitution = patternSubstitution, supply = patternSupply, names = patternNames, error = None } ->
                    match mergePatternUnification(
                        patternSubstitution,
                        unify(
                            applySubstitution(patternSubstitution)(parameterType),
                            applySubstitution(patternSubstitution)(patternType)
                        ),
                        patternSupply,
                        parameterType,
                        patternEnvironment,
                        patternNames
                    ) with
                        | PatternInferenceResult { semanticType = _unifiedType, environment = unifiedEnvironment, substitution = unifiedSubstitution, supply = unifiedSupply, names = unifiedNames, error = None } ->
                            inferConstructorPatternArguments(
                                constructorName,
                                patternTail,
                                parameterTail,
                                resultType,
                                unifiedEnvironment,
                                unifiedSubstitution,
                                unifiedSupply,
                                unifiedNames
                            )
                        | failure -> failure
                | failure -> failure
        | _ ->
            patternFailure(
                SemNever,
                environment,
                substitution,
                supply,
                names,
                ConstructorPatternArityMismatch(constructorName)
            )
and inferRecordPatternFields constructorName fields fieldNames fieldTypes resultType environment substitution supply names seenFields =
    match fields with
        | [] -> patternSuccess(applySubstitution(substitution)(resultType))(environment)(substitution)(supply)(names)
        | (fieldName, fieldPattern) :: tail ->
            if patternNameExists(fieldName)(seenFields)
            then
                patternFailure(
                    SemNever,
                    environment,
                    substitution,
                    supply,
                    names,
                    DuplicateRecordPatternField(fieldName)
                )
            else
                match findRecordFieldType(fieldName)(fieldNames)(fieldTypes) with
                    | None ->
                        patternFailure(
                            SemNever,
                            environment,
                            substitution,
                            supply,
                            names,
                            UnknownRecordPatternField(constructorName)(fieldName)
                        )
                    | Some(fieldType) ->
                        match inferPattern(fieldPattern)(environment)(substitution)(supply)(names) with
                            | PatternInferenceResult { semanticType = patternType, environment = patternEnvironment, substitution = patternSubstitution, supply = patternSupply, names = patternNames, error = None } ->
                                match mergePatternUnification(
                                    patternSubstitution,
                                    unify(
                                        applySubstitution(patternSubstitution)(fieldType),
                                        applySubstitution(patternSubstitution)(patternType)
                                    ),
                                    patternSupply,
                                    fieldType,
                                    patternEnvironment,
                                    patternNames
                                ) with
                                    | PatternInferenceResult { semanticType = _unifiedType, environment = unifiedEnvironment, substitution = unifiedSubstitution, supply = unifiedSupply, names = unifiedNames, error = None } ->
                                        inferRecordPatternFields(
                                            constructorName,
                                            tail,
                                            fieldNames,
                                            fieldTypes,
                                            resultType,
                                            unifiedEnvironment,
                                            unifiedSubstitution,
                                            unifiedSupply,
                                            unifiedNames,
                                            fieldName :: seenFields
                                        )
                                    | failure -> failure
                            | failure -> failure
and inferRecordExpressionFields recordName fields fieldNames fieldTypes resultType requireAll environment substitution supply ambientRow seenFields accumulatedConstraints =
    match fields with
        | [] ->
            if requireAll
            then
                match findMissingRecordField(fieldNames)(seenFields) with
                    | None ->
                        addConstraints(
                            accumulatedConstraints,
                            inferenceSuccess(applySubstitution(substitution)(resultType))(substitution)(supply)
                        )
                    | Some(missing) ->
                        inferenceFailure(
                            SemNever,
                            substitution,
                            supply,
                            MissingRecordField(recordName)(missing)
                        )
            else
                addConstraints(
                    accumulatedConstraints,
                    inferenceSuccess(applySubstitution(substitution)(resultType))(substitution)(supply)
                )
        | (fieldName, fieldExpression) :: tail ->
            if patternNameExists(fieldName)(seenFields)
            then inferenceFailure(SemNever)(substitution)(supply)(DuplicateRecordField(fieldName))
            else
                match findRecordFieldType(fieldName)(fieldNames)(fieldTypes) with
                    | None ->
                        inferenceFailure(
                            SemNever,
                            substitution,
                            supply,
                            UnknownRecordField(recordName)(fieldName)
                        )
                    | Some(fieldType) ->
                        match inferWith(fieldExpression)(environment)(substitution)(supply)(ambientRow) with
                            | TypeInferenceResult { semanticType = expressionType, substitution = expressionSubstitution, supply = expressionSupply, constraints = expressionConstraints, error = None } ->
                                match mergeUnification(
                                    expressionSubstitution,
                                    unify(
                                        applySubstitution(expressionSubstitution)(fieldType),
                                        applySubstitution(expressionSubstitution)(expressionType)
                                    ),
                                    expressionSupply,
                                    resultType
                                ) with
                                    | TypeInferenceResult { semanticType = _unifiedType, substitution = unifiedSubstitution, supply = unifiedSupply, constraints = _unificationConstraints, error = None } ->
                                        inferRecordExpressionFields(
                                            recordName,
                                            tail,
                                            fieldNames,
                                            fieldTypes,
                                            resultType,
                                            requireAll,
                                            environment,
                                            unifiedSubstitution,
                                            unifiedSupply,
                                            ambientRow,
                                            fieldName :: seenFields,
                                            appendConstraints(accumulatedConstraints)(expressionConstraints)
                                        )
                                    | failure -> failure
                            | failure -> failure
and inferResultSuccessPipe left right environment substitution supply ambientRow =
    match inferWith(left)(environment)(substitution)(supply)(ambientRow) with
        | TypeInferenceResult { semanticType = leftType, substitution = leftSubstitution, supply = leftSupply, constraints = leftConstraints, error = None } ->
            let resolvedLeft = applySubstitution(leftSubstitution)(leftType)
            in
                match resultTypeShape(resolvedLeft) with
                    | None -> inferenceFailure(SemNever)(leftSubstitution)(leftSupply)(ExpectedResultType(resolvedLeft))
                    | Some(ResultTypeShape { symbolId = symbolId, errorType = errorType, successType = successType }) ->
                        match inferWith(right)(environment)(leftSubstitution)(leftSupply)(ambientRow) with
                            | TypeInferenceResult { semanticType = mapperType, substitution = mapperSubstitution, supply = mapperSupply, constraints = mapperConstraints, error = None } ->
                                match freshTypeVariable(mapperSupply) with
                                    | (mappedType, mappedSupply) ->
                                        match freshTypeVariable(mappedSupply) with
                                            | (mapperRow, mapperRowSupply) ->
                                                let expectedMapper =
                                                    SemFunction(
                                                        applySubstitution(mapperSubstitution)(successType),
                                                        mappedType,
                                                        Some(mapperRow)
                                                    )
                                                in
                                                    match mergeUnification(
                                                        mapperSubstitution,
                                                        unify(
                                                            applySubstitution(mapperSubstitution)(mapperType),
                                                            expectedMapper
                                                        ),
                                                        mapperRowSupply,
                                                        mappedType
                                                    ) with
                                                        | TypeInferenceResult { semanticType = unifiedMappedType, substitution = unifiedSubstitution, supply = unifiedSupply, constraints = _unificationConstraints, error = None } ->
                                                            match subsumeCapabilityRow(
                                                                mapperRow,
                                                                environment,
                                                                ambientRow,
                                                                unifiedSubstitution,
                                                                unifiedSupply,
                                                                unifiedMappedType
                                                            ) with
                                                                | TypeInferenceResult { semanticType = subsumedMappedType, substitution = subsumedSubstitution, supply = subsumedSupply, constraints = _subsumptionConstraints, error = None } ->
                                                                    let resolvedMappedType =
                                                                        applySubstitution(
                                                                            subsumedSubstitution,
                                                                            subsumedMappedType
                                                                        )
                                                                    in
                                                                        match resultTypeShape(resolvedMappedType) with
                                                                            | None ->
                                                                                addConstraints(
                                                                                    appendConstraints(
                                                                                        leftConstraints,
                                                                                        mapperConstraints
                                                                                    ),
                                                                                    inferenceSuccess(
                                                                                        SemNamed(
                                                                                            symbolId,
                                                                                            "Result",
                                                                                            [applySubstitution(
                                                                                                subsumedSubstitution,
                                                                                                errorType
                                                                                            ), resolvedMappedType]
                                                                                        ),
                                                                                        subsumedSubstitution,
                                                                                        subsumedSupply
                                                                                    )
                                                                                )
                                                                            | Some(ResultTypeShape { symbolId = mappedSymbolId, errorType = mappedErrorType, successType = mappedSuccessType }) ->
                                                                                match mergeUnification(
                                                                                    subsumedSubstitution,
                                                                                    unify(
                                                                                        applySubstitution(
                                                                                            subsumedSubstitution,
                                                                                            errorType
                                                                                        ),
                                                                                        mappedErrorType
                                                                                    ),
                                                                                    subsumedSupply,
                                                                                    SemNamed(
                                                                                        mappedSymbolId,
                                                                                        "Result",
                                                                                        [
                                                                                            mappedErrorType,
                                                                                            mappedSuccessType
                                                                                        ]
                                                                                    )
                                                                                ) with
                                                                                    | success ->
                                                                                        addConstraints(
                                                                                            appendConstraints(
                                                                                                leftConstraints,
                                                                                                mapperConstraints
                                                                                            ),
                                                                                            success
                                                                                        )
                                                                | failure -> failure
                                                        | failure -> failure
                            | failure -> failure
        | failure -> failure
and inferResultErrorPipe left right environment substitution supply ambientRow =
    match inferWith(left)(environment)(substitution)(supply)(ambientRow) with
        | TypeInferenceResult { semanticType = leftType, substitution = leftSubstitution, supply = leftSupply, constraints = leftConstraints, error = None } ->
            let resolvedLeft = applySubstitution(leftSubstitution)(leftType)
            in
                match resultTypeShape(resolvedLeft) with
                    | None -> inferenceFailure(SemNever)(leftSubstitution)(leftSupply)(ExpectedResultType(resolvedLeft))
                    | Some(ResultTypeShape { symbolId = symbolId, errorType = errorType, successType = successType }) ->
                        match inferWith(right)(environment)(leftSubstitution)(leftSupply)(ambientRow) with
                            | TypeInferenceResult { semanticType = mapperType, substitution = mapperSubstitution, supply = mapperSupply, constraints = mapperConstraints, error = None } ->
                                match freshTypeVariable(mapperSupply) with
                                    | (mappedErrorType, mappedSupply) ->
                                        match freshTypeVariable(mappedSupply) with
                                            | (mapperRow, mapperRowSupply) ->
                                                let expectedMapper =
                                                    SemFunction(
                                                        applySubstitution(mapperSubstitution)(errorType),
                                                        mappedErrorType,
                                                        Some(mapperRow)
                                                    )
                                                in
                                                    let resultType =
                                                        SemNamed(
                                                            symbolId,
                                                            "Result",
                                                            [mappedErrorType, applySubstitution(
                                                                mapperSubstitution,
                                                                successType
                                                            )]
                                                        )
                                                    in
                                                        match mergeUnification(
                                                            mapperSubstitution,
                                                            unify(
                                                                applySubstitution(mapperSubstitution)(mapperType),
                                                                expectedMapper
                                                            ),
                                                            mapperRowSupply,
                                                            resultType
                                                        ) with
                                                            | TypeInferenceResult { semanticType = unifiedResultType, substitution = unifiedSubstitution, supply = unifiedSupply, constraints = _unificationConstraints, error = None } ->
                                                                addConstraints(
                                                                    appendConstraints(
                                                                        leftConstraints,
                                                                        mapperConstraints
                                                                    ),
                                                                    subsumeCapabilityRow(
                                                                        mapperRow,
                                                                        environment,
                                                                        ambientRow,
                                                                        unifiedSubstitution,
                                                                        unifiedSupply,
                                                                        unifiedResultType
                                                                    )
                                                                )
                                                            | failure -> failure
                            | failure -> failure
        | failure -> failure
and inferAwait task environment substitution supply ambientRow =
    match inferWith(task)(environment)(substitution)(supply)(ambientRow) with
        | TypeInferenceResult { semanticType = taskType, substitution = taskSubstitution, supply = taskSupply, constraints = taskConstraints, error = None } ->
            match freshTypeVariable(taskSupply) with
                | (errorType, errorSupply) ->
                    match freshTypeVariable(errorSupply) with
                        | (successType, successSupply) ->
                            let expectedTask = SemNamed(resolveTaskSymbolId(environment))("Task")([errorType, successType])
                            in
                                match mergeUnification(
                                    taskSubstitution,
                                    unify(applySubstitution(taskSubstitution)(taskType))(expectedTask),
                                    successSupply,
                                    expectedTask
                                ) with
                                    | TypeInferenceResult { substitution = unifiedSubstitution, supply = unifiedSupply, error = None } ->
                                        addConstraints(
                                            taskConstraints,
                                            inferenceSuccess(
                                                applySubstitution(unifiedSubstitution)(SemNamed(resolveResultSymbolId(environment))("Result")([errorType, successType])),
                                                unifiedSubstitution,
                                                unifiedSupply
                                            )
                                        )
                                    | TypeInferenceResult { substitution = failedSubstitution, supply = failedSupply } ->
                                        inferenceFailure(
                                            SemNever,
                                            failedSubstitution,
                                            failedSupply,
                                            ExpectedTaskType(applySubstitution(taskSubstitution)(taskType))
                                        )
        | failure -> failure
and inferLetResult name value body environment substitution supply ambientRow =
    match inferWith(value)(environment)(substitution)(supply)(ambientRow) with
        | TypeInferenceResult { semanticType = valueType, substitution = valueSubstitution, supply = valueSupply, constraints = valueConstraints, error = None } ->
            let resolvedValue = applySubstitution(valueSubstitution)(valueType)
            in
                match resultTypeShape(resolvedValue) with
                    | None ->
                        inferenceFailure(
                            SemNever,
                            valueSubstitution,
                            valueSupply,
                            ExpectedResultType(resolvedValue)
                        )
                    | Some(ResultTypeShape { symbolId = _symbolId, errorType = errorType, successType = successType }) ->
                        let binding = TypeScheme(quantified = [], body = successType, constraints = [])
                        in
                            match inferWith(
                                body,
                                addTypeBinding(name)(binding)(environment),
                                valueSubstitution,
                                valueSupply,
                                ambientRow
                            ) with
                                | TypeInferenceResult { semanticType = bodyType, substitution = bodySubstitution, supply = bodySupply, constraints = bodyConstraints, error = None } ->
                                    let resolvedBody = applySubstitution(bodySubstitution)(bodyType)
                                    in
                                        match resultTypeShape(resolvedBody) with
                                            | None ->
                                                inferenceFailure(
                                                    SemNever,
                                                    bodySubstitution,
                                                    bodySupply,
                                                    ExpectedResultType(resolvedBody)
                                                )
                                            | Some(ResultTypeShape { symbolId = bodySymbolId, errorType = bodyErrorType, successType = bodySuccessType }) ->
                                                let resultType =
                                                    SemNamed(
                                                        bodySymbolId,
                                                        "Result",
                                                        [bodyErrorType, bodySuccessType]
                                                    )
                                                in
                                                    addConstraints(
                                                        appendConstraints(valueConstraints)(bodyConstraints),
                                                        mergeUnification(
                                                            bodySubstitution,
                                                            unify(
                                                                applySubstitution(bodySubstitution)(errorType),
                                                                bodyErrorType
                                                            ),
                                                            bodySupply,
                                                            resultType
                                                        )
                                                    )
                                | failure -> failure
        | failure -> failure
and prepareHandlerOperationType operation capabilityType parameters substitution supply =
    match operation with
        | CapabilityOperationInferenceDefinition { name = operationName, scheme = scheme, hasExplicitSignature = hasExplicitSignature } ->
            match instantiate(scheme)(supply) with
                | InstantiationResult { semanticType = instantiatedType, constraints = _constraints, supply = instantiatedSupply } ->
                    if hasExplicitSignature
                    then
                        match capabilityType with
                            | SemCapability(capabilityName, _arguments) ->
                                match operationCapabilityFromType(capabilityName)(instantiatedType) with
                                    | None ->
                                        HandlerOperationTypePreparation(semanticType = SemNever, substitution = substitution, supply = instantiatedSupply, error = Some(
                                            InvalidHandler(
                                                "operation " + capabilityName + "." + operationName + " has no capability row"
                                            )
                                        ))
                                    | Some(operationCapability) ->
                                        match unify(
                                            applySubstitution(substitution)(operationCapability),
                                            applySubstitution(substitution)(capabilityType)
                                        ) with
                                            | UnificationResult { substitution = capabilitySubstitution, error = None } ->
                                                let combined = appendSubstitution(capabilitySubstitution)(substitution)
                                                in
                                                    HandlerOperationTypePreparation(semanticType = applySubstitution(
                                                        combined,
                                                        instantiatedType
                                                    ), substitution = combined, supply = instantiatedSupply, error = None)
                                            | UnificationResult { substitution = _capabilitySubstitution, error = Some(error) } ->
                                                HandlerOperationTypePreparation(semanticType = SemNever, substitution = substitution, supply = instantiatedSupply, error = Some(
                                                    InferenceUnificationError(error)
                                                ))
                            | _ ->
                                HandlerOperationTypePreparation(semanticType = SemNever, substitution = substitution, supply = instantiatedSupply, error = Some(
                                    InvalidHandler("handled capability has an invalid semantic type")
                                ))
                    else
                        match buildUnsignedOperationType(parameters)(capabilityType)(instantiatedSupply)([]) with
                            | HandlerOperationTypePreparation { semanticType = inferredOperationType, substitution = _inferredSubstitution, supply = inferredSupply, error = None } ->
                                match unify(
                                    applySubstitution(substitution)(instantiatedType),
                                    inferredOperationType
                                ) with
                                    | UnificationResult { substitution = operationSubstitution, error = None } ->
                                        let combined = appendSubstitution(operationSubstitution)(substitution)
                                        in
                                            HandlerOperationTypePreparation(semanticType = applySubstitution(
                                                combined,
                                                inferredOperationType
                                            ), substitution = combined, supply = inferredSupply, error = None)
                                    | UnificationResult { substitution = _operationSubstitution, error = Some(error) } ->
                                        HandlerOperationTypePreparation(semanticType = SemNever, substitution = substitution, supply = inferredSupply, error = Some(
                                            InferenceUnificationError(error)
                                        ))
                            | HandlerOperationTypePreparation { semanticType = _inferredOperationType, substitution = _inferredSubstitution, supply = inferredSupply, error = Some(error) } ->
                                HandlerOperationTypePreparation(semanticType = SemNever, substitution = substitution, supply = inferredSupply, error = Some(
                                    error
                                ))
and inferHandlerParameters patterns parameterTypes environment substitution supply names =
    match (patterns, parameterTypes) with
        | ([], []) -> HandlerParameterInference(environment = environment, substitution = substitution, supply = supply, error = None)
        | (pattern :: patternTail, parameterType :: typeTail) ->
            match inferPattern(pattern)(environment)(substitution)(supply)(names) with
                | PatternInferenceResult { semanticType = patternType, environment = patternEnvironment, substitution = patternSubstitution, supply = patternSupply, names = patternNames, error = None } ->
                    match mergePatternUnification(
                        patternSubstitution,
                        unify(
                            applySubstitution(patternSubstitution)(parameterType),
                            applySubstitution(patternSubstitution)(patternType)
                        ),
                        patternSupply,
                        parameterType,
                        patternEnvironment,
                        patternNames
                    ) with
                        | PatternInferenceResult { semanticType = _unifiedType, environment = unifiedEnvironment, substitution = unifiedSubstitution, supply = unifiedSupply, names = unifiedNames, error = None } ->
                            inferHandlerParameters(
                                patternTail,
                                typeTail,
                                unifiedEnvironment,
                                unifiedSubstitution,
                                unifiedSupply,
                                unifiedNames
                            )
                        | PatternInferenceResult { semanticType = _failedType, environment = _failedEnvironment, substitution = failedSubstitution, supply = failedSupply, names = _failedNames, error = Some(error) } ->
                            HandlerParameterInference(environment = environment, substitution = failedSubstitution, supply = failedSupply, error = Some(
                                error
                            ))
                | PatternInferenceResult { semanticType = _failedType, environment = _failedEnvironment, substitution = failedSubstitution, supply = failedSupply, names = _failedNames, error = Some(error) } ->
                    HandlerParameterInference(environment = environment, substitution = failedSubstitution, supply = failedSupply, error = Some(
                        error
                    ))
        | _ ->
            HandlerParameterInference(environment = environment, substitution = substitution, supply = supply, error = Some(
                InvalidHandler("operation arm parameter count does not match its signature")
            ))
and inferHandlerOperationArms arms handledCapabilities handlerResult environment substitution supply ambientRow accumulatedConstraints =
    match arms with
        | [] -> HandlerArmInference(substitution = substitution, supply = supply, constraints = accumulatedConstraints, error = None)
        | HandlerOperationArmDefinition { capabilityName = capabilityName, operationName = operationName, parameters = parameters, body = body } :: tail ->
            match (findHandledCapability(capabilityName)(handledCapabilities), resolveCapabilityOperation(
                capabilityName,
                operationName,
                environment
            )) with
                | (Some(HandledCapabilityDefinition { name = _name, semanticType = capabilityType, operations = _operations }), Some(operation)) ->
                    match prepareHandlerOperationType(operation)(capabilityType)(parameters)(substitution)(supply) with
                        | HandlerOperationTypePreparation { semanticType = operationType, substitution = operationSubstitution, supply = operationSupply, error = None } ->
                            match splitHandlerOperationType(
                                parameters,
                                applySubstitution(operationSubstitution)(operationType),
                                []
                            ) with
                                | None ->
                                    HandlerArmInference(substitution = operationSubstitution, supply = operationSupply, constraints = accumulatedConstraints, error = Some(
                                        InvalidHandler(
                                            "operation arm parameter count does not match " + capabilityName + "." + operationName
                                        )
                                    ))
                                | Some(HandlerOperationShape { parameters = parameterTypes, resultType = operationResultType }) ->
                                    match inferHandlerParameters(
                                        parameters,
                                        parameterTypes,
                                        environment,
                                        operationSubstitution,
                                        operationSupply,
                                        []
                                    ) with
                                        | HandlerParameterInference { environment = armEnvironment, substitution = parameterSubstitution, supply = parameterSupply, error = None } ->
                                            let resumeType =
                                                SemFunction(
                                                    applySubstitution(parameterSubstitution)(operationResultType),
                                                    applySubstitution(parameterSubstitution)(handlerResult),
                                                    None
                                                )
                                            in
                                                let resumeScheme = TypeScheme(quantified = [], body = resumeType, constraints = [])
                                                in
                                                    match inferWith(
                                                        body,
                                                        addTypeBinding("resume")(resumeScheme)(armEnvironment),
                                                        parameterSubstitution,
                                                        parameterSupply,
                                                        ambientRow
                                                    ) with
                                                        | TypeInferenceResult { semanticType = armType, substitution = armSubstitution, supply = armSupply, constraints = armConstraints, error = None } ->
                                                            match mergeUnification(
                                                                armSubstitution,
                                                                unify(
                                                                    applySubstitution(armSubstitution)(handlerResult),
                                                                    applySubstitution(armSubstitution)(armType)
                                                                ),
                                                                armSupply,
                                                                handlerResult
                                                            ) with
                                                                | TypeInferenceResult { semanticType = _unifiedHandlerType, substitution = unifiedSubstitution, supply = unifiedSupply, constraints = _unificationConstraints, error = None } ->
                                                                    inferHandlerOperationArms(
                                                                        tail,
                                                                        handledCapabilities,
                                                                        handlerResult,
                                                                        environment,
                                                                        unifiedSubstitution,
                                                                        unifiedSupply,
                                                                        ambientRow,
                                                                        appendConstraints(
                                                                            accumulatedConstraints,
                                                                            armConstraints
                                                                        )
                                                                    )
                                                                | TypeInferenceResult { semanticType = _failedType, substitution = failedSubstitution, supply = failedSupply, constraints = _failedConstraints, error = Some(error) } ->
                                                                    HandlerArmInference(substitution = failedSubstitution, supply = failedSupply, constraints = accumulatedConstraints, error = Some(
                                                                        error
                                                                    ))
                                                        | TypeInferenceResult { semanticType = _failedType, substitution = failedSubstitution, supply = failedSupply, constraints = _failedConstraints, error = Some(error) } ->
                                                            HandlerArmInference(substitution = failedSubstitution, supply = failedSupply, constraints = accumulatedConstraints, error = Some(
                                                                error
                                                            ))
                                        | HandlerParameterInference { environment = _armEnvironment, substitution = failedSubstitution, supply = failedSupply, error = Some(error) } ->
                                            HandlerArmInference(substitution = failedSubstitution, supply = failedSupply, constraints = accumulatedConstraints, error = Some(
                                                error
                                            ))
                        | HandlerOperationTypePreparation { semanticType = _operationType, substitution = failedSubstitution, supply = failedSupply, error = Some(error) } ->
                            HandlerArmInference(substitution = failedSubstitution, supply = failedSupply, constraints = accumulatedConstraints, error = Some(
                                error
                            ))
                | _ ->
                    HandlerArmInference(substitution = substitution, supply = supply, constraints = accumulatedConstraints, error = Some(
                        InvalidHandler("unknown operation " + capabilityName + "." + operationName)
                    ))
and unifyOrPatternBindings names expectedEnvironment actualEnvironment substitution supply resultType =
    match names with
        | [] ->
            patternSuccess(
                applySubstitution(substitution)(resultType),
                expectedEnvironment,
                substitution,
                supply,
                []
            )
        | name :: tail ->
            match (resolveTypeBinding(name)(expectedEnvironment), resolveTypeBinding(name)(actualEnvironment)) with
                | (Some(TypeScheme { quantified = _expectedQuantified, body = expectedType, constraints = _expectedConstraints }), Some(TypeScheme { quantified = _actualQuantified, body = actualType, constraints = _actualConstraints })) ->
                    match mergePatternUnification(
                        substitution,
                        unify(
                            applySubstitution(substitution)(expectedType),
                            applySubstitution(substitution)(actualType)
                        ),
                        supply,
                        resultType,
                        expectedEnvironment,
                        names
                    ) with
                        | PatternInferenceResult { semanticType = _unifiedType, environment = _unifiedEnvironment, substitution = unifiedSubstitution, supply = unifiedSupply, names = _unifiedNames, error = None } ->
                            unifyOrPatternBindings(
                                tail,
                                expectedEnvironment,
                                actualEnvironment,
                                unifiedSubstitution,
                                unifiedSupply,
                                resultType
                            )
                        | failure -> failure
                | _ ->
                    patternFailure(
                        SemNever,
                        expectedEnvironment,
                        substitution,
                        supply,
                        names,
                        InconsistentOrPatternBindings
                    )
and inferOrPatternAlternatives alternatives baseEnvironment expectedEnvironment expectedNames expectedType substitution supply outerNames =
    match alternatives with
        | [] ->
            match (expectedEnvironment, expectedType) with
                | (Some(environment), Some(resultType)) ->
                    patternSuccess(
                        applySubstitution(substitution)(resultType),
                        environment,
                        substitution,
                        supply,
                        expectedNames
                    )
                | _ ->
                    patternFailure(
                        SemNever,
                        baseEnvironment,
                        substitution,
                        supply,
                        outerNames,
                        UnsupportedInferencePattern("or-pattern must contain an alternative")
                    )
        | alternative :: tail ->
            match inferPattern(alternative)(baseEnvironment)(substitution)(supply)(outerNames) with
                | PatternInferenceResult { semanticType = alternativeType, environment = alternativeEnvironment, substitution = alternativeSubstitution, supply = alternativeSupply, names = alternativeNames, error = None } ->
                    match (expectedEnvironment, expectedType) with
                        | (None, None) ->
                            inferOrPatternAlternatives(
                                tail,
                                baseEnvironment,
                                Some(alternativeEnvironment),
                                alternativeNames,
                                Some(alternativeType),
                                alternativeSubstitution,
                                alternativeSupply,
                                outerNames
                            )
                        | (Some(firstEnvironment), Some(firstType)) ->
                            if samePatternNameSets(expectedNames)(alternativeNames)
                            then
                                match mergePatternUnification(
                                    alternativeSubstitution,
                                    unify(
                                        applySubstitution(alternativeSubstitution)(firstType),
                                        applySubstitution(alternativeSubstitution)(alternativeType)
                                    ),
                                    alternativeSupply,
                                    firstType,
                                    alternativeEnvironment,
                                    alternativeNames
                                ) with
                                    | PatternInferenceResult { semanticType = unifiedType, environment = _unifiedEnvironment, substitution = unifiedSubstitution, supply = unifiedSupply, names = _unifiedNames, error = None } ->
                                        match unifyOrPatternBindings(
                                            expectedNames,
                                            firstEnvironment,
                                            alternativeEnvironment,
                                            unifiedSubstitution,
                                            unifiedSupply,
                                            unifiedType
                                        ) with
                                            | PatternInferenceResult { semanticType = bindingType, environment = _bindingEnvironment, substitution = bindingSubstitution, supply = bindingSupply, names = _bindingNames, error = None } ->
                                                inferOrPatternAlternatives(
                                                    tail,
                                                    baseEnvironment,
                                                    Some(firstEnvironment),
                                                    expectedNames,
                                                    Some(bindingType),
                                                    bindingSubstitution,
                                                    bindingSupply,
                                                    outerNames
                                                )
                                            | failure -> failure
                                    | failure -> failure
                            else
                                patternFailure(
                                    SemNever,
                                    firstEnvironment,
                                    alternativeSubstitution,
                                    alternativeSupply,
                                    expectedNames,
                                    InconsistentOrPatternBindings
                                )
                        | _ ->
                            patternFailure(
                                SemNever,
                                baseEnvironment,
                                alternativeSubstitution,
                                alternativeSupply,
                                outerNames,
                                InconsistentOrPatternBindings
                            )
                | failure -> failure
and inferPattern pattern environment substitution supply names =
    match pattern with
        | PatternAt(_span, inner) -> inferPattern(inner)(environment)(substitution)(supply)(names)
        | PatternEmptyList ->
            match freshTypeVariable(supply) with
                | (elementType, nextSupply) ->
                    patternSuccess(
                        SemList(elementType),
                        environment,
                        substitution,
                        nextSupply,
                        names
                    )
        | PatternVar(name) ->
            if isNullaryConstructorName(name)(environment)
            then inferPattern(PatternConstructor(name)([]))(environment)(substitution)(supply)(names)
            else
                if patternNameExists(name)(names)
                then patternFailure(SemNever)(environment)(substitution)(supply)(names)(DuplicatePatternBinding(name))
                else
                    match freshTypeVariable(supply) with
                        | (variableType, nextSupply) ->
                            let scheme = TypeScheme(quantified = [], body = variableType, constraints = [])
                            in
                                patternSuccess(
                                    variableType,
                                    addTypeBinding(name)(scheme)(environment),
                                    substitution,
                                    nextSupply,
                                    name :: names
                                )
        | PatternWildcard ->
            match freshTypeVariable(supply) with
                | (wildcardType, nextSupply) ->
                    patternSuccess(
                        wildcardType,
                        environment,
                        substitution,
                        nextSupply,
                        names
                    )
        | PatternCons(head, tail) ->
            match inferPattern(head)(environment)(substitution)(supply)(names) with
                | PatternInferenceResult { semanticType = headType, environment = headEnvironment, substitution = headSubstitution, supply = headSupply, names = headNames, error = None } ->
                    match inferPattern(tail)(headEnvironment)(headSubstitution)(headSupply)(headNames) with
                        | PatternInferenceResult { semanticType = tailType, environment = tailEnvironment, substitution = tailSubstitution, supply = tailSupply, names = tailNames, error = None } ->
                            let listType = SemList(applySubstitution(tailSubstitution)(headType))
                            in
                                mergePatternUnification(
                                    tailSubstitution,
                                    unify(applySubstitution(tailSubstitution)(tailType))(listType),
                                    tailSupply,
                                    listType,
                                    tailEnvironment,
                                    tailNames
                                )
                        | failure -> failure
                | failure -> failure
        | PatternTuple(elements) ->
            match inferPatternList(elements)(environment)(substitution)(supply)(names)([]) with
                | PatternInferenceResult { semanticType = SemTuple(reversedTypes), environment = tupleEnvironment, substitution = tupleSubstitution, supply = tupleSupply, names = tupleNames, error = None } ->
                    patternSuccess(
                        SemTuple(reverse(reversedTypes)),
                        tupleEnvironment,
                        tupleSubstitution,
                        tupleSupply,
                        tupleNames
                    )
                | failure -> failure
        | PatternAs(inner, name) ->
            match inferPattern(inner)(environment)(substitution)(supply)(names) with
                | PatternInferenceResult { semanticType = innerType, environment = innerEnvironment, substitution = innerSubstitution, supply = innerSupply, names = innerNames, error = None } ->
                    if patternNameExists(name)(innerNames)
                    then
                        patternFailure(
                            innerType,
                            innerEnvironment,
                            innerSubstitution,
                            innerSupply,
                            innerNames,
                            DuplicatePatternBinding(name)
                        )
                    else
                        let scheme = TypeScheme(quantified = [], body = innerType, constraints = [])
                        in
                            patternSuccess(
                                innerType,
                                addTypeBinding(name)(scheme)(innerEnvironment),
                                innerSubstitution,
                                innerSupply,
                                name :: innerNames
                            )
                | failure -> failure
        | PatternConstructor(name, patterns) ->
            match resolveConstructorBinding(name)(environment) with
                | None ->
                    patternFailure(
                        SemNever,
                        environment,
                        substitution,
                        supply,
                        names,
                        UnknownPatternConstructor(name)
                    )
                | Some(ConstructorInferenceDefinition { name = _constructorName, scheme = scheme, fieldNames = _fieldNames }) ->
                    match instantiate(scheme)(supply) with
                        | InstantiationResult { semanticType = constructorType, constraints = _constraints, supply = constructorSupply } ->
                            match splitConstructorType(constructorType)([]) with
                                | ConstructorTypeShape { parameters = parameterTypes, resultType = resultType } ->
                                    inferConstructorPatternArguments(
                                        name,
                                        patterns,
                                        parameterTypes,
                                        resultType,
                                        environment,
                                        substitution,
                                        constructorSupply,
                                        names
                                    )
        | PatternRecord(name, fields) ->
            match resolveConstructorBinding(name)(environment) with
                | None ->
                    patternFailure(
                        SemNever,
                        environment,
                        substitution,
                        supply,
                        names,
                        UnknownPatternConstructor(name)
                    )
                | Some(ConstructorInferenceDefinition { name = _constructorName, scheme = scheme, fieldNames = fieldNames }) ->
                    match instantiate(scheme)(supply) with
                        | InstantiationResult { semanticType = constructorType, constraints = _constraints, supply = constructorSupply } ->
                            match splitConstructorType(constructorType)([]) with
                                | ConstructorTypeShape { parameters = fieldTypes, resultType = resultType } ->
                                    inferRecordPatternFields(
                                        name,
                                        fields,
                                        fieldNames,
                                        fieldTypes,
                                        resultType,
                                        environment,
                                        substitution,
                                        constructorSupply,
                                        names,
                                        []
                                    )
        | PatternOr(alternatives) ->
            inferOrPatternAlternatives(
                alternatives,
                environment,
                None,
                [],
                None,
                substitution,
                supply,
                names
            )
        | PatternInt(_) -> patternSuccess(SemInt)(environment)(substitution)(supply)(names)
        | PatternString(_) -> patternSuccess(SemString)(environment)(substitution)(supply)(names)
        | PatternRune(_) -> patternSuccess(SemRune)(environment)(substitution)(supply)(names)
        | PatternBool(_) -> patternSuccess(SemBool)(environment)(substitution)(supply)(names)
        | _ ->
            patternFailure(
                SemNever,
                environment,
                substitution,
                supply,
                names,
                UnsupportedInferencePattern("pattern case is not implemented yet")
            )
and inferMatchCases cases scrutineeType resultType environment substitution supply ambientRow accumulatedConstraints =
    match cases with
        | [] ->
            addConstraints(
                accumulatedConstraints,
                inferenceSuccess(applySubstitution(substitution)(resultType))(substitution)(supply)
            )
        | (pattern, body, guard) :: tail ->
            match inferPattern(pattern)(environment)(substitution)(supply)([]) with
                | PatternInferenceResult { semanticType = patternType, environment = patternEnvironment, substitution = patternSubstitution, supply = patternSupply, names = _patternNames, error = None } ->
                    match mergePatternUnification(
                        patternSubstitution,
                        unify(
                            applySubstitution(patternSubstitution)(scrutineeType),
                            applySubstitution(patternSubstitution)(patternType)
                        ),
                        patternSupply,
                        scrutineeType,
                        patternEnvironment,
                        []
                    ) with
                        | PatternInferenceResult { semanticType = _matchedType, environment = matchedEnvironment, substitution = matchedSubstitution, supply = matchedSupply, names = _matchedNames, error = None } ->
                            inferMatchGuard(
                                guard,
                                body,
                                tail,
                                scrutineeType,
                                resultType,
                                environment,
                                matchedEnvironment,
                                matchedSubstitution,
                                matchedSupply,
                                ambientRow,
                                accumulatedConstraints
                            )
                        | PatternInferenceResult { semanticType = failedType, environment = _failedEnvironment, substitution = failedSubstitution, supply = failedSupply, names = _failedNames, error = Some(error) } ->
                            inferenceFailure(
                                failedType,
                                failedSubstitution,
                                failedSupply,
                                error
                            )
                | PatternInferenceResult { semanticType = failedType, environment = _failedEnvironment, substitution = failedSubstitution, supply = failedSupply, names = _failedNames, error = Some(error) } ->
                    inferenceFailure(
                        failedType,
                        failedSubstitution,
                        failedSupply,
                        error
                    )
and inferMatchGuard guard body tail scrutineeType resultType environment patternEnvironment substitution supply ambientRow accumulatedConstraints =
    match guard with
        | None ->
            inferMatchBody(
                body,
                tail,
                scrutineeType,
                resultType,
                environment,
                patternEnvironment,
                substitution,
                supply,
                ambientRow,
                accumulatedConstraints
            )
        | Some(guardExpression) ->
            match inferWith(guardExpression)(patternEnvironment)(substitution)(supply)(ambientRow) with
                | TypeInferenceResult { semanticType = guardType, substitution = guardSubstitution, supply = guardSupply, constraints = guardConstraints, error = None } ->
                    match mergeUnification(
                        guardSubstitution,
                        unify(applySubstitution(guardSubstitution)(guardType))(SemBool),
                        guardSupply,
                        SemBool
                    ) with
                        | TypeInferenceResult { semanticType = _booleanType, substitution = booleanSubstitution, supply = booleanSupply, constraints = _unificationConstraints, error = None } ->
                            inferMatchBody(
                                body,
                                tail,
                                scrutineeType,
                                resultType,
                                environment,
                                patternEnvironment,
                                booleanSubstitution,
                                booleanSupply,
                                ambientRow,
                                appendConstraints(accumulatedConstraints)(guardConstraints)
                            )
                        | failure -> failure
                | failure -> failure
and inferMatchBody body tail scrutineeType resultType environment patternEnvironment substitution supply ambientRow accumulatedConstraints =
    match inferWith(body)(patternEnvironment)(substitution)(supply)(ambientRow) with
        | TypeInferenceResult { semanticType = bodyType, substitution = bodySubstitution, supply = bodySupply, constraints = bodyConstraints, error = None } ->
            match mergeUnification(
                bodySubstitution,
                unify(applySubstitution(bodySubstitution)(resultType))(applySubstitution(bodySubstitution)(bodyType)),
                bodySupply,
                resultType
            ) with
                | TypeInferenceResult { semanticType = _unifiedResult, substitution = resultSubstitution, supply = resultSupply, constraints = _unificationConstraints, error = None } ->
                    inferMatchCases(
                        tail,
                        scrutineeType,
                        resultType,
                        environment,
                        resultSubstitution,
                        resultSupply,
                        ambientRow,
                        appendConstraints(accumulatedConstraints)(bodyConstraints)
                    )
                | failure -> failure
        | failure -> failure
and inferBinaryTrait traitName returnsBool left right environment substitution supply ambientRow =
    match inferWith(left)(environment)(substitution)(supply)(ambientRow) with
        | TypeInferenceResult { semanticType = leftType, substitution = leftSubstitution, supply = leftSupply, constraints = leftConstraints, error = None } ->
            match inferWith(right)(environment)(leftSubstitution)(leftSupply)(ambientRow) with
                | TypeInferenceResult { semanticType = rightType, substitution = rightSubstitution, supply = rightSupply, constraints = rightConstraints, error = None } ->
                    let unification =
                        unify(
                            applySubstitution(rightSubstitution)(leftType),
                            applySubstitution(rightSubstitution)(rightType)
                        )
                    in
                        match mergeUnification(rightSubstitution)(unification)(rightSupply)(leftType) with
                            | TypeInferenceResult { semanticType = unifiedType, substitution = unifiedSubstitution, supply = unifiedSupply, constraints = _unificationConstraints, error = None } ->
                                let resultType =
                                    if returnsBool
                                    then SemBool
                                    else unifiedType
                                in
                                    let constraint =
                                        TraitConstraint(traitName = traitName, typeArguments = [applySubstitution(
                                            unifiedSubstitution,
                                            unifiedType
                                        )])
                                    in
                                        addConstraints(
                                            appendConstraints(
                                                leftConstraints,
                                                appendConstraints(rightConstraints)([constraint])
                                            ),
                                            inferenceSuccess(resultType)(unifiedSubstitution)(unifiedSupply)
                                        )
                            | failure -> failure
                | failure -> failure
        | failure -> failure
and inferUnaryTrait traitName operand environment substitution supply ambientRow =
    match inferWith(operand)(environment)(substitution)(supply)(ambientRow) with
        | TypeInferenceResult { semanticType = operandType, substitution = operandSubstitution, supply = operandSupply, constraints = operandConstraints, error = None } ->
            let resolvedOperand = applySubstitution(operandSubstitution)(operandType)
            in
                let constraint = TraitConstraint(traitName = traitName, typeArguments = [resolvedOperand])
                in
                    addConstraints(
                        appendConstraints(operandConstraints)([constraint]),
                        inferenceSuccess(resolvedOperand)(operandSubstitution)(operandSupply)
                    )
        | failure -> failure
and inferHandlerReturn returnArm bodyType handlerResult environment substitution supply ambientRow accumulatedConstraints =
    match returnArm with
        | None ->
            addConstraints(
                accumulatedConstraints,
                mergeUnification(
                    substitution,
                    unify(applySubstitution(substitution)(handlerResult))(applySubstitution(substitution)(bodyType)),
                    supply,
                    handlerResult
                )
            )
        | Some((pattern, returnBody)) ->
            match inferPattern(pattern)(environment)(substitution)(supply)([]) with
                | PatternInferenceResult { semanticType = patternType, environment = returnEnvironment, substitution = patternSubstitution, supply = patternSupply, names = _names, error = None } ->
                    match mergePatternUnification(
                        patternSubstitution,
                        unify(
                            applySubstitution(patternSubstitution)(bodyType),
                            applySubstitution(patternSubstitution)(patternType)
                        ),
                        patternSupply,
                        bodyType,
                        returnEnvironment,
                        []
                    ) with
                        | PatternInferenceResult { semanticType = _unifiedBodyType, environment = unifiedEnvironment, substitution = unifiedSubstitution, supply = unifiedSupply, names = _unifiedNames, error = None } ->
                            match inferWith(
                                returnBody,
                                unifiedEnvironment,
                                unifiedSubstitution,
                                unifiedSupply,
                                ambientRow
                            ) with
                                | TypeInferenceResult { semanticType = returnType, substitution = returnSubstitution, supply = returnSupply, constraints = returnConstraints, error = None } ->
                                    addConstraints(
                                        appendConstraints(accumulatedConstraints)(returnConstraints),
                                        mergeUnification(
                                            returnSubstitution,
                                            unify(
                                                applySubstitution(returnSubstitution)(handlerResult),
                                                applySubstitution(returnSubstitution)(returnType)
                                            ),
                                            returnSupply,
                                            handlerResult
                                        )
                                    )
                                | failure -> failure
                        | PatternInferenceResult { semanticType = failedType, environment = _failedEnvironment, substitution = failedSubstitution, supply = failedSupply, names = _failedNames, error = Some(error) } ->
                            inferenceFailure(
                                failedType,
                                failedSubstitution,
                                failedSupply,
                                error
                            )
                | PatternInferenceResult { semanticType = failedType, environment = _failedEnvironment, substitution = failedSubstitution, supply = failedSupply, names = _failedNames, error = Some(error) } ->
                    inferenceFailure(
                        failedType,
                        failedSubstitution,
                        failedSupply,
                        error
                    )
and inferHandler body arms environment substitution supply ambientRow =
    match collectHandlerArms(arms)(environment)([])([])(None) with
        | HandlerArmCollection { operationArms = _operationArms, capabilityNames = _capabilityNames, returnArm = _returnArm, error = Some(error) } ->
            inferenceFailure(
                SemNever,
                substitution,
                supply,
                error
            )
        | HandlerArmCollection { operationArms = operationArms, capabilityNames = capabilityNames, returnArm = returnArm, error = None } ->
            match findMissingHandlerOperation(capabilityNames)(environment)(operationArms) with
                | Some(missing) ->
                    inferenceFailure(
                        SemNever,
                        substitution,
                        supply,
                        InvalidHandler("missing operation arm " + missing)
                    )
                | None ->
                    match prepareHandledCapabilities(capabilityNames)(environment)(supply)([]) with
                        | HandledCapabilityPreparation { capabilities = _handledCapabilities, supply = failedSupply, error = Some(error) } ->
                            inferenceFailure(
                                SemNever,
                                substitution,
                                failedSupply,
                                error
                            )
                        | HandledCapabilityPreparation { capabilities = handledCapabilities, supply = capabilitySupply, error = None } ->
                            match freshTypeVariable(capabilitySupply) with
                                | (handlerResult, handlerResultSupply) ->
                                    match inferHandlerOperationArms(
                                        operationArms,
                                        handledCapabilities,
                                        handlerResult,
                                        environment,
                                        substitution,
                                        handlerResultSupply,
                                        ambientRow,
                                        []
                                    ) with
                                        | HandlerArmInference { substitution = failedSubstitution, supply = failedSupply, constraints = _armConstraints, error = Some(error) } ->
                                            inferenceFailure(
                                                SemNever,
                                                failedSubstitution,
                                                failedSupply,
                                                error
                                            )
                                        | HandlerArmInference { substitution = armSubstitution, supply = armSupply, constraints = armConstraints, error = None } ->
                                            match freshTypeVariable(armSupply) with
                                                | (bodyTail, bodyTailSupply) ->
                                                    let bodyAmbient =
                                                        SemRow(
                                                            handledCapabilityTypes(handledCapabilities),
                                                            Some(bodyTail)
                                                        )
                                                    in
                                                        match inferWith(
                                                            body,
                                                            withHandledCapabilities(capabilityNames)(environment),
                                                            armSubstitution,
                                                            bodyTailSupply,
                                                            bodyAmbient
                                                        ) with
                                                            | TypeInferenceResult { semanticType = bodyType, substitution = bodySubstitution, supply = bodySupply, constraints = bodyConstraints, error = None } ->
                                                                match subsumeCapabilityRow(
                                                                    bodyTail,
                                                                    environment,
                                                                    ambientRow,
                                                                    bodySubstitution,
                                                                    bodySupply,
                                                                    bodyType
                                                                ) with
                                                                    | TypeInferenceResult { semanticType = subsumedBodyType, substitution = subsumedSubstitution, supply = subsumedSupply, constraints = _subsumptionConstraints, error = None } ->
                                                                        inferHandlerReturn(
                                                                            returnArm,
                                                                            subsumedBodyType,
                                                                            handlerResult,
                                                                            environment,
                                                                            subsumedSubstitution,
                                                                            subsumedSupply,
                                                                            ambientRow,
                                                                            appendConstraints(
                                                                                armConstraints,
                                                                                bodyConstraints
                                                                            )
                                                                        )
                                                                    | failure -> failure
                                                            | failure -> failure
and inferCalledFunction expression environment substitution supply ambientRow =
    match expression with
        | ExprAt(_span, inner) -> inferCalledFunction(inner)(environment)(substitution)(supply)(ambientRow)
        | ExprVar(name) ->
            match resolveExternalFunctionBinding(name)(environment) with
                | None -> inferWith(expression)(environment)(substitution)(supply)(ambientRow)
                | Some(ExternalFunctionInferenceDefinition { directScheme = scheme }) ->
                    match instantiate(scheme)(supply) with
                        | InstantiationResult { semanticType = instantiatedType, constraints = instantiatedConstraints, supply = nextSupply } ->
                            addConstraints(
                                instantiatedConstraints,
                                inferenceSuccess(
                                    applySubstitution(substitution)(instantiatedType),
                                    substitution,
                                    nextSupply
                                )
                            )
        | ExprQualifiedVar(capabilityName, operationName) ->
            match resolveCapabilityBinding(capabilityName)(environment) with
                | None -> inferWith(expression)(environment)(substitution)(supply)(ambientRow)
                | Some(_) ->
                    match resolveCapabilityOperation(capabilityName)(operationName)(environment) with
                        | None ->
                            inferenceFailure(
                                SemNever,
                                substitution,
                                supply,
                                UnknownCapabilityOperation(capabilityName)(operationName)
                            )
                        | Some(CapabilityOperationInferenceDefinition { name = _name, scheme = scheme, hasExplicitSignature = _hasExplicitSignature }) ->
                            match instantiate(scheme)(supply) with
                                | InstantiationResult { semanticType = instantiatedType, constraints = instantiatedConstraints, supply = nextSupply } ->
                                    addConstraints(
                                        instantiatedConstraints,
                                        inferenceSuccess(
                                            applySubstitution(substitution)(instantiatedType),
                                            substitution,
                                            nextSupply
                                        )
                                    )
        | _ -> inferWith(expression)(environment)(substitution)(supply)(ambientRow)
and inferWith expression environment substitution supply ambientRow =
    match expression with
        | ExprAt(_span, inner) -> inferWith(inner)(environment)(substitution)(supply)(ambientRow)
        | ExprInt(_) -> inferenceSuccess(SemInt)(substitution)(supply)
        | ExprBigInt(_) -> inferenceSuccess(SemBigInt)(substitution)(supply)
        | ExprUInt(_value, bits, _text) -> inferenceSuccess(SemUInt(bits))(substitution)(supply)
        | ExprFloat(_value, _text) -> inferenceSuccess(SemFloat)(substitution)(supply)
        | ExprString(_) -> inferenceSuccess(SemString)(substitution)(supply)
        | ExprRune(_) -> inferenceSuccess(SemRune)(substitution)(supply)
        | ExprBool(_) -> inferenceSuccess(SemBool)(substitution)(supply)
        | ExprVar(name) ->
            match resolveExternalFunctionBinding(name)(environment) with
                | Some(ExternalFunctionInferenceDefinition { firstClassScheme = None }) ->
                    inferenceFailure(
                        SemNever,
                        substitution,
                        supply,
                        ExternalFunctionRequiresDirectCall(name)
                    )
                | _ ->
                    match resolveTypeBinding(name)(environment) with
                        | None -> inferenceFailure(SemNever)(substitution)(supply)(UnknownValue(name))
                        | Some(scheme) ->
                            match instantiate(scheme)(supply) with
                                | InstantiationResult { semanticType = instantiatedType, constraints = instantiatedConstraints, supply = nextSupply } ->
                                    addConstraints(
                                        instantiatedConstraints,
                                        inferenceSuccess(
                                            applySubstitution(substitution)(instantiatedType),
                                            substitution,
                                            nextSupply
                                        )
                                    )
        | ExprQualifiedVar(moduleName, name) ->
            match resolveCapabilityBinding(moduleName)(environment) with
                | None -> inferWith(ExprVar(moduleName + "." + name))(environment)(substitution)(supply)(ambientRow)
                | Some(_) ->
                    match resolveCapabilityOperation(moduleName)(name)(environment) with
                        | None ->
                            inferenceFailure(
                                SemNever,
                                substitution,
                                supply,
                                UnknownCapabilityOperation(moduleName)(name)
                            )
                        | Some(CapabilityOperationInferenceDefinition { name = _operationName, scheme = _scheme, hasExplicitSignature = false }) ->
                            inferenceFailure(
                                SemNever,
                                substitution,
                                supply,
                                UnsignedCapabilityOperationRequiresSignature(moduleName)(name)
                            )
                        | Some(CapabilityOperationInferenceDefinition { name = _operationName, scheme = scheme, hasExplicitSignature = true }) ->
                            match instantiate(scheme)(supply) with
                                | InstantiationResult { semanticType = instantiatedType, constraints = instantiatedConstraints, supply = nextSupply } ->
                                    addConstraints(
                                        instantiatedConstraints,
                                        inferenceSuccess(
                                            applySubstitution(substitution)(instantiatedType),
                                            substitution,
                                            nextSupply
                                        )
                                    )
        | ExprPerform(operation) ->
            match capabilityOperationCallRoot(operation)(false) with
                | None -> inferenceFailure(SemNever)(substitution)(supply)(PerformRequiresCapabilityOperation)
                | Some((capabilityName, operationName)) ->
                    match resolveCapabilityOperation(capabilityName)(operationName)(environment) with
                        | None ->
                            inferenceFailure(
                                SemNever,
                                substitution,
                                supply,
                                UnknownCapabilityOperation(capabilityName)(operationName)
                            )
                        | Some(_) -> inferWith(operation)(environment)(substitution)(supply)(ambientRow)
        | ExprHandle(body, arms) -> inferHandler(body)(arms)(environment)(substitution)(supply)(ambientRow)
        | ExprLambda(name, body, annotation) ->
            match freshTypeVariable(supply) with
                | (parameterType, afterParameter) ->
                    let annotationResult =
                        match annotation with
                            | None -> inferenceSuccess(parameterType)(substitution)(afterParameter)
                            | Some(typeExpression) ->
                                checkInferenceAnnotation(
                                    typeExpression,
                                    parameterType,
                                    environment,
                                    substitution,
                                    afterParameter
                                )
                    in
                        match annotationResult with
                            | TypeInferenceResult { semanticType = checkedParameter, substitution = annotationSubstitution, supply = annotationSupply, constraints = _annotationConstraints, error = None } ->
                                let parameterScheme = TypeScheme(quantified = [], body = checkedParameter, constraints = [])
                                in
                                    let bodyEnvironment = addTypeBinding(name)(parameterScheme)(environment)
                                    in
                                        match freshTypeVariable(annotationSupply) with
                                            | (bodyAmbientRow, bodyAmbientSupply) ->
                                                match inferWith(
                                                    body,
                                                    bodyEnvironment,
                                                    annotationSubstitution,
                                                    bodyAmbientSupply,
                                                    bodyAmbientRow
                                                ) with
                                                    | TypeInferenceResult { semanticType = bodyType, substitution = bodySubstitution, supply = bodySupply, constraints = bodyConstraints, error = None } ->
                                                        let resolvedParameter =
                                                            applySubstitution(
                                                                bodySubstitution,
                                                                checkedParameter
                                                            )
                                                        in
                                                            let resolvedBody =
                                                                applySubstitution(
                                                                    bodySubstitution,
                                                                    bodyType
                                                                )
                                                            in
                                                                let resolvedBodyRow =
                                                                    applySubstitution(
                                                                        bodySubstitution,
                                                                        bodyAmbientRow
                                                                    )
                                                                in
                                                                    addConstraints(
                                                                        bodyConstraints,
                                                                        inferenceSuccess(
                                                                            SemFunction(
                                                                                resolvedParameter,
                                                                                resolvedBody,
                                                                                Some(resolvedBodyRow)
                                                                            ),
                                                                            bodySubstitution,
                                                                            bodySupply
                                                                        )
                                                                    )
                                                    | failure -> failure
                            | failure -> failure
        | ExprCall(function, argument, _whitespace, _layout) ->
            match inferCalledFunction(function)(environment)(substitution)(supply)(ambientRow) with
                | TypeInferenceResult { semanticType = functionType, substitution = functionSubstitution, supply = functionSupply, constraints = functionConstraints, error = None } ->
                    match inferWith(argument)(environment)(functionSubstitution)(functionSupply)(ambientRow) with
                        | TypeInferenceResult { semanticType = argumentType, substitution = argumentSubstitution, supply = argumentSupply, constraints = argumentConstraints, error = None } ->
                            match freshTypeVariable(argumentSupply) with
                                | (resultType, resultSupply) ->
                                    match freshTypeVariable(resultSupply) with
                                        | (callRow, callRowSupply) ->
                                            let expectedFunction = SemFunction(argumentType)(resultType)(Some(callRow))
                                            in
                                                let unification =
                                                    unify(
                                                        applySubstitution(argumentSubstitution)(functionType),
                                                        expectedFunction
                                                    )
                                                in
                                                    match mergeUnification(
                                                        argumentSubstitution,
                                                        unification,
                                                        callRowSupply,
                                                        resultType
                                                    ) with
                                                        | TypeInferenceResult { semanticType = unifiedResultType, substitution = unifiedSubstitution, supply = unifiedSupply, constraints = _unificationConstraints, error = None } ->
                                                            let callResult =
                                                                subsumeCapabilityRow(
                                                                    callRow,
                                                                    environment,
                                                                    ambientRow,
                                                                    unifiedSubstitution,
                                                                    unifiedSupply,
                                                                    unifiedResultType
                                                                )
                                                            in
                                                                addConstraints(
                                                                    appendConstraints(
                                                                        functionConstraints,
                                                                        argumentConstraints
                                                                    ),
                                                                    subsumeUnsignedCapabilityOperation(
                                                                        function,
                                                                        environment,
                                                                        ambientRow,
                                                                        callResult
                                                                    )
                                                                )
                                                        | failure -> failure
                        | failure -> failure
                | failure -> failure
        // The ambient capability row belongs to the surrounding environment; only the successful
        // value type and its selected trait constraints are generalized for the body.
        | ExprLet(name, value, body, _parameters, annotation, requirements) ->
            match inferWith(value)(environment)(substitution)(supply)(ambientRow) with
                | TypeInferenceResult { semanticType = valueType, substitution = valueSubstitution, supply = valueSupply, constraints = valueConstraints, error = None } ->
                    match checkInferenceBindingSignature(
                        annotation,
                        requirements,
                        valueType,
                        valueConstraints,
                        environment,
                        valueSubstitution,
                        valueSupply
                    ) with
                        | TypeInferenceResult { semanticType = checkedValue, substitution = checkedSubstitution, supply = checkedSupply, constraints = selectedConstraints, error = None } ->
                            let resolvedValue = applySubstitution(checkedSubstitution)(checkedValue)
                            in
                                let scheme =
                                    generalize(
                                        inferenceEnvironmentSchemes(environment),
                                        resolvedValue,
                                        selectedConstraints
                                    )
                                in
                                    let bodyEnvironment = addTypeBinding(name)(scheme)(environment)
                                    in inferWith(body)(bodyEnvironment)(checkedSubstitution)(checkedSupply)(ambientRow)
                        | failure -> failure
                | failure -> failure
        | ExprLetRecursive(name, value, body, _parameters, annotation, requirements) ->
            match freshTypeVariable(supply) with
                | (recursiveType, afterRecursive) ->
                    let recursiveScheme = TypeScheme(quantified = [], body = recursiveType, constraints = [])
                    in
                        let recursiveEnvironment = addTypeBinding(name)(recursiveScheme)(environment)
                        in
                            match inferWith(value)(recursiveEnvironment)(substitution)(afterRecursive)(ambientRow) with
                                | TypeInferenceResult { semanticType = valueType, substitution = valueSubstitution, supply = valueSupply, constraints = valueConstraints, error = None } ->
                                    let unification =
                                        unify(
                                            applySubstitution(valueSubstitution)(recursiveType),
                                            applySubstitution(valueSubstitution)(valueType)
                                        )
                                    in
                                        match mergeUnification(
                                            valueSubstitution,
                                            unification,
                                            valueSupply,
                                            recursiveType
                                        ) with
                                            | TypeInferenceResult { semanticType = unifiedType, substitution = unifiedSubstitution, supply = unifiedSupply, constraints = _unificationConstraints, error = None } ->
                                                match checkInferenceBindingSignature(
                                                    annotation,
                                                    requirements,
                                                    unifiedType,
                                                    valueConstraints,
                                                    environment,
                                                    unifiedSubstitution,
                                                    unifiedSupply
                                                ) with
                                                    | TypeInferenceResult { semanticType = checkedType, substitution = checkedSubstitution, supply = checkedSupply, constraints = selectedConstraints, error = None } ->
                                                        let resolvedType =
                                                            applySubstitution(
                                                                checkedSubstitution,
                                                                checkedType
                                                            )
                                                        in
                                                            let scheme =
                                                                generalize(
                                                                    inferenceEnvironmentSchemes(environment),
                                                                    resolvedType,
                                                                    selectedConstraints
                                                                )
                                                            in
                                                                inferWith(
                                                                    body,
                                                                    addTypeBinding(name)(scheme)(environment),
                                                                    checkedSubstitution,
                                                                    checkedSupply,
                                                                    ambientRow
                                                                )
                                                    | failure -> failure
                                            | failure -> failure
                                | failure -> failure
        | ExprIf(condition, thenBranch, elseBranch) ->
            match inferWith(condition)(environment)(substitution)(supply)(ambientRow) with
                | TypeInferenceResult { semanticType = conditionType, substitution = conditionSubstitution, supply = conditionSupply, constraints = conditionConstraints, error = None } ->
                    let conditionUnification = unify(applySubstitution(conditionSubstitution)(conditionType))(SemBool)
                    in
                        match mergeUnification(
                            conditionSubstitution,
                            conditionUnification,
                            conditionSupply,
                            SemBool
                        ) with
                            | TypeInferenceResult { semanticType = _booleanType, substitution = booleanSubstitution, supply = booleanSupply, constraints = _unificationConstraints, error = None } ->
                                match inferWith(
                                    thenBranch,
                                    environment,
                                    booleanSubstitution,
                                    booleanSupply,
                                    ambientRow
                                ) with
                                    | TypeInferenceResult { semanticType = thenType, substitution = thenSubstitution, supply = thenSupply, constraints = thenConstraints, error = None } ->
                                        match inferWith(
                                            elseBranch,
                                            environment,
                                            thenSubstitution,
                                            thenSupply,
                                            ambientRow
                                        ) with
                                            | TypeInferenceResult { semanticType = elseType, substitution = elseSubstitution, supply = elseSupply, constraints = elseConstraints, error = None } ->
                                                let branchUnification =
                                                    unify(
                                                        applySubstitution(elseSubstitution)(thenType),
                                                        applySubstitution(elseSubstitution)(elseType)
                                                    )
                                                in
                                                    addConstraints(
                                                        appendConstraints(
                                                            conditionConstraints,
                                                            appendConstraints(thenConstraints)(elseConstraints)
                                                        ),
                                                        mergeUnification(
                                                            elseSubstitution,
                                                            branchUnification,
                                                            elseSupply,
                                                            thenType
                                                        )
                                                    )
                                            | failure -> failure
                                    | failure -> failure
                            | failure -> failure
                | failure -> failure
        | ExprTuple(elements) ->
            match inferExpressions(elements)(environment)(substitution)(supply)(ambientRow)([]) with
                | TypeInferenceResult { semanticType = SemTuple(reversedTypes), substitution = tupleSubstitution, supply = tupleSupply, constraints = tupleConstraints, error = None } ->
                    addConstraints(
                        tupleConstraints,
                        inferenceSuccess(SemTuple(reverse(reversedTypes)))(tupleSubstitution)(tupleSupply)
                    )
                | failure -> failure
        | ExprList(elements, _isMultiline) ->
            match freshTypeVariable(supply) with
                | (elementType, nextSupply) ->
                    inferListElements(
                        elements,
                        elementType,
                        environment,
                        substitution,
                        nextSupply,
                        ambientRow
                    )
        | ExprCons(head, tail) ->
            match inferWith(head)(environment)(substitution)(supply)(ambientRow) with
                | TypeInferenceResult { semanticType = headType, substitution = headSubstitution, supply = headSupply, constraints = headConstraints, error = None } ->
                    match inferWith(tail)(environment)(headSubstitution)(headSupply)(ambientRow) with
                        | TypeInferenceResult { semanticType = tailType, substitution = tailSubstitution, supply = tailSupply, constraints = tailConstraints, error = None } ->
                            let unification =
                                unify(
                                    applySubstitution(tailSubstitution)(tailType),
                                    SemList(applySubstitution(tailSubstitution)(headType))
                                )
                            in
                                addConstraints(
                                    appendConstraints(headConstraints)(tailConstraints),
                                    mergeUnification(tailSubstitution)(unification)(tailSupply)(tailType)
                                )
                        | failure -> failure
                | failure -> failure
        | ExprRecord(name, fields, _isMultiline) ->
            match resolveConstructorBinding(name)(environment) with
                | None -> inferenceFailure(SemNever)(substitution)(supply)(UnknownRecordType(name))
                | Some(ConstructorInferenceDefinition { name = _constructorName, scheme = scheme, fieldNames = fieldNames }) ->
                    match instantiate(scheme)(supply) with
                        | InstantiationResult { semanticType = constructorType, constraints = constructorConstraints, supply = constructorSupply } ->
                            match splitConstructorType(constructorType)([]) with
                                | ConstructorTypeShape { parameters = fieldTypes, resultType = resultType } ->
                                    addConstraints(
                                        constructorConstraints,
                                        inferRecordExpressionFields(
                                            name,
                                            fields,
                                            fieldNames,
                                            fieldTypes,
                                            resultType,
                                            true,
                                            environment,
                                            substitution,
                                            constructorSupply,
                                            ambientRow,
                                            [],
                                            []
                                        )
                                    )
        | ExprRecordUpdate(target, fields) ->
            match inferWith(target)(environment)(substitution)(supply)(ambientRow) with
                | TypeInferenceResult { semanticType = targetType, substitution = targetSubstitution, supply = targetSupply, constraints = targetConstraints, error = None } ->
                    match applySubstitution(targetSubstitution)(targetType) with
                        | SemNamed(_symbolId, name, _arguments) ->
                            match resolveConstructorBinding(name)(environment) with
                                | None ->
                                    inferenceFailure(
                                        SemNever,
                                        targetSubstitution,
                                        targetSupply,
                                        UnknownRecordType(name)
                                    )
                                | Some(ConstructorInferenceDefinition { name = _constructorName, scheme = scheme, fieldNames = fieldNames }) ->
                                    match instantiate(scheme)(targetSupply) with
                                        | InstantiationResult { semanticType = constructorType, constraints = constructorConstraints, supply = constructorSupply } ->
                                            match splitConstructorType(constructorType)([]) with
                                                | ConstructorTypeShape { parameters = fieldTypes, resultType = resultType } ->
                                                    match mergeUnification(
                                                        targetSubstitution,
                                                        unify(
                                                            applySubstitution(targetSubstitution)(targetType),
                                                            resultType
                                                        ),
                                                        constructorSupply,
                                                        resultType
                                                    ) with
                                                        | TypeInferenceResult { semanticType = unifiedResult, substitution = unifiedSubstitution, supply = unifiedSupply, constraints = _unificationConstraints, error = None } ->
                                                            addConstraints(
                                                                appendConstraints(
                                                                    targetConstraints,
                                                                    constructorConstraints
                                                                ),
                                                                inferRecordExpressionFields(
                                                                    name,
                                                                    fields,
                                                                    fieldNames,
                                                                    fieldTypes,
                                                                    unifiedResult,
                                                                    false,
                                                                    environment,
                                                                    unifiedSubstitution,
                                                                    unifiedSupply,
                                                                    ambientRow,
                                                                    [],
                                                                    []
                                                                )
                                                            )
                                                        | failure -> failure
                        | other ->
                            inferenceFailure(
                                SemNever,
                                targetSubstitution,
                                targetSupply,
                                RecordUpdateRequiresRecord(other)
                            )
                | failure -> failure
        | ExprResultPipe(left, right) ->
            inferResultSuccessPipe(
                left,
                right,
                environment,
                substitution,
                supply,
                ambientRow
            )
        | ExprResultMapErrorPipe(left, right) ->
            inferResultErrorPipe(
                left,
                right,
                environment,
                substitution,
                supply,
                ambientRow
            )
        | ExprAwait(task) -> inferAwait(task)(environment)(substitution)(supply)(ambientRow)
        | ExprLetResult(name, value, body) ->
            inferLetResult(
                name,
                value,
                body,
                environment,
                substitution,
                supply,
                ambientRow
            )
        | ExprMatch(scrutinee, cases, _position) ->
            match inferWith(scrutinee)(environment)(substitution)(supply)(ambientRow) with
                | TypeInferenceResult { semanticType = scrutineeType, substitution = scrutineeSubstitution, supply = scrutineeSupply, constraints = scrutineeConstraints, error = None } ->
                    match freshTypeVariable(scrutineeSupply) with
                        | (resultType, resultSupply) ->
                            checkMatchCoverage(cases)(scrutineeType)(environment)(
                                inferMatchCases(
                                    cases,
                                    scrutineeType,
                                    resultType,
                                    environment,
                                    scrutineeSubstitution,
                                    resultSupply,
                                    ambientRow,
                                    scrutineeConstraints
                                )
                            )
                | failure -> failure
        | ExprAdd(left, right) ->
            inferBinaryTrait(
                "Add",
                false,
                left,
                right,
                environment,
                substitution,
                supply,
                ambientRow
            )
        | ExprSubtract(left, right) ->
            inferBinaryTrait(
                "Subtract",
                false,
                left,
                right,
                environment,
                substitution,
                supply,
                ambientRow
            )
        | ExprMultiply(left, right) ->
            inferBinaryTrait(
                "Multiply",
                false,
                left,
                right,
                environment,
                substitution,
                supply,
                ambientRow
            )
        | ExprDivide(left, right) ->
            inferBinaryTrait(
                "Divide",
                false,
                left,
                right,
                environment,
                substitution,
                supply,
                ambientRow
            )
        | ExprModulo(left, right) ->
            inferBinaryTrait(
                "Remainder",
                false,
                left,
                right,
                environment,
                substitution,
                supply,
                ambientRow
            )
        | ExprBitwiseAnd(left, right) ->
            inferBinaryTrait(
                "BitAnd",
                false,
                left,
                right,
                environment,
                substitution,
                supply,
                ambientRow
            )
        | ExprBitwiseOr(left, right) ->
            inferBinaryTrait(
                "BitOr",
                false,
                left,
                right,
                environment,
                substitution,
                supply,
                ambientRow
            )
        | ExprBitwiseXor(left, right) ->
            inferBinaryTrait(
                "BitXor",
                false,
                left,
                right,
                environment,
                substitution,
                supply,
                ambientRow
            )
        | ExprShiftLeft(left, right) ->
            inferBinaryTrait(
                "ShiftLeft",
                false,
                left,
                right,
                environment,
                substitution,
                supply,
                ambientRow
            )
        | ExprShiftRight(left, right) ->
            inferBinaryTrait(
                "ShiftRight",
                false,
                left,
                right,
                environment,
                substitution,
                supply,
                ambientRow
            )
        | ExprEqual(left, right) ->
            inferBinaryTrait(
                "Eq",
                true,
                left,
                right,
                environment,
                substitution,
                supply,
                ambientRow
            )
        | ExprNotEqual(left, right) ->
            inferBinaryTrait(
                "Eq",
                true,
                left,
                right,
                environment,
                substitution,
                supply,
                ambientRow
            )
        | ExprLessThan(left, right) ->
            inferBinaryTrait(
                "Ord",
                true,
                left,
                right,
                environment,
                substitution,
                supply,
                ambientRow
            )
        | ExprLessOrEqual(left, right) ->
            inferBinaryTrait(
                "Ord",
                true,
                left,
                right,
                environment,
                substitution,
                supply,
                ambientRow
            )
        | ExprGreaterThan(left, right) ->
            inferBinaryTrait(
                "Ord",
                true,
                left,
                right,
                environment,
                substitution,
                supply,
                ambientRow
            )
        | ExprGreaterOrEqual(left, right) ->
            inferBinaryTrait(
                "Ord",
                true,
                left,
                right,
                environment,
                substitution,
                supply,
                ambientRow
            )
        | ExprBitwiseNot(operand) ->
            inferUnaryTrait(
                "BitwiseNot",
                operand,
                environment,
                substitution,
                supply,
                ambientRow
            )
        | ExprLogicalNot(operand) -> inferUnaryTrait("Not")(operand)(environment)(substitution)(supply)(ambientRow)
        | ExprLogicalAnd(left, right) ->
            match inferWith(left)(environment)(substitution)(supply)(ambientRow) with
                | TypeInferenceResult { semanticType = leftType, substitution = leftSubstitution, supply = leftSupply, constraints = leftConstraints, error = None } ->
                    let leftUnification = unify(applySubstitution(leftSubstitution)(leftType))(SemBool)
                    in
                        match mergeUnification(leftSubstitution, leftUnification, leftSupply, SemBool) with
                            | TypeInferenceResult { semanticType = _leftBool, substitution = leftBoolSubstitution, supply = leftBoolSupply, constraints = _leftUnificationConstraints, error = None } ->
                                match inferWith(right)(environment)(leftBoolSubstitution)(leftBoolSupply)(ambientRow) with
                                    | TypeInferenceResult { semanticType = rightType, substitution = rightSubstitution, supply = rightSupply, constraints = rightConstraints, error = None } ->
                                        let rightUnification = unify(applySubstitution(rightSubstitution)(rightType))(SemBool)
                                        in
                                            addConstraints(
                                                appendConstraints(leftConstraints)(rightConstraints),
                                                mergeUnification(rightSubstitution, rightUnification, rightSupply, SemBool)
                                            )
                                    | failure -> failure
                            | failure -> failure
                | failure -> failure
        | ExprLogicalOr(left, right) ->
            match inferWith(left)(environment)(substitution)(supply)(ambientRow) with
                | TypeInferenceResult { semanticType = leftType, substitution = leftSubstitution, supply = leftSupply, constraints = leftConstraints, error = None } ->
                    let leftUnification = unify(applySubstitution(leftSubstitution)(leftType))(SemBool)
                    in
                        match mergeUnification(leftSubstitution, leftUnification, leftSupply, SemBool) with
                            | TypeInferenceResult { semanticType = _leftBool, substitution = leftBoolSubstitution, supply = leftBoolSupply, constraints = _leftUnificationConstraints, error = None } ->
                                match inferWith(right)(environment)(leftBoolSubstitution)(leftBoolSupply)(ambientRow) with
                                    | TypeInferenceResult { semanticType = rightType, substitution = rightSubstitution, supply = rightSupply, constraints = rightConstraints, error = None } ->
                                        let rightUnification = unify(applySubstitution(rightSubstitution)(rightType))(SemBool)
                                        in
                                            addConstraints(
                                                appendConstraints(leftConstraints)(rightConstraints),
                                                mergeUnification(rightSubstitution, rightUnification, rightSupply, SemBool)
                                            )
                                    | failure -> failure
                            | failure -> failure
                | failure -> failure
        | _ ->
            inferenceFailure(
                SemNever,
                substitution,
                supply,
                UnsupportedInferenceExpression("expression case is not implemented yet")
            )

let inferExpressionFrom expression environment substitution supply =
    match freshTypeVariable(supply) with
        | (ambientRow, nextSupply) -> inferWith(expression)(environment)(substitution)(nextSupply)(ambientRow)

let inferTopLevelBinding name value annotation requirements environment substitution supply =
    match inferExpressionFrom(value)(environment)(substitution)(supply) with
        | TypeInferenceResult { semanticType = valueType, substitution = valueSubstitution, supply = valueSupply, constraints = valueConstraints, error = None } ->
            match checkInferenceBindingSignature(
                annotation,
                requirements,
                valueType,
                valueConstraints,
                environment,
                valueSubstitution,
                valueSupply
            ) with
                | TypeInferenceResult { semanticType = checkedValue, substitution = checkedSubstitution, supply = checkedSupply, constraints = selectedConstraints, error = None } ->
                    let resolvedValue = applySubstitution(checkedSubstitution)(checkedValue)
                    in
                        let scheme =
                            generalize(
                                inferenceEnvironmentSchemes(environment),
                                resolvedValue,
                                selectedConstraints
                            )
                        in
                            TopLevelBindingInferenceResult(environment = addTypeBinding(
                                name,
                                scheme,
                                environment
                            ), semanticType = resolvedValue, substitution = checkedSubstitution, supply = checkedSupply, error = None)
                | TypeInferenceResult { semanticType = failedType, substitution = failedSubstitution, supply = failedSupply, constraints = _failedConstraints, error = Some(error) } ->
                    TopLevelBindingInferenceResult(environment = environment, semanticType = failedType, substitution = failedSubstitution, supply = failedSupply, error = Some(
                        error
                    ))
        | TypeInferenceResult { semanticType = failedType, substitution = failedSubstitution, supply = failedSupply, constraints = _failedConstraints, error = Some(error) } ->
            TopLevelBindingInferenceResult(environment = environment, semanticType = failedType, substitution = failedSubstitution, supply = failedSupply, error = Some(
                error
            ))

let inferExpression expression environment =
    inferExpressionFrom(
        expression,
        environment,
        [],
        initialTypeVariableSupply(Unit)
    )
