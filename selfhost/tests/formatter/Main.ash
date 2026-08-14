import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Formatter.Formatter
let expressionFrom source =
    match parseExpression(source) with
        | ExpressionParseResult { expression = expression, diagnostics = [] } -> expression
        | _ -> test.fail("source should parse")

let typeFrom source =
    match parseTypeExpression(source) with
        | TypeExpressionParseResult { typeExpression = typeExpression, diagnostics = [] } -> typeExpression
        | _ -> test.fail("type source should parse")

let recursive firstMatchPattern expression =
    match expression with
        | ExprAt(_span, inner) -> firstMatchPattern(inner)
        | ExprMatch(_value, (pattern, _body, _guard) :: _tail, _position) -> pattern
        | _ -> test.fail("source should contain a match case")

let patternFrom source = firstMatchPattern(expressionFrom(source))

let assertExpression expected source =
    (let actual = formatExpression(expressionFrom(source))
    in
        if expected == actual
        then Unit
        else test.fail("expected formatter output:\n" + expected + "actual formatter output:\n" + actual))

let assertIdempotent source =
    (let first = formatExpression(expressionFrom(source))
    in test.assertEqual(first)(formatExpression(expressionFrom(first))))

let assertPattern expected pattern =
    (let actual = formatPattern(pattern)
    in
        if expected == actual
        then Unit
        else test.fail("expected pattern: " + expected + "\nactual pattern: " + actual + "\nactual syntax: " + Ashes.Trait.Show.show(pattern)))

let run unit =
    (let precedenceChecked = assertExpression("1 + 2 * 3\n")("1+2*3")
    in
        let groupingChecked = assertExpression("(1 + 2) * 3\n")("(1+2)*3")
        in
            let callChecked = assertExpression("map transform values\n")("map transform values")
            in
                let conditionalChecked = assertExpression("if ready\nthen run(Unit)\nelse stop(Unit)\n")("if ready then run(Unit) else stop(Unit)")
                in
                    let lambdaChecked = assertExpression("given (left) -> given (right) -> left + right\n")("given (left, right)->left+right")
                    in
                        let letSugarChecked = assertExpression("let add x y = x + y\nin add(1)(2)\n")("let add x y=x+y in add(1)(2)")
                        in
                            let literalChecked = assertExpression("18446744073709551615u64\n")("18446744073709551615u64")
                            in
                                let recordChecked = assertExpression("point with x = 5, y = 6\n")("point with x=5,y=6")
                                in
                                    let typeChecked = test.assertEqual("a -> List(a) needs {ConsoleIO | e}")(formatTypeExpression(typeFrom("a -> List(a) needs {ConsoleIO | e}")))
                                    in
                                        let effectfulArrowChecked = test.assertEqual("A -> (B -> C) needs {Log}")(formatTypeExpression(typeFrom("A -> (B -> C) needs {Log}")))
                                        in
                                            let patternChecked = assertPattern("Some(head :: tail) as items | None")(patternFrom("match value with | Some(head :: tail) as items | None -> items"))
                                            in
                                                let idempotentChecked = assertIdempotent("match value with | Some(x) when x > 0 -> x | None -> 0")
                                                in Ashes.IO.print("all self-hosted formatter tests passed"))

run(Unit)
