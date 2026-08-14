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
        let identifier = unicodeTokenAt(0)(result.tokens)
        in
            let keyword = unicodeTokenAt(1)(result.tokens)
            in
                let bad = unicodeTokenAt(2)(result.tokens)
                in
                    let eof = unicodeTokenAt(3)(result.tokens)
                    in
                        let identifierChecked = test.assertEqual(Token(kind = Ident, text = "naïve", intValue = 0, floatValue = 0.0, position = 0, length = 6))(identifier)
                        in
                            let keywordChecked = test.assertEqual(Token(kind = Let, text = "let", intValue = 0, floatValue = 0.0, position = 8, length = 3))(keyword)
                            in
                                let badChecked = test.assertEqual(Token(kind = Bad, text = "😀", intValue = 0, floatValue = 0.0, position = 12, length = 4))(bad)
                                in
                                    let eofPositionChecked = test.assertEqual(16)(eof.position)
                                    in
                                        let diagnosticChecked = test.assertEqual([DiagnosticEntry(span = TextSpan(start = 12, end = 16), message = "Unexpected character: '😀'.", code = Some("ASH003"))])(result.diagnostics)
                                        in
                                            let suffixBoundary = tokenize("1u8é")
                                            in
                                                let suffixKinds =
                                                    suffixBoundary.tokens
                                                    |> LexerCoreTests.lexerKindNames
                                                    |> test.assertEqual(["Int", "Ident", "EOF"])
                                                in
                                                    let unicodeString = tokenize("\"café 😀\"")
                                                    in
                                                        let unicodeStringToken = unicodeTokenAt(0)(unicodeString.tokens)
                                                        in
                                                            let unicodeStringChecked = test.assertEqual("café 😀")(unicodeStringToken.text)
                                                            in Unit)
