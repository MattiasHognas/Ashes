// Planning for ordinary constrained bindings. The source scheme stays independent
// of the hidden dictionary parameters used by the emitted closure.
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.TypeSchemes
import AshesCompiler.Semantics.TraitEvidenceAbi
import AshesCompiler.Semantics.TraitEvidenceRewriting.traitMethodParameterName
import Ashes.Collection.List.append
import Ashes.Collection.List.length
export (
    type CoreConstrainedInference(..),
    type CoreConstrainedLayout(..),
    value inferCoreConstrainedBinding,
    value coreConstrainedLayout,
    value coreConstrainedPhysicalType,
    value dictionaryMethodType,
    value coreConstrainedShapes,
)

type CoreConstrainedInference =
    | scheme: TypeScheme
    | boundaryRequirements: List(TraitConstraint)
    | substitution: List((Int, SemanticType))
    | supply: TypeVariableSupply
    | error: Maybe(TypeInferenceError)

type CoreConstrainedLayout =
    | parameterTypes: List(SemanticType)
    | methods: List((Str, SemanticType, Str, Str))
    | error: Maybe(Str)

let recursive containsQuantified variable quantified =
    match quantified with
        | [] -> false
        | (candidate, _name) :: rest -> candidate == variable || containsQuantified(variable)(rest)

let recursive allQuantified variables quantified =
    match variables with
        | [] -> true
        | variable :: rest -> containsQuantified(variable)(quantified) && allQuantified(rest)(quantified)

// Requirements fixed by an outer binding belong to that scope. A concrete
// requirement is also checked at that scope, rather than becoming a dictionary
// parameter of an unrelated local value.
let generalizedRequirement arguments quantified =
    match freeTypeVariables(SemTuple(arguments)) with
        | [] -> false
        | variables -> allQuantified(variables)(quantified)

let recursive selectRequirements generalized constraints quantified =
    match constraints with
        | [] -> []
        | (TraitConstraint { typeArguments = arguments } as constraint) :: rest ->
            if generalizedRequirement(arguments)(quantified) == generalized
            then constraint :: selectRequirements(generalized)(rest)(quantified)
            else selectRequirements(generalized)(rest)(quantified)

// A recursive binding must be inferred monomorphically inside its own body, and
// generalized only outside that self environment. Inferring a synthetic let keeps
// that rule in the existing inference engine instead of duplicating it here.
// The caller merges returned substitutions affecting outer variables and advances
// its supply when it adopts this plan; unsupported inference can retain its
// original lowering path without adopting any speculative state.
let inferCoreConstrainedBinding name isRecursive value annotation requirements environment supply =
    (let expression =
        if isRecursive
        then ExprLetRecursive(name)(value)(ExprVar(name))([])(annotation)(requirements)
        else ExprLet(name)(value)(ExprVar(name))([])(annotation)(requirements)
    in
        match inferExpressionFrom(expression)(environment)([])(supply) with
            | TypeInferenceResult { semanticType = semanticType, substitution = substitution, supply = nextSupply, constraints = constraints, error = error } ->
                let resolved = applySubstitution(substitution)(semanticType)
                in
                    let scheme =
                        constraints
                        |> applyInferenceConstraints(substitution)
                        |> generalizeUnderSubstitution(inferenceEnvironmentSchemes(environment))(substitution)(resolved)
                    in CoreConstrainedInference(scheme = (scheme with constraints = selectRequirements(true)(scheme.constraints)(scheme.quantified)), boundaryRequirements = selectRequirements(false)(scheme.constraints)(scheme.quantified), substitution = substitution, supply = nextSupply, error = error))

let recursive traitParameterSubstitution parameters arguments =
    match (parameters, arguments) with
        | ([], []) -> Some([])
        | (SemParameter(id, _name) :: restParameters, argument :: restArguments) ->
            match traitParameterSubstitution(restParameters)(restArguments) with
                | Some(rest) -> Some((id, argument) :: rest)
                | None -> None
        | (SemVariable(id) :: restParameters, argument :: restArguments) ->
            match traitParameterSubstitution(restParameters)(restArguments) with
                | Some(rest) -> Some((id, argument) :: rest)
                | None -> None
        | _ -> None

let dictionaryMethodType traitName methodName arguments environment =
    match (resolveTraitBinding(traitName)(environment), resolveTraitMethod(traitName)(methodName)(environment)) with
        | (Some(TraitInferenceDefinition { parameters = parameters }), Some(TraitMethodInferenceDefinition { scheme = TypeScheme { body = body } })) ->
            match traitParameterSubstitution(parameters)(arguments) with
                | Some(substitution) ->
                    body
                    |> substituteEvidenceType(substitution)
                    |> Some
                | None -> None
        | _ -> None

let failedLayout name = CoreConstrainedLayout(parameterTypes = [], methods = [], error = Some(name))

let recursive methodLayout index path traitName arguments methods environment =
    match methods with
        | [] -> CoreConstrainedLayout(parameterTypes = [], methods = [], error = None)
        | methodName :: rest ->
            match (arguments, dictionaryMethodType(traitName)(methodName)(arguments)(environment)) with
                | (argument :: [], Some(methodType)) ->
                    match methodLayout(index)(path)(traitName)(arguments)(rest)(environment) with
                        | CoreConstrainedLayout { parameterTypes = restTypes, methods = restMethods, error = None } -> CoreConstrainedLayout(parameterTypes = methodType :: restTypes, methods = (traitName, argument, methodName, traitMethodParameterName(index)(path)(traitName)(methodName)) :: restMethods, error = None)
                        | failed -> failed
                | _ -> failedLayout(traitName + "." + methodName)

let dictionaryType fields =
    match fields with
        | field :: [] -> field
        | _ -> SemTuple(fields)

// Both types and active method names follow the existing canonical ABI, including
// supertraits. Same-trait dictionaries at distinct types retain separate entries;
// Core's typed active-method lookup selects one by its operand type.
let recursive shapeLayout path shape environment =
    match shape with
        | TraitDictionaryAbiShape { parameterIndex = index, constraint = TraitConstraint { traitName = traitName, typeArguments = arguments }, methods = methods, supertraits = supertraits } ->
            match methodLayout(index)(path)(traitName)(arguments)(methods)(environment) with
                | CoreConstrainedLayout { parameterTypes = methodTypes, methods = methodEntries, error = None } ->
                    match supertraitLayout(path)(0)(supertraits)(environment) with
                        | CoreConstrainedLayout { parameterTypes = superTypes, methods = superEntries, error = None } ->
                            CoreConstrainedLayout(parameterTypes = [superTypes
                            |> append(methodTypes)
                            |> dictionaryType], methods = append(methodEntries)(superEntries), error = None)
                        | failed -> failed
                | failed -> failed
and supertraitLayout path ordinal shapes environment =
    match shapes with
        | [] -> CoreConstrainedLayout(parameterTypes = [], methods = [], error = None)
        | shape :: rest ->
            match shapeLayout(path + "_" + Ashes.Text.fromInt(ordinal))(shape)(environment) with
                | CoreConstrainedLayout { parameterTypes = types, methods = methods, error = None } ->
                    match supertraitLayout(path)(ordinal + 1)(rest)(environment) with
                        | CoreConstrainedLayout { parameterTypes = restTypes, methods = restMethods, error = None } -> CoreConstrainedLayout(parameterTypes = append(types)(restTypes), methods = append(methods)(restMethods), error = None)
                        | failed -> failed
                | failed -> failed

let recursive plannedLayout shapes environment =
    match shapes with
        | [] -> CoreConstrainedLayout(parameterTypes = [], methods = [], error = None)
        | shape :: rest ->
            match shapeLayout("root")(shape)(environment) with
                | CoreConstrainedLayout { parameterTypes = types, methods = methods, error = None } ->
                    match plannedLayout(rest)(environment) with
                        | CoreConstrainedLayout { parameterTypes = restTypes, methods = restMethods, error = None } -> CoreConstrainedLayout(parameterTypes = append(types)(restTypes), methods = append(methods)(restMethods), error = None)
                        | failed -> failed
                | failed -> failed

// The source scheme defines physical slot order. Concrete instantiation may make
// formerly distinct requirements identical, but both slots must still be supplied.
let recursive indexedConstrainedShape index (shape: TraitDictionaryAbiShape) =
    shape with parameterIndex = index, supertraits = Ashes.Collection.List.map(indexedConstrainedShape(index))(shape.supertraits)

let recursive constrainedShapesFrom constraints environment index =
    match constraints with
        | [] -> []
        | constraint :: rest ->
            match planTraitEvidenceAbi([constraint])(environment) with
                | shape :: [] -> indexedConstrainedShape(index)(shape) :: constrainedShapesFrom(rest)(environment)(index + 1)
                | _ -> []

let coreConstrainedShapes constraints environment = constrainedShapesFrom(constraints)(environment)(0)

let coreConstrainedLayout constraints environment =
    (let shapes = coreConstrainedShapes(constraints)(environment)
    in
        if length(shapes) == length(constraints)
        then plannedLayout(shapes)(environment)
        else failedLayout("unresolved trait dictionary layout"))

let recursive coreConstrainedPhysicalType parameters result =
    match parameters with
        | [] -> result
        | parameter :: rest ->
            SemFunction(parameter)(coreConstrainedPhysicalType(rest)(result))(None)
