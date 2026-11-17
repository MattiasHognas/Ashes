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
import AshesCompiler.Frontend.Token.TextSpan
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.Unification
import AshesCompiler.Semantics.TypeSchemes
import AshesCompiler.Semantics.TypeResolution
import Ashes.Collection.List.append
import Ashes.Collection.List.reverse
import Ashes.Collection.Map.MapTree
export (
    type TypeEnvironment(..),
    type ExternalFunctionInferenceDefinition(..),
    type ConstructorInferenceDefinition(..),
    type ConstructorFieldGroup(..),
    type HeapConstructorLookupIndex(..),
    type CapabilityInferenceDefinition(..),
    type CapabilityOperationInferenceDefinition(..),
    type CapabilityProviderInferenceDefinition(..),
    type CapabilityProviderOperationInferenceDefinition(..),
    type TraitInferenceDefinition(..),
    type TraitMethodInferenceDefinition(..),
    type TraitImplementationInferenceDefinition(..),
    type TraitImplementationSite(..),
    type TraitImplementationMethodInferenceDefinition(..),
    type TypeInferenceError(..),
    type TypeInferenceResult(..),
    type TopLevelBindingInferenceResult(..),
    value emptyTypeEnvironment,
    value emptyTypeEnvironmentForPackage,
    value withInferencePackage,
    value inferencePackageId,
    value withInferenceUnit,
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
    value canonicalTraitName,
    value withProgramInferenceRules,
    value withoutProgramInferenceRules,
    value withClosedGeneralizationScope,
    value addTraitBinding,
    value resolveTraitBinding,
    value resolveTraitMethod,
    value addTraitImplementation,
    value addTraitImplementationAt,
    value resolveTraitImplementations,
    value inferenceTypeResolutionContext,
    value inferenceEnvironmentSchemes,
    value applyInferenceConstraints,
    value simplifyTraitConstraints,
    value firstAmbiguousTraitRequirementUnder,
    value resolveWrittenTypeBinding,
    value requirementsLeftToWrite,
    value defaultBuiltinArguments,
    value checkInferenceBindingSignature,
    value checkInferenceAnnotation,
    value seedRecursiveSignature,
    value inferExpressionFrom,
    value inferExpressionFromLocated,
    value inferTopLevelExpressionLocated,
    value checkTrailingRequirements,
    value isBuiltinRuntimeCapability,
    value inferTopLevelBindingLocated,
    value inferenceErrorSpan,
    value unlocatedInferenceError,
    value inferTopLevelBinding,
    value inferExpression,
    value matchCoverageError,
)

type ConstructorInferenceDefinition =
    | name: Str
    | scheme: TypeScheme
    | fieldNames: List(Str)

// One named type's constructors with their field lists already peeled off their curried schemes,
// in declaration order: what a layout query needs, so an environment that indexes its constructors
// this way answers one without scanning every constructor it knows. `groupFields` is the answer
// itself for a type with no parameters; `groupGenericFields` also keeps each constructor's result
// arguments, the variables a use site's concrete arguments are substituted for.
type ConstructorFieldGroup =
    | groupSymbolId: Int
    | groupTypeName: Str
    | groupIsGeneric: Bool
    | groupFields: List((Str, List(SemanticType)))
    | groupGenericFields: List((Str, List(SemanticType), List(SemanticType)))

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

// Where an implementation was declared: the inferred unit it belongs to, the offset of its declaration
// in that unit's text, and the unit's file once the caller maps units to files.
type TraitImplementationSite =
    | unitIndex: Int
    | offset: Int
    | sourcePath: Maybe(Str)
    deriving {Eq, Show}

type TraitImplementationInferenceDefinition =
    | traitName: Str
    | typeArguments: List(SemanticType)
    | requirements: List(TraitConstraint)
    | methods: List(TraitImplementationMethodInferenceDefinition)
    | site: Maybe(TraitImplementationSite)

type ExternalFunctionInferenceDefinition =
    | name: Str
    | directScheme: TypeScheme
    | firstClassScheme: Maybe(TypeScheme)

// Name-keyed views of an environment's value bindings, constructors and record constructors.
// Each answers what a scan of the list it mirrors answers: the youngest entry for a name.
type BindingLookupIndex =
    | valuesByName: MapTree(Str, TypeScheme)
    | constructorsByName: MapTree(Str, ConstructorInferenceDefinition)
    | recordsByTypeName: MapTree(Str, ConstructorInferenceDefinition)

type HeapConstructorLookupIndex =
    | groups: MapTree(Int, MapTree(Str, ConstructorFieldGroup))
    | records: MapTree(Int, MapTree(Str, Bool))

type TypeEnvironment =
    | packageId: Str
    // The index of the unit a program's inference is in, among the units it infers.
    | currentUnit: Int
    | bindings: List((Str, TypeScheme))
    | constructors: List(ConstructorInferenceDefinition)
    | constructorFieldGroups: List(ConstructorFieldGroup)
    | heapConstructorIndex: Maybe(HeapConstructorLookupIndex)
    | capabilities: List(CapabilityInferenceDefinition)
    | traits: List(TraitInferenceDefinition)
    | traitImplementations: List(TraitImplementationInferenceDefinition)
    | providers: List(CapabilityProviderInferenceDefinition)
    | handledCapabilities: List(Str)
    | externalFunctions: List(ExternalFunctionInferenceDefinition)
    | typeResolutionContext: TypeResolutionContext
    // When present, the only schemes of `bindings` that can hold a free type variable; every
    // other binding is closed. Generalization then scans these instead of the whole environment.
    | generalizationSchemes: Maybe(List(TypeScheme))
    // Bindings a lookup by name reaches only after `bindings` has no entry for the name.
    | fallbackBindings: List((Str, TypeScheme))
    // Whether the rules only whole-program inference applies are on: a stitched standard trait's
    // compiler name (`Ashes_Trait_Eq`) is stored and looked up under its bare name, and `0 - x` is
    // the negation of `x`. Program inference turns it on; an environment built for lowering
    // leaves it off, so its speculative inference is unchanged.
    | programInferenceRules: Bool
    // When present, mirrors `bindings` and `constructors` by name. Only an environment that
    // extends them through `addTypeBinding` and `addConstructorBinding` alone may carry it;
    // program inference does, an environment built for lowering does not.
    | lookupIndex: Maybe(BindingLookupIndex)

type TypeInferenceError =
    | UnknownValue(Str)
    | InferenceUnificationError(UnificationError)
    | InferenceTypeResolutionError(TypeResolutionError)
    | DuplicatePatternBinding(Str)
    | UnknownPatternConstructor(Str)
    | ConstructorPatternArityMismatch(Str, Int, Int)
    // A match arm's tuple pattern against a scrutinee tuple of another length: (expected, actual).
    | TuplePatternArityMismatch(Int, Int)
    // An argument of another type than `Int` passed to a builtin that converts from `Int`, named by
    // its qualified name, with the argument's type.
    | BuiltinIntArgumentMismatch(Str, SemanticType)
    | UnknownRecordPatternField(Str, Str)
    | DuplicateRecordPatternField(Str)
    | UnknownRecordType(Str)
    | RecordUpdateRequiresRecord(SemanticType)
    | MissingRecordField(Str, Str)
    | UnknownRecordField(Str, Str)
    | DuplicateRecordField(Str)
    // The left operand of a Result-aware pipe is not a Result.
    | ExpectedResultType(SemanticType)
    // The value of a `let?` is not a Result.
    | LetResultRequiresResult(SemanticType)
    // The body of a `let?` is not a Result.
    | LetResultBodyRequiresResult(SemanticType)
    | ExpectedTaskType(SemanticType)
    | PerformRequiresCapabilityOperation
    | UnknownCapabilityOperation(Str, Str)
    | UnsignedCapabilityOperationRequiresSignature(Str, Str)
    | InvalidHandler(Str)
    | AmbiguousCapabilitySatisfaction(Str)
    | UnsatisfiedCapability(Str)
    | UnknownCapabilityInRow(Str)
    | InconsistentOrPatternBindings
    | UnknownWrittenTraitRequirement(Str)
    | WrittenTraitRequirementArityMismatch(Str, Int, Int)
    | MissingWrittenTraitRequirement(Str, List(SemanticType))
    | UnjustifiedWrittenTraitRequirement(Str, List(SemanticType))
    | AmbiguousTraitRequirement(Str, List(SemanticType))
    | NoTraitImplementation(Str, List(SemanticType))
    | ExternalFunctionRequiresDirectCall(Str)
    | NonExhaustiveMatch(Str)
    | UnreachableMatchArm(Str)
    | ConstructorPatternsFromDifferentAdts(List(Str))
    | UnsupportedInferencePattern(Str)
    | UnsupportedInferenceExpression(Str)
    // An error with the span of the innermost located expression it was raised in. Only the
    // `Located` entry points return it; every other entry point reports the bare error.
    | InferenceErrorAt(TextSpan, TypeInferenceError)
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
    TypeEnvironment(packageId = packageId, currentUnit = 0, bindings = [], constructors = [], constructorFieldGroups = [], heapConstructorIndex = None, capabilities = [], traits = [], traitImplementations = [], providers = [], handledCapabilities = [], externalFunctions = [], generalizationSchemes = None, fallbackBindings = [], programInferenceRules = false, lookupIndex = None, typeResolutionContext = emptyTypeResolutionContext(
        Unit
    ))

let emptyTypeEnvironment unit = emptyTypeEnvironmentForPackage("standalone")

let withInferencePackage packageId (environment: TypeEnvironment) = environment with packageId = packageId

let withInferenceUnit unitIndex (environment: TypeEnvironment) = environment with currentUnit = unitIndex

let inferencePackageId environment =
    match environment with
        | TypeEnvironment { packageId = packageId } -> packageId

let recursive constructorResultTypeName semanticType =
    match semanticType with
        | SemFunction(_argument, result, None) -> constructorResultTypeName(result)
        | SemNamed(_symbolId, name, _arguments) -> Some(name)
        | _ -> None

let indexValueBinding name scheme (index: Maybe(BindingLookupIndex)) =
    match index with
        | None -> None
        | Some(lookup) -> Some((lookup with valuesByName = Ashes.Collection.Map.setStr(name)(scheme)(lookup.valuesByName)))

let indexRecordConstructor (definition: ConstructorInferenceDefinition) (lookup: BindingLookupIndex) =
    match (definition.fieldNames, definition.scheme) with
        | (_field :: _rest, TypeScheme { body = body }) ->
            match constructorResultTypeName(body) with
                | Some(typeName) -> lookup with recordsByTypeName = Ashes.Collection.Map.setStr(typeName)(definition)(lookup.recordsByTypeName)
                | None -> lookup
        | _ -> lookup

let indexConstructorBinding (definition: ConstructorInferenceDefinition) (index: Maybe(BindingLookupIndex)) =
    match index with
        | None -> None
        | Some(lookup) ->
            Some(
                indexRecordConstructor(
                    definition,
                    lookup with constructorsByName = Ashes.Collection.Map.setStr(definition.name)(definition)(lookup.constructorsByName)
                )
            )

let addTypeBinding name scheme (environment: TypeEnvironment) =
    match environment with
        | TypeEnvironment { bindings = bindings, generalizationSchemes = schemes, lookupIndex = index } ->
            let extended =
                match schemes with
                    | Some(open) -> Some(scheme :: open)
                    | None -> None
            in environment with bindings = (name, scheme) :: bindings, generalizationSchemes = extended, lookupIndex = indexValueBinding(name)(scheme)(index)

let addConstructorBinding name scheme fieldNames (environment: TypeEnvironment) =
    match addTypeBinding(name)(scheme)(environment) with
        | TypeEnvironment { constructors = constructors, lookupIndex = index } as withBinding ->
            let definition = ConstructorInferenceDefinition(name = name, scheme = scheme, fieldNames = fieldNames)
            in withBinding with constructors = definition :: constructors, heapConstructorIndex = None, lookupIndex = indexConstructorBinding(definition)(index)

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

let resolveWrittenTypeBinding name environment =
    match environment with
        | TypeEnvironment { lookupIndex = Some(BindingLookupIndex { valuesByName = values }), fallbackBindings = fallback } ->
            match Ashes.Collection.Map.getStr(name)(values) with
                | Some(scheme) -> Some(scheme)
                | None -> findTypeBinding(name)(fallback)
        | TypeEnvironment { bindings = bindings, fallbackBindings = fallback } ->
            match findTypeBinding(name)(bindings) with
                | Some(scheme) -> Some(scheme)
                | None -> findTypeBinding(name)(fallback)

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

let resolveWrittenConstructorBinding name environment =
    match environment with
        | TypeEnvironment { lookupIndex = Some(BindingLookupIndex { constructorsByName = indexed }) } -> Ashes.Collection.Map.getStr(name)(indexed)
        | TypeEnvironment { bindings = _bindings, constructors = constructors, capabilities = _capabilities, traits = _traits, providers = _providers, handledCapabilities = _handledCapabilities, typeResolutionContext = _typeResolutionContext } ->
            findConstructorBinding(
                name,
                constructors
            )

// `Ordering` and its constructors are declared by the stitched `Ashes.Trait` module under
// module-qualified compiler names, and are in scope everywhere under their bare names. Under the
// program rules a constructor name nothing else declares is looked up under that module's spelling.
let resolveConstructorBinding (name: Str) (environment: TypeEnvironment) =
    match resolveWrittenConstructorBinding(name)(environment) with
        | Some(definition) -> Some(definition)
        | None ->
            if environment.programInferenceRules
            then resolveWrittenConstructorBinding("Ashes_Trait_" + name)(environment)
            else None

let resolveTypeBinding (name: Str) (environment: TypeEnvironment) =
    match resolveWrittenTypeBinding(name)(environment) with
        | Some(scheme) -> Some(scheme)
        | None ->
            if environment.programInferenceRules
            then
                match resolveWrittenConstructorBinding("Ashes_Trait_" + name)(environment) with
                    | Some(ConstructorInferenceDefinition { scheme = scheme }) -> Some(scheme)
                    | None -> None
            else None

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

// A trait declared by the stitched `Ashes.Trait` module keeps its module-qualified compiler name
// (`Ashes_Trait_Eq`), while the operators, derived implementations and the seeded standard
// environment name the same trait bare (`Eq`). The environment stores and looks traits up under
// the bare name, so every spelling reaches one trait.
let canonicalTraitName (name: Str) =
    if Ashes.Text.startsWith(name)("Ashes_Trait_")
    then Ashes.Text.substring(name)(12)(Ashes.Text.length(name) - 12)
    else name

let traitNameIn (environment: TypeEnvironment) (name: Str) =
    if environment.programInferenceRules
    then canonicalTraitName(name)
    else name

let withIndexedValue name scheme (lookup: BindingLookupIndex) = lookup with valuesByName = Ashes.Collection.Map.setStr(name)(scheme)(lookup.valuesByName)

let withIndexedConstructor (definition: ConstructorInferenceDefinition) (lookup: BindingLookupIndex) =
    indexRecordConstructor(
        definition,
        lookup with constructorsByName = Ashes.Collection.Map.setStr(definition.name)(definition)(lookup.constructorsByName)
    )

let recursive indexValueBindings bindings (lookup: BindingLookupIndex) =
    match bindings with
        | [] -> lookup
        | (name, scheme) :: older ->
            lookup
            |> indexValueBindings(older)
            |> withIndexedValue(name)(scheme)

let recursive indexConstructorBindings (definitions: List(ConstructorInferenceDefinition)) (lookup: BindingLookupIndex) =
    match definitions with
        | [] -> lookup
        | definition :: older ->
            lookup
            |> indexConstructorBindings(older)
            |> withIndexedConstructor(definition)

// Program inference's own view of an environment: the program-only rules on and the bindings
// indexed by name, oldest first so the youngest entry for a name is the one kept.
let withProgramInferenceRules (environment: TypeEnvironment) =
    environment with programInferenceRules = true, lookupIndex = Some(
        BindingLookupIndex(valuesByName = Ashes.Collection.Map.empty, constructorsByName = Ashes.Collection.Map.empty, recordsByTypeName = Ashes.Collection.Map.empty)
        |> indexValueBindings(environment.bindings)
        |> indexConstructorBindings(environment.constructors)
    )

let withoutProgramInferenceRules (environment: TypeEnvironment) = environment with programInferenceRules = false, lookupIndex = None, generalizationSchemes = None

// Between two top-level declarations every binding in scope is generalized, so none holds a free
// type variable: a declaration's generalization only has to look at what it adds itself.
let withClosedGeneralizationScope (environment: TypeEnvironment) = environment with generalizationSchemes = Some([])

let appliesProgramInferenceRules (environment: TypeEnvironment) = environment.programInferenceRules

let addTraitBinding name parameterCount parameters methods supertraits (environment: TypeEnvironment) =
    match environment with
        | TypeEnvironment { packageId = packageId, traits = traits } ->
            environment with traits = TraitInferenceDefinition(name = traitNameIn(environment)(name), parameterCount = parameterCount, parameters = parameters, methods = methods, supertraits = canonicalizeTraitConstraints(
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
                traitNameIn(environment)(name),
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

let addTraitImplementationAt (site: Maybe(TraitImplementationSite)) traitName typeArguments requirements methods (environment: TypeEnvironment) =
    match environment with
        | TypeEnvironment { traitImplementations = traitImplementations } ->
            let definition = TraitImplementationInferenceDefinition(traitName = traitNameIn(environment)(traitName), typeArguments = typeArguments, requirements = requirements, methods = methods, site = site)
            in environment with traitImplementations = definition :: traitImplementations

let addTraitImplementation traitName typeArguments requirements methods (environment: TypeEnvironment) = addTraitImplementationAt(None)(traitName)(typeArguments)(requirements)(methods)(environment)

let recursive filterTraitImplementations traitName implementations =
    match implementations with
        | [] -> []
        | TraitImplementationInferenceDefinition { traitName = candidateName, typeArguments = typeArguments, requirements = requirements, methods = methods, site = site } :: tail ->
            let rest = filterTraitImplementations(traitName)(tail)
            in
                if traitName == candidateName
                then TraitImplementationInferenceDefinition(traitName = candidateName, typeArguments = typeArguments, requirements = requirements, methods = methods, site = site) :: rest
                else rest

let resolveTraitImplementations traitName environment =
    match environment with
        | TypeEnvironment { traitImplementations = implementations } ->
            filterTraitImplementations(
                traitNameIn(environment)(traitName),
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
                                        then
                                            arguments
                                            |> SemCapability(name)
                                            |> Some
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
                                    | [] ->
                                        None
                                        |> SemRow([capabilityType])
                                        |> Some
                                        |> SemFunction(argument)(body)
                                    | _ ->
                                        SemFunction(argument)(wrap(tail)(body))(None)
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

// The identity directly around `expression`, looking through its location, or -1.
let recursive identifiedNode expression =
    match expression with
        | ExprAt(_span, inner) -> identifiedNode(inner)
        | ExprNode(node, _inner) -> node
        | _ -> -1

// Recording against identified nodes happens only under the program inference rules and only
// where a node is known, so an inference run for lowering writes nothing.
let recordsAt (environment: TypeEnvironment) (node: Int) = environment.programInferenceRules && node >= 0

let recordNodeType (node: Int) (result: TypeInferenceResult) =
    match result with
        | TypeInferenceResult { semanticType = semanticType, supply = supply, error = None } ->
            result with supply = withNodeNote(NodeTypeNote(node)(semanticType))(supply)
        | _ -> result

let noteOperator (environment: TypeEnvironment) (node: Int) (traitName: Str) operandType (result: TypeInferenceResult) =
    if recordsAt(environment)(node)
    then
        match result with
            | TypeInferenceResult { supply = supply, error = None } ->
                result with supply = withNodeNote(NodeOperatorNote(node)(traitName)(operandType))(supply)
            | _ -> result
    else result

// The literal zero of `0 - x` is not inferred as a value; it stands at the type its operand has.
let noteNegatedZero (environment: TypeEnvironment) (node: Int) (result: TypeInferenceResult) =
    if recordsAt(environment)(node)
    then recordNodeType(node)(result)
    else result

let noteBinding (environment: TypeEnvironment) (node: Int) (name: Str) (scheme: TypeScheme) supply =
    if recordsAt(environment)(node)
    then
        withNodeNote(NodeBindingNote(node)(name)(scheme))(supply)
    else supply

let recursive noteBindingsIn (environment: TypeEnvironment) (node: Int) (names: List(Str)) (scope: TypeEnvironment) supply =
    if recordsAt(environment)(node)
    then
        match names with
            | [] -> supply
            | name :: rest ->
                match resolveWrittenTypeBinding(name)(scope) with
                    | Some(scheme) ->
                        supply
                        |> withNodeNote(NodeBindingNote(node)(name)(scheme))
                        |> noteBindingsIn(environment)(node)(rest)(scope)
                    | None -> noteBindingsIn(environment)(node)(rest)(scope)(supply)
    else supply

// `instantiate` mints one variable per quantified variable, in order, from `firstId`.
let recursive instantiationArguments (quantified: List((Int, Str))) (firstId: Int) =
    match quantified with
        | [] -> []
        | (variable, _name) :: rest -> (variable, SemVariable(firstId)) :: instantiationArguments(rest)(firstId + 1)

let recursive mentionsQuantified (quantified: List((Int, Str))) (variable: Int) =
    match quantified with
        | [] -> false
        | (candidate, _name) :: rest -> candidate == variable || mentionsQuantified(rest)(variable)

let recursive anyMentionsQuantified (quantified: List((Int, Str))) (variables: List(Int)) =
    match variables with
        | [] -> false
        | head :: rest -> mentionsQuantified(quantified)(head) || anyMentionsQuantified(quantified)(rest)

let recursive typesMentionQuantified (quantified: List((Int, Str))) (semanticTypes: List(SemanticType)) =
    match semanticTypes with
        | [] -> false
        | head :: rest ->
            anyMentionsQuantified(quantified)(freeTypeVariables(head)) || typesMentionQuantified(quantified)(rest)

let recursive instantiatedTypes (arguments: List((Int, SemanticType))) (semanticTypes: List(SemanticType)) =
    match semanticTypes with
        | [] -> []
        | head :: rest -> applySubstitution(arguments)(head) :: instantiatedTypes(arguments)(rest)

// The requirements of a scheme that depend on what it quantifies, at one instantiation and in the
// scheme's own order.
let recursive dictionaryRequirements (quantified: List((Int, Str))) (arguments: List((Int, SemanticType))) (constraints: List(TraitConstraint)) =
    match constraints with
        | [] -> []
        | TraitConstraint { traitName = traitName, typeArguments = typeArguments } :: rest ->
            if typesMentionQuantified(quantified)(typeArguments)
            then TraitConstraint(traitName = traitName, typeArguments = instantiatedTypes(arguments)(typeArguments)) :: dictionaryRequirements(quantified)(arguments)(rest)
            else dictionaryRequirements(quantified)(arguments)(rest)

// Notes at `node` the instantiation `instantiate(scheme)(before)` made.
let noteInstantiationAt (environment: TypeEnvironment) (node: Int) (scheme: TypeScheme) (before: TypeVariableSupply) (requirements: List(TraitConstraint)) (after: TypeVariableSupply) =
    if recordsAt(environment)(node)
    then
        match scheme.quantified with
            | [] -> after
            | quantified ->
                let arguments = instantiationArguments(quantified)(before.nextId)
                in
                    withNodeNote(scheme.constraints
                    |> dictionaryRequirements(quantified)(arguments)
                    |> NodeInstantiationNote(node)(arguments)(requirements))(after)
    else after

// The names a pattern binds, read off its shape. An or-pattern's alternatives bind the same
// names, so the first one speaks for all.
let recursive patternVariableNames pattern (names: List(Str)) =
    match pattern with
        | PatternAt(_span, inner) -> patternVariableNames(inner)(names)
        | PatternVar(name) -> name :: names
        | PatternCons(head, tail) ->
            names
            |> patternVariableNames(head)
            |> patternVariableNames(tail)
        | PatternTuple(elements) -> patternVariableNamesIn(elements)(names)
        | PatternConstructor(_name, arguments) -> patternVariableNamesIn(arguments)(names)
        | PatternRecord(_name, fields) -> patternFieldVariableNames(fields)(names)
        | PatternAs(inner, name) -> name :: patternVariableNames(inner)(names)
        | PatternOr(first :: _rest) -> patternVariableNames(first)(names)
        | _ -> names
and patternVariableNamesIn patterns (names: List(Str)) =
    match patterns with
        | [] -> names
        | head :: tail ->
            names
            |> patternVariableNames(head)
            |> patternVariableNamesIn(tail)
and patternFieldVariableNames (fields: List((Str, Pattern))) (names: List(Str)) =
    match fields with
        | [] -> names
        | (_field, pattern) :: tail ->
            names
            |> patternVariableNames(pattern)
            |> patternFieldVariableNames(tail)

// Notes, at the node `before` was inferring, the instantiation `instantiate(scheme)(before)` made.
let noteInstantiation (environment: TypeEnvironment) (scheme: TypeScheme) (before: TypeVariableSupply) (requirements: List(TraitConstraint)) (after: TypeVariableSupply) =
    if recordsAt(environment)(before.currentNode)
    then
        match scheme.quantified with
            | [] -> after
            | quantified ->
                let arguments = instantiationArguments(quantified)(before.nextId)
                in
                    withNodeNote(scheme.constraints
                    |> dictionaryRequirements(quantified)(arguments)
                    |> NodeInstantiationNote(before.currentNode)(arguments)(requirements))(after)
    else after

// Unary `-x` reaches inference as `0 - x`: the parser has no negation node. A subtraction whose
// left operand is the literal zero is therefore the operand's own negation, at the operand's type.
let recursive isZeroLiteralExpression expression =
    match expression with
        | ExprAt(_span, inner) -> isZeroLiteralExpression(inner)
        | ExprNode(_id, inner) -> isZeroLiteralExpression(inner)
        | ExprInt(0) -> true
        | _ -> false

// Attaches `span` to a failure that has no location yet, so the innermost located expression
// around the failing node is the one reported.
let locateInferenceError span (result: TypeInferenceResult) =
    match result with
        | TypeInferenceResult { error = None } -> result
        | TypeInferenceResult { error = Some(InferenceErrorAt(_, _)) } -> result
        | TypeInferenceResult { error = Some(error) } ->
            result with error = Some(InferenceErrorAt(span)(error))

let inferenceErrorSpan error =
    match error with
        | InferenceErrorAt(span, _) -> Some(span)
        | _ -> None

let unlocatedInferenceError error =
    match error with
        | InferenceErrorAt(_, inner) -> inner
        | _ -> error

let unlocatedInferenceResult (result: TypeInferenceResult) =
    match result with
        | TypeInferenceResult { error = Some(InferenceErrorAt(_, inner)) } -> result with error = Some(inner)
        | _ -> result

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
                    in
                        supertraits
                        |> applyInferenceConstraints(substitution)
                        |> canonicalizeTraitConstraints

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
                            environment
                            |> directSupertraitConstraints(head)
                            |> appendConstraints(tail),
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
    in
        environment
        |> removeImpliedTraitConstraints(canonical)(canonical)
        |> canonicalizeTraitConstraints)

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
                                        TraitConstraint(traitName = traitNameIn(environment)(traitName), typeArguments = resolvedArguments) :: reversed
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
                else Some((traitName, typeArguments))

let recursive appendSubstitution left right =
    match left with
        | [] -> right
        | head :: tail -> head :: appendSubstitution(tail)(right)

// Unifying two open rows that each hold a capability the other lacks gives both one shared tail.
// The unifier has no variable supply and numbers that tail from the size of its own local
// substitution, below every supplied id, so two unifications produce the same variable. Under the
// program rules each such tail is replaced by a variable from the supply before the local
// substitution joins the running one.
let recursive mintedRowTailsIn (variables: List(Int)) (found: List(Int)) =
    match variables with
        | [] -> found
        | head :: tail ->
            if head <= -1000000
            then
                if integerExists(head)(found)
                then mintedRowTailsIn(tail)(found)
                else mintedRowTailsIn(tail)(head :: found)
            else mintedRowTailsIn(tail)(found)

let recursive mintedRowTails (entries: List((Int, SemanticType))) (found: List(Int)) =
    match entries with
        | [] -> found
        | (variableId, semanticType) :: tail ->
            found
            |> mintedRowTailsIn(variableId :: freeTypeVariables(semanticType))
            |> mintedRowTails(tail)

let recursive supplyRowTails (minted: List(Int)) supply (renaming: List((Int, SemanticType))) =
    match minted with
        | [] -> (renaming, supply)
        | head :: tail ->
            match freshTypeVariable(supply) with
                | (suppliedTail, nextSupply) -> supplyRowTails(tail)(nextSupply)((head, suppliedTail) :: renaming)

let recursive renameRowTails (renaming: List((Int, SemanticType))) (entries: List((Int, SemanticType))) =
    match entries with
        | [] -> []
        | (variableId, semanticType) :: tail ->
            match applySubstitution(renaming)(SemVariable(variableId)) with
                | SemVariable(renamedId) -> (renamedId, applySubstitution(renaming)(semanticType)) :: renameRowTails(renaming)(tail)
                | _ -> (variableId, applySubstitution(renaming)(semanticType)) :: renameRowTails(renaming)(tail)

let settleRowTails (environment: TypeEnvironment) supply (entries: List((Int, SemanticType))) =
    if environment.programInferenceRules
    then
        match mintedRowTails(entries)([]) with
            | [] -> (entries, supply)
            | minted ->
                match supplyRowTails(minted)(supply)([]) with
                    | (renaming, nextSupply) -> (renameRowTails(renaming)(entries), nextSupply)
    else (entries, supply)

let patternSuccess semanticType environment substitution supply names = PatternInferenceResult(semanticType = semanticType, environment = environment, substitution = substitution, supply = supply, names = names, error = None)

let patternFailure semanticType environment substitution supply names error =
    PatternInferenceResult(semanticType = semanticType, environment = environment, substitution = substitution, supply = supply, names = names, error = Some(
        error
    ))

let mergePatternUnification currentSubstitution result supply fallbackType environment names =
    match result with
        | UnificationResult { substitution = unificationSubstitution, error = None } ->
            match settleRowTails(environment)(supply)(unificationSubstitution) with
                | (settled, settledSupply) ->
                    let combined = appendSubstitution(settled)(currentSubstitution)
                    in
                        patternSuccess(applySubstitution(combined)(fallbackType))(environment)(combined)(settledSupply)(names)
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

// A match arm whose whole pattern is a tuple of another length than the scrutinee's tuple type.
let tuplePatternArityError (scrutineeType: SemanticType) pattern =
    match (scrutineeType, coverageUnwrap(pattern)) with
        | (SemTuple(elements), PatternTuple(patterns)) ->
            let expected = coverageLength(elements)(0)
            in
                let actual = coverageLength(patterns)(0)
                in
                    if expected == actual
                    then None
                    else
                        actual
                        |> TuplePatternArityMismatch(expected)
                        |> Some
        | _ -> None

// The builtins that convert from `Int` and that stage 0 checks at the call, naming the builtin when
// the argument has another type.
let intArgumentBuiltins = ["Ashes.Rune.fromInt", "Ashes.Number.BigInt.fromInt", "Ashes.Number.UInt.fromInt", "Ashes.Number.UInt.fromInt64"]

let recursive calledVariableName expression =
    match expression with
        | ExprAt(_span, inner) -> calledVariableName(inner)
        | ExprNode(_id, inner) -> calledVariableName(inner)
        | ExprVar(name) -> Some(name)
        | ExprQualifiedVar(moduleName, member) -> Some(moduleName + "." + member)
        | _ -> None

let recursive textListContains (items: List(Str)) (wanted: Str) =
    match items with
        | [] -> false
        | head :: rest ->
            if head == wanted
            then true
            else textListContains(rest)(wanted)

// A failed call of an `Int`-converting builtin reads as that builtin's argument error.
let builtinArgumentFailure function (argumentType: SemanticType) (failure: TypeInferenceResult) =
    match calledVariableName(function) with
        | Some(name) ->
            if textListContains(intArgumentBuiltins)(name)
            then
                failure with error = Some(argumentType
                |> applySubstitution(failure.substitution)
                |> BuiltinIntArgumentMismatch(name))
            else failure
        | None -> failure

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
                | Some(ConstructorInferenceDefinition { name = declaredName }) -> Some(declaredName)
                | None -> None
        | PatternVar(name) ->
            match resolveConstructorBinding(name)(environment) with
                | Some(ConstructorInferenceDefinition { scheme = TypeScheme { body = SemFunction(_, _, _) } }) -> None
                | Some(ConstructorInferenceDefinition { name = declaredName }) -> Some(declaredName)
                | None -> None
        | _ -> None

// Every constructor of an ADT, in declaration order, as (name, arity). The environment holds the
// most recently added constructor first, so collecting them front to back restores the order.
let recursive coverageAdtConstructors (definitions: List(ConstructorInferenceDefinition)) (adtName: Str) acc =
    match definitions with
        | [] -> acc
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
            then
                pattern
                |> coverageFormatPattern
                |> coverageJoinPatterns(rest)
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
                | PatternTuple(elements) ->
                    0
                    |> coverageLength(elements)
                    |> Some
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
            | Some(field) ->
                []
                |> coverageReplaceAt(coverageWildcards(arity)([]))(fieldIndex)(field)
                |> PatternConstructor(constructorName)
            | None ->
                []
                |> coverageWildcards(arity)
                |> PatternConstructor(constructorName)

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
        | [] ->
            PatternWildcard
            |> PatternCons(PatternWildcard)
            |> Some
        | _ ->
            let elementType =
                match valueType with
                    | Some(SemList(element)) -> Some(element)
                    | _ -> None
            in
                match []
                |> coverageConsHeads(conses)
                |> coverageMissing(environment)(elementType) with
                    | Some(missingHead) ->
                        PatternWildcard
                        |> PatternCons(missingHead)
                        |> Some
                    | None ->
                        match []
                        |> coverageConsTails(conses)
                        |> coverageMissing(environment)(valueType) with
                            | Some(missingTail) ->
                                missingTail
                                |> PatternCons(PatternWildcard)
                                |> Some
                            | None -> None
and coverageMissingTuple environment valueType patterns =
    (let arity =
        match valueType with
            | Some(SemTuple(elements)) ->
                0
                |> coverageLength(elements)
                |> Some
            | _ -> coverageFirstTupleArity(patterns)
    in
        match arity with
            | None -> None
            | Some(count) ->
                match coverageTuplePatterns(patterns)(count)([]) with
                    | [] ->
                        []
                        |> coverageWildcards(count)
                        |> PatternTuple
                        |> Some
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
            match []
            |> coverageColumn(rows)(index)
            |> coverageMissing(environment)(elementType) with
                | Some(missing) ->
                    []
                    |> coverageReplaceAt(coverageWildcards(count)([]))(index)(missing)
                    |> PatternTuple
                    |> Some
                | None -> coverageMissingTupleColumn(environment)(valueType)(rows)(count)(index + 1)
and coverageMissingAdt environment valueType patterns =
    (let adtName =
        match valueType with
            | Some(SemNamed(_id, name, _arguments)) -> Some(name)
            | _ -> coverageSharedAdt(patterns)(environment)(None)
    in
        match adtName with
            | None -> None
            | Some(name) ->
                []
                |> coverageAdtConstructors(environmentConstructors(environment))(name)
                |> coverageMissingConstructor(environment)(patterns))
and coverageMissingConstructor environment patterns (constructors: List((Str, Int))) =
    match constructors with
        | [] -> None
        | (constructorName, arity) :: rest ->
            match coveragePatternsForConstructor(patterns)(constructorName)(environment)([]) with
                | [] ->
                    None
                    |> coverageMissingConstructorPattern(constructorName)(arity)(-1)
                    |> Some
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
        match []
        |> coverageColumn(rows)(index)
        |> coverageMissing(environment)(None) with
            | Some(missing) ->
                Some(missing)
                |> coverageMissingConstructorPattern(constructorName)(arity)(index)
                |> Some
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
                | Some(index) ->
                    []
                    |> coverageReplaceAt(positional)(index)(fieldPattern)
                    |> coveragePlaceRecordFields(rest)(fieldNames)
                | None -> coveragePlaceRecordFields(rest)(fieldNames)(positional)

let recursive coverageExpandPattern pattern environment =
    match pattern with
        | PatternAt(_span, inner) -> coverageExpandPattern(inner)(environment)
        | PatternOr(alternatives) -> coverageExpandAlternatives(alternatives)(environment)([])
        | PatternAs(inner, _name) -> coverageExpandPattern(inner)(environment)
        | PatternCons(head, tail) ->
            coverageConsCombinations(coverageCombineChildren([head, tail])(environment))([])
        | PatternTuple(elements) ->
            coverageTupleCombinations(coverageCombineChildren(elements)(environment))([])
        | PatternConstructor(name, patterns) ->
            coverageConstructorCombinations(name)(coverageCombineChildren(patterns)(environment))([])
        | PatternRecord(name, fields) ->
            match resolveConstructorBinding(name)(environment) with
                | Some(ConstructorInferenceDefinition { fieldNames = fieldNames }) ->
                    match fieldNames with
                        | [] -> [pattern]
                        | _ ->
                            let positional =
                                []
                                |> coverageWildcards(coverageLength(fieldNames)(0))
                                |> coveragePlaceRecordFields(fields)(fieldNames)
                            in
                                coverageConstructorCombinations(name)(coverageCombineChildren(positional)(environment))([])
                | None -> [pattern]
        | other -> [other]
and coverageExpandAlternatives alternatives environment acc =
    match alternatives with
        | [] -> acc
        | alternative :: rest ->
            environment
            |> coverageExpandPattern(alternative)
            |> append(acc)
            |> coverageExpandAlternatives(rest)(environment)
and coverageCombineChildren children environment =
    match children with
        | [] -> [[]]
        | child :: rest ->
            coverageProduct(coverageExpandPattern(child)(environment))(coverageCombineChildren(rest)(environment))([])
and coverageProduct heads (tails: List(List(Pattern))) acc =
    match heads with
        | [] -> acc
        | head :: rest ->
            []
            |> coveragePrefixAll(head)(tails)
            |> append(acc)
            |> coverageProduct(rest)(tails)
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
        | (pattern, body, guard) :: rest ->
            acc
            |> coverageExpandedArms(coverageExpandPattern(pattern)(environment))(body)(guard)
            |> coverageExpandCases(rest)(environment)
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
                                    else coverageUnreachableConstructor(pattern)(rest)(environment)(Ashes.Internal.deepCopy(key) :: composites)(ints)(texts)(seenTrue)(seenFalse)(constructors)
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

let coverageAdtNameExhaustiveness environment (valueType: SemanticType) (adtName: Str) unguarded =
    match coverageAdtConstructors(environmentConstructors(environment))(adtName)([]) with
        | [] -> coverageMissingCaseError(environment)(valueType)(unguarded)
        | constructors ->
            match coverageMissingNames(constructors)(coverageSeenConstructors(unguarded)(environment)([]))([]) with
                | [] -> coverageMissingCaseError(environment)(valueType)(unguarded)
                | missing ->
                    missing
                    |> coverageMissingConstructorsMessage(adtName)
                    |> NonExhaustiveMatch
                    |> Some

// A scrutinee whose type is still an unresolved inference variable (a lambda parameter matched
// before any call pins it) can still name its ADT through the arms: the first pattern that
// resolves to a known constructor gives the declaring type, and the constructor-set check runs
// exactly as it would with the resolved scrutinee type.
let recursive coverageAdtNameFromArms environment patterns =
    match patterns with
        | [] -> None
        | pattern :: rest ->
            match coverageConstructorName(pattern)(environment) with
                | Some(name) ->
                    match coverageConstructorShape(name)(environment) with
                        | Some((adtName, _arity)) -> Some(adtName)
                        | None -> coverageAdtNameFromArms(environment)(rest)
                | None -> coverageAdtNameFromArms(environment)(rest)

let coverageExhaustiveness environment (valueType: SemanticType) cases =
    (let unguarded = coverageUnguardedPatterns(cases)([])
    in
        if coverageAnyCatchAll(environment)(unguarded)
        then None
        else
            match valueType with
                | SemNamed(_id, adtName, _arguments) -> coverageAdtNameExhaustiveness(environment)(valueType)(adtName)(unguarded)
                | SemVariable(_variableId) ->
                    match coverageAdtNameFromArms(environment)(unguarded) with
                        | Some(adtName) -> coverageAdtNameExhaustiveness(environment)(valueType)(adtName)(unguarded)
                        | None -> None
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

// Arms whose constructor patterns belong to different ADTs fail to unify with each other; that
// failure is reported as the arms' own error, as stage 0 reports it.
let checkMatchCoverage cases scrutineeType environment (result: TypeInferenceResult) =
    match result with
        | TypeInferenceResult { error = Some(_error) } ->
            match coverageDistinctAdts(coverageExpandCases(cases)(environment)([]))(environment)([]) with
                | first :: second :: more -> result with error = Some(ConstructorPatternsFromDifferentAdts(first :: second :: more))
                | _ -> result
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

// Field-name inference is allowed only for one visible record definition. Keep
// first-match constructor shadowing, as ordinary constructor lookup does.
let recursive recordFieldCandidates fieldName definitions seen found =
    match definitions with
        | [] -> found
        | (ConstructorInferenceDefinition { name = name, fieldNames = fields } as definition) :: rest ->
            match Ashes.Collection.Map.getStr(name)(seen) with
                | Some(_present) -> recordFieldCandidates(fieldName)(rest)(seen)(found)
                | None ->
                    let nextSeen = Ashes.Collection.Map.setStr(name)(true)(seen)
                    in
                        if patternNameExists(fieldName)(fields)
                        then
                            match found with
                                | [] -> recordFieldCandidates(fieldName)(rest)(nextSeen)([definition])
                                | _ -> definition :: found
                        else recordFieldCandidates(fieldName)(rest)(nextSeen)(found)

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
            in
                inferenceSuccess(applySubstitution(combined)(fallbackType))(combined)(supply)
        | UnificationResult { substitution = _unificationSubstitution, error = Some(error) } ->
            inferenceFailure(
                fallbackType,
                currentSubstitution,
                supply,
                InferenceUnificationError(error)
            )

let unifyIn (environment: TypeEnvironment) left right =
    if environment.programInferenceRules
    then unifyFlatRows(left)(right)
    else unify(left)(right)

let mergeUnificationIn (environment: TypeEnvironment) currentSubstitution result supply fallbackType =
    match result with
        | UnificationResult { substitution = unificationSubstitution, error = None } ->
            match settleRowTails(environment)(supply)(unificationSubstitution) with
                | (settled, settledSupply) ->
                    let combined = appendSubstitution(settled)(currentSubstitution)
                    in
                        inferenceSuccess(applySubstitution(combined)(fallbackType))(combined)(settledSupply)
        | UnificationResult { substitution = _unificationSubstitution, error = Some(error) } ->
            inferenceFailure(
                fallbackType,
                currentSubstitution,
                supply,
                InferenceUnificationError(error)
            )

let inferRecordFieldDefinition receiverType fieldName (definition: ConstructorInferenceDefinition) substitution supply constraints =
    match instantiate(definition.scheme)(supply) with
        | InstantiationResult { semanticType = constructorType, constraints = constructorConstraints, supply = constructorSupply } ->
            match splitConstructorType(constructorType)([]) with
                | ConstructorTypeShape { parameters = fieldTypes, resultType = recordType } ->
                    match findRecordFieldType(fieldName)(definition.fieldNames)(fieldTypes) with
                        | None ->
                            fieldName
                            |> UnknownRecordField(definition.name)
                            |> inferenceFailure(SemNever)(substitution)(constructorSupply)
                        | Some(fieldType) ->
                            addConstraints(appendConstraints(constraints)(constructorConstraints))(
                                mergeUnification(substitution)(unify(applySubstitution(substitution)(receiverType))(recordType))(constructorSupply)(fieldType)
                            )

// A record's constructor is found by the type it constructs: a stitched project
// spells the type and its constructor differently, so the names cannot be compared.
let recursive findRecordDefinitionByType typeName definitions =
    match definitions with
        | [] -> None
        | (ConstructorInferenceDefinition { scheme = TypeScheme { body = body }, fieldNames = _field :: _rest } as definition) :: rest ->
            match splitConstructorType(body)([]) with
                | ConstructorTypeShape { resultType = SemNamed(_symbolId, candidate, _arguments) } ->
                    if candidate == typeName
                    then Some(definition)
                    else findRecordDefinitionByType(typeName)(rest)
                | _ -> findRecordDefinitionByType(typeName)(rest)
        | _definition :: rest -> findRecordDefinitionByType(typeName)(rest)

let resolveRecordDefinitionByType typeName (environment: TypeEnvironment) =
    match environment.lookupIndex with
        | Some(BindingLookupIndex { recordsByTypeName = records }) -> Ashes.Collection.Map.getStr(typeName)(records)
        | None -> findRecordDefinitionByType(typeName)(environment.constructors)

// The constructor a record update rebuilds. Under the program-only rules a type whose constructor
// is spelled differently (an exported type with a private constructor in a stitched project) is
// found by the type it constructs, as a field access finds it.
let recordUpdateDefinition name (environment: TypeEnvironment) =
    match resolveConstructorBinding(name)(environment) with
        | Some(definition) -> Some(definition)
        | None ->
            if environment.programInferenceRules
            then resolveRecordDefinitionByType(name)(environment)
            else None

let inferRecordField receiverType fieldName (environment: TypeEnvironment) substitution supply constraints =
    match applySubstitution(substitution)(receiverType) with
        | SemNamed(_symbolId, name, _arguments) ->
            match resolveRecordDefinitionByType(name)(environment) with
                | Some(definition) -> inferRecordFieldDefinition(receiverType)(fieldName)(definition)(substitution)(supply)(constraints)
                | None ->
                    fieldName
                    |> UnknownRecordField(name)
                    |> inferenceFailure(SemNever)(substitution)(supply)
        | SemVariable(_id) as unresolved ->
            match recordFieldCandidates(fieldName)(environment.constructors)(Ashes.Collection.Map.empty)([]) with
                | definition :: [] -> inferRecordFieldDefinition(unresolved)(fieldName)(definition)(substitution)(supply)(constraints)
                | _ -> inferenceFailure(SemNever)(substitution)(supply)(RecordUpdateRequiresRecord(unresolved))
        | other -> inferenceFailure(SemNever)(substitution)(supply)(RecordUpdateRequiresRecord(other))

// The capabilities the runtime itself provides. They declare no operations a handler could take,
// so one left in a program's top-level row is not an unsatisfied requirement.
let isBuiltinRuntimeCapability (name: Str) =
    match name with
        | "ConsoleIO" -> true
        | "FileRead" -> true
        | "FileWrite" -> true
        | "ProcessSpawn" -> true
        | "ProcessExit" -> true
        | "TimeRead" -> true
        | "EnvironmentRead" -> true
        | "Entropy" -> true
        | "UnsafeFfi" -> true
        | "NetListen" -> true
        | "NetConnect" -> true
        | "Stop" -> true
        | _ -> false

let recursive firstUnknownCapabilityIn semanticTypes (environment: TypeEnvironment) =
    match semanticTypes with
        | [] -> None
        | head :: tail ->
            match firstUnknownCapability(head)(environment) with
                | Some(name) -> Some(name)
                | None -> firstUnknownCapabilityIn(tail)(environment)
and firstUnknownCapability semanticType (environment: TypeEnvironment) =
    match semanticType with
        | SemList(element) -> firstUnknownCapability(element)(environment)
        | SemTuple(elements) -> firstUnknownCapabilityIn(elements)(environment)
        | SemFunction(argument, result, None) -> firstUnknownCapabilityIn([argument, result])(environment)
        | SemFunction(argument, result, Some(row)) -> firstUnknownCapabilityIn([argument, result, row])(environment)
        | SemCapability(name, arguments) ->
            if isBuiltinRuntimeCapability(name)
            then firstUnknownCapabilityIn(arguments)(environment)
            else
                match resolveCapabilityBinding(name)(environment) with
                    | None -> Some(name)
                    | Some(_) -> firstUnknownCapabilityIn(arguments)(environment)
        | SemRow(capabilities, None) -> firstUnknownCapabilityIn(capabilities)(environment)
        | SemRow(capabilities, Some(tail)) -> firstUnknownCapabilityIn(tail :: capabilities)(environment)
        | SemNamed(_symbolId, _name, arguments) -> firstUnknownCapabilityIn(arguments)(environment)
        | SemPointer(pointee) -> firstUnknownCapability(pointee)(environment)
        | _ -> None

// A written type may name only declared capabilities and the runtime's own in its `needs` rows.
let resolveWrittenType typeExpression context (environment: TypeEnvironment) =
    match resolveTypeExpression(typeExpression)(context) with
        | TypeResolutionResult { semanticType = semanticType, error = None } ->
            if environment.programInferenceRules
            then
                match firstUnknownCapability(semanticType)(environment) with
                    | Some(name) -> (semanticType, Some(UnknownCapabilityInRow(name)))
                    | None -> (semanticType, None)
            else (semanticType, None)
        | TypeResolutionResult { semanticType = semanticType, error = Some(error) } -> (semanticType, Some(InferenceTypeResolutionError(error)))

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

// A row closed or extended through its tail reads the same as one written out: under the program
// rules it is brought to that form before it is asked whether it is empty, closed or open.
let recursive writtenOutRow (environment: TypeEnvironment) row =
    if environment.programInferenceRules
    then
        match row with
            | SemRow(capabilities, Some(SemRow(inner, innerTail))) ->
                innerTail
                |> SemRow(append(capabilities)(inner))
                |> writtenOutRow(environment)
            | _ -> row
    else row

// The ambient row is resolved only where it is unified. A call whose row is empty never reads
// it, and resolving an open row walks the whole substitution.
let subsumeCapabilityRow capabilityRow environment ambientRow substitution supply resultType =
    (let resolvedRow = applySubstitution(substitution)(capabilityRow)
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
                match writtenOutRow(environment)(providerResolvedRow) with
                    | SemRow([], None) ->
                        inferenceSuccess(
                            applySubstitution(substitution)(resultType),
                            substitution,
                            supply
                        )
                    | SemRow(capabilities, None) ->
                        match freshTypeVariable(supply) with
                            | (tailType, nextSupply) ->
                                mergeUnificationIn(environment)(
                                    substitution,
                                    Some(tailType)
                                    |> SemRow(capabilities)
                                    |> unifyIn(environment)(applySubstitution(substitution)(ambientRow)),
                                    nextSupply,
                                    resultType
                                )
                    | _ ->
                        mergeUnificationIn(environment)(
                            substitution,
                            unifyIn(environment)(applySubstitution(substitution)(ambientRow))(providerResolvedRow),
                            supply,
                            resultType
                        ))

let recursive capabilityOperationCallRoot expression sawArgument =
    match expression with
        | ExprAt(_span, inner) -> capabilityOperationCallRoot(inner)(sawArgument)
        | ExprNode(_id, inner) -> capabilityOperationCallRoot(inner)(sawArgument)
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
            match resolveWrittenType(annotation)(preparedContext)(environment) with
                | (annotationType, None) ->
                    mergeUnificationIn(environment)(
                        substitution,
                        unifyIn(environment)(applySubstitution(substitution)(expectedType))(annotationType),
                        preparedSupply,
                        expectedType
                    )
                | (_annotationType, Some(error)) ->
                    inferenceFailure(
                        expectedType,
                        substitution,
                        preparedSupply,
                        error
                    )

// A recursive binding's written signature types the uses of the binding inside its own value. Left
// to the body alone, a call the binding makes to itself has an unknown row, which takes on the row
// of the function it is made in and so puts that row on every arrow of the binding.
let seedRecursiveSignature annotation recursiveType (environment: TypeEnvironment) substitution supply =
    match annotation with
        | None -> inferenceSuccess(recursiveType)(substitution)(supply)
        | Some(typeExpression) ->
            if environment.programInferenceRules
            then
                checkInferenceAnnotation(
                    typeExpression,
                    recursiveType,
                    environment,
                    substitution,
                    supply
                )
            else inferenceSuccess(recursiveType)(substitution)(supply)

let inferenceEnvironmentSchemes environment =
    match environment with
        | TypeEnvironment { generalizationSchemes = Some(open) } -> open
        | TypeEnvironment { bindings = bindings } ->
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
                    let environmentVariables =
                        environment
                        |> inferenceEnvironmentSchemes
                        |> freeEnvironmentVariables
                    in
                        if firstAmbiguousRequirementVariable(variables)(bodyVariables)(environmentVariables)
                        then Some((traitName, typeArguments))
                        else firstAmbiguousTraitRequirement(tail)(bindingType)(environment)

let recursive resolvedVariablesOf substitution variables =
    match variables with
        | [] -> []
        | variable :: rest ->
            rest
            |> resolvedVariablesOf(substitution)
            |> append(SemVariable(variable)
            |> applySubstitution(substitution)
            |> freeTypeVariables)

let recursive firstAmbiguousResolvedRequirement constraints bodyVariables environmentVariables =
    match constraints with
        | [] -> None
        | TraitConstraint { traitName = traitName, typeArguments = typeArguments } :: tail ->
            if firstAmbiguousRequirementVariable(freeTypeVariables(SemTuple(typeArguments)))(bodyVariables)(environmentVariables)
            then Some((traitName, typeArguments))
            else firstAmbiguousResolvedRequirement(tail)(bodyVariables)(environmentVariables)

// The ambiguity check with the environment's free variables resolved first, as generalization
// resolves them: a monomorphic variable in scope can have become an arrow while the binding was
// inferred, and a requirement on its parameter or result still belongs to the environment.
let firstAmbiguousTraitRequirementUnder substitution constraints bindingType environment =
    firstAmbiguousResolvedRequirement(
        applyInferenceConstraints(substitution)(constraints),
        bindingType
        |> applySubstitution(substitution)
        |> freeTypeVariables,
        environment
        |> inferenceEnvironmentSchemes
        |> freeEnvironmentVariables
        |> resolvedVariablesOf(substitution)
    )

let bindingRequirementAmbiguity constraints bindingType environment substitution =
    if appliesProgramInferenceRules(environment)
    then firstAmbiguousTraitRequirementUnder(substitution)(constraints)(bindingType)(environment)
    else firstAmbiguousTraitRequirement(constraints)(bindingType)(environment)

// Whether the implementations in scope supply a requirement. A requirement on a type that is not
// settled yet, or on a type parameter the enclosing declaration may itself require, is open: only
// one whose outermost types are known and that no implementation head can fit has failed.
type ImplementationHeadFit =
    | HeadFits(List((Int, SemanticType)))
    | HeadMayFit
    | HeadDiffers

type RequirementStanding =
    | RequirementHolds
    | RequirementOpen
    | RequirementFails

let recursive headParameterType (parameterId: Int) (bindings: List((Int, SemanticType))) =
    match bindings with
        | [] -> None
        | (candidateId, bound) :: tail ->
            if parameterId == candidateId
            then Some(bound)
            else headParameterType(parameterId)(tail)

let recursive withHeadParameters (bindings: List((Int, SemanticType))) semanticType =
    match semanticType with
        | SemParameter(parameterId, _name) ->
            match headParameterType(parameterId)(bindings) with
                | Some(bound) -> bound
                | None -> semanticType
        | SemList(element) ->
            element
            |> withHeadParameters(bindings)
            |> SemList
        | SemTuple(elements) ->
            elements
            |> withHeadParametersIn(bindings)
            |> SemTuple
        | SemFunction(argument, result, row) ->
            SemFunction(withHeadParameters(bindings)(argument))(withHeadParameters(bindings)(result))(row)
        | SemNamed(symbolId, name, arguments) ->
            arguments
            |> withHeadParametersIn(bindings)
            |> SemNamed(symbolId)(name)
        | SemPointer(pointee) ->
            pointee
            |> withHeadParameters(bindings)
            |> SemPointer
        | other -> other
and withHeadParametersIn (bindings: List((Int, SemanticType))) semanticTypes =
    match semanticTypes with
        | [] -> []
        | head :: tail -> withHeadParameters(bindings)(head) :: withHeadParametersIn(bindings)(tail)

let recursive requirementsWithHeadParameters (bindings: List((Int, SemanticType))) (requirements: List(TraitConstraint)) =
    match requirements with
        | [] -> []
        | TraitConstraint { traitName = traitName, typeArguments = typeArguments } :: tail -> TraitConstraint(traitName = traitName, typeArguments = withHeadParametersIn(bindings)(typeArguments)) :: requirementsWithHeadParameters(bindings)(tail)

let recursive implementationHeadsFit heads semanticTypes (bindings: List((Int, SemanticType))) (uncertain: Bool) =
    match (heads, semanticTypes) with
        | ([], []) ->
            if uncertain
            then HeadMayFit
            else HeadFits(bindings)
        | (head :: otherHeads, semanticType :: otherTypes) ->
            match implementationHeadFits(head)(semanticType)(bindings) with
                | HeadDiffers -> HeadDiffers
                | HeadMayFit -> implementationHeadsFit(otherHeads)(otherTypes)(bindings)(true)
                | HeadFits(nextBindings) -> implementationHeadsFit(otherHeads)(otherTypes)(nextBindings)(uncertain)
        | _ -> HeadDiffers
and implementationHeadFits head semanticType (bindings: List((Int, SemanticType))) =
    match head with
        | SemParameter(parameterId, _name) -> HeadFits((parameterId, semanticType) :: bindings)
        | SemVariable(_) -> HeadFits(bindings)
        | _ ->
            match semanticType with
                | SemVariable(_) -> HeadMayFit
                | SemParameter(_, _) -> HeadMayFit
                | SemNever -> HeadMayFit
                | _ ->
                    match (head, semanticType) with
                        | (SemList(headElement), SemList(element)) -> implementationHeadFits(headElement)(element)(bindings)
                        | (SemTuple(headElements), SemTuple(elements)) -> implementationHeadsFit(headElements)(elements)(bindings)(false)
                        | (SemFunction(headArgument, headResult, _headRow), SemFunction(argument, result, _row)) -> implementationHeadsFit([headArgument, headResult])([argument, result])(bindings)(false)
                        | (SemNamed(headId, headName, headArguments), SemNamed(symbolId, name, arguments)) ->
                            if headId == symbolId
                            then implementationHeadsFit(headArguments)(arguments)(bindings)(false)
                            else
                                if headName == name
                                then implementationHeadsFit(headArguments)(arguments)(bindings)(false)
                                else HeadDiffers
                        | (SemPointer(headPointee), SemPointer(pointee)) -> implementationHeadFits(headPointee)(pointee)(bindings)
                        | _ ->
                            if head == semanticType
                            then HeadFits(bindings)
                            else HeadDiffers

let recursive requirementStanding (fuel: Int) (environment: TypeEnvironment) (constraint: TraitConstraint) =
    if fuel <= 0
    then RequirementOpen
    else
        match resolveTraitBinding(constraint.traitName)(environment) with
            | None -> RequirementOpen
            | Some(_) ->
                implementationsStanding(fuel)(environment)(constraint.typeArguments)(resolveTraitImplementations(constraint.traitName)(environment))(false)
and implementationsStanding (fuel: Int) (environment: TypeEnvironment) typeArguments (implementations: List(TraitImplementationInferenceDefinition)) (uncertain: Bool) =
    match implementations with
        | [] ->
            if uncertain
            then RequirementOpen
            else RequirementFails
        | implementation :: tail ->
            match implementationHeadsFit(implementation.typeArguments)(typeArguments)([])(false) with
                | HeadDiffers -> implementationsStanding(fuel)(environment)(typeArguments)(tail)(uncertain)
                | HeadMayFit -> implementationsStanding(fuel)(environment)(typeArguments)(tail)(true)
                | HeadFits(bindings) ->
                    implementation.requirements
                    |> requirementsWithHeadParameters(bindings)
                    |> requirementsStanding(fuel - 1)(environment)
and requirementsStanding (fuel: Int) (environment: TypeEnvironment) (constraints: List(TraitConstraint)) =
    match constraints with
        | [] -> RequirementHolds
        | head :: tail ->
            match requirementStanding(fuel)(environment)(head) with
                | RequirementFails -> RequirementFails
                | RequirementHolds -> requirementsStanding(fuel)(environment)(tail)
                | RequirementOpen ->
                    match requirementsStanding(fuel)(environment)(tail) with
                        | RequirementFails -> RequirementFails
                        | _ -> RequirementOpen

let recursive firstFailedRequirement (environment: TypeEnvironment) (constraints: List(TraitConstraint)) =
    match constraints with
        | [] -> None
        | head :: tail ->
            match requirementStanding(16)(environment)(head) with
                | RequirementFails -> Some(head)
                | _ -> firstFailedRequirement(environment)(tail)

let recursive requirementsNotHeld (environment: TypeEnvironment) (constraints: List(TraitConstraint)) =
    match constraints with
        | [] -> []
        | head :: tail ->
            match requirementStanding(16)(environment)(head) with
                | RequirementHolds -> requirementsNotHeld(environment)(tail)
                | _ -> head :: requirementsNotHeld(environment)(tail)

// Under the program rules a binding's requirements are weighed against the implementations: one
// that no implementation supplies is an error where it arises, and one an implementation settles
// is not something the binding's written `requires` has to repeat.
let failedBindingRequirement (environment: TypeEnvironment) (constraints: List(TraitConstraint)) =
    if environment.programInferenceRules
    then firstFailedRequirement(environment)(constraints)
    else None

// The requirements a binding still has to carry: one that the implementations in scope already
// supply names an instance, so a scheme that kept it would pass it on to every binding that uses
// this one. Only the program inference rules drop them; lowering's own inference keeps them all.
let requirementsLeftToWrite (environment: TypeEnvironment) (constraints: List(TraitConstraint)) =
    if environment.programInferenceRules
    then requirementsNotHeld(environment)(constraints)
    else constraints

// A requirement no implementation can fit, on a type that is still partly unknown, is reported as
// ambiguous: nothing says which type was meant. On a known type it has no implementation.
let failedRequirementError (traitName: Str) (typeArguments: List(SemanticType)) =
    match freeTypeVariables(SemTuple(typeArguments)) with
        | [] -> NoTraitImplementation(traitName)(typeArguments)
        | _ -> AmbiguousTraitRequirement(traitName)(typeArguments)

// The trailing expression is generalized by nothing, so a requirement that has failed there has
// failed for good.
let checkSettledTrailingRequirements (environment: TypeEnvironment) (result: TypeInferenceResult) =
    if environment.programInferenceRules
    then
        match result with
            | TypeInferenceResult { semanticType = semanticType, substitution = substitution, supply = supply, constraints = constraints, error = None } ->
                let settled =
                    constraints
                    |> applyInferenceConstraints(substitution)
                    |> simplifyTraitConstraints(environment)
                in
                    match firstFailedRequirement(environment)(settled) with
                        | Some(TraitConstraint { traitName = traitName, typeArguments = typeArguments }) ->
                            inferenceFailure(
                                semanticType,
                                substitution,
                                supply,
                                failedRequirementError(traitName)(typeArguments)
                            )
                        | None -> result
            | _ -> result
    else result

let bindingSignatureSuccess semanticType substitution supply constraints = TypeInferenceResult(semanticType = semanticType, substitution = substitution, supply = supply, constraints = constraints, error = None)

let validateWrittenBindingRequirements inferred written semanticType substitution supply =
    match firstConstraintMissingFrom(inferred)(written) with
        | Some((traitName, typeArguments)) ->
            inferenceFailure(
                semanticType,
                substitution,
                supply,
                MissingWrittenTraitRequirement(traitName)(typeArguments)
            )
        | None ->
            match firstConstraintMissingFrom(written)(inferred) with
                | Some((traitName, typeArguments)) ->
                    inferenceFailure(
                        semanticType,
                        substitution,
                        supply,
                        UnjustifiedWrittenTraitRequirement(traitName)(typeArguments)
                    )
                | None -> bindingSignatureSuccess(semanticType)(substitution)(supply)(written)

// `Ashes.Text.fromInt` and `Ashes.Text.toHex` are typed `Int -> Str` and take an unsigned width
// only where the argument is already known to be one, so a requirement of theirs on a variable the
// binding would generalize over settles that variable to `Int` instead.
let defaultsBuiltinArgumentToInt (traitName: Str) = traitName == "$TextFromIntArgument" || traitName == "$TextToHexArgument"

let recursive variableListed (variableId: Int) (variables: List(Int)) =
    match variables with
        | [] -> false
        | head :: tail -> head == variableId || variableListed(variableId)(tail)

let recursive builtinArgumentCandidates (constraints: List(TraitConstraint)) substitution (found: List(Int)) =
    match constraints with
        | [] -> found
        | TraitConstraint { traitName = traitName, typeArguments = argument :: [] } :: rest ->
            if defaultsBuiltinArgumentToInt(traitName)
            then
                match applySubstitution(substitution)(argument) with
                    | SemVariable(variableId) -> builtinArgumentCandidates(rest)(substitution)(variableId :: found)
                    | _ -> builtinArgumentCandidates(rest)(substitution)(found)
            else builtinArgumentCandidates(rest)(substitution)(found)
        | _ :: rest -> builtinArgumentCandidates(rest)(substitution)(found)

let recursive bindBuiltinArgumentDefaults (candidates: List(Int)) (environmentVariables: List(Int)) substitution =
    match candidates with
        | [] -> substitution
        | variableId :: rest ->
            if variableListed(variableId)(environmentVariables) || variableListed(variableId)(rest)
            then bindBuiltinArgumentDefaults(rest)(environmentVariables)(substitution)
            else bindBuiltinArgumentDefaults(rest)(environmentVariables)((variableId, SemInt) :: substitution)

// The substitution with every such variable bound to `Int`, under the program inference rules
// only; the environment is scanned only when a candidate exists.
let defaultBuiltinArguments (environment: TypeEnvironment) (constraints: List(TraitConstraint)) substitution =
    if environment.programInferenceRules
    then
        match builtinArgumentCandidates(constraints)(substitution)([]) with
            | [] -> substitution
            | candidates ->
                bindBuiltinArgumentDefaults(candidates)(resolveEnvironmentVariables(substitution)(environment
                |> inferenceEnvironmentSchemes
                |> freeEnvironmentVariables)([]))(substitution)
    else substitution

let validateSettledBindingRequirements requirements inferred written selected semanticType environment substitution supply =
    match bindingRequirementAmbiguity(selected)(semanticType)(environment)(substitution) with
        | Some((traitName, typeArguments)) ->
            inferenceFailure(
                semanticType,
                substitution,
                supply,
                AmbiguousTraitRequirement(traitName)(typeArguments)
            )
        | None ->
            match requirements with
                | [] ->
                    if environment.programInferenceRules
                    then
                        let defaulted = defaultBuiltinArguments(environment)(selected)(substitution)
                        in
                            selected
                            |> applyInferenceConstraints(defaulted)
                            |> requirementsLeftToWrite(environment)
                            |> bindingSignatureSuccess(applySubstitution(defaulted)(semanticType))(defaulted)(supply)
                    else
                        selected
                        |> requirementsLeftToWrite(environment)
                        |> bindingSignatureSuccess(semanticType)(substitution)(supply)
                | _ ->
                    validateWrittenBindingRequirements(requirementsLeftToWrite(environment)(inferred))(written)(semanticType)(substitution)(supply)

let validateBindingRequirementSet requirements inferred written semanticType environment substitution supply =
    (let selected =
        match requirements with
            | [] -> inferred
            | _ -> written
    in
        match failedBindingRequirement(environment)(inferred) with
            | Some(TraitConstraint { traitName = failedTrait, typeArguments = failedArguments }) ->
                inferenceFailure(
                    semanticType,
                    substitution,
                    supply,
                    failedRequirementError(failedTrait)(failedArguments)
                )
            | None -> validateSettledBindingRequirements(requirements)(inferred)(written)(selected)(semanticType)(environment)(substitution)(supply))

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
                        match resolveWrittenType(typeExpression)(context)(environment) with
                            | (annotationType, None) ->
                                mergeUnificationIn(environment)(
                                    substitution,
                                    unifyIn(environment)(applySubstitution(substitution)(expectedType))(annotationType),
                                    preparedSupply,
                                    expectedType
                                )
                            | (_annotationType, Some(error)) ->
                                inferenceFailure(
                                    expectedType,
                                    substitution,
                                    preparedSupply,
                                    error
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

let checkTrailingRequirements expression (environment: TypeEnvironment) (result: TypeInferenceResult) =
    match expression with
        | ExprAt(span, _inner) ->
            result
            |> checkSettledTrailingRequirements(environment)
            |> locateInferenceError(span)
        | _ -> checkSettledTrailingRequirements(environment)(result)

// The empty tuple a standard trait's method scheme writes for `Unit` (`Default.default`), as the
// type of the `Unit` value the environment defines.
let recursive withEnvironmentUnit (unitType: SemanticType) (semanticType: SemanticType) =
    match semanticType with
        | SemTuple([]) -> unitType
        | SemFunction(parameter, result, row) ->
            SemFunction(withEnvironmentUnit(unitType)(parameter))(withEnvironmentUnit(unitType)(result))(row)
        | other -> other

let schemeWithEnvironmentUnit (environment: TypeEnvironment) (scheme: TypeScheme) =
    match resolveConstructorBinding("Unit")(environment) with
        | Some(ConstructorInferenceDefinition { scheme = TypeScheme { body = unitType } }) ->
            match scheme with
                | TypeScheme { body = body } -> scheme with body = withEnvironmentUnit(unitType)(body)
        | None -> scheme

// A trait's method named through the trait (`Eq.equal`, `Hash.hash`) where no value binding answers
// to it, as in a constrained binding's inference environment: the method's own scheme, its
// requirement on the trait included, instantiated as a variable's would be. The scheme's `()` is
// the environment's `Unit`, so `Default.default(Unit)` takes the value the program passes.
let inferTraitMethodReference (methodScheme: TypeScheme) (environment: TypeEnvironment) substitution supply =
    (let scheme = schemeWithEnvironmentUnit(environment)(methodScheme)
    in
        match instantiate(scheme)(supply) with
            | InstantiationResult { semanticType = instantiatedType, constraints = instantiatedConstraints, supply = nextSupply } ->
                addConstraints(
                    instantiatedConstraints,
                    inferenceSuccess(
                        applySubstitution(substitution)(instantiatedType),
                        substitution,
                        noteInstantiation(environment)(scheme)(supply)(instantiatedConstraints)(nextSupply)
                    )
                ))

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
        | [] ->
            inferenceSuccess(elementType
            |> applySubstitution(substitution)
            |> SemList)(substitution)(supply)
        | head :: tail ->
            match inferWith(head)(environment)(substitution)(supply)(ambientRow) with
                | TypeInferenceResult { semanticType = inferredType, substitution = nextSubstitution, supply = nextSupply, constraints = inferredConstraints, error = None } ->
                    let unification =
                        unifyIn(environment)(
                            applySubstitution(nextSubstitution)(elementType),
                            applySubstitution(nextSubstitution)(inferredType)
                        )
                    in
                        match mergeUnificationIn(environment)(nextSubstitution)(unification)(nextSupply)(elementType) with
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
                        unifyIn(environment)(
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
                0
                |> coverageLength(patterns)
                |> ConstructorPatternArityMismatch(constructorName)(coverageLength(parameterTypes)(0))
            )
and inferRecordPatternFields constructorName fields fieldNames fieldTypes resultType environment substitution supply names seenFields =
    match fields with
        | [] ->
            patternSuccess(applySubstitution(substitution)(resultType))(environment)(substitution)(supply)(names)
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
                                    unifyIn(environment)(
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
                                match mergeUnificationIn(environment)(
                                    expressionSubstitution,
                                    unifyIn(environment)(
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
                                                    match mergeUnificationIn(environment)(
                                                        mapperSubstitution,
                                                        unifyIn(environment)(
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
                                                                                match mergeUnificationIn(environment)(
                                                                                    subsumedSubstitution,
                                                                                    unifyIn(environment)(
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
                                                        match mergeUnificationIn(environment)(
                                                            mapperSubstitution,
                                                            unifyIn(environment)(
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
                            let expectedTask =
                                SemNamed(resolveTaskSymbolId(environment))("Task")([errorType, successType])
                            in
                                match mergeUnificationIn(environment)(
                                    taskSubstitution,
                                    unifyIn(environment)(applySubstitution(taskSubstitution)(taskType))(expectedTask),
                                    successSupply,
                                    expectedTask
                                ) with
                                    | TypeInferenceResult { substitution = unifiedSubstitution, supply = unifiedSupply, error = None } ->
                                        addConstraints(
                                            taskConstraints,
                                            inferenceSuccess(
                                                [errorType, successType]
                                                |> SemNamed(resolveResultSymbolId(environment))("Result")
                                                |> applySubstitution(unifiedSubstitution),
                                                unifiedSubstitution,
                                                unifiedSupply
                                            )
                                        )
                                    | TypeInferenceResult { substitution = failedSubstitution, supply = failedSupply } ->
                                        inferenceFailure(
                                            SemNever,
                                            failedSubstitution,
                                            failedSupply,
                                            taskType
                                            |> applySubstitution(taskSubstitution)
                                            |> ExpectedTaskType
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
                            LetResultRequiresResult(resolvedValue)
                        )
                    | Some(ResultTypeShape { symbolId = _symbolId, errorType = errorType, successType = successType }) ->
                        let binding = TypeScheme(quantified = [], body = successType, constraints = [])
                        in
                            match inferWith(
                                body,
                                addTypeBinding(name)(binding)(environment),
                                valueSubstitution,
                                noteBinding(environment)(currentNodeOf(supply))(name)(binding)(valueSupply),
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
                                                    LetResultBodyRequiresResult(resolvedBody)
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
                                                        mergeUnificationIn(environment)(
                                                            bodySubstitution,
                                                            unifyIn(environment)(
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
                        unifyIn(environment)(
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
                                                        parameterSupply
                                                        |> noteBinding(environment)(identifiedNode(body))("resume")(resumeScheme)
                                                        |> noteBindingsIn(environment)(identifiedNode(body))(patternVariableNamesIn(parameters)([]))(armEnvironment),
                                                        ambientRow
                                                    ) with
                                                        | TypeInferenceResult { semanticType = armType, substitution = armSubstitution, supply = armSupply, constraints = armConstraints, error = None } ->
                                                            match mergeUnificationIn(environment)(
                                                                armSubstitution,
                                                                unifyIn(environment)(
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
                        unifyIn(expectedEnvironment)(
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
                                    unifyIn(baseEnvironment)(
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
            then
                inferPattern(PatternConstructor(name)([]))(environment)(substitution)(supply)(names)
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
                            let listType =
                                headType
                                |> applySubstitution(tailSubstitution)
                                |> SemList
                            in
                                mergePatternUnification(
                                    tailSubstitution,
                                    unifyIn(environment)(applySubstitution(tailSubstitution)(tailType))(listType),
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
                        reversedTypes
                        |> reverse
                        |> SemTuple,
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
                                    let expectedCount = coverageLength(parameterTypes)(0)
                                    in
                                        let actualCount = coverageLength(patterns)(0)
                                        in
                                            if expectedCount != actualCount
                                            then
                                                patternFailure(
                                                    SemNever,
                                                    environment,
                                                    substitution,
                                                    constructorSupply,
                                                    names,
                                                    ConstructorPatternArityMismatch(name)(expectedCount)(actualCount)
                                                )
                                            else
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
            match tuplePatternArityError(applySubstitution(substitution)(scrutineeType))(pattern) with
                | Some(arityError) -> inferenceFailure(SemNever)(substitution)(supply)(arityError)
                | None ->
                    match inferPattern(pattern)(environment)(substitution)(supply)([]) with
                        | PatternInferenceResult { semanticType = patternType, environment = patternEnvironment, substitution = patternSubstitution, supply = patternSupply, names = patternNames, error = None } ->
                            match mergePatternUnification(
                                patternSubstitution,
                                unifyIn(environment)(
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
                                        noteBindingsIn(environment)(identifiedNode(body))(patternNames)(matchedEnvironment)(matchedSupply),
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
                    match mergeUnificationIn(environment)(
                        guardSubstitution,
                        unifyIn(environment)(applySubstitution(guardSubstitution)(guardType))(SemBool),
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
            match mergeUnificationIn(environment)(
                bodySubstitution,
                bodyType
                |> applySubstitution(bodySubstitution)
                |> unifyIn(environment)(applySubstitution(bodySubstitution)(resultType)),
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
                        unifyIn(environment)(
                            applySubstitution(rightSubstitution)(leftType),
                            applySubstitution(rightSubstitution)(rightType)
                        )
                    in
                        match mergeUnificationIn(environment)(rightSubstitution)(unification)(rightSupply)(leftType) with
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
                                        noteOperator(environment)(currentNodeOf(supply))(traitName)(unifiedType)(addConstraints(
                                            appendConstraints(
                                                leftConstraints,
                                                appendConstraints(rightConstraints)([constraint])
                                            ),
                                            inferenceSuccess(resultType)(unifiedSubstitution)(unifiedSupply)
                                        ))
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
                    noteOperator(environment)(currentNodeOf(supply))(traitName)(resolvedOperand)(addConstraints(
                        appendConstraints(operandConstraints)([constraint]),
                        inferenceSuccess(resolvedOperand)(operandSubstitution)(operandSupply)
                    ))
        | failure -> failure
and inferHandlerReturn returnArm bodyType handlerResult environment substitution supply ambientRow accumulatedConstraints =
    match returnArm with
        | None ->
            addConstraints(
                accumulatedConstraints,
                mergeUnificationIn(environment)(
                    substitution,
                    bodyType
                    |> applySubstitution(substitution)
                    |> unifyIn(environment)(applySubstitution(substitution)(handlerResult)),
                    supply,
                    handlerResult
                )
            )
        | Some((pattern, returnBody)) ->
            match inferPattern(pattern)(environment)(substitution)(supply)([]) with
                | PatternInferenceResult { semanticType = patternType, environment = returnEnvironment, substitution = patternSubstitution, supply = patternSupply, names = returnNames, error = None } ->
                    match mergePatternUnification(
                        patternSubstitution,
                        unifyIn(environment)(
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
                                noteBindingsIn(environment)(identifiedNode(returnBody))(returnNames)(unifiedEnvironment)(unifiedSupply),
                                ambientRow
                            ) with
                                | TypeInferenceResult { semanticType = returnType, substitution = returnSubstitution, supply = returnSupply, constraints = returnConstraints, error = None } ->
                                    addConstraints(
                                        appendConstraints(accumulatedConstraints)(returnConstraints),
                                        mergeUnificationIn(environment)(
                                            returnSubstitution,
                                            unifyIn(environment)(
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
        | ExprAt(span, inner) ->
            locateInferenceError(
                span,
                inferCalledFunction(inner)(environment)(substitution)(supply)(ambientRow)
            )
        | ExprNode(node, inner) ->
            if environment.programInferenceRules
            then
                ambientRow
                |> inferCalledFunction(inner)(environment)(substitution)(withCurrentNode(node)(supply))
                |> recordNodeType(node)
            else inferCalledFunction(inner)(environment)(substitution)(supply)(ambientRow)
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
                                    noteInstantiation(environment)(scheme)(supply)(instantiatedConstraints)(nextSupply)
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
                                            noteInstantiation(environment)(scheme)(supply)(instantiatedConstraints)(nextSupply)
                                        )
                                    )
        | _ -> inferWith(expression)(environment)(substitution)(supply)(ambientRow)
and inferSeededRecursiveValue annotation recursiveType value recursiveEnvironment environment substitution supply ambientRow =
    match seedRecursiveSignature(annotation)(recursiveType)(environment)(substitution)(supply) with
        | TypeInferenceResult { semanticType = _seededType, substitution = seededSubstitution, supply = seededSupply, constraints = _seededConstraints, error = None } -> inferWith(value)(recursiveEnvironment)(seededSubstitution)(seededSupply)(ambientRow)
        | failure -> failure
// A shipped trait's method is bound under the trait's own name (`Hash.hash`); a reference that
// stitching qualified (`Ashes_Trait_Hash.hash`) finds it there when nothing answers to the
// qualified name itself.
and inferQualifiedValue moduleName name environment substitution supply ambientRow =
    (let qualified =
        match (resolveTypeBinding(traitNameIn(environment)(moduleName) + "." + name)(environment), resolveTypeBinding(canonicalTraitName(moduleName) + "." + name)(environment)) with
            | (None, Some(_canonical)) -> canonicalTraitName(moduleName) + "." + name
            | _ -> traitNameIn(environment)(moduleName) + "." + name
    in
        match resolveTypeBinding(qualified)(environment) with
            | Some(_scheme) -> inferWith(ExprVar(qualified))(environment)(substitution)(supply)(ambientRow)
            | None ->
                match resolveTypeBinding(moduleName)(environment) with
                    | None ->
                        match resolveTraitMethod(canonicalTraitName(moduleName))(name)(environment) with
                            | Some(TraitMethodInferenceDefinition { scheme = methodScheme }) -> inferTraitMethodReference(methodScheme)(environment)(substitution)(supply)
                            | None -> inferWith(ExprVar(qualified))(environment)(substitution)(supply)(ambientRow)
                    | Some(_receiver) ->
                        match inferWith(ExprVar(moduleName))(environment)(substitution)(supply)(ambientRow) with
                            | TypeInferenceResult { semanticType = receiverType, substitution = receiverSubstitution, supply = receiverSupply, constraints = receiverConstraints, error = None } -> inferRecordField(receiverType)(name)(environment)(receiverSubstitution)(receiverSupply)(receiverConstraints)
                            | failed -> failed)
// A value inferred against the type its binding's signature writes: a lambda takes its parameter
// type from the signature instead of a fresh variable, so what its body does with the parameter
// can depend on that type. Anything that is not a lambda is inferred as it is.
and inferAgainst expected expression environment substitution supply ambientRow =
    match expression with
        | ExprAt(span, inner) ->
            locateInferenceError(
                span,
                inferAgainst(expected)(inner)(environment)(substitution)(supply)(ambientRow)
            )
        | ExprNode(node, inner) ->
            if environment.programInferenceRules
            then
                ambientRow
                |> inferAgainst(expected)(inner)(environment)(substitution)(withCurrentNode(node)(supply))
                |> recordNodeType(node)
            else inferAgainst(expected)(inner)(environment)(substitution)(supply)(ambientRow)
        | ExprLambda(name, body, None) ->
            match applySubstitution(substitution)(expected) with
                | SemFunction(expectedParameter, expectedResult, _expectedRow) ->
                    let parameterScheme = TypeScheme(quantified = [], body = expectedParameter, constraints = [])
                    in
                        match freshTypeVariable(supply) with
                            | (bodyAmbientRow, bodyAmbientSupply) ->
                                match inferAgainst(expectedResult)(body)(addTypeBinding(name)(parameterScheme)(environment))(substitution)(noteBinding(environment)(currentNodeOf(supply))(name)(parameterScheme)(bodyAmbientSupply))(bodyAmbientRow) with
                                    | TypeInferenceResult { semanticType = bodyType, substitution = bodySubstitution, supply = bodySupply, constraints = bodyConstraints, error = None } ->
                                        addConstraints(
                                            bodyConstraints,
                                            inferenceSuccess(
                                                SemFunction(
                                                    applySubstitution(bodySubstitution)(expectedParameter),
                                                    applySubstitution(bodySubstitution)(bodyType),
                                                    bodyAmbientRow
                                                    |> applySubstitution(bodySubstitution)
                                                    |> Some
                                                ),
                                                bodySubstitution,
                                                bodySupply
                                            )
                                        )
                                    | failure -> failure
                | _ -> inferWith(expression)(environment)(substitution)(supply)(ambientRow)
        | _ -> inferWith(expression)(environment)(substitution)(supply)(ambientRow)
// The second inference of a binding's value, against its written signature. `direct` is the
// failure the value gave on its own, which stays the answer unless this inference succeeds.
and inferAgainstSignature typeExpression value environment substitution supply ambientRow direct =
    match freshTypeVariable(supply) with
        | (expected, expectedSupply) ->
            match checkInferenceAnnotation(
                typeExpression,
                expected,
                environment,
                substitution,
                expectedSupply
            ) with
                | TypeInferenceResult { semanticType = _seededType, substitution = seededSubstitution, supply = seededSupply, constraints = _seededConstraints, error = None } ->
                    let seeded = inferAgainst(expected)(value)(environment)(seededSubstitution)(seededSupply)(ambientRow)
                    in
                        match seeded with
                            | TypeInferenceResult { error = None } -> seeded
                            | _ -> direct
                | _ -> direct
// A binding's value. Under the program inference rules, a value that does not infer on its own
// and whose binding writes a signature is inferred once more against that signature.
and inferAnnotatedValue annotation value environment substitution supply ambientRow =
    (let direct = inferWith(value)(environment)(substitution)(supply)(ambientRow)
    in
        match (direct, annotation) with
            | (TypeInferenceResult { error = Some(_error) }, Some(typeExpression)) ->
                if appliesProgramInferenceRules(environment)
                then inferAgainstSignature(typeExpression)(value)(environment)(substitution)(supply)(ambientRow)(direct)
                else direct
            | _ -> direct)
and inferWith expression environment substitution supply ambientRow =
    match expression with
        | ExprAt(span, inner) ->
            locateInferenceError(
                span,
                inferWith(inner)(environment)(substitution)(supply)(ambientRow)
            )
        | ExprNode(node, inner) ->
            if environment.programInferenceRules
            then
                ambientRow
                |> inferWith(inner)(environment)(substitution)(withCurrentNode(node)(supply))
                |> recordNodeType(node)
            else inferWith(inner)(environment)(substitution)(supply)(ambientRow)
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
                                            noteInstantiation(environment)(scheme)(supply)(instantiatedConstraints)(nextSupply)
                                        )
                                    )
        | ExprQualifiedVar(moduleName, name) ->
            match resolveCapabilityBinding(moduleName)(environment) with
                | None -> inferQualifiedValue(moduleName)(name)(environment)(substitution)(supply)(ambientRow)
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
                                            noteInstantiation(environment)(scheme)(supply)(instantiatedConstraints)(nextSupply)
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
                                                    noteBinding(environment)(currentNodeOf(supply))(name)(parameterScheme)(bodyAmbientSupply),
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
                                                    unifyIn(environment)(
                                                        applySubstitution(argumentSubstitution)(functionType),
                                                        expectedFunction
                                                    )
                                                in
                                                    match mergeUnificationIn(environment)(
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
                                                        | failure -> builtinArgumentFailure(function)(argumentType)(failure)
                        | failure -> failure
                | failure -> failure
        // The ambient capability row belongs to the surrounding environment; only the successful
        // value type and its selected trait constraints are generalized for the body.
        | ExprLet(name, value, body, _parameters, annotation, requirements) ->
            match inferAnnotatedValue(annotation)(value)(environment)(substitution)(supply)(ambientRow) with
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
                                    generalizeUnderSubstitution(
                                        inferenceEnvironmentSchemes(environment),
                                        checkedSubstitution,
                                        resolvedValue,
                                        selectedConstraints
                                    )
                                in
                                    let bodyEnvironment = addTypeBinding(name)(scheme)(environment)
                                    in
                                        inferWith(body)(bodyEnvironment)(checkedSubstitution)(noteBinding(environment)(currentNodeOf(supply))(name)(scheme)(checkedSupply))(ambientRow)
                        | failure -> failure
                | failure -> failure
        | ExprLetRecursive(name, value, body, _parameters, annotation, requirements) ->
            match freshTypeVariable(supply) with
                | (recursiveType, afterRecursive) ->
                    let recursiveScheme = TypeScheme(quantified = [], body = recursiveType, constraints = [])
                    in
                        let recursiveEnvironment = addTypeBinding(name)(recursiveScheme)(environment)
                        in
                            match inferSeededRecursiveValue(annotation)(recursiveType)(value)(recursiveEnvironment)(environment)(substitution)(afterRecursive)(ambientRow) with
                                | TypeInferenceResult { semanticType = valueType, substitution = valueSubstitution, supply = valueSupply, constraints = valueConstraints, error = None } ->
                                    let unification =
                                        unifyIn(environment)(
                                            applySubstitution(valueSubstitution)(recursiveType),
                                            applySubstitution(valueSubstitution)(valueType)
                                        )
                                    in
                                        match mergeUnificationIn(environment)(
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
                                                                generalizeUnderSubstitution(
                                                                    inferenceEnvironmentSchemes(environment),
                                                                    checkedSubstitution,
                                                                    resolvedType,
                                                                    selectedConstraints
                                                                )
                                                            in
                                                                inferWith(
                                                                    body,
                                                                    addTypeBinding(name)(scheme)(environment),
                                                                    checkedSubstitution,
                                                                    noteBinding(environment)(currentNodeOf(supply))(name)(scheme)(checkedSupply),
                                                                    ambientRow
                                                                )
                                                    | failure -> failure
                                            | failure -> failure
                                | failure -> failure
        | ExprIf(condition, thenBranch, elseBranch) ->
            match inferWith(condition)(environment)(substitution)(supply)(ambientRow) with
                | TypeInferenceResult { semanticType = conditionType, substitution = conditionSubstitution, supply = conditionSupply, constraints = conditionConstraints, error = None } ->
                    let conditionUnification =
                        unifyIn(environment)(applySubstitution(conditionSubstitution)(conditionType))(SemBool)
                    in
                        match mergeUnificationIn(environment)(
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
                                                    unifyIn(environment)(
                                                        applySubstitution(elseSubstitution)(thenType),
                                                        applySubstitution(elseSubstitution)(elseType)
                                                    )
                                                in
                                                    addConstraints(
                                                        appendConstraints(
                                                            conditionConstraints,
                                                            appendConstraints(thenConstraints)(elseConstraints)
                                                        ),
                                                        mergeUnificationIn(environment)(
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
                        inferenceSuccess(reversedTypes
                        |> reverse
                        |> SemTuple)(tupleSubstitution)(tupleSupply)
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
                                unifyIn(environment)(
                                    applySubstitution(tailSubstitution)(tailType),
                                    headType
                                    |> applySubstitution(tailSubstitution)
                                    |> SemList
                                )
                            in
                                addConstraints(
                                    appendConstraints(headConstraints)(tailConstraints),
                                    mergeUnificationIn(environment)(tailSubstitution)(unification)(tailSupply)(tailType)
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
                                            noteInstantiation(environment)(scheme)(supply)(constructorConstraints)(constructorSupply),
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
                            match recordUpdateDefinition(name)(environment) with
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
                                                    match mergeUnificationIn(environment)(
                                                        targetSubstitution,
                                                        unifyIn(environment)(
                                                            applySubstitution(targetSubstitution)(targetType),
                                                            resultType
                                                        ),
                                                        noteInstantiationAt(environment)(currentNodeOf(supply))(scheme)(targetSupply)(constructorConstraints)(constructorSupply),
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
            if appliesProgramInferenceRules(environment) && isZeroLiteralExpression(left)
            then
                ambientRow
                |> inferUnaryTrait("Negate")(right)(environment)(substitution)(supply)
                |> noteNegatedZero(environment)(identifiedNode(left))
            else
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
                    let leftUnification =
                        unifyIn(environment)(applySubstitution(leftSubstitution)(leftType))(SemBool)
                    in
                        match mergeUnificationIn(environment)(leftSubstitution)(leftUnification)(leftSupply)(SemBool) with
                            | TypeInferenceResult { semanticType = _leftBool, substitution = leftBoolSubstitution, supply = leftBoolSupply, constraints = _leftUnificationConstraints, error = None } ->
                                match inferWith(right)(environment)(leftBoolSubstitution)(leftBoolSupply)(ambientRow) with
                                    | TypeInferenceResult { semanticType = rightType, substitution = rightSubstitution, supply = rightSupply, constraints = rightConstraints, error = None } ->
                                        let rightUnification =
                                            unifyIn(environment)(applySubstitution(rightSubstitution)(rightType))(SemBool)
                                        in
                                            addConstraints(
                                                appendConstraints(leftConstraints)(rightConstraints),
                                                mergeUnificationIn(environment)(rightSubstitution)(rightUnification)(rightSupply)(SemBool)
                                            )
                                    | failure -> failure
                            | failure -> failure
                | failure -> failure
        | ExprLogicalOr(left, right) ->
            match inferWith(left)(environment)(substitution)(supply)(ambientRow) with
                | TypeInferenceResult { semanticType = leftType, substitution = leftSubstitution, supply = leftSupply, constraints = leftConstraints, error = None } ->
                    let leftUnification =
                        unifyIn(environment)(applySubstitution(leftSubstitution)(leftType))(SemBool)
                    in
                        match mergeUnificationIn(environment)(leftSubstitution)(leftUnification)(leftSupply)(SemBool) with
                            | TypeInferenceResult { semanticType = _leftBool, substitution = leftBoolSubstitution, supply = leftBoolSupply, constraints = _leftUnificationConstraints, error = None } ->
                                match inferWith(right)(environment)(leftBoolSubstitution)(leftBoolSupply)(ambientRow) with
                                    | TypeInferenceResult { semanticType = rightType, substitution = rightSubstitution, supply = rightSupply, constraints = rightConstraints, error = None } ->
                                        let rightUnification =
                                            unifyIn(environment)(applySubstitution(rightSubstitution)(rightType))(SemBool)
                                        in
                                            addConstraints(
                                                appendConstraints(leftConstraints)(rightConstraints),
                                                mergeUnificationIn(environment)(rightSubstitution)(rightUnification)(rightSupply)(SemBool)
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

let inferExpressionFromLocated expression environment substitution supply =
    match freshTypeVariable(supply) with
        | (ambientRow, nextSupply) -> inferWith(expression)(environment)(substitution)(nextSupply)(ambientRow)

// What is left of a top-level expression's row once handlers and providers have taken theirs has
// nothing to satisfy it, unless the runtime does.
let recursive firstUnsatisfiedCapability row (environment: TypeEnvironment) =
    match row with
        | SemRow([], Some(tail)) -> firstUnsatisfiedCapability(tail)(environment)
        | SemRow((SemCapability(name, _arguments) as needed) :: rest, tail) ->
            if isBuiltinRuntimeCapability(name)
            then
                firstUnsatisfiedCapability(SemRow(rest)(tail))(environment)
            else
                match resolveCapabilityProvider(needed)(environment) with
                    | Some(_) ->
                        firstUnsatisfiedCapability(SemRow(rest)(tail))(environment)
                    | None -> Some(name)
        | SemRow(_other :: rest, tail) ->
            firstUnsatisfiedCapability(SemRow(rest)(tail))(environment)
        | _ -> None

let checkResidualRow ambientRow (environment: TypeEnvironment) (result: TypeInferenceResult) =
    if environment.programInferenceRules
    then
        match result with
            | TypeInferenceResult { semanticType = semanticType, substitution = substitution, supply = supply, constraints = _constraints, error = None } ->
                match firstUnsatisfiedCapability(applySubstitution(substitution)(ambientRow))(environment) with
                    | Some(name) ->
                        inferenceFailure(
                            semanticType,
                            substitution,
                            supply,
                            UnsatisfiedCapability(name)
                        )
                    | None -> result
            | _ -> result
    else result

// A top-level value or the program's trailing expression: nothing encloses it, so its row has to
// be satisfied here.
let inferTopLevelExpressionLocated expression environment substitution supply =
    match freshTypeVariable(supply) with
        | (ambientRow, nextSupply) ->
            ambientRow
            |> inferWith(expression)(environment)(substitution)(nextSupply)
            |> checkResidualRow(ambientRow)(environment)

// As `inferTopLevelExpressionLocated`, for the value of a binding that may write a signature.
let inferTopLevelValueLocated annotation value environment substitution supply =
    match freshTypeVariable(supply) with
        | (ambientRow, nextSupply) ->
            ambientRow
            |> inferAnnotatedValue(annotation)(value)(environment)(substitution)(nextSupply)
            |> checkResidualRow(ambientRow)(environment)

let inferExpressionFrom expression environment substitution supply =
    unlocatedInferenceResult(
        inferExpressionFromLocated(expression)(environment)(substitution)(supply)
    )

let inferTopLevelBindingLocated name value annotation requirements environment substitution supply =
    match inferTopLevelValueLocated(annotation)(value)(environment)(substitution)(supply) with
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
                            generalizeUnderSubstitution(
                                inferenceEnvironmentSchemes(environment),
                                checkedSubstitution,
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

let inferTopLevelBinding name value annotation requirements environment substitution supply =
    match inferTopLevelBindingLocated(name)(value)(annotation)(requirements)(environment)(substitution)(supply) with
        | TopLevelBindingInferenceResult { environment = failedEnvironment, semanticType = failedType, substitution = failedSubstitution, supply = failedSupply, error = Some(InferenceErrorAt(_, inner)) } ->
            TopLevelBindingInferenceResult(environment = failedEnvironment, semanticType = failedType, substitution = failedSubstitution, supply = failedSupply, error = Some(
                inner
            ))
        | result -> result

// Starts above the reserved ids for the same reason `initialProgramInferenceState` does: an
// environment carrying statically embedded schemes (the builtin signatures, the standard traits'
// parameters) must never meet a fresh variable with one of their quantified ids.
let inferExpression expression environment =
    inferExpressionFrom(
        expression,
        environment,
        [],
        typeVariableSupplyFrom(reservedInferenceTypeVariableCount)
    )
