import Ashes.Test as test
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
let resultType errorType successType = SemNamed(-1)("Result")([errorType, successType])

let scheme semanticType = TypeScheme(quantified = [], body = semanticType, constraints = [])

let expectResultType expected expression environment =
    match inferExpression(expression)(environment) with
        | TypeInferenceResult { semanticType = semanticType, substitution = substitution, supply = _supply, constraints = _constraints, error = None } ->
            let actual =
                semanticType
                |> applySubstitution(substitution)
                |> formatSemanticType
            in
                if actual == expected
                then Unit
                else test.fail("expected " + expected + " but inferred " + actual)
        | _ -> test.fail("Result expression should infer")

let resultEnvironment unit =
    Unit
    |> emptyTypeEnvironment
    |> addTypeBinding("input")(SemString
    |> resultType(SemInt)
    |> scheme)

let expectSuccessResultPipe unit =
    (let mapper =
        ExprLambda("text")(ExprEqual(ExprVar("text"))(ExprString("ok")))(None)
    in
        Unit
        |> resultEnvironment
        |> expectResultType("Result(Int, Bool)")(ExprResultPipe(ExprVar("input"))(mapper)))

let expectFlatResultPipe unit =
    (let mapperType =
        SemFunction(SemString)(resultType(SemInt)(SemBool))(None)
    in
        let environment =
            Unit
            |> resultEnvironment
            |> addTypeBinding("flatMap")(scheme(mapperType))
        in
            expectResultType("Result(Int, Bool)")(ExprResultPipe(ExprVar("input"))(ExprVar("flatMap")))(environment))

let expectErrorResultPipe unit =
    (let mapper = ExprLambda("error")(ExprString("mapped"))(None)
    in
        Unit
        |> resultEnvironment
        |> expectResultType("Result(Str, Str)")(ExprResultMapErrorPipe(ExprVar("input"))(mapper)))

let expectLetResult unit =
    (let continuationType =
        SemFunction(SemString)(resultType(SemInt)(SemBool))(None)
    in
        let environment =
            Unit
            |> resultEnvironment
            |> addTypeBinding("continueWith")(scheme(continuationType))
        in
            let expression =
                callArgumentsInline
                |> ExprCall(ExprVar("continueWith"))(ExprVar("value"))(false)
                |> ExprLetResult("value")(ExprVar("input"))
            in expectResultType("Result(Int, Bool)")(expression)(environment))

let rejectNonResultPipe unit =
    (let expression =
        None
        |> ExprLambda("value")(ExprVar("value"))
        |> ExprResultPipe(ExprInt(1))
    in
        match Unit
        |> emptyTypeEnvironment
        |> inferExpression(expression) with
            | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, constraints = _constraints, error = Some(ExpectedResultType(SemInt)) } -> Unit
            | _ -> test.fail("Result pipelines should require a Result left operand"))

let rejectNonResultBody unit =
    (let expression = ExprLetResult("value")(ExprVar("input"))(ExprVar("value"))
    in
        match Unit
        |> resultEnvironment
        |> inferExpression(expression) with
            | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, constraints = _constraints, error = Some(ExpectedResultType(SemString)) } -> Unit
            | _ -> test.fail("let? bodies should produce Result"))

let reportResultInferenceSuccess unit = Ashes.IO.print("all self-hosted Result inference tests passed")

let runResultInferenceTests unit =
    unit
    |> expectSuccessResultPipe
    |> expectFlatResultPipe
    |> expectErrorResultPipe
    |> expectLetResult
    |> rejectNonResultPipe
    |> rejectNonResultBody
    |> reportResultInferenceSuccess
