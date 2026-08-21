import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TraitResolution
import AshesCompiler.Semantics.TraitDictionaryConstruction
import AshesCompiler.Semantics.TraitEvidenceRewriting
import TraitDictionaryConstructionTests
import TraitEvidenceArgumentTests
let rewriteResolvedDictionary constraint environment =
    match resolveTraitEvidence(constraint)(environment) with
        | TraitEvidenceResolution { plan = Some(evidence), error = None } ->
            rewriteTraitDictionaryValue(
                evidence,
                environment
            )
        | _ -> test.fail("dictionary evidence should resolve before rewriting")

let expectAbiOrderedMethodTuple unit =
    (let environment = TraitDictionaryConstructionTests.displayEnvironment(Unit)
    in
        let constraint = TraitConstraint(traitName = "Display", typeArguments = [SemInt])
        in
            let orderChecked =
                match TraitDictionaryConstructionTests.planResolvedDictionary(constraint)(environment) with
                    | TraitDictionaryConstructionPlanning { construction = Some(TraitDictionaryConstructionPlan { methodConstructionOrder = TraitDictionaryMethodField { methodName = "render" } :: TraitDictionaryMethodField { methodName = "tag" } :: [] }), error = None } -> Unit
                    | _ -> test.fail("display dictionary should retain its method construction order")
            in
                let rewriting = rewriteResolvedDictionary(constraint)(environment)
                in
                    ((given (_) ->
                        match rewriting with
                            | TraitDictionaryValueRewriting { expression = Some(ExprLet("__trait_selected_Display_render", _render, ExprLet("__trait_selected_Display_tag", _tag, ExprTuple(ExprVar("__trait_selected_Display_render") :: ExprVar("__trait_selected_Display_tag") :: []), [], None, []), [], None, [])), error = None } -> Unit
                            | TraitDictionaryValueRewriting { expression = Some(ExprLet(name, _value, _body, _parameters, _annotation, _requirements)), error = None } ->
                                test.fail(
                                    "unexpected outer dictionary method binding: " + name
                                )
                            | TraitDictionaryValueRewriting { expression = Some(ExprLetRecursive(name, _value, _body, _parameters, _annotation, _requirements)), error = None } ->
                                test.fail(
                                    "unexpected recursive outer dictionary method binding: " + name
                                )
                            | TraitDictionaryValueRewriting { expression = Some(ExprTuple(_elements)), error = None } ->
                                test.fail(
                                    "dictionary rewrite omitted method bindings"
                                )
                            | TraitDictionaryValueRewriting { expression = Some(ExprAt(_span, _inner)), error = None } ->
                                test.fail(
                                    "dictionary rewrite retained an unexpected source span"
                                )
                            | TraitDictionaryValueRewriting { expression = Some(_expression), error = None } ->
                                test.fail(
                                    "dictionary rewrite produced an unexpected expression"
                                )
                            | TraitDictionaryValueRewriting { expression = None, error = Some(_error) } ->
                                test.fail(
                                    "dictionary rewrite should succeed"
                                )
                            | TraitDictionaryValueRewriting { expression = None, error = None } ->
                                test.fail(
                                    "dictionary rewrite should produce an expression"
                                )))(orderChecked))

let expectNestedSupertraitDictionary unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> rewriteResolvedDictionary(TraitConstraint(traitName = "Ordered", typeArguments = [SemInt])) with
        | TraitDictionaryValueRewriting { expression = Some(ExprLet("__trait_selected_Ordered_compare", _compare, ExprTuple(ExprVar("__trait_selected_Ordered_compare") :: ExprLet("__trait_selected_Equal_equal", _equal, ExprVar("__trait_selected_Equal_equal"), [], None, []) :: []), [], None, [])), error = None } -> Unit
        | _ -> test.fail("dictionary values should retain recursively constructed supertrait evidence")

let rejectAbstractDictionaryRewrite unit =
    match Unit
    |> TraitDictionaryConstructionTests.displayEnvironment
    |> rewriteTraitDictionaryValue(
        TraitEvidenceParameter(TraitConstraint(traitName = "Display", typeArguments = [SemVariable(7)]))
    ) with
        | TraitDictionaryValueRewriting { expression = None, error = Some(TraitDictionaryConstructionRequiresParameter(TraitConstraint { traitName = "Display", typeArguments = SemVariable(7) :: [] })) } -> Unit
        | _ -> test.fail("abstract dictionaries should be forwarded instead of constructed")

let expectSelectedMethodReferenceRewriting unit =
    match "tag"
    |> ExprQualifiedVar("Ashes.Trait.Display")
    |> rewriteSelectedTraitMethodImplementation("Ashes.Trait.Display")("render") with
        | ExprVar("__trait_selected_Display_tag") -> Unit
        | _ -> test.fail("selected method bodies should reference sibling bindings by leaf-qualified names")

let expectSelectedMethodSelfRewriting unit =
    match "render"
    |> ExprQualifiedVar("Ashes.Trait.Display")
    |> rewriteSelectedTraitMethodImplementation("Ashes.Trait.Display")("render") with
        | ExprLetRecursive("__trait_impl_Display_render", ExprVar("__trait_impl_Display_render"), ExprVar("__trait_impl_Display_render"), [], None, []) -> Unit
        | _ -> test.fail("selected recursive methods should bind their rewritten self reference")

let runTraitDictionaryRewritingTests unit =
    unit
    |> expectAbiOrderedMethodTuple
    |> expectNestedSupertraitDictionary
    |> rejectAbstractDictionaryRewrite
    |> expectSelectedMethodReferenceRewriting
    |> expectSelectedMethodSelfRewriting
    |> (given (_) -> Ashes.IO.print("all self-hosted trait dictionary rewriting tests passed"))
