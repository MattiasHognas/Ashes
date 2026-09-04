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

let checkMatchScrutinee unit =
    match "match match x with | 0 -> \"zero\" | _ -> \"other\" with | \"zero\" -> true | _ -> false"
    |> ParserExpressionTests.expectClean
    |> ParserExpressionTests.unspan with
        | ExprMatch(scrutinee, (_, outerFirst, None) :: (_, outerSecond, None) :: [], _) ->
            match (ParserExpressionTests.unspan(outerFirst), ParserExpressionTests.unspan(outerSecond), ParserExpressionTests.unspan(scrutinee)) with
                | (ExprBool(true), ExprBool(false), ExprMatch(inner, (_, innerFirst, None) :: (_, innerSecond, None) :: [], _)) ->
                    match (ParserExpressionTests.unspan(inner), ParserExpressionTests.unspan(innerFirst), ParserExpressionTests.unspan(innerSecond)) with
                        | (ExprVar("x"), ExprString("zero"), ExprString("other")) -> Unit
                        | _ -> test.fail("expected the inner match arms to end at the outer with")
                | _ -> test.fail("expected inner match as scrutinee")
        | _ -> test.fail("expected outer match over a match scrutinee")

let checkConditionalScrutinee unit =
    match "match if flag then 1 else 2 with | 1 -> true | _ -> false"
    |> ParserExpressionTests.expectClean
    |> ParserExpressionTests.unspan with
        | ExprMatch(scrutinee, (_, first, None) :: (_, second, None) :: [], _) ->
            match (ParserExpressionTests.unspan(scrutinee), ParserExpressionTests.unspan(first), ParserExpressionTests.unspan(second)) with
                | (ExprIf(_, _, _), ExprBool(true), ExprBool(false)) -> Unit
                | _ -> test.fail("expected conditional as scrutinee")
        | _ -> test.fail("expected match over a conditional scrutinee")

let checkRecordUpdateScrutineeNeedsParentheses unit =
    match parseExpression("match p with x = 1 with | _ -> 0") with
        | ExpressionParseResult { expression = _expression, diagnostics = diagnostic :: _ } ->
            test.assertEqual(
                "Expected Arrow but found Equals.",
                diagnostic.message
            )
        | _ -> test.fail("expected the scrutinee to end at the first with")

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
    |> checkMatchScrutinee
    |> checkConditionalScrutinee
    |> checkRecordUpdateScrutineeNeedsParentheses
    |> checkMissingElse
