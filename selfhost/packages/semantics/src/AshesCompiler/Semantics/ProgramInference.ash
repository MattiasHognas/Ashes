// Infers and validates a complete program in sequential top-level declaration order.
//
// Invariants:
// - A declaration is visible only to later declarations unless it is explicitly recursive.
// - Recursive groups are monomorphic while inferred and generalized only after the group succeeds.
// - Trait registration, coherence, defaults, and termination checks produce deterministic errors.

import AshesCompiler.Frontend.Syntax
import AshesCompiler.Frontend.Token.TextSpan
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeSchemes
import AshesCompiler.Semantics.TypeResolution
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.Unification
import AshesCompiler.Semantics.DerivingExpansion
import AshesCompiler.Semantics.NodeIdentity
import AshesCompiler.Semantics.NodeTypeTable
import AshesCompiler.Semantics.ExternalTyping
import AshesCompiler.Semantics.ExternalAbi
import Ashes.Collection.List.reverse
import Ashes.Collection.Map.MapTree
import Ashes.Collection.List.sortBy
import Ashes.Text.compare as compareText
export (
    type ProgramInferenceError(..),
    type ProgramInferenceResult(..),
    type InferenceErrorLocation(..),
    type ProgramInferenceUnit(..),
    type IdentifiedProgramUnits(..),
    value identifyProgramUnits,
    value inferIdentifiedProgramUnitsFrom,
    value identifiedProgramSyntax,
    value inferProgramUnitsFrom,
    value inferProgramFromPackage,
    value inferProgramInPackage,
    value inferProgram,
    value expressionDependsOnTraitMethod,
)

// Where an inference error was raised. `unitIndex` is the position of the failing unit among the
// units inferred, or -1 for the trailing expression; `moduleName` and `sourcePath` are filled in
// by a caller that knows which module each unit is. `span` is the innermost located expression
// around the failure and `declarationSpan` the top-level declaration, both as offsets into that
// module's own text.
type InferenceErrorLocation =
    | packageId: Str
    | unitIndex: Int
    | moduleName: Maybe(Str)
    | sourcePath: Maybe(Str)
    | declaration: Maybe(Str)
    | declarationSpan: Maybe(TextSpan)
    | span: Maybe(TextSpan)
    deriving {Eq, Show}

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
    // A generic implementation's requirement no smaller than its head: the implemented trait, the
    // head's types, the required trait, and the requirement's types.
    | NonDecreasingTraitImplementationRequirement(Str, List(SemanticType), Str, List(SemanticType))
    // The implemented trait and the cycle among its selected defaults, written `a -> b -> a`.
    | DefaultTraitMethodDependencyCycle(Str, Str)
    | UnknownTraitImplementationMethod(Str, Str)
    | DuplicateTraitImplementationMethod(Str, Str)
    | MissingTraitImplementationMethod(Str, Str)
    // The trait, the implementation head, and the package that declares the implementation.
    | OrphanTraitImplementation(Str, List(SemanticType), Str)
    // The trait, where the implementation already registered was declared, and where the one that
    // overlaps it is; a site is None for an implementation the compiler supplies.
    | OverlappingTraitImplementations(Str, Maybe(TraitImplementationSite), Maybe(TraitImplementationSite))
    | ProgramDerivingExpansionError(DerivingExpansionError)
    // The external declaration's name (empty for an opaque type) and what is wrong with it.
    | ProgramExternalTypingError(Str, ExternalTypingError)
    | ProgramExternalAbiError(ExternalAbiError)
    | InvalidStitchedProgram(Str)
    | UnsupportedTopLevelDeclaration(Str)
    // A transparent alias whose expansion reaches itself, with the cycle written `A -> B -> A`.
    | RecursiveTypeAlias(Str)
    // A type, alias or zero-cost type declared under `Ashes` or a builtin type's name.
    | ReservedTypeDeclaration(Str)
    // An error tagged with where it was raised while the program is being inferred. A finished
    // `ProgramInferenceResult` never carries it: the bare error is in `error` and the place in
    // `errorLocation`.
    | ProgramErrorAt(InferenceErrorLocation, ProgramInferenceError)
    deriving {Eq, Show}

type ProgramInferenceResult =
    | semanticType: SemanticType
    | substitution: List((Int, SemanticType))
    | environment: TypeEnvironment
    | externalAbi: Maybe(ExternalProgramAbi)
    // What was recorded against the identified nodes of the program. Empty when the program
    // inferred carried no identities.
    | nodeTypes: NodeTypeTable
    | errorLocation: Maybe(InferenceErrorLocation)
    | error: Maybe(ProgramInferenceError)

type ProgramInferenceState =
    | environment: TypeEnvironment
    | substitution: List((Int, SemanticType))
    | supply: TypeVariableSupply
    | nextTypeSymbolId: Int
    | error: Maybe(ProgramInferenceError)

type ProgramInferenceUnit =
    | packageId: Str
    | program: ProgramSyntax

// The units of a program after `deriving` is expanded and every expression node has its identity,
// with the trailing expression numbered after them. `nodeCount` is the number of identities
// given, which are 0 up to it.
type IdentifiedProgramUnits =
    | units: List(ProgramInferenceUnit)
    | body: Maybe(Expr)
    | nodeCount: Int
    | error: Maybe(ProgramInferenceError)

type ProgramUnitExpansion =
    | units: List(ProgramInferenceUnit)
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
    | requirements: List(TraitConstraintSyntax)
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

type DefaultMethodCycleValidation =
    | completed: List(Str)
    // The cycle found, written as `defaultCycleTrace` writes it.
    | cycle: Maybe(Str)

let recursive syntaxListLength parameters =
    match parameters with
        | [] -> 0
        | _head :: tail -> 1 + syntaxListLength(tail)

let recursive registerAliasParameters parameters context nextId reversedIds =
    match parameters with
        | [] -> AliasParameterRegistration(context = context, parameterIds = reverse(reversedIds))
        | TypeParameter { name = name } :: tail ->
            registerAliasParameters(
                tail,
                addTypeParameter(name)(SemParameter(nextId)(name))(context),
                nextId + 1,
                nextId :: reversedIds
            )

let recursive appendProgramSubstitution left right =
    match left with
        | [] -> right
        | head :: tail -> head :: appendProgramSubstitution(tail)(right)

let recursive prepareRecursiveBindings bindings environment supply reversedPending =
    match bindings with
        | [] -> RecursivePreparation(environment = environment, pending = reverse(reversedPending), supply = supply)
        | LetBindingSyntax { name = name, value = value, sugarParameters = _parameters, typeAnnotation = annotation, requirements = requirements } :: tail ->
            match freshTypeVariable(supply) with
                | (placeholderType, nextSupply) ->
                    let scheme = TypeScheme(quantified = [], body = placeholderType, constraints = [])
                    in
                        let pending = PendingRecursiveBinding(name = name, value = value, annotation = annotation, requirements = requirements, placeholderType = placeholderType)
                        in
                            prepareRecursiveBindings(
                                tail,
                                addTypeBinding(name)(scheme)(environment),
                                nextSupply,
                                pending :: reversedPending
                            )

let recursive appendProgramConstraints left right =
    match left with
        | [] -> right
        | head :: tail -> head :: appendProgramConstraints(tail)(right)

let recursive pendingPlaceholderTypes pending =
    match pending with
        | [] -> []
        | PendingRecursiveBinding { placeholderType = placeholderType } :: tail -> placeholderType :: pendingPlaceholderTypes(tail)

// The boundary a recursive member's signature is checked against, with the group's own types in
// scope: a requirement on a type a sibling has yet to settle is not ambiguous while the group
// is still being inferred. The group as a whole is checked once every member is typed.
let recursiveGroupBoundary groupType substitution boundaryEnvironment = addTypeBinding("$recursive group")(TypeScheme(quantified = [], body = applySubstitution(substitution)(groupType), constraints = []))(boundaryEnvironment)

let recursive resolvedGroupTypes substitution resolved =
    match resolved with
        | [] -> []
        | ResolvedRecursiveBinding { semanticType = semanticType } :: tail -> applySubstitution(substitution)(semanticType) :: resolvedGroupTypes(substitution)(tail)

let recursive resolvedGroupConstraints substitution resolved =
    match resolved with
        | [] -> []
        | ResolvedRecursiveBinding { constraints = constraints } :: tail ->
            tail
            |> resolvedGroupConstraints(substitution)
            |> appendProgramConstraints(applyInferenceConstraints(substitution)(constraints))

let recursiveGroupAmbiguity resolved substitution outerEnvironment =
    firstAmbiguousTraitRequirementUnder(
        substitution,
        resolvedGroupConstraints(substitution)(resolved),
        resolved
        |> resolvedGroupTypes(substitution)
        |> SemTuple,
        outerEnvironment
    )

let recursive inferRecursiveBindings groupType pending environment boundaryEnvironment substitution supply reversedResolved =
    match pending with
        | [] ->
            RecursiveInference(resolved = reverse(
                reversedResolved
            ), substitution = substitution, supply = supply, error = None)
        | PendingRecursiveBinding { name = name, value = value, annotation = annotation, requirements = requirements, placeholderType = placeholderType } :: tail ->
            match inferExpressionFromLocated(value)(environment)(substitution)(supply) with
                | TypeInferenceResult { semanticType = valueType, substitution = valueSubstitution, supply = valueSupply, constraints = valueConstraints, error = None } ->
                    match valueType
                    |> applySubstitution(valueSubstitution)
                    |> unify(applySubstitution(valueSubstitution)(placeholderType)) with
                        | UnificationResult { substitution = unificationSubstitution, error = None } ->
                            let unifiedSubstitution =
                                appendProgramSubstitution(
                                    unificationSubstitution,
                                    valueSubstitution
                                )
                            in
                                let unifiedType = applySubstitution(unifiedSubstitution)(placeholderType)
                                in
                                    match checkValueBindingSignature(
                                        value,
                                        annotation,
                                        requirements,
                                        unifiedType,
                                        valueConstraints,
                                        recursiveGroupBoundary(groupType)(unifiedSubstitution)(boundaryEnvironment),
                                        unifiedSubstitution,
                                        valueSupply
                                    ) with
                                        | TypeInferenceResult { semanticType = annotatedType, substitution = annotatedSubstitution, supply = annotatedSupply, constraints = selectedConstraints, error = None } ->
                                            let resolvedBinding = ResolvedRecursiveBinding(name = name, semanticType = annotatedType, constraints = selectedConstraints)
                                            in
                                                inferRecursiveBindings(
                                                    groupType,
                                                    tail,
                                                    environment,
                                                    boundaryEnvironment,
                                                    annotatedSubstitution,
                                                    annotatedSupply,
                                                    resolvedBinding :: reversedResolved
                                                )
                                        | TypeInferenceResult { semanticType = _failedType, substitution = failedSubstitution, supply = failedSupply, constraints = _failedConstraints, error = Some(error) } ->
                                            RecursiveInference(resolved = [], substitution = failedSubstitution, supply = failedSupply, error = Some(
                                                error
                                            ))
                        | UnificationResult { substitution = _unificationSubstitution, error = Some(error) } ->
                            RecursiveInference(resolved = [], substitution = valueSubstitution, supply = valueSupply, error = Some(
                                InferenceUnificationError(error)
                            ))
                | TypeInferenceResult { semanticType = _failedType, substitution = failedSubstitution, supply = failedSupply, constraints = _failedConstraints, error = Some(error) } ->
                    RecursiveInference(resolved = [], substitution = failedSubstitution, supply = failedSupply, error = Some(
                        error
                    ))

let recursive seedRecursiveBindings pending environment substitution supply =
    match pending with
        | [] -> RecursiveInference(resolved = [], substitution = substitution, supply = supply, error = None)
        | PendingRecursiveBinding { annotation = annotation, placeholderType = placeholderType } :: tail ->
            match seedRecursiveSignature(annotation)(placeholderType)(environment)(substitution)(supply) with
                | TypeInferenceResult { semanticType = _seededType, substitution = seededSubstitution, supply = seededSupply, constraints = _seededConstraints, error = None } -> seedRecursiveBindings(tail)(environment)(seededSubstitution)(seededSupply)
                | TypeInferenceResult { semanticType = _failedType, substitution = failedSubstitution, supply = failedSupply, constraints = _failedConstraints, error = Some(error) } ->
                    RecursiveInference(resolved = [], substitution = failedSubstitution, supply = failedSupply, error = Some(
                        error
                    ))

// Every member's written signature is in place before any member's value is inferred, so a call
// to a sibling or to the binding itself is typed by what was written.
let inferSeededRecursiveBindings groupType pending environment boundaryEnvironment substitution supply =
    match seedRecursiveBindings(pending)(boundaryEnvironment)(substitution)(supply) with
        | RecursiveInference { resolved = _resolved, substitution = seededSubstitution, supply = seededSupply, error = None } ->
            inferRecursiveBindings(
                groupType,
                pending,
                environment,
                boundaryEnvironment,
                seededSubstitution,
                seededSupply,
                []
            )
        | failure -> failure

// The group's substitution with each builtin argument requirement on a variable the group would
// generalize over settled as a single binding's is.
let recursive defaultRecursiveBuiltinArguments bindings outerEnvironment substitution =
    match bindings with
        | [] -> substitution
        | ResolvedRecursiveBinding { constraints = constraints } :: tail ->
            substitution
            |> defaultBuiltinArguments(outerEnvironment)(applyInferenceConstraints(substitution)(constraints))
            |> defaultRecursiveBuiltinArguments(tail)(outerEnvironment)

// The `print` argument a member of the group would generalize over, once the group's defaults are
// settled.
let recursive recursiveOpenPrintArgument bindings outerEnvironment substitution =
    match bindings with
        | [] -> None
        | ResolvedRecursiveBinding { constraints = constraints } :: tail ->
            match openBuiltinPrintArgument(outerEnvironment)(applyInferenceConstraints(substitution)(constraints))(substitution) with
                | Some(argument) -> Some(argument)
                | None -> recursiveOpenPrintArgument(tail)(outerEnvironment)(substitution)

let recursive generalizeRecursiveBindings bindings outerEnvironment substitution resultEnvironment =
    match bindings with
        | [] -> resultEnvironment
        | ResolvedRecursiveBinding { name = name, semanticType = semanticType, constraints = constraints } :: tail ->
            let resolvedType = applySubstitution(substitution)(semanticType)
            in
                let resolvedConstraints = applyInferenceConstraints(substitution)(constraints)
                in
                    let scheme =
                        resolvedConstraints
                        |> simplifyTraitConstraints(outerEnvironment)
                        |> requirementsLeftToWrite(outerEnvironment)
                        |> generalizeUnderSubstitution(inferenceEnvironmentSchemes(outerEnvironment))(substitution)(resolvedType)
                    in
                        resultEnvironment
                        |> addTypeBinding(name)(scheme)
                        |> generalizeRecursiveBindings(tail)(outerEnvironment)(substitution)

let inferRecursiveGroup bindings state =
    match state with
        | ProgramInferenceState { environment = outerEnvironment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = None } ->
            match prepareRecursiveBindings(bindings)(outerEnvironment)(supply)([]) with
                | RecursivePreparation { environment = recursiveEnvironment, pending = pending, supply = preparedSupply } ->
                    match inferSeededRecursiveBindings(
                        pending
                        |> pendingPlaceholderTypes
                        |> SemTuple,
                        pending,
                        recursiveEnvironment,
                        outerEnvironment,
                        substitution,
                        preparedSupply
                    ) with
                        | RecursiveInference { resolved = resolved, substitution = resolvedSubstitution, supply = resolvedSupply, error = None } ->
                            match recursiveGroupAmbiguity(resolved)(resolvedSubstitution)(outerEnvironment) with
                                | Some((traitName, typeArguments)) ->
                                    ProgramInferenceState(environment = outerEnvironment, substitution = resolvedSubstitution, supply = resolvedSupply, nextTypeSymbolId = nextTypeSymbolId, error = Some(
                                        typeArguments
                                        |> AmbiguousTraitRequirement(traitName)
                                        |> ProgramExpressionError
                                    ))
                                | None ->
                                    let defaultedSubstitution = defaultRecursiveBuiltinArguments(resolved)(outerEnvironment)(resolvedSubstitution)
                                    in
                                        match recursiveOpenPrintArgument(resolved)(outerEnvironment)(defaultedSubstitution) with
                                            | Some(argument) ->
                                                ProgramInferenceState(environment = outerEnvironment, substitution = defaultedSubstitution, supply = resolvedSupply, nextTypeSymbolId = nextTypeSymbolId, error = Some(
                                                    [argument]
                                                    |> NoTraitImplementation("$PrintArgument")
                                                    |> ProgramExpressionError
                                                ))
                                            | None ->
                                                let finalEnvironment =
                                                    generalizeRecursiveBindings(
                                                        resolved,
                                                        outerEnvironment,
                                                        defaultedSubstitution,
                                                        outerEnvironment
                                                    )
                                                in ProgramInferenceState(environment = finalEnvironment, substitution = defaultedSubstitution, supply = resolvedSupply, nextTypeSymbolId = nextTypeSymbolId, error = None)
                        | RecursiveInference { resolved = _resolved, substitution = failedSubstitution, supply = failedSupply, error = Some(error) } ->
                            ProgramInferenceState(environment = outerEnvironment, substitution = failedSubstitution, supply = failedSupply, nextTypeSymbolId = nextTypeSymbolId, error = Some(
                                ProgramExpressionError(error)
                            ))
        | failedState -> failedState

let recursive registerTypeParameters parameters context supply reversedTypes reversedQuantified =
    match parameters with
        | [] ->
            TypeParameterRegistration(context = context, semanticTypes = reverse(
                reversedTypes
            ), quantified = reverse(
                reversedQuantified
            ), supply = supply)
        | TypeParameter { name = name } :: tail ->
            match freshTypeVariable(supply) with
                | (SemVariable(variableId), nextSupply) ->
                    registerTypeParameters(
                        tail,
                        addTypeParameter(name)(SemVariable(variableId))(context),
                        nextSupply,
                        SemVariable(variableId) :: reversedTypes,
                        (variableId, name) :: reversedQuantified
                    )
                | (_unexpected, nextSupply) ->
                    registerTypeParameters(
                        tail,
                        context,
                        nextSupply,
                        reversedTypes,
                        reversedQuantified
                    )

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
            then
                Some(
                    TraitDecl(name = candidateName, typeParameters = typeParameters, supertraits = supertraits, methods = methods)
                )
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
                | None ->
                    traitName
                    |> UnknownSupertrait(owner)
                    |> Some
                | Some(TraitDecl { name = _name, typeParameters = parameters, supertraits = _supertraits, methods = _methods }) ->
                    let expectedArity = syntaxListLength(parameters)
                    in
                        let actualArity = syntaxListLength(typeArguments)
                        in
                            if expectedArity == actualArity
                            then validateSupertraitReferences(owner)(tail)(declarations)
                            else
                                actualArity
                                |> SupertraitArityMismatch(traitName)(expectedArity)
                                |> Some

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
            | Some(TraitDecl { name = _declarationName, typeParameters = _parameters, supertraits = supertraits, methods = _methods }) ->
                findSupertraitCycleInRequirements(
                    supertraits,
                    declarations,
                    name :: path
                )

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
                            | Some(parameterName) ->
                                parameterName
                                |> DuplicateTraitTypeParameter(name)
                                |> Some
                            | None ->
                                match findDuplicateTraitMethod(methods)([]) with
                                    | Some(methodName) ->
                                        methodName
                                        |> DuplicateTraitMethod(name)
                                        |> Some
                                    | None ->
                                        match validateSupertraitReferences(name)(supertraits)(allDeclarations) with
                                            | Some(error) -> Some(error)
                                            | None ->
                                                match findSupertraitCycle(name)(allDeclarations)([]) with
                                                    | Some(cycle) -> Some(CyclicSupertraitRequirements(cycle))
                                                    | None ->
                                                        validateTraitDeclarations(
                                                            tail,
                                                            allDeclarations,
                                                            name :: seen
                                                        )

let recursive addCapabilityToInnermostArrow capabilityType semanticType =
    match semanticType with
        | SemFunction(argument, result, row) ->
            match result with
                | SemFunction(_, _, _) ->
                    match addCapabilityToInnermostArrow(capabilityType)(result) with
                        | None -> None
                        | Some(effectfulResult) ->
                            row
                            |> SemFunction(argument)(effectfulResult)
                            |> Some
                | _ ->
                    Some(SemRow([capabilityType])(None))
                    |> SemFunction(argument)(result)
                    |> Some
        | _ -> None

let recursive registerCapabilityOperations capabilityName declarations capabilityType capabilityScheme quantified context environment supply reversed =
    match declarations with
        | [] ->
            CapabilityOperationRegistration(environment = addCapabilityBinding(
                capabilityName,
                capabilityScheme,
                reverse(reversed),
                environment
            ), operations = reverse(reversed), supply = supply, error = None)
        | CapabilityOperation { name = operationName, signature = signature } :: tail ->
            if operationNameExists(operationName)(reversed)
            then
                CapabilityOperationRegistration(environment = environment, operations = reverse(
                    reversed
                ), supply = supply, error = operationName
                |> DuplicateCapabilityOperation(capabilityName)
                |> Some)
            else
                match signature with
                    | None ->
                        match quantified with
                            | _ :: _ ->
                                CapabilityOperationRegistration(environment = environment, operations = reverse(
                                    reversed
                                ), supply = supply, error = operationName
                                |> ParameterizedCapabilityOperationRequiresSignature(capabilityName)
                                |> Some)
                            | [] ->
                                match freshTypeVariable(supply) with
                                    | (operationType, nextSupply) ->
                                        let scheme = TypeScheme(quantified = [], body = operationType, constraints = [])
                                        in
                                            let definition = CapabilityOperationInferenceDefinition(name = operationName, scheme = scheme, hasExplicitSignature = false)
                                            in
                                                registerCapabilityOperations(
                                                    capabilityName,
                                                    tail,
                                                    capabilityType,
                                                    capabilityScheme,
                                                    quantified,
                                                    context,
                                                    addTypeBinding(
                                                        capabilityName + "." + operationName,
                                                        scheme,
                                                        environment
                                                    ),
                                                    nextSupply,
                                                    definition :: reversed
                                                )
                    | Some(signatureType) ->
                        match resolveTypeExpression(signatureType)(context) with
                            | TypeResolutionResult { semanticType = resolvedSignature, error = None } ->
                                match addCapabilityToInnermostArrow(capabilityType)(resolvedSignature) with
                                    | None ->
                                        CapabilityOperationRegistration(environment = environment, operations = reverse(
                                            reversed
                                        ), supply = supply, error = operationName
                                        |> CapabilityOperationRequiresFunction(capabilityName)
                                        |> Some)
                                    | Some(operationType) ->
                                        let scheme = TypeScheme(quantified = quantified, body = operationType, constraints = [])
                                        in
                                            let definition = CapabilityOperationInferenceDefinition(name = operationName, scheme = scheme, hasExplicitSignature = true)
                                            in
                                                registerCapabilityOperations(
                                                    capabilityName,
                                                    tail,
                                                    capabilityType,
                                                    capabilityScheme,
                                                    quantified,
                                                    context,
                                                    addTypeBinding(
                                                        capabilityName + "." + operationName,
                                                        scheme,
                                                        environment
                                                    ),
                                                    supply,
                                                    definition :: reversed
                                                )
                            | TypeResolutionResult { semanticType = _resolvedSignature, error = Some(error) } ->
                                CapabilityOperationRegistration(environment = environment, operations = reverse(
                                    reversed
                                ), supply = supply, error = Some(
                                    ProgramTypeResolutionError(error)
                                ))

let registerCapabilityDeclaration declaration state =
    match (declaration, state) with
        | (CapabilityDecl { name = name, typeParameters = parameters, operations = operations }, ProgramInferenceState { environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = None }) ->
            if isBuiltinRuntimeCapability(name)
            then
                ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = Some(
                    ReservedCapabilityDeclaration(name)
                ))
            else
                match resolveCapabilityBinding(name)(environment) with
                    | Some(_) ->
                        ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = Some(
                            DuplicateCapabilityDeclaration(name)
                        ))
                    | None ->
                        match registerTypeParameters(
                            parameters,
                            inferenceTypeResolutionContext(environment),
                            supply,
                            [],
                            []
                        ) with
                            | TypeParameterRegistration { context = context, semanticTypes = parameterTypes, quantified = quantified, supply = parameterSupply } ->
                                let capabilityType = SemCapability(name)(parameterTypes)
                                in
                                    let capabilityScheme = TypeScheme(quantified = quantified, body = capabilityType, constraints = [])
                                    in
                                        match registerCapabilityOperations(
                                            name,
                                            operations,
                                            capabilityType,
                                            capabilityScheme,
                                            quantified,
                                            context,
                                            environment,
                                            parameterSupply,
                                            []
                                        ) with
                                            | CapabilityOperationRegistration { environment = capabilityEnvironment, operations = _registeredOperations, supply = operationSupply, error = None } -> ProgramInferenceState(environment = capabilityEnvironment, substitution = substitution, supply = operationSupply, nextTypeSymbolId = nextTypeSymbolId, error = None)
                                            | CapabilityOperationRegistration { environment = _capabilityEnvironment, operations = _registeredOperations, supply = operationSupply, error = Some(error) } ->
                                                ProgramInferenceState(environment = environment, substitution = substitution, supply = operationSupply, nextTypeSymbolId = nextTypeSymbolId, error = Some(
                                                    error
                                                ))
        | (_declaration, failedState) -> failedState

let recursive resolveConstructorParameters parameters context reversed =
    match parameters with
        | [] -> TypeListResolutionResult(semanticTypes = reverse(reversed), error = None)
        | head :: tail ->
            match resolveTypeExpression(head)(context) with
                | TypeResolutionResult { semanticType = semanticType, error = None } ->
                    resolveConstructorParameters(
                        tail,
                        context,
                        semanticType :: reversed
                    )
                | TypeResolutionResult { semanticType = _semanticType, error = Some(error) } ->
                    TypeListResolutionResult(semanticTypes = [], error = Some(
                        error
                    ))

let recursive constructorFunctionType parameters resultType =
    match parameters with
        | [] -> resultType
        | head :: tail ->
            SemFunction(head)(constructorFunctionType(tail)(resultType))(None)

let recursive registerConstructors constructors resultType quantified context environment =
    match constructors with
        | [] -> ConstructorRegistration(environment = environment, error = None)
        | TypeConstructor { name = name, parameters = parameters, fieldNames = fieldNames } :: tail ->
            match resolveConstructorParameters(parameters)(context)([]) with
                | TypeListResolutionResult { semanticTypes = parameterTypes, error = None } ->
                    let constructorType = constructorFunctionType(parameterTypes)(resultType)
                    in
                        let scheme = TypeScheme(quantified = quantified, body = constructorType, constraints = [])
                        in
                            environment
                            |> addConstructorBinding(name)(scheme)(fieldNames)
                            |> registerConstructors(tail)(resultType)(quantified)(context)
                | TypeListResolutionResult { semanticTypes = _parameterTypes, error = Some(error) } ->
                    ConstructorRegistration(environment = environment, error = Some(
                        error
                    ))

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
                                        then
                                            arguments
                                            |> SemCapability(name)
                                            |> Some
                                        else findCapability(tail)
                                    | _ :: tail -> findCapability(tail)
                            in findCapability(capabilities)
                        | _ -> None
        | _ -> None

let recursive detachProviderOperationRow semanticType =
    match semanticType with
        | SemFunction(argument, result, row) ->
            match result with
                | SemFunction(_, _, _) ->
                    SemFunction(argument)(detachProviderOperationRow(result))(row)
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
                            | None ->
                                TypeInferenceResult(semanticType = SemNever, substitution = substitution, supply = operationSupply, constraints = [], error = Some(
                                    UnsupportedInferenceExpression("provider operation has no capability row")
                                ))
                            | Some(operationCapability) ->
                                match capabilityType
                                |> applySubstitution(substitution)
                                |> unify(applySubstitution(substitution)(operationCapability)) with
                                    | UnificationResult { substitution = operationSubstitution, error = None } ->
                                        let combined = appendProgramSubstitution(operationSubstitution)(substitution)
                                        in
                                            TypeInferenceResult(semanticType = operationType
                                            |> detachProviderOperationRow
                                            |> applySubstitution(
                                                combined
                                            ), substitution = combined, supply = operationSupply, constraints = [], error = None)
                                    | UnificationResult { substitution = _operationSubstitution, error = Some(error) } ->
                                        TypeInferenceResult(semanticType = SemNever, substitution = substitution, supply = operationSupply, constraints = [], error = Some(
                                            InferenceUnificationError(error)
                                        ))
                    else TypeInferenceResult(semanticType = operationType, substitution = substitution, supply = operationSupply, constraints = [], error = None)

let recursive registerProviderOperations capabilityName capabilityType operationDefinitions bindings environment substitution supply reversed =
    match operationDefinitions with
        | [] ->
            ProviderOperationRegistration(operations = reverse(
                reversed
            ), substitution = substitution, supply = supply, error = None)
        | operation :: tail ->
            match operation with
                | CapabilityOperationInferenceDefinition { name = operationName, scheme = _scheme, hasExplicitSignature = _hasExplicitSignature } ->
                    match findProviderBinding(operationName)(bindings) with
                        | None ->
                            ProviderOperationRegistration(operations = reverse(
                                reversed
                            ), substitution = substitution, supply = supply, error = operationName
                            |> MissingProviderOperation(capabilityName)
                            |> Some)
                        | Some(implementation) ->
                            match prepareProviderExpectedType(
                                capabilityName,
                                capabilityType,
                                operation,
                                substitution,
                                supply
                            ) with
                                | TypeInferenceResult { semanticType = expectedType, substitution = expectedSubstitution, supply = expectedSupply, constraints = _expectedConstraints, error = None } ->
                                    match inferExpressionFromLocated(
                                        implementation,
                                        environment,
                                        expectedSubstitution,
                                        expectedSupply
                                    ) with
                                        | TypeInferenceResult { semanticType = implementationType, substitution = implementationSubstitution, supply = implementationSupply, constraints = _implementationConstraints, error = None } ->
                                            match implementationType
                                            |> applySubstitution(implementationSubstitution)
                                            |> unify(applySubstitution(implementationSubstitution)(expectedType)) with
                                                | UnificationResult { substitution = providerSubstitution, error = None } ->
                                                    let combined =
                                                        appendProgramSubstitution(
                                                            providerSubstitution,
                                                            implementationSubstitution
                                                        )
                                                    in
                                                        let registered =
                                                            CapabilityProviderOperationInferenceDefinition(name = operationName, semanticType = applySubstitution(
                                                                combined,
                                                                implementationType
                                                            ))
                                                        in
                                                            registerProviderOperations(
                                                                capabilityName,
                                                                capabilityType,
                                                                tail,
                                                                bindings,
                                                                environment,
                                                                combined,
                                                                implementationSupply,
                                                                registered :: reversed
                                                            )
                                                | UnificationResult { substitution = _providerSubstitution, error = Some(error) } ->
                                                    ProviderOperationRegistration(operations = reverse(
                                                        reversed
                                                    ), substitution = implementationSubstitution, supply = implementationSupply, error = Some(
                                                        ProgramExpressionError(InferenceUnificationError(error))
                                                    ))
                                        | TypeInferenceResult { semanticType = _implementationType, substitution = failedSubstitution, supply = failedSupply, constraints = _implementationConstraints, error = Some(error) } ->
                                            ProviderOperationRegistration(operations = reverse(
                                                reversed
                                            ), substitution = failedSubstitution, supply = failedSupply, error = Some(
                                                ProgramExpressionError(error)
                                            ))
                                | TypeInferenceResult { semanticType = _expectedType, substitution = failedSubstitution, supply = failedSupply, constraints = _expectedConstraints, error = Some(error) } ->
                                    ProviderOperationRegistration(operations = reverse(
                                        reversed
                                    ), substitution = failedSubstitution, supply = failedSupply, error = Some(
                                        ProgramExpressionError(error)
                                    ))

// The declaring type's own bare name in a constructor field is shorthand for the type applied to
// its own parameters (`Node(Int, MapTree, K, V, MapTree)` inside `MapTree(K, V)`), the language's
// one exception to the arity rule; the field is rewritten to the applied form before resolution.
let recursive selfAppliedTypeExpr (name: Str) (parameters: List(TypeParameter)) (typeExpr: TypeExpr) =
    match typeExpr with
        | TypeAt(span, inner) ->
            inner
            |> selfAppliedTypeExpr(name)(parameters)
            |> TypeAt(span)
        | TypeNamed(candidate) ->
            if candidate == name && syntaxListLength(parameters) > 0
            then
                parameters
                |> typeParameterNames
                |> TypeApplied(name)
            else typeExpr
        | other -> other
and typeParameterNames (parameters: List(TypeParameter)) =
    match parameters with
        | [] -> []
        | TypeParameter { name = parameterName } :: rest -> TypeNamed(parameterName) :: typeParameterNames(rest)

let recursive selfAppliedTypeExprs (name: Str) (parameters: List(TypeParameter)) (typeExprs: List(TypeExpr)) =
    match typeExprs with
        | [] -> []
        | typeExpr :: rest -> selfAppliedTypeExpr(name)(parameters)(typeExpr) :: selfAppliedTypeExprs(name)(parameters)(rest)

let recursive selfAppliedConstructors (name: Str) (parameters: List(TypeParameter)) (constructors: List(TypeConstructor)) =
    match constructors with
        | [] -> []
        | TypeConstructor { name = constructorName, parameters = fields, fieldNames = fieldNames } :: rest -> TypeConstructor(name = constructorName, parameters = selfAppliedTypeExprs(name)(parameters)(fields), fieldNames = fieldNames) :: selfAppliedConstructors(name)(parameters)(rest)

// A type the stitched `Ashes.Trait` module declares is also known by its bare name, as its
// constructors are: `Ordering` can be written in a signature without an import.
let withBareStandardTypeName (name: Str) parameters resultType environment =
    match parameters with
        | [] ->
            if canonicalTraitName(name) == name
            then environment
            else
                addInferenceTypeAlias(canonicalTraitName(name))([])(resultType)(environment)
        | _ -> environment

let registerParameterizedTypeDeclaration declaration state =
    match (declaration, state) with
        | (TypeDecl { name = name, typeParameters = parameters, constructors = declaredConstructors, isRecord = _isRecord, derivingTraits = _derivingTraits }, ProgramInferenceState { environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = symbolId, error = None }) ->
            let constructors = selfAppliedConstructors(name)(parameters)(declaredConstructors)
            in
                let typedEnvironment =
                    addInferenceTypeDefinition(symbolId)(name)(syntaxListLength(parameters))(environment)
                in
                    match registerTypeParameters(
                        parameters,
                        inferenceTypeResolutionContext(typedEnvironment),
                        supply,
                        [],
                        []
                    ) with
                        | TypeParameterRegistration { context = context, semanticTypes = parameterTypes, quantified = quantified, supply = parameterSupply } ->
                            let resultType = SemNamed(symbolId)(name)(parameterTypes)
                            in
                                match registerConstructors(
                                    constructors,
                                    resultType,
                                    quantified,
                                    context,
                                    typedEnvironment
                                ) with
                                    | ConstructorRegistration { environment = constructorEnvironment, error = None } -> ProgramInferenceState(environment = withBareStandardTypeName(name)(parameters)(resultType)(constructorEnvironment), substitution = substitution, supply = parameterSupply, nextTypeSymbolId = symbolId + 1, error = None)
                                    | ConstructorRegistration { environment = _constructorEnvironment, error = Some(error) } ->
                                        ProgramInferenceState(environment = environment, substitution = substitution, supply = parameterSupply, nextTypeSymbolId = symbolId + 1, error = Some(
                                            ProgramTypeResolutionError(error)
                                        ))
        | (_declaration, failedState) -> failedState

// A type declared without parameters takes one for each payload name that denotes no known type,
// in the order the names first appear. The declaring type's own name and every declared or
// built-in type stay concrete field types.
let recursive implicitTypeParameterNames (owner: Str) context (typeExpr: TypeExpr) (found: List(Str)) =
    match typeExpr with
        | TypeAt(_span, inner) -> implicitTypeParameterNames(owner)(context)(inner)(found)
        | TypeNamed(candidate) ->
            if candidate == owner
            then found
            else
                if nameExists(candidate)(found)
                then found
                else
                    match resolveTypeExpression(typeExpr)(context) with
                        | TypeResolutionResult { semanticType = _semanticType, error = Some(UnknownTypeName(_)) } -> candidate :: found
                        | _ -> found
        | TypeApplied(_name, arguments) -> implicitTypeParameterNamesIn(owner)(context)(arguments)(found)
        | TypeArrow(argument, result, _capabilities, _tail) -> implicitTypeParameterNamesIn(owner)(context)([argument, result])(found)
        | TypeTuple(elements) -> implicitTypeParameterNamesIn(owner)(context)(elements)(found)
        | TypeUnit -> found
and implicitTypeParameterNamesIn (owner: Str) context (typeExprs: List(TypeExpr)) (found: List(Str)) =
    match typeExprs with
        | [] -> found
        | head :: tail ->
            found
            |> implicitTypeParameterNames(owner)(context)(head)
            |> implicitTypeParameterNamesIn(owner)(context)(tail)

let recursive implicitConstructorParameterNames (owner: Str) context (constructors: List(TypeConstructor)) (found: List(Str)) =
    match constructors with
        | [] -> found
        | TypeConstructor { parameters = fields } :: rest ->
            found
            |> implicitTypeParameterNamesIn(owner)(context)(fields)
            |> implicitConstructorParameterNames(owner)(context)(rest)

let recursive typeParametersNamed (names: List(Str)) =
    match names with
        | [] -> []
        | head :: tail -> TypeParameter(name = head) :: typeParametersNamed(tail)

let registerTypeDeclaration declaration state =
    match (declaration, state) with
        | (TypeDecl { name = name, typeParameters = [], constructors = constructors, isRecord = isRecord, derivingTraits = derivingTraits }, ProgramInferenceState { environment = environment, error = None }) ->
            match []
            |> implicitConstructorParameterNames(name)(inferenceTypeResolutionContext(environment))(constructors)
            |> reverse with
                | [] -> registerParameterizedTypeDeclaration(declaration)(state)
                | names -> registerParameterizedTypeDeclaration(TypeDecl(name = name, typeParameters = typeParametersNamed(names), constructors = constructors, isRecord = isRecord, derivingTraits = derivingTraits))(state)
        | _ -> registerParameterizedTypeDeclaration(declaration)(state)

let registerTypeAlias declaration state =
    match (declaration, state) with
        | (TypeAliasDecl { name = name, typeParameters = parameters, target = target }, ProgramInferenceState { environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = None }) ->
            match registerAliasParameters(parameters)(inferenceTypeResolutionContext(environment))(0)([]) with
                | AliasParameterRegistration { context = aliasContext, parameterIds = parameterIds } ->
                    match resolveTypeExpression(target)(aliasContext) with
                        | TypeResolutionResult { semanticType = targetType, error = None } ->
                            ProgramInferenceState(environment = addInferenceTypeAlias(
                                name,
                                parameterIds,
                                targetType,
                                environment
                            ), substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = None)
                        | TypeResolutionResult { semanticType = _targetType, error = Some(error) } ->
                            ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = Some(
                                ProgramTypeResolutionError(error)
                            ))
        | (_declaration, failedState) -> failedState

// The type names a type expression mentions.
let recursive typeExprNames (typeExpr: TypeExpr) (found: List(Str)) =
    match typeExpr with
        | TypeAt(_span, inner) -> typeExprNames(inner)(found)
        | TypeNamed(name) -> name :: found
        | TypeApplied(name, arguments) -> typeExprNamesIn(arguments)(name :: found)
        | TypeArrow(argument, result, _capabilities, _tail) -> typeExprNamesIn([argument, result])(found)
        | TypeTuple(elements) -> typeExprNamesIn(elements)(found)
        | TypeUnit -> found
and typeExprNamesIn (typeExprs: List(TypeExpr)) (found: List(Str)) =
    match typeExprs with
        | [] -> found
        | head :: tail ->
            found
            |> typeExprNames(head)
            |> typeExprNamesIn(tail)

let recursive isParameterName (name: Str) (parameters: List(TypeParameter)) =
    match parameters with
        | [] -> false
        | TypeParameter { name = candidate } :: rest -> candidate == name || isParameterName(name)(rest)

let recursive withoutParameterNames (parameters: List(TypeParameter)) (names: List(Str)) =
    match names with
        | [] -> []
        | name :: rest ->
            if isParameterName(name)(parameters)
            then withoutParameterNames(parameters)(rest)
            else name :: withoutParameterNames(parameters)(rest)

// Each alias a unit declares, with the type names its target mentions other than its own
// parameters.
let recursive aliasDependencies (items: List(TopLevelItem)) =
    match items with
        | [] -> []
        | TopLevelAt(_span, inner) :: rest -> aliasDependencies(inner :: rest)
        | TopLevelTypeAlias(TypeAliasDecl { name = name, typeParameters = parameters, target = target }) :: rest ->
            (name, []
            |> typeExprNames(target)
            |> withoutParameterNames(parameters)) :: aliasDependencies(rest)
        | _ :: rest -> aliasDependencies(rest)

let recursive aliasDependenciesOf (name: Str) (dependencies: List((Str, List(Str)))) =
    match dependencies with
        | [] -> None
        | (candidate, mentioned) :: rest ->
            if candidate == name
            then Some(mentioned)
            else aliasDependenciesOf(name)(rest)

// The path from the first occurrence of `name` on a walk, written in walking order and closed
// with `name` again. `reversedPath` holds the walk most recent first.
let recursive cyclePathFrom (name: Str) (reversedPath: List(Str)) (cycle: List(Str)) =
    match reversedPath with
        | [] -> cycle
        | head :: rest ->
            if head == name
            then head :: cycle
            else cyclePathFrom(name)(rest)(head :: cycle)

let recursive joinCycle (names: List(Str)) =
    match names with
        | [] -> ""
        | head :: [] -> head
        | head :: rest -> head + " -> " + joinCycle(rest)

let recursive aliasCycleThrough (dependencies: List((Str, List(Str)))) (reversedPath: List(Str)) (name: Str) =
    if nameExists(name)(reversedPath)
    then
        [name]
        |> cyclePathFrom(name)(reversedPath)
        |> joinCycle
        |> Some
    else
        match aliasDependenciesOf(name)(dependencies) with
            | None -> None
            | Some(mentioned) -> aliasCycleThroughAny(dependencies)(name :: reversedPath)(mentioned)
and aliasCycleThroughAny (dependencies: List((Str, List(Str)))) (reversedPath: List(Str)) (names: List(Str)) =
    match names with
        | [] -> None
        | head :: rest ->
            match aliasCycleThrough(dependencies)(reversedPath)(head) with
                | Some(cycle) -> Some(cycle)
                | None -> aliasCycleThroughAny(dependencies)(reversedPath)(rest)

let recursive firstAliasCycle (dependencies: List((Str, List(Str)))) (remaining: List((Str, List(Str)))) =
    match remaining with
        | [] -> None
        | (name, _mentioned) :: rest ->
            match aliasCycleThrough(dependencies)([])(name) with
                | Some(cycle) -> Some(cycle)
                | None -> firstAliasCycle(dependencies)(rest)

// A unit's aliases checked for a cycle before any is registered, so an alias that names one
// declared after it in a cycle is reported as the cycle rather than as an unknown type. Applied
// only under the program inference rules.
let checkTypeAliasCycles (items: List(TopLevelItem)) (state: ProgramInferenceState) =
    match state with
        | ProgramInferenceState { environment = environment, error = None } ->
            if environment.programInferenceRules
            then
                let dependencies = aliasDependencies(items)
                in
                    match firstAliasCycle(dependencies)(dependencies) with
                        | Some(cycle) -> state with error = Some(RecursiveTypeAlias(cycle))
                        | None -> state
            else state
        | _ -> state

let recursive quantifiedTypeIds quantified =
    match quantified with
        | [] -> []
        | (variableId, _name) :: tail -> variableId :: quantifiedTypeIds(tail)

let registerResolvedZeroCostRepresentation name symbolId registered resolved =
    match (resolved, registered) with
        | (Some(ConstructorInferenceDefinition { scheme = TypeScheme { quantified = quantified, body = SemFunction(representation, _result, _row) } }), ProgramInferenceState { environment = environment, error = None }) ->
            registered with environment = addInferenceZeroCostTypeDefinition(
                symbolId,
                name,
                quantifiedTypeIds(quantified),
                representation,
                environment
            )
        | _ -> registered

let registerZeroCostRepresentation name symbolId registered =
    match registered with
        | ProgramInferenceState { environment = environment, error = None } ->
            environment
            |> resolveConstructorBinding(name)
            |> registerResolvedZeroCostRepresentation(name)(symbolId)(registered)
        | failed -> failed

let registerZeroCostType declaration state =
    match (declaration, state) with
        | (ZeroCostTypeDecl { name = name, typeParameters = typeParameters, constructor = constructor, derivingTraits = derivingTraits }, ProgramInferenceState { nextTypeSymbolId = symbolId, error = None }) ->
            state
            |> registerTypeDeclaration(TypeDecl(
                name = name,
                typeParameters = typeParameters,
                constructors = [
                    constructor
                ],
                isRecord = false,
                derivingTraits = derivingTraits
            ))
            |> registerZeroCostRepresentation(name)(symbolId)
        | (_declaration, failedState) -> failedState

let registerCapabilityProvider declaration state =
    match (declaration, state) with
        | (ProvideDecl { capabilityName = capabilityName, typeArguments = typeArguments, bindings = bindings }, ProgramInferenceState { environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = None }) ->
            match resolveCapabilityBinding(capabilityName)(environment) with
                | None ->
                    ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = Some(
                        UnknownProviderCapability(capabilityName)
                    ))
                | Some(CapabilityInferenceDefinition { name = _name, scheme = capabilityScheme, operations = operations }) ->
                    match capabilityScheme with
                        | TypeScheme { quantified = _quantified, body = SemCapability(_schemeName, parameterTypes), constraints = _constraints } ->
                            let expectedArity = semanticTypeListLength(parameterTypes)
                            in
                                let actualArity = syntaxListLength(typeArguments)
                                in
                                    if expectedArity == actualArity
                                    then
                                        match resolveConstructorParameters(
                                            typeArguments,
                                            inferenceTypeResolutionContext(environment),
                                            []
                                        ) with
                                            | TypeListResolutionResult { semanticTypes = resolvedArguments, error = None } ->
                                                let capabilityType = SemCapability(capabilityName)(resolvedArguments)
                                                in
                                                    match resolveCapabilityProvider(capabilityType)(environment) with
                                                        | Some(_) ->
                                                            ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = Some(
                                                                DuplicateCapabilityProvider(capabilityType)
                                                            ))
                                                        | None ->
                                                            match findDuplicateProviderBinding(bindings)([]) with
                                                                | Some(operationName) ->
                                                                    ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = operationName
                                                                    |> DuplicateProviderOperation(capabilityName)
                                                                    |> Some)
                                                                | None ->
                                                                    match findUnknownProviderBinding(
                                                                        capabilityName,
                                                                        bindings,
                                                                        environment
                                                                    ) with
                                                                        | Some(operationName) ->
                                                                            ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = operationName
                                                                            |> UnknownProviderOperation(capabilityName)
                                                                            |> Some)
                                                                        | None ->
                                                                            match findMissingProviderBinding(
                                                                                operations,
                                                                                bindings
                                                                            ) with
                                                                                | Some(operationName) ->
                                                                                    ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = operationName
                                                                                    |> MissingProviderOperation(
                                                                                        capabilityName
                                                                                    )
                                                                                    |> Some)
                                                                                | None ->
                                                                                    match registerProviderOperations(
                                                                                        capabilityName,
                                                                                        capabilityType,
                                                                                        operations,
                                                                                        bindings,
                                                                                        environment,
                                                                                        substitution,
                                                                                        supply,
                                                                                        []
                                                                                    ) with
                                                                                        | ProviderOperationRegistration { operations = registeredOperations, substitution = providerSubstitution, supply = providerSupply, error = None } ->
                                                                                            ProgramInferenceState(environment = addCapabilityProvider(
                                                                                                capabilityType,
                                                                                                registeredOperations,
                                                                                                environment
                                                                                            ), substitution = providerSubstitution, supply = providerSupply, nextTypeSymbolId = nextTypeSymbolId, error = None)
                                                                                        | ProviderOperationRegistration { operations = _registeredOperations, substitution = failedSubstitution, supply = failedSupply, error = Some(error) } ->
                                                                                            ProgramInferenceState(environment = environment, substitution = failedSubstitution, supply = failedSupply, nextTypeSymbolId = nextTypeSymbolId, error = Some(
                                                                                                error
                                                                                            ))
                                            | TypeListResolutionResult { semanticTypes = _resolvedArguments, error = Some(error) } ->
                                                ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = Some(
                                                    ProgramTypeResolutionError(error)
                                                ))
                                    else
                                        ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = actualArity
                                        |> ProviderCapabilityArityMismatch(capabilityName)(expectedArity)
                                        |> Some)
                        | _ ->
                            ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = Some(
                                UnknownProviderCapability(capabilityName)
                            ))
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
        | [] ->
            TraitMethodRegistration(environment = environment, methods = reverse(
                reversed
            ), supply = supply, error = None)
        | TraitMethodDecl { name = methodName, signature = signature, defaultImplementation = defaultImplementation } :: tail ->
            match resolveTypeExpression(signature)(context) with
                | TypeResolutionResult { semanticType = methodType, error = None } ->
                    if anyTraitParameterOccurs(parameterTypes)(methodType)
                    then
                        let constraint = TraitConstraint(traitName = canonicalTraitName(traitName), typeArguments = parameterTypes)
                        in
                            let scheme = TypeScheme(quantified = quantified, body = methodType, constraints = [constraint])
                            in
                                let definition = TraitMethodInferenceDefinition(name = methodName, scheme = scheme, defaultImplementation = defaultImplementation)
                                in
                                    registerTraitMethods(
                                        traitName,
                                        tail,
                                        parameterTypes,
                                        quantified,
                                        context,
                                        addTypeBinding(canonicalTraitName(traitName) + "." + methodName)(scheme)(environment),
                                        supply,
                                        definition :: reversed
                                    )
                    else
                        TraitMethodRegistration(environment = environment, methods = reverse(
                            reversed
                        ), supply = supply, error = methodName
                        |> TraitMethodMustMentionParameter(traitName)
                        |> Some)
                | TypeResolutionResult { semanticType = _methodType, error = Some(error) } ->
                    TraitMethodRegistration(environment = environment, methods = reverse(
                        reversed
                    ), supply = supply, error = Some(
                        ProgramTypeResolutionError(error)
                    ))

let recursive resolveTraitConstraints declarations context reversed =
    match declarations with
        | [] -> TraitConstraintRegistration(constraints = reverse(reversed), error = None)
        | TraitConstraintSyntax { traitName = traitName, typeArguments = typeArguments } :: tail ->
            match resolveConstructorParameters(typeArguments)(context)([]) with
                | TypeListResolutionResult { semanticTypes = arguments, error = None } ->
                    let constraint = TraitConstraint(traitName = canonicalTraitName(traitName), typeArguments = arguments)
                    in resolveTraitConstraints(tail)(context)(constraint :: reversed)
                | TypeListResolutionResult { semanticTypes = _arguments, error = Some(error) } ->
                    TraitConstraintRegistration(constraints = reverse(
                        reversed
                    ), error = Some(
                        ProgramTypeResolutionError(error)
                    ))

let registerTraitDeclaration declaration state =
    match (declaration, state) with
        | (TraitDecl { name = name, typeParameters = parameters, supertraits = supertraits, methods = methods }, ProgramInferenceState { environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = None }) ->
            match registerTypeParameters(parameters)(inferenceTypeResolutionContext(environment))(supply)([])([]) with
                | TypeParameterRegistration { context = context, semanticTypes = parameterTypes, quantified = quantified, supply = parameterSupply } ->
                    match resolveTraitConstraints(supertraits)(context)([]) with
                        | TraitConstraintRegistration { constraints = resolvedSupertraits, error = None } ->
                            match registerTraitMethods(
                                name,
                                methods,
                                parameterTypes,
                                quantified,
                                context,
                                environment,
                                parameterSupply,
                                []
                            ) with
                                | TraitMethodRegistration { environment = methodEnvironment, methods = registeredMethods, supply = methodSupply, error = None } ->
                                    ProgramInferenceState(environment = addTraitBinding(
                                        name,
                                        syntaxListLength(parameters),
                                        parameterTypes,
                                        registeredMethods,
                                        resolvedSupertraits,
                                        methodEnvironment
                                    ), substitution = substitution, supply = methodSupply, nextTypeSymbolId = nextTypeSymbolId, error = None)
                                | TraitMethodRegistration { environment = _methodEnvironment, methods = _registeredMethods, supply = methodSupply, error = Some(error) } ->
                                    ProgramInferenceState(environment = environment, substitution = substitution, supply = methodSupply, nextTypeSymbolId = nextTypeSymbolId, error = Some(
                                        error
                                    ))
                        | TraitConstraintRegistration { constraints = _resolvedSupertraits, error = Some(error) } ->
                            ProgramInferenceState(environment = environment, substitution = substitution, supply = parameterSupply, nextTypeSymbolId = nextTypeSymbolId, error = Some(
                                error
                            ))
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
                            let first =
                                0
                                |> Ashes.Byte.get(bytes)
                                |> Ashes.Number.UInt.toInt
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
        | head :: tail ->
            names
            |> collectImplementationTypeNames(head)
            |> collectImplementationTypeNamesFromList(tail)

let sortImplementationNames : List(Str) -> List(Str) =
    given (names) ->
        sortBy(given (left) ->
            given (right) -> compareText(left)(right) <= 0)(names)

let collectImplementationHeadNames typeArguments =
    []
    |> collectImplementationTypeNamesFromList(typeArguments)
    |> sortImplementationNames

let recursive registerImplementationParameters names context nextTypeSymbolId =
    match names with
        | [] -> TraitImplementationParameterRegistration(context = context, nextTypeSymbolId = nextTypeSymbolId)
        | name :: tail ->
            registerImplementationParameters(
                tail,
                addTypeParameter(name)(SemParameter(nextTypeSymbolId)(name))(context),
                nextTypeSymbolId + 1
            )

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
                | None ->
                    TraitConstraintRegistration(constraints = reverse(reversed), error = traitName
                    |> UnknownTraitImplementationRequirement(owner)
                    |> Some)
                | Some(TraitInferenceDefinition { name = _name, parameterCount = expectedArity, methods = _methods, supertraits = _supertraits }) ->
                    let actualArity = syntaxListLength(typeArguments)
                    in
                        if expectedArity == actualArity
                        then
                            let requirementNames =
                                []
                                |> collectImplementationTypeNamesFromList(typeArguments)
                                |> sortImplementationNames
                            in
                                match firstNameNotIn(requirementNames)(headNames) with
                                    | Some(name) ->
                                        TraitConstraintRegistration(constraints = reverse(reversed), error = name
                                        |> TraitImplementationRequirementVariableEscapes(owner)
                                        |> Some)
                                    | None ->
                                        match resolveConstructorParameters(typeArguments)(context)([]) with
                                            | TypeListResolutionResult { semanticTypes = arguments, error = None } ->
                                                resolveImplementationRequirements(
                                                    owner,
                                                    tail,
                                                    headNames,
                                                    context,
                                                    environment,
                                                    TraitConstraint(traitName = canonicalTraitName(traitName), typeArguments = arguments) :: reversed
                                                )
                                            | TypeListResolutionResult { semanticTypes = _arguments, error = Some(error) } ->
                                                TraitConstraintRegistration(constraints = reverse(
                                                    reversed
                                                ), error = Some(
                                                    ProgramTypeResolutionError(error)
                                                ))
                        else
                            TraitConstraintRegistration(constraints = reverse(reversed), error = actualArity
                            |> TraitImplementationRequirementArityMismatch(traitName)(expectedArity)
                            |> Some)

let recursive semanticTypesAreConcrete semanticTypes =
    match semanticTypes with
        | [] -> true
        | head :: tail ->
            if semanticTypeIsConcrete(head)
            then semanticTypesAreConcrete(tail)
            else false
and semanticTypeIsConcrete semanticType =
    match semanticType with
        | SemVariable(_) -> false
        | SemParameter(_, _) -> false
        | SemFunction(argument, result, row) ->
            if semanticTypeIsConcrete(argument)
            then
                if semanticTypeIsConcrete(result)
                then
                    match row with
                        | None -> true
                        | Some(rowType) -> semanticTypeIsConcrete(rowType)
                else false
            else false
        | SemList(element) -> semanticTypeIsConcrete(element)
        | SemTuple(elements) -> semanticTypesAreConcrete(elements)
        | SemNamed(_symbolId, _name, arguments) -> semanticTypesAreConcrete(arguments)
        | SemPointer(pointee) -> semanticTypeIsConcrete(pointee)
        | SemCapability(_name, arguments) -> semanticTypesAreConcrete(arguments)
        | SemRow(capabilities, tail) ->
            if semanticTypesAreConcrete(capabilities)
            then
                match tail with
                    | None -> true
                    | Some(tailType) -> semanticTypeIsConcrete(tailType)
            else false
        | _ -> true

let recursive semanticTypesStructuralSize : List(SemanticType) -> Int =
    given (semanticTypes) ->
        match semanticTypes with
            | [] -> 0
            | head :: tail -> semanticTypeStructuralSize(head) + semanticTypesStructuralSize(tail)
and semanticTypeStructuralSize : SemanticType -> Int =
    given (semanticType) ->
        match semanticType with
            | SemVariable(_) -> 0
            | SemParameter(_, _) -> 0
            | SemFunction(argument, result, _row) ->
                1 + semanticTypeStructuralSize(
                    argument
                ) + semanticTypeStructuralSize(result)
            | SemList(element) -> 1 + semanticTypeStructuralSize(element)
            | SemTuple(elements) -> 1 + semanticTypesStructuralSize(elements)
            | SemNamed(_symbolId, _name, arguments) -> 1 + semanticTypesStructuralSize(arguments)
            | SemPointer(pointee) -> 1 + semanticTypeStructuralSize(pointee)
            | SemCapability(_name, arguments) -> 1 + semanticTypesStructuralSize(arguments)
            | SemRow(capabilities, tail) ->
                let tailSize =
                    match tail with
                        | None -> 0
                        | Some(tailType) -> semanticTypeStructuralSize(tailType)
                in 1 + semanticTypesStructuralSize(capabilities) + tailSize
            | _ -> 1

let recursive firstNonDecreasingRequirement headSize requirements =
    match requirements with
        | [] -> None
        | TraitConstraint { traitName = traitName, typeArguments = typeArguments } :: tail ->
            let requirementSize = 1 + semanticTypesStructuralSize(typeArguments)
            in
                if requirementSize >= headSize
                then Some((traitName, typeArguments))
                else firstNonDecreasingRequirement(headSize)(tail)

// Structural decrease is load-bearing only for generic heads. Concrete heads cannot expand through
// substitution and are instead bounded by exact cycle detection and the resolution-depth guard.
let nonDecreasingImplementationRequirement headTypes requirements =
    if semanticTypesAreConcrete(headTypes)
    then None
    else firstNonDecreasingRequirement(1 + semanticTypesStructuralSize(headTypes))(requirements)

let recursive implementationBindingExists methodName bindings =
    match bindings with
        | [] -> false
        | TraitImplementationMethodBinding { methodName = candidateName, implementation = _implementation } :: tail ->
            if methodName == candidateName
            then true
            else implementationBindingExists(methodName)(tail)

let recursive expressionPairDependsOnTraitMethod traitName methodName left right =
    if expressionDependsOnTraitMethod(traitName)(methodName)(left)
    then true
    else expressionDependsOnTraitMethod(traitName)(methodName)(right)
and expressionsDependOnTraitMethod traitName methodName expressions =
    match expressions with
        | [] -> false
        | head :: tail ->
            if expressionDependsOnTraitMethod(traitName)(methodName)(head)
            then true
            else expressionsDependOnTraitMethod(traitName)(methodName)(tail)
and expressionFieldsDependOnTraitMethod traitName methodName fields =
    match fields with
        | [] -> false
        | (_name, value) :: tail ->
            if expressionDependsOnTraitMethod(traitName)(methodName)(value)
            then true
            else expressionFieldsDependOnTraitMethod(traitName)(methodName)(tail)
and matchCasesDependOnTraitMethod traitName methodName cases =
    match cases with
        | [] -> false
        | (_pattern, body, guard) :: tail ->
            if expressionDependsOnTraitMethod(traitName)(methodName)(body)
            then true
            else
                let guardDepends =
                    match guard with
                        | None -> false
                        | Some(guardExpression) ->
                            expressionDependsOnTraitMethod(
                                traitName,
                                methodName,
                                guardExpression
                            )
                in
                    if guardDepends
                    then true
                    else matchCasesDependOnTraitMethod(traitName)(methodName)(tail)
and handlerArmsDependOnTraitMethod traitName methodName arms =
    match arms with
        | [] -> false
        | (_capabilityName, _operationName, _patterns, body) :: tail ->
            if expressionDependsOnTraitMethod(traitName)(methodName)(body)
            then true
            else handlerArmsDependOnTraitMethod(traitName)(methodName)(tail)
and expressionDependsOnTraitMethod traitName methodName expression =
    match expression with
        | ExprAt(_span, inner) -> expressionDependsOnTraitMethod(traitName)(methodName)(inner)
        | ExprNode(_id, inner) -> expressionDependsOnTraitMethod(traitName)(methodName)(inner)
        | ExprQualifiedVar(candidateTraitName, candidateMethodName) ->
            if traitName == candidateTraitName
            then methodName == candidateMethodName
            else false
        | ExprAdd(left, right) -> expressionPairDependsOnTraitMethod(traitName)(methodName)(left)(right)
        | ExprSubtract(left, right) -> expressionPairDependsOnTraitMethod(traitName)(methodName)(left)(right)
        | ExprMultiply(left, right) -> expressionPairDependsOnTraitMethod(traitName)(methodName)(left)(right)
        | ExprDivide(left, right) -> expressionPairDependsOnTraitMethod(traitName)(methodName)(left)(right)
        | ExprModulo(left, right) -> expressionPairDependsOnTraitMethod(traitName)(methodName)(left)(right)
        | ExprBitwiseAnd(left, right) -> expressionPairDependsOnTraitMethod(traitName)(methodName)(left)(right)
        | ExprBitwiseOr(left, right) -> expressionPairDependsOnTraitMethod(traitName)(methodName)(left)(right)
        | ExprBitwiseXor(left, right) -> expressionPairDependsOnTraitMethod(traitName)(methodName)(left)(right)
        | ExprShiftLeft(left, right) -> expressionPairDependsOnTraitMethod(traitName)(methodName)(left)(right)
        | ExprShiftRight(left, right) -> expressionPairDependsOnTraitMethod(traitName)(methodName)(left)(right)
        | ExprGreaterThan(left, right) -> expressionPairDependsOnTraitMethod(traitName)(methodName)(left)(right)
        | ExprLessThan(left, right) -> expressionPairDependsOnTraitMethod(traitName)(methodName)(left)(right)
        | ExprGreaterOrEqual(left, right) -> expressionPairDependsOnTraitMethod(traitName)(methodName)(left)(right)
        | ExprLessOrEqual(left, right) -> expressionPairDependsOnTraitMethod(traitName)(methodName)(left)(right)
        | ExprEqual(left, right) -> expressionPairDependsOnTraitMethod(traitName)(methodName)(left)(right)
        | ExprNotEqual(left, right) -> expressionPairDependsOnTraitMethod(traitName)(methodName)(left)(right)
        | ExprResultPipe(left, right) -> expressionPairDependsOnTraitMethod(traitName)(methodName)(left)(right)
        | ExprResultMapErrorPipe(left, right) -> expressionPairDependsOnTraitMethod(traitName)(methodName)(left)(right)
        | ExprCons(left, right) -> expressionPairDependsOnTraitMethod(traitName)(methodName)(left)(right)
        | ExprBitwiseNot(operand) -> expressionDependsOnTraitMethod(traitName)(methodName)(operand)
        | ExprLogicalNot(operand) -> expressionDependsOnTraitMethod(traitName)(methodName)(operand)
        | ExprLogicalAnd(left, right) -> expressionPairDependsOnTraitMethod(traitName)(methodName)(left)(right)
        | ExprLogicalOr(left, right) -> expressionPairDependsOnTraitMethod(traitName)(methodName)(left)(right)
        | ExprLet(_name, value, body, _parameters, _annotation, _requirements) ->
            expressionPairDependsOnTraitMethod(
                traitName,
                methodName,
                value,
                body
            )
        | ExprLetResult(_name, value, body) -> expressionPairDependsOnTraitMethod(traitName)(methodName)(value)(body)
        | ExprLetRecursive(_name, value, body, _parameters, _annotation, _requirements) ->
            expressionPairDependsOnTraitMethod(
                traitName,
                methodName,
                value,
                body
            )
        | ExprIf(condition, thenBranch, elseBranch) ->
            if expressionDependsOnTraitMethod(traitName)(methodName)(condition)
            then true
            else expressionPairDependsOnTraitMethod(traitName)(methodName)(thenBranch)(elseBranch)
        | ExprLambda(_name, body, _annotation) -> expressionDependsOnTraitMethod(traitName)(methodName)(body)
        | ExprCall(function, argument, _whitespace, _layout) ->
            expressionPairDependsOnTraitMethod(
                traitName,
                methodName,
                function,
                argument
            )
        | ExprTuple(elements) -> expressionsDependOnTraitMethod(traitName)(methodName)(elements)
        | ExprList(elements, _isMultiline) -> expressionsDependOnTraitMethod(traitName)(methodName)(elements)
        | ExprMatch(value, cases, _position) ->
            if expressionDependsOnTraitMethod(traitName)(methodName)(value)
            then true
            else matchCasesDependOnTraitMethod(traitName)(methodName)(cases)
        | ExprAwait(task) -> expressionDependsOnTraitMethod(traitName)(methodName)(task)
        | ExprRecord(_name, fields, _isMultiline) -> expressionFieldsDependOnTraitMethod(traitName)(methodName)(fields)
        | ExprRecordUpdate(target, fields) ->
            if expressionDependsOnTraitMethod(traitName)(methodName)(target)
            then true
            else expressionFieldsDependOnTraitMethod(traitName)(methodName)(fields)
        | ExprPerform(operation) -> expressionDependsOnTraitMethod(traitName)(methodName)(operation)
        | ExprHandle(body, arms) ->
            if expressionDependsOnTraitMethod(traitName)(methodName)(body)
            then true
            else handlerArmsDependOnTraitMethod(traitName)(methodName)(arms)
        | ExprInt(_) -> false
        | ExprBigInt(_) -> false
        | ExprUInt(_, _, _) -> false
        | ExprFloat(_, _) -> false
        | ExprString(_) -> false
        | ExprRune(_) -> false
        | ExprBool(_) -> false
        | ExprVar(_) -> false

let recursive activeDefaultMethodNames methods bindings reversed =
    match methods with
        | [] ->
            reversed
            |> reverse
            |> sortImplementationNames
        | TraitMethodInferenceDefinition { name = methodName, scheme = _scheme, defaultImplementation = defaultImplementation } :: tail ->
            match defaultImplementation with
                | None -> activeDefaultMethodNames(tail)(bindings)(reversed)
                | Some(_) ->
                    if implementationBindingExists(methodName)(bindings)
                    then activeDefaultMethodNames(tail)(bindings)(reversed)
                    else activeDefaultMethodNames(tail)(bindings)(methodName :: reversed)

let recursive findInferenceTraitMethod methodName methods =
    match methods with
        | [] -> None
        | TraitMethodInferenceDefinition { name = candidateName, scheme = scheme, defaultImplementation = defaultImplementation } :: tail ->
            if methodName == candidateName
            then
                Some(
                    TraitMethodInferenceDefinition(name = candidateName, scheme = scheme, defaultImplementation = defaultImplementation)
                )
            else findInferenceTraitMethod(methodName)(tail)

// A default-method cycle as stage 0 names it: the methods from the first one visited to the one
// reached again, joined by arrows. `path` holds the methods visited so far, most recent first.
let defaultCycleTrace (methodName: Str) (path: List(Str)) =
    methodName :: path
    |> reverse
    |> Ashes.Text.join(" -> ")

let recursive validateDefaultMethodDependencies traitName methodName body remaining activeDefaults methods path completed =
    match remaining with
        | [] -> DefaultMethodCycleValidation(completed = methodName :: completed, cycle = None)
        | dependency :: tail ->
            if expressionDependsOnTraitMethod(traitName)(dependency)(body)
            then
                match validateDefaultMethodDependency(
                    traitName,
                    dependency,
                    activeDefaults,
                    methods,
                    methodName :: path,
                    completed
                ) with
                    | DefaultMethodCycleValidation { completed = dependencyCompleted, cycle = None } ->
                        validateDefaultMethodDependencies(
                            traitName,
                            methodName,
                            body,
                            tail,
                            activeDefaults,
                            methods,
                            path,
                            dependencyCompleted
                        )
                    | cycle -> cycle
            else
                validateDefaultMethodDependencies(
                    traitName,
                    methodName,
                    body,
                    tail,
                    activeDefaults,
                    methods,
                    path,
                    completed
                )
and validateDefaultMethodDependency traitName methodName activeDefaults methods path completed =
    if nameExists(methodName)(completed)
    then DefaultMethodCycleValidation(completed = completed, cycle = None)
    else
        if nameExists(methodName)(path)
        then
            DefaultMethodCycleValidation(completed = completed, cycle = path
            |> defaultCycleTrace(methodName)
            |> Some)
        else
            match findInferenceTraitMethod(methodName)(methods) with
                | None -> DefaultMethodCycleValidation(completed = completed, cycle = None)
                | Some(TraitMethodInferenceDefinition { name = _name, scheme = _scheme, defaultImplementation = None }) -> DefaultMethodCycleValidation(completed = methodName :: completed, cycle = None)
                | Some(TraitMethodInferenceDefinition { name = _name, scheme = _scheme, defaultImplementation = Some(body) }) ->
                    validateDefaultMethodDependencies(
                        traitName,
                        methodName,
                        body,
                        activeDefaults,
                        activeDefaults,
                        methods,
                        path,
                        completed
                    )

let recursive validateDefaultMethodDependencyRoots traitName remaining activeDefaults methods completed =
    match remaining with
        | [] -> None
        | methodName :: tail ->
            match validateDefaultMethodDependency(traitName)(methodName)(activeDefaults)(methods)([])(completed) with
                | DefaultMethodCycleValidation { completed = nextCompleted, cycle = None } ->
                    validateDefaultMethodDependencyRoots(
                        traitName,
                        tail,
                        activeDefaults,
                        methods,
                        nextCompleted
                    )
                | DefaultMethodCycleValidation { completed = _nextCompleted, cycle = Some(trace) } ->
                    Some(
                        trace
                    )

let validateDefaultMethodDependencyCycles traitName methods bindings =
    (let activeDefaults = activeDefaultMethodNames(methods)(bindings)([])
    in validateDefaultMethodDependencyRoots(traitName)(activeDefaults)(activeDefaults)(methods)([]))

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
                | TraitHeadOverlapResult { substitutions = nextSubstitutions, overlaps = true } ->
                    traitHeadTypesOverlap(
                        leftTail,
                        rightTail,
                        nextSubstitutions
                    )
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
                        | (SemList(leftElement), SemList(rightElement)) ->
                            traitHeadTypeOverlaps(
                                leftElement,
                                rightElement,
                                substitutions
                            )
                        | (SemTuple(leftElements), SemTuple(rightElements)) ->
                            traitHeadTypesOverlap(
                                leftElements,
                                rightElements,
                                substitutions
                            )
                        | (SemNamed(_leftId, leftName, leftArguments), SemNamed(_rightId, rightName, rightArguments)) ->
                            if leftName == rightName
                            then traitHeadTypesOverlap(leftArguments)(rightArguments)(substitutions)
                            else TraitHeadOverlapResult(substitutions = substitutions, overlaps = false)
                        | (SemPointer(leftPointee), SemPointer(rightPointee)) ->
                            traitHeadTypeOverlaps(
                                leftPointee,
                                rightPointee,
                                substitutions
                            )
                        | (SemOpaque(leftName), SemOpaque(rightName)) -> TraitHeadOverlapResult(substitutions = substitutions, overlaps = leftName == rightName)
                        | _ -> TraitHeadOverlapResult(substitutions = substitutions, overlaps = false)

let existingImplementationSite (found: Maybe(TraitImplementationInferenceDefinition)) =
    match found with
        | Some(TraitImplementationInferenceDefinition { site = site }) -> site
        | None -> None

// The site of an implementation declared at `offset` in the unit being inferred.
let implementationSiteAt (environment: TypeEnvironment) (offset: Maybe(Int)) =
    match offset with
        | Some(start) -> Some(TraitImplementationSite(unitIndex = environment.currentUnit, offset = start, sourcePath = None))
        | None -> None

// The registered implementation, if any, whose head overlaps the candidate's.
let recursive firstOverlappingImplementation candidateArguments implementations =
    match implementations with
        | [] -> None
        | (TraitImplementationInferenceDefinition { typeArguments = existingArguments } as existing) :: tail ->
            match traitHeadTypesOverlap(existingArguments)(candidateArguments)([]) with
                | TraitHeadOverlapResult { substitutions = _substitutions, overlaps = true } -> Some(existing)
                | _ -> firstOverlappingImplementation(candidateArguments)(tail)

let recursive anyTraitImplementationHeadOverlaps candidateArguments implementations =
    match implementations with
        | [] -> false
        | TraitImplementationInferenceDefinition { traitName = _traitName, typeArguments = existingArguments, requirements = _requirements, methods = _methods } :: tail ->
            match traitHeadTypesOverlap(existingArguments)(candidateArguments)([]) with
                | TraitHeadOverlapResult { substitutions = _substitutions, overlaps = true } -> true
                | _ -> anyTraitImplementationHeadOverlaps(candidateArguments)(tail)

let provenanceBelongsToPackage packageId provenance =
    match provenance with
        | DeclarationProvenance { packageId = declarationPackageId } -> packageId == declarationPackageId

let nominalHeadBelongsToPackage packageId semanticType context =
    match semanticType with
        | SemNamed(symbolId, _name, _arguments) ->
            match nominalTypeProvenance(symbolId)(context) with
                | Some(provenance) -> provenanceBelongsToPackage(packageId)(provenance)
                | None -> false
        | _ -> false

let recursive anyNominalHeadBelongsToPackage packageId semanticTypes context =
    match semanticTypes with
        | [] -> false
        | head :: tail ->
            if nominalHeadBelongsToPackage(packageId)(head)(context)
            then true
            else anyNominalHeadBelongsToPackage(packageId)(tail)(context)

let implementationSatisfiesOrphanRule traitProvenance typeArguments environment =
    (let packageId = inferencePackageId(environment)
    in
        if provenanceBelongsToPackage(packageId)(traitProvenance)
        then true
        else
            environment
            |> inferenceTypeResolutionContext
            |> anyNominalHeadBelongsToPackage(packageId)(typeArguments))

let recursive implementationMethodSubstitution quantified typeArguments reversed =
    match (quantified, typeArguments) with
        | ((variableId, _name) :: quantifiedTail, argument :: argumentTail) ->
            implementationMethodSubstitution(
                quantifiedTail,
                argumentTail,
                (variableId, argument) :: reversed
            )
        | _ -> reversed

let recursive registerImplementationMethods traitName bindings typeArguments environment substitution supply reversed =
    match bindings with
        | [] ->
            TraitImplementationMethodRegistration(methods = reverse(
                reversed
            ), substitution = substitution, supply = supply, error = None)
        | TraitImplementationMethodBinding { methodName = methodName, implementation = implementation } :: tail ->
            match resolveTraitMethod(traitName)(methodName)(environment) with
                | None ->
                    TraitImplementationMethodRegistration(methods = reverse(
                        reversed
                    ), substitution = substitution, supply = supply, error = methodName
                    |> UnknownTraitImplementationMethod(traitName)
                    |> Some)
                | Some(TraitMethodInferenceDefinition { name = _name, scheme = TypeScheme { quantified = quantified, body = methodBody, constraints = _constraints }, defaultImplementation = _defaultImplementation }) ->
                    let methodSubstitution = implementationMethodSubstitution(quantified)(typeArguments)([])
                    in
                        let expectedType = applySubstitution(methodSubstitution)(methodBody)
                        in
                            match inferExpressionFromLocated(implementation)(environment)(substitution)(supply) with
                                | TypeInferenceResult { semanticType = implementationType, substitution = implementationSubstitution, supply = implementationSupply, constraints = _implementationConstraints, error = None } ->
                                    match implementationType
                                    |> applySubstitution(implementationSubstitution)
                                    |> unify(applySubstitution(implementationSubstitution)(expectedType)) with
                                        | UnificationResult { substitution = methodUnification, error = None } ->
                                            let combined =
                                                appendProgramSubstitution(
                                                    methodUnification,
                                                    implementationSubstitution
                                                )
                                            in
                                                let registered =
                                                    TraitImplementationMethodInferenceDefinition(name = methodName, implementation = implementation, semanticType = applySubstitution(
                                                        combined,
                                                        expectedType
                                                    ))
                                                in
                                                    registerImplementationMethods(
                                                        traitName,
                                                        tail,
                                                        typeArguments,
                                                        environment,
                                                        combined,
                                                        implementationSupply,
                                                        registered :: reversed
                                                    )
                                        | UnificationResult { substitution = _methodUnification, error = Some(error) } ->
                                            TraitImplementationMethodRegistration(methods = reverse(
                                                reversed
                                            ), substitution = implementationSubstitution, supply = implementationSupply, error = Some(
                                                ProgramExpressionError(InferenceUnificationError(error))
                                            ))
                                | TypeInferenceResult { semanticType = _implementationType, substitution = failedSubstitution, supply = failedSupply, constraints = _implementationConstraints, error = Some(error) } ->
                                    TraitImplementationMethodRegistration(methods = reverse(
                                        reversed
                                    ), substitution = failedSubstitution, supply = failedSupply, error = Some(
                                        ProgramExpressionError(error)
                                    ))

let registerTraitImplementation (offset: Maybe(Int)) declaration state =
    match (declaration, state) with
        | (TraitImplementationDecl { traitName = traitName, typeArguments = typeArguments, requirements = requirements, bindings = bindings }, ProgramInferenceState { environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = None }) ->
            match resolveTraitBinding(traitName)(environment) with
                | None ->
                    ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = Some(
                        UnknownTraitImplementation(traitName)
                    ))
                | Some(TraitInferenceDefinition { name = _name, parameterCount = expectedArity, methods = traitMethods, supertraits = _supertraits, provenance = traitProvenance }) ->
                    let actualArity = syntaxListLength(typeArguments)
                    in
                        if expectedArity == actualArity
                        then
                            let headNames = collectImplementationHeadNames(typeArguments)
                            in
                                match registerImplementationParameters(
                                    headNames,
                                    inferenceTypeResolutionContext(environment),
                                    nextTypeSymbolId
                                ) with
                                    | TraitImplementationParameterRegistration { context = context, nextTypeSymbolId = implementationNextTypeSymbolId } ->
                                        match resolveConstructorParameters(typeArguments)(context)([]) with
                                            | TypeListResolutionResult { semanticTypes = resolvedTypeArguments, error = None } ->
                                                match resolveImplementationRequirements(
                                                    traitName,
                                                    requirements,
                                                    headNames,
                                                    context,
                                                    environment,
                                                    []
                                                ) with
                                                    | TraitConstraintRegistration { constraints = resolvedRequirements, error = None } ->
                                                        match nonDecreasingImplementationRequirement(
                                                            resolvedTypeArguments,
                                                            resolvedRequirements
                                                        ) with
                                                            | Some((requirementTraitName, requirementTypes)) ->
                                                                ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = implementationNextTypeSymbolId, error = requirementTypes
                                                                |> NonDecreasingTraitImplementationRequirement(traitName)(resolvedTypeArguments)(requirementTraitName)
                                                                |> Some)
                                                            | None ->
                                                                if implementationSatisfiesOrphanRule(
                                                                    traitProvenance,
                                                                    resolvedTypeArguments,
                                                                    environment
                                                                )
                                                                then
                                                                    match findUnknownImplementationBinding(
                                                                        traitName,
                                                                        bindings,
                                                                        environment
                                                                    ) with
                                                                        | Some(methodName) ->
                                                                            ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = implementationNextTypeSymbolId, error = methodName
                                                                            |> UnknownTraitImplementationMethod(
                                                                                traitName
                                                                            )
                                                                            |> Some)
                                                                        | None ->
                                                                            match findDuplicateImplementationBinding(
                                                                                bindings,
                                                                                []
                                                                            ) with
                                                                                | Some(methodName) ->
                                                                                    ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = implementationNextTypeSymbolId, error = methodName
                                                                                    |> DuplicateTraitImplementationMethod(
                                                                                        traitName
                                                                                    )
                                                                                    |> Some)
                                                                                | None ->
                                                                                    match findMissingImplementationBinding(
                                                                                        traitMethods,
                                                                                        bindings
                                                                                    ) with
                                                                                        | Some(methodName) ->
                                                                                            ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = implementationNextTypeSymbolId, error = methodName
                                                                                            |> MissingTraitImplementationMethod(
                                                                                                traitName
                                                                                            )
                                                                                            |> Some)
                                                                                        | None ->
                                                                                            if environment
                                                                                            |> resolveTraitImplementations(
                                                                                                traitName
                                                                                            )
                                                                                            |> anyTraitImplementationHeadOverlaps(
                                                                                                resolvedTypeArguments
                                                                                            )
                                                                                            then
                                                                                                ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = implementationNextTypeSymbolId, error = Some(
                                                                                                    OverlappingTraitImplementations(
                                                                                                        traitName,
                                                                                                        environment
                                                                                                        |> resolveTraitImplementations(traitName)
                                                                                                        |> firstOverlappingImplementation(resolvedTypeArguments)
                                                                                                        |> existingImplementationSite,
                                                                                                        implementationSiteAt(environment)(offset)
                                                                                                    )
                                                                                                ))
                                                                                            else
                                                                                                match registerImplementationMethods(
                                                                                                    traitName,
                                                                                                    bindings,
                                                                                                    resolvedTypeArguments,
                                                                                                    // The implementation is in scope for its own methods: one that calls itself at the type it
                                                                                                    // is being written for is supplied by this implementation.
                                                                                                    addTraitImplementation(traitName)(resolvedTypeArguments)(simplifyTraitConstraints(environment)(resolvedRequirements))([])(environment),
                                                                                                    substitution,
                                                                                                    supply,
                                                                                                    []
                                                                                                ) with
                                                                                                    | TraitImplementationMethodRegistration { methods = registeredMethods, substitution = implementationSubstitution, supply = implementationSupply, error = None } ->
                                                                                                        match validateDefaultMethodDependencyCycles(
                                                                                                            traitName,
                                                                                                            traitMethods,
                                                                                                            bindings
                                                                                                        ) with
                                                                                                            | Some(trace) ->
                                                                                                                ProgramInferenceState(environment = environment, substitution = implementationSubstitution, supply = implementationSupply, nextTypeSymbolId = implementationNextTypeSymbolId, error = trace
                                                                                                                |> DefaultTraitMethodDependencyCycle(
                                                                                                                    traitName
                                                                                                                )
                                                                                                                |> Some)
                                                                                                            | None ->
                                                                                                                ProgramInferenceState(environment = addTraitImplementationAt(
                                                                                                                    implementationSiteAt(environment)(offset),
                                                                                                                    traitName,
                                                                                                                    resolvedTypeArguments,
                                                                                                                    simplifyTraitConstraints(
                                                                                                                        environment,
                                                                                                                        resolvedRequirements
                                                                                                                    ),
                                                                                                                    registeredMethods,
                                                                                                                    environment
                                                                                                                ), substitution = implementationSubstitution, supply = implementationSupply, nextTypeSymbolId = implementationNextTypeSymbolId, error = None)
                                                                                                    | TraitImplementationMethodRegistration { methods = _registeredMethods, substitution = failedSubstitution, supply = failedSupply, error = Some(error) } ->
                                                                                                        ProgramInferenceState(environment = environment, substitution = failedSubstitution, supply = failedSupply, nextTypeSymbolId = implementationNextTypeSymbolId, error = Some(
                                                                                                            error
                                                                                                        ))
                                                                else
                                                                    ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = implementationNextTypeSymbolId, error = Some(
                                                                        environment
                                                                        |> inferencePackageId
                                                                        |> OrphanTraitImplementation(traitName)(resolvedTypeArguments)
                                                                    ))
                                                    | TraitConstraintRegistration { constraints = _resolvedRequirements, error = Some(error) } ->
                                                        ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = implementationNextTypeSymbolId, error = Some(
                                                            error
                                                        ))
                                            | TypeListResolutionResult { semanticTypes = _resolvedTypeArguments, error = Some(error) } ->
                                                ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = implementationNextTypeSymbolId, error = Some(
                                                    ProgramTypeResolutionError(error)
                                                ))
                        else
                            ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = actualArity
                            |> TraitImplementationArityMismatch(traitName)(expectedArity)
                            |> Some)
        | (_declaration, failedState) -> failedState

let recursive validateTraitDefaultMethods traitName methods state =
    match (methods, state) with
        | ([], _) -> state
        | (TraitMethodDecl { name = methodName, signature = _signature, defaultImplementation = None } :: tail, _) ->
            validateTraitDefaultMethods(
                traitName,
                tail,
                state
            )
        | (TraitMethodDecl { name = methodName, signature = _signature, defaultImplementation = Some(defaultImplementation) } :: tail, ProgramInferenceState { environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = None }) ->
            match resolveTraitMethod(traitName)(methodName)(environment) with
                | None ->
                    ProgramInferenceState(environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = Some(
                        UnsupportedTopLevelDeclaration("trait default method")
                    ))
                | Some(TraitMethodInferenceDefinition { name = _name, scheme = scheme, defaultImplementation = _registeredDefault }) ->
                    match instantiate(scheme)(supply) with
                        | InstantiationResult { semanticType = expectedType, constraints = _expectedConstraints, supply = expectedSupply } ->
                            match inferExpressionFromLocated(
                                defaultImplementation,
                                environment,
                                substitution,
                                expectedSupply
                            ) with
                                | TypeInferenceResult { semanticType = implementationType, substitution = implementationSubstitution, supply = implementationSupply, constraints = _implementationConstraints, error = None } ->
                                    match implementationType
                                    |> applySubstitution(implementationSubstitution)
                                    |> unify(applySubstitution(implementationSubstitution)(expectedType)) with
                                        | UnificationResult { substitution = defaultSubstitution, error = None } ->
                                            let combined =
                                                appendProgramSubstitution(
                                                    defaultSubstitution,
                                                    implementationSubstitution
                                                )
                                            in
                                                validateTraitDefaultMethods(
                                                    traitName,
                                                    tail,
                                                    ProgramInferenceState(environment = environment, substitution = combined, supply = implementationSupply, nextTypeSymbolId = nextTypeSymbolId, error = None)
                                                )
                                        | UnificationResult { substitution = _defaultSubstitution, error = Some(error) } ->
                                            ProgramInferenceState(environment = environment, substitution = implementationSubstitution, supply = implementationSupply, nextTypeSymbolId = nextTypeSymbolId, error = Some(
                                                ProgramExpressionError(InferenceUnificationError(error))
                                            ))
                                | TypeInferenceResult { semanticType = _implementationType, substitution = failedSubstitution, supply = failedSupply, constraints = _implementationConstraints, error = Some(error) } ->
                                    ProgramInferenceState(environment = environment, substitution = failedSubstitution, supply = failedSupply, nextTypeSymbolId = nextTypeSymbolId, error = Some(
                                        ProgramExpressionError(error)
                                    ))
        | (_methods, failedState) -> failedState

let recursive validateTraitDefaults declarations state =
    match declarations with
        | [] -> state
        | TraitDecl { name = name, typeParameters = _parameters, supertraits = _supertraits, methods = methods } :: tail ->
            state
            |> validateTraitDefaultMethods(name)(methods)
            |> validateTraitDefaults(tail)

let inferTopLevelLet binding isRecursive state =
    match (binding, state) with
        | (LetBindingSyntax { name = name, value = value, sugarParameters = _parameters, typeAnnotation = annotation, requirements = requirements }, ProgramInferenceState { environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = None }) ->
            if isRecursive
            then inferRecursiveGroup([binding])(state)
            else
                let bindingValue = value
                in
                    match inferTopLevelBindingLocated(
                        name,
                        bindingValue,
                        annotation,
                        requirements,
                        environment,
                        substitution,
                        supply
                    ) with
                        | TopLevelBindingInferenceResult { environment = nextEnvironment, semanticType = _semanticType, substitution = nextSubstitution, supply = nextSupply, error = None } -> ProgramInferenceState(environment = nextEnvironment, substitution = nextSubstitution, supply = nextSupply, nextTypeSymbolId = nextTypeSymbolId, error = None)
                        | TopLevelBindingInferenceResult { environment = _nextEnvironment, semanticType = _semanticType, substitution = nextSubstitution, supply = nextSupply, error = Some(error) } ->
                            ProgramInferenceState(environment = environment, substitution = nextSubstitution, supply = nextSupply, nextTypeSymbolId = nextTypeSymbolId, error = Some(
                                ProgramExpressionError(error)
                            ))
        | (_binding, failedState) -> failedState

let recursive collectExternalDeclarations items reversed =
    match items with
        | [] -> reverse(reversed)
        | TopLevelAt(_span, inner) :: tail -> collectExternalDeclarations(inner :: tail)(reversed)
        | TopLevelExternal(declaration) :: tail -> collectExternalDeclarations(tail)(declaration :: reversed)
        | _head :: tail -> collectExternalDeclarations(tail)(reversed)

let withProgramInferenceEnvironment environment (state: ProgramInferenceState) = state with environment = environment

let withProgramInferenceError error (state: ProgramInferenceState) = state with error = error

let recursive registerExternalOpaqueTypes declarations state =
    match declarations with
        | [] -> state
        | ExternalOpaqueType(name, destructor) :: tail ->
            match state with
                | ProgramInferenceState { environment = environment, error = None } ->
                    state
                    |> withProgramInferenceEnvironment(addInferenceExternalType(
                        name,
                        destructor,
                        environment
                    ))
                    |> registerExternalOpaqueTypes(tail)
                | failedState -> failedState
        | _declaration :: tail -> registerExternalOpaqueTypes(tail)(state)

// External typing writes `void` and the argument of a parameterless external as the empty tuple.
// The language gives both the built-in `Unit`, the type of the `Unit` value a call passes, so an
// environment that defines `Unit` has them rewritten to it.
let recursive withNominalUnit unitType semanticType =
    match semanticType with
        | SemTuple([]) -> unitType
        | SemTuple(elements) ->
            elements
            |> withNominalUnitIn(unitType)
            |> SemTuple
        | SemList(element) ->
            element
            |> withNominalUnit(unitType)
            |> SemList
        | SemFunction(argument, result, row) ->
            SemFunction(withNominalUnit(unitType)(argument))(withNominalUnit(unitType)(result))(row)
        | SemNamed(symbolId, name, arguments) ->
            arguments
            |> withNominalUnitIn(unitType)
            |> SemNamed(symbolId)(name)
        | SemPointer(pointee) ->
            pointee
            |> withNominalUnit(unitType)
            |> SemPointer
        | other -> other
and withNominalUnitIn unitType semanticTypes =
    match semanticTypes with
        | [] -> []
        | head :: tail -> withNominalUnit(unitType)(head) :: withNominalUnitIn(unitType)(tail)

let externalTypeWithUnit environment semanticType =
    match environment
    |> inferenceTypeResolutionContext
    |> resolveTypeExpression(TypeNamed("Unit")) with
        | TypeResolutionResult { semanticType = SemNamed(symbolId, name, []), error = None } ->
            withNominalUnit(SemNamed(symbolId)(name)([]))(semanticType)
        | _ -> semanticType

let externalDeclarationName declaration =
    match declaration with
        | ExternalFunction(name, _parameterTypes, _returnType, _symbol, _ownerships, _capabilityRow) -> name
        | ExternalOpaqueType(name, _destructor) -> name

let registerExternalFunction declaration declarations state =
    match state with
        | ProgramInferenceState { environment = environment, error = None } ->
            match typeExternalFunction(
                declaration,
                declarations,
                inferenceTypeResolutionContext(environment)
            ) with
                | ExternalFunctionTypingResult { typing = Some(ExternalFunctionTyping { name = name, directType = directType, firstClassType = firstClassType }), error = None } ->
                    let directScheme = TypeScheme(quantified = [], body = externalTypeWithUnit(environment)(directType), constraints = [])
                    in
                        let firstClassScheme =
                            match firstClassType with
                                | None -> None
                                | Some(semanticType) -> Some(TypeScheme(quantified = [], body = externalTypeWithUnit(environment)(semanticType), constraints = []))
                        in
                            environment
                            |> addExternalFunctionBinding(
                                name,
                                directScheme,
                                firstClassScheme
                            )
                            |> (given (nextEnvironment) -> withProgramInferenceEnvironment(nextEnvironment)(state))
                | ExternalFunctionTypingResult { error = Some(error) } ->
                    withProgramInferenceError(error
                    |> ProgramExternalTypingError(externalDeclarationName(declaration))
                    |> Some)(state)
        | failedState -> failedState

let recursive groupBindingNames bindings =
    match bindings with
        | [] -> ""
        | LetBindingSyntax { name = name } :: [] -> name
        | LetBindingSyntax { name = name } :: tail -> name + ", " + groupBindingNames(tail)

let recursive topLevelItemDescription item =
    match item with
        | TopLevelAt(_span, inner) -> topLevelItemDescription(inner)
        | TopLevelType(TypeDecl { name = name }) -> Some("type " + name)
        | TopLevelTypeAlias(TypeAliasDecl { name = name }) -> Some("type " + name)
        | TopLevelZeroCostType(ZeroCostTypeDecl { name = name }) -> Some("type " + name)
        | TopLevelCapability(CapabilityDecl { name = name }) -> Some("capability " + name)
        | TopLevelProvide(ProvideDecl { capabilityName = name }) -> Some("provide " + name)
        | TopLevelTrait(TraitDecl { name = name }) -> Some("trait " + name)
        | TopLevelImplementation(TraitImplementationDecl { traitName = name }) -> Some("implement " + name)
        | TopLevelLet(LetBindingSyntax { name = name }, _isRecursive) -> Some("let " + name)
        | TopLevelRecursiveGroup(bindings) -> Some("let recursive " + groupBindingNames(bindings))
        | TopLevelExternal(ExternalFunction(name, _, _, _, _, _)) -> Some("external " + name)
        | TopLevelExternal(ExternalOpaqueType(name, _)) -> Some("external type " + name)
        | TopLevelExport(_export) -> None

let topLevelItemSpan item =
    match item with
        | TopLevelAt(span, _inner) -> Some(span)
        | _ -> None

let programErrorSpan error =
    match error with
        | ProgramExpressionError(inner) -> inferenceErrorSpan(inner)
        | _ -> None

let unlocatedProgramError error =
    match error with
        | ProgramExpressionError(inner) ->
            inner
            |> unlocatedInferenceError
            |> ProgramExpressionError
        | other -> other

let locatedProgramError packageId declaration declarationSpan error =
    ProgramErrorAt(
        InferenceErrorLocation(
            packageId = packageId,
            unitIndex = -1,
            moduleName = None,
            sourcePath = None,
            declaration = declaration,
            declarationSpan = declarationSpan,
            span = programErrorSpan(error)
        ),
        unlocatedProgramError(error)
    )

let withDeclarationSpan span (location: InferenceErrorLocation) = location with declarationSpan = Some(span)

let withUnitIndex unitIndex (location: InferenceErrorLocation) = location with unitIndex = unitIndex

// Tags the error a declaration raised with the declaration it was raised in. A declaration that
// is wrapped in its span is tagged once by its inner form, so the span is added here.
let locateTopLevelItemError item (state: ProgramInferenceState) =
    match state with
        | ProgramInferenceState { error = None } -> state
        | ProgramInferenceState { error = Some(ProgramErrorAt(location, error)) } ->
            match (location.declarationSpan, topLevelItemSpan(item)) with
                | (None, Some(span)) ->
                    state with error = Some(
                        ProgramErrorAt(withDeclarationSpan(span)(location))(error)
                    )
                | _ -> state
        | ProgramInferenceState { environment = environment, error = Some(error) } ->
            state with error = Some(
                locatedProgramError(
                    inferencePackageId(environment),
                    topLevelItemDescription(item),
                    topLevelItemSpan(item),
                    error
                )
            )

let hasProgramInferenceError (state: ProgramInferenceState) =
    match state with
        | ProgramInferenceState { error = Some(_error) } -> true
        | _ -> false

let locateUnitError unitIndex (state: ProgramInferenceState) =
    match state with
        | ProgramInferenceState { error = Some(ProgramErrorAt(location, error)) } ->
            state with error = Some(
                ProgramErrorAt(withUnitIndex(unitIndex)(location))(error)
            )
        | _ -> state

// Splits a tagged error into the bare error and its location for the finished result, and hands
// the environment back with the program-only rules off, as an environment for lowering has them.
let withProgramErrorLocation (finished: ProgramInferenceResult) =
    (let result = finished with environment = withoutProgramInferenceRules(finished.environment)
    in
        match result with
            | ProgramInferenceResult { error = Some(ProgramErrorAt(location, error)) } -> result with error = Some(error), errorLocation = Some(location)
            | ProgramInferenceResult { error = Some(error) } ->
                result with error = Some(unlocatedProgramError(error))
            | _ -> result)

// A program's inference between two top-level declarations. Every binding in scope is generalized
// there, so no later declaration can reach a type variable an earlier one bound: each declaration
// starts from an empty substitution and a closed generalization scope, and what it bound moves
// into `resolved`, the map the finished result's substitution is read from.
type ProgramInferenceProgress =
    | state: ProgramInferenceState
    | resolved: MapTree(Int, SemanticType)
    | nodeTypes: NodeTypeTable

let settleProgramInferenceState (resolved: MapTree(Int, SemanticType)) (nodeTypes: NodeTypeTable) (state: ProgramInferenceState) =
    (let settled = state with substitution = [], environment = withClosedGeneralizationScope(state.environment), supply = withoutNodeNotes(state.supply)
    in
        ProgramInferenceProgress(state = settled, resolved = extendSubstitutionMap(state.substitution)(resolved), nodeTypes = settleNodeNotes(state.substitution)(nodeNotesOf(state.supply))(nodeTypes)))

let startProgramInferenceProgress (state: ProgramInferenceState) = settleProgramInferenceState(Ashes.Collection.Map.empty)(emptyNodeTypeTable)(state)

let advanceProgramInference step (progress: ProgramInferenceProgress) =
    progress.state
    |> step
    |> settleProgramInferenceState(progress.resolved)(progress.nodeTypes)

// Notes each binding's finished scheme against the root node of its value, so a reader of the
// node table finds a top-level binding the way it finds a local one.
let recursive noteDeclarations (bindings: List(LetBindingSyntax)) (state: ProgramInferenceState) =
    match (bindings, state) with
        | ([], _) -> state
        | (_, ProgramInferenceState { error = Some(_error) }) -> state
        | (binding :: rest, _) ->
            match (nodeIdentityOf(binding.value) >= 0, resolveWrittenTypeBinding(binding.name)(state.environment)) with
                | (true, Some(scheme)) ->
                    let noted =
                        withNodeNote(NodeDeclarationNote(nodeIdentityOf(binding.value))(binding.name)(scheme))(state.supply)
                    in
                        let next = state with supply = noted
                        in noteDeclarations(rest)(next)
                | _ -> noteDeclarations(rest)(state)

// `Ashes` and the builtin runtime and primitive type names, which no declaration may take; the
// same names lowering refuses.
let isReservedDeclarationName (name: Str) = name == "Ashes" || name == "Unit" || name == "List" || name == "Maybe" || name == "Result" || name == "Socket" || name == "TlsSocket" || name == "Task" || name == "JoinHandle" || name == "Process" || name == "FileHandle" || name == "Float" || name == "Bytes" || name == "Rune" || name == "u8" || name == "u16" || name == "u32" || name == "u64"

// Refuses a reserved declaration name under the program inference rules; otherwise registers.
let refuseReservedDeclaration (name: Str) register (state: ProgramInferenceState) =
    match state with
        | ProgramInferenceState { environment = environment, error = None } ->
            if environment.programInferenceRules && isReservedDeclarationName(name)
            then state with error = Some(ReservedTypeDeclaration(name))
            else register(state)
        | _ -> register(state)

let recursive inferTopLevelItem externalDeclarations item state =
    locateTopLevelItemError(
        item,
        match item with
            | TopLevelAt(TextSpan { start = start }, TopLevelImplementation(declaration)) -> registerTraitImplementation(Some(start))(declaration)(state)
            | TopLevelAt(_span, inner) -> inferTopLevelItem(externalDeclarations)(inner)(state)
            | TopLevelExport(_export) -> state
            | TopLevelType(declaration) ->
                refuseReservedDeclaration(declaration.name)(registerTypeDeclaration(declaration))(state)
            | TopLevelTypeAlias(declaration) ->
                refuseReservedDeclaration(declaration.name)(registerTypeAlias(declaration))(state)
            | TopLevelZeroCostType(declaration) ->
                refuseReservedDeclaration(declaration.name)(registerZeroCostType(declaration))(state)
            | TopLevelCapability(declaration) -> registerCapabilityDeclaration(declaration)(state)
            | TopLevelProvide(declaration) -> registerCapabilityProvider(declaration)(state)
            | TopLevelTrait(declaration) -> registerTraitDeclaration(declaration)(state)
            | TopLevelImplementation(declaration) -> registerTraitImplementation(None)(declaration)(state)
            | TopLevelLet(binding, isRecursive) ->
                state
                |> inferTopLevelLet(binding)(isRecursive)
                |> noteDeclarations([binding])
            | TopLevelRecursiveGroup(bindings) ->
                state
                |> inferRecursiveGroup(bindings)
                |> noteDeclarations(bindings)
            | TopLevelExternal(ExternalOpaqueType(_name, _destructor)) -> state
            | TopLevelExternal(declaration) -> registerExternalFunction(declaration)(externalDeclarations)(state)
    )

let recursive inferTopLevelItems items externalDeclarations (progress: ProgramInferenceProgress) =
    match items with
        | [] -> progress
        | head :: tail ->
            if hasProgramInferenceError(progress.state)
            then progress
            else
                progress
                |> advanceProgramInference(inferTopLevelItem(externalDeclarations)(head))
                |> inferTopLevelItems(tail)(externalDeclarations)

let recursive appendProgramUnitItems left right =
    match left with
        | [] -> right
        | head :: tail -> head :: appendProgramUnitItems(tail)(right)

let recursive expandedProgramUnitItems units =
    match units with
        | [] -> []
        | ProgramInferenceUnit { program = ProgramSyntax { items = items } } :: tail ->
            tail
            |> expandedProgramUnitItems
            |> appendProgramUnitItems(items)

let recursive derivingContextForProgramUnits units context =
    match units with
        | [] -> context
        | ProgramInferenceUnit { program = ProgramSyntax { items = items } } :: tail ->
            context
            |> addDerivingItemsToContext(items)
            |> derivingContextForProgramUnits(tail)

let recursive expandProgramInferenceUnitsFrom context units =
    match units with
        | [] -> ProgramUnitExpansion(units = [], error = None)
        | ProgramInferenceUnit { packageId = packageId, program = program } :: tail ->
            match expandDerivedImplementationsFrom(context)(program) with
                | Error(error) -> ProgramUnitExpansion(units = [], error = Some(ProgramDerivingExpansionError(error)))
                | Ok(expanded) ->
                    match expandProgramInferenceUnitsFrom(context)(tail) with
                        | ProgramUnitExpansion { units = expandedTail, error = None } -> ProgramUnitExpansion(units = ProgramInferenceUnit(packageId = packageId, program = expanded) :: expandedTail, error = None)
                        | ProgramUnitExpansion { units = _expandedTail, error = Some(error) } ->
                            ProgramUnitExpansion(units = [], error = Some(
                                error
                            ))

let expandProgramInferenceUnits units =
    Unit
    |> emptyDerivingTypeContext
    |> derivingContextForProgramUnits(units)
    |> (given (context) -> expandProgramInferenceUnitsFrom(context)(units))

let stateWithInferencePackage packageId state =
    match state with
        | ProgramInferenceState { environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = error } ->
            ProgramInferenceState(environment = withInferencePackage(
                packageId,
                environment
            ), substitution = substitution, supply = supply, nextTypeSymbolId = nextTypeSymbolId, error = error)

let stateWithInferenceUnit unitIndex (state: ProgramInferenceState) = state with environment = withInferenceUnit(unitIndex)(state.environment)

let locateProgressUnitError unitIndex (progress: ProgramInferenceProgress) = ProgramInferenceProgress(state = locateUnitError(unitIndex)(progress.state), resolved = progress.resolved, nodeTypes = progress.nodeTypes)

let recursive inferExpandedProgramUnitsAt unitIndex units externalDeclarations (progress: ProgramInferenceProgress) =
    match units with
        | [] -> progress
        | ProgramInferenceUnit { packageId = packageId, program = ProgramSyntax { items = items } } :: tail ->
            if hasProgramInferenceError(progress.state)
            then progress
            else
                progress
                |> advanceProgramInference(stateWithInferencePackage(packageId))
                |> advanceProgramInference(stateWithInferenceUnit(unitIndex))
                |> advanceProgramInference(checkTypeAliasCycles(items))
                |> inferTopLevelItems(items)(externalDeclarations)
                |> locateProgressUnitError(unitIndex)
                |> inferExpandedProgramUnitsAt(unitIndex + 1)(tail)(externalDeclarations)

let inferExpandedProgramUnits units externalDeclarations progress = inferExpandedProgramUnitsAt(0)(units)(externalDeclarations)(progress)

let recursive appendResolvedSubstitution entries substitution =
    match entries with
        | [] -> substitution
        | head :: tail -> head :: appendResolvedSubstitution(tail)(substitution)

// The finished result's substitution: the trailing expression's own bindings ahead of everything
// the declarations bound, so a lookup still finds the youngest entry for a variable first.
let withResolvedSubstitution (resolved: MapTree(Int, SemanticType)) (result: ProgramInferenceResult) =
    result with substitution = appendResolvedSubstitution(result.substitution)(Ashes.Collection.Map.toList(resolved))

let inferProgramUnitBody body entryPackageId (nodeTypes: NodeTypeTable) state =
    match stateWithInferencePackage(entryPackageId)(state) with
        | ProgramInferenceState { environment = environment, substitution = substitution, supply = supply, nextTypeSymbolId = _nextTypeSymbolId, error = None } ->
            match body with
                | None ->
                    ProgramInferenceResult(
                        semanticType = SemTuple([]),
                        substitution = substitution,
                        environment = environment,
                        externalAbi = None,
                        nodeTypes = nodeTypes,
                        errorLocation = None,
                        error = None
                    )
                | Some(expression) ->
                    match supply
                    |> inferTopLevelExpressionLocated(expression)(environment)(substitution)
                    |> checkTrailingRequirements(expression)(environment) with
                        | TypeInferenceResult { semanticType = semanticType, substitution = bodySubstitution, supply = bodySupply, constraints = _constraints, error = None } ->
                            ProgramInferenceResult(
                                semanticType = semanticType,
                                substitution = bodySubstitution,
                                environment = environment,
                                externalAbi = None,
                                nodeTypes = settleNodeNotes(bodySubstitution)(nodeNotesOf(bodySupply))(nodeTypes),
                                errorLocation = None,
                                error = None
                            )
                        | TypeInferenceResult { semanticType = semanticType, substitution = bodySubstitution, supply = _bodySupply, constraints = _constraints, error = Some(error) } ->
                            ProgramInferenceResult(
                                semanticType = semanticType,
                                substitution = bodySubstitution,
                                environment = environment,
                                externalAbi = None,
                                nodeTypes = nodeTypes,
                                errorLocation = None,
                                error = Some(
                                    locatedProgramError(
                                        entryPackageId,
                                        None,
                                        None,
                                        ProgramExpressionError(error)
                                    )
                                )
                            )
        | ProgramInferenceState { environment = environment, substitution = substitution, supply = _supply, nextTypeSymbolId = _nextTypeSymbolId, error = Some(error) } ->
            ProgramInferenceResult(
                semanticType = SemNever,
                substitution = substitution,
                environment = environment,
                externalAbi = None,
                nodeTypes = nodeTypes,
                errorLocation = None,
                error = Some(error)
            )

let inferProgramProgressBody body entryPackageId (progress: ProgramInferenceProgress) =
    progress.state
    |> inferProgramUnitBody(body)(entryPackageId)(progress.nodeTypes)
    |> withResolvedSubstitution(progress.resolved)

// The supply starts above every id a statically embedded scheme already quantifies over — the
// builtin signatures and the standard traits' parameters — because reissuing one makes `instantiate`
// mint a self-mapping substitution that `applySubstitution` recurses on forever. See
// `reservedInferenceTypeVariableCount`.
let initialProgramInferenceState error environment =
    ProgramInferenceState(environment = withProgramInferenceRules(environment), substitution = [], supply = typeVariableSupplyFrom(
        reservedInferenceTypeVariableCount
    ), nextTypeSymbolId = environment
    |> inferenceTypeResolutionContext
    |> nextTypeDefinitionSymbolId, error = error)

let withExternalAbi metadata (result: ProgramInferenceResult) = result with externalAbi = Some(metadata)

let withExternalAbiError error (result: ProgramInferenceResult) =
    result with error = Some(
        ProgramExternalAbiError(error)
    )

let attachExternalAbi declarations (result: ProgramInferenceResult) =
    match result with
        | ProgramInferenceResult { error = Some(_error) } -> result
        | ProgramInferenceResult { environment = environment, error = None } ->
            match validateExternalProgramAbi(
                declarations,
                inferenceTypeResolutionContext(environment)
            ) with
                | ExternalAbiValidation { metadata = Some(metadata), error = None } -> withExternalAbi(metadata)(result)
                | ExternalAbiValidation { error = Some(error) } -> withExternalAbiError(error)(result)

let inferExpandedProgramUnitsFrom baseEnvironment body entryPackageId expansion =
    match expansion with
        | ProgramUnitExpansion { units = _units, error = Some(error) } ->
            ProgramInferenceResult(
                semanticType = SemNever,
                substitution = [],
                environment = baseEnvironment,
                externalAbi = None,
                nodeTypes = emptyNodeTypeTable,
                errorLocation = None,
                error = Some(error)
            )
        | ProgramUnitExpansion { units = units, error = None } ->
            let items = expandedProgramUnitItems(units)
            in
                let declarations = collectTraitDeclarations(items)([])
                in
                    let externalDeclarations = collectExternalDeclarations(items)([])
                    in
                        baseEnvironment
                        |> initialProgramInferenceState(
                            validateTraitDeclarations(declarations)(declarations)([])
                        )
                        |> registerExternalOpaqueTypes(externalDeclarations)
                        |> startProgramInferenceProgress
                        |> inferExpandedProgramUnits(units)(externalDeclarations)
                        |> advanceProgramInference(validateTraitDefaults(declarations))
                        |> inferProgramProgressBody(body)(entryPackageId)
                        |> attachExternalAbi(externalDeclarations)
                        |> withProgramErrorLocation

let recursive identifyExpandedUnits (next: Int) (units: List(ProgramInferenceUnit)) (reversed: List(ProgramInferenceUnit)) =
    match units with
        | [] -> (reverse(reversed), next)
        | ProgramInferenceUnit { packageId = packageId, program = program } :: tail ->
            match identifyProgram(next)(program) with
                | (identified, after) -> identifyExpandedUnits(after)(tail)(ProgramInferenceUnit(packageId = packageId, program = identified) :: reversed)

// Expands `deriving` in `units` and gives every expression node of the result, and of `body`, its
// identity. Both steps depend only on the units, so the same units are numbered the same way.
let identifyProgramUnits units (body: Maybe(Expr)) =
    match expandProgramInferenceUnits(units) with
        | ProgramUnitExpansion { units = _units, error = Some(error) } -> IdentifiedProgramUnits(units = [], body = body, nodeCount = 0, error = Some(error))
        | ProgramUnitExpansion { units = expanded, error = None } ->
            match identifyExpandedUnits(0)(expanded)([]) with
                | (identified, afterUnits) ->
                    match body with
                        | None -> IdentifiedProgramUnits(units = identified, body = None, nodeCount = afterUnits, error = None)
                        | Some(expression) ->
                            match identifyExpression(afterUnits)(expression) with
                                | (identifiedBody, after) -> IdentifiedProgramUnits(units = identified, body = Some(identifiedBody), nodeCount = after, error = None)

let inferIdentifiedProgramUnitsFrom baseEnvironment entryPackageId (identified: IdentifiedProgramUnits) = inferExpandedProgramUnitsFrom(baseEnvironment)(identified.body)(entryPackageId)(ProgramUnitExpansion(units = identified.units, error = identified.error))

let inferProgramUnitsFrom baseEnvironment units body entryPackageId =
    body
    |> identifyProgramUnits(units)
    |> inferIdentifiedProgramUnitsFrom(baseEnvironment)(entryPackageId)

let inferExpandedProgramFromPackage packageId baseEnvironment program =
    match program with
        | ProgramSyntax { items = items, body = body } ->
            let initialEnvironment = withInferencePackage(packageId)(baseEnvironment)
            in
                let declarations = collectTraitDeclarations(items)([])
                in
                    let externalDeclarations = collectExternalDeclarations(items)([])
                    in
                        initialEnvironment
                        |> initialProgramInferenceState(
                            validateTraitDeclarations(declarations)(declarations)([])
                        )
                        |> registerExternalOpaqueTypes(externalDeclarations)
                        |> startProgramInferenceProgress
                        |> inferTopLevelItems(items)(externalDeclarations)
                        |> advanceProgramInference(validateTraitDefaults(declarations))
                        |> inferProgramProgressBody(body)(packageId)
                        |> attachExternalAbi(externalDeclarations)
                        |> withProgramErrorLocation

let inferProgramFromPackage packageId baseEnvironment program =
    match expandDerivedImplementations(program) with
        | Error(error) ->
            ProgramInferenceResult(
                semanticType = SemNever,
                substitution = [],
                environment = withInferencePackage(
                    packageId,
                    baseEnvironment
                ),
                externalAbi = None,
                nodeTypes = emptyNodeTypeTable,
                errorLocation = None,
                error = Some(ProgramDerivingExpansionError(error))
            )
        | Ok(expanded) ->
            match identifyProgram(0)(expanded) with
                | (identified, _nodeCount) -> inferExpandedProgramFromPackage(packageId)(baseEnvironment)(identified)

let inferProgramInPackage packageId program =
    inferProgramFromPackage(packageId)(emptyTypeEnvironmentForPackage(packageId))(program)

let inferProgram program = inferProgramInPackage("standalone")(program)

// The numbered units as the one program lowering walks: their items in unit order, then the
// numbered trailing expression.
let identifiedProgramSyntax (identified: IdentifiedProgramUnits) = ProgramSyntax(items = expandedProgramUnitItems(identified.units), body = identified.body)
