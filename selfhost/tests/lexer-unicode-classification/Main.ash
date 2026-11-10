import AshesCompiler.Frontend.UnicodeClassification
import Ashes.Test as test
let recursive checkAll code mismatches reference =
    if code == 1114112
    then mismatches
    else
        let expected =
            code
            |> Ashes.Byte.get(reference)
            |> Ashes.Number.UInt.toInt
        in
            let actual =
                (if isUnicodeLetter(code)
                then 1
                else 0) + (if isUnicodeIdentifierCharacter(code)
                then 2
                else 0) + (if isUnicodeWhiteSpace(code)
                then 4
                else 0)
            in
                checkAll(code + 1)(mismatches + (if expected == actual
                then 0
                else 1))(reference)

match Ashes.IO.args with
    | path :: [] ->
        match Ashes.IO.File.readAllBytes(path) with
            | Error(message) -> Ashes.IO.panic(message)
            | Ok(reference) ->
                let _ =
                    reference
                    |> Ashes.Byte.length
                    |> test.assertEqual(1114112)
                in
                    let _ =
                        reference
                        |> checkAll(0)(0)
                        |> test.assertEqual(0)
                    in Ashes.IO.print("all Unicode classifications match bundled PCRE2")
    | _ -> Ashes.IO.panic("usage: lexer-unicode-classification <reference.bin from generate-lexer-unicode.sh>")
