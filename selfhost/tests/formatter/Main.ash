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

let programFrom source =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } -> program
        | _ -> test.fail("program source should parse")

let assertProgram expected source =
    (let actual = formatProgram(programFrom(source))
    in
        if expected != actual
        then test.fail("expected program:\n" + expected + "actual program:\n" + actual)
        else test.assertEqual(actual)(formatProgram(programFrom(actual))))

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
    (let programChecked = assertProgram("let a = 1\n\nlet b = 2\n")("let a=1\nlet b=2")
    in
        let declarationProgramChecked = assertProgram("export (\n    value run,\n    type Result(..),\n    module Internal,\n)\n\ntype alias Identity(a) = a\n\ntype UserId = UserId(Int)\n    deriving {Eq}\n\ntype Result(e, a) =\n    | Ok(a)\n    | Error(e)\n\ntype Point =\n    | x: Int\n    | y: Int\n\nlet run value = value\n\nrun(42)\n")("export(value run,type Result(..),module Internal)\ntype alias Identity(a)=a\ntype UserId=UserId(Int) deriving {Eq}\ntype Result(e,a)=|Ok(a)|Error(e)\ntype Point=|x:Int|y:Int\nlet run value=value\nrun(42)")
        in
            let externalProgramChecked = assertProgram("external type Handle resource destructor closeHandle\nexternal read(borrow *UInt8, consume FfiBuffer(UInt8), out Int) -> FfiStr(nullable owned freeText) needs {Clock} = \"read_native\"\n\n0\n")("external type Handle resource destructor closeHandle\nexternal read(borrow *UInt8,consume FfiBuffer(UInt8),out Int)->FfiStr(nullable owned freeText) needs {Clock}=\"read_native\"\n0")
            in
                let capabilityProgramChecked = assertProgram("capability State(a) =\n    | get : Unit -> a\n    | put : a -> Unit\n\nprovide State(Int) =\n    | get = 0\n    | put =\n        given (value) -> Unit\n\ntrait Display(a) requires {Eq(a)} =\n    | display : a -> Str\n    | fallback : a -> Str =\n        given (value) -> \"?\"\n\nimplement Display(Int) =\n    | display =\n        given (value) -> \"int\"\n\n0\n")("capability State(a)=|get:Unit->a|put:a->Unit\nprovide State(Int)=|get=0|put=given value->Unit\ntrait Display(a) requires {Eq(a)}=|display:a->Str|fallback:a->Str=given value->\"?\"\nimplement Display(Int)=|display=given value->\"int\"\n0")
                in
                    let recursiveProgramChecked = assertProgram("let recursive even n = odd n\nand odd n = even n\n")("let recursive even n=odd n\nand odd n=even n")
                    in
                        let annotatedProgramChecked = assertProgram("let compare : a -> a -> Bool requires {Eq(a), Show(a)} =\n    given (left) ->\n        given (right) -> true\n")("let compare : a->a->Bool requires {Show(a),Eq(a)}=given(left,right)->true")
                        in
                            let precedenceChecked = assertExpression("1 + 2 * 3\n")("1+2*3")
                            in
                                let groupingChecked = assertExpression("(1 + 2) * 3\n")("(1+2)*3")
                                in
                                    let callChecked = assertExpression("map transform values\n")("map transform values")
                                    in
                                        let conditionalChecked = assertExpression("if ready\nthen run(Unit)\nelse stop(Unit)\n")("if ready then run(Unit) else stop(Unit)")
                                        in
                                            let lambdaChecked = assertExpression("given (left) ->\n    given (right) -> left + right\n")("given (left, right)->left+right")
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
