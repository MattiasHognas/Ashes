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
        result.diagnostics
        |> test.assertEqual([])
        |> (given (_) ->
            result.tokens
            |> LexerCoreTests.lexerTokenAt(0)
            |> (given (token) -> token.intValue)
            |> test.assertEqual(expected)))

let assertFirstText source expected =
    (let result = tokenize(source)
    in
        result.tokens
        |> LexerCoreTests.lexerTokenAt(0)
        |> (given (token) -> token.text)
        |> test.assertEqual(expected))

let assertDiagnosticMessage source expected =
    source
    |> literalFirstDiagnostic
    |> (given (diagnostic) -> diagnostic.message)
    |> test.assertEqual(expected)

let expectLiteralTokens unit =
    (let result = tokenize("12345 3.14 99N 255u8 65535u16 4294967295u32 18446744073709551615u64 \"a\\\\b\\\"c\\nd\\re\\tf\" '\\u{1F600}'")
    in
        result.diagnostics
        |> test.assertEqual([])
        |> (given (_) ->
            result.tokens
            |> LexerCoreTests.lexerTokenAt(0)
            |> test.assertEqual(Token(kind = Int, text = "12345", intValue = 12345, floatValue = 0.0, position = 0, length = 5)))
        |> (given (_) ->
            result.tokens
            |> LexerCoreTests.lexerTokenAt(1)
            |> (given (token) -> token.floatValue)
            |> test.assertEqual(3.14))
        |> (given (_) ->
            result.tokens
            |> LexerCoreTests.lexerTokenAt(2)
            |> test.assertEqual(Token(kind = BigInt, text = "99", intValue = 0, floatValue = 0.0, position = 11, length = 3)))
        |> (given (_) ->
            result.tokens
            |> LexerCoreTests.lexerTokenAt(3)
            |> (given (token) -> token.intValue)
            |> test.assertEqual(255))
        |> (given (_) ->
            result.tokens
            |> LexerCoreTests.lexerTokenAt(4)
            |> (given (token) -> token.intValue)
            |> test.assertEqual(65535))
        |> (given (_) ->
            result.tokens
            |> LexerCoreTests.lexerTokenAt(5)
            |> (given (token) -> token.intValue)
            |> test.assertEqual(4294967295))
        |> (given (_) ->
            result.tokens
            |> LexerCoreTests.lexerTokenAt(6)
            |> (given (token) -> token.intValue)
            |> test.assertEqual(-1))
        |> (given (_) ->
            result.tokens
            |> LexerCoreTests.lexerTokenAt(7)
            |> (given (token) -> token.text)
            |> test.assertEqual("a\\b\"c\nd\re\tf"))
        |> (given (_) ->
            result.tokens
            |> LexerCoreTests.lexerTokenAt(8)
            |> (given (token) -> token.intValue)
            |> test.assertEqual(128512)))

let expectLiteralBoundaries unit =
    unit
    |> (given (_) -> LexerCoreTests.assertLexerKinds(["Int", "Ident", "Int", "Ident", "Int", "Ident", "EOF"])("255u81 255u164 123u8abc"))
    |> (given (_) -> LexerCoreTests.assertLexerKinds(["Int", "Dot", "Ident", "EOF"])("42.x"))
    |> (given (_) -> assertFirstText("\"\"")(""))
    |> (given (_) -> assertFirstText("\"\\a\"")("a"))
    |> (given (_) -> assertFirstText("\"\\n\\r\\t\\\\\\\"\"")("\n\r\t\\\""))

let expectRuneLiterals unit =
    unit
    |> (given (_) -> assertRune("'\\\\'")(92))
    |> (given (_) -> assertRune("'\\''")(39))
    |> (given (_) -> assertRune("'\\n'")(10))
    |> (given (_) -> assertRune("'\\r'")(13))
    |> (given (_) -> assertRune("'\\t'")(9))
    |> (given (_) -> assertRune("'\\0'")(0))
    |> (given (_) -> assertRune("'😀'")(128512))

let rejectInvalidLiterals unit =
    unit
    |> (given (_) -> assertDiagnosticMessage("\"abc")("Unterminated string literal."))
    |> (given (_) -> assertDiagnosticMessage("\"abc\\")("Unterminated string literal."))
    |> (given (_) -> assertDiagnosticMessage("999999999999999999999999999999999999999")("Invalid integer literal: 999999999999999999999999999999999999999."))
    |> (given (_) -> assertDiagnosticMessage("256u8")("Unsigned integer literal out of range for u8: 256u8."))
    |> (given (_) -> assertDiagnosticMessage("65536u16")("Unsigned integer literal out of range for u16: 65536u16."))
    |> (given (_) -> assertDiagnosticMessage("4294967296u32")("Unsigned integer literal out of range for u32: 4294967296u32."))
    |> (given (_) -> assertDiagnosticMessage("18446744073709551616u64")("Unsigned integer literal out of range for u64: 18446744073709551616u64."))
    |> (given (_) -> assertDiagnosticMessage("''")("A rune literal must contain exactly one valid Unicode scalar value."))
    |> (given (_) -> assertDiagnosticMessage("'ab'")("A rune literal must contain exactly one valid Unicode scalar value."))
    |> (given (_) -> assertDiagnosticMessage("'\\u{D800}'")("A rune literal must contain exactly one valid Unicode scalar value."))
    |> (given (_) -> assertDiagnosticMessage("'\\u{110000}'")("A rune literal must contain exactly one valid Unicode scalar value."))

let run unit =
    unit
    |> expectLiteralTokens
    |> expectLiteralBoundaries
    |> expectRuneLiterals
    |> rejectInvalidLiterals
