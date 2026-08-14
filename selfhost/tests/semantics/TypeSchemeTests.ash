import Ashes.Test as test
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeSchemes
let runTypeSchemeTests unit =
    (let identityScheme = TypeScheme(quantified = [(0, "a")], body = TypeFunction(TypeVariable(0))(TypeVariable(0))(None), constraints = [TraitConstraint(traitName = "Eq", typeArguments = [TypeVariable(0)])])
    in
        match instantiate(identityScheme)(TypeVariableSupply(nextId = 10)) with
            | InstantiationResult { semanticType = instantiatedType, constraints = instantiatedConstraints, supply = afterFirst } ->
                let typeChecked = test.assertEqual("?10 -> ?10")(formatSemanticType(instantiatedType))
                in
                    let constraintChecked =
                        match instantiatedConstraints with
                            | TraitConstraint { traitName = "Eq", typeArguments = TypeVariable(10) :: [] } :: [] -> Unit
                            | _ -> test.fail("constraint should use the fresh variable")
                    in
                        match instantiate(identityScheme)(afterFirst) with
                            | InstantiationResult { semanticType = secondType, constraints = _secondConstraints, supply = _afterSecond } ->
                                let freshnessChecked = test.assertEqual("?11 -> ?11")(formatSemanticType(secondType))
                                in
                                    let environment = [TypeScheme(quantified = [], body = TypeVariable(0), constraints = [])]
                                    in
                                        let generalized = generalize(environment)(TypeFunction(TypeVariable(0))(TypeVariable(1))(None))([])
                                        in
                                            match generalized with
                                                | TypeScheme { quantified = (1, _name) :: [], body = _body, constraints = [] } -> Ashes.IO.print("all self-hosted type scheme tests passed")
                                                | _ -> test.fail("generalization should exclude environment variables"))
