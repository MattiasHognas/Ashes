import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TraitEvidenceThreading
import AshesCompiler.Semantics.TraitEvidenceRewriting
import TraitEvidenceArgumentTests
let equalVariable unit = TraitConstraint(traitName = "Equal", typeArguments = [SemVariable(7)])

let orderedVariable unit = TraitConstraint(traitName = "Ordered", typeArguments = [SemVariable(7)])

let expectExactEvidenceCallRewrite unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> rewriteTraitConstrainedReference(ExprVar("equalValues"))([equalVariable(Unit)])([equalVariable(Unit)]) with
        | TraitConstrainedReferenceRewriting { expression = Some(ExprCall(ExprVar("equalValues"), ExprVar("__trait_evidence_0"), false)), error = None } -> Unit
        | _ -> test.fail("constrained references should receive their exact active dictionary")

let expectInheritedEvidenceCallRewrite unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> rewriteTraitConstrainedReference(ExprVar("equalValues"))([equalVariable(Unit)])([orderedVariable(Unit)]) with
        | TraitConstrainedReferenceRewriting { expression = Some(ExprCall(ExprVar("equalValues"), ExprVar("__trait_0_root_super_0"), false)), error = None } -> Unit
        | _ -> test.fail("constrained references should receive inherited supertrait evidence")

let expectAbiOrderedEvidenceCallRewrite unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> rewriteTraitConstrainedReference(ExprVar("compareAndEqual"))([orderedVariable(Unit), equalVariable(Unit)])([orderedVariable(Unit), equalVariable(Unit)]) with
        | TraitConstrainedReferenceRewriting { expression = Some(ExprCall(ExprCall(ExprVar("compareAndEqual"), ExprVar("__trait_evidence_0"), false), ExprVar("__trait_evidence_1"), false)), error = None } -> Unit
        | _ -> test.fail("multiple dictionaries should be applied in canonical ABI order")

let rejectUnavailableEvidenceCallRewrite unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> rewriteTraitConstrainedReference(ExprVar("compareValues"))([orderedVariable(Unit)])([equalVariable(Unit)]) with
        | TraitConstrainedReferenceRewriting { expression = None, error = Some(MissingActiveTraitEvidence(TraitConstraint { traitName = "Ordered", typeArguments = SemVariable(7) :: [] })) } -> Unit
        | _ -> test.fail("constrained references should reject unavailable evidence")

let runTraitEvidenceCallRewritingTests unit =
    unit
    |> expectExactEvidenceCallRewrite
    |> expectInheritedEvidenceCallRewrite
    |> expectAbiOrderedEvidenceCallRewrite
    |> rejectUnavailableEvidenceCallRewrite
    |> (given (_) -> Ashes.IO.print("all self-hosted trait evidence call rewriting tests passed"))
