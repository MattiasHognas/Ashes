import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import Ashes.Collection.List.map
import Ashes.Collection.List.sortBy
import Ashes.Text.compare as compareText
export (
    type TraitDictionaryAbiShape(..),
    value planTraitEvidenceAbi,
)

type TraitDictionaryAbiShape =
    | parameterIndex: Int
    | constraint: TraitConstraint
    | methods: List(Str)
    | supertraits: List(TraitDictionaryAbiShape)
    deriving {Eq, Show}

type TraitDictionaryShapeBuild =
    | shape: Maybe(TraitDictionaryAbiShape)
    | includedTraits: List(Str)

type TraitDictionaryShapeListBuild =
    | shapes: List(TraitDictionaryAbiShape)
    | includedTraits: List(Str)

let recursive containsTraitName name names =
    match names with
        | [] -> false
        | head :: tail ->
            if name == head
            then true
            else containsTraitName(name)(tail)

let recursive findEvidenceSubstitution parameterId substitutions =
    match substitutions with
        | [] -> None
        | (candidateId, replacement) :: tail ->
            if parameterId == candidateId
            then Some(replacement)
            else findEvidenceSubstitution(parameterId)(tail)

let recursive substituteEvidenceTypes substitutions values =
    match values with
        | [] -> []
        | head :: tail -> substituteEvidenceType(substitutions)(head) :: substituteEvidenceTypes(substitutions)(tail)
and substituteEvidenceType substitutions semanticType =
    match semanticType with
        | SemParameter(parameterId, _name) ->
            match findEvidenceSubstitution(parameterId)(substitutions) with
                | Some(replacement) -> replacement
                | None -> semanticType
        | SemVariable(parameterId) ->
            match findEvidenceSubstitution(parameterId)(substitutions) with
                | Some(replacement) -> replacement
                | None -> semanticType
        | SemFunction(argument, result, row) ->
            let nextRow =
                match row with
                    | None -> None
                    | Some(value) ->
                        value
                        |> substituteEvidenceType(substitutions)
                        |> Some
            in
                SemFunction(substituteEvidenceType(substitutions)(argument))(substituteEvidenceType(substitutions)(result))(nextRow)
        | SemList(element) ->
            element
            |> substituteEvidenceType(substitutions)
            |> SemList
        | SemTuple(elements) ->
            elements
            |> substituteEvidenceTypes(substitutions)
            |> SemTuple
        | SemNamed(symbolId, name, arguments) ->
            arguments
            |> substituteEvidenceTypes(substitutions)
            |> SemNamed(symbolId)(name)
        | SemPointer(pointee) ->
            pointee
            |> substituteEvidenceType(substitutions)
            |> SemPointer
        | SemCapability(name, arguments) ->
            arguments
            |> substituteEvidenceTypes(substitutions)
            |> SemCapability(name)
        | SemRow(capabilities, tail) ->
            let nextTail =
                match tail with
                    | None -> None
                    | Some(value) ->
                        value
                        |> substituteEvidenceType(substitutions)
                        |> Some
            in
                SemRow(substituteEvidenceTypes(substitutions)(capabilities))(nextTail)
        | _ -> semanticType

let substituteEvidenceConstraint substitutions constraint =
    match constraint with
        | TraitConstraint { traitName = traitName, typeArguments = typeArguments } -> TraitConstraint(traitName = traitName, typeArguments = substituteEvidenceTypes(substitutions)(typeArguments))

let recursive buildEvidenceSubstitutions parameters arguments =
    match (parameters, arguments) with
        | (SemParameter(parameterId, _name) :: parameterTail, argument :: argumentTail) -> (parameterId, argument) :: buildEvidenceSubstitutions(parameterTail)(argumentTail)
        | (SemVariable(parameterId) :: parameterTail, argument :: argumentTail) -> (parameterId, argument) :: buildEvidenceSubstitutions(parameterTail)(argumentTail)
        | _ -> []

let traitMethodName method =
    match method with
        | TraitMethodInferenceDefinition { name = name, scheme = _scheme, defaultImplementation = _defaultImplementation } -> name

let recursive traitMethodNames methods =
    match methods with
        | [] -> []
        | head :: tail -> traitMethodName(head) :: traitMethodNames(tail)

let sortTraitMethodNames methods =
    methods
    |> traitMethodNames
    |> sortBy(given (left) ->
        given (right) -> compareText(left)(right) <= 0)

let recursive buildSupertraitShapes constraints environment parameterIndex includedTraits =
    match constraints with
        | [] -> TraitDictionaryShapeListBuild(shapes = [], includedTraits = includedTraits)
        | (TraitConstraint { traitName = traitName, typeArguments = _typeArguments } as constraint) :: tail ->
            if containsTraitName(traitName)(includedTraits)
            then buildSupertraitShapes(tail)(environment)(parameterIndex)(includedTraits)
            else
                match buildTraitDictionaryShape(constraint)(environment)(parameterIndex)(traitName :: includedTraits) with
                    | TraitDictionaryShapeBuild { shape = None, includedTraits = nextIncluded } -> buildSupertraitShapes(tail)(environment)(parameterIndex)(nextIncluded)
                    | TraitDictionaryShapeBuild { shape = Some(shape), includedTraits = nextIncluded } ->
                        match buildSupertraitShapes(tail)(environment)(parameterIndex)(nextIncluded) with
                            | TraitDictionaryShapeListBuild { shapes = tailShapes, includedTraits = finalIncluded } -> TraitDictionaryShapeListBuild(shapes = shape :: tailShapes, includedTraits = finalIncluded)
and buildTraitDictionaryShape constraint environment parameterIndex includedTraits =
    match constraint with
        | TraitConstraint { traitName = traitName, typeArguments = typeArguments } ->
            match resolveTraitBinding(traitName)(environment) with
                | None -> TraitDictionaryShapeBuild(shape = None, includedTraits = includedTraits)
                | Some(TraitInferenceDefinition { name = _name, parameterCount = _parameterCount, parameters = parameters, methods = methods, supertraits = supertraits, provenance = _provenance }) ->
                    let substitutions = buildEvidenceSubstitutions(parameters)(typeArguments)
                    in
                        let specializedSupertraits =
                            supertraits
                            |> map(substituteEvidenceConstraint(substitutions))
                            |> canonicalizeTraitConstraints
                        in
                            match buildSupertraitShapes(specializedSupertraits)(environment)(parameterIndex)(includedTraits) with
                                | TraitDictionaryShapeListBuild { shapes = shapes, includedTraits = finalIncluded } -> TraitDictionaryShapeBuild(shape = Some(TraitDictionaryAbiShape(parameterIndex = parameterIndex, constraint = constraint, methods = sortTraitMethodNames(methods), supertraits = shapes)), includedTraits = finalIncluded)

let recursive planTraitEvidenceAbiFrom constraints environment parameterIndex =
    match constraints with
        | [] -> []
        | (TraitConstraint { traitName = traitName, typeArguments = _typeArguments } as constraint) :: tail ->
            match buildTraitDictionaryShape(constraint)(environment)(parameterIndex)([traitName]) with
                | TraitDictionaryShapeBuild { shape = None, includedTraits = _includedTraits } -> planTraitEvidenceAbiFrom(tail)(environment)(parameterIndex)
                | TraitDictionaryShapeBuild { shape = Some(shape), includedTraits = _includedTraits } -> shape :: planTraitEvidenceAbiFrom(tail)(environment)(parameterIndex + 1)

let planTraitEvidenceAbi constraints environment =
    planTraitEvidenceAbiFrom(canonicalizeTraitConstraints(constraints))(environment)(0)
