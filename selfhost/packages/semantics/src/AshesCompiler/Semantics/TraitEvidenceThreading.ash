import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.TraitResolution
import AshesCompiler.Semantics.TraitEvidenceAbi
import Ashes.Collection.List.reverse
export (
    type TraitEvidenceArgument(..),
    type TraitEvidenceArgumentPlanning(..),
    value planTraitEvidenceArguments,
)

type TraitEvidenceArgument =
    | shape: TraitDictionaryAbiShape
    | evidence: TraitEvidencePlan

type TraitEvidenceArgumentPlanning =
    | arguments: List(TraitEvidenceArgument)
    | error: Maybe(TraitResolutionError)

let recursive planTraitEvidenceArgumentsFrom shapes environment reversed =
    match shapes with
        | [] -> TraitEvidenceArgumentPlanning(arguments = reverse(reversed), error = None)
        | (TraitDictionaryAbiShape { parameterIndex = _parameterIndex, constraint = constraint, methods = _methods, supertraits = _supertraits } as shape) :: tail ->
            match resolveTraitEvidence(constraint)(environment) with
                | TraitEvidenceResolution { plan = Some(evidence), error = None } -> planTraitEvidenceArgumentsFrom(tail)(environment)(TraitEvidenceArgument(shape = shape, evidence = evidence) :: reversed)
                | TraitEvidenceResolution { plan = _plan, error = Some(error) } -> TraitEvidenceArgumentPlanning(arguments = reverse(reversed), error = Some(error))
                | _ -> TraitEvidenceArgumentPlanning(arguments = reverse(reversed), error = Some(MissingTraitImplementation(constraint)([constraint])))

let planTraitEvidenceArguments constraints environment = planTraitEvidenceArgumentsFrom(planTraitEvidenceAbi(constraints)(environment))(environment)([])
