import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import ParserExpressionTests
let unspanPattern pattern =
    match pattern with
        | PatternAt(_span, inner) -> inner
        | _ -> pattern

let run unit =
    (let matched = ParserExpressionTests.expectClean("match value with | Some(x) when x > 0 -> x | None -> 0")
    in
        let matchedChecked =
            match ParserExpressionTests.unspan(matched) with
                | ExprMatch(scrutinee, first :: second :: [], Some(position)) ->
                    match (ParserExpressionTests.unspan(scrutinee), first, second) with
                        | (ExprVar("value"), (firstPattern, _, Some(_)), (secondPattern, _, None)) ->
                            match (unspanPattern(firstPattern), unspanPattern(secondPattern)) with
                                | (PatternConstructor("Some", PatternAt(_, PatternVar("x")) :: []), PatternVar("None")) -> test.assertEqual(0)(position)
                                | _ -> test.fail("expected constructor patterns")
                        | _ -> test.fail("expected guarded match cases")
                | _ -> test.fail("expected match")
        in
            let complex = ParserExpressionTests.expectClean("match item with | ((head as first) :: tail, Point { x = _, y = 2 }) | ([], _) -> first")
            in
                let complexChecked =
                    match ParserExpressionTests.unspan(complex) with
                        | ExprMatch(_, (pattern, _, None) :: [], _) ->
                            match unspanPattern(pattern) with
                                | PatternOr(_alternatives) -> Unit
                                | _ -> test.fail("expected or-pattern")
                        | _ -> test.fail("expected complex match")
                in
                    let letPattern = ParserExpressionTests.expectClean("let (left, right) = pair in left")
                    in
                        let letPatternChecked =
                            match ParserExpressionTests.unspan(letPattern) with
                                | ExprMatch(_, (pattern, _, None) :: [], _) ->
                                    match unspanPattern(pattern) with
                                        | PatternTuple(_elements) -> Unit
                                        | _ -> test.fail("expected tuple let-pattern")
                                | _ -> test.fail("expected let-pattern desugaring")
                        in
                            let refutable = parseExpression("let (Some(value), other) = item in value")
                            in
                                match refutable.diagnostics with
                                    | diagnostic :: _ -> test.assertEqual("Refutable pattern in let binding. Only irrefutable patterns (variable, wildcard, tuple, cons) are allowed — use 'match' for refutable patterns.")(diagnostic.message)
                                    | [] -> test.fail("expected refutable let-pattern diagnostic"))
