import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import ParserExpressionTests
let expressionAt : Int -> List(Expr) -> Expr needs {ConsoleIO} =
    given (index) ->
        given (expressions) ->
            let recursive find current remaining =
                match remaining with
                    | [] -> test.fail("expression index out of range")
                    | expression :: tail ->
                        if current == index
                        then expression
                        else find(current + 1)(tail)
            in find(0)(expressions)

let tupleElements : Expr -> List(Expr) needs {ConsoleIO} =
    given (expression) ->
        match ParserExpressionTests.unspan(expression) with
            | ExprTuple(elements) -> elements
            | _ -> test.fail("expected tuple expression")

let assertRecord : Expr -> Unit needs {ConsoleIO} =
    given (expression) ->
        match ParserExpressionTests.unspan(expression) with
            | ExprRecord(typeName, fields) ->
                match fields with
                    | (firstName, _firstValue) :: (secondName, _secondValue) :: [] ->
                        if typeName != "Point"
                        then test.fail("expected Point record")
                        else
                            if firstName != "x"
                            then test.fail("expected x field")
                            else
                                if secondName == "y"
                                then Unit
                                else test.fail("expected y field")
                    | _ -> test.fail("expected two record fields")
            | _ -> test.fail("expected record construction")

let checkUnsignedLiteral unit =
    match "18446744073709551615u64"
    |> ParserExpressionTests.expectClean
    |> ParserExpressionTests.unspan with
        | ExprUInt(value, bits, text) ->
            if value != -1
            then test.fail("expected u64 bit value")
            else
                if bits != 64
                then test.fail("expected u64 width")
                else test.assertEqual("18446744073709551615u64")(text)
        | _ -> test.fail("expected u64 literal")

let checkLiteralTuple unit =
    (let elements =
        "(99N, 3.14, \"hello\", '😀', true, false)"
        |> ParserExpressionTests.expectClean
        |> tupleElements
    in
        (match elements
        |> expressionAt(0)
        |> ParserExpressionTests.unspan with
            | ExprBigInt("99") -> Unit
            | _ -> test.fail("expected bigint literal"))
        |> (given (_) ->
            match elements
            |> expressionAt(1)
            |> ParserExpressionTests.unspan with
                | ExprFloat(value, text) -> test.assertEqual((3.14, "3.14"))((value, text))
                | _ -> test.fail("expected float literal"))
        |> (given (_) ->
            match elements
            |> expressionAt(2)
            |> ParserExpressionTests.unspan with
                | ExprString("hello") -> Unit
                | _ -> test.fail("expected string literal"))
        |> (given (_) ->
            match elements
            |> expressionAt(3)
            |> ParserExpressionTests.unspan with
                | ExprRune(128512) -> Unit
                | _ -> test.fail("expected rune literal"))
        |> (given (_) ->
            match (elements
            |> expressionAt(4)
            |> ParserExpressionTests.unspan, elements
            |> expressionAt(5)
            |> ParserExpressionTests.unspan) with
                | (ExprBool(true), ExprBool(false)) -> Unit
                | _ -> test.fail("expected boolean literals")))

let checkWhitespaceCall unit =
    match "map transform values"
    |> ParserExpressionTests.expectClean
    |> ParserExpressionTests.unspan with
        | ExprCall(first, values, true, _layout) ->
            match (ParserExpressionTests.unspan(first), ParserExpressionTests.unspan(values)) with
                | (ExprCall(_, _, true, _innerLayout), ExprVar("values")) -> Unit
                | _ -> test.fail("expected curried whitespace application")
        | _ -> test.fail("expected whitespace call")

let checkRecordAndSpan unit =
    "Point(x = 1, y = 2)"
    |> ParserExpressionTests.expectClean
    |> assertRecord
    |> (given (_) ->
        match ParserExpressionTests.expectClean("\"é\" + 1") with
            | ExprAt(span, _) -> test.assertEqual((0, 8))((span.start, span.end))
            | _ -> test.fail("expected expression span"))

let checkNamedArgumentDiagnostic unit =
    match parseExpression("point(x = 1)") with
        | ExpressionParseResult { expression = _expression, diagnostics = diagnostic :: [] } ->
            test.assertEqual(
                "Named arguments are only allowed in record construction.",
                diagnostic.message
            )
        | _ -> test.fail("expected named-argument diagnostic")

let run unit =
    unit
    |> checkUnsignedLiteral
    |> checkLiteralTuple
    |> checkWhitespaceCall
    |> checkRecordAndSpan
    |> checkNamedArgumentDiagnostic
