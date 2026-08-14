import Ashes.Test as test
import AshesCompiler.Frontend.Lexer
import AshesCompiler.Frontend.Token
let recursive assertTokenSequence (tokens: List(Token)) (previousEnd: Int) (sourceLength: Int) =
    match tokens with
        | [] -> test.fail("lexer result must end with EOF")
        | token :: tail ->
            let startsAfterPrevious = test.assertEqual(true)(token.position >= previousEnd)
            in
                let endsInsideSource = test.assertEqual(true)(tokenEnd(token) <= sourceLength)
                in
                    match token.kind with
                        | EOF ->
                            let eofIsLast = test.assertEqual([])(tail)
                            in
                                let eofLength = test.assertEqual(0)(token.length)
                                in test.assertEqual(sourceLength)(token.position)
                        | _ ->
                            let positiveLength = test.assertEqual(true)(token.length > 0)
                            in
                                assertTokenSequence(tail)(tokenEnd(token))(sourceLength)

let recursive assertDiagnosticSpans diagnostics sourceLength =
    match diagnostics with
        | [] -> Unit
        | diagnostic :: tail ->
            let span = diagnostic.span
            in
                let startsInsideSource = test.assertEqual(true)(span.start >= 0)
                in
                    let endsAfterStart = test.assertEqual(true)(span.end >= span.start)
                    in
                        let endsInsideSource = test.assertEqual(true)(span.end <= sourceLength)
                        in assertDiagnosticSpans(tail)(sourceLength)

let assertLexerInvariants source =
    (let result = tokenize(source)
    in
        let sourceLength = Ashes.Text.byteLength(source)
        in
            let tokenSequenceChecked = assertTokenSequence(result.tokens)(0)(sourceLength)
            in assertDiagnosticSpans(result.diagnostics)(sourceLength))

let recursive assertCorpus cases =
    match cases with
        | [] -> Unit
        | source :: tail ->
            let sourceChecked = assertLexerInvariants(source)
            in assertCorpus(tail)

let run unit = assertCorpus(["", "let recursive map f xs = match xs with | [] -> [] | x :: tail -> f(x) :: map(f)(tail)", "type Result(a, e) = | Ok: a | Error: e", "external puts(Str) -> Int = \"puts@c\"", "provide Clock = handle perform now(Unit)", "trait Eq(a) = { equals: a -> a -> Bool }", "1 1.0 1N 255u8 65535u16 4294967295u32 18446744073709551615u64", "\"escaped\\ntext\" 'x' '\\u{1F600}'", "naïve Ελληνικά переменная 变量", "// comment without newline", "@#$ 😀", "\"unterminated", "'ab' '\\u{D800}'", "|?>|!>->>=<===!=<<>>::|><>+-*/%~&^!=,|()[]{}.:"])
