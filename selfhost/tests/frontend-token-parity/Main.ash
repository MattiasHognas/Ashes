import Ashes.Test as test
import AshesCompiler.Frontend.Lexer
import AshesCompiler.Frontend.TokenSerialization
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
            ".tokens"
            |> fixturePath(root)(name)
            |> readFixture
        in
            let result = tokenize(source)
            in
                result.diagnostics
                |> test.assertEqual([])
                |> (given (_) ->
                    let actual = serializeTokens(result.tokens)
                    in
                        if actual == expected
                        then Unit
                        else
                            test.fail(
                                "token parity mismatch for " + name + "\nexpected:\n" + expected + "actual:\n" + actual
                            )))

match Ashes.IO.args with
    | root :: [] ->
        Unit
        |> (given (_) -> checkFixture(root)("keywords"))
        |> (given (_) -> checkFixture(root)("literals"))
        |> (given (_) -> checkFixture(root)("operators-unicode"))
        |> (given (_) -> Ashes.IO.print("all self-hosted frontend token parity fixtures passed"))
    | _ -> Ashes.IO.panic("usage: frontend-token-parity <fixture-directory>")
