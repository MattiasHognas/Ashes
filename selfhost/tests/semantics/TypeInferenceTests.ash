import Ashes.Test as test
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
let expectType expected expression =
    match inferExpression(expression)(emptyTypeEnvironment(Unit)) with
        | TypeInferenceResult { semanticType = semanticType, substitution = substitution, supply = _supply, error = None } ->
            let actual = formatSemanticType(applySubstitution(substitution)(semanticType))
            in
                if actual == expected
                then Unit
                else test.fail("expected " + expected + " but inferred " + actual)
        | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, error = Some(error) } -> test.fail("inference should succeed: " + Ashes.Trait.Show.show(error))

let runTypeInferenceTests unit =
    (let identity = ExprLambda("value")(ExprVar("value"))(None)
    in
        let identityChecked = expectType("?0 -> ?0")(identity)
        in
            let applicationChecked = expectType("Int")(ExprCall(identity)(ExprInt(42))(false))
            in
                let identityReference = ExprVar("identity")
                in
                    let polymorphicBody = ExprTuple([ExprCall(identityReference)(ExprInt(1))(false), ExprCall(identityReference)(ExprString("text"))(false)])
                    in
                        let polymorphismChecked = expectType("(Int, Str)")(ExprLet("identity")(identity)(polymorphicBody)([])(None)([]))
                        in
                            let selfApplication = ExprLambda("value")(ExprCall(ExprVar("value"))(ExprVar("value"))(false))(None)
                            in
                                let occursChecked =
                                    match inferExpression(selfApplication)(emptyTypeEnvironment(Unit)) with
                                        | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, error = Some(InferenceUnificationError(InfiniteType(_variableId, _type))) } -> Unit
                                        | _ -> test.fail("self application should fail the occurs check")
                                in
                                    let unknownChecked =
                                        match inferExpression(ExprVar("missing"))(emptyTypeEnvironment(Unit)) with
                                            | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, error = Some(UnknownValue("missing")) } -> Unit
                                            | _ -> test.fail("unknown value should be reported")
                                    in Ashes.IO.print("all self-hosted core inference tests passed"))
