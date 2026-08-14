import Ashes.Test as test
import AshesCompiler.Frontend.Lexer
import AshesCompiler.Frontend.Token
import LexerCoreTests
let literalFirstDiagnostic source =
    (let result = tokenize(source)
    in
        match result.diagnostics with
            | head :: _ -> head
            | [] -> test.fail("expected lexer diagnostic"))

let assertRune (source: Str) (expected: Int) =
    (let result = tokenize(source)
    in
        let diagnosticsChecked = test.assertEqual([])(result.diagnostics)
        in
            let token = LexerCoreTests.lexerTokenAt(0)(result.tokens)
            in test.assertEqual(expected)(token.intValue))

let assertFirstText source expected =
    (let result = tokenize(source)
    in
        let token = LexerCoreTests.lexerTokenAt(0)(result.tokens)
        in test.assertEqual(expected)(token.text))

let assertDiagnosticMessage source expected =
    (let diagnostic = literalFirstDiagnostic(source)
    in test.assertEqual(expected)(diagnostic.message))

let run unit =
    (let result = tokenize("12345 3.14 99N 255u8 65535u16 4294967295u32 18446744073709551615u64 \"a\\\\b\\\"c\\nd\\re\\tf\" '\\u{1F600}'")
    in
        let diagnosticsChecked = test.assertEqual([])(result.diagnostics)
        in
            let integer = LexerCoreTests.lexerTokenAt(0)(result.tokens)
            in
                let floating = LexerCoreTests.lexerTokenAt(1)(result.tokens)
                in
                    let big = LexerCoreTests.lexerTokenAt(2)(result.tokens)
                    in
                        let unsigned8 = LexerCoreTests.lexerTokenAt(3)(result.tokens)
                        in
                            let unsigned16 = LexerCoreTests.lexerTokenAt(4)(result.tokens)
                            in
                                let unsigned32 = LexerCoreTests.lexerTokenAt(5)(result.tokens)
                                in
                                    let unsigned64 = LexerCoreTests.lexerTokenAt(6)(result.tokens)
                                    in
                                        let string = LexerCoreTests.lexerTokenAt(7)(result.tokens)
                                        in
                                            let rune = LexerCoreTests.lexerTokenAt(8)(result.tokens)
                                            in
                                                let integerChecked = test.assertEqual(Token(kind = Int, text = "12345", intValue = 12345, floatValue = 0.0, position = 0, length = 5))(integer)
                                                in
                                                    let floatingChecked = test.assertEqual(3.14)(floating.floatValue)
                                                    in
                                                        let bigChecked = test.assertEqual(Token(kind = BigInt, text = "99", intValue = 0, floatValue = 0.0, position = 11, length = 3))(big)
                                                        in
                                                            let unsigned8Checked = test.assertEqual(255)(unsigned8.intValue)
                                                            in
                                                                let unsigned16Checked = test.assertEqual(65535)(unsigned16.intValue)
                                                                in
                                                                    let unsigned32Checked = test.assertEqual(4294967295)(unsigned32.intValue)
                                                                    in
                                                                        let unsigned64Checked = test.assertEqual(-1)(unsigned64.intValue)
                                                                        in
                                                                            let stringChecked = test.assertEqual("a\\b\"c\nd\re\tf")(string.text)
                                                                            in
                                                                                let runeChecked = test.assertEqual(128512)(rune.intValue)
                                                                                in
                                                                                    let suffixBoundaryChecked = LexerCoreTests.assertLexerKinds(["Int", "Ident", "Int", "Ident", "Int", "Ident", "EOF"])("255u81 255u164 123u8abc")
                                                                                    in
                                                                                        let dottedChecked = LexerCoreTests.assertLexerKinds(["Int", "Dot", "Ident", "EOF"])("42.x")
                                                                                        in
                                                                                            let emptyStringChecked = assertFirstText("\"\"")("")
                                                                                            in
                                                                                                let unknownEscapeChecked = assertFirstText("\"\\a\"")("a")
                                                                                                in
                                                                                                    let allEscapesChecked = assertFirstText("\"\\n\\r\\t\\\\\\\"\"")("\n\r\t\\\"")
                                                                                                    in
                                                                                                        let slashRuneChecked = assertRune("'\\\\'")(92)
                                                                                                        in
                                                                                                            let quoteRuneChecked = assertRune("'\\''")(39)
                                                                                                            in
                                                                                                                let newlineRuneChecked = assertRune("'\\n'")(10)
                                                                                                                in
                                                                                                                    let returnRuneChecked = assertRune("'\\r'")(13)
                                                                                                                    in
                                                                                                                        let tabRuneChecked = assertRune("'\\t'")(9)
                                                                                                                        in
                                                                                                                            let nulRuneChecked = assertRune("'\\0'")(0)
                                                                                                                            in
                                                                                                                                let literalRuneChecked = assertRune("'😀'")(128512)
                                                                                                                                in
                                                                                                                                    let unterminatedChecked = assertDiagnosticMessage("\"abc")("Unterminated string literal.")
                                                                                                                                    in
                                                                                                                                        let trailingSlashChecked = assertDiagnosticMessage("\"abc\\")("Unterminated string literal.")
                                                                                                                                        in
                                                                                                                                            let integerOverflowChecked = assertDiagnosticMessage("999999999999999999999999999999999999999")("Invalid integer literal: 999999999999999999999999999999999999999.")
                                                                                                                                            in
                                                                                                                                                let u8RangeChecked = assertDiagnosticMessage("256u8")("Unsigned integer literal out of range for u8: 256u8.")
                                                                                                                                                in
                                                                                                                                                    let u16RangeChecked = assertDiagnosticMessage("65536u16")("Unsigned integer literal out of range for u16: 65536u16.")
                                                                                                                                                    in
                                                                                                                                                        let u32RangeChecked = assertDiagnosticMessage("4294967296u32")("Unsigned integer literal out of range for u32: 4294967296u32.")
                                                                                                                                                        in
                                                                                                                                                            let u64RangeChecked = assertDiagnosticMessage("18446744073709551616u64")("Unsigned integer literal out of range for u64: 18446744073709551616u64.")
                                                                                                                                                            in
                                                                                                                                                                let emptyRuneChecked = assertDiagnosticMessage("''")("A rune literal must contain exactly one valid Unicode scalar value.")
                                                                                                                                                                in
                                                                                                                                                                    let multipleRuneChecked = assertDiagnosticMessage("'ab'")("A rune literal must contain exactly one valid Unicode scalar value.")
                                                                                                                                                                    in
                                                                                                                                                                        let surrogateRuneChecked = assertDiagnosticMessage("'\\u{D800}'")("A rune literal must contain exactly one valid Unicode scalar value.")
                                                                                                                                                                        in
                                                                                                                                                                            let largeRuneChecked = assertDiagnosticMessage("'\\u{110000}'")("A rune literal must contain exactly one valid Unicode scalar value.")
                                                                                                                                                                            in Unit)
