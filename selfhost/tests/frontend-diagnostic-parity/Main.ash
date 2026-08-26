import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.DiagnosticSerialization
let fixturePath root name extension = root + "/" + name + extension

let readFixture path =
    match Ashes.IO.File.readText(path) with
        | Ok(value) -> value
        | Error(message) -> test.fail("could not read parity fixture " + path + ": " + message)

let checkFixture root name =
    (let source =
        ".source"
        |> fixturePath(root)(name)
        |> readFixture
    in
        let expected =
            ".diagnostics"
            |> fixturePath(root)(name)
            |> readFixture
        in
            match parseProgram(source) with
                | ProgramParseResult { program = program, diagnostics = diagnostics } ->
                    let actual =
                        serializeDiagnostics(Ashes.Collection.List.length(program.items))(diagnostics)
                    in
                        if actual == expected
                        then Unit
                        else
                            test.fail(
                                "diagnostic parity mismatch for " + name + "\nexpected:\n" + expected + "actual:\n" + actual
                            ))

match Ashes.IO.args with
    | root :: [] ->
        Unit
        |> (given (_) -> checkFixture(root)("lexer_unterminated_string"))
        |> (given (_) -> checkFixture(root)("lexer_invalid_float"))
        |> (given (_) -> checkFixture(root)("lexer_unsigned_out_of_range"))
        |> (given (_) -> checkFixture(root)("lexer_unexpected_character"))
        |> (given (_) -> checkFixture(root)("parser_expected_expression_recovery"))
        |> (given (_) -> checkFixture(root)("parser_expected_pattern"))
        |> (given (_) -> checkFixture(root)("parser_unexpected_token_after_program"))
        |> (given (_) -> checkFixture(root)("parser_refutable_pattern_in_let"))
        |> (given (_) -> checkFixture(root)("parser_and_without_let_recursive"))
        |> (given (_) -> checkFixture(root)("parser_type_needs_constructor"))
        |> (given (_) -> Ashes.IO.print("all self-hosted frontend diagnostic parity fixtures passed"))
    | _ -> Ashes.IO.panic("usage: frontend-diagnostic-parity <fixture-directory>")
