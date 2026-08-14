import Ashes.Test as test
import AshesCompiler.Frontend.Token
let recursive allKindNames kinds =
    match kinds with
        | [] -> []
        | kind :: tail -> tokenKindName(kind) :: allKindNames(tail)

let run unit =
    (let allKinds = [EOF, Bad, Int, Float, String, Rune, Ident, Let, LetQuestion, Recursive, In, And, If, Then, Else, Match, With, When, Given, True, False, Type, Plus, Minus, Star, Slash, Percent, BigInt, Tilde, Ampersand, Caret, LessLess, GreaterGreater, GreaterThan, LessThan, GreaterEquals, LessEquals, EqualsEquals, BangEquals, Bang, Equals, Comma, Pipe, PipeGreater, PipeQuestionGreater, PipeBangGreater, ColonColon, LParen, RParen, LBracket, RBracket, Arrow, Dot, Colon, LBrace, RBrace, Await, LetBang, External, Capability, Needs, Provide, Perform, Handle, Trait, Implement, Requires, Deriving]
    in
        let expectedNames = ["EOF", "Bad", "Int", "Float", "String", "Rune", "Ident", "Let", "LetQuestion", "Recursive", "In", "And", "If", "Then", "Else", "Match", "With", "When", "Given", "True", "False", "Type", "Plus", "Minus", "Star", "Slash", "Percent", "BigInt", "Tilde", "Ampersand", "Caret", "LessLess", "GreaterGreater", "GreaterThan", "LessThan", "GreaterEquals", "LessEquals", "EqualsEquals", "BangEquals", "Bang", "Equals", "Comma", "Pipe", "PipeGreater", "PipeQuestionGreater", "PipeBangGreater", "ColonColon", "LParen", "RParen", "LBracket", "RBracket", "Arrow", "Dot", "Colon", "LBrace", "RBrace", "Await", "LetBang", "External", "Capability", "Needs", "Provide", "Perform", "Handle", "Trait", "Implement", "Requires", "Deriving"]
        in
            let kindsChecked = test.assertEqual(expectedNames)(allKindNames(allKinds))
            in
                let token = Token(kind = Ident, text = "naïve", intValue = 0, floatValue = 0.0, position = 4, length = 6)
                in
                    let kindChecked = test.assertEqual(Ident)(token.kind)
                    in
                        let textChecked = test.assertEqual("naïve")(token.text)
                        in
                            let positionChecked = test.assertEqual(4)(token.position)
                            in
                                let lengthChecked = test.assertEqual(6)(token.length)
                                in
                                    let endChecked = test.assertEqual(10)(tokenEnd(token))
                                    in
                                        let spanChecked = test.assertEqual(TextSpan(start = 4, end = 10))(tokenSpan(token))
                                        in
                                            let normalizedSpanChecked = test.assertEqual(TextSpan(start = 0, end = 0))(spanFromBounds(-2)(-4))
                                            in
                                                let startLengthChecked = test.assertEqual(TextSpan(start = 8, end = 12))(spanFromStartLength(8)(4))
                                                in
                                                    let spanLengthChecked = test.assertEqual(4)(spanLength(TextSpan(start = 8, end = 12)))
                                                    in
                                                        let diagnostic = DiagnosticEntry(span = TextSpan(start = 8, end = 12), message = "Unexpected character: '😀'.", code = Some("ASH003"))
                                                        in
                                                            let diagnosticSpanChecked = test.assertEqual(TextSpan(start = 8, end = 12))(diagnostic.span)
                                                            in
                                                                let diagnosticMessageChecked = test.assertEqual("Unexpected character: '😀'.")(diagnostic.message)
                                                                in
                                                                    let diagnosticCodeChecked = test.assertEqual(Some("ASH003"))(diagnostic.code)
                                                                    in Unit)
