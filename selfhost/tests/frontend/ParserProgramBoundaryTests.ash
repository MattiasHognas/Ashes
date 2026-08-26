import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import ParserExpressionTests
import ParserProgramTests
let expectNoDiagnostics result =
    match result with
        | ProgramParseResult { program = syntax, diagnostics = diagnostics } ->
            let checked = test.assertEqual([])(diagnostics)
            in syntax

let expectDiagnostics source =
    match parseProgram(source) with
        | ProgramParseResult { program = _program, diagnostics = _diagnostic :: _tail } -> Unit
        | _ -> test.fail("expected parser diagnostics")

let checkLoneExpression unit =
    match "42"
    |> parseProgram
    |> expectNoDiagnostics with
        | ProgramSyntax { items = [], body = Some(body) } ->
            match ParserExpressionTests.unspan(body) with
                | ExprInt(42) -> Unit
                | _ -> test.fail("expected integer body")
        | _ -> test.fail("expected lone trailing expression")

let checkConsecutiveDeclarations unit =
    match "let x = 1\nlet y = 2"
    |> parseProgram
    |> expectNoDiagnostics with
        | ProgramSyntax { items = first :: second :: [], body = None } ->
            match (ParserProgramTests.unspanTopLevel(first), ParserProgramTests.unspanTopLevel(second)) with
                | (TopLevelLet(LetBindingSyntax { name = "x", value = _firstValue, sugarParameters = [], typeAnnotation = None, requirements = [] }, false), TopLevelLet(LetBindingSyntax { name = "y", value = _secondValue, sugarParameters = [], typeAnnotation = None, requirements = [] }, false)) -> Unit
                | _ -> test.fail("expected two flat declarations")
        | _ -> test.fail("expected declaration-only program")

let checkNestedLetBody unit =
    match "let x = 1 in x"
    |> parseProgram
    |> expectNoDiagnostics with
        | ProgramSyntax { items = [], body = Some(body) } ->
            match ParserExpressionTests.unspan(body) with
                | ExprLet("x", _value, _body, [], None, []) -> Unit
                | _ -> test.fail("expected nested let body")
        | _ -> test.fail("expected expression program")

let checkIndentedTrailingExpression unit =
    match "let a = X.test(1)\n    Ashes.IO.print(a)"
    |> parseProgram
    |> expectNoDiagnostics with
        | ProgramSyntax { items = item :: [], body = Some(body) } ->
            let bindingChecked =
                match ParserProgramTests.unspanTopLevel(item) with
                    | TopLevelLet(LetBindingSyntax { name = "a", value = value, sugarParameters = [], typeAnnotation = None, requirements = [] }, false) ->
                        match ParserExpressionTests.unspan(value) with
                            | ExprCall(_, _, false, _layout) -> Unit
                            | _ -> test.fail("expected completed call binding")
                    | _ -> test.fail("expected flat binding")
            in
                match ParserExpressionTests.unspan(body) with
                    | ExprCall(_, _, false, _layout) -> Unit
                    | _ -> test.fail("expected trailing call")
        | _ -> test.fail("expected indented trailing expression")

let checkContinuationAfterCompletedCall unit =
    match "let f x =\n    if g(x)\n    then 1\n    else 2\n\nlet h y =\n    k(y)\n    |> m\n\nlet z = f(1)"
    |> parseProgram
    |> expectNoDiagnostics with
        | ProgramSyntax { items = first :: second :: third :: [], body = None } ->
            match (ParserProgramTests.unspanTopLevel(first), ParserProgramTests.unspanTopLevel(second), ParserProgramTests.unspanTopLevel(third)) with
                | (TopLevelLet(LetBindingSyntax { name = "f" }, false), TopLevelLet(LetBindingSyntax { name = "h" }, false), TopLevelLet(LetBindingSyntax { name = "z" }, false)) -> Unit
                | _ -> test.fail("expected the three flat declarations f, h, and z")
        | _ -> test.fail("a then/else or pipe line after a completed call must continue the declaration value")

let run unit =
    unit
    |> checkLoneExpression
    |> checkConsecutiveDeclarations
    |> checkNestedLetBody
    |> checkIndentedTrailingExpression
    |> checkContinuationAfterCompletedCall
    |> (given (_) -> expectDiagnostics(""))
    |> (given (_) -> expectDiagnostics("and x = 1"))
    |> (given (_) -> expectDiagnostics("let x = 1 and y = 2"))
    |> (given (_) -> expectDiagnostics("let x = let y = 2 in y"))
    |> (given (_) -> expectDiagnostics("type Empty = | Empty deriving {}"))
    |> (given (_) -> expectDiagnostics("type Bad = | x: Int | Other(Int)"))
