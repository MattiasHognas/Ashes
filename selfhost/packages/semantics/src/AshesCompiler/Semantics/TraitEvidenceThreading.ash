import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.TraitResolution
import AshesCompiler.Semantics.TraitEvidenceAbi
import Ashes.Collection.List.reverse
export (
    type TraitEvidenceArgument(..),
    type TraitEvidenceArgumentPlanning(..),
    type TraitFunctionApplicationError(..),
    type TraitFunctionApplicationPlanning(..),
    value planTraitEvidenceArguments,
    value planTraitFunctionApplication,
)

type TraitEvidenceArgument =
    | shape: TraitDictionaryAbiShape
    | evidence: TraitEvidencePlan

type TraitEvidenceArgumentPlanning =
    | arguments: List(TraitEvidenceArgument)
    | error: Maybe(TraitResolutionError)

type TraitFunctionApplicationError =
    | TraitFunctionEvidenceResolution(TraitResolutionError)
    | TraitFunctionOverapplication(Int, Int)
    | TraitFunctionInvalidArgumentCount(Int)
    deriving {Eq, Show}

type TraitFunctionApplicationPlanning =
    | evidenceArguments: List(TraitEvidenceArgument)
    | ordinaryArity: Int
    | suppliedOrdinaryArguments: Int
    | remainingOrdinaryArguments: Int
    | capturesEvidence: Bool
    | error: Maybe(TraitFunctionApplicationError)

let recursive planTraitEvidenceArgumentsFrom shapes environment reversed =
    match shapes with
        | [] -> TraitEvidenceArgumentPlanning(arguments = reverse(reversed), error = None)
        | (TraitDictionaryAbiShape { parameterIndex = _parameterIndex, constraint = constraint, methods = _methods, supertraits = _supertraits } as shape) :: tail ->
            match resolveTraitEvidence(constraint)(environment) with
                | TraitEvidenceResolution { plan = Some(evidence), error = None } -> planTraitEvidenceArgumentsFrom(tail)(environment)(TraitEvidenceArgument(shape = shape, evidence = evidence) :: reversed)
                | TraitEvidenceResolution { plan = _plan, error = Some(error) } -> TraitEvidenceArgumentPlanning(arguments = reverse(reversed), error = Some(error))
                | _ -> TraitEvidenceArgumentPlanning(arguments = reverse(reversed), error = Some(MissingTraitImplementation(constraint)([constraint])))

let planTraitEvidenceArguments constraints environment = planTraitEvidenceArgumentsFrom(planTraitEvidenceAbi(constraints)(environment))(environment)([])

let recursive traitFunctionOrdinaryArity semanticType =
    match semanticType with
        | SemFunction(_argument, result, _capabilityRow) -> 1 + traitFunctionOrdinaryArity(result)
        | _ -> 0

let traitEvidenceArgumentsPresent arguments =
    match arguments with
        | [] -> false
        | _head :: _tail -> true

let traitFunctionApplicationFailure ordinaryArity supplied error = TraitFunctionApplicationPlanning(evidenceArguments = [], ordinaryArity = ordinaryArity, suppliedOrdinaryArguments = supplied, remainingOrdinaryArguments = 0, capturesEvidence = false, error = Some(error))

let traitFunctionCapturesEvidence remaining arguments =
    if remaining > 0
    then traitEvidenceArgumentsPresent(arguments)
    else false

let planResolvedTraitFunctionApplication ordinaryArity suppliedOrdinaryArguments arguments = TraitFunctionApplicationPlanning(evidenceArguments = arguments, ordinaryArity = ordinaryArity, suppliedOrdinaryArguments = suppliedOrdinaryArguments, remainingOrdinaryArguments = ordinaryArity - suppliedOrdinaryArguments, capturesEvidence = traitFunctionCapturesEvidence(ordinaryArity - suppliedOrdinaryArguments)(arguments), error = None)

let planTraitFunctionApplicationWithArity ordinaryArity suppliedOrdinaryArguments constraints environment =
    if suppliedOrdinaryArguments < 0
    then traitFunctionApplicationFailure(ordinaryArity)(suppliedOrdinaryArguments)(TraitFunctionInvalidArgumentCount(suppliedOrdinaryArguments))
    else
        if suppliedOrdinaryArguments > ordinaryArity
        then traitFunctionApplicationFailure(ordinaryArity)(suppliedOrdinaryArguments)(TraitFunctionOverapplication(ordinaryArity)(suppliedOrdinaryArguments))
        else
            match planTraitEvidenceArguments(constraints)(environment) with
                | TraitEvidenceArgumentPlanning { arguments = arguments, error = Some(error) } -> TraitFunctionApplicationPlanning(evidenceArguments = arguments, ordinaryArity = ordinaryArity, suppliedOrdinaryArguments = suppliedOrdinaryArguments, remainingOrdinaryArguments = ordinaryArity - suppliedOrdinaryArguments, capturesEvidence = false, error = Some(TraitFunctionEvidenceResolution(error)))
                | TraitEvidenceArgumentPlanning { arguments = arguments, error = None } -> planResolvedTraitFunctionApplication(ordinaryArity)(suppliedOrdinaryArguments)(arguments)

let planTraitFunctionApplication scheme suppliedOrdinaryArguments environment =
    match scheme with
        | TypeScheme { quantified = _quantified, body = body, constraints = constraints } -> planTraitFunctionApplicationWithArity(traitFunctionOrdinaryArity(body))(suppliedOrdinaryArguments)(constraints)(environment)
