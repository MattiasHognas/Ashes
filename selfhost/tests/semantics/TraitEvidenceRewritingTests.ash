import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TraitEvidenceRewriting
import TraitEvidenceArgumentTests
let equalVariable unit = TraitConstraint(traitName = "Equal", typeArguments = [SemVariable(7)])

let orderedVariable unit = TraitConstraint(traitName = "Ordered", typeArguments = [SemVariable(7)])

let expectSingleDictionaryRewrite unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> rewriteTraitConstrainedValue(ExprQualifiedVar("Equal")("equal"))([equalVariable(Unit)]) with
        | ExprLambda("__trait_evidence_0", ExprMatch(ExprVar("__trait_evidence_0"), (PatternVar("__trait_0_root_Equal_equal_raw"), ExprLet("__trait_0_root_Equal_equal", ExprVar("__trait_0_root_Equal_equal_raw"), ExprVar("__trait_0_root_Equal_equal"), [], None, []), None) :: [], None), None) -> Unit
        | _ -> test.fail("constrained values should destructure and select their method dictionary")

let expectSupertraitDictionaryRewrite unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> rewriteTraitConstrainedValue(
        ExprTuple([ExprQualifiedVar("Ordered")("compare"), ExprQualifiedVar("Equal")("equal")]),
        [orderedVariable(Unit)]
    ) with
        | ExprLambda("__trait_evidence_0", ExprMatch(ExprVar("__trait_evidence_0"), (PatternTuple(PatternVar("__trait_0_root_Ordered_compare_raw") :: PatternVar("__trait_0_root_super_0") :: []), ExprLet("__trait_0_root_Ordered_compare", ExprVar("__trait_0_root_Ordered_compare_raw"), ExprMatch(ExprVar("__trait_0_root_super_0"), (PatternVar("__trait_0_root_0_Equal_equal_raw"), ExprLet("__trait_0_root_0_Equal_equal", ExprVar("__trait_0_root_0_Equal_equal_raw"), ExprTuple(ExprVar("__trait_0_root_Ordered_compare") :: ExprVar("__trait_0_root_0_Equal_equal") :: []), [], None, []), None) :: [], None), [], None, []), None) :: [], None), None) -> Unit
        | _ -> test.fail("supertrait dictionaries should destructure before rewritten method use")

let recursive containsQualifiedEqual expression =
    match expression with
        | ExprQualifiedVar("Equal", "equal") -> true
        | ExprLambda(_name, body, _annotation) -> containsQualifiedEqual(body)
        | ExprMatch(value, cases, _offset) ->
            if containsQualifiedEqual(value)
            then true
            else containsQualifiedEqualCases(cases)
        | ExprLet(_name, value, body, _parameters, _annotation, _requirements) ->
            if containsQualifiedEqual(value)
            then true
            else containsQualifiedEqual(body)
        | _ -> false
and containsQualifiedEqualCases cases =
    match cases with
        | [] -> false
        | (_pattern, body, _guard) :: tail ->
            if containsQualifiedEqual(body)
            then true
            else containsQualifiedEqualCases(tail)

let preserveAmbiguousSameTraitReference unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> rewriteTraitConstrainedValue(
        ExprQualifiedVar("Equal")("equal"),
        [
            TraitConstraint(traitName = "Equal", typeArguments = [SemInt]),
            TraitConstraint(traitName = "Equal", typeArguments = [SemString])
        ]
    )
    |> containsQualifiedEqual with
        | true -> Unit
        | false -> test.fail("same-trait evidence should remain for typed lowering to disambiguate")

let preserveUnconstrainedValue unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> rewriteTraitConstrainedValue(ExprInt(42))([]) with
        | ExprInt(42) -> Unit
        | _ -> test.fail("unconstrained values should not acquire hidden parameters")

let runTraitEvidenceRewritingTests unit =
    unit
    |> expectSingleDictionaryRewrite
    |> expectSupertraitDictionaryRewrite
    |> preserveAmbiguousSameTraitReference
    |> preserveUnconstrainedValue
    |> (given (_) -> Ashes.IO.print("all self-hosted trait evidence rewriting tests passed"))
