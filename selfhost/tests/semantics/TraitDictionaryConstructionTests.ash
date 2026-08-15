import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.ProgramInference
import AshesCompiler.Semantics.TraitResolution
import AshesCompiler.Semantics.TraitDictionaryConstruction
import TraitEvidenceArgumentTests
let unaryStringMethod name defaultImplementation = TraitMethodDecl(name = name, signature = TypeArrow(TypeNamed("a"))(TypeNamed("Str"))([])(None), defaultImplementation = defaultImplementation)

let displayTrait unit =
    TraitDecl(name = "Display", typeParameters = [TypeParameter(name = "a")], supertraits = [], methods = [unaryStringMethod("tag")(None), unaryStringMethod("render")(None
    |> ExprLambda("value")(ExprString("default"))
    |> Some)])

let displayIntImplementation unit = TraitImplementationDecl(traitName = "Display", typeArguments = [TypeNamed("Int")], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "tag", implementation = ExprLambda("value")(ExprString("tag"))(None))])

let displayEnvironment unit =
    match inferProgram(ProgramSyntax(items = [Unit
    |> displayTrait
    |> TopLevelTrait, Unit
    |> displayIntImplementation
    |> TopLevelImplementation], body = None)) with
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = environment, error = None } -> environment
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } -> test.fail("display implementation should infer: " + Ashes.Trait.Show.show(error))

let genericEqualImplementation unit =
    TraitImplementationDecl(traitName = "Equal", typeArguments = [TypeApplied("List")([TypeNamed("a")])], requirements = [TraitConstraintSyntax(traitName = "Equal", typeArguments = [TypeNamed("a")])], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = ExprLambda("left")(ExprLambda("right")(ExprBool(true))(None))(None))])

let conditionalEvidenceEnvironment unit =
    match inferProgram(ProgramSyntax(items = [Unit
    |> TraitEvidenceArgumentTests.equalTrait
    |> TopLevelTrait, Unit
    |> TraitEvidenceArgumentTests.equalIntImplementation
    |> TopLevelImplementation, Unit
    |> genericEqualImplementation
    |> TopLevelImplementation], body = None)) with
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = environment, error = None } -> environment
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } -> test.fail("conditional Equal implementation should infer: " + Ashes.Trait.Show.show(error))

let planResolvedDictionary constraint environment =
    match resolveTraitEvidence(constraint)(environment) with
        | TraitEvidenceResolution { plan = Some(evidence), error = None } -> planTraitDictionaryConstruction(evidence)(environment)
        | _ -> test.fail("dictionary evidence should resolve")

let expectSelectedMethodSources unit =
    match Unit
    |> displayEnvironment
    |> planResolvedDictionary(TraitConstraint(traitName = "Display", typeArguments = [SemInt])) with
        | TraitDictionaryConstructionPlanning { construction = Some(TraitDictionaryConstructionPlan { constraint = TraitConstraint { traitName = "Display", typeArguments = SemInt :: [] }, methods = TraitDictionaryMethodField { methodIndex = 0, methodName = "render", source = TraitDictionaryDefaultMethod, implementation = _render } :: TraitDictionaryMethodField { methodIndex = 1, methodName = "tag", source = TraitDictionarySuppliedMethod, implementation = _tag } :: [], requirements = [], supertraits = [] }), error = None } -> Unit
        | _ -> test.fail("dictionary methods should select defaults and supplied bindings in ABI order")

let expectSupertraitConstructionInput unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> planResolvedDictionary(TraitConstraint(traitName = "Ordered", typeArguments = [SemInt])) with
        | TraitDictionaryConstructionPlanning { construction = Some(TraitDictionaryConstructionPlan { constraint = _constraint, methods = _methods, requirements = [], supertraits = TraitEvidenceInstance(TraitConstraint { traitName = "Equal", typeArguments = SemInt :: [] }, _implementation, [], []) :: [] }), error = None } -> Unit
        | _ -> test.fail("dictionary construction should retain inherited evidence inputs")

let expectRequirementConstructionInput unit =
    match Unit
    |> conditionalEvidenceEnvironment
    |> planResolvedDictionary(TraitConstraint(traitName = "Equal", typeArguments = [SemList(SemInt)])) with
        | TraitDictionaryConstructionPlanning { construction = Some(TraitDictionaryConstructionPlan { constraint = _constraint, methods = _methods, requirements = TraitEvidenceInstance(TraitConstraint { traitName = "Equal", typeArguments = SemInt :: [] }, _implementation, [], []) :: [], supertraits = [] }), error = None } -> Unit
        | _ -> test.fail("dictionary construction should retain conditional requirement evidence")

let rejectAbstractDictionaryConstruction unit =
    match Unit
    |> displayEnvironment
    |> planTraitDictionaryConstruction(TraitEvidenceParameter(TraitConstraint(traitName = "Display", typeArguments = [SemVariable(7)]))) with
        | TraitDictionaryConstructionPlanning { construction = None, error = Some(TraitDictionaryConstructionRequiresParameter(TraitConstraint { traitName = "Display", typeArguments = SemVariable(7) :: [] })) } -> Unit
        | _ -> test.fail("abstract evidence should be forwarded instead of constructed")

let runTraitDictionaryConstructionTests unit =
    unit
    |> expectSelectedMethodSources
    |> expectSupertraitConstructionInput
    |> expectRequirementConstructionInput
    |> rejectAbstractDictionaryConstruction
    |> (given (_) -> Ashes.IO.print("all self-hosted trait dictionary construction tests passed"))
