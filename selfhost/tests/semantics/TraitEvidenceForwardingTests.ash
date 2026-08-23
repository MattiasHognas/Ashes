import Ashes.IO
import Ashes.Test as test
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TraitEvidenceThreading
import TraitEvidenceArgumentTests
let equalVariable unit = TraitConstraint(traitName = "Equal", typeArguments = [SemVariable(7)])

let orderedVariable unit = TraitConstraint(traitName = "Ordered", typeArguments = [SemVariable(7)])

let expectExactRecursiveEvidenceForwarding unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> planTraitEvidenceForwarding([equalVariable(Unit)])([equalVariable(Unit)]) with
        | TraitEvidenceForwardingPlanning { arguments = TraitEvidenceForwardingArgument { shape = _shape, forwarding = TraitEvidenceForwarding { rootParameterIndex = 0, supertraitPath = [] } } :: [], error = None } -> Unit
        | _ -> test.fail("recursive self calls should forward their exact active dictionary")

let expectSupertraitRecursiveEvidenceForwarding unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> planTraitEvidenceForwarding([equalVariable(Unit)])([orderedVariable(Unit)]) with
        | TraitEvidenceForwardingPlanning { arguments = TraitEvidenceForwardingArgument { shape = _shape, forwarding = TraitEvidenceForwarding { rootParameterIndex = 0, supertraitPath = 0 :: [] } } :: [], error = None } -> Unit
        | _ -> test.fail("recursive calls should forward required supertrait evidence")

let rejectMissingRecursiveEvidenceForwarding unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> planTraitEvidenceForwarding([orderedVariable(Unit)])([equalVariable(Unit)]) with
        | TraitEvidenceForwardingPlanning { arguments = [], error = Some(MissingActiveTraitEvidence(TraitConstraint { traitName = "Ordered", typeArguments = SemVariable(7) :: [] })) } -> Unit
        | _ -> test.fail("recursive calls should reject unavailable active evidence")

let runTraitEvidenceForwardingTests unit =
    unit
    |> expectExactRecursiveEvidenceForwarding
    |> expectSupertraitRecursiveEvidenceForwarding
    |> rejectMissingRecursiveEvidenceForwarding
    |> (given (_) -> Ashes.IO.print("all self-hosted trait evidence forwarding tests passed"))
