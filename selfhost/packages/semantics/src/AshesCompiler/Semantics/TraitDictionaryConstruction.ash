// Plans deterministic runtime dictionaries from resolved trait implementations.
//
// Invariants:
// - Explicit implementation methods override defaults and fields follow ABI method order.
// - Default methods are constructed after the defaults on which they depend.
// - Cyclic defaults and missing required methods fail before a dictionary is emitted.

import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.TraitResolution
import AshesCompiler.Semantics.ProgramInference
import AshesCompiler.Semantics.TraitEvidenceRewriting
import Ashes.Collection.List.append as appendList
import Ashes.Collection.List.reverse
import Ashes.Collection.List.sortBy
import Ashes.Text.compare as compareText
export (
    type TraitDictionaryMethodSource(..),
    type TraitDictionaryMethodField(..),
    type TraitDictionaryConstructionPlan(..),
    type TraitDictionaryConstructionError(..),
    type TraitDictionaryConstructionPlanning(..),
    type TraitDictionaryValueRewriting(..),
    value planTraitDictionaryConstruction,
    value rewriteTraitDictionaryValue,
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
    | methodConstructionOrder: List(TraitDictionaryMethodField)
    | requirements: List(TraitEvidencePlan)
    | supertraits: List(TraitEvidencePlan)

type TraitDictionaryConstructionError =
    | TraitDictionaryConstructionRequiresParameter(TraitConstraint)
    | TraitDictionaryConstructionUnknownTrait(TraitConstraint)
    | TraitDictionaryConstructionMissingMethod(TraitConstraint, Str)
    | TraitDictionaryConstructionMethodCycle(TraitConstraint, Str)
    deriving {Eq, Show}

type TraitDictionaryConstructionPlanning =
    | construction: Maybe(TraitDictionaryConstructionPlan)
    | error: Maybe(TraitDictionaryConstructionError)

type TraitDictionaryValueRewriting =
    | expression: Maybe(Expr)
    | error: Maybe(TraitDictionaryConstructionError)

type TraitDictionaryExpressionListRewriting =
    | expressions: List(Expr)
    | error: Maybe(TraitDictionaryConstructionError)

type TraitDictionaryMethodFieldPlanning =
    | fields: List(TraitDictionaryMethodField)
    | error: Maybe(TraitDictionaryConstructionError)

type TraitDictionaryMethodOrderBuild =
    | completed: List(Str)
    | reversedOrder: List(TraitDictionaryMethodField)
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
        | (TraitMethodInferenceDefinition { name = leftName, scheme = _leftScheme, defaultImplementation = _leftDefault }, TraitMethodInferenceDefinition { name = rightName, scheme = _rightScheme, defaultImplementation = _rightDefault }) ->
            compareText(
                leftName,
                rightName
            ) <= 0

let sortTraitConstructionMethods methods = sortBy(traitConstructionMethodBefore)(methods)

let recursive planTraitDictionaryMethodFields constraint methods suppliedMethods methodIndex reversed =
    match methods with
        | [] -> TraitDictionaryMethodFieldPlanning(fields = reverse(reversed), error = None)
        | TraitMethodInferenceDefinition { name = methodName, scheme = _scheme, defaultImplementation = defaultImplementation } :: tail ->
            match findSuppliedTraitMethod(methodName)(suppliedMethods) with
                | Some(TraitImplementationMethodInferenceDefinition { name = _name, implementation = implementation, semanticType = _semanticType }) ->
                    planTraitDictionaryMethodFields(
                        constraint,
                        tail,
                        suppliedMethods,
                        methodIndex + 1,
                        TraitDictionaryMethodField(methodIndex = methodIndex, methodName = methodName, source = TraitDictionarySuppliedMethod, implementation = implementation) :: reversed
                    )
                | None ->
                    match defaultImplementation with
                        | Some(implementation) ->
                            planTraitDictionaryMethodFields(
                                constraint,
                                tail,
                                suppliedMethods,
                                methodIndex + 1,
                                TraitDictionaryMethodField(methodIndex = methodIndex, methodName = methodName, source = TraitDictionaryDefaultMethod, implementation = implementation) :: reversed
                            )
                        | None ->
                            TraitDictionaryMethodFieldPlanning(fields = reverse(reversed), error = Some(
                                TraitDictionaryConstructionMissingMethod(constraint)(methodName)
                            ))

let recursive traitConstructionNameExists name names =
    match names with
        | [] -> false
        | head :: tail ->
            if head == name
            then true
            else traitConstructionNameExists(name)(tail)

let recursive visitTraitMethodDependencies constraint traitName implementation methodName candidates allFields active completed reversedOrder =
    match candidates with
        | [] -> TraitDictionaryMethodOrderBuild(completed = completed, reversedOrder = reversedOrder, error = None)
        | (TraitDictionaryMethodField { methodIndex = _candidateIndex, methodName = candidateName, source = _candidateSource, implementation = _candidateImplementation } as candidate) :: tail ->
            if candidateName == methodName
            then
                visitTraitMethodDependencies(
                    constraint,
                    traitName,
                    implementation,
                    methodName,
                    tail,
                    allFields,
                    active,
                    completed,
                    reversedOrder
                )
            else
                if expressionDependsOnTraitMethod(traitName)(candidateName)(implementation)
                then
                    match visitTraitMethodField(
                        constraint,
                        traitName,
                        candidate,
                        allFields,
                        active,
                        completed,
                        reversedOrder
                    ) with
                        | TraitDictionaryMethodOrderBuild { completed = nextCompleted, reversedOrder = nextOrder, error = None } ->
                            visitTraitMethodDependencies(
                                constraint,
                                traitName,
                                implementation,
                                methodName,
                                tail,
                                allFields,
                                active,
                                nextCompleted,
                                nextOrder
                            )
                        | failure -> failure
                else
                    visitTraitMethodDependencies(
                        constraint,
                        traitName,
                        implementation,
                        methodName,
                        tail,
                        allFields,
                        active,
                        completed,
                        reversedOrder
                    )
and visitTraitMethodField constraint traitName field allFields active completed reversedOrder =
    match field with
        | TraitDictionaryMethodField { methodIndex = methodIndex, methodName = methodName, source = source, implementation = implementation } ->
            if traitConstructionNameExists(methodName)(completed)
            then TraitDictionaryMethodOrderBuild(completed = completed, reversedOrder = reversedOrder, error = None)
            else
                if traitConstructionNameExists(methodName)(active)
                then
                    TraitDictionaryMethodOrderBuild(completed = completed, reversedOrder = reversedOrder, error = Some(
                        TraitDictionaryConstructionMethodCycle(constraint)(methodName)
                    ))
                else
                    match visitTraitMethodDependencies(
                        constraint,
                        traitName,
                        implementation,
                        methodName,
                        allFields,
                        allFields,
                        methodName :: active,
                        completed,
                        reversedOrder
                    ) with
                        | TraitDictionaryMethodOrderBuild { completed = dependencyCompleted, reversedOrder = dependencyOrder, error = None } -> TraitDictionaryMethodOrderBuild(completed = methodName :: dependencyCompleted, reversedOrder = TraitDictionaryMethodField(methodIndex = methodIndex, methodName = methodName, source = source, implementation = implementation) :: dependencyOrder, error = None)
                        | failure -> failure

let recursive planTraitMethodConstructionOrderFrom constraint traitName remaining allFields completed reversedOrder =
    match remaining with
        | [] -> TraitDictionaryMethodOrderBuild(completed = completed, reversedOrder = reversedOrder, error = None)
        | head :: tail ->
            match visitTraitMethodField(constraint)(traitName)(head)(allFields)([])(completed)(reversedOrder) with
                | TraitDictionaryMethodOrderBuild { completed = nextCompleted, reversedOrder = nextOrder, error = None } ->
                    planTraitMethodConstructionOrderFrom(
                        constraint,
                        traitName,
                        tail,
                        allFields,
                        nextCompleted,
                        nextOrder
                    )
                | failure -> failure

let planTraitMethodConstructionOrder constraint traitName fields =
    planTraitMethodConstructionOrderFrom(
        constraint,
        traitName,
        fields,
        fields,
        [],
        []
    )

let finishTraitDictionaryInstancePlan constraint traitName fields requirements supertraits =
    match planTraitMethodConstructionOrder(constraint)(traitName)(fields) with
        | TraitDictionaryMethodOrderBuild { completed = _completed, reversedOrder = reversedOrder, error = None } ->
            TraitDictionaryConstructionPlanning(construction = Some(
                TraitDictionaryConstructionPlan(constraint = constraint, methods = fields, methodConstructionOrder = reverse(
                    reversedOrder
                ), requirements = requirements, supertraits = supertraits)
            ), error = None)
        | TraitDictionaryMethodOrderBuild { completed = _completed, reversedOrder = _reversedOrder, error = Some(error) } ->
            TraitDictionaryConstructionPlanning(construction = None, error = Some(
                error
            ))

let planTraitDictionaryInstance constraint implementation requirements supertraits environment =
    match constraint with
        | TraitConstraint { traitName = traitName, typeArguments = _typeArguments } ->
            match (resolveTraitBinding(traitName)(environment), implementation) with
                | (Some(TraitInferenceDefinition { name = _name, parameterCount = _parameterCount, parameters = _parameters, methods = methods, supertraits = _traitSupertraits, provenance = _provenance }), TraitImplementationInferenceDefinition { traitName = _implementationTraitName, typeArguments = _implementationTypeArguments, requirements = _implementationRequirements, methods = suppliedMethods }) ->
                    match planTraitDictionaryMethodFields(
                        constraint,
                        sortTraitConstructionMethods(methods),
                        suppliedMethods,
                        0,
                        []
                    ) with
                        | TraitDictionaryMethodFieldPlanning { fields = fields, error = None } ->
                            finishTraitDictionaryInstancePlan(
                                constraint,
                                traitName,
                                fields,
                                requirements,
                                supertraits
                            )
                        | TraitDictionaryMethodFieldPlanning { fields = _fields, error = Some(error) } ->
                            TraitDictionaryConstructionPlanning(construction = None, error = Some(
                                error
                            ))
                | _ ->
                    TraitDictionaryConstructionPlanning(construction = None, error = Some(
                        TraitDictionaryConstructionUnknownTrait(constraint)
                    ))

let planTraitDictionaryConstruction evidence environment =
    match evidence with
        | TraitEvidenceParameter(constraint) ->
            TraitDictionaryConstructionPlanning(construction = None, error = Some(
                TraitDictionaryConstructionRequiresParameter(constraint)
            ))
        | TraitEvidenceInstance(constraint, implementation, requirements, supertraits) ->
            planTraitDictionaryInstance(
                constraint,
                implementation,
                requirements,
                supertraits,
                environment
            )

let recursive traitDictionaryMethodExpressions traitName fields =
    match fields with
        | [] -> []
        | TraitDictionaryMethodField { methodIndex = _methodIndex, methodName = methodName, source = _source, implementation = _implementation } :: tail ->
            ExprVar(
                selectedTraitMethodBindingName(traitName)(methodName)
            ) :: traitDictionaryMethodExpressions(traitName)(tail)

let packTraitDictionaryExpressions fields =
    match fields with
        | field :: [] -> field
        | values -> ExprTuple(values)

let recursive bindTraitDictionaryMethods traitName constructionOrder body =
    match constructionOrder with
        | [] -> body
        | TraitDictionaryMethodField { methodIndex = _methodIndex, methodName = methodName, source = _source, implementation = implementation } :: tail ->
            ExprLet(
                selectedTraitMethodBindingName(traitName)(methodName),
                rewriteSelectedTraitMethodImplementation(traitName)(methodName)(implementation),
                bindTraitDictionaryMethods(traitName)(tail)(body),
                [],
                None,
                []
            )

let recursive rewriteTraitDictionarySupertraits evidence environment =
    match evidence with
        | [] -> TraitDictionaryExpressionListRewriting(expressions = [], error = None)
        | head :: tail ->
            match rewriteTraitDictionaryValue(head)(environment) with
                | TraitDictionaryValueRewriting { expression = None, error = error } -> TraitDictionaryExpressionListRewriting(expressions = [], error = error)
                | TraitDictionaryValueRewriting { expression = Some(headExpression), error = None } ->
                    match rewriteTraitDictionarySupertraits(tail)(environment) with
                        | TraitDictionaryExpressionListRewriting { expressions = _expressions, error = Some(error) } ->
                            TraitDictionaryExpressionListRewriting(expressions = [], error = Some(
                                error
                            ))
                        | TraitDictionaryExpressionListRewriting { expressions = tailExpressions, error = None } -> TraitDictionaryExpressionListRewriting(expressions = headExpression :: tailExpressions, error = None)
and rewriteTraitDictionaryConstruction construction environment =
    match construction with
        | TraitDictionaryConstructionPlan { constraint = TraitConstraint { traitName = traitName, typeArguments = _typeArguments }, methods = methods, methodConstructionOrder = constructionOrder, requirements = _requirements, supertraits = supertraits } ->
            match rewriteTraitDictionarySupertraits(supertraits)(environment) with
                | TraitDictionaryExpressionListRewriting { expressions = supertraitExpressions, error = None } ->
                    let dictionary =
                        packTraitDictionaryExpressions(
                            appendList(traitDictionaryMethodExpressions(traitName)(methods))(supertraitExpressions)
                        )
                    in
                        TraitDictionaryValueRewriting(expression = Some(
                            bindTraitDictionaryMethods(traitName)(constructionOrder)(dictionary)
                        ), error = None)
                | TraitDictionaryExpressionListRewriting { expressions = _expressions, error = error } -> TraitDictionaryValueRewriting(expression = None, error = error)
and rewriteTraitDictionaryValue evidence environment =
    match planTraitDictionaryConstruction(evidence)(environment) with
        | TraitDictionaryConstructionPlanning { construction = Some(construction), error = None } ->
            rewriteTraitDictionaryConstruction(
                construction,
                environment
            )
        | TraitDictionaryConstructionPlanning { construction = _construction, error = error } -> TraitDictionaryValueRewriting(expression = None, error = error)
