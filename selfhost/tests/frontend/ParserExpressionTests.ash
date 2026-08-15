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

let checkPrecedence unit =
    match "1 + 2 * 3"
    |> expectClean
    |> unspan with
        | ExprAdd(left, right) ->
            match (unspan(left), unspan(right)) with
                | (ExprInt(1), ExprMultiply(middle, last)) ->
                    match (unspan(middle), unspan(last)) with
                        | (ExprInt(2), ExprInt(3)) -> Unit
                        | _ -> test.fail("expected multiplication operands")
                | _ -> test.fail("expected precedence tree")
        | _ -> test.fail("expected addition root")

let checkApplication unit =
    match "Module.map(f)([1, 2])"
    |> expectClean
    |> unspan with
        | ExprCall(firstCall, listArgument, false) ->
            match (unspan(firstCall), unspan(listArgument)) with
                | (ExprCall(qualified, functionArgument, false), ExprList(_elements)) ->
                    match (unspan(qualified), unspan(functionArgument)) with
                        | (ExprQualifiedVar("Module", "map"), ExprVar("f")) -> Unit
                        | _ -> test.fail("expected qualified call")
                | _ -> test.fail("expected curried arguments")
        | _ -> test.fail("expected call expression")

let checkCons unit =
    match "1 :: 2 :: []"
    |> expectClean
    |> unspan with
        | ExprCons(head, tail) ->
            match (unspan(head), unspan(tail)) with
                | (ExprInt(1), ExprCons(_, _)) -> Unit
                | _ -> test.fail("expected right-associative cons")
        | _ -> test.fail("expected cons expression")

let checkUnexpectedToken unit =
    match parseExpression("1 2 )") with
        | ExpressionParseResult { expression = _expression, diagnostics = diagnostic :: _ } -> test.assertEqual("Unexpected token after end of expression: RParen.")(diagnostic.message)
        | _ -> test.fail("expected trailing-token diagnostic")

let run unit =
    unit
    |> checkPrecedence
    |> checkApplication
    |> checkCons
    |> checkUnexpectedToken
