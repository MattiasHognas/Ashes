import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Frontend.Token
import ParserExpressionTests
let expectNoDiagnostics source =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = diagnostics } ->
            let checked = test.assertEqual([])(diagnostics)
            in program

let checkFlatParenthesizedBlock unit =
    match expectNoDiagnostics("(let xs = [1, 2, 3]\nlet n = List.length(xs)\nprint(n))") with
        | ProgramSyntax { items = [], body = Some(body) } ->
            match ParserExpressionTests.unspan(body) with
                | ExprLet("xs", _xs, inner, [], None, []) ->
                    match ParserExpressionTests.unspan(inner) with
                        | ExprLet("n", _n, call, [], None, []) ->
                            match ParserExpressionTests.unspan(call) with
                                | ExprCall(_, _, false, _layout) -> Unit
                                | _ -> test.fail("expected flat-block trailing call")
                        | _ -> test.fail("expected inner flat binding")
                | _ -> test.fail("expected outer flat binding")
        | _ -> test.fail("expected parenthesized expression body")

// A parenthesized `let` after an operator, as the formatter lays it out: its value is a nested
// `let ... in` indented left of the outer `let`, which its own `in` closes, not the layout.
let checkParenthesizedLetWithNestedLetValue unit =
    match expectNoDiagnostics("let r =\n    \"q\" + (let g =\n        let s = \"x\"\n        in s\n    in g)\n\nr") with
        | ProgramSyntax { items = _binding :: [], body = Some(_body) } -> Unit
        | _ -> test.fail("expected one binding and a body")

// The same layout with a lambda value whose body starts on a line left of the `let`: after `->`
// the line continues the value, as stage 0's call loop is the only place the layout ends one.
let checkParenthesizedLetWithLambdaValueLeftOfTheLet unit =
    match expectNoDiagnostics("let r =\n    \"q\" + (let recursive f : List(Int) -> Str =\n        given (items: List(Int)) ->\n            match items with\n                | [] -> \"]\"\n                | _ :: tail -> f(tail)\n    in f([1]))\n\nr") with
        | ProgramSyntax { items = _binding :: [], body = Some(_body) } -> Unit
        | _ -> test.fail("expected one binding and a body")

let checkCapabilityBoundary unit =
    match expectNoDiagnostics(
        "capability Value(a) =\n    | get : Unit -> a\n\n(handle perform Value.get(Unit) with\n    | Value.get(_) -> resume(1)\n    | return(value) -> value)"
    ) with
        | ProgramSyntax { items = _capability :: [], body = Some(body) } ->
            match ParserExpressionTests.unspan(body) with
                | ExprHandle(_, _) -> Unit
                | _ -> test.fail("expected handle body")
        | _ -> test.fail("expected capability and body")

let expectDiagnostic source =
    match parseProgram(source) with
        | ProgramParseResult { program = _program, diagnostics = _diagnostic :: _tail } -> Unit
        | _ -> test.fail("expected parity diagnostic")

let recursive containsCode code diagnostics =
    match diagnostics with
        | [] -> false
        | DiagnosticEntry { span = _span, message = _message, code = Some(actual) } :: tail ->
            if actual == code
            then true
            else containsCode(code)(tail)
        | _diagnostic :: tail -> containsCode(code)(tail)

let expectDiagnosticCode code source =
    match parseProgram(source) with
        | ProgramParseResult { program = _program, diagnostics = diagnostics } ->
            diagnostics
            |> containsCode(code)
            |> test.assertEqual(true)

let run unit =
    unit
    |> checkFlatParenthesizedBlock
    |> checkParenthesizedLetWithNestedLetValue
    |> checkParenthesizedLetWithLambdaValueLeftOfTheLet
    |> checkCapabilityBoundary
    |> (given (_) -> expectDiagnosticCode("ASH045")("let value : out Int = 1\nvalue"))
    |> (given (_) -> expectDiagnosticCode("ASH046")("let value : FfiStr(borrowed) = 1\nvalue"))
    |> (given (_) -> expectDiagnosticCode("ASH040")("type Bad = Bad\n0"))
    |> (given (_) -> expectDiagnostic("type Point = { x: Int, y: Int }\n0"))
    |> (given (_) -> expectDiagnostic("Point { x = 1, y = 2 }"))
    |> (given (_) -> expectDiagnostic("{ p with x = 1 }"))
