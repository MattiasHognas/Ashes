import Ashes.Test as test
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
let resultType errorType successType = SemNamed(-1)("Result")([errorType, successType])

let scheme semanticType = TypeScheme(quantified = [], body = semanticType, constraints = [])

let expectResultType expected expression environment =
    match inferExpression(expression)(environment) with
        | TypeInferenceResult { semanticType = semanticType, substitution = substitution, supply = _supply, constraints = _constraints, error = None } ->
            let actual = formatSemanticType(applySubstitution(substitution)(semanticType))
            in
                if actual == expected
                then Unit
                else test.fail("expected " + expected + " but inferred " + actual)
        | _ -> test.fail("Result expression should infer")

let runResultInferenceTests unit =
    (let inputType = resultType(SemInt)(SemString)
    in
        let baseEnvironment = addTypeBinding("input")(scheme(inputType))(emptyTypeEnvironment(Unit))
        in
            let successMapper = ExprLambda("text")(ExprEqual(ExprVar("text"))(ExprString("ok")))(None)
            in
                let successChecked = expectResultType("Result(Int, Bool)")(ExprResultPipe(ExprVar("input"))(successMapper))(baseEnvironment)
                in
                    let flatMapperType = SemFunction(SemString)(resultType(SemInt)(SemBool))(None)
                    in
                        let flatMapEnvironment = addTypeBinding("flatMap")(scheme(flatMapperType))(baseEnvironment)
                        in
                            let flatMapChecked = expectResultType("Result(Int, Bool)")(ExprResultPipe(ExprVar("input"))(ExprVar("flatMap")))(flatMapEnvironment)
                            in
                                let errorMapper = ExprLambda("error")(ExprString("mapped"))(None)
                                in
                                    let errorChecked = expectResultType("Result(Str, Str)")(ExprResultMapErrorPipe(ExprVar("input"))(errorMapper))(baseEnvironment)
                                    in
                                        let continuationType = SemFunction(SemString)(resultType(SemInt)(SemBool))(None)
                                        in
                                            let continuationEnvironment = addTypeBinding("continueWith")(scheme(continuationType))(baseEnvironment)
                                            in
                                                let letResult = ExprLetResult("value")(ExprVar("input"))(ExprCall(ExprVar("continueWith"))(ExprVar("value"))(false))
                                                in
                                                    let letResultChecked = expectResultType("Result(Int, Bool)")(letResult)(continuationEnvironment)
                                                    in
                                                        let invalidLeft = ExprResultPipe(ExprInt(1))(ExprLambda("value")(ExprVar("value"))(None))
                                                        in
                                                            let invalidLeftChecked =
                                                                match inferExpression(invalidLeft)(emptyTypeEnvironment(Unit)) with
                                                                    | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, constraints = _constraints, error = Some(ExpectedResultType(SemInt)) } -> Unit
                                                                    | _ -> test.fail("Result pipelines should require a Result left operand")
                                                            in
                                                                let invalidBody = ExprLetResult("value")(ExprVar("input"))(ExprVar("value"))
                                                                in
                                                                    let invalidBodyChecked =
                                                                        match inferExpression(invalidBody)(baseEnvironment) with
                                                                            | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, constraints = _constraints, error = Some(ExpectedResultType(SemString)) } -> Unit
                                                                            | _ -> test.fail("let? bodies should produce Result")
                                                                    in Ashes.IO.print("all self-hosted Result inference tests passed"))
