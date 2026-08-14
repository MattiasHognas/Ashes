import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
let unspan expression =
    match expression with
        | ExprAt(_span, inner) -> inner
        | _ -> expression

let expectClean source =
    (let result = parseExpression(source)
    in
        let diagnosticsChecked = test.assertEqual([])(result.diagnostics)
        in result.expression)

let run unit =
    (let precedence = expectClean("1 + 2 * 3")
    in
        let precedenceChecked =
            match unspan(precedence) with
                | ExprAdd(left, right) ->
                    match (unspan(left), unspan(right)) with
                        | (ExprInt(1), ExprMultiply(middle, last)) ->
                            match (unspan(middle), unspan(last)) with
                                | (ExprInt(2), ExprInt(3)) -> Unit
                                | _ -> test.fail("expected multiplication operands")
                        | _ -> test.fail("expected precedence tree")
                | _ -> test.fail("expected addition root")
        in
            let application = expectClean("Module.map(f)([1, 2])")
            in
                let applicationChecked =
                    match unspan(application) with
                        | ExprCall(firstCall, listArgument, false) ->
                            match (unspan(firstCall), unspan(listArgument)) with
                                | (ExprCall(qualified, functionArgument, false), ExprList(_elements)) ->
                                    match (unspan(qualified), unspan(functionArgument)) with
                                        | (ExprQualifiedVar("Module", "map"), ExprVar("f")) -> Unit
                                        | _ -> test.fail("expected qualified call")
                                | _ -> test.fail("expected curried arguments")
                        | _ -> test.fail("expected call expression")
                in
                    let cons = expectClean("1 :: 2 :: []")
                    in
                        let consChecked =
                            match unspan(cons) with
                                | ExprCons(head, tail) ->
                                    match (unspan(head), unspan(tail)) with
                                        | (ExprInt(1), ExprCons(_, _)) -> Unit
                                        | _ -> test.fail("expected right-associative cons")
                                | _ -> test.fail("expected cons expression")
                        in
                            let unexpected = parseExpression("1 2 )")
                            in
                                match unexpected.diagnostics with
                                    | diagnostic :: _ -> test.assertEqual("Unexpected token after end of expression: RParen.")(diagnostic.message)
                                    | [] -> test.fail("expected trailing-token diagnostic"))
