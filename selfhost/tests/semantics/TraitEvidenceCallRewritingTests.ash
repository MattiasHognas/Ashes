import Ashes.IO
import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TraitEvidenceThreading
import AshesCompiler.Semantics.TraitEvidenceRewriting
import TraitEvidenceArgumentTests
let equalVariable unit = TraitConstraint(traitName = "Equal", typeArguments = [SemVariable(7)])

let orderedVariable unit = TraitConstraint(traitName = "Ordered", typeArguments = [SemVariable(7)])

// Two more DIFFERENT variable ids for the same trait — the shape two independently-generalized
// schemes' "same" abstract requirement actually takes in practice (each carries its own fresh
// quantified variable; nothing makes them equal by construction). Named distinctly from
// equalVariable/orderedVariable, which both reuse SemVariable(7) and so can only ever exercise
// EXACT matching.
let equalOtherVariable unit = TraitConstraint(traitName = "Equal", typeArguments = [SemVariable(9)])

let equalThirdVariable unit = TraitConstraint(traitName = "Equal", typeArguments = [SemVariable(11)])

let expectExactEvidenceCallRewrite unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> rewriteTraitConstrainedReference(ExprVar("equalValues"))([equalVariable(Unit)])([equalVariable(Unit)]) with
        | TraitConstrainedReferenceRewriting { expression = Some(ExprCall(ExprVar("equalValues"), ExprVar("__trait_evidence_0"), false, _layout)), error = None } -> Unit
        | _ -> test.fail("constrained references should receive their exact active dictionary")

let expectInheritedEvidenceCallRewrite unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> rewriteTraitConstrainedReference(ExprVar("equalValues"))([equalVariable(Unit)])([orderedVariable(Unit)]) with
        | TraitConstrainedReferenceRewriting { expression = Some(ExprCall(ExprVar("equalValues"), ExprVar("__trait_0_root_super_0"), false, _layout)), error = None } -> Unit
        | _ -> test.fail("constrained references should receive inherited supertrait evidence")

let expectAbiOrderedEvidenceCallRewrite unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> rewriteTraitConstrainedReference(
        ExprVar("compareAndEqual"),
        [orderedVariable(Unit), equalVariable(Unit)],
        [orderedVariable(Unit), equalVariable(Unit)]
    ) with
        | TraitConstrainedReferenceRewriting { expression = Some(ExprCall(ExprCall(ExprVar("compareAndEqual"), ExprVar("__trait_evidence_0"), false, _innerLayout), ExprVar("__trait_evidence_1"), false, _layout)), error = None } -> Unit
        | _ -> test.fail("multiple dictionaries should be applied in canonical ABI order")

let rejectUnavailableEvidenceCallRewrite unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> rewriteTraitConstrainedReference(ExprVar("compareValues"))([orderedVariable(Unit)])([equalVariable(Unit)]) with
        | TraitConstrainedReferenceRewriting { expression = None, error = Some(MissingActiveTraitEvidence(TraitConstraint { traitName = "Ordered", typeArguments = SemVariable(7) :: [] })) } -> Unit
        | _ -> test.fail("constrained references should reject unavailable evidence")

// A required constraint whose type argument is STILL A FREE VARIABLE (never unified with
// anything), and exactly ONE active dictionary parameter for the SAME trait name (a DIFFERENT
// variable id — equalOtherVariable, not equalVariable) — falls back to that sole parameter rather
// than rejecting via exact stable-key matching, which can never fire between two independently
// generalized variables. Mirrors stage-0's FindActiveTraitDictionaryParameter fallback
// (Lowering.TraitEvidence.cs) and produces the identical rewrite shape
// expectExactEvidenceCallRewrite already proves for the exact-match case — same evidence slot,
// reached a different way.
let expectSoleActiveParameterFallbackCallRewrite unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> rewriteTraitConstrainedReference(ExprVar("equalValues"))([equalOtherVariable(Unit)])([equalVariable(Unit)]) with
        | TraitConstrainedReferenceRewriting { expression = Some(ExprCall(ExprVar("equalValues"), ExprVar("__trait_evidence_0"), false, _layout)), error = None } -> Unit
        | _ -> test.fail("a still-abstract requirement should fall back to the sole active parameter for the same trait")

// Two ACTIVE parameters for the same trait (equalVariable and equalOtherVariable, both "Equal" at
// different variable ids) and a required constraint (equalThirdVariable) matching NEITHER exactly
// — makes the fallback ambiguous on purpose: which of the two active parameters is right depends
// on type information this check doesn't have, so no fallback fires and the ordinary
// MissingActiveTraitEvidence rejection takes over. Proves the fallback doesn't over-fire once
// there's real ambiguity to worry about.
let rejectAmbiguousActiveParametersCallRewrite unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> rewriteTraitConstrainedReference(ExprVar("equalValues"))([equalThirdVariable(Unit)])([equalVariable(Unit), equalOtherVariable(Unit)]) with
        | TraitConstrainedReferenceRewriting { expression = None, error = Some(MissingActiveTraitEvidence(TraitConstraint { traitName = "Equal", typeArguments = SemVariable(11) :: [] })) } -> Unit
        | _ -> test.fail("ambiguous active parameters for the same trait should reject cleanly, never silently pick the wrong one")

let runTraitEvidenceCallRewritingTests unit =
    unit
    |> expectExactEvidenceCallRewrite
    |> expectInheritedEvidenceCallRewrite
    |> expectAbiOrderedEvidenceCallRewrite
    |> rejectUnavailableEvidenceCallRewrite
    |> expectSoleActiveParameterFallbackCallRewrite
    |> rejectAmbiguousActiveParametersCallRewrite
    |> (given (_) -> Ashes.IO.print("all self-hosted trait evidence call rewriting tests passed"))
