import Ashes.IO
import Ashes.Test as test
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeSchemes
let identityScheme =
    TypeScheme(quantified = [(0, "a")], body = SemFunction(
        SemVariable(0),
        SemVariable(0),
        None
    ), constraints = [TraitConstraint(traitName = "Eq", typeArguments = [SemVariable(0)])])

let instantiatesSchemeConstraints unit =
    match instantiate(identityScheme)(TypeVariableSupply(nextId = 10)) with
        | InstantiationResult { semanticType = instantiatedType, constraints = instantiatedConstraints, supply = _afterFirst } ->
            let typeChecked =
                instantiatedType
                |> formatSemanticType
                |> test.assertEqual("?10 -> ?10")
            in
                match instantiatedConstraints with
                    | TraitConstraint { traitName = "Eq", typeArguments = SemVariable(10) :: [] } :: [] -> Unit
                    | _ -> test.fail("constraint should use the fresh variable")

let instantiationAdvancesSupply unit =
    match instantiate(identityScheme)(TypeVariableSupply(nextId = 10)) with
        | InstantiationResult { semanticType = _firstType, constraints = _firstConstraints, supply = afterFirst } ->
            match instantiate(identityScheme)(afterFirst) with
                | InstantiationResult { semanticType = secondType, constraints = _secondConstraints, supply = _afterSecond } ->
                    secondType
                    |> formatSemanticType
                    |> test.assertEqual("?11 -> ?11")

let generalizationExcludesEnvironmentVariables unit =
    (let environment = [TypeScheme(quantified = [], body = SemVariable(0), constraints = [])]
    in
        match generalize(environment)(SemFunction(SemVariable(0))(SemVariable(1))(None))([]) with
            | TypeScheme { quantified = (1, _name) :: [], body = _body, constraints = [] } -> Unit
            | _ -> test.fail("generalization should exclude environment variables"))

let canonicalizesGeneralizedConstraints unit =
    (let constraints =
        [TraitConstraint(traitName = "Show", typeArguments = [SemVariable(
            2
        )]), TraitConstraint(traitName = "Eq", typeArguments = [SemVariable(
            10
        )]), TraitConstraint(traitName = "Eq", typeArguments = [SemVariable(
            2
        )]), TraitConstraint(traitName = "Eq", typeArguments = [SemVariable(
            2
        )])]
    in
        match generalize([])(SemTuple([SemVariable(2), SemVariable(10)]))(constraints) with
            | TypeScheme { quantified = _quantified, body = _body, constraints = TraitConstraint { traitName = "Eq", typeArguments = SemVariable(2) :: [] } :: TraitConstraint { traitName = "Eq", typeArguments = SemVariable(10) :: [] } :: TraitConstraint { traitName = "Show", typeArguments = SemVariable(2) :: [] } :: [] } -> Unit
            | _ ->
                test.fail(
                    "generalized constraints should be stably ordered, deduplicated, and retain distinct arguments"
                ))

let reportTypeSchemeSuccess unit = Ashes.IO.print("all self-hosted type scheme tests passed")

let runTypeSchemeTests unit =
    unit
    |> instantiatesSchemeConstraints
    |> instantiationAdvancesSupply
    |> generalizationExcludesEnvironmentVariables
    |> canonicalizesGeneralizedConstraints
    |> reportTypeSchemeSuccess
