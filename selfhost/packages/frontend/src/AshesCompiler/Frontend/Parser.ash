// Parses tokens with forward-only state and recoverable diagnostics.
//
// Invariants:
// - Failed expectations synthesize zero-width tokens so parsing can continue.
// - Programs follow import* declaration* expression? with sequential top-level scope.
// - Parsed nodes retain UTF-8 byte spans without changing their semantic shape.

import Ashes.Collection.List.append as appendList
import Ashes.Collection.List.reverse as reverseList
import Ashes.Text.join
import AshesCompiler.Frontend.Lexer
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Frontend.Token
export (
    type ExpressionParseResult(..),
    type TypeExpressionParseResult(..),
    type ProgramParseResult(..),
    value parseExpression,
    value parseTypeExpression,
    value parseProgram,
)

type ExpressionParseResult =
    | expression: Expr
    | diagnostics: List(DiagnosticEntry)

type TypeExpressionParseResult =
    | typeExpression: TypeExpr
    | diagnostics: List(DiagnosticEntry)

type ProgramParseResult =
    | program: ProgramSyntax
    | diagnostics: List(DiagnosticEntry)

// The source travels as its byte view: the parser reads it only for layout checks, and a bare
// string element would be cloned every time the state tuple is rebuilt.
type alias ParserState = (List(Token), List(DiagnosticEntry), Bytes)

type ParsedTypeBranches =
    | constructors: List(TypeConstructor)
    | fieldNames: List(Str)
    | fieldTypes: List(TypeExpr)
    | sawField: Bool
    | sawConstructor: Bool
    | endPosition: Int
    | state: ParserState

let parserSyntheticToken kind position = Token(kind = kind, text = "", intValue = 0, floatValue = 0.0, position = position, length = 0)

let parserCurrent (state: ParserState) =
    match state with
        | (token :: _, _diagnostics, _source) -> token
        | ([], _diagnostics, _source) -> parserSyntheticToken(EOF)(0)

let parserAdvance (state: ParserState) =
    match state with
        | (token :: tail, diagnostics, source) -> (token, (tail, diagnostics, source))
        | ([], _diagnostics, _source) -> (parserSyntheticToken(EOF)(0), state)

let parserDiagnosticWithCode (state: ParserState) (token: Token) (message: Str) (code: Str) =
    (let diagnostic = DiagnosticEntry(span = tokenSpan(token), message = message, code = Some(code))
    in
        match state with
            | (tokens, diagnostics, source) -> (tokens, diagnostic :: diagnostics, source))

let parserDiagnostic state token message = parserDiagnosticWithCode(state)(token)(message)("ASH003")

let parserDiagnosticNoCode (state: ParserState) (token: Token) (message: Str) =
    (let diagnostic = DiagnosticEntry(span = tokenSpan(token), message = message, code = None)
    in
        match state with
            | (tokens, diagnostics, source) -> (tokens, diagnostic :: diagnostics, source))

let parserDiagnosticAtSpanWithCode (state: ParserState) (span: TextSpan) (message: Str) (code: Str) =
    (let diagnostic = DiagnosticEntry(span = span, message = message, code = Some(code))
    in
        match state with
            | (tokens, diagnostics, source) -> (tokens, diagnostic :: diagnostics, source))

let parserDiagnosticAtSpan state span message = parserDiagnosticAtSpanWithCode(state)(span)(message)("ASH003")

let parserConsume (expected: TokenKind) (state: ParserState) =
    (let current = parserCurrent(state)
    in
        if current.kind == expected
        then parserAdvance(state)
        else
            let message = "Expected " + tokenKindName(expected) + " but found " + tokenKindName(current.kind) + "."
            in (parserSyntheticToken(expected)(current.position), parserDiagnostic(state)(current)(message)))

let parserExprSpan expression =
    match expression with
        | ExprAt(span, _inner) -> span
        | _ -> TextSpan(start = 0, end = 0)

let parserExprStart expression =
    (let span = parserExprSpan(expression)
    in span.start)

let parserExprEnd expression =
    (let span = parserExprSpan(expression)
    in span.end)

let parserCurrentKind (state: ParserState) =
    (let current = parserCurrent(state)
    in current.kind)

let parserCurrentText (state: ParserState) =
    (let current = parserCurrent(state)
    in current.text)

let parserCurrentPosition (state: ParserState) =
    (let current = parserCurrent(state)
    in current.position)

let parserAt start end expression =
    ExprAt(spanFromBounds(start)(end))(expression)

let parserUnspan expression =
    match expression with
        | ExprAt(_span, inner) -> inner
        | _ -> expression

let parserIsWhitespaceArgument kind =
    match kind with
        | Ident -> true
        | Int -> true
        | BigInt -> true
        | Float -> true
        | String -> true
        | Rune -> true
        | True -> true
        | False -> true
        | LBracket -> true
        | Await -> true
        | Let -> true
        | If -> true
        | Match -> true
        | Given -> true
        | _ -> false

let parserUnsignedBits text =
    (let bytes = Ashes.Byte.fromText(text)
    in
        let count = Ashes.Byte.length(bytes)
        in
            if count >= 2
            then
                if Ashes.Byte.subText(bytes)(count - 2)(2) == "u8"
                then 8
                else
                    if count >= 3
                    then
                        let suffix = Ashes.Byte.subText(bytes)(count - 3)(3)
                        in
                            if suffix == "u16"
                            then 16
                            else
                                if suffix == "u32"
                                then 32
                                else
                                    if suffix == "u64"
                                    then 64
                                    else 0
                    else 0
            else 0)

let parserToggleFloatSign text =
    (let bytes = Ashes.Byte.fromText(text)
    in
        let count = Ashes.Byte.length(bytes)
        in
            if count <= 0
            then ""
            else
                if Ashes.Number.UInt.toInt(Ashes.Byte.get(bytes)(0)) == 45
                then Ashes.Byte.subText(bytes)(1)(count - 1)
                else "-" + text)

let parserIsUpperName text =
    (let bytes = Ashes.Byte.fromText(text)
    in
        if Ashes.Byte.length(bytes) <= 0
        then false
        else
            let first =
                0
                |> Ashes.Byte.get(bytes)
                |> Ashes.Number.UInt.toInt
            in
                if first < 65
                then false
                else first <= 90)

let parserIsLowerName text =
    (let bytes = Ashes.Byte.fromText(text)
    in
        if Ashes.Byte.length(bytes) <= 0
        then false
        else
            let first =
                0
                |> Ashes.Byte.get(bytes)
                |> Ashes.Number.UInt.toInt
            in
                if first < 97
                then false
                else first <= 122)

let parserQualifiedName parts =
    match parts with
        | [] -> ("", "")
        | name :: moduleParts ->
            (moduleParts
            |> reverseList
            |> join("."), name)

let parserCurrentStartsNamedArgument (state: ParserState) =
    match state with
        | (first :: second :: _, _diagnostics, _source) ->
            if first.kind == Ident
            then second.kind == Equals
            else false
        | _ -> false

let parserStateDiagnostics (state: ParserState) =
    match state with
        | (_tokens, diagnostics, _source) -> diagnostics

let parserStateTokens (state: ParserState) =
    match state with
        | (tokens, _diagnostics, _source) -> tokens

let parserStateWithTokens (state: ParserState) tokens =
    match state with
        | (_oldTokens, diagnostics, source) -> (tokens, diagnostics, source)

let parserSourceByteAt bytes position =
    position
    |> Ashes.Byte.get(bytes)
    |> Ashes.Number.UInt.toInt

let recursive parserSourceContainsLineBreak bytes position endPosition =
    if position >= endPosition
    then false
    else
        match parserSourceByteAt(bytes)(position) with
            | 10 -> true
            | 13 -> true
            | _ -> parserSourceContainsLineBreak(bytes)(position + 1)(endPosition)

// The source stays inside the state tuple: a helper returning the source string would hand out a
// copy of the whole file on every layout check, so the scan reads the bytes in place.
let parserStateContainsLineBreak (state: ParserState) position endPosition =
    match state with
        | (_tokens, _diagnostics, sourceBytes) -> parserSourceContainsLineBreak(sourceBytes)(position)(endPosition)

let parserCallStartsOnNewLine state leftParen firstArgument =
    match parserCurrentKind(state) with
        | RParen -> false
        | _ ->
            firstArgument
            |> parserExprStart
            |> parserStateContainsLineBreak(state)(tokenEnd(leftParen))

let parserListStartsOnNewLine state leftBracket firstElement =
    match parserCurrentKind(state) with
        | RBracket -> false
        | _ ->
            firstElement
            |> parserExprStart
            |> parserStateContainsLineBreak(state)(tokenEnd(leftBracket))

let parserRecordStartsOnNewLine state leftParen =
    state
    |> parserCurrentPosition
    |> parserStateContainsLineBreak(state)(tokenEnd(leftParen))

let recursive parserLineStart bytes position =
    if position <= 0
    then 0
    else
        if parserSourceByteAt(bytes)(position - 1) == 10
        then position
        else parserLineStart(bytes)(position - 1)

let recursive parserOnlyIndentBefore bytes position current =
    if current >= position
    then true
    else
        let value = parserSourceByteAt(bytes)(current)
        in
            if value == 32
            then parserOnlyIndentBefore(bytes)(position)(current + 1)
            else
                if value == 9
                then parserOnlyIndentBefore(bytes)(position)(current + 1)
                else
                    if value == 13
                    then parserOnlyIndentBefore(bytes)(position)(current + 1)
                    else false

let parserStartsSourceLine bytes position =
    position
    |> parserLineStart(bytes)
    |> parserOnlyIndentBefore(bytes)(position)

let parserSourceColumn bytes position = position - parserLineStart(bytes)(position)

let parserPreviousCompletesCall (reversedTokens: List(Token)) =
    (let recursive findCallOpen (remaining: List(Token)) depth =
        match remaining with
            | [] -> false
            | token :: tail ->
                match token.kind with
                    | RParen -> findCallOpen(tail)(depth + 1)
                    | LParen ->
                        if depth == 1
                        then
                            match tail with
                                | functionToken :: _ ->
                                    match functionToken.kind with
                                        | Ident -> true
                                        | RParen -> true
                                        | _ -> false
                                | [] -> false
                        else findCallOpen(tail)(depth - 1)
                    | _ -> findCallOpen(tail)(depth)
    in
        match reversedTokens with
            | token :: _ ->
                if token.kind == RParen
                then findCallOpen(reversedTokens)(0)
                else false
            | [] -> false)

// An indented line after a completed call `f(x)` starts the trailing expression only when its first
// token could begin a whitespace-application argument; a continuation such as `then`, `else`, `in`,
// `with`, a pipe, or an operator keeps the value going.
let parserStartsIndentedTrailingExpression (token: Token) (reversedTokens: List(Token)) =
    if parserIsWhitespaceArgument(token.kind)
    then parserPreviousCompletesCall(reversedTokens)
    else false

let parserStartsDeclarationBinding (tokens: List(Token)) =
    match tokens with
        | pipe :: name :: equals :: _ ->
            if pipe.kind != Pipe
            then false
            else
                if name.kind != Ident
                then false
                else
                    if equals.kind == Equals
                    then true
                    else equals.kind == Colon
        | _ -> false

// Never lets a bracket/brace/paren depth counter go negative: a value expression can contain a
// stray unmatched closing delimiter while it's being recovered from a parse error, and a negative
// depth would permanently suppress boundary detection for the rest of the file (every following
// declaration column check requires depth == 0) instead of just this one malformed declaration.
let parserNonNegativeDepth depth =
    if depth < 0
    then 0
    else depth

let parserSplitTopLevelTokens bytes declarationColumn splitBindingPipes (tokens: List(Token)) =
    (let recursive split (remaining: List(Token)) reversed sawToken parenthesisDepth bracketDepth braceDepth =
        match remaining with
            | [] -> (reverseList(reversed), [])
            | token :: tail ->
                if token.kind == EOF
                then (reverseList(reversed), remaining)
                else
                    let atBoundary =
                        if !sawToken
                        then false
                        else
                            if parenthesisDepth != 0
                            then false
                            else
                                if bracketDepth != 0
                                then false
                                else
                                    if braceDepth != 0
                                    then false
                                    else
                                        if !parserStartsSourceLine(bytes)(token.position)
                                        then false
                                        else
                                            let column = parserSourceColumn(bytes)(token.position)
                                            in
                                                if splitBindingPipes
                                                then
                                                    if parserStartsDeclarationBinding(remaining)
                                                    then true
                                                    else
                                                        if column <= declarationColumn
                                                        then true
                                                        else parserStartsIndentedTrailingExpression(token)(reversed)
                                                else
                                                    if column <= declarationColumn
                                                    then true
                                                    else parserStartsIndentedTrailingExpression(token)(reversed)
                    in
                        if atBoundary
                        then (reverseList(reversed), remaining)
                        else
                            let nextParenthesisDepth =
                                match token.kind with
                                    | LParen -> parenthesisDepth + 1
                                    | RParen -> parserNonNegativeDepth(parenthesisDepth - 1)
                                    | _ -> parenthesisDepth
                            in
                                let nextBracketDepth =
                                    match token.kind with
                                        | LBracket -> bracketDepth + 1
                                        | RBracket -> parserNonNegativeDepth(bracketDepth - 1)
                                        | _ -> bracketDepth
                                in
                                    let nextBraceDepth =
                                        match token.kind with
                                            | LBrace -> braceDepth + 1
                                            | RBrace -> parserNonNegativeDepth(braceDepth - 1)
                                            | _ -> braceDepth
                                    in
                                        split(
                                            tail,
                                            token :: reversed,
                                            true,
                                            nextParenthesisDepth,
                                            nextBracketDepth,
                                            nextBraceDepth
                                        )
    in split(tokens)([])(false)(0)(0)(0))

let recursive parserTokensBeforeEof (tokens: List(Token)) =
    match tokens with
        | [] -> []
        | (Token { kind = kind, text = _text, intValue = _intValue, floatValue = _floatValue, position = _position, length = _length } as token) :: tail ->
            if kind == EOF
            then []
            else token :: parserTokensBeforeEof(tail)

let parserTopLevelAt start end item =
    TopLevelAt(spanFromBounds(start)(end))(item)

let parserPatternSpan pattern =
    match pattern with
        | PatternAt(span, _inner) -> span
        | _ -> TextSpan(start = 0, end = 0)

let parserPatternStart pattern =
    (let span = parserPatternSpan(pattern)
    in span.start)

let parserPatternEnd pattern =
    (let span = parserPatternSpan(pattern)
    in span.end)

let parserPatternAt start end pattern =
    PatternAt(spanFromBounds(start)(end))(pattern)

let parserUnspanPattern pattern =
    match pattern with
        | PatternAt(_span, inner) -> inner
        | _ -> pattern

let parserTypeSpan typeExpression =
    match typeExpression with
        | TypeAt(span, _inner) -> span
        | _ -> TextSpan(start = 0, end = 0)

let parserTypeStart typeExpression =
    (let span = parserTypeSpan(typeExpression)
    in span.start)

let parserTypeEnd typeExpression =
    (let span = parserTypeSpan(typeExpression)
    in span.end)

let parserTypeAt start end typeExpression =
    TypeAt(spanFromBounds(start)(end))(typeExpression)

let parserCapabilityName capabilityReference =
    match capabilityReference with
        | CapabilityRefSyntax { name = value, args = _arguments } -> value

let parserCapabilityArguments capabilityReference =
    match capabilityReference with
        | CapabilityRefSyntax { name = _name, args = arguments } -> arguments

let recursive parserConvertCapabilities (remaining: List(CapabilityRefSyntax)) =
    match remaining with
        | [] -> []
        | capabilityReference :: tail ->
            (parserCapabilityName(capabilityReference), parserCapabilityArguments(
                capabilityReference
            )) :: parserConvertCapabilities(tail)

let parserNeedsParts (needsRow: NeedsRowSyntax) =
    match needsRow with
        | NeedsRowSyntax { capabilities = capabilities, tailVariable = tailVariable } ->
            (parserConvertCapabilities(
                capabilities
            ), tailVariable)

let parserPipeStartsArm (state: ParserState) =
    (let recursive scan (tokens: List(Token)) parenthesisDepth bracketDepth =
        match tokens with
            | [] -> false
            | token :: tail ->
                match token.kind with
                    | LParen -> scan(tail)(parenthesisDepth + 1)(bracketDepth)
                    | RParen -> scan(tail)(parenthesisDepth - 1)(bracketDepth)
                    | LBracket -> scan(tail)(parenthesisDepth)(bracketDepth + 1)
                    | RBracket -> scan(tail)(parenthesisDepth)(bracketDepth - 1)
                    | Arrow ->
                        if parenthesisDepth == 0
                        then bracketDepth == 0
                        else false
                    | Pipe ->
                        if parenthesisDepth == 0
                        then
                            if bracketDepth == 0
                            then false
                            else scan(tail)(parenthesisDepth)(bracketDepth)
                        else scan(tail)(parenthesisDepth)(bracketDepth)
                    | EOF -> false
                    | _ -> scan(tail)(parenthesisDepth)(bracketDepth)
    in
        match state with
            | (pipe :: tail, _diagnostics, _source) ->
                if pipe.kind == Pipe
                then scan(tail)(0)(0)
                else false
            | _ -> false)

let parserLetStartsPattern (state: ParserState) =
    match state with
        | (letToken :: next :: tail, _diagnostics, _source) ->
            if letToken.kind != Let
            then false
            else
                if next.kind == LParen
                then true
                else
                    if next.kind != Ident
                    then false
                    else
                        match tail with
                            | colonColon :: _ -> colonColon.kind == ColonColon
                            | [] -> false
        | _ -> false

let parserLastMatchEnd reversedCases fallback =
    match reversedCases with
        | (_pattern, body, _guard) :: _ -> parserExprEnd(body)
        | [] -> parserExprEnd(fallback)

let recursive parserLastFieldEnd fields fallback =
    match fields with
        | [] -> parserExprEnd(fallback)
        | (_name, value) :: [] -> parserExprEnd(value)
        | _head :: tail -> parserLastFieldEnd(tail)(fallback)

let recursive parserParameterNames parameters =
    match parameters with
        | [] -> []
        | (name, _annotation) :: tail -> name :: parserParameterNames(tail)

let recursive parserParseTypeExpressionState state =
    match parserParseTypeWithNeeds(state) with
        | (typeExpression, pendingNeeds, afterType) ->
            match pendingNeeds with
                | None -> (typeExpression, afterType)
                | Some(_needs) ->
                    let current = parserCurrent(afterType)
                    in
                        (typeExpression, parserDiagnostic(
                            afterType,
                            current,
                            "'needs' requires a function type to attach to."
                        ))
and parserParseTypeWithNeeds state =
    match parserParseTypePrimary(state) with
        | (atom, afterAtom) ->
            if parserCurrentKind(afterAtom) == Arrow
            then
                match parserAdvance(afterAtom) with
                    | (_arrow, afterArrow) ->
                        match parserParseTypeWithNeeds(afterArrow) with
                            | (destination, pendingNeeds, afterDestination) ->
                                let row =
                                    match pendingNeeds with
                                        | None -> ([], None)
                                        | Some(needsRow) -> parserNeedsParts(needsRow)
                                in
                                    match row with
                                        | (capabilities, tailVariable) ->
                                            (parserTypeAt(
                                                parserTypeStart(atom),
                                                parserTypeEnd(destination),
                                                TypeArrow(atom)(destination)(capabilities)(tailVariable)
                                            ), None, afterDestination)
            else
                if parserCurrentKind(afterAtom) == Needs
                then
                    match parserParseNeedsRow(afterAtom) with
                        | (needsRow, afterNeeds) -> (atom, Some(needsRow), afterNeeds)
                else (atom, None, afterAtom)
and parserParseTypePrimary state =
    if parserCurrentKind(state) == Ident
    then
        if parserCurrentText(state) == "out"
        then
            match parserAdvance(state) with
                | (outToken, afterOut) ->
                    parserParseTypePrimary(
                        parserDiagnosticWithCode(
                            afterOut,
                            outToken,
                            "out T is supported only as a direct external parameter.",
                            "ASH045"
                        )
                    )
        else
            let checkedState =
                if parserCurrentText(state) == "FfiStr"
                then
                    parserDiagnosticWithCode(
                        state,
                        parserCurrent(state),
                        "FfiStr is supported only as a direct external return type.",
                        "ASH046"
                    )
                else state
            in parserParseNamedTypePrimary(checkedState)
    else
        if parserCurrentKind(state) == LParen
        then
            match parserAdvance(state) with
                | (leftParen, afterLeftParen) ->
                    if parserCurrentKind(afterLeftParen) == RParen
                    then
                        match parserAdvance(afterLeftParen) with
                            | (rightParen, afterRightParen) ->
                                (parserTypeAt(
                                    leftParen.position,
                                    tokenEnd(rightParen),
                                    TypeUnit
                                ), afterRightParen)
                    else
                        match parserParseTypeExpressionState(afterLeftParen) with
                            | (first, afterFirst) ->
                                if parserCurrentKind(afterFirst) == Comma
                                then parserParseTupleTypeTail(leftParen.position)(first :: [])(afterFirst)
                                else
                                    match parserConsume(RParen)(afterFirst) with
                                        | (_rightParen, afterRightParen) -> (first, afterRightParen)
        else parserParseNamedTypePrimary(state)
and parserParseNamedTypePrimary state =
    match parserConsume(Ident)(state) with
        | (name, afterName) ->
            if parserCurrentKind(afterName) == LParen
            then
                match parserAdvance(afterName) with
                    | (_leftParen, afterLeftParen) ->
                        match parserParseTypeArguments(afterLeftParen) with
                            | (arguments, rightParen, afterArguments) ->
                                (parserTypeAt(
                                    name.position,
                                    tokenEnd(rightParen),
                                    TypeApplied(name.text)(arguments)
                                ), afterArguments)
            else
                (parserTypeAt(name.position)(tokenEnd(name))(TypeNamed(name.text)), afterName)
and parserParseTupleTypeTail start reversed state =
    match parserConsume(Comma)(state) with
        | (_comma, afterComma) ->
            match parserParseTypeExpressionState(afterComma) with
                | (element, afterElement) ->
                    let elements = element :: reversed
                    in
                        if parserCurrentKind(afterElement) == Comma
                        then parserParseTupleTypeTail(start)(elements)(afterElement)
                        else
                            match parserConsume(RParen)(afterElement) with
                                | (rightParen, afterRightParen) ->
                                    (parserTypeAt(
                                        start,
                                        tokenEnd(rightParen),
                                        elements
                                        |> reverseList
                                        |> TypeTuple
                                    ), afterRightParen)
and parserParseTypeArguments state =
    if parserCurrentKind(state) == RParen
    then
        match parserAdvance(state) with
            | (rightParen, afterRightParen) -> ([], rightParen, afterRightParen)
    else
        match parserParseTypeExpressionState(state) with
            | (first, afterFirst) -> parserParseMoreTypeArguments(first :: [])(afterFirst)
and parserParseMoreTypeArguments reversed state =
    if parserCurrentKind(state) == Comma
    then
        match parserAdvance(state) with
            | (_comma, afterComma) ->
                match parserParseTypeExpressionState(afterComma) with
                    | (argument, afterArgument) -> parserParseMoreTypeArguments(argument :: reversed)(afterArgument)
    else
        match parserConsume(RParen)(state) with
            | (rightParen, afterRightParen) -> (reverseList(reversed), rightParen, afterRightParen)
and parserParseNeedsRow state =
    match parserAdvance(state) with
        | (_needsToken, afterNeeds) ->
            if parserCurrentKind(afterNeeds) == Ident
            then
                match parserAdvance(afterNeeds) with
                    | (tailVariable, afterTail) ->
                        (NeedsRowSyntax(capabilities = [], tailVariable = Some(
                            tailVariable.text
                        )), afterTail)
            else
                match parserConsume(LBrace)(afterNeeds) with
                    | (_leftBrace, afterLeftBrace) ->
                        if parserCurrentKind(afterLeftBrace) == RBrace
                        then
                            match parserAdvance(afterLeftBrace) with
                                | (_rightBrace, afterRightBrace) -> (NeedsRowSyntax(capabilities = [], tailVariable = None), afterRightBrace)
                        else
                            match parserParseCapabilityRef(afterLeftBrace) with
                                | (first, afterFirst) -> parserParseNeedsRowTail(first :: [])(afterFirst)
and parserParseNeedsRowTail reversed state =
    match parserCurrentKind(state) with
        | Comma ->
            match parserAdvance(state) with
                | (_comma, afterComma) ->
                    match parserParseCapabilityRef(afterComma) with
                        | (capabilityReference, afterCapability) ->
                            parserParseNeedsRowTail(
                                capabilityReference :: reversed,
                                afterCapability
                            )
        | Pipe ->
            match parserAdvance(state) with
                | (_pipe, afterPipe) ->
                    match parserConsume(Ident)(afterPipe) with
                        | (tailVariable, afterTail) ->
                            match parserConsume(RBrace)(afterTail) with
                                | (_rightBrace, afterRightBrace) ->
                                    (NeedsRowSyntax(capabilities = reverseList(
                                        reversed
                                    ), tailVariable = Some(
                                        tailVariable.text
                                    )), afterRightBrace)
        | _ ->
            match parserConsume(RBrace)(state) with
                | (_rightBrace, afterRightBrace) ->
                    (NeedsRowSyntax(capabilities = reverseList(
                        reversed
                    ), tailVariable = None), afterRightBrace)
and parserParseCapabilityRef state =
    match parserConsume(Ident)(state) with
        | (name, afterName) ->
            if parserCurrentKind(afterName) == LParen
            then
                match parserAdvance(afterName) with
                    | (_leftParen, afterLeftParen) ->
                        match parserParseTypeArguments(afterLeftParen) with
                            | (arguments, _rightParen, afterArguments) -> (CapabilityRefSyntax(name = name.text, args = arguments), afterArguments)
            else (CapabilityRefSyntax(name = name.text, args = []), afterName)
and parserParseRequiresClause state =
    match parserAdvance(state) with
        | (_requiresToken, afterRequires) ->
            match parserConsume(LBrace)(afterRequires) with
                | (_leftBrace, afterLeftBrace) ->
                    if parserCurrentKind(afterLeftBrace) == RBrace
                    then
                        match parserAdvance(afterLeftBrace) with
                            | (_rightBrace, afterRightBrace) -> ([], afterRightBrace)
                    else
                        match parserParseTraitConstraint(afterLeftBrace) with
                            | (first, afterFirst) -> parserParseMoreTraitConstraints(first :: [])(afterFirst)
and parserParseMoreTraitConstraints reversed state =
    if parserCurrentKind(state) == Comma
    then
        match parserAdvance(state) with
            | (_comma, afterComma) ->
                match parserParseTraitConstraint(afterComma) with
                    | (constraint, afterConstraint) ->
                        parserParseMoreTraitConstraints(
                            constraint :: reversed,
                            afterConstraint
                        )
    else
        match parserConsume(RBrace)(state) with
            | (_rightBrace, afterRightBrace) -> (reverseList(reversed), afterRightBrace)
and parserParseTraitConstraint state =
    match parserParseQualifiedIdentifier(state) with
        | (name, afterName) ->
            if parserCurrentKind(afterName) == LParen
            then
                match parserAdvance(afterName) with
                    | (_leftParen, afterLeftParen) ->
                        match parserParseTypeArguments(afterLeftParen) with
                            | (arguments, _rightParen, afterArguments) -> (TraitConstraintSyntax(traitName = name, typeArguments = arguments), afterArguments)
            else (TraitConstraintSyntax(traitName = name, typeArguments = []), afterName)
and parserParseQualifiedIdentifier state =
    match parserConsume(Ident)(state) with
        | (first, afterFirst) -> parserParseQualifiedIdentifierTail(first.text :: [])(afterFirst)
and parserParseQualifiedIdentifierTail reversed state =
    if parserCurrentKind(state) != Dot
    then
        (reversed
        |> reverseList
        |> join("."), state)
    else
        match parserAdvance(state) with
            | (_dot, afterDot) ->
                match parserConsume(Ident)(afterDot) with
                    | (part, afterPart) -> parserParseQualifiedIdentifierTail(part.text :: reversed)(afterPart)

let parserCallArgumentLayout isMultiline isFirst =
    match (isMultiline, isFirst) with
        | (false, _) -> callArgumentsInline
        | (true, true) -> callArgumentsMultilineStart
        | (true, false) -> callArgumentsMultilineContinuation

let parserBuildCallArguments function arguments start end isWhitespace isMultiline =
    (let recursive build current remaining isFirst =
        match remaining with
            | [] -> current
            | argument :: tail ->
                let layout = parserCallArgumentLayout(isMultiline)(isFirst)
                in
                    build(layout
                    |> ExprCall(current)(argument)(isWhitespace)
                    |> parserAt(start)(end))(tail)(false)
    in build(function)(arguments)(true))

let recursive parserParseExpression state = parserParseMatch(state)
and parserParseMatch state =
    if parserCurrentKind(state) != Match
    then parserParseHandle(state)
    else
        match parserAdvance(state) with
            | (matchToken, afterMatch) ->
                match parserParsePipe(afterMatch) with
                    | (scrutinee, afterScrutinee) ->
                        match parserConsume(With)(afterScrutinee) with
                            | (_withToken, afterWith) ->
                                let caseStartState =
                                    if parserCurrentKind(afterWith) == Pipe
                                    then
                                        match parserAdvance(afterWith) with
                                            | (_pipe, afterPipe) -> afterPipe
                                    else afterWith
                                in
                                    match parserParseMatchCase(caseStartState) with
                                        | (firstCase, afterFirst) ->
                                            parserParseMatchCases(
                                                matchToken.position,
                                                scrutinee,
                                                firstCase :: [],
                                                afterFirst
                                            )
and parserParseMatchCases start scrutinee reversedCases state =
    if parserCurrentKind(state) != Pipe
    then
        (parserAt(
            start,
            parserLastMatchEnd(reversedCases)(scrutinee),
            ExprMatch(scrutinee)(reverseList(reversedCases))(Some(start))
        ), state)
    else
        match parserAdvance(state) with
            | (_pipe, afterPipe) ->
                match parserParseMatchCase(afterPipe) with
                    | (nextCase, afterCase) ->
                        parserParseMatchCases(
                            start,
                            scrutinee,
                            nextCase :: reversedCases,
                            afterCase
                        )
and parserParseMatchCase state =
    match parserParsePattern(state) with
        | (pattern, afterPattern) ->
            let guarded =
                if parserCurrentKind(afterPattern) == When
                then
                    match parserAdvance(afterPattern) with
                        | (_when, afterWhen) ->
                            match parserParseExpression(afterWhen) with
                                | (guard, afterGuard) -> (Some(guard), afterGuard)
                else (None, afterPattern)
            in
                match guarded with
                    | (guard, afterGuard) ->
                        match parserConsume(Arrow)(afterGuard) with
                            | (_arrow, afterArrow) ->
                                match parserParseExpression(afterArrow) with
                                    | (body, afterBody) -> ((pattern, body, guard), afterBody)
and parserParseHandle state =
    if parserCurrentKind(state) != Handle
    then parserParseIf(state)
    else
        match parserAdvance(state) with
            | (handleToken, afterHandle) ->
                match parserParsePipe(afterHandle) with
                    | (body, afterBody) ->
                        match parserConsume(With)(afterBody) with
                            | (_withToken, afterWith) ->
                                let armStartState =
                                    if parserCurrentKind(afterWith) == Pipe
                                    then
                                        match parserAdvance(afterWith) with
                                            | (_pipe, afterPipe) -> afterPipe
                                    else afterWith
                                in
                                    match parserParseHandlerArm(armStartState) with
                                        | (firstArm, afterFirst) ->
                                            parserParseHandlerArms(
                                                handleToken.position,
                                                body,
                                                firstArm :: [],
                                                afterFirst
                                            )
and parserParseHandlerArms start body reversedArms state =
    if parserCurrentKind(state) != Pipe
    then
        match reversedArms with
            | (_capability, _operation, _parameters, lastBody) :: _ ->
                (parserAt(
                    start,
                    parserExprEnd(lastBody),
                    reversedArms
                    |> reverseList
                    |> ExprHandle(body)
                ), state)
            | [] ->
                ([]
                |> ExprHandle(body)
                |> parserAt(start)(parserExprEnd(body)), state)
    else
        match parserAdvance(state) with
            | (_pipe, afterPipe) ->
                match parserParseHandlerArm(afterPipe) with
                    | (nextArm, afterArm) -> parserParseHandlerArms(start)(body)(nextArm :: reversedArms)(afterArm)
and parserParseHandlerArm state =
    match parserConsume(Ident)(state) with
        | (head, afterHead) ->
            let named =
                if parserCurrentKind(afterHead) == Dot
                then
                    match parserAdvance(afterHead) with
                        | (_dot, afterDot) ->
                            match parserConsume(Ident)(afterDot) with
                                | (operation, afterOperation) -> (Some(head.text), operation.text, afterOperation)
                else
                    if head.text == "return"
                    then (None, head.text, afterHead)
                    else
                        let diagnosed =
                            parserDiagnostic(
                                afterHead,
                                head,
                                "Handler arm must be 'Capability.op(args)' or 'return(value)'."
                            )
                        in (None, head.text, diagnosed)
            in
                match named with
                    | (capabilityName, operationName, afterName) ->
                        match parserConsume(LParen)(afterName) with
                            | (_leftParen, afterLeftParen) ->
                                match parserParsePatternList(afterLeftParen)(RParen) with
                                    | (parameters, afterParameters) ->
                                        match parserConsume(Arrow)(afterParameters) with
                                            | (_arrow, afterArrow) ->
                                                match parserParseExpression(afterArrow) with
                                                    | (armBody, afterArm) -> ((capabilityName, operationName, parameters, armBody), afterArm)
and parserParseIf state =
    if parserCurrentKind(state) != If
    then parserParseLet(state)
    else
        match parserAdvance(state) with
            | (ifToken, afterIf) ->
                match parserParseExpression(afterIf) with
                    | (condition, afterCondition) ->
                        match parserConsume(Then)(afterCondition) with
                            | (_thenToken, afterThen) ->
                                match parserParseExpression(afterThen) with
                                    | (thenBranch, afterThenBranch) ->
                                        match parserConsume(Else)(afterThenBranch) with
                                            | (elseToken, afterElse) ->
                                                if elseToken.length == 0
                                                then
                                                    let missing =
                                                        parserAt(
                                                            elseToken.position,
                                                            elseToken.position,
                                                            ExprInt(0)
                                                        )
                                                    in
                                                        (parserAt(
                                                            ifToken.position,
                                                            parserExprEnd(thenBranch),
                                                            ExprIf(condition)(thenBranch)(missing)
                                                        ), afterElse)
                                                else
                                                    match parserParseExpression(afterElse) with
                                                        | (elseBranch, afterElseBranch) ->
                                                            (parserAt(
                                                                ifToken.position,
                                                                parserExprEnd(elseBranch),
                                                                ExprIf(condition)(thenBranch)(elseBranch)
                                                            ), afterElseBranch)
and parserParseLet state =
    match parserCurrentKind(state) with
        | Let ->
            if parserLetStartsPattern(state)
            then parserParseLetPattern(state)
            else parserParseNamedLet(state)
        | LetQuestion -> parserParseLetResult(state)
        | LetBang -> parserParseLetBang(state)
        | _ -> parserParseLambda(state)
and parserParseNamedLet state =
    match parserAdvance(state) with
        | (letToken, afterLet) ->
            let recursiveBinding = parserCurrentKind(afterLet) == Recursive
            in
                let afterRecursive =
                    if recursiveBinding
                    then
                        match parserAdvance(afterLet) with
                            | (_recursive, next) -> next
                    else afterLet
                in
                    match parserConsume(Ident)(afterRecursive) with
                        | (name, afterName) ->
                            let header =
                                if parserCurrentKind(afterName) == Colon
                                then
                                    match parserAdvance(afterName) with
                                        | (_colon, afterColon) ->
                                            match parserParseTypeExpressionState(afterColon) with
                                                | (annotation, afterAnnotation) ->
                                                    if parserCurrentKind(afterAnnotation) == Requires
                                                    then
                                                        match parserParseRequiresClause(afterAnnotation) with
                                                            | (requirements, afterRequirements) ->
                                                                ([], Some(
                                                                    annotation
                                                                ), requirements, afterRequirements)
                                                    else ([], Some(annotation), [], afterAnnotation)
                                else
                                    match parserParseSugarParameters([])(afterName) with
                                        | (parameters, afterParameters) -> (parameters, None, [], afterParameters)
                            in
                                match header with
                                    | (parameters, typeAnnotation, requirements, afterParameters) ->
                                        match parserConsume(Equals)(afterParameters) with
                                            | (_equals, afterEquals) ->
                                                match parserParseExpression(afterEquals) with
                                                    | (rawValue, afterValue) ->
                                                        match parserConsume(In)(afterValue) with
                                                            | (_inToken, afterIn) ->
                                                                match parserParseExpression(afterIn) with
                                                                    | (body, afterBody) ->
                                                                        let value =
                                                                            parserBuildLambdas(
                                                                                parameters,
                                                                                rawValue,
                                                                                letToken.position
                                                                            )
                                                                        in
                                                                            let parameterNames =
                                                                                parserParameterNames(
                                                                                    parameters
                                                                                )
                                                                            in
                                                                                let expression =
                                                                                    if recursiveBinding
                                                                                    then
                                                                                        ExprLetRecursive(
                                                                                            name.text,
                                                                                            value,
                                                                                            body,
                                                                                            parameterNames,
                                                                                            typeAnnotation,
                                                                                            requirements
                                                                                        )
                                                                                    else
                                                                                        ExprLet(
                                                                                            name.text,
                                                                                            value,
                                                                                            body,
                                                                                            parameterNames,
                                                                                            typeAnnotation,
                                                                                            requirements
                                                                                        )
                                                                                in
                                                                                    (parserAt(
                                                                                        letToken.position,
                                                                                        parserExprEnd(body),
                                                                                        expression
                                                                                    ), afterBody)
// A monomorphic reverse for List((Str, Maybe(TypeExpr))) (sugar-parameter name/annotation pairs).
// The generic `Ashes.Collection.List.reverse` corrupts an accumulator of heap-composite elements
// (a tuple here, since one field is `Maybe(TypeExpr)`) once enough allocation happens after the
// call — the same stage-0 bug already worked around in SourceContext.ash's index builders; a
// monomorphic reverse over the concrete type is unaffected. Found via the self-hosted formatter
// segfaulting on any parsed `let name param = ...` binding.
and reverseParameterList (parameters: List((Str, Maybe(TypeExpr)))) (acc: List((Str, Maybe(TypeExpr)))) =
    match parameters with
        | [] -> acc
        | head :: tail -> reverseParameterList(tail)(head :: acc)
and parserParseSugarParameters reversed state =
    match parserCurrentKind(state) with
        | Ident ->
            match parserAdvance(state) with
                | (parameter, afterParameter) ->
                    parserParseSugarParameters(
                        (parameter.text, None) :: reversed,
                        afterParameter
                    )
        | LParen ->
            match parserAdvance(state) with
                | (_leftParen, afterLeftParen) ->
                    match parserConsume(Ident)(afterLeftParen) with
                        | (parameter, afterParameter) ->
                            match parserConsume(Colon)(afterParameter) with
                                | (_colon, afterColon) ->
                                    match parserParseTypeExpressionState(afterColon) with
                                        | (annotation, afterAnnotation) ->
                                            match parserConsume(RParen)(afterAnnotation) with
                                                | (_rightParen, afterRightParen) ->
                                                    parserParseSugarParameters(
                                                        (parameter.text, Some(annotation)) :: reversed,
                                                        afterRightParen
                                                    )
        | _ -> (reverseParameterList(reversed)([]), state)
and parserBuildLambdas parameters body start =
    (let recursive build reversed current =
        match reversed with
            | [] -> current
            | (parameter, annotation) :: tail ->
                build(
                    tail,
                    annotation
                    |> ExprLambda(parameter)(current)
                    |> parserAt(start)(parserExprEnd(current))
                )
    in
        build(reverseParameterList(parameters)([]))(body))
and parserParseLetResult state =
    match parserAdvance(state) with
        | (letToken, afterLet) ->
            match parserConsume(Ident)(afterLet) with
                | (name, afterName) ->
                    match parserConsume(Equals)(afterName) with
                        | (_equals, afterEquals) ->
                            match parserParseExpression(afterEquals) with
                                | (value, afterValue) ->
                                    match parserConsume(In)(afterValue) with
                                        | (_inToken, afterIn) ->
                                            match parserParseExpression(afterIn) with
                                                | (body, afterBody) ->
                                                    (parserAt(
                                                        letToken.position,
                                                        parserExprEnd(body),
                                                        ExprLetResult(name.text)(value)(body)
                                                    ), afterBody)
and parserParseLetBang state =
    match parserAdvance(state) with
        | (letToken, afterLet) ->
            match parserConsume(Ident)(afterLet) with
                | (name, afterName) ->
                    match parserConsume(Equals)(afterName) with
                        | (_equals, afterEquals) ->
                            match parserParseExpression(afterEquals) with
                                | (value, afterValue) ->
                                    let awaited =
                                        parserAt(
                                            parserExprStart(value),
                                            parserExprEnd(value),
                                            ExprAwait(value)
                                        )
                                    in
                                        let bodyResult =
                                            if parserCurrentKind(afterValue) == LetBang
                                            then parserParseLetBang(afterValue)
                                            else
                                                match parserConsume(In)(afterValue) with
                                                    | (_inToken, afterIn) -> parserParseExpression(afterIn)
                                        in
                                            match bodyResult with
                                                | (body, afterBody) ->
                                                    (parserAt(
                                                        letToken.position,
                                                        parserExprEnd(body),
                                                        ExprLet(name.text)(awaited)(body)([])(None)([])
                                                    ), afterBody)
and parserParseLambda state =
    if parserCurrentKind(state) != Given
    then parserParseWith(state)
    else
        match parserAdvance(state) with
            | (givenToken, afterGiven) ->
                let parametersResult =
                    if parserCurrentKind(afterGiven) == LParen
                    then
                        match parserAdvance(afterGiven) with
                            | (_leftParen, afterLeftParen) -> parserParseLambdaParameterList(afterLeftParen)
                    else
                        match parserConsume(Ident)(afterGiven) with
                            | (parameter, afterParameter) -> ((parameter.text, None) :: [], afterParameter)
                in
                    match parametersResult with
                        | (parameters, afterParameters) ->
                            match parserConsume(Arrow)(afterParameters) with
                                | (_arrow, afterArrow) ->
                                    match parserParseExpression(afterArrow) with
                                        | (body, afterBody) ->
                                            (parserBuildLambdas(
                                                parameters,
                                                body,
                                                givenToken.position
                                            ), afterBody)
and parserParseLambdaParameterList state =
    match parserParseLambdaParameter(state) with
        | (first, afterFirst) -> parserParseMoreLambdaParameters(first :: [])(afterFirst)
and parserParseMoreLambdaParameters reversed state =
    if parserCurrentKind(state) == Comma
    then
        match parserAdvance(state) with
            | (_comma, afterComma) ->
                match parserParseLambdaParameter(afterComma) with
                    | (parameter, afterParameter) ->
                        parserParseMoreLambdaParameters(
                            parameter :: reversed,
                            afterParameter
                        )
    else
        match parserConsume(RParen)(state) with
            | (_end, afterEnd) -> (reverseList(reversed), afterEnd)
and parserParseLambdaParameter state =
    match parserConsume(Ident)(state) with
        | (parameter, afterParameter) ->
            if parserCurrentKind(afterParameter) == Colon
            then
                match parserAdvance(afterParameter) with
                    | (_colon, afterColon) ->
                        match parserParseTypeExpressionState(afterColon) with
                            | (annotation, afterAnnotation) -> ((parameter.text, Some(annotation)), afterAnnotation)
            else ((parameter.text, None), afterParameter)
and parserParseWith state =
    match parserParsePipe(state) with
        | (target, afterTarget) -> parserParseWithTail(target)(afterTarget)
and parserParseWithTail target state =
    if parserCurrentKind(state) != With
    then (target, state)
    else
        match parserAdvance(state) with
            | (_withToken, afterWith) ->
                match parserParseUpdateFields([])(afterWith) with
                    | (fields, afterFields) ->
                        parserParseWithTail(
                            parserAt(
                                parserExprStart(target),
                                parserLastFieldEnd(fields)(target),
                                ExprRecordUpdate(target)(fields)
                            ),
                            afterFields
                        )
and parserParseUpdateFields reversed state =
    match parserConsume(Ident)(state) with
        | (name, afterName) ->
            match parserConsume(Equals)(afterName) with
                | (_equals, afterEquals) ->
                    match parserParsePipe(afterEquals) with
                        | (value, afterValue) ->
                            let fields = (name.text, value) :: reversed
                            in
                                if parserCurrentKind(afterValue) == Comma
                                then
                                    match parserAdvance(afterValue) with
                                        | (_comma, afterComma) -> parserParseUpdateFields(fields)(afterComma)
                                else (reverseList(fields), afterValue)
and parserParsePipe state =
    match parserParseComparison(state) with
        | (left, nextState) -> parserParsePipeTail(left)(nextState)
and parserParsePipeTail left state =
    (let current = parserCurrent(state)
    in
        match current.kind with
            | PipeGreater ->
                match parserAdvance(state) with
                    | (_operator, afterOperator) ->
                        match parserParseComparison(afterOperator) with
                            | (right, afterRight) ->
                                parserParsePipeTail(
                                    parserAt(
                                        parserExprStart(left),
                                        parserExprEnd(right),
                                        ExprCall(right)(left)(false)(callArgumentsInline)
                                    ),
                                    afterRight
                                )
            | PipeQuestionGreater ->
                match parserAdvance(state) with
                    | (_operator, afterOperator) ->
                        match parserParseComparison(afterOperator) with
                            | (right, afterRight) ->
                                parserParsePipeTail(
                                    right
                                    |> ExprResultPipe(left)
                                    |> parserAt(parserExprStart(left))(parserExprEnd(right)),
                                    afterRight
                                )
            | PipeBangGreater ->
                match parserAdvance(state) with
                    | (_operator, afterOperator) ->
                        match parserParseComparison(afterOperator) with
                            | (right, afterRight) ->
                                parserParsePipeTail(
                                    parserAt(
                                        parserExprStart(left),
                                        parserExprEnd(right),
                                        ExprResultMapErrorPipe(left)(right)
                                    ),
                                    afterRight
                                )
            | _ -> (left, state))
and parserParseComparison state =
    match parserParseBitwiseOr(state) with
        | (left, nextState) -> parserParseComparisonTail(left)(nextState)
and parserParseComparisonTail left state =
    (let current = parserCurrent(state)
    in
        let kind = current.kind
        in
            let comparison =
                match kind with
                    | GreaterThan -> true
                    | GreaterEquals -> true
                    | LessThan -> true
                    | LessEquals -> true
                    | EqualsEquals -> true
                    | BangEquals -> true
                    | _ -> false
            in
                if !comparison
                then (left, state)
                else
                    match parserAdvance(state) with
                        | (_operator, afterOperator) ->
                            match parserParseBitwiseOr(afterOperator) with
                                | (right, afterRight) ->
                                    let body =
                                        match kind with
                                            | GreaterThan -> ExprGreaterThan(left)(right)
                                            | GreaterEquals -> ExprGreaterOrEqual(left)(right)
                                            | LessThan -> ExprLessThan(left)(right)
                                            | LessEquals -> ExprLessOrEqual(left)(right)
                                            | EqualsEquals -> ExprEqual(left)(right)
                                            | _ -> ExprNotEqual(left)(right)
                                    in
                                        parserParseComparisonTail(
                                            parserAt(parserExprStart(left))(parserExprEnd(right))(body),
                                            afterRight
                                        ))
and parserParseBitwiseOr state =
    match parserParseBitwiseXor(state) with
        | (left, nextState) -> parserParseBitwiseOrTail(left)(nextState)
and parserParseBitwiseOrTail left state =
    (let current = parserCurrent(state)
    in
        if current.kind != Pipe
        then (left, state)
        else
            if parserPipeStartsArm(state)
            then (left, state)
            else
                match parserAdvance(state) with
                    | (_operator, afterOperator) ->
                        match parserParseBitwiseXor(afterOperator) with
                            | (right, afterRight) ->
                                parserParseBitwiseOrTail(
                                    right
                                    |> ExprBitwiseOr(left)
                                    |> parserAt(parserExprStart(left))(parserExprEnd(right)),
                                    afterRight
                                ))
and parserParseBitwiseXor state =
    match parserParseBitwiseAnd(state) with
        | (left, nextState) -> parserParseBitwiseXorTail(left)(nextState)
and parserParseBitwiseXorTail left state =
    (let current = parserCurrent(state)
    in
        if current.kind != Caret
        then (left, state)
        else
            match parserAdvance(state) with
                | (_operator, afterOperator) ->
                    match parserParseBitwiseAnd(afterOperator) with
                        | (right, afterRight) ->
                            parserParseBitwiseXorTail(
                                right
                                |> ExprBitwiseXor(left)
                                |> parserAt(parserExprStart(left))(parserExprEnd(right)),
                                afterRight
                            ))
and parserParseBitwiseAnd state =
    match parserParseCons(state) with
        | (left, nextState) -> parserParseBitwiseAndTail(left)(nextState)
and parserParseBitwiseAndTail left state =
    (let current = parserCurrent(state)
    in
        if current.kind != Ampersand
        then (left, state)
        else
            match parserAdvance(state) with
                | (_operator, afterOperator) ->
                    match parserParseCons(afterOperator) with
                        | (right, afterRight) ->
                            parserParseBitwiseAndTail(
                                right
                                |> ExprBitwiseAnd(left)
                                |> parserAt(parserExprStart(left))(parserExprEnd(right)),
                                afterRight
                            ))
and parserParseCons state =
    match parserParseShift(state) with
        | (left, nextState) ->
            let current = parserCurrent(nextState)
            in
                if current.kind != ColonColon
                then (left, nextState)
                else
                    match parserAdvance(nextState) with
                        | (_operator, afterOperator) ->
                            match parserParseCons(afterOperator) with
                                | (right, afterRight) ->
                                    (parserAt(
                                        parserExprStart(left),
                                        parserExprEnd(right),
                                        ExprCons(left)(right)
                                    ), afterRight)
and parserParseShift state =
    match parserParseAdditive(state) with
        | (left, nextState) -> parserParseShiftTail(left)(nextState)
and parserParseShiftTail left state =
    (let current = parserCurrent(state)
    in
        if current.kind == LessLess
        then parserParseShiftOperator(left)(state)(current.kind)
        else
            if current.kind == GreaterGreater
            then parserParseShiftOperator(left)(state)(current.kind)
            else (left, state))
and parserParseShiftOperator left state kind =
    match parserAdvance(state) with
        | (_operator, afterOperator) ->
            match parserParseAdditive(afterOperator) with
                | (right, afterRight) ->
                    let body =
                        if kind == LessLess
                        then ExprShiftLeft(left)(right)
                        else ExprShiftRight(left)(right)
                    in
                        parserParseShiftTail(parserAt(parserExprStart(left))(parserExprEnd(right))(body))(afterRight)
and parserParseAdditive state =
    match parserParseMultiplicative(state) with
        | (left, nextState) -> parserParseAdditiveTail(left)(nextState)
and parserParseAdditiveTail left state =
    (let current = parserCurrent(state)
    in
        if current.kind == Plus
        then parserParseAdditiveOperator(left)(state)(current.kind)
        else
            if current.kind == Minus
            then parserParseAdditiveOperator(left)(state)(current.kind)
            else (left, state))
and parserParseAdditiveOperator left state kind =
    match parserAdvance(state) with
        | (_operator, afterOperator) ->
            match parserParseMultiplicative(afterOperator) with
                | (right, afterRight) ->
                    let body =
                        if kind == Plus
                        then ExprAdd(left)(right)
                        else ExprSubtract(left)(right)
                    in
                        parserParseAdditiveTail(parserAt(parserExprStart(left))(parserExprEnd(right))(body))(afterRight)
and parserParseMultiplicative state =
    match parserParseUnary(state) with
        | (left, nextState) -> parserParseMultiplicativeTail(left)(nextState)
and parserParseMultiplicativeTail left state =
    (let current = parserCurrent(state)
    in
        if current.kind == Star
        then parserParseMultiplicativeOperator(left)(state)(current.kind)
        else
            if current.kind == Slash
            then parserParseMultiplicativeOperator(left)(state)(current.kind)
            else
                if current.kind == Percent
                then parserParseMultiplicativeOperator(left)(state)(current.kind)
                else (left, state))
and parserParseMultiplicativeOperator left state kind =
    match parserAdvance(state) with
        | (_operator, afterOperator) ->
            match parserParseUnary(afterOperator) with
                | (right, afterRight) ->
                    let body =
                        match kind with
                            | Star -> ExprMultiply(left)(right)
                            | Slash -> ExprDivide(left)(right)
                            | _ -> ExprModulo(left)(right)
                    in
                        parserParseMultiplicativeTail(
                            parserAt(parserExprStart(left))(parserExprEnd(right))(body),
                            afterRight
                        )
and parserParseUnary state =
    (let current = parserCurrent(state)
    in
        match current.kind with
            | Bang ->
                match parserAdvance(state) with
                    | (operator, afterOperator) ->
                        match parserParseUnary(afterOperator) with
                            | (operand, afterOperand) ->
                                (parserAt(
                                    operator.position,
                                    parserExprEnd(operand),
                                    ExprLogicalNot(operand)
                                ), afterOperand)
            | Tilde ->
                match parserAdvance(state) with
                    | (operator, afterOperator) ->
                        match parserParseUnary(afterOperator) with
                            | (operand, afterOperand) ->
                                (parserAt(
                                    operator.position,
                                    parserExprEnd(operand),
                                    ExprBitwiseNot(operand)
                                ), afterOperand)
            | Await ->
                match parserAdvance(state) with
                    | (operator, afterOperator) ->
                        match parserParseCall(afterOperator) with
                            | (operand, afterOperand) ->
                                (parserAt(
                                    operator.position,
                                    parserExprEnd(operand),
                                    ExprAwait(operand)
                                ), afterOperand)
            | Perform ->
                match parserAdvance(state) with
                    | (operator, afterOperator) ->
                        match parserParseCall(afterOperator) with
                            | (operand, afterOperand) ->
                                (parserAt(
                                    operator.position,
                                    parserExprEnd(operand),
                                    ExprPerform(operand)
                                ), afterOperand)
            | Minus ->
                match parserAdvance(state) with
                    | (operator, afterOperator) ->
                        match parserParseUnary(afterOperator) with
                            | (operand, afterOperand) ->
                                match parserUnspan(operand) with
                                    | ExprFloat(value, text) ->
                                        (parserAt(
                                            operator.position,
                                            parserExprEnd(operand),
                                            text
                                            |> parserToggleFloatSign
                                            |> ExprFloat(0.0 - value)
                                        ), afterOperand)
                                    | _ ->
                                        let zero = parserAt(operator.position)(operator.position + 1)(ExprInt(0))
                                        in
                                            (parserAt(
                                                operator.position,
                                                parserExprEnd(operand),
                                                ExprSubtract(zero)(operand)
                                            ), afterOperand)
            | _ -> parserParseCall(state))
and parserParseCall state =
    match parserParsePrimary(state) with
        | (function, nextState) -> parserParseCallTail(function)(nextState)
and parserParseCallTail function state =
    (let current = parserCurrent(state)
    in
        if current.kind == LParen
        then
            let start = parserExprStart(function)
            in
                match parserAdvance(state) with
                    | (leftParen, afterLeftParen) ->
                        if parserCurrentStartsNamedArgument(afterLeftParen)
                        then parserParseRecordArguments(function)(start)(leftParen)(afterLeftParen)
                        else
                            match parserParseCallArgumentList(afterLeftParen) with
                                | (arguments, rightParen, afterArguments) ->
                                    let isMultiline =
                                        match arguments with
                                            | first :: _ -> parserCallStartsOnNewLine(afterLeftParen)(leftParen)(first)
                                            | [] -> false
                                    in
                                        parserParseCallTail(
                                            parserBuildCallArguments(
                                                function,
                                                arguments,
                                                start,
                                                tokenEnd(rightParen),
                                                false,
                                                isMultiline
                                            ),
                                            afterArguments
                                        )
        else
            if parserIsWhitespaceArgument(current.kind)
            then
                let start = parserExprStart(function)
                in
                    match parserParseWhitespaceArgument(state) with
                        | (argument, afterArgument) ->
                            parserParseCallTail(
                                parserAt(
                                    start,
                                    parserExprEnd(argument),
                                    ExprCall(function)(argument)(true)(callArgumentsInline)
                                ),
                                afterArgument
                            )
            else (function, state))
and parserParseCallArgumentList state =
    (let current = parserCurrent(state)
    in
        if current.kind == RParen
        then
            match parserAdvance(state) with
                | (rightParen, afterRightParen) ->
                    let unit = parserAt(current.position)(current.position)(ExprVar("Unit"))
                    in ([unit], rightParen, afterRightParen)
        else
            match parserParseExpression(state) with
                | (first, afterFirst) -> parserParseMoreCallArguments(first :: [])(afterFirst))
and parserParseMoreCallArguments reversedArguments state =
    (let current = parserCurrent(state)
    in
        if current.kind == Comma
        then
            match parserAdvance(state) with
                | (_comma, afterComma) ->
                    match parserParseExpression(afterComma) with
                        | (argument, afterArgument) ->
                            parserParseMoreCallArguments(
                                argument :: reversedArguments,
                                afterArgument
                            )
        else
            match parserConsume(RParen)(state) with
                | (rightParen, afterRightParen) -> (reverseList(reversedArguments), rightParen, afterRightParen))
and parserParseRecordArguments function start leftParen state =
    leftParen
    |> parserRecordStartsOnNewLine(state)
    |> parserParseRecordArgumentsWithLayout(function)(start)(state)
and parserParseRecordArgumentsWithLayout function start state isMultiline =
    match parserUnspan(function) with
        | ExprVar(typeName) ->
            state
            |> parserCheckRecordTypeName(typeName)
            |> parserFinishRecordArguments(typeName)(start)(isMultiline)
        | _ ->
            state
            |> parserInvalidRecordConstruction
            |> parserFinishRecordArguments("")(start)(isMultiline)
and parserCheckRecordTypeName typeName state =
    if parserIsUpperName(typeName)
    then state
    else parserInvalidRecordConstruction(state)
and parserInvalidRecordConstruction state =
    parserDiagnostic(
        state,
        parserCurrent(state),
        "Named arguments are only allowed in record construction."
    )
and parserFinishRecordArguments typeName start isMultiline state =
    match parserParseNamedFields([])(state) with
        | (fields, rightParen, afterFields) ->
            parserParseCallTail(
                isMultiline
                |> ExprRecord(typeName)(fields)
                |> parserAt(start)(tokenEnd(rightParen)),
                afterFields
            )
and parserParseNamedFields reversedFields state =
    match parserConsume(Ident)(state) with
        | (name, afterName) ->
            match parserConsume(Equals)(afterName) with
                | (_equals, afterEquals) ->
                    match parserParseExpression(afterEquals) with
                        | (value, afterValue) ->
                            let fields = (name.text, value) :: reversedFields
                            in
                                if parserCurrentKind(afterValue) == Comma
                                then
                                    match parserAdvance(afterValue) with
                                        | (_comma, afterComma) -> parserParseNamedFields(fields)(afterComma)
                                else
                                    match parserConsume(RParen)(afterValue) with
                                        | (rightParen, afterRightParen) ->
                                            (reverseList(
                                                fields
                                            ), rightParen, afterRightParen)
and parserParseWhitespaceArgument state =
    match parserCurrentKind(state) with
        | Await -> parserParseUnary(state)
        | Let -> parserParseLet(state)
        | If -> parserParseIf(state)
        | Match -> parserParseMatch(state)
        | Given -> parserParseLambda(state)
        | _ -> parserParsePrimary(state)
and parserParsePrimary state =
    (let current = parserCurrent(state)
    in
        match current.kind with
            | Int ->
                match parserAdvance(state) with
                    | (token, afterToken) ->
                        let bits = parserUnsignedBits(token.text)
                        in
                            if bits > 0
                            then
                                (parserAt(
                                    token.position,
                                    tokenEnd(token),
                                    ExprUInt(token.intValue)(bits)(token.text)
                                ), afterToken)
                            else
                                (parserAt(token.position)(tokenEnd(token))(ExprInt(token.intValue)), afterToken)
            | BigInt ->
                match parserAdvance(state) with
                    | (token, afterToken) ->
                        (parserAt(
                            token.position,
                            tokenEnd(token),
                            ExprBigInt(token.text)
                        ), afterToken)
            | Float ->
                match parserAdvance(state) with
                    | (token, afterToken) ->
                        (parserAt(
                            token.position,
                            tokenEnd(token),
                            ExprFloat(token.floatValue)(token.text)
                        ), afterToken)
            | String ->
                match parserAdvance(state) with
                    | (token, afterToken) ->
                        (parserAt(
                            token.position,
                            tokenEnd(token),
                            ExprString(token.text)
                        ), afterToken)
            | Rune ->
                match parserAdvance(state) with
                    | (token, afterToken) ->
                        (parserAt(
                            token.position,
                            tokenEnd(token),
                            ExprRune(token.intValue)
                        ), afterToken)
            | True ->
                match parserAdvance(state) with
                    | (token, afterToken) ->
                        (parserAt(token.position)(tokenEnd(token))(ExprBool(true)), afterToken)
            | False ->
                match parserAdvance(state) with
                    | (token, afterToken) ->
                        (parserAt(token.position)(tokenEnd(token))(ExprBool(false)), afterToken)
            | Ident -> parserParseVariable(state)
            | LParen -> parserParseParenthesized(state)
            | LBracket -> parserParseList(state)
            | _ -> parserBadPrimary(state))
and parserParseVariable state =
    match parserAdvance(state) with
        | (first, afterFirst) ->
            parserParseVariableTail(first.position)(tokenEnd(first))(first.text :: [])(afterFirst)
and parserParseVariableTail start end reversedParts state =
    (let current = parserCurrent(state)
    in
        if current.kind != Dot
        then
            match parserQualifiedName(reversedParts) with
                | (moduleName, name) ->
                    if moduleName == ""
                    then (parserAt(start)(end)(ExprVar(name)), state)
                    else
                        (name
                        |> ExprQualifiedVar(moduleName)
                        |> parserAt(start)(end), state)
        else
            match parserAdvance(state) with
                | (_dot, afterDot) ->
                    match parserConsume(Ident)(afterDot) with
                        | (part, afterPart) ->
                            parserParseVariableTail(
                                start,
                                tokenEnd(part),
                                part.text :: reversedParts,
                                afterPart
                            ))
and parserParseParenthesized state =
    match parserAdvance(state) with
        | (leftParen, afterLeftParen) ->
            let bodyResult =
                if parserCurrentKind(afterLeftParen) == Let
                then
                    if parserLetStartsPattern(afterLeftParen)
                    then parserParseExpression(afterLeftParen)
                    else
                        match afterLeftParen with
                            | (_tokens, _diagnostics, sourceBytes) -> parserParseParenthesizedFlatBody(sourceBytes)(afterLeftParen)
                else parserParseExpression(afterLeftParen)
            in
                match bodyResult with
                    | (first, afterFirst) ->
                        if parserCurrentKind(afterFirst) == Comma
                        then parserParseTupleTail(leftParen.position)(first :: [])(afterFirst)
                        else
                            match parserConsume(RParen)(afterFirst) with
                                | (_rightParen, afterRightParen) -> (first, afterRightParen)
and parserParseParenthesizedFlatBody sourceBytes state =
    if parserCurrentKind(state) != Let
    then parserParseExpression(state)
    else
        match parserAdvance(state) with
            | (letToken, afterLet) ->
                let recursiveBinding = parserCurrentKind(afterLet) == Recursive
                in
                    let afterRecursive =
                        if recursiveBinding
                        then
                            match parserAdvance(afterLet) with
                                | (_recursive, next) -> next
                        else afterLet
                    in
                        match parserConsume(Ident)(afterRecursive) with
                            | (name, afterName) ->
                                let header =
                                    if parserCurrentKind(afterName) == Colon
                                    then
                                        match parserAdvance(afterName) with
                                            | (_colon, afterColon) ->
                                                match parserParseTypeExpressionState(afterColon) with
                                                    | (annotation, afterAnnotation) ->
                                                        if parserCurrentKind(afterAnnotation) == Requires
                                                        then
                                                            match parserParseRequiresClause(afterAnnotation) with
                                                                | (requirements, afterRequirements) ->
                                                                    ([], Some(
                                                                        annotation
                                                                    ), requirements, afterRequirements)
                                                        else ([], Some(annotation), [], afterAnnotation)
                                    else
                                        match parserParseSugarParameters([])(afterName) with
                                            | (parameters, afterParameters) -> (parameters, None, [], afterParameters)
                                in
                                    match header with
                                        | (parameters, annotation, requirements, afterHeader) ->
                                            match parserConsume(Equals)(afterHeader) with
                                                | (_equals, afterEquals) ->
                                                    match parserParseFlatExpressionValue(
                                                        sourceBytes,
                                                        parserSourceColumn(sourceBytes)(letToken.position),
                                                        afterEquals
                                                    ) with
                                                        | (rawValue, afterValue) ->
                                                            let value =
                                                                parserBuildLambdas(
                                                                    parameters,
                                                                    rawValue,
                                                                    letToken.position
                                                                )
                                                            in
                                                                if parserCurrentKind(afterValue) == In
                                                                then
                                                                    match parserAdvance(afterValue) with
                                                                        | (_inToken, afterIn) ->
                                                                            match parserParseExpression(afterIn) with
                                                                                | (body, afterBody) ->
                                                                                    (parserBuildLetExpression(
                                                                                        letToken.position,
                                                                                        recursiveBinding,
                                                                                        name.text,
                                                                                        value,
                                                                                        body,
                                                                                        parserParameterNames(parameters),
                                                                                        annotation,
                                                                                        requirements
                                                                                    ), afterBody)
                                                                else
                                                                    match parserParseParenthesizedFlatBody(
                                                                        sourceBytes,
                                                                        afterValue
                                                                    ) with
                                                                        | (body, afterBody) ->
                                                                            (parserBuildLetExpression(
                                                                                letToken.position,
                                                                                recursiveBinding,
                                                                                name.text,
                                                                                value,
                                                                                body,
                                                                                parserParameterNames(parameters),
                                                                                annotation,
                                                                                requirements
                                                                            ), afterBody)
and parserParseFlatExpressionValue sourceBytes declarationColumn state =
    match state
    |> parserStateTokens
    |> parserSplitTopLevelTokens(sourceBytes)(declarationColumn)(false) with
        | (valueTokens, remainingTokens) ->
            let boundaryPosition =
                match remainingTokens with
                    | token :: _ -> token.position
                    | [] -> 0
            in
                let temporaryState =
                    parserStateWithTokens(
                        state,
                        appendList(valueTokens)(parserSyntheticToken(EOF)(boundaryPosition) :: [])
                    )
                in
                    match parserParseExpression(temporaryState) with
                        | (value, afterValue) ->
                            let unconsumed =
                                afterValue
                                |> parserStateTokens
                                |> parserTokensBeforeEof
                            in
                                (value, remainingTokens
                                |> appendList(unconsumed)
                                |> parserStateWithTokens(afterValue))
and parserBuildLetExpression start recursiveBinding name value body parameters annotation requirements =
    (let expression =
        if recursiveBinding
        then ExprLetRecursive(name)(value)(body)(parameters)(annotation)(requirements)
        else ExprLet(name)(value)(body)(parameters)(annotation)(requirements)
    in
        parserAt(start)(parserExprEnd(body))(expression))
and parserParseTupleTail start reversedElements state =
    match parserConsume(Comma)(state) with
        | (_comma, afterComma) ->
            match parserParseExpression(afterComma) with
                | (element, afterElement) ->
                    let elements = element :: reversedElements
                    in
                        if parserCurrentKind(afterElement) == Comma
                        then parserParseTupleTail(start)(elements)(afterElement)
                        else
                            match parserConsume(RParen)(afterElement) with
                                | (rightParen, afterRightParen) ->
                                    (parserAt(
                                        start,
                                        tokenEnd(rightParen),
                                        elements
                                        |> reverseList
                                        |> ExprTuple
                                    ), afterRightParen)
and parserParseList state =
    match parserAdvance(state) with
        | (leftBracket, afterLeftBracket) ->
            if parserCurrentKind(afterLeftBracket) == RBracket
            then
                match parserAdvance(afterLeftBracket) with
                    | (rightBracket, afterRightBracket) ->
                        (parserAt(
                            leftBracket.position,
                            tokenEnd(rightBracket),
                            ExprList([])(false)
                        ), afterRightBracket)
            else
                match parserParseExpression(afterLeftBracket) with
                    | (first, afterFirst) ->
                        parserParseListTail(
                            leftBracket.position,
                            parserListStartsOnNewLine(afterLeftBracket)(leftBracket)(first),
                            first :: [],
                            afterFirst
                        )
and parserParseListTail start isMultiline reversedElements state =
    (let current = parserCurrent(state)
    in
        if current.kind == Comma
        then
            match parserAdvance(state) with
                | (_comma, afterComma) ->
                    match parserParseExpression(afterComma) with
                        | (element, afterElement) ->
                            parserParseListTail(
                                start,
                                isMultiline,
                                element :: reversedElements,
                                afterElement
                            )
        else
            match parserConsume(RBracket)(state) with
                | (rightBracket, afterRightBracket) ->
                    (parserAt(
                        start,
                        tokenEnd(rightBracket),
                        ExprList(reverseList(reversedElements))(isMultiline)
                    ), afterRightBracket))
and parserParsePattern state =
    match parserParsePatternAs(state) with
        | (first, afterFirst) -> parserParsePatternOrTail(first)(first :: [])(afterFirst)
and parserParsePatternOrTail first reversed state =
    if parserCurrentKind(state) != Pipe
    then
        match reversed with
            | _single :: [] -> (first, state)
            | _ ->
                let alternatives = reverseList(reversed)
                in
                    match reversed with
                        | last :: _ ->
                            (parserPatternAt(
                                parserPatternStart(first),
                                parserPatternEnd(last),
                                PatternOr(alternatives)
                            ), state)
                        | [] -> (first, state)
    else
        match parserAdvance(state) with
            | (_pipe, afterPipe) ->
                match parserParsePatternAs(afterPipe) with
                    | (alternative, afterAlternative) ->
                        parserParsePatternOrTail(
                            first,
                            alternative :: reversed,
                            afterAlternative
                        )
and parserParsePatternAs state =
    match parserParsePatternCons(state) with
        | (inner, afterInner) ->
            if parserCurrentKind(afterInner) != Ident
            then (inner, afterInner)
            else
                if parserCurrentText(afterInner) != "as"
                then (inner, afterInner)
                else
                    match parserAdvance(afterInner) with
                        | (_asToken, afterAs) ->
                            match parserConsume(Ident)(afterAs) with
                                | (name, afterName) ->
                                    let checkedState =
                                        if name.text == "_"
                                        then
                                            parserDiagnostic(
                                                afterName,
                                                name,
                                                "An as-pattern must bind a lower-case name other than '_'."
                                            )
                                        else
                                            if parserIsLowerName(name.text)
                                            then afterName
                                            else
                                                parserDiagnostic(
                                                    afterName,
                                                    name,
                                                    "An as-pattern must bind a lower-case name other than '_'."
                                                )
                                    in
                                        (parserPatternAt(
                                            parserPatternStart(inner),
                                            tokenEnd(name),
                                            PatternAs(inner)(name.text)
                                        ), checkedState)
and parserParsePatternCons state =
    match parserParsePatternPrimary(state) with
        | (head, afterHead) ->
            if parserCurrentKind(afterHead) != ColonColon
            then (head, afterHead)
            else
                match parserAdvance(afterHead) with
                    | (_colonColon, afterOperator) ->
                        match parserParsePatternCons(afterOperator) with
                            | (tail, afterTail) ->
                                (parserPatternAt(
                                    parserPatternStart(head),
                                    parserPatternEnd(tail),
                                    PatternCons(head)(tail)
                                ), afterTail)
and parserParsePatternPrimary state =
    match parserCurrentKind(state) with
        | LBracket -> parserParseEmptyListPattern(state)
        | Ident -> parserParseIdentifierPattern(state)
        | LParen -> parserParseParenthesizedPattern(state)
        | Int -> parserParseIntegerPattern(state)
        | String -> parserParseStringPattern(state)
        | Rune -> parserParseRunePattern(state)
        | True -> parserParseBooleanPattern(state)(true)
        | False -> parserParseBooleanPattern(state)(false)
        | Minus -> parserParseNegativeIntegerPattern(state)
        | _ -> parserBadPattern(state)
and parserParseEmptyListPattern state =
    match parserAdvance(state) with
        | (leftBracket, afterLeft) ->
            match parserConsume(RBracket)(afterLeft) with
                | (rightBracket, afterRight) ->
                    (parserPatternAt(
                        leftBracket.position,
                        tokenEnd(rightBracket),
                        PatternEmptyList
                    ), afterRight)
and parserParseIdentifierPattern state =
    match parserAdvance(state) with
        | (name, afterName) ->
            if name.text == "_"
            then
                (parserPatternAt(name.position)(tokenEnd(name))(PatternWildcard), afterName)
            else
                match parserCurrentKind(afterName) with
                    | LParen ->
                        match parserAdvance(afterName) with
                            | (_leftParen, afterLeftParen) ->
                                match parserParsePatternList(afterLeftParen)(RParen) with
                                    | (patterns, afterPatterns) ->
                                        let endPosition =
                                            match afterPatterns with
                                                | (next :: _, _diagnostics, _source) -> next.position
                                                | _ -> tokenEnd(name)
                                        in
                                            (parserPatternAt(
                                                name.position,
                                                endPosition,
                                                PatternConstructor(name.text)(patterns)
                                            ), afterPatterns)
                    | LBrace ->
                        match parserAdvance(afterName) with
                            | (_leftBrace, afterLeftBrace) ->
                                match parserParsePatternFields([])(afterLeftBrace) with
                                    | (fields, rightBrace, afterFields) ->
                                        (parserPatternAt(
                                            name.position,
                                            tokenEnd(rightBrace),
                                            PatternRecord(name.text)(fields)
                                        ), afterFields)
                    | _ ->
                        (parserPatternAt(name.position)(tokenEnd(name))(PatternVar(name.text)), afterName)
and parserParsePatternList state terminator =
    if parserCurrentKind(state) == terminator
    then
        match parserAdvance(state) with
            | (_terminator, afterTerminator) -> ([], afterTerminator)
    else
        match parserParsePattern(state) with
            | (first, afterFirst) -> parserParseMorePatterns(first :: [])(afterFirst)(terminator)
and parserParseMorePatterns reversed state terminator =
    if parserCurrentKind(state) == Comma
    then
        match parserAdvance(state) with
            | (_comma, afterComma) ->
                match parserParsePattern(afterComma) with
                    | (pattern, afterPattern) -> parserParseMorePatterns(pattern :: reversed)(afterPattern)(terminator)
    else
        match parserConsume(terminator)(state) with
            | (_terminator, afterTerminator) -> (reverseList(reversed), afterTerminator)
and parserParsePatternFields reversed state =
    if parserCurrentKind(state) == RBrace
    then
        match parserAdvance(state) with
            | (rightBrace, afterRightBrace) -> (reverseList(reversed), rightBrace, afterRightBrace)
    else
        match parserConsume(Ident)(state) with
            | (name, afterName) ->
                match parserConsume(Equals)(afterName) with
                    | (_equals, afterEquals) ->
                        match parserParsePattern(afterEquals) with
                            | (pattern, afterPattern) ->
                                let fields = (name.text, pattern) :: reversed
                                in
                                    if parserCurrentKind(afterPattern) == Comma
                                    then
                                        match parserAdvance(afterPattern) with
                                            | (_comma, afterComma) -> parserParsePatternFields(fields)(afterComma)
                                    else
                                        match parserConsume(RBrace)(afterPattern) with
                                            | (rightBrace, afterRightBrace) ->
                                                (reverseList(
                                                    fields
                                                ), rightBrace, afterRightBrace)
and parserParseParenthesizedPattern state =
    match parserAdvance(state) with
        | (leftParen, afterLeftParen) ->
            match parserParsePattern(afterLeftParen) with
                | (first, afterFirst) ->
                    if parserCurrentKind(afterFirst) != Comma
                    then
                        match parserConsume(RParen)(afterFirst) with
                            | (_rightParen, afterRightParen) -> (first, afterRightParen)
                    else parserParseTuplePatternTail(leftParen.position)(first :: [])(afterFirst)
and parserParseTuplePatternTail start reversed state =
    match parserConsume(Comma)(state) with
        | (_comma, afterComma) ->
            match parserParsePattern(afterComma) with
                | (pattern, afterPattern) ->
                    let elements = pattern :: reversed
                    in
                        if parserCurrentKind(afterPattern) == Comma
                        then parserParseTuplePatternTail(start)(elements)(afterPattern)
                        else
                            match parserConsume(RParen)(afterPattern) with
                                | (rightParen, afterRightParen) ->
                                    (parserPatternAt(
                                        start,
                                        tokenEnd(rightParen),
                                        elements
                                        |> reverseList
                                        |> PatternTuple
                                    ), afterRightParen)
and parserParseIntegerPattern state =
    match parserAdvance(state) with
        | (token, afterToken) ->
            (parserPatternAt(
                token.position,
                tokenEnd(token),
                PatternInt(token.intValue)
            ), afterToken)
and parserParseNegativeIntegerPattern state =
    match parserAdvance(state) with
        | (minus, afterMinus) ->
            if parserCurrentKind(afterMinus) != Int
            then parserBadPattern(afterMinus)
            else
                match parserAdvance(afterMinus) with
                    | (token, afterToken) ->
                        (parserPatternAt(
                            minus.position,
                            tokenEnd(token),
                            PatternInt(-token.intValue)
                        ), afterToken)
and parserParseStringPattern state =
    match parserAdvance(state) with
        | (token, afterToken) ->
            (parserPatternAt(
                token.position,
                tokenEnd(token),
                PatternString(token.text)
            ), afterToken)
and parserParseRunePattern state =
    match parserAdvance(state) with
        | (token, afterToken) ->
            (parserPatternAt(
                token.position,
                tokenEnd(token),
                PatternRune(token.intValue)
            ), afterToken)
and parserParseBooleanPattern state value =
    match parserAdvance(state) with
        | (token, afterToken) ->
            (parserPatternAt(token.position)(tokenEnd(token))(PatternBool(value)), afterToken)
and parserBadPattern state =
    (let current = parserCurrent(state)
    in
        let diagnosed =
            parserDiagnostic(
                state,
                current,
                "Expected pattern but found " + tokenKindName(current.kind) + "."
            )
        in
            match parserAdvance(diagnosed) with
                | (_bad, afterBad) ->
                    (parserPatternAt(current.position)(tokenEnd(current))(PatternWildcard), afterBad))
and parserPatternIsIrrefutable pattern =
    match parserUnspanPattern(pattern) with
        | PatternVar(_) -> true
        | PatternWildcard -> true
        | PatternTuple(elements) -> parserPatternsAreIrrefutable(elements)
        | PatternCons(head, tail) ->
            if parserPatternIsIrrefutable(head)
            then parserPatternIsIrrefutable(tail)
            else false
        | PatternRecord(_name, fields) -> parserPatternFieldsAreIrrefutable(fields)
        | PatternAs(inner, _name) -> parserPatternIsIrrefutable(inner)
        | _ -> false
and parserPatternsAreIrrefutable patterns =
    match patterns with
        | [] -> true
        | head :: tail ->
            if parserPatternIsIrrefutable(head)
            then parserPatternsAreIrrefutable(tail)
            else false
and parserPatternFieldsAreIrrefutable fields =
    match fields with
        | [] -> true
        | (_name, pattern) :: tail ->
            if parserPatternIsIrrefutable(pattern)
            then parserPatternFieldsAreIrrefutable(tail)
            else false
and parserParseLetPattern state =
    match parserAdvance(state) with
        | (letToken, afterLet) ->
            match parserParsePattern(afterLet) with
                | (pattern, afterPattern) ->
                    let checkedState =
                        if parserPatternIsIrrefutable(pattern)
                        then afterPattern
                        else
                            parserDiagnosticAtSpan(
                                afterPattern,
                                pattern
                                |> parserPatternSpan
                                |> Ashes.Internal.deepCopy,
                                "Refutable pattern in let binding. Only irrefutable patterns (variable, wildcard, tuple, cons) are allowed — use 'match' for refutable patterns."
                            )
                    in
                        match parserConsume(Equals)(checkedState) with
                            | (_equals, afterEquals) ->
                                match parserParseExpression(afterEquals) with
                                    | (value, afterValue) ->
                                        match parserConsume(In)(afterValue) with
                                            | (_inToken, afterIn) ->
                                                match parserParseExpression(afterIn) with
                                                    | (body, afterBody) ->
                                                        let cases = (pattern, body, None) :: []
                                                        in
                                                            (parserAt(
                                                                letToken.position,
                                                                parserExprEnd(body),
                                                                ExprMatch(value)(cases)(Some(letToken.position))
                                                            ), afterBody)
and parserBadPrimary state =
    (let current = parserCurrent(state)
    in
        let diagnosed =
            parserDiagnostic(
                state,
                current,
                "Expected expression but found " + tokenKindName(current.kind) + "."
            )
        in
            match parserAdvance(diagnosed) with
                | (_bad, afterBad) ->
                    (parserAt(current.position)(tokenEnd(current))(ExprInt(0)), afterBad))

let parserParseDelimitedTopLevelValue sourceBytes declarationColumn splitBindingPipes state =
    (let tokens = parserStateTokens(state)
    in
        match parserSplitTopLevelTokens(sourceBytes)(declarationColumn)(splitBindingPipes)(tokens) with
            | (valueTokens, remainingTokens) ->
                let boundaryPosition =
                    match remainingTokens with
                        | token :: _ -> token.position
                        | [] -> 0
                in
                    let temporaryTokens = appendList(valueTokens)(parserSyntheticToken(EOF)(boundaryPosition) :: [])
                    in
                        let temporaryState = parserStateWithTokens(state)(temporaryTokens)
                        in
                            match parserParseExpression(temporaryState) with
                                | (value, afterValue) ->
                                    let unconsumed =
                                        afterValue
                                        |> parserStateTokens
                                        |> parserTokensBeforeEof
                                    in
                                        let mergedTokens = appendList(unconsumed)(remainingTokens)
                                        in (value, parserStateWithTokens(afterValue)(mergedTokens)))

let parserParseTopLevelValue sourceBytes declarationColumn state =
    parserParseDelimitedTopLevelValue(
        sourceBytes,
        declarationColumn,
        false,
        state
    )

let parserParseDeclarationBindingValue sourceBytes declarationColumn state =
    parserParseDelimitedTopLevelValue(
        sourceBytes,
        declarationColumn,
        true,
        state
    )

// A top-level let whose next tokens form a pattern starts the trailing expression instead of a declaration.
// A monomorphic reverse for List(TopLevelItem) (the parsed program's top-level declarations). The
// generic `Ashes.Collection.List.reverse` corrupts an accumulator of heap-composite elements once
// enough allocation happens after the call — the same stage-0 bug already worked around above for
// sugar parameters and in SourceContext.ash's index builders; a monomorphic reverse over the
// concrete type is unaffected. Confirmed as the trigger for the self-hosted formatter segfaulting
// on a parsed program with any nested `let name param = ...` binding.
let recursive reverseTopLevelItems (items: List(TopLevelItem)) (acc: List(TopLevelItem)) =
    match items with
        | [] -> acc
        | head :: tail -> reverseTopLevelItems(tail)(head :: acc)

let recursive parserParseProgramItems sourceBytes reversedItems state =
    if parserIsExportDeclaration(state)
    then
        match parserParseExportDeclaration(state) with
            | (item, afterItem) -> parserParseProgramItems(sourceBytes)(item :: reversedItems)(afterItem)
    else
        match parserCurrentKind(state) with
            | Type ->
                match parserParseTypeTopLevel(state) with
                    | (item, afterItem) -> parserParseProgramItems(sourceBytes)(item :: reversedItems)(afterItem)
            | External ->
                match parserParseExternalTopLevel(state) with
                    | (item, afterItem) -> parserParseProgramItems(sourceBytes)(item :: reversedItems)(afterItem)
            | Capability ->
                match parserParseCapabilityTopLevel(sourceBytes)(state) with
                    | (item, afterItem) -> parserParseProgramItems(sourceBytes)(item :: reversedItems)(afterItem)
            | Provide ->
                match parserParseProvideTopLevel(sourceBytes)(state) with
                    | (item, afterItem) -> parserParseProgramItems(sourceBytes)(item :: reversedItems)(afterItem)
            | Trait ->
                match parserParseTraitTopLevel(sourceBytes)(state) with
                    | (item, afterItem) -> parserParseProgramItems(sourceBytes)(item :: reversedItems)(afterItem)
            | Implement ->
                match parserParseImplementationTopLevel(sourceBytes)(state) with
                    | (item, afterItem) -> parserParseProgramItems(sourceBytes)(item :: reversedItems)(afterItem)
            | Let ->
                if parserLetStartsPattern(state)
                then parserParseProgramBody(reversedItems)(state)
                else parserParseTopLevelLet(sourceBytes)(reversedItems)(state)
            | EOF ->
                match reversedItems with
                    | [] -> parserParseProgramBody(reversedItems)(state)
                    | _ -> (reverseTopLevelItems(reversedItems)([]), None, state)
            | _ -> parserParseProgramBody(reversedItems)(state)
and parserParseProgramBody reversedItems state =
    match parserParseExpression(state) with
        | (body, afterBody) -> (reverseTopLevelItems(reversedItems)([]), Some(body), afterBody)
and parserIsExportDeclaration state =
    if parserCurrentKind(state) != Ident
    then false
    else parserCurrentText(state) == "export"
and parserParseExportDeclaration state =
    match parserAdvance(state) with
        | (exportToken, afterExport) ->
            match parserConsume(LParen)(afterExport) with
                | (_leftParen, afterLeftParen) ->
                    match parserParseExportItems([])(afterLeftParen) with
                        | (items, rightParen, afterItems) ->
                            (parserTopLevelAt(
                                exportToken.position,
                                tokenEnd(rightParen),
                                TopLevelExport(ExportDecl(items = items))
                            ), afterItems)
and parserParseExportItems reversed state =
    if parserCurrentKind(state) == RParen
    then
        match parserAdvance(state) with
            | (rightParen, afterRightParen) -> (reverseList(reversed), rightParen, afterRightParen)
    else
        match parserParseExportItem(state) with
            | (Some(item), afterItem) -> parserParseExportItemsTail(item :: reversed)(afterItem)
            | (None, afterItem) -> parserParseExportItemsTail(reversed)(afterItem)
and parserParseExportItemsTail reversed state =
    if parserCurrentKind(state) == Comma
    then
        match parserAdvance(state) with
            | (_comma, afterComma) -> parserParseExportItems(reversed)(afterComma)
    else
        match parserConsume(RParen)(state) with
            | (rightParen, afterRightParen) -> (reverseList(reversed), rightParen, afterRightParen)
and parserParseExportItem state =
    if parserCurrentKind(state) == Type
    then
        match parserAdvance(state) with
            | (_typeToken, afterType) ->
                match parserConsume(Ident)(afterType) with
                    | (name, afterName) ->
                        if parserCurrentKind(afterName) != LParen
                        then
                            (ExportConstructorsHidden
                            |> ExportType(name.text)
                            |> Some, afterName)
                        else
                            match parserAdvance(afterName) with
                                | (_leftParen, afterLeftParen) ->
                                    if parserCurrentKind(afterLeftParen) == Dot
                                    then
                                        match parserAdvance(afterLeftParen) with
                                            | (_firstDot, afterFirstDot) ->
                                                match parserConsume(Dot)(afterFirstDot) with
                                                    | (_secondDot, afterSecondDot) ->
                                                        match parserConsume(RParen)(afterSecondDot) with
                                                            | (_rightParen, afterRightParen) ->
                                                                (Some(
                                                                    ExportType(name.text)(ExportConstructorsAll)
                                                                ), afterRightParen)
                                    else
                                        match parserParseExportNames([])(afterLeftParen) with
                                            | (names, afterNames) ->
                                                (Some(
                                                    ExportType(name.text)(ExportConstructorsSelected(names))
                                                ), afterNames)
    else
        match parserConsume(Ident)(state) with
            | (category, afterCategory) ->
                match parserConsume(Ident)(afterCategory) with
                    | (name, afterName) ->
                        if category.text == "value"
                        then (Some(ExportValue(name.text)), afterName)
                        else
                            if category.text == "module"
                            then (Some(ExportModule(name.text)), afterName)
                            else
                                (None, parserDiagnostic(
                                    afterName,
                                    category,
                                    "Expected 'value', 'type', or 'module' in export."
                                ))
and parserParseExportNames reversed state =
    match parserConsume(Ident)(state) with
        | (name, afterName) ->
            let names = name.text :: reversed
            in
                if parserCurrentKind(afterName) == Comma
                then
                    match parserAdvance(afterName) with
                        | (_comma, afterComma) -> parserParseExportNames(names)(afterComma)
                else
                    match parserConsume(RParen)(afterName) with
                        | (_rightParen, afterRightParen) -> (reverseList(names), afterRightParen)
and parserParseExternalTopLevel state =
    match parserAdvance(state) with
        | (externalToken, afterExternal) ->
            if parserCurrentKind(afterExternal) == Type
            then parserParseExternalType(externalToken.position)(afterExternal)
            else parserParseExternalFunction(externalToken.position)(afterExternal)
and parserParseExternalType start state =
    match parserAdvance(state) with
        | (_typeToken, afterType) ->
            match parserConsume(Ident)(afterType) with
                | (name, afterName) ->
                    if parserCurrentKind(afterName) == Ident
                    then
                        if parserCurrentText(afterName) == "resource"
                        then
                            match parserAdvance(afterName) with
                                | (_resourceToken, afterResource) ->
                                    match parserConsume(Ident)(afterResource) with
                                        | (destructorKeyword, afterDestructorKeyword) ->
                                            let checkedState =
                                                if destructorKeyword.text == "destructor"
                                                then afterDestructorKeyword
                                                else
                                                    parserDiagnosticWithCode(
                                                        afterDestructorKeyword,
                                                        destructorKeyword,
                                                        "Expected 'destructor' after external resource type.",
                                                        "ASH041"
                                                    )
                                            in
                                                match parserConsume(Ident)(checkedState) with
                                                    | (destructorName, afterDestructor) ->
                                                        (parserTopLevelAt(
                                                            start,
                                                            tokenEnd(destructorName),
                                                            TopLevelExternal(
                                                                ExternalOpaqueType(name.text)(Some(destructorName.text))
                                                            )
                                                        ), afterDestructor)
                        else
                            (parserTopLevelAt(
                                start,
                                tokenEnd(name),
                                None
                                |> ExternalOpaqueType(name.text)
                                |> TopLevelExternal
                            ), afterName)
                    else
                        (parserTopLevelAt(
                            start,
                            tokenEnd(name),
                            None
                            |> ExternalOpaqueType(name.text)
                            |> TopLevelExternal
                        ), afterName)
and parserParseExternalFunction start state =
    match parserConsume(Ident)(state) with
        | (name, afterName) ->
            match parserConsume(LParen)(afterName) with
                | (_leftParen, afterLeftParen) ->
                    match parserParseExternalParameters([])([])(afterLeftParen) with
                        | (parameterTypes, ownerships, afterParameters) ->
                            match parserConsume(Arrow)(afterParameters) with
                                | (_arrow, afterArrow) ->
                                    match parserParseFfiType(afterArrow) with
                                        | (returnType, returnEnd, afterReturn) ->
                                            let needsResult =
                                                if parserCurrentKind(afterReturn) == Needs
                                                then
                                                    match parserParseNeedsRow(afterReturn) with
                                                        | (needsRow, afterNeeds) -> (Some(needsRow), afterNeeds)
                                                else (None, afterReturn)
                                            in
                                                match needsResult with
                                                    | (needsRow, afterNeeds) ->
                                                        if parserCurrentKind(afterNeeds) == Equals
                                                        then
                                                            match parserAdvance(afterNeeds) with
                                                                | (_equals, afterEquals) ->
                                                                    match parserConsume(String)(afterEquals) with
                                                                        | (symbol, afterSymbol) ->
                                                                            (parserTopLevelAt(
                                                                                start,
                                                                                tokenEnd(symbol),
                                                                                TopLevelExternal(
                                                                                    ExternalFunction(
                                                                                        name.text,
                                                                                        parameterTypes,
                                                                                        returnType,
                                                                                        Some(symbol.text),
                                                                                        ownerships,
                                                                                        needsRow
                                                                                    )
                                                                                )
                                                                            ), afterSymbol)
                                                        else
                                                            (parserTopLevelAt(
                                                                start,
                                                                returnEnd,
                                                                TopLevelExternal(
                                                                    ExternalFunction(
                                                                        name.text,
                                                                        parameterTypes,
                                                                        returnType,
                                                                        None,
                                                                        ownerships,
                                                                        needsRow
                                                                    )
                                                                )
                                                            ), afterNeeds)
and parserParseExternalParameters reversedTypes reversedOwnerships state =
    if parserCurrentKind(state) == RParen
    then
        match parserAdvance(state) with
            | (_rightParen, afterRightParen) ->
                (reverseList(reversedTypes), reverseList(
                    reversedOwnerships
                ), afterRightParen)
    else
        match parserParseExternalOwnership(state) with
            | (ownership, afterOwnership) ->
                match parserParseFfiType(afterOwnership) with
                    | (parameterType, _endPosition, afterType) ->
                        if parserCurrentKind(afterType) == Comma
                        then
                            match parserAdvance(afterType) with
                                | (_comma, afterComma) ->
                                    parserParseExternalParameters(
                                        parameterType :: reversedTypes,
                                        ownership :: reversedOwnerships,
                                        afterComma
                                    )
                        else
                            match parserConsume(RParen)(afterType) with
                                | (_rightParen, afterRightParen) ->
                                    (reverseList(
                                        parameterType :: reversedTypes
                                    ), reverseList(
                                        ownership :: reversedOwnerships
                                    ), afterRightParen)
and parserParseExternalOwnership state =
    if parserCurrentKind(state) != Ident
    then (ExternalOwnershipUnspecified, state)
    else
        if parserCurrentText(state) == "borrow"
        then
            match parserAdvance(state) with
                | (_borrow, afterBorrow) -> (ExternalOwnershipBorrow, afterBorrow)
        else
            if parserCurrentText(state) == "consume"
            then
                match parserAdvance(state) with
                    | (_consume, afterConsume) -> (ExternalOwnershipConsume, afterConsume)
            else (ExternalOwnershipUnspecified, state)
and parserParseFfiType state =
    if parserCurrentKind(state) == Star
    then
        match parserAdvance(state) with
            | (_star, afterStar) ->
                match parserParseFfiType(afterStar) with
                    | (inner, endPosition, afterInner) -> (ParsedPointer(inner), endPosition, afterInner)
    else
        match parserConsume(Ident)(state) with
            | (name, afterName) ->
                if name.text == "out"
                then
                    match parserParseFfiType(afterName) with
                        | (inner, endPosition, afterInner) -> (ParsedOut(inner), endPosition, afterInner)
                else
                    if name.text == "FfiBuffer"
                    then
                        match parserConsume(LParen)(afterName) with
                            | (_leftParen, afterLeftParen) ->
                                match parserParseFfiType(afterLeftParen) with
                                    | (inner, _innerEnd, afterInner) ->
                                        match parserConsume(RParen)(afterInner) with
                                            | (rightParen, afterRightParen) ->
                                                (ParsedBuffer(inner), tokenEnd(
                                                    rightParen
                                                ), afterRightParen)
                    else
                        if name.text == "FfiStr"
                        then parserParseFfiString(afterName)
                        else (ParsedNamed(name.text), tokenEnd(name), afterName)
and parserParseFfiString state =
    match parserConsume(LParen)(state) with
        | (_leftParen, afterLeftParen) ->
            let nullableResult =
                if parserCurrentKind(afterLeftParen) == Ident
                then
                    if parserCurrentText(afterLeftParen) == "nullable"
                    then
                        match parserAdvance(afterLeftParen) with
                            | (_nullable, afterNullable) -> (true, afterNullable)
                    else (false, afterLeftParen)
                else (false, afterLeftParen)
            in
                match nullableResult with
                    | (nullable, afterNullable) ->
                        match parserConsume(Ident)(afterNullable) with
                            | (ownership, afterOwnership) ->
                                if ownership.text == "owned"
                                then
                                    if parserCurrentKind(afterOwnership) == Ident
                                    then
                                        match parserAdvance(afterOwnership) with
                                            | (destructor, afterDestructor) ->
                                                match parserConsume(RParen)(afterDestructor) with
                                                    | (rightParen, afterRightParen) ->
                                                        (ParsedNativeString(
                                                            nullable,
                                                            FfiStringOwned,
                                                            Some(destructor.text)
                                                        ), tokenEnd(rightParen), afterRightParen)
                                    else
                                        let diagnosed =
                                            parserDiagnosticWithCode(
                                                afterOwnership,
                                                parserCurrent(afterOwnership),
                                                "Owned FfiStr requires an external destructor name.",
                                                "ASH046"
                                            )
                                        in
                                            match parserConsume(RParen)(diagnosed) with
                                                | (rightParen, afterRightParen) ->
                                                    (ParsedNativeString(
                                                        nullable,
                                                        FfiStringOwned,
                                                        None
                                                    ), tokenEnd(rightParen), afterRightParen)
                                else
                                    let checkedState =
                                        if ownership.text == "borrowed"
                                        then afterOwnership
                                        else
                                            parserDiagnosticWithCode(
                                                afterOwnership,
                                                ownership,
                                                "FfiStr expects 'borrowed' or 'owned disposeName'.",
                                                "ASH046"
                                            )
                                    in
                                        match parserConsume(RParen)(checkedState) with
                                            | (rightParen, afterRightParen) ->
                                                (ParsedNativeString(
                                                    nullable,
                                                    FfiStringBorrowed,
                                                    None
                                                ), tokenEnd(rightParen), afterRightParen)
and parserParseCapabilityTopLevel sourceBytes state =
    match parserAdvance(state) with
        | (startToken, afterStart) ->
            match parserConsume(Ident)(afterStart) with
                | (name, afterName) ->
                    match parserParseTypeParameters(afterName) with
                        | (parameters, afterParameters) ->
                            match parserConsume(Equals)(afterParameters) with
                                | (_equals, afterEquals) ->
                                    match parserParseCapabilityOperations(
                                        sourceBytes,
                                        parserSourceColumn(sourceBytes)(startToken.position),
                                        [],
                                        afterEquals
                                    ) with
                                        | (operations, afterOperations) ->
                                            let checkedState =
                                                match operations with
                                                    | [] ->
                                                        parserDiagnostic(
                                                            afterOperations,
                                                            parserCurrent(afterOperations),
                                                            "Capability '" + name.text + "' must declare at least one operation."
                                                        )
                                                    | _ -> afterOperations
                                            in
                                                (parserTopLevelAt(
                                                    startToken.position,
                                                    parserCurrentPosition(afterOperations),
                                                    TopLevelCapability(
                                                        CapabilityDecl(name = name.text, typeParameters = parameters, operations = operations)
                                                    )
                                                ), checkedState)
and parserParseCapabilityOperations sourceBytes declarationColumn reversed state =
    if parserCurrentKind(state) != Pipe
    then (reverseList(reversed), state)
    else
        match parserAdvance(state) with
            | (_pipe, afterPipe) ->
                match parserConsume(Ident)(afterPipe) with
                    | (name, afterName) ->
                        if parserCurrentKind(afterName) == Colon
                        then
                            match parserAdvance(afterName) with
                                | (_colon, afterColon) ->
                                    match parserParseDelimitedTypeValue(sourceBytes)(declarationColumn)(afterColon) with
                                        | (signature, afterSignature) ->
                                            parserParseCapabilityOperations(
                                                sourceBytes,
                                                declarationColumn,
                                                CapabilityOperation(name = name.text, signature = Some(
                                                    signature
                                                )) :: reversed,
                                                afterSignature
                                            )
                        else
                            parserParseCapabilityOperations(
                                sourceBytes,
                                declarationColumn,
                                CapabilityOperation(name = name.text, signature = None) :: reversed,
                                afterName
                            )
and parserParseDelimitedTypeValue sourceBytes declarationColumn state =
    match state
    |> parserStateTokens
    |> parserSplitTopLevelTokens(sourceBytes)(declarationColumn)(true) with
        | (typeTokens, remainingTokens) ->
            let boundaryPosition =
                match remainingTokens with
                    | token :: _ -> token.position
                    | [] -> 0
            in
                let temporaryState =
                    parserStateWithTokens(
                        state,
                        appendList(typeTokens)(parserSyntheticToken(EOF)(boundaryPosition) :: [])
                    )
                in
                    match parserParseTypeExpressionState(temporaryState) with
                        | (typeExpression, afterType) ->
                            let unconsumed =
                                afterType
                                |> parserStateTokens
                                |> parserTokensBeforeEof
                            in
                                (typeExpression, parserStateWithTokens(
                                    afterType,
                                    appendList(unconsumed)(remainingTokens)
                                ))
and parserParseOptionalTypeArguments state =
    if parserCurrentKind(state) != LParen
    then ([], state)
    else
        match parserAdvance(state) with
            | (_leftParen, afterLeftParen) ->
                match parserParseTypeArguments(afterLeftParen) with
                    | (arguments, _rightParen, afterArguments) -> (arguments, afterArguments)
and parserParseProvideTopLevel sourceBytes state =
    match parserAdvance(state) with
        | (startToken, afterStart) ->
            match parserConsume(Ident)(afterStart) with
                | (name, afterName) ->
                    match parserParseOptionalTypeArguments(afterName) with
                        | (arguments, afterArguments) ->
                            match parserConsume(Equals)(afterArguments) with
                                | (_equals, afterEquals) ->
                                    match parserParseProvideBindings(
                                        sourceBytes,
                                        parserSourceColumn(sourceBytes)(startToken.position),
                                        [],
                                        afterEquals
                                    ) with
                                        | (bindings, afterBindings) ->
                                            let checkedState =
                                                match bindings with
                                                    | [] ->
                                                        parserDiagnostic(
                                                            afterBindings,
                                                            parserCurrent(afterBindings),
                                                            "Provider for '" + name.text + "' must supply at least one operation."
                                                        )
                                                    | _ -> afterBindings
                                            in
                                                (parserTopLevelAt(
                                                    startToken.position,
                                                    parserCurrentPosition(afterBindings),
                                                    TopLevelProvide(
                                                        ProvideDecl(capabilityName = name.text, typeArguments = arguments, bindings = bindings)
                                                    )
                                                ), checkedState)
and parserParseProvideBindings sourceBytes declarationColumn reversed state =
    if parserCurrentKind(state) != Pipe
    then (reverseList(reversed), state)
    else
        match parserAdvance(state) with
            | (_pipe, afterPipe) ->
                match parserConsume(Ident)(afterPipe) with
                    | (name, afterName) ->
                        match parserConsume(Equals)(afterName) with
                            | (_equals, afterEquals) ->
                                match parserParseDeclarationBindingValue(
                                    sourceBytes,
                                    declarationColumn,
                                    afterEquals
                                ) with
                                    | (implementation, afterImplementation) ->
                                        parserParseProvideBindings(
                                            sourceBytes,
                                            declarationColumn,
                                            ProvideBinding(operationName = name.text, implementation = implementation) :: reversed,
                                            afterImplementation
                                        )
and parserParseTraitTopLevel sourceBytes state =
    match parserAdvance(state) with
        | (startToken, afterStart) ->
            match parserConsume(Ident)(afterStart) with
                | (name, afterName) ->
                    match parserParseTypeParameters(afterName) with
                        | (parameters, afterParameters) ->
                            let supertraitResult =
                                if parserCurrentKind(afterParameters) == Requires
                                then parserParseRequiresClause(afterParameters)
                                else ([], afterParameters)
                            in
                                match supertraitResult with
                                    | (supertraits, afterSupertraits) ->
                                        match parserConsume(Equals)(afterSupertraits) with
                                            | (_equals, afterEquals) ->
                                                match parserParseTraitMethods(
                                                    sourceBytes,
                                                    parserSourceColumn(sourceBytes)(startToken.position),
                                                    [],
                                                    afterEquals
                                                ) with
                                                    | (methods, afterMethods) ->
                                                        let parameterChecked =
                                                            match parameters with
                                                                | [] ->
                                                                    parserDiagnostic(
                                                                        afterMethods,
                                                                        name,
                                                                        "Trait '" + name.text + "' must declare at least one type parameter."
                                                                    )
                                                                | _ -> afterMethods
                                                        in
                                                            let methodChecked =
                                                                match methods with
                                                                    | [] ->
                                                                        parserDiagnostic(
                                                                            parameterChecked,
                                                                            parserCurrent(parameterChecked),
                                                                            "Trait '" + name.text + "' must declare at least one method."
                                                                        )
                                                                    | _ -> parameterChecked
                                                            in
                                                                (parserTopLevelAt(
                                                                    startToken.position,
                                                                    parserCurrentPosition(afterMethods),
                                                                    TopLevelTrait(
                                                                        TraitDecl(name = name.text, typeParameters = parameters, supertraits = supertraits, methods = methods)
                                                                    )
                                                                ), methodChecked)
and parserParseTraitMethods sourceBytes declarationColumn reversed state =
    if parserCurrentKind(state) != Pipe
    then (reverseList(reversed), state)
    else
        match parserAdvance(state) with
            | (_pipe, afterPipe) ->
                match parserConsume(Ident)(afterPipe) with
                    | (name, afterName) ->
                        match parserConsume(Colon)(afterName) with
                            | (_colon, afterColon) ->
                                match parserParseTypeExpressionState(afterColon) with
                                    | (signature, afterSignature) ->
                                        if parserCurrentKind(afterSignature) == Equals
                                        then
                                            match parserAdvance(afterSignature) with
                                                | (_equals, afterEquals) ->
                                                    match parserParseDeclarationBindingValue(
                                                        sourceBytes,
                                                        declarationColumn,
                                                        afterEquals
                                                    ) with
                                                        | (implementation, afterImplementation) ->
                                                            parserParseTraitMethods(
                                                                sourceBytes,
                                                                declarationColumn,
                                                                TraitMethodDecl(name = name.text, signature = signature, defaultImplementation = Some(
                                                                    implementation
                                                                )) :: reversed,
                                                                afterImplementation
                                                            )
                                        else
                                            parserParseTraitMethods(
                                                sourceBytes,
                                                declarationColumn,
                                                TraitMethodDecl(name = name.text, signature = signature, defaultImplementation = None) :: reversed,
                                                afterSignature
                                            )
and parserParseTraitApplication state =
    match parserParseQualifiedIdentifier(state) with
        | (name, afterName) ->
            match parserConsume(LParen)(afterName) with
                | (_leftParen, afterLeftParen) ->
                    match parserParseTypeArguments(afterLeftParen) with
                        | (arguments, _rightParen, afterArguments) -> (name, arguments, afterArguments)
and parserParseImplementationTopLevel sourceBytes state =
    match parserAdvance(state) with
        | (startToken, afterStart) ->
            match parserParseTraitApplication(afterStart) with
                | (traitName, arguments, afterApplication) ->
                    let requirementResult =
                        if parserCurrentKind(afterApplication) == Requires
                        then parserParseRequiresClause(afterApplication)
                        else ([], afterApplication)
                    in
                        match requirementResult with
                            | (requirements, afterRequirements) ->
                                match parserConsume(Equals)(afterRequirements) with
                                    | (_equals, afterEquals) ->
                                        match parserParseImplementationBindings(
                                            sourceBytes,
                                            parserSourceColumn(sourceBytes)(startToken.position),
                                            [],
                                            afterEquals
                                        ) with
                                            | (bindings, afterBindings) ->
                                                let checkedState =
                                                    match bindings with
                                                        | [] ->
                                                            parserDiagnostic(
                                                                afterBindings,
                                                                parserCurrent(afterBindings),
                                                                "Implementation of '" + traitName + "' must supply at least one method."
                                                            )
                                                        | _ -> afterBindings
                                                in
                                                    (parserTopLevelAt(
                                                        startToken.position,
                                                        parserCurrentPosition(afterBindings),
                                                        TopLevelImplementation(
                                                            TraitImplementationDecl(traitName = traitName, typeArguments = arguments, requirements = requirements, bindings = bindings)
                                                        )
                                                    ), checkedState)
and parserParseImplementationBindings sourceBytes declarationColumn reversed state =
    if parserCurrentKind(state) != Pipe
    then (reverseList(reversed), state)
    else
        match parserAdvance(state) with
            | (_pipe, afterPipe) ->
                match parserConsume(Ident)(afterPipe) with
                    | (name, afterName) ->
                        match parserConsume(Equals)(afterName) with
                            | (_equals, afterEquals) ->
                                match parserParseDeclarationBindingValue(
                                    sourceBytes,
                                    declarationColumn,
                                    afterEquals
                                ) with
                                    | (implementation, afterImplementation) ->
                                        parserParseImplementationBindings(
                                            sourceBytes,
                                            declarationColumn,
                                            TraitImplementationMethodBinding(methodName = name.text, implementation = implementation) :: reversed,
                                            afterImplementation
                                        )
and parserParseTypeTopLevel state =
    match parserAdvance(state) with
        | (typeToken, afterType) ->
            if parserCurrentKind(afterType) == Ident
            then
                if parserCurrentText(afterType) == "alias"
                then parserParseTypeAliasDeclaration(typeToken.position)(afterType)
                else parserParseNamedTypeDeclaration(typeToken.position)(afterType)
            else parserParseNamedTypeDeclaration(typeToken.position)(afterType)
and parserParseTypeAliasDeclaration start state =
    match parserAdvance(state) with
        | (_alias, afterAlias) ->
            match parserConsume(Ident)(afterAlias) with
                | (name, afterName) ->
                    match parserParseTypeParameters(afterName) with
                        | (parameters, afterParameters) ->
                            match parserConsume(Equals)(afterParameters) with
                                | (_equals, afterEquals) ->
                                    match parserParseTypeExpressionState(afterEquals) with
                                        | (target, afterTarget) ->
                                            (parserTopLevelAt(
                                                start,
                                                parserTypeEnd(target),
                                                TopLevelTypeAlias(
                                                    TypeAliasDecl(name = name.text, typeParameters = parameters, target = target)
                                                )
                                            ), afterTarget)
and parserParseNamedTypeDeclaration start state =
    match parserConsume(Ident)(state) with
        | (name, afterName) ->
            match parserParseTypeParameters(afterName) with
                | (parameters, afterParameters) ->
                    match parserConsume(Equals)(afterParameters) with
                        | (_equals, afterEquals) ->
                            if parserCurrentKind(afterEquals) == Ident
                            then parserParseZeroCostType(start)(name.text)(parameters)(afterEquals)
                            else parserParseAlgebraicType(start)(name.text)(parameters)(afterEquals)
and parserParseTypeParameters state =
    if parserCurrentKind(state) != LParen
    then ([], state)
    else
        match parserAdvance(state) with
            | (_leftParen, afterLeftParen) ->
                if parserCurrentKind(afterLeftParen) == RParen
                then
                    match parserAdvance(afterLeftParen) with
                        | (_rightParen, afterRightParen) -> ([], afterRightParen)
                else parserParseTypeParameterList([])(afterLeftParen)
and parserParseTypeParameterList reversed state =
    match parserConsume(Ident)(state) with
        | (name, afterName) ->
            let parameters = TypeParameter(name = name.text) :: reversed
            in
                if parserCurrentKind(afterName) == Comma
                then
                    match parserAdvance(afterName) with
                        | (_comma, afterComma) -> parserParseTypeParameterList(parameters)(afterComma)
                else
                    match parserConsume(RParen)(afterName) with
                        | (_rightParen, afterRightParen) -> (reverseList(parameters), afterRightParen)
and parserParseZeroCostType start typeName parameters state =
    match parserConsume(Ident)(state) with
        | (constructorName, afterConstructor) ->
            let payloadResult =
                if parserCurrentKind(afterConstructor) == LParen
                then
                    match parserAdvance(afterConstructor) with
                        | (_leftParen, afterLeftParen) -> parserParseTypeArguments(afterLeftParen)
                else ([], constructorName, afterConstructor)
            in
                match payloadResult with
                    | (payloads, endToken, afterPayloads) ->
                        let checkedState =
                            match payloads with
                                | _single :: [] -> afterPayloads
                                | _ ->
                                    parserDiagnosticWithCode(
                                        afterPayloads,
                                        constructorName,
                                        "Type '" + typeName + "' must have exactly one constructor payload.",
                                        "ASH040"
                                    )
                        in
                            match parserParseDerivingClause([])(tokenEnd(endToken))(checkedState) with
                                | (traits, endPosition, afterDeriving) ->
                                    let constructor = TypeConstructor(name = constructorName.text, parameters = payloads, fieldNames = [])
                                    in
                                        let declaration = ZeroCostTypeDecl(name = typeName, typeParameters = parameters, constructor = constructor, derivingTraits = traits)
                                        in
                                            (parserTopLevelAt(
                                                start,
                                                endPosition,
                                                TopLevelZeroCostType(declaration)
                                            ), afterDeriving)
and parserParseAlgebraicType start typeName parameters state =
    match parserParseTypeBranches([])([])([])(false)(false)(start)(state) with
        | ParsedTypeBranches { constructors = constructors, fieldNames = fieldNames, fieldTypes = fieldTypes, sawField = sawField, sawConstructor = sawConstructor, endPosition = branchEnd, state = afterBranches } ->
            let checkedState =
                if sawField
                then
                    if sawConstructor
                    then
                        parserDiagnostic(
                            afterBranches,
                            parserCurrent(afterBranches),
                            "Record field alternatives cannot be mixed with constructor alternatives."
                        )
                    else afterBranches
                else
                    match constructors with
                        | [] ->
                            parserDiagnosticNoCode(
                                afterBranches,
                                parserCurrent(afterBranches),
                                "Type '" + typeName + "' must have at least one constructor."
                            )
                        | _ -> afterBranches
            in
                match parserParseDerivingClause([])(branchEnd)(checkedState) with
                    | (traits, endPosition, afterDeriving) ->
                        if sawField
                        then
                            let recordConstructor = TypeConstructor(name = typeName, parameters = fieldTypes, fieldNames = fieldNames)
                            in
                                let declaration = TypeDecl(name = typeName, typeParameters = parameters, constructors = recordConstructor :: [], isRecord = true, derivingTraits = traits)
                                in (parserTopLevelAt(start)(endPosition)(TopLevelType(declaration)), afterDeriving)
                        else
                            let declaration = TypeDecl(name = typeName, typeParameters = parameters, constructors = constructors, isRecord = false, derivingTraits = traits)
                            in (parserTopLevelAt(start)(endPosition)(TopLevelType(declaration)), afterDeriving)
and parserParseTypeBranches constructors fieldNames fieldTypes sawField sawConstructor endPosition state =
    if parserCurrentKind(state) != Pipe
    then
        ParsedTypeBranches(constructors = reverseList(
            constructors
        ), fieldNames = reverseList(fieldNames), fieldTypes = reverseList(
            fieldTypes
        ), sawField = sawField, sawConstructor = sawConstructor, endPosition = endPosition, state = state)
    else
        match parserAdvance(state) with
            | (_pipe, afterPipe) ->
                match parserConsume(Ident)(afterPipe) with
                    | (branchName, afterName) ->
                        if parserCurrentKind(afterName) == Colon
                        then
                            match parserAdvance(afterName) with
                                | (_colon, afterColon) ->
                                    match parserParseTypeExpressionState(afterColon) with
                                        | (fieldType, afterField) ->
                                            parserParseTypeBranches(
                                                constructors,
                                                branchName.text :: fieldNames,
                                                fieldType :: fieldTypes,
                                                true,
                                                sawConstructor,
                                                parserTypeEnd(fieldType),
                                                afterField
                                            )
                        else
                            let payloadResult =
                                if parserCurrentKind(afterName) == LParen
                                then
                                    match parserAdvance(afterName) with
                                        | (_leftParen, afterLeftParen) -> parserParseTypeArguments(afterLeftParen)
                                else ([], branchName, afterName)
                            in
                                match payloadResult with
                                    | (payloads, endToken, afterPayloads) ->
                                        let constructor = TypeConstructor(name = branchName.text, parameters = payloads, fieldNames = [])
                                        in
                                            parserParseTypeBranches(
                                                constructor :: constructors,
                                                fieldNames,
                                                fieldTypes,
                                                sawField,
                                                true,
                                                tokenEnd(endToken),
                                                afterPayloads
                                            )
and parserParseDerivingClause reversed endPosition state =
    if parserCurrentKind(state) != Deriving
    then (reverseList(reversed), endPosition, state)
    else
        match parserAdvance(state) with
            | (_deriving, afterDeriving) ->
                match parserConsume(LBrace)(afterDeriving) with
                    | (_leftBrace, afterLeftBrace) ->
                        if parserCurrentKind(afterLeftBrace) == RBrace
                        then
                            let diagnosed =
                                parserDiagnostic(
                                    afterLeftBrace,
                                    parserCurrent(afterLeftBrace),
                                    "A 'deriving' clause must contain at least one trait."
                                )
                            in
                                match parserAdvance(diagnosed) with
                                    | (rightBrace, afterRightBrace) ->
                                        (reverseList(reversed), tokenEnd(
                                            rightBrace
                                        ), afterRightBrace)
                        else parserParseDerivingTraits(reversed)(afterLeftBrace)
and parserParseDerivingTraits reversed state =
    match parserParseQualifiedIdentifier(state) with
        | (traitName, afterTrait) ->
            let traits = traitName :: reversed
            in
                if parserCurrentKind(afterTrait) == Comma
                then
                    match parserAdvance(afterTrait) with
                        | (_comma, afterComma) -> parserParseDerivingTraits(traits)(afterComma)
                else
                    match parserConsume(RBrace)(afterTrait) with
                        | (rightBrace, afterRightBrace) -> (reverseList(traits), tokenEnd(rightBrace), afterRightBrace)
and parserParseTopLevelLet sourceBytes reversedItems state =
    match parserAdvance(state) with
        | (letToken, afterLet) ->
            let recursiveBinding = parserCurrentKind(afterLet) == Recursive
            in
                let afterRecursive =
                    if recursiveBinding
                    then
                        match parserAdvance(afterLet) with
                            | (_recursive, next) -> next
                    else afterLet
                in
                    match parserParseTopLevelBinding(
                        sourceBytes,
                        parserSourceColumn(sourceBytes)(letToken.position),
                        afterRecursive
                    ) with
                        | (binding, valueEnd, valueLeadsWithLet, afterBinding) ->
                            match binding with
                                | LetBindingSyntax { name = bindingName, value = bindingValue, sugarParameters = sugarParameters, typeAnnotation = typeAnnotation, requirements = requirements } ->
                                    if parserCurrentKind(afterBinding) == In
                                    then
                                        match parserAdvance(afterBinding) with
                                            | (_inToken, afterIn) ->
                                                match parserParseExpression(afterIn) with
                                                    | (body, afterBody) ->
                                                        let expression =
                                                            if recursiveBinding
                                                            then
                                                                ExprLetRecursive(
                                                                    bindingName,
                                                                    bindingValue,
                                                                    body,
                                                                    sugarParameters,
                                                                    typeAnnotation,
                                                                    requirements
                                                                )
                                                            else
                                                                ExprLet(
                                                                    bindingName,
                                                                    bindingValue,
                                                                    body,
                                                                    sugarParameters,
                                                                    typeAnnotation,
                                                                    requirements
                                                                )
                                                        in
                                                            (reverseList(reversedItems), Some(
                                                                parserAt(
                                                                    letToken.position,
                                                                    parserExprEnd(body),
                                                                    expression
                                                                )
                                                            ), afterBody)
                                    else
                                        if parserCurrentKind(afterBinding) == And
                                        then
                                            parserParseRecursiveGroup(
                                                sourceBytes,
                                                reversedItems,
                                                letToken.position,
                                                binding :: [],
                                                recursiveBinding,
                                                afterBinding
                                            )
                                        else
                                            if valueLeadsWithLet
                                            then
                                                if parserCurrentKind(afterBinding) == EOF
                                                then
                                                    let missingIn =
                                                        parserDiagnostic(
                                                            afterBinding,
                                                            parserCurrent(afterBinding),
                                                            "Expected In but found EOF."
                                                        )
                                                    in
                                                        let missingBodyState =
                                                            parserDiagnostic(
                                                                missingIn,
                                                                parserCurrent(missingIn),
                                                                "Expected expression but found EOF."
                                                            )
                                                        in
                                                            let missingBody = parserAt(valueEnd)(valueEnd)(ExprInt(0))
                                                            in
                                                                let expression =
                                                                    if recursiveBinding
                                                                    then
                                                                        ExprLetRecursive(
                                                                            bindingName,
                                                                            bindingValue,
                                                                            missingBody,
                                                                            sugarParameters,
                                                                            typeAnnotation,
                                                                            requirements
                                                                        )
                                                                    else
                                                                        ExprLet(
                                                                            bindingName,
                                                                            bindingValue,
                                                                            missingBody,
                                                                            sugarParameters,
                                                                            typeAnnotation,
                                                                            requirements
                                                                        )
                                                                in
                                                                    (reverseList(reversedItems), Some(
                                                                        parserAt(
                                                                            letToken.position,
                                                                            valueEnd,
                                                                            expression
                                                                        )
                                                                    ), missingBodyState)
                                                else
                                                    let item =
                                                        parserTopLevelAt(
                                                            letToken.position,
                                                            valueEnd,
                                                            TopLevelLet(binding)(recursiveBinding)
                                                        )
                                                    in
                                                        parserParseProgramItems(
                                                            sourceBytes,
                                                            item :: reversedItems,
                                                            afterBinding
                                                        )
                                            else
                                                let item =
                                                    parserTopLevelAt(
                                                        letToken.position,
                                                        valueEnd,
                                                        TopLevelLet(binding)(recursiveBinding)
                                                    )
                                                in
                                                    parserParseProgramItems(
                                                        sourceBytes,
                                                        item :: reversedItems,
                                                        afterBinding
                                                    )
and parserParseTopLevelBinding sourceBytes declarationColumn state =
    match parserConsume(Ident)(state) with
        | (name, afterName) ->
            let header =
                if parserCurrentKind(afterName) == Colon
                then
                    match parserAdvance(afterName) with
                        | (_colon, afterColon) ->
                            match parserParseTypeExpressionState(afterColon) with
                                | (annotation, afterAnnotation) ->
                                    if parserCurrentKind(afterAnnotation) == Requires
                                    then
                                        match parserParseRequiresClause(afterAnnotation) with
                                            | (requirements, afterRequirements) ->
                                                ([], Some(
                                                    annotation
                                                ), requirements, afterRequirements)
                                    else ([], Some(annotation), [], afterAnnotation)
                else
                    match parserParseSugarParameters([])(afterName) with
                        | (parameters, afterParameters) -> (parameters, None, [], afterParameters)
            in
                match header with
                    | (parameters, annotation, requirements, afterHeader) ->
                        match parserConsume(Equals)(afterHeader) with
                            | (_equals, afterEquals) ->
                                let valueLeadsWithLet =
                                    match parameters with
                                        | [] ->
                                            match parserCurrentKind(afterEquals) with
                                                | Let -> true
                                                | LetBang -> true
                                                | LetQuestion -> true
                                                | _ -> false
                                        | _ -> false
                                in
                                    match parserParseTopLevelValue(sourceBytes)(declarationColumn)(afterEquals) with
                                        | (rawValue, afterValue) ->
                                            // Deep-copied immediately: `value` is the parsed value expression, which
                                            // may itself contain a nested `let` whose own sugar-parameter list
                                            // (`AshesCompiler.Frontend.Syntax.ExprLet`'s `sugarParameters: List(Str)`)
                                            // was built in an inner arena scope that this binding's own later
                                            // processing (e.g. parserExprEnd below) can reclaim and reuse — a stage-0
                                            // arena-escape gap where a nested value's own nested owned lists don't
                                            // reliably survive their enclosing parse returning. Copying the whole
                                            // tree here, before anything else runs, keeps every nested list intact.
                                            let value =
                                                name.position
                                                |> parserBuildLambdas(parameters)(rawValue)
                                                |> Ashes.Internal.deepCopy
                                            in
                                                let binding =
                                                    LetBindingSyntax(name = name.text, value = value, sugarParameters = parserParameterNames(
                                                        parameters
                                                    ), typeAnnotation = annotation, requirements = requirements)
                                                in (binding, parserExprEnd(value), valueLeadsWithLet, afterValue)
and parserParseRecursiveGroup sourceBytes reversedItems start reversedBindings recursiveBinding state =
    (let checkedState =
        if recursiveBinding
        then state
        else
            parserDiagnostic(state)(parserCurrent(state))("'and' is only allowed in a 'let recursive' binding group.")
    in
        match parserAdvance(checkedState) with
            | (andToken, afterAnd) ->
                match parserParseTopLevelBinding(
                    sourceBytes,
                    parserSourceColumn(sourceBytes)(andToken.position),
                    afterAnd
                ) with
                    | (binding, valueEnd, _valueLeadsWithLet, afterBinding) ->
                        let bindings = binding :: reversedBindings
                        in
                            if parserCurrentKind(afterBinding) == And
                            then
                                parserParseRecursiveGroup(
                                    sourceBytes,
                                    reversedItems,
                                    start,
                                    bindings,
                                    recursiveBinding,
                                    afterBinding
                                )
                            else
                                let item =
                                    parserTopLevelAt(
                                        start,
                                        valueEnd,
                                        bindings
                                        |> reverseList
                                        |> TopLevelRecursiveGroup
                                    )
                                in parserParseProgramItems(sourceBytes)(item :: reversedItems)(afterBinding))

// Every top-level parse entry point reports the same generic message regardless of what kind of
// unit it parsed (a whole program, one expression, or one type expression): a trailing token past
// the parsed unit is always "unexpected... after end of expression".
let parserEnsureEndOfInput state =
    (let current = parserCurrent(state)
    in
        if current.kind == EOF
        then state
        else parserDiagnostic(state)(current)("Unexpected token after end of expression: " + tokenKindName(current.kind) + "."))

let parseProgram source =
    (let lexed = tokenize(source)
    in
        let initial : ParserState = (lexed.tokens, [], Ashes.Byte.fromText(source))
        in
            let sourceBytes = Ashes.Byte.fromText(source)
            in
                match parserParseProgramItems(sourceBytes)([])(initial) with
                    | (items, body, state) ->
                        let parserDiagnostics =
                            state
                            |> parserEnsureEndOfInput
                            |> parserStateDiagnostics
                            |> reverseList
                        in
                            ProgramParseResult(program = ProgramSyntax(items = items, body = body), diagnostics = appendList(
                                lexed.diagnostics,
                                parserDiagnostics
                            )))

let parseExpression source =
    (let lexed = tokenize(source)
    in
        let initial : ParserState = (lexed.tokens, [], Ashes.Byte.fromText(source))
        in
            match parserParseExpression(initial) with
                | (expression, state) ->
                    let parserDiagnostics =
                        state
                        |> parserEnsureEndOfInput
                        |> parserStateDiagnostics
                        |> reverseList
                    in
                        ExpressionParseResult(expression = expression, diagnostics = appendList(
                            lexed.diagnostics,
                            parserDiagnostics
                        )))

let parseTypeExpression source =
    (let lexed = tokenize(source)
    in
        let initial : ParserState = (lexed.tokens, [], Ashes.Byte.fromText(source))
        in
            match parserParseTypeExpressionState(initial) with
                | (typeExpression, state) ->
                    let parserDiagnostics =
                        state
                        |> parserEnsureEndOfInput
                        |> parserStateDiagnostics
                        |> reverseList
                    in
                        TypeExpressionParseResult(typeExpression = typeExpression, diagnostics = appendList(
                            lexed.diagnostics,
                            parserDiagnostics
                        )))
