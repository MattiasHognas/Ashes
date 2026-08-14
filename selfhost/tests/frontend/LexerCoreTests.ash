import Ashes.Test as test
import AshesCompiler.Frontend.Lexer
import AshesCompiler.Frontend.Token
let recursive lexerKindNames tokens =
    match tokens with
        | [] -> []
        | token :: tail -> tokenKindName(token.kind) :: lexerKindNames(tail)

let lexerTokenAt (index: Int) (tokens: List(Token)) =
    (let recursive find (remaining: List(Token)) (current: Int) =
        match remaining with
            | [] -> test.fail("token index out of range")
            | token :: tail ->
                if current == index
                then token
                else find(tail)(current + 1)
    in find(tokens)(0))

let assertLexerKinds expected source =
    (let result = tokenize(source)
    in
        let diagnosticsChecked = test.assertEqual([])(result.diagnostics)
        in
            result.tokens
            |> lexerKindNames
            |> test.assertEqual(expected))

let run unit =
    (let emptyChecked = assertLexerKinds(["EOF"])("")
    in
        let whitespaceChecked = assertLexerKinds(["EOF"])(" \t\r\n")
        in
            let commentsChecked = assertLexerKinds(["Let", "Ident", "Equals", "Int", "EOF"])("// heading\nlet // inline\nx = 1")
            in
                let onlyCommentChecked = assertLexerKinds(["EOF"])("// no newline")
                in
                    let keywordSource = "let let? let! recursive in and if then else match with when given true false type await external capability needs provide perform handle trait implement requires deriving async"
                    in
                        let keywordsChecked = assertLexerKinds(["Let", "LetQuestion", "LetBang", "Recursive", "In", "And", "If", "Then", "Else", "Match", "With", "When", "Given", "True", "False", "Type", "Await", "External", "Capability", "Needs", "Provide", "Perform", "Handle", "Trait", "Implement", "Requires", "Deriving", "Ident", "EOF"])(keywordSource)
                        in
                            let keywordPrefixesChecked = assertLexerKinds(["Ident", "Ident", "Ident", "Ident", "EOF"])("android andFoo band asyncFoo")
                            in
                                let identifiersChecked = assertLexerKinds(["Ident", "Ident", "Ident", "Ident", "Ident", "EOF"])("_ _x my_var __test x123")
                                in
                                    let operatorsChecked = assertLexerKinds(["PipeQuestionGreater", "PipeBangGreater", "Arrow", "GreaterEquals", "LessEquals", "EqualsEquals", "BangEquals", "LessLess", "GreaterGreater", "ColonColon", "PipeGreater", "LessThan", "GreaterThan", "Plus", "Minus", "Star", "Slash", "Percent", "Tilde", "Ampersand", "Caret", "Bang", "Equals", "Comma", "Pipe", "LParen", "RParen", "LBracket", "RBracket", "Dot", "Colon", "LBrace", "RBrace", "EOF"])("|?> |!> -> >= <= == != << >> :: |> < > + - * / % ~ & ^ ! = , | ( ) [ ] . : { }")
                                    in
                                        let positionResult = tokenize("let x = 42")
                                        in
                                            let positions = positionResult.tokens
                                            in
                                                let letChecked =
                                                    positions
                                                    |> lexerTokenAt(0)
                                                    |> test.assertEqual(Token(kind = Let, text = "let", intValue = 0, floatValue = 0.0, position = 0, length = 3))
                                                in
                                                    let xChecked =
                                                        positions
                                                        |> lexerTokenAt(1)
                                                        |> test.assertEqual(Token(kind = Ident, text = "x", intValue = 0, floatValue = 0.0, position = 4, length = 1))
                                                    in
                                                        let equalsToken = lexerTokenAt(2)(positions)
                                                        in
                                                            let equalsChecked = test.assertEqual(6)(equalsToken.position)
                                                            in
                                                                let integerToken = lexerTokenAt(3)(positions)
                                                                in
                                                                    let integerChecked = test.assertEqual(2)(integerToken.length)
                                                                    in Unit)
