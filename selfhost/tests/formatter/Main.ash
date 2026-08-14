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

let patternFrom source =
    source
    |> expressionFrom
    |> firstMatchPattern

let programFrom source =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } -> program
        | _ -> test.fail("program source should parse")

let assertProgram expected source =
    (let actual =
        source
        |> programFrom
        |> formatProgram
    in
        if expected != actual
        then test.fail("expected program:\n" + expected + "actual program:\n" + actual)
        else
            actual
            |> programFrom
            |> formatProgram
            |> test.assertEqual(actual))

let assertExpression expected source =
    (let actual =
        source
        |> expressionFrom
        |> formatExpression
    in
        if expected == actual
        then Unit
        else test.fail("expected formatter output:\n" + expected + "actual formatter output:\n" + actual))

let assertIdempotent source =
    (let first =
        source
        |> expressionFrom
        |> formatExpression
    in
        first
        |> expressionFrom
        |> formatExpression
        |> test.assertEqual(first))

let assertPattern expected pattern =
    (let actual = formatPattern(pattern)
    in
        if expected == actual
        then Unit
        else test.fail("expected pattern: " + expected + "\nactual pattern: " + actual + "\nactual syntax: " + Ashes.Trait.Show.show(pattern)))

let run unit =
    unit
    |> (given (_) -> assertProgram("let a = 1\n\nlet b = 2\n")("let a=1\nlet b=2"))
    |> (given (_) -> assertProgram("export (\n    value run,\n    type Result(..),\n    module Internal,\n)\n\ntype alias Identity(a) = a\n\ntype UserId = UserId(Int)\n    deriving {Eq}\n\ntype Result(e, a) =\n    | Ok(a)\n    | Error(e)\n\ntype Point =\n    | x: Int\n    | y: Int\n\nlet run value = value\n\nrun(42)\n")("export(value run,type Result(..),module Internal)\ntype alias Identity(a)=a\ntype UserId=UserId(Int) deriving {Eq}\ntype Result(e,a)=|Ok(a)|Error(e)\ntype Point=|x:Int|y:Int\nlet run value=value\nrun(42)"))
    |> (given (_) -> assertProgram("external type Handle resource destructor closeHandle\nexternal read(borrow *UInt8, consume FfiBuffer(UInt8), out Int) -> FfiStr(nullable owned freeText) needs {Clock} = \"read_native\"\n\n0\n")("external type Handle resource destructor closeHandle\nexternal read(borrow *UInt8,consume FfiBuffer(UInt8),out Int)->FfiStr(nullable owned freeText) needs {Clock}=\"read_native\"\n0"))
    |> (given (_) -> assertProgram("capability State(a) =\n    | get : Unit -> a\n    | put : a -> Unit\n\nprovide State(Int) =\n    | get = 0\n    | put =\n        given (value) -> Unit\n\ntrait Display(a) requires {Eq(a)} =\n    | display : a -> Str\n    | fallback : a -> Str =\n        given (value) -> \"?\"\n\nimplement Display(Int) =\n    | display =\n        given (value) -> \"int\"\n\n0\n")("capability State(a)=|get:Unit->a|put:a->Unit\nprovide State(Int)=|get=0|put=given value->Unit\ntrait Display(a) requires {Eq(a)}=|display:a->Str|fallback:a->Str=given value->\"?\"\nimplement Display(Int)=|display=given value->\"int\"\n0"))
    |> (given (_) -> assertProgram("let recursive even n = odd n\nand odd n = even n\n")("let recursive even n=odd n\nand odd n=even n"))
    |> (given (_) -> assertProgram("let compare : a -> a -> Bool requires {Eq(a), Show(a)} =\n    given (left) ->\n        given (right) -> true\n")("let compare : a->a->Bool requires {Show(a),Eq(a)}=given(left,right)->true"))
    |> (given (_) -> assertExpression("1 + 2 * 3\n")("1+2*3"))
    |> (given (_) -> assertExpression("(1 + 2) * 3\n")("(1+2)*3"))
    |> (given (_) -> assertExpression("map transform values\n")("map transform values"))
    |> (given (_) -> assertExpression("if ready\nthen run(Unit)\nelse stop(Unit)\n")("if ready then run(Unit) else stop(Unit)"))
    |> (given (_) -> assertExpression("given (left) ->\n    given (right) -> left + right\n")("given (left, right)->left+right"))
    |> (given (_) -> assertExpression("let add x y = x + y\nin add(1)(2)\n")("let add x y=x+y in add(1)(2)"))
    |> (given (_) -> assertExpression("18446744073709551615u64\n")("18446744073709551615u64"))
    |> (given (_) -> assertExpression("point with x = 5, y = 6\n")("point with x=5,y=6"))
    |> (given (_) ->
        "a -> List(a) needs {ConsoleIO | e}"
        |> typeFrom
        |> formatTypeExpression
        |> test.assertEqual("a -> List(a) needs {ConsoleIO | e}"))
    |> (given (_) ->
        "A -> (B -> C) needs {Log}"
        |> typeFrom
        |> formatTypeExpression
        |> test.assertEqual("A -> (B -> C) needs {Log}"))
    |> (given (_) ->
        "match value with | Some(head :: tail) as items | None -> items"
        |> patternFrom
        |> assertPattern("Some(head :: tail) as items | None"))
    |> (given (_) -> assertIdempotent("match value with | Some(x) when x > 0 -> x | None -> 0"))
    |> (given (_) -> Ashes.IO.print("all self-hosted formatter tests passed"))

run(Unit)
