import Ashes.Test as test
import AshesCompiler.Frontend.Lexer
import AshesCompiler.Frontend.Token
import LexerCoreTests
let unicodeTokenAt (index: Int) (tokens: List(Token)) =
    (let recursive find (remaining: List(Token)) (current: Int) =
        match remaining with
            | [] -> test.fail("token index out of range")
            | token :: tail ->
                if current == index
                then token
                else find(tail)(current + 1)
    in find(tokens)(0))

let run unit =
    (let result = tokenize("naïve let 😀")
    in
        result.tokens
        |> unicodeTokenAt(0)
        |> test.assertEqual(
            Token(kind = Ident, text = "naïve", intValue = 0, floatValue = 0.0, position = 0, length = 6)
        )
        |> (given (_) ->
            result.tokens
            |> unicodeTokenAt(1)
            |> test.assertEqual(
                Token(kind = Let, text = "let", intValue = 0, floatValue = 0.0, position = 8, length = 3)
            ))
        |> (given (_) ->
            result.tokens
            |> unicodeTokenAt(2)
            |> test.assertEqual(
                Token(kind = Bad, text = "😀", intValue = 0, floatValue = 0.0, position = 12, length = 4)
            ))
        |> (given (_) ->
            result.tokens
            |> unicodeTokenAt(3)
            |> (given (token) -> token.position)
            |> test.assertEqual(16))
        |> (given (_) ->
            test.assertEqual(
                [DiagnosticEntry(span = TextSpan(start = 12, end = 16), message = "Unexpected character: '😀'.", code = Some(
                    "ASH003"
                ))],
                result.diagnostics
            ))
        |> (given (_) ->
            "1u8é"
            |> tokenize
            |> (given (suffix) -> suffix.tokens)
            |> LexerCoreTests.lexerKindNames
            |> test.assertEqual(["Int", "Ident", "EOF"]))
        |> (given (_) ->
            "\"café 😀\""
            |> tokenize
            |> (given (unicodeString) -> unicodeString.tokens)
            |> unicodeTokenAt(0)
            |> (given (token) -> token.text)
            |> test.assertEqual("café 😀")))
