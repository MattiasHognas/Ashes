type TokenKind =
    | TokEof
    | TokBad
    | TokInt
    | TokFloat
    | TokString
    | TokIdent
    | TokLet
    | TokLetQuestion
    | TokRecursive
    | TokIn
    | TokAnd
    | TokIf
    | TokThen
    | TokElse
    | TokMatch
    | TokWith
    | TokWhen
    | TokGiven
    | TokTrue
    | TokFalse
    | TokType
    | TokPlus
    | TokMinus
    | TokStar
    | TokSlash
    | TokPercent
    | TokBigInt
    | TokTilde
    | TokAmpersand
    | TokCaret
    | TokLessLess
    | TokGreaterGreater
    | TokGreaterThan
    | TokLessThan
    | TokGreaterEquals
    | TokLessEquals
    | TokEqualsEquals
    | TokBangEquals
    | TokEquals
    | TokComma
    | TokPipe
    | TokPipeGreater
    | TokPipeQuestionGreater
    | TokPipeBangGreater
    | TokColonColon
    | TokLParen
    | TokRParen
    | TokLBracket
    | TokRBracket
    | TokArrow
    | TokDot
    | TokColon
    | TokLBrace
    | TokRBrace
    | TokAwait
    | TokLetBang
    | TokExternal
    | TokCapability
    | TokNeeds
    | TokProvide
    | TokPerform
    | TokHandle

type Token =
    | kind: TokenKind
    | text: Str
    | intValue: Int
    | floatValue: Float
    | position: Int
    | length: Int

let tokenKindName kind =
    match kind with
        | TokEof -> "EOF"
        | TokBad -> "Bad"
        | TokInt -> "Int"
        | TokFloat -> "Float"
        | TokString -> "String"
        | TokIdent -> "Ident"
        | TokLet -> "Let"
        | TokLetQuestion -> "LetQuestion"
        | TokRecursive -> "Recursive"
        | TokIn -> "In"
        | TokAnd -> "And"
        | TokIf -> "If"
        | TokThen -> "Then"
        | TokElse -> "Else"
        | TokMatch -> "Match"
        | TokWith -> "With"
        | TokWhen -> "When"
        | TokGiven -> "Given"
        | TokTrue -> "True"
        | TokFalse -> "False"
        | TokType -> "Type"
        | TokPlus -> "Plus"
        | TokMinus -> "Minus"
        | TokStar -> "Star"
        | TokSlash -> "Slash"
        | TokPercent -> "Percent"
        | TokBigInt -> "BigInt"
        | TokTilde -> "Tilde"
        | TokAmpersand -> "Ampersand"
        | TokCaret -> "Caret"
        | TokLessLess -> "LessLess"
        | TokGreaterGreater -> "GreaterGreater"
        | TokGreaterThan -> "GreaterThan"
        | TokLessThan -> "LessThan"
        | TokGreaterEquals -> "GreaterEquals"
        | TokLessEquals -> "LessEquals"
        | TokEqualsEquals -> "EqualsEquals"
        | TokBangEquals -> "BangEquals"
        | TokEquals -> "Equals"
        | TokComma -> "Comma"
        | TokPipe -> "Pipe"
        | TokPipeGreater -> "PipeGreater"
        | TokPipeQuestionGreater -> "PipeQuestionGreater"
        | TokPipeBangGreater -> "PipeBangGreater"
        | TokColonColon -> "ColonColon"
        | TokLParen -> "LParen"
        | TokRParen -> "RParen"
        | TokLBracket -> "LBracket"
        | TokRBracket -> "RBracket"
        | TokArrow -> "Arrow"
        | TokDot -> "Dot"
        | TokColon -> "Colon"
        | TokLBrace -> "LBrace"
        | TokRBrace -> "RBrace"
        | TokAwait -> "Await"
        | TokLetBang -> "LetBang"
        | TokExternal -> "External"
        | TokCapability -> "Capability"
        | TokNeeds -> "Needs"
        | TokProvide -> "Provide"
        | TokPerform -> "Perform"
        | TokHandle -> "Handle"

let keywordKind text =
    match text with
        | "let" -> TokLet
        | "recursive" -> TokRecursive
        | "in" -> TokIn
        | "and" -> TokAnd
        | "if" -> TokIf
        | "then" -> TokThen
        | "else" -> TokElse
        | "match" -> TokMatch
        | "with" -> TokWith
        | "when" -> TokWhen
        | "given" -> TokGiven
        | "true" -> TokTrue
        | "false" -> TokFalse
        | "type" -> TokType
        | "await" -> TokAwait
        | "external" -> TokExternal
        | "capability" -> TokCapability
        | "needs" -> TokNeeds
        | "provide" -> TokProvide
        | "perform" -> TokPerform
        | "handle" -> TokHandle
        | _ -> TokIdent
