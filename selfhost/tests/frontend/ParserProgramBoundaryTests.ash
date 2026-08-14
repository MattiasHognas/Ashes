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
    match expectNoDiagnostics(parseProgram("42")) with
        | ProgramSyntax { items = [], body = Some(body) } ->
            match ParserExpressionTests.unspan(body) with
                | ExprInt(42) -> Unit
                | _ -> test.fail("expected integer body")
        | _ -> test.fail("expected lone trailing expression")

let checkConsecutiveDeclarations unit =
    match expectNoDiagnostics(parseProgram("let x = 1\nlet y = 2")) with
        | ProgramSyntax { items = first :: second :: [], body = None } ->
            match (ParserProgramTests.unspanTopLevel(first), ParserProgramTests.unspanTopLevel(second)) with
                | (TopLevelLet(LetBindingSyntax { name = "x", value = _firstValue, sugarParameters = [], typeAnnotation = None, requirements = [] }, false), TopLevelLet(LetBindingSyntax { name = "y", value = _secondValue, sugarParameters = [], typeAnnotation = None, requirements = [] }, false)) -> Unit
                | _ -> test.fail("expected two flat declarations")
        | _ -> test.fail("expected declaration-only program")

let checkNestedLetBody unit =
    match expectNoDiagnostics(parseProgram("let x = 1 in x")) with
        | ProgramSyntax { items = [], body = Some(body) } ->
            match ParserExpressionTests.unspan(body) with
                | ExprLet("x", _value, _body, [], None, []) -> Unit
                | _ -> test.fail("expected nested let body")
        | _ -> test.fail("expected expression program")

let checkIndentedTrailingExpression unit =
    match expectNoDiagnostics(parseProgram("let a = X.test(1)\n    Ashes.IO.print(a)")) with
        | ProgramSyntax { items = item :: [], body = Some(body) } ->
            let bindingChecked =
                match ParserProgramTests.unspanTopLevel(item) with
                    | TopLevelLet(LetBindingSyntax { name = "a", value = value, sugarParameters = [], typeAnnotation = None, requirements = [] }, false) ->
                        match ParserExpressionTests.unspan(value) with
                            | ExprCall(_, _, false) -> Unit
                            | _ -> test.fail("expected completed call binding")
                    | _ -> test.fail("expected flat binding")
            in
                match ParserExpressionTests.unspan(body) with
                    | ExprCall(_, _, false) -> Unit
                    | _ -> test.fail("expected trailing call")
        | _ -> test.fail("expected indented trailing expression")

let run unit =
    (let loneChecked = checkLoneExpression(Unit)
    in
        let consecutiveChecked = checkConsecutiveDeclarations(Unit)
        in
            let nestedChecked = checkNestedLetBody(Unit)
            in
                let indentedChecked = checkIndentedTrailingExpression(Unit)
                in
                    let emptyChecked = expectDiagnostics("")
                    in
                        let bareAndChecked = expectDiagnostics("and x = 1")
                        in
                            let groupedAndChecked = expectDiagnostics("let x = 1 and y = 2")
                            in
                                let unfinishedPyramidChecked = expectDiagnostics("let x = let y = 2 in y")
                                in
                                    let emptyDerivingChecked = expectDiagnostics("type Empty = | Empty deriving {}")
                                    in expectDiagnostics("type Bad = | x: Int | Other(Int)"))
