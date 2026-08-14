import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import ParserExpressionTests
let run unit =
    (let conditional = ParserExpressionTests.expectClean("if true then 1 else 2")
    in
        let conditionalChecked =
            match ParserExpressionTests.unspan(conditional) with
                | ExprIf(condition, thenBranch, elseBranch) ->
                    match (ParserExpressionTests.unspan(condition), ParserExpressionTests.unspan(thenBranch), ParserExpressionTests.unspan(elseBranch)) with
                        | (ExprBool(true), ExprInt(1), ExprInt(2)) -> Unit
                        | _ -> test.fail("expected conditional branches")
                | _ -> test.fail("expected conditional")
        in
            let lambda = ParserExpressionTests.expectClean("given (x, y) -> x + y")
            in
                let lambdaChecked =
                    match ParserExpressionTests.unspan(lambda) with
                        | ExprLambda("x", inner, None) ->
                            match ParserExpressionTests.unspan(inner) with
                                | ExprLambda("y", _, None) -> Unit
                                | _ -> test.fail("expected nested lambda")
                        | _ -> test.fail("expected lambda")
                in
                    let binding = ParserExpressionTests.expectClean("let recursive add x y = x + y in add(1)(2)")
                    in
                        let bindingChecked =
                            match ParserExpressionTests.unspan(binding) with
                                | ExprLetRecursive("add", value, _, "x" :: "y" :: [], None, []) ->
                                    match ParserExpressionTests.unspan(value) with
                                        | ExprLambda("x", _, None) -> Unit
                                        | _ -> test.fail("expected desugared binding lambda")
                                | _ -> test.fail("expected recursive binding")
                        in
                            let resultBinding = ParserExpressionTests.expectClean("let? value = result in value")
                            in
                                let resultBindingChecked =
                                    match ParserExpressionTests.unspan(resultBinding) with
                                        | ExprLetResult("value", _, _) -> Unit
                                        | _ -> test.fail("expected result binding")
                                in
                                    let awaitBinding = ParserExpressionTests.expectClean("let! first = one let! second = two in first + second")
                                    in
                                        let awaitBindingChecked =
                                            match ParserExpressionTests.unspan(awaitBinding) with
                                                | ExprLet("first", awaited, body, [], None, []) ->
                                                    match (ParserExpressionTests.unspan(awaited), ParserExpressionTests.unspan(body)) with
                                                        | (ExprAwait(_), ExprLet("second", _, _, [], None, [])) -> Unit
                                                        | _ -> test.fail("expected chained await bindings")
                                                | _ -> test.fail("expected await binding")
                                        in
                                            let update = ParserExpressionTests.expectClean("point with x = 1, y = 2")
                                            in
                                                let updateChecked =
                                                    match ParserExpressionTests.unspan(update) with
                                                        | ExprRecordUpdate(_, ("x", _) :: ("y", _) :: []) -> Unit
                                                        | _ -> test.fail("expected record update")
                                                in
                                                    let handled = ParserExpressionTests.expectClean("handle Clock.now(Unit) with | Clock.now(value) -> value | return(value) -> value")
                                                    in
                                                        let handledChecked =
                                                            match ParserExpressionTests.unspan(handled) with
                                                                | ExprHandle(_, (Some("Clock"), "now", _parameters, _) :: (None, "return", _, _) :: []) -> Unit
                                                                | _ -> test.fail("expected operation and return handler arms")
                                                        in
                                                            let missingElse = parseExpression("if true then 1")
                                                            in
                                                                match missingElse.diagnostics with
                                                                    | diagnostic :: _ -> test.assertEqual("Expected Else but found EOF.")(diagnostic.message)
                                                                    | [] -> test.fail("expected missing-else diagnostic"))
