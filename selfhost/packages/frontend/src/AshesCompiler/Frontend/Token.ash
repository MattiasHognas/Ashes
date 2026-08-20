// Defines lexical tokens and the frontend's canonical source-location model.
//
// Invariants:
// - All absolute positions, lengths, and span bounds count UTF-8 bytes.
// - Span helpers normalize empty or inverted bounds without inventing source text.
// - Token text is the exact source spelling, independent of decoded literal values.

export (
    type TokenKind(..),
    type Token(..),
    type TextSpan(..),
    type DiagnosticEntry(..),
    value tokenKindName,
    value tokenEnd,
    value tokenSpan,
    value spanFromBounds,
    value spanFromStartLength,
    value spanLength,
)

type TokenKind =
    | EOF
    | Bad
    | Int
    | Float
    | String
    | Rune
    | Ident
    | Let
    | LetQuestion
    | Recursive
    | In
    | And
    | If
    | Then
    | Else
    | Match
    | With
    | When
    | Given
    | True
    | False
    | Type
    | Plus
    | Minus
    | Star
    | Slash
    | Percent
    | BigInt
    | Tilde
    | Ampersand
    | Caret
    | LessLess
    | GreaterGreater
    | GreaterThan
    | LessThan
    | GreaterEquals
    | LessEquals
    | EqualsEquals
    | BangEquals
    | Bang
    | Equals
    | Comma
    | Pipe
    | PipeGreater
    | PipeQuestionGreater
    | PipeBangGreater
    | ColonColon
    | LParen
    | RParen
    | LBracket
    | RBracket
    | Arrow
    | Dot
    | Colon
    | LBrace
    | RBrace
    | Await
    | LetBang
    | External
    | Capability
    | Needs
    | Provide
    | Perform
    | Handle
    | Trait
    | Implement
    | Requires
    | Deriving
    deriving {Eq, Show}

type Token =
    | kind: TokenKind
    | text: Str
    | intValue: Int
    | floatValue: Float
    | position: Int
    | length: Int
    deriving {Eq, Show}

type TextSpan =
    | start: Int
    | end: Int
    deriving {Eq, Show}

type DiagnosticEntry =
    | span: TextSpan
    | message: Str
    | code: Maybe(Str)
    deriving {Eq, Show}

let maximum left right =
    if left > right
    then left
    else right

let spanFromBounds start end =
    (let normalizedStart = maximum(start)(0)
    in TextSpan(start = normalizedStart, end = maximum(end)(normalizedStart)))

let spanFromStartLength start length = spanFromBounds(start)(start + maximum(length)(0))

let spanLength (span: TextSpan) = maximum(span.end - span.start)(0)

let tokenEnd (value: Token) = value.position + value.length

let tokenSpan (value: Token) = spanFromBounds(value.position)(tokenEnd(value))

let tokenKindName (kind: TokenKind) =
    match kind with
        | EOF -> "EOF"
        | Bad -> "Bad"
        | Int -> "Int"
        | Float -> "Float"
        | String -> "String"
        | Rune -> "Rune"
        | Ident -> "Ident"
        | Let -> "Let"
        | LetQuestion -> "LetQuestion"
        | Recursive -> "Recursive"
        | In -> "In"
        | And -> "And"
        | If -> "If"
        | Then -> "Then"
        | Else -> "Else"
        | Match -> "Match"
        | With -> "With"
        | When -> "When"
        | Given -> "Given"
        | True -> "True"
        | False -> "False"
        | Type -> "Type"
        | Plus -> "Plus"
        | Minus -> "Minus"
        | Star -> "Star"
        | Slash -> "Slash"
        | Percent -> "Percent"
        | BigInt -> "BigInt"
        | Tilde -> "Tilde"
        | Ampersand -> "Ampersand"
        | Caret -> "Caret"
        | LessLess -> "LessLess"
        | GreaterGreater -> "GreaterGreater"
        | GreaterThan -> "GreaterThan"
        | LessThan -> "LessThan"
        | GreaterEquals -> "GreaterEquals"
        | LessEquals -> "LessEquals"
        | EqualsEquals -> "EqualsEquals"
        | BangEquals -> "BangEquals"
        | Bang -> "Bang"
        | Equals -> "Equals"
        | Comma -> "Comma"
        | Pipe -> "Pipe"
        | PipeGreater -> "PipeGreater"
        | PipeQuestionGreater -> "PipeQuestionGreater"
        | PipeBangGreater -> "PipeBangGreater"
        | ColonColon -> "ColonColon"
        | LParen -> "LParen"
        | RParen -> "RParen"
        | LBracket -> "LBracket"
        | RBracket -> "RBracket"
        | Arrow -> "Arrow"
        | Dot -> "Dot"
        | Colon -> "Colon"
        | LBrace -> "LBrace"
        | RBrace -> "RBrace"
        | Await -> "Await"
        | LetBang -> "LetBang"
        | External -> "External"
        | Capability -> "Capability"
        | Needs -> "Needs"
        | Provide -> "Provide"
        | Perform -> "Perform"
        | Handle -> "Handle"
        | Trait -> "Trait"
        | Implement -> "Implement"
        | Requires -> "Requires"
        | Deriving -> "Deriving"
