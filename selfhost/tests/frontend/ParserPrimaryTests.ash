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

let run unit =
    (let unsigned = ParserExpressionTests.expectClean("18446744073709551615u64")
    in
        let unsignedChecked =
            match ParserExpressionTests.unspan(unsigned) with
                | ExprUInt(value, bits, text) ->
                    if value != -1
                    then test.fail("expected u64 bit value")
                    else
                        if bits != 64
                        then test.fail("expected u64 width")
                        else test.assertEqual("18446744073709551615u64")(text)
                | _ -> test.fail("expected u64 literal")
        in
            let literals = ParserExpressionTests.expectClean("(99N, 3.14, \"hello\", '😀', true, false)")
            in
                let elements = tupleElements(literals)
                in
                    let bigChecked =
                        match ParserExpressionTests.unspan(expressionAt(0)(elements)) with
                            | ExprBigInt(value) ->
                                if value == "99"
                                then Unit
                                else test.fail("expected bigint digits")
                            | _ -> test.fail("expected bigint literal")
                    in
                        let floatChecked =
                            match ParserExpressionTests.unspan(expressionAt(1)(elements)) with
                                | ExprFloat(value, text) ->
                                    if value != 3.14
                                    then test.fail("expected float value")
                                    else
                                        if text == "3.14"
                                        then Unit
                                        else test.fail("expected float text")
                                | _ -> test.fail("expected float literal")
                        in
                            let stringChecked =
                                match ParserExpressionTests.unspan(expressionAt(2)(elements)) with
                                    | ExprString(value) ->
                                        if value == "hello"
                                        then Unit
                                        else test.fail("expected string value")
                                    | _ -> test.fail("expected string literal")
                            in
                                let runeChecked =
                                    match ParserExpressionTests.unspan(expressionAt(3)(elements)) with
                                        | ExprRune(value) ->
                                            if value == 128512
                                            then Unit
                                            else test.fail("expected rune value")
                                        | _ -> test.fail("expected rune literal")
                                in
                                    let booleansChecked =
                                        match (ParserExpressionTests.unspan(expressionAt(4)(elements)), ParserExpressionTests.unspan(expressionAt(5)(elements))) with
                                            | (ExprBool(truth), ExprBool(falsehood)) ->
                                                if !truth
                                                then test.fail("expected true literal")
                                                else
                                                    if falsehood
                                                    then test.fail("expected false literal")
                                                    else Unit
                                            | _ -> test.fail("expected boolean literals")
                                    in
                                        let whitespaceCall = ParserExpressionTests.expectClean("map transform values")
                                        in
                                            let whitespaceCallChecked =
                                                match ParserExpressionTests.unspan(whitespaceCall) with
                                                    | ExprCall(first, values, true) ->
                                                        match (ParserExpressionTests.unspan(first), ParserExpressionTests.unspan(values)) with
                                                            | (ExprCall(_, _, true), ExprVar(name)) ->
                                                                if name == "values"
                                                                then Unit
                                                                else test.fail("expected values argument")
                                                            | _ -> test.fail("expected curried whitespace application")
                                                    | _ -> test.fail("expected whitespace call")
                                            in
                                                let recordChecked = assertRecord(ParserExpressionTests.expectClean("Point(x = 1, y = 2)"))
                                                in
                                                    let sourceSpan = ParserExpressionTests.expectClean("\"é\" + 1")
                                                    in
                                                        let spanChecked =
                                                            match sourceSpan with
                                                                | ExprAt(span, _) ->
                                                                    if span.start != 0
                                                                    then test.fail("expected expression span start")
                                                                    else
                                                                        if span.end == 8
                                                                        then Unit
                                                                        else test.fail("expected expression span end")
                                                                | _ -> test.fail("expected expression span")
                                                        in
                                                            let lowerRecord = parseExpression("point(x = 1)")
                                                            in
                                                                match lowerRecord.diagnostics with
                                                                    | diagnostic :: [] ->
                                                                        if diagnostic.message == "Named arguments are only allowed in record construction."
                                                                        then Unit
                                                                        else test.fail("expected named-argument diagnostic message")
                                                                    | _ -> test.fail("expected named-argument diagnostic"))
