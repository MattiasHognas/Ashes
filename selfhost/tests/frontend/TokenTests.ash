import Ashes.Test as test
import AshesCompiler.Frontend.Token
let recursive allKindNames kinds =
    match kinds with
        | [] -> []
        | kind :: tail -> tokenKindName(kind) :: allKindNames(tail)

let run unit =
    (let allKinds =
        [
            EOF,
            Bad,
            Int,
            Float,
            String,
            Rune,
            Ident,
            Let,
            LetQuestion,
            Recursive,
            In,
            And,
            If,
            Then,
            Else,
            Match,
            With,
            When,
            Given,
            True,
            False,
            Type,
            Plus,
            Minus,
            Star,
            Slash,
            Percent,
            BigInt,
            Tilde,
            Ampersand,
            AmpersandAmpersand,
            Caret,
            LessLess,
            GreaterGreater,
            GreaterThan,
            LessThan,
            GreaterEquals,
            LessEquals,
            EqualsEquals,
            BangEquals,
            Bang,
            Equals,
            Comma,
            Pipe,
            PipePipe,
            PipeGreater,
            PipeQuestionGreater,
            PipeBangGreater,
            ColonColon,
            LParen,
            RParen,
            LBracket,
            RBracket,
            Arrow,
            Dot,
            Colon,
            LBrace,
            RBrace,
            Await,
            LetBang,
            External,
            Capability,
            Needs,
            Provide,
            Perform,
            Handle,
            Trait,
            Implement,
            Requires,
            Deriving
        ]
    in
        let expectedNames =
            [
                "EOF",
                "Bad",
                "Int",
                "Float",
                "String",
                "Rune",
                "Ident",
                "Let",
                "LetQuestion",
                "Recursive",
                "In",
                "And",
                "If",
                "Then",
                "Else",
                "Match",
                "With",
                "When",
                "Given",
                "True",
                "False",
                "Type",
                "Plus",
                "Minus",
                "Star",
                "Slash",
                "Percent",
                "BigInt",
                "Tilde",
                "Ampersand",
                "AmpersandAmpersand",
                "Caret",
                "LessLess",
                "GreaterGreater",
                "GreaterThan",
                "LessThan",
                "GreaterEquals",
                "LessEquals",
                "EqualsEquals",
                "BangEquals",
                "Bang",
                "Equals",
                "Comma",
                "Pipe",
                "PipePipe",
                "PipeGreater",
                "PipeQuestionGreater",
                "PipeBangGreater",
                "ColonColon",
                "LParen",
                "RParen",
                "LBracket",
                "RBracket",
                "Arrow",
                "Dot",
                "Colon",
                "LBrace",
                "RBrace",
                "Await",
                "LetBang",
                "External",
                "Capability",
                "Needs",
                "Provide",
                "Perform",
                "Handle",
                "Trait",
                "Implement",
                "Requires",
                "Deriving"
            ]
        in
            let token = Token(kind = Ident, text = "naïve", intValue = 0, floatValue = 0.0, position = 4, length = 6)
            in
                let diagnostic =
                    DiagnosticEntry(span = TextSpan(start = 8, end = 12), message = "Unexpected character: '😀'.", code = Some(
                        "ASH003"
                    ))
                in
                    allKinds
                    |> allKindNames
                    |> test.assertEqual(expectedNames)
                    |> (given (_) -> test.assertEqual(Ident)(token.kind))
                    |> (given (_) -> test.assertEqual("naïve")(token.text))
                    |> (given (_) -> test.assertEqual(4)(token.position))
                    |> (given (_) -> test.assertEqual(6)(token.length))
                    |> (given (_) ->
                        token
                        |> tokenEnd
                        |> test.assertEqual(10))
                    |> (given (_) ->
                        token
                        |> tokenSpan
                        |> test.assertEqual(TextSpan(start = 4, end = 10)))
                    |> (given (_) ->
                        -4
                        |> spanFromBounds(-2)
                        |> test.assertEqual(TextSpan(start = 0, end = 0)))
                    |> (given (_) ->
                        4
                        |> spanFromStartLength(8)
                        |> test.assertEqual(TextSpan(start = 8, end = 12)))
                    |> (given (_) ->
                        TextSpan(start = 8, end = 12)
                        |> spanLength
                        |> test.assertEqual(4))
                    |> (given (_) -> test.assertEqual(TextSpan(start = 8, end = 12))(diagnostic.span))
                    |> (given (_) -> test.assertEqual("Unexpected character: '😀'.")(diagnostic.message))
                    |> (given (_) -> test.assertEqual(Some("ASH003"))(diagnostic.code)))
