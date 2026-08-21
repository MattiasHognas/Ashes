import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import ParserExpressionTests
let unspanTopLevel item =
    match item with
        | TopLevelAt(_span, inner) -> inner
        | _ -> item

let checkExport item =
    match unspanTopLevel(item) with
        | TopLevelExport(ExportDecl { items = ExportValue("run") :: ExportType("Result", ExportConstructorsAll) :: ExportModule("Internal") :: [] }) -> Unit
        | _ -> test.fail("expected export declaration")

let checkAlias item =
    match unspanTopLevel(item) with
        | TopLevelTypeAlias(TypeAliasDecl { name = "Identity", typeParameters = _parameters, target = _target }) -> Unit
        | _ -> test.fail("expected type alias")

let checkNominal item =
    match unspanTopLevel(item) with
        | TopLevelZeroCostType(ZeroCostTypeDecl { name = "UserId", typeParameters = _parameters, constructor = _constructor, derivingTraits = "Eq" :: [] }) -> Unit
        | _ -> test.fail("expected zero-cost type")

let checkAlgebraic item =
    match unspanTopLevel(item) with
        | TopLevelType(TypeDecl { name = "Result", typeParameters = _parameters, constructors = _constructors, isRecord = false, derivingTraits = [] }) -> Unit
        | _ -> test.fail("expected algebraic type")

let checkRecord item =
    match unspanTopLevel(item) with
        | TopLevelType(TypeDecl { name = "Point", typeParameters = [], constructors = TypeConstructor { name = _name, parameters = _parameters, fieldNames = fieldNames } :: [], isRecord = true, derivingTraits = [] }) ->
            test.assertEqual(
                ["x", "y"],
                fieldNames
            )
        | _ -> test.fail("expected record type")

let checkBinding item =
    match unspanTopLevel(item) with
        | TopLevelLet(LetBindingSyntax { name = "run", value = _value, sugarParameters = "value" :: [], typeAnnotation = None, requirements = [] }, false) -> Unit
        | _ -> test.fail("expected flat binding")

let checkBody body =
    match body with
        | Some(expression) ->
            match ParserExpressionTests.unspan(expression) with
                | ExprCall(_, _, false, _layout) -> Unit
                | _ -> test.fail("expected trailing call")
        | None -> test.fail("expected trailing body")

let run unit =
    (let source = "export(value run, type Result(..), module Internal)\ntype alias Identity(a) = a\ntype UserId = UserId(Int) deriving {Eq}\ntype Result(e, a) = | Ok(a) | Error(e)\ntype Point = | x: Int | y: Int\nlet run value = value\nrun(42)"
    in
        match parseProgram(source) with
            | ProgramParseResult { program = ProgramSyntax { items = exported :: alias :: nominal :: algebraic :: record :: binding :: [], body = programBody }, diagnostics = diagnostics } ->
                diagnostics
                |> test.assertEqual([])
                |> (given (_) -> checkExport(exported))
                |> (given (_) -> checkAlias(alias))
                |> (given (_) -> checkNominal(nominal))
                |> (given (_) -> checkAlgebraic(algebraic))
                |> (given (_) -> checkRecord(record))
                |> (given (_) -> checkBinding(binding))
                |> (given (_) -> checkBody(programBody))
            | _ -> test.fail("expected ordered top-level items"))
