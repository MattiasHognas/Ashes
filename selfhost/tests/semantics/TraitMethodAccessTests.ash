import Ashes.Test as test
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TraitEvidenceThreading
import TraitEvidenceArgumentTests
let equalVariable unit = TraitConstraint(traitName = "Equal", typeArguments = [SemVariable(7)])

let orderedVariable unit = TraitConstraint(traitName = "Ordered", typeArguments = [SemVariable(7)])

let expectExactTraitMethodAccess unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> planActiveTraitMethodAccess(equalVariable(Unit))("equal")([equalVariable(Unit)]) with
        | TraitMethodAccessPlanning { access = Some(TraitMethodAccess { forwarding = TraitEvidenceForwarding { rootParameterIndex = 0, supertraitPath = [] }, methodName = "equal", methodIndex = 0 }), error = None } -> Unit
        | _ -> test.fail("active method access should select the exact dictionary method slot")

let expectInheritedTraitMethodAccess unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> planActiveTraitMethodAccess(equalVariable(Unit))("equal")([orderedVariable(Unit)]) with
        | TraitMethodAccessPlanning { access = Some(TraitMethodAccess { forwarding = TraitEvidenceForwarding { rootParameterIndex = 0, supertraitPath = 0 :: [] }, methodName = "equal", methodIndex = 0 }), error = None } -> Unit
        | _ -> test.fail("active method access should follow the inherited dictionary path")

let rejectMissingTraitMethodSlot unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> planActiveTraitMethodAccess(equalVariable(Unit))("compare")([equalVariable(Unit)]) with
        | TraitMethodAccessPlanning { access = None, error = Some(TraitMethodNotInDictionary(TraitConstraint { traitName = "Equal", typeArguments = SemVariable(7) :: [] }, "compare")) } -> Unit
        | _ -> test.fail("active method access should reject methods absent from the dictionary")

let rejectMissingTraitMethodEvidence unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> planActiveTraitMethodAccess(orderedVariable(Unit))("compare")([equalVariable(Unit)]) with
        | TraitMethodAccessPlanning { access = None, error = Some(TraitMethodEvidenceUnavailable(TraitConstraint { traitName = "Ordered", typeArguments = SemVariable(7) :: [] })) } -> Unit
        | _ -> test.fail("active method access should reject unavailable dictionary evidence")

let runTraitMethodAccessTests unit =
    unit
    |> expectExactTraitMethodAccess
    |> expectInheritedTraitMethodAccess
    |> rejectMissingTraitMethodSlot
    |> rejectMissingTraitMethodEvidence
    |> (given (_) -> Ashes.IO.print("all self-hosted trait method access tests passed"))
