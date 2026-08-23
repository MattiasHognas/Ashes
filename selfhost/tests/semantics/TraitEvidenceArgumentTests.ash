import Ashes.IO
import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.ProgramInference
import AshesCompiler.Semantics.TraitResolution
import AshesCompiler.Semantics.TraitEvidenceAbi
import AshesCompiler.Semantics.TraitEvidenceThreading
let equalMethod unit =
    TraitMethodDecl(name = "equal", signature = TypeArrow(
        TypeNamed("a"),
        TypeArrow(TypeNamed("a"))(TypeNamed("Bool"))([])(None),
        [],
        None
    ), defaultImplementation = None)

let compareMethod unit =
    TraitMethodDecl(name = "compare", signature = TypeArrow(
        TypeNamed("a"),
        TypeArrow(TypeNamed("a"))(TypeNamed("Int"))([])(None),
        [],
        None
    ), defaultImplementation = None)

let equalTrait unit =
    TraitDecl(name = "Equal", typeParameters = [TypeParameter(name = "a")], supertraits = [], methods = [equalMethod(
        Unit
    )])

let orderedTrait unit =
    TraitDecl(name = "Ordered", typeParameters = [TypeParameter(name = "a")], supertraits = [TraitConstraintSyntax(traitName = "Equal", typeArguments = [TypeNamed(
        "a"
    )])], methods = [compareMethod(
        Unit
    )])

let equalIntImplementation unit =
    TraitImplementationDecl(traitName = "Equal", typeArguments = [TypeNamed(
        "Int"
    )], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = ExprLambda(
        "left",
        ExprLambda("right")(ExprBool(true))(None),
        None
    ))])

let orderedIntImplementation unit =
    TraitImplementationDecl(traitName = "Ordered", typeArguments = [TypeNamed(
        "Int"
    )], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "compare", implementation = ExprLambda(
        "left",
        ExprLambda("right")(ExprInt(0))(None),
        None
    ))])

let evidenceEnvironment unit =
    match inferProgram(ProgramSyntax(items = [Unit
    |> orderedTrait
    |> TopLevelTrait, Unit
    |> equalTrait
    |> TopLevelTrait, Unit
    |> equalIntImplementation
    |> TopLevelImplementation, Unit
    |> orderedIntImplementation
    |> TopLevelImplementation], body = None)) with
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = environment, error = None } -> environment
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } ->
            test.fail(
                "trait evidence program should infer: " + Ashes.Trait.Show.show(error)
            )

let expectConcreteEvidenceArguments unit =
    match Unit
    |> evidenceEnvironment
    |> planTraitEvidenceArguments(
        [
            TraitConstraint(traitName = "Ordered", typeArguments = [SemInt]),
            TraitConstraint(traitName = "Equal", typeArguments = [SemInt])
        ]
    ) with
        | TraitEvidenceArgumentPlanning { arguments = TraitEvidenceArgument { shape = TraitDictionaryAbiShape { parameterIndex = 0, constraint = TraitConstraint { traitName = "Equal", typeArguments = SemInt :: [] }, methods = "equal" :: [], supertraits = [] }, evidence = TraitEvidenceInstance(TraitConstraint { traitName = "Equal", typeArguments = SemInt :: [] }, _equalImplementation, [], []) } :: TraitEvidenceArgument { shape = TraitDictionaryAbiShape { parameterIndex = 1, constraint = TraitConstraint { traitName = "Ordered", typeArguments = SemInt :: [] }, methods = "compare" :: [], supertraits = _supertraitShapes }, evidence = TraitEvidenceInstance(TraitConstraint { traitName = "Ordered", typeArguments = SemInt :: [] }, _orderedImplementation, [], TraitEvidenceInstance(TraitConstraint { traitName = "Equal", typeArguments = SemInt :: [] }, _inheritedImplementation, [], []) :: []) } :: [], error = None } -> Unit
        | _ -> test.fail("unexpected concrete evidence arguments")

let expectAbstractEvidenceArgument unit =
    match Unit
    |> evidenceEnvironment
    |> planTraitEvidenceArguments([TraitConstraint(traitName = "Equal", typeArguments = [SemVariable(7)])]) with
        | TraitEvidenceArgumentPlanning { arguments = TraitEvidenceArgument { shape = TraitDictionaryAbiShape { parameterIndex = 0, constraint = constraint, methods = "equal" :: [], supertraits = [] }, evidence = TraitEvidenceParameter(evidenceConstraint) } :: [], error = None } ->
            test.assertEqual(
                constraint,
                evidenceConstraint
            )
        | _ -> test.fail("unexpected abstract evidence argument")

let expectEvidenceArgumentFailure unit =
    (let missing = TraitConstraint(traitName = "Equal", typeArguments = [SemString])
    in
        match Unit
        |> evidenceEnvironment
        |> planTraitEvidenceArguments([missing]) with
            | TraitEvidenceArgumentPlanning { arguments = [], error = Some(MissingTraitImplementation(goal, trace)) } ->
                test.assertEqual(
                    (missing, [missing]),
                    (goal, trace)
                )
            | _ -> test.fail("missing concrete evidence should stop argument planning"))

let runTraitEvidenceArgumentTests unit =
    unit
    |> expectConcreteEvidenceArguments
    |> expectAbstractEvidenceArgument
    |> expectEvidenceArgumentFailure
    |> (given (_) -> Ashes.IO.print("all self-hosted trait evidence argument tests passed"))
