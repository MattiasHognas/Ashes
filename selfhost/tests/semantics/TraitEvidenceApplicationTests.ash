import Ashes.Test as test
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TraitResolution
import AshesCompiler.Semantics.TraitEvidenceAbi
import AshesCompiler.Semantics.TraitEvidenceThreading
import TraitEvidenceArgumentTests
let equalIntFunctionScheme unit =
    TypeScheme(quantified = [], body = SemFunction(
        SemInt,
        SemFunction(SemInt)(SemBool)(None),
        None
    ), constraints = [TraitConstraint(traitName = "Equal", typeArguments = [SemInt])])

let equalGenericFunctionScheme unit =
    TypeScheme(quantified = [], body = SemFunction(
        SemVariable(7),
        SemFunction(SemVariable(7))(SemBool)(None),
        None
    ), constraints = [TraitConstraint(traitName = "Equal", typeArguments = [SemVariable(7)])])

let expectPartialTraitApplication unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> planTraitFunctionApplication(equalIntFunctionScheme(Unit))(1) with
        | TraitFunctionApplicationPlanning { evidenceArguments = TraitEvidenceArgument { shape = TraitDictionaryAbiShape { parameterIndex = 0, constraint = TraitConstraint { traitName = "Equal", typeArguments = SemInt :: [] }, methods = "equal" :: [], supertraits = [] }, evidence = TraitEvidenceInstance(_goal, _implementation, [], []) } :: [], ordinaryArity = 2, suppliedOrdinaryArguments = 1, remainingOrdinaryArguments = 1, capturesEvidence = true, error = None } -> Unit
        | _ -> test.fail("partial constrained application should capture one dictionary")

let expectCompleteTraitApplication unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> planTraitFunctionApplication(equalIntFunctionScheme(Unit))(2) with
        | TraitFunctionApplicationPlanning { evidenceArguments = _evidenceArguments, ordinaryArity = 2, suppliedOrdinaryArguments = 2, remainingOrdinaryArguments = 0, capturesEvidence = false, error = None } -> Unit
        | _ -> test.fail("complete constrained application should consume every ordinary argument")

let expectAbstractTraitApplication unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> planTraitFunctionApplication(equalGenericFunctionScheme(Unit))(0) with
        | TraitFunctionApplicationPlanning { evidenceArguments = TraitEvidenceArgument { shape = _shape, evidence = TraitEvidenceParameter(TraitConstraint { traitName = "Equal", typeArguments = SemVariable(7) :: [] }) } :: [], ordinaryArity = 2, suppliedOrdinaryArguments = 0, remainingOrdinaryArguments = 2, capturesEvidence = true, error = None } -> Unit
        | _ -> test.fail("generic function values should capture caller-supplied evidence")

let rejectTraitApplicationOverapplication unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> planTraitFunctionApplication(equalIntFunctionScheme(Unit))(3) with
        | TraitFunctionApplicationPlanning { evidenceArguments = [], ordinaryArity = 2, suppliedOrdinaryArguments = 3, remainingOrdinaryArguments = 0, capturesEvidence = false, error = Some(TraitFunctionOverapplication(2, 3)) } -> Unit
        | _ -> test.fail("trait application planning should reject excess ordinary arguments")

let rejectNegativeTraitApplicationCount unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> planTraitFunctionApplication(equalIntFunctionScheme(Unit))(-1) with
        | TraitFunctionApplicationPlanning { evidenceArguments = [], ordinaryArity = 2, suppliedOrdinaryArguments = -1, remainingOrdinaryArguments = 0, capturesEvidence = false, error = Some(TraitFunctionInvalidArgumentCount(-1)) } -> Unit
        | _ -> test.fail("trait application planning should reject negative ordinary argument counts")

let runTraitEvidenceApplicationTests unit =
    unit
    |> expectPartialTraitApplication
    |> expectCompleteTraitApplication
    |> expectAbstractTraitApplication
    |> rejectTraitApplicationOverapplication
    |> rejectNegativeTraitApplicationCount
    |> (given (_) -> Ashes.IO.print("all self-hosted trait evidence application tests passed"))
