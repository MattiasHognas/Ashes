import Ashes.IO
import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.ProgramInference
import AshesCompiler.Semantics.TraitDictionaryConstruction
import TraitDictionaryConstructionTests
let unaryBoolMethod name defaultImplementation =
    TraitMethodDecl(name = name, signature = TypeArrow(
        TypeNamed("a"),
        TypeNamed("Bool"),
        [],
        None
    ), defaultImplementation = defaultImplementation)

let dependencyTrait unit =
    TraitDecl(name = "Dependency", typeParameters = [TypeParameter(name = "a")], supertraits = [], methods = [unaryBoolMethod(
        "zBase",
        None
    ), unaryBoolMethod("aDerived")(None
    |> ExprLambda(
        "value",
        ExprCall(ExprQualifiedVar("Dependency")("zBase"))(ExprVar("value"))(false)(callArgumentsInline)
    )
    |> Some)])

let dependencyIntImplementation unit =
    TraitImplementationDecl(traitName = "Dependency", typeArguments = [TypeNamed(
        "Int"
    )], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "zBase", implementation = ExprLambda(
        "value",
        ExprBool(true),
        None
    ))])

let dependencyEnvironment unit =
    match inferProgram(ProgramSyntax(items = [Unit
    |> dependencyTrait
    |> TopLevelTrait, Unit
    |> dependencyIntImplementation
    |> TopLevelImplementation], body = None)) with
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = environment, error = None } -> environment
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } ->
            test.fail(
                "dependency implementation should infer: " + Ashes.Trait.Show.show(error)
            )

let expectDependencyAwareMethodConstructionOrder unit =
    match Unit
    |> dependencyEnvironment
    |> TraitDictionaryConstructionTests.planResolvedDictionary(
        TraitConstraint(traitName = "Dependency", typeArguments = [SemInt])
    ) with
        | TraitDictionaryConstructionPlanning { construction = Some(TraitDictionaryConstructionPlan { constraint = _constraint, methods = TraitDictionaryMethodField { methodIndex = 0, methodName = "aDerived", source = TraitDictionaryDefaultMethod, implementation = _derived } :: TraitDictionaryMethodField { methodIndex = 1, methodName = "zBase", source = TraitDictionarySuppliedMethod, implementation = _base } :: [], methodConstructionOrder = TraitDictionaryMethodField { methodName = "zBase" } :: TraitDictionaryMethodField { methodName = "aDerived" } :: [], requirements = [], supertraits = [] }), error = None } -> Unit
        | _ -> test.fail("method construction should build selected dependencies before ABI field order")

let runTraitMethodConstructionOrderTests unit =
    unit
    |> expectDependencyAwareMethodConstructionOrder
    |> (given (_) -> Ashes.IO.print("all self-hosted trait method construction order tests passed"))
