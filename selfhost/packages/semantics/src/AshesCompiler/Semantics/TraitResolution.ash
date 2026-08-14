import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import Ashes.Collection.List.reverse
export (
    type TraitEvidencePlan(..),
    type TraitResolutionError(..),
    type TraitEvidenceResolution(..),
    value resolveTraitEvidence,
)

type TraitEvidencePlan =
    | TraitEvidenceParameter(TraitConstraint)
    | TraitEvidenceInstance(TraitConstraint, TraitImplementationInferenceDefinition, List(TraitEvidencePlan), List(TraitEvidencePlan))

type TraitResolutionError =
    | MissingTraitImplementation(TraitConstraint)
    | AmbiguousTraitImplementation(TraitConstraint)
    | CyclicTraitResolution(List(TraitConstraint))
    | TraitResolutionDepthExceeded(TraitConstraint, Int)
    | NonDecreasingTraitResolutionRequirement(TraitConstraint)
    deriving {Eq, Show}

type TraitEvidenceResolution =
    | plan: Maybe(TraitEvidencePlan)
    | error: Maybe(TraitResolutionError)

type TraitHeadMatch =
    | substitutions: List((Int, SemanticType))
    | matched: Bool

type TraitImplementationMatch =
    | implementation: TraitImplementationInferenceDefinition
    | substitutions: List((Int, SemanticType))

type TraitDependencyResolution =
    | plans: List(TraitEvidencePlan)
    | error: Maybe(TraitResolutionError)

let evidenceSuccess plan = TraitEvidenceResolution(plan = Some(plan), error = None)

let evidenceFailure error = TraitEvidenceResolution(plan = None, error = Some(error))

let nextResolutionDepth : Int -> Int =
    given (depth) -> depth + 1

let resolutionDepthExceeded : Int -> Bool =
    given (depth) -> depth >= 64

let recursive findHeadSubstitution parameterId substitutions =
    match substitutions with
        | [] -> None
        | (candidateId, replacement) :: tail ->
            if parameterId == candidateId
            then Some(replacement)
            else findHeadSubstitution(parameterId)(tail)

let recursive matchTraitHeadTypes heads goals substitutions =
    match (heads, goals) with
        | ([], []) -> TraitHeadMatch(substitutions = substitutions, matched = true)
        | (head :: headTail, goal :: goalTail) ->
            match matchTraitHeadType(head)(goal)(substitutions) with
                | TraitHeadMatch { substitutions = nextSubstitutions, matched = true } -> matchTraitHeadTypes(headTail)(goalTail)(nextSubstitutions)
                | failure -> failure
        | _ -> TraitHeadMatch(substitutions = substitutions, matched = false)
and matchTraitHeadType head goal substitutions =
    match head with
        | SemParameter(parameterId, _name) ->
            match findHeadSubstitution(parameterId)(substitutions) with
                | None -> TraitHeadMatch(substitutions = (parameterId, goal) :: substitutions, matched = true)
                | Some(existing) -> TraitHeadMatch(substitutions = substitutions, matched = existing == goal)
        | SemVariable(parameterId) ->
            match findHeadSubstitution(parameterId)(substitutions) with
                | None -> TraitHeadMatch(substitutions = (parameterId, goal) :: substitutions, matched = true)
                | Some(existing) -> TraitHeadMatch(substitutions = substitutions, matched = existing == goal)
        | _ ->
            match goal with
                | SemVariable(_) -> TraitHeadMatch(substitutions = substitutions, matched = false)
                | SemParameter(_, _) -> TraitHeadMatch(substitutions = substitutions, matched = false)
                | _ -> matchRigidTraitHeadType(head)(goal)(substitutions)
and matchRigidTraitHeadType head goal substitutions =
    match (head, goal) with
        | (SemInt, SemInt) -> TraitHeadMatch(substitutions = substitutions, matched = true)
        | (SemUInt(left), SemUInt(right)) -> TraitHeadMatch(substitutions = substitutions, matched = left == right)
        | (SemFloat, SemFloat) -> TraitHeadMatch(substitutions = substitutions, matched = true)
        | (SemBigInt, SemBigInt) -> TraitHeadMatch(substitutions = substitutions, matched = true)
        | (SemString, SemString) -> TraitHeadMatch(substitutions = substitutions, matched = true)
        | (SemRune, SemRune) -> TraitHeadMatch(substitutions = substitutions, matched = true)
        | (SemBytes, SemBytes) -> TraitHeadMatch(substitutions = substitutions, matched = true)
        | (SemBool, SemBool) -> TraitHeadMatch(substitutions = substitutions, matched = true)
        | (SemNever, SemNever) -> TraitHeadMatch(substitutions = substitutions, matched = true)
        | (SemList(left), SemList(right)) -> matchTraitHeadType(left)(right)(substitutions)
        | (SemTuple(left), SemTuple(right)) -> matchTraitHeadTypes(left)(right)(substitutions)
        | (SemNamed(_leftId, leftName, leftArguments), SemNamed(_rightId, rightName, rightArguments)) ->
            if leftName == rightName
            then matchTraitHeadTypes(leftArguments)(rightArguments)(substitutions)
            else TraitHeadMatch(substitutions = substitutions, matched = false)
        | (SemPointer(left), SemPointer(right)) -> matchTraitHeadType(left)(right)(substitutions)
        | (SemOpaque(left), SemOpaque(right)) -> TraitHeadMatch(substitutions = substitutions, matched = left == right)
        | _ -> TraitHeadMatch(substitutions = substitutions, matched = false)

let recursive findTraitImplementationMatches implementations goalArguments reversed =
    match implementations with
        | [] -> reverse(reversed)
        | implementation :: tail ->
            match implementation with
                | TraitImplementationInferenceDefinition { traitName = _traitName, typeArguments = headArguments, requirements = _requirements, methods = _methods } ->
                    match matchTraitHeadTypes(headArguments)(goalArguments)([]) with
                        | TraitHeadMatch { substitutions = substitutions, matched = true } -> findTraitImplementationMatches(tail)(goalArguments)(TraitImplementationMatch(implementation = implementation, substitutions = substitutions) :: reversed)
                        | _ -> findTraitImplementationMatches(tail)(goalArguments)(reversed)

let recursive substituteTraitParameterTypes substitutions values =
    match values with
        | [] -> []
        | head :: tail -> substituteTraitParameters(substitutions)(head) :: substituteTraitParameterTypes(substitutions)(tail)
and substituteTraitParameters substitutions semanticType =
    match semanticType with
        | SemParameter(parameterId, _name) ->
            match findHeadSubstitution(parameterId)(substitutions) with
                | None -> semanticType
                | Some(replacement) -> replacement
        | SemVariable(parameterId) ->
            match findHeadSubstitution(parameterId)(substitutions) with
                | None -> semanticType
                | Some(replacement) -> replacement
        | SemList(element) -> SemList(substituteTraitParameters(substitutions)(element))
        | SemTuple(elements) -> SemTuple(substituteTraitParameterTypes(substitutions)(elements))
        | SemFunction(argument, result, row) ->
            let substitutedRow =
                match row with
                    | None -> None
                    | Some(value) -> Some(substituteTraitParameters(substitutions)(value))
            in SemFunction(substituteTraitParameters(substitutions)(argument))(substituteTraitParameters(substitutions)(result))(substitutedRow)
        | SemCapability(name, arguments) -> SemCapability(name)(substituteTraitParameterTypes(substitutions)(arguments))
        | SemRow(capabilities, tail) ->
            let substitutedTail =
                match tail with
                    | None -> None
                    | Some(value) -> Some(substituteTraitParameters(substitutions)(value))
            in SemRow(substituteTraitParameterTypes(substitutions)(capabilities))(substitutedTail)
        | SemNamed(symbolId, name, arguments) -> SemNamed(symbolId)(name)(substituteTraitParameterTypes(substitutions)(arguments))
        | SemPointer(pointee) -> SemPointer(substituteTraitParameters(substitutions)(pointee))
        | _ -> semanticType

let substituteTraitConstraint substitutions constraint =
    match constraint with
        | TraitConstraint { traitName = traitName, typeArguments = typeArguments } -> TraitConstraint(traitName = traitName, typeArguments = substituteTraitParameterTypes(substitutions)(typeArguments))

let recursive substituteTraitConstraints substitutions constraints =
    match constraints with
        | [] -> []
        | head :: tail -> substituteTraitConstraint(substitutions)(head) :: substituteTraitConstraints(substitutions)(tail)

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
                        | Some(value) -> semanticTypeIsConcrete(value)
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
                    | Some(value) -> semanticTypeIsConcrete(value)
            else false
        | _ -> true

let traitConstraintIsConcrete constraint =
    match constraint with
        | TraitConstraint { traitName = _traitName, typeArguments = typeArguments } -> semanticTypesAreConcrete(typeArguments)

let recursive semanticTypesStructuralSize : List(SemanticType) -> Int =
    given (values) ->
        match values with
            | [] -> 0
            | head :: tail -> semanticTypeStructuralSize(head) + semanticTypesStructuralSize(tail)
and semanticTypeStructuralSize : SemanticType -> Int =
    given (semanticType) ->
        match semanticType with
            | SemVariable(_) -> 0
            | SemParameter(_, _) -> 0
            | SemFunction(argument, result, _row) -> 1 + semanticTypeStructuralSize(argument) + semanticTypeStructuralSize(result)
            | SemList(element) -> 1 + semanticTypeStructuralSize(element)
            | SemTuple(elements) -> 1 + semanticTypesStructuralSize(elements)
            | SemNamed(_symbolId, _name, arguments) -> 1 + semanticTypesStructuralSize(arguments)
            | SemPointer(pointee) -> 1 + semanticTypeStructuralSize(pointee)
            | SemCapability(_name, arguments) -> 1 + semanticTypesStructuralSize(arguments)
            | SemRow(capabilities, tail) ->
                let tailSize =
                    match tail with
                        | None -> 0
                        | Some(value) -> semanticTypeStructuralSize(value)
                in 1 + semanticTypesStructuralSize(capabilities) + tailSize
            | _ -> 1

let traitConstraintStructuralSize : TraitConstraint -> Int =
    given (constraint) ->
        match constraint with
            | TraitConstraint { traitName = _traitName, typeArguments = typeArguments } -> 1 + semanticTypesStructuralSize(typeArguments)

let recursive traceContains constraint trace =
    match trace with
        | [] -> false
        | head :: tail ->
            if traitConstraintStableKey(head) == traitConstraintStableKey(constraint)
            then true
            else traceContains(constraint)(tail)

let recursive firstNonDecreasingRequirement goalSize requirements =
    match requirements with
        | [] -> None
        | head :: tail ->
            if traitConstraintStructuralSize(head) >= goalSize
            then Some(head)
            else firstNonDecreasingRequirement(goalSize)(tail)

let traitSupertraits goal environment =
    match goal with
        | TraitConstraint { traitName = traitName, typeArguments = typeArguments } ->
            match resolveTraitBinding(traitName)(environment) with
                | None -> []
                | Some(TraitInferenceDefinition { name = _name, parameterCount = _parameterCount, parameters = parameters, methods = _methods, supertraits = supertraits }) ->
                    match matchTraitHeadTypes(parameters)(typeArguments)([]) with
                        | TraitHeadMatch { substitutions = substitutions, matched = true } -> substituteTraitConstraints(substitutions)(supertraits)
                        | _ -> []

let recursive resolveTraitDependencies dependencies environment trace depth reversed =
    match dependencies with
        | [] -> TraitDependencyResolution(plans = reverse(reversed), error = None)
        | head :: tail ->
            match resolveTraitEvidenceFrom(head)(environment)(trace)(depth) with
                | TraitEvidenceResolution { plan = Some(plan), error = None } -> resolveTraitDependencies(tail)(environment)(trace)(depth)(plan :: reversed)
                | TraitEvidenceResolution { plan = _plan, error = Some(error) } -> TraitDependencyResolution(plans = reverse(reversed), error = Some(error))
                | _ -> TraitDependencyResolution(plans = reverse(reversed), error = Some(MissingTraitImplementation(head)))
and resolveMatchedTraitEvidence goal implementation substitutions environment trace depth =
    match implementation with
        | TraitImplementationInferenceDefinition { traitName = _traitName, typeArguments = _typeArguments, requirements = requirements, methods = _methods } ->
            let resolvedRequirements = substituteTraitConstraints(substitutions)(requirements)
            in
                let nonDecreasing =
                    if traitConstraintIsConcrete(goal)
                    then None
                    else firstNonDecreasingRequirement(traitConstraintStructuralSize(goal))(resolvedRequirements)
                in
                    match nonDecreasing with
                        | Some(requirement) -> evidenceFailure(NonDecreasingTraitResolutionRequirement(requirement))
                        | None ->
                            match resolveTraitDependencies(resolvedRequirements)(environment)(trace)(nextResolutionDepth(depth))([]) with
                                | TraitDependencyResolution { plans = _plans, error = Some(error) } -> evidenceFailure(error)
                                | TraitDependencyResolution { plans = requirementPlans, error = None } ->
                                    match resolveTraitDependencies(traitSupertraits(goal)(environment))(environment)(trace)(nextResolutionDepth(depth))([]) with
                                        | TraitDependencyResolution { plans = _plans, error = Some(error) } -> evidenceFailure(error)
                                        | TraitDependencyResolution { plans = supertraitPlans, error = None } -> evidenceSuccess(TraitEvidenceInstance(goal)(implementation)(requirementPlans)(supertraitPlans))
and resolveTraitEvidenceFrom goal environment trace depth =
    if resolutionDepthExceeded(depth)
    then evidenceFailure(TraitResolutionDepthExceeded(goal)(64))
    else
        if traceContains(goal)(trace)
        then evidenceFailure(CyclicTraitResolution(reverse(goal :: trace)))
        else
            match goal with
                | TraitConstraint { traitName = traitName, typeArguments = typeArguments } ->
                    let matches = findTraitImplementationMatches(resolveTraitImplementations(traitName)(environment))(typeArguments)([])
                    in
                        match matches with
                            | [] ->
                                if traitConstraintIsConcrete(goal)
                                then evidenceFailure(MissingTraitImplementation(goal))
                                else evidenceSuccess(TraitEvidenceParameter(goal))
                            | TraitImplementationMatch { implementation = implementation, substitutions = substitutions } :: [] -> resolveMatchedTraitEvidence(goal)(implementation)(substitutions)(environment)(goal :: trace)(depth)
                            | _ -> evidenceFailure(AmbiguousTraitImplementation(goal))

let resolveTraitEvidence goal environment = resolveTraitEvidenceFrom(goal)(environment)([])(0)
