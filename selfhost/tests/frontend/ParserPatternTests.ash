import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import ParserExpressionTests
let unspanPattern pattern =
    match pattern with
        | PatternAt(_span, inner) -> inner
        | _ -> pattern

let checkGuardedMatch unit =
    match "match value with | Some(x) when x > 0 -> x | None -> 0"
    |> ParserExpressionTests.expectClean
    |> ParserExpressionTests.unspan with
        | ExprMatch(scrutinee, first :: second :: [], Some(position)) ->
            match (ParserExpressionTests.unspan(scrutinee), first, second) with
                | (ExprVar("value"), (firstPattern, _, Some(_)), (secondPattern, _, None)) ->
                    match (unspanPattern(firstPattern), unspanPattern(secondPattern)) with
                        | (PatternConstructor("Some", PatternAt(_, PatternVar("x")) :: []), PatternVar("None")) ->
                            test.assertEqual(
                                0,
                                position
                            )
                        | _ -> test.fail("expected constructor patterns")
                | _ -> test.fail("expected guarded match cases")
        | _ -> test.fail("expected match")

let checkComplexPattern unit =
    match "match item with | ((head as first) :: tail, Point { x = _, y = 2 }) | ([], _) -> first"
    |> ParserExpressionTests.expectClean
    |> ParserExpressionTests.unspan with
        | ExprMatch(_, (pattern, _, None) :: [], _) ->
            match unspanPattern(pattern) with
                | PatternOr(_alternatives) -> Unit
                | _ -> test.fail("expected or-pattern")
        | _ -> test.fail("expected complex match")

let checkLetPattern unit =
    match "let (left, right) = pair in left"
    |> ParserExpressionTests.expectClean
    |> ParserExpressionTests.unspan with
        | ExprMatch(_, (pattern, _, None) :: [], _) ->
            match unspanPattern(pattern) with
                | PatternTuple(_elements) -> Unit
                | _ -> test.fail("expected tuple let-pattern")
        | _ -> test.fail("expected let-pattern desugaring")

let checkRefutableLetPattern unit =
    match parseExpression("let (Some(value), other) = item in value") with
        | ExpressionParseResult { expression = _expression, diagnostics = diagnostic :: _ } ->
            test.assertEqual(
                "Refutable pattern in let binding. Only irrefutable patterns (variable, wildcard, tuple, cons) are allowed — use 'match' for refutable patterns.",
                diagnostic.message
            )
        | _ -> test.fail("expected refutable let-pattern diagnostic")

let run unit =
    unit
    |> checkGuardedMatch
    |> checkComplexPattern
    |> checkLetPattern
    |> checkRefutableLetPattern
