import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.TraitResolution
import Ashes.Collection.List.reverse
import Ashes.Collection.List.sortBy
import Ashes.Text.compare as compareText
export (
    type TraitDictionaryMethodSource(..),
    type TraitDictionaryMethodField(..),
    type TraitDictionaryConstructionPlan(..),
    type TraitDictionaryConstructionError(..),
    type TraitDictionaryConstructionPlanning(..),
    value planTraitDictionaryConstruction,
)

type TraitDictionaryMethodSource =
    | TraitDictionarySuppliedMethod
    | TraitDictionaryDefaultMethod
    deriving {Eq, Show}

type TraitDictionaryMethodField =
    | methodIndex: Int
    | methodName: Str
    | source: TraitDictionaryMethodSource
    | implementation: Expr

type TraitDictionaryConstructionPlan =
    | constraint: TraitConstraint
    | methods: List(TraitDictionaryMethodField)
    | requirements: List(TraitEvidencePlan)
    | supertraits: List(TraitEvidencePlan)

type TraitDictionaryConstructionError =
    | TraitDictionaryConstructionRequiresParameter(TraitConstraint)
    | TraitDictionaryConstructionUnknownTrait(TraitConstraint)
    | TraitDictionaryConstructionMissingMethod(TraitConstraint, Str)
    deriving {Eq, Show}

type TraitDictionaryConstructionPlanning =
    | construction: Maybe(TraitDictionaryConstructionPlan)
    | error: Maybe(TraitDictionaryConstructionError)

type TraitDictionaryMethodFieldPlanning =
    | fields: List(TraitDictionaryMethodField)
    | error: Maybe(TraitDictionaryConstructionError)

let recursive findSuppliedTraitMethod methodName methods =
    match methods with
        | [] -> None
        | (TraitImplementationMethodInferenceDefinition { name = name, implementation = _implementation, semanticType = _semanticType } as method) :: tail ->
            if name == methodName
            then Some(method)
            else findSuppliedTraitMethod(methodName)(tail)

let traitConstructionMethodBefore left right =
    match (left, right) with
        | (TraitMethodInferenceDefinition { name = leftName, scheme = _leftScheme, defaultImplementation = _leftDefault }, TraitMethodInferenceDefinition { name = rightName, scheme = _rightScheme, defaultImplementation = _rightDefault }) -> compareText(leftName)(rightName) <= 0

let sortTraitConstructionMethods methods = sortBy(traitConstructionMethodBefore)(methods)

let recursive planTraitDictionaryMethodFields constraint methods suppliedMethods methodIndex reversed =
    match methods with
        | [] -> TraitDictionaryMethodFieldPlanning(fields = reverse(reversed), error = None)
        | TraitMethodInferenceDefinition { name = methodName, scheme = _scheme, defaultImplementation = defaultImplementation } :: tail ->
            match findSuppliedTraitMethod(methodName)(suppliedMethods) with
                | Some(TraitImplementationMethodInferenceDefinition { name = _name, implementation = implementation, semanticType = _semanticType }) -> planTraitDictionaryMethodFields(constraint)(tail)(suppliedMethods)(methodIndex + 1)(TraitDictionaryMethodField(methodIndex = methodIndex, methodName = methodName, source = TraitDictionarySuppliedMethod, implementation = implementation) :: reversed)
                | None ->
                    match defaultImplementation with
                        | Some(implementation) -> planTraitDictionaryMethodFields(constraint)(tail)(suppliedMethods)(methodIndex + 1)(TraitDictionaryMethodField(methodIndex = methodIndex, methodName = methodName, source = TraitDictionaryDefaultMethod, implementation = implementation) :: reversed)
                        | None -> TraitDictionaryMethodFieldPlanning(fields = reverse(reversed), error = Some(TraitDictionaryConstructionMissingMethod(constraint)(methodName)))

let planTraitDictionaryInstance constraint implementation requirements supertraits environment =
    match constraint with
        | TraitConstraint { traitName = traitName, typeArguments = _typeArguments } ->
            match (resolveTraitBinding(traitName)(environment), implementation) with
                | (Some(TraitInferenceDefinition { name = _name, parameterCount = _parameterCount, parameters = _parameters, methods = methods, supertraits = _traitSupertraits, provenance = _provenance }), TraitImplementationInferenceDefinition { traitName = _implementationTraitName, typeArguments = _implementationTypeArguments, requirements = _implementationRequirements, methods = suppliedMethods }) ->
                    match planTraitDictionaryMethodFields(constraint)(sortTraitConstructionMethods(methods))(suppliedMethods)(0)([]) with
                        | TraitDictionaryMethodFieldPlanning { fields = fields, error = None } -> TraitDictionaryConstructionPlanning(construction = Some(TraitDictionaryConstructionPlan(constraint = constraint, methods = fields, requirements = requirements, supertraits = supertraits)), error = None)
                        | TraitDictionaryMethodFieldPlanning { fields = _fields, error = Some(error) } -> TraitDictionaryConstructionPlanning(construction = None, error = Some(error))
                | _ -> TraitDictionaryConstructionPlanning(construction = None, error = Some(TraitDictionaryConstructionUnknownTrait(constraint)))

let planTraitDictionaryConstruction evidence environment =
    match evidence with
        | TraitEvidenceParameter(constraint) -> TraitDictionaryConstructionPlanning(construction = None, error = Some(TraitDictionaryConstructionRequiresParameter(constraint)))
        | TraitEvidenceInstance(constraint, implementation, requirements, supertraits) -> planTraitDictionaryInstance(constraint)(implementation)(requirements)(supertraits)(environment)
