import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import ParserExpressionTests
let checkConditional unit =
    match "if true then 1 else 2"
    |> ParserExpressionTests.expectClean
    |> ParserExpressionTests.unspan with
        | ExprIf(condition, thenBranch, elseBranch) ->
            match (ParserExpressionTests.unspan(condition), ParserExpressionTests.unspan(
                thenBranch
            ), ParserExpressionTests.unspan(
                elseBranch
            )) with
                | (ExprBool(true), ExprInt(1), ExprInt(2)) -> Unit
                | _ -> test.fail("expected conditional branches")
        | _ -> test.fail("expected conditional")

let checkLambda unit =
    match "given (x, y) -> x + y"
    |> ParserExpressionTests.expectClean
    |> ParserExpressionTests.unspan with
        | ExprLambda("x", inner, None) ->
            match ParserExpressionTests.unspan(inner) with
                | ExprLambda("y", _, None) -> Unit
                | _ -> test.fail("expected nested lambda")
        | _ -> test.fail("expected lambda")

let checkRecursiveBinding unit =
    match "let recursive add x y = x + y in add(1)(2)"
    |> ParserExpressionTests.expectClean
    |> ParserExpressionTests.unspan with
        | ExprLetRecursive("add", value, _, "x" :: "y" :: [], None, []) ->
            match ParserExpressionTests.unspan(value) with
                | ExprLambda("x", _, None) -> Unit
                | _ -> test.fail("expected desugared binding lambda")
        | _ -> test.fail("expected recursive binding")

let checkResultBinding unit =
    match "let? value = result in value"
    |> ParserExpressionTests.expectClean
    |> ParserExpressionTests.unspan with
        | ExprLetResult("value", _, _) -> Unit
        | _ -> test.fail("expected result binding")

let checkAwaitBinding unit =
    match "let! first = one let! second = two in first + second"
    |> ParserExpressionTests.expectClean
    |> ParserExpressionTests.unspan with
        | ExprLet("first", awaited, body, [], None, []) ->
            match (ParserExpressionTests.unspan(awaited), ParserExpressionTests.unspan(body)) with
                | (ExprAwait(_), ExprLet("second", _, _, [], None, [])) -> Unit
                | _ -> test.fail("expected chained await bindings")
        | _ -> test.fail("expected await binding")

let checkRecordUpdate unit =
    match "point with x = 1, y = 2"
    |> ParserExpressionTests.expectClean
    |> ParserExpressionTests.unspan with
        | ExprRecordUpdate(_, ("x", _) :: ("y", _) :: []) -> Unit
        | _ -> test.fail("expected record update")

let checkHandler unit =
    match "handle Clock.now(Unit) with | Clock.now(value) -> value | return(value) -> value"
    |> ParserExpressionTests.expectClean
    |> ParserExpressionTests.unspan with
        | ExprHandle(_, (Some("Clock"), "now", _parameters, _) :: (None, "return", _, _) :: []) -> Unit
        | _ -> test.fail("expected operation and return handler arms")

let checkMissingElse unit =
    match parseExpression("if true then 1") with
        | ExpressionParseResult { expression = _expression, diagnostics = diagnostic :: _ } ->
            test.assertEqual(
                "Expected Else but found EOF.",
                diagnostic.message
            )
        | _ -> test.fail("expected missing-else diagnostic")

let run unit =
    unit
    |> checkConditional
    |> checkLambda
    |> checkRecursiveBinding
    |> checkResultBinding
    |> checkAwaitBinding
    |> checkRecordUpdate
    |> checkHandler
    |> checkMissingElse
