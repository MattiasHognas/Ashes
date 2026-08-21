import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.ProgramInference
import AshesCompiler.Semantics.TraitEvidenceAbi
let binaryMethod name result =
    TraitMethodDecl(name = name, signature = TypeArrow(
        TypeNamed("a"),
        TypeArrow(TypeNamed("a"))(result)([])(None),
        [],
        None
    ), defaultImplementation = None)

let equalTrait unit =
    TraitDecl(name = "Equal", typeParameters = [TypeParameter(name = "a")], supertraits = [], methods = [binaryMethod(
        "notEqual",
        TypeNamed("Bool")
    ), binaryMethod("equal")(TypeNamed("Bool"))])

let orderedTrait unit =
    TraitDecl(name = "Ordered", typeParameters = [TypeParameter(name = "a")], supertraits = [TraitConstraintSyntax(traitName = "Equal", typeArguments = [TypeNamed(
        "a"
    )])], methods = [binaryMethod(
        "greater",
        TypeNamed("Bool")
    ), binaryMethod("compare")(TypeNamed("Int"))])

let expectDeterministicDictionaryAbi unit =
    match inferProgram(ProgramSyntax(items = [Unit
    |> orderedTrait
    |> TopLevelTrait, Unit
    |> equalTrait
    |> TopLevelTrait], body = None)) with
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = environment, error = None } ->
            match planTraitEvidenceAbi(
                [
                    TraitConstraint(traitName = "Ordered", typeArguments = [SemInt]),
                    TraitConstraint(traitName = "Equal", typeArguments = [SemString])
                ],
                environment
            ) with
                | TraitDictionaryAbiShape { parameterIndex = 0, constraint = TraitConstraint { traitName = "Equal", typeArguments = SemString :: [] }, methods = "equal" :: "notEqual" :: [], supertraits = [] } :: TraitDictionaryAbiShape { parameterIndex = 1, constraint = TraitConstraint { traitName = "Ordered", typeArguments = SemInt :: [] }, methods = "compare" :: "greater" :: [], supertraits = TraitDictionaryAbiShape { parameterIndex = 1, constraint = TraitConstraint { traitName = "Equal", typeArguments = SemInt :: [] }, methods = "equal" :: "notEqual" :: [], supertraits = [] } :: [] } :: [] -> Unit
                | shapes -> test.fail("unexpected trait dictionary ABI: " + Ashes.Trait.Show.show(shapes))
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } ->
            test.fail(
                "trait declarations should infer: " + Ashes.Trait.Show.show(error)
            )

let runTraitEvidenceAbiTests unit =
    unit
    |> expectDeterministicDictionaryAbi
    |> (given (_) -> Ashes.IO.print("all self-hosted trait evidence ABI tests passed"))
